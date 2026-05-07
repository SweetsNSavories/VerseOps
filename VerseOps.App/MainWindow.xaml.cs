using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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

        // Win32 taskbar icon push. WPF's Icon property is unreliable on
        // Win11 with ExtendsContentIntoTitleBar=True — the shell often
        // queries the HWND for its big/small icons before WPF has had a
        // chance to map the resource onto the window class, so the
        // taskbar tile falls back to the generic .NET host icon. We
        // sidestep that by sending WM_SETICON ourselves on the
        // SourceInitialized event, which is the first moment the HWND
        // exists. The .ico is loaded as native HICONs (16x16 small,
        // 32x32 large) directly via LoadImage so the shell sees real
        // shell-quality icons, not WPF BitmapFrames.
        //
        // We push the icon on FOUR ticks because Win11's taskbar
        // grouping/icon resolution is racy:
        //   1. SourceInitialized — first moment the HWND exists. Sets
        //      the per-HWND AppUserModel.ID property store so the shell
        //      groups this window under our brand identity instead of
        //      "dotnet" or the generic .NET host AUMID.
        //   2. Loaded — second push catches the case where the shell
        //      latched onto a stale icon between HWND creation and the
        //      first WM_SETICON.
        //   3. ContentRendered — fired after the first frame paints; the
        //      tile is usually live in the taskbar by now and a fresh
        //      WM_SETICON forces the shell to re-read.
        //   4. Dispatcher.ApplicationIdle (one-shot) — final safety net
        //      in case the shell hasn't polled for icons until the UI
        //      thread goes idle. Cheap and idempotent.
        SourceInitialized += (_, _) => { TagWindowAumid(); ApplyTaskbarIcon(); };
        Loaded            += (_, _) => ApplyTaskbarIcon();
        ContentRendered   += (_, _) => ApplyTaskbarIcon();
        Dispatcher.BeginInvoke(new Action(ApplyTaskbarIcon),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

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

    // ---------- Win32 taskbar icon push ------------------------------------

    private const int WM_SETICON  = 0x0080;
    private const int ICON_SMALL  = 0;
    private const int ICON_BIG    = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE   = 0x00000010;
    private const uint LR_DEFAULTCOLOR   = 0x00000000;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType,
                                           int cxDesired, int cyDesired, uint fuLoad);

    private void ApplyTaskbarIcon()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // The .ico is embedded as a Resource AND copied next to the EXE
            // via <ApplicationIcon>; LoadImage needs a real file path, so
            // resolve to the on-disk copy beside the executable.
            // AppContext.BaseDirectory is more reliable than
            // Assembly.GetEntryAssembly().Location under `dotnet run` and
            // single-file publish — Location can be empty there.
            var exeDir = AppContext.BaseDirectory;
            var icoPath = System.IO.Path.Combine(exeDir, "Assets", "app.ico");
            if (!System.IO.File.Exists(icoPath))
            {
                // Fallback: extract from the embedded WPF resource into a
                // temp file so LoadImage has something to read.
                var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
                using var rs = System.Windows.Application.GetResourceStream(uri)?.Stream;
                if (rs is null) return;
                icoPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "verseops-app.ico");
                using var fs = System.IO.File.Create(icoPath);
                rs.CopyTo(fs);
            }

            // Load all four standard taskbar / Alt-Tab sizes. The shell
            // picks the closest match for each surface (16/24 = Alt-Tab
            // tooltip, 32/48 = taskbar tile + jump-list header). Pushing
            // both ICON_SMALL and ICON_BIG with the higher-DPI variant
            // prevents the shell from up/down-scaling and producing the
            // muddy "icon is just a blue square" rendering seen in some
            // Win11 builds.
            var hSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE | LR_DEFAULTCOLOR);
            var hBig   = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTCOLOR);
            if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hSmall);
            if (hBig   != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   hBig);
        }
        catch { /* fall back to whatever WPF set via XAML Icon */ }
    }

    // ---------- Per-HWND AppUserModel.ID property store --------------------
    // Win11's taskbar grouping is driven by the per-window
    // System.AppUserModel.ID property — NOT just the process-wide AUMID
    // set in App.OnStartup. If we don't tag the HWND, the shell may
    // group VerseOps under whatever AUMID the .NET host advertised
    // first, and the tile then keeps the generic .NET icon for ~30-60s
    // even though we send WM_SETICON. Tagging the HWND directly forces
    // the shell to identify the window as VerseOps from the moment the
    // HWND becomes visible, so the icon push lands on the correct tile
    // 100% of the time.
    private void TagWindowAumid()
    {
        IntPtr propStorePtr = IntPtr.Zero;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out propStorePtr);
            if (hr != 0 || propStorePtr == IntPtr.Zero) return;

            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(propStorePtr);
            try
            {
                var key = PKEY_AppUserModel_ID;
                var value = new PROPVARIANT();
                try
                {
                    InitPropVariantFromString("VerseOps.PowerPlatformInventory", out value);
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch { /* property store unavailable on this OS — process-wide AUMID still helps */ }
        finally
        {
            if (propStorePtr != IntPtr.Zero) Marshal.Release(propStorePtr);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void InitPropVariantFromString(
        [MarshalAs(UnmanagedType.LPWStr)] string psz,
        out PROPVARIANT ppropvar);

    // System.AppUserModel.ID — {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} pid 5
    private static PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid   = 5
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int  pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1, p2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }
}
