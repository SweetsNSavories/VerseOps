using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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

        public VerseOpsPluginControl()
        {
            InitializeComponent();
            _executor = new ApiExecutor(_auth);
            PopulateOpsTree(filter: null);
            Load += async (_, __) => await ProbeSilentAsync().ConfigureAwait(true);
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
            UpdateExecuteEnabled();
        }

        private void SetSignedIn(string user, string method)
        {
            _statusLabel.Text = "Signed in as " + user + " (" + method + ")";
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = true;
            _isSignedIn = true;
            UpdateExecuteEnabled();
        }

        private void SetSignedOut(string note)
        {
            _statusLabel.Text = note;
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = false;
            _isSignedIn = false;
            UpdateExecuteEnabled();
        }

        private void UpdateExecuteEnabled()
        {
            _btnExecute.Enabled = _isSignedIn && _currentOp != null && _executeCts == null;
            _executeHint.Text = _isSignedIn
                ? (_currentOp == null ? "Pick an operation on the left." : string.Empty)
                : "Sign in to enable Execute.";
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
            UpdateExecuteEnabled();
        }

        private void BuildParamInputs(ApiOperation op)
        {
            _paramTable.SuspendLayout();
            _paramTable.Controls.Clear();
            _paramTable.RowStyles.Clear();
            _paramTable.RowCount = 0;
            _paramInputs.Clear();

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
                    var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 250, ReshowDelay = 100, ShowAlways = true };
                    tip.SetToolTip(label, p.Help);
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

            var url = SubstituteTokens(op.UrlTemplate, values);
            var body = string.IsNullOrEmpty(_bodyEditor.Text) ? null : SubstituteTokens(_bodyEditor.Text, values);

            _executeCts = new CancellationTokenSource();
            UpdateExecuteEnabled();
            _btnExecute.Text = "Running\u2026";
            _responseHeader.Text = "Response \u2014 sending\u2026";
            _responseBox.Text = string.Empty;

            try
            {
                var result = await _executor.ExecuteAsync(op.HttpMethod, url, body, op.TokenScope, _executeCts.Token)
                                            .ConfigureAwait(true);
                RenderResult(op, url, result);
            }
            catch (OperationCanceledException)
            {
                _responseHeader.Text = "Response \u2014 cancelled.";
            }
            catch (MsalException ex)
            {
                _responseHeader.Text = "Response \u2014 sign-in error.";
                _responseBox.Text = "MSAL error: " + ex.Message;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _responseHeader.Text = "Response \u2014 network error.";
                _responseBox.Text = "HTTP error: " + ex.Message;
            }
            finally
            {
                _executeCts.Dispose();
                _executeCts = null;
                _btnExecute.Text = "Execute";
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

        private void RenderResult(ApiOperation op, string url, ApiCallResult result)
        {
            _responseHeader.Text =
                "Response  \u2014  " + result.StatusCode + " " + result.ReasonPhrase +
                "  \u2022  " + result.ElapsedMs + " ms" +
                (string.IsNullOrEmpty(result.CorrelationId) ? string.Empty : "  \u2022  x-ms-correlation-request-id: " + result.CorrelationId);

            // Show the resolved URL in a header comment so the user can copy
            // the exact call back into curl/Postman without re-substituting tokens.
            var sb = new StringBuilder();
            sb.Append("// ").Append(op.HttpMethod).Append("  ").AppendLine(url);
            sb.Append("// scope: ").AppendLine(op.TokenScope);
            if (!string.IsNullOrEmpty(result.OperationLocation))
            {
                sb.Append("// operation-location: ").AppendLine(result.OperationLocation);
            }
            sb.AppendLine();
            sb.Append(result.ResponseBody);
            _responseBox.Text = sb.ToString();
        }
    }
}
