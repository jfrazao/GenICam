using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>Drop-down editor that lists the vendor+model of every enumerated device for the <c>CameraModel</c> property.</summary>
    internal class CameraModelEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
            UITypeEditorEditStyle.DropDown;

        public override object? EditValue(ITypeDescriptorContext context, IServiceProvider provider, object? value)
        {
            var svc = provider?.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (svc == null || !(context?.Instance is IGenICamSource source)) return value;

            var lb = new ListBox { SelectionMode = SelectionMode.One, Height = 120 };
            lb.Items.Add("(none — select by DeviceIndex only)");

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = string.IsNullOrWhiteSpace(source.ProducerPath) ? null : source.ProducerPath;
                foreach (var info in GenTLLoader.EnumerateAllDeviceInfos(path))
                {
                    string combined = (info.Vendor + " " + info.Model).Trim();
                    if (!string.IsNullOrEmpty(combined) && seen.Add(combined))
                        lb.Items.Add(combined);
                }
            }
            catch { }

            if (value is string cur && !string.IsNullOrEmpty(cur))
            {
                int idx = lb.Items.IndexOf(cur);
                if (idx >= 0) lb.SelectedIndex = idx;
            }

            lb.Click += (s, e) => svc.CloseDropDown();
            svc.DropDownControl(lb);

            if (lb.SelectedIndex <= 0) return null;
            return lb.SelectedItem as string ?? value;
        }
    }

    /// <summary>Drop-down editor that lists the serial number of every enumerated device for the <c>SerialNumber</c> property.</summary>
    internal class SerialNumberEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
            UITypeEditorEditStyle.DropDown;

        public override object? EditValue(ITypeDescriptorContext context, IServiceProvider provider, object? value)
        {
            var svc = provider?.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (svc == null || !(context?.Instance is IGenICamSource source)) return value;

            var lb = new ListBox { SelectionMode = SelectionMode.One, Height = 120 };
            lb.Items.Add("(none — match by model or index)");

            try
            {
                var path = string.IsNullOrWhiteSpace(source.ProducerPath) ? null : source.ProducerPath;
                foreach (var info in GenTLLoader.EnumerateAllDeviceInfos(path))
                {
                    if (!string.IsNullOrEmpty(info.SerialNumber))
                        lb.Items.Add(info.SerialNumber);
                }
            }
            catch { }

            if (value is string cur && !string.IsNullOrEmpty(cur))
            {
                int idx = lb.Items.IndexOf(cur);
                if (idx >= 0) lb.SelectedIndex = idx;
            }

            lb.Click += (s, e) => svc.CloseDropDown();
            svc.DropDownControl(lb);

            if (lb.SelectedIndex <= 0) return null;
            return lb.SelectedItem as string ?? value;
        }
    }
}
