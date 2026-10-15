using System;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>What a desktop tile contributes when it is pushed to the phone.</summary>
internal enum PhonePushKind
{
    /// <summary>Nothing sendable: the bridge is off, or the tile holds no text payload.</summary>
    None,

    /// <summary>A web link; the tile target is the URL itself.</summary>
    Link,

    /// <summary>A note; the tile target is a .txt file whose contents are the payload.</summary>
    NoteFile
}

/// <summary>
/// Rules for the desktop to phone direction. The v1 feed carries text only, so a tile qualifies when
/// it is a web link or a note. Apps, folders, photos and widgets are deliberately not sendable.
/// Kept free of WPF and file I/O so both call sites stay testable.
/// </summary>
internal static class MobileBridgePushComposer
{
    private static readonly char[] UrlBreakers = { ' ', '\t', '\r', '\n' };

    /// <summary>Classifies a tile into the payload the phone would receive.</summary>
    internal static PhonePushKind ClassifyTile(bool bridgeRunning, bool isWebLink, bool isNote)
    {
        if (!bridgeRunning)
        {
            return PhonePushKind.None;
        }

        if (isWebLink)
        {
            return PhonePushKind.Link;
        }

        return isNote ? PhonePushKind.NoteFile : PhonePushKind.None;
    }

    /// <summary>
    /// Splits clipboard text into what the phone understands: a single-line http(s) URL becomes a
    /// link, anything else becomes a note. Notes are capped to the length the pin validator accepts
    /// so both directions of the bridge stay comparable.
    /// </summary>
    internal static (string? Url, string? Note) DescribeClipboardPush(string? clipboardText)
    {
        string text = clipboardText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (null, null);
        }

        if (IsHttpUrl(text))
        {
            return (text, null);
        }

        return text.Length > CompanionPinValidator.MaxNoteLength
            ? (null, text[..CompanionPinValidator.MaxNoteLength])
            : (null, text);
    }

    /// <summary>True for a single-line absolute http(s) URL.</summary>
    internal static bool IsHttpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.IndexOfAny(UrlBreakers) >= 0)
        {
            return false;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
