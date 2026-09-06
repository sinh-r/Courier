using System.IO.MemoryMappedFiles;

namespace Courier.Core.Rendering;

/// <summary>
/// The bytes of a response body, wherever they physically are.
/// </summary>
/// <remarks>
/// TECH_SPEC 3.7: bodies over a threshold stream to a temp file and render from a memory-mapped
/// view. Callers do not need to know which case they have — they ask for a span and get one, and a
/// 40MB body never occupies 40MB of managed heap.
/// </remarks>
public sealed unsafe class ResponseBuffer : IDisposable
{
    private readonly byte[]? _inMemory;
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte* _pointer;
    private readonly string? _path;
    private readonly bool _deleteOnDispose;
    private bool _disposed;

    private ResponseBuffer(byte[] bytes)
    {
        _inMemory = bytes;
        Length = bytes.LongLength;
    }

    private ResponseBuffer(string path, bool deleteOnDispose)
    {
        _path = path;
        _deleteOnDispose = deleteOnDispose;
        Length = new FileInfo(path).Length;

        if (Length == 0)
        {
            _inMemory = [];
            return;
        }

        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        _view = _map.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public long Length { get; }

    /// <summary>True when the body was spilled to disk rather than held in memory.</summary>
    public bool IsMapped => _view is not null;

    public static ResponseBuffer FromBytes(byte[] bytes) => new(bytes);

    /// <param name="deleteOnDispose">
    /// True for a body Courier spilled into its own cache; false for a file the user chose.
    /// </param>
    public static ResponseBuffer FromFile(string path, bool deleteOnDispose = true) => new(path, deleteOnDispose);

    /// <summary>
    /// A view over the bytes. Spans cannot exceed int.MaxValue, so a body larger than 2GB is
    /// exposed in windows; nothing in Courier renders more than a few megabytes at once anyway.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan(long offset = 0, int length = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var remaining = Length - offset;
        if (remaining <= 0)
        {
            return [];
        }

        var take = length < 0
            ? (int)Math.Min(remaining, int.MaxValue)
            : (int)Math.Min(length, remaining);

        if (_inMemory is not null)
        {
            return _inMemory.AsSpan((int)offset, take);
        }

        return new ReadOnlySpan<byte>(_pointer + offset, take);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }

        _map?.Dispose();

        if (_deleteOnDispose && _path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // The storage settings page can clear the cache; a stranded file is not worth
                // failing a dispose over.
            }
        }
    }
}
