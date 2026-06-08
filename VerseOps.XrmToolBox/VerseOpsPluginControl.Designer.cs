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

        // ---- Request panel (PR #5 redesign) ---------------------------
        private System.Windows.Forms.Panel _requestPanel;
        private System.Windows.Forms.Label _opMetaLabel;
        // Top strip: row 0 = Method | Scope | Send/Cancel/Decode, row 1 = URL spanning.
        private System.Windows.Forms.TableLayoutPanel _requestTopStrip;
        private System.Windows.Forms.Label _methodLbl;
        private System.Windows.Forms.ComboBox _methodCombo;
        private System.Windows.Forms.Label _scopeLbl;
        private System.Windows.Forms.ComboBox _scopeCombo;
        private System.Windows.Forms.FlowLayoutPanel _requestButtons;
        private System.Windows.Forms.Button _btnExecute;
        private System.Windows.Forms.Button _btnCancel;
        private System.Windows.Forms.Button _btnDecode;
        private System.Windows.Forms.Label _urlLbl;
        private System.Windows.Forms.TextBox _urlBox;
        // Body as Form/Raw tabs (Form keeps existing TableLayoutPanel).
        private System.Windows.Forms.TabControl _bodyTabs;
        private System.Windows.Forms.TabPage _bodyTabForm;
        private System.Windows.Forms.TabPage _bodyTabRaw;
        private System.Windows.Forms.Panel _formScrollHost;
        private System.Windows.Forms.TableLayoutPanel _paramTable;
        private System.Windows.Forms.TextBox _bodyEditor;

        // ---- Response panel (PR #5 redesign) --------------------------
        private System.Windows.Forms.Panel _responsePanel;
        private System.Windows.Forms.Label _responseHeader;
        // Search bar: find-next (body tab) / live filter (tree tab).
        private System.Windows.Forms.Panel _respSearchPanel;
        private System.Windows.Forms.Label _respSearchLbl;
        private System.Windows.Forms.TextBox _respSearchBox;
        private System.Windows.Forms.Button _btnRespSearchNext;
        private System.Windows.Forms.Button _btnRespSearchClear;
        private System.Windows.Forms.Label _respSearchInfo;
        // Response tabs.
        private System.Windows.Forms.TabControl _responseTabs;
        private System.Windows.Forms.TabPage _respTabBody;
        private System.Windows.Forms.TabPage _respTabTree;
        private System.Windows.Forms.TabPage _respTabHeaders;
        private System.Windows.Forms.TabPage _respTabDescription;
        private System.Windows.Forms.TextBox _responseBox;
        private System.Windows.Forms.TreeView _jsonTree;
        private System.Windows.Forms.TextBox _headersBox;
        private System.Windows.Forms.TextBox _descriptionBox;

        // ---- App-wide status strip (PR #5) ----------------------------
        private System.Windows.Forms.StatusStrip _statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel _statusBarLabel;
        private System.Windows.Forms.ToolStripStatusLabel _statusBarElapsed;
        private System.Windows.Forms.ToolStripProgressBar _statusBarProgress;

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
            _requestTopStrip      = new System.Windows.Forms.TableLayoutPanel();
            _methodLbl            = new System.Windows.Forms.Label();
            _methodCombo          = new System.Windows.Forms.ComboBox();
            _scopeLbl             = new System.Windows.Forms.Label();
            _scopeCombo           = new System.Windows.Forms.ComboBox();
            _requestButtons       = new System.Windows.Forms.FlowLayoutPanel();
            _btnExecute           = new System.Windows.Forms.Button();
            _btnCancel            = new System.Windows.Forms.Button();
            _btnDecode            = new System.Windows.Forms.Button();
            _urlLbl               = new System.Windows.Forms.Label();
            _urlBox               = new System.Windows.Forms.TextBox();
            _bodyTabs             = new System.Windows.Forms.TabControl();
            _bodyTabForm          = new System.Windows.Forms.TabPage();
            _bodyTabRaw           = new System.Windows.Forms.TabPage();
            _formScrollHost       = new System.Windows.Forms.Panel();
            _paramTable           = new System.Windows.Forms.TableLayoutPanel();
            _bodyEditor           = new System.Windows.Forms.TextBox();

            _responsePanel        = new System.Windows.Forms.Panel();
            _responseHeader       = new System.Windows.Forms.Label();
            _respSearchPanel      = new System.Windows.Forms.Panel();
            _respSearchLbl        = new System.Windows.Forms.Label();
            _respSearchBox        = new System.Windows.Forms.TextBox();
            _btnRespSearchNext    = new System.Windows.Forms.Button();
            _btnRespSearchClear   = new System.Windows.Forms.Button();
            _respSearchInfo       = new System.Windows.Forms.Label();
            _responseTabs         = new System.Windows.Forms.TabControl();
            _respTabBody          = new System.Windows.Forms.TabPage();
            _respTabTree          = new System.Windows.Forms.TabPage();
            _respTabHeaders       = new System.Windows.Forms.TabPage();
            _respTabDescription   = new System.Windows.Forms.TabPage();
            _responseBox          = new System.Windows.Forms.TextBox();
            _jsonTree             = new System.Windows.Forms.TreeView();
            _headersBox           = new System.Windows.Forms.TextBox();
            _descriptionBox       = new System.Windows.Forms.TextBox();

            _statusStrip          = new System.Windows.Forms.StatusStrip();
            _statusBarLabel       = new System.Windows.Forms.ToolStripStatusLabel();
            _statusBarElapsed     = new System.Windows.Forms.ToolStripStatusLabel();
            _statusBarProgress    = new System.Windows.Forms.ToolStripProgressBar();

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
            _requestTopStrip.SuspendLayout();
            _requestButtons.SuspendLayout();
            _bodyTabs.SuspendLayout();
            _bodyTabForm.SuspendLayout();
            _bodyTabRaw.SuspendLayout();
            _formScrollHost.SuspendLayout();
            _responsePanel.SuspendLayout();
            _respSearchPanel.SuspendLayout();
            _responseTabs.SuspendLayout();
            _respTabBody.SuspendLayout();
            _respTabTree.SuspendLayout();
            _respTabHeaders.SuspendLayout();
            _respTabDescription.SuspendLayout();
            _statusStrip.SuspendLayout();
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
            _rightSplit.SplitterDistance = 300;
            _rightSplit.SplitterWidth = 5;
            _rightSplit.Name = "_rightSplit";

            // ============================================================
            // REQUEST PANEL
            // ============================================================
            _requestPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            _requestPanel.Padding = new System.Windows.Forms.Padding(8);
            _requestPanel.Name = "_requestPanel";

            _opMetaLabel.Dock = System.Windows.Forms.DockStyle.Top;
            _opMetaLabel.AutoSize = false;
            _opMetaLabel.Height = 52;
            _opMetaLabel.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
            _opMetaLabel.Font = new System.Drawing.Font("Segoe UI", 9F);
            _opMetaLabel.Text = "Select an operation from the tree on the left.";
            _opMetaLabel.Name = "_opMetaLabel";

            // ---- Request top strip (Method/URL/Scope + buttons) ---------
            // 5 cols × 2 rows.
            //   Row 0: Method lbl | Method combo | Scope lbl | Scope combo (Fill) | Buttons (auto)
            //   Row 1: URL    lbl | URL textbox spanning cols 1..4
            _requestTopStrip.Dock = System.Windows.Forms.DockStyle.Top;
            _requestTopStrip.AutoSize = true;
            _requestTopStrip.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            _requestTopStrip.ColumnCount = 5;
            _requestTopStrip.RowCount = 2;
            _requestTopStrip.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            _requestTopStrip.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 96F));
            _requestTopStrip.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            _requestTopStrip.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            _requestTopStrip.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            _requestTopStrip.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            _requestTopStrip.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            _requestTopStrip.Padding = new System.Windows.Forms.Padding(0, 2, 0, 6);
            _requestTopStrip.Name = "_requestTopStrip";

            _methodLbl.Text = "Method:";
            _methodLbl.AutoSize = true;
            _methodLbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _methodLbl.Anchor = System.Windows.Forms.AnchorStyles.Left;
            _methodLbl.Margin = new System.Windows.Forms.Padding(0, 6, 6, 4);

            _methodCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            _methodCombo.Items.AddRange(new object[] { "GET", "POST", "PATCH", "PUT", "DELETE" });
            _methodCombo.SelectedIndex = 0;
            _methodCombo.Dock = System.Windows.Forms.DockStyle.Fill;
            _methodCombo.Margin = new System.Windows.Forms.Padding(0, 3, 12, 3);
            _methodCombo.Font = new System.Drawing.Font("Segoe UI", 9F);

            _scopeLbl.Text = "Scope:";
            _scopeLbl.AutoSize = true;
            _scopeLbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _scopeLbl.Anchor = System.Windows.Forms.AnchorStyles.Left;
            _scopeLbl.Margin = new System.Windows.Forms.Padding(0, 6, 6, 4);

            _scopeCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown;
            _scopeCombo.Items.AddRange(new object[]
            {
                "https://api.powerplatform.com/.default",
                "https://service.powerapps.com/.default",
                "https://api.bap.microsoft.com/.default",
                "https://graph.microsoft.com/.default"
            });
            _scopeCombo.SelectedIndex = 0;
            _scopeCombo.Dock = System.Windows.Forms.DockStyle.Fill;
            _scopeCombo.Margin = new System.Windows.Forms.Padding(0, 3, 8, 3);
            _scopeCombo.Font = new System.Drawing.Font("Segoe UI", 9F);

            _requestButtons.FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight;
            _requestButtons.AutoSize = true;
            _requestButtons.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            _requestButtons.WrapContents = false;
            _requestButtons.Margin = new System.Windows.Forms.Padding(0);
            _requestButtons.Padding = new System.Windows.Forms.Padding(0);

            _btnExecute.Text = "Send";
            _btnExecute.Width = 84;
            _btnExecute.Height = 26;
            _btnExecute.Enabled = false;
            _btnExecute.Name = "_btnExecute";
            _btnExecute.UseVisualStyleBackColor = true;
            _btnExecute.Margin = new System.Windows.Forms.Padding(0, 0, 6, 0);
            _btnExecute.Click += BtnExecute_Click;

            _btnCancel.Text = "Cancel";
            _btnCancel.Width = 70;
            _btnCancel.Height = 26;
            _btnCancel.Enabled = false;
            _btnCancel.Name = "_btnCancel";
            _btnCancel.UseVisualStyleBackColor = true;
            _btnCancel.Margin = new System.Windows.Forms.Padding(0, 0, 6, 0);
            _btnCancel.Click += BtnCancel_Click;

            _btnDecode.Text = "Decode bearer";
            _btnDecode.Width = 110;
            _btnDecode.Height = 26;
            _btnDecode.Enabled = false;
            _btnDecode.Name = "_btnDecode";
            _btnDecode.UseVisualStyleBackColor = true;
            _btnDecode.Margin = new System.Windows.Forms.Padding(0);
            _btnDecode.Click += BtnDecode_Click;

            _requestButtons.Controls.Add(_btnExecute);
            _requestButtons.Controls.Add(_btnCancel);
            _requestButtons.Controls.Add(_btnDecode);

            _urlLbl.Text = "URL:";
            _urlLbl.AutoSize = true;
            _urlLbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _urlLbl.Anchor = System.Windows.Forms.AnchorStyles.Left;
            _urlLbl.Margin = new System.Windows.Forms.Padding(0, 6, 6, 4);

            _urlBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _urlBox.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9F);
            _urlBox.Margin = new System.Windows.Forms.Padding(0, 3, 0, 3);
            _urlBox.Name = "_urlBox";

            _requestTopStrip.Controls.Add(_methodLbl,      0, 0);
            _requestTopStrip.Controls.Add(_methodCombo,    1, 0);
            _requestTopStrip.Controls.Add(_scopeLbl,       2, 0);
            _requestTopStrip.Controls.Add(_scopeCombo,     3, 0);
            _requestTopStrip.Controls.Add(_requestButtons, 4, 0);
            _requestTopStrip.Controls.Add(_urlLbl,         0, 1);
            _requestTopStrip.Controls.Add(_urlBox,         1, 1);
            _requestTopStrip.SetColumnSpan(_urlBox, 4);

            // ---- Body tabs (Form / Raw) ---------------------------------
            _bodyTabs.Dock = System.Windows.Forms.DockStyle.Fill;
            _bodyTabs.Font = new System.Drawing.Font("Segoe UI", 9F);
            _bodyTabs.Name = "_bodyTabs";
            _bodyTabs.TabPages.Add(_bodyTabForm);
            _bodyTabs.TabPages.Add(_bodyTabRaw);

            _bodyTabForm.Text = "Form";
            _bodyTabForm.Padding = new System.Windows.Forms.Padding(6);
            _bodyTabForm.UseVisualStyleBackColor = true;
            _bodyTabForm.Controls.Add(_formScrollHost);

            _formScrollHost.Dock = System.Windows.Forms.DockStyle.Fill;
            _formScrollHost.AutoScroll = true;
            _formScrollHost.Padding = new System.Windows.Forms.Padding(0);
            _formScrollHost.Name = "_formScrollHost";

            _paramTable.Dock = System.Windows.Forms.DockStyle.Top;
            _paramTable.AutoSize = true;
            _paramTable.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            _paramTable.ColumnCount = 2;
            _paramTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 160F));
            _paramTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            _paramTable.Padding = new System.Windows.Forms.Padding(0);
            _paramTable.Name = "_paramTable";

            _formScrollHost.Controls.Add(_paramTable);

            _bodyTabRaw.Text = "Raw body";
            _bodyTabRaw.Padding = new System.Windows.Forms.Padding(0);
            _bodyTabRaw.UseVisualStyleBackColor = true;
            _bodyTabRaw.Controls.Add(_bodyEditor);

            _bodyEditor.Dock = System.Windows.Forms.DockStyle.Fill;
            _bodyEditor.Multiline = true;
            _bodyEditor.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            _bodyEditor.AcceptsReturn = true;
            _bodyEditor.AcceptsTab = true;
            _bodyEditor.WordWrap = false;
            _bodyEditor.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9.5F);
            _bodyEditor.Name = "_bodyEditor";

            // Add request-panel children. Fill first (z-order: underneath),
            // Top docks stack last-added-closest-to-edge.
            _requestPanel.Controls.Add(_bodyTabs);          // Fill
            _requestPanel.Controls.Add(_requestTopStrip);   // Top (below opMeta)
            _requestPanel.Controls.Add(_opMetaLabel);       // Top (edge)

            _rightSplit.Panel1.Controls.Add(_requestPanel);

            // ============================================================
            // RESPONSE PANEL
            // ============================================================
            _responsePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            _responsePanel.Padding = new System.Windows.Forms.Padding(8);
            _responsePanel.Name = "_responsePanel";

            _responseHeader.Dock = System.Windows.Forms.DockStyle.Top;
            _responseHeader.AutoSize = false;
            _responseHeader.Height = 22;
            _responseHeader.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            _responseHeader.Text = "Response";
            _responseHeader.Name = "_responseHeader";

            // ---- Search bar above response tabs -------------------------
            _respSearchPanel.Dock = System.Windows.Forms.DockStyle.Top;
            _respSearchPanel.Height = 30;
            _respSearchPanel.Padding = new System.Windows.Forms.Padding(0, 2, 0, 4);
            _respSearchPanel.Name = "_respSearchPanel";

            _respSearchLbl.Text = "Search:";
            _respSearchLbl.AutoSize = false;
            _respSearchLbl.Width = 52;
            _respSearchLbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _respSearchLbl.Dock = System.Windows.Forms.DockStyle.Left;

            _respSearchBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _respSearchBox.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9F);
            _respSearchBox.Name = "_respSearchBox";
            _respSearchBox.TextChanged += RespSearchBox_TextChanged;
            _respSearchBox.KeyDown += RespSearchBox_KeyDown;

            _btnRespSearchNext.Text = "Find next";
            _btnRespSearchNext.Width = 78;
            _btnRespSearchNext.Dock = System.Windows.Forms.DockStyle.Right;
            _btnRespSearchNext.UseVisualStyleBackColor = true;
            _btnRespSearchNext.Click += BtnRespSearchNext_Click;

            _btnRespSearchClear.Text = "Clear";
            _btnRespSearchClear.Width = 60;
            _btnRespSearchClear.Dock = System.Windows.Forms.DockStyle.Right;
            _btnRespSearchClear.UseVisualStyleBackColor = true;
            _btnRespSearchClear.Click += BtnRespSearchClear_Click;

            _respSearchInfo.Text = "";
            _respSearchInfo.AutoSize = false;
            _respSearchInfo.Width = 150;
            _respSearchInfo.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            _respSearchInfo.ForeColor = System.Drawing.SystemColors.GrayText;
            _respSearchInfo.Dock = System.Windows.Forms.DockStyle.Right;

            // Fill first so docked Left/Right siblings layer above it.
            _respSearchPanel.Controls.Add(_respSearchBox);     // Fill
            _respSearchPanel.Controls.Add(_respSearchInfo);    // Right
            _respSearchPanel.Controls.Add(_btnRespSearchClear);// Right
            _respSearchPanel.Controls.Add(_btnRespSearchNext); // Right
            _respSearchPanel.Controls.Add(_respSearchLbl);     // Left

            // ---- Response tabs ------------------------------------------
            _responseTabs.Dock = System.Windows.Forms.DockStyle.Fill;
            _responseTabs.Font = new System.Drawing.Font("Segoe UI", 9F);
            _responseTabs.Name = "_responseTabs";
            _responseTabs.TabPages.Add(_respTabBody);
            _responseTabs.TabPages.Add(_respTabTree);
            _responseTabs.TabPages.Add(_respTabHeaders);
            _responseTabs.TabPages.Add(_respTabDescription);
            _responseTabs.SelectedIndexChanged += ResponseTabs_SelectedIndexChanged;

            _respTabBody.Text = "Body";
            _respTabBody.Padding = new System.Windows.Forms.Padding(0);
            _respTabBody.UseVisualStyleBackColor = true;
            _respTabBody.Controls.Add(_responseBox);

            _responseBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _responseBox.Multiline = true;
            _responseBox.ReadOnly = true;
            _responseBox.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            _responseBox.WordWrap = false;
            _responseBox.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9.5F);
            _responseBox.HideSelection = false;
            _responseBox.Name = "_responseBox";
            _responseBox.BackColor = System.Drawing.SystemColors.Window;

            _respTabTree.Text = "JSON tree";
            _respTabTree.Padding = new System.Windows.Forms.Padding(0);
            _respTabTree.UseVisualStyleBackColor = true;
            _respTabTree.Controls.Add(_jsonTree);

            _jsonTree.Dock = System.Windows.Forms.DockStyle.Fill;
            _jsonTree.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9F);
            _jsonTree.HideSelection = false;
            _jsonTree.ShowRootLines = true;
            _jsonTree.Name = "_jsonTree";

            _respTabHeaders.Text = "Headers";
            _respTabHeaders.Padding = new System.Windows.Forms.Padding(0);
            _respTabHeaders.UseVisualStyleBackColor = true;
            _respTabHeaders.Controls.Add(_headersBox);

            _headersBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _headersBox.Multiline = true;
            _headersBox.ReadOnly = true;
            _headersBox.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            _headersBox.WordWrap = false;
            _headersBox.Font = new System.Drawing.Font("Cascadia Mono, Consolas", 9F);
            _headersBox.Name = "_headersBox";
            _headersBox.BackColor = System.Drawing.SystemColors.Window;

            _respTabDescription.Text = "Description";
            _respTabDescription.Padding = new System.Windows.Forms.Padding(0);
            _respTabDescription.UseVisualStyleBackColor = true;
            _respTabDescription.Controls.Add(_descriptionBox);

            _descriptionBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _descriptionBox.Multiline = true;
            _descriptionBox.ReadOnly = true;
            _descriptionBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            _descriptionBox.WordWrap = true;
            _descriptionBox.Font = new System.Drawing.Font("Segoe UI", 9.5F);
            _descriptionBox.Name = "_descriptionBox";
            _descriptionBox.BackColor = System.Drawing.SystemColors.Window;

            // Fill first; Top-docked siblings layer above.
            _responsePanel.Controls.Add(_responseTabs);    // Fill
            _responsePanel.Controls.Add(_respSearchPanel); // Top (below header)
            _responsePanel.Controls.Add(_responseHeader);  // Top (edge)

            _rightSplit.Panel2.Controls.Add(_responsePanel);

            _outerSplit.Panel2.Controls.Add(_rightSplit);

            // ---- Status strip (bottom) ----------------------------------
            _statusStrip.SizingGrip = false;
            _statusStrip.Name = "_statusStrip";
            _statusStrip.RenderMode = System.Windows.Forms.ToolStripRenderMode.System;
            _statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
            {
                _statusBarLabel,
                _statusBarElapsed,
                _statusBarProgress
            });

            _statusBarLabel.Name = "_statusBarLabel";
            _statusBarLabel.Text = "Ready.";
            _statusBarLabel.Spring = true;
            _statusBarLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            _statusBarElapsed.Name = "_statusBarElapsed";
            _statusBarElapsed.Text = "";
            _statusBarElapsed.AutoSize = true;
            _statusBarElapsed.Margin = new System.Windows.Forms.Padding(0, 3, 12, 2);

            _statusBarProgress.Name = "_statusBarProgress";
            _statusBarProgress.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            _statusBarProgress.Width = 180;
            _statusBarProgress.Visible = false;
            _statusBarProgress.MarqueeAnimationSpeed = 30;

            // ---- Root ----------------------------------------------------
            // Add Fill FIRST, then Top, then Bottom — that way the docked
            // controls layer correctly above the fill.
            Controls.Add(_outerSplit);   // Fill
            Controls.Add(_toolStrip);    // Top
            Controls.Add(_statusStrip);  // Bottom
            Name = "VerseOpsPluginControl";
            Size = new System.Drawing.Size(1000, 600);

            _statusStrip.ResumeLayout(false);
            _statusStrip.PerformLayout();
            _respTabDescription.ResumeLayout(false);
            _respTabHeaders.ResumeLayout(false);
            _respTabTree.ResumeLayout(false);
            _respTabBody.ResumeLayout(false);
            _responseTabs.ResumeLayout(false);
            _respSearchPanel.ResumeLayout(false);
            _responsePanel.ResumeLayout(false);
            _formScrollHost.ResumeLayout(false);
            _bodyTabRaw.ResumeLayout(false);
            _bodyTabForm.ResumeLayout(false);
            _bodyTabs.ResumeLayout(false);
            _requestButtons.ResumeLayout(false);
            _requestTopStrip.ResumeLayout(false);
            _requestTopStrip.PerformLayout();
            _requestPanel.ResumeLayout(false);
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
