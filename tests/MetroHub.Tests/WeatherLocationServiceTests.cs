using System;
using System.Text.Json;
using MetroHub.Core.Models.Geocoding;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class WeatherLocationServiceTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [InlineData("New   York", "New York")]
    [InlineData("   London  ", "London")]
    [InlineData("São   Paulo", "São Paulo")]
    [InlineData("Tokyo,\tJapan", "Tokyo, Japan")]
    public void NormalizeQuery_ValidInput_NormalizesSpacesAndDiacritics(string raw, string expected)
    {
        string? result = WeatherLocationService.NormalizeQuery(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NormalizeQuery_NullOrWhitespace_ReturnsNull(string? raw)
    {
        string? result = WeatherLocationService.NormalizeQuery(raw);
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeQuery_LongQuery_TruncatesTo100Characters()
    {
        string longInput = new('a', 150);
        string? result = WeatherLocationService.NormalizeQuery(longInput);

        Assert.NotNull(result);
        Assert.Equal(100, result.Length);
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("ab", false)]
    [InlineData("NYC", true)]
    [InlineData("Paris", true)]
    [InlineData("Tokyo", true)]
    public void LongEnough_ReturnsExpectedResult_ForLatinLengths(string location, bool expected)
    {
        bool result = WeatherLocationService.LongEnough(location);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseQuery_SingleLocation_ReturnsLocationWithNullQualifier()
    {
        var (location, qualifier) = WeatherLocationService.ParseQuery("Berlin");
        Assert.Equal("Berlin", location);
        Assert.Null(qualifier);
    }

    [Fact]
    public void ParseQuery_LocationWithQualifier_SplitsAtComma()
    {
        var (location, qualifier) = WeatherLocationService.ParseQuery("Paris, France");
        Assert.Equal("Paris", location);
        Assert.Equal("France", qualifier);
    }

    [Fact]
    public void ParseQuery_MultipleCommas_ExtractsPrimaryQualifier()
    {
        var (location, qualifier) = WeatherLocationService.ParseQuery("Springfield, IL, US");
        Assert.Equal("Springfield", location);
        Assert.Equal("IL", qualifier);
    }

    [Fact]
    public void ParseQuery_EmptyQualifier_ReturnsNullQualifier()
    {
        var (location, qualifier) = WeatherLocationService.ParseQuery("London,   ");
        Assert.Equal("London", location);
        Assert.Null(qualifier);
    }

    [Fact]
    public void GeoResult_RecordEqualityAndJsonSerialization()
    {
        var original = new GeoResult(
            Name: "Stockholm",
            Latitude: 59.3293,
            Longitude: 18.0686,
            Admin1: "Stockholm County",
            Admin2: "Stockholm Municipality",
            Country: "Sweden",
            Timezone: "Europe/Stockholm")
        {
            DisplaySubtitle = "Stockholm County, Sweden"
        };

        string json = JsonSerializer.Serialize(original, s_jsonOptions);
        var deserialized = JsonSerializer.Deserialize<GeoResult>(json, s_jsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Latitude, deserialized.Latitude);
        Assert.Equal(original.Longitude, deserialized.Longitude);
        Assert.Equal(original.Admin1, deserialized.Admin1);
        Assert.Equal(original.Admin2, deserialized.Admin2);
        Assert.Equal(original.Country, deserialized.Country);
        Assert.Equal(original.Timezone, deserialized.Timezone);
        
        // DisplaySubtitle is marked [JsonIgnore] so it defaults to empty string after deserialize
        Assert.Equal(string.Empty, deserialized.DisplaySubtitle);
    }

    [Fact]
    public void WeatherLocationService_ClearCache_DoesNotThrow()
    {
        var exception = Record.Exception(() => WeatherLocationService.Instance.ClearCache());
        Assert.Null(exception);
    }

    [Fact]
    public void WeatherLocationDialog_InitializesWithoutXamlExceptions()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var dlg = new MetroHub.Presentation.Controls.WeatherLocationDialog();
            Assert.NotNull(dlg);
            dlg.Close();
        });
    }

    [Fact]
    public void WebLinkDialog_InitializesWithoutXamlExceptions()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var dlg = new MetroHub.Presentation.Controls.WebLinkDialog();
            Assert.NotNull(dlg);
            dlg.Close();
        });
    }

    [Fact]
    public void RadioStationDialog_InitializesWithoutXamlExceptions()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var dlg = new MetroHub.Presentation.Controls.RadioStationDialog();
            Assert.NotNull(dlg);
            dlg.Close();
        });
    }
}

