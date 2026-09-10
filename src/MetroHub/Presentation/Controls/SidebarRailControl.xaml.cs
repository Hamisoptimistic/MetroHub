using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MetroHub.Presentation.Controls
{
    public partial class SidebarRailControl : UserControl
    {
        public event EventHandler? AppsToggleRequested;

        public SidebarRailControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ShortcutsScrollViewer != null && ShortcutsScrollViewer.ScrollableHeight > 0)
            {
                ShortcutsScrollViewer.ScrollToBottom();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ShortcutsScrollViewer != null && ShortcutsScrollViewer.ScrollableHeight > 0)
            {
                ShortcutsScrollViewer.ScrollToBottom();
            }

            try
            {
                var userName = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    UserButton.ToolTip = userName;
                }
            }
            catch
            {
                // Fallback tooltip remains "Account"
            }
        }

        public void SetAppsDrawerActive(bool isOpen)
        {
            AppsToggleButton.Tag = isOpen ? "Active" : null;
        }

        private void OnAppsToggleClick(object sender, RoutedEventArgs e)
        {
            AppsToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnUserClick(object sender, RoutedEventArgs e)
        {
            LaunchUriOrPath("ms-settings:yourinfo", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        private void OnDocumentsClick(object sender, RoutedEventArgs e)
        {
            LaunchFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        private void OnDownloadsClick(object sender, RoutedEventArgs e)
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            LaunchFolder(downloads);
        }

        private void OnMusicClick(object sender, RoutedEventArgs e)
        {
            LaunchFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        }

        private void OnPicturesClick(object sender, RoutedEventArgs e)
        {
            LaunchFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        }

        private void OnVideosClick(object sender, RoutedEventArgs e)
        {
            LaunchFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        }

        private void OnControlsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("control.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open Control Panel: {ex.Message}");
            }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            LaunchUriOrPath("ms-settings:", null);
        }

        private void OnSecurityClick(object sender, RoutedEventArgs e)
        {
            LaunchUriOrPath("windowsdefender:", null);
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
                // Standard Windows command to enter sleep state
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

        private void LaunchFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch folder '{path}': {ex.Message}");
            }
        }

        private void LaunchUriOrPath(string uri, string? fallbackPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch
            {
                if (!string.IsNullOrEmpty(fallbackPath))
                {
                    LaunchFolder(fallbackPath);
                }
            }
        }
    }
}
