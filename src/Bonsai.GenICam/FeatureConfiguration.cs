using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    internal interface IGenICamSource
    {
        string? ProducerPath { get; }
        int DeviceIndex { get; }
    }

    internal enum FeatureKind { Text, Integer, Float, Enumeration, Boolean, Command }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class FeatureConfiguration
    {
        private readonly List<FeatureEntry> entries = new List<FeatureEntry>();
        private readonly Dictionary<string, string> categoryDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        [Browsable(false)]
        public IReadOnlyList<FeatureEntry> Entries => entries;

        [Browsable(false)]
        internal IReadOnlyDictionary<string, string> CategoryDescriptions => categoryDescriptions;

        public override string ToString() => entries.Count == 0 ? "No cached features" : $"{entries.Count} camera features";

        internal void Refresh(NodeMap map)
        {
            var categories = map.GetCategories();
            var featureCategory = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in categories)
                foreach (string feat in kv.Value)
                    if (!featureCategory.ContainsKey(feat))
                        featureCategory[feat] = kv.Key;

            categoryDescriptions.Clear();
            foreach (string catName in categories.Keys)
                categoryDescriptions[catName] = map.GetCategoryDescription(catName);

            var newEntries = new List<FeatureEntry>();
            foreach (var feature in map.TryReadAll())
            {
                var writable = map.CanWrite(feature.Name);
                featureCategory.TryGetValue(feature.Name, out string category);
                string desc = map.GetNodeDescription(feature.Name);
                var kind = map.GetNodeKind(feature.Name);
                var enumEntries = kind == FeatureKind.Enumeration ? map.GetEnumEntries(feature.Name) : null;
                double? minV = null, maxV = null, stepV = null;
                if (kind == FeatureKind.Integer || kind == FeatureKind.Float)
                {
                    var lim = map.GetNodeLimits(feature.Name);
                    minV = lim.min; maxV = lim.max; stepV = lim.step;
                }
                var existing = entries.FirstOrDefault(e => string.Equals(e.Name, feature.Name, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.CurrentValue = feature.Value;
                    existing.Writable = writable;
                    existing.Category = category;
                    existing.Description = desc;
                    existing.Kind = kind;
                    existing.EnumEntries = enumEntries;
                    existing.MinValue = minV; existing.MaxValue = maxV; existing.StepValue = stepV;
                    if (!existing.Modified)
                        existing.EditValue = feature.Value?.ToString();
                    newEntries.Add(existing);
                }
                else
                {
                    newEntries.Add(new FeatureEntry(feature.Name, feature.Value, writable)
                    {
                        Category = category, Description = desc, Kind = kind, EnumEntries = enumEntries,
                        MinValue = minV, MaxValue = maxV, StepValue = stepV
                    });
                }
            }

            foreach (var cmdName in map.GetCommandNodeNames())
            {
                featureCategory.TryGetValue(cmdName, out string cmdCategory);
                string cmdDesc = map.GetNodeDescription(cmdName);
                var existing = entries.FirstOrDefault(e => string.Equals(e.Name, cmdName, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.Kind = FeatureKind.Command;
                    existing.Writable = true;
                    existing.Category = cmdCategory;
                    existing.Description = cmdDesc;
                    newEntries.Add(existing);
                }
                else
                {
                    newEntries.Add(new FeatureEntry(cmdName, null, true)
                    {
                        Kind = FeatureKind.Command, Category = cmdCategory, Description = cmdDesc
                    });
                }
            }

            entries.Clear();
            entries.AddRange(newEntries);
        }

        internal void Apply(NodeMap map)
        {
            foreach (var entry in entries)
            {
                if (!entry.Modified || !entry.Writable) continue;
                map.Write(entry.Name, entry.EditValue ?? string.Empty);
            }
        }
    }

    public class FeatureEntry
    {
        public string Name { get; set; }
        public object? CurrentValue { get; set; }
        public string? EditValue { get; set; }
        public bool Writable { get; set; }
        public bool Modified { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        internal FeatureKind Kind { get; set; }
        internal IReadOnlyList<string>? EnumEntries { get; set; }
        internal double? MinValue { get; set; }
        internal double? MaxValue { get; set; }
        internal double? StepValue { get; set; }

        public FeatureEntry(string name, object? value, bool writable)
        {
            Name = name;
            CurrentValue = value;
            EditValue = value?.ToString();
            Writable = writable;
            Modified = false;
        }

        public override string ToString() => EditValue ?? CurrentValue?.ToString() ?? string.Empty;
    }

    internal class FeatureConfigurationEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
        {
            return UITypeEditorEditStyle.Modal;
        }

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context?.Instance is IGenICamSource node)
            {
                var configuration = value as FeatureConfiguration ?? new FeatureConfiguration();
                using (var form = new FeatureConfigurationForm(configuration, node.ProducerPath, node.DeviceIndex))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        return form.Configuration;
                    }
                }
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
        private readonly Button applyButton;
        private readonly Button closeButton;
        private readonly Label deviceInfoLabel;
        private readonly ComboBox overlayCombo;
        private readonly Panel overlayNumericPanel;
        private readonly NumericUpDown overlayNumeric;
        private readonly TrackBar overlayTrackBar;
        private readonly string? producerPath;
        private bool _suppressSync;
        private readonly int deviceIndex;
        private string? _selectedCategory;
        private FeatureEntry? _overlayEntry;
        private int _overlayRowIndex = -1;
        private int _valueColumnIndex;

        public FeatureConfiguration Configuration { get; }

        public FeatureConfigurationForm(FeatureConfiguration configuration, string? producerPath, int deviceIndex)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.producerPath = producerPath;
            this.deviceIndex = deviceIndex;

            Text = "GenICam Feature Editor";
            Width = 900;
            Height = 600;
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

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Name",
                HeaderText = "Feature Name",
                DataPropertyName = "Name",
                ReadOnly = true,
                FillWeight = 3
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Value",
                HeaderText = "Value",
                DataPropertyName = "EditValue",
                ReadOnly = false,
                FillWeight = 3
            });
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Writable",
                HeaderText = "Writable",
                DataPropertyName = "Writable",
                ReadOnly = true,
                FillWeight = 1
            });
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Modified",
                HeaderText = "Modified",
                DataPropertyName = "Modified",
                ReadOnly = true,
                FillWeight = 1
            });
            _valueColumnIndex = grid.Columns["Value"].Index;

            grid.CellBeginEdit += Grid_CellBeginEdit;
            grid.CellEndEdit += Grid_CellEndEdit;
            grid.CellPainting += Grid_CellPainting;
            grid.CellClick += Grid_CellClick;
            grid.Scroll += (s, e2) => HideOverlays();

            overlayCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
            overlayCombo.SelectedIndexChanged += OverlayCombo_SelectedIndexChanged;
            overlayCombo.KeyDown += (s, e2) => { if (e2.KeyCode == Keys.Escape) HideOverlays(); };

            overlayNumeric = new NumericUpDown { Minimum = -1e15m, Maximum = 1e15m, Increment = 1 };
            overlayNumeric.ValueChanged += OverlayNumeric_ValueChanged;
            overlayNumeric.Leave += OverlayNumericPanel_Leave;
            overlayNumeric.KeyDown += (s, e2) =>
            {
                if (e2.KeyCode == Keys.Enter)
                {
                    CommitNumericValue(); overlayNumericPanel!.Visible = false;
                    _overlayEntry = null; _overlayRowIndex = -1; grid.Focus(); e2.Handled = true;
                }
                else if (e2.KeyCode == Keys.Escape)
                {
                    _overlayEntry = null; overlayNumericPanel!.Visible = false;
                    _overlayRowIndex = -1; grid.Focus(); e2.Handled = true;
                }
            };

            overlayTrackBar = new TrackBar
            {
                Minimum = 0, Maximum = 1000,
                TickStyle = TickStyle.None, Height = 22, Visible = false
            };
            overlayTrackBar.Scroll += OverlayTrackBar_Scroll;
            overlayTrackBar.Leave += OverlayNumericPanel_Leave;
            overlayTrackBar.KeyDown += (s, e2) =>
            {
                if (e2.KeyCode == Keys.Escape)
                {
                    _overlayEntry = null; overlayNumericPanel!.Visible = false;
                    _overlayRowIndex = -1; grid.Focus(); e2.Handled = true;
                }
            };

            overlayNumericPanel = new Panel { Visible = false };
            overlayNumericPanel.Controls.Add(overlayNumeric);
            overlayNumericPanel.Controls.Add(overlayTrackBar);

            deviceInfoLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 6, 0),
                Text = "Connecting to device..."
            };
            var deviceInfoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = SystemColors.Info,
                Padding = new Padding(2)
            };
            deviceInfoPanel.Controls.Add(deviceInfoLabel);

            refreshButton = new Button { Text = "Refresh", Width = 100, Height = 28 };
            applyButton  = new Button { Text = "Apply",   Width = 100, Height = 28 };
            closeButton  = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Height = 28 };

            refreshButton.Click += RefreshButton_Click;
            applyButton.Click += ApplyButton_Click;

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(5, 7, 5, 7),
                WrapContents = false
            };
            buttonPanel.Controls.Add(closeButton);
            buttonPanel.Controls.Add(applyButton);
            buttonPanel.Controls.Add(refreshButton);

            categoryList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false
            };
            categoryList.SelectedIndexChanged += CategoryList_SelectedIndexChanged;

            categoryDescBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control
            };
            categoryDescGroup = new GroupBox
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                Text = "Category"
            };
            categoryDescGroup.Controls.Add(categoryDescBox);

            featureDescBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control
            };
            featureDescGroup = new GroupBox
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                Text = "Feature"
            };
            featureDescGroup.Controls.Add(featureDescBox);

            grid.SelectionChanged += Grid_SelectionChanged;

            splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Panel1MinSize = 120,
                Panel2MinSize = 100
            };
            splitContainer.Panel1.Controls.Add(categoryList);
            splitContainer.Panel1.Controls.Add(categoryDescGroup);
            splitContainer.Panel2.Controls.Add(grid);
            splitContainer.Panel2.Controls.Add(featureDescGroup);
            splitContainer.Panel2.Controls.Add(overlayCombo);
            splitContainer.Panel2.Controls.Add(overlayNumericPanel);

            Controls.Add(splitContainer);
            Controls.Add(buttonPanel);
            Controls.Add(deviceInfoPanel);

            Load += FeatureConfigurationForm_Load;
        }

        private void FeatureConfigurationForm_Load(object sender, EventArgs e)
        {
            splitContainer.SplitterDistance = 200;
            RefreshFromDevice();
        }

        private void Grid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "Value") { e.Cancel = true; return; }
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureEntry;
            if (entry == null || !entry.Writable) { e.Cancel = true; return; }
            if (entry.Kind != FeatureKind.Text) e.Cancel = true;
        }

        private void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Value") return;
            if (grid.Rows[e.RowIndex].DataBoundItem is FeatureEntry entry)
            {
                entry.Modified = !string.Equals(entry.EditValue, entry.CurrentValue?.ToString(), StringComparison.Ordinal);
                RefreshBoundRow(e.RowIndex);
            }
        }

        private void CategoryList_SelectedIndexChanged(object sender, EventArgs e)
        {
            _selectedCategory = categoryList.SelectedIndex > 0 ? categoryList.SelectedItem?.ToString() : null;
            if (_selectedCategory != null)
            {
                categoryDescGroup.Text = $"Category: {_selectedCategory}";
                Configuration.CategoryDescriptions.TryGetValue(_selectedCategory, out string desc);
                categoryDescBox.Text = desc ?? "";
            }
            else
            {
                categoryDescGroup.Text = "Category";
                categoryDescBox.Text = "";
            }
            UpdateGrid();
        }

        private void Grid_SelectionChanged(object sender, EventArgs e)
        {
            HideOverlays();
            if (grid.SelectedRows.Count == 0 || !(grid.SelectedRows[0].DataBoundItem is FeatureEntry entry))
            {
                featureDescGroup.Text = "Feature";
                featureDescBox.Text = "";
                return;
            }
            featureDescGroup.Text = $"Feature: {entry.Name}";
            featureDescBox.Text = entry.Description ?? "";
        }

        private void RefreshButton_Click(object sender, EventArgs e)
        {
            RefreshFromDevice();
        }

        private void ApplyButton_Click(object sender, EventArgs e)
        {
            try
            {
                using (var context = OpenDevice())
                {
                    var map = new NodeMap(context.Api, context.Port);
                    Configuration.Apply(map);
                    Configuration.Refresh(map);
                }
                UpdateCategoryList();
                UpdateGrid();
                MessageBox.Show(this, "Feature edits applied successfully.", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to apply feature changes:\n{ex.Message}", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshFromDevice()
        {
            try
            {
                using (var context = OpenDevice())
                {
                    var map = new NodeMap(context.Api, context.Port);
                    Configuration.Refresh(map);
                    UpdateDeviceInfoLabel(context.Info);
                }
                UpdateCategoryList();
                UpdateGrid();
            }
            catch (Exception ex)
            {
                deviceInfoLabel.Text = $"Device unavailable — showing saved configuration ({ex.Message})";
                // If we have cached entries show them without a blocking dialog.
                // Only pop a dialog when the editor opens with nothing cached at all.
                if (Configuration.Entries.Count == 0)
                    MessageBox.Show(this, $"Cannot connect to camera:\n{ex.Message}\n\nStop the running workflow before opening the feature editor.", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                {
                    UpdateCategoryList();
                    UpdateGrid();
                }
            }
        }

        private void UpdateDeviceInfoLabel(DeviceInfo info)
        {
            if (info == null) { deviceInfoLabel.Text = "No device connected"; return; }
            var name = !string.IsNullOrEmpty(info.DisplayName) ? info.DisplayName
                     : string.Join(" ", new[] { info.Vendor, info.Model }.Where(s => !string.IsNullOrEmpty(s)));
            var details = new List<string>();
            if (!string.IsNullOrEmpty(info.SerialNumber)) details.Add($"S/N: {info.SerialNumber}");
            if (!string.IsNullOrEmpty(info.TLType)) details.Add(info.TLType);
            deviceInfoLabel.Text = details.Count > 0 ? $"{name}    {string.Join("    ", details)}" : name;
        }

        private void UpdateCategoryList()
        {
            categoryList.SelectedIndexChanged -= CategoryList_SelectedIndexChanged;
            try
            {
                string? savedCategory = _selectedCategory;
                categoryList.Items.Clear();
                categoryList.Items.Add("All");
                var cats = Configuration.Entries
                    .Where(e => e.Category != null)
                    .Select(e => e.Category!)
                    .Distinct()
                    .OrderBy(c => c);
                foreach (string cat in cats)
                    categoryList.Items.Add(cat);

                int idx = savedCategory != null ? categoryList.Items.IndexOf(savedCategory) : 0;
                categoryList.SelectedIndex = idx >= 0 ? idx : 0;
                _selectedCategory = idx > 0 ? savedCategory : null;
            }
            finally
            {
                categoryList.SelectedIndexChanged += CategoryList_SelectedIndexChanged;
            }
        }

        private void UpdateGrid()
        {
            var entries = _selectedCategory == null
                ? Configuration.Entries.ToList()
                : Configuration.Entries.Where(e => string.Equals(e.Category, _selectedCategory, StringComparison.Ordinal)).ToList();
            grid.DataSource = null;
            grid.DataSource = new BindingList<FeatureEntry>(entries);
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _valueColumnIndex) return;
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureEntry;
            if (entry == null) return;

            if (entry.Kind == FeatureKind.Boolean)
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
                bool isChecked = string.Equals(entry.EditValue, "True", StringComparison.OrdinalIgnoreCase);
                var state = entry.Writable
                    ? (isChecked ? CheckBoxState.CheckedNormal  : CheckBoxState.UncheckedNormal)
                    : (isChecked ? CheckBoxState.CheckedDisabled : CheckBoxState.UncheckedDisabled);
                var sz = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                var pt = new Point(e.CellBounds.X + (e.CellBounds.Width - sz.Width) / 2,
                                   e.CellBounds.Y + (e.CellBounds.Height - sz.Height) / 2);
                CheckBoxRenderer.DrawCheckBox(e.Graphics, pt, state);
                e.Handled = true;
            }
            else if (entry.Kind == FeatureKind.Command)
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
                if (entry.Writable)
                {
                    var btnRect = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y + 2,
                        Math.Min(90, e.CellBounds.Width - 8), e.CellBounds.Height - 4);
                    ButtonRenderer.DrawButton(e.Graphics, btnRect, "Execute", grid.Font, false, PushButtonState.Normal);
                }
                e.Handled = true;
            }
        }

        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _valueColumnIndex) return;
            var entry = grid.Rows[e.RowIndex].DataBoundItem as FeatureEntry;
            if (entry == null || !entry.Writable) return;

            switch (entry.Kind)
            {
                case FeatureKind.Boolean:
                    entry.EditValue = string.Equals(entry.EditValue, "True", StringComparison.OrdinalIgnoreCase) ? "False" : "True";
                    entry.Modified = !string.Equals(entry.EditValue, entry.CurrentValue?.ToString(), StringComparison.Ordinal);
                    RefreshBoundRow(e.RowIndex);
                    break;
                case FeatureKind.Command:
                    ExecuteCommandNode(entry);
                    break;
                case FeatureKind.Enumeration:
                    ShowEnumOverlay(e.RowIndex, entry);
                    break;
                case FeatureKind.Integer:
                    ShowNumericOverlay(e.RowIndex, entry, 0);
                    break;
                case FeatureKind.Float:
                    ShowNumericOverlay(e.RowIndex, entry, 6);
                    break;
            }
        }

        private void ShowEnumOverlay(int rowIndex, FeatureEntry entry)
        {
            HideOverlays();
            var bounds = grid.GetCellDisplayRectangle(_valueColumnIndex, rowIndex, true);
            if (bounds.IsEmpty) return;
            _overlayEntry = entry;
            _overlayRowIndex = rowIndex;
            overlayCombo.Items.Clear();
            if (entry.EnumEntries != null)
                foreach (string item in entry.EnumEntries)
                    overlayCombo.Items.Add(item);
            overlayCombo.SelectedItem = entry.EditValue;
            overlayCombo.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            overlayCombo.Visible = true;
            overlayCombo.BringToFront();
            overlayCombo.Focus();
            BeginInvoke(new Action(() => { if (overlayCombo.Visible) overlayCombo.DroppedDown = true; }));
        }

        private void ShowNumericOverlay(int rowIndex, FeatureEntry entry, int decimalPlaces)
        {
            HideOverlays();
            var bounds = grid.GetCellDisplayRectangle(_valueColumnIndex, rowIndex, true);
            if (bounds.IsEmpty) return;
            _overlayEntry = entry;
            _overlayRowIndex = rowIndex;

            overlayNumeric.DecimalPlaces = decimalPlaces;
            overlayNumeric.Minimum = entry.MinValue.HasValue ? (decimal)entry.MinValue.Value
                                   : (decimalPlaces == 0 ? (decimal)long.MinValue : -1e15m);
            overlayNumeric.Maximum = entry.MaxValue.HasValue ? (decimal)entry.MaxValue.Value
                                   : (decimalPlaces == 0 ? (decimal)long.MaxValue :  1e15m);
            overlayNumeric.Increment = entry.StepValue.HasValue ? (decimal)entry.StepValue.Value : 1;

            _suppressSync = true;
            if (decimal.TryParse(entry.EditValue, System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture, out decimal v))
                overlayNumeric.Value = Math.Max(overlayNumeric.Minimum, Math.Min(overlayNumeric.Maximum, v));
            _suppressSync = false;

            bool hasLimits = entry.MinValue.HasValue && entry.MaxValue.HasValue;
            int panelH = bounds.Height;
            if (hasLimits)
            {
                double range = entry.MaxValue.GetValueOrDefault() - entry.MinValue.GetValueOrDefault();
                int tick = range > 0 ? (int)(((double)overlayNumeric.Value - entry.MinValue.GetValueOrDefault()) / range * 1000) : 0;
                _suppressSync = true;
                overlayTrackBar.Value = Math.Max(0, Math.Min(1000, tick));
                _suppressSync = false;
                overlayTrackBar.SetBounds(0, bounds.Height, bounds.Width, overlayTrackBar.Height);
                overlayTrackBar.Visible = true;
                panelH = bounds.Height + overlayTrackBar.Height;
            }
            else
            {
                overlayTrackBar.Visible = false;
            }

            overlayNumeric.SetBounds(0, 0, bounds.Width, bounds.Height);
            overlayNumericPanel.SetBounds(bounds.X, bounds.Y, bounds.Width, panelH);
            overlayNumericPanel.Visible = true;
            overlayNumericPanel.BringToFront();
            overlayNumeric.Focus();
        }

        private void HideOverlays()
        {
            if (overlayCombo.Visible)        { CommitComboValue();   overlayCombo.Visible        = false; }
            if (overlayNumericPanel.Visible) { CommitNumericValue(); overlayNumericPanel!.Visible = false; }
            _overlayEntry = null;
            _overlayRowIndex = -1;
        }

        private void CommitComboValue()
        {
            if (_overlayEntry == null || !(overlayCombo.SelectedItem is string val)) return;
            _overlayEntry.EditValue = val;
            _overlayEntry.Modified = !string.Equals(val, _overlayEntry.CurrentValue?.ToString(), StringComparison.Ordinal);
            RefreshBoundRow(_overlayRowIndex);
        }

        private void CommitNumericValue()
        {
            if (_overlayEntry == null) return;
            string val = overlayNumeric.DecimalPlaces == 0
                ? ((long)overlayNumeric.Value).ToString()
                : overlayNumeric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _overlayEntry.EditValue = val;
            _overlayEntry.Modified = !string.Equals(val, _overlayEntry.CurrentValue?.ToString(), StringComparison.Ordinal);
            RefreshBoundRow(_overlayRowIndex);
        }

        private void RefreshBoundRow(int rowIndex)
        {
            if (grid.DataSource is BindingList<FeatureEntry> bl && rowIndex >= 0 && rowIndex < bl.Count)
                bl.ResetItem(rowIndex);
        }

        private void OverlayCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            CommitComboValue();
            _overlayEntry = null;
            overlayCombo.Visible = false;
            _overlayRowIndex = -1;
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
                    _overlayEntry = null;
                    _overlayRowIndex = -1;
                }
            }));
        }

        private void OverlayNumeric_ValueChanged(object sender, EventArgs e)
        {
            if (_suppressSync || _overlayEntry == null || !overlayTrackBar.Visible) return;
            if (!_overlayEntry.MinValue.HasValue || !_overlayEntry.MaxValue.HasValue) return;
            double range = _overlayEntry.MaxValue.Value - _overlayEntry.MinValue.Value;
            if (range <= 0) return;
            int tick = (int)(((double)overlayNumeric.Value - _overlayEntry.MinValue.Value) / range * 1000);
            _suppressSync = true;
            overlayTrackBar.Value = Math.Max(0, Math.Min(1000, tick));
            _suppressSync = false;
        }

        private void OverlayTrackBar_Scroll(object sender, EventArgs e)
        {
            if (_suppressSync || _overlayEntry == null) return;
            if (!_overlayEntry.MinValue.HasValue || !_overlayEntry.MaxValue.HasValue) return;
            double range = _overlayEntry.MaxValue.Value - _overlayEntry.MinValue.Value;
            if (range <= 0) return;
            double val = _overlayEntry.MinValue.Value + (overlayTrackBar.Value / 1000.0) * range;
            if (overlayNumeric.DecimalPlaces == 0) val = Math.Round(val);
            _suppressSync = true;
            overlayNumeric.Value = Math.Max(overlayNumeric.Minimum, Math.Min(overlayNumeric.Maximum, (decimal)val));
            _suppressSync = false;
        }

        private void ExecuteCommandNode(FeatureEntry entry)
        {
            try
            {
                using (var context = OpenDevice())
                {
                    var map = new NodeMap(context.Api, context.Port);
                    map.Write(entry.Name, "");
                }
                MessageBox.Show(this, $"Command '{entry.Name}' executed.", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to execute '{entry.Name}':\n{ex.Message}", "Feature Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private DeviceContext OpenDevice()
        {
            var (api, localIndex) = GenTLLoader.ResolveAndLoad(
                string.IsNullOrWhiteSpace(producerPath) ? null : producerPath, deviceIndex);
            var system = new GenTLSystem(api);
            var (ifaceId, devId, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.ReadOnly);

            string TryGet(DeviceInfoCmd cmd)
            {
                try { return iface.GetDeviceInfoString(devId, cmd); }
                catch { return string.Empty; }
            }
            var info = new DeviceInfo
            {
                GlobalIndex = deviceIndex,
                ID = devId,
                InterfaceID = ifaceId,
                ProducerPath = api.ProducerPath,
                Vendor = TryGet(DeviceInfoCmd.Vendor),
                Model = TryGet(DeviceInfoCmd.Model),
                SerialNumber = TryGet(DeviceInfoCmd.SerialNumber),
                TLType = TryGet(DeviceInfoCmd.TLType),
                DisplayName = TryGet(DeviceInfoCmd.DisplayName)
            };

            return new DeviceContext(api, system, iface, device, info);
        }

        private sealed class DeviceContext : IDisposable
        {
            internal readonly GenTLApi Api;
            internal readonly IntPtr Port;
            internal readonly DeviceInfo Info;
            private readonly GenTLSystem _system;
            private readonly GenTLInterface _iface;
            private readonly GenTLDevice _device;

            internal DeviceContext(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device, DeviceInfo info)
            {
                Api = api;
                _system = system;
                _iface = iface;
                _device = device;
                Port = device.GetPort();
                Info = info;
            }

            public void Dispose()
            {
                _device.Dispose();
                _iface.Dispose();
                _system.Dispose();
                Api.Dispose();
            }
        }
    }
}
