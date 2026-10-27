using System.Collections.Frozen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Photos;

public sealed partial class PhotosWidgetViewModel : WidgetViewModelBase, IWidgetActionHandler, IWidgetContextMenuProvider
{
    private static readonly System.Collections.Frozen.FrozenSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _photoPaths = new();
    private readonly List<int> _shuffledIndices = new();
    private int _currentIndex = -1;
    private System.Threading.Timer? _slideshowTimer;
    private bool _isHubVisible = true;
    private bool _isTransitioning;
    private readonly Random _random = new();

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Large,   // 4x4
        WidgetSize.Banner3, // 8x3
        WidgetSize.Mega     // 8x4
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _folderPath;

    [ObservableProperty]
    private string _folderName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhotos))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _photoCount;

    [ObservableProperty]
    private ImageSource? _photoLayerA;

    [ObservableProperty]
    private ImageSource? _photoLayerB;

    [ObservableProperty]
    private bool _isDisplayingA = true;

    [ObservableProperty]
    private string? _currentPhotoPath;

    [ObservableProperty]
    private bool _isPortraitA;

    [ObservableProperty]
    private bool _isPortraitB;

    private bool _isHovered;

    [ObservableProperty]
    private string _currentPhotoName = string.Empty;

    [ObservableProperty]
    private string _counterDisplayString = string.Empty;

    [ObservableProperty]
    private int _intervalSeconds = 30;

    [ObservableProperty]
    private bool _shuffle = true;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private string _fitMode = "FitWithBlur";

    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath);

    public bool HasPhotos => PhotoCount > 0;

    public bool IsEmpty => !HasFolder || !HasPhotos;

    public PhotosWidgetViewModel(TileModel model) : base(model)
    {
        if (model.SpanX <= 4 && model.SpanY <= 2)
        {
            model.SpanX = 4;
            model.SpanY = 4;
        }

        LoadSettings(model.SettingsJson);
        IsActive = true;

        _slideshowTimer = new System.Threading.Timer(_ =>
        {
            if (!_isHubVisible || _isHovered || PhotoCount <= 1 || _isTransitioning) return;
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                if (!_isHubVisible || _isHovered || PhotoCount <= 1 || _isTransitioning) return;
                await TransitionToNextPhotoAsync(forward: true);
            });
        }, null, Timeout.Infinite, Timeout.Infinite);

        if (HasFolder)
        {
            _ = ScanFolderAsync(FolderPath!, keepIndex: false);
        }
    }

    private void StartSlideshowTimer()
    {
        if (PhotoCount <= 1 || !_isHubVisible || _isHovered) return;
        var interval = TimeSpan.FromSeconds(Math.Max(5, IntervalSeconds));
        _slideshowTimer?.Change(interval, interval);
    }

    private void StopSlideshowTimer()
    {
        _slideshowTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;

        var settings = WidgetSerializer.Deserialize<PhotosWidgetSettings>(settingsJson);
        if (settings != null)
        {
            FolderPath = settings.FolderPath;
            IntervalSeconds = settings.IntervalSeconds > 0 ? settings.IntervalSeconds : 30;
            Shuffle = settings.Shuffle;
            IncludeSubfolders = settings.IncludeSubfolders;
            FitMode = string.IsNullOrWhiteSpace(settings.FitMode) ? "FitWithBlur" : settings.FitMode;

            if (HasFolder)
            {
                FolderName = Path.GetFileName(FolderPath!.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(FolderName)) FolderName = FolderPath!;
            }
        }
    }

    public override void SaveSettings()
    {
        var settings = new PhotosWidgetSettings
        {
            FolderPath = FolderPath,
            IntervalSeconds = IntervalSeconds,
            Shuffle = Shuffle,
            IncludeSubfolders = IncludeSubfolders,
            FitMode = FitMode
        };

        Model.TargetPath = "photos";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        NotifySettingsChanged();
    }

    [RelayCommand]
    public void ChooseFolder()
    {
        using var _ = MainWindow.EnterDialogScope();

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose Photo Stream Folder",
                Multiselect = false
            };

            if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath))
            {
                dialog.InitialDirectory = FolderPath;
            }

            var owner = Application.Current?.MainWindow;
            bool? result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();

            if (result == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SetFolder(dialog.FolderName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotosWidget] Failed to open folder picker: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenCurrentPhoto()
    {
        if (string.IsNullOrWhiteSpace(CurrentPhotoPath) || !File.Exists(CurrentPhotoPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CurrentPhotoPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotosWidget] Failed to open photo: {ex.Message}");
        }
    }

    public void OnPrimaryAction()
    {
        if (IsEmpty) ChooseFolder();
        else OpenCurrentPhoto();
    }

    public IEnumerable<Control> GetContextMenuItems()
    {
        if (!IsEmpty)
        {
            yield return WidgetMenu.Item("Open Photo", SymbolRegular.Image24, OpenCurrentPhoto);
            yield return WidgetMenu.Item("Open Containing Folder", SymbolRegular.FolderOpenVertical20, () =>
            {
                if (!string.IsNullOrWhiteSpace(CurrentPhotoPath) && File.Exists(CurrentPhotoPath))
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{CurrentPhotoPath}\""); } catch { }
                }
            });
            yield return WidgetMenu.Item("Next Photo", SymbolRegular.ArrowRight24, () => _ = NextPhoto());
            yield return WidgetMenu.Divider();
        }

        yield return WidgetMenu.Item("Select Photos Folder...", SymbolRegular.FolderOpen24, ChooseFolder);
        yield return WidgetMenu.Submenu("Slide Interval", SymbolRegular.Timer24,
            WidgetMenu.Item("10 Seconds", () => SetInterval(10), isCheckable: true, isChecked: IntervalSeconds == 10),
            WidgetMenu.Item("30 Seconds (Default)", () => SetInterval(30), isCheckable: true, isChecked: IntervalSeconds == 30),
            WidgetMenu.Item("1 Minute", () => SetInterval(60), isCheckable: true, isChecked: IntervalSeconds == 60),
            WidgetMenu.Item("5 Minutes", () => SetInterval(300), isCheckable: true, isChecked: IntervalSeconds == 300));
        yield return WidgetMenu.Item("Shuffle Photos", SymbolRegular.ArrowShuffle24, ToggleShuffle, isCheckable: true, isChecked: Shuffle);
        yield return WidgetMenu.Item("Include Subfolders", SymbolRegular.Folder24, ToggleIncludeSubfolders, isCheckable: true, isChecked: IncludeSubfolders);
        yield return WidgetMenu.Submenu("Aspect Ratio & Fit", SymbolRegular.SlideSize24,
            WidgetMenu.Item("Smart Fit (With Backdrop)", () => SetFitMode("FitWithBlur"), isCheckable: true, isChecked: FitMode == "FitWithBlur"),
            WidgetMenu.Item("Fill Tile (Crop)", () => SetFitMode("Fill"), isCheckable: true, isChecked: FitMode == "Fill"));
    }

    public void SetHovered(bool hovered)
    {
        _isHovered = hovered;
        if (_isHovered)
        {
            StopSlideshowTimer();
        }
        else
        {
            if (_isHubVisible && PhotoCount > 1 && !_isTransitioning)
            {
                StartSlideshowTimer();
            }
        }
    }

    public void SetFolder(string path)
    {
        if (!Directory.Exists(path)) return;

        FolderPath = path;
        FolderName = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(FolderName)) FolderName = path;

        SaveSettings();
        _ = ScanFolderAsync(path, keepIndex: false);
    }

    public async Task ScanFolderAsync(string path, bool keepIndex = false)
    {
        if (!Directory.Exists(path))
        {
            PhotoCount = 0;
            _photoPaths.Clear();
            _shuffledIndices.Clear();
            PhotoLayerA = null;
            PhotoLayerB = null;
            StopSlideshowTimer();
            return;
        }

        var searchOption = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var paths = await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateFiles(path, "*.*", searchOption)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        });

        _photoPaths.Clear();
        _photoPaths.AddRange(paths);
        PhotoCount = _photoPaths.Count;

        BuildShuffleIndices();

        if (PhotoCount > 0)
        {
            if (!keepIndex || _currentIndex < 0 || _currentIndex >= PhotoCount)
            {
                _currentIndex = 0;
            }

            int targetIndex = Shuffle ? _shuffledIndices[_currentIndex] : _currentIndex;
            await DisplayInitialPhotoAsync(_photoPaths[targetIndex]);

            if (_isHubVisible && !_isHovered)
            {
                StartSlideshowTimer();
            }
        }
        else
        {
            PhotoLayerA = null;
            PhotoLayerB = null;
            StopSlideshowTimer();
        }
    }

    private void BuildShuffleIndices()
    {
        _shuffledIndices.Clear();
        _shuffledIndices.AddRange(Enumerable.Range(0, _photoPaths.Count));

        if (Shuffle && _shuffledIndices.Count > 1)
        {
            // Fisher-Yates shuffle
            for (int i = _shuffledIndices.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_shuffledIndices[i], _shuffledIndices[j]) = (_shuffledIndices[j], _shuffledIndices[i]);
            }
        }
    }

    private async Task DisplayInitialPhotoAsync(string filePath)
    {
        var result = await LoadDecodedPhotoAsync(filePath);
        if (result != null)
        {
            PhotoLayerA = result.Image;
            IsPortraitA = result.IsPortrait;
            IsDisplayingA = true;
            CurrentPhotoPath = filePath;
            CurrentPhotoName = Path.GetFileNameWithoutExtension(filePath);
            UpdateCounter();
        }
    }

    [RelayCommand]
    public async Task NextPhoto()
    {
        if (PhotoCount <= 1 || _isTransitioning) return;
        StopSlideshowTimer();
        await TransitionToNextPhotoAsync(forward: true);
        if (_isHubVisible && !_isHovered) StartSlideshowTimer();
    }

    [RelayCommand]
    public async Task PreviousPhoto()
    {
        if (PhotoCount <= 1 || _isTransitioning) return;
        StopSlideshowTimer();
        await TransitionToNextPhotoAsync(forward: false);
        if (_isHubVisible && !_isHovered) StartSlideshowTimer();
    }

    private async Task TransitionToNextPhotoAsync(bool forward)
    {
        if (PhotoCount <= 0 || _isTransitioning) return;
        _isTransitioning = true;

        try
        {
            if (forward)
            {
                _currentIndex++;
                if (_currentIndex >= PhotoCount)
                {
                    _currentIndex = 0;
                    if (Shuffle) BuildShuffleIndices();
                }
            }
            else
            {
                _currentIndex--;
                if (_currentIndex < 0)
                {
                    _currentIndex = PhotoCount - 1;
                }
            }

            int targetFileIndex = Shuffle ? _shuffledIndices[_currentIndex] : _currentIndex;
            string targetPath = _photoPaths[targetFileIndex];

            var result = await LoadDecodedPhotoAsync(targetPath);
            if (result == null) return;

            CurrentPhotoPath = targetPath;
            CurrentPhotoName = Path.GetFileNameWithoutExtension(targetPath);
            UpdateCounter();

            if (IsDisplayingA)
            {
                // Layer A is active -> Load into Layer B, then cross-fade to B
                PhotoLayerB = result.Image;
                IsPortraitB = result.IsPortrait;
                IsDisplayingA = false;

                await Task.Delay(900);

                if (!IsDisplayingA)
                {
                    PhotoLayerA = null;
                }
            }
            else
            {
                // Layer B is active -> Load into Layer A, then cross-fade to A
                PhotoLayerA = result.Image;
                IsPortraitA = result.IsPortrait;
                IsDisplayingA = true;

                await Task.Delay(900);

                if (IsDisplayingA)
                {
                    PhotoLayerB = null;
                }
            }
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    private void UpdateCounter()
    {
        CounterDisplayString = PhotoCount > 0 ? $"{_currentIndex + 1} / {PhotoCount}" : string.Empty;
    }

    private static async Task<DecodedPhotoResult?> LoadDecodedPhotoAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        return await Task.Run<DecodedPhotoResult?>(() =>
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // Inspect frame header without decoding pixels
                int targetWidth = 640;
                int targetHeight = 0;
                bool isPortrait = false;

                try
                {
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        int origW = frame.PixelWidth;
                        int origH = frame.PixelHeight;

                        if (origH > origW && origH > 0)
                        {
                            isPortrait = true;
                            // Portrait orientation: bound height to 640
                            targetHeight = 640;
                            targetWidth = 0;
                        }
                        else
                        {
                            isPortrait = false;
                            // Landscape orientation: bound width to 640
                            targetWidth = 640;
                            targetHeight = 0;
                        }
                    }
                }
                catch
                {
                    targetWidth = 640;
                    targetHeight = 0;
                }

                fs.Position = 0;

                // Stream directly into WIC downsampled decoder (zero MemoryStream LOH allocation)
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                if (targetWidth > 0)
                {
                    image.DecodePixelWidth = targetWidth;
                }
                else if (targetHeight > 0)
                {
                    image.DecodePixelHeight = targetHeight;
                }

                image.StreamSource = fs;
                image.EndInit();
                image.Freeze();

                return new DecodedPhotoResult(image, isPortrait);
            }
            catch
            {
                return null;
            }
        });
    }

    public void SetInterval(int seconds)
    {
        IntervalSeconds = Math.Max(5, seconds);
        if (_isHubVisible && !_isHovered && HasPhotos && PhotoCount > 1)
        {
            StartSlideshowTimer();
        }
        SaveSettings();
    }

    public void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        BuildShuffleIndices();
        SaveSettings();
    }

    public void ToggleIncludeSubfolders()
    {
        IncludeSubfolders = !IncludeSubfolders;
        SaveSettings();
        if (HasFolder) _ = ScanFolderAsync(FolderPath!, keepIndex: false);
    }

    public void SetFitMode(string mode)
    {
        FitMode = mode;
        SaveSettings();
    }

    public override void Pause()
    {
        _isHubVisible = false;
        StopSlideshowTimer();
    }

    public override void Resume()
    {
        _isHubVisible = true;
        if (HasPhotos && PhotoCount > 1 && !_isHovered)
        {
            StartSlideshowTimer();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopSlideshowTimer();
            _slideshowTimer?.Dispose();
            _slideshowTimer = null;

            _photoPaths.Clear();
            _shuffledIndices.Clear();
            PhotoLayerA = null;
            PhotoLayerB = null;
        }

        base.Dispose(disposing);
    }
}

public sealed record DecodedPhotoResult(ImageSource Image, bool IsPortrait);

