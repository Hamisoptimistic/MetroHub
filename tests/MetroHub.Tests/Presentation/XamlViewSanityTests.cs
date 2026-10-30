using System;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Presentation.Controls;
using MetroHub.Presentation.Dialogs;
using MetroHub.Widgets.Catalog.AudioControls;
using MetroHub.Widgets.Catalog.BrightnessControls;
using MetroHub.Widgets.Catalog.CaffeineSleep;
using MetroHub.Widgets.Catalog.Calendar;
using MetroHub.Widgets.Catalog.Clock;
using MetroHub.Widgets.Catalog.Dino;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Catalog.Media;
using MetroHub.Widgets.Catalog.Network;
using MetroHub.Widgets.Catalog.Notepad;
using MetroHub.Widgets.Catalog.Photos;
using MetroHub.Widgets.Catalog.Pomodoro;
using MetroHub.Widgets.Catalog.QuickControls;
using MetroHub.Widgets.Catalog.Quotes;
using MetroHub.Widgets.Catalog.Radio;
using MetroHub.Widgets.Catalog.Rover;
using MetroHub.Widgets.Catalog.Template;
using MetroHub.Widgets.Catalog.Weather;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Comprehensive XAML sanity test suite.
/// Validates that every widget view, shell control, and dialog instantiates its visual tree
/// and parses all StaticResource/DynamicResource references with zero XamlParseExceptions.
/// </summary>
public class XamlViewSanityTests
{
    [Theory]
    [InlineData(typeof(AudioControlsWidgetView))]
    [InlineData(typeof(BrightnessControlsWidgetView))]
    [InlineData(typeof(CaffeineSleepWidgetView))]
    [InlineData(typeof(CalendarWidgetView))]
    [InlineData(typeof(ClockWidgetView))]
    [InlineData(typeof(DinoWidgetView))]
    [InlineData(typeof(HabitWidgetView))]
    [InlineData(typeof(MediaWidgetView))]
    [InlineData(typeof(NetworkWidgetView))]
    [InlineData(typeof(NotepadWidgetView))]
    [InlineData(typeof(PhotosWidgetView))]
    [InlineData(typeof(PomodoroWidgetView))]
    [InlineData(typeof(QuickControlsWidgetView))]
    [InlineData(typeof(QuotesWidgetView))]
    [InlineData(typeof(RadioWidgetView))]
    [InlineData(typeof(RoverWidgetView))]
    [InlineData(typeof(TemplateWidgetView))]
    [InlineData(typeof(WeatherWidgetView))]
    public void CatalogWidgetView_InitializesWithoutMissingResources(Type widgetViewType)
    {
        WpfTestHost.RunSta(() =>
        {
            var view = Activator.CreateInstance(widgetViewType);
            Assert.NotNull(view);
            Assert.IsAssignableFrom<FrameworkElement>(view);
        });
    }

    [Theory]
    [InlineData(typeof(SidebarRailControl))]
    [InlineData(typeof(AllAppsDrawerControl))]
    [InlineData(typeof(TileControl))]
    public void ShellControl_InitializesWithoutMissingResources(Type controlType)
    {
        WpfTestHost.RunSta(() =>
        {
            var control = Activator.CreateInstance(controlType);
            Assert.NotNull(control);
            Assert.IsAssignableFrom<FrameworkElement>(control);
        });
    }

