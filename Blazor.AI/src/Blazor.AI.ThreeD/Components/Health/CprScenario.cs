using System.Globalization;
using System.Text;

namespace Blazor.AI.ThreeD;

/// <summary>A trainee action in the resuscitation scenario.</summary>
public sealed record TrainingAction(string Id, string Label, string Icon, string Hint);

/// <summary>One logged decision with its effect on the score.</summary>
public sealed record TrainingLogEntry(int Second, string Action, int Points, string Feedback);

/// <summary>
/// Adult basic life support (cardiac arrest, shockable rhythm) as a deterministic state machine, following the
/// usual BLS sequence: scene safety → responsiveness → call for help/AED → breathing &amp; pulse → CPR 30:2 → AED → shock →
/// resume CPR → reassess. Vitals respond to what the trainee does. For training simulation only.
/// </summary>
public sealed class CprScenario
{
    public static readonly IReadOnlyList<TrainingAction> Actions =
    [
        new("safety", "Check scene safety", "bi-cone-striped", "Look for hazards before approaching."),
        new("respond", "Tap & shout", "bi-megaphone", "Check responsiveness."),
        new("help", "Call for help & AED", "bi-telephone", "Activate emergency response and get an AED."),
        new("breathing", "Check breathing & pulse", "bi-lungs", "No more than 10 seconds."),
        new("compress", "30 compressions", "bi-heart-pulse", "Centre of the chest, 100–120/min, 5–6 cm deep."),
        new("breaths", "2 rescue breaths", "bi-wind", "Head tilt, chin lift; 1 second each."),
        new("pads", "Attach AED pads", "bi-plug", "Right upper chest and left side."),
        new("analyse", "Let AED analyse", "bi-activity", "Hands off the patient."),
        new("shock", "Stand clear & shock", "bi-lightning-charge", "Only when the AED advises a shock."),
        new("pulse", "Reassess pulse", "bi-hand-index", "After 2 minutes of CPR."),
    ];

    private readonly List<TrainingLogEntry> _log = [];
    private readonly HashSet<string> _done = [];

    public int Second { get; private set; }
    public int Score { get; private set; }
    public string Phase { get; private set; } = "Collapsed";

    /// <summary>VF, asystole-free scenario: VF → (shock + CPR) → Sinus.</summary>
    public string Rhythm { get; private set; } = "VF";

    public int HeartRate { get; private set; }
    public int SpO2 { get; private set; } = 88;
    public string BloodPressure { get; private set; } = "0/0";
    public int EtCo2 { get; private set; } = 8;
    public bool AedArrived { get; private set; }
    public bool PadsOn { get; private set; }
    public bool ShockAdvised { get; private set; }
    public bool Compressing { get; private set; }
    public bool Rosc { get; private set; }
    public int CompressionCycles { get; private set; }
    public int? FirstCompressionSecond { get; private set; }
    public int ShocksDelivered { get; private set; }
    public bool Finished { get; private set; }

    /// <summary>Compression rate per minute chosen by the trainee.</summary>
    public int Rate { get; set; } = 110;

    /// <summary>Compression depth in cm chosen by the trainee.</summary>
    public double Depth { get; set; } = 5.5;

    public IReadOnlyList<TrainingLogEntry> Log => _log;

    public bool QualityGood => Rate is >= 100 and <= 120 && Depth is >= 5 and <= 6;

    /// <summary>The action the guidelines expect next (shown as a hint in coaching mode).</summary>
    public string Expected
    {
        get
        {
            if (!_done.Contains("safety")) return "safety";
            if (!_done.Contains("respond")) return "respond";
            if (!_done.Contains("help")) return "help";
            if (!_done.Contains("breathing")) return "breathing";
            if (CompressionCycles == 0) return "compress";
            if (!_done.Contains("breaths")) return "breaths";
            if (ShocksDelivered > 0 && !ShockAdvised)
            {
                // After a shock: about 2 minutes (5 cycles) of CPR before reassessing.
                return CompressionCycles - _cyclesAtShock < 3 ? "compress" : Rosc ? "compress" : "pulse";
            }

            if (AedArrived && !PadsOn) return "pads";
            if (PadsOn && !_done.Contains("analyse")) return "analyse";
            return ShockAdvised ? "shock" : "compress";
        }
    }

