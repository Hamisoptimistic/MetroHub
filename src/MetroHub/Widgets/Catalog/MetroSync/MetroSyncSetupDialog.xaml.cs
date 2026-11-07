using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MetroHub.Presentation.Dialogs;
using MetroHub.Widgets.Catalog.MetroSync.Services;

namespace MetroHub.Widgets.Catalog.MetroSync;

/// <summary>
/// Setup wizard for MetroSync inheriting MetroDialog with the 4 visual states:
/// Token entry, Polling wait, Confirmation, and Manual User ID entry.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Window lifecycle manages disposable fields via OnClosed.")]
public partial class MetroSyncSetupDialog : MetroDialog
{
    private enum SetupVisualState
    {
        Intro,
        TokenEntry,
        PollingWait,
        Confirmation,
        ManualEntry
    }

    private readonly MetroSyncWidgetViewModel? _viewModel;
    private readonly MetroSyncSecurity _security;
    private readonly MetroSyncPairingService _pairingService;
    private readonly HashSet<long> _ignoredUserIds = new();

    private HttpClient? _ownedClient;
    private TelegramHttpTransport? _transport;
    private CancellationTokenSource? _pollingCts;
    private TelegramBotUser? _botUser;
    private CandidateSender? _candidate;
    private string? _currentBotToken;
    private SetupVisualState _currentState = SetupVisualState.Intro;

    /// <summary>
    /// Preserves dialog state when MetroHub deactivates or minimizes (e.g. when opening Telegram deep links).
    /// </summary>
    public override bool DismissOnDeactivate => false;

    public MetroSyncSetupDialog()
        : this(null, null, null)
    {
    }

    public MetroSyncSetupDialog(
        MetroSyncWidgetViewModel? viewModel = null,
        MetroSyncSecurity? security = null,
        MetroSyncPairingService? pairingService = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _security = security ?? MetroSyncSecurity.Default;
        _pairingService = pairingService ?? new MetroSyncPairingService(_security);

        PollingProgressRing.IsVisibleChanged += (s, e) =>
        {
            if (PollingProgressRing.IsVisible && _currentState == SetupVisualState.PollingWait)
            {
                PollingProgressRing.IsIndeterminate = false;
                PollingProgressRing.IsIndeterminate = true;
            }
        };

        SetVisualState(SetupVisualState.Intro);
    }

    public static bool Show(Window? owner, MetroSyncWidgetViewModel? viewModel = null)
    {
        var parentWindow = owner ?? Application.Current?.MainWindow;
        var dlg = new MetroSyncSetupDialog(viewModel)
        {
            Owner = parentWindow
        };
        return dlg.ShowDialog() == true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CancelPolling();
        _ownedClient?.Dispose();
        _ownedClient = null;
    }

    private void SetVisualState(SetupVisualState state)
    {
        _currentState = state;

        Title = state == SetupVisualState.Intro
            ? "Follow these steps to connect your device"
            : "Connect MetroSync";

        if (IntroPanel != null)
        {
            IntroPanel.Visibility = state == SetupVisualState.Intro ? Visibility.Visible : Visibility.Collapsed;
        }

        if (WizardPanel != null)
        {
            WizardPanel.Visibility = state != SetupVisualState.Intro ? Visibility.Visible : Visibility.Collapsed;
        }

        State1Panel.Visibility = state == SetupVisualState.TokenEntry ? Visibility.Visible : Visibility.Collapsed;
        State2Panel.Visibility = state == SetupVisualState.PollingWait ? Visibility.Visible : Visibility.Collapsed;
        State3Panel.Visibility = state == SetupVisualState.Confirmation ? Visibility.Visible : Visibility.Collapsed;
        State4Panel.Visibility = state == SetupVisualState.ManualEntry ? Visibility.Visible : Visibility.Collapsed;

        if (State1Actions != null) State1Actions.Visibility = state == SetupVisualState.TokenEntry ? Visibility.Visible : Visibility.Collapsed;
        if (State2Actions != null) State2Actions.Visibility = state == SetupVisualState.PollingWait ? Visibility.Visible : Visibility.Collapsed;
        if (State3Actions != null) State3Actions.Visibility = state == SetupVisualState.Confirmation ? Visibility.Visible : Visibility.Collapsed;
        if (State4Actions != null) State4Actions.Visibility = state == SetupVisualState.ManualEntry ? Visibility.Visible : Visibility.Collapsed;

        if (PollingProgressRing != null)
        {
            if (state == SetupVisualState.PollingWait)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    PollingProgressRing.IsIndeterminate = false;
                    PollingProgressRing.IsIndeterminate = true;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                PollingProgressRing.IsIndeterminate = false;
            }
        }

