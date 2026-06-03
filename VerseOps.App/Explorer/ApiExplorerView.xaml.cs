using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VerseOps.App.Api;
using VerseOps.App.Auth;
using VerseOps.App.Configuration;
using VerseOps.App.Inventory.Services;
using VerseOps.App.Sdk;

namespace VerseOps.App.Explorer;

/// <summary>
/// Self-contained "API Explorer" surface restored from the May 2026 prune
/// (commit 7118b59). Hosts the REST + SDK tree, request/response panes,
/// dynamic Form, and Register-SP / Sign-out auth controls.
///
/// Holds its OWN <see cref="AuthService"/> instance so flipping auth mode
/// (User / App-only) from this tab does not affect the silent delegated
/// auth pipeline used by the Inventory dashboard.
/// </summary>
public partial class ApiExplorerView : UserControl
{
    private readonly AuthService _auth = new();
    private readonly ApiExecutor _executor;
    private readonly SdkExecutor _sdkExecutor;
    private SqliteCatalog? _sqlite;
    private ApiOperation? _selected;
    private SdkOp? _selectedSdk;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // Captured from the most recent response so the header-bar follow-up
    // buttons (Poll op / Delete this env) know what to operate on without
    // forcing the user to copy/paste between panes.
    private string? _lastOperationLocation;
    private string? _lastResponseScope;

    // Response search state. The same search bar drives two behaviors:
    //  - Response body tab: incremental Find-next (cycles through occurrences in TbResponse.Text).
    //  - Response tree  tab: live filter (only nodes whose name OR scalar contains the term are
    //    rendered, with their ancestors). We cache the parsed JNode root so re-filtering after
    //    each keystroke does not require re-parsing the JSON.
    private JNode? _lastJsonRoot;
    private int _lastJsonTotalNodes;
    private int _bodyFindCursor;
    private string? _bodyFindTermLower;
    private DateTime _busyStartedUtc;
    private bool _authCollapsedOnce;

    public ApiExplorerView()
    {
        InitializeComponent();
        _executor = new ApiExecutor(_auth);
        _sdkExecutor = new SdkExecutor(_auth);

        // Hydrate auth panel from persisted settings so the user doesn't have
        // to paste their tenant/client id every launch. Empty fields fall back
        // to AppConstants defaults via ApplyAuthInputs at sign-in time.
        TbTenant.Text         = AppSettings.Current.TenantId;
        TbPublicClientId.Text = AppSettings.Current.PublicClientId;
        TbAppClientId.Text    = AppSettings.Current.AppOnlyClientId;

        CbMethod.SelectedIndex = 0;
        BuildOperationsTree();
        TxtSurfaceNotice.Text = PpacNotice;
        UpdateAuthState();
        _elapsedTimer.Tick += (_, _) =>
            TxtElapsed.Text = $"{(DateTime.UtcNow - _busyStartedUtc).TotalSeconds:0.0}s";
    }

    /// <summary>
    /// Wire the host window handle so any rare WAM-broker prompt parents
    /// correctly. Call this from MainWindow after the explorer is created.
    /// </summary>
    public void SetWindowHandleProvider(Func<IntPtr> handleProvider)
    {
        _auth.WindowHandleProvider = handleProvider;
    }

    /// <summary>
    /// Pre-populate the Environment dropdown cache from the shared
    /// inventory SQLite catalog. Optional — without this, the user must
    /// click "Load environments" first.
    /// </summary>
    public void SeedFromCatalog(SqliteCatalog sqliteCatalog)
    {
        _sqlite = sqliteCatalog;
        TrySeedEnvCacheFromSqlite();
    }

    private void TrySeedEnvCacheFromSqlite()
    {
        try
        {
            if (_sqlite == null) return;
            var envs = _sqlite.ReadAllEnvironments();
            if (envs is null || envs.Count == 0) return;
            _envCache = envs
                .Select(e => (Id: e.EnvId, DisplayName: e.DisplayName ?? e.EnvId))
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .OrderBy(x => x.DisplayName)
                .ToList();
        }
        catch { /* best-effort seed */ }
    }

    // ---------- Operations tree ----------

    private void BuildOperationsTree()
    {
        TvOps.Items.Clear();
        var filter = TbTreeFilter?.Text?.Trim();
        if (RbModeSdk?.IsChecked == true) { BuildSdkTree(filter); return; }
        var ops = ApiCatalog.ForSurface(ApiSurface.Ppac)
            .Where(o => MatchesFilter(o, filter));
        foreach (var grp in ops.GroupBy(o => o.Category).OrderBy(g => g.Key))
        {
            var node = new TreeViewItem { Header = grp.Key, IsExpanded = !string.IsNullOrEmpty(filter) };
            var subGroups = grp.GroupBy(o => o.SubCategory ?? string.Empty).ToList();
            var hasSub = subGroups.Any(s => !string.IsNullOrEmpty(s.Key));
            if (hasSub)
            {
                foreach (var sub in subGroups.OrderBy(s => s.Key))
                {
                    if (string.IsNullOrEmpty(sub.Key))
                    {
                        foreach (var op in sub.OrderBy(o => o.Name)) node.Items.Add(MakeOpLeaf(op));
                        continue;
                    }
                    var subNode = new TreeViewItem { Header = sub.Key, IsExpanded = !string.IsNullOrEmpty(filter) };
                    foreach (var op in sub.OrderBy(o => o.Name))
                        subNode.Items.Add(MakeOpLeaf(op));
                    node.Items.Add(subNode);
                }
            }
            else
            {
                foreach (var op in grp.OrderBy(o => o.Name)) node.Items.Add(MakeOpLeaf(op));
            }
            TvOps.Items.Add(node);
        }
    }

