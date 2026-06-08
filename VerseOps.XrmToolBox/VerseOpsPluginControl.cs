using XrmToolBox.Extensibility;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// Root plugin control hosted inside XrmToolBox. Scaffold only — PR #3
    /// wires MSAL + ConnectionDetail, PR #4 builds the full TreeView UI.
    /// </summary>
    public partial class VerseOpsPluginControl : PluginControlBase
    {
        public VerseOpsPluginControl()
        {
            InitializeComponent();
        }
    }
}
