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

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Root plugin control hosted inside XrmToolBox. PR #3 wires MSAL sign-in
    /// (browser + device-code) on top of a shared MSAL cache with the WPF app.
    /// PR #4 wires the operation catalog tree, parameter form, and Execute.
    /// </summary>
    public partial class VerseOpsPluginControl : PluginControlBase
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
            _executor = new ApiExecutor(_auth);
            PopulateOpsTree(filter: null);
            Load += async (_, __) => await ProbeSilentAsync().ConfigureAwait(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _paramTooltip.Dispose();
            }
            base.Dispose(disposing);
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
                    Height = 24
                };
                if (!string.IsNullOrEmpty(p.Help))
                {
                    _paramTooltip.SetToolTip(label, p.Help);
                }

                Control input = BuildInput(p);
                input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                input.Margin = new Padding(0, 2, 0, 2);
                _paramInputs[p.Token] = input;

                _paramTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _paramTable.RowCount++;
                _paramTable.Controls.Add(label, 0, _paramTable.RowCount - 1);
                _paramTable.Controls.Add(input, 1, _paramTable.RowCount - 1);
            }

            _paramTable.ResumeLayout();
        }

        private static Control BuildInput(OpParam p)
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

                // Dynamic kinds (Environment / EnvironmentGroup / DlpPolicy /
                // BillingPolicy / Template) are wired to live pickers in PR #5.
                // For now the user pastes the id/name as text — same as curl.
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
                ComboBox c     => (c.SelectedItem?.ToString() ?? c.Text) ?? string.Empty,
                TextBox t      => t.Text ?? string.Empty,
                _              => control.Text ?? string.Empty
            };
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
            _responseBox.Text = string.Empty;
            _headersBox.Text = string.Empty;
            _jsonTree.Nodes.Clear();
            SetBusy(true, method + " " + url);

            try
            {
                var result = await _executor.ExecuteAsync(method, url, body, scope, cts.Token)
                                            .ConfigureAwait(true);
                RenderResult(op, method, url, scope, result);
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
            _responseBox.Text = string.Empty;
            _headersBox.Text = string.Empty;
            _jsonTree.Nodes.Clear();
            SetBusy(true, "Decoding bearer\u2026");
            try
            {
                var result = await _executor.ExecuteAsync("GET", "local://decode-token", null, scope, cts.Token)
                                            .ConfigureAwait(true);
                _responseHeader.Text = "Response  \u2014  decoded JWT for scope: " + scope;
                _responseBox.Text = "// scope: " + scope + "\r\n// (local decode \u2014 no network call)\r\n\r\n" + result.ResponseBody;
                PopulateJsonTree(_jsonTree, result.ResponseBody);
                PopulateHeaders(result.ResponseHeaders);
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

        private static string SubstituteTokens(string template, IReadOnlyDictionary<string, string> values)
        {
            return s_tokenRegex.Replace(template, m =>
            {
                var name = m.Groups["name"].Value;
                return values.TryGetValue(name, out var v) ? v : m.Value;
            });
        }

        private void RenderResult(ApiOperation op, string method, string url, string scope, ApiCallResult result)
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

            PopulateHeaders(result.ResponseHeaders);
            PopulateJsonTree(_jsonTree, result.ResponseBody);

            _statusBarElapsed.Text = result.ElapsedMs + " ms  \u2022  " + result.StatusCode + " " + result.ReasonPhrase;
            SetBusy(false, "Ready.");
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
            // Reset the find-next anchor whenever the term changes.
            _lastSearchAnchor = -1;
            // Live-filter the JSON tree when that tab is active.
            if (_responseTabs.SelectedTab == _respTabTree)
            {
                ApplyTreeFilter(_respSearchBox.Text);
            }
            else
            {
                _respSearchInfo.Text = string.Empty;
            }
        }

        private void RespSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                BtnRespSearchNext_Click(sender, e);
            }
        }

        private void BtnRespSearchNext_Click(object sender, EventArgs e)
        {
            var term = _respSearchBox.Text;
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
        // become misleading (e.g. "(wrapped)" on a different surface). Clear it.
        private void ResponseTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            _respSearchInfo.Text = string.Empty;
            _lastSearchAnchor = -1;
        }
    }
}
