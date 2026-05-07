using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
// Disambiguate MessageBox / MessageBoxButton — both Wpf.Ui.Controls and
// System.Windows expose these names. The crash dump uses the classic Win32
// dialog so the user always sees something even if WPF-UI is broken.
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;

namespace VerseOps.App;

/// <summary>
/// Interaction logic for App.xaml.
///
/// Two responsibilities beyond the default WPF App:
///
///   1. Theme management — Fluent v2 light/dark + system-follow. WPF-UI's
///      ApplicationThemeManager handles its own control templates; we
///      additionally swap our Token brushes by replacing the Tokens.Dark.xaml
///      merged dictionary with Tokens.Light.xaml on theme change. The user's
///      choice is persisted in %LOCALAPPDATA%\VerseOps\theme.txt.
///
///   2. Crash diagnostics — every unhandled UI-thread / background-task /
///      AppDomain exception is dumped to %LOCALAPPDATA%\VerseOps\startup-error.log
///      and surfaced via MessageBox so silent exit-code-1 launches stop being
///      silent.
/// </summary>
public partial class App : Application
{
    private const string TokensDarkPack  = "pack://application:,,,/Themes/Tokens.Dark.xaml";
    private const string TokensLightPack = "pack://application:,,,/Themes/Tokens.Light.xaml";

    private static string ThemeStateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerseOps");
    private static string ThemeStatePath => Path.Combine(ThemeStateDir, "theme.txt");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DumpFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, e) =>
        {
            // WPF-UI's default Button hover storyboard animates
            // Background.(SolidColorBrush.Color). When a Button is rendered
            // before its Background DP has been materialised as a SolidColorBrush
            // (common in the first paint of the row-details template), the
            // ColorAnimation throws "'Background' property does not point to a
            // DependencyObject in path '(0).(1)'" the first time the mouse hovers.
            // The error is harmless — the next paint resolves the brush and
            // future hovers succeed — so silently swallow it instead of popping
            // a modal dialog on every mouse-over.
            if (IsStoryboardBackgroundAnimationGlitch(e.Exception))
            {
                LogSilently("Dispatcher.UnhandledException (suppressed: WPF-UI hover storyboard)", e.Exception);
                e.Handled = true;
                return;
            }
            DumpFatal("Dispatcher.UnhandledException", e.Exception);
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DumpFatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static bool IsStoryboardBackgroundAnimationGlitch(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException &&
                e.Message.Contains("'Background' property does not point to a DependencyObject", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static void LogSilently(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(ThemeStateDir);
            File.AppendAllText(Path.Combine(ThemeStateDir, "startup-error.log"),
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-resort: nothing to do if even logging fails
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pin a stable Win32 AppUserModelID BEFORE any window's HWND exists.
        // Without this, the Win11 shell groups VerseOps under the generic
        // .NET host AUMID and only re-resolves the window's WM_SETICON the
        // *second* time it sees the HWND — which is why the taskbar tile
        // would flash the default .NET host glyph for ~30-60s before
        // switching to our brand "P". Setting an app-specific AUMID up
        // front means the shell registers the tile against our identity
        // immediately, so the first WM_SETICON push from MainWindow takes
        // effect on first paint instead of after a deferred refresh.
        try { SetCurrentProcessExplicitAppUserModelID("VerseOps.PowerPlatformInventory"); }
        catch { /* shell32 absent / older OS — taskbar fallback still works */ }

        // Light-only build. Force WPF-UI's controls dictionary into Light
        // before any window paints so Fluent control templates (TitleBar,
        // Snackbar, ToggleSwitch, etc.) match our token brushes. The
        // Tokens.Light.xaml merged dictionary is already wired in App.xaml.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);

    /// <summary>
    /// Theme switching is disabled in the light-only build. Kept as a no-op
    /// so the existing header button click handler still compiles — the
    /// toggle UI will be hidden in XAML.
    /// </summary>
    public static void ApplyTheme(ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
    }

    /// <summary>No-op in the light-only build; returns the locked theme.</summary>
    public static ApplicationTheme ToggleTheme() => ApplicationTheme.Light;

    private static void DumpFatal(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(ThemeStateDir);
            var path = Path.Combine(ThemeStateDir, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            MessageBox.Show(
                $"VerseOps crashed during startup.\n\nSource: {source}\n\n{ex}\n\nLogged to:\n{path}",
                "VerseOps — startup failure",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // last-resort: nothing to do if even logging fails
        }
    }
}

