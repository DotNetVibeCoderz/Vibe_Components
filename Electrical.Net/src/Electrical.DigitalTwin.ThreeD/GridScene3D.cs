using System.Globalization;
using System.Numerics;
using Electrical.Core.Network;
using Electrical.DigitalTwin;
using Electrical.PowerFlow;
using Electrical.Visualization;

namespace Electrical.DigitalTwin.ThreeD;

/// <summary>Kind of a 3D scene object.</summary>
public enum GridElementKind
{
    Ground, Busbar, Gantry, Transformer, Breaker, Disconnector, Tower, OverheadLine, Cable, Generator, Load,
}

/// <summary>
/// One object of a <see cref="GridScene3D"/>: a mesh in world coordinates [m] linked to a network element, with a colour state that
/// live data updates (<see cref="GridScene3D.ApplyPowerFlow"/>, <see cref="GridScene3D.ApplyTwinState"/>).
/// </summary>
public sealed class SceneObject
{
    internal SceneObject(string name, GridElementKind kind, MeshData mesh, Rgba baseColor, float metallic, float roughness)
    {
        Name = name; Kind = kind; Mesh = mesh; BaseColor = baseColor; Color = baseColor; Metallic = metallic; Roughness = roughness;
    }

    public string Name { get; }
    public GridElementKind Kind { get; }
    public MeshData Mesh { get; }
    /// <summary>Bus id (busbars, generators, loads, gantries).</summary>
    public int? BusId { get; internal init; }
    /// <summary>Index into <see cref="PowerNetwork.Branches"/> (lines, cables, transformers, towers, switchgear).</summary>
    public int? BranchIndex { get; internal init; }
    /// <summary>Colour when no live data has been applied.</summary>
    public Rgba BaseColor { get; }
    /// <summary>Current (sRGB) colour.</summary>
    public Rgba Color { get; internal set; }
    public float Metallic { get; }
    public float Roughness { get; }
    /// <summary>True while an alarm is active on the element.</summary>
    public bool Alarm { get; internal set; }
    public TwinAlarmSeverity AlarmSeverity { get; internal set; }
    /// <summary>Emissive glow [0, 1] (alarm flashing).</summary>
    public float Emission { get; internal set; }
    /// <summary>Switch state for breakers / disconnectors and the energization of branches.</summary>
    public bool Closed { get; internal set; } = true;
    /// <summary>Optional label text and its world anchor.</summary>
    public string? Label { get; internal set; }
    public Vector3 LabelAnchor { get; internal init; }
    /// <summary>Incremented whenever colour, emission or label changes (lets renderers push only changed materials).</summary>
    public int Version { get; internal set; }

    internal void Set(Rgba color, bool? alarm = null, string? label = null)
    {
        bool changed = color != Color || (label is not null && label != Label) || (alarm is { } a && a != Alarm);
        Color = color;
        if (alarm is { } al) { Alarm = al; if (!al) Emission = 0; }
        if (label is not null) Label = label;
        if (changed) Version++;
    }

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A procedurally built 3D model of a substation / feeder, independent of any renderer.</summary>
public sealed class GridScene3D
{
    private readonly List<SceneObject> _objects = [];

    internal GridScene3D(PowerNetwork network, GridSceneOptions options, IReadOnlyDictionary<int, Vector3> busPositions)
    {
        Network = network; Options = options; BusPositions = busPositions;
    }

    public PowerNetwork Network { get; }
    public GridSceneOptions Options { get; }
    /// <summary>Busbar centre of every bus [m].</summary>
    public IReadOnlyDictionary<int, Vector3> BusPositions { get; }
    public IReadOnlyList<SceneObject> Objects => _objects;

    internal SceneObject Add(SceneObject o) { _objects.Add(o); return o; }

    /// <summary>Number of objects of a kind.</summary>
    public int Count(GridElementKind kind) => _objects.Count(o => o.Kind == kind);

    public IEnumerable<SceneObject> OfKind(GridElementKind kind) => _objects.Where(o => o.Kind == kind);
    public IEnumerable<SceneObject> ForBus(int busId) => _objects.Where(o => o.BusId == busId);
    public IEnumerable<SceneObject> ForBranch(int index) => _objects.Where(o => o.BranchIndex == index);

    public int TriangleCount => _objects.Sum(o => o.Mesh.TriangleCount);

