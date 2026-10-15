using System.Collections.Generic;
using System.Text.Json.Serialization;
using MetroHub.Core.Services;

namespace MetroHub.Core.Services.MobileBridge;

#region Mobile Bridge DTO Models

/// <summary>
/// Unauthenticated liveness probe. Deliberately minimal: it confirms a bridge exists and nothing else,
/// so an unrelated device on the LAN learns no machine name, address, or workspace detail.
/// </summary>
public sealed record MobileBridgeHealthResponse(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("bridge")] bool Bridge,
    [property: JsonPropertyName("requiresToken")] bool RequiresToken
);

/// <summary>Tile size the phone is allowed to request. Mirrors the companion span clamp.</summary>
public sealed record MobileBridgeSizeDto(
    [property: JsonPropertyName("spanX")] int SpanX,
    [property: JsonPropertyName("spanY")] int SpanY,
    [property: JsonPropertyName("label")] string Label
);

/// <summary>One entry in the phone's recent list, in either direction.</summary>
public sealed record MobileBridgeRecentItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("sentAtUtc")] long SentAtUtc,
    [property: JsonPropertyName("duplicate")] bool Duplicate
);

public sealed record MobileBridgeContextResponse(
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("activeWorkspaceId")] string ActiveWorkspaceId,
    [property: JsonPropertyName("activeWorkspaceName")] string ActiveWorkspaceName,
    [property: JsonPropertyName("workspaces")] IReadOnlyList<CompanionWorkspaceDto> Workspaces,
    [property: JsonPropertyName("allowedSizes")] IReadOnlyList<MobileBridgeSizeDto> AllowedSizes,
    [property: JsonPropertyName("maxUploadBytes")] long MaxUploadBytes,
    [property: JsonPropertyName("canPushToPhone")] bool CanPushToPhone,
    [property: JsonPropertyName("recent")] IReadOnlyList<MobileBridgeRecentItemDto> Recent,
    [property: JsonPropertyName("secure")] bool Secure,
    [property: JsonPropertyName("httpsPort")] int HttpsPort,
    [property: JsonPropertyName("pairedAs")] string? PairedAs,
    [property: JsonPropertyName("canInstallAsApp")] bool CanInstallAsApp,
    [property: JsonPropertyName("deviceToken")] string? DeviceToken = null
);

/// <summary>Generic acknowledgement for phone-side actions such as unpairing.</summary>
public sealed record MobileBridgeActionResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null
);

/// <summary>Payload pushed from the PC to every subscribed phone over server-sent events.</summary>
public sealed record MobileBridgePushEventDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("sentAtUtc")] long SentAtUtc
);

public sealed record MobileBridgePushResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("delivered")] int Delivered,
    [property: JsonPropertyName("message")] string? Message = null
);

#endregion

/// <summary>
/// Source-generated JSON for the phone bridge DTOs. No reflection at runtime.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MobileBridgeHealthResponse))]
[JsonSerializable(typeof(MobileBridgeSizeDto))]
[JsonSerializable(typeof(List<MobileBridgeSizeDto>))]
[JsonSerializable(typeof(MobileBridgeRecentItemDto))]
[JsonSerializable(typeof(List<MobileBridgeRecentItemDto>))]
[JsonSerializable(typeof(MobileBridgeContextResponse))]
[JsonSerializable(typeof(MobileBridgePushEventDto))]
[JsonSerializable(typeof(MobileBridgePushResponse))]
[JsonSerializable(typeof(MobileBridgeActionResponse))]
[JsonSerializable(typeof(PairedPhoneDocument))]
[JsonSerializable(typeof(CompanionWorkspaceDto))]
[JsonSerializable(typeof(List<CompanionWorkspaceDto>))]
internal partial class MobileBridgeJsonContext : JsonSerializerContext
{
}
