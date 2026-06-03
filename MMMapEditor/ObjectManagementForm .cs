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
using IniParser;
using IniParser.Model;

namespace MMMapEditor
{
    public partial class ObjectManagementForm : Form
    {
        private const int AutoSaveDelayMs = 300;
        private const string FallbackObjectName = "Не исследовано";

        private readonly MainForm mainForm;
        private readonly ImageList iconList = new ImageList();
        private readonly System.Windows.Forms.Timer autoSaveTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly List<PendingReferenceReplacement> pendingReferenceReplacements =
            new List<PendingReferenceReplacement>();

        private List<CentralObject> objects = new List<CentralObject>();

        private ListView listViewObjects;
        private TextBox txtName;
        private NumericUpDown nudLeftMargin;
        private NumericUpDown nudRightMargin;
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
        private bool hasPendingSave;
        private bool showWarningOnImport;

        public string ActiveProfileFileName { get; private set; }

        public ObjectManagementForm(MainForm mainForm, string activeConfigFile)
        {
            InitializeComponent();

            this.mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            ActiveProfileFileName = ResolveInitialProfilePath(activeConfigFile);

            BuildInterface();
            ConfigureAutoSave();
            LoadObjectsFromProfile(ActiveProfileFileName);
            if (File.Exists(ActiveProfileFileName))
                ApplyCurrentProfileToMain();
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
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(820, 560);

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
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            root.Controls.Add(CreateHeaderPanel(), 0, 0);
            root.Controls.Add(CreateContentPanel(), 0, 1);
            root.Controls.Add(CreateFooterPanel(), 0, 2);
            Controls.Add(root);

            ResumeLayout(false);
        }

        private Control CreateHeaderPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(242, 245, 249)
            };

