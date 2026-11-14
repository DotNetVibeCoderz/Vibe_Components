using System.Numerics;
using System.Text.Json;
using Electrical.Core.Network;
using Electrical.DigitalTwin;
using Electrical.DigitalTwin.ThreeD;
using Electrical.PowerFlow;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class GridScene3DTests(ITestOutputHelper output)
{
    [Fact]
    public void Case14Scene_CountsMatchNetworkElements()
    {
        var net = TestCases.Case14();
        var s = GridSceneBuilder.Build(net);
        int trafos = net.Branches.Count(b => b.IsTransformer);
        Assert.Equal(net.BusCount, s.Count(GridElementKind.Busbar));
        Assert.Equal(trafos, s.Count(GridElementKind.Transformer));
        Assert.Equal(net.Branches.Count - trafos, s.Count(GridElementKind.OverheadLine));
        Assert.Equal(net.Branches.Count, s.Count(GridElementKind.Breaker));
        Assert.Equal(net.Branches.Count, s.Count(GridElementKind.Disconnector));
        Assert.Equal(net.Generators.Count, s.Count(GridElementKind.Generator));
        Assert.Equal(net.Loads.Count, s.Count(GridElementKind.Load));
        Assert.Equal(1, s.Count(GridElementKind.Ground));
        int towers = net.Branches.Where(b => !b.IsTransformer).Sum(b =>
            GridSceneBuilder.TowerCount(s.BusPositions[b.From], s.BusPositions[b.To], s.Options));
        Assert.Equal(towers, s.Count(GridElementKind.Tower));
        Assert.True(towers > 0);
        Assert.All(net.Buses, b => Assert.Single(s.ForBus(b.Id), o => o.Kind == GridElementKind.Busbar));
        output.WriteLine($"{s.Objects.Count} objects, {s.TriangleCount} triangles, {towers} towers");
    }

    [Fact]
    public void Meshes_AreWoundOutward()
    {
        var m = new MeshData().AddBox(new Vector3(1, 2, 3), new Vector3(1, 2, 0.5f))
            .AddCylinder(Vector3.Zero, new Vector3(0, 3, 1), 0.5f, 0.3f)
            .AddTube([Vector3.Zero, new Vector3(5, 1, 0), new Vector3(9, 0, 3)], 0.2f);
        for (int t = 0; t < m.TriangleCount; t++)
        {
            uint a = m.Indices[3 * t], b = m.Indices[3 * t + 1], c = m.Indices[3 * t + 2];
            var face = Vector3.Cross(m.Positions[(int)b] - m.Positions[(int)a], m.Positions[(int)c] - m.Positions[(int)a]);
            if (face.LengthSquared() < 1e-12f) continue;
            Assert.True(Vector3.Dot(face, m.Normals[(int)a]) > 0, $"triangle {t}");
        }
    }

    [Fact]
    public void Catenary_MidSpanSagMatches()
    {
        double a = Catenary.Parameter(300, 6);
        Assert.Equal(6, a * (Math.Cosh(150 / a) - 1), 8);
        var p = Catenary.Points(new Vector3(0, 20, 0), new Vector3(300, 20, 0), 6, 20);
        Assert.Equal(21, p.Length);
        Assert.Equal(14, p[10].Y, 3);
        Assert.Equal(20, p[0].Y, 4);
        Assert.Equal(20, p[^1].Y, 4);
        Assert.True(p[5].Y > p[10].Y);
    }

    [Fact]
    public void Colours_FollowVoltageAndLoading()
    {
        var net = TestCases.Case33BW();
        var s = GridSceneBuilder.Build(net);
        var bar18 = s.ForBus(18).Single(o => o.Kind == GridElementKind.Busbar);
        var bar1 = s.ForBus(1).Single(o => o.Kind == GridElementKind.Busbar);
        Assert.Equal(bar18.BaseColor, bar18.Color);

        var pf = PowerFlowSolver.Solve(net);
        s.ApplyPowerFlow(pf);
        Assert.NotEqual(bar1.Color, bar18.Color);  // 1.0 pu vs ≈ 0.913 pu
        Assert.Contains("0.913", bar18.Label);
        var line = s.ForBranch(0).Single(o => o.Kind == GridElementKind.OverheadLine);
        var light = s.ForBranch(16).Single(o => o.Kind == GridElementKind.OverheadLine);
        Assert.NotEqual(line.Color, light.Color);
        var before = line.Color;
        int v = line.Version;

        // Heavier loading → the feeder head moves along the loading ramp.
        var heavy = net.Clone();
        foreach (var l in heavy.Loads) { l.P *= 1.6; l.Q *= 1.6; }
        s.ApplyPowerFlow(PowerFlowSolver.Solve(heavy) is var r && r.Converged ? r : throw new Exception("diverged"));
        Assert.Equal(line.Color, before); // relative reference: the head stays the heaviest branch
        var fixedRef = GridSceneBuilder.Build(net, new GridSceneOptions { UnratedReferenceMva = 8 });
        fixedRef.ApplyPowerFlow(pf);
        var c1 = fixedRef.ForBranch(0).Single(o => o.Kind == GridElementKind.OverheadLine).Color;
        fixedRef.ApplyPowerFlow(PowerFlowSolver.Solve(heavy));
        var lineFixed = fixedRef.ForBranch(0).Single(o => o.Kind == GridElementKind.OverheadLine);
        Assert.NotEqual(c1, lineFixed.Color);
        Assert.True(lineFixed.Color.R > c1.R); // green → amber/red
        Assert.True(line.Version >= v);

        // Opening a branch turns its switchgear green and the conductor grey.
        var open = TestCases.Case14();
        open.Branches[0].InService = false; // line 1-2 (the grid stays connected through 1-5)
        var so = GridSceneBuilder.Build(open);
        so.ApplyPowerFlow(PowerFlowSolver.Solve(open));
        Assert.False(so.ForBranch(0).Single(o => o.Kind == GridElementKind.Breaker).Closed);
        Assert.Equal("#2bb673", so.ForBranch(0).Single(o => o.Kind == GridElementKind.Breaker).Color.Hex);
        Assert.Equal("#6b7280", so.ForBranch(0).Single(o => o.Kind == GridElementKind.OverheadLine).Color.Hex);
        Assert.Equal("#e5484d", so.ForBranch(1).Single(o => o.Kind == GridElementKind.Breaker).Color.Hex);
    }

    [Fact]
    public void TwinAlarms_Flash()
    {
        var net = TestCases.Case33BW();
        var sim = new TwinSimulator(net, new TwinSimulatorOptions { Steps = 20, PeakLoadScale = 1.2 });
        TwinState? state = null;
        for (int i = 0; i < 20; i++) { state = sim.Step(); if (state.Alarms.Any(a => a.Element.StartsWith("bus "))) break; }
        Assert.NotNull(state);
        var alarmBus = state!.Alarms.First(a => a.Element.StartsWith("bus ")).Element;
        int id = int.Parse(alarmBus[4..]);
        var s = GridSceneBuilder.Build(net);
        s.ApplyTwinState(state);
        var bar = s.ForBus(id).Single(o => o.Kind == GridElementKind.Busbar);
        Assert.True(bar.Alarm);
        Assert.True(s.UpdateAlarmFlash(0.1) > 0);
        Assert.True(bar.Emission > 0);
        float e1 = bar.Emission;
        s.UpdateAlarmFlash(0.4);
        Assert.NotEqual(e1, bar.Emission);
        Assert.Equal(0, s.ForBus(1).Single(o => o.Kind == GridElementKind.Busbar).Emission);
    }

    [Fact]
    public void GlbExport_IsValidGltf2()
    {
        var net = TestCases.Case14();
        var s = GridSceneBuilder.Build(net);
        s.ApplyPowerFlow(PowerFlowSolver.Solve(net));
        var glb = GlbExporter.Export(s);
        Assert.Equal((byte)'g', glb[0]); Assert.Equal((byte)'l', glb[1]); Assert.Equal((byte)'T', glb[2]); Assert.Equal((byte)'F', glb[3]);
        var (version, length, json) = GlbExporter.ReadHeader(glb);
        Assert.Equal(2u, version);
        Assert.Equal((uint)glb.Length, length);
        Assert.Equal(0, glb.Length % 4);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        int objects = s.Objects.Count(o => o.Mesh.VertexCount > 0);
        Assert.Equal(objects, root.GetProperty("meshes").GetArrayLength());
        Assert.Equal(objects, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(3 * objects, root.GetProperty("accessors").GetArrayLength());
        // Buffer length matches the BIN chunk.
        int jsonLen = BitConverter.ToInt32(glb, 12);
        int binLen = BitConverter.ToInt32(glb, 20 + jsonLen);
        Assert.Equal(binLen, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());
        Assert.Equal(glb.Length, 20 + jsonLen + 8 + binLen);
        var busNode = root.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("extras").GetProperty("kind").GetString() == "Busbar");
        Assert.True(busNode.GetProperty("extras").TryGetProperty("busId", out _));
        output.WriteLine($"GLB {glb.Length / 1024} KiB, {objects} meshes");
    }

    [Fact]
    public void GeographicLayout_UsesBusLocations()
    {
        var net = TestCases.SyntheticGrid(20);
        Assert.All(net.Buses, b => Assert.NotNull(b.Location));
        var s = GridSceneBuilder.Build(net, new GridSceneOptions { ShowLoads = false, ShowGenerators = false });
        var b0 = net.Buses[0]; var b1 = net.Buses[1];
        var d3 = Vector3.Distance(s.BusPositions[b0.Id], s.BusPositions[b1.Id]);
        double km = b0.Location!.Value.DistanceKm(b1.Location!.Value);
        Assert.Equal(km * s.Options.MetresPerKm, d3, km * s.Options.MetresPerKm * 0.01);
        Assert.Equal(0, s.Count(GridElementKind.Load));
    }

    [Fact]
    public void ThreeNet_RealizesSceneAndRendersPng()
    {
        var net = TestCases.Case14();
        var s = GridSceneBuilder.Build(net);
        s.ApplyPowerFlow(PowerFlowSolver.Solve(net));
        ThreeNetGridScene tn;
        try { tn = ThreeNetGridScene.Create(s); }
        catch (Exception e) when (e is DllNotFoundException or ThreeNet.ThreeNetException or BadImageFormatException)
        {
            output.WriteLine($"ThreeNet native core unavailable: {e.Message}");
            return;
        }
        using (tn)
        {
            Assert.Equal(s.Objects.Count(o => o.Mesh.VertexCount > 0), tn.NodeCount);
            Assert.Equal(0, tn.Sync());
            s.UpdateAlarmFlash(0); // no alarms → no change
            var bar = s.ForBus(1).Single(o => o.Kind == GridElementKind.Busbar);
            var heavy = net.Clone();
            heavy.Loads.ForEach(l => l.P *= 1.3);
            s.ApplyPowerFlow(PowerFlowSolver.Solve(heavy));
            Assert.True(tn.Sync() > 0);
            Assert.Equal(GlbExporter.ToLinear(bar.Color).X, tn.MaterialOf(bar).Options.BaseColor.X, 5);
            byte[] png;
            try { png = tn.RenderPng(320, 200); }
            catch (ThreeNet.ThreeNetException e)
            {
                output.WriteLine($"No GPU adapter for off-screen rendering: {e.Message}");
                return;
            }
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            output.WriteLine($"PNG {png.Length} bytes");
            if (Environment.GetEnvironmentVariable("ELECTRICAL_3D_PNG") is { Length: > 0 } outPath) File.WriteAllBytes(outPath, tn.RenderPng(1280, 720));
        }
    }
}
