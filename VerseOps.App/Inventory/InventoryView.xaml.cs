using System.Windows.Controls;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory;
/// <summary>
/// Inventory dashboard view. The DataContext is an <see cref="InventoryViewModel"/>
/// supplied by <see cref="MainWindow"/>; this code-behind only wires up the XAML.
/// </summary>
public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Selection alone no longer auto-expands the row (that fought the new
    /// chevron toggle and made it impossible to "collapse" a row without
    /// clicking somewhere else). The chevron column is the single source of
    /// truth for visibility; selection just drives row highlight.
    /// </summary>
    private void EnvGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // intentionally a no-op — see EnvExpandToggle_Click for the real load.
    }

    /// <summary>
    /// Chevron toggle in the leading column of the env grid. Flips
    /// <see cref="EnvironmentRow.IsExpanded"/> (which is wired to
    /// <c>DataGridRow.DetailsVisibility</c> via a DataTrigger), and on the
    /// first expansion fires the per-env Dataverse drill-down. Decoupled
    /// from selection so multiple rows can be open simultaneously.
    /// </summary>
    private void EnvExpandToggle_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ButtonBase btn) return;
        if (btn.DataContext is not EnvironmentRow row) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded && DataContext is InventoryViewModel vm && !row.DetailsLoaded)
            _ = vm.LoadEnvironmentDetailsAsync(row);
    }

    /// <summary>
    /// Per-row "Group by Solution" / "Flat" radio toggle. Two-way data binding
    /// on RadioButton.IsChecked is famously fragile in WPF (the unchecked side
    /// of a group rarely propagates back to the source), so we drive both
    /// properties explicitly from a Click handler. Tag carries the choice.
    /// </summary>
    private void ViewModeRadio_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb) return;
        if (rb.DataContext is not EnvironmentRow row) return;
        var pick = rb.Tag as string;
        if (string.Equals(pick, "flat", System.StringComparison.OrdinalIgnoreCase))
            row.IsFlatView = true;
        else
            row.IsSolutionsView = true;
    }

    /// <summary>
    /// Mouse-wheel routing fix for the env-row details ScrollViewer. The
    /// per-env detail panel stacks several inner DataGrids (Solutions / Apps
    /// / Flows / Agents / Power Pages / Users), each of which has its own
    /// internal ScrollViewer. By default WPF *handles* the MouseWheel event
    /// inside each inner DataGrid even when the inner ScrollViewer is at its
    /// extent, which traps the wheel input and prevents the outer
    /// row-details ScrollViewer from ever scrolling — so the user can never
    /// reach the Users grid at the bottom of the details panel by scrolling.
    /// We intercept at PreviewMouseWheel (tunneling phase, runs before the
    /// inner grids see it), and:
    ///
    ///   * If the outer details ScrollViewer still has room to scroll in the
    ///     wheel direction → consume the event and scroll only the details
    ///     panel (so wheel inside an inner DataGrid doesn't scroll the inner
    ///     grid; the user sees the whole details panel scroll smoothly).
    ///   * If the outer details ScrollViewer has hit the extent in the wheel
    ///     direction → do NOT mark Handled, so the wheel bubbles up to the
    ///     env DataGrid and the env row itself scrolls. Without this, once
    ///     the user reached the bottom of the details panel they'd be stuck
    ///     and couldn't reveal the env rows below by wheel-scrolling.
    /// </summary>
    private void DetailsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // Wheel up (delta > 0) scrolls toward offset 0; wheel down toward extent.
        var atTop    = sv.VerticalOffset <= 0.0;
        var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 0.5;
        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
            return; // let the env DataGrid handle it
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
