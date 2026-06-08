// WinForms designer-style partial. Treated as auto-generated for nullable
// analysis so the field initializers (set inside InitializeComponent) don't
// trip CS8669 under the repo-wide <Nullable>enable</Nullable>.
#nullable disable
namespace VerseOps.XrmToolBox
{
    partial class VerseOpsPluginControl
    {
        // Toolbar (sign-in surface — wired in PR #3).
        private System.Windows.Forms.ToolStrip _toolStrip;
        private System.Windows.Forms.ToolStripButton _btnSignIn;
        private System.Windows.Forms.ToolStripButton _btnSignInDeviceCode;
        private System.Windows.Forms.ToolStripButton _btnSignOut;
        private System.Windows.Forms.ToolStripSeparator _sep1;
        private System.Windows.Forms.ToolStripLabel _statusLabel;

        // Catalog explorer (PR #4).
        private System.Windows.Forms.SplitContainer _outerSplit;     // left = tree, right = request/response
        private System.Windows.Forms.SplitContainer _rightSplit;     // top = request, bottom = response
        private System.Windows.Forms.TextBox _searchBox;
        private System.Windows.Forms.TreeView _opsTree;
        private System.Windows.Forms.Panel _requestPanel;
        private System.Windows.Forms.Label _opMetaLabel;
        private System.Windows.Forms.TableLayoutPanel _paramTable;
        private System.Windows.Forms.Label _bodyHeader;
        private System.Windows.Forms.TextBox _bodyEditor;
        private System.Windows.Forms.Panel _executeBar;
        private System.Windows.Forms.Button _btnExecute;
        private System.Windows.Forms.Label _executeHint;
        private System.Windows.Forms.Panel _responsePanel;
        private System.Windows.Forms.Label _responseHeader;
        private System.Windows.Forms.TextBox _responseBox;

        private void InitializeComponent()
        {
            _toolStrip            = new System.Windows.Forms.ToolStrip();
            _btnSignIn            = new System.Windows.Forms.ToolStripButton();
            _btnSignInDeviceCode  = new System.Windows.Forms.ToolStripButton();
            _btnSignOut           = new System.Windows.Forms.ToolStripButton();
            _sep1                 = new System.Windows.Forms.ToolStripSeparator();
            _statusLabel          = new System.Windows.Forms.ToolStripLabel();

            _outerSplit           = new System.Windows.Forms.SplitContainer();
            _rightSplit           = new System.Windows.Forms.SplitContainer();
            _searchBox            = new System.Windows.Forms.TextBox();
            _opsTree              = new System.Windows.Forms.TreeView();
            _requestPanel         = new System.Windows.Forms.Panel();
            _opMetaLabel          = new System.Windows.Forms.Label();
            _paramTable           = new System.Windows.Forms.TableLayoutPanel();
            _bodyHeader           = new System.Windows.Forms.Label();
            _bodyEditor           = new System.Windows.Forms.TextBox();
            _executeBar           = new System.Windows.Forms.Panel();
            _btnExecute           = new System.Windows.Forms.Button();
            _executeHint          = new System.Windows.Forms.Label();
            _responsePanel        = new System.Windows.Forms.Panel();
            _responseHeader       = new System.Windows.Forms.Label();
            _responseBox          = new System.Windows.Forms.TextBox();

            _toolStrip.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_outerSplit).BeginInit();
            _outerSplit.Panel1.SuspendLayout();
            _outerSplit.Panel2.SuspendLayout();
            _outerSplit.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_rightSplit).BeginInit();
            _rightSplit.Panel1.SuspendLayout();
            _rightSplit.Panel2.SuspendLayout();
            _rightSplit.SuspendLayout();
            _requestPanel.SuspendLayout();
            _executeBar.SuspendLayout();
            _responsePanel.SuspendLayout();
            SuspendLayout();

