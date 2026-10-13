global using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using MetroHub.Presentation.Themes;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace MetroHub.Widgets;

/// <summary>
/// High-performance, zero-boilerplate factory for creating Windows 11 Fluent context menu items for MetroHub widgets.
/// Centralizes icon sizing (18px), theme tokens, checkable state, and destructive action styling.
/// </summary>
public static class WidgetMenu
{
    public static MenuItem Item(
        string header,
        SymbolRegular? symbol,
        Action onClick,
        bool isCheckable = false,
        bool isChecked = false,
        bool isDestructive = false)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = isCheckable,
            IsChecked = isChecked
        };

        if (symbol.HasValue)
        {
            item.Icon = new SymbolIcon
            {
                Symbol = symbol.Value,
                FontSize = 18,
                Foreground = isDestructive ? ThemeTokens.StatusErrorBrush : ThemeTokens.MenuIconForegroundBrush
            };
        }

        if (isDestructive)
        {
            item.Foreground = ThemeTokens.StatusErrorBrush;
        }

        item.Click += (s, e) => onClick();
        return item;
    }

    public static MenuItem Item(
        string header,
        Action onClick,
        bool isCheckable = false,
        bool isChecked = false,
        bool isDestructive = false)
    {
        return Item(header, null, onClick, isCheckable, isChecked, isDestructive);
    }

    public static MenuItem Submenu(string header, SymbolRegular? symbol, params Control[] children)
    {
        var menu = new MenuItem
        {
            Header = header
        };

        if (symbol.HasValue)
        {
            menu.Icon = new SymbolIcon
            {
                Symbol = symbol.Value,
                FontSize = 18,
                Foreground = ThemeTokens.MenuIconForegroundBrush
            };
        }

        foreach (var child in children)
        {
            menu.Items.Add(child);
        }

        return menu;
    }

    public static MenuItem Submenu(string header, SymbolRegular? symbol, IEnumerable<Control> children)
    {
        var menu = new MenuItem
        {
            Header = header
        };

        if (symbol.HasValue)
        {
            menu.Icon = new SymbolIcon
            {
                Symbol = symbol.Value,
                FontSize = 18,
                Foreground = ThemeTokens.MenuIconForegroundBrush
            };
        }

        foreach (var child in children)
        {
            menu.Items.Add(child);
        }

        return menu;
    }

    public static Separator Divider() => new();
}
