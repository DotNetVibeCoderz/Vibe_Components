using System.Numerics;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;
using Silk.NET.OpenGL;
using GlPrimitive = Silk.NET.OpenGL.PrimitiveType;
using OsgPrimitive = OpenSceneGraph.Net.Scene.PrimitiveType;

namespace OpenSceneGraph.Net.Rendering.OpenGL;

/// <summary>
/// Renders <see cref="RenderList"/>s with OpenGL 3.3 core or OpenGL ES 3.0 (ANGLE, used by Avalonia on Windows).
/// Create it on the thread that owns the GL context and call <see cref="Render(RenderList)"/> every frame.
/// GPU resources are cached by geometry/texture id and released when unused for a few seconds.
/// </summary>
public sealed unsafe class GlRenderer : IRenderer
{
    private readonly GL _gl;
    private readonly bool _es;
    private readonly Dictionary<long, GpuMesh> _meshes = [];
    private readonly Dictionary<long, GpuTexture> _textures = [];
    private readonly Dictionary<long, RenderTarget> _rtt = [];
    private readonly Dictionary<long, GlProgram> _customPrograms = [];
    private readonly GlProgram _main, _depth, _background, _bright, _blur, _composite;
    private readonly uint _emptyVao;
    private readonly uint _whiteTexture;
    private ShadowMap? _shadow;
    private PostChain? _post;
    private long _frame;
    private int _width = 1, _height = 1;
    private double _time;

    /// <param name="gl">The GL API bound to the current context.</param>
    /// <param name="isGles">True for OpenGL ES / WebGL-style contexts (selects the shader header and disables glPolygonMode).</param>
    public GlRenderer(GL gl, bool isGles)
    {
        _gl = gl;
        _es = isGles;
        string header = isGles ? ShaderLibrary.HeaderEs : ShaderLibrary.HeaderCore;
        _main = new GlProgram(gl, header + ShaderLibrary.MainVertex, header + ShaderLibrary.MainFragment, "main");
        _depth = new GlProgram(gl, header + ShaderLibrary.DepthVertex, header + ShaderLibrary.DepthFragment, "depth");
        _background = new GlProgram(gl, header + ShaderLibrary.FullscreenVertex, header + ShaderLibrary.BackgroundFragment, "background");
        _bright = new GlProgram(gl, header + ShaderLibrary.FullscreenVertex, header + ShaderLibrary.BrightFragment, "bright");
        _blur = new GlProgram(gl, header + ShaderLibrary.FullscreenVertex, header + ShaderLibrary.BlurFragment, "blur");
        _composite = new GlProgram(gl, header + ShaderLibrary.FullscreenVertex, header + ShaderLibrary.CompositeFragment, "composite");
        _emptyVao = gl.GenVertexArray();
        _whiteTexture = CreateTexture(1, 1, [255, 255, 255, 255], TextureWrap.Repeat, TextureFilter.Nearest);
        var version = gl.GetStringS(StringName.Version);
        Name = $"OpenGL{(isGles ? " ES" : "")} ({version})";
    }

    public string Name { get; }

    public RendererCapabilities Capabilities =>
        RendererCapabilities.Lighting | RendererCapabilities.Textures | RendererCapabilities.Shadows | RendererCapabilities.Fog |
        RendererCapabilities.CustomShaders | RendererCapabilities.RenderToTexture | RendererCapabilities.PostProcessing |
        RendererCapabilities.Stereo | RendererCapabilities.Skinning;

    /// <summary>Framebuffer to render into (0 = default). Avalonia passes its own FBO id.</summary>
    public uint TargetFramebuffer { get; set; }

    /// <summary>Simulation time exposed to custom shaders as <c>osg_SimulationTime</c>.</summary>
    public double SimulationTime { get => _time; set => _time = value; }

    public int DrawCalls { get; private set; }

    public void Render(RenderList list) => Render(list, list.Camera.Viewport.Width, list.Camera.Viewport.Height);

    public void Render(RenderList list, int width, int height)
    {
        _frame++;
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);
        DrawCalls = 0;
        var env = list.Environment;
        var gl = _gl;

        // 1. pre-render passes (render to texture)
        foreach (var pass in list.Passes)
            if (pass.Kind == PassKind.PreRender && pass.Target is { IsRenderTarget: true } tex)
            {
                var rt = GetRenderTarget(tex);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
                gl.Viewport(0, 0, (uint)rt.Width, (uint)rt.Height);
                ClearPass(pass, pass.ClearColor ?? new Vector4(0, 0, 0, 1));
                DrawPass(pass, list, env, useShadow: false, rt.Height);
            }

