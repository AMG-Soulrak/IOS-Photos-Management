using System.Diagnostics;
using System.Security.Cryptography;

namespace IPhonePhotos.Core;

/// <summary>
/// Stream de solo escritura que calcula el SHA-256 de lo que pasa por él, cuenta bytes,
/// avisa del progreso y (opcionalmente) reenvía los datos a otro stream.
/// Así el hash se obtiene durante la copia, sin releer el archivo.
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream? _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Action<long>? _onBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReportMs = -1000;
    private long _written;
    private string? _result;

    public HashingWriteStream(Stream? inner, Action<long>? onBytes = null)
    {
        _inner = inner;
        _onBytes = onBytes;
    }

    public long BytesWritten => _written;

    /// <summary>Hash final en hexadecimal minúscula. Se puede pedir una sola vez, al terminar.</summary>
    public string GetHashHex()
    {
        _result ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        return _result;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner?.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) return;
        _hash.AppendData(buffer);
        _inner?.Write(buffer);
        Advance(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;
        _hash.AppendData(buffer.Span);
        if (_inner != null) await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Advance(buffer.Length);
    }

    private void Advance(int count)
    {
        _written += count;
        if (_onBytes == null) return;
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastReportMs >= 150)
        {
            _lastReportMs = now;
            _onBytes(_written);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

public static class FileHasher
{
    /// <summary>SHA-256 de un archivo local, en hexadecimal minúscula.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            sha.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
