# 🧩 MetroHub Widget Developer Guide & Syntax Cheat Sheet

Welcome! This folder is the **official boilerplate** for building new widgets in MetroHub.
To create a new widget, simply **copy this folder**, rename the files, register it in `WidgetRegistry.cs` and `WidgetJsonContext.cs`, and you are ready to go.

---

## 🚀 Quick Start (5-Minute Recipe)

1. **Copy this folder**:
   - Duplicate `src/MetroHub/Widgets/Catalog/Template/` to `src/MetroHub/Widgets/Catalog/MyNewWidget/`
2. **Rename the files**:
   - `TemplateWidgetViewModel.cs` &rarr; `MyNewWidgetViewModel.cs`
   - `TemplateWidgetView.xaml` &rarr; `MyNewWidgetView.xaml`
   - `TemplateWidgetView.xaml.cs` &rarr; `MyNewWidgetView.xaml.cs`
   - `TemplateWidgetSettings.cs` &rarr; `MyNewWidgetSettings.cs`
   - *(Optional modal dialog)*: `TemplateDialog.xaml` & `.cs` &rarr; `MyNewWidgetDialog.xaml` & `.cs`
3. **Register your widget** in `src/MetroHub/Widgets/Registry/WidgetRegistry.cs`:
   ```csharp
   Register(new WidgetDefinition(
       Id: "my-widget",
       DisplayName: "My Widget",
       Category: WidgetCategory.Utility,
       DefaultSize: WidgetSize.Medium,
       AllowedSizes: new[] { WidgetSize.Small, WidgetSize.Medium, WidgetSize.Wide },
       Factory: model => new MyNewWidgetViewModel(model),
       ViewModelType: typeof(MyNewWidgetViewModel),
       ViewType: typeof(MyNewWidgetView)
   ));
   ```
4. **Register your settings for JSON** in `src/MetroHub/Widgets/Serialization/WidgetJsonContext.cs`:
   ```csharp
   [JsonSerializable(typeof(MyNewWidgetSettings))]
   ```
5. **Add tests**:
   - Duplicate `tests/MetroHub.Tests/TemplateWidgetTests.cs` to test your new ViewModel and settings serialization!

---

## 🪟 Adding a Modal Dialog (`MetroDialog`)

If your widget needs a popup dialog (e.g. for settings, account login, or search), **do not write Win32 boilerplate**. Use `MetroDialog`:

### In XAML (`MyWidgetDialog.xaml`):
Use `<dialogs:MetroDialog>` as the root tag. The frosted acrylic sidebar, dark obsidian form panel, title bar, and close `[X]` button are automatically provided:
```xml
<dialogs:MetroDialog x:Class="MetroHub.Widgets.Catalog.MyWidget.MyWidgetDialog"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:dialogs="clr-namespace:MetroHub.Presentation.Dialogs"
                     Title="Configure My Widget"
                     Width="480" Height="320">

    <!-- Left 155px Frosted Acrylic Sidebar Content -->
    <dialogs:MetroDialog.SidebarContent>
        <Image Source="/Assets/my-icon.png" Width="64" Height="64" />
    </dialogs:MetroDialog.SidebarContent>

    <!-- Right Obsidian Form Content -->
    <Grid>
        <TextBox x:Name="MyInput" Style="{DynamicResource FluentGlassInputStyle}" />
        <Button Content="Save" Style="{DynamicResource FluentCohesivePrimaryButtonStyle}" Click="OnSaveClick" />
    </Grid>
</dialogs:MetroDialog>
```

### In C# (`MyWidgetDialog.xaml.cs`):
```csharp
public partial class MyWidgetDialog : MetroDialog
{
    public MyWidgetDialog() => InitializeComponent();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
```

### Opening It From Your Widget:
```csharp
[RelayCommand]
public void OpenDialog()
{
    var dialog = new MyWidgetDialog { Owner = Application.Current?.MainWindow };
    if (dialog.ShowDialog() == true)
    {
        // Save changes
        SaveSettings();
    }
}
```

---

## 🎨 Design Tokens (Never Hardcode Fonts or Colors!)

All design tokens are **automatically available globally** from `Tokens.xaml`. Never use hardcoded pixel sizes or hex colors.

### 1. Typography Tokens
| Token Resource | Size / Weight | Usage |
| :--- | :--- | :--- |
| `FontSize="{DynamicResource TypeHeaderFontSize}"` | 16px SemiBold | Widget card titles and section headers |
| `FontSize="{DynamicResource TypeSubtitleFontSize}"` | 18px Bold | Important metrics and sub-headers |
| `FontSize="{DynamicResource TypeBodyFontSize}"` | 14px Regular | Default readable body text |
| `FontSize="{DynamicResource TypeBodyStrongFontSize}"`| 14px SemiBold | Emphasized buttons or labels |
| `FontSize="{DynamicResource TypeCaptionFontSize}"` | 12px Regular | Timestamps, status text, subtitles |
| `FontSize="{DynamicResource TypeDisplayFontSize}"` | 28px SemiBold | Big numeric metrics (e.g. Pomodoro timer) |
| `FontSize="{DynamicResource TypeHeroFontSize}"` | 72px Bold | Giant display digits (e.g. Clock widget) |

