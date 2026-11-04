using System.Numerics;
using OpenSceneGraph.Net.Interop;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Particles;

public enum EmitterShape { Point, Box, Sphere, Disc }

/// <summary>Particle source (<c>osgParticle::ModularEmitter</c> = counter + placer + shooter).</summary>
public sealed class Emitter
{
    /// <summary>Emission point in the particle system's local frame; move it for trails.</summary>
    public Vector3 Position { get; set; }
    public EmitterShape Shape { get; set; } = EmitterShape.Point;
    public Vector3 Extent { get; set; } = Vector3.One;

    /// <summary>Particles per second (continuous emission).</summary>
    public float Rate { get; set; } = 50;

    /// <summary>Main emission direction; particles spread inside <see cref="SpreadDegrees"/>.</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitZ;
    public float SpreadDegrees { get; set; } = 15;
    public float SpeedMin { get; set; } = 1;
    public float SpeedMax { get; set; } = 2;
    public float LifeMin { get; set; } = 1;
    public float LifeMax { get; set; } = 2;
    public bool Enabled { get; set; } = true;

    /// <summary>Inherited velocity added to every new particle (e.g. the moving vehicle's velocity).</summary>
    public Vector3 InheritVelocity { get; set; }
}

/// <summary>
/// CPU particle system (<c>osgParticle::ParticleSystem</c> + <c>ParticleSystemUpdater</c>). It is a
/// <see cref="Geometry"/> drawn as point sprites: <c>TexCoords.X</c> carries the particle size in world units and
/// renderers attenuate it with distance. Buffers are double-buffered so pipelined rendering never sees a half-updated frame.
/// </summary>
public sealed class ParticleSystem : Geometry, IUpdateCallback
{
    private readonly Vector3[] _pos;
    private readonly Vector3[] _vel;
    private readonly float[] _age;
    private readonly float[] _life;
    private readonly float[] _sizeJitter;
    private int _alive;
    private float _emitAccumulator;
    private readonly Random _rng;
    private readonly (Vector3[] P, Vector4[] C, Vector2[] S)[] _out = new (Vector3[], Vector4[], Vector2[])[2];
    private int _buffer;

    public ParticleSystem(int capacity = 2000, int seed = 42)
    {
        Capacity = capacity;
        _pos = new Vector3[capacity];
        _vel = new Vector3[capacity];
        _age = new float[capacity];
        _life = new float[capacity];
        _sizeJitter = new float[capacity];
        _rng = new Random(seed);
        for (int i = 0; i < 2; i++) _out[i] = (new Vector3[capacity], new Vector4[capacity], new Vector2[capacity]);
        PrimitiveType = PrimitiveType.Points;
        Usage = BufferUsage.Stream;
        Positions = [];
        var ss = GetOrCreateStateSet();
        ss.Lighting = false;
        ss.MakeTransparent();
        ss.CastShadows = false;
        ss.ReceiveShadows = false;
        CullingActive = false; // bounds change every frame
        AddUpdateCallback(this);
    }

    public int Capacity { get; }
    public int AliveCount => _alive;
    public Emitter Emitter { get; } = new();

    public Vector3 Gravity { get; set; } = new(0, 0, -9.81f);
    public Vector3 Wind { get; set; }
    public float Drag { get; set; } = 0.1f;

    public float SizeStart { get; set; } = 0.2f;
    public float SizeEnd { get; set; } = 0.6f;
    public Vector4 ColorStart { get; set; } = new(1, 1, 1, 1);
    public Vector4 ColorMid { get; set; } = new(1, 1, 1, 0.8f);
    public Vector4 ColorEnd { get; set; } = new(1, 1, 1, 0);
    public float SizeVariation { get; set; } = 0.3f;

    /// <summary>When false the system stops updating (but keeps drawing its last state).</summary>
    public bool Running { get; set; } = true;

    public void SetBlendAdditive(bool additive) => GetOrCreateStateSet().Blend = additive ? BlendMode.Additive : BlendMode.AlphaBlend;

