namespace AzureBackup.Core.Volumes;

/// <summary>
/// A write-only stream that splits whatever is written into fixed-size volume files
/// <c>{dir}/{baseName}.{n}</c> (n from 0). Used as the encryption sink so a pack's
/// ciphertext is volumized as it is produced.
/// </summary>
public sealed class VolumeWriter : Stream
{
    private readonly string _dir;
    private readonly string _baseName;
    private readonly long _volumeSize;
    private readonly List<string> _paths = [];

    private FileStream? _current;
    private long _currentLen;
    private int _index;

    public VolumeWriter(string dir, string baseName, long volumeSize)
    {
        if (volumeSize <= 0) throw new ArgumentOutOfRangeException(nameof(volumeSize));
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
        _volumeSize = volumeSize;
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<string> VolumePaths => _paths;
    public long TotalBytesWritten { get; private set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        while (count > 0)
        {
            EnsureOpen();
            int room = (int)Math.Min(count, _volumeSize - _currentLen);
            _current!.Write(buffer, offset, room);
            _currentLen += room;
            TotalBytesWritten += room;
            offset += room;
            count -= room;
            if (_currentLen >= _volumeSize)
                Roll();
        }
    }

    private void EnsureOpen()
    {
        if (_current is not null) return;
        string path = Path.Combine(_dir, $"{_baseName}.{_index}");
        _current = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _paths.Add(path);
        _currentLen = 0;
    }

    private void Roll()
    {
        _current?.Dispose();
        _current = null;
        _index++;
    }

    public override void Flush() => _current?.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _current = null;
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
