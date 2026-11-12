using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// State payload tracking Telegram polling offset.
/// </summary>
public sealed class MetroSyncState
{
    public long LastAcknowledgedUpdateId { get; set; }
}

/// <summary>
/// Crash-safe append-only history store for MetroSync messages.
/// Manages history.jsonl and atomic state.json tracking.
/// </summary>
public sealed class MetroSyncHistoryStore : IItemIndex, IDisposable
{
    private readonly string _directoryPath;
    private readonly string _historyPath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _indexLock = new();
    private readonly HashSet<long> _seenUpdateIds = new();
    private readonly HashSet<long> _seenMessageIds = new();
    private bool _isIndexLoaded;
    private bool _isDisposed;

    public string HistoryPath => _historyPath;
    public string StatePath => _statePath;

    public static MetroSyncHistoryStore Default { get; } = new();

    public MetroSyncHistoryStore(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync");
        _historyPath = Path.Combine(_directoryPath, "history.jsonl");
        _statePath = Path.Combine(_directoryPath, "state.json");
    }

    /// <summary>
    /// Fulfills IItemIndex contract: appends item if not already recorded.
    /// </summary>
    public async ValueTask<bool> UpsertAsync(MetroSyncMessageItem item, CancellationToken ct) =>
        await AppendAsync(item, ct).ConfigureAwait(false);

    /// <summary>
    /// Appends a message item to history.jsonl. Deduplicates by MessageId or UpdateId.
    /// Returns true if appended, or false if already recorded.
    /// </summary>
    public async Task<bool> AppendAsync(MetroSyncMessageItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken).ConfigureAwait(false);

            lock (_indexLock)
            {
                if (item.UpdateId > 0 && _seenUpdateIds.Contains(item.UpdateId))
                {
                    return false;
                }
                if (item.MessageId > 0 && _seenMessageIds.Contains(item.MessageId))
                {
                    return false;
                }
            }

            Directory.CreateDirectory(_directoryPath);

            string line = JsonSerializer.Serialize(item, WidgetJsonContext.Default.MetroSyncMessageItem);
            byte[] lineBytes = Encoding.UTF8.GetBytes(line + "\n");

