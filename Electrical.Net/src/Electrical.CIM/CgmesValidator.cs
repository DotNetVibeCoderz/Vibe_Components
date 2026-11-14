using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Electrical.CIM;

/// <summary>Severity of a <see cref="CgmesIssue"/>.</summary>
public enum CgmesSeverity
{
    /// <summary>Informational note.</summary>
    Info,
    /// <summary>Questionable content; the set is still usable.</summary>
    Warning,
    /// <summary>Violation of a CGMES constraint; the set should be rejected.</summary>
    Error,
}

/// <summary>A validation rule of <see cref="CgmesValidator"/>.</summary>
/// <param name="Id">Rule id (e.g. <c>CGM-REF-002</c>).</param>
/// <param name="Severity">Default severity.</param>
/// <param name="Description">What the rule checks.</param>
public sealed record CgmesRule(string Id, CgmesSeverity Severity, string Description);

/// <summary>One finding of <see cref="CgmesValidator"/>.</summary>
/// <param name="RuleId">Id of the violated rule (see <see cref="CgmesValidator.Rules"/>).</param>
/// <param name="Severity">Severity of this finding.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="ObjectId">rdf:ID of the offending object (without <c>#</c>), or the model URN for header rules.</param>
/// <param name="Profile">Profile of the document the finding relates to, if known.</param>
public sealed record CgmesIssue(string RuleId, CgmesSeverity Severity, string Message, string? ObjectId = null, CgmesProfileKind Profile = CgmesProfileKind.Unknown)
{
    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {RuleId}: {Message}";
}

/// <summary>Options for <see cref="CgmesValidator"/>.</summary>
public sealed record CgmesValidationOptions
{
    /// <summary>Allowed SV power mismatch per TopologicalNode [MW and Mvar]. Default 0.01.</summary>
    public double KclToleranceMw { get; init; } = 0.01;
    /// <summary>Allowed relative deviation of PowerTransformerEnd.ratedU from the nominal voltage of its BaseVoltage. Default 0.2 (20 %).</summary>
    public double RatedUTolerance { get; init; } = 0.2;
    /// <summary>Relative tolerance when comparing nominal voltages at the ends of a line. Default 1e-6.</summary>
    public double VoltageTolerance { get; init; } = 1e-6;
}

/// <summary>Result of <see cref="CgmesValidator.Validate(IEnumerable{XDocument}, CgmesValidationOptions?)"/>.</summary>
public sealed class CgmesValidationReport
{
    internal CgmesValidationReport(IReadOnlyList<CgmesIssue> issues, CgmesVersion version, IReadOnlyList<CgmesModelHeader> models)
    {
        Issues = issues; Version = version; Models = models;
    }

    /// <summary>All findings in rule order.</summary>
    public IReadOnlyList<CgmesIssue> Issues { get; }
    /// <summary>Detected CGMES version.</summary>
    public CgmesVersion Version { get; }
    /// <summary>Model headers found in the set.</summary>
    public IReadOnlyList<CgmesModelHeader> Models { get; }
    /// <summary>Error findings.</summary>
    public IEnumerable<CgmesIssue> Errors => Issues.Where(i => i.Severity == CgmesSeverity.Error);
    /// <summary>Warning findings.</summary>
    public IEnumerable<CgmesIssue> Warnings => Issues.Where(i => i.Severity == CgmesSeverity.Warning);
    /// <summary>True when there are no errors.</summary>
    public bool IsValid => !Errors.Any();
    /// <summary>True when at least one finding of <paramref name="ruleId"/> was reported.</summary>
    public bool Has(string ruleId) => Issues.Any(i => i.RuleId == ruleId);
    /// <summary>Findings of one rule.</summary>
    public IEnumerable<CgmesIssue> Of(string ruleId) => Issues.Where(i => i.RuleId == ruleId);

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"CGMES {Version}: {Errors.Count()} error(s), {Warnings.Count()} warning(s)");
        foreach (var i in Issues) sb.AppendLine().Append(i);
        return sb.ToString();
    }
}

