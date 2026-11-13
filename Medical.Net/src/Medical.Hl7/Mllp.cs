using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Medical.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Hl7;

/// <summary>Minimal Lower Layer Protocol framing: &lt;VT&gt; message &lt;FS&gt;&lt;CR&gt;.</summary>
public static class Mllp
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    public static byte[] Frame(string message)
    {
        int n = Encoding.UTF8.GetByteCount(message);
        var buf = new byte[n + 3];
        buf[0] = StartBlock;
        Encoding.UTF8.GetBytes(message, 0, message.Length, buf, 1);
        buf[^2] = EndBlock;
        buf[^1] = CarriageReturn;
        return buf;
    }

    /// <summary>Reads framed messages from a stream until it ends (bytes outside frames are ignored).</summary>
    public static async IAsyncEnumerable<string> ReadMessagesAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        var message = new ArrayBufferWriter<byte>(8192);
        bool inFrame = false, sawEnd = false;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(0, 8192), ct).ConfigureAwait(false);
                if (read == 0) yield break;
                for (int i = 0; i < read; i++)
                {
                    byte b = chunk[i];
                    if (!inFrame)
                    {
                        if (b == StartBlock) { inFrame = true; message.Clear(); sawEnd = false; }
                        continue;
                    }
                    if (sawEnd)
                    {
                        if (b == CarriageReturn)
                        {
                            inFrame = false;
                            yield return Encoding.UTF8.GetString(message.WrittenSpan);
                            continue;
                        }
                        message.Write([EndBlock]);
                        sawEnd = false;
                    }
                    if (b == EndBlock) { sawEnd = true; continue; }
                    message.Write([b]);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }
}

/// <summary>Handles one inbound HL7 message and returns the ACK to send back.</summary>
public delegate ValueTask<string> Hl7Handler(Hl7Message message, CancellationToken ct);

/// <summary>
/// MLLP/TCP listener. Every received message is parsed with NHapi, passed to the handler and acknowledged
/// (AA by the handler; AE when the handler throws; AR when the message cannot be parsed). Published on the event bus as <see cref="Hl7Message"/>.
/// </summary>
public sealed class MllpServer(IPEndPoint endpoint, Hl7Handler handler, IMedicalEventBus? bus = null) : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IPEndPoint Endpoint => _listener?.LocalEndpoint as IPEndPoint ?? endpoint;
    public long MessagesReceived => Interlocked.Read(ref _received);
    private long _received;

    /// <summary>Default handler: accept everything.</summary>
    public static ValueTask<string> AcceptAll(Hl7Message m, CancellationToken _) => ValueTask.FromResult(Hl7V2.BuildAck(m));

    public void Start()
    {
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = AcceptLoop(_cts.Token);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            await foreach (var raw in Mllp.ReadMessagesAsync(stream, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _received);
                string ack;
                Hl7Message? parsed = null;
                try { parsed = Hl7V2.Parse(raw); }
                catch (Exception e) { ack = Hl7V2.BuildReject(e.Message); await stream.WriteAsync(Mllp.Frame(ack), ct).ConfigureAwait(false); continue; }
                try
                {
                    bus?.Publish(parsed);
                    ack = await handler(parsed, ct).ConfigureAwait(false);
                }
                catch (Exception e) { ack = Hl7V2.BuildAck(parsed, "AE", e.Message); }
                await stream.WriteAsync(Mllp.Frame(ack), ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts?.Dispose();
    }
}

/// <summary>Sends framed HL7 messages and waits for the ACK.</summary>
public sealed class MllpClient(string host, int port) : IAsyncDisposable
{
    private TcpClient? _client;
    private IAsyncEnumerator<string>? _reader;

    public async Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        if (_client is null)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            _reader = Mllp.ReadMessagesAsync(_client.GetStream(), ct).GetAsyncEnumerator(ct);
        }
        await _client.GetStream().WriteAsync(Mllp.Frame(Hl7V2.Normalize(message)), ct).ConfigureAwait(false);
        if (!await _reader!.MoveNextAsync().ConfigureAwait(false)) throw new IOException("Connection closed before ACK was received.");
        return _reader.Current;
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
        _client?.Dispose();
    }
}

public static class Hl7BuilderExtensions
{
    /// <summary>Registers nothing stateful; marks the HL7 module so hosts can add <c>MllpHostedService</c> (Medical.AspNetCore).</summary>
    public static MedicalBuilder AddHl7(this MedicalBuilder builder) =>
        builder.TryAddModule("hl7", s => s.TryAddSingleton(new Hl7Endpoint("MEDICAL.NET", "MEDNET")));
}
