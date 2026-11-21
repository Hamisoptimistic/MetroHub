using System;
using System.Linq;
using System.Windows.Controls;
using MetroHub.Widgets.Catalog.Radio.Services;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public class RadioStationDialogTests
{
    [Fact]
    public void RadioStationDialog_LoadsCategoriesFromCatalogService()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("ambient");

            Assert.NotNull(dlg.RadioCategoryComboBox);
            Assert.True(dlg.RadioCategories.Count >= 4);

            var categoryIds = dlg.RadioCategories.Select(c => c.Id).ToList();
            Assert.Contains("ambient", categoryIds);
            Assert.Contains("nature", categoryIds);
            Assert.Contains("lofi", categoryIds);
            Assert.Contains("coding", categoryIds);

            Assert.NotNull(dlg.RadioCategoryComboBox.SelectedItem);
            var selected = (RadioCategory)dlg.RadioCategoryComboBox.SelectedItem;
            Assert.Equal("ambient", selected.Id);

            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_PreselectsRequestedCategory()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("coding");

            Assert.NotNull(dlg.RadioCategoryComboBox.SelectedItem);
            var selected = (RadioCategory)dlg.RadioCategoryComboBox.SelectedItem;
            Assert.Equal("coding", selected.Id);

            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_FallsBackToFirstCategory_WhenUnknownCategoryPassed()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("non_existent_category_xyz");

            Assert.NotNull(dlg.RadioCategoryComboBox.SelectedItem);
            var selected = (RadioCategory)dlg.RadioCategoryComboBox.SelectedItem;
            Assert.Equal(dlg.RadioCategories.First().Id, selected.Id);

            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_UsesDisplayNameAndSelectedValuePath()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("nature");

            Assert.Equal("DisplayName", dlg.RadioCategoryComboBox.DisplayMemberPath);
            Assert.Equal("Id", dlg.RadioCategoryComboBox.SelectedValuePath);
            Assert.Equal("nature", dlg.RadioCategoryComboBox.SelectedValue);

            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_ComboBoxDimensions_MatchPrimaryActionButton()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("ambient");
            dlg.Measure(new System.Windows.Size(620, 420));
            dlg.Arrange(new System.Windows.Rect(0, 0, 620, 420));

            var combo = dlg.RadioCategoryComboBox;
            var btn = dlg.PrimaryActionButton;

            combo.Measure(new System.Windows.Size(1000, 1000));
            btn.Measure(new System.Windows.Size(1000, 1000));

            Assert.Equal(32.0, combo.DesiredSize.Height);
            Assert.Equal(32.0, btn.DesiredSize.Height);
            Assert.True(combo.DesiredSize.Width >= 100.0, $"ComboBox width {combo.DesiredSize.Width} should be >= 100");
            Assert.True(btn.DesiredSize.Width >= 100.0, $"Button width {btn.DesiredSize.Width} should be >= 100");

            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_SelectRadioSearchResult_UpdatesSelectionViewFields()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("ambient");

            var itemWithBoth = new RadioSearchResultItem
            {
                Name = "SomaFM - Groove Salad [128k MP3]",
                Subtitle = "Downtempo • Electronic",
                BitrateDisplay = "128 kbps",
                ResolvedStreamUrl = "https://ice1.somafm.com/groovesalad-128-mp3",
                StationUuid = "test-uuid-1",
                BitrateKbps = 128,
                Codec = "MP3",
                CountryCode = "US",
                Tags = "downtempo, ambient"
            };

            dlg.SelectRadioSearchResult(itemWithBoth);

            Assert.Equal(System.Windows.Visibility.Visible, dlg.RadioSearchSelectionPanel.Visibility);
            Assert.Equal("SomaFM - Groove Salad [128k MP3]", dlg.RadioSelectedStationOriginalName.Text);
            Assert.Equal("https://ice1.somafm.com/groovesalad-128-mp3", dlg.RadioSelectedStationStreamUrl.Text);
            Assert.Equal("US", dlg.RadioSelectedStationCountryText.Text);
            Assert.Equal("128 kbps / MP3", dlg.RadioSelectedStationFormatText.Text);
            Assert.Equal("SomaFM - Groove Salad", dlg.RadioSearchNameInput.Text);

            var itemGlobalFallback = new RadioSearchResultItem
            {
                Name = "Ambient Stream",
                Subtitle = "Chill",
                BitrateDisplay = "Stream",
                ResolvedStreamUrl = "https://example.com/stream",
                StationUuid = "test-uuid-2",
                BitrateKbps = 0,
                Codec = null,
                CountryCode = null,
                Tags = null
            };

            dlg.SelectRadioSearchResult(itemGlobalFallback);

            Assert.Equal("Global", dlg.RadioSelectedStationCountryText.Text);
            Assert.Equal("Audio Stream", dlg.RadioSelectedStationFormatText.Text);

            dlg.Close();
        });
    }

    [Fact]
    public void RadioSearchResultItem_NotifiesPropertyChangedForPlaybackState()
    {
        var item = new RadioSearchResultItem
        {
            Name = "Test Station",
            Subtitle = "US / Pop",
            BitrateDisplay = "128 kbps",
            ResolvedStreamUrl = "https://example.com/test",
            StationUuid = "test-uuid",
            BitrateKbps = 128
        };

        var changedProperties = new System.Collections.Generic.List<string>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProperties.Add(e.PropertyName);
        };

        item.IsBuffering = true;
        item.IsPlaying = true;
        item.IsBuffering = false;

        Assert.Contains(nameof(RadioSearchResultItem.IsBuffering), changedProperties);
        Assert.Contains(nameof(RadioSearchResultItem.IsPlaying), changedProperties);
        Assert.True(item.IsPlaying);
        Assert.False(item.IsBuffering);
    }

    [Fact]
    public void RadioStreamPreviewController_DisposesCleanly()
    {
        WpfTestHost.RunSta(() =>
        {
            using var controller = new RadioStreamPreviewController();
            Assert.False(controller.IsPlaying);
            Assert.False(controller.IsBuffering);
            Assert.Null(controller.ActiveItem);

            controller.Stop();
        });
    }

    [Fact]
    public void RadioStationDialog_SearchResultTemplate_RendersButtonAndDivider()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg = new RadioStationDialog();
            dlg.Setup("ambient");
            var item = new RadioSearchResultItem
            {
                StationUuid = "test-uuid-99",
                Name = "SomaFM Groove Salad",
                Subtitle = "Downtempo • Electronic",
                BitrateDisplay = "128 kbps",
                ResolvedStreamUrl = "https://ice1.somafm.com/groovesalad-128-mp3",
                BitrateKbps = 128
            };
            dlg.RadioSearchResults.Add(item);

            dlg.Measure(new System.Windows.Size(680, 480));
            dlg.Arrange(new System.Windows.Rect(0, 0, 680, 480));
            dlg.UpdateLayout();

            var template = (System.Windows.DataTemplate)dlg.Resources["RadioSearchResultItemTemplate"];
            Assert.NotNull(template);

            var rootBorder = (Border)template.LoadContent();
            Assert.NotNull(rootBorder);
            rootBorder.DataContext = item;

            rootBorder.Measure(new System.Windows.Size(500, 100));
            rootBorder.Arrange(new System.Windows.Rect(0, 0, 500, 100));
            rootBorder.UpdateLayout();

            var grid = rootBorder.Child as Grid;
            Assert.NotNull(grid);
            Assert.Equal(4, grid.ColumnDefinitions.Count);
            Assert.Equal(new System.Windows.GridLength(42), grid.ColumnDefinitions[0].Width);
            Assert.Equal(System.Windows.GridLength.Auto, grid.ColumnDefinitions[1].Width);
            Assert.Equal(new System.Windows.GridLength(1, System.Windows.GridUnitType.Star), grid.ColumnDefinitions[2].Width);
            Assert.Equal(System.Windows.GridLength.Auto, grid.ColumnDefinitions[3].Width);

            var btn = grid.Children[0] as Button;
            Assert.NotNull(btn);
            Assert.Equal(0, Grid.GetColumn(btn));
            Assert.Equal(System.Windows.HorizontalAlignment.Stretch, btn.HorizontalAlignment);
            Assert.Equal(System.Windows.VerticalAlignment.Stretch, btn.VerticalAlignment);
            Assert.Equal(42.0, btn.ActualWidth);
            Assert.True(btn.ActualHeight > 0);

            var rect = grid.Children[1] as System.Windows.Shapes.Rectangle;
            Assert.NotNull(rect);
            Assert.Equal(1, Grid.GetColumn(rect));
            Assert.Equal(1.0, rect.Width);
            Assert.Equal(1.0, rect.ActualWidth);
            Assert.Equal(btn.ActualHeight, rect.ActualHeight);

            dlg.Close();
        });
    }
}