/// <summary>
/// Rule-based checker for CGMES 2.4.15 / 3.0 instance sets, in the spirit of the ENTSO-E QoCDC rules and the CGMES SHACL constraints:
/// header and dependency consistency, unique and matching mRIDs, dangling references, mandatory attributes per profile, terminal counts,
/// nominal-voltage consistency, physical parameter ranges and Kirchhoff's current law on the SV power flows. See <see cref="Rules"/>.
/// </summary>
public static class CgmesValidator
{
    /// <summary>The rule catalogue.</summary>
    public static IReadOnlyList<CgmesRule> Rules { get; } =
    [
        new("CGM-HDR-001", CgmesSeverity.Error, "Every document has exactly one model header (md:FullModel or dcat:Dataset)."),
        new("CGM-HDR-002", CgmesSeverity.Error, "Model header has Model.profile, Model.scenarioTime, Model.created and Model.modelingAuthoritySet."),
        new("CGM-HDR-003", CgmesSeverity.Error, "Every Model.DependentOn target is a model of the set."),
        new("CGM-HDR-004", CgmesSeverity.Error, "All models (except boundaries) share the same Model.scenarioTime."),
        new("CGM-HDR-005", CgmesSeverity.Warning, "Profile dependencies: TP and SSH depend on EQ, SV depends on TP and SSH."),
        new("CGM-HDR-006", CgmesSeverity.Error, "The set does not mix CIM versions (CIM16 vs CIM100)."),
        new("CGM-ID-001", CgmesSeverity.Error, "rdf:ID values are unique across the set."),
        new("CGM-ID-002", CgmesSeverity.Error, "CGMES 3.0: IdentifiedObject.mRID is present and equals rdf:ID without the leading underscore."),
        new("CGM-REF-001", CgmesSeverity.Error, "Every rdf:resource reference resolves to an object of the set."),
        new("CGM-REF-002", CgmesSeverity.Error, "Terminal.ConductingEquipment is present and refers to equipment."),
        new("CGM-REF-003", CgmesSeverity.Error, "A Terminal refers to a ConnectivityNode (EQ) or a TopologicalNode (TP), and the node exists."),
        new("CGM-REF-004", CgmesSeverity.Error, "VoltageLevel.BaseVoltage is present and refers to a BaseVoltage."),
        new("CGM-REF-005", CgmesSeverity.Error, "Objects described with rdf:about are declared (rdf:ID) in some model."),
        new("CGM-MAND-001", CgmesSeverity.Error, "Mandatory attributes are present in their profile (EQ, TP, SSH, SV)."),
        new("CGM-TOPO-001", CgmesSeverity.Error, "Terminal count matches the equipment class (2 for lines/switches, 1 for injections, ends for transformers)."),
        new("CGM-TOPO-002", CgmesSeverity.Error, "Terminal sequence numbers / transformer end numbers are unique per equipment, and transformers have 2 or 3 ends."),
        new("CGM-VOLT-001", CgmesSeverity.Error, "Both terminals of an ACLineSegment are at its BaseVoltage."),
        new("CGM-VOLT-002", CgmesSeverity.Warning, "PowerTransformerEnd.ratedU is within the tolerance of the end's nominal voltage."),
        new("CGM-PAR-001", CgmesSeverity.Error, "Impedances and voltages are physical (r, x, r0, x0 ≥ 0 for lines; r ≥ 0 for transformer ends; nominalVoltage, ratedU > 0)."),
        new("CGM-PAR-002", CgmesSeverity.Warning, "Lines have a non-zero impedance and non-negative charging susceptance."),
        new("CGM-SV-001", CgmesSeverity.Error, "SV power flows satisfy Kirchhoff's current law at every TopologicalNode within the tolerance."),
        new("CGM-SV-002", CgmesSeverity.Warning, "Every TopologicalNode has an SvVoltage with v ≥ 0 when an SV model is present."),
    ];

    private static readonly HashSet<string> TwoTerminal = ["ACLineSegment", "Breaker", "Disconnector", "Switch", "LoadBreakSwitch", "Fuse", "Jumper", "SeriesCompensator", "EquivalentBranch"];
    private static readonly HashSet<string> OneTerminal = ["SynchronousMachine", "AsynchronousMachine", "EnergyConsumer", "ConformLoad", "NonConformLoad", "StationSupply", "EnergySource",
        "LinearShuntCompensator", "NonlinearShuntCompensator", "StaticVarCompensator", "ExternalNetworkInjection", "EquivalentInjection", "GroundDisconnector", "Ground"];
    private static readonly HashSet<string> SwitchClasses = ["Switch", "Breaker", "Disconnector", "LoadBreakSwitch", "Fuse", "Jumper", "GroundDisconnector"];
    private static readonly HashSet<string> LoadClasses = ["EnergyConsumer", "ConformLoad", "NonConformLoad", "StationSupply"];
    private static readonly HashSet<string> TapChangerClasses = ["RatioTapChanger", "PhaseTapChangerLinear", "PhaseTapChangerSymmetrical", "PhaseTapChangerAsymmetrical", "PhaseTapChangerTabular"];
    private static readonly HashSet<string> LimitClasses = ["ApparentPowerLimit", "CurrentLimit", "ActivePowerLimit", "VoltageLimit"];

    /// <summary>Validates an exported document set.</summary>
    public static CgmesValidationReport Validate(CgmesDocumentSet set, CgmesValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Validate(set.All, options);
    }

    /// <summary>Validates a set of CGMES documents (EQ, TP, SSH, SV, boundaries …).</summary>
    public static CgmesValidationReport Validate(IEnumerable<XDocument> documents, CgmesValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var v = new Run(documents.ToList(), options ?? new CgmesValidationOptions());
        v.Execute();
        var order = Rules.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var issues = v.Issues.Select((x, i) => (x, i)).OrderBy(p => order.GetValueOrDefault(p.x.RuleId, int.MaxValue)).ThenBy(p => p.i).Select(p => p.x).ToList();
        return new CgmesValidationReport(issues, v.Version, v.Models);
    }

