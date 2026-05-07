using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.UiTests;

/// <summary>
/// Smoke + chrome-level regression tests for the inventory cockpit.
///
/// What we test:
///   • Window launches with the expected title + minimum dimensions.
///   • All five hero tiles are present and clickable.
///   • Refresh + Cancel buttons exist; Refresh is enabled at idle.
///   • Theme toggle button switches Dark↔Light and persists to disk.
///   • Toolbar utility buttons (Reload from cache, Open trace log) exist.
///   • Environment search box exists and accepts input.
///   • Environments DataGrid is laid out (even if empty without auth).
///
/// What we deliberately do NOT test:
///   • Sign-in / token acquisition (requires interactive Entra prompt).
///   • Refresh execution (requires live PPAC + tenant data).
///   • Drawer contents (depend on real inventory data).
/// </summary>
public class InventoryUiSmokeTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _fix;
    private readonly ITestOutputHelper _log;

    public InventoryUiSmokeTests(AppFixture fix, ITestOutputHelper log)
    {
        _fix = fix;
        _log = log;
    }

    [Fact]
    public void MainWindow_Has_Expected_Title_And_Dimensions()
    {
        var w = _fix.MainWindow;
        Assert.Contains("VerseOps", w.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(w.BoundingRectangle.Width  >= 800,  $"window too narrow: {w.BoundingRectangle.Width}");
        Assert.True(w.BoundingRectangle.Height >= 600,  $"window too short: {w.BoundingRectangle.Height}");
        _log.WriteLine($"MainWindow: '{w.Title}' {w.BoundingRectangle.Width}x{w.BoundingRectangle.Height}");
    }

    [Theory]
    [InlineData("HeroTileEnvironments")]
    [InlineData("HeroTileTenantDatabase")]
    [InlineData("HeroTileTenantFile")]
    [InlineData("HeroTileTotalAssets")]
    [InlineData("HeroTileLicensedUsers")]
    public void All_Five_Hero_Tiles_Are_Present(string automationId)
    {
        var btn = FindByAutomationId(automationId);
        Assert.NotNull(btn);
        Assert.True(btn!.IsEnabled, $"hero tile '{automationId}' is disabled");
    }

    [Fact]
    public void Refresh_Button_Is_Enabled_At_Idle()
    {
        var refresh = FindByAutomationId("RefreshButton");
        Assert.NotNull(refresh);
        Assert.True(refresh!.IsEnabled, "Refresh button should be enabled at idle.");
    }

    [Fact]
    public void Cancel_Button_Is_Disabled_At_Idle()
    {
        // CancelRefreshCommand.CanExecute returns false until a refresh is in flight,
        // so the button binds to a disabled command at startup.
        var cancel = FindByAutomationId("CancelRefreshButton");
        Assert.NotNull(cancel);
        Assert.False(cancel!.IsEnabled, "Cancel should be disabled while no refresh is running.");
    }

    [Fact]
    public void Toolbar_Has_Reload_And_Trace_Log_Buttons()
    {
        Assert.NotNull(FindByAutomationId("ReloadFromCacheButton"));
        Assert.NotNull(FindByAutomationId("OpenTraceLogButton"));
    }

    [Fact]
    public void Search_Box_Accepts_Input()
    {
        var box = FindByAutomationId("EnvSearchBox")?.AsTextBox();
        Assert.NotNull(box);
        box!.Text = "preview";
        Assert.Equal("preview", box.Text);

        // Clear so subsequent tests don't see filtered grid.
        box.Text = string.Empty;
    }

    [Fact]
    public void Environments_Grid_Is_Present()
    {
        var grid = FindByAutomationId("EnvironmentsGrid");
        Assert.NotNull(grid);
        // Grid may be empty (no auth) — we only assert it exists and is laid out.
        Assert.True(grid!.BoundingRectangle.Height > 0);
    }

    [Fact]
    public void Theme_Toggle_Round_Trip_Persists_To_Disk()
    {
        // Starting state per AppFixture: Dark.
        var toggle = FindByAutomationId("ThemeToggleButton")?.AsButton();
        Assert.NotNull(toggle);

        // First click → Light. Use Invoke (UIA InvokePattern) which calls
        // the button's Click handler synchronously on the dispatcher.
        toggle!.Invoke();
        WaitForThemePref("Light", TimeSpan.FromSeconds(5));

        // Pump the UI for a moment so the first Click + ApplyTheme +
        // ApplicationThemeManager.Changed handler fully settles. Without
        // this, a second Invoke() inside the same UIA input frame can be
        // coalesced and never fire the Click handler again.
        Thread.Sleep(500);

        // Second click → back to Dark. Re-resolve the button (the first
        // theme swap rebuilds the visual tree of header chrome) and click
        // through the mouse so we exercise an independent input chain.
        var toggle2 = FindByAutomationId("ThemeToggleButton")?.AsButton();
        Assert.NotNull(toggle2);
        toggle2!.Click();
        WaitForThemePref("Dark", TimeSpan.FromSeconds(5));
    }

    // -------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------

    private AutomationElement? FindByAutomationId(string id)
    {
        var cf = _fix.MainWindow.ConditionFactory;
        // Descendants search — controls are deep inside FluentWindow's
        // title-bar / content composition tree.
        return _fix.MainWindow.FindFirstDescendant(cf.ByAutomationId(id));
    }

    private static void WaitForThemePref(string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string actual = "<missing>";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(AppFixture.ThemePrefPath))
            {
                actual = File.ReadAllText(AppFixture.ThemePrefPath).Trim();
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"Expected theme.txt='{expected}' within {timeout.TotalSeconds}s, last value='{actual}'");
    }
}
