using System.Numerics;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Simulation;

/// <summary>Control inputs shared by all vehicle models (normalised -1..1 / 0..1).</summary>
public struct VehicleControls
{
    public float Throttle;   // 0..1
    public float Pitch;      // elevator, -1..1 (positive = nose up)
    public float Roll;       // aileron, -1..1 (positive = right wing down)
    public float Yaw;        // rudder / steering, -1..1 (positive = right)
    public float Brake;      // 0..1
}

/// <summary>Common state of a simulated vehicle, in local ENU metres (X east, Y north, Z up).</summary>
public abstract class Vehicle
{
    public string Name { get; set; } = "vehicle";
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; protected set; }

    /// <summary>Heading in degrees clockwise from north.</summary>
    public float Heading { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public VehicleControls Controls;

    public float Speed => Velocity.Length();
    public Quaternion Attitude => MathUtil.FromHeadingPitchRoll(Heading, Pitch, Roll);
    public Vector3 Forward => Vector3.Transform(Vector3.UnitY, Attitude);

    /// <summary>Ground elevation provider (terrain following / crash detection).</summary>
    public Func<float, float, float?>? GroundHeight { get; set; }

    public abstract void Step(float dt);

    /// <summary>Writes the vehicle pose into a transform node.</summary>
    public void ApplyTo(PositionAttitudeTransform pat)
    {
        pat.Position = Position;
        pat.Attitude = Attitude;
    }

    /// <summary>Builds an Entity State PDU for this vehicle at the given geodetic origin of the local frame.</summary>
    public EntityStatePdu ToPdu(EntityId id, EntityType type, GeoPoint origin, string marking)
    {
        var e = Ellipsoid.Wgs84;
        var geo = e.FromEnu(Position, origin);
        var ecef = e.ToEcef(geo);
        var (east, north, up) = e.EnuAxes(geo);
        var vel = east * Velocity.X + north * Velocity.Y + up * Velocity.Z;
        return new EntityStatePdu
        {
            Id = id,
            Type = type,
            Location = ecef,
            Velocity = vel,
            Orientation = DisOrientation.FromLocal(geo, Heading, Pitch, Roll),
            Marking = marking,
        };
    }
}

/// <summary>
/// Simplified 6-DOF-ish fixed-wing aircraft: thrust, drag, lift balancing gravity at cruise, bank-to-turn.
/// Good enough for training-style flight and UAV simulators; not an aerodynamic model.
/// </summary>
public sealed class Aircraft : Vehicle
{
    public float MaxThrust { get; set; } = 18f;      // m/s² at full throttle
    public float StallSpeed { get; set; } = 35f;
    public float MaxSpeed { get; set; } = 260f;
    public float PitchRate { get; set; } = 35f;      // deg/s
    public float RollRate { get; set; } = 90f;       // deg/s
    public bool OnGround { get; private set; }
    public bool Crashed { get; private set; }

    private float _speed = 60;

    public Aircraft() => Velocity = new Vector3(0, _speed, 0);

    public override void Step(float dt)
    {
        if (Crashed || dt <= 0) return;
        var c = Controls;

        // attitude rates
        Roll = System.Math.Clamp(Roll + c.Roll * RollRate * dt, -80, 80);
        if (MathF.Abs(c.Roll) < 0.05f) Roll *= MathF.Pow(0.4f, dt); // auto-level
        Pitch = System.Math.Clamp(Pitch + c.Pitch * PitchRate * dt, -45, 45);
        // coordinated turn: yaw rate = g * tan(bank) / v
        float turnRate = MathUtil.Degrees(9.81f * MathF.Tan(MathUtil.Radians(Roll)) / MathF.Max(_speed, 20));
        Heading = (Heading + (turnRate + c.Yaw * 10) * dt + 360) % 360;

        // speed: thrust - drag - gravity component along the flight path
        float drag = 0.00025f * _speed * _speed;
        float gravityAlong = 9.81f * MathF.Sin(MathUtil.Radians(Pitch));
        _speed = System.Math.Clamp(_speed + (c.Throttle * MaxThrust - drag - gravityAlong - c.Brake * 8) * dt, 0, MaxSpeed);

        // below stall the nose drops
        if (_speed < StallSpeed && !OnGround) Pitch -= (StallSpeed - _speed) * 0.8f * dt;

        Velocity = Forward * _speed;
        Position += Velocity * dt;

        if (GroundHeight?.Invoke(Position.X, Position.Y) is { } ground)
        {
            if (Position.Z <= ground + 1.5f)
            {
                // touchdown is fine if slow and level, otherwise a crash
                if (_speed > StallSpeed * 1.6f || MathF.Abs(Roll) > 15 || Pitch < -8) Crashed = true;
                Position = Position with { Z = ground + 1.5f };
                Pitch = MathF.Max(Pitch, 0);
                OnGround = true;
            }
            else OnGround = false;
        }
    }
}

