using System.Numerics;
using OpenSceneGraph.Net.Rendering;
using Silk.NET.OpenGL;

namespace OpenSceneGraph.Net.Rendering.OpenGL;

/// <summary>A linked GLSL program with cached uniform locations.</summary>
internal sealed unsafe class GlProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);

    public GlProgram(GL gl, string vertex, string fragment, string name)
    {
        _gl = gl;
        Name = name;
        uint vs = Compile(ShaderType.VertexShader, vertex);
        uint fs = Compile(ShaderType.FragmentShader, fragment);
        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vs);
        gl.AttachShader(Handle, fs);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int ok);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0) throw new InvalidOperationException($"Shader program '{name}' failed to link: {gl.GetProgramInfoLog(Handle)}");
    }

    public uint Handle { get; }
    public string Name { get; }

    private uint Compile(ShaderType type, string src)
    {
        uint s = _gl.CreateShader(type);
        _gl.ShaderSource(s, src);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} '{Name}' failed to compile: {_gl.GetShaderInfoLog(s)}");
        return s;
    }

    public void Use() => _gl.UseProgram(Handle);

    private int Loc(string name)
    {
        if (!_locations.TryGetValue(name, out int l))
        {
            l = _gl.GetUniformLocation(Handle, name);
            _locations[name] = l;
        }
        return l;
    }

    public void Set(string name, int v) { int l = Loc(name); if (l >= 0) _gl.Uniform1(l, v); }
    public void Set(string name, float v) { int l = Loc(name); if (l >= 0) _gl.Uniform1(l, v); }
    public void Set(string name, Vector2 v) { int l = Loc(name); if (l >= 0) _gl.Uniform2(l, v.X, v.Y); }
    public void Set(string name, Vector3 v) { int l = Loc(name); if (l >= 0) _gl.Uniform3(l, v.X, v.Y, v.Z); }
    public void Set(string name, Vector4 v) { int l = Loc(name); if (l >= 0) _gl.Uniform4(l, v.X, v.Y, v.Z, v.W); }

    /// <summary>System.Numerics matrices are row-major/row-vector; uploading untransposed yields the column-vector GLSL equivalent.</summary>
    public void Set(string name, Matrix4x4 m)
    {
        int l = Loc(name);
        if (l >= 0) _gl.UniformMatrix4(l, 1, false, (float*)&m);
    }

    public void SetNormalMatrix(string name, Matrix4x4 m)
    {
        int l = Loc(name);
        if (l < 0) return;
        float* n = stackalloc float[9] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33 };
        _gl.UniformMatrix3(l, 1, false, n);
    }

    public void SetArray(string name, ReadOnlySpan<Vector4> values)
    {
        int l = Loc(name);
        if (l < 0 || values.IsEmpty) return;
        fixed (Vector4* p = values) _gl.Uniform4(l, (uint)values.Length, (float*)p);
    }

    public void SetObject(string name, object value)
    {
        switch (value)
        {
            case float f: Set(name, f); break;
            case int i: Set(name, i); break;
            case bool b: Set(name, b ? 1 : 0); break;
            case Vector2 v2: Set(name, v2); break;
            case Vector3 v3: Set(name, v3); break;
            case Vector4 v4: Set(name, v4); break;
            case Matrix4x4 m: Set(name, m); break;
        }
    }

    public void Dispose() => _gl.DeleteProgram(Handle);
}

/// <summary>Depth-only framebuffer for directional shadow mapping.</summary>
internal sealed unsafe class ShadowMap : IDisposable
{
    private readonly GL _gl;

    public ShadowMap(GL gl, int size)
    {
        _gl = gl;
        Size = size;
        Depth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Depth);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)size, (uint)size, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, Depth, 0);
        GLEnum none = GLEnum.None;
        gl.DrawBuffers(1, &none);
        gl.ReadBuffer(GLEnum.None);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public int Size { get; }
    public uint Fbo { get; }
    public uint Depth { get; }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(Fbo);
        _gl.DeleteTexture(Depth);
    }
}