    [Theory]
    [InlineData(typeof(WebLinkDialog))]
    [InlineData(typeof(WeatherLocationDialog))]
    [InlineData(typeof(RadioStationDialog))]
    [InlineData(typeof(TemplateDialog))]
    public void ModalDialog_InitializesWithoutMissingResources(Type dialogType)
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = Activator.CreateInstance(dialogType);
            Assert.NotNull(dialog);
            Assert.IsAssignableFrom<Window>(dialog);
            var win = (Window)dialog;
            Assert.NotNull(win.Style);
            Assert.Equal(typeof(MetroDialog), win.Style.TargetType);
            win.ApplyTemplate();
            Assert.NotNull(win.Template);
            var closeBtn = win.Template.FindName("PART_CloseButton", win);
            Assert.NotNull(closeBtn);
        });
    }

    [Theory]
    [InlineData("ControlCornerRadius")]
    [InlineData("SystemAccentColorPrimaryBrush")]
    [InlineData("TextPrimaryBrush")]
    [InlineData("TextSecondaryBrush")]
    [InlineData("TextMutedBrush")]
    [InlineData("TextDisabledBrush")]
    [InlineData("StatusSuccessBrush")]
    [InlineData("StatusWarningBrush")]
    [InlineData("StatusDangerBrush")]
    [InlineData("WidgetIndicatorDotStyle")]
    [InlineData("DefaultButtonStyle")]
    [InlineData("AccentButtonStyle")]
    [InlineData("SubtleButtonStyle")]
    [InlineData("FluentAccentToggleSwitchStyle")]
    [InlineData("FluentAmberToggleSwitchStyle")]
    [InlineData("FluentRedToggleSwitchStyle")]
    [InlineData("SystemAccentColorSecondaryBrush")]
    [InlineData("SystemAccentColorTertiaryBrush")]
    [InlineData("SystemAccentColorForegroundBrush")]
    public void CoreThemeTokens_ExistInMergedResources(string resourceKey)
    {
        WpfTestHost.RunSta(() =>
        {
            var app = Application.Current;
            Assert.NotNull(app);
            var resource = app.TryFindResource(resourceKey);
            Assert.True(resource != null, $"Expected resource '{resourceKey}' to be defined in Application resources.");
        });
    }

    /// <summary>
    /// Validates that default styles for ListBox, ListView, and ui:ListView have native high-FPS per-pixel scrolling
    /// with zero-glide (IsVirtualizing, Recycling mode, Pixel scroll unit, Page cache length, PanningMode VerticalOnly, CanContentScroll).
    /// </summary>
    [Fact]
    public void ListControls_ReceivePerPixelVirtualizationStyleSettings()
    {
        WpfTestHost.RunSta(() =>
        {
            var lbStyle = Application.Current.TryFindResource(typeof(ListBox)) as Style;
            var lvStyle = Application.Current.TryFindResource(typeof(ListView)) as Style;

            Assert.NotNull(lbStyle);
            Assert.NotNull(lvStyle);

            var lb = new ListBox { Style = lbStyle };
            Assert.True((bool)lb.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, lb.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, lb.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, lb.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), lb.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, lb.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)lb.GetValue(ScrollViewer.CanContentScrollProperty));

            var lv = new ListView { Style = lvStyle };
            Assert.True((bool)lv.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, lv.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, lv.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, lv.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), lv.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, lv.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)lv.GetValue(ScrollViewer.CanContentScrollProperty));

            var uilvStyle = Application.Current.TryFindResource(typeof(Wpf.Ui.Controls.ListView)) as Style;
            Assert.NotNull(uilvStyle);
            var uilv = new Wpf.Ui.Controls.ListView { Style = uilvStyle };
            Assert.True((bool)uilv.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, uilv.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, uilv.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, uilv.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), uilv.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, uilv.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)uilv.GetValue(ScrollViewer.CanContentScrollProperty));

            var detailLayout = new MetroHub.Presentation.Views.Settings.Pages.SettingsDetailLayout();
            var style = detailLayout.FindResource(typeof(Wpf.Ui.Controls.ListViewItem)) as Style;
            var basedOn = style?.BasedOn;
            Setter crSetter = null;
            foreach (var s in basedOn.Setters)
            {
                if (s is Setter setter && setter.Property?.Name == "CornerRadius")
                {
                    crSetter = setter;
                    break;
                }
            }
            throw new Exception($"Property: {crSetter?.Property?.Name}, OwnerType: {crSetter?.Property?.OwnerType.FullName}, Value: {crSetter?.Value}");

            // Verify actual dialog and widget instances resolve the per-pixel virtualization settings
            var weatherDialog = new WeatherLocationDialog();
            var weatherList = weatherDialog.WeatherSuggestionsList;
            Assert.NotNull(weatherList);
            Assert.True((bool)weatherList.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, weatherList.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, weatherList.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, weatherList.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), weatherList.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, weatherList.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)weatherList.GetValue(ScrollViewer.CanContentScrollProperty));

            var radioDialog = new RadioStationDialog();
            var radioList = radioDialog.RadioSearchResultsList;
            Assert.NotNull(radioList);
            Assert.True((bool)radioList.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, radioList.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, radioList.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, radioList.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), radioList.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, radioList.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)radioList.GetValue(ScrollViewer.CanContentScrollProperty));

            var networkView = new NetworkWidgetView();
            var netList = networkView.AvailableNetworksListView;
            Assert.NotNull(netList);
            Assert.True((bool)netList.GetValue(VirtualizingPanel.IsVirtualizingProperty));
            Assert.Equal(VirtualizationMode.Recycling, netList.GetValue(VirtualizingPanel.VirtualizationModeProperty));
            Assert.Equal(ScrollUnit.Pixel, netList.GetValue(VirtualizingPanel.ScrollUnitProperty));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, netList.GetValue(VirtualizingPanel.CacheLengthUnitProperty));
            Assert.Equal(new VirtualizationCacheLength(1, 1), netList.GetValue(VirtualizingPanel.CacheLengthProperty));
            Assert.Equal(PanningMode.VerticalOnly, netList.GetValue(ScrollViewer.PanningModeProperty));
            Assert.True((bool)netList.GetValue(ScrollViewer.CanContentScrollProperty));
        });
    }

    [Fact]
    public void DialogsAndSettingsWindow_HaveDoNotRoundWindowCornerPreference()
    {
        WpfTestHost.RunSta(() =>
        {
            var metroDialog = new MetroDialog();
            Assert.Equal(Wpf.Ui.Controls.WindowCornerPreference.DoNotRound, metroDialog.WindowCornerPreference);

            var noteDialog = new NoteViewerDialog(new Core.Models.TileModel());
            Assert.Equal(Wpf.Ui.Controls.WindowCornerPreference.DoNotRound, noteDialog.WindowCornerPreference);

            var settings = new MetroHub.Presentation.Views.Settings.SettingsWindow();
            Assert.Equal(Wpf.Ui.Controls.WindowCornerPreference.DoNotRound, settings.WindowCornerPreference);
        });
    }

    [Fact]
    public void AccentButtonStyle_HasZeroBorderThickness_AndScalesWithResourceTokens()
    {
        WpfTestHost.RunSta(() =>
        {
            var btnStyle = Application.Current.TryFindResource("AccentButtonStyle") as Style;
            Assert.NotNull(btnStyle);

            var button = new Button { Style = btnStyle };
            var window = new Window { Content = button, Width = 100, Height = 100 };
            window.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            Assert.Equal(new Thickness(0), button.BorderThickness);

            var border = button.Template.FindName("BtnBorder", button) as Border;
            Assert.NotNull(border);
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(Application.Current.FindResource("ControlCornerRadius"), border.CornerRadius);

            try
            {
                // Test dynamic token scaling across all standard corner radii
                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(0);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                Assert.Equal(new CornerRadius(0), border.CornerRadius);

                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(4);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                Assert.Equal(new CornerRadius(4), border.CornerRadius);

                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(8);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                Assert.Equal(new CornerRadius(6), border.CornerRadius);
            }
            finally
            {
                // Reset back to standard subtle fluent token (2px)
                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(2);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                Assert.Equal(new CornerRadius(2), border.CornerRadius);
                window.Close();
            }
        });
    }

    [Fact]
    public void MetroSettingsCards_UseTileCornerRadiusToken()
    {
        WpfTestHost.RunSta(() =>
        {
            var cardStyle = Application.Current.FindResource(typeof(Wpf.Ui.Controls.CardControl)) as Style;
            var card = new Wpf.Ui.Controls.CardControl { Style = cardStyle };
            Assert.Equal(Application.Current.FindResource("TileCornerRadius"), card.CornerRadius);

            var expanderStyle = Application.Current.FindResource(typeof(Wpf.Ui.Controls.CardExpander)) as Style;
            var expander = new Wpf.Ui.Controls.CardExpander { Style = expanderStyle };
            Assert.Equal(Application.Current.FindResource("TileCornerRadius"), expander.CornerRadius);
        });
    }

    [Fact]
    public void DialogsAndSettingsWindow_UseDialogCornerRadiusTokens()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialogRadius = (CornerRadius)Application.Current.FindResource("DialogCornerRadius");

            // Strict Sharp Metro: Dialog and Settings surfaces are 0px sharp
            Assert.Equal(new CornerRadius(0), dialogRadius);

            var contextMenuRadius = (CornerRadius)Application.Current.FindResource("ContextMenuCornerRadius");
            var contextMenuItemRadius = (CornerRadius)Application.Current.FindResource("ContextMenuItemCornerRadius");
            Assert.Equal(new CornerRadius(8), contextMenuRadius);
            Assert.Equal(new CornerRadius(4), contextMenuItemRadius);

            var settings = new MetroHub.Presentation.Views.Settings.SettingsWindow();
            var border = settings.Content as Border;
            Assert.NotNull(border);
            Assert.Equal(dialogRadius, border.CornerRadius);

            var grid = border.Child as Grid;
            Assert.NotNull(grid);
            Assert.Equal(dialogRadius, MetroHub.Presentation.Controls.ClipHelper.GetCornerRadius(grid));

            try
            {
                // Verify that changing tile settings preserves 0px sharp dialog windows
                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(0);
                Assert.Equal(new CornerRadius(0), Application.Current.FindResource("DialogCornerRadius"));

                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(4);
                Assert.Equal(new CornerRadius(0), Application.Current.FindResource("DialogCornerRadius"));

                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(8);
                Assert.Equal(new CornerRadius(0), Application.Current.FindResource("DialogCornerRadius"));
            }
            finally
            {
                MetroHub.MainWindow.UpdateCornerRadiusResourceTokens(2);
                Assert.Equal(new CornerRadius(0), Application.Current.FindResource("DialogCornerRadius"));
            }
        });
    }
}
