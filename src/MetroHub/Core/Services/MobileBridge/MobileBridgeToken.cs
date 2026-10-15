using System;
using System.Security.Cryptography;
using System.Text;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>
/// Per-session pairing secret for the phone LAN bridge.
/// Minted fresh on every bridge start, never persisted to disk, and compared in constant time.
/// </summary>
public sealed class MobileBridgeToken
{
    private const int EntropyBytes = 16; // 128 bits

    private readonly string _value;

    private MobileBridgeToken(string value)
    {
        _value = value;
    }

    /// <summary>Lowercase hex representation used in QR payloads and cookies.</summary>
    public string Value => _value;

    /// <summary>Creates a cryptographically random token.</summary>
    public static MobileBridgeToken Create()
    {
        Span<byte> bytes = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(bytes);
        return new MobileBridgeToken(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    /// <summary>
    /// Constant-time comparison. Length differences are rejected up front (length is public knowledge).
    /// </summary>
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != _value.Length)
        {
            return false;
        }

        byte[] expected = Encoding.ASCII.GetBytes(_value);
        byte[] actual = Encoding.ASCII.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