        // 2. shadow map
        var main = list.MainPass;
        bool shadows = env.Shadows.Enabled && list.Lights.Exists(l => l.Light.CastShadows && l.Light.Type == LightType.Directional);
        Matrix4x4 lightSpace = Matrix4x4.Identity;
        if (shadows)
        {
            var light = list.Lights.First(l => l.Light.CastShadows && l.Light.Type == LightType.Directional);
            lightSpace = RenderShadowMap(main, light, list.Focus, env.Shadows);
        }

        // 3. main pass (optionally into the HDR/post buffer)
        bool post = env.Post.Hdr || env.Post.Bloom || env.Post.Vignette;
        uint sceneFbo = TargetFramebuffer;
        if (post)
        {
            _post ??= new PostChain(gl);
            _post.Ensure(_width, _height);
            sceneFbo = _post.SceneFbo;
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        gl.Viewport(0, 0, (uint)_width, (uint)_height);
        var clear = main.ClearColor ?? list.Camera.ClearColor;
        gl.ClearColor(clear.X, clear.Y, clear.Z, clear.W);
        gl.DepthMask(true);
        gl.ColorMask(true, true, true, true);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (env.SkyTopColor is { } top) DrawBackground(top, clear);

        _lightSpace = lightSpace;
        _shadowOn = shadows;
        var stereo = list.Camera.Stereo;
        if (stereo == StereoMode.None) DrawPass(main, list, env, shadows, _height);
        else DrawStereo(main, list, env, shadows, stereo, list.Camera.EyeSeparation);

        // 4. post-render (HUD) passes
        foreach (var pass in list.Passes)
            if (pass.Kind == PassKind.PostRender && pass.Target is null)
            {
                if (pass.ClearDepth) { gl.DepthMask(true); gl.Clear(ClearBufferMask.DepthBufferBit); }
                if (pass.ClearColor is { } cc) { gl.ClearColor(cc.X, cc.Y, cc.Z, cc.W); gl.Clear(ClearBufferMask.ColorBufferBit); }
                DrawPass(pass, list, env, useShadow: false, _height);
            }

        // 5. post-processing to the real target
        if (post) _post!.Run(this, env.Post, TargetFramebuffer, _width, _height);

        if (_frame % 120 == 0) EvictUnused();
    }

    private Matrix4x4 _lightSpace;
    private bool _shadowOn;
    private Matrix4x4? _viewOverride; // per-eye view while rendering stereo

