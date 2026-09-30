using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
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

    protected override void OnActivated()
    {
        base.OnActivated();
        WidgetHeartbeatService.SecondTick -= OnSecondTickInternal;
        WidgetHeartbeatService.SecondTick += OnSecondTickInternal;
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        WidgetHeartbeatService.SecondTick -= OnSecondTickInternal;
    }

    public virtual void Initialize(TileModel model)
    {
        Model = model;
        IsActive = true;
        Safe.Try(() => LoadSettings(model.SettingsJson), context: $"{GetType().Name}.LoadSettings");
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
    /// Notifies the hub via messenger that this widget's settings or layout reference were updated.
    /// Eliminates direct coupling from widget ViewModels to MainWindow.Current.
    /// </summary>
    protected void NotifySettingsChanged()
    {
        WidgetMessenger.Default.Send(new WidgetSettingsChangedMessage(Model.Id, Model.SettingsJson));
    }

    /// <summary>
    /// Lifecycle hook: pause timers/polling when MetroHub is hidden.
    /// </summary>
    public virtual void Pause() { }

    /// <summary>
    /// Lifecycle hook: resume timers/polling when MetroHub is visible.
    /// </summary>
    public virtual void Resume() { }

    /// <summary>
    /// Lifecycle hook: called automatically once every second while MetroHub is visible.
    /// Widgets override this hook to perform work instead of managing their own DispatcherTimer instances.
    /// </summary>
    public virtual void OnSecondTick(DateTime utcNow) { }

    private void OnSecondTickInternal(DateTime utcNow)
    {
        if (IsActive && !_disposed)
        {
            Safe.Try(() => OnSecondTick(utcNow), context: $"{GetType().Name}.OnSecondTick");
        }
    }

    /// <summary>
    /// Centralized hub visibility recipient for all widgets.
    /// Cascades visibility messages directly into Pause() and Resume() hooks.
    /// </summary>
    public virtual void Receive(HubVisibilityChangedMessage message)
    {
        if (message.IsVisible)
        {
            Safe.Try(Resume, context: $"{GetType().Name}.Resume");
        }
        else
        {
            Safe.Try(Pause, context: $"{GetType().Name}.Pause");
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

    ~WidgetViewModelBase()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        WidgetHeartbeatService.SecondTick -= OnSecondTickInternal;

        if (disposing)
        {
            Safe.Try(Pause, context: $"{GetType().Name}.PauseOnDispose");
            IsActive = false; // Deactivates CommunityToolkit messenger subscriptions cleanly
        }

        _disposed = true;
    }
}
