using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using System.Xml.Serialization;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;
using static Bonsai.GenICam.GenApi.NodeRepresentation;
using static Bonsai.GenICam.GenApi.NodeVisibility;

namespace Bonsai.GenICam
{
    internal interface IGenICamSource
    {
        string? ProducerPath { get; }
        int DeviceIndex { get; }
        string? CameraModel { get; }
        string? SerialNumber { get; }
        NodeMap? LiveNodeMap { get; }
    }

    /// <summary>
    /// Stores a single named feature override that is applied to the camera at workflow startup.
    /// </summary>
    public class FeatureOverride
    {
        /// <summary>Gets or sets the GenICam feature name.</summary>
        [XmlAttribute("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the value to write, expressed as a string and coerced to the node type at runtime.</summary>
        [XmlAttribute("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Holds the list of feature overrides applied to a camera before acquisition starts.
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class FeatureConfiguration
    {
        /// <summary>Gets the collection of feature overrides serialized into the workflow file.</summary>
        [Browsable(false)]
        [XmlElement("Feature")]
        public List<FeatureOverride> Overrides { get; } = new List<FeatureOverride>();

        /// <summary>Returns a summary string showing the number of startup overrides.</summary>
        public override string ToString() =>
            Overrides.Count == 0 ? "No startup overrides" : $"{Overrides.Count} startup override(s)";

        internal void SetOverride(string name, string value)
        {
            var existing = Overrides.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
            if (existing != null) existing.Value = value;
            else Overrides.Add(new FeatureOverride { Name = name, Value = value });
        }

        internal void RemoveOverride(string name) =>
            Overrides.RemoveAll(o => string.Equals(o.Name, name, StringComparison.Ordinal));

        internal bool HasOverride(string name) =>
            Overrides.Any(o => string.Equals(o.Name, name, StringComparison.Ordinal));

        internal void Apply(NodeMap map)
        {
            foreach (var ov in Overrides)
            {
                try { map.Write(ov.Name, ov.Value); }
                catch { }
            }
        }
    }

    internal enum FeatureKind { Text, Integer, Float, Enumeration, Boolean, Command }

    internal class FeatureDisplayEntry
    {
        public string Name { get; }
        public string? DisplayValue { get; set; }
        public bool Overridden { get; set; }
        public bool Writable { get; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Unit { get; set; }
        internal FeatureKind Kind { get; set; }
        internal NodeVisibility Visibility { get; set; } = NodeVisibility.Beginner;
        internal NodeRepresentation Representation { get; set; } = NodeRepresentation.Linear;
        internal int DecimalPlaces { get; set; }
        internal IReadOnlyList<string>? EnumEntries { get; set; }
        internal double? MinValue { get; set; }
        internal double? MaxValue { get; set; }
        internal double? StepValue { get; set; }

        public FeatureDisplayEntry(string name, string? displayValue, bool writable)
        {
            Name = name;
            DisplayValue = displayValue;
            Writable = writable;
        }

        internal static string? ValueToString(object? value, NodeRepresentation rep = NodeRepresentation.Linear, int decimalPlaces = -1)
        {
            if (rep == NodeRepresentation.HexNumber)
            {
                if (value is long l)   return $"0x{l:X}";
                if (value is int i)    return $"0x{i:X}";
                if (value is double dh) return $"0x{(long)Math.Round(dh):X}";
            }
            if (value is double d)
            {
                if (decimalPlaces == 0) return ((long)Math.Round(d)).ToString();
                if (decimalPlaces > 0)  return Math.Round(d, decimalPlaces).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value is float f)
            {
                if (decimalPlaces == 0) return ((long)Math.Round(f)).ToString();
                if (decimalPlaces > 0)  return Math.Round(f, decimalPlaces).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return value?.ToString();
        }
    }

    internal class FeatureConfigurationEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
            UITypeEditorEditStyle.Modal;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context?.Instance is IGenICamSource source)
            {
                var config = value as FeatureConfiguration ?? new FeatureConfiguration();
                using (var form = new FeatureConfigurationForm(config, source))
                    form.ShowDialog();
                return value;
            }
            return value;
        }
    }

    internal class FeatureConfigurationForm : Form
    {
        private readonly DataGridView grid;
        private readonly ListBox categoryList;
        private readonly SplitContainer splitContainer;
        private readonly GroupBox categoryDescGroup;
        private readonly TextBox categoryDescBox;
        private readonly GroupBox featureDescGroup;
        private readonly TextBox featureDescBox;
        private readonly Button refreshButton;
        private readonly Button closeButton;
        private readonly Label deviceInfoLabel;
        private readonly ComboBox overlayCombo;
        private readonly ComboBox visibilityCombo;
        private readonly Panel overlayNumericPanel;
        private readonly NumericUpDown overlayNumeric;
        private readonly TrackBar overlayTrackBar;
        private readonly IGenICamSource _source;
        private readonly Dictionary<string, string> _categoryDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _suppressSync;
        private bool _suppressCombo;
        private string? _selectedCategory;
        private FeatureDisplayEntry? _overlayEntry;
        private int _overlayRowIndex = -1;
        private int _valueColumnIndex;
        private int _overrideColumnIndex;
        private List<FeatureDisplayEntry> _displayEntries = new List<FeatureDisplayEntry>();
        private DeviceContext? _designContext;

        internal FeatureConfiguration Configuration { get; }

        internal FeatureConfigurationForm(FeatureConfiguration configuration, IGenICamSource source)
        {
            Configuration = configuration;
            _source = source;

            Text = "GenICam Feature Editor";
            Width = 950; Height = 620;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Feature", DataPropertyName = "Name", ReadOnly = true, FillWeight = 4 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", DataPropertyName = "DisplayValue", ReadOnly = false, FillWeight = 4 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Unit", HeaderText = "Unit", DataPropertyName = "Unit", ReadOnly = true, FillWeight = 1 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Override", HeaderText = "Startup", DataPropertyName = "Overridden", ReadOnly = true, FillWeight = 1 });
            _valueColumnIndex    = grid.Columns["Value"].Index;
            _overrideColumnIndex = grid.Columns["Override"].Index;

            grid.CellBeginEdit   += Grid_CellBeginEdit;
            grid.CellEndEdit     += Grid_CellEndEdit;
            grid.CellPainting    += Grid_CellPainting;
            grid.CellClick       += Grid_CellClick;
            grid.SelectionChanged += Grid_SelectionChanged;
            grid.Scroll          += (s, e) => HideOverlays();

            overlayCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
            overlayCombo.SelectedIndexChanged += OverlayCombo_SelectedIndexChanged;
            overlayCombo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) HideOverlays(); };

            overlayNumeric = new NumericUpDown { Minimum = -1e15m, Maximum = 1e15m, Increment = 1 };
            overlayNumeric.ValueChanged += OverlayNumeric_ValueChanged;
            overlayNumeric.Leave        += OverlayNumericPanel_Leave;
            overlayNumeric.KeyDown      += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                { CommitNumericValue(); overlayNumericPanel!.Visible = false; _overlayEntry = null; _overlayRowIndex = -1; grid.Focus(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape)
                { _overlayEntry = null; overlayNumericPanel!.Visible = false; _overlayRowIndex = -1; grid.Focus(); e.Handled = true; }
            };

            overlayTrackBar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Height = 22, Visible = false };
            overlayTrackBar.Scroll += OverlayTrackBar_Scroll;
            overlayTrackBar.Leave  += OverlayNumericPanel_Leave;
            overlayTrackBar.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                { _overlayEntry = null; overlayNumericPanel!.Visible = false; _overlayRowIndex = -1; grid.Focus(); e.Handled = true; }
            };

