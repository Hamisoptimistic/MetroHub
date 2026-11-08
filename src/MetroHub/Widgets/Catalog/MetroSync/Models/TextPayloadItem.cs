using System;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class TextPayloadItem : MetroSyncMessageItem
{
    private static readonly char[] PunctuationTrimChars = ['.', ',', ';', '!', '?', '"', '\''];

    public string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _faviconImage;

    [JsonIgnore]
    public string? FirstUrl => ExtractFirstUrl(Text);

    [JsonIgnore]
    public bool HasUrl => !string.IsNullOrEmpty(FirstUrl);

    public static string? ExtractFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int httpIndex = text.IndexOf("http://", startIndex, StringComparison.OrdinalIgnoreCase);
            int httpsIndex = text.IndexOf("https://", startIndex, StringComparison.OrdinalIgnoreCase);

            int candidateIndex = -1;
            if (httpIndex >= 0 && httpsIndex >= 0)
            {
                candidateIndex = Math.Min(httpIndex, httpsIndex);
            }
            else if (httpIndex >= 0)
            {
                candidateIndex = httpIndex;
            }
            else if (httpsIndex >= 0)
            {
                candidateIndex = httpsIndex;
            }
            else
            {
                break;
            }

            int endIndex = candidateIndex;
            while (endIndex < text.Length && !char.IsWhiteSpace(text[endIndex]))
            {
                endIndex++;
            }

            string rawCandidate = text[candidateIndex..endIndex];
            rawCandidate = CleanUrlCandidate(rawCandidate);

            if (Uri.TryCreate(rawCandidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return rawCandidate;
            }

            startIndex = endIndex;
        }

        return null;
    }

    private static string CleanUrlCandidate(string candidate)
    {
        while (candidate.Length > 0)
        {
            char lastChar = candidate[^1];
            if (Array.IndexOf(PunctuationTrimChars, lastChar) >= 0 || lastChar == '>' || lastChar == '<')
            {
                candidate = candidate[..^1];
                continue;
            }

            if (lastChar == ')' && CountOccurrences(candidate, ')') > CountOccurrences(candidate, '('))
            {
                candidate = candidate[..^1];
                continue;
            }

            if (lastChar == ']' && CountOccurrences(candidate, ']') > CountOccurrences(candidate, '['))
            {
                candidate = candidate[..^1];
                continue;
            }

            break;
        }

        return candidate;
    }

    private static int CountOccurrences(string text, char c)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == c) count++;
        }
        return count;
    }
}
