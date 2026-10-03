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
using MetroHub.Widgets.Catalog.Photos;
using MetroHub.Widgets.Catalog.Pomodoro;
using MetroHub.Widgets.Catalog.Power;
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
    [InlineData(typeof(PhotosWidgetView))]
    [InlineData(typeof(PomodoroWidgetView))]
    [InlineData(typeof(PowerWidgetView))]
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
    [InlineData(typeof(WidgetTabStrip))]
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
}
