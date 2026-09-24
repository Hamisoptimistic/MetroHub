using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls;

public partial class SidebarRailControl : UserControl
{
    public event EventHandler? AppsToggleRequested;
    public event EventHandler? PinToggled;
    public event EventHandler? ShortcutsChanged;
    public event EventHandler<string?>? AddWebLinkRequested;

    public bool IsPinned { get; private set; } = false;

    public ObservableCollection<SidebarShortcutItem> Shortcuts { get; } = new();

    private AppSettings? _settings;
    private Point _dragStartPoint;
    private SidebarShortcutItem? _draggedItem;
    private bool _isDragging;
    private int _currentDropIndex = -1;
    private const string ShortcutDataFormat = "MetroHub.SidebarShortcut";

    public SidebarRailControl()
    {
        InitializeComponent();
        ShortcutsItemsControl.ItemsSource = Shortcuts;
    }

    public void InitializeSettings(AppSettings settings)
    {
        _settings = settings;
        IsPinned = settings.SidebarPinned;
        UpdatePinVisuals();

        Shortcuts.Clear();
        if (settings.SidebarShortcuts != null)
        {
            foreach (var item in settings.SidebarShortcuts.OrderBy(s => s.SortOrder))
            {
                Shortcuts.Add(item);
            }
        }
    }

    public void SetAppsDrawerActive(bool isActive)
    {
        if (AppsToggleButton != null)
        {
            AppsToggleButton.Tag = isActive ? "Active" : null;
        }
    }

    public void SetPinnedState(bool pinned)
    {
        IsPinned = pinned;
        UpdatePinVisuals();
        if (_settings != null)
        {
            _settings.SidebarPinned = pinned;
            StorageService.SaveSettings(_settings);
        }
    }

    private void UpdatePinVisuals()
    {
        if (PinButton != null)
        {
            PinButton.Tag = IsPinned ? "Active" : null;
            PinButton.ToolTip = IsPinned ? "Unpin sidebar" : "Pin sidebar open";
        }
        if (PinIcon != null)
        {
            PinIcon.Symbol = IsPinned ? Wpf.Ui.Controls.SymbolRegular.PinOff24 : Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
    }

    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        SetPinnedState(!IsPinned);
        PinToggled?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppsToggleClick(object sender, RoutedEventArgs e)
    {
        AppsToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        if (sender is FrameworkElement fe && (fe.DataContext is SidebarShortcutItem item || (fe.Tag is SidebarShortcutItem tagItem && (item = tagItem) != null)))
        {
            _draggedItem = item;
        }
        else
        {
            _draggedItem = null;
        }
    }

    private void OnItemPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedItem = null;
        _isDragging = false;
        HideDragIndicator();
    }

