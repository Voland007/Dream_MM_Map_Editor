using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MMMapEditor
{
    public partial class SearchForm : Form
    {
        private TextBox folderTextBox;
        private TextBox searchTextBox;
        private RichTextBox resultsTextBox;
        private CheckBox authorCheckbox;
        private CheckBox noteCheckbox;
        private CheckBox metadataCheckbox;
        private CheckBox caseSensitiveCheckbox;
        private Button searchButton;
        private Button browseButton;
        private GroupBox whereSearchGroupBox;
        private GroupBox whatSearchGroupBox;
        private GroupBox howSearchGroupBox;
        private GroupBox resultsGroupBox;

        private int totalMatches = 0;           // Общий счётчик найденных кар
        private int totalOccurrences = 0;       // Общее число всех совпаденийт
        private int recordCounter = 0;          // Переменная для хранения текущего номера записи
        private int previousTotalMatches = 0;   // Переменная для отслеживания предыдущего значения totalMatches

        public SearchForm()
        {
            ClientSize = new Size(1080, 730);
            Text = "Глобальный поиск по всем картам";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            InitializeComponent();
            folderTextBox.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MM1_Maps");

            searchTextBox.Focus();
            searchTextBox.KeyDown += OnSearchTextKeyDown;

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            searchTextBox.Focus();
        }

        private void OnSearchTextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                SearchButton_Click(sender, EventArgs.Empty);
            }
        }

        private void InitializeComponent()
        {
            whereSearchGroupBox = new GroupBox
            {
                Text = "Где ищем?",
                Location = new Point(10, 10),
                Width = 1060,
                Height = 80
            };

            whatSearchGroupBox = new GroupBox
            {
                Text = "Что ищем?",
                Location = new Point(whereSearchGroupBox.Left, whereSearchGroupBox.Bottom + 10),
                Width = whereSearchGroupBox.Width,
                Height = 50
            };

            howSearchGroupBox = new GroupBox
            {
                Text = "Как ищем?",
                Location = new Point(whatSearchGroupBox.Left, whatSearchGroupBox.Bottom + 10),
                Width = whatSearchGroupBox.Width,
                Height = 42
            };

            resultsGroupBox = new GroupBox
            {
                Text = "Результат поиска",
                Location = new Point(howSearchGroupBox.Left, howSearchGroupBox.Bottom + 10),
                Width = whatSearchGroupBox.Width,
                Height = 480
            };

            // Выбор каталога
            folderTextBox = new TextBox
            {
                PlaceholderText = "Путь к папке",
                Location = new Point(10, 20),
                Width = whereSearchGroupBox.Width - 70
            };

            // Кнопка для выбора папки
            browseButton = new Button
            {
                Text = "...",
                Location = new Point(folderTextBox.Right + 10, folderTextBox.Top),
                Width = 30
            };
            browseButton.Click += BrowseButton_Click;

            // Фильтры поиска
            authorCheckbox = new CheckBox
            {
                Text = "Искать в папке с авторскими файлами",
                Location = new Point(folderTextBox.Left, folderTextBox.Bottom + 10),
                Width = 300
            };
            authorCheckbox.CheckedChanged += AuthorCheckbox_CheckedChanged;

            caseSensitiveCheckbox = new CheckBox
            {
                Text = "С учётом регистра",
                Location = new Point(10, 15),
                Width = 200,
                Checked = false 
            };

            noteCheckbox = new CheckBox
            {
                Text = "Искать в описаниях клеток",
                Location = new Point(authorCheckbox.Right + 10, authorCheckbox.Top),
                Width = 300,
                Checked = true
            };

            metadataCheckbox = new CheckBox
            {
                Text = "Искать в метаданных",
                Location = new Point(noteCheckbox.Right + 10, authorCheckbox.Top),
                Width = 300,
                Checked = true
            };

            // Поле ввода для поиска
            searchTextBox = new TextBox
            {
                PlaceholderText = "Введите текст для поиска",
                Location = new Point(10, 20),
                Width = whereSearchGroupBox.Width - 20
            };
            searchTextBox.TextChanged += searchTextBox_TextChanged;

            // Поле для вывода результатов
            resultsTextBox = new RichTextBox
            {
                Location = new Point(10, 20),
                Width = resultsGroupBox.Width - 30,
                Height = resultsGroupBox.Height - 30,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            // Кнопка для старта поиска
            searchButton = new Button
            {
                Text = "Поиск",
                Location = new Point(480, resultsGroupBox.Bottom + 10),
                Width = 120,
                Enabled = false
            };
            searchButton.Click += SearchButton_Click;

            // Добавляем элементы в группу
            whereSearchGroupBox.Controls.Add(folderTextBox);
            whereSearchGroupBox.Controls.Add(browseButton);
            whereSearchGroupBox.Controls.Add(authorCheckbox);
            whereSearchGroupBox.Controls.Add(noteCheckbox);
            whereSearchGroupBox.Controls.Add(metadataCheckbox);

            whatSearchGroupBox.Controls.Add(searchTextBox);

            howSearchGroupBox.Controls.Add(caseSensitiveCheckbox);

            resultsGroupBox.Controls.Add(resultsTextBox);

            // Добавляем группы на форму
            Controls.Add(whereSearchGroupBox);
            Controls.Add(whatSearchGroupBox);
            Controls.Add(resultsGroupBox);
            Controls.Add(searchButton);
            Controls.Add(howSearchGroupBox);
        }

        private void searchTextBox_TextChanged(object sender, EventArgs e)
        {
            searchButton.Enabled = !string.IsNullOrEmpty(searchTextBox.Text.Trim());
        }

        private void AuthorCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (authorCheckbox.Checked)
            {
                var result = MessageBox.Show(
                    "Будьте осторожны! Авторские карты могут содержать спойлеры игры.\nХотите продолжить поиск среди авторских карт?",
                    "Предупреждение!",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );

                if (result != DialogResult.Yes)
                {
                    authorCheckbox.Checked = false;
                }
            }
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для поиска:";
                dialog.RootFolder = Environment.SpecialFolder.Desktop;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    folderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void SearchButton_Click(object sender, EventArgs e)
        {
            string rootDir = folderTextBox.Text;
            string searchTerm = searchTextBox.Text;
            bool includeAuthorFolders = authorCheckbox.Checked;
            bool searchInNotes = noteCheckbox.Checked;
            bool searchInMetadata = metadataCheckbox.Checked;

            // Очищаем текстовое поле результатов
            resultsTextBox.Clear();

            // Сбрасываем счётчики
            totalMatches = 0;
            totalOccurrences = 0;
            recordCounter = 0;

            // Начинаем рекурсивный поиск
            RecursivelySearch(rootDir, searchTerm, includeAuthorFolders, searchInNotes, searchInMetadata, rootDir);

            // Выводим итоговую статистику
            resultsTextBox.Select(0, 0);  // Устанавливаем позицию курсора в начало

            // Первая строка должна быть белой на черном фоне
            resultsTextBox.SelectionBackColor = Color.Black;
            resultsTextBox.SelectionColor = Color.White;
            resultsTextBox.SelectionFont = new Font("Lucida Console", 11F);
            resultsTextBox.SelectedText = $"\"{searchTerm}\" найдено {totalOccurrences} раз(а) в {totalMatches} картах:                                                                                                            \n";
        }

        private void RecursivelySearch(string directory, string term, bool includeAuthorFolders, bool searchInNotes, bool searchInMetadata, string rootDir)
        {
            // Рекурсия по подпапкам
            foreach (string dir in Directory.GetDirectories(directory))
            {
                if (!includeAuthorFolders && dir.EndsWith(Path.DirectorySeparatorChar + "Author"))
                    continue;

                RecursivelySearch(dir, term, includeAuthorFolders, searchInNotes, searchInMetadata, rootDir);
            }

            // Обрабатываем файлы в текущей папке
            foreach (string file in Directory.GetFiles(directory, "*.map"))
            {
                ProcessMapFile(file, term, searchInNotes, searchInMetadata, rootDir);
            }
        }

        // Метод обрабатывающий одну карту
        private void ProcessMapFile(string filePath, string term, bool searchInNotes, bool searchInMetadata, string rootDir)
        {
            string jsonContent = File.ReadAllText(filePath);
            dynamic data = JsonConvert.DeserializeObject(jsonContent);

            bool hasMatch = false;

            // Режим поиска: с учётом регистра или без
            bool isCaseSensitive = caseSensitiveCheckbox.Checked;

            // Поиск в заметках на клетках
            if (searchInNotes)
            {
                for (int i = 0; i < data.Cells.Count; i++)
                {
                    dynamic cell = data.Cells[i];
                    string note = cell.Note?.ToString() ?? "";

                    // Вычисляем координаты клетки
                    int x = i % 16;
                    int y = i / 16;

                    if (isCaseSensitive ? note.Contains(term) : note.ToUpper().Contains(term.ToUpper()))
                    {
                        // Передаем координаты клетки в DisplayMatch
                        DisplayMatch(filePath, $"Заметка на клетке [X = {x},Y = {y}]", note, term, rootDir);
                        hasMatch = true;
                    }
                }
            }

            // Поиск в метаданных
            if (searchInMetadata)
            {
                if (data.MetaData != null)
                {
                    string sector = data.MetaData.MapSector?.ToString() ?? "";
                    string surface = data.MetaData.Surface?.ToString() ?? "";

                    if (
                        (isCaseSensitive &&
                             (sector.Contains(term) || surface.Contains(term)))
                        ||
                        (!isCaseSensitive &&
                             (sector.ToUpper().Contains(term.ToUpper()) || surface.ToUpper().Contains(term.ToUpper())))
                    )
                    {
                        DisplayMatch(filePath, "Метаданные:", $"MAP-SECTION: {sector}, SURFACE: {surface}", term, rootDir);
                        hasMatch = true;
                    }
                }
            }

            // Если были найдены совпадения, увеличиваем счётчик карт
            if (hasMatch)
            {
                totalMatches++;
            }
        }

        // отображающий результат поиска
        private void DisplayMatch(string filePath, string type, string field, string term, string rootDir)
        {
            // Относительный путь карты
            string relativePath = filePath.StartsWith(rootDir) ? filePath.Substring(rootDir.Length).TrimStart('\\') : filePath;

            // Добавляем разделитель, если это не первая карта
            if (totalOccurrences > 0)
            {
                // Проверяем, увеличилась ли уникальность карт
                if (previousTotalMatches != totalMatches)
                {
                    resultsTextBox.AppendText("\n\n=========================================================================\n\n");

                    // Применяем полужирный стиль к разделителю
                    int dividerPosition = resultsTextBox.Text.LastIndexOf("=========================================================================");
                    if (dividerPosition != -1)
                    {
                        resultsTextBox.Select(dividerPosition, "=========================================================================".Length);
                        resultsTextBox.SelectionFont = new Font(resultsTextBox.Font.FontFamily, 14F, FontStyle.Bold);
                        resultsTextBox.SelectionColor = Color.Purple;
                    }
                }
                else
                {
                    resultsTextBox.AppendText("\n\n--------------------------------------------------------------------------\n");

                    // Применяем полужирный стиль к разделителю
                    int dividerPosition = resultsTextBox.Text.LastIndexOf("--------------------------------------------------------------------------");
                    if (dividerPosition != -1)
                    {
                        resultsTextBox.Select(dividerPosition, "--------------------------------------------------------------------------".Length);
                        resultsTextBox.SelectionFont = new Font(resultsTextBox.Font.FontFamily, 14F, FontStyle.Bold);
                        resultsTextBox.SelectionColor = Color.RebeccaPurple;
                    }
                }
            }

            // Сохраняем текущее значение totalMatches для следующей итерации
            previousTotalMatches = totalMatches;

            resultsTextBox.AppendText($"\n#{++recordCounter}\t Карта {totalMatches + 1}:\t ..\\{relativePath}\n\n");

            // Подсветка порядкового номера записи
            int startNumberPosition = resultsTextBox.Text.LastIndexOf("#" + recordCounter.ToString());
            if (startNumberPosition != -1)
            {
                resultsTextBox.Select(startNumberPosition, "#".Length + recordCounter.ToString().Length);
                resultsTextBox.SelectionColor = Color.FromArgb(255, 128, 0); // оранжево-красный цвет
                resultsTextBox.SelectionFont = new Font(resultsTextBox.Font.FontFamily, 14F, FontStyle.Bold);
            }

            // Выделяем имя файла
            resultsTextBox.Select(resultsTextBox.TextLength - ("Карта ").Length - totalMatches.ToString().Length - relativePath.Length - 8, ("Карта ").Length + totalMatches.ToString().Length);
            resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Bold | FontStyle.Underline);

            // Выделяем имя файла синего цвета
            resultsTextBox.Select(resultsTextBox.TextLength - relativePath.Length - 2, relativePath.Length);
            resultsTextBox.SelectionColor = Color.Blue;

            // Тип совпадения (например, "Заметка на клетке") 
            //  resultsTextBox.AppendText($"{type}\n\n");

            // Разделяем тип совпадения на две части
            if (type.Contains('['))
            {
                string[] parts = type.Split('[', 2); // Деление только на две части
                string firstPart = parts[0].Trim();
                string secondPart = "[" + parts[1];

                // Первая часть ("Заметка на клетке") выделяется жирным и подчеркнутым текстом
                resultsTextBox.AppendText(firstPart + " ");
                resultsTextBox.Select(resultsTextBox.TextLength - firstPart.Length - 1, firstPart.Length);
                resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Bold | FontStyle.Underline);
                resultsTextBox.SelectionColor = Color.Black;

                // Вторая часть ([X={x},Y={y}]) отображается сразу после первой части на той же строке
                resultsTextBox.AppendText(secondPart + "\n\n");
                resultsTextBox.Select(resultsTextBox.TextLength - secondPart.Length - 2, secondPart.Length);
                resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Bold);
                resultsTextBox.SelectionColor = Color.DarkGreen;
            }
            else
            {
                // Если символ '[' не найден, выводим всю строку целиком
                resultsTextBox.AppendText(type + "\n\n");
                resultsTextBox.Select(resultsTextBox.TextLength - type.Length - 2, type.Length);
                resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Regular);
                resultsTextBox.SelectionColor = Color.Black;
            }

            // Далее остальная логика та же самая
            resultsTextBox.AppendText($"{field}");

            // Находим все вхождения искомого термина с учетом режима чувствительности к регистру
            List<int> indices = FindAllIndices(field, term);

                // Теперь проверяем состояние флага чувствительности к регистру
                if (!caseSensitiveCheckbox.Checked)
                {
                    // Преобразуем оба текста в верхний регистр перед поиском индексов
                    string upperField = field.ToUpper();
                    string upperTerm = term.ToUpper();

                    // Используем вспомогательную функцию для поиска позиций без учета регистра
                    indices = FindAllIndices(upperField, upperTerm);

                    // Индексы теперь относятся к верхнему регистру, преобразуем обратно
                    for (int i = 0; i < indices.Count; i++)
                    {
                        int adjustedIndex = GetAdjustedIndex(field, upperField, indices[i]);
                        if (adjustedIndex >= 0)
                        {
                            resultsTextBox.Select(resultsTextBox.TextLength - field.Length + adjustedIndex, term.Length);
                            resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Bold);
                            resultsTextBox.SelectionColor = Color.Red;
                        }
                    }
                }
                else
                {
                    // Для чувствительного к регистру случая оставляем оригинальную реализацию
                    foreach (int index in indices)
                    {
                        resultsTextBox.Select(resultsTextBox.TextLength - field.Length + index, term.Length);
                        resultsTextBox.SelectionFont = new Font(resultsTextBox.Font, FontStyle.Bold);
                        resultsTextBox.SelectionColor = Color.Red;
                    }
                }

                // Инкрементируем счётчик совпадений
                totalOccurrences++;
            }

            // Функция для преобразования индексов верхнего регистра обратно в оригинальный индекс
            private int GetAdjustedIndex(string originalText, string upperText, int index)
            {
                // Исключение ошибок вне диапазона
                if (index < 0 || index >= upperText.Length)
                    return -1;

                // Возвращаем соответствующую позицию оригинального текста
                return originalText.IndexOf(originalText[index], Math.Max(index - 10, 0));
            }

            // Вспомогательная функция для нахождения всех индексов строки
            private List<int> FindAllIndices(string source, string value)
        {
            List<int> indices = new List<int>();
            int pos = source.IndexOf(value);
            while (pos != -1)
            {
                indices.Add(pos);
                pos = source.IndexOf(value, pos + 1);
            }
            return indices;
        }
    }
}