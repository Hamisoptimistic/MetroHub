using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;

namespace MetroHub.Core.Search;

/// <summary>
/// Source adapter executing terminal and shell commands via reclaimed '>' prefix.
/// Launches Windows Terminal (wt.exe) or PowerShell with arguments without Everything IPC interference.
/// </summary>
public sealed class TerminalSource : ISearchSource
{
    public string SourceId => "terminal";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public TerminalSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        if (route.Kind != RouteKind.Terminal)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string cmd = route.Remainder.Trim();
        string displayName = string.IsNullOrEmpty(cmd)
            ? "Open terminal"
            : $"Run '> {cmd}'";

        var action = new CandidateAction(
            Id: "run_terminal",
            Title: "Run in terminal",
            KeyGestureHint: "Enter",
            ExecuteAsync: _ =>
            {
                LaunchTerminalCommand(cmd);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });

        var candidate = new Candidate(
            Id: "terminal:" + (string.IsNullOrEmpty(cmd) ? "open" : cmd),
            DisplayName: displayName,
            FullPathOrKey: cmd,
            Category: SearchCategory.Apps,
            SourceId: SourceId)
        {
            PrimaryAction = action
        };

        return Task.FromResult<IReadOnlyList<Candidate>>(new[] { candidate });
    }

    public static void LaunchTerminalCommand(string command)
    {
        try
        {
            bool hasWt = IsWindowsTerminalAvailable();
            var psi = new ProcessStartInfo
            {
                FileName = hasWt ? "wt.exe" : "powershell.exe",
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(command))
            {
                if (hasWt)
                {
                    psi.ArgumentList.Add("powershell.exe");
                    psi.ArgumentList.Add("-NoExit");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(command);
                }
                else
                {
                    psi.ArgumentList.Add("-NoExit");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(command);
                }
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Safe.Log("TerminalSource.LaunchTerminalCommand", ex);
        }
    }

    private static bool IsWindowsTerminalAvailable()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wtPath = Path.Combine(localAppData, @"Microsoft\WindowsApps\wt.exe");
            return File.Exists(wtPath);
        }
        catch
        {
            return false;
        }
    }
}