    private int _cyclesAtShock;

    /// <summary>Advances simulated time by one second.</summary>
    public void Tick()
    {
        if (Finished)
        {
            return;
        }

        Second++;
        if (!AedArrived && _done.Contains("help") && Second >= _helpAt + 25)
        {
            AedArrived = true;
            Note(0, "AED arrived", "A bystander brought the AED.");
        }

        if (Rosc)
        {
            HeartRate = 82;
            SpO2 = Math.Min(97, SpO2 + 1);
            BloodPressure = "112/70";
            EtCo2 = 38;
            return;
        }

        HeartRate = 0;
        BloodPressure = Compressing ? (QualityGood ? "60/20" : "40/10") : "0/0";
        EtCo2 = Compressing ? (QualityGood ? 18 : 10) : 6;
        if (Compressing)
        {
            SpO2 = Math.Clamp(SpO2 + (QualityGood ? 0 : -1), 55, 90);
        }
        else if (Second % 2 == 0)
        {
            SpO2 = Math.Max(45, SpO2 - 1);
        }

        if (Compressing && Second - _compressStart >= 18)
        {
            Compressing = false;
        }
    }

    private int _helpAt;
    private int _compressStart;

    /// <summary>Applies a trainee action and returns the feedback.</summary>
    public TrainingLogEntry Do(string id)
    {
        if (Finished)
        {
            return Note(0, id, "The scenario has ended. Restart to practise again.");
        }

        var expected = Expected;
        var inOrder = id == expected;
        switch (id)
        {
            case "safety":
                _done.Add(id);
                return Note(inOrder ? 10 : 2, Label(id), inOrder ? "Good: the scene is safe." : "Scene safety is the very first step.");
            case "respond":
                _done.Add(id);
                Phase = "Unresponsive";
                return Note(inOrder ? 10 : 2, Label(id), "No response.");
            case "help":
                _done.Add(id);
                _helpAt = Second;
                return Note(inOrder ? 10 : 3, Label(id), "Emergency services called; an AED is on its way.");
            case "breathing":
                _done.Add(id);
                Phase = "Cardiac arrest";
                return Note(inOrder ? 10 : 2, Label(id), "Only gasping, no pulse: this is cardiac arrest. Start CPR now.");
            case "compress":
                if (!_done.Contains("breathing"))
                {
                    return Note(-5, Label(id), "Confirm arrest (breathing and pulse) before compressing.");
                }

                Compressing = true;
                _compressStart = Second;
                CompressionCycles++;
                FirstCompressionSecond ??= Second;
                var q = QualityGood ? "Good quality" : $"Adjust: rate {Rate}/min, depth {Depth.ToString("0.0", CultureInfo.InvariantCulture)} cm (target 100–120, 5–6 cm)";
                return Note((inOrder ? 10 : 4) + (QualityGood ? 5 : -3), Label(id), $"{q}. Cycle {CompressionCycles}.");
            case "breaths":
                if (CompressionCycles == 0)
                {
                    return Note(-3, Label(id), "Adults: start with compressions, then breaths (30:2).");
                }

                _done.Add(id);
                return Note(inOrder ? 8 : 3, Label(id), "Chest rises with each breath.");
            case "pads":
                if (!AedArrived)
                {
                    return Note(-2, Label(id), "The AED has not arrived yet; keep doing CPR.");
                }

                PadsOn = true;
                return Note(inOrder ? 10 : 4, Label(id), "Pads placed correctly.");
            case "analyse":
                if (!PadsOn)
                {
                    return Note(-3, Label(id), "Attach the pads first.");
                }

                _done.Add(id);
                Compressing = false;
                ShockAdvised = Rhythm == "VF";
                return Note(inOrder ? 10 : 4, Label(id), ShockAdvised ? "Shockable rhythm (VF). Shock advised." : "No shock advised. Continue CPR.");
            case "shock":
                if (!ShockAdvised)
                {
                    return Note(-10, Label(id), "Unsafe: only shock when the AED advises it.");
                }

                ShockAdvised = false;
                ShocksDelivered++;
                _cyclesAtShock = CompressionCycles;
                Rhythm = "Organised";
                _done.Remove("analyse");
                return Note(15, Label(id), "Shock delivered. Resume compressions immediately.");
            case "pulse":
                if (ShocksDelivered == 0 || CompressionCycles - _cyclesAtShock < 3)
                {
                    return Note(-3, Label(id), "Don't interrupt CPR: reassess after about 2 minutes (5 cycles).");
                }

                Rosc = true;
                Rhythm = "Sinus";
                Phase = "ROSC";
                Compressing = false;
                Finished = true;
                return Note(20, Label(id), "Pulse present: return of spontaneous circulation. Place in recovery and monitor.");
            default:
                return Note(0, id, "Unknown action.");
        }
    }

