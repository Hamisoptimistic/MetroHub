# 🧩 MetroHub Widget Developer Guide & Syntax Cheat Sheet

Welcome! This folder is the **official boilerplate** for building new widgets in MetroHub.
To create a new widget, simply **copy this folder**, rename the files, and register it in `WidgetRegistry.cs`.

---

## 🚀 Quick Start (5-Minute Recipe)

1. **Copy this folder**:
   - Duplicate `src/MetroHub/Widgets/Catalog/Template/` to `src/MetroHub/Widgets/Catalog/MyNewWidget/`
2. **Rename the 4 files**:
   - `TemplateWidgetViewModel.cs` &rarr; `MyNewWidgetViewModel.cs`
   - `TemplateWidgetView.xaml` &rarr; `MyNewWidgetView.xaml`
   - `TemplateWidgetView.xaml.cs` &rarr; `MyNewWidgetView.xaml.cs`
   - `TemplateWidgetSettings.cs` &rarr; `MyNewWidgetSettings.cs`
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
4. **Done!** MetroHub's `WidgetTemplateSelector` will automatically load your View and bind your ViewModel.

---

## 🎨 Design Tokens (Never Hardcode Fonts or Colors!)

All design tokens are **automatically available globally** from `Tokens.xaml`. Never use hardcoded pixel sizes or hex colors.

### 1. Typography Tokens
| Token Resource | Size / Weight | Usage |
| :--- | :--- | :--- |
| `FontSize="{DynamicResource TypeHeader}"` | 16px SemiBold | Widget card titles and section headers |
| `FontSize="{DynamicResource TypeSubtitle}"` | 18px Bold | Important metrics and sub-headers |
| `FontSize="{DynamicResource TypeBody}"` | 14px Regular | Default readable body text |
| `FontSize="{DynamicResource TypeBodyStrong}"`| 14px SemiBold | Emphasized buttons or labels |
| `FontSize="{DynamicResource TypeCaption}"` | 12px Regular | Timestamps, status text, subtitles |
| `FontSize="{DynamicResource TypeDisplay}"` | 28px SemiBold | Big numeric metrics (e.g. Pomodoro timer) |
| `FontSize="{DynamicResource TypeHero}"` | 72px Bold | Giant display digits (e.g. Clock widget) |

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

All widget styles are **automatically available globally** from `WidgetStyles.xaml` and `ControlStyles.xaml`.

### A. Small Micro Button (24×24 Icon Button)
Ideal for play/pause, refresh, or mini navigation:
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
    <TextBlock Text="Save" FontSize="{DynamicResource TypeBody}" />
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
<Border Height="1" 
        Background="{DynamicResource WidgetDividerBrush}" 
        Margin="0,8" />
```

### F. Windows 11 Fluent Icons
MetroHub uses `Wpf.Ui.Controls.SymbolIcon` with authentic Fluent glyphs:
```xml
<ui:SymbolIcon Symbol="Heart24" FontSize="16" Foreground="{DynamicResource TextPrimaryBrush}" />
<ui:SymbolIcon Symbol="Settings24" FontSize="16" />
<ui:SymbolIcon Symbol="WeatherSunny24" FontSize="16" />
<ui:SymbolIcon Symbol="Play24" FontSize="16" />
```

---

## 🧠 ViewModel Architecture Cheat Sheet

### 1. Inheriting `WidgetViewModelBase`
Your ViewModel gets all of this automatically:
```csharp
public sealed partial class MyWidgetViewModel : WidgetViewModelBase
{
    public MyWidgetViewModel(TileModel model) : base(model)
    {
        // Load settings during construction
        LoadSettings(model.SettingsJson);
    }
}
```

### 2. Supported Sizes
Declare which sizes the user can resize your widget to:
```csharp
public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
{
    WidgetSize.Small,   // 1x1
    WidgetSize.Medium,  // 2x2
    WidgetSize.Wide,    // 4x2
    WidgetSize.Large    // 4x4
};
```

### 3. Settings Persistence Across App Restarts
Use `WidgetSerializer` to save/load your settings POCO:
```csharp
protected override void LoadSettings(string? settingsJson)
{
    var settings = WidgetSerializer.Deserialize<MyWidgetSettings>(settingsJson);
    if (settings != null)
    {
        Counter = settings.Counter;
    }
}

public override void SaveSettings()
{
    var settings = new MyWidgetSettings { Counter = Counter };
    Model.SettingsJson = WidgetSerializer.Serialize(settings);
    NotifySettingsChanged();
}
```

### 4. Primary Tile Click (`IWidgetActionHandler`)
Implement this if clicking the tile background should trigger an action:
```csharp
public class MyWidgetViewModel : WidgetViewModelBase, IWidgetActionHandler
{
    public void OnPrimaryAction()
    {
        // Do something when the tile is clicked!
    }
}
```

### 5. Custom Right-Click Menu Items (`IWidgetContextMenuProvider`)
Implement this to add items to MetroHub's right-click context menu:
```csharp
public class MyWidgetViewModel : WidgetViewModelBase, IWidgetContextMenuProvider
{
    public IEnumerable<Control> GetContextMenuItems()
    {
        var item = new MenuItem
        {
            Header = "Custom Action",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Sparkle24,
                FontSize = 20,
                Foreground = ThemeTokens.MenuIconForegroundBrush
            }
        };
        item.Click += (s, ev) => DoCustomAction();

        yield return item;
    }
}
```

### 6. Automatic 1-Second Heartbeat Hook
Never create a manual `DispatcherTimer`! Override `OnSecondTick`:
```csharp
public override void OnSecondTick(DateTime utcNow)
{
    // Executes once per second on the UI thread when MetroHub is visible
    SecondsElapsed++;
}
```
*(MetroHub automatically pauses heartbeats when minimized or hidden to maintain 0.0% idle CPU.)*
