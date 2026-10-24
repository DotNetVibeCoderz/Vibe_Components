using GnuRadio.Sinks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GnuRadio.Blazor;

/// <summary>A tick for the canvas renderer: position 0…1 along the axis and its label.</summary>
public sealed record GrTick(double P, string L, bool Strong = false);

/// <summary>A vertical marker line at position 0…1 with an optional label.</summary>
public sealed record GrMarker(double P, string? L = null, string? Color = null, bool Dashed = false, int Row = 0);

/// <summary>Builds axis ticks for the renderers.</summary>
public static class GrTicks
{
    public static GrTick[] Frequency(double start, double end, int count = 8) =>
        [.. Axis.NiceTicks(start, end, count).Select(t => new GrTick((t - start) / (end - start), Axis.FormatFrequency(t, t % 1e6 == 0 ? 0 : 3)))];

    public static GrTick[] Linear(double min, double max, int count = 5, string unit = "") =>
        [.. Axis.NiceTicks(min, max, count).Select(t => new GrTick((t - min) / (max - min), Axis.Format(t) + unit, t == 0))];

    public static GrTick[] Time(double seconds, int count = 6) =>
        [.. Axis.NiceTicks(0, seconds, count).Select(t => new GrTick(t / seconds, Axis.FormatTime(t)))];

    public static GrTick[] LogDecades(double min, double max)
    {
        var lo = Math.Log10(min);
        var hi = Math.Log10(max);
        var ticks = new List<GrTick>();
        for (var e = (int)Math.Ceiling(lo); e <= (int)Math.Floor(hi); e++)
            ticks.Add(new GrTick((e - lo) / (hi - lo), $"1e{e}"));
        return [.. ticks];
    }
}

/// <summary>
/// Base of the canvas-backed instrument displays: loads the JS module, attaches the canvas, and polls the
/// sink at <see cref="Fps"/>. Rendering runs through the component's synchronization context and only sends data
/// when the sink's version changed, so idle displays cost nothing.
/// </summary>
public abstract class GrCanvasComponent : ComponentBase, IAsyncDisposable
{
    private const string ModulePath = "./_content/GnuRadio.Net.Blazor/gnuradio.js";
    private readonly CancellationTokenSource _cts = new();
    private DotNetObjectReference<GrCanvasComponent>? _self;
    private Task? _loop;

    [Inject] protected IJSRuntime JS { get; set; } = null!;

    /// <summary>Panel title shown above the display.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>CSS height of the display, e.g. "260px".</summary>
    [Parameter] public string Height { get; set; } = "240px";

    [Parameter] public string? Class { get; set; }

    /// <summary>Maximum redraws per second.</summary>
    [Parameter] public double Fps { get; set; } = 30;

    protected ElementReference Canvas;
    protected IJSObjectReference? Module { get; private set; }

    /// <summary>True when the component wants click positions (<see cref="OnCanvasClickAsync"/>).</summary>
    protected virtual bool Interactive => false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            Module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            _self = DotNetObjectReference.Create(this);
            await Module.InvokeVoidAsync("attach", Canvas, Interactive ? _self : null);
            _loop = RenderLoop();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerendering: JS is not available yet; the interactive render will attach.
        }
    }

    private async Task RenderLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, Fps)));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
                await InvokeAsync(async () =>
                {
                    if (Module is not null) await DrawAsync(Module, force: false);
                });
        }
        catch (OperationCanceledException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Draws a frame if there is new data (or always when <paramref name="force"/>).</summary>
    protected abstract ValueTask DrawAsync(IJSObjectReference module, bool force);

    /// <summary>
    /// Copies floats into a reusable byte buffer for JS interop (Blazor sends byte[] as a binary Uint8Array),
    /// growing it only when the size changes.
    /// </summary>
    protected static byte[] ToBytes(ReadOnlySpan<float> data, ref byte[] buffer)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data);
        if (buffer.Length != bytes.Length) buffer = new byte[bytes.Length];
        bytes.CopyTo(buffer);
        return buffer;
    }

    /// <summary>Forces a redraw on the next tick (after a parameter change).</summary>
    protected async Task RedrawAsync()
    {
        if (Module is not null) await DrawAsync(Module, force: true);
    }

    [JSInvokable]
    public Task OnCanvasClick(double x, double y) => OnCanvasClickAsync(x, y);

    /// <summary>Click at plot-relative coordinates (0…1, 0 = left/top).</summary>
    protected virtual Task OnCanvasClickAsync(double x, double y) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_loop is not null) await _loop;
            if (Module is not null)
            {
                await Module.InvokeVoidAsync("detach", Canvas);
                await Module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        _self?.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
