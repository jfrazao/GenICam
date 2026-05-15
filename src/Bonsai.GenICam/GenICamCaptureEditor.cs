using System.ComponentModel;
using System.Windows.Forms;
using Bonsai.Design;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Provides the double-click feature editor for <see cref="GenICamCapture"/> in the Bonsai designer.
    /// </summary>
    public class GenICamCaptureEditor : WorkflowComponentEditor
    {
        /// <summary>Opens the <see cref="FeatureConfiguration"/> editor dialog for the selected <see cref="GenICamCapture"/> operator.</summary>
        public override bool EditComponent(ITypeDescriptorContext context, object component,
            System.IServiceProvider provider, IWin32Window owner)
        {
            var capture = (GenICamCapture)component;
            using var form = new FeatureConfigurationForm(capture.Features, capture);
            form.ShowDialog(owner);
            return true;
        }
    }
}
