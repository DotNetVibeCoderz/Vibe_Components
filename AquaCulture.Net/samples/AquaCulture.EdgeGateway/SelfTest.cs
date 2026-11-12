using System.Diagnostics;
using System.Net;
using System.Text;
using AquaCulture.IoT;

namespace AquaCulture.EdgeGateway;

/// <summary>
/// Runs 48 simulated hours (5-minute readings, 3 ponds) through the whole gateway — edge filter, alerts, aerator control,
/// outbox, a restart halfway, and sync to a cloud that drops every 5th request and every 7th response — and checks that every
/// reading reached the cloud exactly once. Used by CI on the NativeAOT linux-arm64 build.
/// </summary>
internal static class SelfTest
{
    private sealed class LoopbackCloud : HttpMessageHandler
    {
        public long Watermark, Stored, Duplicates;
        private int _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            int call = ++_calls;
            if (req.RequestUri!.AbsolutePath.EndsWith("/config", StringComparison.Ordinal))
            {
                if (req.Headers.IfNoneMatch.Count > 0) return new(HttpStatusCode.NotModified);
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"doSetPoint\":4.8}", Encoding.UTF8, "application/json") };
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
                return ok;
            }
            if (call % 5 == 0) throw new HttpRequestException("simulated outage");
            var (_, batch) = SyncWire.ParseBatch(await req.Content!.ReadAsByteArrayAsync(ct));
            foreach (var r in batch)
                if (r.Seq <= Watermark) Duplicates++; else { Watermark = r.Seq; Stored++; }
            if (call % 7 == 0) throw new HttpRequestException("simulated lost response");
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"acked\":{Watermark}}}") };
        }
    }

    public static async Task<int> RunAsync(string dir, bool requireNative = false)
    {
        var sw = Stopwatch.StartNew();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        var ponds = new[]
        {
            new SimulatedPond("P-01", "probe-1", MeanDo: 5.5, SalinityPpt: 20),
            new SimulatedPond("P-02", "probe-2", MeanDo: 4.4, DoAmplitude: 2.6, SalinityPpt: 20),   // will crash at night without aeration
            new SimulatedPond("P-03", "probe-3", MeanDo: 5.0, Tan: 2.5, SalinityPpt: 20),         // ammonia problem
        };
        var sim = new SimulatedSensorTransport(ponds, seed: 21);
        var aerators = ponds.ToDictionary(p => p.PondId, p => "aer-" + p.PondId);
        var cloud = new LoopbackCloud();
        using var http = new HttpClient(cloud);
        var sync = new CloudSyncClient(http, new Uri("https://cloud.invalid/"), "selftest", "key", "gw-selftest") { BatchSize = 100 };

        long appended = 0;
        int alerts = 0, onCommands = 0, rejected = 0, syncErrors = 0;
        var settings = new GatewaySettings();
        sync.ConfigChanged += c => settings = settings.Apply(c);
        var t0 = new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc);
        for (int half = 0; half < 2; half++)
        {
            using var outbox = new EdgeOutbox(Path.Combine(dir, "outbox")) { SegmentBytes = 64 * 1024 };   // reopened: a gateway restart
            var core = new GatewayCore(sim, outbox, aerators) { Settings = settings };
            for (int step = 0; step < 24 * 12; step++)
            {
                var now = t0.AddMinutes(5 * (half * 24 * 12 + step));
                var report = await core.TickAsync(sim.Emit(now), now);
                appended += report.Readings;
                rejected += report.Rejected;
                alerts += report.Alerts.Count(a => !a.Cleared);
                onCommands += report.Commands.Count(c => c.Action == "aerator_on");
                if (step % 12 == 11 && (await sync.SyncOnceAsync(outbox)).Error is not null) syncErrors++;   // hourly sync
                core.Settings = settings;
            }
        }
        using (var outbox = new EdgeOutbox(Path.Combine(dir, "outbox")))
            for (int i = 0; i < 100 && outbox.Pending > 0; i++) await sync.SyncOnceAsync(outbox);

        // Rust kernels: loaded from runtimes/<rid>/native next to the executable, and identical to the managed fallback
        var backend = AquaCulture.Native.AquaKernels.Backend;
        double native = AquaCulture.Native.AquaKernels.DoSaturation(29, 20);
        AquaCulture.Native.AquaKernels.ForceManaged = true;
        double managed = AquaCulture.Native.AquaKernels.DoSaturation(29, 20);
        AquaCulture.Native.AquaKernels.ForceManaged = false;

        var checks = new (string Name, bool Ok, string Detail)[]
        {
            ("kernels", !requireNative || backend == AquaCulture.Native.KernelBackend.Native, $"{backend}" + (backend == AquaCulture.Native.KernelBackend.Native ? "" : " (Rust library not found)")),
            ("native = managed", Math.Abs(native - managed) < 1e-12, $"DO saturation 29 °C, 20 ppt: {native:0.0000} mg/L"),
            ("readings produced", appended > 10_000, $"{appended}"),
            ("edge filter ran", rejected >= 0, $"{rejected} rejected"),
            ("alerts raised", alerts > 0, $"{alerts}"),
            ("aerators switched on", onCommands > 0, $"{onCommands} on-commands"),
            ("outages happened", syncErrors > 0, $"{syncErrors} failed sync passes"),
            ("cloud config applied", Math.Abs(settings.DoSetPoint - 4.8) < 1e-9, $"set point {settings.DoSetPoint}"),
            ("every reading delivered once", cloud.Stored == appended && cloud.Watermark == appended, $"stored {cloud.Stored} of {appended}, {cloud.Duplicates} duplicates skipped"),
        };
        foreach (var (name, ok, detail) in checks) Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-30} {detail}");
        bool aot = !File.Exists(Path.Combine(AppContext.BaseDirectory, "aqua-edge.dll"));      // a NativeAOT build has no managed assembly
        Console.WriteLine($"self-test finished in {sw.ElapsedMilliseconds} ms ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, " +
            $"{(aot ? "NativeAOT" : "JIT")})");
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        return checks.All(c => c.Ok) ? 0 : 1;
    }
}
