using System.Runtime.InteropServices;

namespace GDAL.Net.Native;

/// <summary>
/// Single SafeHandle type used for every GDAL/OGR/OSR native object.
/// <para>
/// Owned handles carry a release function that runs exactly once (on Dispose or finalization).
/// Borrowed handles (bands, layers, geometries owned by a feature, ...) carry no release function
/// but keep a strong reference to their parent handle, so the owning object can never be finalized
/// while a child is still reachable — and every P/Invoke that takes a <see cref="GdalSafeHandle"/>
/// keeps the whole chain alive for the duration of the native call.
/// </para>
/// </summary>
internal sealed unsafe class GdalSafeHandle : SafeHandle
{
    private readonly delegate*<nint, void> _release;
    private readonly delegate*<nint, nint, void> _releaseWithParent;

    public GdalSafeHandle() : base(0, true) { }

    public GdalSafeHandle(nint handle, delegate*<nint, void> release, GdalSafeHandle? parent = null) : base(0, true)
    {
        _release = release;
        Parent = parent;
        SetHandle(handle);
        if (release == null)
        {
            // Borrowed handles never free anything; no need to pay for finalization.
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Owned handle whose release function also needs the parent's native handle (e.g. SQL result sets).</summary>
    public GdalSafeHandle(nint handle, delegate*<nint, nint, void> releaseWithParent, GdalSafeHandle parent) : base(0, true)
    {
        _releaseWithParent = releaseWithParent;
        Parent = parent;
        SetHandle(handle);
    }

    /// <summary>The handle that owns this one (null for root/owned objects).</summary>
    public GdalSafeHandle? Parent { get; }

    public bool IsOwned => _release != null || _releaseWithParent != null;

    public override bool IsInvalid => handle == 0;

    /// <summary>True when this handle or any of its ancestors has been closed.</summary>
    public bool IsDead
    {
        get
        {
            for (var h = this; h is not null; h = h.Parent)
            {
                if (h.IsClosed) return true;
            }
            return false;
        }
    }

    /// <summary>Gives up ownership: the native object will no longer be released by this handle.</summary>
    internal nint Detach()
    {
        var h = handle;
        SetHandleAsInvalid();
        GC.SuppressFinalize(this);
        return h;
    }

    protected override bool ReleaseHandle()
    {
        if (handle == 0) return true;
        if (_release != null)
        {
            _release(handle);
        }
        else if (_releaseWithParent != null && Parent is { IsClosed: false } parent)
        {
            // If the parent is already closed, the native object died with it.
            _releaseWithParent(handle, parent.DangerousGetHandle());
        }
        return true;
    }

    public static GdalSafeHandle Borrowed(nint handle, GdalSafeHandle parent) => new(handle, (delegate*<nint, void>)null, parent);
}

/// <summary>Base class for every managed wrapper around a native GDAL object.</summary>
public abstract class GdalObject : IDisposable
{
    private readonly GdalSafeHandle _handle;

    internal GdalObject(GdalSafeHandle handle)
    {
        _handle = handle;
    }

    /// <summary>The native handle, validated (throws <see cref="ObjectDisposedException"/> if this object or its owner was disposed).</summary>
    internal GdalSafeHandle Handle
    {
        get
        {
            if (_handle.IsDead)
            {
                ThrowDisposed();
            }
            return _handle;
        }
    }

    /// <summary>Raw native pointer (advanced interop scenarios). Valid only while this object is alive.</summary>
    public nint NativePointer => Handle.DangerousGetHandle();

    /// <summary>True once this object (or the object that owns it) has been disposed.</summary>
    public bool IsDisposed => _handle.IsDead;

    internal GdalSafeHandle RawHandle => _handle;

    private void ThrowDisposed() => throw new ObjectDisposedException(GetType().Name, "The native GDAL object (or its owning dataset/feature) has been disposed.");

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