    private static bool MatchesFilter(ApiOperation op, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return op.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || op.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (op.SubCategory != null && op.SubCategory.Contains(filter, StringComparison.OrdinalIgnoreCase))
            || op.UrlTemplate.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static TreeViewItem MakeOpLeaf(ApiOperation op)
    {
        var item = new TreeViewItem
        {
            Header = $"{op.HttpMethod}  {op.Name}",
            Tag = op
        };
        var docUrl = ExtractDocUrl(op.Description);
        var menu = new ContextMenu();
        if (!string.IsNullOrEmpty(docUrl))
        {
            var open = new MenuItem { Header = "Open documentation" };
            open.Click += (_, _) => OpenUrl(docUrl);
            menu.Items.Add(open);
        }
        var copyUrl = new MenuItem { Header = "Copy URL template" };
        copyUrl.Click += (_, _) => { try { Clipboard.SetText(op.UrlTemplate); } catch { } };
        menu.Items.Add(copyUrl);
        item.ContextMenu = menu;
        if (!string.IsNullOrEmpty(docUrl))
        {
            item.MouseDoubleClick += (s, ev) =>
            {
                if (s is TreeViewItem tvi && tvi.IsSelected) { OpenUrl(docUrl); ev.Handled = true; }
            };
            item.ToolTip = $"Double-click or right-click → Open documentation\n{docUrl}";
        }
        return item;
    }

    private static string? ExtractDocUrl(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var idx = description.IndexOf("Docs:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = description.Substring(idx + 5).TrimStart();
        var end = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '|' });
        return end < 0 ? rest : rest.Substring(0, end);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void OnTreeFilterChanged(object sender, TextChangedEventArgs e) => BuildOperationsTree();

    /// <summary>
    /// Swallow the per-item RequestBringIntoView so selecting a deeply nested
    /// node doesn't scroll the TreeView's horizontal ScrollViewer to the
    /// item's left edge. WPF's default fires this whenever a TreeViewItem
    /// gains keyboard focus / selection and the resulting horizontal jump
    /// hides the path text the user just clicked on.
    /// </summary>
    private void OnTreeItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }

    private void OnTreeModeChanged(object sender, RoutedEventArgs e)
    {
        if (TxtSurfaceNotice == null || TvOps == null) return;
        var sdk = RbModeSdk?.IsChecked == true;
        TxtSurfaceNotice.Text = sdk
            ? "SDK mode — tree built by reflecting Microsoft.PowerPlatform.Management.dll. Selecting a node shows the C# RequestBuilder method signature; Send invokes it via Kiota using the same auth as REST mode. Indexed nodes (e.g. Environments[environmentId]) require the value in the Form tab."
            : PpacNotice;
        _selected = null; _selectedSdk = null;
        BuildOperationsTree();
    }