    private static string Label(string id) => Actions.FirstOrDefault(a => a.Id == id)?.Label ?? id;

    private TrainingLogEntry Note(int points, string action, string feedback)
    {
        Score = Math.Max(0, Score + points);
        var entry = new TrainingLogEntry(Second, action, points, feedback);
        _log.Add(entry);
        return entry;
    }

    /// <summary>Offline debrief: what went well, what to improve, key timings.</summary>
    public string LocalDebrief()
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"**Score {Score}** · outcome: {(Rosc ? "ROSC achieved" : "no ROSC yet")}.\n\n");
        var good = _log.Where(l => l.Points >= 10).Select(l => l.Action).Distinct().ToList();
        var bad = _log.Where(l => l.Points < 0).ToList();
        if (good.Count > 0)
        {
            b.Append("**Went well:** ").AppendJoin(", ", good).Append(".\n\n");
        }

        if (bad.Count > 0)
        {
            b.Append("**Improve:** ").AppendJoin(" ", bad.Select(l => l.Feedback)).Append("\n\n");
        }

        b.Append(CultureInfo.InvariantCulture, $"Time to first compression: {(FirstCompressionSecond is { } f ? $"{f} s" : "not started")} (aim for under 60 s). Shocks: {ShocksDelivered}. Compression cycles: {CompressionCycles}.");
        return b.ToString();
    }

    public string Transcript() =>
        string.Join("; ", _log.Select(l => $"t{l.Second}s {l.Action} ({l.Points:+0;-0;0}): {l.Feedback}")) +
        string.Create(CultureInfo.InvariantCulture, $". Final score {Score}, ROSC {(Rosc ? "yes" : "no")}, first compression at {FirstCompressionSecond?.ToString(CultureInfo.InvariantCulture) ?? "never"} s, shocks {ShocksDelivered}, compression rate {Rate}/min, depth {Depth:0.0} cm.");

    /// <summary>ECG polyline points (0–1 normalised) for the HUD monitor.</summary>
    public IEnumerable<(double X, double Y)> Ecg(int samples = 120)
    {
        for (var i = 0; i < samples; i++)
        {
            var t = (i + Second * 7) / 30.0;
            double y;
            if (Rhythm == "VF" && !Rosc)
            {
                y = 0.5 + 0.22 * Math.Sin(t * 5.3) * Math.Sin(t * 1.7 + 0.4) + 0.08 * Math.Sin(t * 13.1);
            }
            else if (Rosc)
            {
                var phase = t % 1.0;
                y = phase < 0.04 ? 0.5 - phase * 10 : phase < 0.08 ? 0.1 + (phase - 0.04) * 22 : phase < 0.12 ? 0.98 - (phase - 0.08) * 12 : phase is > 0.3 and < 0.42 ? 0.5 - Math.Sin((phase - 0.3) / 0.12 * Math.PI) * 0.12 : 0.5;
            }
            else
            {
                y = 0.5 + (Compressing ? 0.18 * Math.Sin(t * 11) : 0.03 * Math.Sin(t * 3));
            }

            yield return (i / (double)(samples - 1), y);
        }
    }
}