            using (var stream = new FileStream(
                _historyPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true))
            {
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    int lastByte = stream.ReadByte();
                    if (lastByte != '\n' && lastByte != '\r')
                    {
                        byte[] newline = [(byte)'\n'];
                        await stream.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
                    }
                }
                stream.Seek(0, SeekOrigin.End);
                await stream.WriteAsync(lineBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_indexLock)
            {
                if (item.UpdateId > 0) _seenUpdateIds.Add(item.UpdateId);
                if (item.MessageId > 0) _seenMessageIds.Add(item.MessageId);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the latest N items from history.jsonl in chronological order.
    /// Skips corrupt or truncated lines gracefully. Deduplicates by UpdateId and MessageId.
    /// </summary>
    public async Task<IReadOnlyList<MetroSyncMessageItem>> LoadLatestAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return Array.Empty<MetroSyncMessageItem>();
        }

        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_historyPath))
            {
                lock (_indexLock) { _isIndexLoaded = true; }
                return Array.Empty<MetroSyncMessageItem>();
            }

            var allItems = new List<MetroSyncMessageItem>();
            var seenUpdateIdsInFile = new HashSet<long>();
            var seenMessageIdsInFile = new HashSet<long>();

            using (var stream = new FileStream(
                _historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var item = JsonSerializer.Deserialize(line, WidgetJsonContext.Default.MetroSyncMessageItem);
                        if (item == null) continue;

                        if (item.UpdateId > 0 && !seenUpdateIdsInFile.Add(item.UpdateId))
                        {
                            continue;
                        }
                        if (item.MessageId > 0 && !seenMessageIdsInFile.Add(item.MessageId))
                        {
                            continue;
                        }

                        allItems.Add(item);
                    }
                    catch (JsonException ex)
                    {
                        // Tolerates trailing truncated line on unexpected shutdown or corrupt line.
                        Safe.Ignore(ex, "MetroSyncHistoryStore.ReadRecentLine");
                        continue;
                    }
                }
            }

            lock (_indexLock)
            {
                foreach (var id in seenUpdateIdsInFile) _seenUpdateIds.Add(id);
                foreach (var id in seenMessageIdsInFile) _seenMessageIds.Add(id);
                _isIndexLoaded = true;
            }

            if (allItems.Count <= count)
            {
                return allItems;
            }

            return allItems.GetRange(allItems.Count - count, count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks if a message with the specified UpdateId or MessageId is already recorded.
    /// Thread-safe and eagerly verifies against disk index if not yet loaded into memory.
    /// </summary>
    public bool IsRecorded(long updateId, long messageId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_indexLock)
        {
            EnsureIndexLoadedSync();
            if (updateId > 0 && _seenUpdateIds.Contains(updateId)) return true;
            if (messageId > 0 && _seenMessageIds.Contains(messageId)) return true;
            return false;
        }
    }

    /// <summary>
    /// Saves LastAcknowledgedUpdateId atomically to state.json via a temporary file swap.
    /// </summary>
    public async Task SaveLastAcknowledgedUpdateIdAsync(long updateId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directoryPath);

            var state = new MetroSyncState { LastAcknowledgedUpdateId = updateId };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, MetroSyncHistoryStoreJsonContext.Default.MetroSyncState);

            string tmpPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tmpPath, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch (Exception ex) { Safe.Ignore(ex, "MetroSyncHistoryStore.DeleteTmpState"); }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the LastAcknowledgedUpdateId from state.json, or returns 0 if state is missing or corrupt.
    /// </summary>
    public async Task<long> LoadLastAcknowledgedUpdateIdAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_statePath))
            {
                return 0;
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(_statePath, cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0) return 0;
                var state = JsonSerializer.Deserialize(bytes, MetroSyncHistoryStoreJsonContext.Default.MetroSyncState);
                return state?.LastAcknowledgedUpdateId ?? 0;
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "MetroSyncHistoryStore.LoadLastAcknowledgedUpdateId");
                return 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes state.json (used during disconnect) and cleans up orphaned state temporary files.
    /// </summary>
    public void DeleteState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "MetroSyncHistoryStore.DeleteState");
        }

        try
        {
            if (Directory.Exists(_directoryPath))
            {
                foreach (var file in Directory.EnumerateFiles(_directoryPath, "state.json*.tmp"))
                {
                    try { File.Delete(file); } catch (Exception ex) { Safe.Ignore(ex, "MetroSyncHistoryStore.DeleteTmpStateFile"); }
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "MetroSyncHistoryStore.EnumerateTmpFiles");
        }
    }

    private void EnsureIndexLoadedSync()
    {
        if (_isIndexLoaded) return;
        _isIndexLoaded = true;

        if (!File.Exists(_historyPath)) return;

        try
        {
            using var stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize(line, WidgetJsonContext.Default.MetroSyncMessageItem);
                    if (item != null)
                    {
                        if (item.UpdateId > 0) _seenUpdateIds.Add(item.UpdateId);
                        if (item.MessageId > 0) _seenMessageIds.Add(item.MessageId);
                    }
                }
                catch (JsonException ex)
                {
                    Safe.Ignore(ex, "MetroSyncHistoryStore.EnsureIndexLoadedSyncLine");
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSyncHistoryStore.EnsureIndexLoadedSync");
        }
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
    {
        lock (_indexLock)
        {
            if (_isIndexLoaded) return;
        }

        if (!File.Exists(_historyPath))
        {
            lock (_indexLock) { _isIndexLoaded = true; }
            return;
        }

        try
        {
            using var stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            var updateIds = new List<long>();
            var messageIds = new List<long>();

            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize(line, WidgetJsonContext.Default.MetroSyncMessageItem);
                    if (item != null)
                    {
                        if (item.UpdateId > 0) updateIds.Add(item.UpdateId);
                        if (item.MessageId > 0) messageIds.Add(item.MessageId);
                    }
                }
                catch (JsonException ex)
                {
                    // Ignore corrupt/truncated line
                    Safe.Ignore(ex, "MetroSyncHistoryStore.EnsureIndexLoadedAsyncLine");
                }
            }

            lock (_indexLock)
            {
                foreach (var id in updateIds) _seenUpdateIds.Add(id);
                foreach (var id in messageIds) _seenMessageIds.Add(id);
                _isIndexLoaded = true;
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSyncHistoryStore.EnsureIndexLoadedAsync");
            lock (_indexLock) { _isIndexLoaded = true; }
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _gate.Dispose();
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetroSyncState))]
internal sealed partial class MetroSyncHistoryStoreJsonContext : JsonSerializerContext
{
}
