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


﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using IniParser.Model;
using IniParser;

namespace MMMapEditor
{
    public partial class ObjectManagementForm : Form
    {
        // Коллекция объектов
        private List<CentralObject> objects;

        // Контролы для отображения и взаимодействия с объектами
        private ListView listViewObjects;
        private Button btnAdd;
        private Button btnEdit;
        private Button btnDelete;
        private Button btnExport;
        private Button btnImport;
        private ImageList iconList; // Для хранения иконок объектов

        // Элементы для ввода данных объекта
        private Label lblName;
        private Label lblLeftMargin;
        private Label lblRightMargin;
        private Label lblFilterLevel;
        private TextBox txtName;
        private GroupBox grpObject; // Общий groupbox для всей группы
        private GroupBox grpMargins; // Отдельный groupbox для отступов
        private GroupBox grpPicIcon; // Отдельный groupbox для изображения
        private GroupBox grpList; // Отдельный groupbox для списка
        private TextBox txtLeftMargin;
        private TextBox txtRightMargin;
        private TextBox txtFilterLevel; // Поле для уровня фильтра
        private Button btnChooseIcon;
        private PictureBox picIcon;
        private Button btnApplyClose;
        private readonly MainForm mainForm;
        private string activeConfigFile;
        private CheckBox chkUseAsDefault;
        private Control currentFocusedControl;
        private int currentlySelectedlistViewItemIndex = -1;
        private bool iconChangeInProgress = false;
        private bool showWarningOnImport = false; 

        public string ActiveProfileFileName { get; private set; }

