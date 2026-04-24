// Copyright (c) Voland007 2026. All rights reserved.
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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Xml;
using Newtonsoft.Json; // Необходим пакет Newtonsoft.Json для сериализации
using Newtonsoft.Json.Linq;
using IniParser;
using IniParser.Model;
using IniParser.Parser;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMMapEditor
{
    public partial class MainForm : Form
    {
        private const int GridSize = 16;
        private const int CellSize = 40;
        private static readonly Lazy<HashSet<string>> KnownLootItemNamesForFormatting =
            new Lazy<HashSet<string>>(BuildKnownLootItemNamesForFormatting);
        private Button[,] gridButtons;
        private bool[,] highlightedCells; // Массив для подсветки
        private Pen highlightPen = new Pen(Color.White, 2); // Пен для выделения
        private Label infoLabel; // Лейбл для отображения информации о выбранной клетке
        private List<string> options = new List<string>()
        {
            "Пустота",
            "Кирпичная стена",
            "Каменная стена",
            "Еловый лес",
            "Еловый лес(снег)",
            "Дубовый лес",
            "Дубовый лес(снег)",
            "Горы",
            "Горы (снег)",
            "Вода",
            "Пустыня",
            "Болото",
            "Барьер"
        };
        private ComboBox topComboBox, bottomComboBox, leftComboBox, rightComboBox; // Комбобоксы для границ
        private Dictionary<Point, SideValues<int>> settingsDict = new Dictionary<Point, SideValues<int>>();
        private Point? selectedPosition = null; // Текущая выделенная позиция
        private Dictionary<Point, SideValues<string>> borders = new Dictionary<Point, SideValues<string>>();
        private Dictionary<Point, SideValues<int>> passageDict = new Dictionary<Point, SideValues<int>>();
        private Dictionary<Point, SideValues<bool>> closedStates = new Dictionary<Point, SideValues<bool>>();
        private Dictionary<Point, SideValues<bool>> messageStates = new Dictionary<Point, SideValues<bool>>();
        private Dictionary<Point, string> centralOptions = new Dictionary<Point, string>(); // Словарь для хранения значения тела каждой ячейки
        private PictureBox magnifierPictureBox; // Объявление картинки, увеличивающей выделенную ячейку
        private ComboBox passTopComboBox, passBottomComboBox, passLeftComboBox, passRightComboBox; // Комбобоксы для прохода
        private ComboBox centerComboBox; //Комбобокс для тела клетки
        Dictionary<string, string> colorsMap = new Dictionary<string, string>()
        {
            {"72,0,0", "-1,-1,-1"},
            {"0,0,82", "-1,-1,-1"},
            {"0,0,50", "-1,-1,-1"},
            {"0,49,135", "-1,-1,-1"},
            {"0,29,110", "-1,-1,-1"},
            {"72,64,158", "-1,-1,-1"},
            {"116,78,180", "-1,-1,-1"},
            {"116,49,135", "-1,-1,-1"},
            {"116,0,0", "-1,-1,-1"},
            {"156,29,0", "-1,-1,-1"},
            {"191,105,180", "-1,-1,-1"},
            {"156,93,180", "-1,-1,-1"},
            {"224,64,50", "-1,-1,-1"},
            {"255,93,110", "255,105,180"},
            {"255,78,82", "255,105,180"},
            {"255,93,123", "255,105,180"},
            {"255,105,158", "255,105,180"},
            {"255,105,135", "255,105,180"},
            {"224,105,180", "255,105,180"},
            {"174,93,180", "-1,-1,-1"},
            {"191,49,0", "-1,-1,-1"},
            {"72,49,135", "-1,-1,-1"}
        };
        private CheckBox topCheck, bottomCheck, leftCheck, rightCheck;
        private CheckBox topMessageCheck, bottomMessageCheck, leftMessageCheck, rightMessageCheck;
        private RichTextBox notesTextBox;
        private Dictionary<Point, string> notesPerCell = new Dictionary<Point, string>();
        private PictureBox cellImageBox;
        private Button bufferPasteImageButton;
        private Button deleteImageButton;
        private Dictionary<Point, Image> imagesPerCell = new Dictionary<Point, Image>();
        private CheckBox isDangerCheckBox, noMagicCheckBox;
        private Dictionary<Point, bool> isDangerStates = new Dictionary<Point, bool>();
        private Dictionary<Point, bool> noMagicStates = new Dictionary<Point, bool>();
        private RadioButton lightRadioButton, darkRadioButton, darkeningRadioButton;
        private GroupBox darkeningGroupBox;
        private Dictionary<Point, Lighting> darkeningLevels = new Dictionary<Point, Lighting>();
        private Image mainMapImage;
        private ToolStripMenuItem fileMenuItem;
        private ToolStripMenuItem newMapItem, saveAsItem, loadItem, saveItem;
        private ToolStripMenuItem draftLaboratoryItem; //экспериментальный пункт меню
        private ToolStripMenuItem changeToMapsDropdown;
        private ToolStripMenuItem settingMenuItem;
        private ToolStripMenuItem toolStripMenuItemManageObjects;
        private ToolStripMenuItem searchMenuItem;
        private ToolStripMenuItem onMapsSearchItem;
        private ContextMenuStrip contextMenu;
        private CopiedCellInfo? copiedCellInfo; // Переменная для хранения временно скопированной ячейки
        private string lastSavedFilename; // Переменная для хранения пути к последней сохранённой карте
        private bool isMapModified = false; // Флаг отслеживает изменения на карте
        private readonly Dictionary<string, JObject> _objectsData = new Dictionary<string, JObject>(); // Приватное поле для хранения данных объектов из JSON
        public string ActiveConfigObjectFile { get; set; }
        private LocalizedDirectionsForm localizedDirectionsForm; // Новые свойства для управления формой настроек
        private string mapSector = "";
        private string surface = "";
        private Point? mostDangerousCell; //флаг для поментки опасной клетки
        private Point? mostPeacefulCell; // флаг для пометки безопасной клетки


        public MainForm()
        {
            InitializeComponent();
            this.Font = new Font("Segoe UI", 9f); // Явно присваиваем шрифт
            this.Width = 1080;
            this.Height = 703;
            this.Text = "Редактор моей мечты";
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.FormClosing += MainForm_FormClosing; // Подключаем обработчик события
            CreateGrid();
            CreateControls(); // Функция для создания правой панели
            InitializeAllCells(); // Начальное задание свойств каждой клетки
            CreateContxtMenu();

            // Читаем файл по умолчанию из INI
            string defaultConfigObjectFile = GetSetting("General", "DefaultConfigObjectFile");
            if (!string.IsNullOrEmpty(defaultConfigObjectFile))
            {
                // Проверяем существование файла
                if (File.Exists(defaultConfigObjectFile))
                {
                    ActiveConfigObjectFile = defaultConfigObjectFile;
                    ReloadData(defaultConfigObjectFile); // Загружаем файл
                }
                else
                {
                    // Если файл не найден, показываем предупреждение
                    MessageBox.Show("Файл с объектами по умолчанию (" + defaultConfigObjectFile + ") не найден. Будет использоваться базовая конфигурация.");
                    ReloadData(null); // Загружаем базовые объекты
                }
            }
            else
                ReloadData(defaultConfigObjectFile);

        }

        // Дополнительный метод перевода русских названий в английские
        private static string TranslateKey(string key)
        {
            switch (key)
            {
                case "Верх":
                    return "Top";
                case "Низ":
                    return "Bottom";
                case "Право":
                    return "Right";
                case "Лево":
                    return "Left";
                default:
                    return key;
            }
        }

        public static string GetSetting(string section, string key, string fallbackValue = "")
        {
            try
            {
                // Проверяем, существует ли файл
                if (!File.Exists("Settings.ini"))
                {
                    // Если файл не найден, сразу возвращаем fallbackValue
                    return fallbackValue;
                }

                // Парсим файл INI
                var parser = new FileIniDataParser();
                var iniFile = parser.ReadFile("Settings.ini");

                // Секция настроек направлений
                if (section == "CustomDirections")
                {
                    string translatedKey = TranslateKey(key);
                    string customValue = iniFile[section][translatedKey];

                    // Возвращаем кастомизированное значение, если оно есть
                    if (!string.IsNullOrWhiteSpace(customValue))
                    {
                        return customValue.Trim();
                    }
                }

                // Извлекаем значение по заданному ключу
                return iniFile[section][key];
            }
            catch (Exception ex)
            {
                // В случае возникновения ошибки при чтении или разборе файла, выводим сообщение
                MessageBox.Show($"Ошибка при чтении файла Settings.ini: {ex.Message}",
                                "Ошибка",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                // Возвращаем значение по умолчанию
                return fallbackValue;
            }
        }

        public static bool GetBooleanSetting(string section, string key, bool fallbackValue = false)
        {
            string rawValue = GetSetting(section, key, fallbackValue.ToString());
            if (bool.TryParse(rawValue, out bool boolValue))
                return boolValue;

            if (int.TryParse(rawValue, out int intValue))
                return intValue != 0;

            return fallbackValue;
        }

        public void ReloadData(string configCentralObjectFile)
        {
            _objectsData.Clear();
            LoadObjectsData(configCentralObjectFile);  // Новая загрузка данных объектов
            LoadNamesFromJson(); // Заполняем выпадающий список объектов из файла
            foreach (var button in gridButtons)
            {
                button.Invalidate(); // вызываем перерисовку для каждой кнопки
            }
        }

        // Метод для загрузки данных из JSON
        private void LoadObjectsData(string configCentralObjectFile)
        {
            if (!string.IsNullOrEmpty(configCentralObjectFile))
            {
                // Проверяем, существует ли указанный файл
                if (File.Exists(configCentralObjectFile))
                {
                    string jsonContent = File.ReadAllText(configCentralObjectFile);
                    JArray array = JArray.Parse(jsonContent);

                    foreach (JObject obj in array)
                    {
                        string name = obj["Name"].ToString();
                        _objectsData[name] = obj;
                    }
                }
                else
                {
                    // Файл не найден, выводим сообщение
                    MessageBox.Show("Файл с объектами '" + configCentralObjectFile + "' не найден.\nБудет использована базовая конфигурация.");
                }
            }

            // Если файл не указан, используем базовую конфигурацию
            _objectsData["Пустота"] = new JObject(
                new JProperty("Name", "Пустота"),
                new JProperty("LeftMargin", 0),
                new JProperty("RightMargin", 0),
                new JProperty("FilterLevel", 0),
                new JProperty("IconBase64", ""),
                new JProperty("BodyPixels", new JArray())
            );

            _objectsData["Не исследовано"] = new JObject(
                new JProperty("Name", "Не исследовано"),
                new JProperty("LeftMargin", 0),
                new JProperty("RightMargin", 0),
                new JProperty("FilterLevel", 0),
                new JProperty("IconBase64", ""),
                new JProperty("BodyPixels", new JArray())
            );

        }

        private void LoadNamesFromJson()
        {
            try
            {
                // Очистим старый список
                centerComboBox.Items.Clear();

                // Добавляем имена из нашего словаря _objectsData
                foreach (var entry in _objectsData.Keys)
                {
                    centerComboBox.Items.Add(entry);
                }

                // Устанавливаем первое значение по умолчанию
                if (centerComboBox.Items.Count > 0)
                {
                    centerComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при чтении JSON: {ex.Message}");
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isMapModified)
            {
                // Диалог подтверждения выхода
                DialogResult result = MessageBox.Show(
                "Вы действительно хотите выйти?\nВсе текущие данные будут потеряны!",
                "Выход из программы",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning
            );

                // Если пользователь нажал Cancel, отменяем закрытие окна
                if (result == DialogResult.Cancel)
                {
                    e.Cancel = true; // Отмена закрытия окна
                }
            }
        }

        private void CreateContxtMenu()
        {
            // Создаем контекстное меню
            contextMenu = new ContextMenuStrip();
            ToolStripMenuItem pasteItem = new ToolStripMenuItem("&Вставить");
            ToolStripMenuItem copyItem = new ToolStripMenuItem("&Копировать");
            ToolStripMenuItem resetItem = new ToolStripMenuItem("&Сбросить");

            pasteItem.Click += PasteItem_Click;
            copyItem.Click += CopyItem_Click;
            resetItem.Click += ResetItem_Click;

            contextMenu.Items.Add(pasteItem);
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(resetItem);

            // Назначаем контекстное меню кнопкам сетки
            foreach (var button in gridButtons)
            {
                button.ContextMenuStrip = contextMenu;
            }
        }

        private void PasteItem_Click(object sender, EventArgs e)
        {
            if (copiedCellInfo.HasValue && selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;

                // Вставляем состояние из буфера в текущую ячейку
                borders[pos] = copiedCellInfo.Value.Borders.Clone();
                passageDict[pos] = copiedCellInfo.Value.Passages.Clone();
                closedStates[pos] = copiedCellInfo.Value.ClosedStates.Clone();
                messageStates[pos] = copiedCellInfo.Value.Messages.Clone();
                centralOptions[pos] = copiedCellInfo.Value.CentralOption;
                isDangerStates[pos] = copiedCellInfo.Value.IsDanger;
                noMagicStates[pos] = copiedCellInfo.Value.NoMagic;
                darkeningLevels[pos] = copiedCellInfo.Value.DarkeningLevel;
                imagesPerCell[pos] = copiedCellInfo.Value.CellImage;
                notesPerCell[pos] = copiedCellInfo.Value.Notes;

                // Обновляем отображение
                gridButtons[pos.X, GridSize - 1 - pos.Y].Invalidate();
                UpdatePreview();
                RestoreSettings(pos);
                isMapModified = true;
            }
        }

        private void CopyItem_Click(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;

                // Копируем текущее состояние выбранной ячейки
                copiedCellInfo = new CopiedCellInfo
                {
                    Borders = borders[pos].Clone(),
                    Passages = passageDict[pos].Clone(),
                    ClosedStates = closedStates[pos].Clone(),
                    Messages = messageStates[pos].Clone(),
                    CentralOption = centralOptions[pos],
                    IsDanger = isDangerStates[pos],
                    NoMagic = noMagicStates[pos],
                    DarkeningLevel = darkeningLevels[pos],
                    CellImage = imagesPerCell[pos],
                    Notes = notesPerCell[pos]
                };
            }
        }

        private void ResetItem_Click(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                InitializeCell(pos); // Инициализирует выбранную ячейку
                gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();
                UpdatePreview();
                RestoreSettings(pos);
                isMapModified = true;
            }
        }

        private void InitializeCell(Point pos)
        {
            // Сброс настроек выбранной клетки
            borders.Remove(pos);
            passageDict.Remove(pos);
            closedStates.Remove(pos);
            messageStates.Remove(pos);
            notesPerCell.Remove(pos);
            imagesPerCell.Remove(pos);
            isDangerStates.Remove(pos);
            noMagicStates.Remove(pos);
            darkeningLevels.Remove(pos);
            centralOptions.Remove(pos);

            // Дополнительно можно добавить восстановление дефолтных значений, если это требуется
            // Например:
            borders[pos] = new SideValues<string>("Пустота", "Пустота", "Пустота", "Пустота");
            passageDict[pos] = new SideValues<int>(0, 0, 0, 0);
            closedStates[pos] = new SideValues<bool>(false, false, false, false);
            messageStates[pos] = new SideValues<bool>(false, false, false, false);
            notesPerCell[pos] = "";
            imagesPerCell[pos] = null;
            isDangerStates[pos] = false;
            noMagicStates[pos] = false;
            darkeningLevels[pos] = Lighting.Light;
            centralOptions[pos] = "Не исследовано";
        }

        private void ResetForm()
        {
            // Сброс всех чекбоксов
            isDangerCheckBox.Checked = false;
            noMagicCheckBox.Checked = false;

            // Сброс радиокнопок (возвращаем состояние обратно к первому варианту)
            lightRadioButton.Checked = true;
            darkRadioButton.Checked = false;
            darkeningRadioButton.Checked = false;

            // Сброс комбо-боксов
            topComboBox.SelectedIndex = 0;
            bottomComboBox.SelectedIndex = 0;
            leftComboBox.SelectedIndex = 0;
            rightComboBox.SelectedIndex = 0;

            // Очищаем все рисунки и изображения
            cellImageBox.Image = null;
            selectedPosition = null;
            infoLabel.Text = "Нет выделенного квадрата";

            // Очищаем тексты и заметки
            notesTextBox.Clear();
        }

        private void ClearCurrentMapState()
        {
            mapSector = "";
            surface = "";
            mostDangerousCell = null;
            mostPeacefulCell = null;
            copiedCellInfo = null;
            lastSavedFilename = "";

            InitializeAllCells();
            ResetForm();
            UpdatePreview();

            foreach (var button in gridButtons)
            {
                button.Invalidate();
            }

            this.Text = "Редактор моей мечты";
            isMapModified = false;
        }

        private void CreateGrid()
        {
            Panel leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = GridSize * CellSize + 1,
                BackColor = Color.Black
            };

            gridButtons = new Button[GridSize, GridSize];
            highlightedCells = new bool[GridSize, GridSize];

            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    var button = new Button
                    {
                        Size = new Size(CellSize, CellSize),
                        Location = new Point(x * CellSize, y * CellSize),
                        FlatStyle = FlatStyle.Flat,
                        BackgroundImageLayout = ImageLayout.None
                    };

                    button.Paint += Button_Paint;
                    button.MouseDown += Button_MouseDown;
                    button.MouseEnter += Button_MouseEnter;
                    button.MouseLeave += Button_MouseLeave;

                    gridButtons[x, y] = button;
                    leftPanel.Controls.Add(button);
                }
            }

            Controls.Add(leftPanel);
        }

        private CheckBox CreateCheckBox()
        {
            var checkBox = new CheckBox
            {
                AutoSize = true,
                ForeColor = Color.White
            };
            return checkBox;
        }

        // Обработчик изменения состояния чекбокса
        private void Checkbox_CheckedChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;

                SideValues<bool> previousClosedStates = closedStates.TryGetValue(pos, out var prevClosed)
                    ? prevClosed.Clone()
                    : new SideValues<bool>(false, false, false, false);

                SideValues<bool> previousMessageStates = messageStates.TryGetValue(pos, out var prevMsg)
                    ? prevMsg.Clone()
                    : new SideValues<bool>(false, false, false, false);

                CheckBox checkbox = (CheckBox)sender;
                bool checkedState = checkbox.Checked;

                if (checkbox == topCheck)
                    closedStates[pos].Top = checkedState;
                else if (checkbox == bottomCheck)
                    closedStates[pos].Bottom = checkedState;
                else if (checkbox == leftCheck)
                    closedStates[pos].Left = checkedState;
                else if (checkbox == rightCheck)
                    closedStates[pos].Right = checkedState;
                else if (checkbox == topMessageCheck)
                    messageStates[pos].Top = checkedState;
                else if (checkbox == bottomMessageCheck)
                    messageStates[pos].Bottom = checkedState;
                else if (checkbox == leftMessageCheck)
                    messageStates[pos].Left = checkedState;
                else if (checkbox == rightMessageCheck)
                    messageStates[pos].Right = checkedState;

                SideValues<bool> currentClosedStates = closedStates[pos];
                SideValues<bool> currentMessageStates = messageStates[pos];

                bool hasChanged =
                    !previousClosedStates.Equals(currentClosedStates) ||
                    !previousMessageStates.Equals(currentMessageStates);

                if (hasChanged)
                {
                    gridButtons[pos.X, GridSize - 1 - pos.Y].Invalidate();
                    UpdatePreview();
                    isMapModified = true;
                }
            }
        }

        private void CreateControls()
        {
            Panel rightPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 430,                     // Ширина панели
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Создаем инфо-лейбл
            infoLabel = new Label
            {
                Font = new Font(FontFamily.GenericSansSerif, 14f, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Width = 200,
                Height = 100,
                Location = new Point(6, 300),    // Местоположение чуть выше превью
                Text = "Нет выделенного квадрата",
                AutoSize = false
            };

            // Создаем многострочное поле редактирования для заметок о клетке
            notesTextBox = new RichTextBox
            {
                Multiline = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Location = new Point(infoLabel.Right + 5, 300), // позиционируйте внизу панели
                Width = 200,
                Height = 150,
                BackColor = Color.Black,              // чёрный фон
                ForeColor = Color.White,              // белый текст
                Font = new Font("Garamond", 12, FontStyle.Bold)  // шрифт полужирный
            };

            notesTextBox.TextChanged += NotesTextBox_TextChanged;
            notesTextBox.MouseDoubleClick += NotesTextBox_MouseDoubleClick;

            mainMapImage = Properties.Resources.Unknown_Panno;

            bufferPasteImageButton = new Button
            {
                // Text = "Вставить изображение",
                Location = new Point(infoLabel.Left + 45, 400),
                Width = 50,
                Height = 50,
                Image = Properties.Resources.BufferPasterButtonIcon,
                ImageAlign = ContentAlignment.MiddleLeft, // Выравнивание изображения
                                                          //  TextImageRelation = TextImageRelation.ImageBeforeText // Расположение текста относительно изображения
            };

            bufferPasteImageButton.Click += BufferPasteImageButton_Click;

            deleteImageButton = new Button
            {
                // Text = "Вставить изображение",
                Location = new Point(bufferPasteImageButton.Right + 30, 400),
                Width = 50,
                Height = 50,
                Image = Properties.Resources.TrashBasket,
                ImageAlign = ContentAlignment.MiddleLeft, // Выравнивание изображения
                                                          //  TextImageRelation = TextImageRelation.ImageBeforeText // Расположение текста относительно изображения
            };
            deleteImageButton.Click += DeleteImageButton_Click;

            // Добавляем PictureBox для изображений, которую добавляет пользователь
            cellImageBox = new PictureBox
            {
                Width = 390,
                Height = 183,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(infoLabel.Left + 14, 455),
                SizeMode = PictureBoxSizeMode.StretchImage,
            };

            cellImageBox.Paint += CellImageBox_PaintNoPicture;

            // Создаем чекбоксы
            topCheck = CreateCheckBox();
            bottomCheck = CreateCheckBox();
            leftCheck = CreateCheckBox();
            rightCheck = CreateCheckBox();

            // Создаем комбобоксы для границ
            var topCombo = CreateComboBox("Верх");
            var bottomCombo = CreateComboBox("Низ");
            var leftCombo = CreateComboBox("Лево");
            var rightCombo = CreateComboBox("Право");

            topComboBox = topCombo.Item2;
            bottomComboBox = bottomCombo.Item2;
            leftComboBox = leftCombo.Item2;
            rightComboBox = rightCombo.Item2;

            // Создаем комбобоксы для прохода
            var passTopCombo = CreatePassageComboBox("Проход вверх");
            var passBottomCombo = CreatePassageComboBox("Проход вниз");
            var passLeftCombo = CreatePassageComboBox("Проход влево");
            var passRightCombo = CreatePassageComboBox("Проход вправо");

            passTopComboBox = passTopCombo.Item2;
            passBottomComboBox = passBottomCombo.Item2;
            passLeftComboBox = passLeftCombo.Item2;
            passRightComboBox = passRightCombo.Item2;

            // Создаем новые чекбоксы для "Текст"
            topMessageCheck = CreateCheckBox();
            bottomMessageCheck = CreateCheckBox();
            leftMessageCheck = CreateCheckBox();
            rightMessageCheck = CreateCheckBox();

            // Присоединяем обработчики событий с указанием точного источника
            topCheck.CheckedChanged += Checkbox_CheckedChanged;
            bottomCheck.CheckedChanged += Checkbox_CheckedChanged;
            leftCheck.CheckedChanged += Checkbox_CheckedChanged;
            rightCheck.CheckedChanged += Checkbox_CheckedChanged;

            topMessageCheck.CheckedChanged += Checkbox_CheckedChanged;
            bottomMessageCheck.CheckedChanged += Checkbox_CheckedChanged;
            leftMessageCheck.CheckedChanged += Checkbox_CheckedChanged;
            rightMessageCheck.CheckedChanged += Checkbox_CheckedChanged;

            // Создаем tablelayoutpanel с шестью колонками
            TableLayoutPanel layout = new TableLayoutPanel
            {
                ColumnCount = 6,                  // теперь шесть колонок
                RowCount = 5,                     // пять строк
                Location = new Point(5, 150),
                Width = 433,                      // ширина совпадает с панелью
                Height = 133
            };

            // Центровка элементов с помощью padding
            layout.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;

            // Настройка колонок
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12)); // Колонка для лейблов
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); // Колонка для комбобоксов "Граница"
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 11)); // Колонка для новых чекбоксов "Текст"
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); // Колонка для комбобоксов "Проход"
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15)); // Колонка для чекбоксов закрытия

            // Настройка строк
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 15)); // Шапка фиксированной высоты
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            // Центровка элементов в шапке таблицы
            var headerLabels = new[]
            {
        new Label { Text = "", AutoSize = true, ForeColor = Color.White },
        new Label { Text = "Граница", AutoSize = true, ForeColor = Color.White },
        new Label { Text = "Текст", AutoSize = true, ForeColor = Color.White },
        new Label { Text = "Проход", AutoSize = true, ForeColor = Color.White },
        new Label { Text = "Закрыто", AutoSize = true, ForeColor = Color.White }
    };

            // Устанавливаем Anchor для централизации
            foreach (var lbl in headerLabels)
            {
                lbl.Anchor = AnchorStyles.None; // снимаем привязку
            }

            // Добавляем шапку таблицы
            layout.Controls.Add(headerLabels[0], 0, 0); // Пустая первая колонка
            layout.Controls.Add(headerLabels[1], 1, 0); // Колонка "Граница"
            layout.Controls.Add(headerLabels[2], 2, 0); // Колонка "Текст"
            layout.Controls.Add(headerLabels[3], 3, 0); // Колонка "Проход"
            layout.Controls.Add(headerLabels[4], 4, 0); // Колонка "Закрыто"

            // Основная часть таблицы (без учета первой строки-шапки)
            layout.Controls.Add(topCombo.Item1, 0, 1);  // Лейбл "Верх"
            layout.Controls.Add(topCombo.Item2, 1, 1);  // Комбобокс верхнего направления
            layout.Controls.Add(topMessageCheck, 2, 1); // Новый чекбокс "Текст" для верха
            layout.Controls.Add(passTopCombo.Item2, 3, 1);  // Комбобокс прохода сверху
            layout.Controls.Add(topCheck, 4, 1);  // Чекбокс для верхушки

            layout.Controls.Add(bottomCombo.Item1, 0, 2);  // Лейбл "Низ"
            layout.Controls.Add(bottomCombo.Item2, 1, 2);  // Комбобокс нижнего направления
            layout.Controls.Add(bottomMessageCheck, 2, 2); // Новый чекбокс "Текст" для низа
            layout.Controls.Add(passBottomCombo.Item2, 3, 2);  // Комбобокс прохода снизу
            layout.Controls.Add(bottomCheck, 4, 2);  // Чекбокс для низа

            layout.Controls.Add(leftCombo.Item1, 0, 3);  // Лейбл "Лево"
            layout.Controls.Add(leftCombo.Item2, 1, 3);  // Комбобокс левого направления
            layout.Controls.Add(leftMessageCheck, 2, 3); // Новый чекбокс "Текст" для лево
            layout.Controls.Add(passLeftCombo.Item2, 3, 3);  // Комбобокс прохода слева
            layout.Controls.Add(leftCheck, 4, 3);  // Чекбокс для лево

            layout.Controls.Add(rightCombo.Item1, 0, 4);  // Лейбл "Право"
            layout.Controls.Add(rightCombo.Item2, 1, 4);  // Комбобокс правого направления
            layout.Controls.Add(rightMessageCheck, 2, 4); // Новый чекбокс "Текст" для право
            layout.Controls.Add(passRightCombo.Item2, 3, 4);  // Комбобокс прохода справа
            layout.Controls.Add(rightCheck, 4, 4);  // Чекбокс для право

            // Централизуем все элементы, кроме заголовков
            foreach (Control control in layout.Controls)
            {
                if (!(control is Label))
                {
                    control.Anchor = AnchorStyles.None; // Централизуем остальные элементы
                }
                else
                {
                    // Только лейблы с направлением (шапка таблицы) получаем левый anchor
                    if (new[] { "Верх", "Низ", "Лево", "Право" }.Contains(((Label)control).Text))
                    {
                        control.Anchor = AnchorStyles.Left;
                    }
                    else
                    {
                        control.Anchor = AnchorStyles.None; // Остальные лейблы централизуем
                    }
                }
            }


            // Создаем picture box для превью
            magnifierPictureBox = new PictureBox
            {
                Width = 120,
                Height = 120,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(40, 20)
            };

            Panel topPanel = new Panel
            {
                Width = 250,                     // Ширина панели
                Height = 200,
                Location = new Point(magnifierPictureBox.Right + 10, magnifierPictureBox.Top - 10),
                BackColor = Color.FromArgb(30, 30, 30)
            };

            Label centerLabel = new Label
            {
                Text = "Центр",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(magnifierPictureBox.Right - 70, 0)
            };

            centerComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(centerLabel.Left - 70, centerLabel.Top + 20),
                Width = 200,
                BackColor = Color.Black,       // черный фон
                ForeColor = Color.White,       // белый текст
                FlatStyle = FlatStyle.Flat
            };

            centerComboBox.Items.AddRange(new object[] {
                "Пустота",
                "Не исследовано" });
            if (centerComboBox.Items.Count > 0)
            {
                centerComboBox.SelectedIndex = 0;
            }

            isDangerCheckBox = new CheckBox
            {
                Text = "Опасно",
                AutoSize = true,
                Location = new Point(centerComboBox.Left, centerComboBox.Bottom + 5),
                ForeColor = Color.White,
                Appearance = Appearance.Normal,
                //ImageAlign = ContentAlignment.MiddleCenter,
                //BackgroundImageLayout = ImageLayout.Center
            };
            isDangerCheckBox.CheckedChanged += IsDangerCheckBox_CheckedChanged;

            noMagicCheckBox = new CheckBox
            {
                Text = "Нет магии",
                AutoSize = true,
                Location = new Point(isDangerCheckBox.Left, isDangerCheckBox.Bottom),
                ForeColor = Color.White
            };
            noMagicCheckBox.CheckedChanged += NoMagicCheckBox_CheckedChanged;

            darkeningGroupBox = new GroupBox
            {
                Text = "Затемнённость",
                ForeColor = Color.White,
                Location = new Point(isDangerCheckBox.Right + 5, isDangerCheckBox.Top),
                Width = 90,
                Height = 80
            };

            // Радиокнопки
            lightRadioButton = new RadioButton
            {
                Text = "Светло",
                AutoSize = true,
                ForeColor = Color.White,
                Checked = true, // По умолчанию выбран "светло"
                Location = new Point(10, 15)
            };
            lightRadioButton.CheckedChanged += DarkeningRadioButton_CheckedChanged;

            darkRadioButton = new RadioButton
            {
                Text = "Темно",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 35)
            };
            darkRadioButton.CheckedChanged += DarkeningRadioButton_CheckedChanged;

            darkeningRadioButton = new RadioButton
            {
                Text = "Мрак",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 55)
            };
            darkeningRadioButton.CheckedChanged += DarkeningRadioButton_CheckedChanged;

            // Добавляем радиокнопки внутрь группы
            darkeningGroupBox.Controls.Add(lightRadioButton);
            darkeningGroupBox.Controls.Add(darkRadioButton);
            darkeningGroupBox.Controls.Add(darkeningRadioButton);


            topPanel.Controls.Add(centerLabel);
            topPanel.Controls.Add(centerComboBox);
            topPanel.Controls.Add(isDangerCheckBox);
            topPanel.Controls.Add(noMagicCheckBox);
            topPanel.Controls.Add(darkeningGroupBox);

            // Добавляем таблицу компоновки на правую панель
            rightPanel.Controls.Add(layout);
            rightPanel.Controls.Add(topPanel);
            rightPanel.Controls.Add(magnifierPictureBox); // Превью картинка
            rightPanel.Controls.Add(infoLabel); // Информация о ячейке
            rightPanel.Controls.Add(notesTextBox);
            rightPanel.Controls.Add(cellImageBox);
            rightPanel.Controls.Add(bufferPasteImageButton);
            rightPanel.Controls.Add(deleteImageButton);

            // Создание меню
            MenuStrip menuStrip = new MenuStrip();
            fileMenuItem = new ToolStripMenuItem("Карта");
            newMapItem = new ToolStripMenuItem("&Создать новую");
            newMapItem.Click += NewMapItem_Click;
            fileMenuItem.DropDownItems.Add(newMapItem);

            saveItem = new ToolStripMenuItem("&Сохранить");
            saveItem.Click += SaveItem_Click;
            fileMenuItem.DropDownItems.Add(saveItem);

            saveAsItem = new ToolStripMenuItem("&Сохранить как...");
            saveAsItem.Click += SaveAsItem_Click;
            fileMenuItem.DropDownItems.Add(saveAsItem);

            loadItem = new ToolStripMenuItem("&Загрузить");
            loadItem.Click += LoadItem_Click;
            fileMenuItem.DropDownItems.Add(loadItem);

            // Создаем пункт меню "Поменять на"
            changeToMapsDropdown = new ToolStripMenuItem("Поменять на");
            // Подменю для карт
            PopulateChangeToMapsSubmenu(changeToMapsDropdown);
            // Подписываемся на событие открытия подменю
            changeToMapsDropdown.DropDownOpening += (s, ea) =>
            {
                PopulateChangeToMapsSubmenu(changeToMapsDropdown);
            };
            // Добавляем "Поменять на" в главное меню
            fileMenuItem.DropDownItems.Add(changeToMapsDropdown);

            // Добавляем пункт "Метаданные" в меню "Карта"
            ToolStripMenuItem metadataItem = new ToolStripMenuItem("Метаданные");
            metadataItem.Click += MetadataItem_Click;
            fileMenuItem.DropDownItems.Add(metadataItem);

            // Добавляем пункт "Draft_Laboratory" в меню "Карта"
            ToolStripMenuItem draftLaboratoryItem = new ToolStripMenuItem("Открыть оригинальный .OVR");
            draftLaboratoryItem.Click += DraftLaboratoryItem_Click;
            fileMenuItem.DropDownItems.Add(draftLaboratoryItem);

            searchMenuItem = new ToolStripMenuItem("Поиск");
            onMapsSearchItem = new ToolStripMenuItem("По картам");
            onMapsSearchItem.Click += OnMapsSearchItem_Click;
            searchMenuItem.DropDownItems.Add(onMapsSearchItem);

            //  menuStrip.Items.Insert(menuStrip.Items.IndexOf(settingMenuItem), searchMenuItem);

            settingMenuItem = new ToolStripMenuItem("Настройки");
            toolStripMenuItemManageObjects = new ToolStripMenuItem("Управление объектами");

            // Добавляем подпункт в меню "Настройки"
            settingMenuItem.DropDownItems.Add(toolStripMenuItemManageObjects);

            // Создаем пункт меню "Направления"
            ToolStripMenuItem directionsMenuItem = new ToolStripMenuItem("Направления");
            directionsMenuItem.Click += DirectionsMenuItem_Click;
            // Добавляем подпункт в меню "Настройки"
            settingMenuItem.DropDownItems.Add(directionsMenuItem);

            // Создаем пункт меню "Загрузка .OVR файлов"
            ToolStripMenuItem ovrLoadSettingsMenuItem = new ToolStripMenuItem("Загрузка .OVR файлов");
            ovrLoadSettingsMenuItem.Click += OvrLoadSettingsMenuItem_Click;
            settingMenuItem.DropDownItems.Add(ovrLoadSettingsMenuItem);

            ToolStripMenuItem testMenuItem = new ToolStripMenuItem("Тестирование");
            ToolStripMenuItem runAnalyzerTestsItem = new ToolStripMenuItem("Юнит-функциональные тесты");
            runAnalyzerTestsItem.Click += RunAnalyzerTests_Click;


            menuStrip.Items.Add(testMenuItem);

            // Подписываемся на событие Click для вызова формы
            toolStripMenuItemManageObjects.Click += toolStripMenuItemManageObjects_Click;

            // Добавляем панель на главную форму
            Controls.Add(rightPanel);
            menuStrip.Items.Add(fileMenuItem);
            menuStrip.Items.Add(searchMenuItem);
            menuStrip.Items.Add(settingMenuItem);
            menuStrip.Items.Add(testMenuItem);
            testMenuItem.DropDownItems.Add(runAnalyzerTestsItem);
            Controls.Add(menuStrip);
        }

        private void RunAnalyzerTests_Click(object sender, EventArgs e)
        {
            string testsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OvrAnalyzerTests.json");

            var runner = new MMMapEditor.Tests.OvrAnalyzerTestRunner();
            var testCases = runner.LoadTestCases(testsFilePath);

            if (testCases.Count == 0)
            {
                // Создаём пример тестового файла, если его нет
                var result = MessageBox.Show(
                    "Файл с тестами не найден или имеет неверный формат.\n\n" +
                    "Создать пример тестового файла?",
                    "Файл тестов не найден",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    //заглушка
                    //runner.CreateExampleTestFile(testsFilePath);
                    //MessageBox.Show(
                    //    $"Создан пример файла тестов:\n{testsFilePath}\n\n" +
                    //    $"Отредактируйте его и запустите тесты снова.\n\n" +
                    //    $"ВАЖНО: Убедитесь, что файл содержит массив тестов (начинается с [ и заканчивается ]).",
                    //    "Файл тестов создан",
                    //    MessageBoxButtons.OK,
                    //    MessageBoxIcon.Information);
                }
                return;
            }

            // Запускаем тесты
            var results = runner.RunTests(testCases);

            // Показываем результаты
            var viewer = new MMMapEditor.Tests.TestResultsViewer(results, testsFilePath);
            viewer.ShowDialog();
        }

        private void OnMapsSearchItem_Click(object sender, EventArgs e)
        {
            var searchForm = new SearchForm();
            searchForm.ShowDialog();
        }

        private void DraftLaboratoryItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                dialog.Filter = "OVR Files (*.ovr)|*.ovr|Text Files (*.txt)|*.txt|All files (*.*)|*.*";
                dialog.Title = "Select a file to load as a Original Resource Overlay Map File";
                dialog.DefaultExt = ".ovr";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string filename = dialog.FileName;
                    OpenOriginalResourceOverlayMapFile(filename);
                    lastSavedFilename = "";
                    UpdateWindowTitle();
                }
            }
        }

        private void OpenOriginalResourceOverlayMapFile(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            string fileExtension = Path.GetExtension(filename).ToUpper();

            if (fileExtension != ".OVR")
            {
                MessageBox.Show(
                    "Ошибка: ожидается файл с расширением .OVR.",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            ClearCurrentMapState();

            // Определение общего количества строк (33 строки)
            string[] lines = new string[33];

            // Проверяем наличие конфигурации для данного файла
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                MessageBox.Show($"Конфигурация для файла {fileNameOnly} не найдена.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Получаем конфигурационные данные
            var config = OvrFileConfigs.Configs[fileNameOnly];

            // Копируем первую половину данных из конфигурации
            Array.Copy(config.First16Lines, 0, lines, 0, 16);
            // Копируем вторую половину данных из конфигурации
            Array.Copy(config.Second16Lines, 0, lines, 16, 16);

            try
            {
                // Читаем бинарный файл
                byte[] fileData = File.ReadAllBytes(filename);

                // Получаем стартовый адрес данных из конфигурации
                int startAddress = config.StartAddress;

                // Проверяем длину файла
                if (fileData.Length < startAddress)
                {
                    MessageBox.Show($"Файл слишком мал. Длина файла: {fileData.Length}, требуемый адрес: {startAddress}.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lines[32] = "";
                }
                else
                {
                    // Формируем строку данных объектов
                    StringBuilder dataLine = new StringBuilder();

                    for (int i = startAddress; i < fileData.Length; i++)
                    {
                        if (i > startAddress) dataLine.Append(" ");
                        dataLine.AppendFormat("{0:X2}", fileData[i]);
                    }

                    lines[32] = dataLine.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при чтении бинарного файла: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lines[32] = "";
            }

            // Основной цикл обработки данных карты
            for (int y = 0; y < 16; y++)
            {
                if (!string.IsNullOrWhiteSpace(lines[y]) && !string.IsNullOrWhiteSpace(lines[y + 16]))
                {
                    string[] cellValuesFirstLayer = lines[y].Split();
                    string[] cellValuesSecondLayer = lines[y + 16].Split();

                    // Проверяем правильность полученных данных
                    if (cellValuesFirstLayer.Length == 16 && cellValuesSecondLayer.Length == 16)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            ProcessCellDraft(x, y, cellValuesFirstLayer[x], cellValuesSecondLayer[x], config);
                        }
                    }
                }
            }

            var loadResult = OvrOverlayLoader.Load(
                filename,
                centralOptions,
                notesPerCell,
                messageStates);

            centralOptions = loadResult.CentralOptions;
            notesPerCell = loadResult.NotesPerCell;
            messageStates = loadResult.MessageStates;

            this.mostDangerousCell = loadResult.MostDangerousCell;
            this.mostPeacefulCell = loadResult.MostPeacefulCell;
            mapSector = loadResult.SectorMap ?? "";
            surface = loadResult.SurfaceCoords != null
                ? $"X = {loadResult.SurfaceCoords.Item1} Y = {loadResult.SurfaceCoords.Item2}"
                : "";

            string surfaceText = loadResult.SurfaceCoords != null
                ? $"X = {loadResult.SurfaceCoords.Item1} Y = {loadResult.SurfaceCoords.Item2}"
                : "X = 0 Y = 0";

            // Формируем сообщение для отображения
            string message =
                $"Название файла: {Path.GetFileName(filename)}\n" +
                $"MAP SECTOR: {loadResult.SectorMap}\n" +
                $"SURFACE: {surfaceText}\n\n" +
                $"Самая опасная клетка: {loadResult.MostDangerousCell}\n" +
                $"Самая безопасная клетка: {loadResult.MostPeacefulCell}\n\n" +
                $"Шанс случайной встречи: {loadResult.RandomEncounterChancePercent:F2}% (0x{loadResult.RandomEncounterChanceRaw:X2})\n" +
                $"Сила монстров: {loadResult.MonsterPower}\n" +
                $"Уровень монстров: {loadResult.MonsterLevel}\n" +
                $"Уровень затемнённости: {loadResult.DarkeningLevel}\n" +
                $"Количество монстров в группе: {loadResult.MonsterBatchCount}";

            // Перерисовываем интерфейс
            foreach (var button in gridButtons)
            {
                button.Invalidate();
            }

            // Выводим сообщение в всплывающем окне
            MessageBox.Show(
                message,
                "Оверлейные данные успешно загружены",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            // Информационное сообщение о завершении загрузки
            //MessageBox.Show("Лаборатория успешно загружена.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private byte ReadMonsterPower(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return 0;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int monsterPowerAddress = config.MonsterPower;

            byte[] fileData = File.ReadAllBytes(filename);

            if (monsterPowerAddress >= fileData.Length)
            {
                Console.WriteLine($"Адрес MonsterPower выходит за пределы файла.");
                return 0;
            }

            byte power = fileData[monsterPowerAddress];
            Console.WriteLine($"Сила монстра: {power}");
            return power;
        }

        private byte ReadMonsterLevel(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return 0;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int monsterLevelAddress = config.MonsterLevel;

            byte[] fileData = File.ReadAllBytes(filename);

            if (monsterLevelAddress >= fileData.Length)
            {
                Console.WriteLine($"Адрес MonsterLevel выходит за пределы файла.");
                return 0;
            }

            byte level = fileData[monsterLevelAddress];
            Console.WriteLine($"Уровень монстра: {level}");
            return level;
        }

        private byte ReadMonsterBatchCount(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return 0;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int batchCountAddress = config.MonsterBatchCount;

            byte[] fileData = File.ReadAllBytes(filename);

            if (batchCountAddress >= fileData.Length)
            {
                Console.WriteLine($"Адрес MonsterBatchCount выходит за пределы файла.");
                return 0;
            }

            byte count = fileData[batchCountAddress];
            Console.WriteLine($"Количество монстров в партии: {count}");
            return count;
        }

        private Tuple<byte, byte> ReadSurfaceCoordinates(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return null;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int surfaceXAddress = config.SurfaceX;
            int surfaceYAddress = config.SurfaceY;

            byte[] fileData = File.ReadAllBytes(filename);

            if (surfaceXAddress >= fileData.Length || surfaceYAddress >= fileData.Length)
            {
                Console.WriteLine($"Адреса Surface выходят за пределы файла.");
                return null;
            }

            byte x = fileData[surfaceXAddress];
            byte y = fileData[surfaceYAddress];

            Console.WriteLine($"Поверхностные координаты: X={x}, Y={y}");

            surface = "X = " + x.ToString() + " Y = " + y.ToString();
            return Tuple.Create(x, y);
        }

        private string ReadSectorMap(string filename)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return null;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int sectorMapHighAddress = config.SectorMapLetter;
            int sectorMapLowAddress = config.SectorMapDigit;

            byte[] fileData = File.ReadAllBytes(filename);

            if (sectorMapHighAddress >= fileData.Length || sectorMapLowAddress >= fileData.Length)
            {
                Console.WriteLine($"Адреса глобального сектора выходят за пределы файла.");
                return null;
            }

            byte highByte = fileData[sectorMapHighAddress];
            byte lowByte = fileData[sectorMapLowAddress];

            // Применяем шифрующую формулу
            char highChar = (char)(highByte - 0xC1 + 65);
            char lowChar = (char)(lowByte - 0xB1 + 49);

            string sectorMap = $"{highChar}-{lowChar}";
            Console.WriteLine($"Глобальный сектор карты: {sectorMap}");

            mapSector = sectorMap;
            return sectorMap;
        }

        private Point? ReadMostPeacefulCell(string filename)
        {
            // Получаем конфигурацию для файла
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return null;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int mostPeacefulCellAddress = config.MostPeacefulCell;

            // Читаем файл
            byte[] fileData = File.ReadAllBytes(filename);

            // Проверяем длину файла
            if (mostPeacefulCellAddress + 1 >= fileData.Length)
            {
                Console.WriteLine($"Адрес mostPeacefulCell выходит за пределы файла.");
                return null;
            }

            // Читаем координаты X и Y
            byte x = fileData[mostPeacefulCellAddress];
            byte y = fileData[mostPeacefulCellAddress + 1];

            // Координаты сохраняются в пределах от 0 до 15 включительно
            int coordX = x & 0xF;
            int coordY = y & 0xF;

            // Преобразуем в точку
            Point peacefulPoint = new Point(coordX, coordY);

            // Запоминаем координаты самой безопасной клетки
            mostPeacefulCell = peacefulPoint;

            // Добавляем заметку обычным текстом салатовго цвета
            if (notesPerCell.TryGetValue(peacefulPoint, out var currentNotes))
            {
                notesPerCell[peacefulPoint] =
                    "ЭТО САМАЯ БЕЗОПАСНАЯ КЛЕТКА НА КАРТЕ!\n" +
                    currentNotes;
            }
            else
            {
                notesPerCell[peacefulPoint] =
                    "ЭТО САМАЯ БЕЗОПАСНАЯ КЛЕТКА НА КАРТЕ!";
            }
            return peacefulPoint;
        }

        private Point? ReadMostDangerousCell(string filename)
        {
            // Получаем конфигурацию для файла
            string fileNameOnly = Path.GetFileName(filename).ToUpper();
            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
            {
                Console.WriteLine($"Конфигурация для файла {fileNameOnly} не найдена.");
                return null;
            }

            var config = OvrFileConfigs.Configs[fileNameOnly];
            int mostDangerousCellAddress = config.MostDangerousCell;

            // Читаем файл
            byte[] fileData = File.ReadAllBytes(filename);

            // Проверяем длину файла
            if (mostDangerousCellAddress + 1 >= fileData.Length)
            {
                Console.WriteLine($"Адрес mostDangerousCell выходит за пределы файла.");
                return null;
            }

            // Читаем координаты X и Y
            byte x = fileData[mostDangerousCellAddress];
            byte y = fileData[mostDangerousCellAddress + 1];

            // Координаты сохраняются в пределах от 0 до 15 включительно
            int coordX = x & 0xF;
            int coordY = y & 0xF;

            // Преобразуем в точку
            Point dangerousPoint = new Point(coordX, coordY);

            // Запоминаем координаты самой опасной клетки
            mostDangerousCell = dangerousPoint;

            // Добавляем заметку обычной текстовой информацией
            if (notesPerCell.TryGetValue(dangerousPoint, out var currentNotes))
            {
                notesPerCell[dangerousPoint] =
                    "ВНИМАНИЕ! ЭТО САМАЯ ОПАСНАЯ КЛЕТКА НА КАРТЕ!\n" +
                    currentNotes;
            }
            else
            {
                notesPerCell[dangerousPoint] =
                    "ВНИМАНИЕ! ЭТО САМАЯ ОПАСНАЯ КЛЕТКА НА КАРТЕ!";
            }
            return dangerousPoint;
        }

        private void ProcessOvrObjectsWithAdvancedAnalyzer(string filename)
        {
            try
            {
                bool useHierarchical = GetBooleanSetting("OvrLoadSettings", "Hierarchical", true);

                var buildResult = OvrNotesBuilder.BuildNotes(
                    filename,
                    centralOptions,
                    notesPerCell,
                    messageStates,
                    useHierarchical);

                centralOptions = buildResult.CentralOptions;
                notesPerCell = buildResult.NotesPerCell;
                messageStates = buildResult.MessageStates;

                foreach (var button in gridButtons)
                    button.Invalidate();

                if (selectedPosition.HasValue)
                    UpdateNotesFormatting();

                MessageBox.Show(
                    $"Загружено объектов из анализа кода: Всего {buildResult.TotalObjects} " +
                    $"(из таблицы: {buildResult.TableObjects}, AnyObjectSpec: {buildResult.SpecObjects})",
                    "Отладочная информация",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка при обработке OVR файла: {ex.Message}",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ApplyItalicSeaWaveStyle(RichTextBox rt, int startIndex, int length)
        {
            rt.Select(startIndex, length);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
            rt.SelectionColor = Color.Orange; // Цвет шрифта для служебной информации
        }

        private void ApplyBoldRedStyle(RichTextBox rt, int startIndex, int length)
        {
            rt.Select(startIndex, length);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            rt.SelectionColor = Color.FromArgb(0xB539FF);
        }

        private void ApplyVariantHeaderStyle(RichTextBox rt, int startIndex, int length, string headerText)
        {
            rt.Select(startIndex, length);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            rt.SelectionColor = Color.FromArgb(0xB539FF);
            rt.SelectionBackColor = rt.BackColor;

            Match probabilityMatch = Regex.Match(headerText, @"\([^\r\n]*\)");
            if (probabilityMatch.Success)
            {
                int probabilityStart = startIndex + probabilityMatch.Index;
                int probabilityLength = probabilityMatch.Length;

                rt.Select(probabilityStart, probabilityLength);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
                rt.SelectionColor = Color.FromArgb(210, 190, 255);
                rt.SelectionBackColor = Color.FromArgb(45, 28, 60);
            }

            Match choiceMatch = Regex.Match(headerText, @":\s*(([A-ZА-ЯЁ]+|\d+)\)\s*)$");
            if (choiceMatch.Success)
            {
                Group choiceGroup = choiceMatch.Groups[1];
                int choiceStart = startIndex + choiceGroup.Index;
                int choiceLength = choiceGroup.Length;

                rt.Select(choiceStart, choiceLength);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                rt.SelectionColor = Color.FromArgb(255, 220, 120);
                rt.SelectionBackColor = Color.FromArgb(60, 45, 20);
            }
        }

        private void FormatRainbowPartysexNotes(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText))
                return;

            ApplyRainbowBackgroundToExactText(rt, noteText, "Меняется пол у женщин в партии");
            ApplyRainbowBackgroundToExactText(rt, noteText, "Меняется пол у мужчин в партии");
        }

        private void ApplyRainbowBackgroundToExactText(RichTextBox rt, string noteText, string targetText)
        {
            int searchStart = 0;

            while (searchStart < noteText.Length)
            {
                int matchIndex = noteText.IndexOf(targetText, searchStart, StringComparison.Ordinal);
                if (matchIndex < 0)
                    break;

                ApplyRainbowBackgroundStyle(rt, matchIndex, targetText);
                searchStart = matchIndex + targetText.Length;
            }
        }

        private void ApplyRainbowBackgroundStyle(RichTextBox rt, int startIndex, string text)
        {
            Color[] rainbowStops = new Color[]
            {
                Color.FromArgb(230, 60, 60),
                Color.FromArgb(255, 140, 0),
                Color.FromArgb(255, 215, 0),
                Color.FromArgb(70, 190, 110),
                Color.FromArgb(70, 150, 255),
                Color.FromArgb(75, 0, 130),
                Color.FromArgb(180, 70, 220)
            };

            int coloredCharacterCount = 0;
            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch))
                    coloredCharacterCount++;
            }

            if (coloredCharacterCount == 0)
                return;

            int coloredCharacterIndex = 0;
            Color currentBackgroundColor = rainbowStops[0];

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    double progress = coloredCharacterCount == 1
                        ? 1.0
                        : (double)coloredCharacterIndex / (coloredCharacterCount - 1);

                    currentBackgroundColor = InterpolateRainbowColor(rainbowStops, progress);
                    coloredCharacterIndex++;
                }

                rt.Select(startIndex + i, 1);
                rt.SelectionBackColor = currentBackgroundColor;
                rt.SelectionColor = GetReadableTextColor(currentBackgroundColor);
            }
        }

        private Color InterpolateRainbowColor(Color[] rainbowStops, double progress)
        {
            if (rainbowStops == null || rainbowStops.Length == 0)
                return Color.Black;

            if (rainbowStops.Length == 1)
                return rainbowStops[0];

            if (progress <= 0)
                return rainbowStops[0];

            if (progress >= 1)
                return rainbowStops[rainbowStops.Length - 1];

            double scaledProgress = progress * (rainbowStops.Length - 1);
            int leftIndex = (int)Math.Floor(scaledProgress);
            int rightIndex = Math.Min(leftIndex + 1, rainbowStops.Length - 1);
            double segmentProgress = scaledProgress - leftIndex;

            Color leftColor = rainbowStops[leftIndex];
            Color rightColor = rainbowStops[rightIndex];

            return Color.FromArgb(
                (int)Math.Round(leftColor.R + ((rightColor.R - leftColor.R) * segmentProgress)),
                (int)Math.Round(leftColor.G + ((rightColor.G - leftColor.G) * segmentProgress)),
                (int)Math.Round(leftColor.B + ((rightColor.B - leftColor.B) * segmentProgress)));
        }

        private Color GetReadableTextColor(Color backgroundColor)
        {
            double brightness = (backgroundColor.R * 0.299) +
                (backgroundColor.G * 0.587) +
                (backgroundColor.B * 0.114);

            return brightness >= 150 ? Color.Black : Color.White;
        }

        // Теперь форматируем силу, уровень, затемнённость и шанс случайной встречи
        private void FormatMapLevelMetaParameters(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText)) return;

            // Сила монстров
            var powerMatches = Regex.Matches(
                noteText,
                @"Сила монстров (увеличивается с \d+ до \d+|уменьшается с \d+ до \d+|остаётся прежней: \d+)"
            );

            foreach (Match match in powerMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(135, 206, 250); // светло-голубой
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Уровень монстров
            var levelMatches = Regex.Matches(
                noteText,
                @"Уровень монстров (увеличивается с \d+ до \d+|уменьшается с \d+ до \d+|остаётся прежним: \d+)"
            );

            foreach (Match match in levelMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(120, 180, 245); // более светлый голубой
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Количество монстров в группе
            var batchCountMatches = Regex.Matches(
                noteText,
                @"Количество монстров в группе (увеличивается с \d+ до \d+|уменьшается с \d+ до \d+|остаётся прежним: \d+)"
            );

            foreach (Match match in batchCountMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(130, 200, 170);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Уровень затемнённости
            var lightingMatches = Regex.Matches(
                noteText,
                @"Уровень затемнённости (увеличивается с \d+ до \d+|уменьшается с \d+ до \d+|остаётся прежним: \d+)"
            );

            foreach (Match match in lightingMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(145, 130, 235); // 
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Шанс случайной встречи
            var chanceMatches = Regex.Matches(
                noteText,
                @"Шанс случайной встречи (увеличивается с [\d.,]+% \(0x[0-9A-F]{2}\) до [\d.,]+% \(0x[0-9A-F]{2}\)|уменьшается с [\d.,]+% \(0x[0-9A-F]{2}\) до [\d.,]+% \(0x[0-9A-F]{2}\)|остаётся прежним: [\d.,]+% \(0x[0-9A-F]{2}\))",
                RegexOptions.IgnoreCase
            );

            foreach (Match match in chanceMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(150, 160, 175); // 
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }
        }

        /// <summary>
        /// Форматирование информации о битве с конкретными монстрами
        /// </summary>
        private void FormatMonsterBattleInfo(RichTextBox rt, string noteText)
        {
            // Заголовок группы
            var groupHeaderMatches = Regex.Matches(
                noteText,
                @"^([ \t]*)(Битва с группой монстров:)$",
                RegexOptions.Multiline);

            foreach (Match match in groupHeaderMatches)
            {
                Group textGroup = match.Groups[2];
                rt.Select(textGroup.Index, textGroup.Length);
                rt.SelectionColor = Color.LightYellow;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Underline);
            }

            // Строки с монстрами
            var bulletMatches = Regex.Matches(
                noteText,
                @"^([ \t]*)(•\s+[^\n]+?\s+x(\d+|\d+-\d+|\? \(Random count\)))$",
                RegexOptions.Multiline);

            for (int bulletIndex = 0; bulletIndex < bulletMatches.Count; bulletIndex++)
            {
                Match match = bulletMatches[bulletIndex];
                Group bulletTextGroup = match.Groups[2];

                string bulletText = bulletTextGroup.Value;
                bool isRandom = bulletText.Contains("x? (Random count)");

                Color lineColor;
                if (isRandom)
                    lineColor = (bulletIndex % 2 == 0) ? Color.FromArgb(255, 71, 151) : Color.FromArgb(255, 96, 171);
                else
                    lineColor = (bulletIndex % 2 == 0) ? Color.FromArgb(240, 31, 111) : Color.FromArgb(255, 76, 146);

                rt.Select(bulletTextGroup.Index, bulletTextGroup.Length);
                rt.SelectionColor = lineColor;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                if (isRandom)
                {
                    int xPos = bulletText.LastIndexOf('x');
                    if (xPos >= 0)
                    {
                        rt.Select(bulletTextGroup.Index + xPos, bulletText.Length - xPos);
                        rt.SelectionColor = Color.FromArgb(255, 51, 131);
                        rt.SelectionFont = new Font(rt.Font, FontStyle.Regular);

                        int randomCountPos = bulletText.IndexOf("(Random count)");
                        if (randomCountPos >= 0)
                        {
                            rt.Select(bulletTextGroup.Index + randomCountPos, "(Random count)".Length);
                            rt.SelectionColor = Color.FromArgb(255, 51, 131);
                            rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
                        }
                    }
                }
                else
                {
                    var countMatch = Regex.Match(bulletText, @"x(\d+|\d+-\d+)$");
                    if (countMatch.Success)
                    {
                        int countIndex = bulletTextGroup.Index + bulletText.LastIndexOf('x');
                        rt.Select(countIndex, bulletText.Length - bulletText.LastIndexOf('x'));
                        rt.SelectionColor = Color.FromArgb(255, 182, 193);
                        rt.SelectionFont = new Font(rt.Font, FontStyle.Regular);
                    }
                }
            }
        }

        /// <summary>
        /// Форматирование для loot-блоков
        /// </summary>
        private void FormatLootBlocks(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText))
                return;

            // Подчёркиваем только имя контейнера внутри строки:
            // "На ячейке находится GOLD CHEST в котором лежит:"
            var containerLineMatches = Regex.Matches(
                noteText,
                @"На ячейке находится\s+(.*?)\s+в котором лежит:",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match match in containerLineMatches)
            {
                if (!match.Success || match.Groups.Count < 2)
                    continue;

                // Сначала перекрашиваем всю строку контейнера в холодный оттенок,
                // чтобы она заметно отличалась от тёплого цвета ITEM/предметов
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(170, 205, 255);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group containerNameGroup = match.Groups[1];
                if (containerNameGroup.Length <= 0)
                    continue;

                // Затем усиливаем только имя контейнера
                rt.Select(containerNameGroup.Index, containerNameGroup.Length);
                rt.SelectionColor = Color.FromArgb(255, 215, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Underline);
            }

            var numberedLootMatches = Regex.Matches(noteText, @"^(\s*\d+[\)\.]\s+)([^\n]+)$", RegexOptions.Multiline);
            foreach (Match match in numberedLootMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 236, 139);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group numberGroup = match.Groups[1];
                rt.Select(numberGroup.Index, numberGroup.Length);
                rt.SelectionColor = Color.FromArgb(255, 170, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                string body = match.Groups[2].Value;
                int bodyStart = match.Groups[2].Index;

                var valueMatch = Regex.Match(body, @"\b(\d+(?:-\d+)?\s+GEMS?|GEMS?[:\s]+\d+(?:-\d+)?|\d+(?:-\d+)?\s+GOLD|GOLD[:\s]+\d+(?:-\d+)?)\b", RegexOptions.IgnoreCase);
                if (valueMatch.Success)
                {
                    string valueText = valueMatch.Value;
                    Color lootValueColor = Regex.IsMatch(valueText, @"GEMS?", RegexOptions.IgnoreCase)
                        ? Color.FromArgb(105, 228, 185)
                        : Color.FromArgb(165, 235, 120);

                    rt.Select(bodyStart + valueMatch.Index, valueMatch.Length);
                    rt.SelectionColor = lootValueColor;
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }

                var itemMatch = Regex.Match(body, @"^(предмет\b|ITEM\b[:\s]*)", RegexOptions.IgnoreCase);
                if (itemMatch.Success)
                {
                    rt.Select(bodyStart + itemMatch.Index, itemMatch.Length);
                    rt.SelectionColor = Color.FromArgb(255, 245, 180);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);
                }
            }

            var singleLootValueMatches = Regex.Matches(noteText, @"^\s*(предмет\b.*|ITEM[: ].*|\d+(?:-\d+)?\s+GEMS?$|GEMS?[:\s]+\d+(?:-\d+)?$|\d+(?:-\d+)?\s+GOLD$|GOLD[:\s]+\d+(?:-\d+)?$)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match match in singleLootValueMatches)
            {
                bool startsWithNumbering = Regex.IsMatch(match.Value, @"^\s*\d+[\)\.]\s+");
                if (startsWithNumbering)
                    continue;

                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 236, 139);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                string body = match.Value.TrimStart();
                int bodyStart = match.Index + (match.Value.Length - body.Length);

                var valueMatch = Regex.Match(body, @"\b(\d+(?:-\d+)?\s+GEMS?|GEMS?[:\s]+\d+(?:-\d+)?|\d+(?:-\d+)?\s+GOLD|GOLD[:\s]+\d+(?:-\d+)?)\b", RegexOptions.IgnoreCase);
                if (valueMatch.Success)
                {
                    string valueText = valueMatch.Value;
                    Color lootValueColor = Regex.IsMatch(valueText, @"GEMS?", RegexOptions.IgnoreCase)
                        ? Color.FromArgb(105, 228, 185)
                        : Color.FromArgb(165, 235, 120);

                    rt.Select(bodyStart + valueMatch.Index, valueMatch.Length);
                    rt.SelectionColor = lootValueColor;
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }

                var itemMatch = Regex.Match(body, @"^(предмет\b|ITEM\b[:\s]*)", RegexOptions.IgnoreCase);
                if (itemMatch.Success)
                {
                    rt.Select(bodyStart + itemMatch.Index, itemMatch.Length);
                    rt.SelectionColor = Color.FromArgb(255, 245, 180);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);
                }
            }

            var probabilityHeaderMatches = Regex.Matches(
                noteText,
                @"^(\s*)(\d+[\)\.]\s+)?(Возможный предмет:|Возможные предметы:|Possible item:|Possible items:|Случайный предмет:)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match match in probabilityHeaderMatches)
            {
                if (!match.Success || match.Groups.Count < 4)
                    continue;

                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 236, 139);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group numberGroup = match.Groups[2];
                if (numberGroup.Success && numberGroup.Length > 0)
                {
                    rt.Select(numberGroup.Index, numberGroup.Length);
                    rt.SelectionColor = Color.FromArgb(255, 170, 0);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }

                Group headerGroup = match.Groups[3];
                rt.Select(headerGroup.Index, headerGroup.Length);
                rt.SelectionColor = Color.Black;
                rt.SelectionBackColor = Color.FromArgb(180, 230, 255);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);
            }

            var probabilitySubItemMatches = Regex.Matches(
                noteText,
                @"^(\s*•\s+)([^\n]+?)(\s+\(\d+(?:[\.,]\d+)?%\))$",
                RegexOptions.Multiline);

            foreach (Match match in probabilitySubItemMatches)
            {
                if (!match.Success || match.Groups.Count < 4)
                    continue;

                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(210, 230, 255);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Regular);

                Group bulletGroup = match.Groups[1];
                rt.Select(bulletGroup.Index, bulletGroup.Length);
                rt.SelectionColor = Color.FromArgb(120, 180, 245);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group itemGroup = match.Groups[2];
                rt.Select(itemGroup.Index, itemGroup.Length);
                rt.SelectionColor = Color.FromArgb(255, 245, 180);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group probabilityGroup = match.Groups[3];
                rt.Select(probabilityGroup.Index, probabilityGroup.Length);
                rt.SelectionColor = Color.FromArgb(165, 235, 120);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
            }

            FormatUnnumberedLootPayloadsInsideContainerBlocks(rt, noteText);
        }

        private void FormatUnnumberedLootPayloadsInsideContainerBlocks(RichTextBox rt, string noteText)
        {
            bool insideLootBlock = false;
            int lineStart = 0;
            string[] lines = noteText.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                string line = rawLine.TrimEnd('\r');

                if (IsFormattingContainerLootIntroLine(line))
                {
                    insideLootBlock = true;
                }
                else if (insideLootBlock)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        insideLootBlock = false;
                    }
                    else if (Regex.IsMatch(line, @"^\s*\d+[\)\.]\s+"))
                    {
                        // Уже отформатировано как нумерованная запись выше.
                    }
                    else if (IsFormattingProbabilityLootHeader(line))
                    {
                        ApplyStandaloneProbabilityHeaderStyle(rt, lineStart, line);
                    }
                    else if (IsFormattingExplicitLootValueLine(line) || IsFormattingPlainLootItemLine(line))
                    {
                        ApplyStandaloneLootPayloadStyle(rt, lineStart, line);
                    }
                    else if (!IsFormattingProbabilityLootItemLine(line))
                    {
                        insideLootBlock = false;
                    }
                }

                lineStart += rawLine.Length;
                if (i < lines.Length - 1)
                    lineStart++;
            }
        }

        private void ApplyStandaloneProbabilityHeaderStyle(RichTextBox rt, int lineStart, string line)
        {
            rt.Select(lineStart, line.Length);
            rt.SelectionColor = Color.FromArgb(255, 236, 139);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

            string header = line.TrimStart();
            int headerStart = lineStart + (line.Length - header.Length);

            rt.Select(headerStart, header.Length);
            rt.SelectionColor = Color.Black;
            rt.SelectionBackColor = Color.FromArgb(180, 230, 255);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);
        }

        private void ApplyStandaloneLootPayloadStyle(RichTextBox rt, int lineStart, string line)
        {
            rt.Select(lineStart, line.Length);
            rt.SelectionColor = Color.FromArgb(255, 236, 139);
            rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

            string body = line.TrimStart();
            int bodyStart = lineStart + (line.Length - body.Length);

            var valueMatch = Regex.Match(
                body,
                @"\b(\d+(?:-\d+)?\s+GEMS?|GEMS?[:\s]+\d+(?:-\d+)?|\d+(?:-\d+)?\s+GOLD|GOLD[:\s]+\d+(?:-\d+)?)\b",
                RegexOptions.IgnoreCase);
            if (valueMatch.Success)
            {
                string valueText = valueMatch.Value;
                Color lootValueColor = Regex.IsMatch(valueText, @"GEMS?", RegexOptions.IgnoreCase)
                    ? Color.FromArgb(105, 228, 185)
                    : Color.FromArgb(165, 235, 120);

                rt.Select(bodyStart + valueMatch.Index, valueMatch.Length);
                rt.SelectionColor = lootValueColor;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            var itemMatch = Regex.Match(body, @"^(предмет\b|ITEM\b[:\s]*)", RegexOptions.IgnoreCase);
            if (itemMatch.Success)
            {
                rt.Select(bodyStart + itemMatch.Index, itemMatch.Length);
                rt.SelectionColor = Color.FromArgb(255, 245, 180);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);
            }
        }

        private static bool IsFormattingContainerLootIntroLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            return trimmed.StartsWith("На ячейке находится ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith("в котором лежит:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFormattingProbabilityLootHeader(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveFormattingLootNumbering(line.Trim());
            return trimmed.Equals("Возможный предмет:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Случайный предмет:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Возможные предметы:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Possible item:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Possible items:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFormattingProbabilityLootItemLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveFormattingProbabilityBullet(RemoveFormattingLootNumbering(line.Trim()));
            return Regex.IsMatch(
                trimmed,
                @"^[A-ZА-ЯЁ][A-ZА-ЯЁ0-9 '\-\+\.]{1,60}\s+\(\d+(?:[.,]\d+)?%\)$",
                RegexOptions.CultureInvariant);
        }

        private static bool IsFormattingExplicitLootValueLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveFormattingLootNumbering(line.Trim());
            string upper = trimmed.ToUpperInvariant();

            if (trimmed.StartsWith("предмет", StringComparison.OrdinalIgnoreCase))
                return true;

            if (upper.StartsWith("ITEM ") || upper.StartsWith("ITEM:"))
                return true;

            if (Regex.IsMatch(upper, @"^\d+(?:-\d+)?\s+GEMS?$"))
                return true;

            if (Regex.IsMatch(upper, @"^GEMS?[:\s]+\d+(?:-\d+)?$"))
                return true;

            if (Regex.IsMatch(upper, @"^\d+(?:-\d+)?\s+GOLD$"))
                return true;

            if (Regex.IsMatch(upper, @"^GOLD[:\s]+\d+(?:-\d+)?$"))
                return true;

            return false;
        }

        private static bool IsFormattingPlainLootItemLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveFormattingLootNumbering(line.Trim());
            if (trimmed.Length == 0)
                return false;

            if (IsFormattingContainerLootIntroLine(trimmed) || IsFormattingProbabilityLootHeader(trimmed))
                return false;

            if (IsFormattingExplicitLootValueLine(trimmed) || IsFormattingProbabilityLootItemLine(trimmed))
                return false;

            if (trimmed.Length > 60)
                return false;

            if (trimmed.Contains("\"") || trimmed.Contains("...") || trimmed.Contains("! ") || trimmed.Contains("? "))
                return false;

            if (trimmed.Contains(":") || trimmed.Contains(";") || trimmed.Contains(",") || trimmed.Contains("(") || trimmed.Contains(")"))
                return false;

            string normalized = NormalizeFormattingLootItemIdentity(trimmed);
            return normalized.Length > 0 && KnownLootItemNamesForFormatting.Value.Contains(normalized);
        }

        private static HashSet<string> BuildKnownLootItemNamesForFormatting()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in ItemDatabase.Items)
            {
                string normalized = NormalizeFormattingLootItemIdentity(item?.Name);
                if (!string.IsNullOrEmpty(normalized))
                    result.Add(normalized);
            }

            return result;
        }

        private static string NormalizeFormattingLootItemIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = RemoveFormattingProbabilityBullet(RemoveFormattingLootNumbering(value.Trim()));
            if (normalized.Length == 0)
                return string.Empty;

            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.ToUpperInvariant();
        }

        private static string RemoveFormattingLootNumbering(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            string trimmed = line.TrimStart();
            Match match = Regex.Match(trimmed, @"^\d+[\)\.]\s+");
            return match.Success ? trimmed.Substring(match.Length) : trimmed;
        }

        private static string RemoveFormattingProbabilityBullet(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            string trimmed = line.TrimStart();
            Match match = Regex.Match(trimmed, @"^•\s+");
            return match.Success ? trimmed.Substring(match.Length) : trimmed;
        }

        /// <summary>
        /// Вспомогательный метод для определения номера строки по позиции в тексте
        /// </summary>
        private int GetLineNumber(string text, int position)
        {
            int lineNumber = 0;
            int currentPos = 0;

            while (currentPos < position && currentPos < text.Length)
            {
                int nextNewLine = text.IndexOf('\n', currentPos);
                if (nextNewLine == -1) break;

                if (position <= nextNewLine)
                    break;

                currentPos = nextNewLine + 1;
                lineNumber++;
            }

            return lineNumber;
        }

        private void UpdateNotesFormatting()
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                string noteText = notesPerCell[pos];

                // Очищаем выделение
                notesTextBox.DeselectAll();
                notesTextBox.Text = noteText;
                notesTextBox.BackColor = Color.Black;
                notesTextBox.ForeColor = Color.White;

                // Курсив и морская волна для фразы "Эта ячейка содержит различные варианты текста"
                int introIndex = noteText.IndexOf("Эта ячейка содержит различные варианты текста");
                if (introIndex >= 0)
                {
                    ApplyItalicSeaWaveStyle(notesTextBox, introIndex, "Эта ячейка содержит различные варианты текста".Length);
                }

                // Форматирование заголовков вариантов и вероятности в скобках
                MatchCollection matches = Regex.Matches(
                    noteText,
                    @"^[ \t]*Вариант\s+\d+(?:\.\d+)*(?:\s*\([^\r\n]*\))?(?::\s*(?:[A-ZА-ЯЁ]+|\d+)\)|:)",
                    RegexOptions.Multiline
                );
                foreach (Match match in matches)
                {
                    ApplyVariantHeaderStyle(notesTextBox, match.Index, match.Length, match.Value);
                }

                // Форматирование заметки "Ничего не происходит" и пояснения в скобках
                MatchCollection nothingHappensMatches = Regex.Matches(
                    noteText,
                    @"Ничего не происходит(?:\s*\((не выполнены условия для наступления ни одного варианта)\))?"
                );
                foreach (Match match in nothingHappensMatches)
                {
                    notesTextBox.Select(match.Index, match.Length);
                    notesTextBox.SelectionColor = Color.Black;
                    notesTextBox.SelectionBackColor = Color.White;
                    notesTextBox.SelectionFont = new Font(notesTextBox.Font, FontStyle.Bold);

                    if (match.Groups[1].Success)
                    {
                        notesTextBox.Select(match.Groups[1].Index - 1, match.Groups[1].Length + 2);
                        notesTextBox.SelectionColor = Color.Black;
                        notesTextBox.SelectionBackColor = Color.White;
                        notesTextBox.SelectionFont = new Font(notesTextBox.Font, FontStyle.Italic);
                    }
                }

                // Проверяем, является ли данная клетка самой опасной
                if (mostDangerousCell.HasValue && pos == mostDangerousCell.Value)
                {
                    int importantStart = noteText.IndexOf("ВНИМАНИЕ! ЭТО САМАЯ ОПАСНАЯ КЛЕТКА НА КАРТЕ!");
                    if (importantStart >= 0)
                    {
                        int importantEnd = noteText.IndexOf("\n", importantStart);
                        if (importantEnd < 0) importantEnd = noteText.Length;

                        notesTextBox.Select(importantStart, importantEnd - importantStart);
                        notesTextBox.SelectionColor = Color.FromArgb(0xFF3824);
                        notesTextBox.SelectionFont = new Font(notesTextBox.Font, FontStyle.Bold);
                    }
                }

                // Проверяем, является ли данная клетка самой безопасной
                if (mostPeacefulCell.HasValue && pos == mostPeacefulCell.Value)
                {
                    int importantStart = noteText.IndexOf("ЭТО САМАЯ БЕЗОПАСНАЯ КЛЕТКА НА КАРТЕ!");
                    if (importantStart >= 0)
                    {
                        int importantEnd = noteText.IndexOf("\n", importantStart);
                        if (importantEnd < 0) importantEnd = noteText.Length;

                        notesTextBox.Select(importantStart, importantEnd - importantStart);
                        notesTextBox.SelectionColor = Color.LimeGreen;
                        notesTextBox.SelectionFont = new Font(notesTextBox.Font, FontStyle.Bold);
                    }
                }

                // Форматирование для силы, уровня, затемнённости и шанса случайной встречи
                FormatMapLevelMetaParameters(notesTextBox, noteText);

                // Форматирование для информации о битве с монстрами
                FormatMonsterBattleInfo(notesTextBox, noteText);

                // Форматирование для частично определённых битв
                FormatPartiallyDefinedBattles(notesTextBox, noteText);

                // Форматирование для служебных предупреждений
                FormatServiceWarnings(notesTextBox, noteText);

                // Форматирование для временных технических заметок
                FormatTemporaryTechnicalNotes(notesTextBox, noteText);

                // Форматирование для loot-блоков
                FormatLootBlocks(notesTextBox, noteText);

                // Радужный фон для заметок о смене пола в партии
                FormatRainbowPartysexNotes(notesTextBox, noteText);
            }
        }

        /// <summary>
        /// Форматирование для частично определённых битв
        /// </summary>
        private void FormatPartiallyDefinedBattles(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText)) return;

            // Ищем заголовки "Частично определённая битва"
            var headerMatches = Regex.Matches(noteText, @"Частично определённая битва[^\n]*");
            foreach (Match match in headerMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 165, 0); // Оранжевый
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Ищем варианты монстров (• Вариант X: Имя монстра)
            var variantMatches = Regex.Matches(noteText, @"  • Вариант \d+: [^\n]*");
            foreach (Match match in variantMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(180, 230, 255); // Светло-голубой
                rt.SelectionBackColor = rt.BackColor;

                // Выделяем номер варианта жирным
                int colonIndex = match.Value.IndexOf(':');
                if (colonIndex > 0)
                {
                    rt.Select(match.Index, colonIndex + 1);
                    rt.SelectionColor = Color.FromArgb(100, 200, 255);
                    rt.SelectionBackColor = rt.BackColor;
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }

                // Слегка подсвечиваем голубым фон количества монстров в конце строки
                var countMatch = Regex.Match(match.Value, @"x(\d+|\d+-\d+|\? \(Random count\))$");
                if (countMatch.Success)
                {
                    rt.Select(match.Index + countMatch.Index, countMatch.Length);
                    rt.SelectionColor = Color.FromArgb(210, 240, 255);
                    rt.SelectionBackColor = Color.FromArgb(35, 70, 105);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }

                // Сбрасываем выделение
                rt.Select(match.Index + match.Length, 0);
            }

            // Ищем информацию о загрузке из таблиц [Загрузка из таблиц для BX=X]:
            var tableHeaderMatches = Regex.Matches(noteText, @"\[Загрузка из таблиц для BX=\d+\]:");
            foreach (Match match in tableHeaderMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.Gray;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
            }

            // Ищем строки с адресами таблиц (• CDA9+ → 3C58: 0xXX из [XXXX])
            var tableAddrMatches = Regex.Matches(noteText, @"    • (CDA9\+|CDB1\+) → 3C[0-9A-F]{2}: 0x[0-9A-F]{2} from \[[0-9A-F]{4}\]");
            foreach (Match match in tableAddrMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.DarkGray;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Regular);

                // Выделяем адрес жирным
                int addrIndex = match.Value.LastIndexOf('[');
                if (addrIndex >= 0)
                {
                    rt.Select(match.Index + addrIndex, match.Length - addrIndex);
                    rt.SelectionColor = Color.Gray;
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                }
            }

            // Ищем информацию о неполной загрузке
            var incompleteMatches = Regex.Matches(noteText, @"Неполная загрузка из таблиц \(BX=\d+\):");
            foreach (Match match in incompleteMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 140, 0); // Тёмно-оранжевый
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            // Ищем строки с описанием таблиц в неполной загрузке
            var tableDescMatches = Regex.Matches(noteText, @"  • Загружено из (CDA9\+|CDB1\+) → сохранено в 3C[0-9A-F]{2}\+");
            foreach (Match match in tableDescMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(200, 200, 100); // Желтоватый
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
            }

            // Ищем строки с конкретными значениями регистров
            var regValueMatches = Regex.Matches(noteText, @"    (AL|CL|DL|BL|AH|CH|DH|BH) = 0x[0-9A-F]{2} из \[[0-9A-F]{4}\] \([^\)]+\)");
            foreach (Match match in regValueMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.LightGreen;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Regular);

                // Выделяем значение жирным
                int equalIndex = match.Value.IndexOf('=');
                if (equalIndex >= 0)
                {
                    int valueStart = match.Index + equalIndex + 1;
                    int valueEnd = match.Value.IndexOf(' ', equalIndex);
                    if (valueEnd > 0)
                    {
                        rt.Select(valueStart, valueEnd - equalIndex - 1);
                        rt.SelectionColor = Color.White;
                        rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                    }
                }
            }

            // Сбрасываем выделение
            rt.Select(0, 0);
        }

        private void FormatServiceWarnings(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText)) return;

            var serviceWarningMatches = Regex.Matches(
                noteText,
                @"⚠Вызывается random encounter ⚠|! (?:HP|SP) (?:мужчин в партии|каждого мужчины в партии|каждого персонажа партии) уменьшается вдвое !|! У каждого персонажа партии отнимается \d+ (?:HP|SP) !|! (?:HP|SP) (?:каждого мужчины в партии|каждого персонажа партии) обнуляется !",
                RegexOptions.IgnoreCase);

            foreach (Match match in serviceWarningMatches)
            {
                rt.Select(match.Index, match.Length);
                bool isWholePartySpZeroing = string.Equals(
                    match.Value,
                    "! SP каждого персонажа партии обнуляется !",
                    StringComparison.OrdinalIgnoreCase);

                rt.SelectionColor = isWholePartySpZeroing
                    ? Color.FromArgb(120, 210, 255)
                    : Color.FromArgb(255, 80, 80);
                rt.SelectionBackColor = isWholePartySpZeroing
                    ? Color.FromArgb(120, 0, 0)
                    : Color.FromArgb(70, 0, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            var conditionStatusMatches = Regex.Matches(
                noteText,
                @"CONDITION [^\r\n]+? изменяется на (?<statuses>[^\r\n]+)",
                RegexOptions.IgnoreCase);

            foreach (Match match in conditionStatusMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 170, 130);
                rt.SelectionBackColor = Color.FromArgb(70, 0, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group statusesGroup = match.Groups["statuses"];
                if (!statusesGroup.Success || statusesGroup.Length == 0)
                    continue;

                var statusWordMatches = Regex.Matches(
                    statusesGroup.Value,
                    @"GOOD|DISEASED|POISONED|PARALYZED|UNCONSCIOUS|DEAD|ERADICATED",
                    RegexOptions.IgnoreCase);

                foreach (Match statusMatch in statusWordMatches)
                {
                    rt.Select(statusesGroup.Index + statusMatch.Index, statusMatch.Length);
                    rt.SelectionColor = Color.FromArgb(255, 245, 180);
                    rt.SelectionBackColor = Color.FromArgb(100, 20, 20);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Underline);
                }
            }

            var conditionCheckMatches = Regex.Matches(
                noteText,
                @"ПРОВЕРКА УСЛОВИЯ:\s*(?:Проверяется, совпадают ли [^\r\n]+|Сравнивается [^\r\n]+? с [^\r\n]+)",
                RegexOptions.IgnoreCase);

            foreach (Match match in conditionCheckMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(145, 145, 145);
                rt.SelectionBackColor = Color.FromArgb(60, 45, 20);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);

                Match predicateMatch = Regex.Match(match.Value, @"^ПРОВЕРКА УСЛОВИЯ:", RegexOptions.IgnoreCase);
                if (predicateMatch.Success)
                {
                    rt.Select(match.Index + predicateMatch.Index, predicateMatch.Length);
                    rt.SelectionColor = Color.FromArgb(145, 145, 145);
                    rt.SelectionBackColor = Color.FromArgb(85, 60, 20);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Italic | FontStyle.Underline);
                }
            }

            var teleportMatches = Regex.Matches(
                noteText,
                @"Телепорт на (?:(случайную)\s+)?клетку \(X=(?:\??\d+|\d+\.\.\d+), Y=(?:\??\d+|\d+\.\.\d+)\)",
                RegexOptions.IgnoreCase);

            foreach (Match match in teleportMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(120, 210, 255);
                rt.SelectionBackColor = Color.FromArgb(0, 35, 70);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic);

                if (match.Groups.Count > 1 && match.Groups[1].Success)
                {
                    rt.Select(match.Groups[1].Index, match.Groups[1].Length);
                    rt.SelectionColor = Color.FromArgb(120, 210, 255);
                    rt.SelectionBackColor = Color.FromArgb(65, 35, 0);
                    rt.SelectionFont = new Font(rt.Font, FontStyle.Bold | FontStyle.Italic | FontStyle.Underline);
                }
            }

            var lootWarningMatches = Regex.Matches(
                noteText,
                @"!!! (Контейнер с лутом уничтожен|Предмет уничтожен|GOLD уничтожено|GEMS уничтожены) !!!",
                RegexOptions.IgnoreCase);

            foreach (Match match in lootWarningMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(255, 80, 80);
                rt.SelectionBackColor = Color.FromArgb(70, 0, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Italic);
            }

            var spoilerPasswordMatches = Regex.Matches(
                noteText,
                @"!!! ВНИМАНИЕ СПОЙЛЕР !!! ТРЕБУЕМЫЙ ПАРОЛЬ:\s*(?<password>[^\r\n]+)",
                RegexOptions.IgnoreCase);

            foreach (Match match in spoilerPasswordMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(170, 170, 170);
                rt.SelectionBackColor = Color.FromArgb(150, 0, 0);
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);

                Group passwordGroup = match.Groups["password"];
                if (!passwordGroup.Success || passwordGroup.Length == 0)
                    continue;

                rt.Select(passwordGroup.Index, passwordGroup.Length);
                rt.SelectionColor = Color.Black;
                rt.SelectionBackColor = Color.Black;
                rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
            }

            rt.Select(0, 0);
        }

        private void FormatTemporaryTechnicalNotes(RichTextBox rt, string noteText)
        {
            if (string.IsNullOrEmpty(noteText)) return;

            Color technicalBackColor = Color.FromArgb(46, 28, 64);
            Color questNumberColor = Color.FromArgb(225, 214, 255);
            Color lordNameColor = Color.FromArgb(255, 196, 255);

            var technicalNoteMatches = Regex.Matches(
                noteText,
                @"-=\*[^\r\n]*\*=-",
                RegexOptions.IgnoreCase);

            foreach (Match match in technicalNoteMatches)
            {
                rt.Select(match.Index, match.Length);
                rt.SelectionColor = Color.FromArgb(145, 145, 145);
                rt.SelectionBackColor = technicalBackColor;
                rt.SelectionFont = new Font("Segoe UI Light", rt.Font.Size, FontStyle.Italic);

                Match questNumberMatch = Regex.Match(match.Value, @"\bквест(?:ы)?\s+([0-9,\s]+)", RegexOptions.IgnoreCase);
                if (questNumberMatch.Success && questNumberMatch.Groups.Count > 1)
                {
                    Group questNumberGroup = questNumberMatch.Groups[1];
                    rt.Select(match.Index + questNumberGroup.Index, questNumberGroup.Length);
                    rt.SelectionColor = questNumberColor;
                    rt.SelectionBackColor = technicalBackColor;
                    rt.SelectionFont = new Font("Segoe UI", rt.Font.Size, FontStyle.Bold | FontStyle.Italic);
                }

                foreach (Match lordMatch in Regex.Matches(match.Value, @"\bЛорда\d+\b", RegexOptions.IgnoreCase))
                {
                    rt.Select(match.Index + lordMatch.Index, lordMatch.Length);
                    rt.SelectionColor = lordNameColor;
                    rt.SelectionBackColor = technicalBackColor;
                    rt.SelectionFont = new Font("Segoe UI", rt.Font.Size, FontStyle.Bold | FontStyle.Italic);
                }
            }

            rt.Select(0, 0);
        }

        // Новый метод для обработки текстовых записей
        private string ProcessTextEntry(string textEntry)
        {
            if (string.IsNullOrEmpty(textEntry))
                return "";

            // Формат из OvrFileAnalyzer: "Text at 0xXXXX: "encoded_text""
            // Нужно: удалить "Text at 0xXXXX: " и декодировать

            // Шаг 1: Находим двоеточие
            int colonIndex = textEntry.IndexOf(':');
            if (colonIndex < 0)
                return textEntry; // Возвращаем как есть

            // Шаг 2: Извлекаем текст после двоеточия
            string textAfterColon = textEntry.Substring(colonIndex + 1).Trim();

            // Шаг 3: Убираем кавычки если они есть
            if (textAfterColon.Length >= 2 &&
                textAfterColon.StartsWith("\"") &&
                textAfterColon.EndsWith("\""))
            {
                textAfterColon = textAfterColon.Substring(1, textAfterColon.Length - 2);
            }

            // Шаг 4: Декодируем escape-последовательности
            return DecodeTextString(textAfterColon);
        }

        // Метод для декодирования текста с escape-последовательностями
        private string DecodeTextString(string encodedText)
        {
            if (string.IsNullOrEmpty(encodedText))
                return "";

            // Простая декодировка escape-последовательностей
            return encodedText
                .Replace("\\r", "\r")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        // Вспомогательный метод для добавления текстов пути
        private void AddTextsForPath(Point pos, HashSet<string> texts, string prefix)
        {
            if (texts == null || texts.Count == 0)
                return;

            var sortedTexts = texts.OrderBy(t => t).ToList();
            foreach (var text in sortedTexts)
            {
                string cleanText = ExtractCleanTextFromEntry(text);
                if (!string.IsNullOrEmpty(cleanText))
                {
                    string decodedText = DecodeTextString(cleanText);
                    if (!string.IsNullOrEmpty(decodedText))
                    {
                        notesPerCell[pos] += prefix + decodedText + "\n";
                    }
                }
            }
        }

        // Метод для группировки путей с одинаковыми наборами текстов
        private Dictionary<int, HashSet<string>> GroupSimilarPaths(Dictionary<int, HashSet<string>> pathTexts)
        {
            var groupedPaths = new Dictionary<int, HashSet<string>>();
            var processedSets = new List<HashSet<string>>();

            // Сортируем пути по номеру для детерминированного результата
            var sortedPaths = pathTexts.OrderBy(kvp => kvp.Key).ToList();

            foreach (var kvp in sortedPaths)
            {
                bool foundGroup = false;

                // Ищем группу с таким же набором текстов
                for (int i = 0; i < processedSets.Count; i++)
                {
                    if (AreTextSetsEqual(processedSets[i], kvp.Value))
                    {
                        // Нашли существующую группу - используем минимальный номер пути из этой группы
                        var existingGroup = groupedPaths.First(g => AreTextSetsEqual(g.Value, kvp.Value));

                        // Если текущий путь имеет меньший номер, чем существующий в группе,
                        // то мы должны были уже обработать его раньше (из-за сортировки),
                        // поэтому просто пропускаем этот путь (он уже в группе)
                        foundGroup = true;
                        break;
                    }
                }

                if (!foundGroup)
                {
                    // Создаем новую группу с этим уникальным набором текстов
                    groupedPaths[kvp.Key] = kvp.Value;
                    processedSets.Add(kvp.Value);
                }
            }

            return groupedPaths;
        }

        // Метод для сравнения двух наборов текстов
        private bool AreTextSetsEqual(HashSet<string> set1, HashSet<string> set2)
        {
            if (set1 == null || set2 == null) return false;
            if (set1.Count != set2.Count) return false;

            // Сортируем и сравниваем как строки
            var sorted1 = set1.OrderBy(t => t).ToList();
            var sorted2 = set2.OrderBy(t => t).ToList();

            for (int i = 0; i < sorted1.Count; i++)
            {
                if (sorted1[i] != sorted2[i])
                    return false;
            }

            return true;
        }

        // Метод для извлечения чистого текста из записи
        private string ExtractCleanTextFromEntry(string textEntry)
        {
            if (string.IsNullOrEmpty(textEntry))
                return "";

            // Формат: "Text at 0xXXXX: "text""
            // Ищем двоеточие и текст после него
            int colonIndex = textEntry.IndexOf(':');
            if (colonIndex >= 0 && colonIndex + 1 < textEntry.Length)
            {
                string textPart = textEntry.Substring(colonIndex + 1).Trim();

                // Убираем кавычки если есть
                if (textPart.StartsWith("\"") && textPart.EndsWith("\""))
                {
                    textPart = textPart.Substring(1, textPart.Length - 2);
                }

                return textPart;
            }

            // Если формат другой, возвращаем как есть
            return textEntry;
        }

        private List<string> ExtractMessagesFromBytes(List<byte> dataBytes, int startIndex)
        {
            List<string> messages = new List<string>();
            List<byte> currentMessageBytes = new List<byte>();

            for (int i = startIndex; i < dataBytes.Count; i++)
            {
                byte b = dataBytes[i];

                // Разделитель сообщений 0x00
                if (b == 0x00)
                {
                    if (currentMessageBytes.Count > 0)
                    {
                        try
                        {
                            // Преобразуем байты в hex строку
                            StringBuilder hexBuilder = new StringBuilder();
                            foreach (byte msgByte in currentMessageBytes)
                            {
                                if (hexBuilder.Length > 0) hexBuilder.Append(" ");
                                hexBuilder.AppendFormat("{0:X2}", msgByte);
                            }

                            string messageText = HexToAscii(hexBuilder.ToString());
                            messages.Add(messageText);
                            Debug.WriteLine($"Сообщение {messages.Count}: {messageText}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка преобразования сообщения: {ex.Message}");
                            messages.Add($"[Ошибка преобразования]");
                        }
                        currentMessageBytes.Clear();
                    }
                }
                else
                {
                    currentMessageBytes.Add(b);
                }
            }

            // Добавляем последнее сообщение, если есть
            if (currentMessageBytes.Count > 0)
            {
                try
                {
                    StringBuilder hexBuilder = new StringBuilder();
                    foreach (byte msgByte in currentMessageBytes)
                    {
                        if (hexBuilder.Length > 0) hexBuilder.Append(" ");
                        hexBuilder.AppendFormat("{0:X2}", msgByte);
                    }

                    string messageText = HexToAscii(hexBuilder.ToString());
                    messages.Add(messageText);
                }
                catch { }
            }

            return messages;
        }

        private void ProcessObjectsWithTexts(List<byte> coordinates, List<byte> directions, List<string> messages)
        {
            int messageIndex = 0;
            int processedObjects = 0;
            int objectsWithMessages = 0;

            for (int i = 0; i < coordinates.Count; i++)
            {
                byte coordByte = coordinates[i];
                int coordY = (coordByte >> 4) & 0xF;
                int coordX = coordByte & 0xF;

                // Проверяем, что координаты в допустимом диапазоне (0-15)
                if (coordX >= 0 && coordX < 16 && coordY >= 0 && coordY < 16)
                {
                    Point pos = new Point(coordX, coordY);

                    // Заменяем центральный объект на "AnyObject"
                    centralOptions[pos] = "AnyObject";
                    processedObjects++;

                    Debug.WriteLine($"Объект {i}: X={coordX}, Y={coordY}");

                    // Обрабатываем направления, если есть
                    if (i < directions.Count)
                    {
                        byte dirByte = directions[i];
                        bool hasAnyMessage = false;

                        Debug.WriteLine($"  Направления: 0x{dirByte:X2} (бинар: {Convert.ToString(dirByte, 2).PadLeft(8, '0')})");

                        // Проверяем каждое направление (Top, Left, Bottom, Right)
                        for (int k = 0; k < 4; k++)
                        {
                            int mask = 0x3 << (k * 2);
                            bool hasMessage = (dirByte & mask) == mask;

                            if (hasMessage)
                            {
                                hasAnyMessage = true;
                                Direction directionFlag = k switch
                                {
                                    0 => Direction.Bottom,
                                    1 => Direction.Left,
                                    2 => Direction.Right,
                                    3 => Direction.Top,
                                    _ => throw new Exception("Неправильный индекс направления.")
                                };

                                Debug.WriteLine($"    Сообщение в направлении: {directionFlag}");

                                // Устанавливаем флаг сообщения для направления
                                var currentMessages = messageStates.TryGetValue(pos, out var prev)
                                    ? prev
                                    : new SideValues<bool>(false, false, false, false);

                                switch (directionFlag)
                                {
                                    case Direction.Top:
                                        currentMessages.Top = true;
                                        break;
                                    case Direction.Left:
                                        currentMessages.Left = true;
                                        break;
                                    case Direction.Bottom:
                                        currentMessages.Bottom = true;
                                        break;
                                    case Direction.Right:
                                        currentMessages.Right = true;
                                        break;
                                }

                                messageStates[pos] = currentMessages;
                            }
                        }

                        // Добавляем текст сообщения, если объект имеет сообщение
                        if (hasAnyMessage && messageIndex < messages.Count)
                        {
                            if (!notesPerCell.ContainsKey(pos))
                                notesPerCell[pos] = "";

                            notesPerCell[pos] += messages[messageIndex] + "\n";
                            objectsWithMessages++;
                            Debug.WriteLine($"    Добавлено сообщение: {messages[messageIndex]}");
                            messageIndex++;
                        }
                    }
                }
                else
                {
                    Debug.WriteLine($"Некорректные координаты объекта {i}: X={coordX}, Y={coordY}");
                }
            }

            Debug.WriteLine($"Итоги обработки:");
            Debug.WriteLine($"  Обработано объектов: {processedObjects}");
            Debug.WriteLine($"  Объектов с сообщениями: {objectsWithMessages}");
            Debug.WriteLine($"  Использовано сообщений: {messageIndex} из {messages.Count}");
        }

        // Преобразует шестнадцатеричную строку в ASCII-текст
        private string HexToAscii(string hex)
        {
            // Нормализуем строку, удаляя лишние пробелы и ненужные символы
            hex = Regex.Replace(hex, @"\s+", "");

            // Проверьте, что длина нормализованной строки делится на 2
            if (hex.Length % 2 != 0)
            {
                throw new FormatException("Длина строки должна быть чётной!");
            }

            // Конвертировать строку в массив байтов
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            // Преобразуем байты в ASCII-текст
            return Encoding.ASCII.GetString(bytes);
        }

        //Временный костыль до тех пор пока я не разберусь в правильной логике; 
        public static void TransformMessages(List<string> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                // Проверяем конец строки на символ '0D'
                if (messages[i].EndsWith("0D"))
                {
                    // Сохраняем текст удаляемого элемента
                    string removedText = messages[i];

                    // Удаляем элемент из списка
                    messages.RemoveAt(i);

                    // Обновляем последующие элементы
                    for (int j = 0; j < 3 && i + j < messages.Count; j++)
                    {
                        messages[i + j] = removedText + messages[i + j];
                    }
                }
            }
        }

        private void DefineCellObjectWithDirectionAndText(string[] lines)
        {
            if (lines.Length > 34 && !string.IsNullOrWhiteSpace(lines[32]) && !string.IsNullOrWhiteSpace(lines[33]))
            {
                try
                {
                    // Строка 32 (индекс 32) - координаты
                    string coordinatesLine = lines[32].Trim();
                    string[] coordinateElements = coordinatesLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    Debug.WriteLine($"Элементов координат: {coordinateElements.Length}");
                    if (coordinateElements.Length > 0)
                    {
                        Debug.WriteLine($"Первый элемент координат: {coordinateElements[0]}");
                    }

                    // Строка 33 (индекс 33) - направления
                    string directionsLine = lines[33].Trim();
                    string[] directionElements = directionsLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    Debug.WriteLine($"Элементов направлений: {directionElements.Length}");

                    // Строка 34 (индекс 34) - сообщения (может быть пустой)
                    string messagesLine = lines.Length > 34 ? lines[34].Trim() : "";
                    string[] messages = Array.Empty<string>();

                    if (!string.IsNullOrWhiteSpace(messagesLine))
                    {
                        // Разделяем по "00", но нужно быть осторожным с пробелами
                        messages = messagesLine.Split(new[] { "00" }, StringSplitOptions.RemoveEmptyEntries);

                        // Преобразуем каждое сообщение из HEX в ASCII
                        for (int i = 0; i < messages.Length; i++)
                        {
                            try
                            {
                                messages[i] = HexToAscii(messages[i].Trim());
                                Debug.WriteLine($"Сообщение {i}: {messages[i]}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка преобразования сообщения {i}: {ex.Message}");
                                messages[i] = $"[Ошибка: {messages[i].Trim()}]";
                            }
                        }
                    }

                    if (coordinateElements.Length == 0 || directionElements.Length == 0)
                    {
                        Debug.WriteLine("Недостаточно данных в строках координат или направлений");
                        return;
                    }

                    // Первая цифра в первой строке — это общее количество пар
                    if (!int.TryParse(coordinateElements[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int totalPairs))
                    {
                        Debug.WriteLine($"Не удалось распарсить количество пар: '{coordinateElements[0]}'");
                        return;
                    }

                    Debug.WriteLine($"Всего пар: {totalPairs}");
                    Debug.WriteLine($"Координат: {coordinateElements.Length - 1} (должно быть {totalPairs * 2})");
                    Debug.WriteLine($"Направлений: {directionElements.Length} (должно быть {totalPairs})");

                    // Обрабатываем координаты и выставляем AnyObject
                    int maxCoordIndex = Math.Min(coordinateElements.Length - 1, totalPairs * 2);
                    for (int i = 1; i <= maxCoordIndex; i++)
                    {
                        try
                        {
                            int hexCoord = Convert.ToInt32(coordinateElements[i], 16);
                            int coordY = (hexCoord >> 4) & 0xF; // Старшие 4 бита - Y
                            int coordX = hexCoord & 0xF;        // Младшие 4 бита - X

                            Debug.WriteLine($"Координата {i}: 0x{hexCoord:X2} -> X={coordX}, Y={coordY}");

                            Point newPos = new Point(coordX, coordY);
                            centralOptions[newPos] = "AnyObject";
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка обработки координаты {i}: {ex.Message}");
                        }
                    }

                    // Обрабатываем направления и сообщения
                    int messageIndex = 0;
                    int maxDirIndex = Math.Min(directionElements.Length, totalPairs);

                    for (int i = 0; i < maxDirIndex; i++)
                    {
                        try
                        {
                            int hexDir = Convert.ToInt32(directionElements[i], 16);
                            Debug.WriteLine($"Направление {i}: 0x{hexDir:X2} (бинар: {Convert.ToString(hexDir, 2).PadLeft(8, '0')})");

                            // Проверяем каждое направление (Top, Left, Bottom, Right)
                            for (int k = 0; k < 4; k++)
                            {
                                int mask = 0x3 << (k * 2);
                                bool hasMessage = (hexDir & mask) == mask;

                                if (hasMessage)
                                {
                                    Direction directionFlag = k switch
                                    {
                                        0 => Direction.Bottom,
                                        1 => Direction.Left,
                                        2 => Direction.Right,
                                        3 => Direction.Top,
                                        _ => throw new Exception("Неправильный индекс направления.")
                                    };

                                    Debug.WriteLine($"  Направление {directionFlag} имеет сообщение (битовая маска {k})");

                                    // Получаем соответствующие координаты
                                    int coordIndex = i + 1; // +1 потому что 0-й элемент - количество пар
                                    if (coordIndex < coordinateElements.Length)
                                    {
                                        int hexCoord = Convert.ToInt32(coordinateElements[coordIndex], 16);
                                        int coordY = (hexCoord >> 4) & 0xF;
                                        int coordX = hexCoord & 0xF;
                                        Point newPos = new Point(coordX, coordY);

                                        Debug.WriteLine($"  Ячейка: X={coordX}, Y={coordY}");

                                        // Устанавливаем флаг сообщения для направления
                                        var currentMessages = messageStates.TryGetValue(newPos, out var prev)
                                            ? prev
                                            : new SideValues<bool>(false, false, false, false);

                                        switch (directionFlag)
                                        {
                                            case Direction.Top:
                                                currentMessages.Top = true;
                                                break;
                                            case Direction.Left:
                                                currentMessages.Left = true;
                                                break;
                                            case Direction.Bottom:
                                                currentMessages.Bottom = true;
                                                break;
                                            case Direction.Right:
                                                currentMessages.Right = true;
                                                break;
                                        }

                                        messageStates[newPos] = currentMessages;

                                        // Добавляем текст сообщения, если есть
                                        if (messageIndex < messages.Length)
                                        {
                                            if (!notesPerCell.ContainsKey(newPos))
                                                notesPerCell[newPos] = "";

                                            string messageToAdd = messages[messageIndex];
                                            notesPerCell[newPos] += messageToAdd + "\n";
                                            Debug.WriteLine($"  Добавлено сообщение: {messageToAdd}");
                                            messageIndex++;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка обработки направления {i}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Общая ошибка в DefineCellObjectWithDirectionAndText: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"Недостаточно строк или пустые данные. lines.Length={lines.Length}, lines[32]={(lines.Length > 32 ? "не пусто" : "нет")}, lines[33]={(lines.Length > 33 ? "не пусто" : "нет")}");
            }
        }

        public sealed class SideValues<T> : IEquatable<SideValues<T>>
        {
            [JsonProperty("Item1")]
            public T Top { get; set; }

            [JsonProperty("Item2")]
            public T Bottom { get; set; }

            [JsonProperty("Item3")]
            public T Left { get; set; }

            [JsonProperty("Item4")]
            public T Right { get; set; }

            public SideValues()
            {
            }

            public SideValues(T top, T bottom, T left, T right)
            {
                Top = top;
                Bottom = bottom;
                Left = left;
                Right = right;
            }

            public T Get(Direction side)
            {
                return side switch
                {
                    Direction.Top => Top,
                    Direction.Bottom => Bottom,
                    Direction.Left => Left,
                    Direction.Right => Right,
                    _ => throw new ArgumentOutOfRangeException(nameof(side))
                };
            }

            public void Set(Direction side, T value)
            {
                switch (side)
                {
                    case Direction.Top:
                        Top = value;
                        break;
                    case Direction.Bottom:
                        Bottom = value;
                        break;
                    case Direction.Left:
                        Left = value;
                        break;
                    case Direction.Right:
                        Right = value;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(side));
                }
            }

            public SideValues<T> Clone()
            {
                return new SideValues<T>(Top, Bottom, Left, Right);
            }

            public bool Equals(SideValues<T> other)
            {
                if (ReferenceEquals(other, null))
                    return false;

                return EqualityComparer<T>.Default.Equals(Top, other.Top)
                    && EqualityComparer<T>.Default.Equals(Bottom, other.Bottom)
                    && EqualityComparer<T>.Default.Equals(Left, other.Left)
                    && EqualityComparer<T>.Default.Equals(Right, other.Right);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as SideValues<T>);
            }

            public override int GetHashCode()
            {
                int hash = 17;
                hash = hash * 31 + (Top == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Top));
                hash = hash * 31 + (Bottom == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Bottom));
                hash = hash * 31 + (Left == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Left));
                hash = hash * 31 + (Right == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Right));
                return hash;
            }
        }

        private sealed class DirectionBits
        {
            public bool HasWallBit { get; init; }
            public bool HasDoorBit { get; init; }
            public bool SecondLowBit { get; init; }
            public bool SecondHighBit { get; init; }
        }

        private sealed class DirectionState
        {
            public string BorderType { get; init; } = "Пустота";
            public int PassageType { get; init; }
            public bool IsClosed { get; init; }
        }

        private sealed class CellDraftContext
        {
            public bool IsDungeon { get; init; }
            public string WallType { get; init; } = "Кирпичная стена";
        }

        private static readonly (Direction Side, int HighBit, int LowBit)[] DraftSideMappings =
        {
            (Direction.Left, 2, 1),
            (Direction.Bottom, 4, 3),
            (Direction.Right, 6, 5),
            (Direction.Top, 8, 7)
        };

        private static bool IsBitSet(int value, int bitFromRight)
        {
            int zeroBased = bitFromRight - 1;
            return (value & (1 << zeroBased)) != 0;
        }

        private CellDraftContext BuildDraftContext(OvrFileConfig config)
        {
            bool isDungeon = string.Equals(config?.OverlayType, "dungeon", StringComparison.OrdinalIgnoreCase);

            return new CellDraftContext
            {
                IsDungeon = isDungeon,
                WallType = isDungeon ? "Каменная стена" : "Кирпичная стена"
            };
        }

        private DirectionBits ReadDirectionBits(int firstLayerValue, int secondLayerValue, int highBitFromRight, int lowBitFromRight)
        {
            return new DirectionBits
            {
                HasDoorBit = IsBitSet(firstLayerValue, highBitFromRight),
                HasWallBit = IsBitSet(firstLayerValue, lowBitFromRight),
                SecondHighBit = IsBitSet(secondLayerValue, highBitFromRight),
                SecondLowBit = IsBitSet(secondLayerValue, lowBitFromRight)
            };
        }

        private DirectionState ResolveDirectionState(DirectionBits bits, CellDraftContext context)
        {
            bool anyStructure = bits.HasWallBit || bits.HasDoorBit;

            if (!anyStructure)
            {
                return new DirectionState
                {
                    BorderType = bits.SecondLowBit ? "Барьер" : "Пустота"
                };
            }

            return context.IsDungeon
                ? ResolveDungeonDirectionState(bits, context.WallType)
                : ResolveTownDirectionState(bits, context.WallType);
        }

        private DirectionState ResolveTownDirectionState(DirectionBits bits, string wallType)
        {
            int passageType = 0;
            bool isClosed = false;

            if (bits.HasWallBit && !bits.HasDoorBit)
            {
                if (!bits.SecondLowBit)
                    passageType = 3;
            }
            else if (!bits.HasWallBit && bits.HasDoorBit)
            {
                passageType = 1;
                isClosed = bits.SecondLowBit;
            }
            else if (bits.HasWallBit && bits.HasDoorBit)
            {
                if (!bits.SecondLowBit)
                    passageType = 3;
            }

            return new DirectionState
            {
                BorderType = wallType,
                PassageType = passageType,
                IsClosed = isClosed
            };
        }

        private DirectionState ResolveDungeonDirectionState(DirectionBits bits, string wallType)
        {
            int passageType = 0;
            bool isClosed = false;

            if (bits.HasWallBit && !bits.HasDoorBit)
            {
                if (!bits.SecondLowBit)
                    passageType = 3;
            }
            else if (!bits.HasWallBit && bits.HasDoorBit)
            {
                passageType = 1;
                isClosed = bits.SecondLowBit;
            }
            else if (bits.HasWallBit && bits.HasDoorBit)
            {
                passageType = 2;
                isClosed = bits.SecondLowBit;
            }

            return new DirectionState
            {
                BorderType = wallType,
                PassageType = passageType,
                IsClosed = isClosed
            };
        }

        private void ApplyDirectionState(
            Direction side,
            DirectionState state,
            SideValues<string> currentBorders,
            SideValues<int> currentPassages,
            SideValues<bool> currentClosedStates)
        {
            currentBorders.Set(side, state.BorderType);

            if (state.PassageType != 0)
                currentPassages.Set(side, state.PassageType);

            if (state.IsClosed)
                currentClosedStates.Set(side, true);
        }

        private void ApplyCellWideDraftFlags(Point pos, int secondLayerValue)
        {
            isDangerStates[pos] = IsBitSet(secondLayerValue, 4);
            noMagicStates[pos] = IsBitSet(secondLayerValue, 2);
            darkeningLevels[pos] = IsBitSet(secondLayerValue, 6)
                ? Lighting.Dark
                : Lighting.Light;
            centralOptions[pos] = IsBitSet(secondLayerValue, 8)
                ? "Случайная встреча"
                : "Пустота";
        }

        private void ResetDraftTransientState(Point pos)
        {
            messageStates[pos] = new SideValues<bool>(false, false, false, false);
            notesPerCell[pos] = "";
            imagesPerCell[pos] = null;
        }

        private void ProcessCellDraft(int x, int y, string hexValueFirstLayer, string hexValueSecondLayer, OvrFileConfig config)
        {
            int firstLayerValue = Convert.ToInt32(hexValueFirstLayer, 16);
            int secondLayerValue = Convert.ToInt32(hexValueSecondLayer, 16);

            Point pos = new Point(x, y);
            CellDraftContext context = BuildDraftContext(config);

            var currentBorders = borders[pos];
            var currentPassages = passageDict[pos];
            var currentClosedStates = closedStates[pos];

            foreach (var mapping in DraftSideMappings)
            {
                DirectionBits bits = ReadDirectionBits(
                    firstLayerValue,
                    secondLayerValue,
                    mapping.HighBit,
                    mapping.LowBit);

                DirectionState state = ResolveDirectionState(bits, context);

                ApplyDirectionState(
                    mapping.Side,
                    state,
                    currentBorders,
                    currentPassages,
                    currentClosedStates);
            }

            borders[pos] = currentBorders;
            passageDict[pos] = currentPassages;
            closedStates[pos] = currentClosedStates;

            ApplyCellWideDraftFlags(pos, secondLayerValue);
            ResetDraftTransientState(pos);
        }

        // Обработчик события пункта меню "Метаданные"
        private void MetadataItem_Click(object sender, EventArgs e)
        {
            MetadataForm metadataForm = new MetadataForm(mapSector, surface);
            if (metadataForm.ShowDialog() == DialogResult.OK)
            {
                mapSector = metadataForm.SelectedMapSector;
                surface = metadataForm.SelectedSurface;

                UpdateWindowTitle();
            }
        }

        private void UpdateWindowTitle()
        {
            // Обновляем заголовок окна
            if (string.IsNullOrEmpty(mapSector) && string.IsNullOrEmpty(surface))
            {
                this.Text = Path.GetFileNameWithoutExtension(lastSavedFilename);
            }
            else if (string.IsNullOrEmpty(mapSector))
            {
                this.Text = $"{Path.GetFileNameWithoutExtension(lastSavedFilename)} - SURFACE: {surface}";
            }
            else if (string.IsNullOrEmpty(surface))
            {
                this.Text = $"{Path.GetFileNameWithoutExtension(lastSavedFilename)} - MAP SECTOR: {mapSector}";
            }
            else
            {
                this.Text = $"{Path.GetFileNameWithoutExtension(lastSavedFilename)} - MAP SECTOR: {mapSector}    SURFACE: {surface}";
            }
        }

        private void toolStripMenuItemManageObjects_Click(object sender, EventArgs e)
        {
            var form2 = new ObjectManagementForm(this, ActiveConfigObjectFile);
            form2.ShowDialog();
        }

        // Обработчик события для открытия формы настроек
        private void DirectionsMenuItem_Click(object sender, EventArgs e)
        {
            if (localizedDirectionsForm == null || localizedDirectionsForm.IsDisposed)
            {
                localizedDirectionsForm = new LocalizedDirectionsForm();
                localizedDirectionsForm.LocalizationChanged += LocalizedDirectionsForm_LocalizationChanged;
            }
            localizedDirectionsForm.Show();
        }

        private void OvrLoadSettingsMenuItem_Click(object sender, EventArgs e)
        {
            using (var form = new OvrLoadSettingsForm())
            {
                form.ShowDialog();
            }
        }

        // Обработчик события обновления локализации направления
        private void LocalizedDirectionsForm_LocalizationChanged(object sender, EventArgs e)
        {
            // Получаем данные из файла .ini
            var parser = new FileIniDataParser();
            var iniFile = parser.ReadFile("Settings.ini");

            if (iniFile.Sections.ContainsSection("CustomDirections"))
            {
                // Получаем названия направлений из файла .ini
                string topLabel = iniFile["CustomDirections"]["Top"];
                string bottomLabel = iniFile["CustomDirections"]["Bottom"];
                string leftLabel = iniFile["CustomDirections"]["Left"];
                string rightLabel = iniFile["CustomDirections"]["Right"];

                // Находим метки по их русским именам
                Control topLabelCtrl = FindControlByName(this, "lbl_Верх");
                Control bottomLabelCtrl = FindControlByName(this, "lbl_Низ");
                Control leftLabelCtrl = FindControlByName(this, "lbl_Лево");
                Control rightLabelCtrl = FindControlByName(this, "lbl_Право");

                // Обновляем текст меток
                if (topLabelCtrl is Label)
                {
                    topLabelCtrl.Text = topLabel;
                }
                if (bottomLabelCtrl is Label)
                {
                    bottomLabelCtrl.Text = bottomLabel;
                }
                if (leftLabelCtrl is Label)
                {
                    leftLabelCtrl.Text = leftLabel;
                }
                if (rightLabelCtrl is Label)
                {
                    rightLabelCtrl.Text = rightLabel;
                }
            }
        }

        // Метод для поиска контроля по имени
        private Control FindControlByName(Control container, string name)
        {
            foreach (Control child in container.Controls)
            {
                if (child.Name == name)
                {
                    return child;
                }

                // Рекурсивный поиск среди вложенных элементов
                Control foundChild = FindControlByName(child, name);
                if (foundChild != null)
                {
                    return foundChild;
                }
            }

            return null;
        }

        private void DeleteImageButton_Click(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                Image previousImage = imagesPerCell.TryGetValue(pos, out var prev) ? prev : null;

                if (imagesPerCell.TryGetValue(selectedPosition.Value, out var existingImage) && existingImage != null)
                {
                    // Выводим диалоговое окно с вопросом
                    DialogResult result = MessageBox.Show("Вы действительно хотите удалить изображение?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        // Удаляем изображение из текущего положения
                        imagesPerCell[pos] = null;

                        // Обновляем изображение в Preview-контроле
                        cellImageBox.Image = null;

                        // Принудительная перерисовка ячейки
                        gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();

                        // Обновляем предварительный просмотр
                        UpdatePreview();

                        // Метка о внесении изменений в карту
                        isMapModified = true;
                    }
                }
            }
        }

        private void PopulateChangeToMapsSubmenu(ToolStripMenuItem parentMenu)
        {
            // Очищаем существующие пункты
            parentMenu.DropDownItems.Clear();

            // Получаем папку рядом с исполняемым файлом (.exe)
            string mapsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MM1_Maps");

            if (Directory.Exists(mapsDirectory))
            {
                // Берём только файлы с расширением .map
                string[] mapFiles = Directory.GetFiles(mapsDirectory, "*.map");

                foreach (string file in mapFiles)
                {
                    string filename = Path.GetFileNameWithoutExtension(file);

                    // Создаём новый пункт меню для каждой карты
                    ToolStripMenuItem subItem = new ToolStripMenuItem(filename);
                    subItem.Tag = file; // Полный путь сохраняется в Tag
                    subItem.Click += ChangeToMapItem_Click; // Обработчик щелчка по карте

                    // Добавляем пункт в подменю
                    parentMenu.DropDownItems.Add(subItem);
                }
            }
        }

        private void ChangeToMapsDropdown_DropDownOpening(object sender, EventArgs e)
        {
            // Обновляем подменю прямо перед его открытием
            PopulateChangeToMapsSubmenu(changeToMapsDropdown);
        }

        // Обработчик нажатия на пункт меню "Поменять на"
        private void ChangeToMapItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem clickedItem = (ToolStripMenuItem)sender;
            string mapFilePath = (string)clickedItem.Tag;

            // Проверка флага модификации
            if (isMapModified)
            {
                DialogResult result = MessageBox.Show(
                    "Вы действительно хотите сменить карту?\nВсе текущие изменения будут потеряны!",
                    "Смена карты",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.Cancel)
                {
                    return; // Отменяем операцию смены карты
                }
            }

            // Загружаем карту, аналогично команде "Открыть"
            LoadMap(mapFilePath);
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                RestoreSettings(pos); // Восстановление состояния выделенной ячейки
                UpdatePreview();
                isMapModified = false;
            }
        }

        private void SaveItem_Click(object sender, EventArgs e)
        {
            // Проверяем заголовок окна
            if (lastSavedFilename == "")
            {
                // Если заголовок содержит "Редактор моей мечты", вызываем "Сохранить как..."
                SaveAsItem_Click(sender, e);
            }
            else
            {
                // Иначе производим автоматическое сохранение
                if (lastSavedFilename != null)
                {
                    // Сохраняем карту в последний известный путь
                    SaveMap(lastSavedFilename);
                    MessageBox.Show("Карта успешно сохранена", "Сохранение выполнено", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    isMapModified = false;
                    PopulateChangeToMapsSubmenu(changeToMapsDropdown);
                }
                else
                {
                    // Если путь неизвестен, предлагаем пользователю выбрать файл
                    SaveAsItem_Click(sender, e);
                }
            }
        }

        private void LoadItem_Click(object sender, EventArgs e)
        {
            if (isMapModified)
            {
                DialogResult confirmResult = MessageBox.Show(
                    "Вы действительно хотите загрузить новую карту?\nВсе текущие данные будут потеряны!",
                    "Загрузка карты",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning
                );

                if (confirmResult == DialogResult.OK)
                {
                    OpenMapDialog();
                }
            }
            else
            {
                OpenMapDialog();
            }
        }

        private void OpenMapDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                // Директорию для открытия ставим папку рядом с исполняемым файлом
                string mapsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MM1_Maps");

                // Если папки нет, используем директорию исполняемого файла
                if (!Directory.Exists(mapsDirectory))
                {
                    mapsDirectory = AppDomain.CurrentDomain.BaseDirectory;
                }

                dialog.InitialDirectory = mapsDirectory;
                dialog.Filter = "Map Files (*.map)|*.map|All files (*.*)|*.*";
                dialog.Title = "Выбор карты для загрузки";
                dialog.DefaultExt = ".map";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string filename = dialog.FileName;
                    LoadMap(filename);

                    // Если есть выделенная ячейка, обновляем её представление
                    if (selectedPosition.HasValue)
                    {
                        Point pos = selectedPosition.Value;
                        RestoreSettings(pos); // Восстановление состояния выделенной ячейки
                        UpdatePreview();
                        isMapModified = false;
                    }

                }
            }
        }

        private void LoadMap(string filename)
        {
            // Читаем данные из файла
            string jsonContent = File.ReadAllText(filename);

            // Десериализуем JSON
            dynamic data = JsonConvert.DeserializeObject(jsonContent);

            // Получаем список ячеек
            JArray cellsData = data.Cells;

            lastSavedFilename = filename; // Запоминаем путь к файлу

            // Восстанавливаем данные ячеек
            for (int i = 0; i < cellsData.Count; i++)
            {
                dynamic cellInfo = cellsData[i];
                Point pos = new Point(i % GridSize, i / GridSize);

                // Границы: приводим значения к строкам
                borders[pos] = new SideValues<string>(
                    (string)cellInfo.Borders.Item1, (string)cellInfo.Borders.Item2, (string)cellInfo.Borders.Item3, (string)cellInfo.Borders.Item4);

                // Проходы: приводим значения к целочисленным
                passageDict[pos] = new SideValues<int>(
                    (int)cellInfo.Passages.Item1, (int)cellInfo.Passages.Item2, (int)cellInfo.Passages.Item3, (int)cellInfo.Passages.Item4);

                // Закрытость: приводим значения к булевым
                closedStates[pos] = new SideValues<bool>(
                    (bool)cellInfo.ClosedStates.Item1, (bool)cellInfo.ClosedStates.Item2, (bool)cellInfo.ClosedStates.Item3, (bool)cellInfo.ClosedStates.Item4);

                // Сообщения: приводим значения к булевым
                messageStates[pos] = new SideValues<bool>(
                    (bool)cellInfo.Messages.Item1, (bool)cellInfo.Messages.Item2, (bool)cellInfo.Messages.Item3, (bool)cellInfo.Messages.Item4);

                // Центральный элемент
                centralOptions[pos] = (string)cellInfo.CentralOption;

                // Опасность и магия
                isDangerStates[pos] = (bool)cellInfo.IsDanger;
                noMagicStates[pos] = (bool)cellInfo.NoMagic;

                // Затемнённость: поддерживаем и новое поле Darkening, и старое Lighting
                var darkeningToken = cellInfo.Darkening ?? cellInfo.Lighting;
                darkeningLevels[pos] = (Lighting)Enum.Parse(typeof(Lighting), (string)darkeningToken);

                // Примечания
                notesPerCell[pos] = (string)cellInfo.Note;

                // Восстановим изображение (если оно есть)
                if (cellInfo.Image != null)
                {
                    byte[] bytes = Convert.FromBase64String((string)cellInfo.Image);
                    using (MemoryStream ms = new MemoryStream(bytes))
                    {
                        imagesPerCell[pos] = Image.FromStream(ms);
                    }
                }
                else
                    imagesPerCell[pos] = null;
            }

            mapSector = "";
            surface = "";

            // Загружаем метаданные
            if (data.MetaData != null &&
                data.MetaData.MapSector != null &&
                data.MetaData.Surface != null)
            {
                mapSector = (string)data.MetaData.MapSector;
                surface = (string)data.MetaData.Surface;

                UpdateWindowTitle();
            }
            else
            {
                // Если метаданные отсутствуют или неполные, пишем только имя файла(имя карты)
                this.Text = Path.GetFileNameWithoutExtension(filename);
            }


            // Обновляем визуализацию формы
            foreach (var button in gridButtons)
            {
                button.Invalidate();
            }

            // Показываем уведомление о выполнении загрузки
            //  MessageBox.Show("Карта успешно загружена", "Загрузка выполнена", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveAsItem_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                string filename = null;
                string mapsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MM1_Maps");

                Directory.CreateDirectory(mapsDirectory);

                dialog.InitialDirectory = mapsDirectory;
                dialog.Filter = "Map Files (*.map)|*.map|All files (*.*)|*.*";
                dialog.Title = "Сохранить карту как...";
                dialog.DefaultExt = ".map";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    filename = dialog.FileName;
                    SaveMap(filename);

                    lastSavedFilename = filename; // Запоминаем путь к файлу
                    UpdateWindowTitle();

                    // Показываем уведомление о завершении
                    MessageBox.Show("Карта успешно сохранена", "Сохранение выполнено", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    isMapModified = false;
                    PopulateChangeToMapsSubmenu(changeToMapsDropdown);
                }
            }
        }

        private void SaveMap(string filename)
        {
            // Создаем контейнер для данных карты
            var mapData = new Dictionary<string, object>();

            // Состояние каждой ячейки будем собирать в отдельную структуру
            var cellsData = new List<object>();

            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    Point pos = new Point(x, y);
                    var cellInfo = new Dictionary<string, object>()
                    {
                        ["Borders"] = borders[pos],
                        ["Passages"] = passageDict[pos],
                        ["ClosedStates"] = closedStates[pos],
                        ["Messages"] = messageStates[pos],
                        ["CentralOption"] = centralOptions[pos],
                        ["IsDanger"] = isDangerStates[pos],
                        ["NoMagic"] = noMagicStates[pos],
                        ["Darkening"] = darkeningLevels[pos],
                        ["Note"] = notesPerCell[pos],
                        ["Image"] = imagesPerCell[pos]?.ToBase64String() // Сериализуем изображение в Base64
                    };
                    cellsData.Add(cellInfo);
                }
            }

            // Добавляем список ячеек в главный контейнер
            mapData["Cells"] = cellsData;

            // Добавляем MetaData с необходимыми полями
            mapData["MetaData"] = new Dictionary<string, object>()
            {
                ["MapSector"] = mapSector,
                ["Surface"] = surface
            };

            // Сериализуем данные и сохраняем в файл
            string jsonContent = JsonConvert.SerializeObject(mapData, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(filename, jsonContent);

        }

        // Обработчик клика по пункту "Создать новую(карту)"
        private void NewMapItem_Click(object sender, EventArgs e)
        {
            if (isMapModified)
            {
                DialogResult result = MessageBox.Show(
                    "Вы действительно хотите создать новую карту?\nВсе текущие данные будут потеряны!",
                    "Создание новой карты",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.OK)
                {
                    ClearCurrentMapState();
                }
            }
            else
            {
                ClearCurrentMapState();
            }
        }

        private void IsDangerCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                bool previousDangerState = isDangerStates.TryGetValue(pos, out var prev) ? prev : false;

                isDangerStates[pos] = isDangerCheckBox.Checked;

                // Проверка на изменение
                bool hasChanged = previousDangerState != isDangerStates[pos];

                if (hasChanged)
                {

                    gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();

                    UpdatePreview();
                    isMapModified = true;
                }
            }
        }
        private void NoMagicCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                bool previousNoMagicState = noMagicStates.TryGetValue(pos, out var prev) ? prev : false;

                noMagicStates[pos] = noMagicCheckBox.Checked;

                // Проверка на изменение
                bool hasChanged = previousNoMagicState != isDangerStates[pos];

                if (hasChanged)
                {
                    gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();

                    UpdatePreview();

                    isMapModified = true;
                }
            }
        }

        private void DarkeningRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                Lighting previousDarkening = darkeningLevels.TryGetValue(pos, out var prev) ? prev : Lighting.Light;


                RadioButton rb = (RadioButton)sender;
                if (rb.Checked)
                {
                    // Обновляем затемнённость для выбранной ячейки
                    if (rb == lightRadioButton)
                    {
                        ApplyDarkeningEffect(pos, Lighting.Light);
                    }
                    else if (rb == darkRadioButton)
                    {
                        ApplyDarkeningEffect(pos, Lighting.Dark);
                    }
                    else if (rb == darkeningRadioButton)
                    {
                        ApplyDarkeningEffect(pos, Lighting.Darkness);
                    }

                    // Проверка на изменение
                    bool hasChanged = previousDarkening != darkeningLevels[pos];

                    if (hasChanged)
                    {
                        // Обязательно заново перерисуем кнопку
                        gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();

                        UpdatePreview();
                        isMapModified = true;
                    }
                }
            }
        }
        private void ApplyDarkeningEffect(Point pos, Lighting level)
        {
            // Устанавливаем уровень затемнённости для данной позиции
            darkeningLevels[pos] = level;
        }

        private void BufferPasteImageButton_Click(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                Image previousImage = imagesPerCell.TryGetValue(pos, out var prev) ? prev : null;

                if (Clipboard.ContainsImage())
                {
                    Image clipboardImage = Clipboard.GetImage();
                    if (clipboardImage != null)
                    {
                        // Проверяем, есть ли уже изображение у выбранной ячейки
                        if (imagesPerCell.TryGetValue(selectedPosition.Value, out var existingImage) && existingImage != null)
                        {
                            // Выводим диалоговое окно с вопросом
                            DialogResult result = MessageBox.Show("Вы действительно хотите заменить изображение?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (result == DialogResult.Yes)
                            {
                                // Сохраняем новое изображение
                                imagesPerCell[selectedPosition.Value] = clipboardImage;
                                cellImageBox.Image = clipboardImage;
                            }
                        }
                        else
                        {
                            // Если изображения нет, просто сохраняем новое
                            imagesPerCell[selectedPosition.Value] = clipboardImage;
                            cellImageBox.Image = clipboardImage;
                        }
                    }
                    // Проверка на изменение
                    bool hasChanged = previousImage != imagesPerCell[pos];

                    if (hasChanged)
                        isMapModified = true;
                }
                else
                {
                    MessageBox.Show("Буфер обмена пуст или не содержит изображения.");
                }
            }
        }

        // Реализация метода для события Paint
        private void CellImageBox_PaintNoPicture(object sender, PaintEventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                if (imagesPerCell.TryGetValue(selectedPosition.Value, out var image))
                {
                    if (image != null)
                    {
                        e.Graphics.DrawImage(image, 0, 0, cellImageBox.Width, cellImageBox.Height);
                    }
                    else
                    {
                        ShowPlaceholderText(e.Graphics);
                    }
                }
                else
                {
                    ShowPlaceholderText(e.Graphics);
                }
            }
            else
            {
                ShowPlaceholderText(e.Graphics);
            }
        }

        // Метод для вывода большого белого текста по центру
        private void ShowPlaceholderText(Graphics g)
        {
            // Большой белый шрифт
            Font bigFont = new Font("Arial", 16, FontStyle.Bold);
            SolidBrush whiteBrush = new SolidBrush(Color.White);

            // Текст, который хотим показать
            string placeholderText = "Изображение отсутствует";

            // Центрирование текста по ширине и высоте
            StringFormat format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            // Рисуем текст по центру PictureBox
            g.DrawString(placeholderText, bigFont, whiteBrush, new RectangleF(0, 0, cellImageBox.Width, cellImageBox.Height), format);

            // Важно освободить ресурсы шрифтов и кистей
            bigFont.Dispose();
            whiteBrush.Dispose();
        }

        private Tuple<Label, ComboBox> CreatePassageComboBox(string title)
        {
            var label = new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Color.White
            };

            var comboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                BackColor = Color.Black,       // черный фон
                ForeColor = Color.White,       // белый текст
                FlatStyle = FlatStyle.Flat
            };

            comboBox.Items.AddRange(new object[] { "Нет", "Дверь", "Решётка", "Секрет", "Лестница вверх", "Лестница вниз", "Портал", "Выход" });

            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }

            // Важный момент: регистрация обработчика для комбобокса прохода
            comboBox.SelectionChangeCommitted += PassageComboBox_SelectionChanged;

            return new Tuple<Label, ComboBox>(label, comboBox);
        }

        private Tuple<Label, ComboBox> CreateComboBox(string title)
        {
            var label = new Label
            {
                Name = $"lbl_{title}", // Назначение уникального имени метке
                Text = title,
                //AutoSize = true,
                Height = 20,
                ForeColor = Color.White,
                // Anchor = AnchorStyles.Left // Прикрепляем к левому краю и вершине
                //  Location = new Point(10,10)

            };

            // Получаем кастомное название стороны
            string customTitle = GetSetting("CustomDirections", title);
            if (!string.IsNullOrWhiteSpace(customTitle))
            {
                label.Text = customTitle;
            }

            var comboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.Black,       // черный фон
                ForeColor = Color.White,       // белый текст
                FlatStyle = FlatStyle.Flat,
                Width = 200
            };

            comboBox.Items.AddRange(options.ToArray());

            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }

            return new Tuple<Label, ComboBox>(label, comboBox);
        }

        private Point GetPositionFromControl(Button button)
        {
            return new Point(button.Location.X / CellSize, GridSize - 1 - (button.Location.Y / CellSize));
        }

        private void NotesTextBox_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (!selectedPosition.HasValue)
                return;

            Point pos = selectedPosition.Value;

            using (var editorForm = new NotesEditorForm(this, notesTextBox))
            {
                if (editorForm.ShowDialog(this) == DialogResult.OK)
                {
                    string newNoteText = editorForm.EditedText;
                    string previousNote = notesPerCell.TryGetValue(pos, out var prev) ? prev : "";

                    if (previousNote != newNoteText)
                    {
                        notesPerCell[pos] = newNoteText;
                        notesTextBox.Text = newNoteText;
                        UpdateNotesFormatting();
                        isMapModified = true;
                    }
                }
            }
        }

        private void NotesTextBox_TextChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                string previousNote = notesPerCell.TryGetValue(pos, out var prev) ? prev : "";

                notesPerCell[pos] = notesTextBox.Text;

                // Проверка на изменение
                bool hasChanged = previousNote != notesPerCell[pos];

                if (hasChanged)
                    isMapModified = true;
            }
        }

        private void RestoreSettings(Point pos)
        {
            // Проверяем, существует ли запись в словаре границ
            if (borders.TryGetValue(pos, out var borderValues))
            {
                // Устанавливаем значения комбинационных списков
                topComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Top);
                bottomComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Bottom);
                leftComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Left);
                rightComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Right);
            }

            // Проверяем, существует ли запись в словаре переходов
            if (passageDict.TryGetValue(pos, out var passageValues))
            {
                // Устанавливаем значения комбинационных списков перехода
                passTopComboBox.SelectedIndex = passageValues.Top;
                passBottomComboBox.SelectedIndex = passageValues.Bottom;
                passLeftComboBox.SelectedIndex = passageValues.Left;
                passRightComboBox.SelectedIndex = passageValues.Right;
            }

            // Проверяем, существует ли запись в словаре закрытых состояний
            if (closedStates.TryGetValue(pos, out var closedValues))
            {
                // Устанавливаем состояние чекбоксов закрытия
                topCheck.Checked = closedValues.Top;
                bottomCheck.Checked = closedValues.Bottom;
                leftCheck.Checked = closedValues.Left;
                rightCheck.Checked = closedValues.Right;
            }

            // Проверяем, существует ли запись в словаре сообщений
            if (messageStates.TryGetValue(pos, out var messageValues))
            {
                // Устанавливаем состояние чекбоксов сообщений
                topMessageCheck.Checked = messageValues.Top;
                bottomMessageCheck.Checked = messageValues.Bottom;
                leftMessageCheck.Checked = messageValues.Left;
                rightMessageCheck.Checked = messageValues.Right;
            }

            //Считываем значение из тела ячейки
            if (centralOptions.TryGetValue(pos, out var centralOption))
            {
                centerComboBox.SelectedItem = centralOption;
            }

            notesTextBox.Text = notesPerCell[pos];
            // Обновляем изображение в PictureBox
            if (imagesPerCell.TryGetValue(pos, out var image))
            {
                cellImageBox.Image = image;
            }
            else
            {
                cellImageBox.Image = null;
            }

            if (isDangerStates.TryGetValue(pos, out var dangerState))
            {
                isDangerCheckBox.Checked = dangerState;
            }

            if (noMagicStates.TryGetValue(pos, out var noMagicState))
            {
                noMagicCheckBox.Checked = noMagicState;
            }

            // Восстановление состояния затемнённости
            if (darkeningLevels.TryGetValue(pos, out var darkeningLevel))
            {
                switch (darkeningLevel)
                {
                    case Lighting.Light:
                        lightRadioButton.Checked = true;
                        break;
                    case Lighting.Dark:
                        darkRadioButton.Checked = true;
                        break;
                    case Lighting.Darkness:
                        darkeningRadioButton.Checked = true;
                        break;
                }
            }

            UpdatePreview(); // Обновляем предварительное изображение
            UpdateNotesFormatting();
        }

        private Point ParseCurrentPosition()
        {
            string text = infoLabel.Text.Substring(infoLabel.Text.IndexOf(':') + 1).Trim();
            string[] parts = text.Split(',');
            int x = int.Parse(parts[0].Split('=')[1]);
            int y = int.Parse(parts[1].Split('=')[1]);
            return new Point(x, GridSize - 1 - y);
        }

        private void Button_MouseDown(object sender, MouseEventArgs e)
        {
            Button button = (Button)sender;
            Point pos = GetPositionFromControl(button);

            if (selectedPosition.HasValue)
            {
                gridButtons[selectedPosition.Value.X, selectedPosition.Value.Y].Invalidate();
            }

            selectedPosition = pos;
            button.Invalidate();

            infoLabel.Text = $"Выделен квадрат: X={pos.X}, Y={pos.Y}";
            RestoreSettings(pos);
            UpdatePreview();
        }

        private void WallComboBox_SelectionChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                SideValues<string> previousBorders = borders.TryGetValue(pos, out var prev)
                    ? prev.Clone()
                    : new SideValues<string>("Пустота", "Пустота", "Пустота", "Пустота");

                borders[pos] = new SideValues<string>(
                    topComboBox.SelectedItem?.ToString() ?? "",
                    bottomComboBox.SelectedItem?.ToString() ?? "",
                    leftComboBox.SelectedItem?.ToString() ?? "",
                    rightComboBox.SelectedItem?.ToString() ?? ""
                );

                bool hasChanged = !previousBorders.Equals(borders[pos]);

                if (hasChanged)
                {
                    gridButtons[pos.X, GridSize - 1 - pos.Y].Invalidate();
                    UpdatePreview();
                    isMapModified = true;
                }
            }
        }

        private void PassageComboBox_SelectionChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;

                SideValues<int> previousPassages = passageDict.TryGetValue(pos, out var prev)
                    ? prev.Clone()
                    : new SideValues<int>(0, 0, 0, 0);

                passageDict[selectedPosition.Value] = new SideValues<int>(
                    passTopComboBox.SelectedIndex,
                    passBottomComboBox.SelectedIndex,
                    passLeftComboBox.SelectedIndex,
                    passRightComboBox.SelectedIndex
                );

                bool hasChanged = !previousPassages.Equals(passageDict[pos]);

                if (hasChanged)
                {
                    gridButtons[selectedPosition.Value.X, GridSize - 1 - selectedPosition.Value.Y].Invalidate();
                    UpdatePreview();
                    isMapModified = true;
                }
            }
        }

        private void Button_MouseEnter(object sender, EventArgs e)
        {
            Button button = (Button)sender;
            Point pos = GetPositionFromControl(button);

            highlightedCells[pos.X, pos.Y] = true;
            button.Invalidate();
        }

        private void Button_MouseLeave(object sender, EventArgs e)
        {
            Button button = (Button)sender;
            Point pos = GetPositionFromControl(button);

            highlightedCells[pos.X, pos.Y] = false;
            button.Invalidate();
        }

        private void Button_Paint(object sender, PaintEventArgs e)
        {
            Button button = (Button)sender;
            Rectangle rect = e.ClipRectangle;
            Point pos = GetPositionFromControl(button);

            PrepareCellImage(pos, e.Graphics, rect);

            if (highlightedCells[pos.X, pos.Y])
            {
                e.Graphics.DrawRectangle(highlightPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
        }

        private void DrawUnexplored(Graphics g, Rectangle bounds, Point pos)
        {
            pos = new Point(pos.X, GridSize - 1 - (pos.Y));
            if (mainMapImage == null)
            {
                return; // Пропускаем обработку, если нет изображения
            }

            // Целевые координаты участка изображения для отображения на клетке
            int tileSize = CellSize; // Размер одной клетки в сетке
            int mapX = pos.X * tileSize;
            int mapY = pos.Y * tileSize;

            // Область обрезки на главном изображении
            Rectangle cropRect = new Rectangle(mapX, mapY, tileSize, tileSize);

            // Отображаем вырезанный фрагмент на холсте клетки
            g.DrawImage(
                mainMapImage,
                bounds,          // Габариты целевого региона (ячейки)
                cropRect,        // Источник (координаты вырезанного фрагмента)
                GraphicsUnit.Pixel
            );

        }

        // Метод для окраски опасных областей внутри ячейки
        private void PaintDangerArea(Graphics g, Rectangle bounds)
        {
            // Площадь покраски — это внутренний прямоугольник 26x26 пикселей
            int innerSquareSize = 40;
            //int startX = bounds.X + (bounds.Width - innerSquareSize) / 2;
            //int startY = bounds.Y + (bounds.Height - innerSquareSize) / 2;

            // Циклическое окрашивание каждого четвертого пикселя
            for (int y = 0; y < innerSquareSize; y++)
            {
                for (int x = 0; x < innerSquareSize; x++)
                {
                    // Начинаем с индекса 1 (второй пиксель), потом каждый четвёртый
                    if ((x + y) % 4 == 1)
                    {
                        g.FillRectangle(Brushes.Red, x, y, 1, 1);
                    }
                }
            }
        }

        // Метод для окраски зон с отсутствием магии
        private void PaintNoMagicArea(Graphics g, Rectangle bounds)
        {
            // Размер внутреннего квадрата 26x26 пикселей
            int innerSquareSize = 40;
            //int startX = bounds.X + (bounds.Width - innerSquareSize) / 2;
            //int startY = bounds.Y + (bounds.Height - innerSquareSize) / 2;

            // Покраска каждым 4-м пикселем, начиная с 4-го (индекс 3)
            for (int y = 0; y < innerSquareSize; y++)
            {
                for (int x = 0; x < innerSquareSize; x++)
                {
                    // Каждое 4-е смещение, начиная с третьего (индекс 3)
                    if ((x + y) % 4 == 3)
                    {
                        g.FillRectangle(Brushes.Yellow, x, y, 1, 1);
                    }
                }
            }
        }

        // Метод для нанесения эффекта "Темно"
        private void PaintDarkArea(Graphics g, Rectangle bounds)
        {
            // Участок для покрытия — внутренний прямоугольник 26x26 пикселей
            int innerSquareSize = 40;
            //int startX = bounds.X + (bounds.Width - innerSquareSize) / 2;
            //int startY = bounds.Y + (bounds.Height - innerSquareSize) / 2;

            // Окрашивание каждого 4-го пикселя, начиная с третьего (индекс 2)
            for (int y = 0; y < innerSquareSize; y++)
            {
                for (int x = 0; x < innerSquareSize; x++)
                {
                    // Окрашиваем каждый 4-й пиксель, начиная с 3-го (индекс 2)
                    if ((x + y) % 8 == 2)
                    {
                        g.FillRectangle(Brushes.Gray, x, y, 1, 1);
                    }
                }
            }
        }

        // Метод для нанесения эффекта "Мрак"
        private void PaintDarkeningArea(Graphics g, Rectangle bounds)
        {
            // Центральная область для закрашивания (размер 26x26 пикселей)
            int innerSquareSize = 40;
            //int startX = bounds.X + (bounds.Width - innerSquareSize) / 2;
            //int startY = bounds.Y + (bounds.Height - innerSquareSize) / 2;

            // Окрашивание каждого второго пикселя, начиная с первого (индекс 0)
            for (int y = 0; y < innerSquareSize; y++)
            {
                for (int x = 0; x < innerSquareSize; x++)
                {
                    // Красятся пиксели с четными индексами суммы x+y
                    if ((x + y) % 4 == 0)
                    {
                        g.FillRectangle(new SolidBrush(Color.LightGray), x, y, 1, 1);
                    }
                }
            }
        }

        private void DrawCentralCell(Graphics g, Rectangle bounds, int shift_x, int shift_y, int brightness_limit, int[,] body_pixels)
        {
            // Масштабируем пространство под матрицу 26x26
            int scale = Math.Min(bounds.Width / 26, bounds.Height / 26);

            // Отрисовка массива пикселей на графике
            // Обходим все пиксели массива
            for (int y = 0; y < body_pixels.GetLength(1); y++)
            {
                for (int x = 0; x < body_pixels.GetLength(0); x++)
                {
                    int pixelValue = body_pixels[x, y];

                    // Получаем отдельные каналы цвета (RGBA)
                    byte alpha = (byte)((pixelValue >> 24) & 0xFF);
                    byte red = (byte)((pixelValue >> 16) & 0xFF);
                    byte green = (byte)((pixelValue >> 8) & 0xFF);
                    byte blue = (byte)(pixelValue & 0xFF);

                    // Общая яркость пикселя
                    int brightness = red + green + blue;

                    // Если пиксель достаточно тёмный, делаем его прозрачным
                    if (brightness < brightness_limit)
                    {
                        g.FillRectangle(new SolidBrush(Color.Transparent), bounds.X + x * scale, bounds.Y + y * scale, scale, scale);
                    }
                    else
                    {
                        // red = (byte)Math.Min(red + 255, 255); // Ограничим максимальное значение канала красным пределом (255)
                        //red = 255;
                        //blue = (byte)Math.Max(blue - 150, 0);
                        //green = (byte)Math.Max(green - 150, 0);

                        // Рисуем пиксель нормальным цветом
                        Color pixelColor = Color.FromArgb(alpha, red, green, blue);
                        g.FillRectangle(new SolidBrush(pixelColor), shift_x + bounds.X + x * scale, shift_y + bounds.Y + y * scale, scale, scale);
                    }
                }
            }
        }

        // Метод для рисования слова "EXIT" и стрелок слева и справа от него
        private void DrawExitWord(Graphics g, Rectangle bounds, Direction direction, Color exitWordColor)
        {

            // Выбор массива в зависимости от направления
            int[,] exitWordPatternToUse = direction == Direction.Top ? Passage_Pixels_Patterns.exit_top :
                direction == Direction.Left ? Passage_Pixels_Patterns.exit_top :
                direction == Direction.Right ? Passage_Pixels_Patterns.exit_top :
                Passage_Pixels_Patterns.exit_bottom;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, exitWordColor}, // Средняя интенсивность серого
        //{2, Color.FromArgb(160, 160, 160)}, // Светлее серый
        //{3, Color.FromArgb(192, 192, 192)}, // Очень светлый оттенок серого
        //{4, Color.FromArgb(100, 100, 100)},  // Тёмно-серый
        //{5, Color.FromArgb(255, 201, 14)}   // Жёлтая ступенька
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(exitWordPatternToUse.GetLength(1), exitWordPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < exitWordPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < exitWordPatternToUse.GetLength(1); x++)
                    {
                        int value = exitWordPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        //Метод для отрисовки портала
        private void DrawPortal(Graphics graphics, Rectangle bounds, Direction direction)
        {
            // Параметры врат
            int gateWidth = (bounds.Width / 3) + 1;
            int gateHeight = (bounds.Height / 6) - 1;

            // Базовые цвета врат
            Color[] baseColors = new Color[]
            {
        ColorTranslator.FromHtml("#3F48CC"), // Внешние ворота
        ColorTranslator.FromHtml("#00BFFF"), // Средние ворота
        ColorTranslator.FromHtml("#ADD8E6")  // Внутренние ворота
            };

            // Класс Random для генерации случайных чисел
            Random rand = new Random();

            // Массив случайных цветов
            Color[] colors = new Color[baseColors.Length];

            // Генерируем случайные цвета
            for (int i = 0; i < baseColors.Length; i++)
            {
                int r = baseColors[i].R;
                int g = baseColors[i].G;
                int b = baseColors[i].B;

                float variationPercentage = 0.1f; // Вариативность в процентах
                int deltaR = (int)(rand.NextFloat(-variationPercentage, variationPercentage) * r);
                int deltaG = (int)(rand.NextFloat(-variationPercentage, variationPercentage) * g);
                int deltaB = (int)(rand.NextFloat(-variationPercentage, variationPercentage) * b);

                colors[i] = Color.FromArgb(
                    Clamp(r + deltaR, 0, 255),
                    Clamp(g + deltaG, 0, 255),
                    Clamp(b + deltaB, 0, 255)
                );
            }

            // Сохраняем оригинал графического контекста
            GraphicsState savedState = graphics.Save();

            // Координаты начала рисования врат
            int dx = 0, dy = 1;

            switch (direction)
            {
                case Direction.Top:
                    dx = gateWidth - 1;
                    dy = bounds.Y + 1;
                    break;
                case Direction.Bottom:
                    dx = gateWidth - 1;
                    dy = bounds.Bottom - gateHeight - 1;
                    break;
                case Direction.Left:
                    // Перенос и поворот на 90 градусов против часовой стрелки
                    graphics.TranslateTransform(bounds.X + 3, bounds.Y - 1 + gateWidth * 2);
                    graphics.RotateTransform(-90);
                    dy = -gateHeight / 2;
                    break;
                case Direction.Right:
                    // Перенос и поворот на 270 градусов (90 градусов по часовой стрелке)
                    graphics.TranslateTransform(bounds.Right - 3, bounds.Y - 1 + gateWidth);
                    graphics.RotateTransform(90);
                    dy = -gateHeight / 2;
                    break;
            }

            // Толщина линий врат
            int lineThickness = 2;

            // Рисуем врат слоями, начиная с самого внешнего
            for (int layer = 0; layer < colors.Length; layer++)
            {
                // Координаты начала врат
                int x = dx + layer * lineThickness;
                int y = dy + layer * lineThickness;

                // Ширина и высота уменьшаются на величину толщины линии
                int currentWidth = gateWidth - layer * lineThickness * 2;
                int currentHeight = gateHeight - layer * lineThickness;

                // Рисуем ворота
                using (Pen pen = new Pen(colors[layer], lineThickness))
                {
                    graphics.DrawRectangle(pen, new Rectangle(x, y, currentWidth, currentHeight));
                }
            }


            // Чёрный пиксель в верхнем левом углу внешних ворот
            using (SolidBrush blackBrush = new SolidBrush(Color.Black))
            {
                graphics.FillRectangle(blackBrush, dx - 1, dy - 1, 1, 1); // Верхний левый угол
                graphics.FillRectangle(blackBrush, dx + gateWidth, dy - 1, 1, 1); // Верхний правый угол
            }

            // Средний слой получает цвет внешних ворот
            int midX = dx + lineThickness;
            int midY = dy + lineThickness;
            int midW = gateWidth - 2 * lineThickness;
            int midH = gateHeight - 2 * lineThickness;

            // Внутренний слой получает цвет средних ворот
            int innerX = dx + 2 * lineThickness;
            int innerY = dy + 2 * lineThickness;
            int innerW = gateWidth - 4 * lineThickness;
            int innerH = gateHeight - 4 * lineThickness;

            // Уголки средних ворот
            using (SolidBrush midBrush = new SolidBrush(colors[0])) // Цвет внешних ворот
            {
                // Верхний левый угол
                graphics.FillRectangle(midBrush, midX - 1, midY - 1, 1, 1);
                // Верхний правый угол
                graphics.FillRectangle(midBrush, midX + midW, midY - 1, 1, 1);
            }

            // Уголки внутренних ворот
            using (SolidBrush innerBrush = new SolidBrush(colors[1])) // Цвет средних ворот
            {
                // Верхний левый угол
                graphics.FillRectangle(innerBrush, innerX - 1, innerY - 1, 1, 1);
                // Верхний правый угол
                graphics.FillRectangle(innerBrush, innerX + innerW, innerY - 1, 1, 1);
            }

            // Черные линии по сторонам портала
            using (Pen blackPen = new Pen(Color.Black, 1))
            {
                switch (direction)
                {
                    case Direction.Top:
                    case Direction.Bottom:
                        // Левая и правая вертикальные линии
                        graphics.DrawLine(blackPen, dx - 2, dy - 1, dx - 2, dy + gateHeight);
                        graphics.DrawLine(blackPen, dx + gateWidth + 1, dy - 1, dx + gateWidth + 1, dy + gateHeight);
                        break;
                    case Direction.Left:
                        // Левая и правая горизонтальные линии (после поворота) в левой части ячейки
                        graphics.DrawLine(blackPen, dx - 1, dy - 1, dx - 1, dy + gateHeight);                   // Левая сторона
                        graphics.DrawLine(blackPen, dx + gateWidth + 2, dy - 1, dx + gateWidth + 2, dy + gateHeight); // Правая сторона
                        break;
                    case Direction.Right:
                        // Левая и правая горизонтальные линии (после поворота) в правой части ячейки
                        graphics.DrawLine(blackPen, dx - 2, dy, dx - 2, dy + gateHeight + 1);                   // Левая сторона
                        graphics.DrawLine(blackPen, dx + gateWidth + 1, dy, dx + gateWidth + 1, dy + gateHeight + 1); // Правая сторона
                        break;

                }
            }

            // Белый пиксель в центре портала
            int cx = dx + gateWidth / 2;
            int cy = dy + (gateHeight / 2) + 3;
            using (SolidBrush whiteBrush = new SolidBrush(Color.White))
            {
                graphics.FillRectangle(whiteBrush, cx - 1, cy, 2, 1); // Центральные белые пиксели
            }

            // Прозрачная центральная часть врат
            using (SolidBrush transparentBrush = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
            {
                graphics.FillRectangle(transparentBrush, dx + 5, dy + 5, gateWidth - 10, gateHeight - 10);
            }

            // Восстанавливаем оригинал графического контекста
            graphics.Restore(savedState);
        }

        // Функция ограничения RGB-значений
        private int Clamp(int value, int min, int max)
        {
            return Math.Min(Math.Max(value, min), max);
        }

        // Метод для отрисовки лестницы вверх
        private void DrawStairsUp(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] stairsPatternToUse = direction == Direction.Top ?
                Passage_Pixels_Patterns.stairs_pattern_rotated270 : // Используем зеркальный массив для направления Top
                direction == Direction.Bottom ?
                Passage_Pixels_Patterns.stairs_pattern_rotated : // Используем перевернутый массив для направления Bottom
                Passage_Pixels_Patterns.stairs_pattern;         // Используем исходный массив для Left и Right

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.Black},               // Пустой пиксель
        {1, Color.FromArgb(128, 128, 128)}, // Средняя интенсивность серого
        {2, Color.FromArgb(160, 160, 160)}, // Светлее серый
        {3, Color.FromArgb(192, 192, 192)}, // Очень светлый оттенок серого
        {4, Color.FromArgb(100, 100, 100)},  // Тёмно-серый
        {5, Color.FromArgb(255, 201, 14)}   // Жёлтая ступенька
    };

            // Рендер лестницы
            Bitmap stairsBitmap = new Bitmap(stairsPatternToUse.GetLength(1), stairsPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(stairsBitmap))
            {
                // Отрисовка лестницы на временное изображение
                for (int y = 0; y < stairsPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < stairsPatternToUse.GetLength(1); x++)
                    {
                        int value = stairsPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                stairsBitmap.RotateFlip(RotateFlipType.Rotate180FlipY); // Поворот на 180 градусов
            }

            // Размещение изображения лестницы
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X + (bounds.Width - stairsBitmap.Width) / 2, // центрирование по горизонтали
                        bounds.Y,                                          // начало сверху
                        stairsBitmap.Width, stairsBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X + (bounds.Width - stairsBitmap.Width) / 2, // центрирование по горизонтали
                        bounds.Bottom - stairsBitmap.Height,                // начало снизу
                        stairsBitmap.Width, stairsBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,                                         // слева
                        bounds.Y + (bounds.Height - stairsBitmap.Height) / 2, // центрирование по вертикали
                        stairsBitmap.Width, stairsBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - stairsBitmap.Width,                // справа
                        bounds.Y + (bounds.Height - stairsBitmap.Height) / 2, // центрирование по вертикали
                        stairsBitmap.Width, stairsBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка лестницы
            g.DrawImage(stairsBitmap, targetRect);
        }

        // Функция для получения противоположённого направления
        private Direction GetOppositeDirection(Direction direction)
        {
            switch (direction)
            {
                case Direction.Top: return Direction.Bottom;
                case Direction.Bottom: return Direction.Top;
                case Direction.Left: return Direction.Right;
                case Direction.Right: return Direction.Left;
                default: return direction;
            }
        }

        // Метод для отрисовки лестницы вниз
        private void DrawStairsDown(Graphics g, Rectangle bounds, Direction direction)
        {

            // Получаем противоположённое направление
            Direction oppositeDirection = GetOppositeDirection(direction);

            // Создаем изображение лестницы вверх
            Bitmap originalStairsImage = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics imgG = Graphics.FromImage(originalStairsImage))
            {
                DrawStairsUp(imgG, bounds, oppositeDirection); // Нарисовать оригинальную лестницу вверх
            }

            // Зеркально отражаем изображение
            originalStairsImage.RotateFlip(RotateFlipType.Rotate180FlipNone);

            // Теперь переиспользуем старую логику, но с новым направлением
            Rectangle targetRect;
            switch (oppositeDirection)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X + (bounds.Width - originalStairsImage.Width) / 2, // Центрирование по горизонтали
                        bounds.Y, // Начало сверху
                        originalStairsImage.Width, originalStairsImage.Height
                    );
                    break;

                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X + (bounds.Width - originalStairsImage.Width) / 2, // Центрирование по горизонтали
                        bounds.Bottom - originalStairsImage.Height, // Начало снизу
                        originalStairsImage.Width, originalStairsImage.Height
                    );
                    break;

                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X, // Слева
                        bounds.Y + (bounds.Height - originalStairsImage.Height) / 2, // Центрирование по вертикали
                        originalStairsImage.Width, originalStairsImage.Height
                    );
                    break;

                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - originalStairsImage.Width, // Справа
                        bounds.Y + (bounds.Height - originalStairsImage.Height) / 2, // Центрирование по вертикали
                        originalStairsImage.Width, originalStairsImage.Height
                    );
                    break;

                default:
                    return; // Недопустимое направление
            }

            // Отрисовываем преобразованное изображение
            g.DrawImage(originalStairsImage, targetRect);
        }

        // Метод для отрисовки надписи SECRET
        private void DrawSecretWord(Graphics g, Rectangle bounds, Direction direction)
        {
            // Вычисляем максимальный допустимый размер шрифта
            float maxFontSize = CalculateOptimalFontSize(g, "SECRET", bounds.Width, bounds.Height);

            // Создаем оптимальный шрифт
            Font font = new Font(FontFamily.GenericSansSerif, maxFontSize, FontStyle.Bold);
            StringFormat format = new StringFormat();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;

            // Сохраняем исходное состояние графического контекста
            GraphicsState state = g.Save();

            // Высокий уровень сглаживания и интерполяции
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // Промежуточный буфер для отрисовки
            Bitmap bufferBmp = new Bitmap(bounds.Width + 2, bounds.Height);
            using (Graphics bufferG = Graphics.FromImage(bufferBmp))
            {
                // Очистим буфер
                bufferG.Clear(Color.Transparent);

                // Предварительно выводим текст
                bufferG.DrawString("SECRE", font, Brushes.HotPink, bounds, format);
                SizeF textSize = bufferG.MeasureString("SECRE", font);
                float extraSpace = 3f; // Интервал между "E" и "T"
                float nextX = bounds.X + textSize.Width + extraSpace;
                float nextY = bounds.Y + (bounds.Height - textSize.Height) / 2;
                bufferG.DrawString("T", font, Brushes.HotPink, nextX, nextY);
            }

            // Основа: наносим подготовленный буфер на изображение с учётом направления
            switch (direction)
            {
                case Direction.Top:
                    g.DrawImage(bufferBmp, bounds.X - 3, bounds.Y - 15); // Верхний край
                    break;

                case Direction.Bottom:
                    g.DrawImage(bufferBmp, bounds.X - 3, bounds.Bottom - (bufferBmp.Height / 2) - 3); // Нижний край
                    break;

                case Direction.Left:
                    g.TranslateTransform(bounds.X, bounds.Y + bounds.Height / 2); // Поворот и сдвиг влево
                    g.RotateTransform(-90);
                    g.DrawImage(bufferBmp, -2 - bufferBmp.Width / 2, -15); // Сдвигаем картинку по оси Х, чтобы текст не ушёл за пределы
                    break;

                case Direction.Right:
                    g.TranslateTransform(bounds.Right, bounds.Y + bounds.Height / 2); // Поворот и сдвиг вправо
                    g.RotateTransform(90);
                    g.DrawImage(bufferBmp, -2 - bufferBmp.Width / 2, -15); // Сдвигаем картинку по оси Х
                    break;
            }

            // Восстанавливаем исходное состояние графического контекста
            g.Restore(state);
        }

        private void FilterByHotPink(Bitmap bmp)
        {
            Color targetColor = Color.FromArgb(255, 72, 0, 0);

            for (int x = 0; x < bmp.Width; x++)
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Color pixel = bmp.GetPixel(x, y);

                    // Проверяем равенство цвета пикселя целевому цвету
                    if (pixel == targetColor)
                    {
                        SafeSetPixel(bmp, x, y, Color.FromArgb(255, 0, 255, 0));
                    }
                }
            }
        }

        // Метод фильтрации изображений с использованием Dictionary для соответствия цветов
        private void FilterColors(Bitmap bmp, Dictionary<string, string> colorMapping)
        {
            foreach (var pair in colorMapping)
            {
                var fromRgbString = pair.Key.Split(',');
                var toRgbString = pair.Value.Split(',');

                byte rFrom = byte.Parse(fromRgbString[0].Trim());
                byte gFrom = byte.Parse(fromRgbString[1].Trim());
                byte bFrom = byte.Parse(fromRgbString[2].Trim());

                int rTo = int.Parse(toRgbString[0].Trim()); // Может быть отрицательным!
                int gTo = int.Parse(toRgbString[1].Trim());
                int bTo = int.Parse(toRgbString[2].Trim());

                bool isTransparent = rTo == -1 && gTo == -1 && bTo == -1;

                for (int x = 0; x < bmp.Width; x++)
                {
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        Color pixel = bmp.GetPixel(x, y);

                        if (pixel.R == rFrom && pixel.G == gFrom && pixel.B == bFrom)
                        {
                            if (isTransparent)
                            {
                                // Устанавливаем прозрачность, сохраняя оригинальную позицию цвета
                                SafeSetPixel(bmp, x, y, Color.FromArgb(0, pixel.R, pixel.G, pixel.B));
                            }
                            else
                            {
                                // Обычная замена цвета
                                SafeSetPixel(bmp, x, y, Color.FromArgb((byte)rTo, (byte)gTo, (byte)bTo));
                            }
                        }
                    }
                }
            }
        }

        void CorrectLetterC(Bitmap letterC, Direction direction)
        {
            switch (direction)
            {
                case Direction.Top:
                    SafeSetPixel(letterC, 15, 5, Color.FromArgb(255, 255, 105, 180));
                    SafeSetPixel(letterC, 18, 6, Color.Transparent);
                    break;
                case Direction.Bottom:
                    SafeSetPixel(letterC, 15, 5 + 32, Color.FromArgb(255, 255, 105, 180));
                    SafeSetPixel(letterC, 18, 6 + 32, Color.Transparent);
                    break;
                case Direction.Left:
                    SafeSetPixel(letterC, 5, 24, Color.FromArgb(255, 255, 105, 180));
                    SafeSetPixel(letterC, 6, 21, Color.Transparent);
                    SafeSetPixel(letterC, 1, 1, Color.FromArgb(255, 255, 105, 180));
                    break;
                case Direction.Right:
                    SafeSetPixel(letterC, 34, 15, Color.FromArgb(255, 255, 105, 180));
                    SafeSetPixel(letterC, 33, 18, Color.Transparent);
                    break;
                default:
                    return;
            }
        }

        void SafeSetPixel(Bitmap bmp, int x, int y, Color color)
        {
            if (x >= 0 && x < bmp.Width && y >= 0 && y < bmp.Height)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        //мнтод замены писселей одного цвета на пиксели другого цвета в переданноым в него изображении
        private void ReplaceColorInBitmap(Bitmap bmp, Color oldColor, Color newColor)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Color pixel = bmp.GetPixel(x, y);
                    if (pixel == oldColor)
                    {
                        SafeSetPixel(bmp, x, y, newColor);
                    }
                }
            }
        }

        void DrawFilteredHotPinkSecret(Graphics g, Rectangle bounds, Direction direction)
        {
            // Предполагаем, что DrawSecret создает изображение с размерами bounds
            Bitmap originaSecretTextImage = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics imgG = Graphics.FromImage(originaSecretTextImage))
            {
                DrawSecretWord(imgG, bounds, direction); // РИСУЕМ оригинальное изображение
            }

            // Применяем фильтр, оставляем только горячий розовый цвет
            FilterColors(originaSecretTextImage, colorsMap);
            CorrectLetterC(originaSecretTextImage, direction);

            Rectangle targetRect;
            int secretTextWidth = originaSecretTextImage.Width;
            int secretTextHeight = originaSecretTextImage.Height;

            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(bounds.X, bounds.Y, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - secretTextWidth / 2, bounds.Bottom - secretTextHeight, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 - secretTextWidth / 2, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(bounds.Right - secretTextWidth, bounds.Y + bounds.Height / 2 - secretTextWidth / 2, secretTextWidth, secretTextHeight);
                    break;
                default:
                    return; // Недопустимое направление
            }

            // Выводим итоговое изображение двери на холст
            g.DrawImage(originaSecretTextImage, targetRect);
        }

        //Отображение надписи SECRET для белых поверхностией
        private void DrawFilteredDarkPinkSecret(Graphics g, Rectangle bounds, Direction direction)
        {
            // Исходное изображение SECRET
            Bitmap originalSecretTextImage = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics imgG = Graphics.FromImage(originalSecretTextImage))
            {
                DrawFilteredHotPinkSecret(imgG, bounds, direction); // Нарисовать оригинальный текст SECRET
            }

            // Замена цвета розовых пикселей на тёмно-зелёный
            ReplaceColorInBitmap(originalSecretTextImage, Color.FromArgb(255, 105, 180), Color.FromArgb(0, 0, 255));

            // Вывод результата
            Rectangle targetRect;
            int secretTextWidth = originalSecretTextImage.Width;
            int secretTextHeight = originalSecretTextImage.Height;

            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(bounds.X, bounds.Y, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - secretTextWidth / 2, bounds.Bottom - secretTextHeight, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 - secretTextWidth / 2, secretTextWidth, secretTextHeight);
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(bounds.Right - secretTextWidth, bounds.Y + bounds.Height / 2 - secretTextWidth / 2, secretTextWidth, secretTextHeight);
                    break;
                default:
                    return; // Некорректное направление
            }

            g.DrawImage(originalSecretTextImage, targetRect);
        }

        // Метод для отрисовки надписи BARRIER!
        private void Draw_Barrier_Word(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.barrier :
                direction == Direction.Left ? Border_Pixels_Patterns.barrier :
                direction == Direction.Right ? Border_Pixels_Patterns.barrier :
                Border_Pixels_Patterns.barrier;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.White}
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }
            //if (direction == Direction.Bottom)
            //{
            //    exitWordBitmap.RotateFlip(RotateFlipType.Rotate180FlipY); // 
            //}

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        // Вспомогательная функция для автоматического подбора размера шрифта
        private float CalculateOptimalFontSize(Graphics g, string text, float maxWidth, float maxHeight)
        {
            float fontSize = 12f; // начальный предполагаемый размер шрифта
            float step = 1f; // шаг уменьшения размера шрифта

            while (fontSize > 1f) // ограничиваем минимальное значение размера шрифта
            {
                Font testFont = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold);
                SizeF size = g.MeasureString(text, testFont);

                // Если ширина или высота текста превышают доступные пределы, уменьшаем размер шрифта
                if (size.Width > maxWidth || size.Height > maxHeight)
                {
                    fontSize -= step;
                }
                else
                {
                    break;
                }
            }

            return Math.Max(fontSize, 1f); // дополнительно гарантируем, что размер шрифта не меньше 1
        }

        // Вспомогательная функция для правильного выбора метода рисования двери
        private void DrawCorrectDoor(Graphics g, Rectangle bounds, string wallType, Direction direction)
        {
            if (wallType == "Каменная стена")
            {
                DrawRoundedDoor(g, bounds, direction);
            }
            else
            {
                DrawDoor(g, bounds, direction);
            }
        }

        // Вспомогательная функция для правильного выбора метода рисования SECRET
        private void DrawCorrectSecret(Graphics g, Rectangle bounds, string wallType, Direction direction)
        {
            if (wallType == "Кирпичная стена")
            {
                DrawFilteredDarkPinkSecret(g, bounds, direction); // Новое условие для кирпичной стены
            }
            else
            {
                DrawFilteredHotPinkSecret(g, bounds, direction); // Осталось как было раньше
            }
        }

        // Отрисовка закруглённой двери, в случае для каменной стены
        private void DrawRoundedDoor(Graphics g, Rectangle bounds, Direction direction)
        {
            // Создаем оригинальное изображение двери
            Bitmap originalDoorImage = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics drawSurface = Graphics.FromImage(originalDoorImage))
            {
                DrawDoor(drawSurface, bounds, direction); // Рисуем обычную дверь
            }

            // Применяем закругление и удаление пикселей
            ModifyDoor(originalDoorImage, direction);

            // Расположение двери на холсте
            Rectangle targetRect;
            int doorWidth = originalDoorImage.Width;
            int doorHeight = originalDoorImage.Height;

            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - doorWidth / 2, bounds.Y, doorWidth, doorHeight);
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - doorWidth / 2, bounds.Bottom - doorHeight, doorWidth, doorHeight);
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 - doorWidth / 2, doorWidth, doorHeight);
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(bounds.Right - doorWidth, bounds.Y + bounds.Height / 2 - doorWidth / 2, doorWidth, doorHeight);
                    break;
                default:
                    return; // Недопустимое направление
            }

            // Выводим итоговое изображение двери на холст
            g.DrawImage(originalDoorImage, targetRect);
        }

        // Метод для удаления пикселей в конкретных столбцах
        private void ModifyDoor(Bitmap doorImage, Direction direction)
        {
            // Определим начало колонны для обработки
            int doorStartColumn = 13;

            // Список индексов колонок, содержащих важные части рисунка двери
            int[] columnsToProcess = { 0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13 };

            // Карта количества удаляемых пикселей для каждой колонки
            Dictionary<int, int> pixelRemovalMap = new Dictionary<int, int>()
    {
        { 0, 3 }, // Убираем три верхних пикселя
        { 1, 2 }, // Два верхних пикселя
        { 2, 2 }, // Ещё два пикселя
        { 3, 1 }, // Одну строку пикселей
        { 4, 1 }, // Ещё одна строка пикселей
        { 5, 1 }, // Одна строка пикселей
        { 8, 1 }, // Пиксель №8 удаляется одним блоком
        { 9, 1 }, // То же самое для 9-ой колонки
        { 10, 1 }, // Продолжаем удалять
        { 11, 2 }, // Две строки
        { 12, 2 }, // Последние две строки
        { 13, 3 }  // Завершаем картину трёхстрочным удалением
    };

            // Работаем только с нужными колонками
            foreach (int columnOffset in columnsToProcess)
            {
                int actualColumn = doorStartColumn + columnOffset; // Получаем фактический номер колонки

                if (actualColumn < doorImage.Width && pixelRemovalMap.ContainsKey(columnOffset))
                {
                    int pixelsToRemove = pixelRemovalMap[columnOffset];

                    // Удаляем пиксели в указанной колонке
                    for (int y = 0; y < pixelsToRemove; y++)
                    {
                        if (direction == Direction.Top)
                            SafeSetPixel(doorImage, actualColumn, y, Color.Transparent); // Убираем верхний ряд пикселей
                        else if (direction == Direction.Bottom)
                            SafeSetPixel(doorImage, actualColumn, y + 33, Color.Transparent); // Аналогично для нижней части
                        else if (direction == Direction.Left)
                            SafeSetPixel(doorImage, y, actualColumn, Color.Transparent); // Убираем левый ряд пикселей
                        else if (direction == Direction.Right)
                            SafeSetPixel(doorImage, doorImage.Width - y - 1, actualColumn, Color.Transparent); // Убираем правый ряд пикселей
                    }
                }
            }
        }

        // Метод для удаления пикселей в указанном столбце
        private void RemovePixelsInColumn(Bitmap bitmap, int column, int pixelsToRemove)
        {
            for (int y = 0; y < pixelsToRemove; y++)
            {
                // Ставим пиксели прозрачными
                SafeSetPixel(bitmap, column, y, Color.Transparent);
            }
        }

        //Метод отрисовки металлической решётки
        private void DrawGrate(Graphics g, Rectangle bounds, Direction direction)
        {
            // Формирование решётки
            int grateWidth = (bounds.Width / 3) + 1;
            int grateHeight = (bounds.Height / 6) + 1;

            int barThickness = 1;
            int spacing = 1;

            Bitmap grateImage = new Bitmap(grateWidth, grateHeight);
            using (Graphics imgG = Graphics.FromImage(grateImage))
            {
                imgG.Clear(Color.FromArgb(1, 1, 1));

                // Рисуем горизонтальные полосы
                int horizontalBars = (grateHeight - barThickness) / (spacing + barThickness);
                for (int i = 0; i <= horizontalBars; i++)
                {
                    int y = i * (spacing + barThickness);
                    imgG.FillRectangle(Brushes.Silver, 0, y, grateWidth, barThickness);
                }

                // Рисуем вертикальные полосы
                int verticalBars = (grateWidth - barThickness) / (spacing + barThickness);
                for (int j = 0; j <= verticalBars; j++)
                {
                    int x = j * (spacing + barThickness);
                    imgG.FillRectangle(Brushes.Silver, x, 0, barThickness, grateHeight);
                }
            }

            // Позиционирование решётки
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X + bounds.Width / 2 - grateWidth / 2,
                        bounds.Y,
                        grateWidth,
                        grateHeight
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X + bounds.Width / 2 - grateWidth / 2,
                        bounds.Bottom - grateHeight,
                        grateWidth,
                        grateHeight
                    );
                    break;
                case Direction.Left:
                    grateImage.RotateFlip(RotateFlipType.Rotate90FlipX);
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y + bounds.Height / 2 - grateHeight / 2 - 4,
                        grateHeight,
                        grateWidth
                    );
                    break;
                case Direction.Right:
                    grateImage.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    targetRect = new Rectangle(
                        bounds.Right - grateHeight,
                        bounds.Y + bounds.Height / 2 - grateHeight / 2 - 4,
                        grateHeight,
                        grateWidth
                    );
                    break;
                default:
                    return; // Несуществующее направление
            }

            // Оставляем решение, какое изображение возвращать
            g.DrawImage(grateImage, targetRect);
        }

        private void DrawRoundedGrate(Graphics g, Rectangle cellBounds, Direction direction)
        {
            // Создаем оригинальное изображение решётки
            Bitmap originalGrateImage = new Bitmap(cellBounds.Width, cellBounds.Height);
            using (Graphics drawSurface = Graphics.FromImage(originalGrateImage))
            {
                DrawGrate(drawSurface, cellBounds, direction); // Рисуем обычную решётку
            }

            // Применяем закругление и удаление пикселей
            ModifyGrate(originalGrateImage, direction);

            // Расположение решётки на холсте
            Rectangle targetRect;
            int grateWidth = originalGrateImage.Width;
            int grateHeight = originalGrateImage.Height;

            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(cellBounds.X + cellBounds.Width / 2 - grateWidth / 2, cellBounds.Y, grateWidth, grateHeight);
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(cellBounds.X + cellBounds.Width / 2 - grateWidth / 2, cellBounds.Bottom - grateHeight, grateWidth, grateHeight);
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(cellBounds.X, cellBounds.Y + cellBounds.Height / 2 - grateWidth / 2, grateWidth, grateHeight);
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(cellBounds.Right - grateWidth, cellBounds.Y + cellBounds.Height / 2 - grateWidth / 2, grateWidth, grateHeight);
                    break;
                default:
                    return; // Недопустимое направление
            }

            // Выводим итоговое изображение решётки на холст
            g.DrawImage(originalGrateImage, targetRect);
        }

        private void ModifyGrate(Bitmap doorImage, Direction direction)
        {
            // Определим начало колонны для обработки
            int doorStartColumn = 13;

            // Список индексов колонок, содержащих важные части рисунка двери
            int[] columnsToProcess = { 0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13 };

            // Карта количества удаляемых пикселей для каждой колонки
            Dictionary<int, int> pixelRemovalMap = new Dictionary<int, int>()
    {
        { 0, 3 }, // Убираем три верхних пикселя
        { 1, 2 }, // Два верхних пикселя
        { 2, 2 }, // Ещё два пикселя
        { 3, 1 }, // Одну строку пикселей
        { 4, 1 }, // Ещё одна строка пикселей
      //  { 5, 1 }, // Одна строка пикселей
        { 8, 1 }, // Пиксель №8 удаляется одним блоком
        { 9, 1 }, // То же самое для 9-ой колонки
        { 10, 2 }, // Ещё 2 пикселя
        { 11, 2 }, // Две строки
        { 12, 3 }, // Последние две строки
        { 13, 7 },  // Завершаем картину трёхстрочным удалением
    };

            // Работаем только с нужными колонками
            foreach (int columnOffset in columnsToProcess)
            {
                int actualColumn = doorStartColumn + columnOffset; // Получаем фактический номер колонки

                if (actualColumn < doorImage.Width && pixelRemovalMap.ContainsKey(columnOffset))
                {
                    int pixelsToRemove = pixelRemovalMap[columnOffset];

                    // Удаляем пиксели в указанной колонке
                    for (int y = 0; y < pixelsToRemove; y++)
                    {
                        if (direction == Direction.Top)
                            SafeSetPixel(doorImage, actualColumn, y, Color.Transparent); // Убираем верхний ряд пикселей
                        else if (direction == Direction.Bottom)
                            SafeSetPixel(doorImage, actualColumn, y + 33, Color.Transparent); // Аналогично для нижней части
                        else if (direction == Direction.Left)
                            SafeSetPixel(doorImage, y, actualColumn, Color.Transparent); // Убираем левый ряд пикселей
                        else if (direction == Direction.Right)
                            SafeSetPixel(doorImage, doorImage.Width - y - 1, actualColumn, Color.Transparent); // Убираем правый ряд пикселей
                    }
                }
            }
        }

        private Color GetColorForOption(string option)
        {
            switch (option)
            {
                case "Пустота":
                    return Color.Black;
                case "Желтый":
                    return Color.Yellow;
                case "Красный":
                    return Color.Red;
                case "Синий":
                    return Color.Blue;
                case "Зеленый":
                    return Color.Green;
                default:
                    return Color.Black;
            }
        }

        private Brush GetSolidBrushForOption(string colorName)
        {
            switch (colorName)
            {
                case "Желтый":
                    return Brushes.Yellow;
                case "Красный":
                    return Brushes.Red;
                case "Синий":
                    return Brushes.Blue;
                case "Зелёный":
                    return Brushes.Green;
                default:
                    return Brushes.Black;
            }
        }

        private void PrepareCellImage(Point pos, Graphics g, Rectangle bounds)
        {
            // Применяем специальные эффекты согласно состоянию клетки
            if (isDangerStates.TryGetValue(pos, out var isDangerous) && isDangerous)
            {
                PaintDangerArea(g, bounds);
            }

            if (noMagicStates.TryGetValue(pos, out var isNoMagic) && isNoMagic)
            {
                PaintNoMagicArea(g, bounds);
            }

            // Обработка уровней затемнённости
            if (darkeningLevels.TryGetValue(pos, out var darkeningLevel))
            {
                switch (darkeningLevel)
                {
                    case Lighting.Light:
                        break; // Ничего не делаем, нормальная затемнённость
                    case Lighting.Dark:
                        PaintDarkArea(g, bounds);
                        break;
                    case Lighting.Darkness:
                        PaintDarkeningArea(g, bounds);
                        break;
                }
            }

            // Получаем текущее состояние границы клетки
            if (borders.TryGetValue(pos, out var edgeTypes))
            {
                // Проверка материалов стенок
                for (int i = 0; i < 4; i++)
                {
                    string borderType;
                    Direction dir = (Direction)i;

                    switch (i)
                    {
                        case 0:
                            borderType = edgeTypes.Top;
                            break;
                        case 1:
                            borderType = edgeTypes.Bottom;
                            break;
                        case 2:
                            borderType = edgeTypes.Left;
                            break;
                        case 3:
                            borderType = edgeTypes.Right;
                            break;
                        default:
                            throw new InvalidOperationException("Unexpected tuple item");
                    }

                    switch (borderType)
                    {
                        case "Кирпичная стена":
                            DrawBrickWall(g, bounds, dir);
                            break;
                        case "Каменная стена":
                            DrawStoneWall(g, bounds, dir);
                            break;
                        case "Еловый лес":
                            DrawForestFir(g, bounds, dir);
                            break;
                        case "Еловый лес(снег)":
                            DrawForestSnowFir(g, bounds, dir);
                            break;
                        case "Дубовый лес":
                            DrawForestOak(g, bounds, dir);
                            break;
                        case "Дубовый лес(снег)":
                            DrawForestSnowOak(g, bounds, dir);
                            break;
                        case "Горы":
                            DrawMountains(g, bounds, dir);
                            break;
                        case "Горы (снег)":
                            DrawSnowMountains(g, bounds, dir);
                            break;
                        case "Вода":
                            DrawWater(g, bounds, dir);
                            break;
                        case "Пустыня":
                            DrawDesert(g, bounds, dir);
                            break;
                        case "Болото":
                            DrawSwamp(g, bounds, dir);
                            break;
                        case "Барьер":
                            Draw_Barrier_Word(g, bounds, dir);
                            break;
                        default:
                            // Логика по умолчанию или игнорирование
                            break;
                    }
                }

                // Проверка и отрисовка "Секрет"
                if (passageDict.TryGetValue(pos, out var passageData))
                {
                    // Проверяем каждое направление и рисуем тайник, если он найден
                    if (passageData.Top == 3) // секретный проход сверху
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Top, Direction.Top);
                    }
                    if (passageData.Bottom == 3) //  секретный проход снизу
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Bottom, Direction.Bottom);
                    }
                    if (passageData.Left == 3) //  секретный проход слева
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Left, Direction.Left);
                    }
                    if (passageData.Right == 3) //  секретный проход справа
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Right, Direction.Right);
                    }

                    // Остальные элементы (двери и решётки) остались прежними
                    if (passageData.Top == 1) // Дверь сверху
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Top, Direction.Top);
                    }
                    else if (passageData.Top == 2) // Решётка сверху
                    {
                        DrawRoundedGrate(g, bounds, Direction.Top);
                    }

                    if (passageData.Bottom == 1) // Дверь снизу
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Bottom, Direction.Bottom);
                    }
                    else if (passageData.Bottom == 2) // Решётка снизу
                    {
                        DrawRoundedGrate(g, bounds, Direction.Bottom);
                    }

                    if (passageData.Left == 1) // Дверь слева
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Left, Direction.Left);
                    }
                    else if (passageData.Left == 2) // Решётка слева
                    {
                        DrawRoundedGrate(g, bounds, Direction.Left);
                    }

                    if (passageData.Right == 1) // Дверь справа
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Right, Direction.Right);
                    }
                    else if (passageData.Right == 2) // Решётка справа
                    {
                        DrawRoundedGrate(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка лестниц вверх
                    if (passageData.Top == 4) // Лестница вверх сверху
                    {
                        DrawStairsUp(g, bounds, Direction.Top);
                    }
                    if (passageData.Bottom == 4) // Лестница вверх снизу
                    {
                        DrawStairsUp(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Left == 4) // Лестница вверх слева
                    {
                        DrawStairsUp(g, bounds, Direction.Left);
                    }
                    if (passageData.Right == 4) // Лестница вверх справа
                    {
                        DrawStairsUp(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка лестниц вниз
                    if (passageData.Top == 5) // Лестница вниз сверху
                    {
                        DrawStairsDown(g, bounds, Direction.Top);
                    }
                    if (passageData.Bottom == 5) // Лестница вниз снизу
                    {
                        DrawStairsDown(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Left == 5) // Лестница вниз слева
                    {
                        DrawStairsDown(g, bounds, Direction.Left);
                    }
                    if (passageData.Right == 5) // Лестница вниз справа
                    {
                        DrawStairsDown(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка портала
                    if (passageData.Top == 6) // Портал сверху
                    {
                        DrawPortal(g, bounds, Direction.Top);
                    }
                    if (passageData.Bottom == 6) // Портал снизу
                    {
                        DrawPortal(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Left == 6) // Портал слева
                    {
                        DrawPortal(g, bounds, Direction.Left);
                    }
                    if (passageData.Right == 6) // Портал справа
                    {
                        DrawPortal(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка надписи "Выход"
                    if (passageData.Top == 7) // Выход сверху
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Top == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Top, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Top, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Top, Color.LightSkyBlue);
                    }
                    if (passageData.Bottom == 7) // Выход снизу
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Bottom == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Bottom, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Bottom, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Bottom, Color.LightSkyBlue);
                    }
                    if (passageData.Left == 7) // Выход слева
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Left == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Left, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Left, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Left, Color.LightSkyBlue);
                    }
                    if (passageData.Right == 7) // Выход справа
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Right == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Right, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Right, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Right, Color.LightSkyBlue);
                    }
                }
            }

            // Центральный объект клетки
            if (centralOptions.TryGetValue(pos, out var centralOption))
            {
                if (_objectsData.TryGetValue(centralOption, out JObject obj))
                {
                    int leftMargin = obj["LeftMargin"].ToObject<int>();
                    int rightMargin = obj["RightMargin"].ToObject<int>();
                    int brightnessLimit = obj["FilterLevel"].ToObject<int>();

                    // Получаем изображение из IconBase64
                    string iconBase64 = obj["IconBase64"]?.ToString() ?? "";
                    Image icon = null;
                    if (!string.IsNullOrEmpty(iconBase64))
                    {
                        byte[] bytes = Convert.FromBase64String(iconBase64);
                        using (MemoryStream stream = new MemoryStream(bytes))
                        {
                            icon = Image.FromStream(stream);
                        }
                    }

                    if (centralOption == "Пустота") { }
                    else if (centralOption == "Не исследовано")
                        DrawUnexplored(g, bounds, pos);
                    else
                    {
                        // Если изображение доступно, создаем массив пикселей
                        int[,] bodyPixels = null;
                        if (icon != null)
                        {
                            bodyPixels = ConvertImageToPixelArray(icon);

                            // Передаем параметры в DrawCentralCell

                            DrawCentralCell(g, bounds, leftMargin, rightMargin, brightnessLimit, bodyPixels);
                        }
                    }
                }
                else
                {
                    // Если центральный объект не найден, рисуем вопросительный знак
                    Font questionFont = new Font("Arial", 24, FontStyle.Bold);
                    Brush questionBrush = centralOption == "AnyObjectSpec"
                        ? Brushes.LightSkyBlue
                        : centralOption == "AnyObject"
                            ? Brushes.White
                            : Brushes.Yellow;

                    g.DrawString("?", questionFont, questionBrush, new PointF(
            bounds.X + bounds.Width / 2, // центр по горизонтали
            bounds.Y + bounds.Height / 2 + 1 // центр по вертикали + смещение вниз на 1 пиксель
                                           ), new StringFormat()
                                           {
                                               Alignment = StringAlignment.Center,
                                               LineAlignment = StringAlignment.Center
                                           });
                }
            }

            // Визуализация значков сообщений на клетке
            if (messageStates.TryGetValue(pos, out var messages))
            {
                if (messages.Top) // topMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.Right - 7, bounds.Y));
                }
                if (messages.Bottom) // bottomMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.X, bounds.Bottom - 7));
                }
                if (messages.Left) // leftMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.X, bounds.Y));
                }
                if (messages.Right) // rightMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.Right - 7, bounds.Bottom - 7));
                }
            }

        }

        // Вспомогательный метод для преобразования изображения в массив пикселей
        private int[,] ConvertImageToPixelArray(Image image)
        {
            int width = image.Width;
            int height = image.Height;
            int[,] pixels = new int[width, height];

            using (Bitmap bitmap = new Bitmap(image))
            {
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        pixels[x, y] = pixel.ToArgb(); // Преобразуем цвет в целое число
                    }
                }
            }

            return pixels;
        }

        private void UpdatePreview()
        {
            if (!selectedPosition.HasValue)
            {
                magnifierPictureBox.Image = null;
                return;
            }

            Point pos = selectedPosition.Value;

            // Готовим временный битмап для предварительного просмотра
            Bitmap tempBitmap = new Bitmap(CellSize, CellSize);
            using (Graphics g = Graphics.FromImage(tempBitmap))
            {
                PrepareCellImage(pos, g, new Rectangle(0, 0, CellSize, CellSize));
            }

            // Масштабируем изображение для увеличения
            magnifierPictureBox.Image = ScaleImage(tempBitmap, magnifierPictureBox.ClientSize.Width, magnifierPictureBox.ClientSize.Height);
        }

        private Image ScaleImage(Image image, int maxWidth, int maxHeight)
        {
            double ratioX = (double)maxWidth / image.Width;
            double ratioY = (double)maxHeight / image.Height;
            double ratio = Math.Min(ratioX, ratioY);

            int newWidth = Convert.ToInt32(image.Width * ratio);
            int newHeight = Convert.ToInt32(image.Height * ratio);

            Bitmap newImage = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(newImage))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(image, 0, 0, newWidth, newHeight);
            }

            return newImage;
        }

        private void DrawDoor(Graphics g, Rectangle bounds, Direction direction)
        {
            int doorWidth = (bounds.Width / 3) + 1; // Ширина двери треть от всей ячейки
            int doorHeight = (bounds.Height / 6) + 1; // Высоту уменьшаем вдвое

            // Создаем временное изображение двери
            Bitmap doorImage = new Bitmap(doorWidth, doorHeight);
            using (Graphics imgG = Graphics.FromImage(doorImage))
            {
                // Рисунок двери
                Rectangle doorRect = new Rectangle(0, 0, doorWidth, doorHeight);
                imgG.FillRectangle(Brushes.Brown, doorRect); // Основание двери

                // Вертикальные черные полосы
                int numStripes = 2;
                int stripeWidth = doorWidth / 10;
                int gapBetweenStripes = ((doorWidth - (numStripes + 1) * stripeWidth) / ((numStripes + 1) + 1)) + 2;

                int startX = gapBetweenStripes;
                for (int i = 0; i < numStripes; i++)
                {
                    int currentX = startX + i * (stripeWidth + gapBetweenStripes);
                    imgG.FillRectangle(Brushes.Black, currentX, 0, stripeWidth, doorHeight + 1); // Чёрные полосы
                }

                // Горизонтальные стальные накладки
                int steelPlateWidth = (doorWidth / 5) - 1;
                int gapAboveBelow = (doorHeight / 4) + 2;

                imgG.FillRectangle(Brushes.SteelBlue, 0, gapAboveBelow + 1, doorWidth + 1, steelPlateWidth); // Верхняя накладка
                imgG.FillRectangle(Brushes.SteelBlue, 0, doorHeight - gapAboveBelow - steelPlateWidth - 1, doorWidth + 1, steelPlateWidth); // Нижняя накладка

                // Добавляем дверную ручку справа под верхней накладкой
                int handleX = doorWidth - 2; // Немного отступаем от края
                int handleY = gapAboveBelow + steelPlateWidth + 1; // Ниже верхней накладки
                imgG.FillRectangle(Brushes.Black, handleX, handleY, 1, 1); // Единая ручка размером 1x1 пиксель

            }

            // Рассчитываем положение двери на холсте
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - doorWidth / 2, bounds.Y, doorWidth, doorHeight);
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(bounds.X + bounds.Width / 2 - doorWidth / 2, bounds.Bottom - doorHeight, doorWidth, doorHeight);
                    break;
                case Direction.Left:
                    // Поворачиваем дверь на 90 градусов против часовой стрелки
                    doorImage.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    targetRect = new Rectangle(bounds.X, (bounds.Y + bounds.Height / 2 - doorHeight / 2) - 4, doorHeight, doorWidth);
                    break;
                case Direction.Right:
                    // Поворачиваем дверь на 90 градусов по часовой стрелке
                    doorImage.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    targetRect = new Rectangle(bounds.Right - doorHeight, (bounds.Y + bounds.Height / 2 - doorHeight / 2) - 4, doorHeight, doorWidth);
                    break;
                default:
                    return; // Неверное направление
            }

            // Выводим итоговое изображение двери на экран
            g.DrawImage(doorImage, targetRect);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SubscribeEvents(); // Подписываемся на события
        }

        private void SubscribeEvents()
        {
            // Регистрация обработчиков для обычного набора комбобоксов
            topComboBox.SelectionChangeCommitted += WallComboBox_SelectionChanged;
            bottomComboBox.SelectionChangeCommitted += WallComboBox_SelectionChanged;
            leftComboBox.SelectionChangeCommitted += WallComboBox_SelectionChanged;
            rightComboBox.SelectionChangeCommitted += WallComboBox_SelectionChanged;

            // Регистрация обработчиков для второго набора комбобоксов (проходов)
            passTopComboBox.SelectionChangeCommitted += PassageComboBox_SelectionChanged;
            passBottomComboBox.SelectionChangeCommitted += PassageComboBox_SelectionChanged;
            passLeftComboBox.SelectionChangeCommitted += PassageComboBox_SelectionChanged;
            passRightComboBox.SelectionChangeCommitted += PassageComboBox_SelectionChanged;

            // Регистрация обработчиков для центрального комбобокса (телаCenterComboBox_SelectedIndexChanged ячейки)
            centerComboBox.SelectionChangeCommitted += CenterComboBox_SelectedIndexChanged;
        }

        private void InitializeAllCells()
        {
            foreach (var key in gridButtons)
            {
                var position = GetPositionFromControl(key);

                // Инициализация значения центра ячейки
                centralOptions[position] = "Не исследовано";

                // Инициализация состояний границ (старых чекбоксов)
                closedStates[position] = new SideValues<bool>(false, false, false, false);

                // Инициализация состояний новых чекбоксов (текстов)
                messageStates[position] = new SideValues<bool>(false, false, false, false);

                // Инициализация прочих необходимых данных
                borders[position] = new SideValues<string>("Пустота", "Пустота", "Пустота", "Пустота");
                passageDict[position] = new SideValues<int>(0, 0, 0, 0);

                notesPerCell[position] = "";
                imagesPerCell[position] = null;

                isDangerStates[position] = false;
                noMagicStates[position] = false;

                // По умолчанию устанавливаем затемнённость "Светло"
                darkeningLevels[position] = Lighting.Light;

                //   key.Invalidate();
            }
        }

        private void CenterComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                string previousCentralOption = centralOptions.TryGetValue(pos, out var prev) ? prev : "";

                // Обновляем значение в словаре
                centralOptions[pos] = centerComboBox.SelectedItem.ToString();

                // Проверка на изменение
                bool hasChanged = previousCentralOption != centralOptions[pos];

                if (hasChanged)
                {
                    // Получаем соответствующую кнопку по текущей позиции
                    Button correspondingButton = gridButtons[pos.X, GridSize - 1 - (pos.Y)];

                    // Инвалидация кнопки приведет к повторному вызову метода Paint
                    correspondingButton.Invalidate();

                    // Можно обновить UI или любые другие действия
                    UpdatePreview(); // Обновляем предпросмотр, если нужно

                    isMapModified = true;
                }

            }
        }

        private void DrawEnvelope(Graphics g, Rectangle bounds, Point location)
        {
            // Шаблон конверта размером 7x7 пикселей
            char[][] envelopeTemplate = new char[][]
            {
        new char[] {'.', '.', '.', '.', '.', '.', '.'},
        new char[] {'.', ';', ';', ';', ';', ';', '.'},
        new char[] {'.', 'x', ';', ';', ';', 'x', '.'},
        new char[] {'.', ';', 'x', ';', 'x', ';', '.'},
        new char[] {'.', ';', ';', 'x', ';', ';', '.'},
        new char[] {'.', ';', ';', ';', ';', ';', '.'},
        new char[] {'.', '.', '.', '.', '.', '.', '.'}
            };

            // Стартовая позиция конверта
            int xStart = location.X;
            int yStart = location.Y;

            // Проходим по каждому элементу шаблона и рисуем пиксели
            for (int y = 0; y < envelopeTemplate.Length; y++)
            {
                for (int x = 0; x < envelopeTemplate[y].Length; x++)
                {
                    Color pixelColor;
                    switch (envelopeTemplate[y][x])
                    {
                        case '.': pixelColor = Color.Black; break;
                        case ';': pixelColor = Color.FromArgb(185, 137, 90); break;
                        //  case 'x': pixelColor = Color.White; break;
                        default: pixelColor = Color.Black; break;
                    }

                    // Заполняем пиксел цветом
                    g.FillRectangle(new SolidBrush(pixelColor), xStart + x, yStart + y, 1, 1);
                }
            }
        }

        ////// ниже все методы связанные с отрисовкой кирпичной и каменных стен

        private void UpAndDownDrawBrickWall(Graphics g, Rectangle bounds, Direction direction)
        {
            int brickWidth = bounds.Width / 4;
            int brickHeight = bounds.Height / 6;
            Rectangle finalBounds = bounds;

            switch (direction)
            {
                case Direction.Top:
                    break;
                case Direction.Bottom:
                    finalBounds.Offset(0, bounds.Height - brickHeight - 1);
                    break;
            }

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    int offsetX = 0;
                    int adjustedBrickWidth = brickWidth;

                    if (row == 1)
                    {
                        if (col == 0)
                        {
                            adjustedBrickWidth = brickWidth / 2;
                        }
                        else if (col == 4)
                        {
                            adjustedBrickWidth = brickWidth / 2;
                            offsetX -= brickWidth / 2;
                        }
                        else
                        {
                            offsetX = -(brickWidth / 2);
                        }
                    }

                    if ((row != 1) && (col >= 4))
                    {
                        continue;
                    }

                    int scaledBrickHeight = brickHeight / 3;

                    Rectangle brickRect = new Rectangle(
                        finalBounds.X + col * brickWidth + offsetX,
                        finalBounds.Y + row * scaledBrickHeight,
                        adjustedBrickWidth,
                        scaledBrickHeight
                    );

                    g.FillRectangle(Brushes.LightGray, brickRect);
                    g.DrawRectangle(Pens.DarkGray, brickRect);
                }
            }
        }


        private void LeftAndRightDrawBrickWall(Graphics g, Rectangle bounds, Direction direction)
        {
            int brickWidth = (bounds.Width / 6) - 2;
            int brickHeight = bounds.Height / 20;

            Rectangle finalBounds = bounds;

            switch (direction)
            {
                case Direction.Left:
                    finalBounds.Size = new Size(brickWidth * 3, bounds.Height);
                    finalBounds.Offset(0, 0);
                    break;

                case Direction.Right:
                    finalBounds.Size = new Size(brickWidth * 3, bounds.Height);
                    finalBounds.Offset(bounds.Width - 1 - brickWidth * 3 / 2, 0);
                    break;
            }

            for (int row = 0; row < 20; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    int offsetX = 0;
                    int adjustedBrickWidth = brickWidth;

                    if (row % 2 == 1)
                    {
                        if (col == 0)
                        {
                            adjustedBrickWidth = brickWidth / 2;
                        }
                        else if (col == 2)
                        {
                            adjustedBrickWidth = brickWidth / 2;
                            offsetX -= brickWidth / 2;
                        }
                        else if (col == 3)
                        {
                            adjustedBrickWidth = brickWidth / 2;
                            offsetX -= brickWidth / 2;
                        }
                        else
                        {
                            offsetX = -(brickWidth / 2);
                        }
                    }
                    else
                    {
                        if (col % 2 == 1) adjustedBrickWidth = brickWidth / 2;
                    }

                    Rectangle brickRect = new Rectangle(
                        finalBounds.X + col * brickWidth + offsetX,
                        finalBounds.Y + row * brickHeight,
                        adjustedBrickWidth,
                        brickHeight
                    );

                    g.FillRectangle(Brushes.LightGray, brickRect);
                    g.DrawRectangle(Pens.DarkGray, brickRect);
                }
            }
        }

        private void DrawSwamp(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.swamp :
                direction == Direction.Left ? Border_Pixels_Patterns.swamp :
                direction == Direction.Right ? Border_Pixels_Patterns.swamp :
                Border_Pixels_Patterns.swamp;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {2, Color.FromArgb(255, 0x17, 0x78, 0x33)}, // 
        {1, Color.FromArgb(255, 0x22, 0xB1, 0x4C)}, //
        {3, Color.FromArgb(255, 0xB8, 0x5B, 0x1C)},
        {4, Color.FromArgb(255, 0x1C, 0x94, 0x3F)},//
        {6, Color.FromArgb(255, 0xFF, 0xF2, 0x00)},
        {5, Color.FromArgb(255, 0xFF, 0xFF, 0xFF)}
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }
            if (direction == Direction.Bottom)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate180FlipY); // 
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawDesert(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.desert :
                direction == Direction.Left ? Border_Pixels_Patterns.desert :
                direction == Direction.Right ? Border_Pixels_Patterns.desert :
                Border_Pixels_Patterns.desert;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.FromArgb(255, 0xFF, 0x7F, 0x27)}, // 
        {2, Color.FromArgb(255, 0x22, 0xB1, 0x4C)}, //
        {3, Color.FromArgb(255, 0xFF, 0xB7, 0x71)},
        {4, Color.FromArgb(255, 0xB8, 0x5B, 0x1C) }//
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }
            if (direction == Direction.Bottom)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate180FlipY); // 
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawWater(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.water :
                direction == Direction.Left ? Border_Pixels_Patterns.water :
                direction == Direction.Right ? Border_Pixels_Patterns.water :
                Border_Pixels_Patterns.water;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.FromArgb(255, 0x3F, 0x48, 0xCC)}, // 
        {3, Color.FromArgb(255, 0x89, 0x8B, 0xFA)}, //
        {2, Color.FromArgb(255, 0x2C, 0x32, 0x8F)} //
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }
            if (direction == Direction.Bottom)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate180FlipY); // 
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawForestFir(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.fir :
                direction == Direction.Left ? Border_Pixels_Patterns.fir :
                direction == Direction.Right ? Border_Pixels_Patterns.fir :
                Border_Pixels_Patterns.fir;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.ForestGreen}, // 
        {2, Color.Brown}, // 
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawForestOak(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.oak :
                direction == Direction.Left ? Border_Pixels_Patterns.oak :
                direction == Direction.Right ? Border_Pixels_Patterns.oak :
                Border_Pixels_Patterns.oak;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.ForestGreen}, // 
        {2, Color.Brown}, // 
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawMountains(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.mountains :
                direction == Direction.Left ? Border_Pixels_Patterns.mountains :
                direction == Direction.Right ? Border_Pixels_Patterns.mountains :
                Border_Pixels_Patterns.mountains;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.FromArgb(201, 100, 31)}, // 
        {2, Color.FromArgb(150, 75, 23)}, // 
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawSnowMountains(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.mountains_snow :
                direction == Direction.Left ? Border_Pixels_Patterns.mountains_snow :
                direction == Direction.Right ? Border_Pixels_Patterns.mountains_snow :
                Border_Pixels_Patterns.mountains_snow;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.FromArgb(201, 100, 31)}, // 
        {2, Color.FromArgb(150, 75, 23)}, // 
        {3, Color.Snow}, //
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawForestSnowFir(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.fir_snow :
                direction == Direction.Left ? Border_Pixels_Patterns.fir_snow :
                direction == Direction.Right ? Border_Pixels_Patterns.fir_snow :
                Border_Pixels_Patterns.fir_snow;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.ForestGreen}, // 
        {2, Color.Brown}, // 
                {3, Color.Snow }
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawForestSnowOak(Graphics g, Rectangle bounds, Direction direction)
        {

            // Выбор массива в зависимости от направления
            int[,] firPatternToUse = direction == Direction.Top ? Border_Pixels_Patterns.oak_snow :
                direction == Direction.Left ? Border_Pixels_Patterns.oak_snow :
                direction == Direction.Right ? Border_Pixels_Patterns.oak_snow :
                Border_Pixels_Patterns.oak_snow;

            // Цветовая палитра
            Dictionary<int, Color> colorMap = new Dictionary<int, Color>()
    {
        {0, Color.FromArgb(0,0, 0, 0) },               // Пустой пиксель
        {1, Color.ForestGreen}, // 
        {2, Color.Brown}, // 
        {3, Color.Snow}, // 
    };

            // Рендер картинки
            Bitmap exitWordBitmap = new Bitmap(firPatternToUse.GetLength(1), firPatternToUse.GetLength(0));
            using (Graphics stairsGraphics = Graphics.FromImage(exitWordBitmap))
            {
                // Отрисовка картинки на временное изображение
                for (int y = 0; y < firPatternToUse.GetLength(0); y++)
                {
                    for (int x = 0; x < firPatternToUse.GetLength(1); x++)
                    {
                        int value = firPatternToUse[y, x];
                        if (colorMap.ContainsKey(value))
                        {
                            stairsGraphics.FillRectangle(new SolidBrush(colorMap[value]), x, y, 1, 1);
                        }
                    }
                }
            }

            if (direction == Direction.Left)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipXY); // Поворот на 90 градусов
            }
            if (direction == Direction.Right)
            {
                exitWordBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); // Поворот на 270 градусов
            }

            // Размещение изображения
            Rectangle targetRect;
            switch (direction)
            {
                case Direction.Top:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                          // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Bottom:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Bottom - exitWordBitmap.Height,                // начало снизу
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Left:
                    targetRect = new Rectangle(
                        bounds.X,
                        bounds.Y,                                       // начало сверху
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                case Direction.Right:
                    targetRect = new Rectangle(
                        bounds.Right - exitWordBitmap.Width,                // начало справа
                        bounds.Y,
                        exitWordBitmap.Width, exitWordBitmap.Height
                    );
                    break;
                default:
                    return; // недопустимое направление
            }

            // Отрисовка картинки
            g.DrawImage(exitWordBitmap, targetRect);
        }

        private void DrawBrickWall(Graphics g, Rectangle bounds, Direction direction)
        {
            switch (direction)
            {
                case Direction.Top:
                case Direction.Bottom:
                    UpAndDownDrawBrickWall(g, bounds, direction);
                    break;

                case Direction.Left:
                case Direction.Right:
                    LeftAndRightDrawBrickWall(g, bounds, direction);
                    break;
            }
        }

        // Добавляем переменную для временного хранения полного изображения узора
        private Bitmap fullStoneWallPattern;

        private void AddMossSpots(Graphics g, Rectangle bounds, Rectangle segmentBounds, Direction direction)
        {
            Random random = new Random();

            // Количество пятен мха на участке стены
            int mossSpotCount = random.Next(5, 20);

            for (int i = 0; i < mossSpotCount; i++)
            {
                // Случайные координаты центра пятна внутри области стены
                float x = random.Next(segmentBounds.X, segmentBounds.Right);
                float y = random.Next(segmentBounds.Y, segmentBounds.Bottom);

                // Определяем оптимальный радиус пятна
                float initialRadius = random.Next(1, 3); // Маленькие пятна для лучшего восприятия

                // Прямоугольник для анализа пересечения
                RectangleF ellipseRect = new RectangleF(x - initialRadius, y - initialRadius, initialRadius * 2, initialRadius * 2);

                // Формирование пути для нужной формы
                GraphicsPath path = new GraphicsPath();

                // Обрабатываем возможные пересечения границ
                if (ellipseRect.IntersectsWith(segmentBounds))
                {
                    // Вершина (top) и дно (bottom) отличаются углом отсчёта
                    if (direction == Direction.Top)
                    {
                        path.AddArc(ellipseRect, 0, 180); // Полукруг сверху
                    }
                    else if (direction == Direction.Bottom)
                    {
                        path.AddArc(ellipseRect, 180, 180); // Полукруг снизу
                    }
                    else if (direction == Direction.Left)
                    {
                        path.AddArc(ellipseRect, 90, 180); // Полукруг слева
                    }
                    else if (direction == Direction.Right)
                    {
                        path.AddArc(ellipseRect, 270, 180); // Полукруг справа
                    }
                    else
                    {
                        // Если пятно полностью внутри, используем полный круг
                        path.AddEllipse(ellipseRect);
                    }
                }
                else
                {
                    // Если пятно не пересекает границы, рисуем полный круг
                    path.AddEllipse(ellipseRect);
                }

                // Заливка с эффектом натуральных оттенков
                using (Brush brush = new SolidBrush(Color.FromArgb(128, 0, 100, 0)))
                {
                    g.FillPath(brush, path);
                }

                // Границы контура (не обязательно)
                using (Pen pen = new Pen(Color.Green))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        private void DrawStoneWall(Graphics g, Rectangle bounds, Direction direction)
        {
            // Генерируем узор единожды и храним его
            if (fullStoneWallPattern == null)
            {
                fullStoneWallPattern = new Bitmap(bounds.Width, bounds.Height);

                using (Graphics patternGraphics = Graphics.FromImage(fullStoneWallPattern))
                {
                    DrawFullStoneWall(patternGraphics, bounds, direction);
                }
            }

            // Основной сегмент узора
            Rectangle upperSegmentTop = new Rectangle(0, 0, bounds.Width, (bounds.Height / 6) + 1);
            Rectangle upperSegmentBottom = new Rectangle(0, 1, bounds.Width, (bounds.Height / 6) + 1);
            Rectangle upperSegmentLeft = new Rectangle(0, -4, bounds.Width, (bounds.Height / 6) + 4);
            Rectangle upperSegmentRight = new Rectangle(0, 2, bounds.Width, (bounds.Height / 6) + 1);

            // Матрица вращения для некоторых направлений
            Matrix rotationMatrix = new Matrix();

            // Первая задача — рисуем саму стену
            if (direction == Direction.Top)
            {
                // Верхняя часть (серый фон и узор сверху)
                g.FillRectangle(Brushes.Gray, bounds.X, bounds.Y, bounds.Width, (bounds.Height / 6));
                g.DrawImage(fullStoneWallPattern, bounds.X, bounds.Y, upperSegmentTop, GraphicsUnit.Pixel);
            }
            else if (direction == Direction.Bottom)
            {
                // Нижняя часть (серый фон и узор снизу)
                g.FillRectangle(Brushes.Gray, bounds.X, bounds.Bottom - 1 - bounds.Height / 6, bounds.Width, (bounds.Height / 6) + 1);
                g.DrawImage(fullStoneWallPattern, bounds.X, bounds.Bottom - 1 - bounds.Height / 6, upperSegmentBottom, GraphicsUnit.Pixel);
            }
            else if (direction == Direction.Right)
            {
                // Режим Right: узор помещаем справа
                rotationMatrix.RotateAt(90, new PointF(bounds.Width / 2, bounds.Height / 2));
                g.Transform = rotationMatrix;
                g.DrawImage(fullStoneWallPattern, bounds.Right - bounds.Height, bounds.Y, upperSegmentRight, GraphicsUnit.Pixel);
            }
            else if (direction == Direction.Left)
            {
                // Режим Left: узор помещаем слева
                rotationMatrix.RotateAt(90, new PointF(bounds.Width / 2, bounds.Height / 2));
                g.Transform = rotationMatrix;
                g.DrawImage(fullStoneWallPattern, bounds.Right - bounds.Height, bounds.Y + 30, upperSegmentLeft, GraphicsUnit.Pixel);
            }

            // Возвращаемся к исходной трансформации
            g.ResetTransform();

            // Создание сегмента для направления
            Rectangle segmentBounds;

            switch (direction)
            {
                case Direction.Top:
                    segmentBounds = new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height / 6);
                    break;
                case Direction.Bottom:
                    segmentBounds = new Rectangle(bounds.X, bounds.Bottom + 1 - bounds.Height / 6, bounds.Width, (bounds.Height / 6) - 1);
                    break;
                case Direction.Left:
                    segmentBounds = new Rectangle(bounds.X, bounds.Y, (bounds.Width / 6) + 1, bounds.Height);
                    break;
                case Direction.Right:
                    segmentBounds = new Rectangle(bounds.Right - 1 - bounds.Width / 6, bounds.Y, (bounds.Width / 6) + 1, bounds.Height);
                    break;
                default:
                    throw new ArgumentException("Invalid direction");
            }

            // Наконец, добавляем мох НА ПОВЕРХНОСТЬ СТЕНЫ
            AddMossSpots(g, bounds, segmentBounds, direction);
        }

        // Метод для генерации всего узора камня
        private void DrawFullStoneWall(Graphics g, Rectangle bounds, Direction direction)
        {
            Random random = new Random();
            int rows = 6;           // Количество рядов
            int columns = 10;       // Количество колонн
            float overlapFactor = 0.45f; // Степень перекрытия между камнями

            // Средние размеры ячеек
            float avgCellWidth = bounds.Width / columns;
            float avgCellHeight = bounds.Width / rows;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    // Смещаем центры камней случайным образом
                    float x = bounds.X + c * avgCellWidth + random.NextFloat(-avgCellWidth * overlapFactor, avgCellWidth * overlapFactor);
                    float y = bounds.Y + r * avgCellHeight + random.NextFloat(-avgCellHeight * overlapFactor, avgCellHeight * overlapFactor);

                    // Размеры камней варьируют в небольшом диапазоне
                    float stoneWidth = random.NextFloat(avgCellWidth * 2.6f, avgCellWidth * 3.2f);
                    float stoneHeight = random.NextFloat(avgCellHeight * 2.6f, avgCellHeight * 3.2f);

                    // Формируем полигон произвольной формы
                    PointF[] points = GenerateIrregularShape(random, stoneWidth, stoneHeight);

                    // Преобразование координат полигона
                    GraphicsPath path = new GraphicsPath();
                    path.AddPolygon(points);
                    Matrix translationMatrix = new Matrix();
                    translationMatrix.Translate(x, y);
                    path.Transform(translationMatrix);

                    // Рисуем полигон
                    g.FillPath(Brushes.Gray, path);
                    g.DrawPath(Pens.Black, path);
                }
            }
        }

        // Генерация нерегулярной формы камня
        private PointF[] GenerateIrregularShape(Random random, float width, float height)
        {
            int vertices = random.Next(5, 8); // Камни с разным числом вершин
            PointF[] points = new PointF[vertices];

            for (int i = 0; i < vertices; i++)
            {
                float angle = (float)i / vertices * 2 * (float)Math.PI;
                float radius = random.NextFloat(width * 0.3f, width * 0.7f); // Асимметричность формы
                float x = (float)(Math.Sin(angle) * radius);
                float y = (float)(Math.Cos(angle) * radius);
                points[i] = new PointF(x, y);
            }
            return points;
        }

        // Временная структура для хранения состояния ячейки
        private struct CopiedCellInfo
        {
            public SideValues<string> Borders;
            public SideValues<int> Passages;
            public SideValues<bool> ClosedStates;
            public SideValues<bool> Messages;
            public string CentralOption;
            public bool IsDanger;
            public bool NoMagic;
            public Lighting DarkeningLevel;
            public Image CellImage;
            public string Notes;
        };
    }

}
