using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentTeamViewer.Views;

/// <summary>A small modal yes/no prompt in the app's own look (replaces the Windows-only MessageBox). No is the default.</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 13;
        FontWeight = FontWeight.Normal;
        Background = Brush("BgPanelBrush");
        FontFamily = Application.Current?.TryGetResource("MonoFont", ActualThemeVariant, out var f) == true && f is FontFamily ff ? ff : FontFamily.Default;

        var yes = new Button { Content = Label("Yes"), MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = Label("No"), MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextPrimaryBrush"), FontWeight = FontWeight.Normal, LetterSpacing = 0.383 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        Opened += (_, _) => no.Focus();
    }

    /// <summary>13px label with the whole-pixel letter spacing the theme text classes use (LetterSpacing does not inherit).</summary>
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.Normal, LetterSpacing = 0.383 };

    private IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, ActualThemeVariant, out var b) == true ? b as IBrush : null;

    /// <summary>Shows the dialog over <paramref name="owner"/>; true only when the user chose Yes.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message) =>
        await new ConfirmDialog(title, message).ShowDialog<bool>(owner);
}
