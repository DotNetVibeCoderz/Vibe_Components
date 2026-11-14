using System.Net;
using System.Net.Sockets;

namespace Electrical.SCADA.Modbus;

/// <summary>
/// Asynchronous Modbus TCP server backed by a <see cref="ModbusRegisterMap"/>. Each connection is served by its own task;
/// requests are answered in order with the transaction id of the request.
/// </summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly ModbusRegisterMap _map;
    private readonly TcpListener _listener;
    private readonly List<Task> _clients = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private long _requests;

    /// <param name="map">Data model served to clients.</param>
    /// <param name="address">Listen address (default loopback).</param>
    /// <param name="port">TCP port (0 = ephemeral; read <see cref="Port"/> after <see cref="Start"/>). Standard Modbus port is 502.</param>
    public ModbusTcpServer(ModbusRegisterMap map, IPAddress? address = null, int port = 502)
    {
        _map = map;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    /// <summary>When set, only requests addressed to this unit id (or 0/255) are answered; others get <see cref="ModbusExceptionCode.GatewayPathUnavailable"/>.</summary>
    public byte? UnitId { get; init; }

    public ModbusRegisterMap Map => _map;

    /// <summary>Bound TCP port.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Total requests served.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        Task[] tasks;
        lock (_gate) tasks = _acceptLoop is null ? [.. _clients] : [.. _clients, _acceptLoop];
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* connections closed */ }
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            var t = ServeAsync(client, ct);
            lock (_gate)
            {
                _clients.RemoveAll(x => x.IsCompleted);
                _clients.Add(t);
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var header = new byte[ModbusTcpAdu.HeaderLength];
        var pdu = new byte[ModbusPdu.MaxPduLength];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                int n = ModbusTcpAdu.ParseHeader(header, out var tx, out var unit);
                await stream.ReadExactlyAsync(pdu.AsMemory(0, n), ct).ConfigureAwait(false);
                byte[] reply = Handle(pdu.AsSpan(0, n), unit);
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(ModbusTcpAdu.Encode(tx, unit, reply), ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or InvalidDataException or ObjectDisposedException or SocketException)
        {
            // client disconnected, malformed framing or server stopping
        }
    }

    private byte[] Handle(ReadOnlySpan<byte> pdu, byte unit)
    {
        var fc = pdu.Length > 0 ? (ModbusFunction)(pdu[0] & 0x7F) : (ModbusFunction)0;
        if (UnitId is byte u && unit != u && unit != 0 && unit != 255)
            return ModbusPdu.EncodeResponse(ModbusResponse.Error(fc, ModbusExceptionCode.GatewayPathUnavailable));
        try
        {
            return ModbusPdu.EncodeResponse(_map.Process(ModbusPdu.DecodeRequest(pdu)));
        }
        catch (ModbusException e)
        {
            return ModbusPdu.EncodeResponse(ModbusResponse.Error(fc, e.Code));
        }
        catch (Exception)
        {
            return ModbusPdu.EncodeResponse(ModbusResponse.Error(fc, ModbusExceptionCode.ServerDeviceFailure));
        }
    }
}

/// <summary>
/// Asynchronous Modbus TCP client (master). Requests are serialized over one connection; each carries an incrementing transaction id
/// that is checked against the response. Calls honour the cancellation token and <see cref="Timeout"/>.
/// </summary>
public sealed class ModbusTcpClient : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _io = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _tx;

    /// <summary>Per-request timeout (default 3 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Unit identifier used when a call does not specify one (default 1).</summary>
    public byte DefaultUnitId { get; set; } = 1;

    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Transaction id that will be used by the next request.</summary>
    public ushort NextTransactionId => (ushort)(Volatile.Read(ref _tx) + 1);

    public async Task ConnectAsync(string host, int port = 502, CancellationToken ct = default)
    {
        _tcp?.Dispose();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    public Task ConnectAsync(IPAddress address, int port = 502, CancellationToken ct = default) => ConnectAsync(address.ToString(), port, ct);

    /// <summary>Sends a request and returns the decoded response. Throws <see cref="ModbusException"/> for exception responses.</summary>
    public async Task<ModbusResponse> SendAsync(ModbusRequest request, byte? unitId = null, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        byte unit = unitId ?? DefaultUnitId;
        byte[] pdu = ModbusPdu.EncodeRequest(request);
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            ushort tx = (ushort)Interlocked.Increment(ref _tx);
            await stream.WriteAsync(ModbusTcpAdu.Encode(tx, unit, pdu), cts.Token).ConfigureAwait(false);
            var header = new byte[ModbusTcpAdu.HeaderLength];
            while (true)
            {
                await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
                int n = ModbusTcpAdu.ParseHeader(header, out var rtx, out _);
                var body = new byte[n];
                await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);
                if (rtx != tx) continue; // stale response of an earlier timed-out request
                var resp = ModbusPdu.DecodeResponse(body, request);
                if (resp.IsException) throw new ModbusException(request.Function, resp.Exception);
                return resp;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Modbus request {request.Function} timed out after {Timeout.TotalMilliseconds} ms.");
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<bool[]> ReadCoilsAsync(ushort address, ushort quantity, byte? unitId = null, CancellationToken ct = default) =>
        (await SendAsync(ModbusRequest.ReadCoils(address, quantity), unitId, ct).ConfigureAwait(false)).Bits!;

    public async Task<bool[]> ReadDiscreteInputsAsync(ushort address, ushort quantity, byte? unitId = null, CancellationToken ct = default) =>
        (await SendAsync(ModbusRequest.ReadDiscreteInputs(address, quantity), unitId, ct).ConfigureAwait(false)).Bits!;

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort quantity, byte? unitId = null, CancellationToken ct = default) =>
        (await SendAsync(ModbusRequest.ReadHoldingRegisters(address, quantity), unitId, ct).ConfigureAwait(false)).Registers!;

    public async Task<ushort[]> ReadInputRegistersAsync(ushort address, ushort quantity, byte? unitId = null, CancellationToken ct = default) =>
        (await SendAsync(ModbusRequest.ReadInputRegisters(address, quantity), unitId, ct).ConfigureAwait(false)).Registers!;

    public Task WriteSingleCoilAsync(ushort address, bool value, byte? unitId = null, CancellationToken ct = default) =>
        SendAsync(ModbusRequest.WriteSingleCoil(address, value), unitId, ct);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, byte? unitId = null, CancellationToken ct = default) =>
        SendAsync(ModbusRequest.WriteSingleRegister(address, value), unitId, ct);

    public Task WriteMultipleCoilsAsync(ushort address, bool[] values, byte? unitId = null, CancellationToken ct = default) =>
        SendAsync(ModbusRequest.WriteMultipleCoils(address, values), unitId, ct);

    public Task WriteMultipleRegistersAsync(ushort address, ushort[] values, byte? unitId = null, CancellationToken ct = default) =>
        SendAsync(ModbusRequest.WriteMultipleRegisters(address, values), unitId, ct);

    /// <summary>Reads a float (two registers, high word first) from the input or holding table.</summary>
    public async Task<float> ReadFloatAsync(ModbusTable table, ushort address, byte? unitId = null, CancellationToken ct = default)
    {
        var r = table == ModbusTable.InputRegister
            ? await ReadInputRegistersAsync(address, 2, unitId, ct).ConfigureAwait(false)
            : await ReadHoldingRegistersAsync(address, 2, unitId, ct).ConfigureAwait(false);
        return ModbusData.ToFloat(r[0], r[1]);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
