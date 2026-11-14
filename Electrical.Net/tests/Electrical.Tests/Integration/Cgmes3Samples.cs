namespace Electrical.Tests;

/// <summary>
/// Hand-written minimal CGMES 3.0 set (no <c>eln:</c> extensions): 110 kV, two substations, one breaker between busbar 1 and the line
/// bay, one 30 km line (r = 2.42 Ω, x = 12.1 Ω, bch = 165.29 µS → 0.02 / 0.1 / 0.02 pu on 100 MVA), one 250 MVA machine regulating
/// 112.2 kV (1.02 pu) with referencePriority 1, one 50 MW / 20 Mvar load, a 500 A PATL current limit on the line.
/// </summary>
internal static class Cgmes3Samples
{
    private const string Ns = """xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:cim="http://iec.ch/TC57/CIM100#" xmlns:eu="http://iec.ch/TC57/CIM100-European#" xmlns:md="http://iec.ch/TC57/61970-552/ModelDescription/1#" """;

    private static string Header(string id, string profile, string deps = "") => $"""
          <md:FullModel rdf:about="urn:uuid:{id}">
            <md:Model.scenarioTime>2026-06-01T12:00:00Z</md:Model.scenarioTime>
            <md:Model.created>2026-06-01T08:00:00Z</md:Model.created>
            <md:Model.version>1</md:Model.version>
            <md:Model.modelingAuthoritySet>http://example.org/tso</md:Model.modelingAuthoritySet>
            <md:Model.profile>http://iec.ch/TC57/ns/CIM/{profile}/3.0</md:Model.profile>
            {deps}
          </md:FullModel>
        """;

    public const string EqId = "a0000000-0000-4000-8000-0000000000e0";
    public const string TpId = "a0000000-0000-4000-8000-0000000000e1";
    public const string SshId = "a0000000-0000-4000-8000-0000000000e2";
    public const string SvId = "a0000000-0000-4000-8000-0000000000e3";

    private static string Io(string cls, string id, string name, string body) =>
        $"""  <cim:{cls} rdf:ID="_{id}"><cim:IdentifiedObject.mRID>{id}</cim:IdentifiedObject.mRID><cim:IdentifiedObject.name>{name}</cim:IdentifiedObject.name>{body}</cim:{cls}>""" + "\n";

    private static string Term(string id, string eq, int seq, string cn) =>
        Io("Terminal", id, id, $"""<cim:Terminal.ConductingEquipment rdf:resource="#_{eq}"/><cim:ACDCTerminal.sequenceNumber>{seq}</cim:ACDCTerminal.sequenceNumber><cim:Terminal.ConnectivityNode rdf:resource="#_{cn}"/>""");

    private static readonly string[] Terminals = ["BRK_T1", "BRK_T2", "LINE_T1", "LINE_T2", "SM_T1", "LOAD_T1"];

