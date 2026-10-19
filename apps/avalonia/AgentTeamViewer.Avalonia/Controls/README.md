# Theme + Controls API for Avalonia (owned by avalonia-fx)

Theme and control API. Names and defaults follow the original WPF design (historical, see `docs/TEAM.md`);
Avalonia-specific points are marked **Avalonia:**. Everything lives in the `AgentTeamViewer.Avalonia` assembly.

- C# namespace: `AgentTeamViewer.Controls`. In AXAML: `xmlns:ctl="using:AgentTeamViewer.Controls"`.
- Theme: ONE include in `App.axaml`, after the base theme:

```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://AgentTeamViewer.Avalonia/Theme/Theme.axaml" />
</Application.Styles>
```
  `Theme.axaml` is a `Styles` file: it merges `Colors.axaml`, `Brushes.axaml`, `Typography.axaml`
  (so all resource keys below resolve with `{DynamicResource ...}` anywhere in the app) and then
  the style classes from `Styles.axaml`. Set `RequestedThemeVariant="Dark"` on the Application.
- `Controls/Gallery.axaml` (`ctl:Gallery`, a UserControl) shows every style and control in sample states.
- Nothing here depends on the data layer. Controls are plain `Control`s with `StyledProperty`s, so they bind normally.
- All animation is driven by `TopLevel.RequestAnimationFrame` while the control is attached
  and only when something is animating; per-frame paths allocate nothing.

**Avalonia: style classes instead of x:Key styles.** Avalonia has no keyed styles, so the WPF
`Style="{StaticResource TerminalPanel}"` becomes `Classes="terminal-panel"` (kebab-case of the WPF key).
Resources use `{DynamicResource Key}` (StaticResource also works for plain colours/brushes).

---

## Resource keys

### Colors.axaml (`Color`)
Identical keys and values to WPF: `BgDeepColor` #0B1020, `BgPanelColor` #0E1526, `BgTerminalColor` #070B16,
`CyanColor` #4FD9FF, `CyanDimColor` #2A7A99, `CyanGlowColor` #8C4FD9FF, `TextPrimaryColor` #A9C7D8,
`TextBrightColor` #D8F3FF, `TextDimColor` #7FA6B8, `TextThinkingColor` #6E8EA0, `TextFaintColor` #3F5566,
`AmberColor` #FFB347, `GreenColor` #4CE0A6, `RedColor` #FF5C7A, `UserPromptColor` #9D90C2,
`UserPromptBrightColor` #B388FF, `ToolMutedColor` #B89B72, `DeliveryMutedColor` #8FB6C8, `CyanMutedColor` #69AFC6,
`HairlineColor` #2E4FD9FF, `DimOverlayColor` #B30B1020, `CyanFaintGlowColor` #334FD9FF.
(**Avalonia:** colour literals are `#AARRGGBB`, same as WPF.)

### Brushes.axaml (`SolidColorBrush`)
One brush per colour, same name with `Brush`: `BgDeepBrush`, `BgPanelBrush`, `BgTerminalBrush`, `CyanBrush`,
`CyanDimBrush`, `CyanGlowBrush`, `TextPrimaryBrush`, `TextBrightBrush`, `TextDimBrush`, `TextThinkingBrush`,
`TextFaintBrush`, `AmberBrush`, `GreenBrush`, `RedBrush`, `UserPromptBrush`, `UserPromptBrightBrush`,
`ToolMutedBrush`, `DeliveryMutedBrush`, `CyanMutedBrush`, plus `PanelFrameBrush`, `PanelFrameDimBrush`,
`PanelTitleBrush`, `DimOverlayBrush`, `HairlineBrush`, `CyanFaintGlowBrush`.

Status brushes: `Status.running`, `.thinking`, `.tool`, `.waiting`, `.done`, `.failed`, `.idle`, `.gone`
and aliases `.completed`, `.killed`, `.error`, `.working`, `.pending` (same colours as WPF).
`StatusBrushConverter` (resource key `StatusBrushConverter`, `IValueConverter`): status string in, brush out.
Static helper `AgentTeamViewer.Controls.StatusPalette`: `Normalize(string?)`, `ColorFor(string?) -> Color`,
`BrushFor(string?) -> IBrush`, `IsActive(string?)`, `ReadyGroup(string?) -> ReadyState`.
**Avalonia:** `ColorFor` returns `Avalonia.Media.Color`, `BrushFor` returns `IBrush`.

