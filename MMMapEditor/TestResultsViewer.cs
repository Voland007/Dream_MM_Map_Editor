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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Форма для просмотра результатов тестов
    /// </summary>
    public class TestResultsViewer : Form
    {
        private enum CharDiffKind
        {
            Replace,
            Insert,
            Delete
        }

        private sealed class CharDiffEntry
        {
            public CharDiffKind Kind { get; set; }
            public int ExpectedIndex { get; set; }
            public int ActualIndex { get; set; }
            public char? ExpectedChar { get; set; }
            public char? ActualChar { get; set; }
        }

        private sealed class CharDiffResult
        {
            public string ExpectedText { get; set; }
            public string ActualText { get; set; }
            public List<CharDiffEntry> Differences { get; } = new List<CharDiffEntry>();

            public bool HasDifferences => Differences.Count > 0;
        }

        private sealed class DisplayedVariant
        {
            public string HeaderKey { get; set; } = string.Empty;
            public string BodyKey { get; set; } = string.Empty;

            public string HeaderBodyKey => HeaderKey + "\n" + BodyKey;
        }

        private sealed class DisplayedVariantGroup
        {
            public string ScaffoldKey { get; set; } = string.Empty;
            public List<DisplayedVariant> Variants { get; } = new List<DisplayedVariant>();
        }

        private sealed class DisplayedVariantHeader
        {
            public int LineIndex { get; set; }
            public int Depth { get; set; }
            public string ParentNumber { get; set; } = string.Empty;
            public string HeaderKey { get; set; } = string.Empty;
        }

        private sealed class VariantOrderRecommendation
        {
            public int VariantCount { get; set; }
        }

        private static readonly Regex DisplayVariantHeaderRegex = new Regex(
            @"^(?<indent>\s*)Вариант\s+(?<number>\d+(?:\.\d+)*)(?<tail>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly List<TestResult> _results;
        private readonly TestExpectationStore _expectationStore;
        private TreeView _treeView;
        private RichTextBox _detailsBox;
        private Button _runSelectedButton;
        private Button _runAllButton;
        private Button _exportHtmlButton;
        private Button _exportTextButton;
        private Button _acceptActualButton;
        private Button _undoExpectationButton;
        private Label _acceptRecommendationLabel = null!;
        private TextBox _filterBox;
        private ComboBox _ovrFileCombo;
        private DataGridView _summaryGrid;
        private System.Windows.Forms.Timer _filterTimer;
        private TestResult _selectedTestResult;
        private CellCheckResult _selectedCellResult;
        private int _historyCount;

        public TestResultsViewer(List<TestResult> results)
            : this(results, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OvrAnalyzerTests.json"))
        {
        }

        public TestResultsViewer(List<TestResult> results, string testsFilePath)
        {
            _results = results ?? new List<TestResult>();
            _expectationStore = new TestExpectationStore(testsFilePath);
            _historyCount = _expectationStore.GetHistoryCount();
            InitializeComponent();
            PopulateResults();
            UpdateActionButtons();
        }

        private void InitializeComponent()
        {
            this.Text = "Результаты тестирования анализатора оверлеев";
            this.Size = new Size(1400, 900);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(45, 45, 45);

            // Основной разделитель
            var mainSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(60, 60, 60),
                SplitterWidth = 2,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Устанавливаем минимальные размеры после загрузки формы
            this.Load += (s, e) =>
            {
                mainSplitContainer.Panel1MinSize = 300;
                mainSplitContainer.Panel2MinSize = 500;

                // Устанавливаем начальную позицию разделителя
                if (mainSplitContainer.Width > 0)
                {
                    mainSplitContainer.SplitterDistance = Math.Min(400, mainSplitContainer.Width - mainSplitContainer.Panel2MinSize - 10);
                }
            };

            // Устанавливаем цвет разделителя через фон панелей
            mainSplitContainer.Panel1.BackColor = Color.FromArgb(45, 45, 45);
            mainSplitContainer.Panel2.BackColor = Color.FromArgb(45, 45, 45);

            // Левая панель - дерево тестов
            var leftPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45) };

            // Панель фильтрации
            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(5), BackColor = Color.FromArgb(60, 60, 60) };

            var filterLabel = new Label
            {
                Text = "Фильтр:",
                Location = new Point(5, 8),
                Size = new Size(40, 23),
                ForeColor = Color.White
            };

            _filterBox = new TextBox
            {
                Location = new Point(50, 5),
                Width = 200,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var clearFilterButton = new Button
            {
                Text = "✕",
                Location = new Point(255, 5),
                Size = new Size(25, 23),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 }
            };
            clearFilterButton.Click += (s, e) => { _filterBox.Text = ""; };

            var statsLabel = new Label
            {
                Text = "Всего: 0 | Пройдено: 0 | Провалено: 0",
                Location = new Point(5, 35),
                Size = new Size(300, 20),
                ForeColor = Color.LightGray
            };
            statsLabel.Name = "statsLabel";

            filterPanel.Controls.AddRange(new Control[] { filterLabel, _filterBox, clearFilterButton, statsLabel });

            // Дерево тестов
            _treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowNodeToolTips = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Indent = 20,
                ItemHeight = 20,
                LineColor = Color.FromArgb(80, 80, 80)
            };
            _treeView.AfterSelect += TreeView_AfterSelect;

            leftPanel.Controls.Add(_treeView);
            leftPanel.Controls.Add(filterPanel);

            mainSplitContainer.Panel1.Controls.Add(leftPanel);

            // Правая панель - детали
            var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45) };

            // Панель кнопок
            var buttonPanel = new Panel { Dock = DockStyle.Top, Height = 85, Padding = new Padding(5), BackColor = Color.FromArgb(60, 60, 60) };

            _runSelectedButton = new Button
            {
                Text = "▶ Запустить выбранный",
                Location = new Point(5, 5),
                Width = 150,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 }
            };

            _runAllButton = new Button
            {
                Text = "▶▶ Запустить все",
                Location = new Point(160, 5),
                Width = 150,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 }
            };

            _exportHtmlButton = new Button
            {
                Text = "🌐 Экспорт HTML",
                Location = new Point(320, 5),
                Width = 120,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 }
            };

            _exportTextButton = new Button
            {
                Text = "📄 Экспорт TXT",
                Location = new Point(445, 5),
                Width = 120,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 }
            };

            _ovrFileCombo = new ComboBox
            {
                Location = new Point(575, 5),
                Width = 180,
                Height = 30,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };

            _acceptRecommendationLabel = new Label
            {
                Text = "",
                Location = new Point(5, 45),
                Width = 385,
                Height = 30,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 6, 0),
                BackColor = Color.FromArgb(45, 95, 55),
                ForeColor = Color.White,
                Visible = false
            };

            _acceptActualButton = new Button
            {
                Text = "✓ Принять фактический",
                Location = new Point(400, 45),
                Width = 180,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(106, 153, 85),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 },
                Enabled = false
            };

            _undoExpectationButton = new Button
            {
                Text = "↶ Откатить",
                Location = new Point(585, 45),
                Width = 170,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(160, 120, 50),
                ForeColor = Color.White,
                FlatAppearance = { BorderSize = 0 },
                Enabled = false
            };

            _runSelectedButton.Click += RunSelectedTest;
            _runAllButton.Click += RunAllTests;
            _exportHtmlButton.Click += ExportHtml;
            _exportTextButton.Click += ExportText;
            _acceptActualButton.Click += AcceptActualAsExpected;
            _undoExpectationButton.Click += UndoLastExpectationChange;

            buttonPanel.Controls.AddRange(new Control[] {
                _runSelectedButton, _runAllButton, _exportHtmlButton, _exportTextButton,
                _ovrFileCombo, _acceptRecommendationLabel, _acceptActualButton, _undoExpectationButton
            });

            // Вкладки
            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White
            };

            // Настройка стиля вкладок
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem += (s, e) =>
            {
                var g = e.Graphics;
                var backColor = e.Index == tabControl.SelectedIndex ? Color.FromArgb(60, 60, 60) : Color.FromArgb(45, 45, 45);
                var foreColor = Color.White;

                var bgBrush = new SolidBrush(backColor);
                var textBrush = new SolidBrush(foreColor);

                try
                {
                    e.DrawBackground();
                    g.FillRectangle(bgBrush, e.Bounds);

                    var text = tabControl.TabPages[e.Index].Text;
                    var textSize = g.MeasureString(text, e.Font);
                    var textX = e.Bounds.X + (e.Bounds.Width - textSize.Width) / 2;
                    var textY = e.Bounds.Y + (e.Bounds.Height - textSize.Height) / 2;

                    g.DrawString(text, e.Font, textBrush, textX, textY);

                    e.DrawFocusRectangle();
                }
                finally
                {
                    bgBrush.Dispose();
                    textBrush.Dispose();
                }
            };

            // Вкладка с деталями
            var detailsTab = new TabPage("Детали") { BackColor = Color.FromArgb(45, 45, 45) };
            _detailsBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.None
            };
            detailsTab.Controls.Add(_detailsBox);

            // Вкладка со сводкой
            var summaryTab = new TabPage("Сводка") { BackColor = Color.FromArgb(45, 45, 45) };
            _summaryGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(80, 80, 80),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(60, 60, 60),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(0, 122, 204),
                    SelectionForeColor = Color.White,
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                },
                GridColor = Color.FromArgb(80, 80, 80),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
            };

            // Добавляем контекстное меню для копирования
            var gridContextMenu = new ContextMenuStrip();
            var copyCellMenuItem = new ToolStripMenuItem("Копировать ячейку");
            var copyRowMenuItem = new ToolStripMenuItem("Копировать строку");

            copyCellMenuItem.Click += (s, e) =>
            {
                if (_summaryGrid.CurrentCell != null && _summaryGrid.CurrentCell.Value != null)
                {
                    Clipboard.SetText(_summaryGrid.CurrentCell.Value.ToString());
                }
            };

            copyRowMenuItem.Click += (s, e) =>
            {
                if (_summaryGrid.CurrentRow != null)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (DataGridViewCell cell in _summaryGrid.CurrentRow.Cells)
                    {
                        if (cell.Value != null)
                            sb.Append(cell.Value.ToString() + "\t");
                    }
                    Clipboard.SetText(sb.ToString());
                }
            };

            gridContextMenu.Items.AddRange(new ToolStripItem[] { copyCellMenuItem, copyRowMenuItem });
            _summaryGrid.ContextMenuStrip = gridContextMenu;

            summaryTab.Controls.Add(_summaryGrid);

            tabControl.TabPages.Add(detailsTab);
            tabControl.TabPages.Add(summaryTab);

            rightPanel.Controls.Add(tabControl);
            rightPanel.Controls.Add(buttonPanel);

            mainSplitContainer.Panel2.Controls.Add(rightPanel);

            this.Controls.Add(mainSplitContainer);

            // Таймер для отложенной фильтрации
            _filterTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _filterTimer.Tick += (s, e) =>
            {
                _filterTimer.Stop();
                FilterResults();
            };

            _filterBox.TextChanged += (s, e) =>
            {
                _filterTimer.Stop();
                _filterTimer.Start();
            };

            // Добавляем обработчик для клавиши Escape
            this.KeyPreview = true;
            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    this.Close();
                }
            };

            // Добавляем обработчик для изменения размера
            this.Resize += (s, e) =>
            {
                // Обновляем соотношение панелей при изменении размера окна
                if (mainSplitContainer.Width > 0 && mainSplitContainer.Panel2MinSize > 0)
                {
                    int maxSplitterDistance = mainSplitContainer.Width - mainSplitContainer.Panel2MinSize - 10;
                    if (mainSplitContainer.SplitterDistance > maxSplitterDistance)
                    {
                        mainSplitContainer.SplitterDistance = Math.Max(mainSplitContainer.Panel1MinSize,
                            Math.Min(400, maxSplitterDistance));
                    }
                }
            };
        }

        private void PopulateResults()
        {
            if (_treeView.InvokeRequired)
            {
                _treeView.Invoke(new Action(PopulateResults));
                return;
            }

            _treeView.Nodes.Clear();
            _selectedTestResult = null;
            _selectedCellResult = null;
            _treeView.BeginUpdate();

            try
            {
                int passedCount = 0;
                int failedCount = 0;

                var passedNode = new TreeNode("Пройдены")
                {
                    ForeColor = Color.FromArgb(106, 153, 85),
                    NodeFont = new Font(_treeView.Font, FontStyle.Bold)
                };

                var failedNode = new TreeNode("Провалены")
                {
                    ForeColor = Color.FromArgb(244, 135, 113),
                    NodeFont = new Font(_treeView.Font, FontStyle.Bold)
                };

                foreach (var result in _results.OrderBy(r => r.TestCase.Name))
                {
                    // Пересчитываем Passed на основе результатов клеток
                    result.Passed = result.CellResults.All(r => r.Passed);

                    if (result.Passed)
                        passedCount++;
                    else
                        failedCount++;

                    var testNode = new TreeNode($"{result.TestCase.Name} [{result.TestCase.Id}] - {result.PassedChecks}/{result.TotalChecks}")
                    {
                        Tag = result,
                        ForeColor = result.Passed ? Color.FromArgb(106, 153, 85) : Color.FromArgb(244, 135, 113),
                        ToolTipText = result.ErrorMessage ?? $"{result.PassedChecks}/{result.TotalChecks} проверок пройдено"
                    };

                    // Добавляем дочерние узлы для каждой клетки
                    foreach (var cellResult in result.CellResults.OrderBy(c => c.Cell.Y).ThenBy(c => c.Cell.X))
                    {
                        var modeSuffix = string.IsNullOrWhiteSpace(cellResult.ViewMode)
                            ? ""
                            : $" [{cellResult.ViewMode}]";

                        var cellNode = new TreeNode($"Клетка ({cellResult.Cell.X},{cellResult.Cell.Y}){modeSuffix}")
                        {
                            Tag = cellResult,
                            ForeColor = cellResult.Passed ? Color.FromArgb(106, 153, 85) : Color.FromArgb(244, 135, 113),
                            ToolTipText = cellResult.Passed ? "Ожидание совпадает" : "Ожидание не совпадает"
                        };

                        // Для отображения в дереве используем первые 200 символов или первые 5 строк
                        string actualPreview = GetPreviewText(cellResult.Actual, 200, 3);
                        string expectedPreview = GetPreviewText(cellResult.Expected, 200, 3);

                        var actualNode = new TreeNode($"Фактический: {actualPreview}")
                        {
                            Tag = cellResult,
                            ForeColor = Color.FromArgb(206, 145, 120),
                            ToolTipText = cellResult.Actual // Полный текст в подсказке
                        };

                        var expectedNode = new TreeNode($"Ожидаемый: {expectedPreview}")
                        {
                            Tag = cellResult,
                            ForeColor = Color.FromArgb(156, 220, 254),
                            ToolTipText = cellResult.Expected // Полный текст в подсказке
                        };

                        // Добавляем заметки, если есть
                        if (!string.IsNullOrEmpty(cellResult.Notes))
                        {
                            var notesNode = new TreeNode($"Заметки: {GetPreviewText(cellResult.Notes, 100, 2)}")
                            {
                                Tag = cellResult,
                                ForeColor = Color.FromArgb(255, 215, 0),
                                ToolTipText = cellResult.Notes
                            };
                            cellNode.Nodes.Add(notesNode);
                        }

                        // Добавляем информацию о трассировке, если есть и тест провален
                        if (!cellResult.Passed && !string.IsNullOrEmpty(cellResult.AnalysisTrace))
                        {
                            var traceNode = new TreeNode($"Трассировка (есть)")
                            {
                                Tag = cellResult,
                                ForeColor = Color.Gray
                            };
                            cellNode.Nodes.Add(traceNode);
                        }

                        cellNode.Nodes.Add(actualNode);
                        cellNode.Nodes.Add(expectedNode);

                        testNode.Nodes.Add(cellNode);
                    }

                    if (result.Passed)
                        passedNode.Nodes.Add(testNode);
                    else
                        failedNode.Nodes.Add(testNode);
                }

                if (passedNode.Nodes.Count > 0)
                    _treeView.Nodes.Add(passedNode);

                if (failedNode.Nodes.Count > 0)
                    _treeView.Nodes.Add(failedNode);

                // Обновляем статистику
                UpdateStats(passedCount, failedCount);

                // Заполняем комбобокс с файлами оверлеев
                _ovrFileCombo.Items.Clear();
                _ovrFileCombo.Items.Add("Все файлы");

                var ovrFiles = _results.Select(r => r.TestCase.OvrFilePath)
                                       .Distinct()
                                       .OrderBy(f => f)
                                       .ToList();

                foreach (var file in ovrFiles)
                {
                    _ovrFileCombo.Items.Add(file);
                }
                _ovrFileCombo.SelectedIndex = 0;

                // Заполняем сводную таблицу
                FillSummaryGrid();

                // По умолчанию список пройденных тестов схлопываем,
                // а список проваленных раскрываем
                if (passedNode.Nodes.Count > 0)
                    passedNode.Collapse();

                if (failedNode.Nodes.Count > 0)
                    failedNode.Expand();

                UpdateActionButtons();
            }
            finally
            {
                _treeView.EndUpdate();
            }
        }

        /// <summary>
        /// Получить предварительный просмотр текста для отображения в дереве
        /// </summary>
        private string GetPreviewText(string text, int maxLength = 200, int maxLines = 3)
        {
            if (string.IsNullOrEmpty(text))
                return "<пусто>";

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var previewLines = lines.Take(maxLines).ToList();

            string result = string.Join("\n", previewLines);

            if (lines.Length > maxLines)
            {
                result += $"\n... и ещё {lines.Length - maxLines} строк";
            }
            else if (result.Length > maxLength)
            {
                result = result.Substring(0, maxLength) + "...";
            }

            return result.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private void UpdateStats(int passedCount, int failedCount)
        {
            // Рекурсивно ищем лейбл с именем "statsLabel"
            Label statsLabel = FindControl<Label>(this, "statsLabel");

            if (statsLabel != null)
            {
                statsLabel.Text = $"Всего: {_results.Count} | Пройдено: {passedCount} | Провалено: {failedCount}";
            }
            else
            {
                // Если не нашли, выведем в отладку
                System.Diagnostics.Debug.WriteLine("statsLabel не найден!");
            }
        }

        /// <summary>
        /// Рекурсивно ищет контрол указанного типа по имени
        /// </summary>
        private T FindControl<T>(Control parent, string name) where T : Control
        {
            foreach (Control control in parent.Controls)
            {
                if (control is T typedControl && control.Name == name)
                {
                    return typedControl;
                }

                // Рекурсивно ищем в дочерних контролах
                T found = FindControl<T>(control, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void FillSummaryGrid()
        {
            if (_summaryGrid.InvokeRequired)
            {
                _summaryGrid.Invoke(new Action(FillSummaryGrid));
                return;
            }

            _summaryGrid.Columns.Clear();

            _summaryGrid.Columns.Add("TestName", "Тест");
            _summaryGrid.Columns.Add("Status", "Статус");
            _summaryGrid.Columns.Add("Passed", "Пройдено");
            _summaryGrid.Columns.Add("Failed", "Провалено");
            _summaryGrid.Columns.Add("Time", "Время (мс)");
            _summaryGrid.Columns.Add("File", "Файл");
            _summaryGrid.Columns.Add("Error", "Ошибка");

            _summaryGrid.Rows.Clear();

            foreach (var result in _results.OrderBy(r => r.TestCase.Name))
            {
                int rowIndex = _summaryGrid.Rows.Add();
                var row = _summaryGrid.Rows[rowIndex];

                row.Cells["TestName"].Value = result.TestCase.Name;
                row.Cells["Status"].Value = result.Passed ? "✓" : "✗";
                row.Cells["Passed"].Value = result.PassedChecks;
                row.Cells["Failed"].Value = result.FailedChecks;
                row.Cells["Time"].Value = result.Logger?.ExecutionTimeMs ?? 0;
                row.Cells["File"].Value = Path.GetFileName(result.TestCase.OvrFilePath);
                row.Cells["Error"].Value = Truncate(result.ErrorMessage, 100);

                // Устанавливаем цвет статуса
                if (!result.Passed)
                {
                    row.Cells["Status"].Style.ForeColor = Color.FromArgb(244, 135, 113);
                    row.Cells["Status"].Style.Font = new Font(_summaryGrid.Font, FontStyle.Bold);
                }
                else
                {
                    row.Cells["Status"].Style.ForeColor = Color.FromArgb(106, 153, 85);
                    row.Cells["Status"].Style.Font = new Font(_summaryGrid.Font, FontStyle.Bold);
                }

                row.Tag = result;
            }

            // Добавляем обработчик двойного щелчка для перехода к деталям
            _summaryGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && _summaryGrid.Rows[e.RowIndex].Tag is TestResult result)
                {
                    SelectTest(result.TestCase.Id);
                }
            };
        }

        private void FilterResults()
        {
            if (_treeView.InvokeRequired)
            {
                _treeView.Invoke(new Action(FilterResults));
                return;
            }

            string filter = _filterBox.Text.Trim().ToLower();

            _treeView.BeginUpdate();

            try
            {
                foreach (TreeNode categoryNode in _treeView.Nodes)
                {
                    bool categoryHasVisible = false;

                    foreach (TreeNode testNode in categoryNode.Nodes)
                    {
                        if (testNode.Tag is TestResult result)
                        {
                            bool testMatches = string.IsNullOrEmpty(filter) ||
                                              result.TestCase.Name.ToLower().Contains(filter) ||
                                              result.TestCase.Id.ToLower().Contains(filter) ||
                                              (result.TestCase.Description?.ToLower().Contains(filter) ?? false);

                            // Проверяем дочерние узлы
                            bool hasMatchingChild = false;
                            foreach (TreeNode cellNode in testNode.Nodes)
                            {
                                bool cellMatches = cellNode.Text.ToLower().Contains(filter);

                                // Проверяем подузлы клетки
                                foreach (TreeNode subNode in cellNode.Nodes)
                                {
                                    if (subNode.Text.ToLower().Contains(filter))
                                    {
                                        cellMatches = true;
                                        break;
                                    }
                                }

                                if (cellMatches)
                                {
                                    hasMatchingChild = true;
                                }
                            }

                            if (testMatches || hasMatchingChild)
                            {
                                categoryHasVisible = true;
                            }
                        }
                    }

                    // Разворачиваем/сворачиваем категории в зависимости от наличия совпадений
                    if (!string.IsNullOrEmpty(filter))
                    {
                        if (categoryHasVisible)
                        {
                            categoryNode.Expand();
                        }
                        else
                        {
                            categoryNode.Collapse();
                        }
                    }
                    else
                    {
                        categoryNode.Expand();
                    }
                }
            }
            finally
            {
                _treeView.EndUpdate();
            }
        }

        private void SelectTest(string testId)
        {
            foreach (TreeNode categoryNode in _treeView.Nodes)
            {
                foreach (TreeNode testNode in categoryNode.Nodes)
                {
                    if (testNode.Tag is TestResult result && result.TestCase.Id == testId)
                    {
                        _treeView.SelectedNode = testNode;
                        testNode.EnsureVisible();
                        return;
                    }
                }
            }
        }

        private void SelectCell(string testId, Point cell, string viewMode)
        {
            foreach (TreeNode categoryNode in _treeView.Nodes)
            {
                foreach (TreeNode testNode in categoryNode.Nodes)
                {
                    if (testNode.Tag is not TestResult result || result.TestCase.Id != testId)
                        continue;

                    foreach (TreeNode cellNode in testNode.Nodes)
                    {
                        if (cellNode.Tag is not CellCheckResult cellResult)
                            continue;

                        bool sameCell = cellResult.Cell.Equals(cell);
                        bool sameMode = string.IsNullOrWhiteSpace(viewMode)
                            || string.Equals(cellResult.ViewMode, viewMode, StringComparison.Ordinal);

                        if (!sameCell || !sameMode)
                            continue;

                        categoryNode.Expand();
                        testNode.Expand();
                        _treeView.SelectedNode = cellNode;
                        cellNode.EnsureVisible();
                        return;
                    }

                    categoryNode.Expand();
                    testNode.Expand();
                    _treeView.SelectedNode = testNode;
                    testNode.EnsureVisible();
                    return;
                }
            }
        }

        private void TreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            _detailsBox.Clear();
            _selectedTestResult = null;
            _selectedCellResult = null;

            if (e.Node.Tag is TestResult testResult)
            {
                _selectedTestResult = testResult;
                ShowTestResult(testResult);
            }
            else if (e.Node.Tag is CellCheckResult cellResult)
            {
                _selectedCellResult = cellResult;
                _selectedTestResult = GetSelectedTestResult();
                ShowCellResult(cellResult);
            }

            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            if (_acceptActualButton != null)
                _acceptActualButton.Enabled = _selectedCellResult != null;

            if (_undoExpectationButton != null)
            {
                _undoExpectationButton.Enabled = _historyCount > 0;
                _undoExpectationButton.Text = _historyCount > 0
                    ? $"↶ Откатить ({_historyCount})"
                    : "↶ Откатить";
            }

            UpdateVariantOrderRecommendation();
        }

        private void UpdateVariantOrderRecommendation()
        {
            if (_acceptRecommendationLabel == null)
                return;

            VariantOrderRecommendation? recommendation = BuildVariantOrderRecommendation(_selectedCellResult);
            if (recommendation == null)
            {
                _acceptRecommendationLabel.Visible = false;
                _acceptRecommendationLabel.Text = "";
                return;
            }

            _acceptRecommendationLabel.Text =
                $"Рекомендация: принять - только порядок вариантов ({recommendation.VariantCount})";
            _acceptRecommendationLabel.Visible = true;
        }

        private static VariantOrderRecommendation? BuildVariantOrderRecommendation(CellCheckResult? cellResult)
        {
            if (cellResult == null || cellResult.Passed)
                return null;

            foreach (string expectedCandidate in SplitExpectedTextAlternatives(cellResult.Expected))
            {
                if (TryBuildVariantOrderRecommendation(expectedCandidate, cellResult.Actual, out var recommendation))
                    return recommendation;
            }

            return null;
        }

        private static bool TryBuildVariantOrderRecommendation(
            string expectedText,
            string actualText,
            out VariantOrderRecommendation? recommendation)
        {
            recommendation = null;

            List<DisplayedVariantGroup> expectedGroups = BuildDisplayedVariantGroups(expectedText);
            List<DisplayedVariantGroup> actualGroups = BuildDisplayedVariantGroups(actualText);

            foreach (DisplayedVariantGroup expectedGroup in expectedGroups)
            {
                foreach (DisplayedVariantGroup actualGroup in actualGroups)
                {
                    if (!string.Equals(expectedGroup.ScaffoldKey, actualGroup.ScaffoldKey, StringComparison.Ordinal))
                        continue;

                    if (!TryBuildVariantOrderRecommendation(expectedGroup, actualGroup, out recommendation))
                        continue;

                    return true;
                }
            }

            return false;
        }

        private static bool TryBuildVariantOrderRecommendation(
            DisplayedVariantGroup expectedGroup,
            DisplayedVariantGroup actualGroup,
            out VariantOrderRecommendation? recommendation)
        {
            recommendation = null;

            var expectedVariants = expectedGroup?.Variants ?? new List<DisplayedVariant>();
            var actualVariants = actualGroup?.Variants ?? new List<DisplayedVariant>();

            if (expectedVariants.Count != actualVariants.Count || expectedVariants.Count < 2)
                return false;

            if (!HaveSameMultiset(
                    expectedVariants.Select(v => v.BodyKey),
                    actualVariants.Select(v => v.BodyKey)))
            {
                return false;
            }

            if (!HaveSameMultiset(
                    expectedVariants.Select(v => v.HeaderBodyKey),
                    actualVariants.Select(v => v.HeaderBodyKey)))
            {
                return false;
            }

            if (SequencesEqual(
                    expectedVariants.Select(v => v.HeaderBodyKey),
                    actualVariants.Select(v => v.HeaderBodyKey)))
            {
                return false;
            }

            recommendation = new VariantOrderRecommendation
            {
                VariantCount = expectedVariants.Count
            };
            return true;
        }

        private static IEnumerable<string> SplitExpectedTextAlternatives(string expectedText)
        {
            if (string.IsNullOrWhiteSpace(expectedText))
                yield break;

            string[] lines = NormalizeTextForDiff(expectedText).Split('\n');
            var current = new List<string>();

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                bool isAlternativeSeparator =
                    string.Equals(trimmedLine, "ИЛИ", StringComparison.Ordinal) ||
                    string.Equals(trimmedLine, "РР›Р", StringComparison.Ordinal);

                if (isAlternativeSeparator)
                {
                    string alternative = string.Join("\n", current).Trim();
                    if (!string.IsNullOrWhiteSpace(alternative))
                        yield return alternative;

                    current.Clear();
                    continue;
                }

                current.Add(line);
            }

            string lastAlternative = string.Join("\n", current).Trim();
            if (!string.IsNullOrWhiteSpace(lastAlternative))
                yield return lastAlternative;
        }

        private static List<DisplayedVariantGroup> BuildDisplayedVariantGroups(string text)
        {
            var groups = new List<DisplayedVariantGroup>();
            if (string.IsNullOrWhiteSpace(text))
                return groups;

            string[] lines = NormalizeTextForDiff(text).Split('\n');
            var headers = new List<DisplayedVariantHeader>();

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = DisplayVariantHeaderRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                string number = match.Groups["number"].Value;
                headers.Add(new DisplayedVariantHeader
                {
                    LineIndex = i,
                    Depth = GetVariantNumberDepth(number),
                    ParentNumber = GetVariantParentNumber(number),
                    HeaderKey = NormalizeVariantHeaderForComparison(lines[i])
                });
            }

            if (headers.Count < 2)
                return groups;

            foreach (var siblingHeaders in headers
                .GroupBy(h => new { h.Depth, h.ParentNumber })
                .Select(group => group.OrderBy(h => h.LineIndex).ToList())
                .Where(group => group.Count >= 2)
                .OrderBy(group => group[0].LineIndex))
            {
                var group = new DisplayedVariantGroup
                {
                    ScaffoldKey = BuildVariantScaffoldKey(lines, headers, siblingHeaders)
                };

                foreach (DisplayedVariantHeader header in siblingHeaders)
                {
                    int bodyStartLine = header.LineIndex + 1;
                    int bodyEndLine = FindVariantBlockEndLine(headers, header, lines.Length);

                    group.Variants.Add(new DisplayedVariant
                    {
                        HeaderKey = header.HeaderKey,
                        BodyKey = BuildVariantBodyKey(lines, bodyStartLine, bodyEndLine)
                    });
                }

                groups.Add(group);
            }

            return groups;
        }

        private static int GetVariantNumberDepth(string number)
        {
            if (string.IsNullOrWhiteSpace(number))
                return 0;

            return number.Split('.').Length;
        }

        private static string GetVariantParentNumber(string number)
        {
            if (string.IsNullOrWhiteSpace(number))
                return string.Empty;

            int lastDot = number.LastIndexOf('.');
            return lastDot < 0
                ? string.Empty
                : number.Substring(0, lastDot);
        }

        private static int FindVariantBlockEndLine(
            List<DisplayedVariantHeader> headers,
            DisplayedVariantHeader currentHeader,
            int lineCount)
        {
            foreach (DisplayedVariantHeader header in headers
                .Where(h => h.LineIndex > currentHeader.LineIndex)
                .OrderBy(h => h.LineIndex))
            {
                if (header.Depth <= currentHeader.Depth)
                    return header.LineIndex;
            }

            return lineCount;
        }

        private static string BuildVariantScaffoldKey(
            string[] lines,
            List<DisplayedVariantHeader> allHeaders,
            List<DisplayedVariantHeader> removedHeaders)
        {
            var removedLineIndexes = new HashSet<int>();
            foreach (DisplayedVariantHeader header in removedHeaders)
            {
                int blockEnd = FindVariantBlockEndLine(allHeaders, header, lines.Length);
                for (int lineIndex = header.LineIndex; lineIndex < blockEnd; lineIndex++)
                    removedLineIndexes.Add(lineIndex);
            }

            var scaffoldLines = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (removedLineIndexes.Contains(i))
                    continue;

                scaffoldLines.Add(NormalizeVariantBodyLine(lines[i]));
            }

            return NormalizeScaffoldLines(scaffoldLines);
        }

        private static string NormalizeScaffoldLines(IEnumerable<string> lines)
        {
            var normalized = new List<string>();
            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                string normalizedLine = (line ?? string.Empty).TrimEnd();
                if (string.IsNullOrWhiteSpace(normalizedLine))
                {
                    if (normalized.Count > 0 && normalized[normalized.Count - 1].Length != 0)
                        normalized.Add(string.Empty);

                    continue;
                }

                normalized.Add(normalizedLine);
            }

            while (normalized.Count > 0 && normalized[0].Length == 0)
                normalized.RemoveAt(0);

            while (normalized.Count > 0 && normalized[normalized.Count - 1].Length == 0)
                normalized.RemoveAt(normalized.Count - 1);

            return string.Join("\n", normalized);
        }

        private static string BuildVariantBodyKey(string[] lines, int startLine, int endLine)
        {
            int start = Math.Max(0, startLine);
            int end = Math.Min(lines?.Length ?? 0, Math.Max(start, endLine));

            while (start < end && string.IsNullOrWhiteSpace(lines[start]))
                start++;

            while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
                end--;

            var normalizedLines = new List<string>();
            for (int i = start; i < end; i++)
                normalizedLines.Add(NormalizeVariantBodyLine(lines[i]));

            return string.Join("\n", normalizedLines);
        }

        private static string NormalizeVariantBodyLine(string line)
        {
            string normalizedLine = (line ?? string.Empty).TrimEnd();
            Match match = DisplayVariantHeaderRegex.Match(normalizedLine);
            if (!match.Success)
                return normalizedLine;

            string indent = match.Groups["indent"].Value;
            string headerTail = NormalizeVariantHeaderForComparison(normalizedLine);
            return string.IsNullOrEmpty(headerTail)
                ? indent + "Вариант"
                : indent + "Вариант " + headerTail;
        }

        private static string NormalizeVariantHeaderForComparison(string headerLine)
        {
            Match match = DisplayVariantHeaderRegex.Match((headerLine ?? string.Empty).TrimEnd());
            if (!match.Success)
                return string.Empty;

            string tail = match.Groups["tail"].Value.Trim();
            if (tail.StartsWith(":", StringComparison.Ordinal))
                tail = tail.Substring(1).Trim();

            if (tail.EndsWith(":", StringComparison.Ordinal))
                tail = tail.Substring(0, tail.Length - 1).Trim();

            return Regex.Replace(tail, @"\s+", " ").Trim();
        }

        private static bool HaveSameMultiset(IEnumerable<string> expected, IEnumerable<string> actual)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string value in expected ?? Enumerable.Empty<string>())
            {
                string key = value ?? string.Empty;
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }

            foreach (string value in actual ?? Enumerable.Empty<string>())
            {
                string key = value ?? string.Empty;
                if (!counts.TryGetValue(key, out int count) || count == 0)
                    return false;

                if (count == 1)
                    counts.Remove(key);
                else
                    counts[key] = count - 1;
            }

            return counts.Count == 0;
        }

        private static bool SequencesEqual(IEnumerable<string> expected, IEnumerable<string> actual)
        {
            using var expectedEnumerator = (expected ?? Enumerable.Empty<string>()).GetEnumerator();
            using var actualEnumerator = (actual ?? Enumerable.Empty<string>()).GetEnumerator();

            while (true)
            {
                bool hasExpected = expectedEnumerator.MoveNext();
                bool hasActual = actualEnumerator.MoveNext();

                if (hasExpected != hasActual)
                    return false;

                if (!hasExpected)
                    return true;

                if (!string.Equals(expectedEnumerator.Current, actualEnumerator.Current, StringComparison.Ordinal))
                    return false;
            }
        }

        private void ShowTestResult(TestResult result)
        {
            _detailsBox.Clear();

            AppendStyledText($"=== ТЕСТ: {result.TestCase.Name} ===\n\n",
                new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(255, 215, 0));

            AppendStyledText($"ID: {result.TestCase.Id}\n",
                _detailsBox.Font, Color.White);
            AppendStyledText($"Файл: {result.TestCase.OvrFilePath}\n",
                _detailsBox.Font, Color.White);
            AppendStyledText($"Конфигурация: {result.TestCase.OvrConfigName}\n",
                _detailsBox.Font, Color.White);
            AppendStyledText($"Описание: {result.TestCase.Description}\n\n",
                _detailsBox.Font, Color.White);

            AppendStyledText($"Статус: ",
                _detailsBox.Font, Color.White);
            AppendStyledText($"{(result.Passed ? "ПРОЙДЕН" : "ПРОВАЛЕН")}\n",
                new Font(_detailsBox.Font, FontStyle.Bold),
                result.Passed ? Color.FromArgb(106, 153, 85) : Color.FromArgb(244, 135, 113));

            AppendStyledText($"Проверок: {result.TotalChecks}, пройдено: {result.PassedChecks}, провалено: {result.FailedChecks}\n",
                _detailsBox.Font, Color.White);
            AppendStyledText($"Время: {result.Logger?.ExecutionTimeMs} мс\n\n",
                _detailsBox.Font, Color.White);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                AppendStyledText($"ОШИБКА: {result.ErrorMessage}\n\n",
                    new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(244, 135, 113));
            }

            EnsureCharDiffsLogged(result);

            if (result.Logger != null)
            {
                AppendStyledText("\n=== ЛОГ ВЫПОЛНЕНИЯ ===\n\n",
                    new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(78, 201, 176));

                int lineCount = 0;
                int maxLines = 300;
                int totalLines = result.Logger.CapturedLogs.Count;

                foreach (var log in result.Logger.CapturedLogs)
                {
                    if (lineCount++ < maxLines)
                    {
                        AppendStyledText(log + "\n",
                            new Font("Consolas", 9), Color.FromArgb(212, 212, 212));
                    }
                    else
                    {
                        AppendStyledText($"\n... и ещё {totalLines - maxLines} строк\n",
                            _detailsBox.Font, Color.Gray);
                        break;
                    }
                }
            }
        }

        private void EnsureCharDiffsLogged(TestResult result)
        {
            if (result?.Logger == null || result.Passed || result.CellResults == null)
                return;

            foreach (var cellResult in result.CellResults.Where(c => c != null && !c.Passed))
            {
                var diffResult = BuildCharDiff(cellResult.Expected, cellResult.Actual);
                if (!diffResult.HasDifferences)
                    continue;

                string header = $"=== ПОСИМВОЛЬНОЕ СРАВНЕНИЕ ДЛЯ КЛЕТКИ ({cellResult.Cell.X},{cellResult.Cell.Y}) ===";
                bool alreadyLogged = result.Logger.CapturedLogs.Any(log =>
                    string.Equals(log, header, StringComparison.Ordinal));

                if (!alreadyLogged)
                    result.Logger.LogLines(BuildDiffLogLines(cellResult, diffResult));
            }
        }

        private void ShowCellResult(CellCheckResult result)
        {
            _detailsBox.Clear();

            AppendStyledText($"=== КЛЕТКА ({result.Cell.X},{result.Cell.Y}) ===\n\n",
                new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(255, 215, 0));

            CharDiffResult diffResult = !result.Passed
                ? BuildCharDiff(result.Expected, result.Actual)
                : null;

            if (diffResult != null && diffResult.HasDifferences)
                LogCharDiffs(result, diffResult);

            AppendStyledText("Ожидаемый текст:\n",
                new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(156, 220, 254));
            if (diffResult != null && diffResult.HasDifferences)
                AppendDiffText(diffResult.ExpectedText, diffResult.Differences, true);
            else
                AppendStyledText(result.Expected + "\n\n",
                    _detailsBox.Font, Color.FromArgb(156, 220, 254));

            AppendStyledText("Фактический текст:\n",
                new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(206, 145, 120));
            if (diffResult != null && diffResult.HasDifferences)
                AppendDiffText(diffResult.ActualText, diffResult.Differences, false);
            else
                AppendStyledText(result.Actual + "\n\n",
                    _detailsBox.Font, Color.FromArgb(206, 145, 120));

            if (diffResult != null && diffResult.HasDifferences)
            {
                AppendStyledText("Посимвольные расхождения:\n",
                    new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(255, 215, 0));

                foreach (var line in BuildDiffLogLines(result, diffResult))
                {
                    AppendStyledText(line + "\n",
                        new Font("Consolas", 9), Color.FromArgb(255, 215, 0));
                }

                AppendStyledText("\n", _detailsBox.Font, Color.White);
            }

            AppendStyledText(new string('-', 50) + "\n\n",
                _detailsBox.Font, Color.Gray);

            AppendStyledText($"Результат: ",
                _detailsBox.Font, Color.White);
            AppendStyledText($"{(result.Passed ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")}\n\n",
                new Font(_detailsBox.Font, FontStyle.Bold),
                result.Passed ? Color.FromArgb(106, 153, 85) : Color.FromArgb(244, 135, 113));

            if (!string.IsNullOrEmpty(result.Notes))
            {
                AppendStyledText($"Заметки: {result.Notes}\n\n",
                    _detailsBox.Font, Color.FromArgb(255, 215, 0));
            }

            if (!string.IsNullOrEmpty(result.AnalysisTrace))
            {
                AppendStyledText("=== ТРАССИРОВКА АНАЛИЗА ===\n\n",
                    new Font(_detailsBox.Font, FontStyle.Bold), Color.FromArgb(78, 201, 176));

                AppendStyledText(result.AnalysisTrace,
                    new Font("Consolas", 9), Color.FromArgb(212, 212, 212));
            }
        }

        private static string NormalizeTextForDiff(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            return text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
        }

        private CharDiffResult BuildCharDiff(string expectedText, string actualText)
        {
            var result = new CharDiffResult
            {
                ExpectedText = NormalizeTextForDiff(expectedText),
                ActualText = NormalizeTextForDiff(actualText)
            };

            string expected = result.ExpectedText;
            string actual = result.ActualText;
            int expectedLength = expected.Length;
            int actualLength = actual.Length;

            var lcs = new int[expectedLength + 1, actualLength + 1];

            for (int i = expectedLength - 1; i >= 0; i--)
            {
                for (int j = actualLength - 1; j >= 0; j--)
                {
                    if (expected[i] == actual[j])
                        lcs[i, j] = lcs[i + 1, j + 1] + 1;
                    else
                        lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0;
            int y = 0;

            while (x < expectedLength && y < actualLength)
            {
                if (expected[x] == actual[y])
                {
                    x++;
                    y++;
                    continue;
                }

                if (lcs[x + 1, y] == lcs[x, y + 1])
                {
                    result.Differences.Add(new CharDiffEntry
                    {
                        Kind = CharDiffKind.Replace,
                        ExpectedIndex = x,
                        ActualIndex = y,
                        ExpectedChar = expected[x],
                        ActualChar = actual[y]
                    });
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] > lcs[x, y + 1])
                {
                    result.Differences.Add(new CharDiffEntry
                    {
                        Kind = CharDiffKind.Delete,
                        ExpectedIndex = x,
                        ActualIndex = y,
                        ExpectedChar = expected[x],
                        ActualChar = null
                    });
                    x++;
                }
                else
                {
                    result.Differences.Add(new CharDiffEntry
                    {
                        Kind = CharDiffKind.Insert,
                        ExpectedIndex = x,
                        ActualIndex = y,
                        ExpectedChar = null,
                        ActualChar = actual[y]
                    });
                    y++;
                }
            }

            while (x < expectedLength)
            {
                result.Differences.Add(new CharDiffEntry
                {
                    Kind = CharDiffKind.Delete,
                    ExpectedIndex = x,
                    ActualIndex = actualLength,
                    ExpectedChar = expected[x],
                    ActualChar = null
                });
                x++;
            }

            while (y < actualLength)
            {
                result.Differences.Add(new CharDiffEntry
                {
                    Kind = CharDiffKind.Insert,
                    ExpectedIndex = expectedLength,
                    ActualIndex = y,
                    ExpectedChar = null,
                    ActualChar = actual[y]
                });
                y++;
            }

            return result;
        }

        private IEnumerable<string> BuildDiffLogLines(CellCheckResult result, CharDiffResult diffResult)
        {
            yield return $"=== ПОСИМВОЛЬНОЕ СРАВНЕНИЕ ДЛЯ КЛЕТКИ ({result.Cell.X},{result.Cell.Y}) ===";
            yield return $"Всего расхождений: {diffResult.Differences.Count}";

            foreach (var diff in diffResult.Differences)
            {
                switch (diff.Kind)
                {
                    case CharDiffKind.Insert:
                        yield return $"Позиция {diff.ActualIndex + 1}: ожидался <отсутствует>, фактически {FormatCharForLog(diff.ActualChar)}";
                        break;

                    case CharDiffKind.Delete:
                        yield return $"Позиция {diff.ExpectedIndex + 1}: ожидался {FormatCharForLog(diff.ExpectedChar)}, фактически <отсутствует>";
                        break;

                    default:
                        yield return $"Позиция {Math.Max(diff.ExpectedIndex, diff.ActualIndex) + 1}: ожидался {FormatCharForLog(diff.ExpectedChar)}, фактически {FormatCharForLog(diff.ActualChar)}";
                        break;
                }
            }
        }

        private void LogCharDiffs(CellCheckResult result, CharDiffResult diffResult)
        {
            var testResult = GetSelectedTestResult();
            if (testResult?.Logger == null)
                return;

            string header = $"=== ПОСИМВОЛЬНОЕ СРАВНЕНИЕ ДЛЯ КЛЕТКИ ({result.Cell.X},{result.Cell.Y}) ===";
            bool alreadyLogged = testResult.Logger.CapturedLogs.Any(log =>
                string.Equals(log, header, StringComparison.Ordinal));

            if (alreadyLogged)
                return;

            testResult.Logger.LogLines(BuildDiffLogLines(result, diffResult));
        }

        private TestResult GetSelectedTestResult()
        {
            TreeNode node = _treeView.SelectedNode;
            while (node != null)
            {
                if (node.Tag is TestResult testResult)
                    return testResult;

                node = node.Parent;
            }

            return null;
        }

        private void AppendDiffText(string text, IEnumerable<CharDiffEntry> differences, bool expectedSide)
        {
            Color normalColor = expectedSide
                ? Color.FromArgb(156, 220, 254)
                : Color.FromArgb(206, 145, 120);

            var diffIndexes = new HashSet<int>();
            if (differences != null)
            {
                foreach (var diff in differences)
                {
                    char? relevantChar = expectedSide ? diff.ExpectedChar : diff.ActualChar;
                    if (!relevantChar.HasValue)
                        continue;

                    int charIndex = expectedSide ? diff.ExpectedIndex : diff.ActualIndex;
                    if (charIndex >= 0)
                        diffIndexes.Add(charIndex);
                }
            }

            string sourceText = text ?? string.Empty;
            for (int i = 0; i < sourceText.Length; i++)
            {
                bool highlight = diffIndexes.Contains(i);
                string displayChunk = highlight
                    ? GetVisibleDiffChunk(sourceText[i])
                    : sourceText[i].ToString();

                AppendStyledText(
                    displayChunk,
                    highlight ? new Font(_detailsBox.Font, FontStyle.Bold) : _detailsBox.Font,
                    highlight ? Color.White : normalColor);

                if (highlight)
                {
                    _detailsBox.Select(_detailsBox.TextLength - displayChunk.Length, displayChunk.Length);
                    _detailsBox.SelectionBackColor = Color.FromArgb(120, 180, 50, 50);
                }
            }

            AppendStyledText("\n\n", _detailsBox.Font, normalColor);
            _detailsBox.Select(_detailsBox.TextLength, 0);
            _detailsBox.SelectionBackColor = _detailsBox.BackColor;
            _detailsBox.SelectionColor = _detailsBox.ForeColor;
            _detailsBox.SelectionFont = _detailsBox.Font;
        }

        private string GetVisibleDiffChunk(char value)
        {
            switch (value)
            {
                case '\r':
                    return "␍";
                case '\n':
                    return "␊";
                case '\t':
                    return "⇥";
                default:
                    return char.IsControl(value)
                        ? $"\\u{(int)value:X4}"
                        : value.ToString();
            }
        }

        private string FormatCharForLog(char? value)
        {
            if (!value.HasValue)
                return "<отсутствует>";

            switch (value.Value)
            {
                case '\r':
                    return "'\\r'";
                case '\n':
                    return "'\\n'";
                case '\t':
                    return "'\\t'";
                case ' ':
                    return "'пробел'";
                default:
                    return $"'{value.Value}'";
            }
        }

        /// <summary>
        /// Извлечь чистый текст из строки анализатора для отображения
        /// </summary>
        private string ExtractCleanTextForDisplay(string analyzerText)
        {
            if (string.IsNullOrEmpty(analyzerText))
                return analyzerText;

            // Ищем паттерн "Text at 0xXXXX: "
            int colonIndex = analyzerText.IndexOf(": ");
            if (colonIndex >= 0 && analyzerText.StartsWith("Text at 0x"))
            {
                return analyzerText.Substring(colonIndex + 2);
            }

            return analyzerText;
        }

        /// <summary>
        /// Декодировать строку для отображения (преобразовать \r в реальные переносы)
        /// </summary>
        private string DecodeStringForDisplay(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return input
                .Replace("\\r", "\r")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private void AppendStyledText(string text, Font font, Color color)
        {
            _detailsBox.SelectionStart = _detailsBox.TextLength;
            _detailsBox.SelectionLength = 0;
            _detailsBox.SelectionFont = font;
            _detailsBox.SelectionColor = color;
            _detailsBox.AppendText(text);
        }

        private void RunSelectedTest(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is TestResult oldResult)
            {
                RunTestAndUpdate(oldResult.TestCase);
            }
            else if (_treeView.SelectedNode?.Tag is CellCheckResult cellResult)
            {
                // Ищем родительский тест для этой клетки
                var parentNode = _treeView.SelectedNode.Parent?.Parent;
                if (parentNode?.Tag is TestResult parentTest)
                {
                    RunTestAndUpdate(parentTest.TestCase);
                }
            }
        }

        private TestResult? RunTestAndUpdate(
            TestCase testCase,
            bool showCompletionMessage = true,
            Point? cellToSelect = null,
            string? viewModeToSelect = null)
        {
            Cursor = Cursors.WaitCursor;

            try
            {
                var runner = new OvrAnalyzerTestRunner();
                var newResult = runner.RunTest(testCase, null);

                // Обновляем результат в списке
                var index = _results.FindIndex(r => r.TestCase.Id == newResult.TestCase.Id);
                if (index >= 0)
                    _results[index] = newResult;

                // Обновляем отображение
                PopulateResults();

                // Показываем новый результат
                if (cellToSelect.HasValue)
                    SelectCell(newResult.TestCase.Id, cellToSelect.Value, viewModeToSelect);
                else
                    SelectTest(newResult.TestCase.Id);

                if (showCompletionMessage)
                {
                    MessageBox.Show($"Тест '{testCase.Name}' завершен.\n" +
                                  $"Пройдено: {newResult.PassedChecks}/{newResult.TotalChecks}\n" +
                                  $"Время: {newResult.Logger?.ExecutionTimeMs} мс",
                                  "Тест завершен",
                                  MessageBoxButtons.OK,
                                  newResult.Passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }

                return newResult;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при запуске теста: {ex.Message}",
                              "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void AcceptActualAsExpected(object sender, EventArgs e)
        {
            if (_selectedCellResult == null || _selectedTestResult == null)
                return;

            if (string.IsNullOrWhiteSpace(_selectedCellResult.Actual))
            {
                DialogResult emptyResultConfirmation = MessageBox.Show(
                    "Фактический результат пустой.\n\n" +
                    "Будет установлен флаг пустого ожидания для клетки, а сохранённые тексты для обоих режимов очистятся.\n\n" +
                    "Продолжить?",
                    "Подтверждение изменения",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (emptyResultConfirmation != DialogResult.OK)
                    return;
            }

            Cursor = Cursors.WaitCursor;

            try
            {
                TestExpectationUpdateResult updateResult = _expectationStore.AcceptActualAsExpected(
                    _selectedTestResult.TestCase.Id,
                    _selectedCellResult.Cell,
                    _selectedCellResult.ViewMode,
                    _selectedCellResult.Actual);

                if (!updateResult.Success)
                {
                    MessageBox.Show(updateResult.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _historyCount = _expectationStore.GetHistoryCount();
                UpdateActionButtons();

                if (!updateResult.Changed)
                {
                    MessageBox.Show(updateResult.Message, "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                TestResult? refreshedResult = RunTestAndUpdate(
                    updateResult.TestCase,
                    showCompletionMessage: false,
                    cellToSelect: updateResult.Cell,
                    viewModeToSelect: updateResult.ViewMode);

                if (refreshedResult != null)
                {
                    MessageBox.Show(updateResult.Message,
                        "Ожидание обновлено",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось обновить ожидаемый результат: {ex.Message}",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UndoLastExpectationChange(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;

            try
            {
                TestExpectationUpdateResult rollbackResult = _expectationStore.RollbackLastChange();

                if (!rollbackResult.Success)
                {
                    MessageBox.Show(rollbackResult.Message, "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _historyCount = _expectationStore.GetHistoryCount();
                UpdateActionButtons();

                TestResult? refreshedResult = RunTestAndUpdate(
                    rollbackResult.TestCase,
                    showCompletionMessage: false,
                    cellToSelect: rollbackResult.Cell,
                    viewModeToSelect: rollbackResult.ViewMode);

                if (refreshedResult != null)
                {
                    MessageBox.Show(rollbackResult.Message,
                        "Откат выполнен",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось откатить последнее изменение: {ex.Message}",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void RunAllTests(object sender, EventArgs e)
        {
            string selectedFile = _ovrFileCombo.SelectedItem?.ToString();

            IEnumerable<TestCase> testCasesQuery;
            if (selectedFile == "Все файлы" || string.IsNullOrEmpty(selectedFile))
            {
                testCasesQuery = _results.Select(r => r.TestCase);
            }
            else
            {
                testCasesQuery = _results.Where(r => r.TestCase.OvrFilePath == selectedFile)
                                         .Select(r => r.TestCase);
            }

            var testCasesToRun = testCasesQuery.ToList();
            int count = testCasesToRun.Count;

            if (count == 0)
            {
                MessageBox.Show("Нет тестов для запуска", "Информация",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirmResult = MessageBox.Show(
                $"Запустить {count} тестов?\nЭто может занять некоторое время.",
                "Подтверждение",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (confirmResult != DialogResult.OK)
                return;

            Cursor = Cursors.WaitCursor;

            try
            {
                var runner = new OvrAnalyzerTestRunner();
                var newResults = runner.RunTests(testCasesToRun);

                // Обновляем результаты
                foreach (var newResult in newResults)
                {
                    var index = _results.FindIndex(r => r.TestCase.Id == newResult.TestCase.Id);
                    if (index >= 0)
                        _results[index] = newResult;
                }

                PopulateResults();

                int passed = newResults.Count(r => r.Passed);
                MessageBox.Show($"Запущено тестов: {newResults.Count}\n" +
                              $"Пройдено: {passed}\n" +
                              $"Провалено: {newResults.Count - passed}",
                              "Тестирование завершено",
                              MessageBoxButtons.OK,
                              passed == newResults.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при запуске тестов: {ex.Message}",
                              "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ExportHtml(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "HTML files (*.html)|*.html";
                dialog.FileName = $"TestReport_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                dialog.Title = "Сохранить HTML отчёт";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        //заглушка
                        //var exporter = new TestReportExporter();
                        //exporter.ExportToHtml(_results, dialog.FileName);

                        //MessageBox.Show($"Отчёт сохранён в:\n{dialog.FileName}",
                        //    "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при экспорте: {ex.Message}",
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportText(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Text files (*.txt)|*.txt";
                dialog.FileName = $"TestReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                dialog.Title = "Сохранить текстовый отчёт";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        //загрушка
                        //var exporter = new TestReportExporter();
                        //exporter.ExportToText(_results, dialog.FileName);

                        //MessageBox.Show($"Отчёт сохранён в:\n{dialog.FileName}",
                        //    "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при экспорте: {ex.Message}",
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private string Truncate(string text, int maxLength = 150, int maxLines = 3)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return "";

            // Если всего строк немного, показываем их все
            if (lines.Length <= maxLines)
            {
                string result = string.Join("\n", lines);
                if (result.Length <= maxLength)
                    return result;
            }

            // Показываем первые maxLines строк
            var firstLines = lines.Take(maxLines).ToList();
            string preview = string.Join("\n", firstLines);

            // Если есть ещё строки, добавляем индикатор
            if (lines.Length > maxLines)
            {
                preview += $"\n... и ещё {lines.Length - maxLines} строк";
            }
            // Если текст слишком длинный даже в первых строках, обрезаем
            else if (preview.Length > maxLength)
            {
                preview = preview.Substring(0, maxLength) + "...";
            }

            return preview;
        }
    }
}