    /// <summary>Emits <paramref name="count"/> particles immediately (explosions, muzzle flashes).</summary>
    public void Burst(int count)
    {
        for (int i = 0; i < count; i++) Spawn();
    }

    public void Clear() => _alive = 0;

    private float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    private void Spawn()
    {
        if (_alive >= Capacity) return;
        int i = _alive++;
        var e = Emitter;
        var p = e.Position;
        switch (e.Shape)
        {
            case EmitterShape.Box: p += new Vector3(Rand(-0.5f, 0.5f) * e.Extent.X, Rand(-0.5f, 0.5f) * e.Extent.Y, Rand(-0.5f, 0.5f) * e.Extent.Z); break;
            case EmitterShape.Sphere:
                Vector3 d;
                do d = new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1)); while (d.LengthSquared() > 1);
                p += d * e.Extent.X;
                break;
            case EmitterShape.Disc:
                float a = Rand(0, MathF.Tau), r = MathF.Sqrt(Rand(0, 1)) * e.Extent.X;
                p += new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0);
                break;
        }

        // random direction inside a cone around Emitter.Direction
        var dir = e.Direction.LengthSquared() > 0 ? Vector3.Normalize(e.Direction) : Vector3.UnitZ;
        float spread = e.SpreadDegrees * MathF.PI / 180;
        float cosT = MathF.Cos(spread);
        float z = Rand(cosT, 1), phi = Rand(0, MathF.Tau), sinT = MathF.Sqrt(1 - z * z);
        var local = new Vector3(sinT * MathF.Cos(phi), sinT * MathF.Sin(phi), z);
        var rot = FromTo(Vector3.UnitZ, dir);
        var v = Vector3.Transform(local, rot) * Rand(e.SpeedMin, e.SpeedMax) + e.InheritVelocity;

        _pos[i] = p;
        _vel[i] = v;
        _age[i] = 0;
        _life[i] = Rand(e.LifeMin, e.LifeMax);
        _sizeJitter[i] = 1 + Rand(-SizeVariation, SizeVariation);
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(a, b));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(d));
    }

    public void Update(Node node, in FrameStamp frame)
    {
        if (!Running) return;
        float dt = (float)System.Math.Min(frame.DeltaTime, 0.1); // clamp hitches
        if (dt <= 0) { Publish(); return; }

        if (Emitter.Enabled && Emitter.Rate > 0)
        {
            _emitAccumulator += Emitter.Rate * dt;
            int n = (int)_emitAccumulator;
            _emitAccumulator -= n;
            for (int i = 0; i < n; i++) Spawn();
        }

        int count = _alive;
        var gravity = Gravity + Wind * Drag;
        if (!FastPath.TryParticlesStep(_pos.AsSpan(0, count), _vel.AsSpan(0, count), _age.AsSpan(0, count), gravity, Drag, dt))
        {
            float damp = MathF.Max(0, 1 - Drag * dt);
            for (int i = 0; i < count; i++)
            {
                _vel[i] = (_vel[i] + gravity * dt) * damp;
                _pos[i] += _vel[i] * dt;
                _age[i] += dt;
            }
        }

        // remove dead particles (swap with last)
        for (int i = 0; i < _alive;)
        {
            if (_age[i] < _life[i]) { i++; continue; }
            int last = --_alive;
            _pos[i] = _pos[last]; _vel[i] = _vel[last]; _age[i] = _age[last]; _life[i] = _life[last]; _sizeJitter[i] = _sizeJitter[last];
        }
        Publish();
    }

    private void Publish()
    {
        _buffer ^= 1;
        var (p, c, s) = _out[_buffer];
        for (int i = 0; i < _alive; i++)
        {
            float t = _age[i] / _life[i];
            p[i] = _pos[i];
            c[i] = t < 0.5f ? Vector4.Lerp(ColorStart, ColorMid, t * 2) : Vector4.Lerp(ColorMid, ColorEnd, (t - 0.5f) * 2);
            s[i] = new Vector2((SizeStart + (SizeEnd - SizeStart) * t) * _sizeJitter[i], t);
        }
        // expose the live range only (renderers draw DrawCount elements)
        Colors = c;
        TexCoords = s;
        DrawFirst = 0;
        DrawCount = _alive;
        Positions = p; // setter marks the geometry dirty
    }
}

