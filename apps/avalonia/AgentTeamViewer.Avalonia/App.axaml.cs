using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AgentTeamViewer.Feed;
using AgentTeamViewer.ViewModels;

namespace AgentTeamViewer;

/// <summary>
/// Startup: <c>--demo [dir]</c> replays the sample feed at ~20 events/s; <c>--session &lt;dir&gt;</c>
/// tails a specific session directory; no arguments tails the newest session under
/// ~/.claude/agent-feed (%USERPROFILE% on Windows, $HOME on macOS). <c>--rate N</c> changes the demo
/// speed, <c>--root &lt;dir&gt;</c> changes the feed root.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.Exception);
                e.Handled = true;
            };

            var args = desktop.Args ?? Array.Empty<string>();
            bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            string? sessionDir = ValueAfter(args, "--session");
            string? root = ValueAfter(args, "--root");
            double rate = double.TryParse(ValueAfter(args, "--rate"), out var r) && r > 0 ? r : 20;

            var vm = new MainViewModel(Dispatcher.UIThread, ValueAfter(args, "--sessions"));
            var window = new MainWindow(vm);
            desktop.MainWindow = window;

            // Open the source once the window exists so the first layout pass sees an empty team.
            window.Opened += (_, _) => OpenSource(vm, args, demo, sessionDir, root, rate);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void OpenSource(MainViewModel vm, string[] args, bool demo, string? sessionDir, string? root, double rate)
    {
        if (demo)
        {
            var dir = ValueAfter(args, "--demo") is { } given && !given.StartsWith("--") && Directory.Exists(given)
                ? given
                : FindDemoSession();
            if (dir is null) vm.OpenNothing("demo feed not found (Demo/sample-feed)");
            else vm.OpenDemo(dir, rate);
            return;
        }

        if (sessionDir is not null)
        {
            if (Directory.Exists(sessionDir)) { vm.OpenLive(sessionDir); vm.RefreshSessions(root); }
            else vm.OpenNothing($"session directory not found: {sessionDir}");
            return;
        }

        var newest = SessionLocator.FindNewest(root);
        if (newest is null)
        {
            vm.OpenNothing($"no sessions under {root ?? SessionLocator.DefaultRoot} · start a Claude Code session with the agent-feed mod, or run with --demo");
            return;
        }
        vm.OpenLive(newest.Directory);
        vm.RefreshSessions(root);
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>The sample session directory: next to the exe (copied at build) or up the tree from it.</summary>
    private static string? FindDemoSession()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Demo", "sample-feed");
            if (Directory.Exists(candidate))
            {
                var session = Directory.EnumerateDirectories(candidate).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (session is not null) return session;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
