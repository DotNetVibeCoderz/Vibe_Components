using System.Net;
using System.Net.Sockets;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;
using AquaCulture.Data;
using AquaCulture.DigitalTwin;
using AquaCulture.Environment;
using AquaCulture.Gallery.Shared;
using AquaCulture.GIS;
using AquaCulture.GIS.Gdal;
using AquaCulture.IoT;
using AquaCulture.IoT.OpcUa;
using AquaCulture.WaterQuality;
using Microsoft.Data.Sqlite;

namespace AquaCulture.Tests;

public class ConnectorParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Bmkg_forecast_parses_location_and_three_hourly_steps()
    {
        var f = BmkgClient.Parse(Fixture("bmkg-kemayoran.json"));
        Assert.Equal("31.71.03.1001", f.Location.Adm4);
        Assert.Equal("Kemayoran", f.Location.Village);
        Assert.Equal(-6.16, f.Location.Latitude, 2);
        Assert.Equal(21, f.Hourly.Count);
        var first = f.Hourly[0];
        Assert.Equal(new DateTime(2026, 11, 12, 10, 0, 0), first.Time);
        Assert.Equal(32, first.AirTempC);
        Assert.Equal(4.5 / 3.6, first.WindMs, 6);
        Assert.Equal(77, first.CloudPct);
        Assert.All(f.Hourly, h => Assert.InRange(h.SolarWm2, 0, 1100));
        Assert.Equal("Partly Cloudy", f.Conditions[0].Description.En);
        Assert.Equal("Cerah Berawan", f.Conditions[0].Description.Id);

        var hourly = BmkgClient.ToHourly(f.Hourly, f.Location.Latitude);
        Assert.True(hourly.Count >= 3 * (f.Hourly.Count - 1));
        Assert.All(hourly.Zip(hourly.Skip(1)), p => Assert.Equal(TimeSpan.FromHours(1), p.Second.Time - p.First.Time));
    }

    [Fact]
    public void Bmkg_error_response_throws()
    {
        Assert.Throws<InvalidOperationException>(() => BmkgClient.Parse("""{"error":"Data not found"}"""));
    }

    [Fact]
    public void Nasa_power_daily_history_feeds_climate_risk()
    {
        var days = NasaPowerClient.Parse(Fixture("nasa-power-indramayu-2025-01.json"));
        Assert.Equal(31, days.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), days[0].Date);
        Assert.Equal(26.48, days[0].TmeanC, 2);
        Assert.All(days, d => Assert.InRange(d.PressureHPa, 950, 1050));
        Assert.All(days, d => Assert.True(d.TmaxC >= d.TminC));
        var risk = ClimateRiskAnalysis.Analyze(days, Core.Farming.CultureSystem.EarthenPond);
        Assert.NotNull(risk);
    }

    [Fact]
    public void Marine_forecast_parses_and_assesses_cage_risk()
    {
        var sea = MarineForecastClient.Parse(Fixture("open-meteo-marine.json"));
        Assert.Equal(48, sea.Count);
        Assert.Equal(30.7, sea[0].SeaSurfaceTempC, 2);
        Assert.Equal(1.7 / 3.6, sea.Max(s => s.CurrentMs), 6);
        var risk = MarineForecastClient.Assess(sea, SpeciesCatalog.Get("kerapu"));
        Assert.Equal(0.42, risk.MaxWaveM, 2);
        Assert.DoesNotContain(risk.Impacts, i => i.Code.StartsWith("wave", StringComparison.Ordinal));

        var storm = sea.Select(s => s with { WaveHeightM = 2.8, CurrentMs = 0.9 }).ToList();
        var bad = MarineForecastClient.Assess(storm, SpeciesCatalog.Get("kerapu"));
        Assert.Contains(bad.Impacts, i => i.Severity == ImpactSeverity.Severe);
    }

    [Fact]
    public void ChirpStack_uplink_decodes_pond_codec_object_and_tags()
    {
        var payload = Convert.ToBase64String(LoRaPayload.EncodePond(5.4, 29.2, 7.9, 18, 0.35, 3.6));
        string json = $$$"""
        {"deduplicationId":"x","time":"2026-11-12T11:00:00Z",
         "deviceInfo":{"devEui":"a84041000181c061","deviceName":"probe-07","tags":{"pond":"P-03"}},
         "fPort":2,"data":"{{{payload}}}"}
        """;
        var r = ChirpStack.ParseUplink(json);
        Assert.All(r, x => { Assert.Equal("P-03", x.PondId); Assert.Equal("probe-07", x.DeviceId); });
        Assert.Equal(5.4, r.Single(x => x.Kind == SensorKind.DissolvedOxygen).Value, 1);
        Assert.Equal(new DateTime(2026, 11, 12, 11, 0, 0, DateTimeKind.Utc), r[0].Timestamp);

        string decoded = """
        {"time":"2026-11-12T11:00:00Z","deviceInfo":{"devEui":"a84041000181c061"},"data":"",
         "object":{"temperature":28.4,"ph":8.1,"label":"x"}}
        """;
        var o = ChirpStack.ParseUplink(decoded, eui => eui == "a84041000181c061" ? "P-09" : "");
        Assert.Equal(2, o.Count);
        Assert.All(o, x => Assert.Equal("P-09", x.PondId));
        Assert.True(ChirpStack.IsUplink("UP"));
        Assert.False(ChirpStack.IsUplink("join"));
    }
}

