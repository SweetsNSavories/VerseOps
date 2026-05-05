using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VerseOps.App.Api;
using VerseOps.App.Auth;
using VerseOps.App.Sdk;

namespace VerseOps.App;

public partial class MainWindow : Window
{
    private readonly AuthService _auth = new();
    private readonly ApiExecutor _executor;
    private readonly SdkExecutor _sdkExecutor;
    private ApiOperation? _selected;
    private SdkOp? _selectedSdk;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _busyStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        _executor = new ApiExecutor(_auth);
        _sdkExecutor = new SdkExecutor(_auth);
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
        if (RbModeSdk?.IsChecked == true) { BuildSdkTree(); return; }
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

    private ApiSurface GetSelectedSurface() => ApiSurface.Ppac;

    // ---------- SDK mode tree (reflected from Microsoft.PowerPlatform.Management) ----------
    private void OnTreeModeChanged(object sender, RoutedEventArgs e)
    {
        if (TxtSurfaceNotice == null || TvOps == null) return;
        var sdk = RbModeSdk?.IsChecked == true;
        TxtSurfaceNotice.Text = sdk
            ? "SDK mode — tree built by reflecting Microsoft.PowerPlatform.Management.dll. Selecting a node shows the C# RequestBuilder method signature; Send invokes it via Kiota using the same auth as REST mode. Indexed nodes (e.g. Environments[environment]) require the value in the Form tab."
            : PpacNotice;
        _selected = null; _selectedSdk = null;
        BuildOperationsTree();
    }

    private void BuildSdkTree()
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
        if (TxtSurfaceNotice != null) TxtSurfaceNotice.Text += $"\r\nDiscovered {ops.Count} SDK ops.";
        // Group into a tree. Fold the Kiota Item[position] indexer into the parent
        // collection name: e.g. "Environments" + "Item[position]" => one tree node
        // "Environments[environmentId]".
        var root = new TreeNode("");
        foreach (var op in ops)
        {
            var node = root;
            for (int i = 0; i < op.Path.Count; i++)
            {
                var step = op.Path[i];
                string key;
                if (step.IsIndexer && string.Equals(step.PropertyName, "Item", StringComparison.Ordinal)
                    && i > 0 && !op.Path[i - 1].IsIndexer
                    && node.Name == op.Path[i - 1].PropertyName)
                {
                    // Re-label the *current* (parent collection) node to include the indexer.
                    var newName = $"{node.Name}[{step.FriendlyParamName}]";
                    if (!ReferenceEquals(node, root) && node.Name != newName)
                    {
                        // Move children/ops to a renamed node under the same parent.
                        var parent = node.Parent!;
                        if (!parent.Children.TryGetValue(newName, out var renamed))
                        {
                            renamed = new TreeNode(newName) { Parent = parent };
                            // Migrate existing collection-only ops onto a child labelled "(collection)" if any
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
            TvOps.Items.Add(BuildTreeViewItem(top));
    }

    private static TreeViewItem BuildTreeViewItem(TreeNode node)
    {
        var tvi = new TreeViewItem { Header = node.Name, IsExpanded = false };
        foreach (var child in node.Children.Values.OrderBy(c => c.Name))
            tvi.Items.Add(BuildTreeViewItem(child));
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
            string role = p.ParameterType == typeof(System.Threading.CancellationToken) ? "cancellation"
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
        if (ret != typeof(void) && ret != typeof(System.Threading.Tasks.Task))
            DescribeType(ret, sb, depth: 1, maxDepth: 2, visited: new HashSet<Type>());
        return sb.ToString();
    }

    private static Type UnwrapTask(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>))
            return t.GetGenericArguments()[0];
        if (t == typeof(System.Threading.Tasks.Task)) return typeof(void);
        return t;
    }

    private static void DescribeType(Type t, StringBuilder sb, int depth, int maxDepth, HashSet<Type> visited)
    {
        if (depth > maxDepth) return;
        if (!visited.Add(t)) return;
        if (t.IsPrimitive || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset)) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (props.Length == 0) return;
        var indent = new string(' ', depth * 4);
        foreach (var p in props.Take(40))
        {
            sb.AppendLine($"{indent}- {p.Name} : {FriendlyName(p.PropertyType)}");
            // Recurse into complex SDK model types only.
            var inner = p.PropertyType;
            if (inner.IsGenericType && (inner.GetGenericTypeDefinition() == typeof(List<>) || inner.GetGenericTypeDefinition() == typeof(IList<>) || inner.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                inner = inner.GetGenericArguments()[0];
            if (inner.Namespace?.StartsWith("Microsoft.PowerPlatform.Management", StringComparison.Ordinal) == true)
                DescribeType(inner, sb, depth + 1, maxDepth, visited);
        }
        if (props.Length > 40) sb.AppendLine($"{indent}… ({props.Length - 40} more properties)");
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
        try
        {
            // Try construct + serialize an empty instance to expose default property names.
            var inst = Activator.CreateInstance(op.BodyType, nonPublic: true);
            return JsonSerializer.Serialize(inst, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return $"// SDK body type: {op.BodyType.FullName}{Environment.NewLine}{{}}";
        }
    }

    /// <summary>For SDK ops, the form only needs inputs for indexer params (envId, policyId, ...).</summary>
    private void BuildSdkForm(SdkOp op)
    {
        _formInputs.Clear();
        GridForm.Children.Clear();
        GridForm.RowDefinitions.Clear();
        GridForm.ColumnDefinitions.Clear();
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        GridForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var indexers = op.Path.Where(s => s.IsIndexer).ToList();
        // Dedupe by SlotKey so when the same collection appears more than once in a path
        // we still ask for it once (and reuse the same value across both indexers).
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
            // Strip leading // ... comment line if user left the auto-comment
            if (body != null && body.StartsWith("//")) body = string.Join('\n', body.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
            var result = await Task.Run(() => _sdkExecutor.ExecuteAsync(op, values, body, ct), ct);
            TbResponse.Text = result.Body;
            RenderJsonTree(result.Body);
            TxtRespMeta.Text = $"{(result.Success ? "OK" : "ERROR")}   {result.ElapsedMs} ms   ({result.StatusText})";
            TxtStatus.Text = result.Success ? "SDK call succeeded." : $"SDK call failed: {result.Error}";
            UpdateAuthState();
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Cancelled."; TxtRespMeta.Text = "CANCELLED"; }
        catch (Exception ex) { TbResponse.Text = ex.ToString(); TxtRespMeta.Text = "EXCEPTION"; TxtStatus.Text = $"Error: {ex.Message}"; }
        finally { EndBusy(); }
    }

    private async void OnSendLegacy_KeepCompiler() { await Task.CompletedTask; } // ensures async context unaffected if dead

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