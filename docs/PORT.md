# Agent Team Viewer for Avalonia: port brief

> Historical design brief: the WPF app was retired and the Avalonia app is the maintained one.

A cross-platform (Windows + macOS) port of the WPF Agent Team Viewer, as a
separate app. The WPF app (retired, no longer in this repo) was the
reference and must NOT be modified: read it, never write to it, never build
into its folders, never kill its windows.

## Where things are
- Reference app: the retired WPF app (README.md, TEAM.md,
  FEED.md, `AgentTeamViewer\` source, `AgentTeamViewer\Controls\README.md`).
  Its teammates are still changing it (dismiss button, connector fixes, a calmer
  restyle). Port the current state; the lead will ask for a sync pass later.
- This app: `apps/avalonia/`, solution
  `AgentTeamViewer.Avalonia.sln`, project `AgentTeamViewer.Avalonia\`, Avalonia 11
  (latest stable), .NET 9, desktop only, compiled bindings.
- Feed contract: `docs/FEED.md`. Same feed, same folders:
  `~/.claude/agent-feed/<sessionId>/` and `~/.claude/sessions/<pid>.json`
  (`%USERPROFILE%` on Windows, `$HOME` on macOS). Per-user app state (card
  order) goes under `Environment.SpecialFolder.LocalApplicationData` /
  `AgentTeamViewer.Avalonia/`, which maps to `~/Library/Application Support` on
  macOS. No Windows-only API anywhere (no P/Invoke, no registry, no
  System.Windows.*); guard anything platform-specific with OperatingSystem checks.

## Ownership (same split as the WPF app)
- **avalonia-core**: the .sln/.csproj, App.axaml(.cs), Program.cs,
  MainWindow.*, Feed/, Model/, ViewModels/, Views/, Demo/. Port Feed/ and Model/
  near-verbatim (plain .NET), ViewModels with CommunityToolkit.Mvvm or plain
  INotifyPropertyChanged, Views and MainWindow to AXAML.
- **avalonia-fx**: Theme/ (resource dictionaries / Styles) and Controls/
  (StatusPalette, GlowBorder, WindowDots, StatusGlyph incl. Ready mode, HubRing,
  ConnectorLayer incl. Pulse and Branch edges), Controls/README.md, Gallery.
  WPF effects map to Avalonia: DropShadowEffect -> BoxShadow on Border or
  DropShadowEffect, DrawingVisual -> custom Control.Render(DrawingContext),
  BeginAnimation/Storyboard -> Avalonia Animation/Transitions or a
  DispatcherTimer/`TopLevel.RequestAnimationFrame` loop. Keep 60 fps-friendly.
- Message each other for API questions; never edit the other's files. avalonia-fx
  publishes Controls/README.md early; avalonia-core codes against it with
  placeholders until it lands.

## Feature parity checklist (all from the WPF app)
Three columns (lead / hub / cards); live tailing and --demo replay (copy the
reference Demo/sample-feed); session picker with names from the registry, live
dot, no-feed rows, delete; lead panel with prompt/thinking/text/tool/delivery
blocks and paused auto-scroll; cards with Ready glyph, model chip, worked time,
last lines; nested cards with branch edges; expand in place pushing others down;
drag-to-reorder within the column with persisted order; connectors that follow
layout and pulse on `message` events; hub animating at rest with the larger
centre dot. The dismiss X button is being added to the WPF app now: port it once
it exists there.

## Done means
`dotnet build` green with zero warnings, `dotnet run --project
AgentTeamViewer.Avalonia -- --demo` shows the demo with every item above, no
args tails the newest real session, verified by screenshot on Windows. The
macOS build is verified only to compile (`dotnet publish -r osx-arm64` succeeds);
note anything you could not test on a Mac.
