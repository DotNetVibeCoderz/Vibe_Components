using System.IO.MemoryMappedFiles;

namespace GeoPandas.Net.IO;

/// <summary>
/// Read-only memory-mapped file exposing zero-copy spans at 64-bit offsets. Readers (FlatGeobuf, LAS, Arrow IPC) use it so
/// that opening a multi-gigabyte dataset costs nothing until pages are touched, and the OS page cache is shared between processes.
/// </summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _ptr;

    public MappedFile(string path)
    {
        Path = path;
        Length = new FileInfo(path).Length;
        if (Length == 0) return;
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
    }

    public string Path { get; }
    public long Length { get; }

    /// <summary>A span over [offset, offset + length). Valid until the file is disposed.</summary>
    public ReadOnlySpan<byte> Span(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Range {offset}+{length} is outside the file ({Length} bytes).");
        return length == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_ptr + offset, length);
    }

    /// <summary>A read-only stream over the exact file length (memory-mapped views are otherwise rounded up to the page size).</summary>
    public Stream OpenStream() => Length == 0 ? new MemoryStream([], false) : new UnmanagedMemoryStream(_ptr, Length, Length, FileAccess.Read);

    public void Dispose()
    {
        if (_view is not null && _ptr != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
    }
}
