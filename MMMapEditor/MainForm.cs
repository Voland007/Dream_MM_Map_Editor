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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Diagnostics;
using System.Xml;
using Newtonsoft.Json; // Необходим пакет Newtonsoft.Json для сериализации
using Newtonsoft.Json.Linq;
using IniParser;
using IniParser.Model;
using IniParser.Parser;

namespace MMMapEditor
{
    public partial class MainForm : Form
    {
        private const int GridSize = 16;
        private const int CellSize = 40;
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
        private Dictionary<Point, Tuple<int, int, int, int>> settingsDict = new Dictionary<Point, Tuple<int, int, int, int>>();
        private Point? selectedPosition = null; // Текущая выделенная позиция
        private Dictionary<Point, Tuple<string, string, string, string>> borders = new Dictionary<Point, Tuple<string, string, string, string>>();
        private Dictionary<Point, Tuple<int, int, int, int>> passageDict = new Dictionary<Point, Tuple<int, int, int, int>>();
        private Dictionary<Point, Tuple<bool, bool, bool, bool>> closedStates = new Dictionary<Point, Tuple<bool, bool, bool, bool>>();
        private Dictionary<Point, Tuple<bool, bool, bool, bool>> messageStates = new Dictionary<Point, Tuple<bool, bool, bool, bool>>();
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
        private RadioButton lightRadioButton, darkRadioButton, darknessRadioButton;
        private GroupBox lightingGroupBox;
        private Dictionary<Point, Lighting> lightingLevels = new Dictionary<Point, Lighting>();
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
                borders[pos] = copiedCellInfo.Value.Borders;
                passageDict[pos] = copiedCellInfo.Value.Passages;
                closedStates[pos] = copiedCellInfo.Value.ClosedStates;
                messageStates[pos] = copiedCellInfo.Value.Messages;
                centralOptions[pos] = copiedCellInfo.Value.CentralOption;
                isDangerStates[pos] = copiedCellInfo.Value.IsDanger;
                noMagicStates[pos] = copiedCellInfo.Value.NoMagic;
                lightingLevels[pos] = copiedCellInfo.Value.LightingLevel;
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
                    Borders = borders[pos],
                    Passages = passageDict[pos],
                    ClosedStates = closedStates[pos],
                    Messages = messageStates[pos],
                    CentralOption = centralOptions[pos],
                    IsDanger = isDangerStates[pos],
                    NoMagic = noMagicStates[pos],
                    LightingLevel = lightingLevels[pos],
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
            lightingLevels.Remove(pos);
            centralOptions.Remove(pos);

            // Дополнительно можно добавить восстановление дефолтных значений, если это требуется
            // Например:
            borders[pos] = new Tuple<string, string, string, string>("Пустота", "Пустота", "Пустота", "Пустота");
            passageDict[pos] = new Tuple<int, int, int, int>(0, 0, 0, 0);
            closedStates[pos] = new Tuple<bool, bool, bool, bool>(false, false, false, false);
            messageStates[pos] = new Tuple<bool, bool, bool, bool>(false, false, false, false);
            notesPerCell[pos] = "";
            imagesPerCell[pos] = null;
            isDangerStates[pos] = false;
            noMagicStates[pos] = false;
            lightingLevels[pos] = Lighting.Light;
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
            darknessRadioButton.Checked = false;

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

                // 1. Сначала сохраним текущее состояние закрытыъ дверей и сообщений ячейки
                Tuple<bool, bool, bool, bool>? previousClosedStates = closedStates.TryGetValue(pos, out var prevClosed) ? prevClosed : null;
                Tuple<bool, bool, bool, bool>? previousMessageStates = messageStates.TryGetValue(pos, out var prevMsg) ? prevMsg : null;

                CheckBox checkbox = (CheckBox)sender;
                bool checkedState = checkbox.Checked;