    private sealed record Prop(int Doc, string Name, string Value, string? Ref);

    private sealed class Node(string id)
    {
        public string Id { get; } = id;
        public string Class { get; set; } = "";
        public int DeclaredIn { get; set; } = -1;
        public bool DeclaredByRdfId { get; set; }
        public List<Prop> Props { get; } = [];
        public string? S(string name) => Props.FirstOrDefault(p => p.Name == name)?.Value;
        public string? R(string name) => Props.FirstOrDefault(p => p.Name == name && p.Ref is not null)?.Ref;
        public double? D(string name) => double.TryParse(S(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private sealed class Run(List<XDocument> docs, CgmesValidationOptions opt)
    {
        public List<CgmesIssue> Issues { get; } = [];
        public CgmesVersion Version { get; private set; }
        public List<CgmesModelHeader> Models { get; } = [];
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly List<CgmesProfileKind> _docProfile = [];
        private bool _missingDependency;

        private void Add(string rule, string message, string? obj = null, CgmesProfileKind p = CgmesProfileKind.Unknown, CgmesSeverity? sev = null) =>
            Issues.Add(new CgmesIssue(rule, sev ?? Rules.First(r => r.Id == rule).Severity, message, obj, p));

        private bool HasProfile(CgmesProfileKind k) => _docProfile.Contains(k);
        private IEnumerable<Node> All => _order.Select(i => _nodes[i]);
        private IEnumerable<Node> OfClass(Func<string, bool> pred) => All.Where(n => pred(n.Class));
        private Node? Get(string? id) => id is not null && _nodes.TryGetValue(id, out var n) && n.DeclaredIn >= 0 ? n : null;

        public void Execute()
        {
            Headers();
            Read();
            References();
            Mandatory();
            TerminalCounts();
            Voltages();
            Parameters();
            StateVariables();
        }

        // ---------- headers ----------
        private void Headers()
        {
            var versions = docs.Select(Cgmes3Namespaces.DetectVersion).Where(x => x != CgmesVersion.Unknown).Distinct().ToList();
            Version = versions.DefaultIfEmpty(CgmesVersion.Unknown).Max();
            if (versions.Count > 1) Add("CGM-HDR-006", "The set mixes CIM16 (CGMES 2.4.15) and CIM100 (CGMES 3.0) documents.");
            for (int d = 0; d < docs.Count; d++)
            {
                var hs = docs[d].Root?.Elements().Where(e => e.Name.LocalName is "FullModel" or "Dataset" or "DifferenceModel").ToList() ?? [];
                if (hs.Count != 1)
                {
                    Add("CGM-HDR-001", $"Document {d + 1} has {hs.Count} model headers (expected 1).");
                    _docProfile.Add(hs.Count == 0 ? CgmesProfileKind.Unknown : CgmesModelHeader.Read(hs[0]).Profile);
                    if (hs.Count == 0) continue;
                }
                var h = CgmesModelHeader.Read(hs[0]);
                if (hs.Count == 1) _docProfile.Add(h.Profile);
                Models.Add(h);
                var missing = new List<string>();
                if (h.ProfileUris.Count == 0) missing.Add("Model.profile");
                if (h.ScenarioTime is null) missing.Add("Model.scenarioTime");
                if (h.Created is null) missing.Add("Model.created");
                if (string.IsNullOrWhiteSpace(h.ModelingAuthoritySet)) missing.Add("Model.modelingAuthoritySet");
                if (missing.Count > 0) Add("CGM-HDR-002", $"Model '{h.Id}' lacks {string.Join(", ", missing)}.", h.Id, h.Profile);
            }
            var ids = Models.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
            foreach (var m in Models)
            {
                foreach (var dep in m.DependentOn.Where(dep => !ids.ContainsKey(dep)))
                {
                    _missingDependency = true;
                    Add("CGM-HDR-003", $"Model '{m.Id}' ({m.Profile}) depends on '{dep}', which is not in the set.", m.Id, m.Profile);
                }
                CgmesProfileKind[] required = m.Profile switch
                {
                    CgmesProfileKind.Topology or CgmesProfileKind.SteadyStateHypothesis => [CgmesProfileKind.Equipment],
                    CgmesProfileKind.StateVariables => [CgmesProfileKind.Topology, CgmesProfileKind.SteadyStateHypothesis],
                    _ => [],
                };
                var depKinds = m.DependentOn.Select(x => ids.GetValueOrDefault(x)?.Profile).ToList();
                foreach (var k in required.Where(k => !depKinds.Contains(k)) )
                    if (m.DependentOn.All(ids.ContainsKey))
                        Add("CGM-HDR-005", $"{m.Profile} model '{m.Id}' does not depend on a {k} model.", m.Id, m.Profile);
            }
            var times = Models.Where(m => m.Profile is not (CgmesProfileKind.EquipmentBoundary or CgmesProfileKind.TopologyBoundary) && m.ScenarioTime is not null)
                .GroupBy(m => m.ScenarioTime!.Value.UtcTicks).ToList();
            if (times.Count > 1)
                Add("CGM-HDR-004", "Models have different scenario times: " +
                    string.Join(", ", times.Select(t => $"{t.First().ScenarioTime!.Value.UtcDateTime.ToString("s", CultureInfo.InvariantCulture)}Z ({string.Join("/", t.Select(m => m.Profile))})")) + ".");
        }

        // ---------- graph ----------
        private void Read()
        {
            XNamespace rdf = CimNamespaces.Rdf;
            for (int d = 0; d < docs.Count; d++)
            {
                if (docs[d].Root is not XElement root) continue;
                foreach (var e in root.Elements())
                {
                    if (e.Name.LocalName is "FullModel" or "Dataset" or "DifferenceModel") continue;
                    string? declared = (string?)e.Attribute(rdf + "ID");
                    string? raw = declared ?? (string?)e.Attribute(rdf + "about");
                    if (raw is null) continue;
                    string id = Norm(raw);
                    if (!_nodes.TryGetValue(id, out var n)) { _nodes[id] = n = new Node(id); _order.Add(id); }
                    if (declared is not null)
                    {
                        if (n.DeclaredIn >= 0 && n.DeclaredByRdfId)
                            Add("CGM-ID-001", $"rdf:ID '{id}' is declared more than once (documents {n.DeclaredIn + 1} and {d + 1}).", id, _docProfile[d]);
                        else { n.DeclaredIn = d; n.DeclaredByRdfId = true; }
                        n.Class = e.Name.LocalName;
                    }
                    else if (raw.StartsWith("urn:uuid:", StringComparison.Ordinal) && n.DeclaredIn < 0)
                    {
                        // Sets written with rdf:about="urn:uuid:…" everywhere: the first description declares the object.
                        n.DeclaredIn = d;
                        n.Class = e.Name.LocalName;
                    }
                    else if (n.Class.Length == 0) n.Class = e.Name.LocalName;
                    foreach (var p in e.Elements())
                    {
                        var res = (string?)p.Attribute(rdf + "resource");
                        string? r = null;
                        string value = p.Value;
                        if (res is not null)
                        {
                            value = res;
                            bool enumeration = res.StartsWith(Cgmes3Namespaces.Cim, StringComparison.Ordinal) || res.StartsWith(Cgmes3Namespaces.Eu, StringComparison.Ordinal)
                                               || res.StartsWith("http://iec.ch/TC57/2013/CIM-schema-cim16#", StringComparison.Ordinal) || res.StartsWith("http://entsoe.eu/CIM/SchemaExtension", StringComparison.Ordinal);
                            if (!enumeration) r = Norm(res.Contains('#') && !res.StartsWith('#') ? res[res.LastIndexOf('#')..] : res);
                        }
                        n.Props.Add(new Prop(d, p.Name.LocalName, value, r));
                    }
                }
            }
            // ID-002: CGMES 3.0 mRID
            if (Version != CgmesVersion.Cgmes3_0) return;
            foreach (var n in All.Where(n => n.DeclaredIn >= 0 && IsIdentifiedObject(n.Class)))
            {
                var mrid = n.Props.FirstOrDefault(p => p.Name == "IdentifiedObject.mRID")?.Value.Trim();
                string expected = n.Id.StartsWith('_') ? n.Id[1..] : n.Id;
                if (mrid is null) Add("CGM-ID-002", $"{n.Class} '{n.Id}' has no IdentifiedObject.mRID.", n.Id, _docProfile[n.DeclaredIn]);
                else if (!string.Equals(mrid, expected, StringComparison.OrdinalIgnoreCase) && !string.Equals(mrid, n.Id, StringComparison.OrdinalIgnoreCase))
                    Add("CGM-ID-002", $"{n.Class} '{n.Id}' has mRID '{mrid}', which does not match its rdf:ID.", n.Id, _docProfile[n.DeclaredIn]);
            }
        }

        private static bool IsIdentifiedObject(string cls) =>
            !(cls.StartsWith("Sv", StringComparison.Ordinal) || cls.EndsWith("TablePoint", StringComparison.Ordinal) || cls is "PositionPoint" or "CurveData");

        private static string Norm(string id)
        {
            if (id.StartsWith('#')) id = id[1..];
            if (id.StartsWith("urn:uuid:", StringComparison.Ordinal)) id = "_" + id[9..];
            return id;
        }

        private CgmesSeverity RefSeverity => _missingDependency ? CgmesSeverity.Warning : CgmesSeverity.Error;

        // ---------- references ----------
        private void References()
        {
            var special = new HashSet<string>(StringComparer.Ordinal)
                { "Terminal.ConductingEquipment", "Terminal.ConnectivityNode", "Terminal.TopologicalNode", "VoltageLevel.BaseVoltage" };
            foreach (var n in All)
            {
                if (n.DeclaredIn < 0)
                    Add("CGM-REF-005", $"{n.Class} '{n.Id}' is described with rdf:about but never declared.", n.Id, _docProfile[n.Props.FirstOrDefault()?.Doc ?? 0], RefSeverity);
                foreach (var p in n.Props.Where(p => p.Ref is not null && !special.Contains(p.Name)))
                    if (Get(p.Ref) is null)
                        Add("CGM-REF-001", $"{n.Class} '{n.Id}': {p.Name} refers to missing object '{p.Ref}'.", n.Id, _docProfile[p.Doc], RefSeverity);
            }
            foreach (var t in OfClass(c => c == "Terminal").Where(t => t.DeclaredIn >= 0))
            {
                var ce = t.R("Terminal.ConductingEquipment");
                if (ce is null) Add("CGM-REF-002", $"Terminal '{t.Id}' has no Terminal.ConductingEquipment.", t.Id, CgmesProfileKind.Equipment);
                else if (Get(ce) is not { } eq) Add("CGM-REF-002", $"Terminal '{t.Id}' refers to missing equipment '{ce}'.", t.Id, CgmesProfileKind.Equipment, RefSeverity);
                else if (eq.Class is "Terminal" or "ConnectivityNode" or "TopologicalNode" or "BaseVoltage" or "VoltageLevel" or "Substation")
                    Add("CGM-REF-002", $"Terminal '{t.Id}' refers to a {eq.Class}, not to conducting equipment.", t.Id, CgmesProfileKind.Equipment);
                var cn = t.R("Terminal.ConnectivityNode");
                var tn = t.R("Terminal.TopologicalNode");
                if (cn is null && tn is null)
                    Add("CGM-REF-003", $"Terminal '{t.Id}' is connected to neither a ConnectivityNode nor a TopologicalNode.", t.Id, CgmesProfileKind.Equipment);
                foreach (var (name, target, cls) in new[] { ("Terminal.ConnectivityNode", cn, "ConnectivityNode"), ("Terminal.TopologicalNode", tn, "TopologicalNode") })
                {
                    if (target is null) continue;
                    if (Get(target) is not { } node) Add("CGM-REF-003", $"Terminal '{t.Id}': {name} refers to missing node '{target}'.", t.Id, CgmesProfileKind.Equipment, RefSeverity);
                    else if (node.Class != cls) Add("CGM-REF-003", $"Terminal '{t.Id}': {name} refers to a {node.Class}.", t.Id, CgmesProfileKind.Equipment);
                }
            }
            foreach (var vl in OfClass(c => c == "VoltageLevel").Where(x => x.DeclaredIn >= 0))
            {
                var bv = vl.R("VoltageLevel.BaseVoltage");
                if (bv is null) Add("CGM-REF-004", $"VoltageLevel '{vl.Id}' has no VoltageLevel.BaseVoltage.", vl.Id, CgmesProfileKind.Equipment);
                else if (Get(bv) is not { } b) Add("CGM-REF-004", $"VoltageLevel '{vl.Id}' refers to missing BaseVoltage '{bv}'.", vl.Id, CgmesProfileKind.Equipment, RefSeverity);
                else if (b.Class != "BaseVoltage") Add("CGM-REF-004", $"VoltageLevel '{vl.Id}': VoltageLevel.BaseVoltage refers to a {b.Class}.", vl.Id, CgmesProfileKind.Equipment);
            }
        }

        // ---------- mandatory attributes ----------
        private IEnumerable<(CgmesProfileKind Profile, string Attr)> Requirements(string cls)
        {
            const CgmesProfileKind EQ = CgmesProfileKind.Equipment, SSH = CgmesProfileKind.SteadyStateHypothesis, TP = CgmesProfileKind.Topology, SV = CgmesProfileKind.StateVariables;
            switch (cls)
            {
                case "BaseVoltage": yield return (EQ, "BaseVoltage.nominalVoltage"); break;
                case "Substation": yield return (EQ, "Substation.Region"); break;
                case "VoltageLevel": yield return (EQ, "VoltageLevel.Substation"); break;
                case "ConnectivityNode": yield return (EQ, "ConnectivityNode.ConnectivityNodeContainer"); break;
                case "Terminal": yield return (EQ, "ACDCTerminal.sequenceNumber"); yield return (SSH, "ACDCTerminal.connected"); break;
                case "ACLineSegment": yield return (EQ, "ACLineSegment.r"); yield return (EQ, "ACLineSegment.x"); yield return (EQ, "ACLineSegment.bch"); break;
                case "PowerTransformerEnd":
                    foreach (var a in new[] { "PowerTransformerEnd.r", "PowerTransformerEnd.x", "PowerTransformerEnd.ratedU", "TransformerEnd.endNumber",
                                 "PowerTransformerEnd.PowerTransformer", "TransformerEnd.Terminal" }) yield return (EQ, a);
                    break;
                case "SynchronousMachine":
                    yield return (EQ, "RotatingMachine.ratedS");
                    yield return (SSH, "RotatingMachine.p"); yield return (SSH, "RotatingMachine.q"); yield return (SSH, "RegulatingCondEq.controlEnabled");
                    break;
                case "ExternalNetworkInjection": yield return (SSH, "ExternalNetworkInjection.p"); yield return (SSH, "ExternalNetworkInjection.q"); break;
                case "LinearShuntCompensator":
                    yield return (EQ, "LinearShuntCompensator.bPerSection"); yield return (EQ, "ShuntCompensator.maximumSections");
                    yield return (EQ, "ShuntCompensator.nomU"); yield return (EQ, "ShuntCompensator.normalSections"); yield return (SSH, "ShuntCompensator.sections");
                    break;
                case "RegulatingControl": yield return (SSH, "RegulatingControl.enabled"); yield return (SSH, "RegulatingControl.targetValue"); break;
                case "TopologicalNode": yield return (TP, "TopologicalNode.BaseVoltage"); break;
                case "SvVoltage": yield return (SV, "SvVoltage.v"); yield return (SV, "SvVoltage.angle"); yield return (SV, "SvVoltage.TopologicalNode"); break;
                case "SvPowerFlow": yield return (SV, "SvPowerFlow.p"); yield return (SV, "SvPowerFlow.q"); yield return (SV, "SvPowerFlow.Terminal"); break;
                case "SvTapStep": yield return (SV, "SvTapStep.position"); yield return (SV, "SvTapStep.TapChanger"); break;
                case "SvStatus": yield return (SV, "SvStatus.inService"); yield return (SV, "SvStatus.ConductingEquipment"); break;
                case "SvShuntCompensatorSections": yield return (SV, "SvShuntCompensatorSections.sections"); yield return (SV, "SvShuntCompensatorSections.ShuntCompensator"); break;
            }
            if (cls.EndsWith("GeneratingUnit", StringComparison.Ordinal))
            {
                yield return (EQ, "GeneratingUnit.maxOperatingP"); yield return (EQ, "GeneratingUnit.minOperatingP");
            }
            if (SwitchClasses.Contains(cls)) { yield return (EQ, "Switch.normalOpen"); yield return (SSH, "Switch.open"); }
            if (LoadClasses.Contains(cls)) { yield return (SSH, "EnergyConsumer.p"); yield return (SSH, "EnergyConsumer.q"); }
            if (TapChangerClasses.Contains(cls))
            {
                foreach (var a in new[] { "TapChanger.lowStep", "TapChanger.highStep", "TapChanger.neutralStep", "TapChanger.normalStep" }) yield return (EQ, a);
                yield return (SSH, "TapChanger.step");
            }
            if (LimitClasses.Contains(cls))
            {
                yield return (EQ, "OperationalLimit.OperationalLimitSet");
                yield return (EQ, Version == CgmesVersion.Cgmes3_0 ? cls + ".normalValue" : cls + ".value");
                if (Version == CgmesVersion.Cgmes3_0) yield return (SSH, cls + ".value");
            }
        }

        private void Mandatory()
        {
            foreach (var n in All.Where(n => n.DeclaredIn >= 0))
            {
                var home = _docProfile[n.DeclaredIn];
                if (home is CgmesProfileKind.Equipment or CgmesProfileKind.EquipmentBoundary && IsIdentifiedObject(n.Class) && n.S("IdentifiedObject.name") is null)
                    Add("CGM-MAND-001", $"{n.Class} '{n.Id}' lacks IdentifiedObject.name.", n.Id, home);
                foreach (var (profile, attr) in Requirements(n.Class))
                {
                    // Only check profiles present in the set; merged documents (unknown profile) satisfy every profile.
                    if (!HasProfile(profile) && !_docProfile.Contains(CgmesProfileKind.Unknown)) continue;
                    if (profile == CgmesProfileKind.Equipment && home is not (CgmesProfileKind.Equipment or CgmesProfileKind.EquipmentBoundary or CgmesProfileKind.Unknown)) continue;
                    bool ok = n.Props.Any(p => p.Name == attr && (_docProfile[p.Doc] == profile || _docProfile[p.Doc] == CgmesProfileKind.Unknown
                                                                  || (profile == CgmesProfileKind.Equipment && _docProfile[p.Doc] == CgmesProfileKind.EquipmentBoundary)));
                    if (!ok) Add("CGM-MAND-001", $"{n.Class} '{n.Id}' lacks {attr} in the {profile} profile.", n.Id, profile);
                }
            }
        }

        // ---------- topology ----------
        private Dictionary<string, List<Node>> TerminalsByEquipment()
        {
            var d = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (var t in OfClass(c => c == "Terminal").Where(t => t.DeclaredIn >= 0))
                if (t.R("Terminal.ConductingEquipment") is string ce)
                {
                    if (!d.TryGetValue(ce, out var l)) d[ce] = l = [];
                    l.Add(t);
                }
            return d;
        }

        private void TerminalCounts()
        {
            var terms = TerminalsByEquipment();
            var ends = OfClass(c => c == "PowerTransformerEnd").Where(e => e.DeclaredIn >= 0)
                .GroupBy(e => e.R("PowerTransformerEnd.PowerTransformer") ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var n in All.Where(n => n.DeclaredIn >= 0))
            {
                int count = terms.TryGetValue(n.Id, out var l) ? l.Count : 0;
                int? expected = TwoTerminal.Contains(n.Class) ? 2 : OneTerminal.Contains(n.Class) ? 1 : null;
                if (n.Class == "PowerTransformer")
                {
                    var e = ends.GetValueOrDefault(n.Id) ?? [];
                    if (e.Count is not (2 or 3)) Add("CGM-TOPO-002", $"PowerTransformer '{n.Id}' has {e.Count} ends (expected 2 or 3).", n.Id, CgmesProfileKind.Equipment);
                    var dup = e.GroupBy(x => x.S("TransformerEnd.endNumber")).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    if (dup.Count > 0) Add("CGM-TOPO-002", $"PowerTransformer '{n.Id}' repeats endNumber {string.Join(", ", dup)}.", n.Id, CgmesProfileKind.Equipment);
                    expected = e.Count is 2 or 3 ? e.Count : null;
                }
                if (expected is int x && count != x)
                    Add("CGM-TOPO-001", $"{n.Class} '{n.Id}' has {count} terminal(s) (expected {x}).", n.Id, CgmesProfileKind.Equipment);
                if (l is not null && l.Count > 1)
                {
                    var dupSeq = l.GroupBy(t => t.S("ACDCTerminal.sequenceNumber") ?? t.S("Terminal.sequenceNumber")).Where(g => g.Key is not null && g.Count() > 1).Select(g => g.Key).ToList();
                    if (dupSeq.Count > 0) Add("CGM-TOPO-002", $"{n.Class} '{n.Id}' repeats terminal sequenceNumber {string.Join(", ", dupSeq)}.", n.Id, CgmesProfileKind.Equipment);
                }
            }
        }

        // ---------- voltages ----------
        private double? Nominal(string? baseVoltage) => Get(baseVoltage)?.D("BaseVoltage.nominalVoltage");

        /// <summary>Nominal voltage [kV] of the node a terminal is connected to.</summary>
        private double? TerminalKv(Node t)
        {
            if (Get(t.R("Terminal.ConnectivityNode")) is { } cn)
            {
                var vl = Get(cn.R("ConnectivityNode.ConnectivityNodeContainer"));
                if (vl?.Class == "VoltageLevel" && Nominal(vl.R("VoltageLevel.BaseVoltage")) is double kv) return kv;
                if (Get(cn.R("ConnectivityNode.TopologicalNode")) is { } tn1 && Nominal(tn1.R("TopologicalNode.BaseVoltage")) is double kv1) return kv1;
            }
            return Get(t.R("Terminal.TopologicalNode")) is { } tn ? Nominal(tn.R("TopologicalNode.BaseVoltage")) : null;
        }

        private void Voltages()
        {
            var terms = TerminalsByEquipment();
            foreach (var l in OfClass(c => c == "ACLineSegment").Where(x => x.DeclaredIn >= 0))
            {
                var kvs = (terms.GetValueOrDefault(l.Id) ?? []).Select(TerminalKv).Where(k => k is not null).Select(k => k!.Value).ToList();
                double? own = Nominal(l.R("ConductingEquipment.BaseVoltage"));
                if (own is double o) kvs.Add(o);
                if (kvs.Count >= 2 && kvs.Max() - kvs.Min() > opt.VoltageTolerance * kvs.Max())
                    Add("CGM-VOLT-001", $"ACLineSegment '{l.Id}' connects different nominal voltages ({string.Join(" / ", kvs.Select(k => k.ToString("0.###", CultureInfo.InvariantCulture)))} kV).", l.Id, CgmesProfileKind.Equipment);
            }
            foreach (var e in OfClass(c => c == "PowerTransformerEnd").Where(x => x.DeclaredIn >= 0))
            {
                if (e.D("PowerTransformerEnd.ratedU") is not double u) continue;
                double? kv = Nominal(e.R("TransformerEnd.BaseVoltage"));
                if (kv is null && Get(e.R("TransformerEnd.Terminal")) is { } t) kv = TerminalKv(t);
                if (kv is double k && k > 0 && Math.Abs(u / k - 1) > opt.RatedUTolerance)
                    Add("CGM-VOLT-002", $"PowerTransformerEnd '{e.Id}' ratedU {u.ToString("0.###", CultureInfo.InvariantCulture)} kV deviates more than {opt.RatedUTolerance:P0} from its nominal voltage {k.ToString("0.###", CultureInfo.InvariantCulture)} kV.", e.Id, CgmesProfileKind.Equipment);
            }
        }

        // ---------- parameters ----------
        private void Parameters()
        {
            foreach (var n in All.Where(x => x.DeclaredIn >= 0))
            {
                switch (n.Class)
                {
                    case "ACLineSegment":
                        foreach (var a in new[] { "ACLineSegment.r", "ACLineSegment.x", "ACLineSegment.r0", "ACLineSegment.x0" })
                            if (n.D(a) is double v && v < 0) Add("CGM-PAR-001", $"ACLineSegment '{n.Id}' has negative {a} = {v.ToString("R", CultureInfo.InvariantCulture)} Ω.", n.Id, CgmesProfileKind.Equipment);
                        if ((n.D("ACLineSegment.r") ?? 0) == 0 && (n.D("ACLineSegment.x") ?? 0) == 0)
                            Add("CGM-PAR-002", $"ACLineSegment '{n.Id}' has zero impedance.", n.Id, CgmesProfileKind.Equipment);
                        if (n.D("ACLineSegment.bch") is double b && b < 0)
                            Add("CGM-PAR-002", $"ACLineSegment '{n.Id}' has negative charging susceptance bch = {b.ToString("R", CultureInfo.InvariantCulture)} S.", n.Id, CgmesProfileKind.Equipment);
                        break;
                    case "PowerTransformerEnd":
                        if (n.D("PowerTransformerEnd.r") is double r && r < 0) Add("CGM-PAR-001", $"PowerTransformerEnd '{n.Id}' has negative r = {r.ToString("R", CultureInfo.InvariantCulture)} Ω.", n.Id, CgmesProfileKind.Equipment);
                        if (n.D("PowerTransformerEnd.ratedU") is double u && u <= 0) Add("CGM-PAR-001", $"PowerTransformerEnd '{n.Id}' has ratedU ≤ 0.", n.Id, CgmesProfileKind.Equipment);
                        break;
                    case "BaseVoltage":
                        if (n.D("BaseVoltage.nominalVoltage") is double kv && kv <= 0) Add("CGM-PAR-001", $"BaseVoltage '{n.Id}' has nominalVoltage ≤ 0.", n.Id, CgmesProfileKind.Equipment);
                        break;
                }
            }
        }

        // ---------- state variables ----------
        private void StateVariables()
        {
            if (!HasProfile(CgmesProfileKind.StateVariables)) return;
            // Terminal → TopologicalNode (TP Terminal.TopologicalNode, else through its ConnectivityNode).
            string? TnOf(Node t) => t.R("Terminal.TopologicalNode") ?? Get(t.R("Terminal.ConnectivityNode"))?.R("ConnectivityNode.TopologicalNode");
            var balance = new Dictionary<string, (double P, double Q, int N)>(StringComparer.Ordinal);
            foreach (var f in OfClass(c => c == "SvPowerFlow"))
            {
                if (Get(f.R("SvPowerFlow.Terminal")) is not { } t || TnOf(t) is not string tn) continue;
                var (p, q, n) = balance.GetValueOrDefault(tn);
                balance[tn] = (p + (f.D("SvPowerFlow.p") ?? 0), q + (f.D("SvPowerFlow.q") ?? 0), n + 1);
            }
            var rank = _order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
            foreach (var (tn, (p, q, n)) in balance.OrderBy(k => rank.GetValueOrDefault(k.Key)))
                if (Math.Abs(p) > opt.KclToleranceMw || Math.Abs(q) > opt.KclToleranceMw)
                    Add("CGM-SV-001", $"TopologicalNode '{tn}' ({Get(tn)?.S("IdentifiedObject.name")}): SV flows of {n} terminal(s) do not balance " +
                        $"(ΣP = {p.ToString("0.######", CultureInfo.InvariantCulture)} MW, ΣQ = {q.ToString("0.######", CultureInfo.InvariantCulture)} Mvar).", tn, CgmesProfileKind.StateVariables);
            var withVoltage = OfClass(c => c == "SvVoltage").Select(s => (Tn: s.R("SvVoltage.TopologicalNode"), V: s.D("SvVoltage.v"))).ToList();
            var covered = withVoltage.Where(x => x.Tn is not null).Select(x => x.Tn!).ToHashSet(StringComparer.Ordinal);
            foreach (var tn in OfClass(c => c == "TopologicalNode").Where(x => x.DeclaredIn >= 0 && !covered.Contains(x.Id)))
                Add("CGM-SV-002", $"TopologicalNode '{tn.Id}' has no SvVoltage.", tn.Id, CgmesProfileKind.StateVariables);
            foreach (var (tn, v) in withVoltage.Where(x => x.V is < 0))
                Add("CGM-SV-002", $"SvVoltage of TopologicalNode '{tn}' is negative.", tn, CgmesProfileKind.StateVariables);
        }
    }
}
