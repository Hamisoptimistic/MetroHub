using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Radio.Services;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// ViewModel managing radio categories, including adding, reordering, inline renaming, and deletion.
/// </summary>
public sealed partial class RadioCategoryManagementViewModel : ObservableObject, IDisposable
{
    private readonly RadioCatalogService _catalogService;
    private bool _isDisposed;

    public ObservableCollection<RadioCategoryItemViewModel> Categories { get; } = new();

    public IReadOnlyList<RadioSymbolItem> CuratedIcons => RadioSymbols.CuratedIcons;

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    private string _selectedNewIconKey = "Headphones24";

    [ObservableProperty]
    private SymbolRegular _selectedNewIconSymbol = SymbolRegular.Headphones24;

    [ObservableProperty]
    private string _statusOrValidationMessage = string.Empty;

    [ObservableProperty]
    private bool _isIconPickerOpen;

    public bool CanAddCategory => Categories.Count < 12;

    public RadioCategoryManagementViewModel(RadioCatalogService? catalogService = null)
    {
        _catalogService = catalogService ?? RadioCatalogService.Instance;
        _catalogService.CatalogChanged += OnCatalogChanged;
        RefreshCategories();
    }

    public void RefreshCategories()
    {
        var catalog = _catalogService.GetCatalog();
        var currentItems = catalog?.Categories ?? new List<RadioCategory>();

        Categories.Clear();
        int count = currentItems.Count;
        for (int i = 0; i < count; i++)
        {
            var cat = currentItems[i];
            int stationCount = cat.Stations?.Count ?? 0;
            var vm = new RadioCategoryItemViewModel(cat, stationCount)
            {
                CanMoveUp = i > 0,
                CanMoveDown = i < count - 1,
                CanDelete = count > 1
            };
            Categories.Add(vm);
        }

        OnPropertyChanged(nameof(CanAddCategory));
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(RefreshCategories).FireAndForget("RadioCategoryManagementViewModel.OnCatalogChanged");
        }
        else
        {
            RefreshCategories();
        }
    }

    partial void OnSelectedNewIconKeyChanged(string value)
    {
        SelectedNewIconSymbol = RadioSymbols.ParseSymbol(value);
    }

    partial void OnNewCategoryNameChanged(string value)
    {
        if (!string.IsNullOrEmpty(StatusOrValidationMessage))
        {
            StatusOrValidationMessage = string.Empty;
        }
    }

    [RelayCommand]
    public async Task AddCategoryAsync()
    {
        string name = NewCategoryName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            StatusOrValidationMessage = "Category name cannot be empty.";
            return;
        }

        if (name.Length > 24)
        {
            StatusOrValidationMessage = "Category name cannot exceed 24 characters.";
            return;
        }

        if (Categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusOrValidationMessage = $"Category \"{name}\" already exists.";
            return;
        }

        if (Categories.Count >= 12)
        {
            StatusOrValidationMessage = "Maximum limit of 12 categories reached.";
            return;
        }

        StatusOrValidationMessage = string.Empty;

        var category = new RadioCategory
        {
            Id = name.ToLowerInvariant().Replace(' ', '_'),
            DisplayName = name,
            IconSymbol = SelectedNewIconKey,
            SortOrder = Categories.Count
        };

        await _catalogService.AddCategoryAsync(category);
        RefreshCategories();

        NewCategoryName = string.Empty;
        SelectedNewIconKey = "Headphones24";
        IsIconPickerOpen = false;
    }

    [RelayCommand]
    public async Task RequestDeleteAsync(RadioCategoryItemViewModel? item)
    {
        if (item == null || Categories.Count <= 1) return;

        if (item.StationCount == 0)
        {
            var fallback = Categories.FirstOrDefault(c => c != item);
            string fallbackId = fallback?.Id ?? string.Empty;
            await _catalogService.DeleteCategoryAsync(item.Id, fallbackId);
            RefreshCategories();
            return;
        }

        foreach (var cat in Categories)
        {
            if (cat != item)
            {
                cat.IsPendingDelete = false;
            }
        }

        var fallbackTarget = Categories.FirstOrDefault(c => c != item);
        item.DeleteFallbackName = fallbackTarget?.Name ?? "Ambient";
        item.IsPendingDelete = true;
    }

    [RelayCommand]
    public async Task ConfirmDeleteAsync(RadioCategoryItemViewModel? item)
    {
        if (item == null || Categories.Count <= 1) return;

        var fallback = Categories.FirstOrDefault(c => c != item);
        string fallbackId = fallback?.Id ?? string.Empty;

        await _catalogService.DeleteCategoryAsync(item.Id, fallbackId);
        RefreshCategories();
    }

    [RelayCommand]
    public void CancelDelete(RadioCategoryItemViewModel? item)
    {
        if (item != null)
        {
            item.IsPendingDelete = false;
        }
        else
        {
            foreach (var cat in Categories)
            {
                cat.IsPendingDelete = false;
            }
        }
    }

    [RelayCommand]
    public async Task MoveUpAsync(RadioCategoryItemViewModel? item)
    {
        if (item == null) return;
        int index = Categories.IndexOf(item);
        if (index > 0)
        {
            await _catalogService.ReorderCategoriesAsync(index, index - 1);
            RefreshCategories();
        }
    }

    [RelayCommand]
    public async Task MoveDownAsync(RadioCategoryItemViewModel? item)
    {
        if (item == null) return;
        int index = Categories.IndexOf(item);
        if (index >= 0 && index < Categories.Count - 1)
        {
            await _catalogService.ReorderCategoriesAsync(index, index + 1);
            RefreshCategories();
        }
    }

    public async Task ReorderCategory(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Categories.Count ||
            newIndex < 0 || newIndex >= Categories.Count ||
            oldIndex == newIndex)
        {
            return;
        }

        await _catalogService.ReorderCategoriesAsync(oldIndex, newIndex);
        RefreshCategories();
    }

    [RelayCommand]
    public void BeginRename(RadioCategoryItemViewModel? item)
    {
        if (item == null) return;

        foreach (var cat in Categories)
        {
            if (cat != item && cat.IsRenaming)
            {
                cat.DraftName = cat.Name;
                cat.IsRenaming = false;
            }
        }

        item.DraftName = item.Name;
        item.IsRenaming = true;
    }

    [RelayCommand]
    public async Task CommitRenameAsync(RadioCategoryItemViewModel? item)
    {
        if (item == null || !item.IsRenaming) return;

        string newName = item.DraftName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            item.DraftName = item.Name;
            item.IsRenaming = false;
            return;
        }

        if (newName.Length > 24)
        {
            newName = newName[..24].Trim();
        }

        if (Categories.Any(c => c != item && string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            item.DraftName = item.Name;
            item.IsRenaming = false;
            return;
        }

        string oldName = item.Name;
        item.Name = newName;
        item.IsRenaming = false;

        if (!string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            bool ok = await _catalogService.RenameCategoryAsync(item.Id, newName);
            if (!ok)
            {
                item.Name = oldName;
            }
            RefreshCategories();
        }
    }

    [RelayCommand]
    public void CancelRename(RadioCategoryItemViewModel? item)
    {
        if (item != null)
        {
            item.DraftName = item.Name;
            item.IsRenaming = false;
        }
        else
        {
            foreach (var cat in Categories)
            {
                if (cat.IsRenaming)
                {
                    cat.DraftName = cat.Name;
                    cat.IsRenaming = false;
                }
            }
        }
    }

    [RelayCommand]
    public void OpenNewCategoryIconPicker()
    {
        IsIconPickerOpen = !IsIconPickerOpen;
    }

    [RelayCommand]
    public void PickIcon(object? parameter)
    {
        if (parameter is RadioSymbolItem item)
        {
            SelectedNewIconKey = item.Key;
            SelectedNewIconSymbol = item.Symbol;
        }
        else if (parameter is SymbolRegular symbol)
        {
            SelectedNewIconKey = symbol.ToString();
            SelectedNewIconSymbol = symbol;
        }
        else if (parameter is string key)
        {
            SelectedNewIconKey = key;
            SelectedNewIconSymbol = RadioSymbols.ParseSymbol(key);
        }

        IsIconPickerOpen = false;
    }

    [RelayCommand]
    public async Task PickRowIconAsync(object? parameter)
    {
        if (parameter is ValueTuple<RadioCategoryItemViewModel, object> tuple)
        {
            await UpdateRowIconAsync(tuple.Item1, tuple.Item2);
        }
    }

    public async Task UpdateRowIconAsync(RadioCategoryItemViewModel? item, object? iconParam)
    {
        if (item == null || iconParam == null) return;

        string key = iconParam switch
        {
            RadioSymbolItem rsi => rsi.Key,
            SymbolRegular sym => sym.ToString(),
            string str => str,
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _catalogService.UpdateCategoryIconAsync(item.Id, key);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _catalogService.CatalogChanged -= OnCatalogChanged;
    }
}