    private void OnItemPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null || _isDragging)
            return;

        Point currentPoint = e.GetPosition(this);
        Vector diff = _dragStartPoint - currentPoint;

        // Intentional threshold (12px) ensures normal clicks never trigger drag mode or indicator flashes
        double threshold = Math.Max(12.0, SystemParameters.MinimumVerticalDragDistance * 2.5);
        if (Math.Abs(diff.Y) > threshold || Math.Abs(diff.X) > threshold)
        {
            _isDragging = true;
            try
            {
                var data = new DataObject(ShortcutDataFormat, _draggedItem);
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SidebarRail] DragDrop exception: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
                _draggedItem = null;
                _currentDropIndex = -1;
                HideDragIndicator();
            }
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (sender is FrameworkElement fe && (fe.DataContext is SidebarShortcutItem item || (fe.Tag is SidebarShortcutItem tagItem && (item = tagItem) != null)))
            {
                if (!item.IsSeparator)
                {
                    LaunchShortcutInNewWindow(item);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SidebarShortcutItem item })
        {
            if (item.IsSeparator) return;

            bool runAsAdmin = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            LaunchShortcut(item, runAsAdmin);
        }
    }

    private void LaunchShortcut(SidebarShortcutItem item, bool runAsAdmin = false)
    {
        try
        {
            if (item.IsSeparator) return;

            string target = item.Target;
            if (string.IsNullOrWhiteSpace(target)) return;

            var psi = new ProcessStartInfo
            {
                UseShellExecute = true
            };

            if (runAsAdmin)
            {
                psi.Verb = "runas";
            }

            switch (item.TargetType)
            {
                case SidebarShortcutType.SystemFolder:
                case SidebarShortcutType.CustomFolder:
                    if (Directory.Exists(target))
                    {
                        psi.FileName = "explorer.exe";
                        psi.Arguments = $"\"{target}\"";
                    }
                    else
                    {
                        psi.FileName = "explorer.exe";
                    }
                    break;

                case SidebarShortcutType.WebUrl:
                    psi.FileName = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                   target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? target
                        : "https://" + target;
                    break;

                case SidebarShortcutType.Application:
                case SidebarShortcutType.Command:
                default:
                    psi.FileName = target;
                    break;
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SidebarRail] Failed to launch '{item.Title}': {ex.Message}");
        }
    }

    private void LaunchShortcutInNewWindow(SidebarShortcutItem item)
    {
        try
        {
            if (item.IsSeparator) return;

            string target = item.Target;
            if (string.IsNullOrWhiteSpace(target)) return;

            switch (item.TargetType)
            {
                case SidebarShortcutType.SystemFolder:
                case SidebarShortcutType.CustomFolder:
                    if (Directory.Exists(target))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{target}\"") { UseShellExecute = true });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "/n") { UseShellExecute = true });
                    }
                    break;

                case SidebarShortcutType.WebUrl:
                    string webTarget = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                       target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? target
                        : "https://" + target;
                    Process.Start(new ProcessStartInfo(webTarget) { UseShellExecute = true });
                    break;

                case SidebarShortcutType.Application:
                case SidebarShortcutType.Command:
                default:
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SidebarRail] Failed to launch in new window '{item.Title}': {ex.Message}");
        }
    }

    private void OnAddShortcutClick(object sender, RoutedEventArgs e)
    {
        OpenAddShortcutContextMenu();
    }

    private void OnAddShortcutRightClick(object sender, MouseButtonEventArgs e)
    {
        OpenAddShortcutContextMenu();
        e.Handled = true;
    }

    private void OpenAddShortcutContextMenu()
    {
        if (AddShortcutButton.ContextMenu != null)
        {
            AddShortcutButton.ContextMenu.PlacementTarget = AddShortcutButton;
            AddShortcutButton.ContextMenu.Placement = PlacementMode.Right;
            AddShortcutButton.ContextMenu.IsOpen = true;
        }
    }

    private void OnAddSystemFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            var parts = tag.Split('|');
            if (parts.Length >= 3)
            {
                string key = parts[0];
                string title = parts[1];
                string icon = parts[2];

                string target = key switch
                {
                    "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "Settings" => "ms-settings:",
                    "ControlPanel" => "control.exe",
                    "Security" => "windowsdefender:",
                    _ => "explorer.exe"
                };

                var item = new SidebarShortcutItem
                {
                    Title = title,
                    Target = target,
                    TargetType = (key == "ControlPanel" || key == "Security" || key == "Settings") 
                        ? SidebarShortcutType.Command 
                        : SidebarShortcutType.SystemFolder,
                    IconSymbol = icon,
                    SortOrder = Shortcuts.Count,
                    IsRemovable = true
                };

                Shortcuts.Add(item);
                PersistShortcuts();
                ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnAddWebLinkClick(object sender, RoutedEventArgs e)
    {
        AddWebLinkRequested?.Invoke(this, null);
    }

    private void OnBrowseLocalFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Folder to Pin to Sidebar"
            };

            var window = Window.GetWindow(this);
            if (dialog.ShowDialog(window) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                AddPathAsShortcut(dialog.FolderName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SidebarRail] Failed to browse folder: {ex.Message}");
        }
    }

    private void OnBrowseAppClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Application to Pin to Sidebar",
                Filter = "Applications (*.exe;*.lnk)|*.exe;*.lnk|All Files (*.*)|*.*"
            };

            var window = Window.GetWindow(this);
            if (dialog.ShowDialog(window) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
            {
                AddPathAsShortcut(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SidebarRail] Failed to browse app: {ex.Message}");
        }
    }

    private void OnRemoveShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            Shortcuts.Remove(item);
            ReindexSortOrders();
            PersistShortcuts();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            int index = Shortcuts.IndexOf(item);
            if (index > 0)
            {
                Shortcuts.Move(index, index - 1);
                ReindexSortOrders();
                PersistShortcuts();
                ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            int index = Shortcuts.IndexOf(item);
            if (index >= 0 && index < Shortcuts.Count - 1)
            {
                Shortcuts.Move(index, index + 1);
                ReindexSortOrders();
                PersistShortcuts();
                ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static SidebarShortcutItem? GetShortcutFromMenuItem(MenuItem menuItem)
    {
        if (menuItem.DataContext is SidebarShortcutItem directItem)
        {
            return directItem;
        }

        DependencyObject? current = menuItem;
        while (current != null)
        {
            if (current is ContextMenu contextMenu && contextMenu.PlacementTarget is FrameworkElement target)
            {
                return target.Tag as SidebarShortcutItem;
            }

            if (current is MenuItem parentMenuItem)
            {
                if (parentMenuItem.DataContext is SidebarShortcutItem item)
                {
                    return item;
                }
                current = parentMenuItem.Parent as DependencyObject;
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }

        return null;
    }

    private void OnChangeIconClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            if (menuItem.Tag is string newSymbol && !string.IsNullOrWhiteSpace(newSymbol))
            {
                item.IconSymbol = newSymbol;
                item.CustomIconPath = null;
                PersistShortcuts();
                ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ReindexSortOrders()
    {
        for (int i = 0; i < Shortcuts.Count; i++)
        {
            Shortcuts[i].SortOrder = i;
        }
    }

    private void PersistShortcuts()
    {
        if (_settings != null)
        {
            _settings.SidebarShortcuts = Shortcuts.ToList();
            StorageService.SaveSettings(_settings);
        }
    }

    public void AddShortcutItem(SidebarShortcutItem item, int? insertIndex = null)
    {
        int targetIndex = insertIndex.HasValue
            ? Math.Clamp(insertIndex.Value, 0, Shortcuts.Count)
            : Shortcuts.Count;

        Shortcuts.Insert(targetIndex, item);
        ReindexSortOrders();
        PersistShortcuts();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveShortcutItem(SidebarShortcutItem item)
    {
        if (Shortcuts.Remove(item))
        {
            ReindexSortOrders();
            PersistShortcuts();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }

    public void SaveShortcutsState()
    {
        PersistShortcuts();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenInTerminalClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            string folder = item.Target;
            if (Directory.Exists(folder))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
                }
                catch
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"Set-Location '{folder}'\"") { UseShellExecute = true });
                    }
                    catch
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/K cd /d \"{folder}\"") { UseShellExecute = true });
                    }
                }
            }
        }
    }

    private void OnOpenInNewWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            LaunchShortcutInNewWindow(item);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetShortcutFromMenuItem(menuItem) is SidebarShortcutItem item)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Target))
                {
                    Clipboard.SetText(item.Target);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SidebarRail] Failed to copy path: {ex.Message}");
            }
        }
    }

    private void OnAddSeparatorClick(object sender, RoutedEventArgs e)
    {
        var item = new SidebarShortcutItem
        {
            Title = "Divider",
            Target = string.Empty,
            TargetType = SidebarShortcutType.Separator,
            IconSymbol = string.Empty,
            SortOrder = Shortcuts.Count,
            IsRemovable = true
        };

        Shortcuts.Add(item);
        PersistShortcuts();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddPathAsShortcut(string path, int? insertIndex = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        int targetIndex = insertIndex.HasValue
            ? Math.Clamp(insertIndex.Value, 0, Shortcuts.Count)
            : Shortcuts.Count;

        if (Directory.Exists(path))
        {
            string folderName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = path;
            }

            string smartIcon = SidebarShortcutItem.DetectSmartIcon(path);
            string? customIcon = GetCustomFolderIcon(path);

            var item = new SidebarShortcutItem
            {
                Title = folderName,
                Target = path,
                TargetType = SidebarShortcutType.CustomFolder,
                IconSymbol = smartIcon,
                CustomIconPath = customIcon,
                SortOrder = targetIndex,
                IsRemovable = true
            };

            Shortcuts.Insert(targetIndex, item);
            ReindexSortOrders();
            PersistShortcuts();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (File.Exists(path))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string appName = Path.GetFileNameWithoutExtension(path);

            if (ext == ".url")
            {
                string url = ParseUrlShortcut(path);
                AddWebLinkShortcut(appName, url, null, targetIndex);
                return;
            }

            string? iconPath = null;
            try
            {
                iconPath = IconExtractorService.ExtractAndCacheIcon(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SidebarRail] Icon extraction failed for '{path}': {ex.Message}");
            }

            string defaultSymbol = ext switch
            {
                ".exe" or ".lnk" => "AppGeneric24",
                ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".md" => "Document24",
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "Image24",
                ".mp3" or ".wav" or ".flac" or ".m4a" => "MusicNote224",
                ".mp4" or ".mkv" or ".avi" or ".mov" => "Video24",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "FolderZip24",
                _ => "Document24"
            };

            var shortcutItem = new SidebarShortcutItem
            {
                Title = appName,
                Target = path,
                TargetType = SidebarShortcutType.Application,
                IconSymbol = defaultSymbol,
                CustomIconPath = iconPath,
                SortOrder = targetIndex,
                IsRemovable = true
            };

            Shortcuts.Insert(targetIndex, shortcutItem);
            ReindexSortOrders();
            PersistShortcuts();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string? GetCustomFolderIcon(string folderPath)
    {
        try
        {
            string iniPath = Path.Combine(folderPath, "desktop.ini");
            if (File.Exists(iniPath))
            {
                return IconExtractorService.ExtractAndCacheIcon(folderPath);
            }

            string folderIco = Path.Combine(folderPath, "folder.ico");
            if (File.Exists(folderIco))
            {
                return IconExtractorService.ExtractAndCacheIcon(folderIco);
            }

            string iconIco = Path.Combine(folderPath, "icon.ico");
            if (File.Exists(iconIco))
            {
                return IconExtractorService.ExtractAndCacheIcon(iconIco);
            }
        }
        catch { }
        return null;
    }

    private static string ParseUrlShortcut(string urlFilePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlFilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(4).Trim();
                }
            }
        }
        catch { }
        return urlFilePath;
    }

    private void OnShortcutsDragOver(object sender, DragEventArgs e)
    {
        bool isInternal = e.Data.GetDataPresent(ShortcutDataFormat);
        bool isFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        bool isText = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);

        if (!isInternal && !isFile && !isText)
        {
            HideDragIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        Point mousePos = e.GetPosition(ShortcutsContainer);
        int targetIndex = -1;
        double targetY = 0;

        for (int i = 0; i < Shortcuts.Count; i++)
        {
            if (ShortcutsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container && container.IsVisible)
            {
                Point containerPos = container.TranslatePoint(new Point(0, 0), ShortcutsContainer);
                double top = containerPos.Y;
                double height = container.ActualHeight;
                double bottom = top + height;

                if (mousePos.Y >= top && mousePos.Y <= bottom)
                {
                    bool isTopHalf = mousePos.Y < (top + (height / 2.0));
                    if (isTopHalf)
                    {
                        targetIndex = i;
                        targetY = top - 1;
                    }
                    else
                    {
                        targetIndex = i + 1;
                        targetY = bottom - 1;
                    }
                    break;
                }
            }
        }

        if (targetIndex < 0 && Shortcuts.Count > 0)
        {
            if (ShortcutsItemsControl.ItemContainerGenerator.ContainerFromIndex(Shortcuts.Count - 1) is FrameworkElement lastContainer && lastContainer.IsVisible)
            {
                Point lastPos = lastContainer.TranslatePoint(new Point(0, lastContainer.ActualHeight), ShortcutsContainer);
                targetIndex = Shortcuts.Count;
                targetY = lastPos.Y + 1;
            }
        }
        else if (targetIndex < 0 && Shortcuts.Count == 0)
        {
            targetIndex = 0;
            targetY = 4;
        }

        if (isInternal)
        {
            var sourceItem = e.Data.GetData(ShortcutDataFormat) as SidebarShortcutItem;
            if (sourceItem != null)
            {
                int sourceIndex = Shortcuts.IndexOf(sourceItem);
                if (targetIndex == sourceIndex || targetIndex == sourceIndex + 1)
                {
                    HideDragIndicator();
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
            }
        }

        _currentDropIndex = targetIndex;
        if (DragInsertionTransform != null && DragInsertionLine != null)
        {
            DragInsertionTransform.Y = Math.Max(0, targetY);
            DragInsertionLine.Visibility = Visibility.Visible;
        }

        e.Effects = isInternal ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnShortcutsDragLeave(object sender, DragEventArgs e)
    {
        Point p = e.GetPosition(ShortcutsScrollViewer);
        if (p.X < 0 || p.X >= ShortcutsScrollViewer.ActualWidth || p.Y < 0 || p.Y >= ShortcutsScrollViewer.ActualHeight)
        {
            HideDragIndicator();
        }
    }

    private void HideDragIndicator()
    {
        if (DragInsertionLine != null)
        {
            DragInsertionLine.Visibility = Visibility.Collapsed;
        }
        _currentDropIndex = -1;
    }

    private void OnShortcutsDrop(object sender, DragEventArgs e)
    {
        int targetIndex = _currentDropIndex;
        HideDragIndicator();

        if (e.Data.GetDataPresent(ShortcutDataFormat))
        {
            var sourceItem = e.Data.GetData(ShortcutDataFormat) as SidebarShortcutItem;
            if (sourceItem != null && targetIndex >= 0)
            {
                int oldIndex = Shortcuts.IndexOf(sourceItem);
                if (oldIndex >= 0 && targetIndex != oldIndex && targetIndex != oldIndex + 1)
                {
                    int newIndex = targetIndex;
                    if (oldIndex < newIndex)
                    {
                        newIndex--;
                    }
                    newIndex = Math.Clamp(newIndex, 0, Shortcuts.Count - 1);

                    if (oldIndex != newIndex)
                    {
                        Shortcuts.Move(oldIndex, newIndex);
                        ReindexSortOrders();
                        PersistShortcuts();
                        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                int insertIndex = targetIndex >= 0 ? targetIndex : Shortcuts.Count;
                insertIndex = Math.Clamp(insertIndex, 0, Shortcuts.Count);

                for (int i = files.Length - 1; i >= 0; i--)
                {
                    AddPathAsShortcut(files[i], insertIndex);
                }
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text))
        {
            string? text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string trimmed = text.Trim();
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    int insertIndex = targetIndex >= 0 ? targetIndex : Shortcuts.Count;
                    insertIndex = Math.Clamp(insertIndex, 0, Shortcuts.Count);
                    AddWebLinkShortcut(string.Empty, trimmed, null, insertIndex);
                    e.Handled = true;
                }
            }
        }
    }

    public void AddWebLinkShortcut(string title, string url, string? iconPath, int? insertIndex = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string normalized = WebFaviconService.NormalizeUrl(url);
        string resolvedTitle = !string.IsNullOrWhiteSpace(title) 
            ? title 
            : WebFaviconService.InferTitleFromUrl(normalized);

        int targetIndex = insertIndex.HasValue
            ? Math.Clamp(insertIndex.Value, 0, Shortcuts.Count)
            : Shortcuts.Count;

        var item = new SidebarShortcutItem
        {
            Title = resolvedTitle,
            Target = normalized,
            TargetType = SidebarShortcutType.WebUrl,
            IconSymbol = "Globe24",
            CustomIconPath = iconPath,
            SortOrder = targetIndex,
            IsRemovable = true
        };

        Shortcuts.Insert(targetIndex, item);
        ReindexSortOrders();
        PersistShortcuts();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrWhiteSpace(iconPath))
        {
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(normalized);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.CustomIconPath = fetched;
                        PersistShortcuts();
                    });
                }
            });
        }
    }

    private void OnPowerClick(object sender, RoutedEventArgs e)
    {
        if (PowerButton.ContextMenu != null)
        {
            PowerButton.ContextMenu.PlacementTarget = PowerButton;
            PowerButton.ContextMenu.Placement = PlacementMode.Right;
            PowerButton.ContextMenu.IsOpen = true;
        }
    }

    private void OnSleepClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to suspend: {ex.Message}");
        }
    }

    private void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to shutdown: {ex.Message}");
        }
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restart: {ex.Message}");
        }
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to lock workstation: {ex.Message}");
        }
    }
}
