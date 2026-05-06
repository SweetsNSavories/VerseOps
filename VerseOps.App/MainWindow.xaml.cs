using System.Windows.Interop;
using VerseOps.App.Auth;
using VerseOps.App.Inventory;
using VerseOps.App.Inventory.Services;
// Bring in just the FluentWindow type — a `using Wpf.Ui.Controls;` would
// collide with System.Windows.Controls.TextBox / TreeViewItem etc.
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace VerseOps.App;

/// <summary>
/// Shell window for the inventory console. After the May 2026 refactor
/// the API Explorer tab and Sdk explorer tab were extracted to a separate
/// tool, so this window is now a thin host for <c>InventoryView</c>:
///
///   * Constructs <see cref="AuthService"/> in delegated/User mode with the
///     Azure CLI public client id (system-browser interactive sign-in, no
///     WAM broker — see comments on <see cref="AuthService.UseBroker"/>).
///   * Wires the parent window handle so any future broker prompt can
///     parent itself correctly.
///   * Builds the <see cref="PpacInventoryService"/> + SQLite catalog and
///     hands the resulting <see cref="InventoryViewModel"/> to the
///     <c>InventoryView</c> as its DataContext.
///
/// Authentication happens silently on first token call (PPAC refresh,
/// Microsoft Graph license pull, per-env Dataverse loads). The user only
/// ever sees an interactive sign-in window the first time they refresh,
/// or after their MSAL cache expires.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly AuthService _auth = new();
    private readonly InventoryViewModel _inventoryVm;

    public MainWindow()
    {
        InitializeComponent();

        // Silent auth pipeline — no UI controls. Defaults on AuthService
        // (User mode, "common" tenant, Azure CLI public client, broker off)
        // are already what the inventory pipeline needs; we only override
        // the window-handle provider so any rare interactive prompt parents
        // to this window instead of the desktop.
        _auth.WindowHandleProvider = () =>
        {
            try { return new WindowInteropHelper(this).EnsureHandle(); }
            catch { return IntPtr.Zero; }
        };

        // Inventory pipeline: SQLite catalog (cache) → PPAC SDK service
        // (refresh) → view-model (binds to InventoryView).
        var sqliteCatalog    = new SqliteCatalog();
        var inventoryService = new PpacInventoryService(_auth, sqliteCatalog);
        _inventoryVm         = new InventoryViewModel(inventoryService);
        InventoryDashboard.DataContext = _inventoryVm;
    }
}
