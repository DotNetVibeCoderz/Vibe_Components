using Agritech.Core.Geo;
using Agritech.GIS;

namespace Agritech.Robotics;

/// <summary>2D pose in a local metric frame (heading in radians, 0 = +X/east, CCW positive).</summary>
public readonly record struct Pose2(double X, double Y, double Heading)
{
    public Point2 Position => new(X, Y);
}

/// <summary>Occupancy grid for field/greenhouse navigation.</summary>
public sealed class OccupancyGrid(int width, int height, double resolutionM, Point2 origin = default)
{
    private readonly bool[] _occ = new bool[width * height];
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double Resolution { get; } = resolutionM;
    public Point2 Origin { get; } = origin;

    public bool IsBlocked(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height || _occ[y * Width + x];
    public void SetBlocked(int x, int y, bool blocked = true) { if (x >= 0 && y >= 0 && x < Width && y < Height) _occ[y * Width + x] = blocked; }

    public void AddRectangleObstacle(Point2 min, Point2 max)
    {
        var (x0, y0) = ToCell(min); var (x1, y1) = ToCell(max);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) SetBlocked(x, y);
    }

    /// <summary>Inflates obstacles by robot radius (configuration-space).</summary>
    public OccupancyGrid Inflate(double radiusM)
    {
        int r = (int)Math.Ceiling(radiusM / Resolution);
        var g = new OccupancyGrid(Width, Height, Resolution, Origin);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (_occ[y * Width + x])
                    for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r) g.SetBlocked(x + dx, y + dy);
        return g;
    }

    public (int X, int Y) ToCell(Point2 p) => ((int)Math.Floor((p.X - Origin.X) / Resolution), (int)Math.Floor((p.Y - Origin.Y) / Resolution));
    public Point2 ToWorld(int x, int y) => new(Origin.X + (x + 0.5) * Resolution, Origin.Y + (y + 0.5) * Resolution);
}

/// <summary>A* path planner on an occupancy grid (8-connected, octile heuristic, no corner cutting).</summary>
public static class PathPlanner
{
    public static IReadOnlyList<Point2>? AStar(OccupancyGrid grid, Point2 start, Point2 goal)
    {
        var s = grid.ToCell(start); var g = grid.ToCell(goal);
        if (grid.IsBlocked(s.X, s.Y) || grid.IsBlocked(g.X, g.Y)) return null;
        int w = grid.Width;
        var gScore = new Dictionary<int, double> { [s.Y * w + s.X] = 0 };
        var came = new Dictionary<int, int>();
        var open = new PriorityQueue<int, double>();
        open.Enqueue(s.Y * w + s.X, H(s, g));
        var closed = new HashSet<int>();
        ReadOnlySpan<(int Dx, int Dy, double C)> moves = [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.41421356), (1, -1, 1.41421356), (-1, 1, 1.41421356), (-1, -1, 1.41421356)];
        while (open.TryDequeue(out int cur, out _))
        {
            if (!closed.Add(cur)) continue;
            int cx = cur % w, cy = cur / w;
            if (cx == g.X && cy == g.Y)
            {
                var path = new List<Point2> { grid.ToWorld(cx, cy) };
                while (came.TryGetValue(cur, out var prev)) { cur = prev; path.Add(grid.ToWorld(cur % w, cur / w)); }
                path.Reverse();
                path[0] = start; path[^1] = goal;
                return Smooth(grid, path);
            }
            foreach (var (dx, dy, c) in moves)
            {
                int nx = cx + dx, ny = cy + dy;
                if (grid.IsBlocked(nx, ny)) continue;
                if (dx != 0 && dy != 0 && (grid.IsBlocked(cx + dx, cy) || grid.IsBlocked(cx, cy + dy))) continue;
                int key = ny * w + nx;
                double tentative = gScore[cur] + c;
                if (tentative < gScore.GetValueOrDefault(key, double.MaxValue))
                {
                    gScore[key] = tentative; came[key] = cur;
                    open.Enqueue(key, tentative + H((nx, ny), g));
                }
            }
        }
        return null;

        static double H((int X, int Y) a, (int X, int Y) b)
        {
            int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
            return dx + dy + (1.41421356 - 2) * Math.Min(dx, dy);
        }
    }

    /// <summary>Line-of-sight path shortcutting.</summary>
    public static List<Point2> Smooth(OccupancyGrid grid, List<Point2> path)
    {
        if (path.Count < 3) return path;
        var res = new List<Point2> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int j = path.Count - 1;
            while (j > i + 1 && !LineOfSight(grid, path[i], path[j])) j--;
            res.Add(path[j]); i = j;
        }
        return res;
    }

    public static bool LineOfSight(OccupancyGrid g, Point2 a, Point2 b)
    {
        double len = a.DistanceTo(b);
        int steps = Math.Max(1, (int)(len / (g.Resolution * 0.5)));
        for (int k = 0; k <= steps; k++)
        {
            var p = a + (b - a) * (k / (double)steps);
            var (x, y) = g.ToCell(p);
            if (g.IsBlocked(x, y)) return false;
        }
        return true;
    }

    /// <summary>Tractor coverage path with implement width and headland turns, in the field's local frame.</summary>
    public static IReadOnlyList<Point2> FieldCoverage(GeoPolygon field, double implementWidthM, double headlandM, out GeoCoordinate origin)
    {
        var ring = field.ToLocal(out origin);
        double angle = Geometry.LongestEdgeAngle(ring);
        var rot = ring.Select(p => Geometry.Rotate(p, -angle)).ToArray();
        double minY = rot.Min(p => p.Y) + headlandM, maxY = rot.Max(p => p.Y) - headlandM;
        var path = new List<Point2>();
        bool flip = false;
        for (double y = minY + implementWidthM / 2; y <= maxY; y += implementWidthM)
        {
            var iv = Geometry.ScanlineIntervals(rot, y);
            if (iv.Count == 0) continue;
            double x0 = iv[0].X0 + headlandM, x1 = iv[^1].X1 - headlandM;
            if (x1 <= x0) continue;
            var a = new Point2(flip ? x1 : x0, y); var b = new Point2(flip ? x0 : x1, y);
            path.Add(Geometry.Rotate(a, angle)); path.Add(Geometry.Rotate(b, angle));
            flip = !flip;
        }
        return path;
    }
}

