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
            Assert.Equal("128 kbps • MP3", dlg.RadioSelectedStationFormatText.Text);
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
}
