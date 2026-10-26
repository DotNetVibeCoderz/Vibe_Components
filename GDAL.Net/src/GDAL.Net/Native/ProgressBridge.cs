using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GDAL.Net.Native;

/// <summary>
/// Bridges GDAL's <c>GDALProgressFunc</c> to <see cref="IProgress{T}"/> and <see cref="CancellationToken"/>.
/// Uses a single static unmanaged callback (no delegate allocation, no GC-rooted thunks).
/// </summary>
internal sealed unsafe class ProgressBridge : IDisposable
{
    private readonly IProgress<GdalProgress>? _progress;
    private readonly CancellationToken _cancellationToken;
    private GCHandle _self;

    private ProgressBridge(IProgress<GdalProgress>? progress, CancellationToken cancellationToken)
    {
        _progress = progress;
        _cancellationToken = cancellationToken;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Creates a bridge, or returns null when neither progress nor cancellation is requested.</summary>
    public static ProgressBridge? Create(IProgress<GdalProgress>? progress, CancellationToken cancellationToken)
        => progress is null && !cancellationToken.CanBeCanceled ? null : new ProgressBridge(progress, cancellationToken);

    public static nint Function(ProgressBridge? bridge)
        => bridge is null ? 0 : (nint)(delegate* unmanaged[Cdecl]<double, byte*, nint, int>)&Callback;

    public static nint Data(ProgressBridge? bridge) => bridge is null ? 0 : GCHandle.ToIntPtr(bridge._self);

    public bool WasCancelled { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(double complete, byte* message, nint data)
    {
        try
        {
            if (data == 0) return 1;
            var bridge = (ProgressBridge)GCHandle.FromIntPtr(data).Target!;
            if (bridge._cancellationToken.IsCancellationRequested)
            {
                bridge.WasCancelled = true;
                return 0;
            }
            if (bridge._progress is not null)
            {
                var text = message == null ? null : Marshal.PtrToStringUTF8((nint)message);
                bridge._progress.Report(new GdalProgress(Math.Clamp(complete, 0, 1), string.IsNullOrEmpty(text) ? null : text));
            }
            return 1;
        }
        catch
        {
            // Never let a managed exception cross the native boundary.
            return 1;
        }
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> if the operation was interrupted by the token.</summary>
    public static void ThrowIfCancelled(ProgressBridge? bridge, CancellationToken token)
    {
        if (bridge is { WasCancelled: true } || token.IsCancellationRequested)
        {
            GdalNative.CPLErrorReset();
            throw new OperationCanceledException(token);
        }
    }

    public void Dispose()
    {
        if (_self.IsAllocated) _self.Free();
    }
}
