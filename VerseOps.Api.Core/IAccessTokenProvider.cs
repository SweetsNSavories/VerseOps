using System.Threading;
using System.Threading.Tasks;

namespace VerseOps.Api.Core
{
    /// <summary>
    /// Minimal contract for token acquisition. Each host (WPF app, XrmToolBox
    /// plugin, test rig) implements this on top of its own MSAL/identity stack
    /// and hands it to <see cref="ApiExecutor"/>. Keeps the catalog + executor
    /// free of UI/host dependencies so they can ship as a netstandard2.0 lib.
    /// </summary>
    public interface IAccessTokenProvider
    {
        /// <summary>
        /// Returns a bearer access token whose <c>aud</c> claim matches
        /// <paramref name="scope"/> (e.g. <c>https://api.powerplatform.com/.default</c>).
        /// May trigger an interactive sign-in on first call per scope.
        /// </summary>
        Task<string> GetTokenAsync(string scope, CancellationToken ct = default);
    }
}