    public static string Equipment =>
        $"""<?xml version="1.0" encoding="utf-8"?>""" + "\n" + $"<rdf:RDF {Ns}>\n" + Header(EqId, "CoreEquipment-EU") +
        Io("BaseVoltage", "BV110", "110 kV", "<cim:BaseVoltage.nominalVoltage>110</cim:BaseVoltage.nominalVoltage>") +
        Io("GeographicalRegion", "GR", "Region", "") +
        Io("SubGeographicalRegion", "SGR", "Subregion", """<cim:SubGeographicalRegion.Region rdf:resource="#_GR"/>""") +
        Io("Substation", "SS1", "North", """<cim:Substation.Region rdf:resource="#_SGR"/>""") +
        Io("Substation", "SS2", "South", """<cim:Substation.Region rdf:resource="#_SGR"/>""") +
        Io("VoltageLevel", "VL1", "North 110", """<cim:VoltageLevel.Substation rdf:resource="#_SS1"/><cim:VoltageLevel.BaseVoltage rdf:resource="#_BV110"/>""") +
        Io("VoltageLevel", "VL2", "South 110", """<cim:VoltageLevel.Substation rdf:resource="#_SS2"/><cim:VoltageLevel.BaseVoltage rdf:resource="#_BV110"/>""") +
        Io("ConnectivityNode", "CN1", "Busbar North", """<cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL1"/>""") +
        Io("ConnectivityNode", "CN1B", "Line bay North", """<cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL1"/>""") +
        Io("ConnectivityNode", "CN2", "Busbar South", """<cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_VL2"/>""") +
        Io("Breaker", "BRK", "CB North", """<cim:Equipment.EquipmentContainer rdf:resource="#_VL1"/><cim:Switch.normalOpen>false</cim:Switch.normalOpen><cim:Switch.retained>false</cim:Switch.retained>""") +
        Term("BRK_T1", "BRK", 1, "CN1") + Term("BRK_T2", "BRK", 2, "CN1B") +
        Io("ACLineSegment", "LINE", "North-South", """<cim:ConductingEquipment.BaseVoltage rdf:resource="#_BV110"/><cim:ACLineSegment.r>2.42</cim:ACLineSegment.r><cim:ACLineSegment.x>12.1</cim:ACLineSegment.x><cim:ACLineSegment.bch>0.000165289256198</cim:ACLineSegment.bch><cim:ACLineSegment.gch>0</cim:ACLineSegment.gch><cim:Conductor.length>30</cim:Conductor.length>""") +
        Term("LINE_T1", "LINE", 1, "CN1B") + Term("LINE_T2", "LINE", 2, "CN2") +
        Io("OperationalLimitType", "PATL", "PATL", """<cim:OperationalLimitType.isInfiniteDuration>true</cim:OperationalLimitType.isInfiniteDuration><eu:OperationalLimitType.kind rdf:resource="http://iec.ch/TC57/CIM100-European#LimitKind.patl"/>""") +
        Io("OperationalLimitSet", "OLS", "Ratings", """<cim:OperationalLimitSet.Terminal rdf:resource="#_LINE_T1"/>""") +
        Io("CurrentLimit", "CL", "PATL", """<cim:OperationalLimit.OperationalLimitSet rdf:resource="#_OLS"/><cim:OperationalLimit.OperationalLimitType rdf:resource="#_PATL"/><cim:CurrentLimit.normalValue>500</cim:CurrentLimit.normalValue>""") +
        Io("ThermalGeneratingUnit", "GU", "Unit 1", """<cim:Equipment.EquipmentContainer rdf:resource="#_SS1"/><cim:GeneratingUnit.maxOperatingP>200</cim:GeneratingUnit.maxOperatingP><cim:GeneratingUnit.minOperatingP>0</cim:GeneratingUnit.minOperatingP>""") +
        Io("RegulatingControl", "RC", "V control", """<cim:RegulatingControl.Terminal rdf:resource="#_SM_T1"/><cim:RegulatingControl.mode rdf:resource="http://iec.ch/TC57/CIM100#RegulatingControlModeKind.voltage"/>""") +
        Io("SynchronousMachine", "SM", "Gen 1", """<cim:Equipment.EquipmentContainer rdf:resource="#_VL1"/><cim:RotatingMachine.GeneratingUnit rdf:resource="#_GU"/><cim:RegulatingCondEq.RegulatingControl rdf:resource="#_RC"/><cim:RotatingMachine.ratedS>250</cim:RotatingMachine.ratedS><cim:RotatingMachine.ratedU>110</cim:RotatingMachine.ratedU><cim:SynchronousMachine.maxQ>150</cim:SynchronousMachine.maxQ><cim:SynchronousMachine.minQ>-100</cim:SynchronousMachine.minQ><cim:SynchronousMachine.type rdf:resource="http://iec.ch/TC57/CIM100#SynchronousMachineKind.generator"/>""") +
        Term("SM_T1", "SM", 1, "CN1") +
        Io("EnergyConsumer", "LOAD", "Town", """<cim:Equipment.EquipmentContainer rdf:resource="#_VL2"/>""") +
        Term("LOAD_T1", "LOAD", 1, "CN2") +
        "</rdf:RDF>\n";

    public static string Topology =>
        $"""<?xml version="1.0" encoding="utf-8"?>""" + "\n" + $"<rdf:RDF {Ns}>\n" +
        Header(TpId, "Topology-EU", $"""<md:Model.DependentOn rdf:resource="urn:uuid:{EqId}"/>""") +
        Io("TopologicalNode", "TN1", "North", """<cim:TopologicalNode.BaseVoltage rdf:resource="#_BV110"/><cim:TopologicalNode.ConnectivityNodeContainer rdf:resource="#_VL1"/>""") +
        Io("TopologicalNode", "TN2", "South", """<cim:TopologicalNode.BaseVoltage rdf:resource="#_BV110"/><cim:TopologicalNode.ConnectivityNodeContainer rdf:resource="#_VL2"/>""") +
        """  <cim:ConnectivityNode rdf:about="#_CN1"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_TN1"/></cim:ConnectivityNode>""" + "\n" +
        """  <cim:ConnectivityNode rdf:about="#_CN1B"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_TN1"/></cim:ConnectivityNode>""" + "\n" +
        """  <cim:ConnectivityNode rdf:about="#_CN2"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_TN2"/></cim:ConnectivityNode>""" + "\n" +
        string.Concat(Terminals.Select(t => $"""  <cim:Terminal rdf:about="#_{t}"><cim:Terminal.TopologicalNode rdf:resource="#_{(t is "LINE_T2" or "LOAD_T1" ? "TN2" : "TN1")}"/></cim:Terminal>""" + "\n")) +
        "</rdf:RDF>\n";

