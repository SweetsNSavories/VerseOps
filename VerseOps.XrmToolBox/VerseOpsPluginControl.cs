using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Identity.Client;
using VerseOps.Api.Core;
using VerseOps.XrmToolBox.Auth;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Root plugin control hosted inside XrmToolBox. PR #3 wires MSAL sign-in
    /// (browser + device-code) on top of a shared MSAL cache with the WPF app.
    /// PR #4 wires the operation catalog tree, parameter form, and Execute.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="INoConnectionRequired"/>: the plugin talks to
    /// tenant-level PPAC/BAP REST APIs via MSAL with the signed-in user, NOT
    /// via the host's IOrganizationService. Marking the control this way
    /// suppresses XrmToolBox's "pick a Dataverse connection" prompt on first
    /// open — opening the tile lands the user straight on the operation tree.
    /// </remarks>
    public partial class VerseOpsPluginControl : PluginControlBase, INoConnectionRequired
    {
        // One PluginAuthService per control instance. The MSAL cache helper
        // underneath is a process-wide singleton, so multiple plugin instances
        // (XrmToolBox supports more than one) still share the same cache file.
        private readonly PluginAuthService _auth = new PluginAuthService();
        private readonly ApiExecutor _executor;

        // Per-selected-op state. _currentOp is the row from the catalog the
        // user clicked; _paramInputs maps OpParam.Token -> the editor control
        // we rendered for it (TextBox, ComboBox, NumericUpDown).
        private ApiOperation? _currentOp;
        private readonly Dictionary<string, Control> _paramInputs = new Dictionary<string, Control>(StringComparer.Ordinal);
        private bool _isSignedIn;
        private CancellationTokenSource? _executeCts;

        // Last successful response — cached so the JSON tree + Headers tab can
        // be populated lazily when the user actually switches to them. Builds
        // can be expensive for large bodies (e.g. 700+ envs) so we skip the
        // work on the Send path and only pay it on demand.
        private string? _lastResponseBody;
        private IReadOnlyDictionary<string, string>? _lastResponseHeaders;
        private bool _treePopulatedForCurrentBody;
        private bool _headersPopulatedForCurrentBody;
        // Captured from the last 202 response's operation-location header, plus
        // the scope used to make the call. Enables the "Poll op" button to
        // re-GET the long-running operation without retyping the URL.
        private string? _lastOperationLocation;
        private string? _lastResponseScope;

        // Per-kind dynamic dropdown caches. null = never loaded; an empty list
        // means the load completed but returned no rows (still better than null
        // so we don't show the "click Load" hint after a real call). Shared
        // across the lifetime of the control so switching operations doesn't
        // re-fetch the same list. Same shape as ApiExplorerView in VerseOps.App.
        private List<(string Id, string DisplayName)>? _envCache;
        private List<(string Id, string DisplayName)>? _groupCache;
        private List<(string Id, string DisplayName)>? _dlpCache;
        private List<(string Id, string DisplayName)>? _billingCache;
        private CancellationTokenSource? _loaderCts;

        // One ToolTip provider for every parameter-form row. ToolTip is IDisposable;
        // creating one per row leaks a native handle on each operation switch.
        private readonly ToolTip _paramTooltip = new ToolTip
        {
            AutoPopDelay = 30000,
            InitialDelay = 250,
            ReshowDelay = 100,
            ShowAlways = true
        };

        public VerseOpsPluginControl()
        {
            InitializeComponent();
            // Centralised Fluent / XrmToolBox-friendly styling pass. Walks
            // every child control once and normalises fonts, button sizes,
            // combo chrome, and tab strip height. See FluentStyles.cs.
            FluentStyles.Apply(this);
            _executor = new ApiExecutor(_auth);
            PopulateOpsTree(filter: null);
            Load += async (_, __) => await ProbeSilentAsync().ConfigureAwait(true);
            // Designer-time SplitterDistance values get clamped to the design
            // surface size (1000 px), so a larger value silently shrinks once
            // the control is docked into the XrmToolBox host. Reapply on load
            // and on resize so the left tree column is wide enough to show
            // full API names and the request/response panes are 50/50.
            Load += (_, __) => ApplySplitterDefaults();
            SizeChanged += (_, __) => ApplySplitterDefaults();
        }

        // Track whether the user has manually dragged either splitter; once
        // they have, stop force-resizing so we don't fight them.
        private bool _outerSplitUserMoved;
        private bool _rightSplitUserMoved;
        private bool _applyingSplitterDefaults;

        private void ApplySplitterDefaults()
        {
            _applyingSplitterDefaults = true;
            try
            {
                if (_outerSplit != null && _outerSplit.IsHandleCreated && !_outerSplitUserMoved)
                {
                    int target = Math.Min(Math.Max(360, (int)(ClientSize.Width * 0.28)), 520);
                    if (_outerSplit.Width > _outerSplit.Panel1MinSize + _outerSplit.Panel2MinSize + _outerSplit.SplitterWidth + 16)
                    {
                        _outerSplit.SplitterDistance = Math.Min(target, _outerSplit.Width - _outerSplit.Panel2MinSize - _outerSplit.SplitterWidth - 1);
                    }
                    // Hook once: mark as user-moved when the splitter is
                    // actually dragged so future resizes don't snap back.
                    _outerSplit.SplitterMoved -= OuterSplit_OnSplitterMoved;
                    _outerSplit.SplitterMoved += OuterSplit_OnSplitterMoved;
                }
                if (_rightSplit != null && _rightSplit.IsHandleCreated && !_rightSplitUserMoved)
                {
                    // 50/50 vertical split between request and response.
                    if (_rightSplit.Height > 80)
                    {
                        _rightSplit.SplitterDistance = Math.Max(120, _rightSplit.Height / 2);
                    }
                    _rightSplit.SplitterMoved -= RightSplit_OnSplitterMoved;
                    _rightSplit.SplitterMoved += RightSplit_OnSplitterMoved;
                }
            }
            catch
            {
                // SplitContainer throws InvalidOperationException if the
                // requested distance violates Panel1MinSize/Panel2MinSize
                // during very small transient sizes. Ignore — next resize
                // event will reapply with a valid value.
            }
            finally
            {
                _applyingSplitterDefaults = false;
            }
        }

        private void OuterSplit_OnSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_applyingSplitterDefaults) return;
            _outerSplitUserMoved = true;
        }
        private void RightSplit_OnSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_applyingSplitterDefaults) return;
            _rightSplitUserMoved = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _paramTooltip.Dispose();
                // XrmToolBox can call Dispose(true) more than once on a tab close
                // (PluginControlBase + the WinForms tab-page parent both fire it),
                // and either CTS may already be disposed if a prior load/execute
                // path tore it down. Guard both Cancel() and Dispose() and null
                // out the fields so a second pass is a no-op rather than a crash
                // dialog that masks any real shutdown error.
                DisposeCts(ref _loaderCts);
                DisposeCts(ref _executeCts);
            }
            base.Dispose(disposing);
        }

        private static void DisposeCts(ref CancellationTokenSource? cts)
        {
            var local = cts;
            cts = null;
            if (local == null) return;
            try { local.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
            try { local.Dispose(); } catch (ObjectDisposedException) { }
        }

        // Best-effort silent token probe on plugin load. If the user signed
        // into the WPF app earlier, the shared cache lights up and they see
        // "Signed in as ..." without any prompt. Failures are non-fatal —
        // they just mean the Sign-in buttons stay enabled.
        private async Task ProbeSilentAsync()
        {
            try
            {
                SetStatus("Checking for cached sign-in\u2026", busy: true);
                var token = await _auth.TryGetTokenSilentAsync(
                    PluginAuthService.ScopePpac, CancellationToken.None).ConfigureAwait(true);
                if (token != null && _auth.LastSignedInUser != null)
                {
                    SetSignedIn(_auth.LastSignedInUser, "silent (shared cache)");
                }
                else
                {
                    SetSignedOut("No cached sign-in. Click Sign in to authenticate.");
                }
            }
            catch (OperationCanceledException)
            {
                SetSignedOut("Silent check cancelled.");
            }
            catch (MsalException ex)
            {
                SetSignedOut("Silent check failed: " + ex.Message);
            }
        }

        private async void BtnSignIn_Click(object sender, EventArgs e)
        {
            try
            {
                SetStatus("Opening system browser\u2026", busy: true);
                var token = await _auth.SignInInteractiveAsync(
                    PluginAuthService.ScopePpac, CancellationToken.None).ConfigureAwait(true);
                if (token != null && _auth.LastSignedInUser != null)
                {
                    SetSignedIn(_auth.LastSignedInUser, "browser");
                }
                else
                {
                    SetSignedOut("Sign-in returned without a token.");
                }
            }
            catch (OperationCanceledException)
            {
                SetSignedOut("Sign-in cancelled.");
            }
            catch (MsalException ex)
            {
                SetSignedOut("Sign-in failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "VerseOps sign-in",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnSignInDeviceCode_Click(object sender, EventArgs e)
        {
            using (var dlg = new DeviceCodeDialog())
            using (var cts = new CancellationTokenSource())
            {
                dlg.Cancelled += (_, __) => cts.Cancel();

                SetStatus("Requesting device code\u2026", busy: true);
                var signInTask = _auth.SignInDeviceCodeAsync(
                    PluginAuthService.ScopePpac,
                    msg =>
                    {
                        // Marshal MSAL's callback back onto the UI thread to
                        // update the dialog text safely.
                        if (dlg.IsHandleCreated)
                        {
                            dlg.BeginInvoke((MethodInvoker)(() => dlg.ShowCode(msg.UserCode, msg.VerificationUrl, msg.Message)));
                        }
                        return Task.CompletedTask;
                    },
                    cts.Token);

                // Show the dialog modally; MSAL keeps polling AAD until the
                // user finishes the flow or the dialog is cancelled.
                dlg.ShowDialog(this);

                try
                {
                    var token = await signInTask.ConfigureAwait(true);
                    if (token != null && _auth.LastSignedInUser != null)
                    {
                        SetSignedIn(_auth.LastSignedInUser, "device code");
                    }
                    else
                    {
                        SetSignedOut("Sign-in returned without a token.");
                    }
                }
                catch (OperationCanceledException)
                {
                    SetSignedOut("Device-code sign-in cancelled.");
                }
                catch (MsalException ex)
                {
                    SetSignedOut("Device-code sign-in failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "VerseOps sign-in",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async void BtnSignOut_Click(object sender, EventArgs e)
        {
            var ok = MessageBox.Show(this,
                "Sign out of VerseOps?\r\n\r\n" +
                "This wipes the shared MSAL cache and also signs out the VerseOps WPF app.",
                "VerseOps sign-out",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;

            try
            {
                await _auth.SignOutAsync().ConfigureAwait(true);
                SetSignedOut("Signed out.");
            }
            catch (MsalException ex)
            {
                SetSignedOut("Sign-out failed: " + ex.Message);
            }
            catch (IOException ex)
            {
                SetSignedOut("Sign-out failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                SetSignedOut("Sign-out failed: " + ex.Message);
            }
        }

        private void SetStatus(string text, bool busy)
        {
            _statusLabel.Text = text;
            _btnSignIn.Enabled = !busy;
            _btnSignInDeviceCode.Enabled = !busy;
            _btnSignOut.Enabled = false;
            SetBusy(busy, text);
            UpdateExecuteEnabled();
        }

        private void SetSignedIn(string user, string method)
        {
            _statusLabel.Text = "Signed in as " + user + " (" + method + ")";
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = true;
            _isSignedIn = true;
            SetBusy(false, "Signed in.");
            UpdateExecuteEnabled();
        }

        private void SetSignedOut(string note)
        {
            _statusLabel.Text = note;
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = false;
            _isSignedIn = false;
            SetBusy(false, note);
            UpdateExecuteEnabled();
        }

        // Status-bar toggle. Indeterminate marquee + status text update; elapsed
        // is cleared on entry and re-populated by RenderResult on completion.
        private void SetBusy(bool busy, string statusText)
        {
            _statusBarLabel.Text = statusText;
            _statusBarProgress.Visible = busy;
            if (busy) _statusBarElapsed.Text = string.Empty;
        }

        private void UpdateExecuteEnabled()
        {
            var canSend = _isSignedIn && _currentOp != null && _executeCts == null;
            _btnExecute.Enabled = canSend;
            _btnCancel.Enabled  = _executeCts != null;
            // Decode bearer needs a token (i.e. signed in) but doesn't need a selected op.
            _btnDecode.Enabled  = _isSignedIn && _executeCts == null;
            // Apply just needs an operation; works offline too.
            _btnApply.Enabled   = _currentOp != null && _executeCts == null;
            if (!_isSignedIn)
                _statusBarLabel.Text = "Sign in to enable Send.";
            else if (_currentOp == null)
                _statusBarLabel.Text = "Pick an operation on the left.";
        }

        // ============================================================
        // Catalog tree (left pane)
        // ============================================================

        private static readonly Regex s_tokenRegex = new Regex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

        // Build the operations tree. Grouping: Surface -> Category -> [SubCategory] -> Operation.
        // Filter is a case-insensitive substring match against category/subcategory/name/url.
        private void PopulateOpsTree(string? filter)
        {
            _opsTree.BeginUpdate();
            try
            {
                _opsTree.Nodes.Clear();

                // BAP operations live under ApiCatalog.Operations, PPAC under PpacOperations.
                // Same shape — we just root them under different top-level nodes so the user
                // can tell at a glance which token audience the call needs.
                AppendSurface("BAP (api.bap.microsoft.com)", ApiCatalog.Operations, filter);
                AppendSurface("PPAC (api.powerplatform.com)", ApiCatalog.PpacOperations, filter);

                // If filtering, expand everything so matches are visible without manual drill-down.
                if (!string.IsNullOrEmpty(filter))
                {
                    _opsTree.ExpandAll();
                }
            }
            finally
            {
                _opsTree.EndUpdate();
            }
        }

        private void AppendSurface(string label, IReadOnlyList<ApiOperation> ops, string? filter)
        {
            var matches = string.IsNullOrEmpty(filter)
                ? ops
                : ops.Where(o => OpMatchesFilter(o, filter!)).ToList();
            if (matches.Count == 0) return;

            var surfaceNode = new TreeNode(label + "  (" + matches.Count + ")");
            foreach (var byCategory in matches.GroupBy(o => string.IsNullOrEmpty(o.Category) ? "(uncategorised)" : o.Category)
                                              .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var catNode = new TreeNode(byCategory.Key);
                var hasSubs = byCategory.Any(o => !string.IsNullOrEmpty(o.SubCategory));
                if (hasSubs)
                {
                    foreach (var bySub in byCategory.GroupBy(o => string.IsNullOrEmpty(o.SubCategory) ? "(general)" : o.SubCategory!)
                                                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var subNode = new TreeNode(bySub.Key);
                        foreach (var op in bySub.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            subNode.Nodes.Add(MakeOpNode(op));
                        }
                        catNode.Nodes.Add(subNode);
                    }
                }
                else
                {
                    foreach (var op in byCategory.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        catNode.Nodes.Add(MakeOpNode(op));
                    }
                }
                surfaceNode.Nodes.Add(catNode);
            }
            _opsTree.Nodes.Add(surfaceNode);
        }

        private static TreeNode MakeOpNode(ApiOperation op)
        {
            // Show HTTP verb up front so GET vs POST vs DELETE is obvious in the tree.
            return new TreeNode(op.HttpMethod + "  " + op.Name) { Tag = op };
        }

        private static bool OpMatchesFilter(ApiOperation op, string filter)
        {
            return Contains(op.Name, filter)
                || Contains(op.Category, filter)
                || Contains(op.SubCategory, filter)
                || Contains(op.UrlTemplate, filter)
                || Contains(op.Description, filter);
        }

        private static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack)
            && haystack!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void SearchBox_TextChanged(object sender, EventArgs e)
        {
            var filter = _searchBox.Text?.Trim();
            PopulateOpsTree(string.IsNullOrEmpty(filter) ? null : filter);
        }

        // ============================================================
        // Parameter form (right pane, top)
        // ============================================================

        private void OpsTree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node?.Tag is ApiOperation op)
            {
                LoadOperation(op);
            }
            else
            {
                _currentOp = null;
                _opMetaLabel.Text = "Select an operation from the tree on the left.";
                _paramTable.Controls.Clear();
                _paramTable.RowStyles.Clear();
                _paramTable.RowCount = 0;
                _paramInputs.Clear();
                _bodyEditor.Text = string.Empty;
                _urlBox.Text = string.Empty;
                _descriptionBox.Text = string.Empty;
                UpdateExecuteEnabled();
            }
        }

        private void LoadOperation(ApiOperation op)
        {
            _currentOp = op;
            _opMetaLabel.Text =
                op.HttpMethod + "  " + op.UrlTemplate + "\r\n" +
                "scope: " + op.TokenScope + "\r\n" +
                (string.IsNullOrEmpty(op.Description) ? string.Empty : op.Description);

            BuildParamInputs(op);
            UpdateLoadButtonVisibility(op);
            _bodyEditor.Text = op.RequestBodyTemplate ?? string.Empty;

            // Drive the editable controls from the catalog so the user can
            // tweak method / URL / scope without losing the catalog defaults.
            SelectComboValue(_methodCombo, op.HttpMethod);
            _urlBox.Text = op.UrlTemplate;
            SelectComboValue(_scopeCombo, op.TokenScope);

            _descriptionBox.Text = string.IsNullOrEmpty(op.Description)
                ? "(no description in catalog)"
                : op.HttpMethod + "  " + op.UrlTemplate + "\r\n" +
                  "scope: " + op.TokenScope + "\r\n\r\n" +
                  op.Description;

            UpdateExecuteEnabled();
        }

        private static void SelectComboValue(ComboBox combo, string value)
        {
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                var idx = combo.FindStringExact(value);
                if (idx >= 0) combo.SelectedIndex = idx;
            }
            else
            {
                var idx = combo.FindStringExact(value);
                if (idx >= 0) combo.SelectedIndex = idx;
                else combo.Text = value ?? string.Empty;
            }
        }

        private void BuildParamInputs(ApiOperation op)
        {
            _paramTable.SuspendLayout();
            _paramTable.Controls.Clear();
            _paramTable.RowStyles.Clear();
            _paramTable.RowCount = 0;
            _paramInputs.Clear();
            _paramTooltip.RemoveAll();

            var parameters = op.Parameters ?? Array.Empty<OpParam>();
            foreach (var p in parameters)
            {
                var label = new Label
                {
                    Text = p.Label + (p.Required ? " *" : ""),
                    AutoSize = false,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 4, 8, 4),
                    Height = 26
                };
                if (!string.IsNullOrEmpty(p.Help))
                {
                    _paramTooltip.SetToolTip(label, p.Help);
                }

                Control input = BuildInput(p);
                input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                input.Margin = new Padding(0, 2, 0, 2);
                _paramInputs[p.Token] = input;

                // Picker kinds keep their cache between op-switches; surface
                // that on the input itself so the user can see at a glance
                // that the dropdown is already populated.
                var hint = GetCacheHint(p.Kind);
                if (!string.IsNullOrEmpty(hint))
                {
                    _paramTooltip.SetToolTip(input, hint);
                }

                _paramTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _paramTable.RowCount++;
                _paramTable.Controls.Add(label, 0, _paramTable.RowCount - 1);
                _paramTable.Controls.Add(input, 1, _paramTable.RowCount - 1);
            }

            _paramTable.ResumeLayout();

            // Bring the just-built dynamic controls in line with the rest of
            // the chrome (URL box, method combo, scope combo, search box).
            // FluentStyles.Apply runs once in the constructor — before any
            // param row exists — so these post-construction controls would
            // otherwise keep their WinForms defaults and look 1-2pt smaller
            // than every other ComboBox/TextBox on the surface.
            FluentStyles.Apply(_paramTable);
        }

        // Returns a human cache hint for picker-kind params, or null for
        // kinds that don't use one. Mirrors the loader-button text so the
        // user gets the same signal in two places.
        private string? GetCacheHint(ParamKind kind)
        {
            List<(string Id, string DisplayName)>? cache;
            string loadHint;
            switch (kind)
            {
                case ParamKind.Environment:      cache = _envCache;     loadHint = "Load environments"; break;
                case ParamKind.EnvironmentGroup: cache = _groupCache;   loadHint = "Load groups";       break;
                case ParamKind.DlpPolicy:        cache = _dlpCache;     loadHint = "Load DLP";          break;
                case ParamKind.BillingPolicy:    cache = _billingCache; loadHint = "Load billing";      break;
                default: return null;
            }
            if (cache == null)    return "Click '" + loadHint + "' once to populate this picker (cached for the session).";
            if (cache.Count == 0) return "No items returned from last '" + loadHint + "' \u2014 click to retry.";
            return "Cached " + cache.Count + " items \u2014 click the dropdown or type to filter. ('" + loadHint + "' to refresh.)";
        }

        private Control BuildInput(OpParam p)
        {
            switch (p.Kind)
            {
                case ParamKind.Choice:
                    var combo = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDown,
                        Font = new Font("Segoe UI", 9F),
                    };
                    if (p.Choices != null)
                    {
                        foreach (var c in p.Choices) combo.Items.Add(c);
                    }
                    combo.Text = p.Default ?? string.Empty;
                    return combo;

                case ParamKind.Integer:
                    var num = new NumericUpDown
                    {
                        Minimum = int.MinValue,
                        Maximum = int.MaxValue,
                        Font = new Font("Segoe UI", 9F),
                    };
                    if (int.TryParse(p.Default, out var n)) num.Value = n;
                    return num;

                case ParamKind.MultilineText:
                    return new TextBox
                    {
                        Multiline = true,
                        ScrollBars = ScrollBars.Vertical,
                        Height = 80,
                        Font = new Font("Consolas", 9F),
                        Text = p.Default ?? string.Empty
                    };

                case ParamKind.Environment:
                    return MakePickerCombo(_envCache, "Load environments", p.Default);
                case ParamKind.EnvironmentGroup:
                    return MakePickerCombo(_groupCache, "Load groups", p.Default);
                case ParamKind.DlpPolicy:
                    return MakePickerCombo(_dlpCache, "Load DLP", p.Default);
                case ParamKind.BillingPolicy:
                    return MakePickerCombo(_billingCache, "Load billing", p.Default);
                case ParamKind.Template:
                    // No catalog-wide loader yet (templates are per-location);
                    // accept free text so the user can paste a name.
                    return new TextBox
                    {
                        Font = new Font("Segoe UI", 9F),
                        Text = p.Default ?? string.Empty
                    };

                default:
                    return new TextBox
                    {
                        Font = new Font("Segoe UI", 9F),
                        Text = p.Default ?? string.Empty
                    };
            }
        }

        private string ReadInputValue(Control control)
        {
            return control switch
            {
                NumericUpDown n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // Picker combos store a PickerItem in SelectedItem whose Id
                // (Tag-equivalent) is the value we want to substitute. If the
                // user typed text without clicking a row, try to match it
                // against the items so we still send the right GUID.
                ComboBox c when c.SelectedItem is PickerItem pi && !string.IsNullOrEmpty(pi.Id) => pi.Id,
                ComboBox c when ResolvePickerByText(c) is PickerItem mp => mp.Id,
                ComboBox c     => (c.SelectedItem?.ToString() ?? c.Text) ?? string.Empty,
                TextBox t      => t.Text ?? string.Empty,
                _              => control.Text ?? string.Empty
            };
        }

        // Try to resolve typed text to a PickerItem by full ToString, by
        // DisplayName, or by raw Id. Returns null when no items are
        // PickerItems (e.g. plain string ComboBoxes for HTTP methods).
        private static PickerItem? ResolvePickerByText(ComboBox cb)
        {
            var text = (cb.Text ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            foreach (var o in cb.Items)
            {
                if (o is not PickerItem pi) continue;
                if (string.Equals(pi.ToString(), text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pi.DisplayName, text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pi.Id, text, StringComparison.OrdinalIgnoreCase))
                {
                    return pi;
                }
            }
            return null;
        }

        // ============================================================
        // Execute (right pane, bottom)
        // ============================================================

        private async void BtnExecute_Click(object sender, EventArgs e)
        {
            if (_currentOp == null) return;
            var op = _currentOp;

            // Substitute {token} placeholders from the param form into both URL and body.
            // Any required parameter left blank is reported back to the user before we
            // attempt the call; missing tokens just stay literal in the request.
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (var p in op.Parameters ?? Array.Empty<OpParam>())
            {
                var v = _paramInputs.TryGetValue(p.Token, out var ctrl)
                    ? ReadInputValue(ctrl).Trim()
                    : string.Empty;
                values[p.Token] = v;
                if (p.Required && string.IsNullOrEmpty(v)) missing.Add(p.Label);
            }
            if (missing.Count > 0)
            {
                MessageBox.Show(this,
                    "Fill in the required parameters: " + string.Join(", ", missing),
                    "VerseOps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Read method/URL/scope from the editable controls so the user can
            // override the catalog defaults inline. Body still substitutes tokens.
            var method = (_methodCombo.SelectedItem?.ToString() ?? _methodCombo.Text ?? op.HttpMethod).Trim();
            var urlTemplate = string.IsNullOrWhiteSpace(_urlBox.Text) ? op.UrlTemplate : _urlBox.Text;
            var scope = (_scopeCombo.SelectedItem?.ToString() ?? _scopeCombo.Text ?? op.TokenScope).Trim();
            var url = SubstituteTokens(urlTemplate, values);
            var body = string.IsNullOrEmpty(_bodyEditor.Text) ? null : SubstituteTokens(_bodyEditor.Text, values);

            using var cts = new CancellationTokenSource();
            _executeCts = cts;
            UpdateExecuteEnabled();
            _btnExecute.Text = "Running\u2026";
            _responseHeader.Text = "Response \u2014 sending\u2026";
            ClearResponseSurface();
            SetBusy(true, method + " " + url);

            try
            {
                var result = await _executor.ExecuteAsync(method, url, body, scope, cts.Token)
                                            .ConfigureAwait(true);
                RenderResult(method, url, scope, result);
            }
            catch (OperationCanceledException)
            {
                _responseHeader.Text = "Response \u2014 cancelled.";
                SetBusy(false, "Cancelled.");
            }
            catch (MsalException ex)
            {
                _responseHeader.Text = "Response \u2014 sign-in error.";
                _responseBox.Text = "MSAL error: " + ex.Message;
                SetBusy(false, "Sign-in error.");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _responseHeader.Text = "Response \u2014 network error.";
                _responseBox.Text = "HTTP error: " + ex.Message;
                SetBusy(false, "Network error.");
            }
            finally
            {
                _executeCts = null;
                _btnExecute.Text = "Send";
                UpdateExecuteEnabled();
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            try { _executeCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Apply the current form values into the URL and body editors so the user
        // can review the substituted request before sending. Mirrors the WPF API
        // Explorer's "Apply to URL + Body" button. Send still re-substitutes as a
        // safety net.
        private void BtnApply_Click(object sender, EventArgs e)
        {
            if (_currentOp == null) return;
            var op = _currentOp;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in op.Parameters ?? Array.Empty<OpParam>())
            {
                var v = _paramInputs.TryGetValue(p.Token, out var ctrl)
                    ? ReadInputValue(ctrl).Trim()
                    : string.Empty;
                values[p.Token] = v;
            }

            var urlTemplate = string.IsNullOrWhiteSpace(_urlBox.Text)
                ? (op.UrlTemplate ?? string.Empty)
                : _urlBox.Text;
            _urlBox.Text = SubstituteTokens(urlTemplate, values);

            if (!string.IsNullOrEmpty(_bodyEditor.Text))
                _bodyEditor.Text = SubstituteTokens(_bodyEditor.Text, values);
            else if (!string.IsNullOrEmpty(op.RequestBodyTemplate))
                _bodyEditor.Text = SubstituteTokens(op.RequestBodyTemplate!, values);

            var applied = 0;
            foreach (var kv in values) if (!string.IsNullOrEmpty(kv.Value)) applied++;
            _formLoaderStatus.Text = $"Applied {applied} value(s) to URL and body.";
        }

        // Decode the current bearer (for the selected scope, or PPAC default)
        // into header+payload+expiry. Pure local op — no HTTP. Routed through
        // ApiExecutor's special URL so we reuse its decode + pretty-print logic.
        private async void BtnDecode_Click(object sender, EventArgs e)
        {
            var scope = (_scopeCombo.SelectedItem?.ToString() ?? _scopeCombo.Text ?? PluginAuthService.ScopePpac).Trim();
            if (string.IsNullOrEmpty(scope)) scope = PluginAuthService.ScopePpac;

            using var cts = new CancellationTokenSource();
            _executeCts = cts;
            UpdateExecuteEnabled();
            _responseHeader.Text = "Response \u2014 decoding bearer\u2026";
            ClearResponseSurface();
            SetBusy(true, "Decoding bearer\u2026");
            try
            {
                var result = await _executor.ExecuteAsync("GET", "local://decode-token", null, scope, cts.Token)
                                            .ConfigureAwait(true);
                _responseHeader.Text = "Response  \u2014  decoded JWT for scope: " + scope;
                _responseBox.Text = "// scope: " + scope + "\r\n// (local decode \u2014 no network call)\r\n\r\n" + result.ResponseBody;
                CacheResponse(result.ResponseBody, result.ResponseHeaders, scope, operationLocation: null);
                EnsureActiveTabPopulated();
                SetBusy(false, "Decoded.");
                _responseTabs.SelectedTab = _respTabBody;
            }
            catch (OperationCanceledException)
            {
                _responseHeader.Text = "Response \u2014 decode cancelled.";
                SetBusy(false, "Cancelled.");
            }
            catch (MsalException ex)
            {
                _responseHeader.Text = "Response \u2014 sign-in error.";
                _responseBox.Text = "MSAL error: " + ex.Message;
                SetBusy(false, "Sign-in error.");
            }
            finally
            {
                _executeCts = null;
                UpdateExecuteEnabled();
            }
        }

        // Re-GET the URL captured from the last response's operation-location
        // header. Mirrors the WPF API Explorer's "Poll op" button. The Async
        // Power Platform APIs typically return 202 with this header for long
        // running operations (env create, link, etc.) and the client is
        // expected to poll until 200/201/204. We render the response into the
        // same surface so the user can hit Poll op repeatedly to watch state
        // transition (Running -> Succeeded).
        private async void BtnPollOp_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastOperationLocation)) return;
            var url = _lastOperationLocation!;
            var scope = string.IsNullOrEmpty(_lastResponseScope) ? PluginAuthService.ScopePpac : _lastResponseScope!;

            using var cts = new CancellationTokenSource();
            _executeCts = cts;
            UpdateExecuteEnabled();
            _responseHeader.Text = "Response \u2014 polling\u2026";
            ClearResponseSurface();
            SetBusy(true, "GET " + url);

            try
            {
                var result = await _executor.ExecuteAsync("GET", url, null, scope, cts.Token)
                                            .ConfigureAwait(true);
                RenderResult("GET", url, scope, result);
            }
            catch (OperationCanceledException)
            {
                _responseHeader.Text = "Response \u2014 poll cancelled.";
                SetBusy(false, "Cancelled.");
            }
            catch (MsalException ex)
            {
                _responseHeader.Text = "Response \u2014 sign-in error.";
                _responseBox.Text = "MSAL error: " + ex.Message;
                SetBusy(false, "Sign-in error.");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _responseHeader.Text = "Response \u2014 network error.";
                _responseBox.Text = "HTTP error: " + ex.Message;
                SetBusy(false, "Network error.");
            }
            finally
            {
                _executeCts = null;
                UpdateExecuteEnabled();
            }
        }

        // Poll op is meaningful only when the previous response captured an
        // operation-location header. Cleared by ClearResponseSurface so a fresh
        // Send hides the button until the new response either provides one or
        // doesn't.
        private void UpdatePollOpVisibility()
        {
            _btnPollOp.Visible = !string.IsNullOrEmpty(_lastOperationLocation);
        }

        private static string SubstituteTokens(string template, IReadOnlyDictionary<string, string> values)
        {
            return s_tokenRegex.Replace(template, m =>
            {
                var name = m.Groups["name"].Value;
                return values.TryGetValue(name, out var v) ? v : m.Value;
            });
        }

        private void RenderResult(string method, string url, string scope, ApiCallResult result)
        {
            _responseHeader.Text =
                "Response  \u2014  " + result.StatusCode + " " + result.ReasonPhrase +
                "  \u2022  " + result.ElapsedMs + " ms" +
                (string.IsNullOrEmpty(result.CorrelationId) ? string.Empty : "  \u2022  x-ms-correlation-request-id: " + result.CorrelationId);

            // Show the resolved URL in a header comment so the user can copy
            // the exact call back into curl/Postman without re-substituting tokens.
            var sb = new StringBuilder();
            sb.Append("// ").Append(method).Append("  ").AppendLine(url);
            sb.Append("// scope: ").AppendLine(scope);
            if (!string.IsNullOrEmpty(result.OperationLocation))
            {
                sb.Append("// operation-location: ").AppendLine(result.OperationLocation);
            }
            sb.AppendLine();
            sb.Append(result.ResponseBody);
            _responseBox.Text = sb.ToString();

            CacheResponse(result.ResponseBody, result.ResponseHeaders, scope, result.OperationLocation);
            EnsureActiveTabPopulated();

            _statusBarElapsed.Text = result.ElapsedMs + " ms  \u2022  " + result.StatusCode + " " + result.ReasonPhrase;
            SetBusy(false, "Ready.");
        }

        // Wipe response surfaces fast: clear the body/header textboxes and the
        // JSON tree, and reset the lazy-populate cache so the next switch
        // repopulates from scratch. BeginUpdate avoids per-node redraws when
        // the previous response was large.
        private void ClearResponseSurface()
        {
            _responseBox.Text = string.Empty;
            _headersBox.Text = string.Empty;
            _jsonTree.BeginUpdate();
            try { _jsonTree.Nodes.Clear(); } finally { _jsonTree.EndUpdate(); }
            _lastResponseBody = null;
            _lastResponseHeaders = null;
            _lastResponseScope = null;
            _lastOperationLocation = null;
            _treePopulatedForCurrentBody = false;
            _headersPopulatedForCurrentBody = false;
            UpdatePollOpVisibility();
        }

        private void CacheResponse(string body, IReadOnlyDictionary<string, string> headers, string scope, string? operationLocation)
        {
            _lastResponseBody = body;
            _lastResponseHeaders = headers;
            _lastResponseScope = scope;
            _lastOperationLocation = operationLocation;
            _treePopulatedForCurrentBody = false;
            _headersPopulatedForCurrentBody = false;
            UpdatePollOpVisibility();
        }

        // Populate whichever response tab is active right now (no-op for Body
        // since it was already filled). Heavy work (parsing JSON, building
        // thousands of TreeNodes) is deferred until the user actually opens
        // the tab, which is the main win for large responses.
        private void EnsureActiveTabPopulated()
        {
            var tab = _responseTabs.SelectedTab;
            if (tab == _respTabTree && !_treePopulatedForCurrentBody)
            {
                PopulateJsonTree(_jsonTree, _lastResponseBody ?? string.Empty);
                _treePopulatedForCurrentBody = true;
            }
            else if (tab == _respTabHeaders && !_headersPopulatedForCurrentBody)
            {
                PopulateHeaders(_lastResponseHeaders ?? new Dictionary<string, string>(0));
                _headersPopulatedForCurrentBody = true;
            }
        }

        // ============================================================
        // Headers tab
        // ============================================================

        private void PopulateHeaders(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                _headersBox.Text = "(no response headers)";
                return;
            }
            var sb = new StringBuilder();
            foreach (var kv in headers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(kv.Key).Append(": ").AppendLine(kv.Value);
            }
            _headersBox.Text = sb.ToString();
        }

        // ============================================================
        // JSON tree tab
        // ============================================================

        // Parse `body` as JSON and project it into the TreeView. Failures are
        // non-fatal — we just show a single "(not JSON)" placeholder so the
        // user knows the body wasn't structured.
        private static void PopulateJsonTree(TreeView tree, string body)
        {
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                if (string.IsNullOrWhiteSpace(body))
                {
                    tree.Nodes.Add(new TreeNode("(empty body)"));
                    return;
                }
                // The body often has a `// VerseOps diagnosis: ...` preamble from
                // ApiExecutor; strip leading comment lines before parsing.
                var trimmed = StripLeadingComments(body).TrimStart();
                if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                {
                    tree.Nodes.Add(new TreeNode("(not JSON)"));
                    return;
                }
                using var doc = JsonDocument.Parse(trimmed);
                var root = BuildJsonNode("root", doc.RootElement);
                tree.Nodes.Add(root);
                root.Expand();
            }
            catch (JsonException ex)
            {
                tree.Nodes.Add(new TreeNode("(JSON parse error: " + ex.Message + ")"));
            }
            finally
            {
                tree.EndUpdate();
            }
        }

        private static string StripLeadingComments(string s)
        {
            using var reader = new StringReader(s);
            var sb = new StringBuilder();
            string? line;
            var inLeading = true;
            while ((line = reader.ReadLine()) != null)
            {
                if (inLeading && (line.StartsWith("//") || string.IsNullOrWhiteSpace(line))) continue;
                inLeading = false;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        private static TreeNode BuildJsonNode(string name, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var n = new TreeNode(name + " : {} (" + CountObjectProps(element) + ")");
                    foreach (var prop in element.EnumerateObject())
                        n.Nodes.Add(BuildJsonNode(prop.Name, prop.Value));
                    return n;
                }
                case JsonValueKind.Array:
                {
                    var len = element.GetArrayLength();
                    var n = new TreeNode(name + " : [] (" + len + ")");
                    var i = 0;
                    foreach (var item in element.EnumerateArray())
                        n.Nodes.Add(BuildJsonNode("[" + (i++) + "]", item));
                    return n;
                }
                case JsonValueKind.String:
                    return new TreeNode(name + " : \"" + Truncate(element.GetString() ?? "") + "\"");
                case JsonValueKind.Number:
                    return new TreeNode(name + " : " + element.GetRawText());
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return new TreeNode(name + " : " + element.GetRawText());
                case JsonValueKind.Null:
                    return new TreeNode(name + " : null");
                default:
                    return new TreeNode(name + " : " + element.GetRawText());
            }
        }

        private static int CountObjectProps(JsonElement obj)
        {
            var n = 0;
            foreach (var _ in obj.EnumerateObject()) n++;
            return n;
        }

        private static string Truncate(string s, int max = 200)
        {
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\u2026";
        }

        // ============================================================
        // Response search (find-next in body / live filter in tree)
        // ============================================================

        private int _lastSearchAnchor = -1;

        private void RespSearchBox_TextChanged(object sender, EventArgs e)
        {
            // Search runs on Enter only. Edits just reset the find-next anchor
            // and the previous result label so the UI doesn't lie about staleness.
            _lastSearchAnchor = -1;
            _respSearchInfo.Text = string.Empty;
            // If the user cleared the box on the tree tab, restore the full tree
            // (otherwise the previous filter would stay visible until next Enter).
            if (string.IsNullOrEmpty(_respSearchBox.Text) && _responseTabs.SelectedTab == _respTabTree)
            {
                PopulateJsonTree(_jsonTree, _responseBox.Text);
            }
        }

        private void RespSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Trace.WriteLine($"[VerseOps] RespSearch: Enter pressed, term='{_respSearchBox.Text}'");
                BtnRespSearchNext_Click(sender, e);
            }
        }

        private void BtnRespSearchNext_Click(object sender, EventArgs e)
        {
            var term = _respSearchBox.Text;
            var tabName = _responseTabs.SelectedTab?.Text ?? "(none)";
            Trace.WriteLine($"[VerseOps] RespSearch: run term='{term}' tab='{tabName}' anchor={_lastSearchAnchor}");
            if (string.IsNullOrEmpty(term)) return;
            if (_responseTabs.SelectedTab == _respTabTree)
            {
                ApplyTreeFilter(term);
                return;
            }
            // Determine target body (Body tab or Headers tab).
            var target = _responseTabs.SelectedTab == _respTabHeaders ? _headersBox : _responseBox;
            if (string.IsNullOrEmpty(target.Text))
            {
                _respSearchInfo.Text = "(empty)";
                return;
            }
            var start = _lastSearchAnchor < 0 ? 0 : _lastSearchAnchor;
            var idx = target.Text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 && start > 0)
            {
                // Wrap around to top.
                idx = target.Text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);
                _respSearchInfo.Text = idx >= 0 ? "(wrapped)" : "no matches";
            }
            else
            {
                _respSearchInfo.Text = idx >= 0 ? "match" : "no matches";
            }
            if (idx >= 0)
            {
                target.Select(idx, term.Length);
                target.ScrollToCaret();
                _lastSearchAnchor = idx + term.Length;
                target.Focus();
            }
            else
            {
                _lastSearchAnchor = -1;
            }
        }

        private void BtnRespSearchClear_Click(object sender, EventArgs e)
        {
            _respSearchBox.Text = string.Empty;
            _respSearchInfo.Text = string.Empty;
            _lastSearchAnchor = -1;
            // Repopulate the tree from the current body to undo any filtering.
            if (_responseTabs.SelectedTab == _respTabTree)
            {
                PopulateJsonTree(_jsonTree, _responseBox.Text);
            }
        }

        // Tree filter: rebuild the tree from the current body, then prune branches
        // whose subtree contains no node whose text matches the term.
        private void ApplyTreeFilter(string term)
        {
            // Always rebuild from source so successive edits don't compound.
            PopulateJsonTree(_jsonTree, _responseBox.Text);
            if (string.IsNullOrEmpty(term))
            {
                _respSearchInfo.Text = string.Empty;
                return;
            }
            _jsonTree.BeginUpdate();
            try
            {
                var kept = 0;
                for (var i = _jsonTree.Nodes.Count - 1; i >= 0; i--)
                {
                    var n = _jsonTree.Nodes[i];
                    if (!PruneTree(n, term)) _jsonTree.Nodes.RemoveAt(i);
                    else { kept++; ExpandAll(n); }
                }
                _respSearchInfo.Text = kept > 0 ? "filtered" : "no matches";
            }
            finally
            {
                _jsonTree.EndUpdate();
            }
        }

        // Recursive prune: keep node if its own text matches OR any descendant matches.
        private static bool PruneTree(TreeNode node, string term)
        {
            var selfMatch = node.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            var anyChild = false;
            for (var i = node.Nodes.Count - 1; i >= 0; i--)
            {
                if (PruneTree(node.Nodes[i], term)) anyChild = true;
                else node.Nodes.RemoveAt(i);
            }
            return selfMatch || anyChild;
        }

        private static void ExpandAll(TreeNode node)
        {
            node.Expand();
            foreach (TreeNode c in node.Nodes) ExpandAll(c);
        }

        // When the active response tab changes, the search-info label can
        // become misleading (e.g. "(wrapped)" on a different surface). Clear
        // it, and lazily populate the freshly-activated tab from the cached
        // body if we deferred it during Render.
        private void ResponseTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            _respSearchInfo.Text = string.Empty;
            _lastSearchAnchor = -1;
            EnsureActiveTabPopulated();
        }

        // ============================================================
        // Dynamic dropdowns (Environment / Group / DLP / Billing)
        // ============================================================

        // Tiny carrier so the ComboBox displays "name (id)" but ReadInputValue
        // can recover the raw id for token substitution. ToString() drives both
        // the dropdown label and what shows in the edit area after selection.
        private sealed class PickerItem
        {
            public string Id { get; }
            public string DisplayName { get; }
            public PickerItem(string id, string displayName) { Id = id; DisplayName = displayName ?? id; }
            public override string ToString() => string.IsNullOrEmpty(Id) ? DisplayName : DisplayName + "  (" + Id + ")";
        }

        // Build an editable ComboBox seeded from a cached list. Uses native
        // WinForms SuggestAppend autocomplete over the Items collection: the
        // user types the start of the display name, the dropdown narrows,
        // arrow-keys / Enter / mouse-click pick a row. No manual TextUpdate
        // filter — that path turned out to be impossible to make non-finicky.
        private ComboBox MakePickerCombo(List<(string Id, string DisplayName)>? cache, string loadHint, string? def)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Font = new Font("Segoe UI", 9F),
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                IntegralHeight = false,
                MaxDropDownItems = 20,
                DropDownHeight = 320,
                Sorted = false
            };
            if (cache != null)
            {
                foreach (var (id, name) in cache) cb.Items.Add(new PickerItem(id, name));
            }
            else
            {
                cb.Items.Add(new PickerItem(string.Empty, "(click '" + loadHint + "' to populate)"));
            }
            cb.Text = def ?? string.Empty;

            // When the dropdown opens, leave the typed query intact; when the
            // user clicks back in after a selection, select-all so the next
            // keystroke replaces the previous label instead of appending.
            cb.Enter += (_, __) =>
            {
                if (!string.IsNullOrEmpty(cb.Text)) cb.SelectAll();
            };
            return cb;
        }

        // Turn an editable ComboBox into a substring-filtered picker: as the
        // user types, the dropdown narrows to items whose ToString() contains
        // the entered query (case-insensitive). WinForms equivalent of the WPF
        // ICollectionView filter in VerseOps.App.Explorer.ApiExplorerView.
        // NOTE: Replaced by the native SuggestAppend autocomplete in
        // MakePickerCombo — keystroke-driven Items rewriting could not be made
        // reliable (caret jitter, sticky selected item). Left as a no-op so we
        // don't break any future callers that haven't been updated.
        private static void EnableSubstringFilter(ComboBox cb)
        {
            cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cb.AutoCompleteSource = AutoCompleteSource.ListItems;
        }

        // Show only the loader buttons that match parameters declared by the
        // current op. Mirrors UpdateLoadButtonVisibility in the WPF explorer.
        private void UpdateLoadButtonVisibility(ApiOperation? op)
        {
            bool needEnv = false, needGroup = false, needDlp = false, needBilling = false;
            var ps = op?.Parameters;
            if (ps != null)
            {
                foreach (var p in ps)
                {
                    switch (p.Kind)
                    {
                        case ParamKind.Environment: needEnv = true; break;
                        case ParamKind.EnvironmentGroup: needGroup = true; break;
                        case ParamKind.DlpPolicy: needDlp = true; break;
                        case ParamKind.BillingPolicy: needBilling = true; break;
                    }
                }
            }
            _btnLoadEnvs.Visible    = needEnv;
            _btnLoadGroups.Visible  = needGroup;
            _btnLoadDlp.Visible     = needDlp;
            _btnLoadBilling.Visible = needBilling;
            // Apply is always available when an op is selected, even if no
            // loader buttons are needed, so the strip stays visible.
            _formLoadersStrip.Visible = op != null;
        }

        // ---------- Loader button handlers ----------

        private async void BtnLoadEnvs_Click(object sender, EventArgs e)
        {
            await LoadDropdownAsync(
                label: "environments",
                url: "https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview",
                scope: ApiCatalog.ScopePpac,
                assignCache: list => _envCache = list,
                extraArrayProps: null,
                triggeringButton: _btnLoadEnvs);
        }

        private async void BtnLoadGroups_Click(object sender, EventArgs e)
        {
            await LoadDropdownAsync(
                label: "environment groups",
                url: "https://api.powerplatform.com/environmentmanagement/environmentGroups?api-version=2022-03-01-preview",
                scope: ApiCatalog.ScopePpac,
                assignCache: list => _groupCache = list,
                extraArrayProps: null,
                triggeringButton: _btnLoadGroups);
        }

        private async void BtnLoadDlp_Click(object sender, EventArgs e)
        {
            await LoadDropdownAsync(
                label: "DLP policies",
                url: "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01",
                scope: ApiCatalog.ScopePowerApps,
                assignCache: list => _dlpCache = list,
                extraArrayProps: new[] { "value", "policies" },
                triggeringButton: _btnLoadDlp);
        }

        private async void BtnLoadBilling_Click(object sender, EventArgs e)
        {
            await LoadDropdownAsync(
                label: "billing policies",
                url: "https://api.powerplatform.com/licensing/billingPolicies?api-version=2022-03-01-preview",
                scope: ApiCatalog.ScopePpac,
                assignCache: list => _billingCache = list,
                extraArrayProps: null,
                triggeringButton: _btnLoadBilling);
        }

        // Shared async loader. Hits a list endpoint, parses out (id, name)
        // tuples, caches the result, and rebuilds the form so the matching
        // picker swaps from "(click Load...)" to a populated, filterable
        // dropdown. Same JSON-shape tolerance as the WPF Explorer's
        // LoadDropdownAsync: top-level array, .value, or any extra-named prop.
        private async Task LoadDropdownAsync(
            string label,
            string url,
            string scope,
            Action<List<(string Id, string DisplayName)>> assignCache,
            string[]? extraArrayProps,
            Button triggeringButton)
        {
            // One loader at a time; the button is disabled while in flight.
            _loaderCts?.Cancel();
            _loaderCts = new CancellationTokenSource();
            var ct = _loaderCts.Token;

            triggeringButton.Enabled = false;
            _formLoaderStatus.Text = "Loading " + label + "\u2026";
            SetStatus("Loading " + label + "\u2026", busy: true);
            try
            {
                if (!_isSignedIn)
                {
                    _formLoaderStatus.Text = "Sign in first to load " + label + ".";
                    SetStatus("Sign in required.", busy: false);
                    return;
                }

                var result = await Task.Run(() => _executor.ExecuteAsync("GET", url, null, scope, ct), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;

                var items = new List<(string Id, string DisplayName)>();
                if (!string.IsNullOrWhiteSpace(result.ResponseBody))
                {
                    using var doc = JsonDocument.Parse(result.ResponseBody);
                    JsonElement arr = default;
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        arr = doc.RootElement;
                    }
                    else
                    {
                        foreach (var prop in extraArrayProps ?? new[] { "value" })
                        {
                            if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
                            { arr = v; break; }
                        }
                    }
                    if (arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            string id =
                                TryGuidFromArmId(el)
                                ?? TryStr(el, "policyId")
                                ?? TryStr(el, "id")
                                ?? TryStr(el, "name")
                                ?? string.Empty;
                            string name =
                                TryStr(el, "displayName")
                                ?? TryStr(el, "name")
                                ?? (el.TryGetProperty("properties", out var pp) ? (TryStr(pp, "displayName") ?? id) : id);
                            if (string.IsNullOrEmpty(name)) name = id;
                            if (!string.IsNullOrEmpty(id)) items.Add((id, name));
                        }
                    }
                }
                var sorted = items.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                assignCache(sorted);
                _formLoaderStatus.Text = sorted.Count > 0
                    ? "Loaded " + sorted.Count + " " + label + "."
                    : "No " + label + " returned (HTTP " + (int)result.StatusCode + ").";
                SetStatus(_formLoaderStatus.Text, busy: false);

                // Surface the count on the button itself so the user can see
                // the cache survived across op-switches and doesn't need to
                // be re-loaded. Tag holds the original caption so re-loads
                // overwrite the "(N)" suffix instead of doubling it.
                if (!(triggeringButton.Tag is string baseCaption))
                {
                    baseCaption = triggeringButton.Text;
                    triggeringButton.Tag = baseCaption;
                }
                triggeringButton.Text = sorted.Count > 0
                    ? baseCaption + " (" + sorted.Count + ")"
                    : baseCaption;
                _paramTooltip.SetToolTip(triggeringButton, sorted.Count > 0
                    ? "Cached " + sorted.Count + " " + label + " for this session. Click to refresh."
                    : "Click to load " + label + ".");

                // Rebuild the form so any matching kind shows the populated picker.
                if (_currentOp != null)
                {
                    BuildParamInputs(_currentOp);
                }
            }
            catch (OperationCanceledException)
            {
                _formLoaderStatus.Text = "Cancelled.";
                SetStatus("Cancelled.", busy: false);
            }
            catch (Exception ex)
            {
                _formLoaderStatus.Text = "Load failed: " + ex.Message;
                SetStatus("Load " + label + " failed.", busy: false);
            }
            finally
            {
                triggeringButton.Enabled = true;
            }
        }

        private static string? TryStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ARM-style ids end in "/<guid>"; strip the trailing segment when it
        // parses as a Guid. Mirrors the WPF Explorer helper of the same name.
        private static string? TryGuidFromArmId(JsonElement el)
        {
            var s = TryStr(el, "id");
            if (string.IsNullOrEmpty(s)) return null;
            var tail = s!.TrimEnd('/').Split('/').Last();
            return Guid.TryParse(tail, out _) ? tail : null;
        }
    }
}