            // ---- Toolbar (PR #3) ------------------------------------------
            _toolStrip.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
            _toolStrip.ImageScalingSize = new System.Drawing.Size(20, 20);
            _toolStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
            {
                _btnSignIn,
                _btnSignInDeviceCode,
                _btnSignOut,
                _sep1,
                _statusLabel
            });
            _toolStrip.Location = new System.Drawing.Point(0, 0);
            _toolStrip.Name = "_toolStrip";
            _toolStrip.RenderMode = System.Windows.Forms.ToolStripRenderMode.System;
            _toolStrip.Size = new System.Drawing.Size(1000, 27);
            _toolStrip.TabIndex = 0;

            _btnSignIn.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignIn.Name = "_btnSignIn";
            _btnSignIn.Text = "Sign in (browser)";
            _btnSignIn.ToolTipText = "Opens the system browser for an MSAL interactive sign-in. Token cache is shared with the VerseOps WPF app.";
            _btnSignIn.Click += BtnSignIn_Click;

            _btnSignInDeviceCode.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignInDeviceCode.Name = "_btnSignInDeviceCode";
            _btnSignInDeviceCode.Text = "Sign in (device code)";
            _btnSignInDeviceCode.ToolTipText = "MSAL device-code flow \u2014 copy the user code into microsoft.com/devicelogin on any browser. Use when the host has no usable browser.";
            _btnSignInDeviceCode.Click += BtnSignInDeviceCode_Click;

            _btnSignOut.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignOut.Enabled = false;
            _btnSignOut.Name = "_btnSignOut";
            _btnSignOut.Text = "Sign out";
            _btnSignOut.ToolTipText = "Clears the MSAL cache (also signs out the VerseOps WPF app \u2014 shared cache).";
            _btnSignOut.Click += BtnSignOut_Click;

            _statusLabel.Name = "_statusLabel";
            _statusLabel.Text = "Initialising\u2026";

            // ---- Outer split: left tree, right request/response ----------
            _outerSplit.Dock = System.Windows.Forms.DockStyle.Fill;
            _outerSplit.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
            _outerSplit.Orientation = System.Windows.Forms.Orientation.Vertical;
            _outerSplit.SplitterDistance = 320;
            _outerSplit.SplitterWidth = 5;
            _outerSplit.Name = "_outerSplit";

            // Left pane: search + tree
            _searchBox.Dock = System.Windows.Forms.DockStyle.Top;
            _searchBox.Font = new System.Drawing.Font("Segoe UI", 9.5F);
            _searchBox.Name = "_searchBox";
            _searchBox.TextChanged += SearchBox_TextChanged;

            _opsTree.Dock = System.Windows.Forms.DockStyle.Fill;
            _opsTree.FullRowSelect = true;
            _opsTree.HideSelection = false;
            _opsTree.Font = new System.Drawing.Font("Segoe UI", 9.5F);
            _opsTree.Name = "_opsTree";
            _opsTree.ShowRootLines = true;
            _opsTree.AfterSelect += OpsTree_AfterSelect;

            _outerSplit.Panel1.Controls.Add(_opsTree);
            _outerSplit.Panel1.Controls.Add(_searchBox);
            _outerSplit.Panel1.Padding = new System.Windows.Forms.Padding(0);

            // Right pane: nested vertical split (request top, response bottom)
            _rightSplit.Dock = System.Windows.Forms.DockStyle.Fill;
            _rightSplit.Orientation = System.Windows.Forms.Orientation.Horizontal;
            _rightSplit.SplitterDistance = 320;
            _rightSplit.SplitterWidth = 5;
            _rightSplit.Name = "_rightSplit";

            // Request panel ------------------------------------------------
            _requestPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            _requestPanel.Padding = new System.Windows.Forms.Padding(8);
            _requestPanel.Name = "_requestPanel";

            _opMetaLabel.Dock = System.Windows.Forms.DockStyle.Top;
            _opMetaLabel.AutoSize = false;
            _opMetaLabel.Height = 56;
            _opMetaLabel.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
            _opMetaLabel.Font = new System.Drawing.Font("Segoe UI", 9F);
            _opMetaLabel.Text = "Select an operation from the tree on the left.";
            _opMetaLabel.Name = "_opMetaLabel";

            _paramTable.Dock = System.Windows.Forms.DockStyle.Top;
            _paramTable.AutoSize = true;
            _paramTable.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            _paramTable.ColumnCount = 2;
            _paramTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 160F));
            _paramTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            _paramTable.Padding = new System.Windows.Forms.Padding(0, 4, 0, 4);
            _paramTable.Name = "_paramTable";

            _bodyHeader.Dock = System.Windows.Forms.DockStyle.Top;
            _bodyHeader.AutoSize = true;
            _bodyHeader.Padding = new System.Windows.Forms.Padding(0, 8, 0, 2);
            _bodyHeader.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            _bodyHeader.Text = "Request body (JSON)";
            _bodyHeader.Name = "_bodyHeader";

            _bodyEditor.Dock = System.Windows.Forms.DockStyle.Fill;
            _bodyEditor.Multiline = true;
            _bodyEditor.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            _bodyEditor.AcceptsReturn = true;
            _bodyEditor.AcceptsTab = true;
            _bodyEditor.WordWrap = false;
            _bodyEditor.Font = new System.Drawing.Font("Consolas", 9.5F);
            _bodyEditor.Name = "_bodyEditor";

            _executeBar.Dock = System.Windows.Forms.DockStyle.Bottom;
            _executeBar.Height = 36;
            _executeBar.Name = "_executeBar";

            _btnExecute.Dock = System.Windows.Forms.DockStyle.Right;
            _btnExecute.Width = 120;
            _btnExecute.Height = 30;
            _btnExecute.Text = "Execute";
            _btnExecute.Enabled = false;
            _btnExecute.Name = "_btnExecute";
            _btnExecute.UseVisualStyleBackColor = true;
            _btnExecute.Click += BtnExecute_Click;

            _executeHint.Dock = System.Windows.Forms.DockStyle.Fill;
            _executeHint.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _executeHint.ForeColor = System.Drawing.SystemColors.GrayText;
            _executeHint.Name = "_executeHint";
            _executeHint.Text = "";

            _executeBar.Controls.Add(_executeHint);
            _executeBar.Controls.Add(_btnExecute);

            // Order matters with DockStyle.Fill + Top + Bottom siblings: the
            // Fill control must be added BEFORE the Top/Bottom docked controls
            // so it ends up underneath them in z-order.
            _requestPanel.Controls.Add(_bodyEditor);
            _requestPanel.Controls.Add(_bodyHeader);
            _requestPanel.Controls.Add(_paramTable);
            _requestPanel.Controls.Add(_opMetaLabel);
            _requestPanel.Controls.Add(_executeBar);

            _rightSplit.Panel1.Controls.Add(_requestPanel);

            // Response panel ----------------------------------------------
            _responsePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            _responsePanel.Padding = new System.Windows.Forms.Padding(8);
            _responsePanel.Name = "_responsePanel";

            _responseHeader.Dock = System.Windows.Forms.DockStyle.Top;
            _responseHeader.AutoSize = false;
            _responseHeader.Height = 22;
            _responseHeader.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            _responseHeader.Text = "Response";
            _responseHeader.Name = "_responseHeader";

            _responseBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _responseBox.Multiline = true;
            _responseBox.ReadOnly = true;
            _responseBox.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            _responseBox.WordWrap = false;
            _responseBox.Font = new System.Drawing.Font("Consolas", 9.5F);
            _responseBox.Name = "_responseBox";
            _responseBox.BackColor = System.Drawing.SystemColors.Window;

            _responsePanel.Controls.Add(_responseBox);
            _responsePanel.Controls.Add(_responseHeader);

            _rightSplit.Panel2.Controls.Add(_responsePanel);

            _outerSplit.Panel2.Controls.Add(_rightSplit);

            // ---- Root ----------------------------------------------------
            Controls.Add(_outerSplit);
            Controls.Add(_toolStrip);
            Name = "VerseOpsPluginControl";
            Size = new System.Drawing.Size(1000, 600);

            _executeBar.ResumeLayout(false);
            _requestPanel.ResumeLayout(false);
            _requestPanel.PerformLayout();
            _responsePanel.ResumeLayout(false);
            _rightSplit.Panel1.ResumeLayout(false);
            _rightSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_rightSplit).EndInit();
            _rightSplit.ResumeLayout(false);
            _outerSplit.Panel1.ResumeLayout(false);
            _outerSplit.Panel1.PerformLayout();
            _outerSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_outerSplit).EndInit();
            _outerSplit.ResumeLayout(false);
            _toolStrip.ResumeLayout(false);
            _toolStrip.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
