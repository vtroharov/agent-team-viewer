using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentTeamViewer.ViewModels;

namespace AgentTeamViewer.Views;

public partial class SessionPickerRow : UserControl
{
    public SessionPickerRow()
    {
        InitializeComponent();
        DeleteButton.AddHandler(PointerPressedEvent, DeleteButton_PointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Tunnel-stage handler so the ComboBoxItem never sees the click: the row is not selected and
    /// the dropdown stays open while the confirmation dialog runs.
    /// </summary>
    private void DeleteButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SessionPickerItem item) item.RequestDelete();
    }
}
