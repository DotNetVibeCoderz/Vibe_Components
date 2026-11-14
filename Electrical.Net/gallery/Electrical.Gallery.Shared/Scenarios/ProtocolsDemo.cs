using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Dnp3;
using Electrical.SCADA.Iec104;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// Utility protocols: an in-process DNP3 outstation/master pair and an IEC 60870-5-104 server/client pair, both serving the IEEE 14-bus
/// RTU, talk through a loopback TCP tap that records every frame. Shows the integrity poll / general interrogation result, one control
/// round trip (CROB / single command) and the bytes of a chosen frame with its decoded layers. Nothing leaves 127.0.0.1.
/// </summary>
public sealed class ProtocolsDemo : IInteractiveScenario
{
    public string Id => "protocols";

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("proto", new("Protocol", "Protokol"), [("dnp3", Same("DNP3 (IEEE 1815)")), ("iec104", Same("IEC 60870-5-104"))], "dnp3"),
        new ChoiceParam("cmd", new("Command", "Perintah"), [("close", new("close / on", "tutup / on")), ("trip", new("trip / off", "buka / off"))], "close"),
        new ChoiceParam("mode", new("Control mode", "Mode kendali"), [("sbo", new("select before operate", "select before operate")), ("direct", new("direct operate", "operasi langsung"))], "sbo"),
        new ChoiceParam("frame", new("Inspect frame", "Periksa frame"),
            [("request", new("poll / GI request", "permintaan poll / GI")), ("response", new("data response", "respons data")),
             ("command", new("control request", "permintaan kendali")), ("confirm", new("control response", "respons kendali"))], "response"),
    ];

    // ------------------------------------------------------------------ loopback tap

    /// <summary>One frame seen on the wire. <see cref="Up"/> = master/client → outstation/server.</summary>
    private sealed record Wire(int Seq, bool Up, byte[] Data, double TimeMs);

    /// <summary>TCP relay on 127.0.0.1 that splits both directions into protocol frames and records them.</summary>
    private sealed class LoopbackTap : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<byte[], int, int> _frameLength;
        private readonly List<Wire> _frames = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _sockets = [];
        private Task? _accept;

        public LoopbackTap(int targetPort, Func<byte[], int, int> frameLength)
        {
            _frameLength = frameLength;
            _listener.Start();
            _accept = AcceptAsync(targetPort);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public IReadOnlyList<Wire> Frames { get { lock (_frames) return [.. _frames]; } }

        private async Task AcceptAsync(int target)
        {
            try
            {
                var down = await _listener.AcceptTcpClientAsync(_cts.Token);
                var up = new TcpClient { NoDelay = true };
                await up.ConnectAsync(IPAddress.Loopback, target, _cts.Token);
                down.NoDelay = true;
                lock (_sockets) { _sockets.Add(down); _sockets.Add(up); }
                _ = PumpAsync(down.GetStream(), up.GetStream(), true);
                _ = PumpAsync(up.GetStream(), down.GetStream(), false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, bool up)
        {
            var buf = new byte[8192];
            var pending = new byte[65536];
            int have = 0;
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf, _cts.Token);
                    if (n == 0) break;
                    Buffer.BlockCopy(buf, 0, pending, have, n);
                    have += n;
                    while (true)
                    {
                        int len = _frameLength(pending, have);
                        if (len <= 0 || len > have) break;
                        lock (_frames) _frames.Add(new Wire(_frames.Count, up, pending[..len], _clock.Elapsed.TotalMilliseconds));
                        Buffer.BlockCopy(pending, len, pending, 0, have - len);
                        have -= len;
                    }
                    await to.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_sockets) foreach (var s in _sockets) s.Dispose();
            if (_accept is not null) await _accept.ConfigureAwait(false);
        }
    }

    private static int Dnp3Length(byte[] b, int have) =>
        have < 3 ? 0 : b[0] != 0x05 || b[1] != 0x64 ? have /* resync: drop garbage */ : Dnp3LinkFrame.EncodedLength(b[2]);

    private static int Iec104Length(byte[] b, int have) => have < 2 ? 0 : b[0] != 0x68 ? have : 2 + b[1];

    // ------------------------------------------------------------------ exchanges

    private sealed record Exchange(
        IReadOnlyList<Wire> Frames, IReadOnlyList<Cell[]> Points, int PointCount, string CommandStatus, bool CommandOk, double CommandMs, double PollMs,
        string? Error, Dictionary<string, int> Picks, int EventCount);

    private static readonly Dictionary<string, Exchange> Cache = [];
    private static readonly Lock Gate = new();
    private static readonly Lazy<PowerFlowResult> Case14 = new(() => PowerFlowSolver.Solve(TestCases.Case14()));

    private static Exchange Get(string proto, bool close, bool sbo)
    {
        string key = $"{proto}|{close}|{sbo}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var x)) return x;
            try
            {
                x = Task.Run(() => proto == "iec104" ? RunIec104(close, sbo) : RunDnp3(close, sbo)).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is TimeoutException or SocketException or IOException or InvalidOperationException or InvalidDataException)
            {
                return new Exchange([], [], 0, "—", false, 0, 0, e.Message, [], 0);
            }
            Cache[key] = x;
            return x;
        }
    }

    private static async Task<Exchange> RunDnp3(bool close, bool sbo)
    {
        var pf = Case14.Value;
        var rtu = new RtuSimulator(pf);
        var tags = new TagDatabase();
        rtu.UpdateTags(tags);
        var map = rtu.CreateDnp3Map();
        var db = map.CreateDatabase(tags);
        db.AddBinaryOutput(0, value: !close);
        using var link = map.Attach(tags, db);
        await using var os = new Dnp3Outstation(db, new Dnp3OutstationOptions(), IPAddress.Loopback, 0);
        os.Start();
        await using var tap = new LoopbackTap(os.Port, Dnp3Length);
        await using var master = new Dnp3Master(1, os.Options.LocalAddress) { ResponseTimeout = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await master.ConnectAsync("127.0.0.1", tap.Port, cts.Token);

        var picks = new Dictionary<string, int>();
        var sw = Stopwatch.StartNew();
        picks["request"] = tap.Frames.Count;
        var poll = await master.IntegrityPollAsync(cts.Token);
        double pollMs = sw.Elapsed.TotalMilliseconds;
        picks["response"] = picks["request"] + 1;
        await master.ClearRestartAsync(cts.Token);

        await Task.Delay(20, cts.Token);
        picks["command"] = tap.Frames.Count;
        sw.Restart();
        var status = await master.OperateBinaryAsync(0, close ? Crob.LatchOn() : Crob.LatchOff(), sbo, cts.Token);
        double cmdMs = sw.Elapsed.TotalMilliseconds;
        // with SBO the OPERATE request follows the SELECT and its response: inspect the operate exchange
        if (sbo) picks["command"] += 2;
        picks["confirm"] = picks["command"] + 1;
        bool executed = db.Get(Dnp3PointType.BinaryOutput, 0).State == close;

        // a new operating point → class 1/2 events
        var vm = rtu.Points.First(p => p.Name == "BUS14.VM");
        tags.UpdateValue("BUS14.VM", rtu.MeasuredValue(vm) - 0.012);
        tags.UpdateValue("BR0.CLOSED", 0);
        var ev = await master.EventPollAsync(ct: cts.Token);
        await Task.Delay(30, cts.Token);

        var names = map.Bindings.ToDictionary(b => (b.Type, b.Index), b => b.Tag);
        var rows = new List<Cell[]>();
        foreach (var (i, p) in poll.Measurements.AnalogInputs.OrderBy(k => k.Key).Take(10))
            rows.Add([new(i.ToString(CultureInfo.InvariantCulture), null, true), "AI · g30v5", names.GetValueOrDefault((Dnp3PointType.AnalogInput, i), "?"),
                Cell.Num(p.Value, "0.0000"), Cell.Chip(p.Flags.HasFlag(Dnp3Flags.Online) ? "ONLINE" : p.Flags.ToString(), "ok")]);
        foreach (var (i, p) in poll.Measurements.BinaryInputs.OrderBy(k => k.Key).Take(3))
            rows.Add([new(i.ToString(CultureInfo.InvariantCulture), null, true), "BI · g1v2", names.GetValueOrDefault((Dnp3PointType.BinaryInput, i), "?"),
                p.State ? "1 (closed)" : "0 (open)", Cell.Chip(p.Flags.HasFlag(Dnp3Flags.Online) ? "ONLINE" : p.Flags.ToString(), "ok")]);
        int count = poll.Measurements.AnalogInputs.Count + poll.Measurements.BinaryInputs.Count + poll.Measurements.BinaryOutputs.Count;
        return new Exchange(tap.Frames, rows, count, $"{status}", status == Dnp3CommandStatus.Success && executed, cmdMs, pollMs, null, picks, ev.Measurements.Events.Count);
    }

    private static async Task<Exchange> RunIec104(bool close, bool sbo)
    {
        var pf = Case14.Value;
        var rtu = new RtuSimulator(pf);
        var tags = new TagDatabase();
        rtu.UpdateTags(tags);
        var map = rtu.CreateIec104Map();
        tags.Add("CB1.CMD");
        tags.UpdateValue("CB1.CMD", close ? 0 : 1);
        map.Add(5001, "CB1.CMD", TypeId.C_SC_NA_1, selectBeforeOperate: sbo);
        await using var server = new Iec104Server(tags, map, new Iec104ServerOptions(), IPAddress.Loopback, 0);
        server.Start();
        await using var tap = new LoopbackTap(server.Port, Iec104Length);
        await using var client = new Iec104Client { RequestTimeout = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await client.ConnectAsync("127.0.0.1", tap.Port, cts.Token);
        await client.StartDataTransferAsync(cts.Token);
        for (int i = 0; i < 50 && client.EndOfInitializationCount == 0; i++) await Task.Delay(10, cts.Token);

        var picks = new Dictionary<string, int>();
        var sw = Stopwatch.StartNew();
        int before = tap.Frames.Count;
        var gi = await client.GeneralInterrogationAsync(cts.Token);
        double pollMs = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(20, cts.Token);
        var frames = tap.Frames;
        picks["request"] = Find(frames, before, true, TypeId.C_IC_NA_1);
        picks["response"] = Find(frames, before, false, TypeId.M_ME_NC_1);

        before = tap.Frames.Count;
        sw.Restart();
        var cmd = await client.SingleCommandAsync(5001, close, sbo, ct: cts.Token);
        double cmdMs = sw.Elapsed.TotalMilliseconds;
        bool executed = tags["CB1.CMD"].Value == (close ? 1 : 0);

        int spont = 0;
        client.AsduReceived += a => { if (a.Cause == CauseOfTransmission.Spontaneous) Interlocked.Increment(ref spont); };
        tags.UpdateValue("BR0.CLOSED", 0);
        for (int i = 0; i < 50 && Volatile.Read(ref spont) == 0; i++) await Task.Delay(10, cts.Token);
        await client.TestAsync(cts.Token);
        await Task.Delay(30, cts.Token);
        frames = tap.Frames;
        // the execute (last activation) of the command and its confirmation
        picks["command"] = LastActivation(frames, before);
        picks["confirm"] = picks["command"] < 0 ? -1 : Find(frames, picks["command"] + 1, false, TypeId.C_SC_NA_1);

        var tagOf = map.Points.ToDictionary(p => p.Ioa, p => p.Tag);
        var rows = gi.OrderBy(g => g.Object.Address).Where(g => g.Type == TypeId.M_ME_NC_1).Take(10)
            .Concat(gi.Where(g => g.Type == TypeId.M_SP_NA_1).OrderBy(g => g.Object.Address).Take(3))
            .Select(g => new Cell[]
            {
                new(g.Object.Address.ToString(CultureInfo.InvariantCulture), null, true), g.Type.ToString(), tagOf.GetValueOrDefault(g.Object.Address, "?"),
                g.Type == TypeId.M_SP_NA_1 ? (g.Object.State ? "1 (closed)" : "0 (open)") : Cell.Num(g.Object.Value, "0.0000"),
                Cell.Chip(g.Object.Quality.ToString().ToUpperInvariant(), g.Object.Quality == Iec104Quality.Good ? "ok" : "warn"),
            }).ToList();
        return new Exchange(frames, rows, gi.Count, cmd.Success ? (cmd.Terminated ? "ACTCON + ACTTERM" : "ACTCON") : $"negative ({cmd.Cause})",
            cmd.Success && executed, cmdMs, pollMs, null, picks, spont);
    }

    private static int Find(IReadOnlyList<Wire> frames, int from, bool up, TypeId type)
    {
        for (int i = Math.Max(0, from); i < frames.Count; i++)
            if (frames[i].Up == up && frames[i].Data.Length > 6 && (frames[i].Data[2] & 1) == 0 && frames[i].Data[6] == (byte)type) return i;
        return -1;
    }

    private static int LastActivation(IReadOnlyList<Wire> frames, int from)
    {
        int last = -1;
        for (int i = Math.Max(0, from); i < frames.Count; i++)
            if (frames[i].Up && frames[i].Data.Length > 6 && (frames[i].Data[2] & 1) == 0 && frames[i].Data[6] == (byte)TypeId.C_SC_NA_1) last = i;
        return last;
    }

    // ------------------------------------------------------------------ decoding for display

    private static string Hex(byte[] f, int maxRows = 16)
    {
        var sb = new StringBuilder();
        var rows = HexDump.Rows(f);
        foreach (var r in rows.Take(maxRows))
            sb.Append(CultureInfo.InvariantCulture, $"{r.Offset:X4}  {HexDump.Spaced(r.Bytes),-47}  {string.Concat(r.Bytes.Select(HexDump.Ascii))}\n");
        if (rows.Count > maxRows) sb.Append(CultureInfo.InvariantCulture, $"…  ({f.Length - maxRows * 16} more bytes)\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Application fragment starting with the FIR segment at <paramref name="i"/>, joined with the following segments of the same direction.</summary>
    private static (byte[]? Fragment, int Segments) Reassemble(IReadOnlyList<Wire> frames, int i)
    {
        var parts = new List<byte>();
        int n = 0;
        bool dir = frames[i].Up;
        for (int k = i; k < frames.Count; k++)
        {
            if (frames[k].Up != dir) continue;
            Dnp3LinkFrame lf;
            try { lf = Dnp3LinkFrame.Decode(frames[k].Data); } catch (InvalidDataException) { return (null, 0); }
            if (lf.UserData.Length == 0) continue;
            byte th = lf.UserData[0];
            if (k == i && (th & 0x40) == 0) return (null, 0);
            if (k > i && (th & 0x40) != 0) return (null, 0);
            parts.AddRange(lf.UserData.AsSpan(1).ToArray());
            n++;
            if ((th & 0x80) != 0) return ([.. parts], n);
        }
        return (null, 0);
    }

    private static (string Link, string Transport, string App) Dnp3Summary(byte[] f, byte[]? reassembled = null, int segments = 0)
    {
        try
        {
            var lf = Dnp3LinkFrame.Decode(f);
            string link = $"{(lf.IsPrimary ? ((LinkFunction)lf.Function).ToString() : ((LinkSecondaryFunction)lf.Function).ToString())} {lf.Source}→{lf.Destination}";
            if (lf.UserData.Length == 0) return (link, "—", "—");
            byte th = lf.UserData[0];
            string tr = $"{((th & 0x40) != 0 ? "FIR " : "")}{((th & 0x80) != 0 ? "FIN " : "")}seq {th & 0x3F}";
            if ((th & 0xC0) != 0xC0 && reassembled is null) return (link, tr, "segment");
            var app = Dnp3Fragment.Decode(reassembled ?? lf.UserData.AsSpan(1).ToArray());
            string objs = app.Objects.Length == 0 ? "" : " · " + string.Join(" ", ParseObjects(app).Take(4).Select(h => $"g{h.Group}v{h.Variation}"));
            return (link, tr, $"{app.Function} seq {app.Control.Seq}{(app.IsResponse ? $" IIN 0x{(ushort)app.Iin:X4}" : "")}{objs}{(segments > 1 ? $" · {segments} segments" : "")}");
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { return ("?", "?", e.Message); }
    }

    private static List<Dnp3ObjectHeader> ParseObjects(Dnp3Fragment app)
    {
        bool data = app.IsResponse || app.Function is Dnp3Function.Select or Dnp3Function.Operate or Dnp3Function.DirectOperate or Dnp3Function.Write;
        try { return Dnp3ObjectParser.Parse(app.Objects, data); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { return []; }
    }

    private static string Dnp3Layers(byte[] f, string lang, byte[]? reassembled = null, int segments = 0)
    {
        var sb = new StringBuilder();
        var lf = Dnp3LinkFrame.Decode(f);
        string fn = lf.IsPrimary ? ((LinkFunction)lf.Function).ToString() : ((LinkSecondaryFunction)lf.Function).ToString();
        sb.Append(CultureInfo.InvariantCulture, $"DATA LINK   start 05 64 · LEN {f[2]} · DEST {lf.Destination} · SRC {lf.Source}\n");
        sb.Append(CultureInfo.InvariantCulture, $"            CTRL 0x{lf.Control:X2}: DIR={(lf.FromMaster ? 1 : 0)} PRM={(lf.IsPrimary ? 1 : 0)} FC={lf.Function} {fn}\n");
        sb.Append(CultureInfo.InvariantCulture, $"            header CRC {f[8]:X2} {f[9]:X2} ok · {(lf.UserData.Length + 15) / 16} data block(s) + CRC-16/DNP\n");
        if (lf.UserData.Length == 0) return sb.ToString().TrimEnd();
        byte th = lf.UserData[0];
        sb.Append(CultureInfo.InvariantCulture, $"TRANSPORT   0x{th:X2} · FIN={(th >> 7) & 1} FIR={(th >> 6) & 1} SEQ={th & 0x3F}\n");
        if ((th & 0xC0) != 0xC0 && reassembled is null) { sb.Append(Pick(lang, "APPLICATION (continues in the next segment)", "APLIKASI (berlanjut di segmen berikutnya)")); return sb.ToString(); }
        if (segments > 1)
            sb.Append(CultureInfo.InvariantCulture, $"            {Pick(lang, $"fragment of {reassembled!.Length} octets reassembled from {segments} transport segments", $"fragmen {reassembled!.Length} oktet dirakit dari {segments} segmen transport")}\n");
        var app = Dnp3Fragment.Decode(reassembled ?? lf.UserData.AsSpan(1).ToArray());
        var c = app.Control;
        sb.Append(CultureInfo.InvariantCulture, $"APPLICATION AC 0x{c.Encode():X2} (FIR={(c.Fir ? 1 : 0)} FIN={(c.Fin ? 1 : 0)} CON={(c.Con ? 1 : 0)} UNS={(c.Uns ? 1 : 0)} SEQ={c.Seq}) · FC {(byte)app.Function} {app.Function}\n");
        if (app.IsResponse) sb.Append(CultureInfo.InvariantCulture, $"            IIN 0x{(ushort)app.Iin:X4} {(app.Iin == Dnp3Iin.None ? "" : app.Iin.ToString())}\n");
        foreach (var h in ParseObjects(app).Take(8))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  OBJECT    {h}{(h.Items.Count > 0 ? $" · index {h.Items[0].Index}…{h.Items[^1].Index}" : "")}\n");
            if (h.Group == 12 && h.Items.Count > 0)
            {
                var crob = Crob.Decode(h.Items[0].Data);
                sb.Append(CultureInfo.InvariantCulture, $"            CROB {crob.OpType} {crob.TripClose} count {crob.Count} on {crob.OnTimeMs} ms off {crob.OffTimeMs} ms · status {crob.Status}\n");
            }
            else if (h.Group is 30 or 32 && h.Items.Count > 0)
            {
                var a = Dnp3Objects.ReadAnalog(h.Group, h.Variation, h.Items[0].Data);
                sb.Append(CultureInfo.InvariantCulture, $"            [{h.Items[0].Index}] = {a.Value:0.0000} flags {a.Flags}{(h.Items.Count > 1 ? $"  (+{h.Items.Count - 1} more)" : "")}\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static (string Apci, string Asdu) Iec104Summary(byte[] f)
    {
        try
        {
            var a = Iec104Codec.Decode(f);
            string apci = a.Format switch
            {
                ApciFormat.I => $"I  N(S)={a.SendSequence} N(R)={a.ReceiveSequence}",
                ApciFormat.S => $"S  N(R)={a.ReceiveSequence}",
                _ => $"U  {a.Function}",
            };
            string asdu = a.Asdu is { } s ? $"{s.Type} · {s.Cause}{(s.Negative ? " (neg)" : "")} · CA {s.CommonAddress} · {s.Objects.Count} obj · IOA {s.Objects.FirstOrDefault()?.Address}" : "—";
            return (apci, asdu);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException) { return ("?", e.Message); }
    }

    private static string Iec104Layers(byte[] f)
    {
        var sb = new StringBuilder();
        var a = Iec104Codec.Decode(f);
        sb.Append(CultureInfo.InvariantCulture, $"APCI   start 0x68 · length {f[1]} · control {HexDump.Spaced(f.AsSpan(2, 4))}\n");
        sb.Append(a.Format switch
        {
            ApciFormat.I => $"       I-format · N(S) = {a.SendSequence} · N(R) = {a.ReceiveSequence}\n",
            ApciFormat.S => $"       S-format · N(R) = {a.ReceiveSequence}\n",
            _ => $"       U-format · {a.Function}\n",
        });
        if (a.Asdu is not { } s) return sb.ToString().TrimEnd();
        byte vsq = f[7], cot = f[8];
        sb.Append(CultureInfo.InvariantCulture, $"ASDU   type {(byte)s.Type} {s.Type}\n");
        sb.Append(CultureInfo.InvariantCulture, $"       VSQ 0x{vsq:X2} (SQ={(vsq >> 7) & 1}, n={vsq & 0x7F}) · COT 0x{cot:X2} {s.Cause} (T={(cot >> 7) & 1}, P/N={(cot >> 6) & 1}) · OA {f[9]} · CA {BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(10))}\n");
        foreach (var o in s.Objects.Take(6))
            sb.Append(CultureInfo.InvariantCulture, $"  IOA {o.Address,-6} value {o.Value.ToString("0.####", CultureInfo.InvariantCulture)}{(o.Select ? " S/E=select" : "")}{(o.Qualifier != 0 ? $" QU/QOI {o.Qualifier}" : "")} · quality {o.Quality}\n");
        if (s.Objects.Count > 6) sb.Append(CultureInfo.InvariantCulture, $"  … +{s.Objects.Count - 6} objects\n");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ run

    public InteractiveResult Run(ParamValues v, string lang)
    {
        string proto = v.Get("proto", "dnp3");
        bool dnp = proto != "iec104";
        bool close = v.Get("cmd", "close") == "close";
        bool sbo = v.Get("mode", "sbo") == "sbo";
        var x = Get(proto, close, sbo);
        if (x.Error is not null)
            return new InteractiveResult([new(new("Loopback session", "Sesi loopback"), Pick(lang, "FAILED", "GAGAL"), null, Same(x.Error), "alarm")], []);

        string up = dnp ? "M→O" : "C→S", down = dnp ? "O→M" : "S→C";
        var frameRows = x.Frames.Take(40).Select(w =>
        {
            Cell dir = Cell.Chip(w.Up ? up : down, w.Up ? "ok" : "na");
            if (dnp)
            {
                var (frag, segs) = Reassemble(x.Frames, w.Seq);
                var (l, t, a) = Dnp3Summary(w.Data, frag, segs);
                return new Cell[] { new(w.Seq.ToString(CultureInfo.InvariantCulture), null, true), dir, new(w.Data.Length.ToString(CultureInfo.InvariantCulture), null, true), l, t, a };
            }
            var (apci, asdu) = Iec104Summary(w.Data);
            return new Cell[] { new(w.Seq.ToString(CultureInfo.InvariantCulture), null, true), dir, new(w.Data.Length.ToString(CultureInfo.InvariantCulture), null, true), apci, asdu };
        }).ToList();

        string pick = v.Get("frame", "response");
        int idx = x.Picks.GetValueOrDefault(pick, -1);
        var sel = idx >= 0 && idx < x.Frames.Count ? x.Frames[idx] : null;
        string layers = sel is null ? Pick(lang, "(frame not captured)", "(frame tidak tertangkap)") : dnp ? Dnp3Layers(sel.Data, lang, Reassemble(x.Frames, sel.Seq).Fragment, Reassemble(x.Frames, sel.Seq).Segments) : Iec104Layers(sel.Data);

        int bytes = x.Frames.Sum(f => f.Data.Length);
        var kpis = new List<Kpi>
        {
            new(dnp ? new("Integrity poll", "Poll integritas") : new("General interrogation", "Interogasi umum"), x.PointCount.ToString(CultureInfo.InvariantCulture),
                Pick(lang, "points", "titik"), Same($"{F(x.PollMs, "0.0")} ms"), "ok"),
            new(dnp ? new("CROB", "CROB") : new("Single command", "Perintah tunggal"), x.CommandOk ? Pick(lang, "EXECUTED", "DIEKSEKUSI") : Pick(lang, "REJECTED", "DITOLAK"), null,
                Same($"{x.CommandStatus} · {F(x.CommandMs, "0.0")} ms"), x.CommandOk ? "ok" : "alarm"),
            new(dnp ? new("Events after change", "Event setelah perubahan") : new("Spontaneous ASDUs", "ASDU spontan"), x.EventCount.ToString(CultureInfo.InvariantCulture), null,
                dnp ? new("class 1/2 event poll", "poll event kelas 1/2") : new("COT 3 after a status change", "COT 3 setelah perubahan status"), x.EventCount > 0 ? "ok" : "warn"),
            new(new("On the wire", "Di jalur"), x.Frames.Count.ToString(CultureInfo.InvariantCulture), Pick(lang, "frames", "frame"), Same($"{bytes} B · 127.0.0.1")),
        };

        L[] headers = dnp
            ? [Same("#"), Same(""), Same("B"), new("Link", "Link"), Same("Transport"), new("Application", "Aplikasi")]
            : [Same("#"), Same(""), Same("B"), Same("APCI"), Same("ASDU")];
        var blocks = new List<Block>
        {
            new TableBlock(dnp ? new("Integrity poll result (first points)", "Hasil poll integritas (titik pertama)") : new("General interrogation result (first points)", "Hasil interogasi umum (titik pertama)"),
                [dnp ? Same("Index") : Same("IOA"), dnp ? new("Object", "Objek") : new("Type", "Tipe"), Same("Tag"), new("Value", "Nilai"), dnp ? Same("Flags") : new("Quality", "Kualitas")], x.Points,
                dnp ? new("RtuSimulator(case14).CreateDnp3Map() → Dnp3Database", "RtuSimulator(case14).CreateDnp3Map() → Dnp3Database")
                    : new("RtuSimulator(case14).CreateIec104Map() → Iec104Server", "RtuSimulator(case14).CreateIec104Map() → Iec104Server")),
            new ListingBlock(new("Frame bytes", "Byte frame"), sel is null ? "" : Hex(sel.Data), "text", Same(sel is null ? "" : $"#{sel.Seq} · {(sel.Up ? up : down)} · {sel.Data.Length} B")),
            new ListingBlock(new("Decoded layers", "Lapisan terdekode"), layers, "text", Same(sel is null ? "" : $"#{sel.Seq} · {(sel.Up ? up : down)}"), Wide: true),
            new TableBlock(new("Session on the loopback tap", "Sesi pada tap loopback"), headers, frameRows,
                new($"every frame between the two stacks, in order ({up} = request direction)", $"setiap frame di antara kedua stack, berurutan ({up} = arah permintaan)"), Wide: true),
            new NoteBlock(dnp
                ? new("DNP3: the master sends READ g60v2/3/4/1 (class 1, 2, 3, 0) and the outstation answers with static analog (g30v5) and binary (g1v2) objects; every link frame carries a CRC-16/DNP per 16-octet block. The CROB (g12v1) goes through SELECT then OPERATE (or DIRECT_OPERATE) and is echoed with its status. Changing BUS14.VM and BR0.CLOSED creates class 2/1 events that the next event poll collects and confirms.",
                      "DNP3: master mengirim READ g60v2/3/4/1 (kelas 1, 2, 3, 0) dan outstation menjawab dengan objek analog (g30v5) dan biner (g1v2) statis; setiap frame link membawa CRC-16/DNP per blok 16 oktet. CROB (g12v1) melalui SELECT lalu OPERATE (atau DIRECT_OPERATE) dan digemakan bersama statusnya. Mengubah BUS14.VM dan BR0.CLOSED membuat event kelas 2/1 yang dikumpulkan dan dikonfirmasi oleh poll event berikutnya.")
                : new("IEC 104: STARTDT act/con opens data transfer, the controlled station sends M_EI_NA_1, and C_IC_NA_1 (QOI 20) returns ACTCON, the M_ME_NC_1 / M_SP_NA_1 ASDUs with COT 20 and ACTTERM. The single command C_SC_NA_1 is sent with S/E = 1 (select) then S/E = 0 (execute) when select-before-operate is configured for IOA 5001. I-frames are acknowledged by S-frames after w frames or t2; idle links exchange TESTFR.",
                      "IEC 104: STARTDT act/con membuka transfer data, stasiun terkendali mengirim M_EI_NA_1, dan C_IC_NA_1 (QOI 20) mengembalikan ACTCON, ASDU M_ME_NC_1 / M_SP_NA_1 dengan COT 20, lalu ACTTERM. Perintah tunggal C_SC_NA_1 dikirim dengan S/E = 1 (select) lalu S/E = 0 (execute) bila select-before-operate diatur untuk IOA 5001. Frame I dikonfirmasi oleh frame S setelah w frame atau t2; link yang diam bertukar TESTFR.")),
        };
        return new InteractiveResult(kpis, blocks);
    }
}
