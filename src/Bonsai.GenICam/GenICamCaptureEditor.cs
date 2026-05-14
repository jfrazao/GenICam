using System.ComponentModel;
using System.Windows.Forms;
using Bonsai.Design;

namespace Bonsai.GenICam
{
    public class GenICamCaptureEditor : WorkflowComponentEditor
    {
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