public class ModbusRtuTests
{
    /// <summary>A fake RS-485 probe: answers read-holding-register requests from a register bank.</summary>
    private sealed class FakeProbe(byte unit, ushort[] bank, bool exception = false) : Stream
    {
        private readonly Queue<byte> _out = new();
        public int Requests { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var req = buffer.AsSpan(offset, count);
            Requests++;
            Assert.Equal(Modbus.Crc16(req[..6]), (ushort)(req[6] | req[7] << 8));
            if (req[0] != unit) return;
            if (exception)
            {
                byte[] ex = [unit, 0x83, 0x02, 0, 0];
                var crc = Modbus.Crc16(ex.AsSpan(0, 3)); ex[3] = (byte)crc; ex[4] = (byte)(crc >> 8);
                foreach (var b in ex) _out.Enqueue(b);
                return;
            }
            int addr = req[2] << 8 | req[3], n = req[4] << 8 | req[5];
            foreach (var b in Modbus.BuildReadResponseRtu(unit, bank.AsSpan(addr, n))) _out.Enqueue(b);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && _out.Count > 0) buffer[offset + n++] = _out.Dequeue();
            return n;
        }
    }

    [Fact]
    public async Task Rtu_client_reads_probe_over_a_stream()
    {
        ushort[] bank = [512, 291, 785, unchecked((ushort)(short)-120), 205, 343, 0, 0];
        var probe = new FakeProbe(7, bank);
        await using var client = new ModbusRtuClient(probe);
        var regs = await client.ReadHoldingRegistersAsync(7, 1, 3);
        Assert.Equal(new ushort[] { 291, 785, unchecked((ushort)(short)-120) }, regs);

        var readings = await client.ReadProbeAsync(7, "probe-7", "P-01", Modbus.TypicalProbeMap);
        var v = readings.ToDictionary(r => r.Kind, r => r.Value);
        Assert.Equal(5.12, v[SensorKind.DissolvedOxygen], 6);
        Assert.Equal(-120, v[SensorKind.Orp]);
        Assert.Equal(34.3, v[SensorKind.Turbidity], 6);
        Assert.All(readings, r => Assert.Equal("P-01", r.PondId));
    }

    [Fact]
    public async Task Rtu_client_surfaces_exceptions_and_timeouts()
    {
        await using var bad = new ModbusRtuClient(new FakeProbe(3, new ushort[8], exception: true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bad.ReadHoldingRegistersAsync(3, 0, 2));
        Assert.Contains("code 2", ex.Message);

        // wrong unit: the probe stays silent and the read fails (end of stream or timeout)
        await using var silent = new ModbusRtuClient(new FakeProbe(3, new ushort[8])) { Timeout = TimeSpan.FromMilliseconds(200) };
        await Assert.ThrowsAnyAsync<Exception>(() => silent.ReadHoldingRegistersAsync(9, 0, 2));
    }
}