            overlayNumericPanel = new Panel { Visible = false };
            overlayNumericPanel.Controls.Add(overlayNumeric);
            overlayNumericPanel.Controls.Add(overlayTrackBar);

            deviceInfoLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 6, 0),
                Text = "Connecting..."
            };
            var deviceInfoPanel = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = SystemColors.Info, Padding = new Padding(2) };
            deviceInfoPanel.Controls.Add(deviceInfoLabel);

            refreshButton = new Button { Text = "Refresh", Width = 100, Height = 28 };
            closeButton   = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 100, Height = 28 };
            refreshButton.Click += (s, e) => RefreshFromDevice();

            visibilityCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            visibilityCombo.Items.AddRange(new object[] { "All", "Beginner", "Expert", "Guru" });
            visibilityCombo.SelectedIndex = 0;
            visibilityCombo.SelectedIndexChanged += (s, e) => UpdateGrid();

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom, Height = 42,
                Padding = new Padding(5, 7, 5, 7), WrapContents = false
            };
            buttonPanel.Controls.Add(closeButton);
            buttonPanel.Controls.Add(refreshButton);
            // In RightToLeft flow, last-added controls appear leftmost; add combo before label
            buttonPanel.Controls.Add(visibilityCombo);
            buttonPanel.Controls.Add(new Label { Text = "Show:", AutoSize = true, Padding = new Padding(4, 6, 2, 0) });

            categoryList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            categoryList.SelectedIndexChanged += CategoryList_SelectedIndexChanged;

            categoryDescBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
            categoryDescGroup = new GroupBox { Dock = DockStyle.Bottom, Height = 80, Text = "Category" };
            categoryDescGroup.Controls.Add(categoryDescBox);

            featureDescBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
            featureDescGroup = new GroupBox { Dock = DockStyle.Bottom, Height = 80, Text = "Feature" };
            featureDescGroup.Controls.Add(featureDescBox);

            splitContainer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Panel1MinSize = 120, Panel2MinSize = 100 };
            splitContainer.Panel1.Controls.Add(categoryList);
            splitContainer.Panel1.Controls.Add(categoryDescGroup);
            splitContainer.Panel2.Controls.Add(grid);
            splitContainer.Panel2.Controls.Add(featureDescGroup);
            splitContainer.Panel2.Controls.Add(overlayCombo);
            splitContainer.Panel2.Controls.Add(overlayNumericPanel);

            Controls.Add(splitContainer);
            Controls.Add(buttonPanel);
            Controls.Add(deviceInfoPanel);

            Load += OnLoad;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            splitContainer.SplitterDistance = 200;
            if (_source.LiveNodeMap == null)
            {
                try { _designContext = OpenDevice(); }
                catch (Exception ex) { deviceInfoLabel.Text = $"Cannot connect — {ex.Message}"; }
            }
            RefreshFromDevice();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _designContext?.Dispose();
            _designContext = null;
        }

        private void Grid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _valueColumnIndex) { e.Cancel = true; return; }
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureDisplayEntry;
            if (entry == null || !entry.Writable || entry.Kind != FeatureKind.Text) e.Cancel = true;
        }

        private void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _valueColumnIndex) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is FeatureDisplayEntry entry)
                WriteLiveOrDesign(entry, entry.DisplayValue ?? string.Empty);
        }

        private void CategoryList_SelectedIndexChanged(object sender, EventArgs e)
        {
            _selectedCategory = categoryList.SelectedIndex > 0 ? categoryList.SelectedItem?.ToString() : null;
            categoryDescGroup.Text = _selectedCategory != null ? $"Category: {_selectedCategory}" : "Category";
            if (_selectedCategory != null)
            { _categoryDescriptions.TryGetValue(_selectedCategory, out string desc); categoryDescBox.Text = desc ?? ""; }
            else categoryDescBox.Text = "";
            UpdateGrid();
        }

        private void Grid_SelectionChanged(object sender, EventArgs e)
        {
            HideOverlays();
            if (grid.SelectedRows.Count == 0 || !(grid.SelectedRows[0].DataBoundItem is FeatureDisplayEntry entry))
            { featureDescGroup.Text = "Feature"; featureDescBox.Text = ""; return; }
            featureDescGroup.Text = $"Feature: {entry.Name}";
            featureDescBox.Text   = entry.Description ?? "";
        }

        private void RefreshFromDevice()
        {
            var map = _source.LiveNodeMap ?? _designContext?.Map;
            if (map == null)
            {
                ShowOverridesOnly();
                return;
            }
            try
            {
                _categoryDescriptions.Clear();
                _displayEntries = BuildDisplayEntries(map);
                if (_source.LiveNodeMap != null) deviceInfoLabel.Text = "Live (workflow running)";
                else if (_designContext != null) UpdateDeviceInfoLabel(_designContext.Info);
                UpdateCategoryList();
                UpdateGrid();
            }
            catch (Exception ex)
            {
                deviceInfoLabel.Text = $"Read error — {ex.Message}";
                ShowOverridesOnly();
            }
        }

        private List<FeatureDisplayEntry> BuildDisplayEntries(NodeMap map)
        {
            var categories   = map.GetCategories();
            var featCategory = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in categories)
                foreach (string feat in kv.Value)
                    if (!featCategory.ContainsKey(feat))
                        featCategory[feat] = kv.Key;

            foreach (string cat in categories.Keys)
                _categoryDescriptions[cat] = map.GetCategoryDescription(cat);

            var entries = new List<FeatureDisplayEntry>();
            foreach (var fv in map.TryReadAll())
            {
                bool writable   = map.CanWrite(fv.Name);
                featCategory.TryGetValue(fv.Name, out string category);
                var kind        = map.GetNodeKind(fv.Name);
                var vis         = map.GetNodeVisibility(fv.Name);
                var rep         = map.GetNodeRepresentation(fv.Name);
                var unit        = map.GetNodeUnit(fv.Name);
                var enumEntries = kind == FeatureKind.Enumeration ? map.GetEnumEntries(fv.Name) : null;
                double? min = null, max = null, step = null;
                if (kind == FeatureKind.Integer || kind == FeatureKind.Float)
                { var lim = map.GetNodeLimits(fv.Name); min = lim.min; max = lim.max; step = lim.step; }
                int dp = kind == FeatureKind.Integer ? 0 : (map.GetNodeDisplayPrecision(fv.Name) ?? 6);

                var entry = new FeatureDisplayEntry(fv.Name, FeatureDisplayEntry.ValueToString(fv.Value, rep, dp), writable)
                {
                    Category = category, Description = map.GetNodeDescription(fv.Name),
                    Kind = kind, Visibility = vis, Representation = rep, Unit = unit,
                    DecimalPlaces = dp, EnumEntries = enumEntries,
                    MinValue = min, MaxValue = max, StepValue = step,
                    Overridden = Configuration.HasOverride(fv.Name)
                };
                entries.Add(entry);
            }

            foreach (string cmdName in map.GetCommandNodeNames())
            {
                featCategory.TryGetValue(cmdName, out string cmdCat);
                entries.Add(new FeatureDisplayEntry(cmdName, null, true)
                {
                    Kind = FeatureKind.Command, Category = cmdCat,
                    Visibility = map.GetNodeVisibility(cmdName),
                    Description = map.GetNodeDescription(cmdName),
                    Overridden = false
                });
            }
            return entries;
        }

        private void ShowOverridesOnly()
        {
            _displayEntries = Configuration.Overrides
                .Select(o => new FeatureDisplayEntry(o.Name, o.Value, true) { Overridden = true })
                .ToList();
            deviceInfoLabel.Text = "Camera not connected — showing stored startup overrides only";
            UpdateCategoryList();
            UpdateGrid();
        }

        private void UpdateDeviceInfoLabel(DeviceInfo? info)
        {
            if (info == null) { deviceInfoLabel.Text = "No device"; return; }
            var name = !string.IsNullOrEmpty(info.DisplayName) ? info.DisplayName
                : string.Join(" ", new[] { info.Vendor, info.Model }.Where(s => !string.IsNullOrEmpty(s)));
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(info.SerialNumber)) parts.Add($"S/N: {info.SerialNumber}");
            if (!string.IsNullOrEmpty(info.TLType)) parts.Add(info.TLType);
            deviceInfoLabel.Text = parts.Count > 0 ? $"{name}    {string.Join("    ", parts)}" : name;
        }

        private void UpdateCategoryList()
        {
            categoryList.SelectedIndexChanged -= CategoryList_SelectedIndexChanged;
            try
            {
                string? saved = _selectedCategory;
                categoryList.Items.Clear();
                categoryList.Items.Add("All");
                foreach (string cat in _displayEntries.Where(e => e.Category != null).Select(e => e.Category!).Distinct().OrderBy(c => c))
                    categoryList.Items.Add(cat);
                int idx = saved != null ? categoryList.Items.IndexOf(saved) : 0;
                categoryList.SelectedIndex = idx >= 0 ? idx : 0;
                _selectedCategory = idx > 0 ? saved : null;
            }
            finally { categoryList.SelectedIndexChanged += CategoryList_SelectedIndexChanged; }
        }

        private void UpdateGrid()
        {
            var entries = _selectedCategory == null
                ? _displayEntries
                : _displayEntries.Where(e => string.Equals(e.Category, _selectedCategory, StringComparison.Ordinal)).ToList();

            // Always hide Invisible features; then apply the Show filter
            NodeVisibility maxVis = visibilityCombo.SelectedIndex switch
            {
                1 => Beginner,
                2 => Expert,
                3 => Guru,
                _ => Guru   // "All" → show up to Guru (Invisible always hidden)
            };
            entries = entries.Where(e => e.Visibility != Invisible && e.Visibility <= maxVis).ToList();

            grid.DataSource = null;
            grid.DataSource = new BindingList<FeatureDisplayEntry>(entries);
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureDisplayEntry;
            if (entry == null) return;

            if (e.ColumnIndex == _overrideColumnIndex)
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
                var state = entry.Writable
                    ? (entry.Overridden ? CheckBoxState.CheckedNormal   : CheckBoxState.UncheckedNormal)
                    : (entry.Overridden ? CheckBoxState.CheckedDisabled  : CheckBoxState.UncheckedDisabled);
                var sz = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                var pt = new Point(e.CellBounds.X + (e.CellBounds.Width - sz.Width) / 2,
                                   e.CellBounds.Y + (e.CellBounds.Height - sz.Height) / 2);
                CheckBoxRenderer.DrawCheckBox(e.Graphics, pt, state);
                e.Handled = true;
                return;
            }

            if (e.ColumnIndex == _valueColumnIndex)
            {
                if (entry.Kind == FeatureKind.Boolean)
                {
                    e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
                    bool isChecked = string.Equals(entry.DisplayValue, "True", StringComparison.OrdinalIgnoreCase);
                    var state = entry.Writable
                        ? (isChecked ? CheckBoxState.CheckedNormal   : CheckBoxState.UncheckedNormal)
                        : (isChecked ? CheckBoxState.CheckedDisabled : CheckBoxState.UncheckedDisabled);
                    var sz = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                    var pt = new Point(e.CellBounds.X + (e.CellBounds.Width - sz.Width) / 2,
                                       e.CellBounds.Y + (e.CellBounds.Height - sz.Height) / 2);
                    CheckBoxRenderer.DrawCheckBox(e.Graphics, pt, state);
                    e.Handled = true;
                }
                else if (entry.Kind == FeatureKind.Command && entry.Writable)
                {
                    e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
                    var r = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y + 2, Math.Min(90, e.CellBounds.Width - 8), e.CellBounds.Height - 4);
                    ButtonRenderer.DrawButton(e.Graphics, r, "Execute", grid.Font, false, PushButtonState.Normal);
                    e.Handled = true;
                }
            }
        }

        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureDisplayEntry;
            if (entry == null) return;

            if (e.ColumnIndex == _overrideColumnIndex && entry.Writable)
            {
                entry.Overridden = !entry.Overridden;
                if (entry.Overridden) Configuration.SetOverride(entry.Name, entry.DisplayValue ?? string.Empty);
                else Configuration.RemoveOverride(entry.Name);
                RefreshBoundRow(e.RowIndex);
                return;
            }

            if (e.ColumnIndex != _valueColumnIndex || !entry.Writable) return;
            switch (entry.Kind)
            {
                case FeatureKind.Boolean:
                    entry.DisplayValue = string.Equals(entry.DisplayValue, "True", StringComparison.OrdinalIgnoreCase) ? "False" : "True";
                    WriteLiveOrDesign(entry, entry.DisplayValue);
                    break;
                case FeatureKind.Command:
                    ExecuteCommandNode(entry);
                    break;
                case FeatureKind.Enumeration:
                    ShowEnumOverlay(e.RowIndex, entry);
                    break;
                case FeatureKind.Integer:
                case FeatureKind.Float:
                    ShowNumericOverlay(e.RowIndex, entry);
                    break;
            }
        }

        private void ShowEnumOverlay(int rowIndex, FeatureDisplayEntry entry)
        {
            HideOverlays();
            var bounds = grid.GetCellDisplayRectangle(_valueColumnIndex, rowIndex, true);
            if (bounds.IsEmpty) return;
            _overlayEntry = entry; _overlayRowIndex = rowIndex;
            overlayCombo.Items.Clear();
            if (entry.EnumEntries != null)
                foreach (string item in entry.EnumEntries) overlayCombo.Items.Add(item);
            _suppressCombo = true;
            overlayCombo.SelectedItem = entry.DisplayValue;
            _suppressCombo = false;
            overlayCombo.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            overlayCombo.Visible = true;
            overlayCombo.BringToFront();
            overlayCombo.Focus();
            BeginInvoke(new Action(() => { if (overlayCombo.Visible) overlayCombo.DroppedDown = true; }));
        }

        private void ShowNumericOverlay(int rowIndex, FeatureDisplayEntry entry)
        {
            HideOverlays();
            var bounds = grid.GetCellDisplayRectangle(_valueColumnIndex, rowIndex, true);
            if (bounds.IsEmpty) return;
            _overlayEntry = entry; _overlayRowIndex = rowIndex;

            int dp = entry.DecimalPlaces;
            bool isHex = entry.Representation == HexNumber;
            overlayNumeric.DecimalPlaces  = isHex ? 0 : dp;
            overlayNumeric.Hexadecimal    = isHex;
            overlayNumeric.Minimum = entry.MinValue.HasValue ? (decimal)Math.Max((double)decimal.MinValue / 2, entry.MinValue.Value)
                                   : (dp == 0 ? (decimal)long.MinValue : -1e15m);
            overlayNumeric.Maximum = entry.MaxValue.HasValue ? (decimal)Math.Min((double)decimal.MaxValue / 2, entry.MaxValue.Value)
                                   : (dp == 0 ? (decimal)long.MaxValue :  1e15m);
            overlayNumeric.Increment = entry.StepValue.HasValue ? (decimal)entry.StepValue.Value : 1;

            _suppressSync = true;
            // Parse hex display values ("0x1A2B") or plain numbers
            string rawVal = entry.DisplayValue ?? "0";
            if (isHex && rawVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                rawVal = ((long)Convert.ToUInt64(rawVal.Substring(2), 16)).ToString();
            if (decimal.TryParse(rawVal, System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture, out decimal v))
                overlayNumeric.Value = Math.Max(overlayNumeric.Minimum, Math.Min(overlayNumeric.Maximum, v));
            _suppressSync = false;

            bool hasLimits = entry.MinValue.HasValue && entry.MaxValue.HasValue &&
                             entry.MaxValue.Value > entry.MinValue.Value;
            int panelH = bounds.Height;
            if (hasLimits)
            {
                int tick = ComputeTick(entry, (double)overlayNumeric.Value);
                _suppressSync = true;
                overlayTrackBar.Value = Math.Max(0, Math.Min(1000, tick));
                _suppressSync = false;
                overlayTrackBar.SetBounds(0, bounds.Height, bounds.Width, overlayTrackBar.Height);
                overlayTrackBar.Visible = true;
                panelH = bounds.Height + overlayTrackBar.Height;
            }
            else overlayTrackBar.Visible = false;

            overlayNumeric.SetBounds(0, 0, bounds.Width, bounds.Height);
            overlayNumericPanel.SetBounds(bounds.X, bounds.Y, bounds.Width, panelH);
            overlayNumericPanel.Visible = true;
            overlayNumericPanel.BringToFront();
            overlayNumeric.Focus();
        }

        // Convert a value to a 0-1000 trackbar tick, respecting log scale.
        private int ComputeTick(FeatureDisplayEntry entry, double val)
        {
            double min = entry.MinValue!.Value, max = entry.MaxValue!.Value;
            if (entry.Representation == Logarithmic && min > 0 && max > 0)
            {
                double logMin = Math.Log10(min), logMax = Math.Log10(max);
                double logVal = Math.Log10(Math.Max(min, Math.Max(double.Epsilon, val)));
                return (int)((logVal - logMin) / (logMax - logMin) * 1000);
            }
            return (int)((val - min) / (max - min) * 1000);
        }

        // Convert a 0-1000 trackbar tick back to a value, respecting log scale.
        private double TickToValue(FeatureDisplayEntry entry, int tick)
        {
            double min = entry.MinValue!.Value, max = entry.MaxValue!.Value;
            if (entry.Representation == Logarithmic && min > 0 && max > 0)
            {
                double logMin = Math.Log10(min), logMax = Math.Log10(max);
                return Math.Pow(10, logMin + (tick / 1000.0) * (logMax - logMin));
            }
            return min + (tick / 1000.0) * (max - min);
        }

        private void HideOverlays()
        {
            if (overlayCombo.Visible)        { CommitComboValue();   overlayCombo.Visible        = false; }
            if (overlayNumericPanel.Visible) { CommitNumericValue(); overlayNumericPanel!.Visible = false; }
            _overlayEntry = null; _overlayRowIndex = -1;
        }

        private void CommitComboValue()
        {
            if (_overlayEntry == null || !(overlayCombo.SelectedItem is string val)) return;
            _overlayEntry.DisplayValue = val;
            WriteLiveOrDesign(_overlayEntry, val);
        }

        private void CommitNumericValue()
        {
            if (_overlayEntry == null) return;
            long intVal = (long)overlayNumeric.Value;
            // Write always uses plain decimal integer or float string — the device does not accept "0x…"
            string writeVal = overlayNumeric.DecimalPlaces == 0
                ? intVal.ToString()
                : overlayNumeric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _overlayEntry.DisplayValue = _overlayEntry.Representation == HexNumber
                ? $"0x{intVal:X}"
                : writeVal;
            WriteLiveOrDesign(_overlayEntry, writeVal);
        }

        private void WriteLiveOrDesign(FeatureDisplayEntry entry, string value)
        {
            var map = _source.LiveNodeMap ?? _designContext?.Map;
            if (map == null)
            {
                // No camera — just update override list so the value is persisted
                Configuration.SetOverride(entry.Name, value);
                entry.Overridden = true;
                int ri = FindRowIndex(entry);
                if (ri >= 0) RefreshBoundRow(ri);
                return;
            }
            try
            {
                map.Write(entry.Name, value);
                var confirmed = map.Read(entry.Name);
                entry.DisplayValue = FeatureDisplayEntry.ValueToString(confirmed.Value, entry.Representation, entry.DecimalPlaces);
                Configuration.SetOverride(entry.Name, entry.DisplayValue ?? value);
                entry.Overridden = true;
                int ri = FindRowIndex(entry);
                if (ri >= 0) RefreshBoundRow(ri);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to write '{entry.Name}':\n{ex.Message}", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshBoundRow(int rowIndex)
        {
            if (grid.DataSource is BindingList<FeatureDisplayEntry> bl && rowIndex >= 0 && rowIndex < bl.Count)
                bl.ResetItem(rowIndex);
        }

        private int FindRowIndex(FeatureDisplayEntry entry)
        {
            if (grid.DataSource is not BindingList<FeatureDisplayEntry> bl) return -1;
            for (int i = 0; i < bl.Count; i++)
                if (ReferenceEquals(bl[i], entry)) return i;
            return -1;
        }

        private void OverlayCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressCombo) return;
            CommitComboValue();
            _overlayEntry = null; overlayCombo.Visible = false; _overlayRowIndex = -1;
            grid.Focus();
        }

        private void OverlayNumericPanel_Leave(object sender, EventArgs e)
        {
            BeginInvoke(new Action(() =>
            {
                if (overlayNumericPanel.Visible && !overlayNumericPanel.ContainsFocus)
                {
                    CommitNumericValue();
                    overlayNumericPanel!.Visible = false;
                    _overlayEntry = null; _overlayRowIndex = -1;
                }
            }));
        }

        private void OverlayNumeric_ValueChanged(object sender, EventArgs e)
        {
            if (_suppressSync || _overlayEntry == null || !overlayTrackBar.Visible) return;
            if (!_overlayEntry.MinValue.HasValue || !_overlayEntry.MaxValue.HasValue) return;
            if (_overlayEntry.MaxValue.Value <= _overlayEntry.MinValue.Value) return;
            int tick = ComputeTick(_overlayEntry, (double)overlayNumeric.Value);
            _suppressSync = true;
            overlayTrackBar.Value = Math.Max(0, Math.Min(1000, tick));
            _suppressSync = false;
        }

        private void OverlayTrackBar_Scroll(object sender, EventArgs e)
        {
            if (_suppressSync || _overlayEntry == null) return;
            if (!_overlayEntry.MinValue.HasValue || !_overlayEntry.MaxValue.HasValue) return;
            if (_overlayEntry.MaxValue.Value <= _overlayEntry.MinValue.Value) return;
            double val = TickToValue(_overlayEntry, overlayTrackBar.Value);
            if (overlayNumeric.DecimalPlaces == 0) val = Math.Round(val);
            _suppressSync = true;
            overlayNumeric.Value = Math.Max(overlayNumeric.Minimum, Math.Min(overlayNumeric.Maximum, (decimal)val));
            _suppressSync = false;
        }

        private void ExecuteCommandNode(FeatureDisplayEntry entry)
        {
            var map = _source.LiveNodeMap ?? _designContext?.Map;
            if (map == null) { MessageBox.Show(this, "Camera not connected.", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try { map.Write(entry.Name, ""); }
            catch (Exception ex) { MessageBox.Show(this, $"Failed to execute '{entry.Name}':\n{ex.Message}", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private DeviceContext OpenDevice()
        {
            var path = string.IsNullOrWhiteSpace(_source.ProducerPath) ? null : _source.ProducerPath;
            var (api, localIndex) = GenTLLoader.ResolveAndLoad(path, _source.DeviceIndex);
            var system = new GenTLSystem(api);

            (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) t;
            if (!string.IsNullOrEmpty(_source.SerialNumber))
                t = system.FindAndOpenDeviceBySerial(_source.SerialNumber!, DeviceAccessFlags.Control);
            else if (!string.IsNullOrEmpty(_source.CameraModel))
                t = system.FindAndOpenDeviceByModel(_source.CameraModel!, localIndex, DeviceAccessFlags.Control);
            else
                t = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.Control);

            string TryGet(DeviceInfoCmd cmd)
            { try { return t.iface.GetDeviceInfoString(t.devId, cmd); } catch { return string.Empty; } }

            var info = new DeviceInfo
            {
                GlobalIndex   = _source.DeviceIndex,
                ID            = t.devId,
                InterfaceID   = t.ifaceId,
                ProducerPath  = api.ProducerPath,
                Vendor        = TryGet(DeviceInfoCmd.Vendor),
                Model         = TryGet(DeviceInfoCmd.Model),
                SerialNumber  = TryGet(DeviceInfoCmd.SerialNumber),
                TLType        = TryGet(DeviceInfoCmd.TLType),
                DisplayName   = TryGet(DeviceInfoCmd.DisplayName)
            };
            var map = new NodeMap(api, t.device.GetPort());
            return new DeviceContext(api, system, t.iface, t.device, map, info);
        }

        private sealed class DeviceContext : IDisposable
        {
            internal NodeMap Map { get; }
            internal DeviceInfo Info { get; }
            private readonly GenTLApi _api;
            private readonly GenTLSystem _system;
            private readonly GenTLInterface _iface;
            private readonly GenTLDevice _device;

            internal DeviceContext(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device, NodeMap map, DeviceInfo info)
            {
                _api = api; _system = system; _iface = iface; _device = device;
                Map = map; Info = info;
            }

            public void Dispose()
            {
                _device.Dispose();
                _iface.Dispose();
                _system.Dispose();
                _api.Dispose();
            }
        }
    }
}
