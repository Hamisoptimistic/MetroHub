using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Widgets.Catalog.Radio.Services;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Presentation item for an individual radio category row in the management dialog.
/// </summary>
public sealed partial class RadioCategoryItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _iconKey = "Headphones24";

    [ObservableProperty]
    private SymbolRegular _iconSymbol = SymbolRegular.Headphones24;

    [ObservableProperty]
    private int _stationCount;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _draftName = string.Empty;

    [ObservableProperty]
    private bool _isPendingDelete;

    [ObservableProperty]
    private string _deleteFallbackName = string.Empty;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [ObservableProperty]
    private bool _canDelete = true;

    public RadioCategoryItemViewModel()
    {
    }

    public RadioCategoryItemViewModel(RadioCategory category, int stationCount)
    {
        ArgumentNullException.ThrowIfNull(category);
        _id = category.Id;
        _name = category.DisplayName;
        _iconKey = string.IsNullOrWhiteSpace(category.IconSymbol) ? "Headphones24" : category.IconSymbol;
        _iconSymbol = RadioSymbols.ParseSymbol(_iconKey);
        _stationCount = stationCount;
        _draftName = category.DisplayName;
    }

    partial void OnIconKeyChanged(string value)
    {
        IconSymbol = RadioSymbols.ParseSymbol(value);
    }
}