    private void BuildSdkTree(string? filter)
    {
        IReadOnlyList<SdkOp> ops;
        try { ops = SdkCatalog.Operations; }
        catch (Exception ex)
        {
            TvOps.Items.Add(new TreeViewItem { Header = $"(SDK reflection failed: {ex.GetType().Name}: {ex.Message})" });
            return;
        }
        if (ops.Count == 0)
        {
            TvOps.Items.Add(new TreeViewItem { Header = "(No SDK operations discovered — check SdkCatalog reflection)" });
            return;
        }
        TxtSurfaceNotice.Text += $"\r\nDiscovered {ops.Count} SDK ops.";
        var root = new TreeNode("");
        foreach (var op in ops)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                var hay = op.PathText + " " + op.Method.Name + " " + op.SignatureText;
                if (!hay.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            }
            var node = root;
            for (int i = 0; i < op.Path.Count; i++)
            {
                var step = op.Path[i];
                string key;
                if (step.IsIndexer && string.Equals(step.PropertyName, "Item", StringComparison.Ordinal)
                    && i > 0 && !op.Path[i - 1].IsIndexer
                    && node.Name == op.Path[i - 1].PropertyName)
                {
                    var newName = $"{node.Name}[{step.FriendlyParamName}]";
                    if (!ReferenceEquals(node, root) && node.Name != newName)
                    {
                        var parent = node.Parent!;
                        if (!parent.Children.TryGetValue(newName, out var renamed))
                        {
                            renamed = new TreeNode(newName) { Parent = parent };
                            parent.Children[newName] = renamed;
                        }
                        node = renamed;
                    }
                    continue;
                }
                key = step.IsIndexer ? $"{step.PropertyName}[{step.FriendlyParamName}]" : step.PropertyName;
                if (!node.Children.TryGetValue(key, out var child))
                {
                    child = new TreeNode(key) { Parent = node };
                    node.Children[key] = child;
                }
                node = child;
            }
            node.Ops.Add(op);
        }
        foreach (var top in root.Children.Values.OrderBy(c => c.Name))
            TvOps.Items.Add(BuildTreeViewItem(top, expand: !string.IsNullOrEmpty(filter)));
    }

    private static TreeViewItem BuildTreeViewItem(TreeNode node, bool expand)
    {
        var tvi = new TreeViewItem { Header = node.Name, IsExpanded = expand };
        foreach (var child in node.Children.Values.OrderBy(c => c.Name))
            tvi.Items.Add(BuildTreeViewItem(child, expand));
        foreach (var op in node.Ops.OrderBy(o => o.HttpMethod))
        {
            var leaf = new TreeViewItem
            {
                Header = $"{op.HttpMethod}  {op.Method.Name}  — {op.SignatureText}",
                Tag = op,
                ToolTip = op.PathText
            };
            tvi.Items.Add(leaf);
        }
        return tvi;
    }

    private sealed class TreeNode
    {
        public string Name { get; }
        public TreeNode? Parent { get; set; }
        public SortedDictionary<string, TreeNode> Children { get; } = new(StringComparer.Ordinal);
        public List<SdkOp> Ops { get; } = new();
        public TreeNode(string name) { Name = name; }
    }

    private const string PpacNotice =
        "PPAC surface (preview). api.powerplatform.com is the new control plane and the\r\n" +
        "long-term replacement for BAP. Many routes return RouteNotFound today; that is\r\n" +
        "expected during preview. Edit the URL / body inline and re-Send to experiment.\r\n" +
        "BAP routes shown alongside (e.g. Create Environment) are the documented fallback\r\n" +
        "for capabilities PPAC has not yet replaced.";

    // ---------- Auth panel ----------

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

        if (!_authCollapsedOnce && _auth.LastSignedInUser != null && AuthExpander != null)
        {
            AuthExpander.IsExpanded = false;
            _authCollapsedOnce = true;
        }
    }

    private void ApplyAuthInputs()
    {
        _auth.Mode = RbUser.IsChecked == true ? AuthService.AuthMode.User : AuthService.AuthMode.AppOnly;
        _auth.TenantId = string.IsNullOrWhiteSpace(TbTenant.Text) ? AppConstants.DefaultTenant : TbTenant.Text.Trim();
        _auth.PublicClientId = string.IsNullOrWhiteSpace(TbPublicClientId.Text)
            ? AppConstants.AzureCliPublicClientId
            : TbPublicClientId.Text.Trim();
        _auth.AppOnlyClientId = TbAppClientId.Text.Trim();
        _auth.AppOnlyClientSecret = PbAppSecret.Password;
    }

    private void OnSaveAuthDefaults(object sender, RoutedEventArgs e)
    {
        // Persist Tenant + Public client id + App client id to
        // %LOCALAPPDATA%\VerseOps\appsettings.json. Secrets (PbAppSecret) are
        // intentionally NOT saved — they live in memory only.
        try
        {
            AppSettings.Current.TenantId        = string.IsNullOrWhiteSpace(TbTenant.Text) ? AppConstants.DefaultTenant : TbTenant.Text.Trim();
            AppSettings.Current.PublicClientId  = string.IsNullOrWhiteSpace(TbPublicClientId.Text) ? AppConstants.AzureCliPublicClientId : TbPublicClientId.Text.Trim();
            AppSettings.Current.AppOnlyClientId = TbAppClientId.Text.Trim();
            AppSettings.Current.Save();
            TxtStatus.Text = $"Saved defaults to {AppSettings.UserSettingsPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings.\n\nPath: {AppSettings.UserSettingsPath}\n\n{ex.GetType().Name}: {ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Operation selection ----------

    private void OnOperationSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: SdkOp sop })
        {
            _selected = null;
            _selectedSdk = sop;
            CbMethod.SelectedItem = CbMethod.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Content, sop.HttpMethod, StringComparison.OrdinalIgnoreCase))
                ?? CbMethod.Items[0];
            CbScope.SelectedItem = CbScope.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Content == "https://api.powerplatform.com/.default") ?? CbScope.Items[0];
            TbUrl.Text = sop.PathText + "   // SDK call";
            TbBody.Text = BuildSdkBodyTemplate(sop);
            TbDescription.Text = DescribeSdkOp(sop);
            TxtStatus.Text = $"SDK: {sop.PathText}.{sop.Method.Name}";
            BuildSdkForm(sop);
            BuildReturnTypeTree(sop, populatedJson: null);
            return;
        }
        if (e.NewValue is TreeViewItem { Tag: ApiOperation op })
        {
            _selected = op;
            _selectedSdk = null;
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

    // ---------- SDK return-type tree ----------

    private void BuildReturnTypeTree(SdkOp op, string? populatedJson)
    {
        TvReturn.Items.Clear();
        var ret = UnwrapTask(op.Method.ReturnType);
        TxtReturnTypeHeader.Text = $"{op.Method.Name}() -> {FriendlyName(ret)}"
            + (populatedJson == null ? "  (schema only — press Send to populate values)" : "  (with response values)");
        if (ret == typeof(void)) return;
        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(populatedJson))
        {
            try { root = JsonDocument.Parse(populatedJson).RootElement.Clone(); } catch { }
        }
        var node = BuildReturnNode("(return)", ret, root, depth: 0, maxDepth: 4, visited: new HashSet<Type>());
        TvReturn.Items.Add(node);
        node.IsExpanded = true;
    }

    private static TreeViewItem BuildReturnNode(string name, Type t, JsonElement? value, int depth, int maxDepth, HashSet<Type> visited)
    {
        var typeText = FriendlyName(t);
        string valueText = "";
        if (value.HasValue)
        {
            valueText = value.Value.ValueKind switch
            {
                JsonValueKind.String => $"  =  \"{value.Value.GetString()}\"",
                JsonValueKind.Number => $"  =  {value.Value.GetRawText()}",
                JsonValueKind.True or JsonValueKind.False => $"  =  {value.Value.GetRawText()}",
                JsonValueKind.Null => "  =  null",
                JsonValueKind.Array => $"  =  [{value.Value.GetArrayLength()} items]",
                JsonValueKind.Object => "",
                _ => ""
            };
        }
        var tvi = new TreeViewItem { Header = $"{name} : {typeText}{valueText}" };
        if (depth >= maxDepth) return tvi;
        var elemType = t;
        bool isCollection = false;
        if (t.IsGenericType)
        {
            var gd = t.GetGenericTypeDefinition();
            if (gd == typeof(List<>) || gd == typeof(IList<>) || gd == typeof(IEnumerable<>) || gd == typeof(ICollection<>))
            { elemType = t.GetGenericArguments()[0]; isCollection = true; }
        }
        if (isCollection && value?.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var v in value.Value.EnumerateArray().Take(5))
                tvi.Items.Add(BuildReturnNode($"[{i++}]", elemType, v, depth + 1, maxDepth, visited));
            if (value.Value.GetArrayLength() > 5)
                tvi.Items.Add(new TreeViewItem { Header = $"… ({value.Value.GetArrayLength() - 5} more)" });
            return tvi;
        }
        if (isCollection)
        {
            tvi.Items.Add(BuildReturnNode("[item]", elemType, null, depth + 1, maxDepth, visited));
            return tvi;
        }
        if (t.IsPrimitive || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t.IsEnum) return tvi;
        if (!visited.Add(t)) return tvi;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(60).ToList();
        foreach (var p in props)
        {
            JsonElement? childVal = null;
            if (value?.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in value.Value.EnumerateObject())
                {
                    if (string.Equals(kv.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    { childVal = kv.Value; break; }
                }
            }
            tvi.Items.Add(BuildReturnNode(p.Name, p.PropertyType, childVal, depth + 1, maxDepth, visited));
        }
        return tvi;
    }

    private static string DescribeSdkOp(SdkOp op)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PATH:  {op.PathText}");
        sb.AppendLine($"VERB:  {op.HttpMethod}   ({op.Method.Name})");
        sb.AppendLine($"BUILDER: {op.BuilderType.FullName}");
        sb.AppendLine();
        sb.AppendLine("--- INPUT PARAMETERS ---");
        var ps = op.Method.GetParameters();
        if (ps.Length == 0) sb.AppendLine("  (none)");
        foreach (var p in ps)
        {
            string role = p.ParameterType == typeof(CancellationToken) ? "cancellation"
                : p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>) ? "request configuration"
                : "BODY";
            sb.AppendLine($"  {p.Name}  :  {FriendlyName(p.ParameterType)}   [{role}]");
        }
        var indexers = op.Path.Where(s => s.IsIndexer).ToList();
        if (indexers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- URL INDEXERS (set in Form tab) ---");
            foreach (var s in indexers)
                sb.AppendLine($"  {s.IndexParamName}  :  string   [path token]");
        }
        sb.AppendLine();
        sb.AppendLine("--- OUTPUT ---");
        var ret = UnwrapTask(op.Method.ReturnType);
        sb.AppendLine($"  Return: {FriendlyName(ret)}");
        return sb.ToString();
    }

    private static Type UnwrapTask(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            return t.GetGenericArguments()[0];
        if (t == typeof(Task)) return typeof(void);
        return t;
    }

    private static string FriendlyName(Type t)
    {
        if (t == typeof(void)) return "void";
        if (t.IsGenericType)
        {
            var n = t.GetGenericTypeDefinition().Name;
            var i = n.IndexOf('`');
            if (i >= 0) n = n.Substring(0, i);
            var inner = string.Join(", ", t.GetGenericArguments().Select(FriendlyName));
            return $"{n}<{inner}>";
        }
        return t.Name;
    }

    private static string BuildSdkBodyTemplate(SdkOp op)
    {
        if (op.BodyType == null || op.BodyType == typeof(CancellationToken)) return string.Empty;
        // Activator.CreateInstance + Serialize gives a useless all-nulls payload (or {} for Kiota
        // models that ignore nulls). Walk the writable scalar properties instead so the user sees
        // "displayName": "" as a real field they can fill in. Nested object/collection properties
        // are emitted as {} / [] placeholders.
        try
        {
            return BuildSkeletonJson(op.BodyType, depth: 0, maxDepth: 3);
        }
        catch
        {
            return $"// SDK body type: {op.BodyType.FullName}{Environment.NewLine}{{}}";
        }
    }

    private static string BuildSkeletonJson(Type t, int depth, int maxDepth)
    {
        var sb = new StringBuilder();
        WriteSkeleton(sb, t, depth, maxDepth, indent: 0);
        return sb.ToString();
    }

    private static void WriteSkeleton(StringBuilder sb, Type t, int depth, int maxDepth, int indent)
    {
        var pad = new string(' ', indent * 2);
        var inner = new string(' ', (indent + 1) * 2);
        if (depth > maxDepth) { sb.Append("{}"); return; }
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToList();
        if (props.Count == 0) { sb.Append("{}"); return; }
        sb.AppendLine("{");
        for (int i = 0; i < props.Count; i++)
        {
            var p = props[i];
            var jsonName = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
            sb.Append(inner).Append('"').Append(jsonName).Append("\": ");
            WriteValuePlaceholder(sb, p.PropertyType, depth + 1, maxDepth, indent + 1);
            if (i < props.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.Append(pad).Append('}');
    }

    private static void WriteValuePlaceholder(StringBuilder sb, Type t, int depth, int maxDepth, int indent)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string) || u == typeof(Guid)) { sb.Append("\"\""); return; }
        if (u == typeof(bool)) { sb.Append("false"); return; }
        if (u.IsPrimitive || u == typeof(decimal)) { sb.Append('0'); return; }
        if (u == typeof(DateTime) || u == typeof(DateTimeOffset)) { sb.Append("\"\""); return; }
        if (u.IsEnum) { sb.Append('"').Append(Enum.GetNames(u).FirstOrDefault() ?? "").Append('"'); return; }
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(u) && u != typeof(string))
        { sb.Append("[]"); return; }
        // nested Kiota model — recurse one level
        WriteSkeleton(sb, u, depth, maxDepth, indent);
    }

    // ---------- Form (REST + SDK) ----------

    private readonly Dictionary<string, FrameworkElement> _formInputs = new();
    private List<(string Id, string DisplayName)>? _envCache;
    private List<(string Id, string DisplayName)>? _groupCache;
    private List<(string Id, string DisplayName)>? _dlpCache;
    private List<(string Id, string DisplayName)>? _billingCache;

    private void BuildSdkForm(SdkOp op)
    {
        _formInputs.Clear();
        GridForm.Children.Clear();
        GridForm.RowDefinitions.Clear();
        GridForm.ColumnDefinitions.Clear();
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var indexers = op.Path.Where(s => s.IsIndexer).ToList();
        var uniqueSlots = indexers
            .GroupBy(s => s.SlotKey)
            .Select(g => g.First())
            .ToList();
        bool needEnv = uniqueSlots.Any(s => LooksLikeEnv(s.FriendlyParamName));
        bool needGroup = uniqueSlots.Any(s => LooksLikeGroup(s.FriendlyParamName));
        bool needBilling = uniqueSlots.Any(s => LooksLikeBilling(s.FriendlyParamName));
        BtnLoadEnvs.Visibility    = needEnv     ? Visibility.Visible : Visibility.Collapsed;
        BtnLoadGroups.Visibility  = needGroup   ? Visibility.Visible : Visibility.Collapsed;
        BtnLoadDlp.Visibility     = Visibility.Collapsed;
        BtnLoadBilling.Visibility = needBilling ? Visibility.Visible : Visibility.Collapsed;

        if (uniqueSlots.Count == 0)
        {
            TxtFormHint.Text = "This SDK call takes no indexer values. Edit the body in the Raw body tab if needed and press Send.";
            return;
        }
        TxtFormHint.Text = "Provide each indexer value (one per row). Use the Load buttons to populate dropdowns.";
        int row = 0;
        foreach (var step in uniqueSlots)
        {
            GridForm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new Label { Content = step.FriendlyParamName };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            GridForm.Children.Add(lbl);

            FrameworkElement input = LooksLikeEnv(step.FriendlyParamName) ? MakeEnvCombo(null)
                : LooksLikeGroup(step.FriendlyParamName) ? MakeGroupCombo(null)
                : LooksLikeBilling(step.FriendlyParamName) ? MakeCachedCombo(_billingCache, "Load billing", null)
                : (FrameworkElement)MakeText(null);
            input.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            GridForm.Children.Add(input);
            _formInputs[step.SlotKey] = input;
            row++;
        }
    }

    private static bool LooksLikeEnv(string? n)     => n != null && n.Contains("environment", StringComparison.OrdinalIgnoreCase) && !n.Contains("group", StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeGroup(string? n)   => n != null && n.Contains("group", StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeBilling(string? n) => n != null && n.Contains("billing", StringComparison.OrdinalIgnoreCase);

    private void BuildForm(ApiOperation op)
    {
        _formInputs.Clear();
        GridForm.Children.Clear();
        GridForm.RowDefinitions.Clear();
        GridForm.ColumnDefinitions.Clear();
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ps = op.Parameters;
        UpdateLoadButtonVisibility(ps);
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

    private void UpdateLoadButtonVisibility(IReadOnlyList<OpParam>? ps)
    {
        bool needEnv = false, needGroup = false, needDlp = false, needBilling = false;
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
        BtnLoadEnvs.Visibility    = needEnv     ? Visibility.Visible : Visibility.Collapsed;
        BtnLoadGroups.Visibility  = needGroup   ? Visibility.Visible : Visibility.Collapsed;
        BtnLoadDlp.Visibility     = needDlp     ? Visibility.Visible : Visibility.Collapsed;
        BtnLoadBilling.Visibility = needBilling ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ComboBox MakeChoice(IReadOnlyList<string> choices, string? def)
    {
        var cb = new ComboBox { IsEditable = true };
        foreach (var c in choices) cb.Items.Add(c);
        cb.Text = def ?? (choices.Count > 0 ? choices[0] : "");
        EnableSubstringFilter(cb);
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
        EnableSubstringFilter(cb);
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
        EnableSubstringFilter(cb);
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
        EnableSubstringFilter(cb);
        return cb;
    }

    // Turn an editable ComboBox into a substring-filtered picker: as the user
    // types, the dropdown narrows to items whose display text contains the
    // entered query (case-insensitive), instead of WPF's default "jump to
    // first item starting with the typed character" TextSearch behavior.
    private static void EnableSubstringFilter(ComboBox cb)
    {
        cb.IsTextSearchEnabled = false;
        cb.StaysOpenOnEdit = true;
        // Cap popup height so a 700-item dropdown doesn't fill the screen.
        if (double.IsNaN(cb.MaxDropDownHeight) || cb.MaxDropDownHeight > 360)
            cb.MaxDropDownHeight = 360;

        // Move items off the local Items collection onto an ItemsSource so we
        // can drive filtering through ICollectionView without rebuilding the
        // visual list on every keystroke.
        var snapshot = new List<object>(cb.Items.Count);
        foreach (var it in cb.Items) snapshot.Add(it);
        cb.Items.Clear();
        cb.ItemsSource = snapshot;
        var view = CollectionViewSource.GetDefaultView(cb.ItemsSource);

        // Suppress filter-refresh when the text change is caused by the user
        // *picking* an item (mouse click / Enter on a highlighted row), so the
        // dropdown doesn't re-open on top of the just-made selection.
        bool suppress = false;
        cb.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count == 0) return;
            suppress = true;
            view.Filter = null;
            cb.Dispatcher.BeginInvoke(new Action(() => suppress = false), DispatcherPriority.Background);
        };

        // Debounce keystrokes — re-filtering 700 items + re-laying the popup
        // on every key fires a layout storm on slower machines.
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        string lastQuery = string.Empty;

        // PART_EditableTextBox doesn't exist until the template applies, which
        // happens on Loaded for items added after construction.
        RoutedEventHandler? hookup = null;
        hookup = (_, __) =>
        {
            cb.Loaded -= hookup;
            if (cb.Template.FindName("PART_EditableTextBox", cb) is not TextBox tb) return;

            debounce.Tick += (_, _) =>
            {
                debounce.Stop();
                if (suppress) return;
                var q = (tb.Text ?? string.Empty).Trim();
                if (string.Equals(q, lastQuery, StringComparison.Ordinal)) return;
                lastQuery = q;

                bool wasFiltered = view.Filter != null;
                view.Filter = string.IsNullOrEmpty(q)
                    ? null
                    : o =>
                    {
                        var s = o switch
                        {
                            ComboBoxItem ci => ci.Content?.ToString() ?? string.Empty,
                            _ => o?.ToString() ?? string.Empty
                        };
                        return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    };

                // Only force-open the dropdown when the filter just turned on
                // (null -> non-null). After that, let the user's normal
                // dropdown gesture govern open/close so we don't fight them.
                if (!wasFiltered && view.Filter != null && !cb.IsDropDownOpen)
                    cb.IsDropDownOpen = true;
            };

            tb.TextChanged += (_, _) =>
            {
                if (suppress) return;
                debounce.Stop();
                debounce.Start();
            };
        };
        cb.Loaded += hookup;
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

    // ---------- Dropdown loaders ----------

    private async void OnLoadEnvironments(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAuthInputs();
            BeginBusy("Loading environments...");
            var ct = _cts!.Token;
            var url   = "https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview";
            var scope = ApiCatalog.ScopePpac;
            var result = await Task.Run(() => _executor.ExecuteAsync("GET", url, null, scope, ct), ct);
            var envs = new List<(string Id, string DisplayName)>();
            using var doc = JsonDocument.Parse(result.ResponseBody);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var env in arr.EnumerateArray())
                {
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
                ? $"Loaded {_envCache.Count} environments (PPAC)."
                : $"No environments returned. HTTP {result.StatusCode}. Check Body tab for details.";
            if (_selected != null) BuildForm(_selected);
            if (_selectedSdk != null) BuildSdkForm(_selectedSdk);
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
            if (_selectedSdk != null) BuildSdkForm(_selectedSdk);
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
            if (_selectedSdk != null) BuildSdkForm(_selectedSdk);
        }
        catch (OperationCanceledException) { TxtFormStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtFormStatus.Text = $"Load failed: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private static string? TryStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? TryGuidFromArmId(JsonElement el)
    {
        var s = TryStr(el, "id");
        if (string.IsNullOrEmpty(s)) return null;
        var tail = s.TrimEnd('/').Split('/').Last();
        return Guid.TryParse(tail, out _) ? tail : null;
    }

    // ---------- Send / Decode / Cancel ----------

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_selectedSdk != null) { await SendSdkAsync(); return; }
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
            var result = await Task.Run(() => _executor.ExecuteAsync(method, url, body, scope, ct), ct);

            TbResponse.Text = result.ResponseBody;
            RenderJsonTree(result.ResponseBody);
            TbHeaders.Text = string.Join(Environment.NewLine,
                result.ResponseHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
            TxtRespMeta.Text = $"{result.StatusCode} {result.ReasonPhrase}   {result.ElapsedMs} ms"
                               + (result.CorrelationId is null ? "" : $"   correlation={result.CorrelationId}")
                               + (result.OperationLocation is null ? "" : $"   op-location={result.OperationLocation}");
            TxtStatus.Text = $"Done. HTTP {result.StatusCode}.";
            SurfaceFollowUps(result, scope);
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

    // Extracts the GUID-shaped environment id from a response body. Matches the
    // same shapes the SDK test rig handles: {"name":"<guid>"}, /environments/<guid>
    // in any id/path field, or {"links":{"environment":{"path":".../environments/<guid>"}}}.
    private static string? TryExtractEnvId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var path = Regex.Match(body, "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
        if (path.Success) return path.Groups["id"].Value;
        var name = Regex.Match(body, "\"name\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"", RegexOptions.IgnoreCase);
        if (name.Success) return name.Groups["id"].Value;
        return null;
    }

    // Decides which header-bar follow-up buttons should appear based on the last response.
    private void SurfaceFollowUps(ApiCallResult result, string scope)
    {
        _lastOperationLocation = result.OperationLocation;
        _lastResponseScope = scope;

        // Poll op: visible whenever the response carried operation-location (typical 202).
        BtnPollOpLocation.Visibility = string.IsNullOrEmpty(result.OperationLocation)
            ? Visibility.Collapsed : Visibility.Visible;

        // Env id capture: visible when the body contains a GUID we can plausibly delete.
        var capturedEnvId = TryExtractEnvId(result.ResponseBody);
        var show = !string.IsNullOrEmpty(capturedEnvId);
        TxtCapturedEnvLabel.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        TbCapturedEnvId.Visibility        = show ? Visibility.Visible : Visibility.Collapsed;
        BtnDeleteCapturedEnv.Visibility   = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) TbCapturedEnvId.Text = capturedEnvId!;
    }

    // "Poll op" — re-GETs the operation-location captured from the last response and
    // dumps the operation status into the response panes (so you can watch a long-running
    // create/copy/restore march to Succeeded without typing the URL by hand).
    private async void OnPollOpLocation(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOperationLocation))
        {
            TxtStatus.Text = "No operation-location captured from the last response.";
            return;
        }
        var scope = _lastResponseScope ?? ApiCatalog.ScopePpac;
        try
        {
            ApplyAuthInputs();
            BeginBusy($"Polling {_lastOperationLocation} ...");
            var ct = _cts!.Token;
            var result = await Task.Run(() => _executor.ExecuteAsync("GET", _lastOperationLocation!, null, scope, ct), ct);
            TbResponse.Text = result.ResponseBody;
            RenderJsonTree(result.ResponseBody);
            TbHeaders.Text = string.Join(Environment.NewLine,
                result.ResponseHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
            TxtRespMeta.Text = $"POLL {result.StatusCode} {result.ReasonPhrase}   {result.ElapsedMs} ms"
                               + (result.CorrelationId is null ? "" : $"   correlation={result.CorrelationId}");
            TxtStatus.Text = $"Poll done. HTTP {result.StatusCode}.";
            // Re-evaluate follow-ups against the polled body — envId often only appears here.
            SurfaceFollowUps(result, scope);
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtStatus.Text = $"Poll error: {ex.Message}"; }
        finally { EndBusy(); }
    }

    // "Delete this env" — fires DELETE /environments/{id}?api-version=2024-10-01 against
    // whatever is currently in TbCapturedEnvId.Text (the user can edit it first). Confirms
    // before sending; PPAC soft-deletes, so the env stays recoverable for 7 days.
    private async void OnDeleteCapturedEnv(object sender, RoutedEventArgs e)
    {
        var envId = TbCapturedEnvId.Text?.Trim();
        if (string.IsNullOrWhiteSpace(envId)) { TxtStatus.Text = "No environment id captured."; return; }
        var ok = MessageBox.Show(
            $"Soft-delete environment {envId}?\n\nDELETE /environmentmanagement/environments/{envId}?api-version=2024-10-01\n\nThe environment is recoverable for 7 days via POST /recover.",
            "Confirm delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        var url = $"https://api.powerplatform.com/environmentmanagement/environments/{envId}?api-version=2024-10-01";
        try
        {
            ApplyAuthInputs();
            BeginBusy($"DELETE /environments/{envId} ...");
            var ct = _cts!.Token;
            var result = await Task.Run(() => _executor.ExecuteAsync("DELETE", url, null, ApiCatalog.ScopePpac, ct), ct);
            TbResponse.Text = result.ResponseBody;
            RenderJsonTree(result.ResponseBody);
            TbHeaders.Text = string.Join(Environment.NewLine,
                result.ResponseHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
            TxtRespMeta.Text = $"DELETE {result.StatusCode} {result.ReasonPhrase}   {result.ElapsedMs} ms"
                               + (result.OperationLocation is null ? "" : $"   op-location={result.OperationLocation}");
            TxtStatus.Text = $"Delete sent. HTTP {result.StatusCode}.";
            SurfaceFollowUps(result, ApiCatalog.ScopePpac);
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Cancelled."; }
        catch (Exception ex) { TxtStatus.Text = $"Delete error: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private async Task SendSdkAsync()
    {
        var op = _selectedSdk!;
        try
        {
            ApplyAuthInputs();
            var values = ReadFormValues();
            BeginBusy($"SDK invoke: {op.PathText}.{op.Method.Name} ...");
            TbResponse.Text = ""; TbHeaders.Text = ""; TxtRespMeta.Text = ""; TvJson.Items.Clear();
            var ct = _cts!.Token;
            var body = string.IsNullOrWhiteSpace(TbBody.Text) ? null : TbBody.Text;
            if (body != null && body.StartsWith("//")) body = string.Join('\n', body.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
            var result = await Task.Run(() => _sdkExecutor.ExecuteAsync(op, values, body, ct), ct);
            TbResponse.Text = result.Body;
            RenderJsonTree(result.Success ? result.Body : "");
            TbHeaders.Text = $"SDK call:  {op.PathText}.{op.Method.Name}\r\n" +
                             $"Verb:      {op.HttpMethod}\r\n" +
                             $"Status:    {result.StatusText}\r\n" +
                             $"Elapsed:   {result.ElapsedMs} ms\r\n" +
                             (result.OperationLocation == null ? "" : $"OpLocation: {result.OperationLocation}\r\n") +
                             (result.CorrelationId    == null ? "" : $"x-ms-correlation-id: {result.CorrelationId}\r\n") +
                             (result.Error == null ? "" : $"Error:     {result.Error}\r\n");
            // Mirror REST mode: surface HTTP status, op-location, correlation id on the meta line.
            // For 202 long-running operations (Recover/Restore/Copy/Backups/ModifySku/Settings/...) the
            // operation-location header is the ONLY way to get the operation id back — lose it and the
            // user has to redo the call to track provisioning.
            var status = result.HttpStatusCode is int sc ? $"HTTP {sc}" : result.StatusText;
            TxtRespMeta.Text = $"{status}   {result.ElapsedMs} ms"
                + (result.OperationLocation == null ? "" : $"   op-location={result.OperationLocation}")
                + (result.CorrelationId    == null ? "" : $"   correlation={result.CorrelationId}")
                + (result.Success ? "" : $"   ({result.Error})");
            TxtStatus.Text = result.Success ? "SDK call succeeded." : $"SDK call failed: {result.Error}";
            UpdateAuthState();
            if (_selectedSdk != null) BuildReturnTypeTree(_selectedSdk, result.Body);
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Cancelled."; TxtRespMeta.Text = "CANCELLED"; }
        catch (Exception ex) { TbResponse.Text = ex.ToString(); TxtRespMeta.Text = "EXCEPTION"; TxtStatus.Text = $"Error: {ex.Message}"; }
        finally { EndBusy(); }
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
        catch (OperationCanceledException) { TxtStatus.Text = "Cancelled."; }
        catch (Exception ex)
        {
            TbResponse.Text = ex.ToString();
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally { EndBusy(); }
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
        BtnCancel.IsEnabled = false;
        Mouse.OverrideCursor = null;
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await _auth.SignOutAsync();
        UpdateAuthState();
        TxtStatus.Text = "Signed out.";
    }

    // ---------- SP registration (one-time, per tenant) ----------

    private async void OnRegisterSp(object sender, RoutedEventArgs e)
    {
        var clientId = TbAppClientId?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            MessageBox.Show(
                "Fill the App-only ClientId field first — that's the SP we will register.\n\n" +
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

            var admin = new AuthService
            {
                Mode = AuthService.AuthMode.User,
                TenantId = tenant,
                PublicClientId = string.IsNullOrWhiteSpace(TbPublicClientId?.Text)
                    ? AppConstants.AzureCliPublicClientId
                    : TbPublicClientId.Text.Trim(),
                WindowHandleProvider = _auth.WindowHandleProvider,
            };

            var bapScope = "https://api.bap.microsoft.com/.default";
            var token = await Task.Run(() => admin.GetTokenAsync(bapScope, ct), ct);

            var url = $"https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform" +
                      $"/adminApplications/{Uri.EscapeDataString(clientId)}?api-version=2020-10-01";
            using var http = new System.Net.Http.HttpClient();
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            TbResponse.Text = body;
            RenderJsonTree(body);
            TbHeaders.Text = $"PUT {url}\nAdmin: {admin.LastSignedInUser}\nHTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            TxtRespMeta.Text = $"SP register   HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";

            if (resp.IsSuccessStatusCode)
            {
                TxtStatus.Text = $"Registered. The SP {clientId} is now a tenant admin management application. Switch auth to App-only to use it.";
                MessageBox.Show(
                    $"Success.\n\nClientId {clientId} is now registered as a tenant admin management application.\n\n" +
                    "Switch the auth radio to App-only and you should be able to call PPAC / BAP admin endpoints with the client secret.",
                    "Registered", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtStatus.Text = $"Registration failed: HTTP {(int)resp.StatusCode}.";
                MessageBox.Show(
                    $"Registration call returned HTTP {(int)resp.StatusCode}.\n\nCommon causes:\n" +
                    "  • Signed-in user is not a Power Platform Administrator.\n" +
                    "  • ClientId does not match an existing App Registration in the tenant.\n" +
                    "  • Tenant id is wrong.\n\nSee response body in the Body tab for details.",
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

    // ---------- JSON tree viewer (lazy) ----------

    private static readonly Brush KeyBrush     = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
    private static readonly Brush StringBrush  = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly Brush NumberBrush  = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
    private static readonly Brush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly Brush MetaBrush    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private sealed class JNode
    {
        public string Name = "";
        public JsonValueKind Kind;
        public string? Scalar;
        public List<JNode>? Children;
        public int Count;
    }

    private async void RenderJsonTree(string? text)
    {
        TvJson.Items.Clear();
        TxtTreeStats.Text = "";
        _lastJsonRoot = null;
        _lastJsonTotalNodes = 0;
        // New response invalidates any in-progress body find cursor.
        _bodyFindCursor = 0;
        if (string.IsNullOrWhiteSpace(text)) { UpdateSearchInfoForCurrentTab(); return; }

        const int MaxBytes = 8 * 1024 * 1024;
        if (text.Length > MaxBytes)
        {
            TvJson.Items.Add(new TreeViewItem { Header = $"(response too large for tree view: {text.Length:N0} chars)", Foreground = MetaBrush });
            UpdateSearchInfoForCurrentTab();
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
            UpdateSearchInfoForCurrentTab();
            return;
        }
        if (root is null) { UpdateSearchInfoForCurrentTab(); return; }

        _lastJsonRoot = root;
        _lastJsonTotalNodes = totalNodes;
        ApplyTreeFilter(TbRespSearch?.Text);
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
            item.Items.Add("…loading");
            item.Expanded += OnNodeExpanded;
        }
        return item;
    }

    private static void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem tvi) return;
        if (tvi.Tag is not JNode node) return;
        if (tvi.Items.Count != 1 || tvi.Items[0] is not string) return;
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
            tvi.IsExpanded = true;
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

    // ---------- Response search (body Find-next + tree live-filter) ----------

    private void OnResponseSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Tree filtering is live; body find resets cursor and rehighlights from the top
        // so the very next Enter/Find-next jumps to the first match for the new term.
        _bodyFindCursor = 0;
        _bodyFindTermLower = TbRespSearch.Text?.ToLowerInvariant();
        if (IsResponseTreeTabActive())
            ApplyTreeFilter(TbRespSearch.Text);
        UpdateSearchInfoForCurrentTab();
    }

    private void OnResponseSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BodyFindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnResponseSearchClear(sender, e);
            e.Handled = true;
        }
    }

    private void OnResponseSearchNext(object sender, RoutedEventArgs e) => BodyFindNext();

    private void OnResponseSearchClear(object sender, RoutedEventArgs e)
    {
        TbRespSearch.Text = "";
        _bodyFindCursor = 0;
        TbResponse.Select(0, 0);
        if (_lastJsonRoot != null) ApplyTreeFilter(null);
        UpdateSearchInfoForCurrentTab();
    }

    private void OnResponseTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Re-apply current term to whatever tab the user just switched to. Avoids
        // showing stale match counts from the previous tab.
        if (!ReferenceEquals(e.OriginalSource, TcResponse)) return;
        if (IsResponseTreeTabActive() && _lastJsonRoot != null)
            ApplyTreeFilter(TbRespSearch?.Text);
        UpdateSearchInfoForCurrentTab();
    }

    private bool IsResponseTreeTabActive()
        => TcResponse?.SelectedItem is TabItem ti
        && ti.Header is string h
        && string.Equals(h, "Response tree", StringComparison.OrdinalIgnoreCase);

    private bool IsResponseBodyTabActive()
        => TcResponse?.SelectedItem is TabItem ti
        && ti.Header is string h
        && string.Equals(h, "Response body", StringComparison.OrdinalIgnoreCase);

    private void BodyFindNext()
    {
        if (TxtRespSearchInfo == null) return;
        var term = TbRespSearch?.Text;
        if (string.IsNullOrEmpty(term)) { TxtRespSearchInfo.Text = ""; return; }
        var body = TbResponse?.Text;
        if (string.IsNullOrEmpty(body)) { TxtRespSearchInfo.Text = "no body"; return; }

        // Switch user to body tab so the highlight is visible.
        if (!IsResponseBodyTabActive() && TcResponse != null)
        {
            foreach (var o in TcResponse.Items)
                if (o is TabItem ti && string.Equals(ti.Header as string, "Response body", StringComparison.OrdinalIgnoreCase))
                { TcResponse.SelectedItem = ti; break; }
        }

        int start = Math.Clamp(_bodyFindCursor, 0, body.Length);
        int idx = body.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
        bool wrapped = false;
        if (idx < 0 && start > 0)
        {
            idx = body.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);
            wrapped = true;
        }
        if (idx < 0)
        {
            TxtRespSearchInfo.Text = "no matches";
            return;
        }
        TbResponse!.Focus();
        TbResponse.Select(idx, term.Length);
        // ScrollToLine on a selection position by computing the line index.
        int lineIndex = TbResponse.GetLineIndexFromCharacterIndex(idx);
        if (lineIndex >= 0) TbResponse.ScrollToLine(lineIndex);
        _bodyFindCursor = idx + term.Length;

        // Count total matches once per term update so the counter is meaningful.
        int total = CountOccurrences(body, term);
        int currentOrdinal = CountOccurrences(body.AsSpan(0, idx).ToString(), term) + 1;
        TxtRespSearchInfo.Text = $"{currentOrdinal} of {total}" + (wrapped ? " (wrapped)" : "");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        { count++; i += needle.Length; }
        return count;
    }

    private void UpdateSearchInfoForCurrentTab()
    {
        if (TxtRespSearchInfo == null) return;
        var term = TbRespSearch?.Text;
        if (string.IsNullOrEmpty(term)) { TxtRespSearchInfo.Text = ""; return; }
        if (IsResponseBodyTabActive())
        {
            var body = TbResponse?.Text ?? "";
            var total = CountOccurrences(body, term);
            TxtRespSearchInfo.Text = total == 0 ? "no matches" : $"{total} match(es)";
        }
        // Tree tab updates TxtTreeStats directly inside ApplyTreeFilter; clear the shared label
        // so we don't show a stale body-tab count.
        else if (IsResponseTreeTabActive())
        {
            TxtRespSearchInfo.Text = "";
        }
    }

    // Rebuilds the JSON tree view from the cached _lastJsonRoot, optionally pruned to
    // subtrees that contain `term` (case-insensitive substring match on Name or Scalar).
    // Matching nodes' ancestors are kept so the path is visible; matching subtrees are
    // auto-expanded. When term is null/empty, falls back to the original lazy-rendered tree.
    private void ApplyTreeFilter(string? term)
    {
        TvJson.Items.Clear();
        if (_lastJsonRoot is null) { TxtTreeStats.Text = ""; return; }
        if (string.IsNullOrWhiteSpace(term))
        {
            var rootItem = MakeItem(_lastJsonRoot);
            rootItem.IsExpanded = true;
            TvJson.Items.Add(rootItem);
            TxtTreeStats.Text = $"{_lastJsonTotalNodes} nodes";
            return;
        }
        int matches = 0;
        var filteredRoot = MakeItemFiltered(_lastJsonRoot, term.ToLowerInvariant(), ref matches);
        if (filteredRoot != null)
        {
            filteredRoot.IsExpanded = true;
            TvJson.Items.Add(filteredRoot);
        }
        TxtTreeStats.Text = matches == 0
            ? $"0 matches of {_lastJsonTotalNodes} nodes"
            : $"{matches} match(es) of {_lastJsonTotalNodes} nodes";
    }

    // Returns null when the subtree contains no match. Otherwise returns a fully-built
    // (not lazy) TreeViewItem containing only matching descendants and their parents.
    private static TreeViewItem? MakeItemFiltered(JNode node, string termLower, ref int matches)
    {
        bool selfMatch =
            (!string.IsNullOrEmpty(node.Name) && node.Name.ToLowerInvariant().Contains(termLower))
            || (!string.IsNullOrEmpty(node.Scalar) && node.Scalar!.ToLowerInvariant().Contains(termLower));

        List<TreeViewItem>? visibleChildren = null;
        if (node.Children is { Count: > 0 })
        {
            foreach (var c in node.Children)
            {
                var ci = MakeItemFiltered(c, termLower, ref matches);
                if (ci != null)
                {
                    (visibleChildren ??= new List<TreeViewItem>()).Add(ci);
                }
            }
        }
        if (!selfMatch && (visibleChildren is null || visibleChildren.Count == 0))
            return null;
        if (selfMatch) matches++;

        var item = new TreeViewItem { Tag = node, Header = BuildHeader(node), IsExpanded = true };
        if (visibleChildren != null)
            foreach (var c in visibleChildren) item.Items.Add(c);
        return item;
    }
}
