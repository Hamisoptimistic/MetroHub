using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MetroHub.Core.Services;

/// <summary>
/// Contract for per-widget state files. State-carrying widgets (e.g. Habit) keep their
/// bulk payload here instead of inside <c>layout.json</c>, so frequent updates
/// never rewrite the whole hub layout.
/// </summary>
public interface IWidgetStateStore
{
    /// <summary>Returns the stored JSON, falling back to the rollover copy. Null when neither exists.</summary>
    string? Read(string widgetId, string tileId);

    /// <summary>Atomically writes the JSON, rotating the previous content into the rollover copy.</summary>
    void Write(string widgetId, string tileId, string json);

    /// <summary>Removes the state file and its rollover copy.</summary>
    void Delete(string widgetId, string tileId);

    /// <summary>Deletes state files for tiles that are no longer part of the layout.</summary>
    void PruneExcept(string widgetId, IReadOnlyCollection<string> knownTileIds);

    /// <summary>Prunes every widget's state directory against the supplied tile ids.</summary>
    void PruneAllExcept(IReadOnlyCollection<string> knownTileIds);
}

/// <summary>
/// File-backed store for widget-owned state, one file per widget instance:
/// <c>%LocalAppData%\MetroHub\config\widgets\{widgetId}\{tileId}.json</c> with the rollover copy at
/// <c>backups\widgets\{widgetId}\{tileId}.json.bak</c>.
/// <para>
/// Deliberately lighter than <see cref="StorageService"/>'s layout writer: a state file is an
/// autosave mirror (temp file + atomic move, no forced flush), not the hub's durable layout
/// record, so it does not pay <c>FlushFileBuffers</c> on every keystroke pause.
/// </para>
/// <para>
/// Files are keyed by <c>TileModel.Id</c>, so two instances of the same widget never share a
/// file and deleting one never touches the other. Pruning runs at startup only, which keeps
/// same-session undo/unpin recoverable.
/// </para>
/// </summary>
public sealed class WidgetStateStore : IWidgetStateStore
{
    /// <summary>Process-wide default instance (plain file IO, no UI dependencies).</summary>
    public static WidgetStateStore Default { get; } = new();

    private static readonly object Gate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _rootDir;
    private readonly string _bakRootDir;

    public WidgetStateStore()
        : this(AppPaths.WidgetStateDir, AppPaths.WidgetStateBakDir)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary root instead of %LocalAppData%.</summary>
    public WidgetStateStore(string rootDir, string bakRootDir)
    {
        _rootDir = rootDir;
        _bakRootDir = bakRootDir;
    }

    public string? Read(string widgetId, string tileId)
    {
        return TryReadFile(PathFor(widgetId, tileId)) ?? TryReadFile(BakFor(widgetId, tileId));
    }

    public void Write(string widgetId, string tileId, string json)
    {
        string path = PathFor(widgetId, tileId);
        string bak = BakFor(widgetId, tileId);
        string tmp = path + ".tmp";
        lock (Gate)
        {
            try
            {
                AppPaths.EnsureDirectory(path);
                AppPaths.EnsureDirectory(bak);

                File.WriteAllText(tmp, json, Utf8NoBom);

                if (File.Exists(path))
                {
                    try
                    {
                        // Rotates the previous content into .bak in one coordinated OS call.
                        File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Safe.Logger(ex, $"WidgetStateStore.Write.ReplaceFallback({path})");
                        // Non-NTFS / locked target: fall back to a manual rotate.
                        Safe.Try(() => File.Copy(path, bak, overwrite: true), context: $"WidgetStateStore.Write.CopyBakFallback({path})");
                    }
                }

                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Safe.Logger(ex, $"WidgetStateStore.Write({path})");
                System.Diagnostics.Debug.WriteLine($"[WidgetStateStore] Write failed for {path}: {ex.Message}");
            }
            finally
            {
                Safe.Try(() => { if (File.Exists(tmp)) File.Delete(tmp); }, context: $"WidgetStateStore.Write.DeleteTmp({tmp})");
            }
        }
    }

    public void Delete(string widgetId, string tileId)
    {
        foreach (string path in new[] { PathFor(widgetId, tileId), BakFor(widgetId, tileId), PathFor(widgetId, tileId) + ".tmp" })
        {
            Safe.Try(() => { if (File.Exists(path)) File.Delete(path); }, context: $"WidgetStateStore.Delete({path})");
        }
    }

    public void PruneExcept(string widgetId, IReadOnlyCollection<string> knownTileIds)
    {
        Safe.Try(() =>
        {
            string dir = DirectoryFor(widgetId);
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (knownTileIds.Contains(stem)) continue;
                Delete(widgetId, stem);
            }
        }, context: $"WidgetStateStore.PruneExcept({widgetId})");
    }

    public void PruneAllExcept(IReadOnlyCollection<string> knownTileIds)
    {
        Safe.Try(() =>
        {
            if (!Directory.Exists(_rootDir)) return;
            foreach (string dir in Directory.EnumerateDirectories(_rootDir))
            {
                PruneExcept(Path.GetFileName(dir), knownTileIds);
            }
        }, context: "WidgetStateStore.PruneAllExcept");
    }

    private static string? TryReadFile(string path)
    {
        return Safe.Try(() =>
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 2) return null; // "{}" or empty — not usable state.
            string text = File.ReadAllText(path, Utf8NoBom);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }, fallback: null, context: $"WidgetStateStore.TryReadFile({path})");
    }

    private string DirectoryFor(string widgetId)
        => Path.Combine(_rootDir, Sanitize(widgetId));

    private string PathFor(string widgetId, string tileId)
        => Path.Combine(DirectoryFor(widgetId), Sanitize(tileId) + ".json");

    private string BakFor(string widgetId, string tileId)
        => Path.Combine(_bakRootDir, Sanitize(widgetId), Sanitize(tileId) + ".json.bak");

    /// <summary>File-name safety: ids are registry strings / GUID "N" values, but never trust them blindly.</summary>
    private static string Sanitize(string token)
    {
        if (string.IsNullOrEmpty(token)) return "unknown";
        var sb = new StringBuilder(token.Length);
        foreach (char c in token)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
