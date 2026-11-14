using System.Xml.Linq;
using Electrical.CIM;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class CimTests
{
    private static readonly XNamespace Cim = CimNamespaces.Cim;

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

    [Fact]
    public void Case14_MergedRoundTrip_IdenticalPowerFlow()
    {
        var net = TestCases.Case14();
        string xml = CimExporter.ExportString(net);
        var back = CimImporter.Import(xml);
        Assert.Equal(net.BaseMva, back.BaseMva);
        Assert.Equal(14, back.BusCount);
        Assert.Equal(20, back.Branches.Count);
        Assert.Equal(5, back.Generators.Count);
        Assert.Equal(net.Loads.Count, back.Loads.Count);
        Assert.Equal(19, back.GetBus(9).Bs, 1e-9); // bus 9 capacitor bank (MATPOWER case14)
        for (int k = 0; k < net.Branches.Count; k++)
        {
            Assert.Equal(net.Branches[k].EffectiveTap, back.Branches[k].EffectiveTap, 1e-12);
            Assert.Equal(net.Branches[k].X, back.Branches[k].X, 1e-12);
        }
        AssertSamePowerFlow(net, back);
        Assert.Equal(13.393, PowerFlowSolver.Solve(back).LossesMw, 2);
    }

    [Fact]
    public void Case9_SplitProfiles_RoundTrip()
    {
        var net = TestCases.Case9();
        var profiles = CimExporter.ExportProfiles(net);
        // SSH holds the set-points as rdf:about references, EQ declares the objects.
        Assert.Contains(profiles.SteadyStateHypothesis.Root!.Elements(Cim + "SynchronousMachine"),
            e => e.Attribute(XName.Get("about", CimNamespaces.Rdf)) is not null && e.Element(Cim + "RotatingMachine.p") is not null);
        Assert.DoesNotContain(profiles.Equipment.Descendants(Cim + "RotatingMachine.p"), _ => true);
        Assert.Equal(9, profiles.StateVariables.Root!.Elements(Cim + "SvVoltage").Count());
        var back = CimImporter.Import(profiles.All);
        Assert.Equal(BusType.Slack, back.GetBus(1).Type);
        Assert.Equal(GeneratorKind.Hydro, back.Generators[0].Kind);
        Assert.Equal(23.64, back.Generators[0].H, 12);
        AssertSamePowerFlow(net, back);
    }

    [Fact]
    public void Export_UsesPhysicalUnits()
    {
        var net = TestCases.Case14();
        var doc = CimExporter.Export(net);
        var root = doc.Root!;
        Assert.Equal(17, root.Elements(Cim + "ACLineSegment").Count());
        Assert.Equal(3, root.Elements(Cim + "PowerTransformer").Count());
        Assert.Equal(6, root.Elements(Cim + "PowerTransformerEnd").Count());
        Assert.Equal(5, root.Elements(Cim + "SynchronousMachine").Count());
        Assert.Single(root.Elements(Cim + "LinearShuntCompensator"));
        // Branch 1-2: r = 0.01938 pu × 138² / 100 = 3.6907 Ω, bch = 0.0528 pu / 190.44 Ω = 2.77253e-4 S
        var line = root.Elements(Cim + "ACLineSegment").First();
        Assert.Equal(3.6907, (double)line.Element(Cim + "ACLineSegment.r")!, 4);
        Assert.Equal(2.77253e-4, (double)line.Element(Cim + "ACLineSegment.bch")!, 8);
        // Generator 1: SSH p in load sign convention (negative for generation)
        var sm = root.Elements(Cim + "SynchronousMachine").First();
        Assert.True((double)sm.Element(Cim + "RotatingMachine.p")! < 0);
        Assert.Equal(1, (int)sm.Element(Cim + "SynchronousMachine.referencePriority")!);
        Assert.Equal(CimExporter.ExportString(net), CimExporter.ExportString(net)); // deterministic
    }

    [Fact]
    public void Case33bw_WithoutExtensions_DerivesBusTypes()
    {
        var net = TestCases.Case33BW();
        string xml = CimExporter.ExportString(net, new CimExportOptions { IncludeExtensions = false });
        Assert.DoesNotContain("eln:ConnectivityNode", xml);
        var back = CimImporter.Import(xml, new CimImportOptions { BaseMva = 10, UseExtensions = false });
        Assert.Equal(33, back.BusCount);
        Assert.Equal(BusType.Slack, back.Buses[0].Type);
        Assert.All(back.Buses.Skip(1), b => Assert.Equal(BusType.PQ, b.Type));
        AssertSamePowerFlow(net, back);
        Assert.Equal(0.2027, PowerFlowSolver.Solve(back).LossesMw, 3); // Baran & Wu: 202.7 kW
    }

    [Fact]
    public void TransformerTapPhaseShiftAndLocations_Survive()
    {
        var b = new NetworkBuilder("pst", 100, 50)
            .AddBus(1, 150, BusType.Slack, location: new GeoPoint(-6.2, 106.8))
            .AddBus(2, 150, location: new GeoPoint(-6.25, 106.85))
            .AddBus(3, 20, location: new GeoPoint(-6.26, 106.86));
        b.AddLine(1, 2, 12, "243-AL1/39-ST1A 110.0");
        b.AddTransformer(2, 3, 60, 12, 250, tap: 1.025, shiftDeg: -30, connection: WindingConnection.YNd);
        b.AddGenerator(1, 0, 1.02);
        b.AddLoad(3, 30, 10, model: new ZipModel(0.3, 0.2, 0.5));
        var net = b.Build();
        var back = CimImporter.Import(CimExporter.ExportString(net));
        var tr = back.Branches[1];
        Assert.True(tr.IsTransformer);
        Assert.Equal(1.025, tr.Tap, 12);
        Assert.Equal(-30, tr.ShiftDeg, 12);
        Assert.Equal(WindingConnection.YNd, tr.Connection);
        Assert.Equal(60, tr.RateA, 9);
        Assert.Equal(12, back.Branches[0].LengthKm, 12);
        Assert.Equal(net.Branches[0].RateA, back.Branches[0].RateA, 9);
        Assert.Equal(new ZipModel(0.3, 0.2, 0.5), back.Loads[0].Model);
        Assert.Equal(new GeoPoint(-6.25, 106.85), back.GetBus(2).Location);
        AssertSamePowerFlow(net, back);
    }

    private const string SwitchSample = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#">
          <cim:BaseVoltage rdf:ID="_BV20"><cim:BaseVoltage.nominalVoltage>20</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
          <cim:VoltageLevel rdf:ID="_VL"><cim:VoltageLevel.BaseVoltage rdf:resource="#_BV20"/></cim:VoltageLevel>
          <cim:ConnectivityNode rdf:ID="_A"><cim:IdentifiedObject.name>Busbar A</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL"/></cim:ConnectivityNode>
          <cim:ConnectivityNode rdf:ID="_B"><cim:IdentifiedObject.name>Feeder bay</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL"/></cim:ConnectivityNode>
          <cim:ConnectivityNode rdf:ID="_C"><cim:IdentifiedObject.name>Remote</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL"/></cim:ConnectivityNode>
          <cim:ConnectivityNode rdf:ID="_D"><cim:IdentifiedObject.name>Spur</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL"/></cim:ConnectivityNode>
          <cim:Breaker rdf:ID="_BRK"><cim:Switch.open>false</cim:Switch.open></cim:Breaker>
          <cim:Terminal rdf:ID="_BRK_1"><cim:Terminal.ConductingEquipment rdf:resource="#_BRK"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_A"/></cim:Terminal>
          <cim:Terminal rdf:ID="_BRK_2"><cim:Terminal.ConductingEquipment rdf:resource="#_BRK"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_B"/></cim:Terminal>
          <cim:Disconnector rdf:ID="_DIS"><cim:Switch.open>true</cim:Switch.open></cim:Disconnector>
          <cim:Terminal rdf:ID="_DIS_1"><cim:Terminal.ConductingEquipment rdf:resource="#_DIS"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_C"/></cim:Terminal>
          <cim:Terminal rdf:ID="_DIS_2"><cim:Terminal.ConductingEquipment rdf:resource="#_DIS"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_D"/></cim:Terminal>
          <cim:ACLineSegment rdf:ID="_L1"><cim:ACLineSegment.r>1.2</cim:ACLineSegment.r><cim:ACLineSegment.x>2.4</cim:ACLineSegment.x><cim:ACLineSegment.bch>0</cim:ACLineSegment.bch><cim:Conductor.length>5</cim:Conductor.length></cim:ACLineSegment>
          <cim:Terminal rdf:ID="_L1_1"><cim:Terminal.ConductingEquipment rdf:resource="#_L1"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_B"/></cim:Terminal>
          <cim:Terminal rdf:ID="_L1_2"><cim:Terminal.ConductingEquipment rdf:resource="#_L1"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_C"/></cim:Terminal>
          <cim:SynchronousMachine rdf:ID="_G"><cim:RotatingMachine.p>0</cim:RotatingMachine.p><cim:SynchronousMachine.referencePriority>1</cim:SynchronousMachine.referencePriority></cim:SynchronousMachine>
          <cim:Terminal rdf:ID="_G_1"><cim:Terminal.ConductingEquipment rdf:resource="#_G"/><cim:Terminal.ConnectivityNode rdf:resource="#_A"/></cim:Terminal>
          <cim:EnergyConsumer rdf:ID="_LD"><cim:EnergyConsumer.p>4</cim:EnergyConsumer.p><cim:EnergyConsumer.q>1.5</cim:EnergyConsumer.q></cim:EnergyConsumer>
          <cim:Terminal rdf:ID="_LD_1"><cim:Terminal.ConductingEquipment rdf:resource="#_LD"/><cim:Terminal.ConnectivityNode rdf:resource="#_C"/></cim:Terminal>
        </rdf:RDF>
        """;

    [Fact]
    public void TopologyProcessing_MergesClosedSwitches()
    {
        var r = CimImporter.ImportDetailed([XDocument.Parse(SwitchSample)]);
        var net = r.Network;
        Assert.Equal(3, net.BusCount); // A+B merged by the closed breaker, C, D separated by the open disconnector
        Assert.Equal(r.NodeToBus["_A"], r.NodeToBus["_B"]);
        Assert.NotEqual(r.NodeToBus["_C"], r.NodeToBus["_D"]);
        Assert.Equal("Busbar A", net.Buses[0].Name);
        Assert.Equal(BusType.Slack, net.Buses[0].Type);
        Assert.All(net.Buses, b => Assert.Equal(20, b.BaseKv));
        var line = Assert.Single(net.Branches);
        Assert.Equal(1.2 / 4.0, line.R, 12); // Z_base = 20² / 100 = 4 Ω
        Assert.Equal(2, Topology.Islands(net).Count);
        net.Buses[2].Type = BusType.Isolated; // spur bus D is de-energized
        var pf = PowerFlowSolver.Solve(net);
        Assert.True(pf.Converged);
        Assert.Equal(4, pf.TotalLoadMw, 9);
    }

    [Fact]
    public void AddCim_RegistersService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddCim().AddCim()).BuildServiceProvider();
        var svc = sp.GetRequiredService<ICimService>();
        var res = svc.Import(svc.ExportProfiles(TestCases.Case9()).All);
        Assert.Empty(res.Warnings);
        Assert.Equal(9, res.Network.BusCount);
        Assert.Contains("CIM", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
    }
}
