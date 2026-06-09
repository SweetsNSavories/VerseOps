using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Single source of truth for the plugin's visual style. Called once
    /// from the control constructor after <c>InitializeComponent</c> to
    /// normalise fonts, control heights, and chrome across every Button,
    /// ComboBox, TextBox, Label and TabControl in the tree.
    ///
    /// Why this exists: the per-control <c>.Font = new Font("Segoe UI", 9F)</c>
    /// overrides scattered through the designer were 1pt smaller than the
    /// XrmToolBox host's default (10pt Segoe UI) and 4-6px shorter than
    /// the OS-themed button height, so the whole plugin looked cramped
    /// compared to its host. Centralising the rules here lets us follow
    /// either the host font (when running inside XrmToolBox) or our own
    /// Fluent baseline (when running standalone) without editing the
    /// designer file every time.
    /// </summary>
    internal static class FluentStyles
    {
        // Fluent / Segoe UI Variable baseline. 10pt matches both XrmToolBox's
        // own default and the Win11 Fluent system text size.
        public static readonly Font BaseFont = new Font("Segoe UI", 10F);
        public static readonly Font BoldFont = new Font("Segoe UI", 10F, FontStyle.Bold);
        // Code-style fields (URL, body editor, response/headers boxes).
        public static readonly Font MonoFont = new Font(
            FontFamily.GenericMonospace.IsStyleAvailable(FontStyle.Regular)
                ? "Cascadia Mono"
                : "Consolas",
            10F);

        public const int ButtonMinHeight   = 30; // matches Fluent default; XrmToolBox is ~28-30 too
        public const int ComboMinHeight    = 26; // ComboBox can't be much taller without clipping the dropdown glyph
        public const int RowGutter         = 4;
        public const int ButtonHorizontalPad = 14;

        /// <summary>
        /// Walks the control tree and normalises fonts + sizes. Idempotent.
        /// Safe to call from the constructor and again on theme changes.
        /// </summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            // Setting the root font lets WinForms cascade it to any child
            // whose Font property is still default. Children that set their
            // own font (e.g. mono-font response boxes) are unaffected.
            root.Font = BaseFont;
            ApplyRecursive(root);
        }

        private static void ApplyRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                StyleOne(c);
                // Tab pages and panels nest other controls — recurse.
                if (c.HasChildren) ApplyRecursive(c);
                // TabControl exposes pages via TabPages (not Controls in older
                // designer code paths), so recurse into those explicitly.
                if (c is TabControl tabs)
                {
                    foreach (TabPage page in tabs.TabPages) ApplyRecursive(page);
                }
            }
        }

        private static void StyleOne(Control c)
        {
            switch (c)
            {
                case Button btn:
                    StyleButton(btn);
                    break;
                case ComboBox combo:
                    StyleCombo(combo);
                    break;
                case TextBox tb:
                    StyleTextBox(tb);
                    break;
                case TreeView tv:
                    StyleTree(tv);
                    break;
                case TabControl tabs:
                    StyleTabs(tabs);
                    break;
                case Label lbl:
                    StyleLabel(lbl);
                    break;
            }
        }

        private static void StyleButton(Button btn)
        {
            // FlatStyle.System routes painting to the OS theme so the
            // button picks up Win11 Fluent rounded corners + hover. This
            // also makes the button auto-size its height to the OS metric
            // (~25-30px depending on DPI), which we floor at 30 below.
            btn.FlatStyle = FlatStyle.System;
            btn.UseVisualStyleBackColor = true;
            // AutoSize stretches the button to fit its text + padding.
            // Some buttons in the designer set an explicit Width which
            // would clip a longer caption; AutoSize prevents that.
            if (btn.AutoSize == false && btn.Tag as string != "fixed-width")
            {
                btn.AutoSize = true;
                btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }
            // Floor the height so the button never renders shorter than
            // the Fluent baseline even if the designer set it to 26.
            if (btn.MinimumSize.Height < ButtonMinHeight)
            {
                btn.MinimumSize = new Size(btn.MinimumSize.Width, ButtonMinHeight);
            }
            // Comfortable horizontal padding around the caption.
            if (btn.Padding == Padding.Empty)
            {
                btn.Padding = new Padding(ButtonHorizontalPad, 2, ButtonHorizontalPad, 2);
            }
            btn.Font = BaseFont;
        }

        private static void StyleCombo(ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.System;
            combo.Font = BaseFont;
            // ComboBox height is driven by font + ItemHeight; bumping the
            // font alone gets us to ~22-23 px. We don't force a height
            // because tall combos clip the dropdown chevron.
        }

        private static void StyleTextBox(TextBox tb)
        {
            // Keep monospace boxes monospace, just bump them to BaseFont
            // size so they match the rest of the UI weight-wise.
            if (IsMono(tb.Font))
            {
                tb.Font = MonoFont;
            }
            else
            {
                tb.Font = BaseFont;
            }
            // Fixed3D is the WinForms default; Fluent guidance is FixedSingle
            // with a subtle accent on focus, but FlatStyle on TextBox doesn't
            // exist — using the modern border style here is the equivalent.
            if (tb.BorderStyle == BorderStyle.Fixed3D)
            {
                tb.BorderStyle = BorderStyle.FixedSingle;
            }
        }

        private static void StyleTree(TreeView tv)
        {
            // Mono treeview for the JSON tree, Segoe for the catalog tree.
            tv.Font = IsMono(tv.Font) ? MonoFont : BaseFont;
            tv.ShowLines = false; // cleaner Fluent look
            tv.HotTracking = true;
            tv.FullRowSelect = true;
        }

        private static void StyleTabs(TabControl tabs)
        {
            tabs.Font = BaseFont;
            // Slightly taller tab strip to match the rest of the controls.
            tabs.ItemSize = new Size(0, 24);
            tabs.SizeMode = TabSizeMode.Normal;
            tabs.Appearance = TabAppearance.Normal;
        }

        private static void StyleLabel(Label lbl)
        {
            // Preserve bold labels (e.g. response header). Only touch
            // labels that are still on the default font.
            if (lbl.Font.Bold) { lbl.Font = BoldFont; return; }
            // Don't promote labels whose designer caller deliberately
            // chose a smaller meta size — heuristic: <9pt stays.
            if (lbl.Font.SizeInPoints >= 9F) lbl.Font = BaseFont;
        }

        private static readonly HashSet<string> MonoFamilies =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "Cascadia Mono",
                "Cascadia Code",
                "Consolas",
                "Courier New",
                "Lucida Console"
            };

        private static bool IsMono(Font f)
        {
            return f != null && MonoFamilies.Contains(f.Name);
        }
    }
}
