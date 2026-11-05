using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.Robotics;

public sealed record FruitTarget(int Id, Point2 Position, double HeightM, double Ripeness);

public sealed record HarvestPlan(IReadOnlyList<FruitTarget> Order, double TravelDistanceM, double EstimatedMinutes, int Skipped);

/// <summary>Harvest robot task planner: ripeness filtering + TSP route (nearest neighbour seeded, 2-opt improved).</summary>
public static class HarvestPlanner
{
    /// <summary>Ripeness 0–1 from fruit RGB (tomato/chili red index: (R−G)/(R+G)).</summary>
    public static double RipenessFromRgb(byte r, byte g, byte b) => Math.Clamp(((double)r - g) / Math.Max(1, r + g) * 1.6 + 0.2, 0, 1);

    public static HarvestPlan Plan(IEnumerable<FruitTarget> targets, Point2 start, double minRipeness = 0.75, double pickSeconds = 6, double speedMs = 0.5)
    {
        var all = targets.ToList();
        var ripe = all.Where(t => t.Ripeness >= minRipeness).ToList();
        if (ripe.Count == 0) return new HarvestPlan([], 0, 0, all.Count);
        // nearest neighbour
        var route = new List<FruitTarget>();
        var left = new HashSet<FruitTarget>(ripe);
        var cur = start;
        while (left.Count > 0)
        {
            var next = left.MinBy(t => t.Position.DistanceTo(cur))!;
            route.Add(next); left.Remove(next); cur = next.Position;
        }
        // 2-opt
        bool improved = true;
        while (improved)
        {
            improved = false;
            for (int i = 0; i < route.Count - 1; i++)
                for (int j = i + 1; j < route.Count; j++)
                {
                    var a = i == 0 ? start : route[i - 1].Position;
                    var b = route[i].Position; var c = route[j].Position;
                    var d = j + 1 < route.Count ? route[j + 1].Position : (Point2?)null;
                    double before = a.DistanceTo(b) + (d is { } dd ? c.DistanceTo(dd) : 0);
                    double after = a.DistanceTo(c) + (d is { } de ? b.DistanceTo(de) : 0);
                    if (after + 1e-9 < before) { route.Reverse(i, j - i + 1); improved = true; }
                }
        }
        double dist = 0; cur = start;
        foreach (var t in route) { dist += cur.DistanceTo(t.Position); cur = t.Position; }
        double minutes = (dist / speedMs + route.Count * pickSeconds) / 60;
        return new HarvestPlan(route, Math.Round(dist, 2), Math.Round(minutes, 1), all.Count - ripe.Count);
    }
}

public enum GreenhouseRobotState { Idle, Navigating, Inspecting, Spraying, ReturningToDock, Charging }

public sealed record RobotLogEntry(TimeSpan Time, GreenhouseRobotState State, string Message, double BatteryPct);

/// <summary>
/// Greenhouse scouting/spraying robot: event-driven state machine with battery management.
/// Patrols benches, inspects plants (pluggable inspector) and spot-sprays when a problem is detected.
/// </summary>
public sealed class GreenhouseRobot(IReadOnlyList<Point2> benches, Point2 dock, Func<int, double>? inspector = null)
{
    private readonly Func<int, double> _inspector = inspector ?? (i => (i * 37 % 11) / 10.0); // deterministic default: disease score 0–1
    private readonly List<RobotLogEntry> _log = [];
    public GreenhouseRobotState State { get; private set; } = GreenhouseRobotState.Idle;
    public double BatteryPct { get; private set; } = 100;
    public double SpeedMs { get; init; } = 0.6;
    public double DrainPerMeterPct { get; init; } = 0.08;
    public double DrainPerActionPct { get; init; } = 0.4;
    public double LowBatteryPct { get; init; } = 20;
    public double SprayThreshold { get; init; } = 0.6;
    public IReadOnlyList<RobotLogEntry> Log => _log;

    /// <summary>Runs one complete patrol mission, returning the number of benches sprayed.</summary>
    public int RunMission()
    {
        var t = TimeSpan.Zero; var pos = dock; int sprayed = 0;
        void Log(string m) => _log.Add(new RobotLogEntry(t, State, m, Math.Round(BatteryPct, 1)));
        void Move(Point2 to)
        {
            double d = pos.DistanceTo(to);
            t += TimeSpan.FromSeconds(d / SpeedMs);
            BatteryPct -= d * DrainPerMeterPct;
            pos = to;
        }
        for (int i = 0; i < benches.Count; i++)
        {
            double needed = (pos.DistanceTo(benches[i]) + benches[i].DistanceTo(dock)) * DrainPerMeterPct + 2 * DrainPerActionPct;
            if (BatteryPct - needed < LowBatteryPct)
            {
                State = GreenhouseRobotState.ReturningToDock; Log($"Battery low ({BatteryPct:F0}%), returning to dock"); Move(dock);
                State = GreenhouseRobotState.Charging; t += TimeSpan.FromMinutes((100 - BatteryPct) * 0.9); BatteryPct = 100; Log("Charged to 100%");
            }
            State = GreenhouseRobotState.Navigating; Move(benches[i]);
            State = GreenhouseRobotState.Inspecting; t += TimeSpan.FromSeconds(20); BatteryPct -= DrainPerActionPct;
            double score = _inspector(i);
            Log($"Bench {i + 1}: disease score {score:F2}");
            if (score >= SprayThreshold)
            {
                State = GreenhouseRobotState.Spraying; t += TimeSpan.FromSeconds(30); BatteryPct -= DrainPerActionPct * 2; sprayed++;
                Log($"Bench {i + 1}: spot-sprayed");
            }
        }
        State = GreenhouseRobotState.ReturningToDock; Move(dock);
        State = GreenhouseRobotState.Idle; Log($"Mission complete — {sprayed}/{benches.Count} benches treated");
        return sprayed;
    }
}

public static class RoboticsBuilderExtensions
{
    public static AgritechBuilder AddRobotics(this AgritechBuilder builder) { builder.TryAddModule("Robotics"); return builder; }
}
