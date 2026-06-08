using System.ComponentModel.Composition;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace VerseOps.XrmToolBox
{
    /// <summary>
    /// MEF entry point. XrmToolBox discovers plugins by composing every
    /// <see cref="IXrmToolBoxPlugin"/> export it finds under
    /// <c>%appdata%\MscrmTools\XrmToolBox\Plugins\</c>. The ExportMetadata
    /// values populate the plugin tile on the XrmToolBox landing page.
    /// </summary>
    [Export(typeof(IXrmToolBoxPlugin)),
     ExportMetadata("Name", "VerseOps API Explorer"),
     ExportMetadata("Description", "Power Platform PPAC/BAP REST API explorer — same catalog as the standalone VerseOps app, embedded in XrmToolBox."),
     ExportMetadata("BackgroundColor", "DimGray"),
     ExportMetadata("PrimaryFontColor", "White"),
     ExportMetadata("SecondaryFontColor", "Gainsboro"),
     ExportMetadata("SmallImageBase64", ""),
     ExportMetadata("BigImageBase64", "")]
    public class VerseOpsPluginFactory : PluginBase
    {
        public override IXrmToolBoxPluginControl GetControl()
        {
            return new VerseOpsPluginControl();
        }
    }
}