### 2. Color & Brush Tokens
| Token Resource | Appearance | Usage |
| :--- | :--- | :--- |
| `Foreground="{DynamicResource TextPrimaryBrush}"` | Clean white/high-contrast | Primary readable titles and labels |
| `Foreground="{DynamicResource TextSecondaryBrush}"` | Soft white/gray | Secondary descriptions |
| `Foreground="{DynamicResource TextMutedBrush}"` | Subdued gray | Captions and inactive hints |
| `Background="{DynamicResource WidgetDividerBrush}"` | Subtle hairline line | Dividers separating card sections |
| `Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"` | User's system accent | Icons, active highlights, key values |
| `Background="{DynamicResource SystemAccentColorSecondaryBrush}"`| Hover accent | Button hover tints |

### 3. Corner Radius Tokens
| Token Resource | Typical Value | Usage |
| :--- | :--- | :--- |
| `CornerRadius="{DynamicResource TileCornerRadius}"` | 12px | Outer tile boundaries |
| `CornerRadius="{DynamicResource ControlCornerRadius}"` | 6px | Inner cards, buttons, input boxes |

---

## 🎛️ Reusable UI Control Snippets

### A. Small Micro Button (24×24 Icon Button)
Ideal for play/pause, refresh, or settings in header:
```xml
<Button Style="{StaticResource WidgetMicroButtonStyle}"
        Command="{Binding RefreshCommand}"
        ToolTip="Refresh Data">
    <ui:SymbolIcon Symbol="ArrowSync24" FontSize="14" />
</Button>
```

### B. Medium Button (Standard Action Button)
```xml
<Button Style="{StaticResource WidgetMediumButtonStyle}"
        Command="{Binding SaveCommand}">
    <TextBlock Text="Save" FontSize="{DynamicResource TypeBodyFontSize}" />
</Button>
```

### C. Animated Toggle Switch
```xml
<ui:ToggleSwitch IsChecked="{Binding IsEnabled, Mode=TwoWay}" />
```

### D. Slider (Volume, Brightness, Metrics)
```xml
<Slider Minimum="0" Maximum="100"
        Value="{Binding Volume, Mode=TwoWay}" />
```

### E. Hairline Section Divider
```xml
<Border Style="{StaticResource WidgetHairlineDividerStyle}" Margin="0,8" />
```

### F. Windows 11 Fluent Icons
```xml
<ui:SymbolIcon Symbol="Heart24" FontSize="16" Foreground="{DynamicResource TextPrimaryBrush}" />
<ui:SymbolIcon Symbol="Settings24" FontSize="16" />
<ui:SymbolIcon Symbol="Cube24" FontSize="16" />
```

---

## 🧠 Lifecycle & Background Architecture

### 1. Automatic 1-Second Heartbeat Hook
**Never create a manual `DispatcherTimer`!** Override `OnSecondTick`:
```csharp
public override void OnSecondTick(DateTime utcNow)
{
    // Executes once per second on UI thread when MetroHub is visible.
    // Automatically pauses when MetroHub is minimized or hidden.
}
```

### 2. Hub Visibility Hooks (Pause / Resume)
MetroHub automatically calls `Pause()` when the hub window hides, and `Resume()` when it opens:
```csharp
public override void Pause()
{
    base.Pause();
    // Stop expensive animations or background sensor polling
}

public override void Resume()
{
    base.Resume();
    // Resume polling
}
```

### 3. Safe Async Polling & Cleanup (`Dispose`)
Always support cancellation and clean up tokens to avoid memory leaks:
```csharp
private CancellationTokenSource? _cts;

[RelayCommand]
public async Task FetchDataAsync()
{
    _cts?.Cancel();
    _cts = new CancellationTokenSource();
    try
    {
        await Task.Delay(500, _cts.Token);
    }
    catch (OperationCanceledException) { }
}

protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
    base.Dispose(disposing);
}
```

---

## 🧪 Testing Your Widget
Whenever you submit a PR, GitHub Actions runs `dotnet test`.
Copy `tests/MetroHub.Tests/TemplateWidgetTests.cs` to test your:
* Default settings and allowed sizes
* Button commands and state transitions
* JSON serialization round-trip
* Clean disposal without exceptions