            var titleLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 30,
                Text = "Управление объектами",
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(25, 35, 48)
            };

            profileLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 28,
                ForeColor = Color.FromArgb(91, 103, 117),
                AutoEllipsis = true
            };

            panel.Controls.Add(profileLabel);
            panel.Controls.Add(titleLabel);
            return panel;
        }

        private Control CreateContentPanel()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
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

            layout.Controls.Add(objectListPanel, 0, 0);
            layout.Controls.Add(editorPanel, 1, 0);
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

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };

            btnNew = CreatePrimaryButton("Новый", 96);
            btnNew.Click += BtnNew_Click;

            btnDelete = CreateSecondaryButton("Удалить", 96);
            btnDelete.Enabled = false;
            btnDelete.Click += BtnDelete_Click;

            buttons.Controls.Add(btnNew);
            buttons.Controls.Add(btnDelete);

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

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 158,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(0, 8, 0, 0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

            txtName = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 0)
            };
            txtName.TextChanged += TxtName_TextChanged;

            nudLeftMargin = CreateNumberBox();
            nudLeftMargin.ValueChanged += NumericField_ValueChanged;

            nudRightMargin = CreateNumberBox();
            nudRightMargin.ValueChanged += NumericField_ValueChanged;

            nudFilterLevel = CreateNumberBox();
            nudFilterLevel.Maximum = 1000;
            nudFilterLevel.ValueChanged += NumericField_ValueChanged;

            validationLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(185, 28, 28),
                TextAlign = ContentAlignment.MiddleLeft
            };

            layout.Controls.Add(CreateFieldLabel("Название"), 0, 0);
            layout.Controls.Add(txtName, 1, 0);
            layout.Controls.Add(CreateFieldLabel("Отступ слева"), 0, 1);
            layout.Controls.Add(nudLeftMargin, 1, 1);
            layout.Controls.Add(CreateFieldLabel("Отступ справа"), 0, 2);
            layout.Controls.Add(nudRightMargin, 1, 2);
            layout.Controls.Add(CreateFieldLabel("Порог фильтра"), 0, 3);
            layout.Controls.Add(nudFilterLevel, 1, 3);
            layout.Controls.Add(validationLabel, 1, 4);

            var iconPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 94,
                Padding = new Padding(0)
            };

            picIcon = new PictureBox
            {
                Location = new Point(0, 4),
                Size = new Size(82, 82),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(248, 250, 252)
            };

            btnChooseIcon = CreateSecondaryButton("Выбрать иконку", 140);
            btnChooseIcon.Location = new Point(100, 8);
            btnChooseIcon.Click += BtnChooseIcon_Click;

            btnClearIcon = CreateSecondaryButton("Очистить", 100);
            btnClearIcon.Location = new Point(100, 46);
            btnClearIcon.Click += BtnClearIcon_Click;

            iconPanel.Controls.Add(picIcon);
            iconPanel.Controls.Add(btnChooseIcon);
            iconPanel.Controls.Add(btnClearIcon);

            var profileButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };

            btnLoadProfile = CreateSecondaryButton("Загрузить", 96);
            btnLoadProfile.Click += BtnLoadProfile_Click;

            btnSaveProfileAs = CreateSecondaryButton("Сохранить как", 120);
            btnSaveProfileAs.Click += BtnSaveProfileAs_Click;

            btnShowPath = CreateSecondaryButton("Путь", 64);
            btnShowPath.Click += BtnShowPath_Click;

            profileButtons.Controls.Add(btnLoadProfile);
            profileButtons.Controls.Add(btnSaveProfileAs);
            profileButtons.Controls.Add(btnShowPath);

            chkUseAsDefault = new CheckBox
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Text = "Использовать этот набор по умолчанию",
                AutoSize = false,
                ForeColor = Color.FromArgb(55, 65, 81)
            };
            chkUseAsDefault.CheckedChanged += ChkUseAsDefault_CheckedChanged;

            panel.Controls.Add(profileButtons);
            panel.Controls.Add(chkUseAsDefault);
            panel.Controls.Add(iconPanel);
            panel.Controls.Add(layout);
            panel.Controls.Add(header);

            toolTip.SetToolTip(nudLeftMargin, "Смещение пикселей слева при рисовании объекта.");
            toolTip.SetToolTip(nudRightMargin, "Смещение пикселей справа при рисовании объекта.");
            toolTip.SetToolTip(nudFilterLevel, "Порог яркости для пиксельного фильтра объекта.");

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

            btnClose = CreatePrimaryButton("Закрыть", 104);
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
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private NumericUpDown CreateNumberBox()
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Left,
                Width = 96,
                Minimum = 0,
                Maximum = 255,
                Margin = new Padding(0, 4, 0, 0)
            };
        }

        private void ConfigureAutoSave()
        {
            autoSaveTimer.Interval = AutoSaveDelayMs;
            autoSaveTimer.Tick += AutoSaveTimer_Tick;
            FormClosing += ObjectManagementForm_FormClosing;
        }

        private string ResolveInitialProfilePath(string activeConfigFile)
        {
            if (!string.IsNullOrWhiteSpace(activeConfigFile) && File.Exists(activeConfigFile))
                return Path.GetFullPath(activeConfigFile);

            string localObjectsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "objects.json");
            return Path.GetFullPath(localObjectsPath);
        }

        private void LoadObjectsFromProfile(string profilePath)
        {
            try
            {
                ActiveProfileFileName = Path.GetFullPath(profilePath);
                objects = File.Exists(ActiveProfileFileName)
                    ? DataManager.LoadObjects(ActiveProfileFileName)
                    : new List<CentralObject>();

                RefreshListView(objects.FirstOrDefault());
                UpdateProfileLabel();
                SetStatus(File.Exists(ActiveProfileFileName)
                    ? "Набор объектов загружен и применен."
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
                item.SubItems.Add($"{obj.LeftMargin}/{obj.RightMargin}");
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
            nudRightMargin.Enabled = enabled;
            nudFilterLevel.Enabled = enabled;
            btnChooseIcon.Enabled = enabled;
            btnClearIcon.Enabled = enabled;
            btnDelete.Enabled = enabled;

            if (obj == null)
            {
                txtName.Text = "";
                nudLeftMargin.Value = 0;
                nudRightMargin.Value = 0;
                nudFilterLevel.Value = 0;
                picIcon.Image = null;
                validationLabel.Text = "";
            }
            else
            {
                txtName.Text = obj.Name ?? "";
                nudLeftMargin.Value = ClampToNumericRange(nudLeftMargin, obj.LeftMargin);
                nudRightMargin.Value = ClampToNumericRange(nudRightMargin, obj.RightMargin);
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
            item.SubItems[1].Text = $"{selectedObject.LeftMargin}/{selectedObject.RightMargin}";
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

        private void ScheduleSaveAndApply(string statusText)
        {
            hasPendingSave = true;
            SetStatus(statusText);
            autoSaveTimer.Stop();
            autoSaveTimer.Start();
        }

        private void SaveAndApplyNow(string successStatus)
        {
            autoSaveTimer.Stop();

            try
            {
                if (string.IsNullOrWhiteSpace(ActiveProfileFileName))
                    ActiveProfileFileName = ResolveInitialProfilePath(null);

                DataManager.SaveObjects(objects, ActiveProfileFileName);
                ApplyCurrentProfileToMain();

                if (chkUseAsDefault.Checked)
                    UpdateDefaultConfigFile(ActiveProfileFileName);

                hasPendingSave = false;
                UpdateProfileLabel();
                CheckAndUpdateDefaultFlag();
                SetStatus($"{successStatus} {DateTime.Now:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка сохранения: {ex.Message}", true);
            }
        }

        private void ApplyCurrentProfileToMain()
        {
            if (string.IsNullOrWhiteSpace(ActiveProfileFileName))
                return;

            mainForm.ActiveConfigObjectFile = ActiveProfileFileName;
            mainForm.ReloadData(ActiveProfileFileName);
            ApplyPendingReferenceReplacements();
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
            string configIniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
            var parser = new FileIniDataParser();

            if (!File.Exists(configIniPath))
                return "";

            try
            {
                IniData data = parser.ReadFile(configIniPath);
                if (data.Sections.ContainsSection("General") &&
                    data["General"].ContainsKey("DefaultConfigObjectFile"))
                {
                    return data["General"]["DefaultConfigObjectFile"];
                }
            }
            catch
            {
                return "";
            }

            return "";
        }

        private void UpdateDefaultConfigFile(string newValue)
        {
            string configIniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
            var parser = new FileIniDataParser();
            IniData iniData;

            if (File.Exists(configIniPath))
            {
                try
                {
                    iniData = parser.ReadFile(configIniPath);
                }
                catch
                {
                    iniData = new IniData();
                }
            }
            else
            {
                iniData = new IniData();
            }

            if (!iniData.Sections.ContainsSection("General"))
                iniData.Sections.AddSection("General");

            iniData["General"]["DefaultConfigObjectFile"] = newValue;
            parser.WriteFile(configIniPath, iniData);
        }

        private DialogResult ShowWarningDialog()
        {
            return MessageBox.Show(
                "ВНИМАНИЕ! Вы пытаетесь открыть набор объектов от автора.\nЭтот набор содержит объекты, являющиеся спойлерами для игрового сюжета и прохождения!\n\nДействительно ли вы хотите открыть его?",
                "Предупреждение",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
        }

        private void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            SaveAndApplyNow("Сохранено и применено.");
        }

        private void ObjectManagementForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (hasPendingSave)
                SaveAndApplyNow("Сохранено и применено.");
        }

        private void BtnNew_Click(object sender, EventArgs e)
        {
            var newObject = new CentralObject
            {
                Name = CreateUniqueObjectName(),
                LeftMargin = 7,
                RightMargin = 7,
                FilterLevel = 100
            };

            objects.Add(newObject);
            RefreshListView(newObject);
            SaveAndApplyNow("Новый объект добавлен и применен.");
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
            SaveAndApplyNow("Объект удален и изменения применены.");
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
                    SaveAndApplyNow("Иконка обновлена и применена.");
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
            SaveAndApplyNow("Иконка очищена и изменения применены.");
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
                ApplyCurrentProfileToMain();
                CheckAndUpdateDefaultFlag();
                SetStatus("Набор объектов загружен и применен.");
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
                SaveAndApplyNow("Набор сохранен и применен.");
            }
        }

        private void BtnShowPath_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ActiveProfileFileName))
                MessageBox.Show(ActiveProfileFileName, "Полный путь к файлу");
        }

        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void ChkUseAsDefault_CheckedChanged(object sender, EventArgs e)
        {
            if (isUpdatingDefaultCheckbox || !chkUseAsDefault.Checked)
                return;

            try
            {
                UpdateDefaultConfigFile(ActiveProfileFileName);
                CheckAndUpdateDefaultFlag();
                SetStatus("Набор объектов назначен по умолчанию.");
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка сохранения настройки по умолчанию: {ex.Message}", true);
            }
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
            ScheduleSaveAndApply("Название обновляется...");
        }

        private void NumericField_ValueChanged(object sender, EventArgs e)
        {
            if (isUpdatingControls || selectedObject == null)
                return;

            selectedObject.LeftMargin = (int)nudLeftMargin.Value;
            selectedObject.RightMargin = (int)nudRightMargin.Value;
            selectedObject.FilterLevel = (int)nudFilterLevel.Value;
            UpdateSelectedListItem();
            ScheduleSaveAndApply("Параметры объекта обновляются...");
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
            SaveAndApplyNow("Порядок объектов обновлен и применен.");
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