    private void ClearPass(RenderPass pass, Vector4 c)
    {
        _gl.ClearColor(c.X, c.Y, c.Z, c.W);
        _gl.DepthMask(true);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    private void DrawStereo(RenderPass main, RenderList list, SceneEnvironment env, bool shadows, StereoMode mode, float separation)
    {
        for (int eye = 0; eye < 2; eye++)
        {
            float offset = (eye == 0 ? 0.5f : -0.5f) * separation;
            _viewOverride = main.View * Matrix4x4.CreateTranslation(offset, 0, 0);
            switch (mode)
            {
                case StereoMode.AnaglyphRedCyan:
                    _gl.ColorMask(eye == 0, eye == 1, eye == 1, true);
                    if (eye == 1) _gl.Clear(ClearBufferMask.DepthBufferBit);
                    DrawPass(main, list, env, shadows, _height);
                    break;
                case StereoMode.SideBySide:
                    _gl.Viewport(eye == 0 ? 0 : _width / 2, 0, (uint)(_width / 2), (uint)_height);
                    DrawPass(main, list, env, shadows, _height);
                    break;
                case StereoMode.TopBottom:
                    _gl.Viewport(0, eye == 0 ? _height / 2 : 0, (uint)_width, (uint)(_height / 2));
                    DrawPass(main, list, env, shadows, _height / 2);
                    break;
            }
        }
        _viewOverride = null;
        _gl.ColorMask(true, true, true, true);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    private void DrawBackground(Vector4 top, Vector4 bottom)
    {
        var gl = _gl;
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.Blend);
        _background.Use();
        _background.Set("uTop", top);
        _background.Set("uBottom", bottom);
        gl.BindVertexArray(_emptyVao);
        gl.DrawArrays(GlPrimitive.Triangles, 0, 3);
        gl.DepthMask(true);
    }

    private void DrawPass(RenderPass pass, RenderList list, SceneEnvironment env, bool useShadow, int viewportHeight)
    {
        var gl = _gl;
        gl.Enable(EnableCap.DepthTest);
        if (!_es) gl.Enable((EnableCap)0x8642); // GL_PROGRAM_POINT_SIZE

        var p = _main;
        p.Use();
        p.Set("uView", _viewOverride ?? pass.View);
        p.Set("uProj", pass.Projection);
        p.Set("uEye", pass.Eye);
        p.Set("uViewportH", (float)viewportHeight);
        p.Set("uAmbient", env.AmbientLight);
        p.Set("uEnvColor", env.ReflectionColor);
        var fog = env.Fog;
        p.Set("uFogColor", fog.Color);
        p.Set("uFogRange", new Vector2(fog.Start, fog.End));
        p.Set("uShadowBias", env.Shadows.Bias);
        p.Set("uTex", 0);
        p.Set("uShadowMap", 1);
        bool hud = pass.Kind == PassKind.PostRender; // RTT passes are lit like the main pass
        UploadLights(p, hud ? null : list.Lights, useShadow);
        if (useShadow && _shadow is not null)
        {
            p.Set("uLightSpace", _lightSpace);
            p.Set("uShadowTexel", 1f / _shadow.Size * env.Shadows.Softness);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, _shadow.Depth);
            gl.ActiveTexture(TextureUnit.Texture0);
        }

        DrawItems(pass.Opaque, pass, env, useShadow, hud);
        DrawItems(pass.Transparent, pass, env, useShadow, hud);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    private void UploadLights(GlProgram p, List<LightInstance>? lights, bool useShadow)
    {
        int count = lights is null ? 0 : System.Math.Min(lights.Count, ShaderLibrary.MaxLights);
        p.Set("uLightCount", count);
        if (lights is null) return;
        Span<Vector4> pos = stackalloc Vector4[8];
        Span<Vector4> col = stackalloc Vector4[8];
        Span<Vector4> dir = stackalloc Vector4[8];
        // The shader applies the shadow map to light 0, but the cull visitor adds the head light first:
        // upload the shadow-casting sun in slot 0 so shadows darken the light that actually casts them.
        int shadowIndex = useShadow ? lights.FindIndex(l => l.Light.CastShadows && l.Light.Type == LightType.Directional) : -1;
        if (shadowIndex >= count) shadowIndex = -1;
        for (int i = 0; i < count; i++)
        {
            int src = shadowIndex <= 0 ? i : i == 0 ? shadowIndex : i == shadowIndex ? 0 : i;
            var l = lights[src];
            pos[i] = l.Light.Type == LightType.Directional ? new Vector4(l.WorldDirection, 0) : new Vector4(l.WorldPosition, l.Light.Type == LightType.Spot ? 2 : 1);
            col[i] = new Vector4(l.Light.Color * l.Light.Intensity, l.Light.Range);
            dir[i] = new Vector4(l.WorldDirection, MathF.Cos(l.Light.SpotCutoffDegrees * MathF.PI / 180));
        }
        p.SetArray("uLightPos[0]", pos[..count]);
        p.SetArray("uLightColor[0]", col[..count]);
        p.SetArray("uLightDir[0]", dir[..count]);
    }

    private void DrawItems(List<DrawItem> items, RenderPass pass, SceneEnvironment env, bool useShadow, bool hud)
    {
        var gl = _gl;
        ResolvedState? last = null;
        GlProgram current = _main;
        foreach (var item in items)
        {
            var s = item.State;
            var g = item.Geometry;
            var mesh = GetMesh(g);
            if (mesh.Count == 0) continue;

            GlProgram prog = s.Program is { } custom ? GetCustomProgram(custom) : _main;
            if (!ReferenceEquals(prog, current))
            {
                prog.Use();
                current = prog;
                if (!ReferenceEquals(prog, _main)) SetBuiltins(prog, pass);
                last = null;
            }

            if (!ReferenceEquals(s, last))
            {
                ApplyState(s, prog, env, useShadow, hud);
                last = s;
            }

            prog.Set(ReferenceEquals(prog, _main) ? "uModel" : "osg_ModelMatrix", item.World);
            Matrix4x4.Invert(item.World, out var inv);
            prog.SetNormalMatrix(ReferenceEquals(prog, _main) ? "uNormalMat" : "osg_NormalMatrix", Matrix4x4.Transpose(inv));
            bool points = g.PrimitiveType == OsgPrimitive.Points;
            if (ReferenceEquals(prog, _main))
            {
                prog.Set("uHasVertexColor", mesh.HasColors ? 1 : 0);
                prog.Set("uIsPoints", points ? 1 : 0);
                prog.Set("uPointMode", points && mesh.HasUv ? 1 : 0);
            }

            gl.BindVertexArray(mesh.Vao);
            int first = System.Math.Max(0, g.DrawFirst);
            int count = g.DrawCount >= 0 ? System.Math.Min(g.DrawCount, mesh.Count - first) : mesh.Count - first;
            if (count <= 0) continue;
            if (_es && s.PolygonMode == State.PolygonMode.Line && g.PrimitiveType is OsgPrimitive.Triangles or OsgPrimitive.TriangleStrip)
            {
                // OpenGL ES has no glPolygonMode: draw the triangle edges as GL_LINES instead
                DrawWireframe(g, mesh);
                DrawCalls++;
                continue;
            }
            var mode = ToGl(g.PrimitiveType);
            if (mesh.Indexed) gl.DrawElements(mode, (uint)count, DrawElementsType.UnsignedInt, (void*)(first * sizeof(uint)));
            else gl.DrawArrays(mode, first, (uint)count);
            DrawCalls++;
        }
        if (!ReferenceEquals(current, _main)) _main.Use();
        gl.BindVertexArray(0);
    }

    private void SetBuiltins(GlProgram prog, RenderPass pass)
    {
        prog.Set("osg_ViewMatrix", _viewOverride ?? pass.View);
        prog.Set("osg_ProjectionMatrix", pass.Projection);
        prog.Set("osg_EyePosition", pass.Eye);
        prog.Set("osg_SimulationTime", (float)_time);
        prog.Set("osg_ViewportSize", new Vector2(_width, _height));
    }

    private void ApplyState(ResolvedState s, GlProgram prog, SceneEnvironment env, bool useShadow, bool hud)
    {
        var gl = _gl;
        if (s.DepthTest) gl.Enable(EnableCap.DepthTest); else gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(s.DepthWrite);
        switch (s.CullFace)
        {
            case CullFaceMode.None: gl.Disable(EnableCap.CullFace); break;
            case CullFaceMode.Back: gl.Enable(EnableCap.CullFace); gl.CullFace(TriangleFace.Back); break;
            case CullFaceMode.Front: gl.Enable(EnableCap.CullFace); gl.CullFace(TriangleFace.Front); break;
        }
        switch (s.Blend)
        {
            case BlendMode.Opaque: gl.Disable(EnableCap.Blend); break;
            case BlendMode.AlphaBlend: gl.Enable(EnableCap.Blend); gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); break;
            case BlendMode.Additive: gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;
            case BlendMode.Multiply: gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero); break;
        }
        if (!_es) gl.PolygonMode(TriangleFace.FrontAndBack, s.PolygonMode switch { State.PolygonMode.Line => Silk.NET.OpenGL.PolygonMode.Line, State.PolygonMode.Point => Silk.NET.OpenGL.PolygonMode.Point, _ => Silk.NET.OpenGL.PolygonMode.Fill });