/// <summary>HDR scene buffer + half-resolution bloom ping-pong buffers.</summary>
internal sealed unsafe class PostChain : IDisposable
{
    private readonly GL _gl;
    private uint _sceneTex, _sceneDepth;
    private readonly uint[] _bloomFbo = new uint[2];
    private readonly uint[] _bloomTex = new uint[2];
    private int _w, _h;
    private bool _float = true;

    public PostChain(GL gl) => _gl = gl;

    public uint SceneFbo { get; private set; }

    public void Ensure(int w, int h)
    {
        if (w == _w && h == _h && SceneFbo != 0) return;
        Release();
        _w = w; _h = h;
        var gl = _gl;
        SceneFbo = gl.GenFramebuffer();
        _sceneTex = ColorTexture(w, h);
        _sceneDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, SceneFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _sceneTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _sceneDepth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete && _float)
        {
            // float colour buffers unsupported (e.g. ES without EXT_color_buffer_float): retry with RGBA8
            _float = false;
            Release();
            Ensure(w, h);
            return;
        }
        for (int i = 0; i < 2; i++)
        {
            _bloomFbo[i] = gl.GenFramebuffer();
            _bloomTex[i] = ColorTexture(System.Math.Max(1, w / 2), System.Math.Max(1, h / 2));
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo[i]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _bloomTex[i], 0);
        }
    }

    private uint ColorTexture(int w, int h)
    {
        var gl = _gl;
        uint t = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t);
        if (_float) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
        else gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return t;
    }

    public void Run(GlRenderer r, PostEffects fx, uint target, int w, int h)
    {
        var gl = _gl;
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.DepthMask(false);
        int bw = System.Math.Max(1, w / 2), bh = System.Math.Max(1, h / 2);
        if (fx.Bloom)
        {
            gl.Viewport(0, 0, (uint)bw, (uint)bh);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo[0]);
            var bright = r.BrightProgram;
            bright.Use();
            bright.Set("uScene", 0);
            bright.Set("uThreshold", fx.BloomThreshold);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _sceneTex);
            r.DrawFullscreen(bright);

            var blur = r.BlurProgram;
            blur.Use();
            blur.Set("uSrc", 0);
            for (int i = 0; i < 4; i++)
            {
                int src = i % 2, dst = 1 - src;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo[dst]);
                gl.BindTexture(TextureTarget.Texture2D, _bloomTex[src]);
                blur.Set("uDir", i % 2 == 0 ? new Vector2(1f / bw, 0) : new Vector2(0, 1f / bh));
                r.DrawFullscreen(blur);
            }
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        var c = r.CompositeProgram;
        c.Use();
        c.Set("uScene", 0);
        c.Set("uBloom", 1);
        c.Set("uUseBloom", fx.Bloom ? 1 : 0);
        c.Set("uBloomIntensity", fx.BloomIntensity);
        c.Set("uHdr", fx.Hdr ? 1 : 0);
        c.Set("uExposure", fx.Exposure);
        c.Set("uVignette", fx.Vignette ? 1 : 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, _bloomTex[0]);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneTex);
        r.DrawFullscreen(c);
        gl.DepthMask(true);
    }

    private void Release()
    {
        var gl = _gl;
        if (SceneFbo != 0) gl.DeleteFramebuffer(SceneFbo);
        if (_sceneTex != 0) gl.DeleteTexture(_sceneTex);
        if (_sceneDepth != 0) gl.DeleteRenderbuffer(_sceneDepth);
        for (int i = 0; i < 2; i++)
        {
            if (_bloomFbo[i] != 0) gl.DeleteFramebuffer(_bloomFbo[i]);
            if (_bloomTex[i] != 0) gl.DeleteTexture(_bloomTex[i]);
            _bloomFbo[i] = _bloomTex[i] = 0;
        }
        SceneFbo = _sceneTex = _sceneDepth = 0;
    }

    public void Dispose() => Release();
}
