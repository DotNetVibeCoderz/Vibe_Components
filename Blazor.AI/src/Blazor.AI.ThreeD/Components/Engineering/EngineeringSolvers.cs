namespace Blazor.AI.ThreeD;

/// <summary>Small, dependency-free engineering solvers used by <c>EngineeringSimulation3D</c>.</summary>
public static class EngineeringSolvers
{
    // ------------------------------------------------------------- potential flow around a cylinder

    /// <summary>Velocity (u, w) of uniform flow U past a cylinder of radius R at (x, z).</summary>
    public static (double U, double W) CylinderFlow(double x, double z, double speed, double radius)
    {
        var r2 = x * x + z * z;
        if (r2 < radius * radius)
        {
            return (0, 0);
        }

        var a = radius * radius / (r2 * r2);
        return (speed * (1 - a * (x * x - z * z)), -speed * a * 2 * x * z);
    }

    /// <summary>Streamlines seeded upstream, integrated with midpoint RK2.</summary>
    public static List<List<(double X, double Z, double Speed)>> Streamlines(double speed, double radius, int count, double extent = 5)
    {
        var lines = new List<List<(double, double, double)>>();
        for (var i = 0; i < count; i++)
        {
            var z = (i / (count - 1.0) - 0.5) * extent * 1.2 + 0.013; // offset avoids the stagnation streamline
            double x = -extent;
            var line = new List<(double, double, double)>();
            for (var step = 0; step < 400 && x < extent; step++)
            {
                var (u, w) = CylinderFlow(x, z, speed, radius);
                var s = Math.Sqrt(u * u + w * w);
                line.Add((x, z, s));
                const double h = 0.06;
                var (u2, w2) = CylinderFlow(x + u / Math.Max(s, 1e-6) * h / 2, z + w / Math.Max(s, 1e-6) * h / 2, speed, radius);
                var s2 = Math.Max(1e-6, Math.Sqrt(u2 * u2 + w2 * w2));
                x += u2 / s2 * h;
                z += w2 / s2 * h;
            }

            lines.Add(line);
        }

        return lines;
    }

    // ------------------------------------------------------------- cantilever (Euler–Bernoulli)

    /// <summary>Tip-loaded cantilever: deflection (m) and bending stress (Pa) at x.</summary>
    public static (double Deflection, double Stress) Cantilever(double x, double length, double load, double youngs, double width, double height)
    {
        var inertia = width * Math.Pow(height, 3) / 12;
        var deflection = load * x * x * (3 * length - x) / (6 * youngs * inertia);
        var moment = load * (length - x);
        return (deflection, moment * (height / 2) / inertia);
    }

    // ------------------------------------------------------------- 2-D heat equation (explicit FTCS)

    /// <summary>Temperature field on an n×n plate with fixed 20 °C edges and a heat source, after <paramref name="steps"/> steps.</summary>
    public static double[] Heat(int n, int steps, double sourceTemp, (int X, int Z) source, double diffusivity = 0.22)
    {
        var t = Enumerable.Repeat(20.0, n * n).ToArray();
        var next = new double[n * n];
        var alpha = Math.Min(diffusivity, 0.24); // stability: α Δt / Δx² ≤ 0.25
        for (var s = 0; s < steps; s++)
        {
            for (var z = 0; z < n; z++)
            {
                for (var x = 0; x < n; x++)
                {
                    var i = z * n + x;
                    if (x == 0 || z == 0 || x == n - 1 || z == n - 1)
                    {
                        next[i] = 20;
                        continue;
                    }

                    next[i] = t[i] + alpha * (t[i - 1] + t[i + 1] + t[i - n] + t[i + n] - 4 * t[i]);
                }
            }

            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    next[(source.Z + dz) * n + source.X + dx] = sourceTemp;
                }
            }

            (t, next) = (next, t);
        }

        return t;
    }

    // ------------------------------------------------------------- Warren truss, method of joints

    public sealed record TrussResult(double[][] Nodes, (int A, int B)[] Members, double[] Forces, double[] Reactions);

    /// <summary>
    /// A Warren truss with <paramref name="panels"/> bottom panels, pinned at the left and on a roller at the right,
    /// with <paramref name="load"/> (N, downward) at every top node. Member forces are + for tension.
    /// </summary>
    public static TrussResult WarrenTruss(int panels, double panelWidth, double height, double load)
    {
        var nodes = new List<double[]>();
        for (var i = 0; i <= panels; i++) nodes.Add([i * panelWidth, 0]);
        for (var i = 0; i < panels; i++) nodes.Add([(i + 0.5) * panelWidth, height]);
        int B(int i) => i;
        int T(int i) => panels + 1 + i;
        var members = new List<(int, int)>();
        for (var i = 0; i < panels; i++) members.Add((B(i), B(i + 1)));
        for (var i = 0; i < panels - 1; i++) members.Add((T(i), T(i + 1)));
        for (var i = 0; i < panels; i++)
        {
            members.Add((B(i), T(i)));
            members.Add((T(i), B(i + 1)));
        }

        var nNodes = nodes.Count;
        var unknowns = members.Count + 3; // members + Rx0, Ry0, Ry(end)
        var rows = 2 * nNodes;
        var a = new double[rows, unknowns];
        var b = new double[rows];
        for (var m = 0; m < members.Count; m++)
        {
            var (i, j) = members[m];
            var dx = nodes[j][0] - nodes[i][0];
            var dy = nodes[j][1] - nodes[i][1];
            var len = Math.Sqrt(dx * dx + dy * dy);
            a[2 * i, m] += dx / len;
            a[2 * i + 1, m] += dy / len;
            a[2 * j, m] -= dx / len;
            a[2 * j + 1, m] -= dy / len;
        }

        a[0, members.Count] = 1;           // Rx at B0
        a[1, members.Count + 1] = 1;       // Ry at B0
        a[2 * B(panels) + 1, members.Count + 2] = 1; // Ry at last bottom node
        for (var i = 0; i < panels; i++)
        {
            b[2 * T(i) + 1] = load; // equilibrium: Σ = −external, external is −load
        }

        var x = Solve(a, b, rows, unknowns);
        return new TrussResult([.. nodes], [.. members], x[..members.Count], x[members.Count..]);
    }

    /// <summary>Least-squares-safe Gaussian elimination with partial pivoting (square systems here).</summary>
    private static double[] Solve(double[,] a, double[] b, int rows, int cols)
    {
        var m = new double[rows, cols + 1];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++) m[r, c] = a[r, c];
            m[r, cols] = b[r];
        }

        var row = 0;
        var pivots = new int[cols];
        Array.Fill(pivots, -1);
        for (var c = 0; c < cols && row < rows; c++)
        {
            var best = row;
            for (var r = row + 1; r < rows; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[best, c])) best = r;
            if (Math.Abs(m[best, c]) < 1e-12) continue;
            for (var k = 0; k <= cols; k++) (m[row, k], m[best, k]) = (m[best, k], m[row, k]);
            for (var r = 0; r < rows; r++)
            {
                if (r == row) continue;
                var f = m[r, c] / m[row, c];
                if (f == 0) continue;
                for (var k = c; k <= cols; k++) m[r, k] -= f * m[row, k];
            }

            pivots[c] = row;
            row++;
        }

        var x = new double[cols];
        for (var c = 0; c < cols; c++) x[c] = pivots[c] >= 0 ? m[pivots[c], cols] / m[pivots[c], c] : 0;
        return x;
    }
}
