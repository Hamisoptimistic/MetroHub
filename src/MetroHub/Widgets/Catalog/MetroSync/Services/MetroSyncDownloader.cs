using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Handles downloading files from Telegram Bot API with 20 MB size limit enforcement,
/// filename sanitization, collision suffixing, and atomic .tmp file replacement.
/// </summary>
public sealed class MetroSyncDownloader
{
    public const long MaxDownloadSizeBytes = 20 * 1024 * 1024; // 20 MB Telegram bot limit

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private readonly ITelegramTransport _transport;
    private readonly string _downloadsDir;

    public string DownloadsDirectory => _downloadsDir;

    public MetroSyncDownloader(ITelegramTransport transport, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        var baseDir = baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync");
        _downloadsDir = Path.Combine(baseDir, "Downloads");
        Directory.CreateDirectory(_downloadsDir);
    }

    /// <summary>
    /// Sanitizes an untrusted raw filename to prevent directory traversal, removes invalid chars,
    /// strips bidirectional override and hidden control chars, prefixes reserved device names,
    /// and caps total length while preserving the extension.
    /// </summary>
    public static string SanitizeFileName(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "file";
        }

        var name = rawFileName.Trim();
        name = name.Replace('/', Path.DirectorySeparatorChar);
        name = Path.GetFileName(name);

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Strip invalid filename chars, control chars, and Unicode bidi formatting overrides (\u202A-\u202E, \u200B-\u200F)
            if (invalidChars.Contains(c) || char.IsControl(c) || c < 32 ||
                (c >= 0x200B && c <= 0x200F) ||
                (c >= 0x202A && c <= 0x202E))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        name = sb.ToString();

        name = name.Replace("..", "_");
        name = name.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name) || name.Trim(' ', '.', '_').Length == 0)
        {
            return "file";
        }

        var baseNameWithoutExt = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        if (IsReservedDeviceName(baseNameWithoutExt))
        {
            name = "_" + name;
            baseNameWithoutExt = Path.GetFileNameWithoutExtension(name);
        }

        const int maxTotalLength = 120;
        if (name.Length > maxTotalLength)
        {
            if (ext.Length > 20)
            {
                ext = ext[..20].TrimEnd(' ', '.');
            }

            int maxBaseLength = Math.Max(1, maxTotalLength - ext.Length);
            if (baseNameWithoutExt.Length > maxBaseLength)
            {
                baseNameWithoutExt = baseNameWithoutExt[..maxBaseLength].TrimEnd(' ', '.');
            }
            name = baseNameWithoutExt + ext;

            if (name.Length > maxTotalLength)
            {
                name = name[..maxTotalLength].TrimEnd(' ', '.');
            }
        }

        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    private static bool IsReservedDeviceName(string name)
    {
        foreach (var r in ReservedDeviceNames)
        {
            if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes a unique destination path in the Downloads directory, appending (1), (2)... if a collision occurs.
    /// Guarantees the destination is strictly contained within the Downloads directory.
    /// </summary>
    public string GetUniqueDestinationPath(string sanitizedFileName)
    {
        var target = Path.Combine(_downloadsDir, sanitizedFileName);
        EnsureContainedInDownloads(target);

        if (!File.Exists(target))
        {
            return target;
        }

        var baseName = Path.GetFileNameWithoutExtension(sanitizedFileName);
        var ext = Path.GetExtension(sanitizedFileName);

        for (int i = 1; i <= 10000; i++)
        {
            var candidateName = $"{baseName} ({i}){ext}";
            var candidatePath = Path.Combine(_downloadsDir, candidateName);
            EnsureContainedInDownloads(candidatePath);

            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        var fallbackPath = Path.Combine(_downloadsDir, $"{baseName}_{Guid.NewGuid():N}{ext}");
        EnsureContainedInDownloads(fallbackPath);
        return fallbackPath;
    }

    private void EnsureContainedInDownloads(string fullPath)
    {
        var normalizedTarget = Path.GetFullPath(fullPath);
        var normalizedDir = Path.GetFullPath(_downloadsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!normalizedTarget.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Target file path escapes the MetroSync Downloads directory.");
        }
    }

    /// <summary>
    /// Downloads a file by file ID, enforcing the 20 MB limit, sanitizing filename,
    /// streaming into an atomic .tmp file, and moving it to Downloads upon completion.
    /// </summary>
    public async Task<string> DownloadFileAsync(
        string fileId,
        string rawFileName,
        long? declaredFileSize = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        if (declaredFileSize.HasValue && declaredFileSize.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredFileSize), "Declared file size cannot be negative.");
        }

        // Pre-check size limit before making any network calls
        if (declaredFileSize.HasValue && declaredFileSize.Value > MaxDownloadSizeBytes)
        {
            throw new FileTooLargeException(MaxDownloadSizeBytes);
        }

        var fileInfo = await _transport.GetFileAsync(fileId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fileInfo.FilePath))
        {
            throw new InvalidOperationException("Telegram did not provide a download path for the requested file.");
        }

        if (fileInfo.FileSize.HasValue && fileInfo.FileSize.Value > MaxDownloadSizeBytes)
        {
            throw new FileTooLargeException(MaxDownloadSizeBytes);
        }

        Directory.CreateDirectory(_downloadsDir);

        var effectiveName = string.IsNullOrWhiteSpace(rawFileName)
            ? Path.GetFileName(fileInfo.FilePath)
            : rawFileName;

        var safeName = SanitizeFileName(effectiveName);
        var destinationPath = GetUniqueDestinationPath(safeName);

        var tmpPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long expectedSize = fileInfo.FileSize ?? declaredFileSize ?? 0;

        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            await using (var progressStream = new ProgressReportingStream(fs, expectedSize, progress))
            {
                await _transport.DownloadAsync(fileInfo.FilePath, progressStream, MaxDownloadSizeBytes, ct).ConfigureAwait(false);
            }

            // In case destinationPath was taken by another operation during the download
            destinationPath = GetUniqueDestinationPath(safeName);
            File.Move(tmpPath, destinationPath, overwrite: false);
            progress?.Report(1.0);
            return destinationPath;
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* best effort cleanup */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Downloads a FilePayloadItem, updating DownloadProgress and LocalPath upon completion.
    /// </summary>
    public async Task<string> DownloadFileAsync(
        FilePayloadItem item,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.FileId))
        {
            throw new ArgumentException("Item does not have a valid FileId.", nameof(item));
        }

        item.IsDownloading = true;
        item.DownloadProgress = 0.0;

        var forwarder = new Progress<double>(p =>
        {
            item.DownloadProgress = p;
            progress?.Report(p);
        });

        try
        {
            var path = await DownloadFileAsync(item.FileId, item.FileName, item.FileSize, forwarder, ct).ConfigureAwait(false);
            item.LocalPath = path;
            item.DownloadProgress = 1.0;
            return path;
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    private sealed class ProgressReportingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private readonly IProgress<double>? _progress;
        private long _bytesWritten;

        public ProgressReportingStream(Stream inner, long expectedLength, IProgress<double>? progress)
        {
            _inner = inner;
            _expectedLength = expectedLength;
            _progress = progress;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Report(count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
            Report(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            Report(buffer.Length);
        }

        private void Report(int count)
        {
            _bytesWritten += count;
            if (_expectedLength > 0 && _progress is not null)
            {
                var frac = Math.Clamp((double)_bytesWritten / _expectedLength, 0.0, 1.0);
                _progress.Report(frac);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
