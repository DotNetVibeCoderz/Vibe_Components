using System.Numerics;
using System.Runtime.InteropServices;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Blazor;

/// <summary>
/// Serialises a <see cref="RenderList"/> into the compact binary frame packet consumed by the WebGL2 renderer
/// (<c>wwwroot/osgCanvas.js</c>). The packet is a sequence of little-endian 32-bit words (ints and floats share one
/// buffer) so the JavaScript side can read it through <c>Int32Array</c>/<c>Float32Array</c> views without parsing.
/// <para>
/// Geometry and texture data are sent only when a resource is first drawn or its <c>Version</c> changes; resources
/// that are no longer drawn are evicted on both sides. Each frame otherwise carries just the environment, the lights,
/// a de-duplicated state table and the draw list (geometry handle, state index, primitive range and world matrix).
/// </para>
/// </summary>
/// <remarks>
/// Layout (word offsets). Header (<see cref="HeaderWords"/> words): magic, total words, geometry uploads,
/// texture uploads, geometry evictions, texture evictions, states, passes, lights, reserved.
/// Then: geometry uploads, texture uploads, evicted geometry handles, evicted texture handles,
/// environment (<see cref="EnvWords"/>), lights (12 each), states (<see cref="StateWords"/> each),
/// passes (<see cref="PassWords"/> header + <see cref="ItemWords"/> per item, opaque then transparent).
/// </remarks>
public sealed class WebGlFrameEncoder
{
    public const int Magic = 0x3147534F; // "OSG1"
    public const int HeaderWords = 10;
    public const int EnvWords = 48;
    public const int LightWords = 12;
    public const int StateWords = 20;
    public const int PassWords = 48;
    public const int ItemWords = 21;

    private sealed class GeoEntry
    {
        public int Handle;
        public int Version = int.MinValue;
        public long LastUsed;
    }

    private sealed class TexEntry
    {
        public int Handle;
        public long ImageId;
        public int ImageVersion = int.MinValue;
        public bool Uploaded;
        public long LastUsed;
    }

    private readonly Dictionary<long, GeoEntry> _geos = [];
    private readonly Dictionary<long, TexEntry> _texs = [];
    private readonly List<(Geometry Geometry, GeoEntry Entry)> _geoUploads = [];
    private readonly List<(Texture2D Texture, TexEntry Entry)> _texUploads = [];
    private readonly List<int> _geoEvict = [];
    private readonly List<int> _texEvict = [];
    private readonly Dictionary<ResolvedState, int> _stateIndex = new(ReferenceEqualityComparer.Instance);
    private readonly List<ResolvedState> _states = [];
    private readonly List<long> _scratch = [];
    private readonly List<int> _itemRefs = []; // (geometry handle, state index) per draw item, in write order
    private int _itemCursor;
    private int[] _w = new int[16 * 1024];
    private int _n;
    private long _frame;
    private int _nextHandle = 1;
    private bool _evictNow;

    /// <summary>Frames a resource may stay undrawn before its GPU copy is released.</summary>
    public int EvictAfterFrames { get; set; } = 600;

    /// <summary>Upper bound for the shadow map size (the JS side reports the GPU's MAX_TEXTURE_SIZE).</summary>
    public int MaxShadowMapSize { get; set; } = 4096;

    /// <summary>Number of geometries currently resident on the GPU.</summary>
    public int ResidentGeometries => _geos.Count;

    /// <summary>Number of textures currently resident on the GPU.</summary>
    public int ResidentTextures => _texs.Count;

    /// <summary>Bytes of geometry/texture data uploaded by the last packet.</summary>
    public int LastUploadBytes { get; private set; }

    /// <summary>Forgets every resource (call when the WebGL context was lost and recreated).</summary>
    public void Reset()
    {
        _geos.Clear();
        _texs.Clear();
    }

    /// <summary>On the next frame, evict every resource not drawn in that frame (e.g. after switching scenes).</summary>
    public void EvictUnusedOnNextFrame() => _evictNow = true;

