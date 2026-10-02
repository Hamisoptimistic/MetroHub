using System;
using System.Windows;
using MetroHub.Presentation.Controls;
using MetroHub.Presentation.Dialogs;
using MetroHub.Widgets.Catalog.AudioControls;
using MetroHub.Widgets.Catalog.BrightnessControls;
using MetroHub.Widgets.Catalog.CaffeineSleep;
using MetroHub.Widgets.Catalog.Calendar;
using MetroHub.Widgets.Catalog.Clock;
using MetroHub.Widgets.Catalog.Dino;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Catalog.Markdown;
using MetroHub.Widgets.Catalog.Media;
using MetroHub.Widgets.Catalog.Network;
using MetroHub.Widgets.Catalog.Notepad;
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
    [InlineData(typeof(MarkdownWidgetView))]
    [InlineData(typeof(MediaWidgetView))]
    [InlineData(typeof(NetworkWidgetView))]
    [InlineData(typeof(NotepadWidgetView))]
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
    [InlineData("CycleDotStyle")]
    [InlineData("FluentGreenToggleSwitchStyle")]
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
}
