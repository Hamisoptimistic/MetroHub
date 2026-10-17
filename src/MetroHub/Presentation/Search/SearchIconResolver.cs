using MetroHub.Core.Search;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Search;

/// <summary>
/// Centralized icon and badge resolver for search candidates.
/// Prioritizes provider-owned explicit icons, falling back to categorical defaults.
/// </summary>
public static class SearchIconResolver
{
    public static SymbolRegular ResolveSymbol(Candidate c) =>
        c.Icon.Kind == CandidateIconKind.Symbol && c.Icon.Symbol != SymbolRegular.Empty
            ? c.Icon.Symbol
            : c.Category switch
            {
                SearchCategory.Actions   => SymbolRegular.Flash24,
                SearchCategory.Apps      => SymbolRegular.Apps24,
                SearchCategory.Folders   => SymbolRegular.Folder24,
                SearchCategory.Images    => SymbolRegular.Image24,
                SearchCategory.Media     => SymbolRegular.MusicNote224,
                SearchCategory.Code      => SymbolRegular.Code24,
                _                        => SymbolRegular.Document24
            };

    public static string ResolveKindBadge(Candidate c) =>
        !string.IsNullOrEmpty(c.KindLabel) ? c.KindLabel : c.Category.ToString();
}

