// WinForms designer-style partial. Treated as auto-generated for nullable
// analysis so the field initializers (set inside InitializeComponent) don't
// trip CS8669 under the repo-wide <Nullable>enable</Nullable>.
#nullable disable
namespace VerseOps.XrmToolBox
{
    partial class VerseOpsPluginControl
    {
        private System.Windows.Forms.Label _helloLabel;

        private void InitializeComponent()
        {
            _helloLabel = new System.Windows.Forms.Label();
            SuspendLayout();
            _helloLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            _helloLabel.Font = new System.Drawing.Font("Segoe UI", 16F);
            _helloLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            _helloLabel.Text = "VerseOps API Explorer — scaffold loaded.\r\n\r\nMSAL auth and the operation tree ship in the next two PRs.";
            Controls.Add(_helloLabel);
            Name = "VerseOpsPluginControl";
            Size = new System.Drawing.Size(800, 450);
            ResumeLayout(false);
        }
    }
}