    /// <summary>Encodes one frame. <paramref name="width"/>/<paramref name="height"/> are the drawing-buffer size in pixels.</summary>
    public byte[] Encode(RenderList list, int width, int height, double simulationTime)
    {
        _frame++;
        _n = 0;
        _geoUploads.Clear();
        _texUploads.Clear();
        _geoEvict.Clear();
        _texEvict.Clear();
        _stateIndex.Clear();
        _states.Clear();
        _itemRefs.Clear();
        _itemCursor = 0;
        LastUploadBytes = 0;

        // ---- pass 1: resolve resources and states
        foreach (var pass in list.Passes)
        {
            if (pass.Target is { } target) TouchTexture(target);
            foreach (ref readonly var item in CollectionsMarshal.AsSpan(pass.Opaque)) Touch(item);
            foreach (ref readonly var item in CollectionsMarshal.AsSpan(pass.Transparent)) Touch(item);
        }

        if (_evictNow || _frame % 60 == 0)
        {
            long limit = _evictNow ? 1 : EvictAfterFrames;
            CollectEvictions(_geos, e => e.LastUsed, e => e.Handle, limit, _geoEvict);
            CollectEvictions(_texs, e => e.LastUsed, e => e.Handle, limit, _texEvict);
            _evictNow = false;
        }

        // ---- pass 2: write
        var env = list.Environment;
        var lights = list.Lights;
        int lightCount = System.Math.Min(lights.Count, ShaderLibrary.MaxLights);
        Reserve(HeaderWords);
        _n = HeaderWords;

        foreach (var (g, e) in _geoUploads) WriteGeometry(g, e);
        foreach (var (t, e) in _texUploads) WriteTexture(t, e);
        foreach (int h in _geoEvict) Int(h);
        foreach (int h in _texEvict) Int(h);
        WriteEnvironment(list, env, width, height, simulationTime);

        // The shader shadows light 0 only, so the shadow-casting sun goes first (the headlight is usually lights[0]).
        int caster = ShadowCaster(list);
        Reserve(lightCount * LightWords);
        for (int i = 0; i < lightCount; i++)
        {
            int src = caster <= 0 ? i : i == 0 ? caster : i <= caster ? i - 1 : i;
            var l = lights[src];
            var light = l.Light;
            if (light.Type == LightType.Directional) Vec4(new Vector4(l.WorldDirection, 0));
            else Vec4(new Vector4(l.WorldPosition, light.Type == LightType.Spot ? 2 : 1));
            Vec4(new Vector4(light.Color * light.Intensity, light.Range));
            Vec4(new Vector4(l.WorldDirection, MathF.Cos(light.SpotCutoffDegrees * MathF.PI / 180)));
        }

        Reserve(_states.Count * StateWords);
        foreach (var s in _states) WriteState(s);

        foreach (var pass in list.Passes) WritePass(pass);

        _w[0] = Magic;
        _w[1] = _n;
        _w[2] = _geoUploads.Count;
        _w[3] = _texUploads.Count;
        _w[4] = _geoEvict.Count;
        _w[5] = _texEvict.Count;
        _w[6] = _states.Count;
        _w[7] = list.Passes.Count;
        _w[8] = lightCount;
        _w[9] = 0;
        return MemoryMarshal.AsBytes(_w.AsSpan(0, _n)).ToArray();
    }

    // ------------------------------------------------------------------ resource tracking
    private void Touch(in DrawItem item)
    {
        var g = item.Geometry;
        if (!_geos.TryGetValue(g.Id, out var e))
        {
            e = new GeoEntry { Handle = _nextHandle++ };
            _geos[g.Id] = e;
        }
        if (e.LastUsed != _frame)
        {
            e.LastUsed = _frame;
            if (e.Version != g.Version)
            {
                e.Version = g.Version;
                _geoUploads.Add((g, e));
            }
        }
        _itemRefs.Add(e.Handle);
        _itemRefs.Add(StateIndex(item.State));
    }

    private int StateIndex(ResolvedState s)
    {
        if (_stateIndex.TryGetValue(s, out int i)) return i;
        i = _states.Count;
        _states.Add(s);
        _stateIndex[s] = i;
        if (s.Texture is { } t) TouchTexture(t);
        return i;
    }

    private int TouchTexture(Texture2D t)
    {
        if (!t.IsRenderTarget && t.Image is null) return -1;
        if (!_texs.TryGetValue(t.Id, out var e))
        {
            e = new TexEntry { Handle = _nextHandle++ };
            _texs[t.Id] = e;
        }
        if (e.LastUsed != _frame)
        {
            e.LastUsed = _frame;
            bool dirty = t.IsRenderTarget ? !e.Uploaded : t.Image!.Id != e.ImageId || t.Image.Version != e.ImageVersion;
            if (dirty)
            {
                e.Uploaded = true;
                if (t.Image is { } img) { e.ImageId = img.Id; e.ImageVersion = img.Version; }
                _texUploads.Add((t, e));
            }
        }
        return e.Handle;
    }

    private int TextureHandle(Texture2D? t)
    {
        if (t is null) return -1;
        return _texs.TryGetValue(t.Id, out var e) ? e.Handle : -1;
    }