                // Индексация чекбоксов по порядку следования
                int index = -1;
                if (checkbox == topCheck) index = 0;
                else if (checkbox == bottomCheck) index = 1;
                else if (checkbox == leftCheck) index = 2;
                else if (checkbox == rightCheck) index = 3;
                else if (checkbox == topMessageCheck) index = 4;
                else if (checkbox == bottomMessageCheck) index = 5;
                else if (checkbox == leftMessageCheck) index = 6;
                else if (checkbox == rightMessageCheck) index = 7;

                if (index >= 0 && index <= 3)
                {
                    // Обновляем состояние закрытых границ
                    var existingClosed = closedStates.ContainsKey(pos)
                                            ? closedStates[pos]
                                            : new Tuple<bool, bool, bool, bool>(false, false, false, false);

                    var updatedClosed = index switch
                    {
                        0 => new Tuple<bool, bool, bool, bool>(checkedState, existingClosed.Item2, existingClosed.Item3, existingClosed.Item4),
                        1 => new Tuple<bool, bool, bool, bool>(existingClosed.Item1, checkedState, existingClosed.Item3, existingClosed.Item4),
                        2 => new Tuple<bool, bool, bool, bool>(existingClosed.Item1, existingClosed.Item2, checkedState, existingClosed.Item4),
                        3 => new Tuple<bool, bool, bool, bool>(existingClosed.Item1, existingClosed.Item2, existingClosed.Item3, checkedState),
                        _ => existingClosed
                    };

                    closedStates[pos] = updatedClosed;
                }
                else if (index >= 4 && index <= 7)
                {
                    // Обновляем состояние чекбоксов "Текст"
                    var existingMessages = messageStates.ContainsKey(pos)
                                              ? messageStates[pos]
                                              : new Tuple<bool, bool, bool, bool>(false, false, false, false);

                    var updatedMessages = index switch
                    {
                        4 => new Tuple<bool, bool, bool, bool>(checkedState, existingMessages.Item2, existingMessages.Item3, existingMessages.Item4),
                        5 => new Tuple<bool, bool, bool, bool>(existingMessages.Item1, checkedState, existingMessages.Item3, existingMessages.Item4),
                        6 => new Tuple<bool, bool, bool, bool>(existingMessages.Item1, existingMessages.Item2, checkedState, existingMessages.Item4),
                        7 => new Tuple<bool, bool, bool, bool>(existingMessages.Item1, existingMessages.Item2, existingMessages.Item3, checkedState),
                        _ => existingMessages
                    };

                    messageStates[pos] = updatedMessages;
                }

                Tuple<bool, bool, bool, bool> currentClosedStates = closedStates[pos];
                Tuple<bool, bool, bool, bool> currentMessageStates = messageStates[pos];


                // Теперь сравним сохранённые ранее значения с новыми
                bool hasChanged =
                    !previousClosedStates.Equals(currentClosedStates) ||
                    !previousMessageStates.Equals(currentMessageStates);

                if (hasChanged)
                     {
                    gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();

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

            lightingGroupBox = new GroupBox
            {
                Text = "Освещение",
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
            lightRadioButton.CheckedChanged += LightingRadioButton_CheckedChanged;

            darkRadioButton = new RadioButton
            {
                Text = "Темно",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 35)
            };
            darkRadioButton.CheckedChanged += LightingRadioButton_CheckedChanged;

            darknessRadioButton = new RadioButton
            {
                Text = "Мрак",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 55)
            };
            darknessRadioButton.CheckedChanged += LightingRadioButton_CheckedChanged;

            // Добавляем радиокнопки внутрь группы
            lightingGroupBox.Controls.Add(lightRadioButton);
            lightingGroupBox.Controls.Add(darkRadioButton);
            lightingGroupBox.Controls.Add(darknessRadioButton);


            topPanel.Controls.Add(centerLabel);
            topPanel.Controls.Add(centerComboBox);
            topPanel.Controls.Add(isDangerCheckBox);
            topPanel.Controls.Add(noMagicCheckBox);
            topPanel.Controls.Add(lightingGroupBox);

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
            ToolStripMenuItem draftLaboratoryItem = new ToolStripMenuItem("Draft_Laboratory");
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

            // Подписываемся на событие Click для вызова формы
            toolStripMenuItemManageObjects.Click += toolStripMenuItemManageObjects_Click;

