# MetroHub Widget ScrollBar & ScrollViewer Design Standard

> **Rule:** Never reinvent scrollbar styles inside individual widgets. All widgets must adhere to this unified, ultra-slim 4px floating auto-hide standard.

---

## 1. Specifications & Behavior

| Property | Value | Notes |
| :--- | :--- | :--- |
| **Width / Thickness** | `4px` (`MinWidth="4"`) | Ultra-slim floating pill; does not crowd widgets |
| **Corner Radius** | `2px` | Creates a smooth rounded capsule pill |
| **Track** | Transparent (`0px` border) | No grey gutters or background rectangles |
| **Resting State** | `Opacity="0.0"` (Invisible) | Clean aesthetics; widget cards look uninterrupted |
| **Hover / Drag Colors** | Resting: `#40FFFFFF`<br>Hover: `#90FFFFFF`<br>Dragging: `#D0FFFFFF` | High-contrast translucent white on Mica glass |
| **Auto-Hide Behavior** | Managed by `AutoHideScrollBehavior` | Smoothly fades in (`100ms`) on scroll/hover, fades out (`300ms`) after 1.2s of inactivity |

---

## 2. Implementation Patterns

All styles and behaviors are centrally located in:
- **Styles**: [WidgetStyles.xaml](file:///d:/MetroHub/src/MetroHub/Widgets/WidgetStyles.xaml) (`WidgetFloatingScrollBarStyle` and `WidgetFloatingScrollViewerStyle`)
- **Behavior**: [AutoHideScrollBehavior.cs](file:///d:/MetroHub/src/MetroHub/Presentation/Controls/AutoHideScrollBehavior.cs)

### Pattern A: Standard `ScrollViewer` (e.g. Device Lists, To-Do Lists)
Simply use `Style="{StaticResource WidgetFloatingScrollViewerStyle}"` and add right padding so content doesn't sit under the scrollbar:

```xaml
<ScrollViewer Style="{StaticResource WidgetFloatingScrollViewerStyle}"
              CanContentScroll="True"
              Padding="0,0,8,0">
    <ItemsControl ItemsSource="{Binding Items}">
        <!-- Items Template -->
    </ItemsControl>
</ScrollViewer>
```

### Pattern B: `ui:ListView`, `ListBox`, or custom virtualizing panel
Attach `controls:AutoHideScrollBehavior.IsEnabled="True"` and reference `WidgetFloatingScrollBarStyle`:

```xaml
<ui:ListView controls:AutoHideScrollBehavior.IsEnabled="True"
             ScrollViewer.HorizontalScrollBarVisibility="Disabled"
             ScrollViewer.VerticalScrollBarVisibility="Auto"
             ScrollViewer.CanContentScroll="True"
             Padding="0,0,10,0">
    <ui:ListView.Resources>
        <Style TargetType="{x:Type ScrollBar}" BasedOn="{StaticResource WidgetFloatingScrollBarStyle}" />
    </ui:ListView.Resources>
    <!-- List Items -->
</ui:ListView>
```

### Pattern C: `TextBox` (e.g. Multi-line Notes Editor)
Attach `controls:AutoHideScrollBehavior.IsEnabled="True"` to the `TextBox` (or to `PART_ContentHost` inside its template):

```xaml
<TextBox x:Name="NotesEditorTextBox"
         Style="{StaticResource NotesTextBoxStyle}"
         AcceptsReturn="True"
         TextWrapping="Wrap"
         VerticalScrollBarVisibility="Auto"
         HorizontalScrollBarVisibility="Disabled"
         controls:AutoHideScrollBehavior.IsEnabled="True" />
```

Ensure the widget resources include:
```xaml
<Style TargetType="{x:Type ScrollBar}" BasedOn="{StaticResource WidgetFloatingScrollBarStyle}" />
```

---

## 3. Checklist for New Widgets
- [ ] No local `FluentSlimScrollBarStyle` (3px) declarations.
- [ ] No local Storyboards or timers trying to animate scrollbar opacity manually.
- [ ] All ScrollBars inherit from `{StaticResource WidgetFloatingScrollBarStyle}`.
- [ ] Right padding is set (`6px` to `10px`) so content text / buttons do not overlap the 4px pill.
