using System;
using System.Linq;
using System.Windows.Controls;
using MetroHub.Core.Radio;
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
}
