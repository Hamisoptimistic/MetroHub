using System;
using QRCoder;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>
/// Renders the pairing QR code shown on the PC. Generated in-process with QRCoder (MIT, pure managed);
/// the pairing URL is never sent to an external QR service.
/// </summary>
public static class PairingQrCode
{
    private const int DefaultPixelsPerModule = 6;

    /// <summary>Dark modules. Kept near-black so the code scans reliably on a bright canvas.</summary>
    private static readonly byte[] DarkRgba = { 0x0F, 0x11, 0x15, 0xFF };

    /// <summary>Light modules, including the mandatory quiet zone.</summary>
    private static readonly byte[] LightRgba = { 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// Produces a PNG byte array for the given payload, or null when the payload cannot be encoded.
    /// </summary>
    public static byte[]? TryRenderPng(string payload, int pixelsPerModule = DefaultPixelsPerModule)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        int scale = Math.Clamp(pixelsPerModule, 2, 20);

        try
        {
            using var generator = new QRCodeGenerator();
            QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

            try
            {
                // PngByteQRCode takes ownership of the QRCodeData; the finally block covers a
                // constructor failure so the matrix is never leaked.
                using var png = new PngByteQRCode(data);
                return png.GetGraphic(scale, DarkRgba, LightRgba, drawQuietZones: true);
            }
            finally
            {
                data.Dispose();
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Failed to render pairing QR code");
            return null;
        }
    }
}
