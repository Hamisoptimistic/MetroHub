namespace MetroHub.Core.Models;

/// <summary>
/// Modular settings model holding configurable search keyword prefixes and triggers.
/// </summary>
public sealed class SearchSettings
{
    public string TerminalPrefix { get; set; } = ">";
    public string SettingsPrefix { get; set; } = "set:";
    public string ProcessKillerPrefix { get; set; } = "kill ";
    public string WindowWalkerPrefix { get; set; } = "win ";
    public string WorldClockPrefix { get; set; } = "time ";
    public string VolumePrefix { get; set; } = "vol ";
    public string AudioTrigger { get; set; } = "audio";
    public string TextTransformPrefix { get; set; } = "case ";
    public string ClipboardPrefix { get; set; } = "cb ";
    public string GoogleBang { get; set; } = "g ";
    public string GitHubBang { get; set; } = "gh ";
    public string YouTubeBang { get; set; } = "yt ";
    public string DuckDuckGoBang { get; set; } = "ddg ";
}

