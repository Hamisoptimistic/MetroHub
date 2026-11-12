using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MetroHub.Core.Services;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Verified credentials required for MetroSync bot communication.
/// </summary>
public sealed record MetroSyncCredentials(string BotToken, long AllowedUserId);

/// <summary>
/// DPAPI-backed secure vault for storing Telegram bot credentials.
/// Tokens are encrypted with DPAPI (CurrentUser) and never stored in plain text.
/// </summary>
public sealed class MetroSyncSecurity
{
    private readonly string _credentialsPath;
    private readonly string _directoryPath;
    private readonly Lock _lock = new();

    public static MetroSyncSecurity Default { get; } = new();

    public string CredentialsPath => _credentialsPath;
    public string DirectoryPath => _directoryPath;

    public MetroSyncSecurity(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync");
        _credentialsPath = Path.Combine(_directoryPath, "credentials.dat");
    }

    /// <summary>
    /// Encrypts and saves credentials to credentials.dat using DPAPI CurrentUser scope.
    /// </summary>
    public void SaveCredentials(string botToken, long allowedUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        if (allowedUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedUserId), "Allowed user ID must be positive.");
        }

        lock (_lock)
        {
            Directory.CreateDirectory(_directoryPath);

            var model = new MetroSyncCredentials(botToken.Trim(), allowedUserId);
            byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(model, MetroSyncSecurityJsonContext.Default.MetroSyncCredentials);

            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

            string tmpPath = _credentialsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmpPath, encryptedBytes);
                File.Move(tmpPath, _credentialsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch (Exception ex) { Safe.Ignore(ex, "MetroSyncSecurity.DeleteTmpPath"); }
                }
            }
        }
    }

    /// <summary>
    /// Attempts to load and decrypt credentials from credentials.dat.
    /// Returns false if file does not exist, is tampered, or is corrupted.
    /// </summary>
    public bool TryLoadCredentials([NotNullWhen(true)] out MetroSyncCredentials? credentials)
    {
        lock (_lock)
        {
            credentials = null;
            if (!File.Exists(_credentialsPath))
            {
                return false;
            }

            try
            {
                byte[] encryptedBytes = File.ReadAllBytes(_credentialsPath);
                if (encryptedBytes.Length == 0)
                {
                    return false;
                }

                byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var result = JsonSerializer.Deserialize(plainBytes, MetroSyncSecurityJsonContext.Default.MetroSyncCredentials);

                if (result != null && !string.IsNullOrWhiteSpace(result.BotToken) && result.AllowedUserId > 0)
                {
                    credentials = result;
                    return true;
                }

                return false;
            }
            catch (CryptographicException ex)
            {
                Safe.Log(ex, "MetroSyncSecurity.DecryptCredentials");
                return false;
            }
            catch (JsonException ex)
            {
                Safe.Log(ex, "MetroSyncSecurity.DeserializeCredentials");
                return false;
            }
            catch (IOException ex)
            {
                Safe.Log(ex, "MetroSyncSecurity.ReadCredentialsFile");
                return false;
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "MetroSyncSecurity.LoadCredentialsUnexpected");
                return false;
            }
        }
    }

    /// <summary>
    /// Deletes credentials.dat and cleans up leftover temporary files if any exist.
    /// </summary>
    public void Delete()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_credentialsPath))
                {
                    File.Delete(_credentialsPath);
                }
            }
            catch (Exception ex)
            {
                // Best effort cleanup.
                Safe.Ignore(ex, "MetroSyncSecurity.DeleteCredentials");
            }

            try
            {
                if (Directory.Exists(_directoryPath))
                {
                    foreach (var file in Directory.EnumerateFiles(_directoryPath, "credentials.dat*.tmp"))
                    {
                        try { File.Delete(file); } catch (Exception ex) { Safe.Ignore(ex, "MetroSyncSecurity.DeleteTmpFile"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Safe.Ignore(ex, "MetroSyncSecurity.EnumerateTmpFiles");
            }
        }
    }

    public static void Save(string botToken, long allowedUserId) => Default.SaveCredentials(botToken, allowedUserId);
    public static bool TryLoad([NotNullWhen(true)] out MetroSyncCredentials? credentials) => Default.TryLoadCredentials(out credentials);
    public static void DeleteCredentials() => Default.Delete();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetroSyncCredentials))]
internal sealed partial class MetroSyncSecurityJsonContext : JsonSerializerContext
{
}