        public ObjectManagementForm(MainForm mainForm, string activeConfigFile)
        {
            InitializeComponent();

            this.MaximizeBox = false;         // Убирает кнопку максимального расширения
            this.MinimizeBox = false;         // Убирает кнопку минимизации
            this.FormBorderStyle = FormBorderStyle.FixedSingle; // Фиксируем размер окна
            this.Width = 610;
            this.Height = 380;
            this.mainForm = mainForm;
            this.Text = "Управление игровыми объектами: конфигурационный файл не выбран";

            // Инициализация ImageList для хранения иконок
            iconList = new ImageList();
            iconList.ColorDepth = ColorDepth.Depth32Bit;
            iconList.ImageSize = new Size(40, 40);

            // Создание ListView для отображения объектов
            listViewObjects = new ListView();
            listViewObjects.View = View.Details;
            listViewObjects.Columns.Add("Название объекта", -2); // Авторазмера колонки
            listViewObjects.FullRowSelect = true;
            listViewObjects.GridLines = true;
            listViewObjects.HeaderStyle = ColumnHeaderStyle.None;
            listViewObjects.LargeImageList = iconList;
            listViewObjects.SmallImageList = iconList;
            listViewObjects.Dock = DockStyle.Left;
            listViewObjects.Width = 300;

            // Подавляем горизонтальную полосу прокрутки

            // Присоединение обработчика кликов мыши
            listViewObjects.SelectedIndexChanged += listViewObjects_SelectedIndexChanged;
            listViewObjects.AllowDrop = true;
            listViewObjects.ItemDrag += listViewObjects_ItemDrag;
            listViewObjects.DragEnter += listViewObjects_DragEnter;
            listViewObjects.DragOver += listViewObjects_DragOver;
            listViewObjects.DragDrop += listViewObjects_DragDrop;
            Controls.Add(listViewObjects);

            // Основной GroupBox для редактирования
            grpObject = new GroupBox();
            grpObject.Text = "Объект";
            grpObject.Location = new Point(listViewObjects.Right + 10, listViewObjects.Top);
            grpObject.Size = new Size(280, 180);
            Controls.Add(grpObject);

            // Надписи для ввода данных
            lblName = new Label();
            lblName.Text = "Название:";
            lblName.AutoSize = true;
            lblName.Location = new Point(5, 22);
            grpObject.Controls.Add(lblName);

            // Поле ввода имени объекта
            txtName = new TextBox();
            txtName.Location = new Point(lblName.Right + 5, lblName.Top - 4);
            txtName.Size = new Size(203, 20);
            txtName.PlaceholderText = "Название нового объекта на карте";
            grpObject.Controls.Add(txtName);

            // Основной GroupBox для редактирования
            grpPicIcon = new GroupBox();
            grpPicIcon.Text = "Изображение";
            grpPicIcon.Location = new Point(lblName.Left, lblName.Bottom + 12);
            grpPicIcon.Size = new Size(100, 120);
            grpObject.Controls.Add(grpPicIcon);

            // Кнопка выбора иконки
            btnChooseIcon = new Button();
            btnChooseIcon.Text = "Выбрать ";
            btnChooseIcon.Location = new Point(5 + 2, 20);
            btnChooseIcon.Width = 80;
            btnChooseIcon.Click += btnChooseIcon_Click;
            grpPicIcon.Controls.Add(btnChooseIcon);

            // Элемент для отображения изображения объекта
            picIcon = new PictureBox();
            picIcon.Location = new Point(btnChooseIcon.Left + 8, btnChooseIcon.Bottom + 5);
            picIcon.Size = new Size(64, 64);
            picIcon.BorderStyle = BorderStyle.FixedSingle;
            picIcon.SizeMode = PictureBoxSizeMode.StretchImage;
            grpPicIcon.Controls.Add(picIcon);

            // Внутренний GroupBox для отступов
            grpMargins = new GroupBox();
            grpMargins.Text = "Отступ";
            grpMargins.Location = new Point(grpPicIcon.Right + 5, grpPicIcon.Top);
            grpMargins.Size = new Size(165, 50);
            grpObject.Controls.Add(grpMargins);

            // Отступ слева
            lblLeftMargin = new Label();
            lblLeftMargin.Text = "Слева:";
            lblLeftMargin.AutoSize = true;
            lblLeftMargin.Location = new Point(5, 22);
            grpMargins.Controls.Add(lblLeftMargin);

            txtLeftMargin = new TextBox();
            txtLeftMargin.Location = new Point(lblLeftMargin.Right + 5, lblLeftMargin.Top - 3);
            txtLeftMargin.Size = new Size(20, 20);
            txtLeftMargin.Text = "7"; // Начальное значение
            grpMargins.Controls.Add(txtLeftMargin);

            // Отступ справа
            lblRightMargin = new Label();
            lblRightMargin.Text = "Справа:";
            lblRightMargin.AutoSize = true;
            lblRightMargin.Location = new Point(txtLeftMargin.Right + 10, lblLeftMargin.Top);
            grpMargins.Controls.Add(lblRightMargin);

            txtRightMargin = new TextBox();
            txtRightMargin.Location = new Point(lblRightMargin.Right + 5, txtLeftMargin.Top);
            txtRightMargin.Size = new Size(20, 20);
            txtRightMargin.Text = "7"; // Начальное значение
            grpMargins.Controls.Add(txtRightMargin);

            // Уровень фильтра
            lblFilterLevel = new Label();
            lblFilterLevel.Text = "Уровень фильтра:";
            lblFilterLevel.AutoSize = true;
            lblFilterLevel.Location = new Point(grpMargins.Left + 10, grpMargins.Bottom + 10);
            grpObject.Controls.Add(lblFilterLevel);

            txtFilterLevel = new TextBox();
            txtFilterLevel.Location = new Point(lblFilterLevel.Right + 5, lblFilterLevel.Top - 3);
            txtFilterLevel.Size = new Size(30, 20);
            txtFilterLevel.Text = "100"; // Начальное значение
            grpObject.Controls.Add(txtFilterLevel);

            // Кнопка добавления объекта
            btnAdd = new Button();
            btnAdd.Text = "Добавить";
            btnAdd.Location = new Point(grpPicIcon.Right + 7, grpPicIcon.Bottom - 30);
            btnAdd.Click += btnAdd_Click;
            btnAdd.Enabled = false;
            grpObject.Controls.Add(btnAdd);

            // Кнопка удаления объекта
            btnDelete = new Button();
            btnDelete.Text = "Удалить";
            btnDelete.Location = new Point(btnAdd.Right + 10, btnAdd.Top);
            btnDelete.Click += btnDelete_Click;
            btnDelete.Enabled = false;
            grpObject.Controls.Add(btnDelete);

            //// Кнопка изменения объекта
            //btnEdit = new Button();
            //btnEdit.Text = "Изменить";
            //btnEdit.Location = new Point(btnAdd.Right + 10, btnAdd.Top);
            //btnEdit.Click += btnEdit_Click;
            //grpObject.Controls.Add(btnEdit);

            // Назначаем существующий метод btnEdit_Click() на нужные события
            txtName.TextChanged += btnEdit_Click;
           // picIcon.ImageChanged += btnEdit_Click;
            txtLeftMargin.TextChanged += btnEdit_Click;
            txtRightMargin.TextChanged += btnEdit_Click;
            txtFilterLevel.TextChanged += btnEdit_Click;

            txtName.Enter += Control_FocusEnter;
            txtLeftMargin.Enter += Control_FocusEnter;
            txtRightMargin.Enter += Control_FocusEnter;
            txtFilterLevel.Enter += Control_FocusEnter;
            listViewObjects.SelectedIndexChanged += Control_FocusEnter;

            txtName.Leave += Control_Leave;
            txtLeftMargin.Leave += Control_Leave;
            txtRightMargin.Leave += Control_Leave;
            txtFilterLevel.Leave += Control_Leave;

            // GroupBox для списка
            grpList = new GroupBox();
            grpList.Text = "Набор объектов";
            grpList.Location = new Point(listViewObjects.Right + 10, grpObject.Bottom + 15);
            grpList.Size = new Size(280, 80);
            Controls.Add(grpList);

            // Кнопка экспорта объектов
            btnExport = new Button();
            btnExport.Text = "Сохранить";
            btnExport.Location = new Point(10, 20);
            btnExport.Click += btnExport_Click;
            grpList.Controls.Add(btnExport);

            // Кнопка импорта объектов
            btnImport = new Button();
            btnImport.Text = "Загрузить";
            btnImport.Location = new Point(btnExport.Right + 10, btnExport.Top);
            btnImport.Click += btnImport_Click;
            grpList.Controls.Add(btnImport);

            // Кнопка "Показать путь к файлу"
            Button btnShowPath = new Button();
            btnShowPath.Text = "Полный путь";
            btnShowPath.Location = new Point(btnImport.Right + 10, btnImport.Top);
            btnShowPath.Size = new Size(btnShowPath.Width + 20, btnShowPath.Height);
            btnShowPath.Click += btnShowPath_Click;
            grpList.Controls.Add(btnShowPath);

            btnApplyClose = new Button();
            btnApplyClose.Text = "Применить и закрыть";
            btnApplyClose.Location = new Point(grpList.Right - btnApplyClose.Width - 80, grpList.Bottom + 25);
            btnApplyClose.Enabled = false;
            btnApplyClose.Click += BtnApplyClose_Click;
            btnApplyClose.Width = 150;
            Controls.Add(btnApplyClose);

            this.activeConfigFile = activeConfigFile;
            this.Shown += ObjectManagementForm_Shown;

            chkUseAsDefault = new CheckBox
            {
                Text = "Использовать по умолчанию",
                AutoSize = true,
                Location = new Point(btnExport.Left - 5, btnExport.Bottom + 10),
                Enabled = true
            };
            chkUseAsDefault.CheckedChanged += ChkUseAsDefault_CheckedChanged;
            grpList.Controls.Add(chkUseAsDefault);

            // Загрузка существующих объектов
            objects = DataManager.LoadObjects();
            RefreshListView();
        }

