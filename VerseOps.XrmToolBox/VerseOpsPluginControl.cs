using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VerseOps.XrmToolBox.Auth;
using XrmToolBox.Extensibility;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Root plugin control hosted inside XrmToolBox. PR #3 wires MSAL sign-in
    /// (browser + device-code) on top of a shared MSAL cache with the WPF app.
    /// PR #4 will replace the body label with the operation catalog tree.
    /// </summary>
    public partial class VerseOpsPluginControl : PluginControlBase
    {
        // One PluginAuthService per control instance. The MSAL cache helper
        // underneath is a process-wide singleton, so multiple plugin instances
        // (XrmToolBox supports more than one) still share the same cache file.
        private readonly PluginAuthService _auth = new PluginAuthService();

        public VerseOpsPluginControl()
        {
            InitializeComponent();
            Load += async (_, __) => await ProbeSilentAsync().ConfigureAwait(true);
        }

        // Best-effort silent token probe on plugin load. If the user signed
        // into the WPF app earlier, the shared cache lights up and they see
        // "Signed in as ..." without any prompt. Failures are non-fatal —
        // they just mean the Sign-in buttons stay enabled.
        private async Task ProbeSilentAsync()
        {
            try
            {
                SetStatus("Checking for cached sign-in\u2026", busy: true);
                var token = await _auth.TryGetTokenSilentAsync(
                    PluginAuthService.ScopePpac, CancellationToken.None).ConfigureAwait(true);
                if (token != null && _auth.LastSignedInUser != null)
                {
                    SetSignedIn(_auth.LastSignedInUser, "silent (shared cache)");
                }
                else
                {
                    SetSignedOut("No cached sign-in. Click Sign in to authenticate.");
                }
            }
            catch (Exception ex)
            {
                SetSignedOut("Silent check failed: " + ex.Message);
            }
        }

        private async void BtnSignIn_Click(object sender, EventArgs e)
        {
            try
            {
                SetStatus("Opening system browser\u2026", busy: true);
                var token = await _auth.SignInInteractiveAsync(
                    PluginAuthService.ScopePpac, CancellationToken.None).ConfigureAwait(true);
                if (token != null && _auth.LastSignedInUser != null)
                {
                    SetSignedIn(_auth.LastSignedInUser, "browser");
                }
                else
                {
                    SetSignedOut("Sign-in returned without a token.");
                }
            }
            catch (OperationCanceledException)
            {
                SetSignedOut("Sign-in cancelled.");
            }
            catch (Exception ex)
            {
                SetSignedOut("Sign-in failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "VerseOps sign-in",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnSignInDeviceCode_Click(object sender, EventArgs e)
        {
            using (var dlg = new DeviceCodeDialog())
            {
                var cts = new CancellationTokenSource();
                dlg.Cancelled += (_, __) => cts.Cancel();

                SetStatus("Requesting device code\u2026", busy: true);
                var signInTask = _auth.SignInDeviceCodeAsync(
                    PluginAuthService.ScopePpac,
                    msg =>
                    {
                        // Marshal MSAL's callback back onto the UI thread to
                        // update the dialog text safely.
                        if (dlg.IsHandleCreated)
                        {
                            dlg.BeginInvoke((MethodInvoker)(() => dlg.ShowCode(msg.UserCode, msg.VerificationUrl, msg.Message)));
                        }
                        return Task.CompletedTask;
                    },
                    cts.Token);

                // Show the dialog modally; MSAL keeps polling AAD until the
                // user finishes the flow or the dialog is cancelled.
                dlg.ShowDialog(this);

                try
                {
                    var token = await signInTask.ConfigureAwait(true);
                    if (token != null && _auth.LastSignedInUser != null)
                    {
                        SetSignedIn(_auth.LastSignedInUser, "device code");
                    }
                    else
                    {
                        SetSignedOut("Sign-in returned without a token.");
                    }
                }
                catch (OperationCanceledException)
                {
                    SetSignedOut("Device-code sign-in cancelled.");
                }
                catch (Exception ex)
                {
                    SetSignedOut("Device-code sign-in failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "VerseOps sign-in",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async void BtnSignOut_Click(object sender, EventArgs e)
        {
            var ok = MessageBox.Show(this,
                "Sign out of VerseOps?\r\n\r\n" +
                "This wipes the shared MSAL cache and also signs out the VerseOps WPF app.",
                "VerseOps sign-out",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;

            try
            {
                await _auth.SignOutAsync().ConfigureAwait(true);
                SetSignedOut("Signed out.");
            }
            catch (Exception ex)
            {
                SetSignedOut("Sign-out failed: " + ex.Message);
            }
        }

        private void SetStatus(string text, bool busy)
        {
            _statusLabel.Text = text;
            _btnSignIn.Enabled = !busy;
            _btnSignInDeviceCode.Enabled = !busy;
            _btnSignOut.Enabled = false;
        }

        private void SetSignedIn(string user, string method)
        {
            _statusLabel.Text = "Signed in as " + user + " (" + method + ")";
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = true;
        }

        private void SetSignedOut(string note)
        {
            _statusLabel.Text = note;
            _btnSignIn.Enabled = true;
            _btnSignInDeviceCode.Enabled = true;
            _btnSignOut.Enabled = false;
        }
    }
}
