using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace VerseOps.UiTests;

/// <summary>
/// Boots the VerseOps WPF app exactly once per test class, attaches the
/// FlaUI <see cref="UIA3Automation"/> driver to it, and exposes the main
/// window so individual tests can interrogate / drive controls.
///
/// Test isolation strategy: each test class gets its own fixture instance
/// (xUnit IClassFixture lifecycle), so two classes never share a process.
/// Within a single class, tests run sequentially against the same window.
///
/// Theme preference is reset to a known state on construction so the
/// theme-toggle round-trip test starts deterministic.
/// </summary>
public sealed class AppFixture : IDisposable
{
    /// <summary>Caption shown in the title bar — used to find the window.</summary>
    public const string WindowTitleSubstring = "VerseOps";

    /// <summary>AutomationId stamped onto FluentWindow in MainWindow.xaml.</summary>
    public const string MainWindowAutomationId = "VerseOpsMainWindow";

    /// <summary>%LOCALAPPDATA%\VerseOps — App.xaml.cs writes theme.txt here.</summary>
    public static readonly string AppStateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VerseOps");

    public static readonly string ThemePrefPath = Path.Combine(AppStateDir, "theme.txt");

    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window MainWindow { get; }

    public AppFixture()
    {
        // Wipe any prior crash log so tests start clean — failures during
        // this run will populate a fresh file.
        TryDelete(Path.Combine(AppStateDir, "startup-error.log"));

        // Force a known starting theme (Dark) so tests that toggle and
        // assert "moved away from Dark" don't depend on the dev's last
        // session.
        Directory.CreateDirectory(AppStateDir);
        File.WriteAllText(ThemePrefPath, "Dark");

        var exe = ResolveExePath();
        Assert.True(File.Exists(exe), $"VerseOps.App.exe not found at {exe} — build the app project first.");

        App = Application.Launch(exe);
        Automation = new UIA3Automation();

        // Wait for the FluentWindow to be visible. WPF + Mica + ExtendsContentIntoTitleBar
        // can take a moment to compose the first frame on cold-start.
        MainWindow = WaitForMainWindow(App, Automation, TimeSpan.FromSeconds(20));

        // Header chrome (theme toggle, refresh, hero tiles) is part of
        // InventoryView, which is loaded as the window's Content. Give
        // WPF a brief best-effort window (5s) to publish the descendant
        // tree to UIA before tests start running. We do NOT throw if the
        // marker isn't found within that window: in some launches the
        // UIA tree only resolves descendants once a real input event hits
        // the window. Tests that need the descendant call
        // <see cref="WaitForDescendantAutomationId"/> on their own with
        // their own timeout.
        TryWaitForDescendantAutomationId(MainWindow, "ThemeToggleButton", TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Spin until <paramref name="parent"/> exposes a descendant with the
    /// given <paramref name="automationId"/>, or the timeout elapses.
    /// </summary>
    public static AutomationElement WaitForDescendantAutomationId(
        AutomationElement parent,
        string automationId,
        TimeSpan timeout)
    {
        var hit = TryWaitForDescendantAutomationId(parent, automationId, timeout);
        if (hit != null) return hit;
        throw new TimeoutException(
            $"AutomationId '{automationId}' was not present under window '{parent.Name}' within {timeout.TotalSeconds}s.");
    }

    /// <summary>
    /// Same as <see cref="WaitForDescendantAutomationId"/> but returns
    /// null on timeout instead of throwing. Used during fixture setup so a
    /// slow first paint doesn't fail the entire class fixture.
    /// </summary>
    public static AutomationElement? TryWaitForDescendantAutomationId(
        AutomationElement parent,
        string automationId,
        TimeSpan timeout)
    {
        var cf = parent.ConditionFactory;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var hit = parent.FindFirstDescendant(cf.ByAutomationId(automationId));
                if (hit != null) return hit;
            }
            catch
            {
                // Treat element-not-available exceptions during early paint
                // as transient — keep polling until the timeout.
            }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>
    /// Walk up from this assembly's bin folder to the repo root, then dive
    /// into VerseOps.App's bin to find the EXE. Works for both Debug/Release.
    /// </summary>
    private static string ResolveExePath()
    {
        var asmDir = Path.GetDirectoryName(typeof(AppFixture).Assembly.Location)!;
        // bin\<Configuration>\<TFM>\
        var configuration = new DirectoryInfo(asmDir).Parent?.Name ?? "Debug";

        // Test bin: ...\VerseOps.UiTests\bin\<cfg>\<tfm>
        // App bin:  ...\VerseOps.App\bin\<cfg>\<tfm>\VerseOps.App.exe
        var dir = new DirectoryInfo(asmDir);
        // climb 4 levels (tfm -> cfg -> bin -> project)
        for (int i = 0; i < 4 && dir?.Parent != null; i++) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not find repo root.");

        return Path.Combine(dir.FullName, "VerseOps.App", "bin", configuration, "net10.0-windows", "VerseOps.App.exe");
    }

    private static Window WaitForMainWindow(Application app, UIA3Automation automation, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var win = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
                if (win != null && win.Title?.Contains(WindowTitleSubstring, StringComparison.OrdinalIgnoreCase) == true)
                    return win;
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(250);
        }
        throw new TimeoutException(
            $"Did not find a window with title containing '{WindowTitleSubstring}' within {timeout.TotalSeconds}s. Last error: {last?.Message}");
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    public void Dispose()
    {
        try { App.Close(); } catch { }
        try { App.Dispose(); } catch { }
        try { Automation.Dispose(); } catch { }
    }
}
