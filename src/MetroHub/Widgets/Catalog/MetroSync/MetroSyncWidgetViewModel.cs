using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Common;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;

namespace MetroHub.Widgets.Catalog.MetroSync;

/// <summary>
/// ViewModel for the MetroSync widget.
/// Bridges Telegram long-polling updates, DPAPI vault, append-only history store,
/// thumbnail hydration, file downloads, and PC -> Phone outbound dispatch.
/// </summary>
public sealed partial class MetroSyncWidgetViewModel : WidgetViewModelBase, IWidgetContextMenuProvider
{
    public const string WidgetId = "metrosync";
    public const int MaxFeedItemCount = 200;
    public const int InitialHistoryCount = 50;
    public const int MaxTextLength = MetroSyncOutboundService.MaxTextLength;

    [GeneratedRegex(@"/bot([0-9]+:[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BotUrlTokenRegex();

    private readonly MetroSyncSecurity _security;
    private readonly MetroSyncHistoryStore _historyStore;
    private readonly bool _ownsHistoryStore;
    private readonly MetroSyncThumbnails _thumbnails;
    private readonly MetroSyncUpdateNormalizer _normalizer;
    private readonly ConduitEngineOptions? _engineOptions;
    private readonly string? _baseDirectory;
    private readonly Func<string, ITelegramTransport>? _transportFactory;
    private readonly TimeProvider _timeProvider;

    private readonly Action<string> _copyTextAction;
    private readonly Func<BitmapSource?> _getClipboardImageAction;
    private readonly Func<string[]?> _pickFilesAction;
    private readonly Action<string> _openUrlAction;
    private readonly Action<string> _openFileAction;
    private readonly Action<string> _showInFolderAction;

    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly SemaphoreSlim _engineLock = new(1, 1);

    private HttpClient? _ownedClient;
    private ITelegramTransport? _transport;
    private MetroSyncDownloader? _downloader;
    private MetroSyncOutboundService? _outboundService;
    private ConduitEngine? _engine;
    private Task? _consumerTask;
    private MetroSyncCredentials? _credentials;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendClipboardImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachFilesCommand))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendClipboardImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachFilesCommand))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isPaired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    private EngineState _engineState = EngineState.Stopped;

    public bool IsConnected => IsPaired && EngineState is EngineState.Online or EngineState.Connecting or EngineState.Backoff;

    public bool CanConnect => !IsPaired;

    public bool CanSend => !IsSending && IsPaired && !string.IsNullOrWhiteSpace(InputText);

    public bool CanSendClipboardImage => !IsSending && IsPaired;

    public bool CanAttachFiles => !IsSending && IsPaired;

    public string ProfilePhotoPath => Path.Combine(_security.DirectoryPath, "profile.jpg");

    public ObservableCollection<MetroSyncMessageItem> Items { get; } = new();

    public ObservableCollection<MetroSyncMessageItem> Feed => Items;

    public override IReadOnlyList<WidgetSize> AllowedSizes =>
    [
        WidgetSize.Full      // 8x10 only
    ];

    public Task InitializationTask { get; }

    public Action? RequestSetupDialog { get; set; }

    public event EventHandler? SetupRequested;

    public MetroSyncWidgetViewModel(TileModel model)
        : this(model, null, null, null, null, null, null, null, null, null, null, null, null, null, null)
    {
    }

    internal MetroSyncWidgetViewModel(
        TileModel model,
        MetroSyncSecurity? security = null,
        MetroSyncHistoryStore? historyStore = null,
        MetroSyncThumbnails? thumbnails = null,
        MetroSyncUpdateNormalizer? normalizer = null,
        ITelegramTransport? transport = null,
        Func<string, ITelegramTransport>? transportFactory = null,
        ConduitEngineOptions? engineOptions = null,
        string? baseDirectory = null,
        Action<string>? copyTextAction = null,
        Func<BitmapSource?>? getClipboardImageAction = null,
        Func<string[]?>? pickFilesAction = null,
        Action<string>? openUrlAction = null,
        Action<string>? openFileAction = null,
        Action<string>? showInFolderAction = null,
        TimeProvider? timeProvider = null,
        Action? requestSetupDialog = null)
        : base(model)
    {
        _baseDirectory = baseDirectory;
        _security = security ?? (baseDirectory != null ? new MetroSyncSecurity(baseDirectory) : MetroSyncSecurity.Default);

        if (historyStore != null)
        {
            _historyStore = historyStore;
            _ownsHistoryStore = false;
        }
        else if (baseDirectory != null)
        {
            _historyStore = new MetroSyncHistoryStore(baseDirectory);
            _ownsHistoryStore = true;
        }
        else
        {
            _historyStore = MetroSyncHistoryStore.Default;
            _ownsHistoryStore = false;
        }

        _thumbnails = thumbnails ?? (baseDirectory != null ? new MetroSyncThumbnails(baseDirectory) : MetroSyncThumbnails.Default);
        _normalizer = normalizer ?? new MetroSyncUpdateNormalizer();
        _transport = transport;
        _transportFactory = transportFactory;
        _engineOptions = engineOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _copyTextAction = copyTextAction ?? DefaultCopyText;
        _getClipboardImageAction = getClipboardImageAction ?? DefaultGetClipboardImage;
        _pickFilesAction = pickFilesAction ?? DefaultPickFiles;
        _openUrlAction = openUrlAction ?? DefaultOpenUrl;
        _openFileAction = openFileAction ?? DefaultOpenFile;
        _showInFolderAction = showInFolderAction ?? DefaultShowInFolder;

        RequestSetupDialog = requestSetupDialog;
        InitializationTask = InitializeInternalAsync();
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            if (_security.TryLoadCredentials(out var credentials))
            {
                _credentials = credentials;
                await StartEngineAsync().ConfigureAwait(false);
            }
            else
            {
                RunOnUIThread(() =>
                {
                    IsPaired = false;
                    EngineState = EngineState.Stopped;
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Initialization cancelled
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSyncViewModel initialization failed");
            RunOnUIThread(() =>
            {
                ErrorMessage = RedactBotToken(ex.Message);
            });
        }
    }

    public async Task ConnectAsync(MetroSyncCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _security.SaveCredentials(credentials.BotToken, credentials.AllowedUserId);
        _credentials = credentials;
        await StartEngineAsync().ConfigureAwait(false);
    }

    private async Task StartEngineAsync()
    {
        await _engineLock.WaitAsync(_lifecycleCts.Token).ConfigureAwait(false);
        try
        {
            if (_lifecycleCts.IsCancellationRequested || _isDisposed) return;
            await StopEngineCoreAsync().ConfigureAwait(false);
            if (_lifecycleCts.IsCancellationRequested || _isDisposed) return;

            if (_credentials == null) return;

            // Load latest 50 history items if feed is empty
            if (Items.Count == 0)
            {
                var history = await _historyStore.LoadLatestAsync(InitialHistoryCount, _lifecycleCts.Token).ConfigureAwait(false);
                await RunOnUIThreadAsync(() =>
                {
                    Items.Clear();
                    foreach (var item in history)
                    {
                        HydrateThumbnail(item);
                        Items.Add(item);
                    }
                    while (Items.Count > MaxFeedItemCount)
                    {
                        Items.RemoveAt(0);
                    }
                }).ConfigureAwait(false);
            }

            // Create transport if not injected
            if (_transport == null)
            {
                _ownedClient = TelegramHttpTransport.CreateDedicatedClient();
                _transport = _transportFactory != null
                    ? _transportFactory(_credentials.BotToken)
                    : new TelegramHttpTransport(_ownedClient, new Uri("https://api.telegram.org"), _credentials.BotToken);
            }

            _downloader = new MetroSyncDownloader(_transport, _baseDirectory);
            _outboundService = new MetroSyncOutboundService(_transport, _normalizer, _historyStore, _thumbnails);

            long lastAck = await _historyStore.LoadLastAcknowledgedUpdateIdAsync(_lifecycleCts.Token).ConfigureAwait(false);
            long initialOffset = lastAck > 0 ? lastAck + 1 : 0;

            var gate = new SenderGate(_credentials.AllowedUserId);
            var options = _engineOptions ?? new ConduitEngineOptions
            {
                InitialOffset = initialOffset
            };

            _engine = new ConduitEngine(_transport, gate, _normalizer, _historyStore, options, _timeProvider);
            _engine.StateChanged += HandleEngineStateChanged;
            _engine.OffsetChanged += HandleEngineOffsetChanged;

            var itemsReader = _engine.Items;
            var lifecycleToken = _lifecycleCts.Token;
            _consumerTask = Task.Run(() => ConsumeEngineItemsAsync(itemsReader, lifecycleToken), lifecycleToken);
            _engine.Start();

            RunOnUIThread(() =>
            {
                IsPaired = true;
                EngineState = _engine.State;
            });
        }
        finally
        {
            try { _engineLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task StopEngineCoreAsync()
    {
        if (_engine != null)
        {
            _engine.StateChanged -= HandleEngineStateChanged;
            _engine.OffsetChanged -= HandleEngineOffsetChanged;
            try
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                _engine.Dispose();
            }
            _engine = null;
        }

        if (_consumerTask != null)
        {
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignored on teardown
            }
            _consumerTask = null;
        }

        if (_ownedClient != null)
        {
            _ownedClient.Dispose();
            _ownedClient = null;
            _transport = null;
        }

        _downloader = null;
        _outboundService = null;

        RunOnUIThread(() =>
        {
            EngineState = EngineState.Stopped;
        });
    }

    private async Task ConsumeEngineItemsAsync(ChannelReader<MetroSyncMessageItem> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    if (ct.IsCancellationRequested) return;

                    // Hydrate incoming photo thumbnails: check disk cache first, download if absent
                    if (item is ImagePayloadItem imageItem &&
                        string.IsNullOrEmpty(imageItem.ThumbnailFilePath) &&
                        !string.IsNullOrEmpty(imageItem.FileId))
                    {
                        var cachedPath = _thumbnails.GetThumbnailPath(imageItem.MessageId);
                        if (File.Exists(cachedPath))
                        {
                            imageItem.ThumbnailFilePath = cachedPath;
                        }
                        else if (_transport != null)
                        {
                            try
                            {
                                var fileInfo = await _transport.GetFileAsync(imageItem.FileId, ct).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(fileInfo.FilePath))
                                {
                                    using var ms = new MemoryStream();
                                    await _transport.DownloadAsync(fileInfo.FilePath, ms, 20 * 1024 * 1024, ct).ConfigureAwait(false);
                                    ms.Position = 0;
                                    var thumbPath = await _thumbnails.SaveThumbnailAsync(imageItem.MessageId, ms, ct).ConfigureAwait(false);
                                    imageItem.ThumbnailFilePath = thumbPath;
                                }
                            }
                            catch (Exception ex)
                            {
                                Safe.Log(ex, "Failed to download thumbnail for incoming Telegram image");
                            }
                        }
                    }

                    HydrateThumbnail(item);
                    AddToFeed(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Error in MetroSync engine item consumer");
        }
    }

    private void HydrateThumbnail(MetroSyncMessageItem item)
    {
        if (item is ImagePayloadItem imageItem && imageItem.ThumbnailImage == null)
        {
            if (string.IsNullOrEmpty(imageItem.ThumbnailFilePath))
            {
                var candidate = _thumbnails.GetThumbnailPath(imageItem.MessageId);
                if (File.Exists(candidate))
                {
                    imageItem.ThumbnailFilePath = candidate;
                }
            }

            if (!string.IsNullOrEmpty(imageItem.ThumbnailFilePath))
            {
                var bmp = MetroSyncThumbnails.LoadThumbnail(imageItem.ThumbnailFilePath);
                if (bmp != null)
                {
                    RunOnUIThread(() => imageItem.ThumbnailImage = bmp);
                }
            }
        }
    }

    internal void AddToFeed(MetroSyncMessageItem item)
    {
        HydrateThumbnail(item);
        RunOnUIThread(() =>
        {
            Items.Add(item);
            while (Items.Count > MaxFeedItemCount)
            {
                Items.RemoveAt(0);
            }
        });
    }

    private void HandleEngineStateChanged(EngineState state)
    {
        RunOnUIThread(() =>
        {
            EngineState = state;
        });
    }

    private void HandleEngineOffsetChanged(long newOffset)
    {
        long lastAck = newOffset - 1;
        if (lastAck > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _historyStore.SaveLastAcknowledgedUpdateIdAsync(lastAck, _lifecycleCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Safe.Log(ex, "Failed to persist acknowledged update ID offset");
                }
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (IsSending) return;

        var text = InputText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > MaxTextLength)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = $"Message exceeds {MaxTextLength} characters.";
            });
            return;
        }

        if (_credentials == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Not connected.";
            });
            return;
        }

        if (_outboundService == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Outbound service unavailable.";
            });
            return;
        }

        RunOnUIThread(() =>
        {
            ErrorMessage = null;
            IsSending = true;
        });

        try
        {
            var sentItem = await _outboundService.SendTextAsync(
                _credentials.AllowedUserId,
                text,
                _lifecycleCts.Token).ConfigureAwait(false);

            RunOnUIThread(() =>
            {
                InputText = string.Empty;
                AddToFeed(sentItem);
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = RedactBotToken(ex.Message);
            });
        }
        finally
        {
            RunOnUIThread(() =>
            {
                IsSending = false;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendClipboardImage))]
    public async Task SendClipboardImageAsync()
    {
        if (IsSending) return;

        var bitmap = _getClipboardImageAction();
        if (bitmap == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "No image found on clipboard.";
            });
            return;
        }

        if (_credentials == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Not connected.";
            });
            return;
        }

        if (_outboundService == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Outbound service unavailable.";
            });
            return;
        }

        RunOnUIThread(() =>
        {
            ErrorMessage = null;
            IsSending = true;
        });

        try
        {
            var sentItem = await _outboundService.SendPhotoAsync(
                _credentials.AllowedUserId,
                bitmap,
                caption: null,
                _lifecycleCts.Token).ConfigureAwait(false);

            HydrateThumbnail(sentItem);
            AddToFeed(sentItem);
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = RedactBotToken(ex.Message);
            });
        }
        finally
        {
            RunOnUIThread(() =>
            {
                IsSending = false;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanAttachFiles))]
    public async Task AttachFilesAsync()
    {
        if (IsSending) return;

        if (_credentials == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Not connected.";
            });
            return;
        }

        if (_outboundService == null)
        {
            RunOnUIThread(() =>
            {
                ErrorMessage = "Outbound service unavailable.";
            });
            return;
        }

        var files = _pickFilesAction();
        if (files == null || files.Length == 0) return;

        RunOnUIThread(() =>
        {
            ErrorMessage = null;
            IsSending = true;
        });

        try
        {
            foreach (var file in files)
            {
                try
                {
                    var sentItem = await _outboundService.SendFileAsync(
                        _credentials.AllowedUserId,
                        file,
                        caption: null,
                        _lifecycleCts.Token).ConfigureAwait(false);

                    AddToFeed(sentItem);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RunOnUIThread(() =>
                    {
                        ErrorMessage = RedactBotToken(ex.Message);
                    });
                }
            }
        }
        finally
        {
            RunOnUIThread(() =>
            {
                IsSending = false;
            });
        }
    }

    [RelayCommand]
    public void CopyText(object? parameter)
    {
        string? textToCopy = null;
        if (parameter is TextPayloadItem textItem)
        {
            textToCopy = textItem.Text;
        }
        else if (parameter is ImagePayloadItem imageItem)
        {
            textToCopy = imageItem.Caption;
        }
        else if (parameter is FilePayloadItem fileItem)
        {
            textToCopy = !string.IsNullOrWhiteSpace(fileItem.Caption) ? fileItem.Caption : fileItem.FileName;
        }
        else if (parameter is string str)
        {
            textToCopy = str;
        }
        else if (parameter != null)
        {
            textToCopy = parameter.ToString();
        }

        if (string.IsNullOrEmpty(textToCopy)) return;

        _copyTextAction(textToCopy);
    }

    [RelayCommand]
    public void OpenLink(object? parameter)
    {
        string? rawUrl = null;
        if (parameter is TextPayloadItem textItem)
        {
            rawUrl = textItem.FirstUrl ?? ExtractFirstUrl(textItem.Text);
        }
        else if (parameter is ImagePayloadItem imageItem)
        {
            rawUrl = ExtractFirstUrl(imageItem.Caption);
        }
        else if (parameter is FilePayloadItem fileItem)
        {
            rawUrl = ExtractFirstUrl(fileItem.Caption);
        }
        else if (parameter is string str)
        {
            rawUrl = str;
        }
        else if (parameter is Uri uri)
        {
            rawUrl = uri.AbsoluteUri;
        }

        if (string.IsNullOrWhiteSpace(rawUrl)) return;

        string? candidate = rawUrl.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var validatedUri) ||
            (validatedUri.Scheme != Uri.UriSchemeHttp && validatedUri.Scheme != Uri.UriSchemeHttps))
        {
            candidate = ExtractFirstUrl(candidate);
            if (candidate == null ||
                !Uri.TryCreate(candidate, UriKind.Absolute, out validatedUri) ||
                (validatedUri.Scheme != Uri.UriSchemeHttp && validatedUri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }
        }

        _openUrlAction(validatedUri.AbsoluteUri);
    }

    [RelayCommand]
    public async Task DownloadFileAsync(FilePayloadItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileId)) return;
        if (item.IsDownloading) return;
        if (item.IsDownloaded) return;

        if (item.FileSize.HasValue && item.FileSize.Value > MetroSyncDownloader.MaxDownloadSizeBytes)
        {
            RunOnUIThread(() =>
            {
                item.ErrorMessage = "Too large for bot download (20 MB limit)";
            });
            return;
        }

        if (_downloader == null)
        {
            RunOnUIThread(() =>
            {
                item.ErrorMessage = "Downloader unavailable.";
            });
            return;
        }

        RunOnUIThread(() =>
        {
            item.ErrorMessage = null;
        });

        try
        {
            await _downloader.DownloadFileAsync(item, ct: _lifecycleCts.Token).ConfigureAwait(false);
        }
        catch (FileTooLargeException)
        {
            RunOnUIThread(() =>
            {
                item.ErrorMessage = "Too large for bot download (20 MB limit)";
                item.IsDownloading = false;
            });
        }
        catch (OperationCanceledException)
        {
            RunOnUIThread(() =>
            {
                item.IsDownloading = false;
            });
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                item.ErrorMessage = "Download failed.";
                item.IsDownloading = false;
            });
            Safe.Log(ex, "MetroSync file download failed");
        }
    }

    [RelayCommand]
    public void OpenFile(object? parameter)
    {
        string? localPath = null;
        if (parameter is FilePayloadItem fileItem)
        {
            localPath = fileItem.LocalPath;
        }
        else if (parameter is string str)
        {
            localPath = str;
        }

        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)) return;

        var downloadsDir = _downloader?.DownloadsDirectory ??
            Path.Combine(_baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync"), "Downloads");

        var normalizedTarget = Path.GetFullPath(localPath);
        var normalizedDir = Path.GetFullPath(downloadsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!normalizedTarget.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _openFileAction(normalizedTarget);
    }

    [RelayCommand]
    public void ShowInFolder(object? parameter)
    {
        string? localPath = null;
        if (parameter is FilePayloadItem fileItem)
        {
            localPath = fileItem.LocalPath;
        }
        else if (parameter is string str)
        {
            localPath = str;
        }

        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)) return;

        var downloadsDir = _downloader?.DownloadsDirectory ??
            Path.Combine(_baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync"), "Downloads");

        var normalizedTarget = Path.GetFullPath(localPath);
        var normalizedDir = Path.GetFullPath(downloadsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!normalizedTarget.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _showInFolderAction(normalizedTarget);
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        try
        {
            _security.Delete();
            _historyStore.DeleteState();
            try
            {
                var profilePath = ProfilePhotoPath;
                if (File.Exists(profilePath))
                {
                    File.Delete(profilePath);
                }
            }
            catch
            {
            }

            await _engineLock.WaitAsync(_lifecycleCts.Token).ConfigureAwait(false);
            try
            {
                await StopEngineCoreAsync().ConfigureAwait(false);
                _credentials = null;

                RunOnUIThread(() =>
                {
                    Items.Clear();
                    IsPaired = false;
                    EngineState = EngineState.Stopped;
                    ErrorMessage = null;
                });
            }
            finally
            {
                try { _engineLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown cancellation
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSync Disconnect failed");
            RunOnUIThread(() =>
            {
                ErrorMessage = RedactBotToken(ex.Message);
            });
        }
    }

    [RelayCommand]
    public void OpenSetup()
    {
        RequestSetupDialog?.Invoke();
        SetupRequested?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<Control> GetContextMenuItems()
    {
        if (IsPaired)
        {
            yield return WidgetMenu.Item("Disconnect", SymbolRegular.PlugDisconnected24, () => DisconnectCommand.Execute(null), isDestructive: true);
        }
        else
        {
            yield return WidgetMenu.Item("Connect...", SymbolRegular.PlugConnected24, () => OpenSetup());
        }
    }

    public static string? ExtractFirstUrl(string? text) => TextPayloadItem.ExtractFirstUrl(text);

    public static string RedactBotToken(string text, string? botToken = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrWhiteSpace(botToken))
        {
            text = text.Replace(botToken, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }
        text = BotUrlTokenRegex().Replace(text, "/bot[REDACTED]");
        return text;
    }

    public string RedactBotToken(string text) => RedactBotToken(text, _credentials?.BotToken);

    private void RunOnUIThread(Action action)
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _ = dispatcher.InvokeAsync(action, DispatcherPriority.Background);
            }
        }
        else
        {
            action();
        }
    }

    private async Task RunOnUIThreadAsync(Action action)
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                await dispatcher.InvokeAsync(action, DispatcherPriority.Background);
            }
        }
        else
        {
            action();
        }
    }

    private static void DefaultCopyText(string text)
    {
        Action copyAction = () =>
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (COMException) when (i < 4)
                {
                    Thread.Sleep(20);
                }
                catch (Exception ex)
                {
                    Safe.Log(ex, "Failed to copy text to Windows clipboard");
                    return;
                }
            }
        };

        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess()) copyAction();
            else _ = dispatcher.InvokeAsync(copyAction, DispatcherPriority.Background);
        }
        else
        {
            copyAction();
        }
    }

    private static BitmapSource? DefaultGetClipboardImage()
    {
        return SafeClipboard.TryGetFrozenImage(SafeClipboard.GetDataObject());
    }

    private static string[]? DefaultPickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    private static void DefaultOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to open URL in default browser");
        }
    }

    private static void DefaultOpenFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to open file via default shell verb");
        }
    }

    private static void DefaultShowInFolder(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to select file in Windows Explorer");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (disposing)
        {
            try
            {
                _lifecycleCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            if (_engine != null)
            {
                _engine.StateChanged -= HandleEngineStateChanged;
                _engine.OffsetChanged -= HandleEngineOffsetChanged;
                _engine.Dispose();
                _engine = null;
            }

            if (_consumerTask is { IsFaulted: true } task)
            {
                _ = task.Exception;
            }

            _ownedClient?.Dispose();
            _ownedClient = null;
            _transport = null;
            _downloader = null;
            _outboundService = null;

            if (_ownsHistoryStore)
            {
                _historyStore.Dispose();
            }

            _lifecycleCts.Dispose();
            _engineLock.Dispose();
        }

        base.Dispose(disposing);
    }
}