using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;

namespace MetroHub.Widgets;

/// <summary>
/// Mandatory base class for all MetroHub widget ViewModels (Phase 0.5 directive).
/// Built on CommunityToolkit.Mvvm ObservableRecipient.
/// Enforces decoupled boundary adaptation with TileModel.SettingsJson, zero WPF UI dependencies,
/// and weak-reference messaging lifecycle support.
/// </summary>
public abstract partial class WidgetViewModelBase : ObservableRecipient, IWidgetViewModel, IRecipient<HubVisibilityChangedMessage>
{
    public TileModel Model { get; protected set; }

    public abstract IReadOnlyList<WidgetSize> AllowedSizes { get; }

    protected WidgetViewModelBase(TileModel model) : base(WidgetMessenger.Default)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        IsActive = true;
    }

    public virtual void Initialize(TileModel model)
    {
        Model = model;
        IsActive = true;
        LoadSettings(model.SettingsJson);
    }

    /// <summary>
    /// Reads widget state from the persisted SettingsJson payload.
    /// Keep all deserialized fields nullable with fallback defaults.
    /// </summary>
    protected virtual void LoadSettings(string? settingsJson) { }

    /// <summary>
    /// Serializes current widget state into Model.SettingsJson.
    /// </summary>
    public virtual void SaveSettings() { }

    /// <summary>
    /// Lifecycle hook: pause timers/polling when MetroHub is hidden.
    /// </summary>
    public virtual void Pause() { }

    /// <summary>
    /// Lifecycle hook: resume timers/polling when MetroHub is visible.
    /// </summary>
    public virtual void Resume() { }

    /// <summary>
    /// Centralized hub visibility recipient for all widgets.
    /// Cascades visibility messages directly into Pause() and Resume() hooks.
    /// </summary>
    public virtual void Receive(HubVisibilityChangedMessage message)
    {
        if (message.IsVisible)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    private bool _disposed;

    /// <summary>
    /// Explicit teardown invoked when the parent tile is unpinned or destroyed.
    /// </summary>
    public void Teardown()
    {
        Dispose();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Pause();
            IsActive = false; // Deactivates CommunityToolkit messenger subscriptions cleanly
        }

        _disposed = true;
    }
}
