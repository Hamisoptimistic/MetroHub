using System;

namespace MetroHub.Widgets.Catalog.PhoneBridge;

/// <summary>
/// Persisted state for the Phone Bridge widget.
/// Round-trips cleanly through <c>TileModel.SettingsJson</c>.
/// </summary>
public sealed class PhoneBridgeWidgetSettings
{
    /// <summary>Render the pairing QR code in the tile body.</summary>
    public bool ShowQrCode { get; set; } = true;

    /// <summary>Show LAN address and port under the QR code.</summary>
    public bool ShowAddress { get; set; } = true;

    /// <summary>Last port the tile switched on, so the widget and the shell agree after a restart.</summary>
    public int LastPort { get; set; }
}
