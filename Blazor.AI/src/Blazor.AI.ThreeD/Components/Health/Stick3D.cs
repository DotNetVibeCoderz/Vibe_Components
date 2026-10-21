namespace Blazor.AI.ThreeD;

/// <summary>Builds cylinders that connect two points (bonds, struts, pipes).</summary>
public static class Stick3D
{
    /// <summary>A cylinder from <paramref name="a"/> to <paramref name="b"/>, optionally shifted sideways by <paramref name="offset"/>.</summary>
    public static Object3D Between(string id, double[] a, double[] b, double radius, string color, double[]? offset = null, string? tooltip = null)
    {
        var d = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
        var len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        var mid = new[] { (a[0] + b[0]) / 2 + (offset?[0] ?? 0), (a[1] + b[1]) / 2 + (offset?[1] ?? 0), (a[2] + b[2]) / 2 + (offset?[2] ?? 0) };
        return new Object3D
        {
            Id = id, Kind = "cylinder", Position = mid, Radius = radius, Height = Math.Max(len, 1e-4), Segments = 12, Color = color,
            Rotation = len < 1e-9 ? null : RotationFromY(d[0] / len, d[1] / len, d[2] / len), Tooltip = tooltip, Pickable = tooltip is not null,
        };
    }

    /// <summary>Euler angles (XYZ order, radians) that rotate the +Y axis onto the unit vector (x, y, z).</summary>
    public static double[] RotationFromY(double x, double y, double z)
    {
        // Quaternion from (0,1,0) to v: axis = up × v = (z, 0, -x), angle = acos(y).
        double qx, qy, qz, qw;
        if (y < -0.999999)
        {
            (qx, qy, qz, qw) = (1, 0, 0, 0);
        }
        else
        {
            qx = z;
            qy = 0;
            qz = -x;
            qw = 1 + y;
            var n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            qx /= n;
            qy /= n;
            qz /= n;
            qw /= n;
        }

        // Rotation matrix elements needed for Three.js Euler 'XYZ' decomposition.
        var m11 = 1 - 2 * (qy * qy + qz * qz);
        var m12 = 2 * (qx * qy - qz * qw);
        var m13 = 2 * (qx * qz + qy * qw);
        var m22 = 1 - 2 * (qx * qx + qz * qz);
        var m23 = 2 * (qy * qz - qx * qw);
        var m32 = 2 * (qy * qz + qx * qw);
        var m33 = 1 - 2 * (qx * qx + qy * qy);
        var ry = Math.Asin(Math.Clamp(m13, -1, 1));
        double rx, rz;
        if (Math.Abs(m13) < 0.9999999)
        {
            rx = Math.Atan2(-m23, m33);
            rz = Math.Atan2(-m12, m11);
        }
        else
        {
            rx = Math.Atan2(m32, m22);
            rz = 0;
        }

        return [rx, ry, rz];
    }

    /// <summary>A unit vector perpendicular to <paramref name="d"/> (for offsetting double bonds).</summary>
    public static double[] Perpendicular(double[] d)
    {
        var (x, y, z) = (d[0], d[1], d[2]);
        var p = Math.Abs(y) < 0.9 ? new[] { z, 0, -x } : [0, -z, y];
        var n = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
        return n < 1e-9 ? [1, 0, 0] : [p[0] / n, p[1] / n, p[2] / n];
    }
}