public class DataStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"aqua-test-{Guid.NewGuid():N}.db");

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public async Task Farm_round_trips_with_all_cycle_logs()
    {
        var path = TempDb();
        try
        {
            using var store = AquaStore.OpenSqlite(path);
            var farm = DemoFarm.Farm;
            await store.SaveFarmAsync(farm);
            await store.SaveFarmAsync(farm); // idempotent replace

            var back = await store.LoadFarmAsync(farm.Id);
            Assert.NotNull(back);
            Assert.Equal(farm.Name, back!.Name);
            Assert.Equal(farm.Ponds.Count, back.Ponds.Count);
            Assert.Equal(farm.Ponds[0].Outline!.Ring.Count, back.Ponds[0].Outline!.Ring.Count + 0);
            Assert.Equal(farm.Ponds[0].Outline!.Ring[0].Latitude, back.Ponds[0].Outline!.Ring[0].Latitude, 6);
            Assert.Equal(farm.Cycles.Count, back.Cycles.Count);
            for (int i = 0; i < farm.Cycles.Count; i++)
            {
                var a = farm.Cycles[i]; var b = back.Cycles.Single(c => c.Id == a.Id);
                Assert.Equal(a.Species.Id, b.Species.Id);
                Assert.Equal(a.StockedCount, b.StockedCount);
                Assert.Equal(a.Samplings.Count, b.Samplings.Count);
                Assert.Equal(a.Feedings.Sum(f => f.Kg), b.Feedings.Sum(f => f.Kg), 6);
                Assert.Equal(a.Mortalities.Sum(m => m.Count), b.Mortalities.Sum(m => m.Count));
                Assert.Equal(a.Harvests.Count, b.Harvests.Count);
                Assert.Equal(a.Expenses.Sum(x => x.Amount), b.Expenses.Sum(x => x.Amount), 6);
            }
            Assert.Null(await store.LoadFarmAsync("nope"));
            Assert.Single(await store.ListFarmsAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Readings_query_series_purge_and_decisions()
    {
        var path = TempDb();
        try
        {
            using var store = AquaStore.OpenSqlite(path);
            var t0 = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
            var rows = Enumerable.Range(0, 144).SelectMany(i => new[]
            {
                new SensorReading("probe-1", "P-01", SensorKind.DissolvedOxygen, 4 + Math.Sin(i / 20.0), t0.AddMinutes(10 * i), "mg/L"),
                new SensorReading("probe-1", "P-01", SensorKind.Temperature, 29, t0.AddMinutes(10 * i), "°C"),
                new SensorReading("probe-2", "P-02", SensorKind.DissolvedOxygen, 5, t0.AddMinutes(10 * i), "mg/L"),
            }).ToList();
            Assert.Equal(432, await store.AppendReadingsAsync(rows));

            var dox = await store.QueryReadingsAsync("P-01", SensorKind.DissolvedOxygen, t0.AddHours(6), t0.AddHours(12));
            Assert.Equal(37, dox.Count);
            Assert.Equal(DateTimeKind.Utc, dox[0].Timestamp.Kind);
            var series = await store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, t0, t0.AddDays(1));
            Assert.Equal(144, series.Count);
            Assert.Equal(4 + Math.Sin(1 / 20.0), series[1].Value, 9);

            Assert.Equal(3 * 72, await store.PurgeReadingsAsync(t0.AddHours(12)));
            Assert.Equal(72, (await store.QueryReadingsAsync("P-02")).Count);

            await store.AppendDecisionsAsync([new TwinDecision(t0, "P-01", DecisionKind.Aeration, AlertSeverity.Warning, new("Run aerators", "Nyalakan aerator"), new("Dawn DO low", "DO subuh rendah"))]);
            var d = Assert.Single(await store.QueryDecisionsAsync("P-01"));
            Assert.Equal("Nyalakan aerator", d.TitleId);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Recording_transport_persists_every_reading()
    {
        var path = TempDb();
        try
        {
            using var store = AquaStore.OpenSqlite(path);
            var sim = new SimulatedSensorTransport([new SimulatedPond("P-01", "probe-1")]) { DropoutProbability = 0 };
            int seen = 0;
            await using (var rec = new RecordingTransport(sim, store, batchSize: 10))
            {
                rec.ReadingReceived += _ => seen++;
                await rec.ConnectAsync();
                var t0 = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < 4; i++) sim.Emit(t0.AddMinutes(10 * i));   // 4 × 7 readings, flushed in batches of 10 + the rest on dispose
                Assert.Equal(20, rec.Recorded);
            }
            Assert.Equal(28, seen);
            Assert.Equal(28, (await store.QueryReadingsAsync("P-01")).Count);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Parquet_and_csv_round_trip()
    {
        var t0 = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 50).Select(i => new SensorReading($"probe,{i % 3}", "P-01", i % 2 == 0 ? SensorKind.Ph : SensorKind.Salinity, 7.5 + i * 0.01, t0.AddMinutes(i), i % 2 == 0 ? "pH" : "ppt")).ToList();

        using var ms = new MemoryStream();
        await TimeSeriesExport.WriteParquetAsync(rows, ms);
        Assert.True(ms.Length > 100);
        ms.Position = 0;
        Assert.Equal("PAR1", System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
        var back = await TimeSeriesExport.ReadParquetAsync(ms);
        Assert.Equal(rows, back);

        var csv = TimeSeriesExport.WriteCsv(rows);
        Assert.StartsWith("timestamp_utc,pond_id,device_id,kind,value,unit", csv);
        Assert.Contains("\"probe,1\"", csv);
        Assert.Equal(rows, TimeSeriesExport.ReadCsv(csv));
    }
}

public class OpcUaTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Simulator_reader_poller_and_aerator_write()
    {
        var pki = Path.Combine(Path.GetTempPath(), "aqua-opcua-tests");
        await using var sim = new PondOpcUaSimulator(["P-01", "P-02"], FreePort()) { Interval = TimeSpan.FromMilliseconds(100), PkiPath = Path.Combine(pki, "server") };
        await sim.StartAsync();
        await using var reader = new OpcUaReader(new OpcUaReaderOptions(sim.EndpointUrl) { PkiPath = Path.Combine(pki, "client") });
        await reader.ConnectAsync();
        Assert.True(reader.IsConnected);

        var poller = new OpcUaSensorPoller(reader, sim.NodeMap());
        var readings = await poller.PollAsync();
        Assert.Equal(12, readings.Count);
        var do1 = readings.Single(r => r.PondId == "P-01" && r.Kind == SensorKind.DissolvedOxygen).Value;
        Assert.InRange(do1, 0, 20);
        Assert.InRange(readings.Single(r => r.PondId == "P-02" && r.Kind == SensorKind.Temperature).Value, 20, 40);

        var aerator = sim.NodeId("P-01", "Aerator");
        Assert.True(await reader.WriteAsync(aerator, true));
        var after = await reader.ReadAsync([aerator]);
        Assert.Equal(1, after[aerator]);
    }
}

public class GdalTests
{
    [Fact]
    public void GeoTiff_round_trip_keeps_bounds_values_and_nodata()
    {
        var bounds = new GeoBounds(-6.40, 108.30, -6.37, 108.34);
        var r = new GeoRaster(bounds, 4, 3);
        for (int i = 0; i < r.Data.Length; i++) r.Data[i] = i * 0.5f;
        r.Data[5] = float.NaN;
        var b2 = new GeoRaster(bounds, 4, 3, Enumerable.Range(0, 12).Select(i => (float)-i).ToArray());
        var path = Path.Combine(Path.GetTempPath(), $"aqua-{Guid.NewGuid():N}.tif");
        try
        {
            GdalRaster.WriteGeoTiff(path, r, b2);
            var back = GdalRaster.Read(path);
            Assert.Equal(4, back.Width);
            Assert.Equal(3, back.Height);
            Assert.Equal(bounds.MinLat, back.Bounds.MinLat, 9);
            Assert.Equal(bounds.MaxLon, back.Bounds.MaxLon, 9);
            Assert.Equal(1.5f, back[3, 0]);
            Assert.True(float.IsNaN(back.Data[5]));
            var all = GdalRaster.ReadAllBands(path);
            Assert.Equal(2, all.Count);
            Assert.Equal(-11f, all[1].Data[11]);
            Assert.False(string.IsNullOrEmpty(GdalRaster.Version));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void Water_colour_indices_follow_published_formulas()
    {
        var bounds = new GeoBounds(-6.40, 108.30, -6.39, 108.31);
        // pixel 0: clear water, 1: green bloom, 2: land
        var green = new GeoRaster(bounds, 3, 1, [0.06f, 0.09f, 0.08f]);
        var red = new GeoRaster(bounds, 3, 1, [0.03f, 0.04f, 0.10f]);
        var redEdge = new GeoRaster(bounds, 3, 1, [0.03f, 0.07f, 0.15f]);
        var nir = new GeoRaster(bounds, 3, 1, [0.01f, 0.03f, 0.30f]);

        var ndwi = WaterColour.Ndwi(green, nir);
        Assert.Equal((0.06 - 0.01) / 0.07, ndwi.Data[0], 5);
        var mask = WaterColour.WaterMask(ndwi);
        Assert.Equal([1f, 1f, 0f], mask.Data);

        var ndci = WaterColour.Ndci(redEdge, red);
        Assert.Equal(0, ndci.Data[0], 6);
        var chl = WaterColour.Chlorophyll(ndci, mask);
        Assert.Equal(14.039, chl.Data[0], 3);
        double n1 = (0.07 - 0.04) / 0.11;
        Assert.Equal(14.039 + 86.115 * n1 + 194.325 * n1 * n1, chl.Data[1], 2);
        Assert.True(float.IsNaN(chl.Data[2]));
        Assert.Equal("mesotrophic", WaterColour.TrophicState(chl.Data[0]).En);
        Assert.Equal("eutrofik", WaterColour.TrophicState(chl.Data[1]).Id);

        Assert.Equal(228.1 * 0.03 / (1 - 0.03 / 0.1641), WaterColour.TurbidityFromRed(0.03), 9);
        var s = WaterColour.Summarize(chl, mask);
        Assert.Equal(2, s.Pixels);
        Assert.True(s.AreaHa > 0);
    }
}
