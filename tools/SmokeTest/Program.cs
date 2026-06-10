// Headless smoke test for VerseOps XrmToolBox plugin.
//
// Compiled with .NET Framework 4.x against the DLLs already produced by
// `dotnet build VerseOps.XrmToolBox`. No XrmToolBox host needed, so this
// works on machines without WIF 3.5 (the XrmToolBox.exe host prerequisite).
//
// Tracks:
//   A — direct API call via PluginAuthService + ApiExecutor (PPAC List Envs)
//   B — instantiate VerseOpsPluginControl, paint, screenshot empty state
//   C — drive UI: PPAC parameterless GET (List Environments For User) via Execute click
//   D — drive UI: BAP parameterless GET (List locations (geos)) — exercises ScopePowerApps scope
//   E — drive UI: PPAC parameterized GET (Get Currencies By Location) — fills the location param,
//       click Execute, capture response with route parameter substituted into URL
//   F — search box: type 'currency' into _searchBox, count remaining leaves, screenshot, then clear

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using VerseOps.Api.Core;
using VerseOps.XrmToolBox;
using VerseOps.XrmToolBox.Auth;

namespace SmokeTest
{
    internal static class Program
    {
        private static string s_outDir = "";
        private static readonly StringBuilder s_report = new StringBuilder();

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // --view : host the plugin in an on-screen interactive form so you
                //         can poke at the UI without needing XrmToolBox.exe (which
                //         fires a WIF 3.5 prereq dialog on Win11). Pass an op-name
                //         hint as the second arg to preselect (substring match).
                if (args.Length > 0 && string.Equals(args[0], "--view", StringComparison.OrdinalIgnoreCase))
                {
                    var preselect = args.Length > 1 ? args[1] : null;
                    return ViewMode(preselect);
                }

                s_outDir = args.Length > 0
                    ? args[0]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "out");
                Directory.CreateDirectory(s_outDir);

                ReportHeader("VerseOps XrmToolBox plugin - headless UI smoke");
                Report("Run time : " + DateTime.Now.ToString("u"));
                Report("Out dir  : " + s_outDir);

                Header("MEF metadata");
                var factory = typeof(VerseOpsPluginFactory);
                foreach (ExportMetadataAttribute meta in factory.GetCustomAttributes(typeof(ExportMetadataAttribute), true))
                    Log("  {0,-22} = {1}", meta.Name, meta.Value);

                Header("Catalog");
                Log("  BAP operations  : {0}", ApiCatalog.Operations.Count);
                Log("  PPAC operations : {0}", ApiCatalog.PpacOperations.Count);

                Header("Track A: direct PPAC GET /environments");
                bool aOk = TrackA().GetAwaiter().GetResult();

                Header("Tracks B-F: UI exercise");
                var ui = TrackBCDEF();

                ReportHeader("Summary");
                Report(string.Format("  Track A (direct API call)              : {0}", aOk ? "PASS" : "FAIL"));
                foreach (var kv in ui)
                    Report(string.Format("  {0,-40} : {1}", kv.Key, kv.Value ? "PASS" : "FAIL"));

                var reportPath = Path.Combine(s_outDir, "smoke-report.md");
                File.WriteAllText(reportPath, s_report.ToString());
                Console.WriteLine();
                Console.WriteLine("Report saved -> " + reportPath);

