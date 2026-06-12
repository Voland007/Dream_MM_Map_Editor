// Copyright (c) Voland007 2025. All rights reserved.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MMMapEditor
{
    public partial class ObjectManagementForm : Form
    {
        private const string FallbackObjectName = "Не исследовано";

        private readonly MainForm mainForm;
        private readonly ImageList iconList = new ImageList();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly List<PendingReferenceReplacement> pendingReferenceReplacements =
            new List<PendingReferenceReplacement>();

        private List<CentralObject> objects = new List<CentralObject>();

        private ListView listViewObjects;
        private TextBox txtName;
        private NumericUpDown nudLeftMargin;
        private NumericUpDown nudUpMargin;
        private NumericUpDown nudFilterLevel;
        private PictureBox picIcon;
        private Button btnNew;
        private Button btnDelete;
        private Button btnChooseIcon;
        private Button btnClearIcon;
        private Button btnLoadProfile;
        private Button btnSaveProfileAs;
        private Button btnShowPath;
        private Button btnClose;
        private CheckBox chkUseAsDefault;
        private Label profileLabel;
        private Label statusLabel;
        private Label validationLabel;

        private CentralObject selectedObject;
        private bool isUpdatingControls;
        private bool isUpdatingDefaultCheckbox;
        private bool hasUnsavedChanges;
        private bool showWarningOnImport;

        public string ActiveProfileFileName { get; private set; }

        public ObjectManagementForm(MainForm mainForm, string activeConfigFile)
        {
            InitializeComponent();

            this.mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            ActiveProfileFileName = ResolveInitialProfilePath(activeConfigFile);

            BuildInterface();
            LoadObjectsFromProfile(ActiveProfileFileName);
            ApplyLoadedProfileToMainIfAvailable();
            CheckAndUpdateDefaultFlag();
        }

        private void BuildInterface()
        {
            SuspendLayout();

            Font = new Font("Segoe UI", 9f);
            Text = "Управление игровыми объектами";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(242, 245, 249);
            ClientSize = new Size(820, 479);
            MinimumSize = new Size(820, 479);

            iconList.ColorDepth = ColorDepth.Depth32Bit;
            iconList.ImageSize = new Size(32, 32);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(14),
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 365));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            root.Controls.Add(CreateContentPanel(), 0, 0);
            root.Controls.Add(CreateProfilePathPanel(), 0, 1);
            root.Controls.Add(CreateFooterPanel(), 0, 2);
            Controls.Add(root);

            ResumeLayout(false);
        }

        private Control CreateProfilePathPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(242, 245, 249),
                Padding = new Padding(0, 6, 0, 0)
            };

            profileLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(91, 103, 117),
                AutoEllipsis = true
            };

            panel.Controls.Add(profileLabel);
            return panel;
        }

        private Control CreateContentPanel()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 365,
                BackColor = BackColor,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 455));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var objectListPanel = CreateObjectListPanel();
            objectListPanel.Margin = new Padding(0, 0, 10, 0);

            var editorPanel = CreateEditorPanel();
            editorPanel.Margin = new Padding(0);

            var rightColumn = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = BackColor,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            rightColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 225));
            rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var profileActionsPanel = CreateProfileActionsPanel();
            profileActionsPanel.Margin = new Padding(0, 10, 0, 0);

            rightColumn.Controls.Add(editorPanel, 0, 0);
            rightColumn.Controls.Add(profileActionsPanel, 0, 1);

            layout.Controls.Add(objectListPanel, 0, 0);
            layout.Controls.Add(rightColumn, 1, 0);
            return layout;
        }

        private Control CreateObjectListPanel()
        {
            var panel = CreateSurfacePanel();

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Объекты",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 12, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            btnNew = CreatePrimaryButton("Новый", 96);
            btnNew.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            btnNew.Margin = new Padding(0);
            btnNew.Click += BtnNew_Click;

            btnDelete = CreateSecondaryButton("Удалить", 96);
            btnDelete.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnDelete.Margin = new Padding(0);
            btnDelete.Enabled = false;
            btnDelete.Click += BtnDelete_Click;

            buttons.Controls.Add(btnNew, 0, 0);
            buttons.Controls.Add(btnDelete, 1, 0);

            listViewObjects = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(31, 41, 55),
                SmallImageList = iconList,
                AllowDrop = true
            };
            listViewObjects.Columns.Add("Объект", 260);
            listViewObjects.Columns.Add("Отступы", 82);
            listViewObjects.Columns.Add("Фильтр", 72);
            listViewObjects.SelectedIndexChanged += ListViewObjects_SelectedIndexChanged;
            listViewObjects.ItemDrag += ListViewObjects_ItemDrag;
            listViewObjects.DragEnter += ListViewObjects_DragEnter;
            listViewObjects.DragOver += ListViewObjects_DragOver;
            listViewObjects.DragDrop += ListViewObjects_DragDrop;

            panel.Controls.Add(listViewObjects);
            panel.Controls.Add(buttons);
            panel.Controls.Add(header);
            return panel;
        }

        private Control CreateEditorPanel()
        {
            var panel = CreateSurfacePanel();

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Параметры объекта",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            var editorLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 174,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(0, 6, 0, 0)
            };
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));

            var nameLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtName = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 0)
            };
            txtName.TextChanged += TxtName_TextChanged;

            nudLeftMargin = CreateNumberBox();
            nudLeftMargin.ValueChanged += NumericField_ValueChanged;

            nudUpMargin = CreateNumberBox();
            nudUpMargin.ValueChanged += NumericField_ValueChanged;

            nudFilterLevel = CreateNumberBox();
            nudFilterLevel.Maximum = 1000;
            nudFilterLevel.ValueChanged += NumericField_ValueChanged;

            validationLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(185, 28, 28),
                TextAlign = ContentAlignment.MiddleLeft
            };

            nameLayout.Controls.Add(CreateFieldLabel("Название"), 0, 0);
            nameLayout.Controls.Add(txtName, 1, 0);

            picIcon = new PictureBox
            {
                Location = new Point(0, 0),
                Size = new Size(82, 82),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(248, 250, 252)
            };

            var detailsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var iconPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 14, 0)
            };
            iconPanel.Controls.Add(picIcon);

            var numericLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0, 0, 0, 0)
            };
            numericLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            numericLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            numericLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            numericLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            numericLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            numericLayout.Controls.Add(CreateFieldLabel("Отступ слева"), 0, 0);
            numericLayout.Controls.Add(nudLeftMargin, 1, 0);
            numericLayout.Controls.Add(CreateFieldLabel("Отступ сверху"), 0, 1);
            numericLayout.Controls.Add(nudUpMargin, 1, 1);
            numericLayout.Controls.Add(CreateFieldLabel("Порог фильтра"), 0, 2);
            numericLayout.Controls.Add(nudFilterLevel, 1, 2);

            detailsLayout.Controls.Add(iconPanel, 0, 0);
            detailsLayout.Controls.Add(numericLayout, 1, 0);

            var iconButtons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0, 8, 0, 0)
            };
            iconButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            iconButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            iconButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            btnChooseIcon = CreatePrimaryButton("Выбрать иконку", 140);
            btnChooseIcon.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            btnChooseIcon.Margin = new Padding(0);
            btnChooseIcon.Click += BtnChooseIcon_Click;

            btnClearIcon = CreateSecondaryButton("Очистить", 100);
            btnClearIcon.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnClearIcon.Margin = new Padding(0);
            btnClearIcon.Click += BtnClearIcon_Click;

            iconButtons.Controls.Add(btnChooseIcon, 0, 0);
            iconButtons.Controls.Add(btnClearIcon, 1, 0);

            validationLabel.Margin = new Padding(96, 0, 0, 0);

            editorLayout.Controls.Add(nameLayout, 0, 0);
            editorLayout.Controls.Add(detailsLayout, 0, 1);
            editorLayout.Controls.Add(iconButtons, 0, 2);
            editorLayout.Controls.Add(validationLabel, 0, 3);

            panel.Controls.Add(editorLayout);
            panel.Controls.Add(header);

            toolTip.SetToolTip(nudLeftMargin, "Смещение пикселей слева при рисовании объекта.");
            toolTip.SetToolTip(nudUpMargin, "Смещение пикселей сверху при рисовании объекта.");
            toolTip.SetToolTip(nudFilterLevel, "Порог яркости для пиксельного фильтра объекта.");

            return panel;
        }

        private Control CreateProfileActionsPanel()
        {
            var panel = CreateSurfacePanel();

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Набор объектов",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 8, 0, 0)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            btnLoadProfile = CreateSecondaryButton("Загрузить", 104);
            btnLoadProfile.Dock = DockStyle.Fill;
            btnLoadProfile.Margin = new Padding(0, 0, 8, 0);
            btnLoadProfile.Click += BtnLoadProfile_Click;

            btnSaveProfileAs = CreateSecondaryButton("Сохранить как", 132);
            btnSaveProfileAs.Dock = DockStyle.Fill;
            btnSaveProfileAs.Margin = new Padding(0, 0, 8, 0);
            btnSaveProfileAs.Click += BtnSaveProfileAs_Click;

            btnShowPath = CreateSecondaryButton("Путь", 76);
            btnShowPath.Dock = DockStyle.Fill;
            btnShowPath.Margin = new Padding(0);
            btnShowPath.Click += BtnShowPath_Click;

            chkUseAsDefault = new CheckBox
            {
                Text = "Использовать этот набор по умолчанию",
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0),
                ForeColor = Color.FromArgb(55, 65, 81)
            };
            chkUseAsDefault.CheckedChanged += ChkUseAsDefault_CheckedChanged;

            var defaultCheckPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(0, 14, 0, 0)
            };
            defaultCheckPanel.Controls.Add(chkUseAsDefault);

            actions.Controls.Add(btnLoadProfile, 0, 0);
            actions.Controls.Add(btnSaveProfileAs, 1, 0);
            actions.Controls.Add(btnShowPath, 2, 0);

            panel.Controls.Add(defaultCheckPanel);
            panel.Controls.Add(actions);
            panel.Controls.Add(header);

            return panel;
        }

        private Control CreateFooterPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(242, 245, 249),
                Padding = new Padding(0, 10, 0, 0)
            };

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(75, 85, 99),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            btnClose = CreatePrimaryButton("Применить и закрыть", 164);
            btnClose.Dock = DockStyle.Right;
            btnClose.Click += BtnClose_Click;

            panel.Controls.Add(statusLabel);
            panel.Controls.Add(btnClose);
            return panel;
        }

        private Panel CreateSurfacePanel()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                BackColor = Color.White
            };
        }

        private Button CreatePrimaryButton(string text, int width)
        {
            var button = CreateBaseButton(text, width);
            button.BackColor = Color.FromArgb(37, 99, 235);
            button.ForeColor = Color.White;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 78, 216);
            return button;
        }

        private Button CreateSecondaryButton(string text, int width)
        {
            var button = CreateBaseButton(text, width);
            button.BackColor = Color.FromArgb(241, 245, 249);
            button.ForeColor = Color.FromArgb(31, 41, 55);
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 232, 240);
            return button;
        }

        private Button CreateBaseButton(string text, int width)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
        }

        private Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(75, 85, 99),
                Padding = new Padding(0, 6, 0, 0),
                TextAlign = ContentAlignment.TopLeft
            };
        }

        private NumericUpDown CreateNumberBox()
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 255,
                Margin = new Padding(0, 4, 0, 0)
            };
        }

        private string ResolveInitialProfilePath(string activeConfigFile)
        {
            string activeProfilePath = ObjectProfileSettings.ResolveProfilePath(activeConfigFile);
            if (!string.IsNullOrWhiteSpace(activeProfilePath) && File.Exists(activeProfilePath))
                return activeProfilePath;

            string defaultProfilePath = ObjectProfileSettings.ResolveDefaultProfilePath(
                initializeFromLocalObjects: true);
            if (!string.IsNullOrWhiteSpace(defaultProfilePath))
                return defaultProfilePath;

            return ObjectProfileSettings.LocalDefaultProfilePath;
        }

        private void LoadObjectsFromProfile(string profilePath)
        {
            try
            {
                ActiveProfileFileName = Path.GetFullPath(profilePath);
                pendingReferenceReplacements.Clear();
                objects = File.Exists(ActiveProfileFileName)
                    ? DataManager.LoadObjects(ActiveProfileFileName)
                    : new List<CentralObject>();

                RefreshListView(objects.FirstOrDefault());
                UpdateProfileLabel();
                SetStatus(File.Exists(ActiveProfileFileName)
                    ? "Набор объектов загружен."
                    : "Будет создан новый набор объектов.");
            }
            catch (Exception ex)
            {
                objects = new List<CentralObject>();
                RefreshListView(null);
                SetStatus($"Ошибка загрузки набора: {ex.Message}", true);
            }
        }

        private void RefreshListView(CentralObject objectToSelect)
        {
            isUpdatingControls = true;
            listViewObjects.BeginUpdate();
            listViewObjects.Items.Clear();
            iconList.Images.Clear();

            foreach (var obj in objects)
            {
                iconList.Images.Add(obj.Icon ?? CreatePlaceholderIcon());

                var item = new ListViewItem(obj.Name ?? "", iconList.Images.Count - 1)
                {
                    Tag = obj
                };
                item.SubItems.Add($"{obj.LeftMargin}/{obj.UpMargin}");
                item.SubItems.Add(obj.FilterLevel.ToString());
                listViewObjects.Items.Add(item);
            }

            listViewObjects.EndUpdate();
            isUpdatingControls = false;

            if (objectToSelect != null)
                SelectObjectInList(objectToSelect);
            else
                SelectObject(null);
        }

        private Image CreatePlaceholderIcon()
        {
            var bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var borderPen = new Pen(Color.FromArgb(203, 213, 225)))
            using (var textBrush = new SolidBrush(Color.FromArgb(100, 116, 139)))
            using (var font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold))
            {
                g.Clear(Color.FromArgb(248, 250, 252));
                g.DrawRectangle(borderPen, 0, 0, 31, 31);
                g.DrawString("?", font, textBrush, new RectangleF(0, 2, 32, 28), new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                });
            }

            return bitmap;
        }

        private void SelectObjectInList(CentralObject obj)
        {
            foreach (ListViewItem item in listViewObjects.Items)
            {
                if (ReferenceEquals(item.Tag, obj))
                {
                    item.Selected = true;
                    item.Focused = true;
                    item.EnsureVisible();
                    SelectObject(obj);
                    return;
                }
            }

            SelectObject(null);
        }

        private void SelectObject(CentralObject obj)
        {
            selectedObject = obj;
            bool enabled = obj != null;

            isUpdatingControls = true;
            txtName.Enabled = enabled;
            nudLeftMargin.Enabled = enabled;
            nudUpMargin.Enabled = enabled;
            nudFilterLevel.Enabled = enabled;
            btnChooseIcon.Enabled = enabled;
            btnClearIcon.Enabled = enabled;
            btnDelete.Enabled = enabled;

            if (obj == null)
            {
                txtName.Text = "";
                nudLeftMargin.Value = 0;
                nudUpMargin.Value = 0;
                nudFilterLevel.Value = 0;
                picIcon.Image = null;
                validationLabel.Text = "";
            }
            else
            {
                txtName.Text = obj.Name ?? "";
                nudLeftMargin.Value = ClampToNumericRange(nudLeftMargin, obj.LeftMargin);
                nudUpMargin.Value = ClampToNumericRange(nudUpMargin, obj.UpMargin);
                nudFilterLevel.Value = ClampToNumericRange(nudFilterLevel, obj.FilterLevel);
                picIcon.Image = obj.Icon;
                validationLabel.Text = "";
            }

            isUpdatingControls = false;
        }

        private decimal ClampToNumericRange(NumericUpDown numericUpDown, int value)
        {
            if (value < numericUpDown.Minimum)
                return numericUpDown.Minimum;

            if (value > numericUpDown.Maximum)
                return numericUpDown.Maximum;

            return value;
        }

        private void UpdateSelectedListItem()
        {
            if (selectedObject == null || listViewObjects.SelectedItems.Count == 0)
                return;

            var item = listViewObjects.SelectedItems[0];
            item.Text = selectedObject.Name ?? "";
            item.SubItems[1].Text = $"{selectedObject.LeftMargin}/{selectedObject.UpMargin}";
            item.SubItems[2].Text = selectedObject.FilterLevel.ToString();
        }

        private bool TryValidateObjectName(string newName, out string message)
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                message = "Название не может быть пустым.";
                return false;
            }

            bool duplicateExists = objects.Any(obj =>
                !ReferenceEquals(obj, selectedObject) &&
                string.Equals(obj.Name, newName, StringComparison.OrdinalIgnoreCase));

            if (duplicateExists)
            {
                message = "Такое название уже есть в наборе.";
                return false;
            }

            message = "";
            return true;
        }

        private void MarkUnsaved(string statusText)
        {
            hasUnsavedChanges = true;
            SetStatus(statusText);
        }

        private bool TryApplyAndClose()
        {
            if (!ValidateCurrentEditor())
                return false;

            try
            {
                if (string.IsNullOrWhiteSpace(ActiveProfileFileName))
                    ActiveProfileFileName = ResolveInitialProfilePath(null);

                DataManager.SaveObjects(objects, ActiveProfileFileName);
                ApplyCurrentProfileToMain();

                if (chkUseAsDefault.Checked)
                    UpdateDefaultConfigFile(ActiveProfileFileName);

                hasUnsavedChanges = false;
                UpdateProfileLabel();
                CheckAndUpdateDefaultFlag();
                SetStatus($"Сохранено и применено. {DateTime.Now:HH:mm:ss}");
                Close();
                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка сохранения: {ex.Message}", true);
                return false;
            }
        }

        private bool ValidateCurrentEditor()
        {
            if (selectedObject == null)
                return true;

            string newName = txtName.Text.Trim();
            if (TryValidateObjectName(newName, out string message))
                return true;

            validationLabel.Text = message;
            txtName.BackColor = Color.FromArgb(254, 242, 242);
            txtName.Focus();
            txtName.SelectAll();
            SetStatus("Исправьте название объекта перед применением.", true);
            return false;
        }

        private void ApplyCurrentProfileToMain()
        {
            if (string.IsNullOrWhiteSpace(ActiveProfileFileName))
                return;

            mainForm.ActiveConfigObjectFile = ActiveProfileFileName;
            mainForm.ReloadData(ActiveProfileFileName);
            ApplyPendingReferenceReplacements();
        }

        private void ApplyLoadedProfileToMainIfAvailable()
        {
            if (!string.IsNullOrWhiteSpace(ActiveProfileFileName) &&
                File.Exists(ActiveProfileFileName))
            {
                ApplyCurrentProfileToMain();
            }
        }

        private void QueueReferenceReplacement(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) ||
                string.IsNullOrWhiteSpace(newName) ||
                string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return;
            }

            var existing = pendingReferenceReplacements
                .FirstOrDefault(replacement =>
                    string.Equals(replacement.NewName, oldName, StringComparison.Ordinal));

            if (existing != null)
            {
                existing.NewName = newName;
                return;
            }

            pendingReferenceReplacements.Add(new PendingReferenceReplacement(oldName, newName));
        }

        private void ApplyPendingReferenceReplacements()
        {
            foreach (var replacement in pendingReferenceReplacements)
            {
                mainForm.ReplaceCentralObjectReferences(replacement.OldName, replacement.NewName);
            }

            pendingReferenceReplacements.Clear();
        }

        private string CreateUniqueObjectName()
        {
            int index = objects.Count + 1;
            string name;

            do
            {
                name = $"Новый объект {index}";
                index++;
            }
            while (objects.Any(obj => string.Equals(obj.Name, name, StringComparison.OrdinalIgnoreCase)));

            return name;
        }

        private void UpdateProfileLabel()
        {
            if (string.IsNullOrWhiteSpace(ActiveProfileFileName))
            {
                profileLabel.Text = "Файл набора: не выбран";
                Text = "Управление игровыми объектами";
                return;
            }

            profileLabel.Text = $"Файл набора: {ActiveProfileFileName}";
            Text = $"Управление игровыми объектами: {Path.GetFileName(ActiveProfileFileName)}";
        }

        private void SetStatus(string text, bool isError = false)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = isError
                ? Color.FromArgb(185, 28, 28)
                : Color.FromArgb(75, 85, 99);
        }

        private void CheckAndUpdateDefaultFlag()
        {
            isUpdatingDefaultCheckbox = true;

            string defaultConfigFilePath = GetDefaultConfigFromINI();
            bool isDefault =
                !string.IsNullOrWhiteSpace(defaultConfigFilePath) &&
                !string.IsNullOrWhiteSpace(ActiveProfileFileName) &&
                string.Equals(
                    Path.GetFullPath(ActiveProfileFileName),
                    Path.GetFullPath(defaultConfigFilePath),
                    StringComparison.OrdinalIgnoreCase);

            chkUseAsDefault.Checked = isDefault;
            chkUseAsDefault.Enabled = !isDefault && !string.IsNullOrWhiteSpace(ActiveProfileFileName);

            isUpdatingDefaultCheckbox = false;
        }

        private string GetDefaultConfigFromINI()
        {
            return ObjectProfileSettings.GetConfiguredDefaultProfilePath();
        }

        private void UpdateDefaultConfigFile(string newValue)
        {
            ObjectProfileSettings.WriteDefaultProfilePath(newValue);
        }

        private DialogResult ShowWarningDialog()
        {
            return MessageBox.Show(
                "ВНИМАНИЕ! Вы пытаетесь открыть набор объектов от автора.\nЭтот набор содержит объекты, являющиеся спойлерами для игрового сюжета и прохождения!\n\nДействительно ли вы хотите открыть его?",
                "Предупреждение",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
        }

        private void BtnNew_Click(object sender, EventArgs e)
        {
            var newObject = new CentralObject
            {
                Name = CreateUniqueObjectName(),
                LeftMargin = 7,
                UpMargin = 7,
                FilterLevel = 100
            };

            objects.Add(newObject);
            RefreshListView(newObject);
            MarkUnsaved("Новый объект добавлен. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
            txtName.Focus();
            txtName.SelectAll();
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            if (selectedObject == null)
                return;

            string deletedName = selectedObject.Name ?? "";
            DialogResult result = MessageBox.Show(
                $"Удалить объект \"{deletedName}\"?\nКлетки карты с этим объектом будут переведены в \"{FallbackObjectName}\".",
                "Удаление объекта",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            int selectedIndex = objects.IndexOf(selectedObject);
            objects.Remove(selectedObject);
            QueueReferenceReplacement(deletedName, FallbackObjectName);

            CentralObject nextSelection = null;
            if (objects.Count > 0)
                nextSelection = objects[Math.Min(selectedIndex, objects.Count - 1)];

            RefreshListView(nextSelection);
            MarkUnsaved("Объект удален. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
        }

        private void BtnChooseIcon_Click(object sender, EventArgs e)
        {
            if (selectedObject == null)
                return;

            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Изображения (*.png; *.jpg; *.bmp)|*.png;*.jpg;*.bmp";
                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                try
                {
                    using (var sourceImage = Image.FromFile(openFileDialog.FileName))
                    {
                        selectedObject.Icon = new Bitmap(sourceImage);
                    }

                    picIcon.Image = selectedObject.Icon;
                    RefreshListView(selectedObject);
                    MarkUnsaved("Иконка обновлена. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
                }
                catch (Exception ex)
                {
                    SetStatus($"Ошибка загрузки иконки: {ex.Message}", true);
                }
            }
        }

        private void BtnClearIcon_Click(object sender, EventArgs e)
        {
            if (selectedObject == null)
                return;

            selectedObject.Icon = null;
            picIcon.Image = null;
            RefreshListView(selectedObject);
            MarkUnsaved("Иконка очищена. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
        }

        private void BtnLoadProfile_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "JSON файлы (*.json)|*.json";
                openFileDialog.InitialDirectory = Directory.Exists(Path.GetDirectoryName(ActiveProfileFileName))
                    ? Path.GetDirectoryName(ActiveProfileFileName)
                    : AppDomain.CurrentDomain.BaseDirectory;

                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                if (Path.GetFileName(openFileDialog.FileName).Equals("Author.json", StringComparison.OrdinalIgnoreCase))
                {
                    showWarningOnImport = true;
                }

                if (showWarningOnImport && ShowWarningDialog() != DialogResult.Yes)
                {
                    showWarningOnImport = false;
                    return;
                }

                showWarningOnImport = false;
                LoadObjectsFromProfile(openFileDialog.FileName);
                CheckAndUpdateDefaultFlag();
                MarkUnsaved("Набор объектов загружен. Нажмите \"Применить и закрыть\", чтобы применить его.");
            }
        }

        private void BtnSaveProfileAs_Click(object sender, EventArgs e)
        {
            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "JSON файлы (*.json)|*.json";
                saveFileDialog.FileName = string.IsNullOrWhiteSpace(ActiveProfileFileName)
                    ? "objects.json"
                    : Path.GetFileName(ActiveProfileFileName);
                saveFileDialog.InitialDirectory = Directory.Exists(Path.GetDirectoryName(ActiveProfileFileName))
                    ? Path.GetDirectoryName(ActiveProfileFileName)
                    : AppDomain.CurrentDomain.BaseDirectory;

                if (saveFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                ActiveProfileFileName = Path.GetFullPath(saveFileDialog.FileName);
                UpdateProfileLabel();
                CheckAndUpdateDefaultFlag();
                MarkUnsaved("Файл назначения выбран. Нажмите \"Применить и закрыть\", чтобы сохранить набор.");
            }
        }

        private void BtnShowPath_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ActiveProfileFileName))
                MessageBox.Show(ActiveProfileFileName, "Полный путь к файлу");
        }

        private void BtnClose_Click(object sender, EventArgs e)
        {
            TryApplyAndClose();
        }

        private void ChkUseAsDefault_CheckedChanged(object sender, EventArgs e)
        {
            if (isUpdatingDefaultCheckbox)
                return;

            MarkUnsaved(chkUseAsDefault.Checked
                ? "Набор будет назначен по умолчанию после применения."
                : "Назначение по умолчанию отменено для этого применения.");
        }

        private void TxtName_TextChanged(object sender, EventArgs e)
        {
            if (isUpdatingControls || selectedObject == null)
                return;

            string newName = txtName.Text.Trim();
            if (!TryValidateObjectName(newName, out string message))
            {
                validationLabel.Text = message;
                txtName.BackColor = Color.FromArgb(254, 242, 242);
                return;
            }

            txtName.BackColor = Color.White;
            validationLabel.Text = "";

            string oldName = selectedObject.Name ?? "";
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            selectedObject.Name = newName;
            QueueReferenceReplacement(oldName, newName);
            UpdateSelectedListItem();
            MarkUnsaved("Название изменено. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
        }

        private void NumericField_ValueChanged(object sender, EventArgs e)
        {
            if (isUpdatingControls || selectedObject == null)
                return;

            selectedObject.LeftMargin = (int)nudLeftMargin.Value;
            selectedObject.UpMargin = (int)nudUpMargin.Value;
            selectedObject.FilterLevel = (int)nudFilterLevel.Value;
            UpdateSelectedListItem();
            MarkUnsaved("Параметры изменены. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
        }

        private void ListViewObjects_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (isUpdatingControls)
                return;

            if (listViewObjects.SelectedItems.Count == 0)
            {
                SelectObject(null);
                return;
            }

            SelectObject((CentralObject)listViewObjects.SelectedItems[0].Tag);
        }

        private void ListViewObjects_ItemDrag(object sender, ItemDragEventArgs e)
        {
            DoDragDrop(e.Item, DragDropEffects.Move);
        }

        private void ListViewObjects_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(ListViewItem))
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void ListViewObjects_DragOver(object sender, DragEventArgs e)
        {
            Point clientPoint = listViewObjects.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = listViewObjects.GetItemAt(clientPoint.X, clientPoint.Y);

            if (targetItem == null)
                return;

            listViewObjects.SelectedIndices.Clear();
            targetItem.Selected = true;
        }

        private void ListViewObjects_DragDrop(object sender, DragEventArgs e)
        {
            Point clientPoint = listViewObjects.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = listViewObjects.GetItemAt(clientPoint.X, clientPoint.Y);

            if (targetItem == null)
                return;

            var draggedItem = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
            int oldIndex = draggedItem.Index;
            int newIndex = targetItem.Index;

            if (oldIndex == newIndex)
                return;

            CentralObject movedObject = objects[oldIndex];
            objects.RemoveAt(oldIndex);
            objects.Insert(newIndex, movedObject);

            RefreshListView(movedObject);
            MarkUnsaved("Порядок объектов изменен. Нажмите \"Применить и закрыть\", чтобы сохранить изменения.");
        }

        private sealed class PendingReferenceReplacement
        {
            public PendingReferenceReplacement(string oldName, string newName)
            {
                OldName = oldName;
                NewName = newName;
            }

            public string OldName { get; }
            public string NewName { get; set; }
        }
    }
}