/// <summary>Ready-made effects (<c>osgParticle::SmokeEffect</c>, <c>FireEffect</c>, <c>ExplosionEffect</c>, precipitation).</summary>
public static class ParticleEffects
{
    public static ParticleSystem Smoke(Vector3 position, float scale = 1f)
    {
        var ps = new ParticleSystem(1200) { Name = "Smoke", Gravity = new Vector3(0, 0, 0.6f * scale), Drag = 0.4f, SizeStart = 0.4f * scale, SizeEnd = 3.5f * scale };
        ps.ColorStart = new Vector4(0.35f, 0.35f, 0.37f, 0.55f);
        ps.ColorMid = new Vector4(0.45f, 0.45f, 0.47f, 0.35f);
        ps.ColorEnd = new Vector4(0.6f, 0.6f, 0.62f, 0f);
        var e = ps.Emitter;
        e.Position = position; e.Rate = 40; e.SpreadDegrees = 20; e.SpeedMin = 0.5f * scale; e.SpeedMax = 1.5f * scale;
        e.LifeMin = 3; e.LifeMax = 6; e.Shape = EmitterShape.Disc; e.Extent = new Vector3(0.3f * scale);
        return ps;
    }

    public static ParticleSystem Fire(Vector3 position, float scale = 1f)
    {
        var ps = new ParticleSystem(1500) { Name = "Fire", Gravity = new Vector3(0, 0, 2.5f * scale), Drag = 0.8f, SizeStart = 0.9f * scale, SizeEnd = 0.15f * scale };
        ps.ColorStart = new Vector4(1f, 0.85f, 0.3f, 0.9f);
        ps.ColorMid = new Vector4(1f, 0.35f, 0.05f, 0.7f);
        ps.ColorEnd = new Vector4(0.3f, 0.05f, 0.02f, 0f);
        ps.SetBlendAdditive(true);
        var e = ps.Emitter;
        e.Position = position; e.Rate = 180; e.SpreadDegrees = 12; e.SpeedMin = 0.5f * scale; e.SpeedMax = 1.6f * scale;
        e.LifeMin = 0.5f; e.LifeMax = 1.2f; e.Shape = EmitterShape.Disc; e.Extent = new Vector3(0.45f * scale);
        return ps;
    }

    /// <summary>One-shot explosion: call <see cref="ParticleSystem.Burst"/> or use <paramref name="autoBurst"/>.</summary>
    public static ParticleSystem Explosion(Vector3 position, float scale = 1f, bool autoBurst = true)
    {
        var ps = new ParticleSystem(1500) { Name = "Explosion", Gravity = new Vector3(0, 0, -3f * scale), Drag = 1.6f, SizeStart = 1.4f * scale, SizeEnd = 2.8f * scale };
        ps.ColorStart = new Vector4(1f, 0.95f, 0.6f, 1f);
        ps.ColorMid = new Vector4(1f, 0.4f, 0.08f, 0.8f);
        ps.ColorEnd = new Vector4(0.15f, 0.12f, 0.1f, 0f);
        ps.SetBlendAdditive(true);
        var e = ps.Emitter;
        e.Position = position; e.Rate = 0; e.SpreadDegrees = 180; e.SpeedMin = 4 * scale; e.SpeedMax = 14 * scale;
        e.LifeMin = 0.6f; e.LifeMax = 1.6f; e.Shape = EmitterShape.Sphere; e.Extent = new Vector3(0.5f * scale);
        if (autoBurst) ps.Burst(900);
        return ps;
    }

