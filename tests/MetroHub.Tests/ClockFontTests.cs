using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Clock;
using Xunit;
using Xunit.Abstractions;

namespace MetroHub.Tests;

public class ClockFontTests
{
    private readonly ITestOutputHelper _output;

    public ClockFontTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RenderClockWidgetsToDisk()
    {
        var thread = new Thread(() =>
        {
            if (Application.Current == null)
            {
                new Application();
            }

            string outDir = @"C:\Users\HamB\.gemini\antigravity-ide\brain\a6e02d95-8bf4-47e5-864a-6d9ee3ef1384\scratch\renders";
            Directory.CreateDirectory(outDir);

            var tests = new[]
            {
                (Name: "Now_4x4", Face: ClockFontFace.Now, SpanX: 4, SpanY: 4, W: 248, H: 248),
                (Name: "Now_Banner8x2", Face: ClockFontFace.Now, SpanX: 8, SpanY: 2, W: 504, H: 120),
                (Name: "SegoeUI_4x4", Face: ClockFontFace.SegoeUI, SpanX: 4, SpanY: 4, W: 248, H: 248),
                (Name: "Monoton_4x4", Face: ClockFontFace.Monoton, SpanX: 4, SpanY: 4, W: 248, H: 248),
                (Name: "Digital7_4x4", Face: ClockFontFace.Digital7, SpanX: 4, SpanY: 4, W: 248, H: 248),
                (Name: "FffForward_4x4", Face: ClockFontFace.FffForward, SpanX: 4, SpanY: 4, W: 248, H: 248),
                (Name: "Karnivore_4x4", Face: ClockFontFace.KarnivoreDigit, SpanX: 4, SpanY: 4, W: 248, H: 248)
            };

            foreach (var t in tests)
            {
                var tile = new TileModel { SpanX = t.SpanX, SpanY = t.SpanY };
                var vm = new ClockWidgetViewModel(tile);
                vm.SetFontFace(t.Face);

                FontWeight fw = t.Face == ClockFontFace.Now ? FontWeights.Bold :
                                (t.Face == ClockFontFace.SegoeUI ? FontWeights.SemiBold : FontWeights.Normal);

                FrameworkElement content;
                if (vm.IsBanner)
                {
                    var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var vb = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 12, 2) };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    sp.Children.Add(new TextBlock { Text = "2", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    sp.Children.Add(new TextBlock { Text = ":", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    sp.Children.Add(new TextBlock { Text = "15", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    vb.Child = sp;
                    Grid.SetColumn(vb, 0);
                    grid.Children.Add(vb);

                    var date = new TextBlock
                    {
                        Text = "Saturday, 26 September",
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                        Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(date, 1);
                    grid.Children.Add(date);

                    content = grid;
                }
                else
                {
                    var grid = new Grid();
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var vb = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    sp.Children.Add(new TextBlock { Text = "2", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    sp.Children.Add(new TextBlock { Text = ":", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    sp.Children.Add(new TextBlock { Text = "15", FontSize = 100, FontWeight = fw, FontFamily = vm.ClockFontFamily, Foreground = Brushes.White });
                    vb.Child = sp;
                    Grid.SetRow(vb, 0);
                    grid.Children.Add(vb);

                    var date = new TextBlock
                    {
                        Text = "Saturday, 26 September",
                        FontSize = 17,
                        FontWeight = FontWeights.SemiBold,
                        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                        Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    Grid.SetRow(date, 1);
                    grid.Children.Add(date);

                    content = grid;
                }

                var container = new Border
                {
                    Width = t.W,
                    Height = t.H,
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(12, 10, 12, 16),
                    Child = content
                };

                container.Measure(new Size(t.W, t.H));
                container.Arrange(new Rect(0, 0, t.W, t.H));
                container.UpdateLayout();

                var rtb = new RenderTargetBitmap(t.W, t.H, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(container);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                string path = Path.Combine(outDir, $"LiveTest_{t.Name}.png");
                using var fs = File.Create(path);
                encoder.Save(fs);

                _output.WriteLine($"Rendered {path}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
