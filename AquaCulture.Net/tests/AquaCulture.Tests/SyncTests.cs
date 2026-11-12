using System.Net;
using System.Text;
using AquaCulture.Data;
using AquaCulture.IoT;

namespace AquaCulture.Tests;

public class EdgeSyncTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("aqua-outbox-").FullName;

    private static IEnumerable<SensorReading> Readings(int n, int offset = 0) => Enumerable.Range(offset, n).Select(i =>
        new SensorReading("probe-" + i % 3, "P-0" + i % 3, i % 2 == 0 ? SensorKind.DissolvedOxygen : SensorKind.Temperature, 5 + i * 0.001,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)));

    [Fact]
    public void Outbox_survives_restart_and_discards_a_torn_line()
    {
        string dir = TempDir();
        using (var o = new EdgeOutbox(dir) { SegmentBytes = 4096 })
        {
            Assert.Equal(250, o.Append(Readings(250)));
            o.Ack(100);
            Assert.True(Directory.GetFiles(dir, "seg-*.jsonl").Length > 1);
        }
        // power cut in the middle of a write
        var last = Directory.GetFiles(dir, "seg-*.jsonl").Order().Last();
        File.AppendAllText(last, "{\"s\":251,\"d\":\"probe-1\",\"p\":\"P-0");

        using var again = new EdgeOutbox(dir) { SegmentBytes = 4096 };
        Assert.Equal(250, again.LastSeq);
        Assert.Equal(100, again.Acked);
        Assert.Equal(150, again.Pending);
        var peek = again.Peek(1000);
        Assert.Equal(Enumerable.Range(101, 150).Select(i => (long)i), peek.Select(r => r.Seq));
        Assert.Equal(SensorKind.DissolvedOxygen, peek[0].Reading.Kind);             // seq 101 = reading index 100 (even → DO)
        Assert.Equal(251, again.Append(Readings(1, 250)));                           // numbering continues after the torn line
    }

    [Fact]
    public void Ack_deletes_delivered_segments_and_the_cap_drops_the_oldest()
    {
        string dir = TempDir();
        using var o = new EdgeOutbox(dir) { SegmentBytes = 2048, MaxBytes = 16_384, Durable = false };
        o.Append(Readings(100));
        int before = Directory.GetFiles(dir, "seg-*.jsonl").Length;
        o.Ack(90);
        Assert.True(Directory.GetFiles(dir, "seg-*.jsonl").Length < before);
        Assert.All(o.Peek(), r => Assert.True(r.Seq > 90));

        o.Append(Readings(2000, 100));                                              // a long outage
        long total = Directory.GetFiles(dir, "seg-*.jsonl").Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 16_384 + 2048, $"{total}");
        Assert.True(o.Dropped > 0);
        Assert.Equal(o.LastSeq - o.Acked, o.Peek(100_000).Count);                   // what is left is contiguous and readable
    }

    [Fact]
    public void Wire_format_round_trips()
    {
        var recs = Readings(3).Select((r, i) => new OutboxRecord(i + 10, r with { Quality = (byte)(i == 1 ? 40 : 100) })).ToList();
        var (gw, back) = SyncWire.ParseBatch(SyncWire.WriteBatch("gw-1", recs));
        Assert.Equal("gw-1", gw);
        Assert.Equal(recs.Select(r => r.Seq), back.Select(r => r.Seq));
        Assert.Equal(recs[1].Reading.Value, back[1].Reading.Value);
        Assert.Equal(recs[1].Reading.Timestamp, back[1].Reading.Timestamp);
        Assert.Equal(40, back[1].Reading.Quality);
        Assert.Throws<FormatException>(() => SyncWire.ParseBatch("{\"gateway\":\"g\",\"readings\":[{\"s\":1,\"k\":\"Plutonium\",\"v\":1,\"t\":0}]}"u8));
        Assert.False(SyncWire.IsValidGatewayId("../etc"));
    }

    /// <summary>The aqua-cloud sync endpoints over an AquaStore, with a link that fails in both directions.</summary>
    private sealed class FlakyCloud(AquaStore store, string apiKey) : HttpMessageHandler
    {
        public int Calls, LostRequests, LostResponses;
        public Func<int, bool> LoseRequest { get; init; } = _ => false;
        public Func<int, bool> LoseResponse { get; init; } = _ => false;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            int call = ++Calls;
            if (req.Headers.GetValues("X-Api-Key").Single() != apiKey) return new(HttpStatusCode.Unauthorized);
            var parts = req.RequestUri!.AbsolutePath.Trim('/').Split('/');          // t/{tenant}/sync/{gateway}/{op}
            string gateway = parts[3], op = parts[4];
            if (op == "config")
            {
                if (await store.GetGatewayConfigAsync(gateway, ct) is not { } cfg) return new(HttpStatusCode.NotFound);
                string etag = $"\"{cfg.Version}\"";
                if (req.Headers.IfNoneMatch.ToString() == etag) return new(HttpStatusCode.NotModified);
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cfg.Json, Encoding.UTF8, "application/json") };
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                return ok;
            }
            if (op == "readings" && call > 1 && LoseRequest(call)) { LostRequests++; throw new HttpRequestException("link down"); }
            var (gw, batch) = SyncWire.ParseBatch(await req.Content!.ReadAsByteArrayAsync(ct));
            var (acked, _, _) = await store.IngestGatewayBatchAsync(gw, batch, ct);
            if (LoseResponse(call)) { LostResponses++; throw new HttpRequestException("connection reset after the cloud stored the batch"); }
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"acked\":{acked}}}") };
        }
    }

    [Fact]
    public async Task Flaky_link_delivers_every_reading_exactly_once()
    {
        string root = TempDir();
        using var store = AquaStore.OpenSqlite(Path.Combine(root, "cloud.db"));
        var cloud = new FlakyCloud(store, "k-123") { LoseRequest = c => c % 3 == 0, LoseResponse = c => c % 4 == 0 };
        using var http = new HttpClient(cloud);
        var client = new CloudSyncClient(http, new Uri("https://cloud.example/"), "koperasi-a", "k-123", "gw-1") { BatchSize = 40 };
        using var outbox = new EdgeOutbox(Path.Combine(root, "outbox")) { SegmentBytes = 4096, Durable = false };

        var errors = 0;
        for (int hour = 0; hour < 24; hour++)
        {
            outbox.Append(Readings(30, hour * 30));
            var r = await client.SyncOnceAsync(outbox);
            if (r.Error is not null) { errors++; Assert.False(r.Online); }
        }
        for (int i = 0; i < 50 && outbox.Pending > 0; i++) await client.SyncOnceAsync(outbox);   // link back

        Assert.True(errors > 0 && cloud.LostRequests > 0 && cloud.LostResponses > 0);
        Assert.Equal(0, outbox.Pending);
        var stored = (await store.QueryReadingsAsync("P-00")).Count + (await store.QueryReadingsAsync("P-01")).Count + (await store.QueryReadingsAsync("P-02")).Count;
        Assert.Equal(720, stored);                                                   // no loss, no duplicates
        var gw = Assert.Single(await store.ListGatewaysAsync());
        Assert.Equal(720, gw.AckedSeq);
        Assert.True(gw.Duplicates > 0);                                              // retries after lost responses were skipped
    }

    [Fact]
    public async Task Gateway_pulls_config_once_per_version()
    {
        string root = TempDir();
        using var store = AquaStore.OpenSqlite(Path.Combine(root, "cloud.db"));
        using var http = new HttpClient(new FlakyCloud(store, "k"));
        var client = new CloudSyncClient(http, new Uri("https://cloud.example/"), "t1", "k", "gw-2");
        Assert.False(await client.PullConfigAsync());                               // none yet
        await store.SetGatewayConfigAsync("gw-2", "{\"doSetPoint\":4.5,\"species\":\"vaname\"}");
        GatewayConfig? seen = null;
        client.ConfigChanged += c => seen = c;
        Assert.True(await client.PullConfigAsync());
        Assert.Equal(4.5, seen!.Number("doSetPoint"));
        Assert.False(await client.PullConfigAsync());                               // 304
        await store.SetGatewayConfigAsync("gw-2", "{\"doSetPoint\":5.0}");
        Assert.True(await client.PullConfigAsync());
        Assert.Equal(5.0, client.Config!.Number("doSetPoint"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetGatewayConfigAsync("gw-2", "[1,2]"));
    }
}