        // texture unit 0
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, s.Texture is { } t ? GetTexture(t) : _whiteTexture);

        if (ReferenceEquals(prog, _main))
        {
            var m = s.Material;
            prog.Set("uDiffuse", m.Diffuse);
            prog.Set("uMatAmbient", m.Ambient);
            prog.Set("uSpecular", m.Specular);
            prog.Set("uEmission", m.Emission);
            prog.Set("uShininess", m.Shininess);
            prog.Set("uReflectivity", m.Reflectivity);
            prog.Set("uLighting", s.Lighting ? 1 : 0);
            prog.Set("uHasTexture", s.Texture is not null ? 1 : 0);
            prog.Set("uPointSize", s.PointSize);
            prog.Set("uFog", env.Fog.Enabled && s.Fog && !hud ? 1 : 0);
            prog.Set("uShadow", useShadow && s.ReceiveShadows ? 1 : 0);
        }
        else if (s.Uniforms is { } u)
        {
            foreach (var (name, value) in u.All) prog.SetObject(name, value);
        }
        if (s.LineWidth != 1f && !_es) gl.LineWidth(s.LineWidth);
    }

    private static GlPrimitive ToGl(OsgPrimitive p) => p switch
    {
        OsgPrimitive.Points => GlPrimitive.Points,
        OsgPrimitive.Lines => GlPrimitive.Lines,
        OsgPrimitive.LineStrip => GlPrimitive.LineStrip,
        OsgPrimitive.TriangleStrip => GlPrimitive.TriangleStrip,
        _ => GlPrimitive.Triangles,
    };

    // ------------------------------------------------------------------ shadows
    private Matrix4x4 RenderShadowMap(RenderPass main, LightInstance light, Vector3 focus, ShadowSettings settings)
    {
        var gl = _gl;
        _shadow ??= new ShadowMap(gl, settings.MapSize);
        if (_shadow.Size != settings.MapSize) { _shadow.Dispose(); _shadow = new ShadowMap(gl, settings.MapSize); }

        float r = settings.Radius;
        var dir = Vector3.Normalize(light.WorldDirection);
        var up = MathF.Abs(dir.Z) > 0.95f ? Vector3.UnitY : Vector3.UnitZ;
        // snap the focus to shadow texels to avoid shimmering while the camera moves
        float texel = 2 * r / _shadow.Size;
        var snapped = new Vector3(MathF.Round(focus.X / texel) * texel, MathF.Round(focus.Y / texel) * texel, MathF.Round(focus.Z / texel) * texel);
        var view = Matrix4x4.CreateLookAt(snapped - dir * r * 2, snapped, up);
        var proj = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.05f, r * 4);
        var lightSpace = view * proj;

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadow.Fbo);
        gl.Viewport(0, 0, (uint)_shadow.Size, (uint)_shadow.Size);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Front); // reduces acne on lit faces
        _depth.Use();
        foreach (var item in main.Opaque)
        {
            if (!item.State.CastShadows || item.Geometry.PrimitiveType is OsgPrimitive.Points or OsgPrimitive.Lines or OsgPrimitive.LineStrip) continue;
            var mesh = GetMesh(item.Geometry);
            if (mesh.Count == 0) continue;
            _depth.Set("uLightMvp", item.World * lightSpace);
            gl.BindVertexArray(mesh.Vao);
            if (mesh.Indexed) gl.DrawElements(ToGl(item.Geometry.PrimitiveType), (uint)mesh.Count, DrawElementsType.UnsignedInt, null);
            else gl.DrawArrays(ToGl(item.Geometry.PrimitiveType), 0, (uint)mesh.Count);
            DrawCalls++;
        }
        gl.CullFace(TriangleFace.Back);
        gl.BindVertexArray(0);
        return lightSpace;
    }

    // ------------------------------------------------------------------ resources
    private sealed class GpuMesh
    {
        public uint Vao, Pos, Nor, Col, Uv, Ebo;
        public int Version = -1;
        public int Count;
        public bool Indexed, HasColors, HasUv;
        public long LastUsed;
        public int Capacity;
        public int VertexCount;
        public uint WireEbo;
        public int WireCount;
        public int WireVersion = -1;
    }

    /// <summary>Wireframe emulation for OpenGL ES: a cached, de-duplicated edge index buffer drawn as GL_LINES.</summary>
    private void DrawWireframe(Geometry g, GpuMesh m)
    {
        var gl = _gl;
        if (m.WireVersion != m.Version)
        {
            var tri = g.Indices is { Length: > 0 } idx ? idx : Enumerable.Range(0, m.VertexCount).Select(i => (uint)i).ToArray();
            var edges = new HashSet<ulong>();
            var lines = new List<uint>(tri.Length * 2);
            void Edge(uint a, uint b)
            {
                if (a == b) return;
                ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
                if (edges.Add(key)) { lines.Add(a); lines.Add(b); }
            }
            if (g.PrimitiveType == OsgPrimitive.TriangleStrip)
                for (int i = 0; i + 2 < tri.Length; i++) { Edge(tri[i], tri[i + 1]); Edge(tri[i + 1], tri[i + 2]); Edge(tri[i + 2], tri[i]); }
            else
                for (int i = 0; i + 2 < tri.Length; i += 3) { Edge(tri[i], tri[i + 1]); Edge(tri[i + 1], tri[i + 2]); Edge(tri[i + 2], tri[i]); }
            if (m.WireEbo == 0) m.WireEbo = gl.GenBuffer();
            gl.BindVertexArray(m.Vao);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.WireEbo);
            var arr = lines.ToArray();
            fixed (uint* p = arr) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(arr.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
            m.WireCount = arr.Length;
            m.WireVersion = m.Version;
        }
        gl.BindVertexArray(m.Vao);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.WireEbo); // element binding is VAO state
        gl.DrawElements(GlPrimitive.Lines, (uint)m.WireCount, DrawElementsType.UnsignedInt, null);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.Indexed ? m.Ebo : 0);
    }

    private GpuMesh GetMesh(Geometry g)
    {
        if (!_meshes.TryGetValue(g.Id, out var m))
        {
            m = new GpuMesh { Vao = _gl.GenVertexArray() };
            _meshes[g.Id] = m;
        }
        m.LastUsed = _frame;
        if (m.Version != g.Version) Upload(g, m);
        return m;
    }

    private void Upload(Geometry g, GpuMesh m)
    {
        var gl = _gl;
        var usage = g.Usage == BufferUsage.Static ? BufferUsageARB.StaticDraw : BufferUsageARB.DynamicDraw;
        int n = g.Positions.Length;
        m.VertexCount = n;
        gl.BindVertexArray(m.Vao);

        Attr(ref m.Pos, 0, 3, g.Positions.AsSpan(), usage);
        if (g.Normals is { Length: > 0 } nor && nor.Length == n) Attr(ref m.Nor, 1, 3, nor.AsSpan(), usage);
        else { gl.DisableVertexAttribArray(1); gl.VertexAttrib3(1, 0f, 0f, 1f); }
        if (g.Colors is { Length: > 0 } col && col.Length == n) Attr(ref m.Col, 2, 4, col.AsSpan(), usage);
        else { gl.DisableVertexAttribArray(2); gl.VertexAttrib4(2, 1f, 1f, 1f, 1f); }
        if (g.TexCoords is { Length: > 0 } uv && uv.Length == n) Attr(ref m.Uv, 3, 2, uv.AsSpan(), usage);
        else { gl.DisableVertexAttribArray(3); gl.VertexAttrib2(3, 0f, 0f); }

        m.HasColors = g.Colors is { } c2 && c2.Length == n;
        m.HasUv = g.TexCoords is { } t2 && t2.Length == n;
        if (g.Indices is { Length: > 0 } idx)
        {
            if (m.Ebo == 0) m.Ebo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.Ebo);
            fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(uint)), p, usage);
            m.Indexed = true;
            m.Count = idx.Length;
        }
        else
        {
            m.Indexed = false;
            m.Count = n;
        }
        m.Version = g.Version;
        gl.BindVertexArray(0);
    }

    private void Attr<T>(ref uint buffer, uint location, int size, ReadOnlySpan<T> data, BufferUsageARB usage) where T : unmanaged
    {
        var gl = _gl;
        if (buffer == 0) buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
        fixed (T* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(T)), p, usage);
        gl.EnableVertexAttribArray(location);
        gl.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, (uint)(size * sizeof(float)), null);
    }

    private sealed class GpuTexture
    {
        public uint Id;
        public int Version;
        public long LastUsed;
    }

    private uint GetTexture(Texture2D t)
    {
        if (t.IsRenderTarget) return GetRenderTarget(t).Color;
        if (t.Image is not { } img) return _whiteTexture;
        if (_textures.TryGetValue(t.Id, out var gt) && gt.Version == img.Version)
        {
            gt.LastUsed = _frame;
            return gt.Id;
        }
        if (gt is not null) _gl.DeleteTexture(gt.Id);
        gt = new GpuTexture { Id = CreateTexture(img.Width, img.Height, img.Pixels, t.Wrap, t.Filter), Version = img.Version, LastUsed = _frame };
        _textures[t.Id] = gt;
        return gt.Id;
    }

    private uint CreateTexture(int w, int h, byte[] rgba, TextureWrap wrap, TextureFilter filter)
    {
        var gl = _gl;
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        fixed (byte* p = rgba) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var glWrap = wrap switch { TextureWrap.ClampToEdge => GLEnum.ClampToEdge, TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat, _ => GLEnum.Repeat };
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)glWrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)glWrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(filter == TextureFilter.Nearest ? GLEnum.Nearest : GLEnum.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(filter switch
        {
            TextureFilter.Nearest => GLEnum.Nearest,
            TextureFilter.Linear => GLEnum.Linear,
            _ => GLEnum.LinearMipmapLinear,
        }));
        if (filter == TextureFilter.LinearMipmap) gl.GenerateMipmap(TextureTarget.Texture2D);
        return id;
    }

    private sealed class RenderTarget
    {
        public uint Fbo, Color, Depth;
        public int Width, Height;
    }

    private RenderTarget GetRenderTarget(Texture2D t)
    {
        if (_rtt.TryGetValue(t.Id, out var rt)) return rt;
        var gl = _gl;
        rt = new RenderTarget { Width = t.RenderTargetWidth, Height = t.RenderTargetHeight };
        rt.Color = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, rt.Color);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)rt.Width, (uint)rt.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        rt.Depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rt.Depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)rt.Width, (uint)rt.Height);
        rt.Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, rt.Color, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, rt.Depth);
        _rtt[t.Id] = rt;
        return rt;
    }

    private GlProgram GetCustomProgram(ShaderProgram sp)
    {
        if (_customPrograms.TryGetValue(sp.Id, out var p)) return p;
        string header = _es ? ShaderLibrary.HeaderEs : ShaderLibrary.HeaderCore;
        const string attribs = "layout(location = 0) in vec3 osg_Vertex;\nlayout(location = 1) in vec3 osg_Normal;\nlayout(location = 2) in vec4 osg_Color;\nlayout(location = 3) in vec2 osg_MultiTexCoord0;\n" +
                               "uniform mat4 osg_ModelMatrix;\nuniform mat4 osg_ViewMatrix;\nuniform mat4 osg_ProjectionMatrix;\nuniform mat3 osg_NormalMatrix;\nuniform float osg_SimulationTime;\nuniform vec2 osg_ViewportSize;\nuniform vec3 osg_EyePosition;\n";
        const string fragBuiltins = "uniform float osg_SimulationTime;\nuniform vec2 osg_ViewportSize;\nuniform vec3 osg_EyePosition;\n";
        p = new GlProgram(_gl, header + attribs + sp.VertexSource, header + fragBuiltins + sp.FragmentSource, sp.Name ?? "custom");
        _customPrograms[sp.Id] = p;
        return p;
    }

    private void EvictUnused()
    {
        const long maxIdle = 600; // frames
        foreach (var (id, m) in _meshes.Where(kv => _frame - kv.Value.LastUsed > maxIdle).ToList())
        {
            _gl.DeleteVertexArray(m.Vao);
            foreach (var b in (ReadOnlySpan<uint>)[m.Pos, m.Nor, m.Col, m.Uv, m.Ebo, m.WireEbo]) if (b != 0) _gl.DeleteBuffer(b);
            _meshes.Remove(id);
        }
        foreach (var (id, t) in _textures.Where(kv => _frame - kv.Value.LastUsed > maxIdle).ToList())
        {
            _gl.DeleteTexture(t.Id);
            _textures.Remove(id);
        }
    }

    internal void DrawFullscreen(GlProgram p)
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(GlPrimitive.Triangles, 0, 3);
    }

    internal GlProgram BrightProgram => _bright;
    internal GlProgram BlurProgram => _blur;
    internal GlProgram CompositeProgram => _composite;

    public void Dispose()
    {
        foreach (var m in _meshes.Values)
        {
            _gl.DeleteVertexArray(m.Vao);
            foreach (var b in (ReadOnlySpan<uint>)[m.Pos, m.Nor, m.Col, m.Uv, m.Ebo, m.WireEbo]) if (b != 0) _gl.DeleteBuffer(b);
        }
        foreach (var t in _textures.Values) _gl.DeleteTexture(t.Id);
        foreach (var r in _rtt.Values) { _gl.DeleteFramebuffer(r.Fbo); _gl.DeleteTexture(r.Color); _gl.DeleteRenderbuffer(r.Depth); }
        foreach (var p in _customPrograms.Values) p.Dispose();
        _meshes.Clear(); _textures.Clear(); _rtt.Clear(); _customPrograms.Clear();
        _shadow?.Dispose();
        _post?.Dispose();
        _main.Dispose(); _depth.Dispose(); _background.Dispose(); _bright.Dispose(); _blur.Dispose(); _composite.Dispose();
        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteTexture(_whiteTexture);
    }
}