### Typography.axaml
`MonoFont` (FontFamily, `Cascadia Mono, Consolas, Menlo, Monaco, Courier New`: the macOS fallbacks are new),
`FontSizeBody` 13, `FontSizeCard` 12, `FontSizeTitle` 12, `FontSizeTag` 10, `FontSizeSmall` 11 (`x:Double`).

TextBlock classes (use `<TextBlock Classes="card-title" Text="..."/>`; each class is complete, no need to add a base class):

| Class (WPF key) | Look | For |
|---|---|---|
| `terminal` (TerminalText) | TextPrimary, 13, wrap, line height 19 | base |
| `prompt` (PromptText) | UserPrompt, regular weight | `row door=prompt` |
| `thinking` (ThinkingText) | TextThinking, Italic | `chunk thinking` |
| `body` (BodyText) | TextPrimary | `chunk text` |
| `tool` (ToolText) | ToolMuted, NoWrap, ellipsis | tool one-liners |
| `tool-result` (ToolResultText) | TextDim, 12 | expanded tool result |
| `delivery` (DeliveryText) | DeliveryMuted, Italic | `row door=delivery` |
| `notice` (NoticeText) | TextDim, 11, Italic | notices |
| `panel-title` (PanelTitleText) | CyanMuted, 12, regular weight, NoWrap | "LEAD · Claude" |
| `activity` (ActivityText) | TextDim, 12, NoWrap, ellipsis | activity line |
| `card-title` (CardTitleText) | TextPrimary, 12, regular weight, NoWrap, ellipsis | card name |
| `card-line` (CardLineText) | TextDim, 12, NoWrap, ellipsis | card activity lines |
| `tag` (TagText) | CyanDim, 10 | inside `tag-chip` |
| `elapsed` (ElapsedText) | TextDim, 11, NoWrap | elapsed time |

### Styles.axaml (classes)

| Class (WPF key) | Target | Look |
|---|---|---|
| `card-panel` (CardPanel) | `Border` | Lead panel / expanded agent view: BgTerminal, 1px CyanDim frame, radius 5, no glow. |
| `card-panel-header` (CardPanelHeader) | `Border` | Header row of a card-panel: transparent, padding 10,7, hairline underneath. |
| `terminal-panel` (TerminalPanel) | `Border` | BgTerminal, 1px Cyan frame, radius 6, soft outer glow (BoxShadow). |
| `terminal-card` (TerminalCard) | `Border` | Compact agent card: CyanDim frame, radius 5, padding 10,7, opacity 0.82, no glow until `:pointerover` (then Cyan frame, opacity 1, glow). **Avalonia:** WPF `Tag="selected"` becomes the extra class `selected` (`Classes="terminal-card selected"`, or `Classes.selected="{Binding ...}"`). Hover and selected changes are animated (150 ms). |
| `panel-title-bar` (PanelTitleBar) | `Border` | BgPanel fill, bottom hairline, radius 6,6,0,0, padding 10,6. |
| `tag-chip` (TagChip) | `Border` | 1px CyanDim frame, radius 3, padding 5,0, centred vertically. |
| `hairline` (Hairline) | `Rectangle` | 1px HairlineBrush separator. |
| `terminal` | `ScrollViewer` (TerminalScrollViewer) | transparent, vertical Auto, horizontal Disabled. |
| `ghost` (GhostButton) | `Button` | borderless mono text button, Cyan on hover. |
| (implicit) | `ScrollBar` | 8px, no arrow buttons, rounded CyanDim thumb, Cyan on hover. Applies to every ScrollBar automatically. |

`Classes` lists with several of these combine as expected (`Classes="card-panel"` on a Border plus a child `Border Classes="card-panel-header"`).

---

## Controls (`AgentTeamViewer.Controls`)

### `GlowBorder : Border`
**Avalonia:** derives from `Border` (not `ContentControl`): put the content in `Child` (XAML content property, so
`<ctl:GlowBorder><Grid/></ctl:GlowBorder>` works exactly like WPF). `Padding`, `Background`, `BorderBrush`,
`BorderThickness`, `CornerRadius` are Border's. Defaults as WPF: BgTerminal, Cyan, 1, radius 6. The glow is the
border's own `BoxShadow`, drawn outside the content, so streaming text never re-renders it.