        BotTokenInput.IsEnabled = state is SetupVisualState.TokenEntry or SetupVisualState.ManualEntry;
        ManualIdToggleButton.Visibility = state is not (SetupVisualState.ManualEntry or SetupVisualState.Intro) ? Visibility.Visible : Visibility.Collapsed;

        if (state == SetupVisualState.TokenEntry)
        {
            BotTokenInput.Focus();
        }
    }

    private void ShowError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            InlineErrorText.Text = string.Empty;
            InlineErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            InlineErrorText.Text = message;
            InlineErrorText.Visibility = Visibility.Visible;
        }
    }

    private void CancelPolling()
    {
        try
        {
            if (PollingProgressRing != null)
            {
                PollingProgressRing.IsIndeterminate = false;
            }
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
        }
        catch
        {
            // Ignored on cleanup
        }
        finally
        {
            _pollingCts = null;
        }
    }

    private void OnIntroNextClick(object sender, RoutedEventArgs e)
    {
        SetVisualState(SetupVisualState.TokenEntry);
    }

    private void OnInstructionsClick(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        SetVisualState(SetupVisualState.Intro);
    }

    private async void OnContinueClick(object sender, RoutedEventArgs e)
    {
        await ProcessTokenContinueAsync();
    }

    private async void OnBotTokenInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _currentState == SetupVisualState.TokenEntry)
        {
            e.Handled = true;
            await ProcessTokenContinueAsync();
        }
    }

    private void OnBotTokenInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _botUser = null;
        _currentBotToken = null;
        ShowError(null);
    }

    private async Task ProcessTokenContinueAsync()
    {
        ShowError(null);
        var token = BotTokenInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowError("Token not valid.");
            return;
        }

        ContinueButton.IsEnabled = false;

        try
        {
            _currentBotToken = token;
            _ownedClient?.Dispose();
            _ownedClient = TelegramHttpTransport.CreateDedicatedClient();
            _transport = new TelegramHttpTransport(_ownedClient, new Uri("https://api.telegram.org"), token);

            _botUser = await _transport.GetMeAsync(CancellationToken.None).ConfigureAwait(true);

            var botHandle = !string.IsNullOrWhiteSpace(_botUser.Username)
                ? $"@{_botUser.Username}"
                : (!string.IsNullOrWhiteSpace(_botUser.FirstName) ? _botUser.FirstName : "your bot");

            if (PollingBotNameRun != null)
            {
                PollingBotNameRun.Text = botHandle;
            }

            if (OpenTelegramButton != null)
            {
                OpenTelegramButton.Visibility = !string.IsNullOrWhiteSpace(_botUser.Username)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            SetVisualState(SetupVisualState.PollingWait);
            StartPolling();
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 401)
        {
            ShowError("Token not valid.");
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 409)
        {
            ShowError("Another app or webhook is already using this bot.");
        }
        catch (Exception ex)
        {
            ShowError(MetroSyncWidgetViewModel.RedactBotToken(ex.Message, token));
        }
        finally
        {
            ContinueButton.IsEnabled = true;
        }
    }

    private void StartPolling()
    {
        CancelPolling();
        if (_transport == null) return;

        _pollingCts = new CancellationTokenSource();
        var ct = _pollingCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var candidate = await _pairingService.WaitForCandidateSenderAsync(_transport, _ignoredUserIds, ct).ConfigureAwait(false);
                if (candidate != null && !ct.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        _candidate = candidate;
                        ConfirmationPromptText.Text = $"Connect as {candidate.DisplayName}?";
                        SetVisualState(SetupVisualState.Confirmation);
                    });
                }
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 401)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowError("Token not valid.");
                    SetVisualState(SetupVisualState.TokenEntry);
                });
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 409)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowError("Another app or webhook is already using this bot.");
                    SetVisualState(SetupVisualState.TokenEntry);
                });
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowError(MetroSyncWidgetViewModel.RedactBotToken(ex.Message, _currentBotToken));
                    SetVisualState(SetupVisualState.TokenEntry);
                });
            }
        }, ct);
    }

    private void OnPollingCancelClick(object sender, RoutedEventArgs e)
    {
        CancelPolling();
        ShowError(null);
        SetVisualState(SetupVisualState.TokenEntry);
    }

    private void OnOpenTelegramClick(object sender, RoutedEventArgs e)
    {
        var username = _botUser?.Username;
        if (string.IsNullOrWhiteSpace(username)) return;

        try
        {
            var uri = new Uri($"https://t.me/{username}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell launch failures
        }
    }

    private void OnNotMeClick(object sender, RoutedEventArgs e)
    {
        if (_candidate != null)
        {
            _ignoredUserIds.Add(_candidate.UserId);
        }

        ShowError(null);
        SetVisualState(SetupVisualState.PollingWait);
        StartPolling();
    }

    private void OnConfirmConnectClick(object sender, RoutedEventArgs e)
    {
        if (_candidate == null) return;

        var token = BotTokenInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowError("Token not valid.");
            return;
        }

        SaveAndConnect(token, _candidate.UserId);
    }

    private void OnManualIdToggleClick(object sender, RoutedEventArgs e)
    {
        CancelPolling();
        ShowError(null);
        SetVisualState(SetupVisualState.ManualEntry);
        ManualUserIdInput.Focus();
    }

    private void OnManualCancelClick(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        SetVisualState(SetupVisualState.TokenEntry);
    }

    private async void OnManualConnectClick(object sender, RoutedEventArgs e)
    {
        await ProcessManualConnectAsync();
    }

    private async void OnManualUserIdInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ProcessManualConnectAsync();
        }
    }

    private async Task ProcessManualConnectAsync()
    {
        ShowError(null);
        var token = BotTokenInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowError("Token not valid.");
            return;
        }

        var rawId = ManualUserIdInput.Text?.Trim();
        if (!long.TryParse(rawId, out var userId) || userId <= 0)
        {
            ShowError("User ID not valid.");
            return;
        }

        try
        {
            if (_botUser == null || !string.Equals(_currentBotToken, token, StringComparison.Ordinal))
            {
                _currentBotToken = token;
                _ownedClient?.Dispose();
                _ownedClient = TelegramHttpTransport.CreateDedicatedClient();
                _transport = new TelegramHttpTransport(_ownedClient, new Uri("https://api.telegram.org"), token);
                _botUser = await _transport.GetMeAsync(CancellationToken.None).ConfigureAwait(true);
            }

            SaveAndConnect(token, userId);
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 401)
        {
            ShowError("Token not valid.");
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 409)
        {
            ShowError("Another app or webhook is already using this bot.");
        }
        catch (Exception ex)
        {
            ShowError(MetroSyncWidgetViewModel.RedactBotToken(ex.Message, token));
        }
    }

    private void SaveAndConnect(string token, long userId)
    {
        _security.SaveCredentials(token, userId);
        if (_viewModel != null)
        {
            _ = _viewModel.ConnectAsync(new MetroSyncCredentials(token, userId));
        }

        DialogResult = true;
        Close();
    }
}