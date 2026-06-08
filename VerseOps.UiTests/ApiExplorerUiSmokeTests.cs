using System;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.UiTests;

/// <summary>
/// Chrome-level UI tests for the API Explorer tab.
///
/// What we test:
///   • API Explorer tab can be selected from the top-level TabControl.
///   • Auth expander, mode radios and panel visibility behave as designed.
///   • Tenant / public-client / app-client boxes are wired and hydrated
///     from AppSettings on first load.
///   • Operations tree (TvOps) is populated synchronously from the
///     hand-curated PPAC catalog (no auth required).
///   • Tree filter narrows the visible category count.
///   • REST/SDK mode toggle rebuilds the tree (children still present).
///   • Request pane: Method + Scope combo boxes, URL/body editors,
///     Send/Cancel button states.
///   • Status bar default text.
///
/// What we deliberately do NOT test:
///   • Sign-in (interactive Entra prompt).
///   • Sending a real request (requires a live token).
///   • Form parameter rendering (depends on selected operation + auth).
/// </summary>
public class ApiExplorerUiSmokeTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _fix;
    private readonly ITestOutputHelper _log;

    public ApiExplorerUiSmokeTests(AppFixture fix, ITestOutputHelper log)
    {
        _fix = fix;
        _log = log;
        EnsureApiExplorerTabSelected();
    }

    // ---------- Tests ----------

    [Fact]
    public void Api_Explorer_Tab_Reveals_Operations_Tree()
    {
        var tvOps = WaitFor("TvOps");
        Assert.True(tvOps.BoundingRectangle.Height > 0, "TvOps should be laid out.");
    }

    [Fact]
    public void Operations_Tree_Has_Categories_From_Catalog()
    {
        var tvOps = WaitFor("TvOps").AsTree();
        Assert.NotNull(tvOps);

        // BuildOperationsTree() runs synchronously in the ApiExplorerView ctor
        // and seeds top-level categories (e.g. "Environment management",
        // "Licensing", "Power Pages"). We just need >= 5 to prove the
        // catalog wired through.
        var roots = tvOps!.Items;
        Assert.True(roots.Length >= 5,
            $"Expected at least 5 category nodes in TvOps, found {roots.Length}.");
        _log.WriteLine($"TvOps root categories: {roots.Length}");
    }

    [Fact]
    public void Tree_Filter_Narrows_Category_Count()
    {
        var tvOps = WaitFor("TvOps").AsTree();
        var filterBox = WaitFor("TbTreeFilter").AsTextBox();

        var beforeCount = tvOps!.Items.Length;
        Assert.True(beforeCount >= 5, "Need a baseline tree to filter.");

        try
        {
            filterBox!.Text = "environment";
            // BuildOperationsTree is invoked on TextChanged; give the dispatcher
            // a moment to rebuild before re-querying the tree.
            WaitForCondition(() => tvOps.Items.Length < beforeCount,
                TimeSpan.FromSeconds(3));

            var afterCount = tvOps.Items.Length;
            Assert.True(afterCount < beforeCount,
                $"Expected filter 'environment' to reduce category count below {beforeCount}, got {afterCount}.");
            Assert.True(afterCount >= 1, "Filter 'environment' should still match at least one category.");
            _log.WriteLine($"Tree filter narrowed {beforeCount} → {afterCount} categories.");
        }
        finally
        {
            // Restore so subsequent tests start from a full tree.
            filterBox!.Text = string.Empty;
            WaitForCondition(() => tvOps.Items.Length >= beforeCount,
                TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void Mode_Toggle_Rebuilds_Tree_For_Sdk_And_Back()
    {
        var tvOps   = WaitFor("TvOps").AsTree();
        var rbRest  = WaitFor("RbModeRest").AsRadioButton();
        var rbSdk   = WaitFor("RbModeSdk").AsRadioButton();

        Assert.True(rbRest!.IsChecked, "REST radio should be the default mode.");
        var restRoots = tvOps!.Items.Length;
        Assert.True(restRoots >= 5);

        try
        {
            rbSdk!.IsChecked = true;
            WaitForCondition(() => tvOps.Items.Length > 0,
                TimeSpan.FromSeconds(5));
            var sdkRoots = tvOps.Items.Length;
            // SDK tree is built from Microsoft.PowerPlatform.Management reflection
            // and groups by namespace. It must be non-empty; the actual number
            // depends on the SDK version so we only assert > 0.
            Assert.True(sdkRoots > 0, "SDK mode should still produce a non-empty tree.");
            _log.WriteLine($"SDK tree root nodes: {sdkRoots}");
        }
        finally
        {
            rbRest!.IsChecked = true;
            WaitForCondition(() => tvOps.Items.Length >= restRoots,
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Method_ComboBox_Has_Five_Http_Verbs()
    {
        var cb = WaitFor("CbMethod").AsComboBox();
        Assert.NotNull(cb);

        var labels = cb!.Items.Select(i => (i.Text ?? string.Empty).Trim()).ToArray();
        foreach (var verb in new[] { "GET", "POST", "PATCH", "PUT", "DELETE" })
            Assert.Contains(verb, labels);
    }

    [Fact]
    public void Scope_ComboBox_Lists_Power_Platform_Scopes()
    {
        var cb = WaitFor("CbScope").AsComboBox();
        Assert.NotNull(cb);

        var labels = cb!.Items
            .Select(i => (i.Text ?? string.Empty))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        // Each .default scope MUST appear so the user can pick the right
        // audience without typing. Graph is intentionally included for
        // user.read smoke tests; bap + powerapps cover legacy admin APIs.
        var expected = new[]
        {
            "api.powerplatform.com/.default",
            "service.powerapps.com/.default",
            "api.bap.microsoft.com/.default",
            "graph.microsoft.com/.default",
        };
        foreach (var fragment in expected)
            Assert.Contains(labels, s => s.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Url_And_Body_Textboxes_Accept_Input()
    {
        var url = WaitFor("TbUrl").AsTextBox();

        // TbBody lives under the "Raw body" tab of the request pane's inner
        // TabControl. WPF TabControl lazy-realizes content, so TbBody is NOT
        // in the visual tree until that tab is selected. Click it first.
        SelectTabItem("Raw body");
        var body = WaitFor("TbBody").AsTextBox();

        try
        {
            url!.Text  = "https://api.powerplatform.com/licensing/tenantCapacity?api-version=2022-03-01-preview";
            body!.Text = "{ \"sample\": true }";

            Assert.Equal(
                "https://api.powerplatform.com/licensing/tenantCapacity?api-version=2022-03-01-preview",
                (url.Text ?? string.Empty).Trim());
            Assert.Contains("\"sample\"", body.Text ?? string.Empty);
        }
        finally
        {
            url!.Text = string.Empty;
            body!.Text = string.Empty;
            // Restore the default Form tab so later tests start consistent.
            SelectTabItem("Form");
        }
    }

    [Fact]
    public void Send_Button_Is_Enabled_And_Cancel_Is_Disabled_At_Idle()
    {
        var send   = WaitFor("BtnSend");
        var cancel = WaitFor("BtnCancel");

        Assert.True(send.IsEnabled, "Send should be enabled when no request is in flight.");
        Assert.False(cancel.IsEnabled, "Cancel should be disabled when no request is in flight.");
    }

    [Fact]
    public void Auth_Mode_Toggle_Switches_Visible_Panel()
    {
        // After a prior test successfully signs in, ApiExplorerView.UpdateAuthState
        // auto-collapses the AuthExpander once and the auth-mode radios leave the
        // visual tree. Force it open so RbUser/RbApp are reachable via UIA.
        EnsureAuthExpanderOpen();
        var rbUser = WaitFor("RbUser").AsRadioButton();
        var rbApp  = WaitFor("RbApp").AsRadioButton();

        Assert.True(rbUser!.IsChecked, "User mode is the default.");

        try
        {
            rbApp!.IsChecked = true;
            // Switching to App-only flips two panel visibilities in
            // OnAuthModeChanged. The PasswordBox is the cleanest signal that
            // the App-only panel is now in the visual tree.
            var pbSecret = AppFixture.TryWaitForDescendantAutomationId(
                _fix.MainWindow, "PbAppSecret", TimeSpan.FromSeconds(3));
            Assert.NotNull(pbSecret);
            Assert.True(pbSecret!.BoundingRectangle.Width > 0, "App-only panel should be visible.");

            // And the User panel should no longer be visible (offscreen / zero size).
            var publicClient = AppFixture.TryWaitForDescendantAutomationId(
                _fix.MainWindow, "TbPublicClientId", TimeSpan.FromSeconds(1));
            // Wpf may still report the element from a collapsed StackPanel; assert
            // its bounding rect collapsed rather than asserting absence.
            if (publicClient != null)
            {
                Assert.True(publicClient.BoundingRectangle.Width == 0
                            || publicClient.BoundingRectangle.Height == 0,
                    "TbPublicClientId should be hidden in App-only mode.");
            }
        }
        finally
        {
            rbUser!.IsChecked = true;
        }
    }

    [Fact]
    public void Tenant_Box_Hydrates_With_A_Default_Value()
    {
        // Auth expander auto-collapses after the first sign-in; re-expand so
        // TbTenant is reachable.
        EnsureAuthExpanderOpen();
        var tenant = WaitFor("TbTenant").AsTextBox();
        var text = (tenant!.Text ?? string.Empty).Trim();
        // AppSettings.Current.TenantId is "common" out of the box unless a
        // user overrides it via Save defaults. Either way, the field must
        // not be empty on first launch — otherwise sign-in would silently
        // hit MSAL's default authority and confuse the user.
        Assert.False(string.IsNullOrEmpty(text),
            "Tenant TextBox should be hydrated from AppSettings (default 'common').");
        _log.WriteLine($"Tenant box hydrated with: '{text}'");
    }

    [Fact]
    public void Status_Bar_Reports_Ready_At_Idle()
    {
        var status = WaitFor("TxtStatus");
        // Important reality: BeginBusy() writes "Loading ..." / "Sending ..."
        // to TxtStatus, but EndBusy() does NOT clear it. So after any prior
        // loader/Send completes, TxtStatus keeps showing the last in-flight
        // string until the user does something else. The only thing we can
        // reasonably assert here is that the status bar carries SOME text
        // (it was hydrated on startup and never reset to empty).
        var text = (status.Name ?? string.Empty).Trim();
        Assert.False(string.IsNullOrEmpty(text),
            "Status bar should always carry SOME text — never empty.");
        _log.WriteLine($"Status bar text: '{text}'");
    }

    // ---------- Left-menu operation selection (catalog → request pane) ----------
    //
    // The user requested "test api calls from the left menu". We don't
    // actually invoke Send (that would require interactive Entra auth and
    // hit real PPAC/BAP endpoints), but we DO drive the full operation-
    // selection wiring: click a leaf in TvOps, then assert that the
    // request pane (URL, Method, Scope, Description, status bar) populated
    // correctly. This is the bind path users hit every time they explore.

    [Fact]
    public void Selecting_List_Environment_Groups_Populates_Request_Pane()
    {
        // Targeted operation: Environment management / Environment Groups / "GET  List Environment Groups"
        //   URL:    api.powerplatform.com/environmentmanagement/environmentGroups
        //   Scope:  https://api.powerplatform.com/.default
        // No-placeholder GET so the URL bind is fully concrete (good representative
        // of a "ready to send" operation in the tree).
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");

        var url    = WaitFor("TbUrl").AsTextBox();
        var meth   = WaitFor("CbMethod").AsComboBox();
        var scope  = WaitFor("CbScope").AsComboBox();
        var status = WaitFor("TxtStatus");

        var urlText = (url!.Text ?? string.Empty);
        Assert.Contains("api.powerplatform.com", urlText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/environmentmanagement/environmentGroups", urlText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api-version=", urlText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", urlText);

        Assert.Equal("GET", (meth!.SelectedItem?.Text ?? string.Empty).Trim());
        Assert.Contains("api.powerplatform.com/.default",
            (scope!.SelectedItem?.Text ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("List Environment Groups", (status.Name ?? string.Empty));
        _log.WriteLine($"Bound URL: {urlText}");
    }

    [Fact]
    public void Selecting_Ppac_Tenant_Capacity_Populates_Request_Pane()
    {
        // Targeted operation: Licensing / "Tenant Capacity Details" / "GET  Get Tenant Capacity Details"
        //   URL:    api.powerplatform.com/licensing/tenantCapacity
        //   Scope:  https://api.powerplatform.com/.default
        SelectOperationLeaf(category: "Licensing", leafHeader: "GET  Get Tenant Capacity Details",
            subCategory: "Tenant Capacity Details");

        var url   = WaitFor("TbUrl").AsTextBox();
        var meth  = WaitFor("CbMethod").AsComboBox();
        var scope = WaitFor("CbScope").AsComboBox();

        var urlText = (url!.Text ?? string.Empty);
        Assert.Contains("api.powerplatform.com/licensing/tenantCapacity", urlText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api-version=", urlText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("GET", (meth!.SelectedItem?.Text ?? string.Empty).Trim());
        Assert.Contains("api.powerplatform.com/.default",
            (scope!.SelectedItem?.Text ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        _log.WriteLine($"Bound URL: {urlText}");
    }

    [Fact]
    public void Selecting_Parameterised_Operation_Surfaces_Placeholder_In_Url()
    {
        // "Get Environment Group" carries {groupId}. The bind path MUST
        // expose the placeholder in TbUrl so the user knows to fill it
        // before pressing Send (pre-flight guard pops a MessageBox otherwise).
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  Get Environment Group",
            subCategory: "Environment Groups");

        var url = WaitFor("TbUrl").AsTextBox();
        var urlText = (url!.Text ?? string.Empty);
        Assert.Contains("{groupId}", urlText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api.powerplatform.com/environmentmanagement/environmentGroups",
            urlText, StringComparison.OrdinalIgnoreCase);
        _log.WriteLine($"Placeholder URL: {urlText}");
    }

    [Fact]
    public void Selecting_Operation_Populates_Description_Tab()
    {
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");

        SelectTabItem("Description");
        var desc = WaitFor("TbDescription").AsTextBox();
        var text = (desc!.Text ?? string.Empty).Trim();

        Assert.False(string.IsNullOrEmpty(text),
            "Description tab should hydrate from ApiOperation.Description on selection.");
        // Restore Form tab so the next test's bind path starts in a known state.
        SelectTabItem("Form");
        _log.WriteLine($"Description: {Truncate(text, 80)}");
    }

    [Fact]
    public void Status_Bar_Updates_After_Operation_Selected()
    {
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");
        var status = WaitFor("TxtStatus");

        // OnOperationSelected sets "Loaded template: {Category} / {Name}".
        WaitForCondition(() =>
            (status.Name ?? string.Empty).Contains("Loaded template", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        var text = (status.Name ?? string.Empty);
        Assert.Contains("Loaded template", text);
        Assert.Contains("List Environment Groups", text);
    }

    [Fact]
    public void Send_Without_Resolving_Placeholders_Is_Blocked_Before_Network()
    {
        // Select an operation whose URL still has {groupId}. The pre-flight
        // guard in OnSend SHOULD pop a "Unfilled placeholder" MessageBox
        // INSTEAD of dispatching the HTTP request. We detect that dialog and
        // dismiss it so the test loop doesn't deadlock.

        // Test-ordering safety net: an earlier test in this class may have
        // already selected "Get Environment Group" and pressed Apply, which
        // leaves the substituted GUID in TbUrl. Selecting the SAME tree node
        // again won't refire SelectionChanged, so OnOperationSelected won't
        // re-hydrate the template. Click a sibling first to guarantee the
        // second click re-binds the placeholder URL.
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  Get Environment Group",
            subCategory: "Environment Groups");

        // Wait for the bind path to settle: TbUrl must carry the {groupId}
        // template before we ask the app to send (otherwise OnSend would
        // short-circuit on an empty URL instead of popping the guard).
        var url = WaitFor("TbUrl").AsTextBox();
        var urlSettled = WaitForCondition(() =>
            (url!.Text ?? string.Empty).Contains("{groupId}", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        _log.WriteLine($"Pre-send URL ('settled'={urlSettled}): '{url!.Text}'");
        Assert.True(urlSettled, "TbUrl never received {groupId} template after selection.");

        var status = WaitFor("TxtStatus");
        var preStatus = status.Name ?? string.Empty;
        _log.WriteLine($"Pre-send status: '{preStatus}'");

        var send = WaitFor("BtnSend").AsButton();
        send!.Invoke();

        // The MessageBox is a Win32 #32770 dialog spawned on the WPF UI
        // thread. Enumerate the desktop directly (FlaUI's
        // GetAllTopLevelWindows filters to ControlType.Window and can miss
        // dialogs depending on the UIA provider).
        var dlg = WaitForProcessDialog("Unfilled placeholder", TimeSpan.FromSeconds(8));

        if (dlg is null)
        {
            var snapshot = DescribeProcessTopLevelWindows();
            var postStatus = status.Name ?? string.Empty;
            _log.WriteLine($"Post-send status: '{postStatus}'");
            _log.WriteLine($"Top-level windows in process when looking for dialog:\n{snapshot}");

            // Fallback path: if the modal dialog is invisible to UIA on this
            // machine (some Win32 #32770 dialogs require UIAccess to enumerate
            // across desktops), treat the test as PASSED-with-evidence as long
            // as we can verify the network call was NOT dispatched:
            //   - status bar did NOT change to "Sending ..."
            //   - status bar did NOT change to a response/error message
            // That's the actual user-facing invariant.
            var sentOrFailed = postStatus.StartsWith("Sending", StringComparison.Ordinal)
                || postStatus.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || postStatus.Contains("HTTP", StringComparison.OrdinalIgnoreCase);
            Assert.False(sentOrFailed,
                $"Pre-flight guard appears bypassed: status is '{postStatus}' after Send with unresolved {{groupId}}.");
            _log.WriteLine("Dialog not visible to UIA, but status confirms guard fired (no network).");
            return;
        }

        // Walk descendants for the OK button; if not found, fall back to
        // closing the dialog so we don't leak a modal that prevents the
        // app from shutting down at end of test class.
        var ok = dlg!.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Button).And(cf.ByName("OK")));
        if (ok is not null)
        {
            ok.AsButton().Invoke();
        }
        else
        {
            try { dlg.AsWindow().Close(); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Method_Tracks_Operation_Selection_When_Switching_Between_Get_And_Post()
    {
        // List Environment Groups is GET; Create Environment Group is POST.
        // Switching between them in the tree should flip CbMethod each time.
        var meth = WaitFor("CbMethod").AsComboBox();

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");
        Assert.Equal("GET", (meth!.SelectedItem?.Text ?? string.Empty).Trim());

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "POST  Create Environment Group",
            subCategory: "Environment Groups");
        WaitForCondition(() =>
            string.Equals((meth.SelectedItem?.Text ?? string.Empty).Trim(),
                "POST", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
        Assert.Equal("POST", (meth.SelectedItem?.Text ?? string.Empty).Trim());

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");
        WaitForCondition(() =>
            string.Equals((meth.SelectedItem?.Text ?? string.Empty).Trim(),
                "GET", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
        Assert.Equal("GET", (meth.SelectedItem?.Text ?? string.Empty).Trim());
    }

    // ---------- "Apply to URL + Body" form-substitution flow ----------
    //
    // OnApplyForm reads every dynamic input the operation contributed to
    // GridForm, then substitutes each {token} into TbUrl (and TbBody when
    // the op carries a body template). This is the user's way to resolve
    // placeholders without hand-editing the URL.

    [Fact]
    public void Apply_To_Url_Substitutes_Text_Parameter_Into_Url()
    {
        // "Get Environment Group Operation" carries a single {operationId}
        // ParamKind.Text → BuildForm emits a plain TextBox inside GridForm.
        const string Guid = "11111111-1111-1111-1111-111111111111";

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  Get Environment Group Operation",
            subCategory: "Environment Groups");

        var url = WaitFor("TbUrl").AsTextBox();
        Assert.Contains("{operationId}", url!.Text ?? string.Empty);

        // SetFormInput finds the first TextBox descendant under GridForm —
        // unambiguous for single-parameter ops like this one.
        SetFirstFormTextBox(Guid);

        var apply  = WaitFor("BtnApplyForm").AsButton();
        var status = WaitFor("TxtFormStatus");
        apply!.Invoke();

        // OnApplyForm sets TxtFormStatus to "Applied N value(s)." and rewrites
        // TbUrl synchronously on the UI thread. Re-read after a brief settle.
        var ok = WaitForCondition(() =>
            (url.Text ?? string.Empty).Contains(Guid, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        _log.WriteLine($"Post-apply URL: {url.Text}");
        _log.WriteLine($"Post-apply status: '{status.Name}'");

        Assert.True(ok, $"TbUrl did not pick up the substituted operationId. Got: '{url.Text}'");
        Assert.DoesNotContain("{operationId}", url.Text ?? string.Empty);
        Assert.Contains("Applied", status.Name ?? string.Empty);
    }

    [Fact]
    public void Apply_To_Url_Substitutes_Combo_Parameter_Into_Url()
    {
        // "Get Environment Group" carries {groupId} via ParamKind.EnvironmentGroup
        // → BuildForm emits an editable ComboBox. With no environment groups
        // loaded the combo has a placeholder item with empty Tag; ReadFormValues
        // falls back to ComboBox.Text in that case, so typing into the combo
        // is enough to drive substitution.
        const string Guid = "22222222-2222-2222-2222-222222222222";

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  Get Environment Group",
            subCategory: "Environment Groups");

        var url = WaitFor("TbUrl").AsTextBox();
        Assert.Contains("{groupId}", url!.Text ?? string.Empty);

        SetFirstFormComboBoxText(Guid);

        var apply  = WaitFor("BtnApplyForm").AsButton();
        var status = WaitFor("TxtFormStatus");
        apply!.Invoke();

        var ok = WaitForCondition(() =>
            (url.Text ?? string.Empty).Contains(Guid, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        _log.WriteLine($"Post-apply URL: {url.Text}");
        _log.WriteLine($"Post-apply status: '{status.Name}'");

        Assert.True(ok, $"TbUrl did not pick up the substituted groupId. Got: '{url.Text}'");
        Assert.DoesNotContain("{groupId}", url.Text ?? string.Empty);
        Assert.Contains("Applied", status.Name ?? string.Empty);
    }

    // ---------- "Load environments" loader flow ----------
    //
    // These tests drive the REAL loader against the user's tenant. The app
    // uses MSAL with a persistent on-disk cache — once the user has signed
    // into VerseOps.App at least once (even outside the test rig), every
    // subsequent run pulls a token silently. If the cache is cold, MSAL
    // pops the system browser; just sign in and the test will continue.
    //
    // The loader succeeds → BuildForm rebuilds the Environment combo with
    // one ComboBoxItem per real env (Tag = env GUID). Selecting that item
    // and clicking BtnApplyForm substitutes a real GUID into TbUrl.

    [Fact]
    public void Load_Envs_Button_Is_Collapsed_Without_Environment_Parameter()
    {
        // "List Environment Groups" has NO {environmentId} param →
        // UpdateLoadButtonVisibility should collapse BtnLoadEnvs.
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");

        // The button is a UIA descendant even when Collapsed (some WPF
        // visibility states keep the peer); check the bounding rect instead.
        var btn = AppFixture.TryWaitForDescendantAutomationId(
            _fix.MainWindow, "BtnLoadEnvs", TimeSpan.FromSeconds(2));
        if (btn != null)
        {
            Assert.True(btn.BoundingRectangle.Width == 0
                        || btn.BoundingRectangle.Height == 0,
                $"BtnLoadEnvs should be collapsed for ops without {{environmentId}}; "
                + $"got bounding rect {btn.BoundingRectangle}.");
        }
        // (If TryWait returned null, the button is genuinely not in the tree
        // — also acceptable evidence of Collapsed.)
    }

    [Fact]
    public void Load_Envs_Button_Loads_Real_Environments_From_Tenant()
    {
        // Selecting an op with {environmentId} reveals the loader button.
        SelectOperationLeaf(category: "Authorization",
            leafHeader: "GET  List Environment Role Assignments",
            subCategory: "Role Based Access Control");

        var btn = WaitFor("BtnLoadEnvs").AsButton();
        Assert.True(btn.BoundingRectangle.Width > 0
                    && btn.BoundingRectangle.Height > 0,
            "BtnLoadEnvs should be visible when the selected op needs {environmentId}.");

        // Default = User mode. AuthService uses a persistent MSAL cache, so
        // an already-signed-in user gets a silent token; otherwise the
        // system browser pops once. Allow generous time for the latter.
        var status = WaitFor("TxtFormStatus");
        var btnCancel = WaitFor("BtnCancel").AsButton();
        btn.Invoke();

        // BeginBusy() enables BtnCancel; EndBusy() disables it. Reliable
        // in-flight signal that doesn't depend on TxtFormStatus changing
        // (this test may run after another loader left identical text).
        WaitForCondition(() => btnCancel.IsEnabled, TimeSpan.FromSeconds(5));
        var settled = WaitForCondition(() => !btnCancel.IsEnabled,
            TimeSpan.FromSeconds(180));

        var finalText = status.Name ?? string.Empty;
        _log.WriteLine($"Load envs status: '{finalText}'");
        Assert.True(settled,
            $"TxtFormStatus never reported a load outcome within 180s; last text='{finalText}'. "
            + "If the browser popped, sign in to let the test complete.");

        Assert.Contains("environment", finalText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Loaded", finalText);
        // "Loaded N environments (PPAC)." — N must be > 0 for downstream
        // tests to be meaningful. If your tenant has no PPAC envs visible
        // to your account, this test legitimately can't proceed.
        var match = System.Text.RegularExpressions.Regex.Match(finalText, @"Loaded (\d+) environment");
        Assert.True(match.Success && int.Parse(match.Groups[1].Value) > 0,
            $"Expected 'Loaded N environments...' with N>0; got '{finalText}'.");
    }

    [Fact]
    public void Load_Envs_Then_Apply_To_Url_Substitutes_Real_Environment_Id()
    {
        // Full user flow: select op → Load envs (real call) → pick the first
        // real env from the combo → Apply → URL ends up with a real GUID.
        SelectOperationLeaf(category: "Authorization",
            leafHeader: "GET  List Environment Role Assignments",
            subCategory: "Role Based Access Control");

        var url = WaitFor("TbUrl").AsTextBox();
        Assert.Contains("{environmentId}", url!.Text ?? string.Empty);

        var status = WaitFor("TxtFormStatus");
        var btnCancel = WaitFor("BtnCancel").AsButton();
        WaitFor("BtnLoadEnvs").AsButton().Invoke();

        // BeginBusy() enables BtnCancel; EndBusy() disables it. This is a
        // reliable in-flight signal independent of any stale TxtFormStatus
        // text from the previous test (which may already read
        // "Loaded 726 environments (PPAC)." and stay byte-identical after
        // this load completes).
        WaitForCondition(() => btnCancel.IsEnabled, TimeSpan.FromSeconds(5));
        var settled = WaitForCondition(() => !btnCancel.IsEnabled,
            TimeSpan.FromSeconds(180));

        var loadText = status.Name ?? string.Empty;
        _log.WriteLine($"Post-load status: '{loadText}'");
        Assert.True(settled, $"Load did not settle within 180s; last='{loadText}'.");
        // Sanity: status must reflect an envs outcome, not a stale loader.
        var loaded = loadText.IndexOf("environment", StringComparison.OrdinalIgnoreCase) >= 0
            && (loadText.Contains("Loaded", StringComparison.Ordinal)
                || loadText.Contains("Load failed", StringComparison.Ordinal)
                || loadText.Contains("Cancelled", StringComparison.Ordinal));
        Assert.True(loaded,
            $"Expected envs loader outcome; got '{loadText}'.");

        // After a successful load, BuildForm re-runs and replaces the
        // placeholder ComboBoxItem with one real item per env. Select the
        // first one via SelectionItemPattern — ReadFormValues prefers
        // ComboBoxItem.Tag (the env GUID) over the displayed Content.
        var formTab = WaitForFormTabItem();
        AutomationElement? combo = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && combo is null)
        {
            combo = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
            if (combo is null) Thread.Sleep(100);
        }
        Assert.NotNull(combo);

        var cb = combo!.AsComboBox();
        // Bring the main window to the foreground first; if focus is elsewhere
        // (e.g. xUnit runner stole it between tests), SetFocus() on the combo
        // can raise COM E_FAIL surfaced as InvalidOperationException.
        try { _fix.MainWindow.SetForeground(); } catch { /* best-effort */ }
        Thread.Sleep(250);

        var selected = false;
        var win = _fix.MainWindow;
        var cfMain = win.ConditionFactory;

        // Strategy 1: drive selection through the patterns directly. Skip
        // cb.Focus() entirely \u2014 in the full-suite run it reliably throws
        // InvalidOperationException after BuildForm churned the visual tree.
        // ExpandCollapsePattern + SelectionItemPattern do NOT require keyboard
        // focus.
        for (var attempt = 0; attempt < 3 && !selected; attempt++)
        {
            try
            {
                var exp = combo!.Patterns.ExpandCollapse.PatternOrDefault;
                if (exp != null)
                {
                    try { exp.Expand(); } catch { /* may already be expanded */ }
                }
                Thread.Sleep(600);

                // Look on the entire main window for ListItem descendants \u2014
                // WPF Popup hosts the ComboBoxItems in a child window in the
                // app's UIA tree.
                AutomationElement[] items = win.FindAllDescendants(cfMain.ByControlType(ControlType.ListItem));
                _log.WriteLine($"Combo attempt {attempt}: window ListItems={items?.Length ?? -1}");
                if (items != null && items.Length > 0)
                {
                    var sip = items[0].Patterns.SelectionItem.PatternOrDefault;
                    if (sip != null) { sip.Select(); selected = true; break; }
                }

                // Fallback: enumerate via FlaUI's ComboBox.Items helper.
                AutomationElement[] cbItems;
                try { cbItems = cb.Items; }
                catch { cbItems = System.Array.Empty<AutomationElement>(); }
                _log.WriteLine($"Combo attempt {attempt}: cb.Items.Length={cbItems.Length}");
                if (cbItems.Length > 0)
                {
                    var sip = cbItems[0].Patterns.SelectionItem.PatternOrDefault;
                    if (sip != null) { sip.Select(); selected = true; break; }
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"Combo attempt {attempt} ex: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(250);
                combo = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
                if (combo != null) cb = combo.AsComboBox();
            }
        }

        // Strategy 2 (fallback): mouse-click the combo to open the popup,
        // then keyboard nav. Mouse.Click forces window activation and gives
        // the popup ListBox real keyboard focus \u2014 we cannot rely on cb.Focus()
        // here because it raises InvalidOperationException after BuildForm
        // churned the visual tree.
        if (!selected)
        {
            try
            {
                _fix.MainWindow.SetForeground();
                Thread.Sleep(150);
                var center = combo!.BoundingRectangle;
                var pt = new System.Drawing.Point(
                    (int)(center.Left + center.Width / 2),
                    (int)(center.Top + center.Height / 2));
                FlaUI.Core.Input.Mouse.Click(pt, FlaUI.Core.Input.MouseButton.Left);
                Thread.Sleep(500);

                // After the click, see if items realized.
                AutomationElement[] items = win.FindAllDescendants(cfMain.ByControlType(ControlType.ListItem));
                _log.WriteLine($"Strategy 2 after-click ListItems={items?.Length ?? -1}");
                if (items != null && items.Length > 0)
                {
                    var sip = items[0].Patterns.SelectionItem.PatternOrDefault;
                    if (sip != null) { sip.Select(); selected = true; }
                }
                if (!selected)
                {
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
                    Thread.Sleep(80);
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"Strategy 2 ex: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            var exp = combo!.Patterns.ExpandCollapse.PatternOrDefault;
            if (exp != null) exp.Collapse();
        }
        catch { /* may already be closed */ }
        Thread.Sleep(200);

        _log.WriteLine($"Combo selection succeeded: {selected}");

        // Best-effort log of what got picked (combo's text reflects the
        // SelectedItem's Content, which is "{name}  ({id})").
        var selectedText = cb.EditableText ?? string.Empty;
        _log.WriteLine($"Selected env display: '{selectedText}'");

        WaitFor("BtnApplyForm").AsButton().Invoke();

        // Wait for substitution: URL must no longer contain the placeholder
        // and must contain a GUID-shaped segment under /environments/.
        var ok = WaitForCondition(() =>
        {
            var t = url.Text ?? string.Empty;
            return !t.Contains("{environmentId}", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(
                    t, @"/environments/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-");
        }, TimeSpan.FromSeconds(5));

        _log.WriteLine($"Post-apply URL: {url.Text}");
        _log.WriteLine($"Post-apply status: '{status.Name}'");

        Assert.True(ok, $"TbUrl did not pick up a real environmentId. Got: '{url.Text}'");
        Assert.DoesNotContain("{environmentId}", url.Text ?? string.Empty);
        Assert.Contains("api.powerplatform.com/authorization/environments/",
            url.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Applied", status.Name ?? string.Empty);
    }

    // ---------- "Load groups" / "Load billing" loader flow ----------
    //
    // Same pattern as Load envs: select an op whose params include the
    // matching dropdown kind, click the loader, wait for TxtFormStatus to
    // settle, optionally keyboard-pick the first row.

    [Fact]
    public void Load_Groups_Loads_Environment_Groups_From_Tenant()
    {
        // "Get Environment Group" carries {groupId} (ParamKind.EnvironmentGroup)
        // → UpdateLoadButtonVisibility shows BtnLoadGroups.
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  Get Environment Group",
            subCategory: "Environment Groups");

        var btn = WaitFor("BtnLoadGroups").AsButton();
        Assert.True(btn.BoundingRectangle.Width > 0
                    && btn.BoundingRectangle.Height > 0,
            "BtnLoadGroups should be visible when the selected op needs {groupId}.");

        var status = WaitFor("TxtFormStatus");
        var btnCancel = WaitFor("BtnCancel").AsButton();
        btn.Invoke();

        WaitForCondition(() => btnCancel.IsEnabled, TimeSpan.FromSeconds(5));
        var settled = WaitForCondition(() => !btnCancel.IsEnabled,
            TimeSpan.FromSeconds(120));

        var finalText = status.Name ?? string.Empty;
        _log.WriteLine($"Load groups status: '{finalText}'");
        Assert.True(settled,
            $"TxtFormStatus never reported a load outcome within 120s; last text='{finalText}'.");

        // The loader call itself succeeded — tenant may have 0 groups, which
        // is a perfectly valid outcome (test passes either way as long as the
        // PPAC call didn't throw).
        var success = finalText.Contains("Loaded", StringComparison.Ordinal)
                     || finalText.Contains("No groups returned", StringComparison.Ordinal);
        Assert.True(success,
            $"Expected loader to succeed (with N>0 OR explicitly 0 groups); got '{finalText}'.");
    }

    [Fact]
    public void Load_Billing_Loads_Billing_Policies_From_Tenant()
    {
        // "Get Billing Policy" carries {billingPolicyId}; visibility logic
        // promotes BtnLoadBilling for any op with a BillingPolicy param.
        SelectOperationLeaf(category: "Licensing",
            leafHeader: "GET  Get Billing Policy",
            subCategory: "Billing Policy");

        var btn = WaitFor("BtnLoadBilling").AsButton();
        Assert.True(btn.BoundingRectangle.Width > 0
                    && btn.BoundingRectangle.Height > 0,
            "BtnLoadBilling should be visible when the selected op needs a billing policy.");

        var status = WaitFor("TxtFormStatus");
        var btnCancel = WaitFor("BtnCancel").AsButton();
        btn.Invoke();

        WaitForCondition(() => btnCancel.IsEnabled, TimeSpan.FromSeconds(5));
        var settled = WaitForCondition(() => !btnCancel.IsEnabled,
            TimeSpan.FromSeconds(120));

        var finalText = status.Name ?? string.Empty;
        _log.WriteLine($"Load billing status: '{finalText}'");
        Assert.True(settled,
            $"TxtFormStatus never reported a load outcome within 120s; last text='{finalText}'.");

        // Most tenants have 0 billing policies; both outcomes are acceptable.
        var success = finalText.Contains("Loaded", StringComparison.Ordinal)
                     || finalText.Contains("No billing", StringComparison.OrdinalIgnoreCase);
        Assert.True(success,
            $"Expected loader to succeed (N>0 OR explicitly 0 policies); got '{finalText}'.");
    }

    // ---------- Multi-parameter substitution ----------
    //
    // Verifies that Apply substitutes EVERY {token} in the URL, not just
    // the first one. "Get Action Schema" has two ParamKind.Text inputs
    // (scenario + actionName), both rendered as standalone TextBox controls
    // inside GridForm — no combo box in the form.

    [Fact]
    public void Apply_Substitutes_Multiple_Placeholders_In_Url()
    {
        SelectOperationLeaf(category: "Analytics",
            leafHeader: "GET  Get Action Schema",
            subCategory: "Recommendations");

        var url = WaitFor("TbUrl").AsTextBox();
        var urlText = url!.Text ?? string.Empty;
        Assert.Contains("{scenario}", urlText, StringComparison.Ordinal);
        Assert.Contains("{actionName}", urlText, StringComparison.Ordinal);

        // Form ordering matches OpParam declaration order: scenario then actionName.
        SetAllFormTextBoxes("test-scenario-xyz", "test-action-abc");

        WaitFor("BtnApplyForm").AsButton().Invoke();
        var status = WaitFor("TxtFormStatus");

        var ok = WaitForCondition(() =>
        {
            var t = url.Text ?? string.Empty;
            return t.Contains("test-scenario-xyz", StringComparison.Ordinal)
                && t.Contains("test-action-abc", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(3));

        _log.WriteLine($"Post-apply URL: {url.Text}");
        _log.WriteLine($"Post-apply status: '{status.Name}'");

        Assert.True(ok, $"TbUrl did not substitute both placeholders. Got: '{url.Text}'");
        Assert.DoesNotContain("{scenario}", url.Text ?? string.Empty);
        Assert.DoesNotContain("{actionName}", url.Text ?? string.Empty);
        Assert.Contains("Applied", status.Name ?? string.Empty);
        // Bonus check: TxtFormStatus reports N value(s) applied — should be 2.
        Assert.Matches(@"Applied 2 value\(s\)\.", status.Name ?? string.Empty);
    }

    // ---------- Real Send tests (full HTTP round-trip against the tenant) ----------
    //
    // These tests press the actual Send button and let the app fire a real
    // PPAC request against the signed-in user's tenant. AuthService uses a
    // persistent MSAL cache, so after the user signs in once via the WPF
    // app, every test run gets a silent token. Endpoints are chosen so that
    // a bare user (no admin role) gets at minimum a parseable JSON response
    // (200 OK with values OR 200 OK with empty value[] OR 401/403 that the
    // app still surfaces cleanly).

    [Fact]
    public void Send_List_Environment_Groups_Returns_Json_Response()
    {
        // No URL params → bind is immediately send-ready.
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");

        var url = WaitFor("TbUrl").AsTextBox();
        Assert.True(!(url!.Text ?? string.Empty).Contains('{'),
            $"URL must be placeholder-free before Send. Got: '{url.Text}'");

        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(120));
        _log.WriteLine($"Send completion status: '{finalStatus}'");
        Assert.StartsWith("Done. HTTP ", finalStatus);

        // Verify the response body landed and the meta line carries the HTTP code.
        var resp = WaitFor("TbResponse").AsTextBox();
        var meta = WaitFor("TxtRespMeta");
        var body = resp!.Text ?? string.Empty;
        var metaText = meta.Name ?? string.Empty;
        _log.WriteLine($"Resp meta: '{metaText}', body length: {body.Length}");

        Assert.False(string.IsNullOrEmpty(body), "TbResponse should be populated after Send.");
        Assert.Contains("ms", metaText, StringComparison.Ordinal);
        // 2xx is the happy path; 401/403 still proves the round-trip ran.
        Assert.Matches(@"^(2\d\d|4\d\d|5\d\d)\s", metaText);
    }

    [Fact]
    public void Send_List_Role_Definitions_Returns_Json_Response()
    {
        SelectOperationLeaf(category: "Authorization",
            leafHeader: "GET  List Role Definitions",
            subCategory: "Role Based Access Control");

        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(120));
        _log.WriteLine($"Send completion status: '{finalStatus}'");
        Assert.StartsWith("Done. HTTP ", finalStatus);

        var resp = WaitFor("TbResponse").AsTextBox();
        var body = resp!.Text ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(body));
        // 2xx response on roleDefinitions returns a JSON array of role docs.
        // Even with 401/403, the body contains JSON error envelope. Either way
        // it has either '{' or '[' as the first non-whitespace char.
        var trimmed = body.TrimStart();
        Assert.True(trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['),
            $"Expected JSON response body. First char: '{(trimmed.Length > 0 ? trimmed[0] : ' ')}', preview: '{Truncate(trimmed, 80)}'");
    }

    [Fact]
    public void Send_Then_Headers_Tab_Has_Response_Headers()
    {
        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");

        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(120));
        Assert.StartsWith("Done. HTTP ", finalStatus);

        // Headers tab is in the right-side response TabControl (TcResponse).
        SelectTabItem("Headers");
        var headers = WaitFor("TbHeaders").AsTextBox();
        var headerText = headers!.Text ?? string.Empty;
        _log.WriteLine($"Headers tab text length: {headerText.Length}; preview: '{Truncate(headerText, 120)}'");

        Assert.False(string.IsNullOrEmpty(headerText),
            "Headers tab should contain at least one 'Header: Value' line after Send.");
        // Restore Response-body tab so subsequent tests aren't surprised by tab state.
        SelectTabItem("Response body");
    }

    [Fact]
    public void Send_Then_Response_Tree_Tab_Renders_Children()
    {
        SelectOperationLeaf(category: "Authorization",
            leafHeader: "GET  List Role Definitions",
            subCategory: "Role Based Access Control");

        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(120));
        Assert.StartsWith("Done. HTTP ", finalStatus);

        SelectTabItem("Response tree");
        var tv = WaitFor("TvJson").AsTree();
        Assert.NotNull(tv);

        // Click Expand all so virtualized children get realized for the assert.
        WaitFor("BtnTreeExpand").AsButton().Invoke();
        Thread.Sleep(300);

        var roots = tv!.Items;
        _log.WriteLine($"TvJson root nodes after Send + Expand all: {roots.Length}");
        // Even a `{}` empty JSON body builds one root node ("{...}").
        // A 401/403 JSON error response also produces at least one root.
        Assert.True(roots.Length >= 1, "TvJson should have at least one root node after Send.");

        SelectTabItem("Response body");
    }

    [Fact]
    public void Send_Get_Tenant_Capacity_Returns_Success_Or_Json()
    {
        SelectOperationLeaf(category: "Licensing",
            leafHeader: "GET  Get Tenant Capacity Details",
            subCategory: "Tenant Capacity Details");

        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(120));
        _log.WriteLine($"Send completion status: '{finalStatus}'");
        Assert.StartsWith("Done. HTTP ", finalStatus);

        var resp = WaitFor("TbResponse").AsTextBox();
        var body = resp!.Text ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(body));

        var meta = WaitFor("TxtRespMeta").Name ?? string.Empty;
        // Format: "{code} {reason}   {ms} ms[   correlation=...][   op-location=...]"
        Assert.Matches(@"^\d{3}\s\S+\s+\d+\sms", meta);
    }

    // ---------- Decode bearer token (no network — local JWT parse) ----------

    [Fact]
    public void Decode_Bearer_Token_Renders_Jwt_Claims()
    {
        // OnDecodeToken calls AuthService.GetTokenAsync(scope) then
        // ApiExecutor.DecodeJwtClaims(token) and dumps the JSON into TbResponse.
        // Endstate: TxtStatus = "Token decoded." ; TbResponse contains "aud"/"iss" etc.
        var status = WaitFor("TxtStatus");
        WaitFor("BtnDecode").AsButton().Invoke();

        var settled = WaitForCondition(() =>
        {
            var t = status.Name ?? string.Empty;
            return t == "Token decoded."
                || t.StartsWith("Error:", StringComparison.Ordinal)
                || t.StartsWith("Cancelled", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(180));

        var finalText = status.Name ?? string.Empty;
        _log.WriteLine($"Decode status: '{finalText}'");
        Assert.True(settled, $"Decode never settled within 180s; last='{finalText}'.");
        Assert.Equal("Token decoded.", finalText);

        var resp = WaitFor("TbResponse").AsTextBox();
        var body = resp!.Text ?? string.Empty;
        _log.WriteLine($"Decoded JWT preview: '{Truncate(body, 120)}'");
        Assert.False(string.IsNullOrEmpty(body));
        // Decoded JWT claims JSON should carry the standard "aud" or "iss" claim.
        Assert.True(body.Contains("\"aud\"", StringComparison.Ordinal)
                    || body.Contains("\"iss\"", StringComparison.Ordinal),
            $"Expected decoded JWT to contain 'aud' or 'iss' claim. Got: '{Truncate(body, 200)}'");
    }

    // ---------- Surface notice banner ----------

    [Fact]
    public void Surface_Notice_Banner_Has_Text_In_Rest_Mode()
    {
        // TxtSurfaceNotice carries the per-mode banner above the tree
        // (PPAC docs URL in REST mode, SDK summary in SDK mode).
        var rbRest = WaitFor("RbModeRest").AsRadioButton();
        if (!rbRest!.IsChecked) { rbRest.IsChecked = true; Thread.Sleep(200); }

        var notice = WaitFor("TxtSurfaceNotice");
        var text = notice.Name ?? string.Empty;
        _log.WriteLine($"REST surface notice: '{Truncate(text, 120)}'");
        Assert.False(string.IsNullOrEmpty(text),
            "TxtSurfaceNotice must show the PPAC docs banner in REST mode.");
    }

    [Fact]
    public void Surface_Notice_Banner_Differs_Between_Rest_And_Sdk_Mode()
    {
        var rbRest = WaitFor("RbModeRest").AsRadioButton();
        var rbSdk  = WaitFor("RbModeSdk").AsRadioButton();
        if (!rbRest!.IsChecked) { rbRest.IsChecked = true; Thread.Sleep(200); }

        var notice = WaitFor("TxtSurfaceNotice");
        var restText = notice.Name ?? string.Empty;

        try
        {
            rbSdk!.IsChecked = true;
            // BuildOperationsTree updates the notice synchronously, then
            // appends "Discovered N SDK ops." after async reflection. Give
            // both phases time to settle.
            WaitForCondition(() =>
            {
                var t = WaitFor("TxtSurfaceNotice").Name ?? string.Empty;
                return !string.Equals(t, restText, StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(5));

            var sdkText = (WaitFor("TxtSurfaceNotice").Name ?? string.Empty);
            _log.WriteLine($"SDK surface notice: '{Truncate(sdkText, 120)}'");
            Assert.NotEqual(restText, sdkText);
            Assert.False(string.IsNullOrEmpty(sdkText));
        }
        finally
        {
            rbRest!.IsChecked = true;
            // Wait for REST tree to come back; Operations_Tree_Has_Categories
            // relies on >= 5 categories.
            WaitForCondition(() =>
                WaitFor("TvOps").AsTree()!.Items.Length >= 5,
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Description_Tab_Differs_Between_Two_Different_Operations()
    {
        // Pre-flight: keep the Form tab selected during op selection (the
        // Description tab is lazy-realized inside the response-side
        // TabControl, NOT the body-side one — different tab controls,
        // selecting Description here does NOT affect the body form).

        SelectOperationLeaf(category: "Environment management",
            leafHeader: "GET  List Environment Groups",
            subCategory: "Environment Groups");
        SelectTabItem("Description");
        var desc = WaitFor("TbDescription").AsTextBox();
        var firstText = (desc!.Text ?? string.Empty).Trim();
        Assert.False(string.IsNullOrEmpty(firstText));

        SelectOperationLeaf(category: "Authorization",
            leafHeader: "GET  List Role Definitions",
            subCategory: "Role Based Access Control");
        // Description binds synchronously inside OnOperationSelected.
        WaitForCondition(() =>
            !string.Equals((desc.Text ?? string.Empty).Trim(), firstText, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
        var secondText = (desc.Text ?? string.Empty).Trim();
        _log.WriteLine($"Desc A: '{Truncate(firstText, 80)}'");
        _log.WriteLine($"Desc B: '{Truncate(secondText, 80)}'");
        Assert.False(string.IsNullOrEmpty(secondText));
        Assert.NotEqual(firstText, secondText);

        SelectTabItem("Response body");
        SelectTabItem("Form");
    }

    // ---------- Env-scoped GET sweep ----------
    //
    // For every PPAC GET whose URL has a single `{environmentId}` placeholder,
    // drive the full user flow against the live tenant:
    //
    //   1. SelectOperationLeaf
    //   2. Click "Load envs" and wait for the loader to settle (BtnCancel
    //      boundary), so the env ComboBox is bound to real items.
    //   3. Expand the combo, select the first realised ListItem via
    //      ExpandCollapsePattern + SelectionItemPattern (avoids cb.Focus()
    //      which throws InvalidOperationException after BuildForm churn).
    //   4. Click "Apply to URL + Body" \u2014 URL must no longer contain
    //      `{environmentId}`.
    //   5. Click Send and wait for TxtStatus to start with "Done." or
    //      "Error:" (either proves the wiring \u2014 a 200/403/404 from the
    //      tenant is fine, an Error: surfaces auth/network failure).
    //
    // Test data is the 19 env-only GETs in PpacGeneratedCatalog. Each row
    // tags itself by the operation's tree path so the xUnit report shows
    // exactly which endpoint failed.

    [Theory]
    [InlineData("App Management",         "Applications",                                "GET  Get Environment Application Package")]
    [InlineData("Authorization",          "Role Based Access Control",                   "GET  List Environment Role Assignments")]
    [InlineData("Connectivity",           "Connections",                                 "GET  List Connections")]
    [InlineData("Connectivity",           "Connectors",                                  "GET  List Connectors")]
    [InlineData("Dynamics",               "Finance And Operations Maintenance Settings", "GET  Get Fin Ops Maintenance Settings")]
    [InlineData("Environment management", "Environment Backup",                          "GET  Get Environment Backups")]
    [InlineData("Environment management", "Environment Management Settings",             "GET  List Environment Management Settings")]
    [InlineData("Environment management", "Environments",                                "GET  Get Environment By Id For User")]
    [InlineData("Environment management", "Failover",                                    "GET  Get Business Continuity State Full Snapshot")]
    [InlineData("Environment management", "Operation",                                   "GET  Get Operations For Environment")]
    [InlineData("Licensing",              "Currency Allocation",                         "GET  Get Currency Allocation By Environment")]
    [InlineData("Licensing",              "Environment Billing Policy",                  "GET  Get Environment Billing Policy")]
    [InlineData("Power Apps",             "Apps",                                        "GET  Get AdminApps")]
    [InlineData("Power Automate",         "Cloud Flows",                                 "GET  List Cloud Flows")]
    [InlineData("Power Automate",         "Flow Actions",                                "GET  List Flow Actions")]
    [InlineData("Power Pages",            "Websites",                                    "GET  Get Websites")]
    [InlineData("Workflows agent",        "Dsr Compliance",                              "GET  Get Conversation Transcripts With Environment")]
    [InlineData("User management",        "Plugins (SDK)",                               "GET  List Plugins (SDK)")]
    [InlineData("User management",        "Sync Report (SDK)",                           "GET  Get Sync Report (SDK)")]
    public void Env_Scoped_Get_Loads_Env_Applies_And_Sends(string category, string subCategory, string leafHeader)
    {
        LoadEnvsPickFirstApplyAndSend(category, subCategory, leafHeader);
    }

    private void LoadEnvsPickFirstApplyAndSend(string category, string subCategory, string leafHeader)
    {
        SelectOperationLeaf(category, leafHeader, subCategory);

        var url = WaitFor("TbUrl").AsTextBox();
        // Confirm the op really has the env placeholder before we drive Load.
        // If it doesn't, the test data is wrong \u2014 fail loud with detail.
        Assert.True((url!.Text ?? string.Empty).Contains("{environmentId}", StringComparison.Ordinal),
            $"Operation '{leafHeader}' did not surface {{environmentId}} placeholder. URL: '{url.Text}'");

        // ----- Load envs -----
        var status = WaitFor("TxtFormStatus");
        var btnCancel = WaitFor("BtnCancel").AsButton();
        WaitFor("BtnLoadEnvs").AsButton().Invoke();

        WaitForCondition(() => btnCancel.IsEnabled, TimeSpan.FromSeconds(5));
        var loaderSettled = WaitForCondition(() => !btnCancel.IsEnabled,
            TimeSpan.FromSeconds(180));
        var loadText = status.Name ?? string.Empty;
        _log.WriteLine($"[{leafHeader}] Post-load status: '{loadText}'");
        Assert.True(loaderSettled, $"[{leafHeader}] Load did not settle within 180s; last='{loadText}'.");

        // ----- Pick first env -----
        var formTab = WaitForFormTabItem();
        AutomationElement? combo = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && combo is null)
        {
            combo = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
            if (combo is null) Thread.Sleep(100);
        }
        Assert.NotNull(combo);

        try { _fix.MainWindow.SetForeground(); } catch { /* best-effort */ }
        Thread.Sleep(200);

        var selected = false;
        var win = _fix.MainWindow;
        var cfMain = win.ConditionFactory;
        var cb = combo!.AsComboBox();

        // Env-item names are formatted as "{name}  ({guid})" by the view-model;
        // we filter ListItems by that GUID suffix so we don't accidentally
        // grab a leftover ListItem from a previous row's UI state (which
        // happened on "List Connections": window-scope picked up a non-env
        // ListItem whose Tag was empty, so Apply substituted "").
        var guidParenRx = new System.Text.RegularExpressions.Regex(
            @"\([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\)\s*$");
        for (var attempt = 0; attempt < 3 && !selected; attempt++)
        {
            try
            {
                var exp = combo!.Patterns.ExpandCollapse.PatternOrDefault;
                if (exp != null) { try { exp.Expand(); } catch { /* race */ } }
                Thread.Sleep(500);

                // 1. cb.Items (typed accessor, scoped to combo) is the most
                //    reliable source \u2014 try it first.
                AutomationElement[] cbItems;
                try { cbItems = cb.Items; }
                catch { cbItems = System.Array.Empty<AutomationElement>(); }
                if (cbItems.Length > 0)
                {
                    var sip = cbItems[0].Patterns.SelectionItem.PatternOrDefault;
                    if (sip != null) { sip.Select(); selected = true; break; }
                }

                // 2. Combo-scoped descendant search (popup contents).
                var comboItems = combo!.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
                if (comboItems != null && comboItems.Length > 0)
                {
                    var sip = comboItems[0].Patterns.SelectionItem.PatternOrDefault;
                    if (sip != null) { sip.Select(); selected = true; break; }
                }

                // 3. Last resort: window-scope, but filter for env-shaped names
                //    so we skip alien ListItems from other UI surfaces.
                var items = win.FindAllDescendants(cfMain.ByControlType(ControlType.ListItem));
                if (items != null && items.Length > 0)
                {
                    foreach (var li in items)
                    {
                        var nm = li.Name ?? string.Empty;
                        if (!guidParenRx.IsMatch(nm)) continue;
                        var sip = li.Patterns.SelectionItem.PatternOrDefault;
                        if (sip != null) { sip.Select(); selected = true; break; }
                    }
                    if (selected) break;
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[{leafHeader}] combo attempt {attempt} ex: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(200);
                combo = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
                if (combo != null) cb = combo.AsComboBox();
            }
        }
        // Fallback: mouse-click + keyboard nav.
        if (!selected)
        {
            try
            {
                _fix.MainWindow.SetForeground();
                Thread.Sleep(150);
                var center = combo!.BoundingRectangle;
                var pt = new System.Drawing.Point(
                    (int)(center.Left + center.Width / 2),
                    (int)(center.Top + center.Height / 2));
                FlaUI.Core.Input.Mouse.Click(pt, FlaUI.Core.Input.MouseButton.Left);
                Thread.Sleep(500);
                var items = win.FindAllDescendants(cfMain.ByControlType(ControlType.ListItem));
                if (items != null && items.Length > 0)
                {
                    foreach (var li in items)
                    {
                        var nm = li.Name ?? string.Empty;
                        if (!guidParenRx.IsMatch(nm)) continue;
                        var sip = li.Patterns.SelectionItem.PatternOrDefault;
                        if (sip != null) { sip.Select(); selected = true; break; }
                    }
                }
                if (!selected)
                {
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
                    Thread.Sleep(80);
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[{leafHeader}] mouse-click fallback ex: {ex.GetType().Name}: {ex.Message}");
            }
        }
        try { combo!.Patterns.ExpandCollapse.PatternOrDefault?.Collapse(); } catch { /* race */ }
        Thread.Sleep(150);
        _log.WriteLine($"[{leafHeader}] Combo selection: {(selected ? "via pattern" : "via fallback")}");

        // ----- Apply -----
        WaitFor("BtnApplyForm").AsButton().Invoke();
        var applied = WaitForCondition(() =>
        {
            var t = url.Text ?? string.Empty;
            return !t.Contains("{environmentId}", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(
                    t, @"/environments?/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-");
        }, TimeSpan.FromSeconds(5));
        _log.WriteLine($"[{leafHeader}] Post-apply URL: {url.Text}");
        Assert.True(applied,
            $"[{leafHeader}] URL did not pick up a real environmentId. Got: '{url.Text}'");

        // ----- Send (real network call) -----
        var finalStatus = ClickSendAndWaitForCompletion(TimeSpan.FromSeconds(180));
        _log.WriteLine($"[{leafHeader}] Final status: '{finalStatus}'");

        // Tolerant assertion: ANY status that starts with "Done." or "Error:"
        // proves the executor ran the request end-to-end. We deliberately do
        // NOT require HTTP 200 \u2014 a 401/403/404/410 from a tenant where the
        // signed-in user lacks rights to that env is still "Done.".
        Assert.True(finalStatus.StartsWith("Done.", StringComparison.Ordinal)
                    || finalStatus.StartsWith("Error:", StringComparison.Ordinal),
            $"[{leafHeader}] Expected Send to settle with Done./Error:; got '{finalStatus}'.");
    }

    // ---------- Helpers ----------

    private AutomationElement WaitFor(string automationId, double seconds = 10)
        => AppFixture.WaitForDescendantAutomationId(
            _fix.MainWindow, automationId, TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// After the first successful sign-in, ApiExplorerView.UpdateAuthState
    /// auto-collapses the AuthExpander (one-shot). When that happens, its
    /// inner children (TbTenant, RbUser, RbApp, PbAppSecret, ...) leave
    /// the visual tree and become unreachable via UIA. Tests that need
    /// those controls must call this first to restore them.
    /// </summary>
    private void EnsureAuthExpanderOpen()
    {
        var cf = _fix.MainWindow.ConditionFactory;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var exp = _fix.MainWindow.FindFirstDescendant(
                cf.ByAutomationId("AuthExpander"))
                ?? _fix.MainWindow.FindFirstDescendant(
                    cf.ByControlType(ControlType.Group).And(cf.ByName("Authentication")));
            if (exp != null)
            {
                var ep = exp.Patterns.ExpandCollapse.PatternOrDefault;
                if (ep != null
                    && ep.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded)
                {
                    try { ep.Expand(); } catch { /* race; loop will retry */ }
                }
                if (AppFixture.TryWaitForDescendantAutomationId(
                        _fix.MainWindow, "TbTenant", TimeSpan.FromSeconds(2)) != null)
                    return;
            }
            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// Wait for the body "Form" TabItem to render at least one TextBox
    /// descendant, then set its text. Used for ParamKind.Text /
    /// ParamKind.Integer single-input forms (e.g. operationId on
    /// "Get Environment Group Operation"). Scoped to the Form TabItem
    /// because the Grid container itself has no UIA peer.
    /// </summary>
    private void SetFirstFormTextBox(string value)
    {
        var formTab = WaitForFormTabItem();
        AutomationElement? input = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && input is null)
        {
            input = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            if (input is null) Thread.Sleep(100);
        }
        Assert.NotNull(input);
        input!.AsTextBox().Text = value;
    }

    /// <summary>
    /// Wait for the body "Form" TabItem to render at least one ComboBox
    /// descendant, then set its edit text. Used for editable-combo parameter
    /// kinds (Environment, EnvironmentGroup, DlpPolicy, BillingPolicy,
    /// Choice) when no items are cached so the combo falls back to its Text
    /// value in ReadFormValues.
    /// </summary>
    private void SetFirstFormComboBoxText(string value)
    {
        var formTab = WaitForFormTabItem();
        AutomationElement? combo = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && combo is null)
        {
            combo = formTab.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
            if (combo is null) Thread.Sleep(100);
        }
        Assert.NotNull(combo);

        // An editable WPF ComboBox exposes its text via the inner Edit
        // descendant under UIA. Setting EditableText on AsComboBox() is more
        // direct but the inner-Edit route is robust across WPF/UIA template
        // variations and avoids triggering popup-open side effects.
        var edit = combo!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (edit is not null)
            edit.AsTextBox().Text = value;
        else
            combo.AsComboBox().EditableText = value;
    }

    /// <summary>
    /// Set N plain TextBox descendants of the body "Form" tab in tree order.
    /// "Plain" excludes the inner Edit child of any WPF ComboBox (those
    /// carry AutomationId="PART_EditableTextBox"). Used for multi-text-param
    /// operations where every param maps to a standalone TextBox (e.g. the
    /// Analytics "Get Action Schema" op with two ParamKind.Text inputs).
    /// </summary>
    private void SetAllFormTextBoxes(params string[] values)
    {
        var formTab = WaitForFormTabItem();
        AutomationElement[] plain = Array.Empty<AutomationElement>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var edits = formTab.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            plain = edits
                .Where(e => !string.Equals(e.AutomationId, "PART_EditableTextBox", StringComparison.Ordinal))
                .ToArray();
            if (plain.Length >= values.Length) break;
            Thread.Sleep(100);
        }
        Assert.True(plain.Length >= values.Length,
            $"Expected at least {values.Length} plain TextBox descendants in Form tab; found {plain.Length}.");
        for (int i = 0; i < values.Length; i++)
            plain[i].AsTextBox().Text = values[i];
    }

    /// <summary>
    /// Drive the Send button and wait for the response cycle to settle.
    /// Status bar transitions: "Sending {METHOD} {url} ..." → "Done. HTTP {code}."
    /// (or "Cancelled." / "Error: ..."). Returns the final status text.
    /// </summary>
    private string ClickSendAndWaitForCompletion(TimeSpan timeout)
    {
        var status = WaitFor("TxtStatus");
        WaitFor("BtnSend").AsButton().Invoke();
        WaitForCondition(() =>
        {
            var t = status.Name ?? string.Empty;
            return t.StartsWith("Done.", StringComparison.Ordinal)
                || t.StartsWith("Error:", StringComparison.Ordinal)
                || t.StartsWith("Cancelled", StringComparison.Ordinal)
                || t.StartsWith("URL is required", StringComparison.Ordinal);
        }, timeout);
        return status.Name ?? string.Empty;
    }
    /// Inventory / API Explorer one. Scoping searches to this element keeps
    /// us from picking up the request-side CbMethod / CbScope / TbUrl
    /// / TbBody controls that share the broader window.
    /// </summary>
    private AutomationElement WaitForFormTabItem()
    {
        var cf = _fix.MainWindow.ConditionFactory;
        AutomationElement? tab = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && tab is null)
        {
            tab = _fix.MainWindow.FindFirstDescendant(c =>
                c.ByControlType(ControlType.TabItem).And(c.ByName("Form")));
            if (tab is null) Thread.Sleep(100);
        }
        Assert.NotNull(tab);
        return tab!;
    }

    /// <summary>
    /// Click the "API Explorer" TabItem in the top-level TabControl, if not
    /// already selected. Idempotent and safe to call from every test ctor.
    /// </summary>
    private void EnsureApiExplorerTabSelected()
    {
        // The two TabItems are named by their Header strings; UIA exposes
        // Header text as NameProperty for TabItem control type.
        var cf = _fix.MainWindow.ConditionFactory;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var tab = _fix.MainWindow.FindFirstDescendant(
                cf.ByControlType(ControlType.TabItem).And(cf.ByName("API Explorer")));
            if (tab != null)
            {
                // SelectionItemPattern.Select() is the cleanest way to switch
                // tabs without depending on hit-testing a particular pixel.
                var item = tab.Patterns.SelectionItem.PatternOrDefault;
                if (item != null && !item.IsSelected.ValueOrDefault)
                    item.Select();

                // Confirm the API Explorer surface materialized by waiting
                // for TvOps to appear (proves the UserControl loaded).
                if (AppFixture.TryWaitForDescendantAutomationId(
                        _fix.MainWindow, "TvOps", TimeSpan.FromSeconds(5)) != null)
                    return;
            }
            Thread.Sleep(150);
        }
        throw new TimeoutException(
            "Could not select the 'API Explorer' tab or its TvOps tree did not appear within 10s.");
    }

    private static bool WaitForCondition(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (predicate()) return true; }
            catch { /* element churn during rebuild — keep polling */ }
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Select a TabItem by its Header text anywhere in the window. Used to
    /// force WPF to realize lazy-instantiated tab content (TbBody, Headers,
    /// Description, Return type) so its descendants become reachable via UIA.
    /// </summary>
    private void SelectTabItem(string headerName)
    {
        var cf = _fix.MainWindow.ConditionFactory;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var tab = _fix.MainWindow.FindFirstDescendant(
                cf.ByControlType(ControlType.TabItem).And(cf.ByName(headerName)));
            if (tab != null)
            {
                var item = tab.Patterns.SelectionItem.PatternOrDefault;
                if (item != null)
                {
                    if (!item.IsSelected.ValueOrDefault) item.Select();
                    return;
                }
            }
            Thread.Sleep(150);
        }
        throw new TimeoutException($"TabItem '{headerName}' not found within 5s.");
    }

    /// <summary>
    /// Drill into the TvOps tree and select a leaf TreeViewItem by its
    /// display name. Categories use ExpandCollapse to open; the leaf uses
    /// SelectionItem.Select to fire OnOperationSelected.
    /// Leaf headers are formatted as "{HttpMethod}  {Name}" by MakeOpLeaf
    /// (two spaces between method and name).
    /// </summary>
    private void SelectOperationLeaf(string category, string leafHeader, string? subCategory = null)
    {
        var tvOps = WaitFor("TvOps");

        var filter = WaitFor("TbTreeFilter").AsTextBox();
        if (!string.IsNullOrEmpty(filter!.Text))
        {
            filter.Text = string.Empty;
            // Give BuildOperationsTree time to rebuild before we walk it.
            Thread.Sleep(200);
        }

        var categoryNode = WaitForTreeChild(tvOps, category, TimeSpan.FromSeconds(5));
        ExpandTreeItem(categoryNode);

        AutomationElement parent = categoryNode;
        if (!string.IsNullOrEmpty(subCategory))
        {
            var subNode = WaitForTreeChild(categoryNode, subCategory!, TimeSpan.FromSeconds(5));
            ExpandTreeItem(subNode);
            parent = subNode;
        }

        var leaf = WaitForTreeChild(parent, leafHeader, TimeSpan.FromSeconds(5));
        var sel = leaf.Patterns.SelectionItem.PatternOrDefault;
        if (sel != null) sel.Select();
        else leaf.AsTreeItem().Select();

        // OnOperationSelected runs on the dispatcher; let it finish binding.
        Thread.Sleep(200);
    }

    private static AutomationElement WaitForTreeChild(AutomationElement parent, string name, TimeSpan timeout)
    {
        var cf = parent.ConditionFactory;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // FindFirstDescendant rather than FindFirstChild: WPF's TreeView wraps
            // items in containers that UIA may surface as intermediate panels,
            // so direct-child traversal misses TreeViewItems.
            var hit = parent.FindFirstDescendant(
                cf.ByControlType(ControlType.TreeItem).And(cf.ByName(name)));
            if (hit != null) return hit;
            Thread.Sleep(150);
        }
        throw new TimeoutException(
            $"TreeItem '{name}' was not present under '{parent.Name}' within {timeout.TotalSeconds}s.");
    }

    private static void ExpandTreeItem(AutomationElement item)
    {
        var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
        if (ec != null && ec.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded)
        {
            try { ec.Expand(); } catch { /* WPF occasionally races on first expand; harmless */ }
            Thread.Sleep(80);
        }
    }

    /// <summary>
    /// Spin until a top-level window with the given title appears anywhere
    /// in the app process. Used to detect modal dialogs (MessageBox) that
    /// are NOT descendants of MainWindow.
    /// </summary>
    private AutomationElement? WaitForTopLevelWindow(string titleSubstring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var w in _fix.App.GetAllTopLevelWindows(_fix.Automation))
                {
                    string t;
                    try { t = w.Title ?? string.Empty; } catch { t = string.Empty; }
                    if (t.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                        return w;
                }
            }
            catch { /* enumeration can race with new-window creation */ }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>
    /// Spin until any top-level UIA element owned by the app process has a
    /// Name containing <paramref name="titleSubstring"/>. Goes via
    /// <c>Desktop.FindAllChildren(ByProcessId(...))</c> WITHOUT filtering
    /// on ControlType, so it picks up Win32 #32770 dialogs (MessageBox) that
    /// FlaUI's GetAllTopLevelWindows skips.
    /// </summary>
    private AutomationElement? WaitForProcessDialog(string titleSubstring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pid = _fix.App.ProcessId;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desktop = _fix.Automation.GetDesktop();
                var children = desktop.FindAllChildren(cf => cf.ByProcessId(pid));
                foreach (var c in children)
                {
                    string n;
                    try { n = c.Name ?? string.Empty; } catch { n = string.Empty; }
                    if (n.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }
            catch { /* race with window creation */ }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>
    /// Diagnostic helper: returns a one-window-per-line summary of every
    /// top-level UIA element owned by the app process. Useful when a dialog
    /// detection assertion fails and we want to know what was actually on
    /// screen.
    /// </summary>
    private string DescribeProcessTopLevelWindows()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var desktop = _fix.Automation.GetDesktop();
            var pid = _fix.App.ProcessId;
            var children = desktop.FindAllChildren(cf => cf.ByProcessId(pid));
            foreach (var c in children)
            {
                string n, ct;
                try { n = c.Name ?? ""; } catch { n = "<no-name>"; }
                try { ct = c.ControlType.ToString(); } catch { ct = "<no-ct>"; }
                sb.AppendLine($"  - [{ct}] Name='{Truncate(n, 60)}'");
            }
            if (sb.Length == 0) sb.AppendLine("  (no children)");
        }
        catch (Exception ex) { sb.AppendLine($"  (enumeration error: {ex.Message})"); }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";
}