| Property | Type | Default |
|---|---|---|
| `GlowColor` | Color | Cyan |
| `GlowIntensity` | double 0..1 | 0.55 |
| `GlowRadius` | double | 18 |
| `Breathing` | bool | false |
| `BreathePeriod` | TimeSpan | 2.6 s |

`Flash()` doubles the glow for about 350 ms, then returns.

### `WindowDots : Control`
`DotSize` (8), `Spacing` (6), `FirstBrush`/`SecondBrush`/`ThirdBrush` (`IBrush`, Red/Amber/Green), `Dim` (false; 55% opacity).

### `StatusGlyph : Control`
`Status` (string, "idle"), `GlyphSize` (double, 12), `Fill` (`IBrush?`, null), `Mode` (`StatusGlyphMode` Glyph | Ready),
`SecondaryMark` (bool, true). Rendering rules, groups and the "leave ~3 px margin for the glow in Ready mode" note are
unchanged from WPF (see `StatusPalette.ReadyGroup`). Vertically centred by default.

### `HubRing : Control`
`Breathe`, `Spin` (bool intensifiers), `Accent` (Color, Cyan), `Caption` (string, ""). Square: the ring is the largest
centred square that fits, with the caption under it. Resting motion (outer dotted ring one turn / 28 s clockwise, inner
dashed ring one turn / 40 s anticlockwise, centre dot pulse 2.6 s) is always on while attached; `Breathe`/`Spin` only
speed things up with no snap. Rotation pivots on the ring centre; outer dots are round-capped so they stay circles.
**Avalonia:** the soft glow is built from layered strokes / a BoxShadow instead of `DropShadowEffect`.

### `ConnectorLayer : Panel`
**Avalonia:** derives from `Panel` (it hosts internal glow/line/pulse layers so the glow can use a real blur effect),
`IsHitTestVisible=false`. Place it in the same Grid cell as the whole content area, last in z-order, no margin.
All points are in the layer's own coordinates. API is the WPF one:

| Method | Meaning |
|---|---|
| `SetAnchor(string key, Point p)` | create or move an anchor (in and out at the same point) |
| `SetAnchor(string key, Point inPoint, Point outPoint)` | edges arrive at `inPoint` (middle of the left side), leave from `outPoint` (middle of the right side) |
| `RemoveAnchor(string key)` / `bool HasAnchor(string key)` | |
| `AddEdge(string fromKey, string toKey, ConnectorShape shape = Horizontal)` | idempotent; `Branch` leaves the source heading down (give the parent an out point on its bottom edge) |
| `RemoveEdge(string fromKey, string toKey)` | |
| `ClearEdges()` | removes edges (anchors stay) |
| `Clear()` | **new**: removes every edge, anchor and running pulse, back to empty. `ClearAll()` is kept as an alias. |
| `Pulse(string fromKey, string toKey)` | bright dot + trail over `PulseDuration`; uses the edge in either direction; if none, a temporary dashed edge for the pulse's duration, then it fades; no-op if an anchor is missing |
| `Point PointFrom(Control element, double xFraction, double yFraction)` | maps a point inside `element` into layer coordinates; `(NaN,NaN)` if not in the same visual tree |