/// <summary>Multirotor UAV: velocity control with smooth acceleration and altitude hold.</summary>
public sealed class Quadcopter : Vehicle
{
    public float MaxHorizontalSpeed { get; set; } = 18f;
    public float MaxClimbRate { get; set; } = 6f;
    public float TargetAltitude { get; set; } = 40f;
    public float YawRate { get; set; } = 90f;

    public override void Step(float dt)
    {
        var c = Controls;
        Heading = (Heading + c.Yaw * YawRate * dt + 360) % 360;
        float h = MathUtil.Radians(Heading);
        var fwd = new Vector3(MathF.Sin(h), MathF.Cos(h), 0);
        var right = new Vector3(fwd.Y, -fwd.X, 0);
        var desired = (fwd * c.Pitch + right * c.Roll) * MaxHorizontalSpeed;
        TargetAltitude += (c.Throttle - 0.5f) * 2 * MaxClimbRate * dt;
        float ground = GroundHeight?.Invoke(Position.X, Position.Y) ?? 0;
        TargetAltitude = MathF.Max(TargetAltitude, ground + 2);
        desired.Z = System.Math.Clamp((TargetAltitude - Position.Z) * 1.2f, -MaxClimbRate, MaxClimbRate);

        var v = Velocity + (desired - Velocity) * MathF.Min(1, 2.5f * dt);
        Velocity = v;
        Position += v * dt;
        // visual tilt follows acceleration
        Pitch = -c.Pitch * 18;
        Roll = c.Roll * 18;
    }
}

/// <summary>Tracked/wheeled ground vehicle with terrain following and slope-aligned attitude.</summary>
public sealed class GroundVehicle : Vehicle
{
    public float MaxSpeed { get; set; } = 18f;
    public float Acceleration { get; set; } = 4f;
    public float TurnRate { get; set; } = 40f;
    public float TurretHeading { get; set; }
    private float _speed;

    public override void Step(float dt)
    {
        var c = Controls;
        _speed += (c.Throttle * Acceleration - c.Brake * Acceleration * 1.5f) * dt;
        _speed -= _speed * 0.15f * dt;
        _speed = System.Math.Clamp(_speed, -MaxSpeed * 0.3f, MaxSpeed);
        Heading = (Heading + c.Yaw * TurnRate * dt * MathF.Min(1, MathF.Abs(_speed) / 2 + 0.4f) + 360) % 360;

        float h = MathUtil.Radians(Heading);
        var fwd = new Vector3(MathF.Sin(h), MathF.Cos(h), 0);
        var p = Position + fwd * _speed * dt;
        if (GroundHeight is { } gh && gh(p.X, p.Y) is { } z)
        {
            p.Z = z;
            // align pitch/roll with the terrain slope
            var right = new Vector3(fwd.Y, -fwd.X, 0);
            float zf = gh(p.X + fwd.X * 2, p.Y + fwd.Y * 2) ?? z, zb = gh(p.X - fwd.X * 2, p.Y - fwd.Y * 2) ?? z;
            float zr = gh(p.X + right.X * 1.5f, p.Y + right.Y * 1.5f) ?? z, zl = gh(p.X - right.X * 1.5f, p.Y - right.Y * 1.5f) ?? z;
            Pitch = MathUtil.Degrees(MathF.Atan2(zf - zb, 4));
            Roll = MathUtil.Degrees(MathF.Atan2(zl - zr, 3));
        }
        Velocity = (p - Position) / dt;
        Position = p;
    }
}

/// <summary>Displacement ship with inertia and a simple wave motion (heave/pitch/roll).</summary>
public sealed class Ship : Vehicle
{
    private float _speed;
    private float _time;
    public float MaxSpeed { get; set; } = 15f;
    public float SeaState { get; set; } = 3;

    public override void Step(float dt)
    {
        _time += dt;
        var c = Controls;
        _speed += (c.Throttle * MaxSpeed - _speed) * 0.08f * dt - c.Brake * 0.5f * dt;
        _speed = System.Math.Clamp(_speed, -MaxSpeed * 0.2f, MaxSpeed);
        Heading = (Heading + c.Yaw * 6 * dt * MathF.Min(1, MathF.Abs(_speed) / 3) + 360) % 360;
        float h = MathUtil.Radians(Heading);
        var fwd = new Vector3(MathF.Sin(h), MathF.Cos(h), 0);
        Velocity = fwd * _speed;
        float amp = SeaState * 0.12f;
        Position = new Vector3(Position.X + Velocity.X * dt, Position.Y + Velocity.Y * dt, MathF.Sin(_time * 0.7f) * amp);
        Pitch = MathF.Sin(_time * 0.53f) * amp * 2.2f;
        Roll = MathF.Sin(_time * 0.41f + 1) * amp * 4f;
    }
}