        private void ObjectManagementForm_Shown(object sender, EventArgs e)
        {
            // Проверка и загрузка файла при каждом открытии формы
            if (!string.IsNullOrEmpty(activeConfigFile) && File.Exists(activeConfigFile))
            {
                LoadObjectsData(activeConfigFile);
            }
            CheckAndUpdateDefaultFlag(); // проверяем флаг при открытии формы
        }

        // Метод для загрузки объектов из файла
        private void LoadObjectsData(string configCentralObjectFile)
        {
            if (showWarningOnImport && configCentralObjectFile.EndsWith("Author.json"))
            {
                // Показываем диалоговое окно подтверждения
                DialogResult dialogResult = ShowWarningDialog();

                if (dialogResult == DialogResult.No)
                {
                    // Пользователь отказался, прерываем процесс
                    return;
                }
            }

            // Продолжаем стандартную обработку файла
            if (File.Exists(configCentralObjectFile))
            {
                objects = DataManager.LoadObjects(configCentralObjectFile);
                RefreshListView();
                ProfileIsSet(configCentralObjectFile);
            }
            else
            {
                MessageBox.Show("Файл с объектами '" + configCentralObjectFile + "' не найден!\nБудет использована базовая конфигурация.");
            }
        }

        // Новый метод для показа диалогового окна
        private DialogResult ShowWarningDialog()
        {
            return MessageBox.Show(
                "ВНИМАНИЕ! Вы пытаетесь открыть набор объектов от автора.\nЭтот набор содержит объекты, являющиеся спойлерами для игрового сюжета и прохождения!\n\nДействительно ли вы хотите открыть его (на свой страх и риск)?",
                "Предупреждение!",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );
        }

