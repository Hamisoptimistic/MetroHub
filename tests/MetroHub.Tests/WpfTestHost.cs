using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using Wpf.Ui.Markup;

namespace MetroHub.Tests;

/// <summary>
/// Helper to initialize WPF Application context and merged theme dictionaries on an STA thread for XAML view sanity tests.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object _syncLock = new();
    private static bool _resourcesInitialized;

    public static void EnsureApplicationResources()
    {
        lock (_syncLock)
        {
            if (_resourcesInitialized) return;

            if (Application.Current == null)
            {
                try { _ = new Application(); } catch { }
            }

            if (Application.Current != null)
            {
                var appResources = Application.Current.Resources;
                if (appResources.MergedDictionaries.Count == 0)
                {
                    try
                    {
                        appResources.MergedDictionaries.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                        appResources.MergedDictionaries.Add(new ControlsDictionary());
                        appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MetroHub;component/Presentation/Themes/Tokens.xaml", UriKind.Absolute) });
                        appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MetroHub;component/Presentation/Themes/ControlStyles.xaml", UriKind.Absolute) });
                        appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MetroHub;component/Presentation/Themes/ContextMenuStyles.xaml", UriKind.Absolute) });
                        appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MetroHub;component/Widgets/WidgetStyles.xaml", UriKind.Absolute) });
                    }
                    catch { }
                }
            }

            _resourcesInitialized = true;
        }
    }

    public static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