/// <summary>DIS orientation helpers (psi/theta/phi relative to ECEF).</summary>
public static class DisOrientation
{
    /// <summary>Converts local heading/pitch/roll (degrees) at a geodetic location to DIS Euler angles (radians).</summary>
    public static (float Psi, float Theta, float Phi) FromLocal(GeoPoint at, float headingDeg, float pitchDeg, float rollDeg)
    {
        var e = Ellipsoid.Wgs84;
        var (east, north, up) = e.EnuAxes(at);
        double h = headingDeg * System.Math.PI / 180, p = pitchDeg * System.Math.PI / 180, r = rollDeg * System.Math.PI / 180;
        // body axes in ENU: x forward, y right, z down (aerospace convention used by DIS)
        var fwdEnu = new Vector3d(System.Math.Sin(h) * System.Math.Cos(p), System.Math.Cos(h) * System.Math.Cos(p), System.Math.Sin(p));
        var rightLevel = new Vector3d(System.Math.Cos(h), -System.Math.Sin(h), 0);
        var upLevel = Vector3d.Cross(rightLevel, fwdEnu);
        var rightEnu = rightLevel * System.Math.Cos(r) + upLevel * -System.Math.Sin(r);
        var downEnu = Vector3d.Cross(fwdEnu, rightEnu);
        Vector3d ToEcef(Vector3d v) => east * v.X + north * v.Y + up * v.Z;
        var x = ToEcef(fwdEnu);
        var y = ToEcef(rightEnu);
        var z = ToEcef(downEnu);
        double psi = System.Math.Atan2(x.Y, x.X);
        double theta = System.Math.Asin(System.Math.Clamp(-x.Z, -1, 1));
        double phi = System.Math.Atan2(y.Z, z.Z);
        return ((float)psi, (float)theta, (float)phi);
    }
}

/// <summary>Two-body Keplerian orbit (for satellite / space visualization).</summary>
public sealed class KeplerOrbit
{
    public const double EarthMu = 3.986004418e14;

    public double SemiMajorAxis { get; init; } = 6_778_000;   // metres (≈400 km LEO)
    public double Eccentricity { get; init; }
    public double InclinationDeg { get; init; } = 51.6;
    public double RaanDeg { get; init; }
    public double ArgPerigeeDeg { get; init; }
    public double MeanAnomalyAtEpochDeg { get; init; }
    public double Mu { get; init; } = EarthMu;

    public double PeriodSeconds => 2 * System.Math.PI * System.Math.Sqrt(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis / Mu);

    /// <summary>Inertial position (metres) at <paramref name="t"/> seconds after epoch.</summary>
    public Vector3d PositionAt(double t)
    {
        const double d = System.Math.PI / 180;
        double n = System.Math.Sqrt(Mu / (SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
        double m = MeanAnomalyAtEpochDeg * d + n * t;
        double e = Eccentricity;
        double E = m;
        for (int i = 0; i < 12; i++) E -= (E - e * System.Math.Sin(E) - m) / (1 - e * System.Math.Cos(E)); // Newton
        double nu = 2 * System.Math.Atan2(System.Math.Sqrt(1 + e) * System.Math.Sin(E / 2), System.Math.Sqrt(1 - e) * System.Math.Cos(E / 2));
        double r = SemiMajorAxis * (1 - e * System.Math.Cos(E));
        double xp = r * System.Math.Cos(nu), yp = r * System.Math.Sin(nu);
        double O = RaanDeg * d, w = ArgPerigeeDeg * d, inc = InclinationDeg * d;
        double cO = System.Math.Cos(O), sO = System.Math.Sin(O), cw = System.Math.Cos(w), sw = System.Math.Sin(w), ci = System.Math.Cos(inc), si = System.Math.Sin(inc);
        return new Vector3d(
            (cO * cw - sO * sw * ci) * xp + (-cO * sw - sO * cw * ci) * yp,
            (sO * cw + cO * sw * ci) * xp + (-sO * sw + cO * cw * ci) * yp,
            sw * si * xp + cw * si * yp);
    }

    /// <summary>Samples one full orbit as a polyline, scaled for rendering.</summary>
    public Vector3[] SampleTrack(int samples = 256, float scale = 1e-6f)
    {
        var pts = new Vector3[samples + 1];
        double period = PeriodSeconds;
        for (int i = 0; i <= samples; i++) pts[i] = PositionAt(period * i / samples).ToVector3() * scale;
        return pts;
    }
}
