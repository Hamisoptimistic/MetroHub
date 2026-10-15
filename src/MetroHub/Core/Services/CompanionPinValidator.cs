using System;
using System.Collections.Generic;

namespace MetroHub.Core.Services;

/// <summary>Outcome of normalising an inbound pin request.</summary>
public sealed record CompanionPinValidationResult(
    CompanionPinRequest? Request,
    int StatusCode,
    string? Error)
{
    public static CompanionPinValidationResult Ok(CompanionPinRequest request) => new(request, 200, null);

    public static CompanionPinValidationResult Fail(int statusCode, string error) => new(null, statusCode, error);
}

/// <summary>
/// Single source of truth for turning an untrusted incoming pin request into a safe one.
/// Shared by the loopback browser-extension companion service and the LAN phone bridge so both
/// surfaces enforce exactly the same scheme allow-list, span clamp, truncation, credential and
/// workspace checks.
/// </summary>
public static class CompanionPinValidator
{
    public const int MaxTitleLength = 256;
    public const int MaxNoteLength = 4096;

    /// <summary>
    /// Validates and normalises a pin request.
    /// Returns <see cref="CompanionPinValidationResult"/> with StatusCode 200 and a non-null
    /// <see cref="CompanionPinValidationResult.Request"/> on success, or a non-null error otherwise.
    /// </summary>
    public static CompanionPinValidationResult Validate(
        CompanionPinRequest? request,
        Func<IReadOnlyList<CompanionWorkspaceDto>>? workspacesProvider = null)
    {
        if (request == null ||
            (string.IsNullOrWhiteSpace(request.Url) &&
             string.IsNullOrWhiteSpace(request.Note) &&
             string.IsNullOrWhiteSpace(request.ThumbnailUrl)))
        {
            return CompanionPinValidationResult.Fail(400, "Missing required 'url', 'note', or 'thumbnailUrl' field.");
        }

        string rawUrl = request.Url?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(rawUrl))
        {
            if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return CompanionPinValidationResult.Fail(400, "Only standard websites can be pinned.");
            }

            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                return CompanionPinValidationResult.Fail(400, "Malformed URL.");
            }

            // Credentials in a shared link would land on the canvas, in the recent-items feed sent to
            // the phone, and in logs. Canvas paste already refuses such links; refuse them here too.
            if (parsedUri.UserInfo.Length > 0)
            {
                return CompanionPinValidationResult.Fail(
                    400,
                    "URLs with embedded credentials are not accepted. Remove the user:password part and try again.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ThumbnailUrl))
        {
            string rawThumb = request.ThumbnailUrl.Trim();
            if (!rawThumb.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawThumb.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return CompanionPinValidationResult.Fail(400, "Invalid thumbnailUrl scheme: only http and https are permitted.");
            }

            if (!Uri.TryCreate(rawThumb, UriKind.Absolute, out Uri? parsedThumb) ||
                (parsedThumb.Scheme != Uri.UriSchemeHttp && parsedThumb.Scheme != Uri.UriSchemeHttps))
            {
                return CompanionPinValidationResult.Fail(400, "Malformed thumbnailUrl.");
            }

            if (parsedThumb.UserInfo.Length > 0)
            {
                return CompanionPinValidationResult.Fail(
                    400,
                    "thumbnailUrl must not contain embedded credentials.");
            }
        }

        // Clamping & dimensions validation (only 2x2 or 4x2 permitted)
        int spanX = request.SpanX ?? 2;
        int spanY = request.SpanY ?? 2;
        if (!((spanX == 2 && spanY == 2) || (spanX == 4 && spanY == 2)))
        {
            return CompanionPinValidationResult.Fail(400, "Invalid span dimensions. Allowed tile dimensions are 2x2 or 4x2.");
        }

        string title = request.Title?.Trim() ?? string.Empty;
        if (title.Length > MaxTitleLength)
        {
            title = title.Substring(0, MaxTitleLength);
        }

        string note = request.Note?.Trim() ?? string.Empty;
        if (note.Length > MaxNoteLength)
        {
            note = note.Substring(0, MaxNoteLength);
        }

        string sanitizedUrl = !string.IsNullOrEmpty(rawUrl) ? LocalCompanionService.SanitizeUrl(rawUrl) : string.Empty;

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            IReadOnlyList<CompanionWorkspaceDto> workspaces = workspacesProvider != null
                ? workspacesProvider()
                : new List<CompanionWorkspaceDto>();

            bool exists = false;
            for (int i = 0; i < workspaces.Count; i++)
            {
                if (workspaces[i].Id.Equals(request.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                return CompanionPinValidationResult.Fail(404, $"Workspace '{request.WorkspaceId}' not found.");
            }
        }

        return CompanionPinValidationResult.Ok(new CompanionPinRequest(
            Url: sanitizedUrl,
            Title: title,
            Note: note,
            WorkspaceId: request.WorkspaceId,
            SpanX: spanX,
            SpanY: spanY,
            ThumbnailUrl: request.ThumbnailUrl));
    }
}