    private void CollectEvictions<T>(Dictionary<long, T> map, Func<T, long> lastUsed, Func<T, int> handle, long limit, List<int> output)
    {
        _scratch.Clear();
        foreach (var (id, e) in map)
            if (_frame - lastUsed(e) >= limit) _scratch.Add(id);
        foreach (long id in _scratch)
        {
            output.Add(handle(map[id]));
            map.Remove(id);
        }
    }

    // ------------------------------------------------------------------ writers
    private void WriteGeometry(Geometry g, GeoEntry e)
    {
        var pos = g.Positions;
        int n = pos.Length;
        var nor = g.Normals is { } nn && nn.Length == n ? nn : null;
        var col = g.Colors is { } cc && cc.Length == n ? cc : null;
        var uv = g.TexCoords is { } tt && tt.Length == n ? tt : null;
        var idx = g.Indices is { Length: > 0 } ii ? ii : null;
        int flags = (nor is null ? 0 : 1) | (col is null ? 0 : 2) | (uv is null ? 0 : 4) | (idx is null ? 0 : 8) | (g.Usage == BufferUsage.Static ? 0 : 16);
        int words = 4 + n * (3 + (nor is null ? 0 : 3) + (col is null ? 0 : 4) + (uv is null ? 0 : 2)) + (idx?.Length ?? 0);
        Reserve(words);
        int start = _n;
        Int(e.Handle);
        Int(flags);
        Int(n);
        Int(idx?.Length ?? 0);
        Bulk(MemoryMarshal.Cast<Vector3, int>(pos.AsSpan()));
        if (nor is not null) Bulk(MemoryMarshal.Cast<Vector3, int>(nor.AsSpan()));
        if (col is not null) Bulk(MemoryMarshal.Cast<Vector4, int>(col.AsSpan()));
        if (uv is not null) Bulk(MemoryMarshal.Cast<Vector2, int>(uv.AsSpan()));
        if (idx is not null) Bulk(MemoryMarshal.Cast<uint, int>(idx.AsSpan()));
        LastUploadBytes += (_n - start) * 4;
    }

    private void WriteTexture(Texture2D t, TexEntry e)
    {
        if (t.IsRenderTarget)
        {
            Reserve(6);
            Int(e.Handle); Int(1); Int(t.RenderTargetWidth); Int(t.RenderTargetHeight); Int((int)t.Wrap); Int((int)TextureFilter.Linear);
            return;
        }
        var img = t.Image!;
        Reserve(6 + img.Width * img.Height);
        int start = _n;
        Int(e.Handle); Int(0); Int(img.Width); Int(img.Height); Int((int)t.Wrap); Int((int)t.Filter);
        Bulk(MemoryMarshal.Cast<byte, int>(img.Pixels.AsSpan(0, img.Width * img.Height * 4)));
        LastUploadBytes += (_n - start) * 4;
    }

    private void WriteEnvironment(RenderList list, SceneEnvironment env, int width, int height, double time)
    {
        Reserve(EnvWords);
        int start = _n;
        var main = list.Passes.Find(p => p.Kind == PassKind.Main);
        var clear = main?.ClearColor ?? list.Camera.ClearColor;

        int casterIndex = ShadowCaster(list);
        LightInstance? caster = casterIndex >= 0 ? list.Lights[casterIndex] : null;
        int mapSize = System.Math.Clamp(env.Shadows.MapSize, 256, System.Math.Max(256, MaxShadowMapSize));
        var lightSpace = caster is { } c && main is not null ? LightSpace(c, list.Focus, env.Shadows, mapSize) : Matrix4x4.Identity;

        var post = env.Post;
        int flags = (env.SkyTopColor is null ? 0 : 1) | (env.Fog.Enabled ? 2 : 0) | (caster is null || main is null ? 0 : 4) |
                    (post.Hdr ? 8 : 0) | (post.Bloom ? 16 : 0) | (post.Vignette ? 32 : 0);
        Int(flags);
        Int(mapSize);
        Vec4(clear);
        Vec4(env.SkyTopColor ?? clear);
        Vec3(env.AmbientLight);
        Vec3(env.ReflectionColor);
        Vec3(env.Fog.Color);
        Float(env.Fog.Start);
        Float(env.Fog.End);
        Float(env.Shadows.Bias);
        Float(env.Shadows.Softness);
        Float(post.Exposure);
        Float(post.BloomThreshold);
        Float(post.BloomIntensity);
        Float((float)time);
        Mat(lightSpace);
        Int(width);
        Int(height);
        while (_n < start + EnvWords) Int(0);
    }

