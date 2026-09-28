using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using MetroHub.Widgets.Catalog.Markdown;

namespace MetroHub.Tests;

/// <summary>
/// Shared plumbing for the Markdown suites: an STA runner for anything that touches WPF objects,
/// a disposable temp sandbox for state/document files, and a module initializer that keeps the
/// removable developer log out of a test run.
/// </summary>
internal static class MarkdownTestHost
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Removable one-flag diagnostic: never let a test run write to %LocalAppData%.
        MarkdownLog.Enabled = false;
    }

    /// <summary>Runs the action on a dedicated STA thread and rethrows whatever it threw.</summary>
    public static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    try { new Application(); } catch { }
                }
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

/// <summary>Throwaway directory layout for state/documents so tests never touch real app data.</summary>
internal sealed class MarkdownSandbox : IDisposable
{
    public MarkdownSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "MetroHub_MarkdownTests_" + Guid.NewGuid().ToString("N"));
        StateDir = Path.Combine(Root, "config", "widgets");
        BakDir = Path.Combine(Root, "backups", "widgets");
        DocsDir = Path.Combine(Root, "docs");
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(BakDir);
        Directory.CreateDirectory(DocsDir);
    }

    public string Root { get; }
    public string StateDir { get; }
    public string BakDir { get; }
    public string DocsDir { get; }

    /// <summary>Creates a document file with a known timestamp baseline.</summary>
    public string WriteDocument(string name, string content)
    {
        string path = Path.Combine(DocsDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