/// <summary>Kinematic bicycle model of a tractor (rear-axle reference).</summary>
public sealed class TractorModel(double wheelbaseM = 2.6, double maxSteerRad = 0.6)
{
    public double Wheelbase { get; } = wheelbaseM;
    public double MaxSteer { get; } = maxSteerRad;

    public Pose2 Step(Pose2 p, double speedMs, double steerRad, double dt)
    {
        steerRad = Math.Clamp(steerRad, -MaxSteer, MaxSteer);
        double h = p.Heading + speedMs / Wheelbase * Math.Tan(steerRad) * dt;
        return new Pose2(p.X + speedMs * Math.Cos(p.Heading) * dt, p.Y + speedMs * Math.Sin(p.Heading) * dt, Math.Atan2(Math.Sin(h), Math.Cos(h)));
    }
}

/// <summary>Pure-pursuit path tracking controller (Coulter 1992) — the standard for autonomous tractors.</summary>
public sealed class PurePursuitController(IReadOnlyList<Point2> path, double lookaheadM = 4)
{
    private int _segment;
    public double Lookahead { get; set; } = lookaheadM;
    public bool Finished { get; private set; }

    public double SteeringAngle(Pose2 pose, double wheelbase)
    {
        var target = LookaheadPoint(pose);
        double dx = target.X - pose.X, dy = target.Y - pose.Y;
        double localY = -Math.Sin(pose.Heading) * dx + Math.Cos(pose.Heading) * dy;
        double ld2 = dx * dx + dy * dy;
        double curvature = ld2 < 1e-9 ? 0 : 2 * localY / ld2;
        return Math.Atan(curvature * wheelbase);
    }

    /// <summary>Signed cross-track error (m) to the current segment.</summary>
    public double CrossTrackError(Pose2 pose)
    {
        if (path.Count < 2) return 0;
        var a = path[Math.Min(_segment, path.Count - 2)]; var b = path[Math.Min(_segment + 1, path.Count - 1)];
        var ab = b - a; var ap = pose.Position - a;
        return ab.Length < 1e-9 ? ap.Length : ab.Cross(ap) / ab.Length;
    }

    private Point2 LookaheadPoint(Pose2 pose)
    {
        var p = pose.Position;
        for (int i = _segment; i < path.Count - 1; i++)
        {
            var a = path[i]; var b = path[i + 1];
            var d = b - a; var f = a - p;
            double A = d.Dot(d), B = 2 * f.Dot(d), C = f.Dot(f) - Lookahead * Lookahead;
            double disc = B * B - 4 * A * C;
            if (disc >= 0 && A > 1e-12)
            {
                double t = (-B + Math.Sqrt(disc)) / (2 * A);
                if (t is >= 0 and <= 1) { _segment = i; return a + d * t; }
            }
        }
        var end = path[^1];
        if (p.DistanceTo(end) < Lookahead * 0.5) Finished = true;
        _segment = Math.Max(0, path.Count - 2);
        return end;
    }

    /// <summary>Closed-loop simulation; returns the trajectory and RMS cross-track error.</summary>
    public static (IReadOnlyList<Pose2> Trajectory, double RmsCte) Simulate(IReadOnlyList<Point2> path, TractorModel tractor, double speedMs = 2, double lookahead = 4, double dt = 0.1, int maxSteps = 200_000)
    {
        var ctl = new PurePursuitController(path, lookahead);
        var d0 = path[1] - path[0];
        var pose = new Pose2(path[0].X, path[0].Y, Math.Atan2(d0.Y, d0.X));
        var traj = new List<Pose2> { pose };
        double se = 0; int n = 0;
        for (int k = 0; k < maxSteps && !ctl.Finished; k++)
        {
            double steer = ctl.SteeringAngle(pose, tractor.Wheelbase);
            pose = tractor.Step(pose, speedMs, steer, dt);
            double e = ctl.CrossTrackError(pose); se += e * e; n++;
            if (k % 5 == 0) traj.Add(pose);
        }
        return (traj, n == 0 ? 0 : Math.Sqrt(se / n));
    }
}
