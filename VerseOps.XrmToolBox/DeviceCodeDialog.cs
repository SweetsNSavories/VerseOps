// WinForms designer-style partial. Treated as auto-generated for nullable
// analysis so InitializeComponent's late field assignments don't trip CS8669.
#nullable disable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Modal dialog that surfaces an MSAL device-code: a big readable user
    /// code, the verification URL, and a "copy" button. The plugin's
    /// device-code sign-in callback feeds us the values asynchronously via
    /// <see cref="ShowCode"/>; the dialog stays open until either the user
    /// cancels (raising <see cref="Cancelled"/>) or the caller closes it
    /// after MSAL's polling loop completes.
    /// </summary>
    internal sealed class DeviceCodeDialog : Form
    {
        public event EventHandler Cancelled;

        private readonly Label _instructions;
        private readonly TextBox _codeBox;
        private readonly LinkLabel _verifyLink;
        private readonly Button _copyBtn;
        private readonly Button _openBtn;
        private readonly Button _cancelBtn;

        private string _verificationUrl;

        public DeviceCodeDialog()
        {
            Text = "VerseOps \u2014 device code sign-in";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 240);
            Font = new Font("Segoe UI", 9.75F);
            AutoScaleMode = AutoScaleMode.Dpi;

            _instructions = new Label
            {
                Location = new Point(16, 16),
                Size = new Size(488, 64),
                Text = "Requesting a device code from Microsoft Entra\u2026"
            };

            var codeLabel = new Label
            {
                Location = new Point(16, 84),
                Size = new Size(120, 24),
                Text = "User code:"
            };

            _codeBox = new TextBox
            {
                Location = new Point(16, 108),
                Size = new Size(280, 32),
                Font = new Font("Consolas", 18F, FontStyle.Bold),
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center
            };

            _copyBtn = new Button
            {
                Location = new Point(304, 108),
                Size = new Size(96, 32),
                Text = "Copy",
                Enabled = false
            };
            _copyBtn.Click += (_, __) =>
            {
                if (!string.IsNullOrEmpty(_codeBox.Text))
                    Clipboard.SetText(_codeBox.Text);
            };

            _openBtn = new Button
            {
                Location = new Point(408, 108),
                Size = new Size(96, 32),
                Text = "Open URL",
                Enabled = false
            };
            _openBtn.Click += (_, __) =>
            {
                if (string.IsNullOrEmpty(_verificationUrl)) return;
                try
                {
                    Process.Start(new ProcessStartInfo(_verificationUrl) { UseShellExecute = true });
                }
                catch
                {
                    // Last-ditch: try Process.Start without ShellExecute, no-op on failure.
                    try { Process.Start(_verificationUrl); } catch { /* ignore */ }
                }
            };

            _verifyLink = new LinkLabel
            {
                Location = new Point(16, 152),
                Size = new Size(488, 24),
                Text = string.Empty
            };
            _verifyLink.LinkClicked += (_, __) =>
            {
                if (string.IsNullOrEmpty(_verificationUrl)) return;
                try { Process.Start(new ProcessStartInfo(_verificationUrl) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };

            _cancelBtn = new Button
            {
                Location = new Point(408, 196),
                Size = new Size(96, 28),
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            _cancelBtn.Click += (_, __) =>
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                Close();
            };
            CancelButton = _cancelBtn;

            Controls.Add(_instructions);
            Controls.Add(codeLabel);
            Controls.Add(_codeBox);
            Controls.Add(_copyBtn);
            Controls.Add(_openBtn);
            Controls.Add(_verifyLink);
            Controls.Add(_cancelBtn);
        }

        /// <summary>
        /// Populate the dialog with the device code MSAL just minted. Safe to
        /// call from any thread — callers from MSAL's callback marshal via
        /// BeginInvoke before invoking this.
        /// </summary>
        public void ShowCode(string userCode, string verificationUrl, string message)
        {
            _codeBox.Text = userCode ?? string.Empty;
            _verificationUrl = verificationUrl;
            _verifyLink.Text = verificationUrl ?? string.Empty;
            _verifyLink.Links.Clear();
            if (!string.IsNullOrEmpty(verificationUrl))
            {
                _verifyLink.Links.Add(0, verificationUrl.Length, verificationUrl);
            }
            _instructions.Text = string.IsNullOrEmpty(message)
                ? "Open the URL below on any device, sign in, then enter the user code."
                : message;
            _copyBtn.Enabled = !string.IsNullOrEmpty(userCode);
            _openBtn.Enabled = !string.IsNullOrEmpty(verificationUrl);
        }
    }
}
