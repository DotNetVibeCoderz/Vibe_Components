using System.Xml.Linq;
using Electrical.CIM;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Interop;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

/// <summary>CGMES 3.0 (IEC 61970-600-1/-2 Ed.2, CIM100) export, import and validation.</summary>
public class Cgmes3Tests
{
    private static readonly XNamespace Cim = Cgmes3Namespaces.Cim;
    private static readonly XNamespace Md = Cgmes3Namespaces.Md;
    private static readonly XNamespace Rdf = CimNamespaces.Rdf;

    private static PowerNetwork Case(string name) => name switch
    {
        "case9" => TestCases.Case9(),
        "case14" => TestCases.Case14(),
        "case33bw" => TestCases.Case33BW(),
        _ => throw new ArgumentException(name),
    };

    private static void AssertSamePowerFlow(PowerNetwork a, PowerNetwork b)
    {
        var ra = PowerFlowSolver.Solve(a);
        var rb = PowerFlowSolver.Solve(b);
        Assert.True(ra.Converged && rb.Converged);
        Assert.Equal(ra.LossesMw, rb.LossesMw, 1e-6);
        Assert.Equal(ra.Buses.Count, rb.Buses.Count);
        for (int i = 0; i < ra.Buses.Count; i++)
        {
            Assert.Equal(ra.Buses[i].BusId, rb.Buses[i].BusId);
            Assert.Equal(ra.Buses[i].Vm, rb.Buses[i].Vm, 1e-9);
            Assert.Equal(ra.Buses[i].VaDeg, rb.Buses[i].VaDeg, 1e-7);
        }
        for (int i = 0; i < ra.Generators.Count; i++) Assert.Equal(ra.Generators[i].P, rb.Generators[i].P, 1e-6);
    }

    private static XDocument[] Sample(bool withSv = true)
    {
        var docs = new List<XDocument>
        {
            XDocument.Parse(Cgmes3Samples.Equipment), XDocument.Parse(Cgmes3Samples.Topology), XDocument.Parse(Cgmes3Samples.SteadyStateHypothesis),
        };
        if (withSv) docs.Add(XDocument.Parse(Cgmes3Samples.StateVariables));
        return [.. docs];
    }

    [Theory]
    [InlineData("case9", CgmesTopologyStyle.ConnectivityNodes, true)]
    [InlineData("case9", CgmesTopologyStyle.NodeBreaker, false)]
    [InlineData("case14", CgmesTopologyStyle.ConnectivityNodes, true)]
    [InlineData("case14", CgmesTopologyStyle.NodeBreaker, true)]
    [InlineData("case14", CgmesTopologyStyle.BusBranch, false)]
    [InlineData("case33bw", CgmesTopologyStyle.ConnectivityNodes, false)]
    [InlineData("case33bw", CgmesTopologyStyle.BusBranch, true)]
    public void RoundTrip_IdenticalPowerFlow(string name, CgmesTopologyStyle style, bool extensions)
    {
        var net = Case(name);
        var set = Cgmes3Exporter.Export(net, PowerFlowSolver.Solve(net), new Cgmes3ExportOptions { Topology = style, IncludeExtensions = extensions });
        var r = CgmesImporter.Import(set, new CimImportOptions { BaseMva = net.BaseMva, UseExtensions = extensions });
        Assert.Equal(CgmesVersion.Cgmes3_0, r.Version);
        Assert.Empty(r.Warnings);
        var back = r.Network;
        Assert.Equal(net.BusCount, back.BusCount);
        Assert.Equal(net.Branches.Count, back.Branches.Count);
        Assert.Equal(net.Generators.Count, back.Generators.Count);
        Assert.Equal(net.Loads.Count, back.Loads.Count);
        AssertSamePowerFlow(net, back);
        if (!extensions) Assert.DoesNotContain(set.All, d => d.Descendants().Any(e => e.Name.NamespaceName == CimNamespaces.Eln));
    }

    [Fact]
    public void Case14_PowerFlowLossesSurviveRoundTrip()
    {
        // MATPOWER case14 reference losses: 13.393 MW.
        var net = TestCases.Case14();
        var back = CgmesImporter.Import(Cgmes3Exporter.Export(net)).Network;
        Assert.Equal(13.393, PowerFlowSolver.Solve(back).LossesMw, 2);
        Assert.Equal(19, back.GetBus(9).Bs, 9);
        for (int k = 0; k < net.Branches.Count; k++)
        {
            Assert.Equal(net.Branches[k].EffectiveTap, back.Branches[k].EffectiveTap, 12);
            Assert.Equal(net.Branches[k].RateA, back.Branches[k].RateA, 9);
        }
    }