    public static ParticleSystem Rain(Vector3 center, float area = 60, float height = 30, float intensity = 1f)
    {
        var ps = new ParticleSystem((int)(6000 * intensity)) { Name = "Rain", Gravity = new Vector3(0, 0, -9.81f), Drag = 0, SizeStart = 0.06f, SizeEnd = 0.06f, SizeVariation = 0.2f };
        ps.ColorStart = ps.ColorMid = new Vector4(0.7f, 0.78f, 0.9f, 0.55f);
        ps.ColorEnd = new Vector4(0.7f, 0.78f, 0.9f, 0.4f);
        var e = ps.Emitter;
        e.Position = center + new Vector3(0, 0, height); e.Shape = EmitterShape.Box; e.Extent = new Vector3(area, area, 1);
        e.Rate = 2500 * intensity; e.Direction = -Vector3.UnitZ; e.SpreadDegrees = 3; e.SpeedMin = 14; e.SpeedMax = 18;
        e.LifeMin = e.LifeMax = height / 18f + 0.4f;
        return ps;
    }

    public static ParticleSystem Snow(Vector3 center, float area = 60, float height = 25, float intensity = 1f)
    {
        var ps = new ParticleSystem((int)(5000 * intensity)) { Name = "Snow", Gravity = new Vector3(0, 0, -0.6f), Drag = 0.6f, SizeStart = 0.12f, SizeEnd = 0.12f, Wind = new Vector3(0.8f, 0.3f, 0) };
        ps.ColorStart = ps.ColorMid = new Vector4(1, 1, 1, 0.9f);
        ps.ColorEnd = new Vector4(1, 1, 1, 0.7f);
        var e = ps.Emitter;
        e.Position = center + new Vector3(0, 0, height); e.Shape = EmitterShape.Box; e.Extent = new Vector3(area, area, 1);
        e.Rate = 600 * intensity; e.Direction = -Vector3.UnitZ; e.SpreadDegrees = 25; e.SpeedMin = 0.5f; e.SpeedMax = 1.2f;
        e.LifeMin = 18; e.LifeMax = 25;
        return ps;
    }

    public static ParticleSystem Dust(Vector3 position, float scale = 1f)
    {
        var ps = new ParticleSystem(800) { Name = "Dust", Gravity = new Vector3(0, 0, 0.15f), Drag = 1.2f, SizeStart = 0.6f * scale, SizeEnd = 2.2f * scale };
        ps.ColorStart = new Vector4(0.62f, 0.52f, 0.38f, 0.45f);
        ps.ColorMid = new Vector4(0.66f, 0.57f, 0.43f, 0.25f);
        ps.ColorEnd = new Vector4(0.7f, 0.62f, 0.5f, 0f);
        var e = ps.Emitter;
        e.Position = position; e.Rate = 30; e.SpreadDegrees = 70; e.SpeedMin = 0.5f * scale; e.SpeedMax = 2f * scale;
        e.LifeMin = 1.5f; e.LifeMax = 3f; e.Shape = EmitterShape.Disc; e.Extent = new Vector3(1.2f * scale);
        return ps;
    }

    public static ParticleSystem Sparks(Vector3 position, float scale = 1f)
    {
        var ps = new ParticleSystem(600) { Name = "Sparks", Gravity = new Vector3(0, 0, -9.81f), Drag = 0.2f, SizeStart = 0.08f * scale, SizeEnd = 0.02f * scale };
        ps.ColorStart = new Vector4(1, 0.95f, 0.7f, 1);
        ps.ColorMid = new Vector4(1, 0.6f, 0.2f, 0.9f);
        ps.ColorEnd = new Vector4(1, 0.3f, 0.1f, 0);
        ps.SetBlendAdditive(true);
        var e = ps.Emitter;
        e.Position = position; e.Rate = 120; e.SpreadDegrees = 35; e.SpeedMin = 3 * scale; e.SpeedMax = 7 * scale; e.LifeMin = 0.4f; e.LifeMax = 1.0f;
        return ps;
    }
}