    /// <summary>Bounds of all objects.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var o in _objects)
        {
            if (o.Mesh.VertexCount == 0) continue;
            var (a, b) = o.Mesh.Bounds();
            lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b);
        }
        return _objects.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (lo, hi);
    }

    private static readonly Rgba ClosedColor = Rgba.Parse("#e5484d"); // SCADA convention: red = closed / energized
    private static readonly Rgba OpenColor = Rgba.Parse("#2bb673");   // green = open
    private static readonly Rgba DeEnergized = Rgba.Parse("#6b7280");

    /// <summary>
    /// Colours buses by voltage (<see cref="ColorRamp.Voltage"/>, ±<see cref="GridSceneOptions.VoltageSpanPu"/>), branches by loading
    /// (<see cref="ColorRamp.Loading"/>; unrated branches relative to <see cref="GridSceneOptions.UnratedReferenceMva"/> or to the
    /// largest flow of the result), out-of-service branches grey, switchgear red (closed) / green (open), and updates labels.
    /// </summary>
    public void ApplyPowerFlow(PowerFlowResult result) => Apply(result.Buses, result.Branches, null);

    /// <summary>As <see cref="ApplyPowerFlow"/> from the estimated state of a <see cref="GridTwin"/>, and flags alarmed elements.</summary>
    public void ApplyTwinState(TwinState state) => Apply(state.Buses, state.Branches, state.Alarms);

    private void Apply(IReadOnlyList<BusResult> buses, IReadOnlyList<BranchResult> branches, IReadOnlyList<ExplainedAlarm>? alarms)
    {
        var vramp = ColorRamp.Voltage(Options.VoltageSpanPu);
        var byBus = buses.ToDictionary(b => b.BusId);
        var byBranch = branches.ToDictionary(b => b.Index);
        double reference = Options.UnratedReferenceMva ?? Math.Max(1e-9, branches.Count == 0 ? 1 : branches.Max(b => Math.Max(b.SFrom, b.STo)));
        var alarmed = new Dictionary<string, TwinAlarmSeverity>(StringComparer.OrdinalIgnoreCase);
        if (alarms is not null)
            foreach (var a in alarms)
                if (!alarmed.TryGetValue(a.Element, out var s) || a.Severity > s) alarmed[a.Element] = a.Severity;

        foreach (var o in _objects)
        {
            if (o.BusId is int bid && byBus.TryGetValue(bid, out var br) && o.Kind == GridElementKind.Busbar)
            {
                bool al = alarmed.TryGetValue($"bus {bid}", out var sev);
                o.AlarmSeverity = sev;
                o.Set(vramp.Map(br.Vm), al, o.Label is null ? null : string.Create(CultureInfo.InvariantCulture, $"{BusName(bid)}  {br.Vm:0.000} pu"));
            }
            else if (o.BranchIndex is int k)
            {
                var branch = Network.Branches[k];
                bool inService = byBranch.TryGetValue(k, out var flow) && branch.InService;
                double loading = !inService ? 0 : flow!.RateA > 0 ? flow.LoadingPercent : 100 * Math.Max(flow.SFrom, flow.STo) / reference;
                bool al = alarmed.TryGetValue($"branch {branch.From}-{branch.To}", out var sev);
                o.AlarmSeverity = sev;
                o.Closed = inService;
                switch (o.Kind)
                {
                    case GridElementKind.OverheadLine or GridElementKind.Cable or GridElementKind.Transformer:
                        o.Set(inService ? ColorRamp.Loading.Map(loading) : DeEnergized, al, o.Label is null ? null :
                            string.Create(CultureInfo.InvariantCulture, $"{BranchName(k)}  {(inService ? $"{loading:0} %" : "open")}"));
                        break;
                    case GridElementKind.Breaker or GridElementKind.Disconnector:
                        o.Set(inService ? ClosedColor : OpenColor);
                        break;
                }
            }
        }
    }

    /// <summary>Clears live colours and alarms.</summary>
    public void Reset()
    {
        foreach (var o in _objects) { o.Set(o.BaseColor, false); o.Emission = 0; }
    }

    /// <summary>
    /// Advances the alarm flashing animation: alarmed objects glow with emission 0.5 + 0.5·sin(2π f t) (f = <see cref="GridSceneOptions.AlarmFlashHz"/>),
    /// others are dark. Returns the number of objects whose emission changed.
    /// </summary>
    public int UpdateAlarmFlash(double timeSeconds)
    {
        int n = 0;
        float on = (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * Options.AlarmFlashHz * timeSeconds));
        foreach (var o in _objects)
        {
            float e = o.Alarm ? on : 0;
            if (e != o.Emission) { o.Emission = e; o.Version++; n++; }
        }
        return n;
    }

    /// <summary>Alarm glow colour of a severity.</summary>
    public static Rgba AlarmColor(TwinAlarmSeverity s) => s >= TwinAlarmSeverity.High ? Rgba.Parse("#ff2d2d") : Rgba.Parse("#f5a524");

    internal string BusName(int id) => Network.GetBus(id).Name is { Length: > 0 } n ? $"Bus {id} {n}" : $"Bus {id}";
    internal string BranchName(int k) => Network.Branches[k] is var b && b.Name is { Length: > 0 } n ? n : $"{(b.IsTransformer ? "Trafo" : "Line")} {b.From}-{b.To}";
}
