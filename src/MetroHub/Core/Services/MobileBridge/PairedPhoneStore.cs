using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetroHub.Core.Common;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>A phone that finished pairing and may reconnect without scanning a new QR code.</summary>
/// <remarks>
/// The pairing survives MetroHub restarts, so the phone stays paired until the desktop forgets it
/// or the phone unpairs itself. The device token itself never leaves memory.
/// </remarks>
public sealed record PairedPhone(
    string Id,
    string Label,
    DateTimeOffset PairedUtc,
    DateTimeOffset? LastSeenUtc);

/// <summary>On-disk row. Only the SHA-256 hash of the device token is written, never the token.</summary>
public sealed record PairedPhoneRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tokenHash")] string TokenHash,
    [property: JsonPropertyName("pairedUtc")] DateTimeOffset PairedUtc,
    [property: JsonPropertyName("lastSeenUtc")] DateTimeOffset? LastSeenUtc);

/// <summary>Persisted pairing list. Versioned so a future change can migrate instead of throwing.</summary>
public sealed record PairedPhoneDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("phones")] IReadOnlyList<PairedPhoneRow> Phones);

/// <summary>
/// Durable allow-list for the phone bridge: which phones may talk to this PC without a fresh QR scan.
/// </summary>
/// <remarks>
/// One entry per phone. A fresh 128-bit token is minted when a phone pairs, the token is handed to
/// the phone as a long-lived cookie, and only its hash is kept here. Entries are compared in constant
/// time and pruned oldest-first once <see cref="MaxDevices"/> phones are paired.
/// </remarks>
public sealed class PairedPhoneStore
{
    /// <summary>Upper bound on remembered phones. Pairing a new one beyond this evicts the oldest.</summary>
    public const int MaxDevices = 16;

    private const int DocumentVersion = 1;

    /// <summary>Last-seen timestamps are cosmetic, so they are not worth a disk write per request.</summary>
    private static readonly TimeSpan LastSeenFlushInterval = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly string _path;
    private readonly List<PairedPhoneEntry> _devices = new();

    private DateTimeOffset _lastFlushUtc = DateTimeOffset.MinValue;

    private PairedPhoneStore(string path)
    {
        _path = path;
    }

    /// <summary>Loads the pairing list from <paramref name="path"/>, or starts empty when it is absent.</summary>
    public static PairedPhoneStore Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var store = new PairedPhoneStore(path);
        store.LoadCore();
        return store;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>Snapshot of the paired phones, most recently paired first.</summary>
    public IReadOnlyList<PairedPhone> Devices
    {
        get
        {
            lock (_sync)
            {
                var snapshot = new PairedPhone[_devices.Count];
                for (int i = 0; i < _devices.Count; i++)
                {
                    snapshot[i] = _devices[i].Phone;
                }

                return snapshot;
            }
        }
    }

