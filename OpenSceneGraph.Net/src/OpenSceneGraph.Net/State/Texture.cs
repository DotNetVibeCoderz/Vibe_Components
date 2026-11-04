using System.Numerics;

namespace OpenSceneGraph.Net.State;

/// <summary>RGBA8 image in memory (<c>osg::Image</c>). Rows are stored top to bottom.</summary>
public sealed class Image
{
    public Image(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer must be width*height*4 bytes.", nameof(pixels));
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Version { get; private set; }
    public long Id { get; } = Interlocked.Increment(ref s_id);
    private static long s_id;

    public void MarkDirty() => Version++;

    public void SetPixel(int x, int y, Vector4 c)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = ToByte(c.X);
        Pixels[i + 1] = ToByte(c.Y);
        Pixels[i + 2] = ToByte(c.Z);
        Pixels[i + 3] = ToByte(c.W);
    }

    public Vector4 GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new Vector4(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]) / 255f;
    }

    private static byte ToByte(float v) => (byte)System.Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>Creates an image by evaluating <paramref name="shader"/> at each pixel's normalized (u,v).</summary>
    public static Image Generate(int width, int height, Func<float, float, Vector4> shader)
    {
        var img = new Image(width, height);
        Parallel.For(0, height, y =>
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++) img.SetPixel(x, y, shader((x + 0.5f) / width, v));
        });
        return img;
    }

    public static Image Checkerboard(int size, int cells, Vector4 a, Vector4 b) =>
        Generate(size, size, (u, v) => (((int)(u * cells) + (int)(v * cells)) & 1) == 0 ? a : b);

    public static Image Solid(Vector4 c) => Generate(1, 1, (_, _) => c);
}

public enum TextureWrap : byte { Repeat, ClampToEdge, MirroredRepeat }
public enum TextureFilter : byte { Nearest, Linear, LinearMipmap }

/// <summary>2D texture (<c>osg::Texture2D</c>). Also used as a render target for render-to-texture cameras.</summary>
public sealed class Texture2D
{
    public Texture2D(Image image) => Image = image;

    /// <summary>Creates an empty render-target texture.</summary>
    public Texture2D(int width, int height)
    {
        RenderTargetWidth = width;
        RenderTargetHeight = height;
    }

    public Image? Image { get; set; }
    public int RenderTargetWidth { get; }
    public int RenderTargetHeight { get; }
    public bool IsRenderTarget => Image is null && RenderTargetWidth > 0;

    public TextureWrap Wrap { get; set; } = TextureWrap.Repeat;
    public TextureFilter Filter { get; set; } = TextureFilter.LinearMipmap;
    public long Id { get; } = Interlocked.Increment(ref s_id);
    private static long s_id;
}