            // Добавляем панель на главную форму
            Controls.Add(rightPanel);
            menuStrip.Items.Add(fileMenuItem);
            menuStrip.Items.Add(searchMenuItem);
            menuStrip.Items.Add(settingMenuItem);
            Controls.Add(menuStrip);
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
                dialog.Filter = "Text Files (*.txt)|*.txt";
                dialog.Title = "Select a text file to load as a laboratory draft";
                dialog.DefaultExt = ".txt";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string filename = dialog.FileName;
                    LoadDraftLaboratory(filename);
                }
            }
        }

        private void LoadDraftLaboratory(string filename)
        {
            // Считываем весь файл целиком
            string[] lines = File.ReadAllLines(filename);

            // Проверяем количество строк и наличие данных
            if (lines.Length != 32 || lines.Any(line => line.Split().Length != 16))
            {
                MessageBox.Show("Формат файла не соответствует ожидаемому.");
                return;
            }

            // Обрабатываем каждую клетку
            for (int y = 0; y < 16; y++)
            {
                string[] cellValuesFirstLayer = lines[y].Split();
                string[] cellValuesSecondLayer = lines[y + 16].Split();

                for (int x = 0; x < 16; x++)
                {
                    ProcessCellDraft(x, y, cellValuesFirstLayer[x], cellValuesSecondLayer[x]);
                }
            }

            // Обновляем внешний вид интерфейса
            foreach (var button in gridButtons)
            {
                button.Invalidate();
            }

            // Сообщаем пользователю о завершении процесса
            MessageBox.Show("Лаборатория успешно загружена.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ProcessCellDraft(int x, int y, string hexValueFirstLayer, string hexValueSecondLayer)
        {
            // Преобразуем шестнадцатеричное значение в десятичное
            int firstDecimalValue = Convert.ToInt32(hexValueFirstLayer, 16);
            int secondDecimalValue = Convert.ToInt32(hexValueSecondLayer, 16);

            // Получаем двоичное представление длины ровно 16 символов
            string firstBinaryRepresentation = Convert.ToString(firstDecimalValue, 2).PadLeft(16, '0');
            string secondBinaryRepresentation = Convert.ToString(secondDecimalValue, 2).PadLeft(16, '0');

            // Получаем позицию клетки
            Point pos = new Point(x, y);

            // Получаем текущие границы клетки
            Tuple<string, string, string, string> currentBorders = borders[pos];
            // Получаем текущие проходы клетки
            Tuple<int, int, int, int> currentPassages = passageDict[pos];
            // Получаем текущие состоянии и закрытых дверях
            Tuple<bool, bool, bool, bool> currentClosedStates = closedStates[pos];

            // Проверяем нужные позиции и устанавливаем стены соответственно
            if (firstBinaryRepresentation[^1] == '1') // самый младший бит
            { 
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, currentBorders.Item2, "Кирпичная стена", currentBorders.Item4); // стена слева
                if (secondBinaryRepresentation[^1] == '0')
                    currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, currentPassages.Item2, 3, currentPassages.Item4);
            }

            if (firstBinaryRepresentation[^3] == '1') // третий бит справа
            {
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, "Кирпичная стена", currentBorders.Item3, currentBorders.Item4); // стена снизу
                if (secondBinaryRepresentation[^3] == '0')
                    currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, 3, currentPassages.Item3, currentPassages.Item4);
            }

            if (firstBinaryRepresentation[^5] == '1') // пятый бит справа
            {
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, currentBorders.Item2, currentBorders.Item3, "Кирпичная стена"); // стена справа
                if (secondBinaryRepresentation[^5] == '0')
                    currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, currentPassages.Item2, currentPassages.Item3, 3);
            }

            if (firstBinaryRepresentation[^7] == '1') // седьмой бит справа
            {
                currentBorders = new Tuple<string, string, string, string>(
                    "Кирпичная стена", currentBorders.Item2, currentBorders.Item3, currentBorders.Item4); // стена сверху
                if (secondBinaryRepresentation[^7] == '0')
                    currentPassages = new Tuple<int, int, int, int>(
                    3, currentPassages.Item2, currentPassages.Item3, currentPassages.Item4);
            }

            // Дополнительные проверки для установки стен и проходов типа "дверь"
            if (firstBinaryRepresentation[^2] == '1' && firstBinaryRepresentation[^1] == '0')
            {
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, currentBorders.Item2, "Кирпичная стена", currentBorders.Item4); // Стена слева
                currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, currentPassages.Item2, 1, currentPassages.Item4); // Дверь слева
                if (secondBinaryRepresentation[^1] == '1')
                    currentClosedStates = new Tuple<bool, bool, bool, bool>(currentClosedStates.Item1, currentClosedStates.Item2, true, currentClosedStates.Item4);
            }

            if (firstBinaryRepresentation[^4] == '1' && firstBinaryRepresentation[^3] == '0')
            {
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, "Кирпичная стена", currentBorders.Item3, currentBorders.Item4); // Стена снизу
                currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, 1, currentPassages.Item3, currentPassages.Item4); // Дверь снизу
                if (secondBinaryRepresentation[^3] == '1')
                    currentClosedStates = new Tuple<bool, bool, bool, bool>(currentClosedStates.Item1, true, currentClosedStates.Item3, currentClosedStates.Item4);
            }

            if (firstBinaryRepresentation[^6] == '1' && firstBinaryRepresentation[^5] == '0')
            {
                currentBorders = new Tuple<string, string, string, string>(
                    currentBorders.Item1, currentBorders.Item2, currentBorders.Item3, "Кирпичная стена"); // Стена справа
                currentPassages = new Tuple<int, int, int, int>(
                    currentPassages.Item1, currentPassages.Item2, currentPassages.Item3, 1); // Дверь справа
                if (secondBinaryRepresentation[^5] == '1')
                    currentClosedStates = new Tuple<bool, bool, bool, bool>(currentClosedStates.Item1, currentClosedStates.Item2, currentClosedStates.Item3, true);
            }

            if (firstBinaryRepresentation[^8] == '1' && firstBinaryRepresentation[^7] == '0')
            {
                currentBorders = new Tuple<string, string, string, string>(
                    "Кирпичная стена", currentBorders.Item2, currentBorders.Item3, currentBorders.Item4); // Стена сверху
                currentPassages = new Tuple<int, int, int, int>(
                    1, currentPassages.Item2, currentPassages.Item3, currentPassages.Item4); // Дверь сверху
                if (secondBinaryRepresentation[^7] == '1')
                    currentClosedStates = new Tuple<bool, bool, bool, bool>(true, currentClosedStates.Item2, currentClosedStates.Item3, currentClosedStates.Item4);
            }

            //if (secondBinaryRepresentation[^8] == '1')
            //{
            //    centralOptions[pos] = "Случайная встреча";
            //}
            //else
            centralOptions[pos] = "Пустота";

            // Обновляем границы клетки
            borders[pos] = currentBorders;
            passageDict[pos] = currentPassages;
            closedStates[pos] = currentClosedStates;

            // Остальные параметры клеток устанавливаются стандартно
            messageStates[pos] = new Tuple<bool, bool, bool, bool>(false, false, false, false);
            notesPerCell[pos] = "";
            imagesPerCell[pos] = null;
            isDangerStates[pos] = false;
            noMagicStates[pos] = false;
            lightingLevels[pos] = Lighting.Light;
        }

        // Обработчик события пункта меню "Метаданные"
        private void MetadataItem_Click(object sender, EventArgs e)
        {
            MetadataForm metadataForm = new MetadataForm(mapSector, surface);
            if (metadataForm.ShowDialog() == DialogResult.OK)
            {
                mapSector = metadataForm.SelectedMapSector;
                surface = metadataForm.SelectedSurface;

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
                borders[pos] = new Tuple<string, string, string, string>(
                    (string)cellInfo.Borders.Item1, (string)cellInfo.Borders.Item2, (string)cellInfo.Borders.Item3, (string)cellInfo.Borders.Item4);

                // Проходы: приводим значения к целочисленным
                passageDict[pos] = new Tuple<int, int, int, int>(
                    (int)cellInfo.Passages.Item1, (int)cellInfo.Passages.Item2, (int)cellInfo.Passages.Item3, (int)cellInfo.Passages.Item4);

                // Закрытость: приводим значения к булевым
                closedStates[pos] = new Tuple<bool, bool, bool, bool>(
                    (bool)cellInfo.ClosedStates.Item1, (bool)cellInfo.ClosedStates.Item2, (bool)cellInfo.ClosedStates.Item3, (bool)cellInfo.ClosedStates.Item4);

                // Сообщения: приводим значения к булевым
                messageStates[pos] = new Tuple<bool, bool, bool, bool>(
                    (bool)cellInfo.Messages.Item1, (bool)cellInfo.Messages.Item2, (bool)cellInfo.Messages.Item3, (bool)cellInfo.Messages.Item4);

                // Центральный элемент
                centralOptions[pos] = (string)cellInfo.CentralOption;

                // Опасность и магия
                isDangerStates[pos] = (bool)cellInfo.IsDanger;
                noMagicStates[pos] = (bool)cellInfo.NoMagic;

                // Освещение: приводим значение к перечислению
                lightingLevels[pos] = (Lighting)Enum.Parse(typeof(Lighting), (string)cellInfo.Lighting);

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

                // Устанавливаем заголовок окна
                if (!string.IsNullOrEmpty(mapSector) && string.IsNullOrEmpty(surface))
                {
                    this.Text = $"{Path.GetFileNameWithoutExtension(filename)} - MAP SECTOR: {mapSector}";
                }
                else if (string.IsNullOrEmpty(mapSector) && !string.IsNullOrEmpty(surface))
                {
                    this.Text = $"{Path.GetFileNameWithoutExtension(filename)} - SURFACE: {surface}";
                }
                else if (!string.IsNullOrEmpty(mapSector) && !string.IsNullOrEmpty(surface))
                {
                    this.Text = $"{Path.GetFileNameWithoutExtension(filename)} - MAP SECTOR: {mapSector}    SURFACE: {surface}";
                }
                else
                {
                    this.Text = Path.GetFileNameWithoutExtension(filename);
                }
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

                    // Меняем заголовок окна на имя файла
                    this.Text = Path.GetFileNameWithoutExtension(filename);
                    lastSavedFilename = filename; // Запоминаем путь к файлу

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
                    ["Lighting"] = lightingLevels[pos],
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
                    // Сбрасываем метаданные
                    mapSector = "";
                    surface = "";
                    // Обновляем заголовок окна
                    this.Text = "Редактор моей мечты";
                    lastSavedFilename = "";

                    InitializeAllCells();
                    ResetForm();
                    UpdatePreview();
                    foreach (var button in gridButtons)
                    {
                        button.Invalidate(); // вызываем перерисовку для каждой кнопки
                    }
                    isMapModified = false;
                }
            }
            else
            {
                // Сбрасываем метаданные
                mapSector = "";
                surface = "";
                // Обновляем заголовок окна
                this.Text = "Редактор моей мечты";
                lastSavedFilename = "";
                InitializeAllCells();
                ResetForm();
                UpdatePreview();
                foreach (var button in gridButtons)
                {
                    button.Invalidate(); // вызываем перерисовку для каждой кнопки
                }
                isMapModified = false;
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

        private void LightingRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (selectedPosition.HasValue)
            {
                Point pos = selectedPosition.Value;
                Lighting previousLighting = lightingLevels.TryGetValue(pos, out var prev) ? prev : Lighting.Light;


                RadioButton rb = (RadioButton)sender;
                if (rb.Checked)
                {
                    // Обновляем освещение для выбранной ячейки
                    if (rb == lightRadioButton)
                    {
                        ApplyLightingEffect(pos, Lighting.Light);
                    }
                    else if (rb == darkRadioButton)
                    {
                        ApplyLightingEffect(pos, Lighting.Dark);
                    }
                    else if (rb == darknessRadioButton)
                    {
                        ApplyLightingEffect(pos, Lighting.Darkness);
                    }

                    // Проверка на изменение
                    bool hasChanged = previousLighting != lightingLevels[pos];

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
        private void ApplyLightingEffect(Point pos, Lighting level)
        {
            // Устанавливаем уровень освещенности для данной позиции
            lightingLevels[pos] = level;
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
                topComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Item1);
                bottomComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Item2);
                leftComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Item3);
                rightComboBox.SelectedIndex = Array.IndexOf(options.ToArray(), borderValues.Item4);
            }

            // Проверяем, существует ли запись в словаре переходов
            if (passageDict.TryGetValue(pos, out var passageValues))
            {
                // Устанавливаем значения комбинационных списков перехода
                passTopComboBox.SelectedIndex = passageValues.Item1;
                passBottomComboBox.SelectedIndex = passageValues.Item2;
                passLeftComboBox.SelectedIndex = passageValues.Item3;
                passRightComboBox.SelectedIndex = passageValues.Item4;
            }

            // Проверяем, существует ли запись в словаре закрытых состояний
            if (closedStates.TryGetValue(pos, out var closedValues))
            {
                // Устанавливаем состояние чекбоксов закрытия
                topCheck.Checked = closedValues.Item1;
                bottomCheck.Checked = closedValues.Item2;
                leftCheck.Checked = closedValues.Item3;
                rightCheck.Checked = closedValues.Item4;
            }

            // Проверяем, существует ли запись в словаре сообщений
            if (messageStates.TryGetValue(pos, out var messageValues))
            {
                // Устанавливаем состояние чекбоксов сообщений
                topMessageCheck.Checked = messageValues.Item1;
                bottomMessageCheck.Checked = messageValues.Item2;
                leftMessageCheck.Checked = messageValues.Item3;
                rightMessageCheck.Checked = messageValues.Item4;
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

            // Восстановление состояния освещения
            if (lightingLevels.TryGetValue(pos, out var lightingLevel))
            {
                switch (lightingLevel)
                {
                    case Lighting.Light:
                        lightRadioButton.Checked = true;
                        break;
                    case Lighting.Dark:
                        darkRadioButton.Checked = true;
                        break;
                    case Lighting.Darkness:
                        darknessRadioButton.Checked = true;
                        break;
                }
            }

            UpdatePreview(); // Обновляем предварительное изображение
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
                Tuple<string, string, string, string> previousBorders = borders.TryGetValue(pos, out var prev) ? prev : new Tuple<string, string, string, string>("Пустота", "Пустота", "Пустота", "Пустота");

                borders[pos] = new Tuple<string, string, string, string>(
                    topComboBox.SelectedItem?.ToString() ?? "",
                    bottomComboBox.SelectedItem?.ToString() ?? "",
                    leftComboBox.SelectedItem?.ToString() ?? "",
                    rightComboBox.SelectedItem?.ToString() ?? ""
                );

                // Проверка на изменение
                bool hasChanged = !previousBorders.Equals(borders[pos]);

                if (hasChanged)
                {
                    gridButtons[pos.X, GridSize - 1 - (pos.Y)].Invalidate();
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

                // Сначала сохраним текущее состояние данных о проходах
                Tuple<int, int, int, int> previousPassages = passageDict.TryGetValue(pos, out var prev) ? prev : new Tuple<int, int, int, int>(0, 0, 0, 0);

                passageDict[selectedPosition.Value] = new Tuple<int, int, int, int>(
                    passTopComboBox.SelectedIndex,
                    passBottomComboBox.SelectedIndex,
                    passLeftComboBox.SelectedIndex,
                    passRightComboBox.SelectedIndex
                );

                // Проверка на изменение
                bool hasChanged = !previousPassages.Equals(passageDict[pos]);

                if (hasChanged)
                {     

                // Обновляем соответствующую ячейку на карте
                gridButtons[selectedPosition.Value.X, GridSize - 1 - (selectedPosition.Value.Y)].Invalidate();
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
        private void PaintDarknessArea(Graphics g, Rectangle bounds)
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
                graphics.FillRectangle(blackBrush, dx-1, dy-1, 1, 1); // Верхний левый угол
                graphics.FillRectangle(blackBrush, dx + gateWidth, dy-1, 1, 1); // Верхний правый угол
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
                        graphics.DrawLine(blackPen, dx + gateWidth+1, dy - 1, dx + gateWidth+1, dy + gateHeight);
                        break;
                    case Direction.Left:
                        // Левая и правая горизонтальные линии (после поворота) в левой части ячейки
                        graphics.DrawLine(blackPen, dx - 1, dy - 1, dx - 1, dy + gateHeight);                   // Левая сторона
                        graphics.DrawLine(blackPen, dx + gateWidth+2, dy - 1, dx + gateWidth+2, dy + gateHeight); // Правая сторона
                        break;
                    case Direction.Right:
                        // Левая и правая горизонтальные линии (после поворота) в правой части ячейки
                        graphics.DrawLine(blackPen, dx - 2 , dy , dx - 2, dy + gateHeight+1);                   // Левая сторона
                        graphics.DrawLine(blackPen, dx + gateWidth + 1, dy, dx + gateWidth + 1, dy + gateHeight+1); // Правая сторона
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
                        SafeSetPixel(bmp,x, y, newColor);
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

            // Обработка уровней освещённости
            if (lightingLevels.TryGetValue(pos, out var lightingLevel))
            {
                switch (lightingLevel)
                {
                    case Lighting.Light:
                        break; // Ничего не делаем, нормальное освещение
                    case Lighting.Dark:
                        PaintDarkArea(g, bounds);
                        break;
                    case Lighting.Darkness:
                        PaintDarknessArea(g, bounds);
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
                            borderType = edgeTypes.Item1;
                            break;
                        case 1:
                            borderType = edgeTypes.Item2;
                            break;
                        case 2:
                            borderType = edgeTypes.Item3;
                            break;
                        case 3:
                            borderType = edgeTypes.Item4;
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
                    if (passageData.Item1 == 3) // секретный проход сверху
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Item1, Direction.Top);
                    }
                    if (passageData.Item2 == 3) //  секретный проход снизу
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Item2, Direction.Bottom);
                    }
                    if (passageData.Item3 == 3) //  секретный проход слева
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Item3, Direction.Left);
                    }
                    if (passageData.Item4 == 3) //  секретный проход справа
                    {
                        DrawCorrectSecret(g, bounds, edgeTypes.Item4, Direction.Right);
                    }

                    // Остальные элементы (двери и решётки) остались прежними
                    if (passageData.Item1 == 1) // Дверь сверху
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Item1, Direction.Top);
                    }
                    else if (passageData.Item1 == 2) // Решётка сверху
                    {
                        DrawRoundedGrate(g, bounds, Direction.Top);
                    }

                    if (passageData.Item2 == 1) // Дверь снизу
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Item2, Direction.Bottom);
                    }
                    else if (passageData.Item2 == 2) // Решётка снизу
                    {
                        DrawRoundedGrate(g, bounds, Direction.Bottom);
                    }

                    if (passageData.Item3 == 1) // Дверь слева
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Item3, Direction.Left);
                    }
                    else if (passageData.Item3 == 2) // Решётка слева
                    {
                        DrawRoundedGrate(g, bounds, Direction.Left);
                    }

                    if (passageData.Item4 == 1) // Дверь справа
                    {
                        DrawCorrectDoor(g, bounds, edgeTypes.Item4, Direction.Right);
                    }
                    else if (passageData.Item4 == 2) // Решётка справа
                    {
                        DrawRoundedGrate(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка лестниц вверх
                    if (passageData.Item1 == 4) // Лестница вверх сверху
                    {
                        DrawStairsUp(g, bounds, Direction.Top);
                    }
                    if (passageData.Item2 == 4) // Лестница вверх снизу
                    {
                        DrawStairsUp(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Item3 == 4) // Лестница вверх слева
                    {
                        DrawStairsUp(g, bounds, Direction.Left);
                    }
                    if (passageData.Item4 == 4) // Лестница вверх справа
                    {
                        DrawStairsUp(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка лестниц вниз
                    if (passageData.Item1 == 5) // Лестница вниз сверху
                    {
                        DrawStairsDown(g, bounds, Direction.Top);
                    }
                    if (passageData.Item2 == 5) // Лестница вниз снизу
                    {
                        DrawStairsDown(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Item3 == 5) // Лестница вниз слева
                    {
                        DrawStairsDown(g, bounds, Direction.Left);
                    }
                    if (passageData.Item4 == 5) // Лестница вниз справа
                    {
                        DrawStairsDown(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка портала
                    if (passageData.Item1 == 6) // Портал сверху
                    {
                        DrawPortal(g, bounds, Direction.Top);
                    }
                    if (passageData.Item2 == 6) // Портал снизу
                    {
                        DrawPortal(g, bounds, Direction.Bottom);
                    }
                    if (passageData.Item3 == 6) // Портал слева
                    {
                        DrawPortal(g, bounds, Direction.Left);
                    }
                    if (passageData.Item4 == 6) // Портал справа
                    {
                        DrawPortal(g, bounds, Direction.Right);
                    }

                    // Проверка и отрисовка надписи "Выход"
                    if (passageData.Item1 == 7) // Выход сверху
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Item1 == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Top, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Top, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Top, Color.LightSkyBlue);
                    }
                    if (passageData.Item2 == 7) // Выход снизу
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Item2 == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Bottom, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Bottom, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Bottom, Color.LightSkyBlue);
                    }
                    if (passageData.Item3 == 7) // Выход слева
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Item3 == "Кирпичная стена")
                                DrawExitWord(g, bounds, Direction.Left, ColorTranslator.FromHtml("#FF0000"));
                            else
                                DrawExitWord(g, bounds, Direction.Left, Color.LightSkyBlue);
                        else
                            DrawExitWord(g, bounds, Direction.Left, Color.LightSkyBlue);
                    }
                    if (passageData.Item4 == 7) // Выход справа
                    {
                        if (borders.TryGetValue(pos, out var borderValues))
                            if (borderValues.Item4 == "Кирпичная стена")
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
                    else  if (centralOption == "Не исследовано")
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
                    g.DrawString("?", questionFont, Brushes.White, new PointF(
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
                if (messages.Item1) // topMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.Right - 7, bounds.Y));
                }
                if (messages.Item2) // bottomMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.X, bounds.Bottom - 7));
                }
                if (messages.Item3) // leftMessageCheck
                {
                    DrawEnvelope(g, bounds, new Point(bounds.X, bounds.Y));
                }
                if (messages.Item4) // rightMessageCheck
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
                closedStates[position] = new Tuple<bool, bool, bool, bool>(false, false, false, false);

                // Инициализация состояний новых чекбоксов (текстов)
                messageStates[position] = new Tuple<bool, bool, bool, bool>(false, false, false, false);

                // Инициализация прочих необходимых данных
                borders[position] = new Tuple<string, string, string, string>("Пустота", "Пустота", "Пустота", "Пустота");
                passageDict[position] = new Tuple<int, int, int, int>(0, 0, 0, 0);

                notesPerCell[position] = "";
                imagesPerCell[position] = null;

                isDangerStates[position] = false;
                noMagicStates[position] = false;

                // По умолчанию устанавливаем освещение "Светло"
                lightingLevels[position] = Lighting.Light;

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
            public Tuple<string, string, string, string> Borders;
            public Tuple<int, int, int, int> Passages;
            public Tuple<bool, bool, bool, bool> ClosedStates;
            public Tuple<bool, bool, bool, bool> Messages;
            public string CentralOption;
            public bool IsDanger;
            public bool NoMagic;
            public Lighting LightingLevel;
            public Image CellImage;
            public string Notes;
        };
    }

    enum Direction
    {
        Top,
        Bottom,
        Left,
        Right
    }

    enum Lighting
    {
        Light,
        Dark,
        Darkness
    }


}