    public static string SteadyStateHypothesis =>
        $"""<?xml version="1.0" encoding="utf-8"?>""" + "\n" + $"<rdf:RDF {Ns}>\n" +
        Header(SshId, "SteadyStateHypothesis-EU", $"""<md:Model.DependentOn rdf:resource="urn:uuid:{EqId}"/>""") +
        string.Concat(Terminals.Select(t => $"""  <cim:Terminal rdf:about="#_{t}"><cim:ACDCTerminal.connected>true</cim:ACDCTerminal.connected></cim:Terminal>""" + "\n")) +
        """
          <cim:Breaker rdf:about="#_BRK"><cim:Switch.open>false</cim:Switch.open><cim:Switch.locked>false</cim:Switch.locked></cim:Breaker>
          <cim:CurrentLimit rdf:about="#_CL"><cim:CurrentLimit.value>500</cim:CurrentLimit.value></cim:CurrentLimit>
          <cim:ThermalGeneratingUnit rdf:about="#_GU"><cim:GeneratingUnit.normalPF>1</cim:GeneratingUnit.normalPF></cim:ThermalGeneratingUnit>
          <cim:RegulatingControl rdf:about="#_RC"><cim:RegulatingControl.discrete>false</cim:RegulatingControl.discrete><cim:RegulatingControl.enabled>true</cim:RegulatingControl.enabled><cim:RegulatingControl.targetDeadband>0</cim:RegulatingControl.targetDeadband><cim:RegulatingControl.targetValue>112.2</cim:RegulatingControl.targetValue><cim:RegulatingControl.targetValueUnitMultiplier rdf:resource="http://iec.ch/TC57/CIM100#UnitMultiplier.k"/></cim:RegulatingControl>
          <cim:SynchronousMachine rdf:about="#_SM"><cim:RotatingMachine.p>-50</cim:RotatingMachine.p><cim:RotatingMachine.q>-10</cim:RotatingMachine.q><cim:RegulatingCondEq.controlEnabled>true</cim:RegulatingCondEq.controlEnabled><cim:SynchronousMachine.referencePriority>1</cim:SynchronousMachine.referencePriority><cim:SynchronousMachine.operatingMode rdf:resource="http://iec.ch/TC57/CIM100#SynchronousMachineOperatingMode.generator"/></cim:SynchronousMachine>
          <cim:EnergyConsumer rdf:about="#_LOAD"><cim:EnergyConsumer.p>50</cim:EnergyConsumer.p><cim:EnergyConsumer.q>20</cim:EnergyConsumer.q></cim:EnergyConsumer>
        </rdf:RDF>
        """;

    public static string StateVariables =>
        $"""<?xml version="1.0" encoding="utf-8"?>""" + "\n" + $"<rdf:RDF {Ns}>\n" +
        Header(SvId, "StateVariables-EU", $"""<md:Model.DependentOn rdf:resource="urn:uuid:{TpId}"/><md:Model.DependentOn rdf:resource="urn:uuid:{SshId}"/>""") +
        """
          <cim:SvVoltage rdf:ID="_SV1"><cim:SvVoltage.TopologicalNode rdf:resource="#_TN1"/><cim:SvVoltage.v>112.2</cim:SvVoltage.v><cim:SvVoltage.angle>0</cim:SvVoltage.angle></cim:SvVoltage>
          <cim:SvVoltage rdf:ID="_SV2"><cim:SvVoltage.TopologicalNode rdf:resource="#_TN2"/><cim:SvVoltage.v>108.5</cim:SvVoltage.v><cim:SvVoltage.angle>-2.5</cim:SvVoltage.angle></cim:SvVoltage>
        </rdf:RDF>
        """;
}
