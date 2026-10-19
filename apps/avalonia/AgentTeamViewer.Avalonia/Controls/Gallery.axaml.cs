using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentTeamViewer.Controls;

/// <summary>Shows every theme style and control in sample states. Host it in any window to judge the look.</summary>
public partial class Gallery : UserControl
{
    private static readonly string[] Statuses =
        { "running", "thinking", "tool", "waiting", "done", "failed", "idle", "gone", "pending" };

    private static readonly string[] Swatch =
    {
        "BgDeep", "BgPanel", "BgTerminal", "Cyan", "CyanDim", "TextPrimary", "TextDim", "TextThinking", "TextFaint",
        "Amber", "Green", "Red", "UserPrompt", "ToolMuted", "DeliveryMuted", "CyanMuted",
    };

    private readonly DispatcherTimer _autoPulse = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private int _autoStep;

    public Gallery()
    {
        InitializeComponent();
        BuildSwatches();
        BuildGlyphs();

        FlashButton.Click += (_, _) => { FlashBox.Flash(); };
        PulseMainA.Click += (_, _) => Connectors.Pulse("main", "A");
        PulseBackA.Click += (_, _) => Connectors.Pulse("A", "main");
        PulseBranch.Click += (_, _) => Connectors.Pulse("A", "C");
        PulseTemp.Click += (_, _) => Connectors.Pulse("main", "C");
        ClearButton.Click += (_, _) => Connectors.Clear();
        RebuildButton.Click += (_, _) => { Connectors.Clear(); BuildEdges(); UpdateAnchors(); };

        BuildEdges();
        Stage.LayoutUpdated += (_, _) => UpdateAnchors();

        _autoPulse.Tick += (_, _) =>
        {
            switch (_autoStep++ % 4)
            {
                case 0: Connectors.Pulse("main", "A"); break;
                case 1: Connectors.Pulse("main", "B"); break;
                case 2: Connectors.Pulse("A", "C"); break;
                default: Connectors.Pulse("B", "main"); FlashBox.Flash(); break;
            }
        };
        AttachedToVisualTree += (_, _) => _autoPulse.Start();
        DetachedFromVisualTree += (_, _) => _autoPulse.Stop();
    }

    private void BuildEdges()
    {
        Connectors.AddEdge("main", "hub");
        Connectors.AddEdge("hub", "A");
        Connectors.AddEdge("hub", "B");
        Connectors.AddEdge("A", "C", ConnectorShape.Branch);
    }

    private void UpdateAnchors()
    {
        Connectors.SetAnchor("main", Connectors.PointFrom(MainBox, 0, 0.5), Connectors.PointFrom(MainBox, 1, 0.5));
        var c = Connectors.PointFrom(Hub, 0.5, 0.5);
        Connectors.SetAnchor("hub", c, c);
        Connectors.SetAnchor("B", Connectors.PointFrom(BoxB, 0, 0.5), Connectors.PointFrom(BoxB, 1, 0.5));
        Connectors.SetAnchor("C", Connectors.PointFrom(BoxC, 0, 0.5), Connectors.PointFrom(BoxC, 1, 0.5));
        // A is the parent of C: it arrives on its left but leaves for the Branch edge from near the left of its bottom edge.
        Connectors.SetAnchor("A", Connectors.PointFrom(BoxA, 0, 0.5), Connectors.PointFrom(BoxA, 0.08, 1));
    }

    private void BuildSwatches()
    {
        foreach (var name in Swatch)
        {
            var color = Tokens.Resolve(name + "Color", Colors.Magenta);
            var chip = new Border
            {
                Width = 120,
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = Tokens.ResolveBrush("HairlineBrush", Colors.Gray),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo"),
                    Foreground = Brushes.White,
                    Margin = new Thickness(5, 3),
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
            };
            Swatches.Children.Add(chip);
        }
    }

    private void BuildGlyphs()
    {
        foreach (var s in Statuses)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 18, 10), Width = 250 };
            cell.Children.Add(new StatusGlyph { Status = s, GlyphSize = 12 });
            cell.Children.Add(new StatusGlyph { Status = s, GlyphSize = 20 });
            cell.Children.Add(new StatusGlyph { Status = s, GlyphSize = 12, Mode = StatusGlyphMode.Ready, Margin = new Thickness(8, 0, 0, 0) });
            cell.Children.Add(new StatusGlyph { Status = s, GlyphSize = 20, Mode = StatusGlyphMode.Ready, Margin = new Thickness(4, 0, 0, 0) });
            cell.Children.Add(new TextBlock { Classes = { "elapsed" }, Text = s, Margin = new Thickness(8, 0, 0, 0) });
            Glyphs.Children.Add(cell);
        }

        // Ready mode with the literal word "tool" (amber gear).
        var tool = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Width = 250 };
        tool.Children.Add(new StatusGlyph { Status = "tool", GlyphSize = 20, Mode = StatusGlyphMode.Ready });
        tool.Children.Add(new TextBlock { Classes = { "elapsed" }, Text = "ready/tool = amber gear" });
        Glyphs.Children.Add(tool);
    }
}
