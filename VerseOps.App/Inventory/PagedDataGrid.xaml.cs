using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace VerseOps.App.Inventory;

/// <summary>
/// Reusable wrapper around a <see cref="DataGrid"/> that adds two pieces of
/// chrome the stock control doesn't have:
///
///   * A per-column filter affordance: each searchable column header gets
///     a small funnel toggle button next to the label. Clicking it opens
///     a Popup with a TextBox + Clear / Apply buttons. The funnel switches
///     to the brand-accent color while a filter is active so the header
///     doubles as a visual indicator. Filters are ANDed across columns.
///   * A pager footer with PREV / NEXT chevrons, the current page text
///     ("Page 3 of 12 • 287 items") and a page-size combo (10/25/50/100).
///
/// Caller usage looks the same as a normal DataGrid except the wrapping
/// element is <c>local:PagedDataGrid</c> and the column children are
/// content (no <c>DataGrid.Columns</c> wrapper needed):
/// <code>
///   &lt;local:PagedDataGrid ItemsSource="{Binding AllApps}"&gt;
///     &lt;DataGridTextColumn Header="Name" Binding="{Binding DisplayName}"/&gt;
///     ...
///   &lt;/local:PagedDataGrid&gt;
/// </code>
///
/// Implementation notes:
///   * <see cref="ContentPropertyAttribute"/> on <see cref="Columns"/> means
///     XAML children of this control are added straight into the column
///     collection, mirroring how <c>DataGrid</c> normally consumes columns.
///   * Bindings inside cell templates that reach for the parent
///     <c>InventoryViewModel</c> via <c>RelativeSource AncestorType=UserControl</c>
///     would now stop at this control (which IS a UserControl) and find
///     <c>EnvironmentRow</c> as the DataContext. Callers must use
///     <c>AncestorType={x:Type local:InventoryView}</c> instead so the lookup
///     walks past us to the real owner.
/// </summary>
[ContentProperty(nameof(Columns))]
public partial class PagedDataGrid : UserControl
{
    public PagedDataGrid()
    {
        InitializeComponent();
        Columns.CollectionChanged += OnColumnsChanged;
        InnerGrid.ItemsSource = _pageItems;
        Loaded += (_, _) => Refresh(); // first paint after templates are alive
    }

    // ---------- Public DPs / content ---------------------------------------

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PagedDataGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>The full source collection. Subscribes to INotifyCollectionChanged when present.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty PageSizeProperty =
        DependencyProperty.Register(nameof(PageSize), typeof(int), typeof(PagedDataGrid),
            new PropertyMetadata(10, OnPageSizeChanged));

    /// <summary>Items per page. Defaults to 10; user can change via the footer combo.</summary>
    public int PageSize
    {
        get => (int)GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    /// <summary>
    /// Forwarded to the inner DataGrid. Marked as <see cref="ContentPropertyAttribute"/>
    /// so XAML children land here directly. We don't bind to
    /// <c>InnerGrid.Columns</c> until <see cref="OnColumnsChanged"/> fires
    /// because the templates aren't loaded at construction time.
    /// </summary>
    public ObservableCollection<DataGridColumn> Columns { get; } = new();

    // ---------- Internal state --------------------------------------------

    private readonly ObservableCollection<object> _pageItems = new();
    private List<object> _allItems = new();
    private List<object> _filteredItems = new();
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase);
    private int _pageIndex; // 0-based
    private bool _suppressPageSizeCallback;

    /// <summary>
    /// Sentinel value stamped onto every header Grid we build, so a
    /// subsequent <see cref="OnColumnsChanged"/> rebuild can detect already-
    /// wrapped columns and skip them instead of re-running .ToString() over
    /// the previously-built Grid (which would print
    /// "System.Windows.Controls.Grid" as the column label).
    /// </summary>
    private const string WrappedHeaderTag = "__pdg_wrapped_header__";

    private int PageCount =>
        _filteredItems.Count == 0 ? 1 : (int)Math.Ceiling(_filteredItems.Count / (double)Math.Max(1, PageSize));