    /// <summary>
    /// Pairs a phone: mints a device token, remembers only its hash, and returns the token for the
    /// caller to hand to the phone exactly once.
    /// </summary>
    public (PairedPhone Device, string Token) Pair(string? label)
    {
        string token = MobileBridgeToken.Create().Value;
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            var device = new PairedPhone(
                Id: Guid.NewGuid().ToString("N")[..12],
                Label: SanitizeLabel(label),
                PairedUtc: now,
                LastSeenUtc: now);

            _devices.Insert(0, new PairedPhoneEntry(device, Hash(token)));

            while (_devices.Count > MaxDevices)
            {
                _devices.RemoveAt(_devices.Count - 1);
            }

            Flush(force: true);
            return (device, token);
        }
    }

    /// <summary>
    /// Authorizes a device token in constant time. Returns false for unknown, empty, or malformed tokens.
    /// </summary>
    public bool TryAuthorize(string? token, out PairedPhone device)
    {
        device = null!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] candidate = Encoding.ASCII.GetBytes(Hash(token));

        lock (_sync)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                byte[] stored = Encoding.ASCII.GetBytes(_devices[i].TokenHash);
                if (!CryptographicOperations.FixedTimeEquals(stored, candidate))
                {
                    continue;
                }

                PairedPhoneEntry entry = _devices[i];
                var seen = new PairedPhone(
                    entry.Phone.Id,
                    entry.Phone.Label,
                    entry.Phone.PairedUtc,
                    DateTimeOffset.UtcNow);

                _devices[i] = entry with { Phone = seen };
                MarkSeen(seen);
                device = seen;
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets one phone by id. Returns true when something was removed.</summary>
    public bool Forget(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_sync)
        {
            int removed = _devices.RemoveAll(entry => string.Equals(entry.Phone.Id, id, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            Flush(force: true);
            return true;
        }
    }

    /// <summary>Forgets the phone that owns <paramref name="token"/>. Used by the phone's own unpair action.</summary>
    public bool TryForgetByToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] candidate = Encoding.ASCII.GetBytes(Hash(token));

        lock (_sync)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                byte[] stored = Encoding.ASCII.GetBytes(_devices[i].TokenHash);
                if (!CryptographicOperations.FixedTimeEquals(stored, candidate))
                {
                    continue;
                }

                _devices.RemoveAt(i);
                Flush(force: true);
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets every paired phone. Returns how many pairings were dropped.</summary>
    public int ForgetAll()
    {
        lock (_sync)
        {
            int removed = _devices.Count;
            if (removed == 0)
            {
                return 0;
            }

            _devices.Clear();
            Flush(force: true);
            return removed;
        }
    }

    /// <summary>Human-readable label, never longer than 48 characters and never empty.</summary>
    private static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Phone";
        }

        string trimmed = label.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..48];
    }

    /// <summary>Lowercase SHA-256 of the token. Device tokens are 32 hex characters, so hashing is cheap.</summary>
    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    private void MarkSeen(PairedPhone device)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFlushUtc < LastSeenFlushInterval)
        {
            return;
        }

        _lastFlushUtc = now;
        Flush(force: false);
    }

    private void LoadCore()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string json = File.ReadAllText(_path);
            PairedPhoneDocument? document = JsonSerializer.Deserialize(
                json,
                MobileBridgeJsonContext.Default.PairedPhoneDocument);

            if (document?.Phones == null)
            {
                return;
            }

            foreach (PairedPhoneRow row in document.Phones)
            {
                if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.TokenHash))
                {
                    continue;
                }

                _devices.Add(new PairedPhoneEntry(
                    new PairedPhone(row.Id, SanitizeLabel(row.Label), row.PairedUtc, row.LastSeenUtc),
                    row.TokenHash));

                if (_devices.Count >= MaxDevices)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // A damaged file must never stop the bridge; the phones simply pair again.
            Safe.Log(ex, "[MobileBridge] Could not read the paired-phone list");
            _devices.Clear();
        }
    }

    /// <summary>Writes the list atomically so a crash mid-write cannot truncate a good file.</summary>
    private void Flush(bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _lastFlushUtc < LastSeenFlushInterval)
        {
            return;
        }

        _lastFlushUtc = DateTimeOffset.UtcNow;

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                AppPaths.EnsureDirectory(directory);
            }

            var rows = new PairedPhoneRow[_devices.Count];
            for (int i = 0; i < _devices.Count; i++)
            {
                PairedPhoneEntry entry = _devices[i];
                rows[i] = new PairedPhoneRow(
                    entry.Phone.Id,
                    entry.Phone.Label,
                    entry.TokenHash,
                    entry.Phone.PairedUtc,
                    entry.Phone.LastSeenUtc);
            }

            var document = new PairedPhoneDocument(DocumentVersion, rows);
            string json = JsonSerializer.Serialize(document, MobileBridgeJsonContext.Default.PairedPhoneDocument);

            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Could not persist the paired-phone list");
        }
    }

    private sealed record PairedPhoneEntry(PairedPhone Phone, string TokenHash);
}
