using System.ComponentModel;
using System.Windows.Forms;
using Bonsai.Design;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Provides the double-click feature editor for <see cref="GenICamDevice"/> in the Bonsai designer.
    /// </summary>
    public class GenICamDeviceEditor : WorkflowComponentEditor
    {
        /// <summary>Opens the <see cref="FeatureConfiguration"/> editor dialog for the selected <see cref="GenICamDevice"/> operator.</summary>
        public override bool EditComponent(ITypeDescriptorContext context, object component,
            System.IServiceProvider provider, IWin32Window owner)
        {
            var device = (GenICamDevice)component;
            using var form = new FeatureConfigurationForm(device.Features, device);
            form.ShowDialog(owner);
            return true;
        }
    }
}
