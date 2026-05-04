using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VerseOps.App.Api;
using VerseOps.App.Auth;

namespace VerseOps.App;

public partial class MainWindow : Window
{
    private readonly AuthService _auth = new();
    private readonly ApiExecutor _executor;
    private ApiOperation? _selected;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _busyStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        _executor = new ApiExecutor(_auth);
        CbMethod.SelectedIndex = 0;
        BuildOperationsTree();
        if (TxtSurfaceNotice != null) TxtSurfaceNotice.Text = PpacNotice;
        UpdateAuthState();
        _elapsedTimer.Tick += (_, _) =>
            TxtElapsed.Text = $"{(DateTime.UtcNow - _busyStartedUtc).TotalSeconds:0.0}s";
    }

    private void BuildOperationsTree()
    {
        TvOps.Items.Clear();
        var surface = GetSelectedSurface();
        var ops = ApiCatalog.ForSurface(surface);
        foreach (var grp in ops.GroupBy(o => o.Category))
        {
            var node = new TreeViewItem { Header = grp.Key, IsExpanded = false };
            // Group by SubCategory when present; otherwise leaves attach directly.
            var subGroups = grp.GroupBy(o => o.SubCategory ?? string.Empty).ToList();
            var hasSub = subGroups.Any(s => !string.IsNullOrEmpty(s.Key));
            if (hasSub)
            {
                foreach (var sub in subGroups.OrderBy(s => s.Key))
                {
                    if (string.IsNullOrEmpty(sub.Key))
                    {
                        foreach (var op in sub) node.Items.Add(MakeOpLeaf(op));
                        continue;
                    }
                    var subNode = new TreeViewItem { Header = sub.Key, IsExpanded = false };
                    foreach (var op in sub.OrderBy(o => o.Name))
                        subNode.Items.Add(MakeOpLeaf(op));
                    node.Items.Add(subNode);
                }
            }
            else
            {
                foreach (var op in grp) node.Items.Add(MakeOpLeaf(op));
            }
            TvOps.Items.Add(node);
        }
    }

    private static TreeViewItem MakeOpLeaf(ApiOperation op) => new()
    {
        Header = $"{op.HttpMethod}  {op.Name}",
        Tag = op
    };

    private ApiSurface GetSelectedSurface() => ApiSurface.Ppac;

    // Disclaimer shown above the operations tree. VerseOps now ships PPAC-only:
    // BAP is deprecated and undocumented, so we no longer expose it in the tree.
    // The Register-SP button still uses the BAP /adminApplications PUT because
    // that is the documented bootstrap path for tenant-admin service principals
    // that PPAC itself does not yet replace.
    private const string BapNotice =
        "BAP surface (deprecated). Hidden from the tree in this build. Use the\r\n" +
        "Register SP button to bootstrap an SP for admin access — that one BAP\r\n" +
        "route remains the documented setup path until PPAC ships an equivalent.";
    private const string PpacNotice =
        "PPAC surface (preview). api.powerplatform.com is the new control plane and the\r\n" +
        "long-term replacement for BAP. Many routes return RouteNotFound today; that is\r\n" +
        "expected during preview. Once Microsoft GAs api.powerplatform.com we hope every\r\n" +
        "entry in this tree responds successfully — we will keep improvising the catalog\r\n" +
        "as new routes light up. Edit the URL / body inline and re-Send to experiment.";

    private static string SurfaceTag(ApiSurface s) => s switch
    {
        ApiSurface.Bap => "bap",
        ApiSurface.Ppac => "ppac",
        _ => "local"
    };

    private void OnSurfaceChanged(object sender, RoutedEventArgs e)
    {
        // Surface toggle removed — PPAC-only build. Method retained as a no-op
        // so the XAML reference remains valid.
    }

    private void OnAuthModeChanged(object sender, RoutedEventArgs e)
    {
        if (UserPanel == null || AppPanel == null) return;
        var isUser = RbUser.IsChecked == true;
        UserPanel.Visibility = isUser ? Visibility.Visible : Visibility.Collapsed;
        AppPanel.Visibility = isUser ? Visibility.Collapsed : Visibility.Visible;
        if (TbTenant != null && isUser && string.IsNullOrWhiteSpace(TbTenant.Text)) TbTenant.Text = "common";
        UpdateAuthState();
    }

    private void UpdateAuthState()
    {
        if (TxtAuthState == null) return;
        var mode = RbUser.IsChecked == true ? "User" : "App-only";
        var who = _auth.LastSignedInUser ?? "(not signed in yet)";
        TxtAuthState.Text = $"Mode: {mode}   Identity: {who}";
    }

    private void OnOperationSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: ApiOperation op })
        {
            _selected = op;
            CbMethod.SelectedItem = CbMethod.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Content, op.HttpMethod, StringComparison.OrdinalIgnoreCase))
                ?? CbMethod.Items[0];
            CbScope.SelectedItem = CbScope.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Content == op.TokenScope) ?? CbScope.Items[0];
            TbUrl.Text = op.UrlTemplate;
            TbBody.Text = op.RequestBodyTemplate ?? string.Empty;
            TbDescription.Text = op.Description;
            TxtStatus.Text = $"Loaded template: {op.Category} / {op.Name}";
            BuildForm(op);
        }
    }

    // -----------------------------------------------------------------
    // Dynamic Form (per-operation request builder)
    // -----------------------------------------------------------------
    private readonly Dictionary<string, FrameworkElement> _formInputs = new();
    private List<(string Id, string DisplayName)>? _envCache;
    private List<(string Id, string DisplayName)>? _groupCache;
    private List<(string Id, string DisplayName)>? _dlpCache;
    private List<(string Id, string DisplayName)>? _billingCache;

    private void BuildForm(ApiOperation op)
    {
        _formInputs.Clear();
        GridForm.Children.Clear();
        GridForm.RowDefinitions.Clear();
        GridForm.ColumnDefinitions.Clear();
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ps = op.Parameters;
        if (ps is null || ps.Count == 0)
        {
            TxtFormHint.Text = "This operation has no defined parameters. Edit URL/body directly in the Raw body tab.";
            return;
        }
        TxtFormHint.Text = $"Fill in the parameters and press Apply. Template tokens like {{environmentId}} will be substituted into URL and body.";

        int row = 0;
        foreach (var p in ps)
        {
            GridForm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new Label { Content = p.Label, ToolTip = p.Help };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            GridForm.Children.Add(lbl);

            FrameworkElement input = p.Kind switch
            {
                ParamKind.Choice => MakeChoice(p.Choices ?? Array.Empty<string>(), p.Default),
                ParamKind.Environment => MakeEnvCombo(p.Default),
                ParamKind.EnvironmentGroup => MakeGroupCombo(p.Default),
                ParamKind.DlpPolicy => MakeCachedCombo(_dlpCache, "Load DLP", p.Default),
                ParamKind.BillingPolicy => MakeCachedCombo(_billingCache, "Load billing", p.Default),
                ParamKind.Template => MakeCachedCombo(null, "(no loader — type template name)", p.Default),
                ParamKind.MultilineText => MakeMultiline(p.Default),
                ParamKind.Integer => MakeText(p.Default, monospace: true),
                _ => MakeText(p.Default)
            };
            input.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            GridForm.Children.Add(input);
            _formInputs[p.Token] = input;
            row++;
        }
    }

    private static ComboBox MakeChoice(IReadOnlyList<string> choices, string? def)
    {
        var cb = new ComboBox { IsEditable = true };
        foreach (var c in choices) cb.Items.Add(c);
        cb.Text = def ?? (choices.Count > 0 ? choices[0] : "");
        return cb;
    }

    private ComboBox MakeEnvCombo(string? def)
    {
        var cb = new ComboBox { IsEditable = true };
        if (_envCache != null)
            foreach (var (id, name) in _envCache) cb.Items.Add(new ComboBoxItem { Content = $"{name}  ({id})", Tag = id });
        else
            cb.Items.Add(new ComboBoxItem { Content = "(click 'Load environments' to populate)", Tag = "" });
        cb.Text = def ?? "";
        return cb;
    }

    private ComboBox MakeGroupCombo(string? def)
    {
        var cb = new ComboBox { IsEditable = true };
        if (_groupCache != null)
            foreach (var (id, name) in _groupCache) cb.Items.Add(new ComboBoxItem { Content = $"{name}  ({id})", Tag = id });
        else
            cb.Items.Add(new ComboBoxItem { Content = "(click 'Load groups' to populate)", Tag = "" });
        cb.Text = def ?? "";
        return cb;
    }

    private static ComboBox MakeCachedCombo(List<(string Id, string DisplayName)>? cache, string loadHint, string? def)
    {
        var cb = new ComboBox { IsEditable = true };
        if (cache != null)
            foreach (var (id, name) in cache) cb.Items.Add(new ComboBoxItem { Content = $"{name}  ({id})", Tag = id });
        else
            cb.Items.Add(new ComboBoxItem { Content = $"(click '{loadHint}' to populate)", Tag = "" });
        cb.Text = def ?? "";
        return cb;
    }

    private static TextBox MakeText(string? def, bool monospace = false)
    {
        var tb = new TextBox { Text = def ?? "" };
        if (monospace) tb.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return tb;
    }

    private static TextBox MakeMultiline(string? def) => new TextBox
    {
        Text = def ?? "",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 90,
        FontFamily = new FontFamily("Cascadia Mono, Consolas")
    };

    private void OnApplyForm(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { TxtFormStatus.Text = "No operation selected."; return; }
        var values = ReadFormValues();
        TbUrl.Text = Substitute(_selected.UrlTemplate, values);
        if (!string.IsNullOrEmpty(_selected.RequestBodyTemplate))
            TbBody.Text = Substitute(_selected.RequestBodyTemplate, values);
        TxtFormStatus.Text = $"Applied {values.Count} value(s).";
    }

    private Dictionary<string, string> ReadFormValues()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, ctrl) in _formInputs)
        {
            string val = ctrl switch
            {
                ComboBox cb when cb.SelectedItem is ComboBoxItem ci && ci.Tag is string tag && !string.IsNullOrEmpty(tag) => tag,
                ComboBox cb => cb.Text?.Trim() ?? "",
                TextBox tb => tb.Text ?? "",
                _ => ""
            };
            d[token] = val;
        }
        return d;
    }

    private static string Substitute(string template, Dictionary<string, string> values)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in values) sb.Replace("{" + k + "}", v);
        return sb.ToString();
    }

    private async void OnLoadEnvironments(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAuthInputs();
            BeginBusy("Loading environments...");
            var ct = _cts!.Token;

            // PPAC-only build: always use the PPAC environment list, regardless of
            // user vs app-only mode. (Once registered, both identity types work.)
            var url   = "https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview";
            var scope = ApiCatalog.ScopePpac;

            var result = await Task.Run(() => _executor.ExecuteAsync("GET", url, null, scope, ct), ct);
            var envs = new List<(string Id, string DisplayName)>();
            using var doc = JsonDocument.Parse(result.ResponseBody);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var env in arr.EnumerateArray())
                {
                    // PPAC uses "name" (id) + "properties.displayName" \u2014 same as BAP.
                    // Some PPAC payloads put the GUID in "id" instead; handle both.
                    var name = env.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name) && env.TryGetProperty("id", out var idEl))
                        name = idEl.GetString() ?? "";
                    var display = name;
                    if (env.TryGetProperty("properties", out var p) && p.TryGetProperty("displayName", out var dn))
                        display = dn.GetString() ?? name;
                    else if (env.TryGetProperty("displayName", out var dn2))
                        display = dn2.GetString() ?? name;
                    if (!string.IsNullOrEmpty(name)) envs.Add((name, display));
                }
            }
            _envCache = envs.OrderBy(x => x.DisplayName).ToList();
            TxtFormStatus.Text = _envCache.Count > 0
                ? $"Loaded {_envCache.Count} environments (via {(scope == ApiCatalog.ScopePpac ? "PPAC" : "BAP")})."
                : $"No environments returned. HTTP {result.StatusCode}. Check Body tab for details.";
            if (_selected != null) BuildForm(_selected); // refresh dropdowns
        }
        catch (OperationCanceledException) { TxtFormStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtFormStatus.Text = $"Load failed: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private async void OnLoadGroups(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAuthInputs();
            BeginBusy("Loading environment groups...");
            var ct = _cts!.Token;
            var url = "https://api.powerplatform.com/environmentmanagement/environmentGroups?api-version=2022-03-01-preview";
            var result = await Task.Run(() => _executor.ExecuteAsync("GET", url, null, ApiCatalog.ScopePpac, ct), ct);
            var groups = new List<(string Id, string DisplayName)>();
            using var doc = JsonDocument.Parse(result.ResponseBody);
            JsonElement arr = default;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) arr = v;
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    var id = g.TryGetProperty("id", out var i) ? i.GetString() ?? "" : (g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                    var name = g.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id
                             : g.TryGetProperty("properties", out var p) && p.TryGetProperty("displayName", out var pdn) ? pdn.GetString() ?? id
                             : id;
                    if (!string.IsNullOrEmpty(id)) groups.Add((id, name));
                }
            }
            _groupCache = groups.OrderBy(x => x.DisplayName).ToList();
            TxtFormStatus.Text = _groupCache.Count > 0
                ? $"Loaded {_groupCache.Count} groups."
                : "No groups returned (response may not be JSON-list shape — check Body tab).";
            if (_selected != null) BuildForm(_selected);
        }
        catch (OperationCanceledException) { TxtFormStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtFormStatus.Text = $"Load failed: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private async void OnLoadDlp(object sender, RoutedEventArgs e)
    {
        await LoadDropdownAsync(
            label: "DLP policies",
            url: "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01",
            scope: ApiCatalog.ScopePowerApps,
            assignCache: list => _dlpCache = list,
            // BAP DLP shape: { "value":[{"name":"<id>","displayName":"..."}], ... } — sometimes "policies" instead of "value".
            extraArrayProps: new[] { "value", "policies" });
    }

    private async void OnLoadBilling(object sender, RoutedEventArgs e)
    {
        await LoadDropdownAsync(
            label: "billing policies",
            url: "https://api.powerplatform.com/licensing/billingPolicies?api-version=2022-03-01-preview",
            scope: ApiCatalog.ScopePpac,
            assignCache: list => _billingCache = list);
    }

    private async Task LoadDropdownAsync(
        string label,
        string url,
        string scope,
        Action<List<(string Id, string DisplayName)>> assignCache,
        string[]? extraArrayProps = null)
    {
        try
        {
            ApplyAuthInputs();
            BeginBusy($"Loading {label}...");
            var ct = _cts!.Token;
            var result = await Task.Run(() => _executor.ExecuteAsync("GET", url, null, scope, ct), ct);
            var items = new List<(string Id, string DisplayName)>();
            using var doc = JsonDocument.Parse(result.ResponseBody);
            JsonElement arr = default;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
            else
            {
                foreach (var prop in (extraArrayProps ?? new[] { "value" }))
                {
                    if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
                    { arr = v; break; }
                }
            }
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    // PPAC billing policies (and similar resources) put a GUID in
                    // the trailing segment of the ARM-style "id" field, while
                    // "name" carries a human-readable slug. The route expects
                    // the GUID. Prefer the trailing segment of "id" when it is
                    // GUID-shaped; fall back to "name" / "policyId" otherwise.
                    string id =
                        TryGuidFromArmId(el)
                        ?? TryStr(el, "policyId")
                        ?? TryStr(el, "id")
                        ?? TryStr(el, "name")
                        ?? "";
                    string name = TryStr(el, "displayName")
                              ?? TryStr(el, "name")
                              ?? (el.TryGetProperty("properties", out var pp) ? TryStr(pp, "displayName") ?? id : id);
                    if (!string.IsNullOrEmpty(id)) items.Add((id, name ?? id));
                }
            }
            var sorted = items.OrderBy(x => x.DisplayName).ToList();
            assignCache(sorted);
            TxtFormStatus.Text = sorted.Count > 0
                ? $"Loaded {sorted.Count} {label}."
                : $"No {label} returned (check Body tab).";
            if (_selected != null) BuildForm(_selected);
        }
        catch (OperationCanceledException) { TxtFormStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtFormStatus.Text = $"Load failed: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private static string? TryStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Extracts a GUID from the trailing segment of an ARM-style id like
    /// "/providers/.../billingPolicies/3b451e9c-c4d7-4c12-8d12-69f996e7fd48".
    /// Returns null if the element has no string "id" or the tail is not GUID-shaped.
    /// </summary>
    private static string? TryGuidFromArmId(JsonElement el)
    {
        var s = TryStr(el, "id");
        if (string.IsNullOrEmpty(s)) return null;
        var tail = s.TrimEnd('/').Split('/').Last();
        return Guid.TryParse(tail, out _) ? tail : null;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        string method = "GET", url = "", scope = ApiCatalog.ScopePowerApps;
        string? body = null;
        try
        {
            ApplyAuthInputs();
            method = ((ComboBoxItem)CbMethod.SelectedItem).Content?.ToString() ?? "GET";
            scope = ((ComboBoxItem)CbScope.SelectedItem).Content?.ToString() ?? ApiCatalog.ScopePowerApps;
            url = TbUrl.Text.Trim();
            body = string.IsNullOrWhiteSpace(TbBody.Text) ? null : TbBody.Text;

            if (string.IsNullOrEmpty(url)) { TxtStatus.Text = "URL is required."; return; }
            if (url.Contains("{") && url.Contains("}"))
            {
                MessageBox.Show("Replace placeholders like {environmentId} in the URL before sending.",
                    "Unfilled placeholder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BeginBusy($"Sending {method} {url} ...");
            TbResponse.Text = ""; TbHeaders.Text = ""; TxtRespMeta.Text = ""; TvJson.Items.Clear();

            var ct = _cts!.Token;
            // Force off the UI thread so MSAL prompts / HTTP / serialization never block the message pump.
            var result = await Task.Run(() => _executor.ExecuteAsync(method, url, body, scope, ct), ct);

            TbResponse.Text = result.ResponseBody;
            RenderJsonTree(result.ResponseBody);
            TbHeaders.Text = string.Join(Environment.NewLine,
                result.ResponseHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
            TxtRespMeta.Text = $"{result.StatusCode} {result.ReasonPhrase}   {result.ElapsedMs} ms"
                               + (result.CorrelationId is null ? "" : $"   correlation={result.CorrelationId}")
                               + (result.OperationLocation is null ? "" : $"   op-location={result.OperationLocation}");
            TxtStatus.Text = $"Done. HTTP {result.StatusCode}.";
            UpdateAuthState();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Cancelled.";
            TxtRespMeta.Text = "CANCELLED";
        }
        catch (Exception ex)
        {
            TbResponse.Text = ex.ToString();
            TxtRespMeta.Text = "EXCEPTION";
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    // ---------------------------------------------------------------------
    // PPAC sweep — runs every PPAC operation against the first N environments
    // and dumps a tab-aligned report into the Body / response area. Operations
    // whose template tokens cannot be auto-filled (groupId, policyId, etc.) are
    // skipped with a "skipped: needs <token>" line so the report is honest.
    // ---------------------------------------------------------------------
    private const int PpacSweepEnvCount = 10;

    private async void OnPpacSweep(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAuthInputs();
            BeginBusy("PPAC sweep: ensuring environment list...");
            var ct = _cts!.Token;

            // 1. Make sure we have an environment list. The "List environments" route
            //    on PPAC isn't reliably available in every tenant yet, so we always
            //    pull from BAP (which definitely lists envs) for the seed set.
            if (_envCache == null || _envCache.Count == 0)
            {
                var listUrl = "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01";
                var listResult = await Task.Run(() =>
                    _executor.ExecuteAsync("GET", listUrl, null, ApiCatalog.ScopePowerApps, ct), ct);
                var envs = new List<(string Id, string DisplayName)>();
                using var doc = JsonDocument.Parse(listResult.ResponseBody);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var env in arr.EnumerateArray())
                    {
                        var name = env.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var display = env.TryGetProperty("properties", out var p) && p.TryGetProperty("displayName", out var dn)
                            ? dn.GetString() ?? name
                            : name;
                        if (!string.IsNullOrEmpty(name)) envs.Add((name, display));
                    }
                }
                _envCache = envs.OrderBy(x => x.DisplayName).ToList();
            }

            var picked = _envCache.Take(PpacSweepEnvCount).ToList();
            if (picked.Count == 0)
            {
                TbResponse.Text = "PPAC sweep aborted: no environments visible to current identity.";
                TxtRespMeta.Text = "ABORTED";
                return;
            }

            var ppacOps = ApiCatalog.PpacOperations
                .Where(o => o.Surface == ApiSurface.Ppac && o.HttpMethod == "GET")
                .ToList();

            // Categorise ops by which template tokens they reference.
            // We only auto-substitute {environmentId}; anything else is skipped.
            static IEnumerable<string> ExtractTokens(string template)
            {
                int i = 0;
                while ((i = template.IndexOf('{', i)) >= 0)
                {
                    var j = template.IndexOf('}', i + 1);
                    if (j < 0) yield break;
                    yield return template.Substring(i + 1, j - i - 1);
                    i = j + 1;
                }
            }

            var report = new StringBuilder();
            report.AppendLine($"PPAC sweep — {picked.Count} environment(s) × {ppacOps.Count} GET operation(s)");
            report.AppendLine($"Started {DateTime.Now:O}");
            report.AppendLine(new string('-', 100));

            int totalCalls = 0, ok = 0, fail = 0, skipped = 0;
            int totalEstimate = ppacOps.Count + ppacOps.Count(o => ExtractTokens(o.UrlTemplate).Contains("environmentId")) * (picked.Count - 1);

            foreach (var op in ppacOps)
            {
                if (ct.IsCancellationRequested) break;

                var tokens = ExtractTokens(op.UrlTemplate).Distinct().ToList();
                var unsupported = tokens.Where(t => t != "environmentId").ToList();
                if (unsupported.Count > 0)
                {
                    skipped++;
                    report.AppendLine($"SKIP  {op.Category,-18} {op.Name,-40} needs: {{{string.Join(", ", unsupported)}}}");
                    continue;
                }

                if (!tokens.Contains("environmentId"))
                {
                    // Tenant-scoped — call once.
                    totalCalls++;
                    TxtStatus.Text = $"PPAC sweep [{totalCalls}/{totalEstimate}] {op.Name}";
                    var (status, ms, snippet) = await SafeExecAsync(op.HttpMethod, op.UrlTemplate, op.RequestBodyTemplate, op.TokenScope, ct);
                    if (status >= 200 && status < 300) ok++; else fail++;
                    report.AppendLine($"{status,3} {ms,5}ms  {op.Category,-18} {op.Name,-40} (tenant)         {snippet}");
                    continue;
                }

                foreach (var (id, display) in picked)
                {
                    if (ct.IsCancellationRequested) break;
                    totalCalls++;
                    TxtStatus.Text = $"PPAC sweep [{totalCalls}/{totalEstimate}] {op.Name} → {display}";
                    var url = op.UrlTemplate.Replace("{environmentId}", id);
                    var (status, ms, snippet) = await SafeExecAsync(op.HttpMethod, url, op.RequestBodyTemplate, op.TokenScope, ct);
                    if (status >= 200 && status < 300) ok++; else fail++;
                    var envTag = display.Length > 18 ? display[..18] : display.PadRight(18);
                    report.AppendLine($"{status,3} {ms,5}ms  {op.Category,-18} {op.Name,-40} {envTag}  {snippet}");
                }
            }

            report.AppendLine(new string('-', 100));
            report.AppendLine($"Done. calls={totalCalls}  ok={ok}  fail={fail}  skipped={skipped}");
            report.AppendLine($"Finished {DateTime.Now:O}");

            TbResponse.Text = report.ToString();
            TvJson.Items.Clear();
            TbHeaders.Text = "";
            TxtRespMeta.Text = $"PPAC sweep   ok={ok}  fail={fail}  skipped={skipped}";
            TxtStatus.Text = $"PPAC sweep complete — {ok} OK, {fail} fail, {skipped} skipped.";
            UpdateAuthState();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "PPAC sweep cancelled.";
        }
        catch (Exception ex)
        {
            TbResponse.Text = ex.ToString();
            TxtStatus.Text = $"PPAC sweep error: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<(int status, long ms, string snippet)> SafeExecAsync(
        string method, string url, string? body, string scope, CancellationToken ct)
    {
        try
        {
            var r = await Task.Run(() => _executor.ExecuteAsync(method, url, body, scope, ct), ct);
            var snippet = r.ResponseBody?.Replace('\n', ' ').Replace('\r', ' ') ?? "";
            if (snippet.Length > 90) snippet = snippet[..90] + "...";
            return (r.StatusCode, r.ElapsedMs, snippet);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 90) msg = msg[..90] + "...";
            return (0, 0, $"EXC {ex.GetType().Name}: {msg}");
        }
    }

    private async void OnDecodeToken(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAuthInputs();
            var scope = ((ComboBoxItem)CbScope.SelectedItem).Content?.ToString() ?? ApiCatalog.ScopePowerApps;
            BeginBusy("Acquiring token to decode...");
            var ct = _cts!.Token;
            var token = await Task.Run(() => _auth.GetTokenAsync(scope, ct), ct);
            TbResponse.Text = ApiExecutor.DecodeJwtClaims(token);
            RenderJsonTree(TbResponse.Text);
            TbHeaders.Text = $"Raw bearer length: {token.Length}{Environment.NewLine}First 60 chars: {token[..Math.Min(60, token.Length)]}...";
            TxtRespMeta.Text = $"Local JWT decode for scope {scope}";
            TxtStatus.Text = "Token decoded.";
            UpdateAuthState();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            TbResponse.Text = ex.ToString();
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); TxtStatus.Text = "Cancelling..."; } catch { }
    }

    private void BeginBusy(string status)
    {
        _cts?.Cancel(); _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _busyStartedUtc = DateTime.UtcNow;
        TxtElapsed.Text = "0.0s";
        _elapsedTimer.Start();
        PbBusy.Visibility = Visibility.Visible;
        BtnSend.IsEnabled = false;
        BtnDecode.IsEnabled = false;
        if (BtnSweep != null) BtnSweep.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        TxtStatus.Text = status;
        Mouse.OverrideCursor = Cursors.AppStarting;
    }

    private void EndBusy()
    {
        _elapsedTimer.Stop();
        PbBusy.Visibility = Visibility.Collapsed;
        BtnSend.IsEnabled = true;
        BtnDecode.IsEnabled = true;
        if (BtnSweep != null) BtnSweep.IsEnabled = true;
        BtnCancel.IsEnabled = false;
        Mouse.OverrideCursor = null;
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await _auth.SignOutAsync();
        UpdateAuthState();
        TxtStatus.Text = "Signed out.";
    }

    // ---------------------------------------------------------------------
    // SP registration (one-time, per tenant)
    //
    // Microsoft has not exposed a PPAC route for this; it must go through BAP:
    //   PUT https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform
    //         /adminApplications/{clientId}?api-version=2020-10-01
    // The PUT MUST use a delegated (interactive) token from a Power Platform
    // Administrator \u2014 the SP cannot register itself.
    // After this one call, the SP behaves tenant-wide as if it had the Power
    // Platform Administrator role for SP-supported APIs.
    // ---------------------------------------------------------------------
    private async void OnRegisterSp(object sender, RoutedEventArgs e)
    {
        var clientId = TbAppClientId?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            MessageBox.Show(
                "Fill the App-only ClientId field first \u2014 that's the SP we will register.\n\n" +
                "Switch the auth radio to App-only to reveal the field, paste the App Registration's Application (client) Id, then click Register SP again.",
                "Missing ClientId", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tenant = TbTenant?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(tenant) || tenant.Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Set Tenant to your tenant id (GUID) or domain. 'common' is not allowed for the registration call.",
                "Tenant required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"This will sign you in interactively as a Power Platform Administrator and register\n\n" +
            $"   ClientId  : {clientId}\n" +
            $"   Tenant    : {tenant}\n\n" +
            $"as a tenant-wide admin management application via BAP.\n" +
            $"Run this once per tenant. Continue?",
            "Confirm SP registration", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            BeginBusy("Registering service principal...");
            var ct = _cts!.Token;

            // Use a dedicated AuthService instance forced into User mode so we
            // never accidentally re-use a cached App-only token for this PUT.
            var admin = new VerseOps.App.Auth.AuthService
            {
                Mode = VerseOps.App.Auth.AuthService.AuthMode.User,
                TenantId = tenant,
                PublicClientId = string.IsNullOrWhiteSpace(TbPublicClientId?.Text)
                    ? "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
                    : TbPublicClientId.Text.Trim(),
            };

            // The /adminApplications endpoint specifically requires a token whose
            // audience is the BAP host (api.bap.microsoft.com). The general
            // service.powerapps.com audience is rejected here with
            // AuthorizationHeaderInvalid / S2S17001 ("none of the inbound policies
            // were satisfied").
            var bapScope = "https://api.bap.microsoft.com/.default";
            var token = await Task.Run(() => admin.GetTokenAsync(bapScope, ct), ct);

            var url = $"https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform" +
                      $"/adminApplications/{Uri.EscapeDataString(clientId)}?api-version=2020-10-01";
            using var http = new System.Net.Http.HttpClient();
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // Empty body \u2014 the endpoint just needs the URL + admin token.
            req.Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            TbResponse.Text = body;
            RenderJsonTree(body);
            TbHeaders.Text = $"PUT {url}\nAdmin: {admin.LastSignedInUser}\nHTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            TxtRespMeta.Text = $"SP register   HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";

            if (resp.IsSuccessStatusCode)
            {
                TxtStatus.Text = $"\u2705 Registered. The SP {clientId} is now a tenant admin management application. Switch auth to App-only to use it.";
                MessageBox.Show(
                    $"Success.\n\nClientId {clientId} is now registered as a tenant admin management application.\n\n" +
                    "Switch the auth radio to App-only and you should be able to call PPAC / BAP admin endpoints with the client secret.",
                    "Registered", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtStatus.Text = $"\u274C Registration failed: HTTP {(int)resp.StatusCode}.";
                MessageBox.Show(
                    $"Registration call returned HTTP {(int)resp.StatusCode}.\n\nCommon causes:\n" +
                    "  \u2022 Signed-in user is not a Power Platform Administrator.\n" +
                    "  \u2022 ClientId does not match an existing App Registration in the tenant.\n" +
                    "  \u2022 Tenant id is wrong.\n\nSee response body in the Body tab for details.",
                    "Registration failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Registration cancelled."; }
        catch (Exception ex)
        {
            TbResponse.Text = ex.ToString();
            TxtStatus.Text = $"Registration error: {ex.Message}";
        }
        finally { EndBusy(); }
    }

    // ---------------------------------------------------------------------
    // JSON tree viewer (lazy: children materialise on first expand)
    // ---------------------------------------------------------------------
    private static readonly Brush KeyBrush     = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
    private static readonly Brush StringBrush  = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly Brush NumberBrush  = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
    private static readonly Brush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly Brush MetaBrush    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    // Lightweight model parsed off-thread; keeps no JsonDocument handle.
    private sealed class JNode
    {
        public string Name = "";
        public JsonValueKind Kind;
        public string? Scalar;          // for primitives, raw or string value
        public List<JNode>? Children;   // for objects/arrays
        public int Count;               // child count for objects/arrays
    }

    private async void RenderJsonTree(string? text)
    {
        TvJson.Items.Clear();
        TxtTreeStats.Text = "";
        if (string.IsNullOrWhiteSpace(text)) return;

        // Hard cap: avoid pathological 50MB pastes.
        const int MaxBytes = 8 * 1024 * 1024;
        if (text.Length > MaxBytes)
        {
            TvJson.Items.Add(new TreeViewItem { Header = $"(response too large for tree view: {text.Length:N0} chars)", Foreground = MetaBrush });
            return;
        }

        JNode? root;
        int totalNodes;
        try
        {
            (root, totalNodes) = await Task.Run(() => ParseToModel(text!));
        }
        catch (JsonException ex)
        {
            TvJson.Items.Add(new TreeViewItem { Header = $"(not JSON: {ex.Message})", Foreground = MetaBrush });
            return;
        }
        if (root is null) return;

        var rootItem = MakeItem(root);
        rootItem.IsExpanded = true; // top-level only; deeper nodes load on demand
        TvJson.Items.Add(rootItem);
        TxtTreeStats.Text = $"{totalNodes} nodes";
    }

    private static (JNode root, int total) ParseToModel(string text)
    {
        using var doc = JsonDocument.Parse(text);
        int n = 0;
        var root = Convert("root", doc.RootElement, ref n);
        return (root, n);

        static JNode Convert(string name, JsonElement el, ref int n)
        {
            n++;
            var node = new JNode { Name = name, Kind = el.ValueKind };
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    node.Children = new List<JNode>();
                    foreach (var p in el.EnumerateObject()) node.Children.Add(Convert(p.Name, p.Value, ref n));
                    node.Count = node.Children.Count;
                    break;
                case JsonValueKind.Array:
                    node.Children = new List<JNode>();
                    int i = 0;
                    foreach (var e in el.EnumerateArray()) node.Children.Add(Convert($"[{i++}]", e, ref n));
                    node.Count = node.Children.Count;
                    break;
                case JsonValueKind.String: node.Scalar = el.GetString(); break;
                default: node.Scalar = el.GetRawText(); break;
            }
            return node;
        }
    }

    private static TreeViewItem MakeItem(JNode node)
    {
        var item = new TreeViewItem { Tag = node };
        item.Header = BuildHeader(node);

        if (node.Children is { Count: > 0 })
        {
            // Placeholder so the expander chevron shows; replaced on first expand.
            item.Items.Add("…loading");
            item.Expanded += OnNodeExpanded;
        }
        return item;
    }

    private static void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        // Expanded bubbles up the tree — only act when this handler's own item is the source.
        if (sender is not TreeViewItem tvi) return;
        if (tvi.Tag is not JNode node) return;
        if (tvi.Items.Count != 1 || tvi.Items[0] is not string) return; // already materialised
        tvi.Items.Clear();
        if (node.Children is null) return;
        foreach (var child in node.Children) tvi.Items.Add(MakeItem(child));
        e.Handled = true;
    }

    private static StackPanel BuildHeader(JNode node)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = node.Name, Foreground = KeyBrush });
        switch (node.Kind)
        {
            case JsonValueKind.Object:
                header.Children.Add(new TextBlock { Text = $"  {{{node.Count}}}", Foreground = MetaBrush }); break;
            case JsonValueKind.Array:
                header.Children.Add(new TextBlock { Text = $"  [{node.Count}]", Foreground = MetaBrush }); break;
            case JsonValueKind.String:
                header.Children.Add(new TextBlock { Text = " : ", Foreground = MetaBrush });
                header.Children.Add(new TextBlock { Text = $"\"{Truncate(node.Scalar)}\"", Foreground = StringBrush }); break;
            case JsonValueKind.Number:
                header.Children.Add(new TextBlock { Text = " : ", Foreground = MetaBrush });
                header.Children.Add(new TextBlock { Text = node.Scalar ?? "", Foreground = NumberBrush }); break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                header.Children.Add(new TextBlock { Text = " : ", Foreground = MetaBrush });
                header.Children.Add(new TextBlock { Text = node.Scalar ?? "", Foreground = KeywordBrush }); break;
        }
        return header;
    }

    private static string Truncate(string? s) => s is null ? "" : (s.Length > 200 ? s[..200] + "…" : s);

    private void OnTreeExpandAll(object sender, RoutedEventArgs e)
    {
        // Expand-all on huge trees is the very thing that froze the UI.
        // Cap the work and warn rather than walking everything.
        const int Cap = 2000;
        int expanded = 0;
        bool hitCap = ExpandRecursive(TvJson.Items, ref expanded, Cap);
        TxtTreeStats.Text = hitCap
            ? $"expanded first {expanded} nodes (cap reached — expand sub-trees manually)"
            : $"expanded {expanded} nodes";
    }

    private void OnTreeCollapseAll(object sender, RoutedEventArgs e)
    {
        foreach (var o in TvJson.Items)
            if (o is TreeViewItem tvi) CollapseRecursive(tvi);
    }

    private static bool ExpandRecursive(System.Collections.IEnumerable items, ref int count, int cap)
    {
        foreach (var o in items)
        {
            if (o is not TreeViewItem tvi) continue;
            if (count >= cap) return true;
            tvi.IsExpanded = true; // triggers OnNodeExpanded which materialises children
            count++;
            if (ExpandRecursive(tvi.Items, ref count, cap)) return true;
        }
        return false;
    }

    private static void CollapseRecursive(TreeViewItem tvi)
    {
        foreach (var o in tvi.Items) if (o is TreeViewItem c) CollapseRecursive(c);
        tvi.IsExpanded = false;
    }

    private void ApplyAuthInputs()
    {
        _auth.Mode = RbUser.IsChecked == true ? AuthService.AuthMode.User : AuthService.AuthMode.AppOnly;
        _auth.TenantId = string.IsNullOrWhiteSpace(TbTenant.Text) ? "common" : TbTenant.Text.Trim();
        _auth.PublicClientId = string.IsNullOrWhiteSpace(TbPublicClientId.Text)
            ? "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
            : TbPublicClientId.Text.Trim();
        _auth.AppOnlyClientId = TbAppClientId.Text.Trim();
        _auth.AppOnlyClientSecret = PbAppSecret.Password;
    }
}