    /// <summary>Index of the first directional shadow-casting light within the uploaded lights, or -1.</summary>
    private static int ShadowCaster(RenderList list)
    {
        if (!list.Environment.Shadows.Enabled) return -1;
        int n = System.Math.Min(list.Lights.Count, ShaderLibrary.MaxLights);
        for (int i = 0; i < n; i++)
            if (list.Lights[i].Light is { CastShadows: true, Type: LightType.Directional }) return i;
        return -1;
    }

    /// <summary>Same light-space fit as the desktop GlRenderer: an ortho box around the (texel-snapped) focus.</summary>
    private static Matrix4x4 LightSpace(LightInstance light, Vector3 focus, ShadowSettings settings, int size)
    {
        float r = settings.Radius;
        var dir = Vector3.Normalize(light.WorldDirection);
        var up = MathF.Abs(dir.Z) > 0.95f ? Vector3.UnitY : Vector3.UnitZ;
        float texel = 2 * r / size;
        var snapped = new Vector3(MathF.Round(focus.X / texel) * texel, MathF.Round(focus.Y / texel) * texel, MathF.Round(focus.Z / texel) * texel);
        var view = Matrix4x4.CreateLookAt(snapped - dir * r * 2, snapped, up);
        var proj = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.05f, r * 4);
        return view * proj;
    }

    private void WriteState(ResolvedState s)
    {
        var m = s.Material;
        Vec4(m.Diffuse);
        Vec3(m.Ambient);
        Vec3(m.Specular);
        Vec3(m.Emission);
        Float(m.Shininess);
        Float(m.Reflectivity);
        Float(s.PointSize);
        int flags = (s.Lighting ? 1 : 0) | ((int)s.CullFace << 1) | ((int)s.Blend << 3) | (s.DepthTest ? 1 << 5 : 0) |
                    (s.DepthWrite ? 1 << 6 : 0) | (s.Fog ? 1 << 7 : 0) | (s.ReceiveShadows ? 1 << 8 : 0) |
                    (s.CastShadows ? 1 << 9 : 0) | ((int)s.PolygonMode << 10);
        Int(flags);
        Int(TextureHandle(s.Texture));
        Float(s.LineWidth);
        Int(0);
    }

    private void WritePass(RenderPass pass)
    {
        Reserve(PassWords + (pass.Opaque.Count + pass.Transparent.Count) * ItemWords);
        int start = _n;
        Int((int)pass.Kind);
        Int(TextureHandle(pass.Target));
        Int(pass.ClearColor is null ? 0 : 1);
        Vec4(pass.ClearColor ?? Vector4.Zero);
        Int(pass.ClearDepth ? 1 : 0);
        Mat(pass.View);
        Mat(pass.Projection);
        Vec3(pass.Eye);
        Int(pass.Opaque.Count);
        Int(pass.Transparent.Count);
        while (_n < start + PassWords) Int(0);
        foreach (ref readonly var item in CollectionsMarshal.AsSpan(pass.Opaque)) WriteItem(item);
        foreach (ref readonly var item in CollectionsMarshal.AsSpan(pass.Transparent)) WriteItem(item);
    }

    private void WriteItem(in DrawItem item)
    {
        var g = item.Geometry;
        Int(_itemRefs[_itemCursor++]);
        Int(_itemRefs[_itemCursor++]);
        Int((int)g.PrimitiveType);
        Int(System.Math.Max(0, g.DrawFirst));
        Int(g.DrawCount);
        Mat(item.World);
    }

    // ------------------------------------------------------------------ primitives
    private void Reserve(int words)
    {
        if (_n + words <= _w.Length) return;
        int size = _w.Length;
        while (size < _n + words) size *= 2;
        Array.Resize(ref _w, size);
    }

    private void Int(int v)
    {
        if (_n >= _w.Length) Reserve(1);
        _w[_n++] = v;
    }

    private void Float(float v) => Int(BitConverter.SingleToInt32Bits(v));

    private void Vec3(Vector3 v) { Float(v.X); Float(v.Y); Float(v.Z); }

    private void Vec4(Vector4 v) { Float(v.X); Float(v.Y); Float(v.Z); Float(v.W); }

    /// <summary>Raw System.Numerics layout (M11, M12, ... M44); GLSL reads it as the column-vector equivalent.</summary>
    private void Mat(in Matrix4x4 m)
    {
        Reserve(16);
        MemoryMarshal.Cast<Matrix4x4, int>(new ReadOnlySpan<Matrix4x4>(in m)).CopyTo(_w.AsSpan(_n));
        _n += 16;
    }

    private void Bulk(ReadOnlySpan<int> data)
    {
        Reserve(data.Length);
        data.CopyTo(_w.AsSpan(_n));
        _n += data.Length;
    }
}
