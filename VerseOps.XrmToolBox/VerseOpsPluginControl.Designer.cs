// WinForms designer-style partial. Treated as auto-generated for nullable
// analysis so the field initializers (set inside InitializeComponent) don't
// trip CS8669 under the repo-wide <Nullable>enable</Nullable>.
#nullable disable
namespace VerseOps.XrmToolBox
{
    partial class VerseOpsPluginControl
    {
        private System.Windows.Forms.ToolStrip _toolStrip;
        private System.Windows.Forms.ToolStripButton _btnSignIn;
        private System.Windows.Forms.ToolStripButton _btnSignInDeviceCode;
        private System.Windows.Forms.ToolStripButton _btnSignOut;
        private System.Windows.Forms.ToolStripSeparator _sep1;
        private System.Windows.Forms.ToolStripLabel _statusLabel;
        private System.Windows.Forms.Label _bodyLabel;

        private void InitializeComponent()
        {
            _toolStrip            = new System.Windows.Forms.ToolStrip();
            _btnSignIn            = new System.Windows.Forms.ToolStripButton();
            _btnSignInDeviceCode  = new System.Windows.Forms.ToolStripButton();
            _btnSignOut           = new System.Windows.Forms.ToolStripButton();
            _sep1                 = new System.Windows.Forms.ToolStripSeparator();
            _statusLabel          = new System.Windows.Forms.ToolStripLabel();
            _bodyLabel            = new System.Windows.Forms.Label();

            _toolStrip.SuspendLayout();
            SuspendLayout();

            // _toolStrip
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
            _toolStrip.Size = new System.Drawing.Size(800, 27);
            _toolStrip.TabIndex = 0;
            _toolStrip.Text = "toolStrip1";

            // _btnSignIn
            _btnSignIn.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignIn.Name = "_btnSignIn";
            _btnSignIn.Text = "Sign in (browser)";
            _btnSignIn.ToolTipText = "Opens the system browser for an MSAL interactive sign-in. Token cache is shared with the VerseOps WPF app.";
            _btnSignIn.Click += BtnSignIn_Click;

            // _btnSignInDeviceCode
            _btnSignInDeviceCode.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignInDeviceCode.Name = "_btnSignInDeviceCode";
            _btnSignInDeviceCode.Text = "Sign in (device code)";
            _btnSignInDeviceCode.ToolTipText = "MSAL device-code flow \u2014 copy the user code into microsoft.com/devicelogin on any browser. Use when the host has no usable browser.";
            _btnSignInDeviceCode.Click += BtnSignInDeviceCode_Click;

            // _btnSignOut
            _btnSignOut.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            _btnSignOut.Enabled = false;
            _btnSignOut.Name = "_btnSignOut";
            _btnSignOut.Text = "Sign out";
            _btnSignOut.ToolTipText = "Clears the MSAL cache (also signs out the VerseOps WPF app \u2014 shared cache).";
            _btnSignOut.Click += BtnSignOut_Click;

            // _statusLabel
            _statusLabel.Name = "_statusLabel";
            _statusLabel.Text = "Initialising\u2026";

            // _bodyLabel
            _bodyLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            _bodyLabel.Font = new System.Drawing.Font("Segoe UI", 11F);
            _bodyLabel.Padding = new System.Windows.Forms.Padding(16);
            _bodyLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            _bodyLabel.Text =
                "VerseOps API Explorer\r\n\r\n" +
                "MSAL sign-in is wired (PR #3). The operation catalog tree and request panel " +
                "land in PR #4. Sign in to verify your token cache is reachable from inside XrmToolBox.";

            // VerseOpsPluginControl
            Controls.Add(_bodyLabel);
            Controls.Add(_toolStrip);
            Name = "VerseOpsPluginControl";
            Size = new System.Drawing.Size(800, 450);

            _toolStrip.ResumeLayout(false);
            _toolStrip.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