                Console.WriteLine();
                Console.WriteLine("Smoke test complete.");
                return (aOk && ui.Values.All(v => v)) ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        private static void Header(string text)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + text + " ===");
            s_report.AppendLine();
            s_report.AppendLine("## " + text);
            s_report.AppendLine();
        }
        private static void ReportHeader(string text)
        {
            Console.WriteLine();
            Console.WriteLine("### " + text);
            s_report.AppendLine();
            s_report.AppendLine("# " + text);
            s_report.AppendLine();
        }
        private static void Log(string fmt, params object[] args)
        {
            var line = string.Format(fmt, args);
            Console.WriteLine(line);
            s_report.AppendLine(line);
        }
        private static void Report(string line)
        {
            Console.WriteLine(line);
            s_report.AppendLine(line);
        }

        private static async Task<bool> TrackA()
        {
            var auth = new PluginAuthService();
            string token;
            try
            {
                token = await auth.TryGetTokenSilentAsync(PluginAuthService.ScopePpac, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log("  silent sign-in threw: {0} - {1}", ex.GetType().Name, ex.Message);
                return false;
            }
            if (token == null)
            {
                Log("  silent sign-in: NO cached token. Skipping API call.");
                return false;
            }
            Log("  silent sign-in: OK as '{0}'  (token length = {1})", auth.LastSignedInUser, token.Length);

            var listOp = ApiCatalog.PpacOperations.FirstOrDefault(o => o.Name == "List Environments For User");
            if (listOp == null) { Log("  no List Environments op in catalog?"); return false; }
            Log("  op: {0} {1}", listOp.HttpMethod, listOp.UrlTemplate);

            var exec = new ApiExecutor(auth);
            var result = await exec.ExecuteAsync(listOp.HttpMethod, listOp.UrlTemplate, null, listOp.TokenScope, CancellationToken.None);
            Log("  HTTP {0} {1}  ({2} ms)", result.StatusCode, result.ReasonPhrase, result.ElapsedMs);
            if (!string.IsNullOrEmpty(result.CorrelationId))
                Log("  x-ms-correlation-request-id: " + result.CorrelationId);

            var bodyPath = Path.Combine(s_outDir, "track-a-response.json");
            File.WriteAllText(bodyPath, result.ResponseBody ?? "");
            Log("  body saved -> {0}  ({1} bytes)", bodyPath, (result.ResponseBody ?? "").Length);

            if (result.StatusCode == 200)
            {
                int envCount = CountEnvelopeItems(result.ResponseBody ?? "");
                Log("  -> {0} environments returned.", envCount);
                using (var doc = System.Text.Json.JsonDocument.Parse(result.ResponseBody))
                {
                    if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int i = 0;
                        foreach (var env in arr.EnumerateArray())
                        {
                            if (i++ >= 3) break;
                            string id = env.TryGetProperty("id", out var n) ? n.GetString() : "?";
                            string display = env.TryGetProperty("displayName", out var dn) ? dn.GetString() : "?";
                            string type = env.TryGetProperty("type", out var t) ? t.GetString() : "?";
                            Log("     - {0,-40}  type={1,-12} id={2}", display, type, id);
                        }
                    }
                }
                return true;
            }
            return false;
        }

        private static int CountEnvelopeItems(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        return arr.GetArrayLength();
                }
            }
            catch { }
            return -1;
        }

        private static Dictionary<string, bool> TrackBCDEF()
        {
            var results = new Dictionary<string, bool>();
            VerseOpsPluginControl ctl = null;
            Form form = null;
            try
            {
                Application.EnableVisualStyles();
                ctl = new VerseOpsPluginControl();
                form = new Form
                {
                    Text = "VerseOps API Explorer (headless smoke)",
                    Size = new Size(1400, 820),
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                };
                form.SetDesktopLocation(-4000, -4000); // offscreen
                ctl.Dock = DockStyle.Fill;
                form.Controls.Add(ctl);
                form.Show();
                PumpFor(TimeSpan.FromSeconds(8));

                var fTree    = Field("_opsTree");
                var fSearch  = Field("_searchBox");
                var fStatus  = Field("_statusLabel");
                var fRespHdr = Field("_responseHeader");
                var fRespBox = Field("_responseBox");
                var fExec    = Field("_btnExecute");
                var fOpMeta  = Field("_opMetaLabel");
                var fParamIn = Field("_paramInputs");

                var tree    = (TreeView)fTree.GetValue(ctl);
                var search  = (TextBox)fSearch.GetValue(ctl);
                var status  = fStatus.GetValue(ctl) as ToolStripItem;
                var respHdr = fRespHdr.GetValue(ctl) as Label;
                var respBox = fRespBox.GetValue(ctl) as TextBox;
                var execBtn = fExec.GetValue(ctl) as Button;
                var opMeta  = fOpMeta.GetValue(ctl) as Label;

                Log("  top-level tree nodes  : {0}", string.Join("; ", tree.Nodes.Cast<TreeNode>().Select(n => n.Text)));
                int leaves = 0; CountLeaves(tree.Nodes, ref leaves);
                Log("  total tree leaves     : {0}", leaves);
                Log("  status label text     : {0}", status?.Text ?? "?");

                SaveScreenshot(form, "track-b-empty.png");
                results["Track B (control renders, tree populated)"] = leaves >= 50;

                Header("Track C: drive UI -> PPAC GET 'List Environments For User'");
                results["Track C (PPAC parameterless via Execute)"] =
                    DriveAndCapture(form, tree, execBtn, respHdr, respBox, opMeta,
                        leafText: "GET  List Environments For User",
                        paramSetter: null,
                        screenshotName: "track-c-ppac-list-envs.png");

                Header("Track D: drive UI -> BAP GET 'List locations (geos)'");
                results["Track D (BAP parameterless, ScopePowerApps)"] =
                    DriveAndCapture(form, tree, execBtn, respHdr, respBox, opMeta,
                        leafText: "GET  List locations (geos)",
                        paramSetter: null,
                        screenshotName: "track-d-bap-list-locations.png");

                Header("Track E: drive UI -> PPAC GET 'Get Currencies By Location' with location=unitedstates");
                results["Track E (PPAC parameterized, form filled)"] =
                    DriveAndCapture(form, tree, execBtn, respHdr, respBox, opMeta,
                        leafText: "GET  Get Currencies By Location",
                        paramSetter: () => SetParam(ctl, fParamIn, "location", "unitedstates"),
                        screenshotName: "track-e-ppac-currencies.png");

                Header("Track F: search box filter -> 'currency'");
                bool fOk = false;
                try
                {
                    search.Focus();
                    search.Text = "currency";
                    PumpFor(TimeSpan.FromMilliseconds(600));
                    int filteredLeaves = 0; CountLeaves(tree.Nodes, ref filteredLeaves);
                    Log("  filtered tree leaves  : {0}  (was {1})", filteredLeaves, leaves);
                    SaveScreenshot(form, "track-f-search-currency.png");
                    fOk = filteredLeaves > 0 && filteredLeaves < leaves;

                    search.Text = "";
                    PumpFor(TimeSpan.FromMilliseconds(400));
                    int restored = 0; CountLeaves(tree.Nodes, ref restored);
                    Log("  cleared filter leaves : {0}  (was {1})", restored, leaves);
                    fOk = fOk && restored == leaves;
                }
                catch (Exception ex)
                {
                    Log("  Track F FAILED: {0}", ex.Message);
                }
                results["Track F (search box filter)"] = fOk;
            }
            finally
            {
                if (form != null) { form.Close(); form.Dispose(); }
                if (ctl != null) ctl.Dispose();
            }
            return results;
        }

        private static bool DriveAndCapture(
            Form form,
            TreeView tree,
            Button execBtn,
            Label respHdr,
            TextBox respBox,
            Label opMeta,
            string leafText,
            Action paramSetter,
            string screenshotName)
        {
            var node = FindNodeByText(tree.Nodes, leafText);
            if (node == null)
            {
                Log("  could not find leaf '{0}' - dumping 10 leaves:", leafText);
                DumpFirstLeaves(tree.Nodes, 10);
                return false;
            }
            Log("  selecting: {0}", BreadcrumbOf(node));
            tree.SelectedNode = node;
            PumpFor(TimeSpan.FromMilliseconds(800));
            Log("  op meta label: {0}", opMeta?.Text ?? "?");

            if (paramSetter != null)
            {
                paramSetter();
                PumpFor(TimeSpan.FromMilliseconds(300));
            }

            if (execBtn == null || !execBtn.Enabled)
            {
                Log("  Execute button not enabled (enabled={0})", execBtn?.Enabled);
                return false;
            }
            Log("  clicking Execute...");
            execBtn.PerformClick();

            var stop = DateTime.UtcNow.AddSeconds(30);
            string finalHdr = "", finalBody = "";
            while (DateTime.UtcNow < stop)
            {
                PumpFor(TimeSpan.FromMilliseconds(150));
                finalHdr = respHdr?.Text ?? "";
                finalBody = respBox?.Text ?? "";
                if (finalHdr.IndexOf("Response", StringComparison.OrdinalIgnoreCase) >= 0 && finalBody.Length > 100) break;
            }
            Log("  response header  : {0}", finalHdr);
            Log("  response length  : {0} chars", finalBody.Length);

            SaveScreenshot(form, screenshotName);
            // Response header format from VerseOpsPluginControl: "Response  -  {code} {reason}    {ms} ms".
            return finalHdr.IndexOf("200 OK", StringComparison.OrdinalIgnoreCase) >= 0
                   || finalHdr.IndexOf("201 ", StringComparison.OrdinalIgnoreCase) >= 0
                   || finalHdr.IndexOf("202 ", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool SetParam(VerseOpsPluginControl ctl, FieldInfo fParamIn, string token, string value)
        {
            var dict = fParamIn.GetValue(ctl) as System.Collections.IDictionary;
            if (dict == null) { Log("  _paramInputs is null"); return false; }
            if (!dict.Contains(token))
            {
                Log("  param '{0}' not on form. Available keys: {1}", token,
                    string.Join(", ", dict.Keys.Cast<object>().Select(k => k.ToString())));
                return false;
            }
            var input = dict[token] as Control;
            if (input == null) { Log("  param '{0}' input is null", token); return false; }
            input.Text = value;
            Log("  set param '{0}' = '{1}'  (control: {2})", token, value, input.GetType().Name);
            return true;
        }

        private static FieldInfo Field(string name) =>
            typeof(VerseOpsPluginControl).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

        private static void PumpFor(TimeSpan d)
        {
            var stop = DateTime.UtcNow + d;
            while (DateTime.UtcNow < stop)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
        }

        private static void SaveScreenshot(Form form, string fileName)
        {
            using (var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height));
                var png = Path.Combine(s_outDir, fileName);
                bmp.Save(png, ImageFormat.Png);
                Log("  PNG saved -> {0}  ({1} bytes)", png, new FileInfo(png).Length);
            }
        }

        private static TreeNode FindNodeByText(TreeNodeCollection nodes, string text)
        {
            foreach (TreeNode n in nodes)
            {
                if (string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase)) return n;
                var inner = FindNodeByText(n.Nodes, text);
                if (inner != null) return inner;
            }
            return null;
        }

        private static string BreadcrumbOf(TreeNode n)
        {
            var parts = new List<string>();
            for (var cur = n; cur != null; cur = cur.Parent) parts.Insert(0, cur.Text);
            return string.Join(" / ", parts);
        }

        private static void DumpFirstLeaves(TreeNodeCollection nodes, int max)
        {
            int shown = 0;
            void Walk(TreeNodeCollection ns)
            {
                foreach (TreeNode n in ns)
                {
                    if (shown >= max) return;
                    if (n.Nodes.Count == 0)
                    {
                        Log("    {0}", BreadcrumbOf(n));
                        shown++;
                    }
                    else Walk(n.Nodes);
                }
            }
            Walk(nodes);
        }

        private static void CountLeaves(TreeNodeCollection nodes, ref int count)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Nodes.Count == 0) count++;
                else CountLeaves(n.Nodes, ref count);
            }
        }
    }
}
