# Agent Team Viewer: who builds what

> Historical design brief: the WPF app was retired and the Avalonia app is the maintained one.

A native Windows window (WPF, .NET 9, no browser engine) that shows a Claude Code
session's agent team: the lead as a large terminal panel with its full
conversation and reasoning, each spawned agent as a compact 3-line terminal card,
glowing connectors between them that pulse when agents message each other, and
click-to-expand on any card to read that agent's full log.

Data comes from a JSONL feed written by the `agent-feed` Claude Code mod. The
format is in [`FEED.md`](FEED.md). The mod is built separately by the lead; the viewer
never depends on anything but the files described there.

## Look

Reference: a dark navy background, terminal panels with a thin bright-cyan
frame and a soft outer glow, monospace text in light cyan/white on near-black,
small coloured window dots top-left of each panel, thin cyan connector lines with
rounded corners and a glow, a central "hub" ring. Futuristic, calm, readable.
No robots, no stock imagery.

Colour tokens (put them in `Theme/Colors.xaml` and use them everywhere):
- `BgDeep` #0B1020, `BgPanel` #0E1526, `BgTerminal` #070B16
- `Cyan` #4FD9FF, `CyanDim` #2A7A99, `CyanGlow` #4FD9FF at 55% alpha
- `TextPrimary` #D8F3FF, `TextDim` #7FA6B8, `TextThinking` #6E8EA0 italic
- `Amber` #FFB347 (tool running), `Green` #4CE0A6 (done), `Red` #FF5C7A (failed)
- `UserPrompt` #B388FF (the user's lines in the lead panel)

Fonts: Cascadia Mono (ships with Windows 11), fallback Consolas.

## Layout

Three columns in a single resizable window, min 1280×800:

1. **Left (≈ 42%)**: the lead panel. Title bar "LEAD · Claude" with status glyph
   and the current activity line. Body: the conversation stream (see FEED.md
   "Viewer expectations") with auto-scroll that pauses when the user scrolls up.
2. **Centre (≈ 16%)**: the hub. A ring that breathes while the lead is thinking
   and spins slowly while a tool runs. Connectors leave the hub to the right and
   also connect the lead panel to the hub on the left.
3. **Right (≈ 42%)**: a vertical stack of agent cards, newest at the bottom,
   nested agents indented under their parent with a short branch connector.
   Each card: title row (status glyph, name or description, type/teammate tag,
   elapsed), then 2–3 lines of its latest activity. Cards animate in.

Clicking a card expands it to a full panel that slides over the lead panel area
(the lead panel dims behind it) showing that agent's full log in the same style
as the lead's. Esc, or clicking the card again, collapses it. Only one expanded
at a time.

## Project layout and ownership

```
AgentTeamViewer.sln
AgentTeamViewer/
  App.xaml, App.xaml.cs          (core)  merges Theme dictionaries
  MainWindow.xaml(.cs)           (core)  layout, wiring, expand/collapse
  Feed/                          (core)  FeedReader (tailing per FEED.md), FeedEvent records, parsing
  Model/                         (core)  AgentState, LogBlock (thinking/text/tool/prompt/delivery), TeamModel (upserts, edges, message pulses)
  ViewModels/                    (core)  MainViewModel, AgentCardViewModel, LogViewModel (ObservableCollections, INotifyPropertyChanged)
  Views/                         (core)  LeadPanel.xaml, AgentCard.xaml, LogView.xaml (use styles/controls from Theme and Controls; keep their own XAML minimal)
  Theme/                         (fx)    Colors.xaml, Brushes.xaml, Typography.xaml, Styles.xaml (TerminalPanel style, card style, scrollbar style, glyphs)
  Controls/                      (fx)    GlowBorder, HubRing, ConnectorLayer (bezier edges + Pulse(fromKey,toKey)), WindowDots, StatusGlyph
  Controls/README.md             (fx)    the API of each control: dependency properties, methods, resource keys
  Demo/sample-feed/              (core)  a hand-written session directory per FEED.md that exercises every event kind, for `--demo` mode
```

**core** = the `viewer-core` teammate. **fx** = the `viewer-fx` teammate.

Rules so two people can work in one tree without stepping on each other:
- Only `viewer-core` edits the `.csproj`, `App.xaml`, `MainWindow.*`, `Feed/`,
  `Model/`, `ViewModels/`, `Views/`, `Demo/`.
- Only `viewer-fx` edits `Theme/` and `Controls/`.
- `viewer-fx` publishes the control API in `Controls/README.md` early (within the
  first iteration) and keeps it accurate; `viewer-core` codes against it.
  `App.xaml` must merge `Theme/Colors.xaml`, `Theme/Brushes.xaml`,
  `Theme/Typography.xaml`, `Theme/Styles.xaml` in that order — core adds the
  merge lines, fx owns the files.
- Both run `dotnet build AgentTeamViewer` before reporting. A build break in your
  own files is yours; if the other side's files break the build, message them
  with the error instead of editing their files.
- Use `SendMessage` to the other teammate for API questions. Message the lead
  when you are done or blocked.
- Keep it to WPF built-ins plus `System.Text.Json`. No NuGet UI frameworks.

## Connector animation (fx)

`ConnectorLayer` is a Canvas-like panel placed over the whole content area.
Core registers anchor points by key (`main`, hub, each agent id) by calling
`SetAnchor(key, Point)` on layout updates (or binding attached properties; fx
decides and documents). Edges are declared with `AddEdge(fromKey, toKey)`.
Each edge is a cubic Bezier with horizontal tangents, drawn twice: a wide
low-alpha glow stroke underneath and a thin bright stroke on top, with a slow
dash animation for idle flow. `Pulse(fromKey, toKey)` sends a bright dot with a
trailing glow along the edge over ~700 ms (ease-in-out) and briefly brightens
the edge. Pulses may overlap. If no edge exists for the pair, draw a temporary
dashed edge for the duration of the pulse.

## Done means

`dotnet run --project AgentTeamViewer -- --demo` opens the window on the sample
feed and shows: lead panel with conversation, three agent cards (one nested),
connectors from hub to cards, a pulse when the sample `message` event is
replayed, click-to-expand on a card, Esc to collapse. Then with no arguments it
opens the newest real session under `%USERPROFILE%\.claude\agent-feed`.