        private void btnShowPath_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(ActiveProfileFileName))
            {
                MessageBox.Show(ActiveProfileFileName, "Полный путь к файлу");
            }
        }

        #region Вспомогательные методы

        // Метод проверки того, щаполнены ли все поля для возможности добавления нового объекта
        private bool IsValidForAdding()
        {
            // Проверяем, заполнено ли имя объекта
            if (string.IsNullOrWhiteSpace(txtName.Text))
                return false;

            // Проверяем, выбрана ли иконка
            if (picIcon.Image == null)
                return false;

            // Проверяем корректность значений чисел в полях
            if (!int.TryParse(txtLeftMargin.Text, out _) ||
                !int.TryParse(txtRightMargin.Text, out _) ||
                !int.TryParse(txtFilterLevel.Text, out _))
                return false;

            return true;
        }

        private void CheckAndUpdateDefaultFlag()
        {
            string defaultConfigFilePath = GetDefaultConfigFromINI();

            bool isDefault = !string.IsNullOrEmpty(defaultConfigFilePath) &&
                             Path.GetFullPath(ActiveProfileFileName).Equals(Path.GetFullPath(defaultConfigFilePath));

            chkUseAsDefault.Checked = isDefault;
            chkUseAsDefault.Enabled = !isDefault;
        }

        private string GetDefaultConfigFromINI()
        {
            string configIniPath = "Settings.ini";
            var parser = new FileIniDataParser();
            IniData data;

            if (File.Exists(configIniPath))
            {
                data = parser.ReadFile(configIniPath);
                if (data.Sections.ContainsSection("General") && data["General"].ContainsKey("DefaultConfigObjectFile"))
                {
                    return data["General"]["DefaultConfigObjectFile"];
                }
            }

            return "";
        }

        private void ChkUseAsDefault_CheckedChanged(object sender, EventArgs e)
        {
            if (chkUseAsDefault.Checked)
            {        // Когда чекбокс отмечен, фиксируем его значение
                     chkUseAsDefault.Enabled = false;
            }
        }

                private void ProfileIsSet(string profilefilename)
        {
            ActiveProfileFileName = profilefilename; // запоминаем активный профиль
            btnApplyClose.Enabled = true;
            this.Text = "Управление игровыми объектами: " + "   ..\\" +Path.GetFileName(profilefilename);
        }

        // Внутри класса ObjectManagementForm
        private void BtnApplyClose_Click(object sender, EventArgs e)
        {
            // Проверяем наличие активного файла профилей
            if (!string.IsNullOrEmpty(ActiveProfileFileName))
            {
                // Сохраняем активированный профиль в основную форму
                mainForm.ActiveConfigObjectFile = ActiveProfileFileName;

                // Перезагружаем данные в основном окне
                mainForm.ReloadData(ActiveProfileFileName);

                // Если установлен флажок "Использовать по умолчанию", обновляем файл настроек
                if (chkUseAsDefault.Checked)
                {
                    // Обновляем значение DefaultConfigObjectFile в Settings.ini
                    UpdateDefaultConfigFile(ActiveProfileFileName);
                }
            }

            // Деактивируем кнопку после применения изменений
            btnApplyClose.Enabled = false;

            // Закрываем форму
            Close();
        }

        // Внутри класса ObjectManagementForm
        private void UpdateDefaultConfigFile(string newValue)
        {
            string configIniPath = "Settings.ini";

            // Создаем объект парсера для работы с INI-файлом
            var parser = new FileIniDataParser();

            // Чтение данных из файла, если он существует
            IniData iniData;
            if (File.Exists(configIniPath))
            {
                try
                {
                    iniData = parser.ReadFile(configIniPath);
                }
                catch (Exception)
                {
                    // Если файл поврежден, очищаем его и начинаем с нуля
                    iniData = new IniData();
                }
            }
            else
            {
                // Если файла нет, создаем новый
                iniData = new IniData();
            }

            // Проверяем наличие секции General и требуемого ключа
            if (!iniData.Sections.ContainsSection("General"))
            {
                iniData.Sections.AddSection("General");
            }

            // Установка значения по умолчанию
            iniData["General"]["DefaultConfigObjectFile"] = newValue;

            // Записываем обновленную версию файла
            parser.WriteFile(configIniPath, iniData);
        }


        private void RefreshListView()
        {
            listViewObjects.Items.Clear();
            iconList.Images.Clear();

            foreach (var obj in objects)
            {
                if (obj.Icon != null)
                {
                    // Добавляем иконку в ImageList
                    iconList.Images.Add(obj.Icon);

                    // Получаем индекс вручную (последняя добавленная иконка)
                    int iconIndex = iconList.Images.Count - 1;

                    // Создаем элемент ListView с иконкой
                    ListViewItem item = new ListViewItem(obj.Name, iconIndex);
                    listViewObjects.Items.Add(item);
                }
                else
                {
                    // Без иконки
                    listViewObjects.Items.Add(new ListViewItem(obj.Name));
                }
            }
        }

        #endregion

        #region Обработчики событий

        private void Control_Leave(object sender, EventArgs e)
        {
            // Сбрасываем текущий активный элемент
            currentFocusedControl = null;
        }

        private void Control_FocusEnter(object sender, EventArgs e)
        {
            currentFocusedControl = (Control)sender;
            if (listViewObjects.SelectedIndices.Count > 0)
            {
                currentlySelectedlistViewItemIndex = listViewObjects.SelectedIndices.Count > 0 ? listViewObjects.SelectedIndices[0] : -1;
                if (currentlySelectedlistViewItemIndex == -1) btnDelete.Enabled = false; else btnDelete.Enabled = true;
            }
        }

            private void btnAdd_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtName.Text.Trim()))
            {
                MessageBox.Show("Необходимо ввести название объекта.", "Ошибка");
                return;
            }

            // Преобразуем введённые пользователем значения
            int leftMargin = 0;
            int rightMargin = 0;
            int filterLevel = 0;

            if (!int.TryParse(txtLeftMargin.Text, out leftMargin))
                leftMargin = 7; // Использовать начальное значение, если ввод неверен

            if (!int.TryParse(txtRightMargin.Text, out rightMargin))
                rightMargin = 7; // Аналогично для правого отступа

            if (!int.TryParse(txtFilterLevel.Text, out filterLevel))
                filterLevel = 100; // Значение по умолчанию для фильтра

            // Создаём новый объект с полной информацией
            var newObject = new CentralObject
            {
                Name = txtName.Text.Trim(),
                Icon = picIcon.Image,
                LeftMargin = leftMargin,
                RightMargin = rightMargin,
                FilterLevel = filterLevel
            };

            // Добавляем объект в коллекцию
            objects.Add(newObject);

            // Обновляем представление
            RefreshListView();

            btnDelete.Enabled = false; 
        }

        private void btnEdit_Click(object sender, EventArgs e)
        {

            // Берём отправителя события (текущее изменённое поле)
            var changedControl = (Control)sender;

            // Проверяем, соответствует ли текущее активное поле изменению
            if (iconChangeInProgress || currentFocusedControl == changedControl)
            { 
                if (currentlySelectedlistViewItemIndex > 0)
                {
                    var selectedObject = objects[currentlySelectedlistViewItemIndex];

                    if (selectedObject != null)
                    {
                        // Проверяем наличие данных в полях
                        if (!string.IsNullOrEmpty(txtName.Text.Trim()))
                        {
                            // Меняем свойства объекта
                            selectedObject.Name = txtName.Text.Trim();
                            selectedObject.Icon = picIcon.Image;

                            // Дополнительно обновляем численные значения
                            if (int.TryParse(txtLeftMargin.Text, out int left))
                                selectedObject.LeftMargin = left;
                            if (int.TryParse(txtRightMargin.Text, out int right))
                                selectedObject.RightMargin = right;
                            if (int.TryParse(txtFilterLevel.Text, out int filter))
                                selectedObject.FilterLevel = filter;

                            // Обновляем отображение
                            RefreshListView();
                        }
                    }
                }
            }
            btnAdd.Enabled = IsValidForAdding();
        }

        private void btnDelete_Click(object sender, EventArgs e)
        {
            if (currentlySelectedlistViewItemIndex >= 0)
            {
                var selectedObject = objects[currentlySelectedlistViewItemIndex];
                objects.Remove(selectedObject);
                RefreshListView();
                currentlySelectedlistViewItemIndex = -1;
                btnDelete.Enabled = false; 
            }
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "JSON файлы (*.json)|*.json";
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Сохраняем объекты в файл
                        DataManager.SaveObjects(objects, saveFileDialog.FileName);
                        MessageBox.Show("Данные успешно экспортированы!", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        ProfileIsSet(saveFileDialog.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при экспорте: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void btnImport_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "JSON файлы (*.json)|*.json";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        showWarningOnImport = true; // Поднимаем флаг перед началом импортирования
                        LoadObjectsData(openFileDialog.FileName);
                        showWarningOnImport = false; // Сбрасываем флаг после успешного завершения
                        CheckAndUpdateDefaultFlag(); // проверка флага после загрузки файла с набором объектов
                        currentlySelectedlistViewItemIndex = -1;
                        btnDelete.Enabled = false;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при импорте: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void btnChooseIcon_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "Изображения (*.png; *.jpg; *.bmp)|*.png;*.jpg;*.bmp";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    picIcon.Image = Image.FromFile(ofd.FileName);
                   
                    iconChangeInProgress = true;  // Включаем флаг, означающий смену иконки
                    btnEdit_Click(sender, EventArgs.Empty);
                    iconChangeInProgress = false; // Сразу сбрасываем флаг обратно
                }
            }
        }

        private void btnConfirmChanges_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtName.Text.Trim()))
            {
                MessageBox.Show("Необходимо ввести название объекта.");
                return;
            }

            // Проверяем, редактируем или добавляем объект
            CentralObject selectedObject = null;
            if (listViewObjects.SelectedItems.Count > 0)
            {
                selectedObject = objects.FirstOrDefault(o => o.Name == listViewObjects.SelectedItems[0].Text);
            }

            if (selectedObject != null)
            {
                // Редактируем существующий объект
                selectedObject.Name = txtName.Text.Trim();
                selectedObject.Icon = picIcon.Image;
            }
            else
            {
                // Добавляем новый объект
                var newObject = new CentralObject
                {
                    Name = txtName.Text.Trim(),
                    Icon = picIcon.Image
                };
                objects.Add(newObject);
            }

            // Обновляем отображение
            RefreshListView();

            btnDelete.Enabled = false; 
        }

        private void listViewObjects_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listViewObjects.SelectedItems.Count > 0)
            {
                var selectedObject = objects.FirstOrDefault(o => o.Name == listViewObjects.SelectedItems[0].Text);
                if (selectedObject != null)
                {
                    // Заполняем текстовое поле названием объекта
                    txtName.Text = selectedObject.Name;

                    // Отображаем картинку объекта
                    picIcon.Image = selectedObject.Icon;

                    // Дополнительно заполним поля с параметрами объекта
                    txtLeftMargin.Text = selectedObject.LeftMargin.ToString();
                    txtRightMargin.Text = selectedObject.RightMargin.ToString();
                    txtFilterLevel.Text = selectedObject.FilterLevel.ToString();
                }
            }
        }

        private void listViewObjects_ItemDrag(object sender, ItemDragEventArgs e)
        {
            DoDragDrop(e.Item, DragDropEffects.Move);
        }

        private void listViewObjects_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = DragDropEffects.Move;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void listViewObjects_DragOver(object sender, DragEventArgs e)
        {
            Point clientPoint = listViewObjects.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = listViewObjects.GetItemAt(clientPoint.X, clientPoint.Y);

            if (targetItem != null)
            {
                // Устанавливаем индикатор вставки перед целью
                listViewObjects.SelectedIndices.Clear();
                targetItem.Selected = true;
            }
        }

        private void listViewObjects_DragDrop(object sender, DragEventArgs e)
        {
            Point clientPoint = listViewObjects.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = listViewObjects.GetItemAt(clientPoint.X, clientPoint.Y);

            if (targetItem != null)
            {
                // Перемещаем объект в нужное место
                int oldIndex = ((ListViewItem)e.Data.GetData(typeof(ListViewItem))).Index;
                int newIndex = targetItem.Index;

                // Обновляем порядок в списке
                ListViewItem draggedItem = listViewObjects.Items[oldIndex];
                listViewObjects.Items.Remove(draggedItem);
                listViewObjects.Items.Insert(newIndex, draggedItem);

                // Переупорядочиваем внутренний список объектов
                ReorderInternalCollection(oldIndex, newIndex);
            }
        }

        // Метод переупорядочивания внутреннего списка объектов
        private void ReorderInternalCollection(int oldIndex, int newIndex)
        {
            CentralObject movedObj = objects[oldIndex];
            objects.RemoveAt(oldIndex);
            objects.Insert(newIndex, movedObj);
        }


        #endregion
    }
}
