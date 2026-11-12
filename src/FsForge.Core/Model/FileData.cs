namespace FsForge.Core;

/// <summary>Immutable content of a regular file. The stream is created on demand.</summary>
public interface IFileData
{
    long Length { get; }

    /// <summary>Opens a new forward-readable stream positioned at the start of the content.</summary>
    Stream OpenRead();
}

/// <summary>
/// Optional knowledge about holes in the content. Writers of formats that support sparse files
/// use it to size a new image without reading the data twice.
/// </summary>
public interface ISparseFileData : IFileData
{
    /// <summary>Upper bound of the number of bytes that are not known to be zero.</summary>
    long DataBytesHint { get; }
}

/// <summary>Content held in memory.</summary>
public sealed class MemoryFileData : IFileData
{
    private readonly byte[] _data;

    public MemoryFileData(byte[] data) => _data = data;

    public long Length => _data.Length;
    public Stream OpenRead() => new MemoryStream(_data, writable: false);
}

/// <summary>Content that lives in a file of the host filesystem and is read lazily.</summary>
public sealed class HostFileData : IFileData
{
    public HostFileData(string path)
    {
        HostPath = path;
        Length = new FileInfo(path).Length;
    }

    public string HostPath { get; }
    public long Length { get; }

    public Stream OpenRead()
    {
        FileStream fs;
        try
        {
            fs = new FileStream(HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FsException($"The added file '{HostPath}' can no longer be read: {ex.Message}", ex);
        }
        if (fs.Length != Length)
        {
            fs.Dispose();
            throw new FsException($"The file '{HostPath}' has changed since it was added to the image. Add it again.");
        }
        return fs;
    }
}

/// <summary>A byte range of a random-access source (temp spool, uncompressed container...).</summary>
public sealed class SliceFileData : IFileData
{
    private readonly IBlockSource _source;
    private readonly long _offset;

    public SliceFileData(IBlockSource source, long offset, long length)
    {
        _source = source;
        _offset = offset;
        Length = length;
    }

    public long Length { get; }
    public Stream OpenRead() => new BlockSourceStream(_source, _offset, Length);
}

/// <summary>
/// Append-only temporary file used to keep content that arrives as a one-pass stream
/// (tar archives). Slices of it stay readable until the spool is disposed.
/// </summary>
public sealed class TempSpool : IDisposable
{
    private readonly string _path;
    private FileStream? _writer;
    private FileBlockSource? _reader;
    private long _length;

    public TempSpool(string? directory = null)
    {
        directory = string.IsNullOrEmpty(directory) ? System.IO.Path.GetTempPath() : directory;
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "fsforge-" + Guid.NewGuid().ToString("N") + ".spool");
        _writer = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 20);
    }

    public long Length => _length;

    /// <summary>Copies exactly <paramref name="count"/> bytes from the stream and returns the slice.</summary>
    public IFileData Append(Stream source, long count)
    {
        FileStream w = _writer ?? throw new ObjectDisposedException(nameof(TempSpool));
        long start = _length;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 17);
        try
        {
            long left = count;
            while (left > 0)
            {
                int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new EndOfStreamException("Unexpected end of the input stream.");
                w.Write(buffer, 0, n);
                left -= n;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        _length += count;
        return new SpoolSlice(this, start, count);
    }

    private FileBlockSource Reader()
    {
        lock (this)
        {
            _writer?.Flush();
            return _reader ??= new FileBlockSource(_path, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    private sealed class SpoolSlice(TempSpool spool, long offset, long length) : IFileData
    {
        public long Length => length;
        public Stream OpenRead() => new BlockSourceStream(spool.Reader(), offset, length);
    }

    public void Dispose()
    {
        lock (this)
        {
            _writer?.Dispose();
            _writer = null;
            _reader?.Dispose();
            _reader = null;
        }
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}

/// <summary>Progress report of a long running core operation.</summary>
public readonly record struct ProgressInfo(string Stage, long Done, long Total, string? Item = null)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}