    [Fact]
    public void Headers_ProfilesDependenciesAndMrids()
    {
        var set = Cgmes3Exporter.Export(TestCases.Case9(), PowerFlowSolver.Solve(TestCases.Case9()));
        var headers = set.All.Select(d => CgmesModelHeader.Read(d)!).ToList();
        Assert.Equal(new[] { CgmesProfileKind.Equipment, CgmesProfileKind.Topology, CgmesProfileKind.SteadyStateHypothesis, CgmesProfileKind.StateVariables },
            headers.Select(h => h.Profile));
        Assert.Equal(Cgmes3Namespaces.EquipmentProfile, headers[0].ProfileUris.Single());
        Assert.Empty(headers[0].DependentOn);
        Assert.Equal(new[] { headers[0].Id }, headers[1].DependentOn);
        Assert.Equal(new[] { headers[0].Id }, headers[2].DependentOn);
        Assert.Equal(new[] { headers[1].Id, headers[2].Id }, headers[3].DependentOn);
        Assert.All(headers, h => Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), h.ScenarioTime));
        Assert.All(headers, h => Assert.StartsWith("urn:uuid:", h.Id));
        Assert.All(set.All, d => Assert.Equal(CgmesVersion.Cgmes3_0, Cgmes3Namespaces.DetectVersion(d)));
        // mRID = rdf:ID without "_", name-based UUID v5
        foreach (var e in set.Equipment.Root!.Elements().Where(e => e.Attribute(Rdf + "ID") is not null))
        {
            string id = (string)e.Attribute(Rdf + "ID")!;
            var mrid = (string?)e.Element(Cim + "IdentifiedObject.mRID");
            if (e.Name.LocalName.EndsWith("TablePoint", StringComparison.Ordinal)) continue;
            Assert.Equal(id[1..], mrid);
            Assert.Equal(5, Guid.Parse(mrid!).Version);
        }
        // SSH holds set-points as rdf:about descriptions, SV the solved state
        Assert.Contains(set.SteadyStateHypothesis.Root!.Elements(Cim + "SynchronousMachine"), e => e.Attribute(Rdf + "about") is not null && e.Element(Cim + "RotatingMachine.p") is not null);
        Assert.Equal(9, set.StateVariables!.Root!.Elements(Cim + "SvVoltage").Count());
        Assert.Equal(set.Equipment.Root!.Elements(Cim + "Terminal").Count(), set.StateVariables.Root!.Elements(Cim + "SvPowerFlow").Count());
    }

    [Fact]
    public void NameBasedUuid_MatchesRfc4122Vector()
    {
        // Python: uuid.uuid5(uuid.NAMESPACE_DNS, "python.org") == 886313e1-3b8a-5372-9b90-0c9aee199e5d
        Assert.Equal(Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d"), CgmesIds.NameBasedUuid(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "python.org"));
        var a = Cgmes3Exporter.Export(TestCases.Case14()).All.Select(CgmesDocumentSet.ToXml).ToList();
        var b = Cgmes3Exporter.Export(TestCases.Case14()).All.Select(CgmesDocumentSet.ToXml).ToList();
        Assert.Equal(a, b); // deterministic output
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case33bw")]
    public void ExportedSetWithPowerFlow_PassesValidation(string name)
    {
        var net = Case(name);
        var pf = PowerFlowSolver.Solve(net);
        var set = Cgmes3Exporter.Export(net, pf, new Cgmes3ExportOptions { Topology = CgmesTopologyStyle.NodeBreaker });
        var report = CgmesValidator.Validate(set, new CgmesValidationOptions { KclToleranceMw = 1e-6 });
        Assert.True(report.Issues.Count == 0, report.ToString());
        Assert.Equal(CgmesVersion.Cgmes3_0, report.Version);
        Assert.Equal(4, report.Models.Count);
    }

    [Fact]
    public void StateVariables_ImportAsInitialVoltages_AndKclHolds()
    {
        var net = TestCases.Case14();
        var pf = PowerFlowSolver.Solve(net);
        var set = Cgmes3Exporter.Export(net, pf);
        var back = CgmesImporter.Import(set).Network;
        foreach (var b in pf.Buses)
        {
            Assert.Equal(b.Vm, back.GetBus(b.BusId).Vm, 12);
            Assert.Equal(b.VaDeg, back.GetBus(b.BusId).VaDeg, 12);
        }
        // Warm start from the SV state converges immediately.
        var warm = PowerFlowSolver.Solve(back, new PowerFlowOptions { FlatStart = false });
        Assert.True(warm.Converged);
        Assert.True(warm.Iterations <= 1);
        // KCL per TopologicalNode: Σ SvPowerFlow.p ≈ 0
        var report = CgmesValidator.Validate(set, new CgmesValidationOptions { KclToleranceMw = 1e-6 });
        Assert.False(report.Has("CGM-SV-001"));
    }

    [Fact]
    public void HandWrittenMinimalSample_ImportsWithoutExtensions()
    {
        var r = CgmesImporter.Import(Sample());
        var net = r.Network;
        Assert.Equal(CgmesVersion.Cgmes3_0, r.Version);
        Assert.Empty(r.Warnings);
        Assert.Equal(2, net.BusCount); // busbar North + line bay merged by the TP node and the closed breaker
        Assert.Equal(r.NodeToBus["_CN1"], r.NodeToBus["_CN1B"]);
        Assert.Equal("Busbar North", net.Buses[0].Name);
        Assert.Equal(BusType.Slack, net.Buses[0].Type);
        Assert.Equal(BusType.PQ, net.Buses[1].Type);
        Assert.All(net.Buses, b => Assert.Equal(110, b.BaseKv));
        var line = Assert.Single(net.Branches);
        Assert.Equal(0.02, line.R, 12);   // 2.42 Ω / (110² / 100 Ω)
        Assert.Equal(0.1, line.X, 12);
        Assert.Equal(0.02, line.B, 9);
        Assert.Equal(Math.Sqrt(3) * 110 * 0.5, line.RateA, 9); // 500 A PATL → 95.26 MVA
        var gen = Assert.Single(net.Generators);
        Assert.Equal(50, gen.P, 12);
        Assert.Equal(1.02, gen.Vset, 12);
        Assert.Equal(200, gen.Pmax, 12);
        Assert.Equal(GeneratorKind.Thermal, gen.Kind);
        var load = Assert.Single(net.Loads);
        Assert.Equal((50.0, 20.0), (load.P, load.Q));
        Assert.Equal(108.5 / 110, net.Buses[1].Vm, 12); // SvVoltage as initial voltage
        Assert.Equal(-2.5, net.Buses[1].VaDeg, 12);
        var pf = PowerFlowSolver.Solve(net);
        Assert.True(pf.Converged);
        Assert.Equal(50, pf.TotalLoadMw, 9);
        Assert.InRange(pf.LossesMw, 0.1, 2);

        // Without TP the breaker alone merges the busbar and the bay.
        var eqSsh = CgmesImporter.Import([XDocument.Parse(Cgmes3Samples.Equipment), XDocument.Parse(Cgmes3Samples.SteadyStateHypothesis)]).Network;
        Assert.Equal(2, eqSsh.BusCount);
        Assert.Equal(pf.LossesMw, PowerFlowSolver.Solve(eqSsh).LossesMw, 9);
    }

    [Fact]
    public void HandWrittenMinimalSample_IsValid()
    {
        var report = CgmesValidator.Validate(Sample());
        Assert.True(report.Issues.Count == 0, report.ToString());
    }

    /// <summary>Deliberately broken variants of the hand-written sample (EQ, TP, SSH, SV = docs[0..3]), one per rule.</summary>
    private static readonly Dictionary<string, Action<XDocument[]>> Breakers = new()
    {
        ["CGM-HDR-001"] = d => d[2].Root!.Element(Md + "FullModel")!.Remove(),
        ["CGM-HDR-002"] = d => d[1].Root!.Element(Md + "FullModel")!.Element(Md + "Model.modelingAuthoritySet")!.Remove(),
        ["CGM-HDR-003"] = d => d[1].Root!.Element(Md + "FullModel")!.Element(Md + "Model.DependentOn")!.SetAttributeValue(Rdf + "resource", "urn:uuid:00000000-0000-4000-8000-00000000dead"),
        ["CGM-HDR-004"] = d => d[2].Root!.Element(Md + "FullModel")!.Element(Md + "Model.scenarioTime")!.Value = "2026-06-01T13:00:00Z",
        ["CGM-HDR-005"] = d => d[3].Root!.Element(Md + "FullModel")!.Elements(Md + "Model.DependentOn").Last().Remove(),
        ["CGM-HDR-006"] = d => Rename(d, 2, "http://iec.ch/TC57/2013/CIM-schema-cim16#"),
        ["CGM-ID-001"] = d => d[1].Root!.Add(new XElement(Cim + "TopologicalNode", new XAttribute(Rdf + "ID", "_TN1"),
            new XElement(Cim + "IdentifiedObject.mRID", "TN1"), new XElement(Cim + "IdentifiedObject.name", "dup"),
            new XElement(Cim + "TopologicalNode.BaseVoltage", new XAttribute(Rdf + "resource", "#_BV110")))),
        ["CGM-ID-002"] = d => Obj(d[0], "_LINE").Element(Cim + "IdentifiedObject.mRID")!.Value = "not-the-id",
        ["CGM-REF-001"] = d => Obj(d[0], "_SM").Element(Cim + "RotatingMachine.GeneratingUnit")!.SetAttributeValue(Rdf + "resource", "#_NOPE"),
        ["CGM-REF-002"] = d => Obj(d[0], "_LOAD_T1").Element(Cim + "Terminal.ConductingEquipment")!.Remove(),
        ["CGM-REF-003"] = d => Obj(d[0], "_LINE_T2").Element(Cim + "Terminal.ConnectivityNode")!.SetAttributeValue(Rdf + "resource", "#_CN9"),
        ["CGM-REF-004"] = d => Obj(d[0], "_VL2").Element(Cim + "VoltageLevel.BaseVoltage")!.Remove(),
        ["CGM-REF-005"] = d => d[2].Root!.Add(new XElement(Cim + "EnergyConsumer", new XAttribute(Rdf + "about", "#_GHOST"),
            new XElement(Cim + "EnergyConsumer.p", 1), new XElement(Cim + "EnergyConsumer.q", 0))),
        ["CGM-MAND-001"] = d => d[2].Root!.Elements(Cim + "EnergyConsumer").Single().Element(Cim + "EnergyConsumer.q")!.Remove(),
        ["CGM-TOPO-001"] = d => Obj(d[0], "_BRK_T2").Remove(),
        ["CGM-TOPO-002"] = d => Obj(d[0], "_LINE_T2").Element(Cim + "ACDCTerminal.sequenceNumber")!.Value = "1",
        ["CGM-VOLT-001"] = d =>
        {
            d[0].Root!.Add(new XElement(Cim + "BaseVoltage", new XAttribute(Rdf + "ID", "_BV20"), new XElement(Cim + "IdentifiedObject.mRID", "BV20"),
                new XElement(Cim + "IdentifiedObject.name", "20 kV"), new XElement(Cim + "BaseVoltage.nominalVoltage", 20)));
            Obj(d[0], "_VL2").Element(Cim + "VoltageLevel.BaseVoltage")!.SetAttributeValue(Rdf + "resource", "#_BV20");
        },
        ["CGM-PAR-001"] = d => Obj(d[0], "_LINE").Element(Cim + "ACLineSegment.r")!.Value = "-0.5",
        ["CGM-PAR-002"] = d => { Obj(d[0], "_LINE").Element(Cim + "ACLineSegment.r")!.Value = "0"; Obj(d[0], "_LINE").Element(Cim + "ACLineSegment.x")!.Value = "0"; },
        ["CGM-SV-002"] = d => d[3].Root!.Elements(Cim + "SvVoltage").Last().Remove(),
    };

    private static XElement Obj(XDocument d, string id) => d.Root!.Elements().Single(e => (string?)e.Attribute(Rdf + "ID") == id);

    private static void Rename(XDocument[] d, int i, string ns)
    {
        foreach (var e in d[i].Root!.Descendants().Where(e => e.Name.Namespace == Cim).ToList()) e.Name = XName.Get(e.Name.LocalName, ns);
        d[i].Root!.SetAttributeValue(XNamespace.Xmlns + "cim", ns);
    }

    public static TheoryData<string> BrokenRules => [.. Breakers.Keys];

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public void Validator_DetectsBrokenSample(string rule)
    {
        var docs = Sample();
        Breakers[rule](docs);
        var reparsed = docs.Select(d => XDocument.Parse(d.ToString())).ToArray();
        var report = CgmesValidator.Validate(reparsed);
        Assert.True(report.Has(rule), $"{rule} not reported:\n{report}");
        Assert.Contains(CgmesValidator.Rules, r => r.Id == rule);
        var severity = CgmesValidator.Rules.Single(r => r.Id == rule).Severity;
        Assert.Equal(severity == CgmesSeverity.Error, !report.IsValid);
    }

    [Fact]
    public void Validator_TransformerRatedUAndKcl()
    {
        var net = TestCases.Case14();
        var set = Cgmes3Exporter.Export(net, PowerFlowSolver.Solve(net));
        // ratedU of the first transformer end 2× its nominal voltage
        var end = set.Equipment.Root!.Elements(Cim + "PowerTransformerEnd").First();
        end.Element(Cim + "PowerTransformerEnd.ratedU")!.Value = (2 * (double)end.Element(Cim + "PowerTransformerEnd.ratedU")!).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // corrupt one SV flow by 5 MW
        var flow = set.StateVariables!.Root!.Elements(Cim + "SvPowerFlow").First().Element(Cim + "SvPowerFlow.p")!;
        flow.Value = ((double)flow + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var report = CgmesValidator.Validate(set);
        Assert.True(report.Has("CGM-VOLT-002"), report.ToString());
        var kcl = Assert.Single(report.Of("CGM-SV-001"));
        Assert.Contains("ΣP = 5", kcl.Message);
        Assert.False(report.IsValid);
        Assert.Equal(22, CgmesValidator.Rules.Count);
        Assert.Equal(CgmesValidator.Rules.Count, CgmesValidator.Rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void TapAndPhaseShift_TabularPhaseTapChanger_WithoutExtensions()
    {
        var b = new NetworkBuilder("pst", 100, 50)
            .AddBus(1, 150, BusType.Slack).AddBus(2, 150).AddBus(3, 20);
        b.AddLine(1, 2, 12, "243-AL1/39-ST1A 110.0");
        b.AddTransformer(2, 3, 60, 12, 250, tap: 1.025, shiftDeg: -30, connection: WindingConnection.YNd);
        b.AddGenerator(1, 0, 1.02);
        b.AddLoad(3, 30, 10, model: new ZipModel(0.3, 0.2, 0.5));
        var net = b.Build();
        var set = Cgmes3Exporter.Export(net, null, new Cgmes3ExportOptions { IncludeExtensions = false });
        Assert.Single(set.Equipment.Root!.Elements(Cim + "PhaseTapChangerTabular"));
        Assert.Equal(2, set.Equipment.Root!.Elements(Cim + "PhaseTapChangerTablePoint").Count());
        Assert.Null(set.StateVariables!.Root!.Elements(Cim + "SvPowerFlow").FirstOrDefault()); // no result → voltages only
        var back = CgmesImporter.Import(set, new CimImportOptions { UseExtensions = false }).Network;
        var tr = back.Branches[1];
        Assert.True(tr.IsTransformer);
        Assert.Equal(1.025, tr.Tap, 12);
        Assert.Equal(-30, tr.ShiftDeg, 12);
        Assert.Equal(WindingConnection.YNd, tr.Connection);
        Assert.Equal(60, tr.RateA, 9);
        Assert.Equal(new ZipModel(0.3, 0.2, 0.5), back.Loads[0].Model);
        AssertSamePowerFlow(net, back);
    }

    [Fact]
    public void Zip_RoundTrip_AndInteropDetection()
    {
        var net = TestCases.Case9();
        var set = Cgmes3Exporter.Export(net, PowerFlowSolver.Solve(net));
        var bytes = set.ToZip();
        Assert.Equal(bytes, Cgmes3Exporter.Export(net, PowerFlowSolver.Solve(net)).ToZip()); // reproducible archive
        var docs = CgmesDocumentSet.ReadZip(new MemoryStream(bytes));
        Assert.Equal(4, docs.Count);
        AssertSamePowerFlow(net, CgmesImporter.ImportZip(new MemoryStream(bytes)).Network);

        string dir = Path.Combine(Path.GetTempPath(), "eln-cgmes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zip = Path.Combine(dir, "case9.zip");
            File.WriteAllBytes(zip, bytes);
            var r = NetworkFormats.Load(zip);
            Assert.Equal(NetworkFormat.Cgmes, r.Format);
            AssertSamePowerFlow(net, r.Network);
            var files = set.Save(Path.Combine(dir, "xml"));
            Assert.Equal(4, files.Count);
            AssertSamePowerFlow(net, CgmesImporter.Load(Path.Combine(dir, "xml")).Network);
            // a single merged RDF/XML document is detected from its content
            string merged = CimExporter.ExportString(net);
            Assert.Equal(NetworkFormat.Cgmes, NetworkFormats.Detect(merged));
            AssertSamePowerFlow(net, NetworkFormats.Parse(merged).Network);
            Assert.Equal(NetworkFormat.Cgmes, NetworkFormats.FromExtension("x_EQ.xml"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Cgmes2_4_15_StillImportsAndIsDetected()
    {
        var net = TestCases.Case9();
        var profiles = CimExporter.ExportProfiles(net);
        Assert.Equal(CgmesVersion.Cgmes2_4_15, Cgmes3Namespaces.DetectVersion(profiles.All));
        var r = CgmesImporter.Import(profiles.All);
        Assert.Equal(CgmesVersion.Cgmes2_4_15, r.Version);
        AssertSamePowerFlow(net, r.Network);
        var report = CgmesValidator.Validate(profiles.All);
        Assert.True(report.IsValid, report.ToString());
        Assert.False(report.Has("CGM-ID-002")); // mRID is a CGMES 3.0 requirement only
        // mixing the 2.4.15 SSH into a 3.0 set is reported
        var mixed = Cgmes3Exporter.Export(net).All.Take(2).Append(profiles.SteadyStateHypothesis);
        Assert.True(CgmesValidator.Validate(mixed).Has("CGM-HDR-006"));
    }

    [Fact]
    public void NodeBreakerAndBusBranchStyles_HaveExpectedStructure()
    {
        var net = TestCases.Case9();
        var nb = Cgmes3Exporter.Export(net, null, new Cgmes3ExportOptions { Topology = CgmesTopologyStyle.NodeBreaker });
        Assert.Equal(2 * net.Branches.Count, nb.Equipment.Root!.Elements(Cim + "Breaker").Count());
        Assert.Equal(net.BusCount + 2 * net.Branches.Count, nb.Equipment.Root!.Elements(Cim + "ConnectivityNode").Count());
        Assert.Equal(net.BusCount, nb.Topology.Root!.Elements(Cim + "TopologicalNode").Count());
        var bb = Cgmes3Exporter.Export(net, null, new Cgmes3ExportOptions { Topology = CgmesTopologyStyle.BusBranch });
        Assert.Empty(bb.Equipment.Root!.Elements(Cim + "ConnectivityNode"));
        Assert.All(bb.Topology.Root!.Elements(Cim + "Terminal"), t => Assert.NotNull(t.Element(Cim + "Terminal.TopologicalNode")));
        // buses joined by transformers share one substation (case14: 4-7, 4-9, 5-6 plus 7-8 and 7-9, which join 14 kV to 18 / 13.8 kV)
        var eq = Cgmes3Exporter.Export(TestCases.Case14()).Equipment.Root!;
        Assert.Equal(5, eq.Elements(Cim + "PowerTransformer").Count());
        Assert.True(eq.Elements(Cim + "Substation").Count() < 14);
        Assert.True(CgmesValidator.Validate(bb).IsValid);
    }

    [Fact]
    public void AddCim_RegistersCgmesService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddCim()).BuildServiceProvider();
        var svc = sp.GetRequiredService<ICgmesService>();
        var net = TestCases.Case9();
        var set = svc.Export(net, PowerFlowSolver.Solve(net));
        Assert.True(svc.Validate(set.All).IsValid);
        Assert.Equal(9, svc.Import(set.All).Network.BusCount);
        Assert.NotNull(sp.GetRequiredService<ICimService>());
    }
}
