using System.Windows;
using System.Windows.Controls;

namespace VerseOps.App.Inventory;

/// <summary>
/// DataGrid column header with an inline filter popup. Renders the column label
/// plus a funnel toggle button; clicking the funnel opens a popup with a TextBox
/// bound TwoWay to <see cref="FilterText"/>. Used by the env grid to give every
/// column a per-column substring filter (Sentinel-style).
///
/// IMPORTANT: this control is hosted inside a <c>DataGridColumn.Header</c>, which
/// is *not* part of the visual tree, so binding from the header to the
/// containing view's DataContext via the usual <c>{Binding ...}</c> path does
/// not work. Hosts must bind <see cref="FilterText"/> using
/// <c>RelativeSource={RelativeSource AncestorType=DataGrid}</c> (or similar) so
/// the binding finds a parent that *is* in the visual tree, e.g.:
/// <code>
///   FilterText="{Binding DataContext.FilterName,
///                        RelativeSource={RelativeSource AncestorType=DataGrid},
///                        Mode=TwoWay}"
/// </code>
/// </summary>
public partial class ColumnFilterHeader : UserControl
{
    public ColumnFilterHeader()
    {
        InitializeComponent();
    }

    /// <summary>Column display label (e.g. "Name", "SKU"). Shown left of the funnel.</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColumnFilterHeader),
            new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// TwoWay binding to the host VM's per-column filter string. Empty / null
    /// means "no filter on this column".
    /// </summary>
    public static readonly DependencyProperty FilterTextProperty =
        DependencyProperty.Register(nameof(FilterText), typeof(string), typeof(ColumnFilterHeader),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnFilterTextChanged));

    public string FilterText
    {
        get => (string)GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    /// <summary>
    /// Convenience flag used by the funnel-icon DataTrigger to tint the icon
    /// when a filter is active on this column. Always recomputed in
    /// <see cref="OnFilterTextChanged"/> so the icon updates the moment the
    /// host clears the filter externally.
    /// </summary>
    public static readonly DependencyPropertyKey HasFilterPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasFilter), typeof(bool), typeof(ColumnFilterHeader),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasFilterProperty = HasFilterPropertyKey.DependencyProperty;

    public bool HasFilter
    {
        get => (bool)GetValue(HasFilterProperty);
        private set => SetValue(HasFilterPropertyKey, value);
    }

    private static void OnFilterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ColumnFilterHeader self) return;
        var text = e.NewValue as string;
        self.HasFilter = !string.IsNullOrWhiteSpace(text);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        FilterText = string.Empty;
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        FunnelButton.IsChecked = false;
    }
}
