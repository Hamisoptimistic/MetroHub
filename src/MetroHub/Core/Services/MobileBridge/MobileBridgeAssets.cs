using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>
/// Serves the phone web app from embedded resources.
/// Nothing is read from disk at runtime, so the bridge works identically from a build output,
/// a published folder, or a single-file publish, and cannot be tampered with by editing files next to the exe.
/// </summary>
public static class MobileBridgeAssets
{
    private const string ResourceFolder = ".Assets.MobileBridge.";

    private static readonly Lazy<Dictionary<string, AssetEntry>> _assets = new(Load);

    /// <summary>Look up a file by its request path (for example <c>/m/app.css</c>).</summary>
    public static bool TryGet(string requestPath, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "application/octet-stream";

        string fileName = NormalizeFileName(requestPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        if (!_assets.Value.TryGetValue(fileName, out AssetEntry? entry))
        {
            return false;
        }

        bytes = entry.Bytes;
        contentType = entry.ContentType;
        return true;
    }

    public static bool HasAsset(string requestPath) => _assets.Value.ContainsKey(NormalizeFileName(requestPath));

    private static string NormalizeFileName(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return string.Empty;
        }

        int slash = requestPath.LastIndexOf('/');
        string name = slash >= 0 ? requestPath[(slash + 1)..] : requestPath;

        // Only a bare file name is ever used as a key, so directory traversal is structurally impossible.
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return name.ToLowerInvariant();
    }

    private static Dictionary<string, AssetEntry> Load()
    {
        var map = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);

        try
        {
            Assembly assembly = typeof(MobileBridgeAssets).Assembly;
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                int marker = resourceName.IndexOf(ResourceFolder, StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }

                string fileName = resourceName[(marker + ResourceFolder.Length)..];
                if (fileName.Length == 0)
                {
                    continue;
                }

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    continue;
                }

                using var buffer = new MemoryStream((int)stream.Length);
                stream.CopyTo(buffer);
                map[fileName.ToLowerInvariant()] = new AssetEntry(buffer.ToArray(), ContentTypeFor(fileName));
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Failed to load embedded phone assets");
        }

        if (map.Count == 0)
        {
            Safe.Log("[MobileBridge] No embedded phone assets were found. The phone page will not load.", new InvalidOperationException("Missing Assets/MobileBridge content"));
        }

        return map;
    }

    private static string ContentTypeFor(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".webmanifest" => "application/manifest+json; charset=utf-8",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    private sealed record AssetEntry(byte[] Bytes, string ContentType);

    /// <summary>Sorted asset names, used by tests to assert the shell shipped intact.</summary>
    public static IReadOnlyList<string> Names => _assets.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}