    // ---------- Columns wiring --------------------------------------------

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // We always rebuild from scratch — column counts are small and this
        // sidesteps having to track per-action add/remove/replace. The
        // "already wrapped" guard inside WrapColumn keeps repeated rebuilds
        // (one fires per Add as XAML hydrates the collection) idempotent so
        // we don't lose the original header text on the second pass.
        InnerGrid.Columns.Clear();
        foreach (var col in Columns)
            InnerGrid.Columns.Add(WrapColumn(col));
    }

    /// <summary>
    /// Replaces the column's <c>Header</c> with a Grid that renders the
    /// original header text alongside a small filter-funnel toggle button.
    /// Clicking the toggle opens a Popup with a TextBox and Clear / Apply
    /// buttons (matches the user-requested UX). The funnel icon adopts the
    /// brand accent color whenever a non-empty filter is active so the
    /// header doubles as a quick "what's filtered" indicator. Skips
    /// non-bound columns (Actions / template columns) — those keep their
    /// original header untouched.
    /// </summary>
    private DataGridColumn WrapColumn(DataGridColumn col)
    {
        var path = ExtractBindingPath(col);
        if (string.IsNullOrEmpty(path))
        {
            // Template columns / unbound columns: leave header alone.
            return col;
        }

        // OnColumnsChanged fires once per Add as XAML hydrates the
        // ObservableCollection<DataGridColumn>; without this guard we'd
        // re-wrap each column N times, and on the second call our Grid
        // would BE the existing header — so col.Header?.ToString() would
        // return "System.Windows.Controls.Grid" and we'd display that as
        // the column label. Stamp the column on first wrap so subsequent
        // rebuilds are no-ops.
        if (col.Header is Grid existingGrid && existingGrid.Tag as string == WrappedHeaderTag)
            return col;

        var labelText = col.Header?.ToString() ?? path!;

        // ---- Header surface: [ label ........ funnel ] -------------------
        var header = new Grid { Tag = WrappedHeaderTag };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = labelText,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        // The funnel icon. We toggle Foreground (and the wpf-ui Filled
        // variant when available) to indicate an active filter.
        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Filter24,
            FontSize = 12,
            Foreground = (Brush?)TryFindResource("TokenTextSecondary") ?? Brushes.Gray
        };
        var toggleBtn = new ToggleButton
        {
            Content = icon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = $"Filter by {labelText}"
        };
        // Suppress the parent column-header sort behavior so clicking the
        // funnel doesn't accidentally re-sort the grid.
        toggleBtn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false; // toggle handles itself
        toggleBtn.Click += (_, e) => e.Handled = true;
        Grid.SetColumn(toggleBtn, 1);
        header.Children.Add(toggleBtn);

        // ---- Popup: [ "Filter by X"  | tbox  | Clear  Apply ] ------------
        var popup = new Popup
        {
            PlacementTarget = toggleBtn,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        var popupBorder = new Border
        {
            Background     = (Brush?)TryFindResource("TokenSurfacePrimary") ?? Brushes.White,
            BorderBrush    = (Brush?)TryFindResource("TokenStrokeSubtle")   ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius   = new CornerRadius(4),
            Padding        = new Thickness(10),
            Effect = new DropShadowEffect
            {
                BlurRadius   = 12,
                ShadowDepth  = 2,
                Direction    = 270,
                Opacity      = 0.25,
                Color        = Colors.Black
            }
        };

        var stack = new StackPanel { Width = 240 };
        stack.Children.Add(new TextBlock
        {
            Text = $"Filter by {labelText}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var tbox = new TextBox
        {
            Padding = new Thickness(6, 4, 6, 4),
            Margin  = new Thickness(0, 0, 0, 8),
            ToolTip = "Type to filter (case-insensitive Contains)"
        };
        // Pre-populate with any existing filter so the user can edit it.
        if (_filters.TryGetValue(path!, out var existing)) tbox.Text = existing;
        stack.Children.Add(tbox);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var clearBtn = new Button
        {
            Content = "Clear",
            Padding = new Thickness(12, 3, 12, 3),
            Margin  = new Thickness(0, 0, 6, 0)
        };
        var applyBtn = new Button
        {
            Content = "Apply",
            Padding = new Thickness(12, 3, 12, 3),
            Background      = (Brush?)TryFindResource("TokenBrandBackground") ?? Brushes.SteelBlue,
            Foreground      = (Brush?)TryFindResource("TokenTextOnAccent")    ?? Brushes.White,
            BorderThickness = new Thickness(0),
            IsDefault       = true
        };
        btnRow.Children.Add(clearBtn);
        btnRow.Children.Add(applyBtn);
        stack.Children.Add(btnRow);

        popupBorder.Child = stack;
        popup.Child = popupBorder;
        // Attach the popup to the header tree so it shares the visual tree
        // with PlacementTarget. Popup is a special UIElement that renders
        // in its own HWND but still needs to be part of the logical tree
        // for resource resolution.
        header.Children.Add(popup);

        // ---- Apply / clear / open behaviors ------------------------------
        void Apply(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                _filters.Remove(path!);
                icon.Filled = false;
                icon.Foreground = (Brush?)TryFindResource("TokenTextSecondary") ?? Brushes.Gray;
                toggleBtn.ToolTip = $"Filter by {labelText}";
            }
            else
            {
                _filters[path!] = value!;
                icon.Filled = true;
                icon.Foreground = (Brush?)TryFindResource("TokenBrandForeground") ?? Brushes.SteelBlue;
                toggleBtn.ToolTip = $"Filtered: \"{value}\" — click to edit";
            }
            _pageIndex = 0;
            Refresh();
            popup.IsOpen = false;
            toggleBtn.IsChecked = false;
        }

        toggleBtn.Checked   += (_, _) => popup.IsOpen = true;
        toggleBtn.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Opened += (_, _) =>
        {
            // Re-sync the textbox in case the filter was cleared from
            // elsewhere, then focus + select-all so the user can just type.
            tbox.Text = _filters.TryGetValue(path!, out var v) ? v : string.Empty;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tbox.Focus();
                tbox.SelectAll();
            }), DispatcherPriority.Background);
        };
        popup.Closed += (_, _) => toggleBtn.IsChecked = false;

        applyBtn.Click += (_, _) => Apply(tbox.Text);
        clearBtn.Click += (_, _) => { tbox.Text = string.Empty; Apply(string.Empty); };
        tbox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  { Apply(tbox.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { popup.IsOpen = false; toggleBtn.IsChecked = false; e.Handled = true; }
        };

        col.Header = header;
        return col;
    }

    private static string? ExtractBindingPath(DataGridColumn col) => col switch
    {
        DataGridBoundColumn bc when bc.Binding is Binding b => b.Path?.Path,
        _ => null
    };

    // ---------- ItemsSource wiring ----------------------------------------

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PagedDataGrid)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= self.OnSourceCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += self.OnSourceCollectionChanged;
        self.RebuildSource();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildSource();

    private void RebuildSource()
    {
        _allItems = ItemsSource is IEnumerable ie
            ? ie.Cast<object>().ToList()
            : new List<object>();
        _pageIndex = 0;
        Refresh();
    }

    // ---------- Filter / page recompute -----------------------------------

    private void Refresh()
    {
        _filteredItems = _filters.Count == 0
            ? _allItems
            : _allItems.Where(MatchesAllFilters).ToList();
        if (_pageIndex >= PageCount) _pageIndex = Math.Max(0, PageCount - 1);

        _pageItems.Clear();
        foreach (var x in _filteredItems.Skip(_pageIndex * PageSize).Take(PageSize))
            _pageItems.Add(x);

        var total = _filteredItems.Count;
        if (total == 0)
        {
            PagerText.Text = "0 items";
        }
        else
        {
            // Use an explicit dot separator so the pager reads cleanly even
            // in narrow layouts; the bullet aligns with the rest of the UI.
            PagerText.Text = $"Page {_pageIndex + 1} of {PageCount} • {total:N0} item{(total == 1 ? "" : "s")}";
        }
        PrevBtn.IsEnabled = _pageIndex > 0;
        NextBtn.IsEnabled = _pageIndex < PageCount - 1;
    }

    private bool MatchesAllFilters(object? item)
    {
        if (item == null) return false;
        var t = item.GetType();
        foreach (var kvp in _filters)
        {
            if (string.IsNullOrEmpty(kvp.Value)) continue;
            // Reflection is fine here — collections are <= a few thousand
            // items and we only enumerate on filter / page changes (not per
            // wheel scroll). PropertyInfo lookups could be cached per-type
            // if profiling ever flags this.
            var prop = t.GetProperty(kvp.Key, BindingFlags.Public | BindingFlags.Instance);
            var val = prop?.GetValue(item)?.ToString() ?? string.Empty;
            if (val.IndexOf(kvp.Value, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    // ---------- Footer event handlers -------------------------------------

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex <= 0) return;
        _pageIndex--;
        Refresh();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex >= PageCount - 1) return;
        _pageIndex++;
        Refresh();
    }

    private static void OnPageSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PagedDataGrid)d;
        self._pageIndex = 0;
        // Keep the combo in sync if PageSize is set externally (e.g., XAML default).
        if (self.PageSizeCombo != null)
        {
            var newSize = (int)e.NewValue;
            self._suppressPageSizeCallback = true;
            try
            {
                foreach (ComboBoxItem ci in self.PageSizeCombo.Items)
                {
                    if (int.TryParse(ci.Content?.ToString(), out var v) && v == newSize)
                    {
                        self.PageSizeCombo.SelectedItem = ci;
                        break;
                    }
                }
            }
            finally { self._suppressPageSizeCallback = false; }
        }
        self.Refresh();
    }

    private void PageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPageSizeCallback) return;
        if (PageSizeCombo.SelectedItem is ComboBoxItem ci &&
            int.TryParse(ci.Content?.ToString(), out var v))
        {
            PageSize = v;
        }
    }
}
