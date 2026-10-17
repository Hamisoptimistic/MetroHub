using System;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized service and single source of truth for application settings.
/// Manages in-memory settings state, change notifications, and debounced persistence.
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public AppSettings Current { get; }

    public event Action<string>? SettingChanged;

    private SettingsService()
    {
        Current = StorageService.LoadSettings();
    }

    public void NotifyChanged(string propertyName)
    {
        SettingChanged?.Invoke(propertyName);
        StorageService.SaveSettings(Current);
    }

    public void Save()
    {
        StorageService.SaveSettings(Current);
    }
}

