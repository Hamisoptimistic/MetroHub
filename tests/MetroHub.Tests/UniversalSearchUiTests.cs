using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class UniversalSearchUiTests
{
    [Fact]
    public void SearchItemRowViewModel_AppCandidate_MapsCorrectly()
    {
        var app = new CatalogItemModel
        {
            Name = "Calculator",
            TargetPath = @"C:\Windows\System32\calc.exe"
        };

        var candidate = new Candidate(
            Id: app.TargetPath,
            DisplayName: app.Name,
            FullPathOrKey: app.TargetPath,
            Category: SearchCategory.Apps,
            SourceId: "installed_apps",
            Tag: app);

        var vm = new SearchItemRowViewModel(candidate, "Apps");

        Assert.True(vm.IsApp);
        Assert.Equal(app, vm.AppModel);
        Assert.Equal("Calculator", vm.DisplayName);
        Assert.Equal(string.Empty, vm.Subtitle); // Apps hide subtitle
        Assert.Equal(Visibility.Collapsed, vm.SubtitleVisibility);
        Assert.Equal("Apps", vm.CategoryHeader);
        Assert.Equal(Visibility.Visible, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Apps24, vm.Symbol);
    }

    [Fact]
    public void SearchItemRowViewModel_FileCandidate_MapsCorrectly()
    {
        var candidate = new Candidate(
            Id: @"D:\Docs\Report.pdf",
            DisplayName: "Report.pdf",
            FullPathOrKey: @"D:\Docs\Report.pdf",
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var vm = new SearchItemRowViewModel(candidate, "");

        Assert.False(vm.IsApp);
        Assert.Null(vm.AppModel);
        Assert.Equal("Report.pdf", vm.DisplayName);
        Assert.Equal(@"D:\Docs", vm.Subtitle);
        Assert.Equal(@"D:\Docs\Report.pdf", vm.FullPath);
        Assert.Equal(Visibility.Visible, vm.SubtitleVisibility);
        Assert.Equal(string.Empty, vm.CategoryHeader);
        Assert.Equal(Visibility.Collapsed, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Document24, vm.Symbol);
        Assert.Equal(Visibility.Visible, vm.SymbolVisibility);
        Assert.Equal(Visibility.Collapsed, vm.CustomIconVisibility);
    }

    [Fact]
    public void SearchItemRowViewModel_FolderCandidate_MapsCorrectly()
    {
        var candidate = new Candidate(
            Id: @"D:\Projects\MetroHub",
            DisplayName: "MetroHub",
            FullPathOrKey: @"D:\Projects\MetroHub",
            Category: SearchCategory.Folders,
            SourceId: "everything",
            IsFolder: true);

        var vm = new SearchItemRowViewModel(candidate, "Folders");

        Assert.False(vm.IsApp);
        Assert.Null(vm.AppModel);
        Assert.Equal("MetroHub", vm.DisplayName);
        Assert.Equal(@"D:\Projects\MetroHub", vm.Subtitle);
        Assert.Equal(@"D:\Projects\MetroHub", vm.FullPath);
        Assert.Equal(Visibility.Visible, vm.SubtitleVisibility);
        Assert.Equal("Folders", vm.CategoryHeader);
        Assert.Equal(Visibility.Visible, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Folder24, vm.Symbol);
    }

    [Fact]
    public void SearchItemRowViewModel_MediaAndCodeCandidates_MapCorrectSymbols()
    {
        var mediaCandidate = new Candidate(
            Id: @"D:\Music\song.mp3",
            DisplayName: "song.mp3",
            FullPathOrKey: @"D:\Music\song.mp3",
            Category: SearchCategory.Media,
            SourceId: "everything");

        var mediaVm = new SearchItemRowViewModel(mediaCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.MusicNote224, mediaVm.Symbol);

        var codeCandidate = new Candidate(
            Id: @"D:\Code\main.rs",
            DisplayName: "main.rs",
            FullPathOrKey: @"D:\Code\main.rs",
            Category: SearchCategory.Code,
            SourceId: "everything");

        var codeVm = new SearchItemRowViewModel(codeCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Code24, codeVm.Symbol);

        var otherCandidate = new Candidate(
            Id: @"D:\Data\archive.zip",
            DisplayName: "archive.zip",
            FullPathOrKey: @"D:\Data\archive.zip",
            Category: SearchCategory.Other,
            SourceId: "everything");

        var otherVm = new SearchItemRowViewModel(otherCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Document24, otherVm.Symbol);
    }

    [Fact]
    public void AllAppsDrawerControl_InstantiationInSta_Succeeds()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            Assert.NotNull(drawer);
            Assert.False(drawer.IsOpen);
        });
    }
}