Properties (`StyledProperty`): `LineBrush` (IBrush, Cyan), `GlowBrush` (IBrush, CyanGlow), `LineThickness` (1.5),
`GlowThickness` (9), `FlowSpeed` (1.0, 0 = no idle flow), `PulseDuration` (700 ms), `PulseColor` (Color, #D8F3FF),
`HubRadius` (0), `SoftGlow` (true). Event `PulseCompleted(object?, ConnectorPulseEventArgs { FromKey, ToKey })`.
`ConnectorShape { Horizontal, Branch }`. Hub rim offsetting for the anchor key `"hub"` works as in WPF.
**Avalonia:** the idle flow is drawn as moving dots along the curve (same look as the WPF dash pattern, no allocations).

Keep calling `SetAnchor` from layout (`LayoutUpdated` or `SizeChanged` of the elements): the layer does not track elements itself.

---

## Conventions for core
- Lead panel: `<Border Classes="terminal-panel">` or `<ctl:GlowBorder Breathing="{Binding IsThinking}">`.
- Card: `<Border Classes="terminal-card">` containing a Grid with `ctl:StatusGlyph Status="{Binding Status}"`,
  `card-title`, `tag-chip`, `elapsed`, then 2-3 `card-line`.
- Anchor keys: `"main"`, `"hub"`, and each agent id, exactly as the feed names them.

### HubRing anchor helpers (added)
`double RingCenterYFraction` (ring centre y / control height; below 0.5 when a caption is shown) and `double RingRadius`
(outer ring radius in layout units). Anchor the hub with `PointFrom(hub, 0.5, hub.RingCenterYFraction)` and set
`ConnectorLayer.HubRadius = hub.RingRadius`. Both read the current `Bounds`, so refresh them on layout updates.

### Text sharpness (added)
Avalonia already draws LCD sub-pixel text over opaque fills, and 11.3 has no hinting option, so the crispness
lever is glyph placement: every text class sets `LetterSpacing` so Cascadia Mono's advance is a whole pixel at 100%
scale (13px +0.383, 12px -0.031, 11px -0.445, 10px +0.141), matching WPF Display formatting and its line wrapping.
Any text styled outside these classes should set the same `LetterSpacing` for its font size.
`ctl:TextRender.Mode` (set to `SubpixelAntialias` on Window in `Styles.axaml`) forwards to `RenderOptions.TextRenderingMode`.

### Markdown classes (added, ported from the WPF Md* styles)
Resources: `Md*Color` / `Md*Brush` for `MdTableHeaderFill`, `MdTableHeaderText`, `MdCodeBg`, `MdCodeText`, `MdInlineCodeBg`, `MdLink`,
`MdBullet`, `MdHeading`, plus the aliases `MdTableBorder`, `MdTableHeaderBg`, `MdCodeBlockBg`, `MdCodeBlockBorder`, `MdQuoteBar`.
All weights are regular, and every text class has the whole-pixel `LetterSpacing` for its size (17px +0.04, 15px +0.211, 14px -0.203).

| Class (WPF key) | Target | Use |
|---|---|---|
| `md-h1` / `md-h2` / `md-h3` (MdHeading1-3) | TextBlock | 17 / 15 / 14 px, MdHeading colour; size and colour carry the hierarchy |
| `md-bullet` (MdBulletText) | TextBlock | the bullet or number glyph of a list item |
| `md-link` (MdLink) | TextBlock | link colour and underline, Cyan on hover. **Avalonia:** no Hyperlink, so a plain TextBlock (use `Inlines` runs or a click handler) |
| `md-inline-code-chip` (MdInlineCodeChip) | Border | subtle chip, padding 5,0, radius 3; wrap an `md-inline-code` TextBlock |
| `md-inline-code` (MdInlineCodeText) | TextBlock | 12 px, NoWrap |
| `md-code-block` (MdCodeBlock) | Border | dark block, dim frame, radius 4, padding 10,8; wrap an `md-code` TextBlock (12 px, NoWrap; use a horizontal ScrollViewer if needed) |
| `md-table` (MdTable) | Border | outer wrapper of the table Grid (top and left hairline) |
| `md-table-header-cell` / `md-table-cell` | Border | each cell (right and bottom hairline, padding 10,5); header has the lighter fill |
| `md-table-header` / `md-table-cell-text` (MdTableHeaderText/CellText) | TextBlock | 12 px; header muted cyan-grey, regular weight (WPF Medium) |
| `md-quote` (MdQuoteText) | TextBlock | quoted lines, dim colour (the bar brush is `MdQuoteBarBrush`) |

The Gallery has a Markdown section showing all of them.

### Selection, copy button and context menu (added)
- `LogSelectionBrush` (`LogSelectionColor` #594FD9FF): text selection highlight for the log text.
- `Button` classes `ghost md-copy-button`: chrome-free code-block copy button (padding 6,1, no min size). Its dim 0.7 opacity and the hover-to-1.0
  fade come from the style, so do not set `Opacity` in code (a local value would beat the hover style). Put a `tag` TextBlock inside.
- `MenuFlyoutPresenter`, `ContextMenu` and `MenuItem` are themed implicitly: mono 12px regular, BgPanel fill, CyanDim frame, radius 4,
  Hairline hover with Cyan text. The Gallery shows an inline `MenuFlyoutPresenter` and a copy button.
