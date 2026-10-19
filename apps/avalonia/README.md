# Agent Team Viewer (Avalonia)

Cross-platform (Windows + macOS) desktop viewer for a Claude Code session's agent team. It reads the feed
(`~/.claude/agent-feed/<sessionId>/`, contract in [`docs/FEED.md`](../../docs/FEED.md)),
and shows three columns: lead, hub, agent cards. Design notes: [`docs/PORT.md`](../../docs/PORT.md).

```
dotnet build AgentTeamViewer.Avalonia
dotnet run --project AgentTeamViewer.Avalonia                      # newest live session
dotnet run --project AgentTeamViewer.Avalonia -- --demo            # replay the bundled sample feed
dotnet run --project AgentTeamViewer.Avalonia -- --session <dir>   # a specific session directory
dotnet run --project AgentTeamViewer.Avalonia -- --root <dir>      # look for sessions elsewhere
dotnet run --project AgentTeamViewer.Avalonia -- --demo --rate 40  # replay faster
dotnet publish AgentTeamViewer.Avalonia -r osx-arm64 -c Release    # macOS build (compile-checked only)
```

Run these from `apps/avalonia`. The first run restores the Avalonia packages and takes about a
minute. For an exe to pin to the taskbar, build with `-c Release` and run
`AgentTeamViewer.Avalonia/bin/Release/net9.0/AgentTeamViewer.Avalonia.exe`.

Layout: `Feed/`, `Model/` (plain .NET), `ViewModels/`,
`Views/`, `MainWindow.axaml`, `Demo/sample-feed` (copied next to the exe). `Theme/` and
`Controls/` (see `Controls/README.md`) are the look. Card order is saved under
`LocalApplicationData/AgentTeamViewer.Avalonia/order`.

The mods that produce the feed are set up as described in the [top-level README](../../README.md).
