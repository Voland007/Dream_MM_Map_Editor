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


﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Результат выполнения одного теста
    /// </summary>
    public class TestResult
    {
        public TestCase TestCase { get; set; }
        public bool Passed { get; set; }
        public List<CellCheckResult> CellResults { get; set; } = new List<CellCheckResult>();
        public TestLogger Logger { get; set; }
        public string ErrorMessage { get; set; }

        public int TotalChecks => CellResults.Count;
        public int PassedChecks => CellResults.Count(r => r.Passed);
        public int FailedChecks => CellResults.Count(r => !r.Passed);
    }

    /// <summary>
    /// Результат проверки конкретной клетки
    /// </summary>
    public class CellCheckResult
    {
        public Point Cell { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public bool Passed { get; set; }
        public string AnalysisTrace { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Основной класс для запуска тестов анализатора
    /// </summary>
    public class OvrAnalyzerTestRunner
    {
        private readonly Dictionary<string, OvrFileConfig> _configs;

        public OvrAnalyzerTestRunner()
        {
            _configs = OvrFileConfigs.Configs;
        }

        /// <summary>
        /// Загрузить тесты из JSON файла
        /// </summary>
        public List<TestCase> LoadTestCases(string jsonFilePath)
        {
            if (!File.Exists(jsonFilePath))
            {
                System.Diagnostics.Debug.WriteLine($"Файл тестов не найден: {jsonFilePath}");
                return new List<TestCase>();
            }

            try
            {
                string json = File.ReadAllText(jsonFilePath, Encoding.UTF8);
                System.Diagnostics.Debug.WriteLine($"Загружен JSON длиной: {json.Length} символов");

                var settings = new JsonSerializerSettings
                {
                    Error = (sender, args) =>
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка десериализации: {args.ErrorContext.Error.Message}");
                        args.ErrorContext.Handled = true;
                    }
                };

                // Создаем список конвертеров
                settings.Converters = new List<JsonConverter>();

                // Добавляем конвертер для словаря с ключами Point
                var converter = new PointDictionaryConverter();
                settings.Converters.Add(converter);

                // Десериализуем JSON
                var testCases = JsonConvert.DeserializeObject<List<TestCase>>(json, settings);

                if (testCases != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Десериализовано тестов: {testCases.Count}");

                    foreach (var testCase in testCases)
                    {
                        System.Diagnostics.Debug.WriteLine($"Тест: {testCase.Name}, клеток: {testCase.ExpectedCellTexts?.Count ?? 0}");

                        // Для отладки выведем все клетки
                        if (testCase.ExpectedCellTexts != null)
                        {
                            foreach (var kvp in testCase.ExpectedCellTexts)
                            {
                                System.Diagnostics.Debug.WriteLine($"  Клетка ({kvp.Key.X},{kvp.Key.Y}): {kvp.Value.ExpectedTexts?.Count ?? 0} текстов, пустая: {kvp.Value.ShouldBeEmpty}");
                            }
                        }
                    }
                }

                return testCases ?? new List<TestCase>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при загрузке тестов: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");

                MessageBox.Show($"Ошибка при загрузке тестов:\n{ex.Message}\n\nПроверьте формат JSON файла.",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return new List<TestCase>();
            }
        }

        /// <summary>
        /// Сохранить тесты в JSON файл
        /// </summary>
        public void SaveTestCases(string jsonFilePath, List<TestCase> testCases)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    Converters = new List<JsonConverter> { new PointDictionaryConverter() }
                };

                string json = JsonConvert.SerializeObject(testCases, settings);
                File.WriteAllText(jsonFilePath, json, Encoding.UTF8);

                System.Diagnostics.Debug.WriteLine($"Тесты сохранены в: {jsonFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при сохранении тестов: {ex.Message}");
                MessageBox.Show($"Ошибка при сохранении тестов:\n{ex.Message}",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Создать пример тестового файла
        /// </summary>
        public void CreateExampleTestFile(string filePath)
        {
            var exampleTests = new List<TestCase>
            {
                new TestCase
                {
                    Id = "EXAMPLE_001",
                    Name = "Пример теста 1",
                    OvrFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MM1_Maps", "UNDERWOR.OVR"),
                    OvrConfigName = "UNDERWOR.OVR",
                    Description = "Пример тестового случая для подземелья",
                    ExpectedCellTexts = new Dictionary<Point, CellExpectation>
                    {
                        {
                            new Point(2, 3),
                            new CellExpectation
                            {
                                ExpectedTexts = new List<string> { "Text at 0x1234: Пример текста" },
                                ShouldBeEmpty = false,
                                Comment = "Тестовый текст"
                            }
                        },
                        {
                            new Point(5, 8),
                            new CellExpectation
                            {
                                ExpectedTexts = new List<string>(),
                                ShouldBeEmpty = true,
                                Comment = "Пустая клетка"
                            }
                        }
                    }
                }
            };

            SaveTestCases(filePath, exampleTests);
        }

        /// <summary>
        /// Запустить один тест
        /// </summary>
        /// <param name="testCase">Тестовый случай</param>
        /// <param name="existingCentralOptions">Существующие центральные опции</param>
        /// <param name="existingNotes">Заметки из notesPerCell</param>
        public TestResult RunTest(TestCase testCase, Dictionary<Point, string> existingCentralOptions = null, Dictionary<Point, string> existingNotes = null)
        {
            var result = new TestResult { TestCase = testCase };

            System.Diagnostics.Debug.WriteLine($"\n=== ЗАПУСК ТЕСТА: {testCase.Name} ===");
            System.Diagnostics.Debug.WriteLine($"Клеток для проверки: {testCase.ExpectedCellTexts.Count}");

            try
            {
                // Проверяем существование файла
                if (!File.Exists(testCase.OvrFilePath))
                {
                    result.Passed = false;
                    result.ErrorMessage = $"Файл {testCase.OvrFilePath} не найден";
                    System.Diagnostics.Debug.WriteLine($"ОШИБКА: {result.ErrorMessage}");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"Файл существует: {Path.GetFileName(testCase.OvrFilePath)}");

                // Проверяем наличие конфигурации
                string configKey = testCase.OvrConfigName ?? Path.GetFileName(testCase.OvrFilePath).ToUpper();
                if (!_configs.ContainsKey(configKey))
                {
                    result.Passed = false;
                    result.ErrorMessage = $"Конфигурация {configKey} не найдена";
                    System.Diagnostics.Debug.WriteLine($"ОШИБКА: {result.ErrorMessage}");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"Конфигурация найдена: {configKey}");

                var config = _configs[configKey];

                // Создаём логгер
                using (var logger = new TestLogger(testCase.Id))
                {
                    result.Logger = logger;

                    // СОЗДАЁМ СЛОВАРЬ CENTRAL OPTIONS ТОЛЬКО ДЛЯ КЛЕТОК, КОТОРЫЕ НЕ ДОЛЖНЫ БЫТЬ ПУСТЫМИ
                    var centralOptions = new Dictionary<Point, string>();

                    // Добавляем клетки со значением "Случайная встреча" ТОЛЬКО если они НЕ должны быть пустыми
                    foreach (var kvp in testCase.ExpectedCellTexts)
                    {
                        var cellPos = kvp.Key;
                        var expectation = kvp.Value;

                        // Если клетка НЕ должна быть пустой, устанавливаем "Случайная встреча"
                        if (!expectation.ShouldBeEmpty)
                        {
                            centralOptions[cellPos] = "Случайная встреча";
                            System.Diagnostics.Debug.WriteLine($"  Установлена centralOptions[{cellPos}] = \"Случайная встреча\"");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"  Клетка {cellPos} должна быть пустой, НЕ устанавливаем centralOptions");
                        }
                    }

                    // Если переданы существующие centralOptions, добавляем их поверх (с приоритетом)
                    if (existingCentralOptions != null)
                    {
                        foreach (var kvp in existingCentralOptions)
                        {
                            centralOptions[kvp.Key] = kvp.Value;
                        }
                    }

                    System.Diagnostics.Debug.WriteLine("Запуск анализатора...");

                    // Запускаем анализатор
                    var objects = OvrFileAnalyzer.AnalyzeOvrFile(
                        testCase.OvrFilePath,
                        config,
                        centralOptions
                    );

                    System.Diagnostics.Debug.WriteLine($"Анализатор вернул {objects.Count} объектов");

                    // После анализатора centralOptions может быть изменён

                    // Проверяем каждую ожидаемую клетку
                    foreach (var kvp in testCase.ExpectedCellTexts)
                    {
                        var cellPos = kvp.Key;
                        var expectation = kvp.Value;

                        System.Diagnostics.Debug.WriteLine($"\nПроверка клетки ({cellPos.X},{cellPos.Y}):");
                        System.Diagnostics.Debug.WriteLine($"  Ожидание: {expectation.GetDescription()}");

                        // Находим объект для этой клетки
                        var cellObject = objects.FirstOrDefault(o => o.X == cellPos.X && o.Y == cellPos.Y);

                        if (cellObject == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Объект для клетки не найден в результатах анализатора");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"  Объект найден: IsFromTable={cellObject.IsFromTable}, путей={cellObject.PathTexts.Count}");
                        }

                        // СОБИРАЕМ ВСЕ ТЕКСТЫ ДЛЯ КЛЕТКИ
                        var allTexts = new HashSet<string>();

                        // 1. Тексты из путей (PathTexts)
                        if (cellObject != null)
                        {
                            foreach (var pathTexts in cellObject.PathTexts.Values)
                            {
                                foreach (var text in pathTexts)
                                {
                                    allTexts.Add(text);
                                    string preview = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                                    System.Diagnostics.Debug.WriteLine($"  Найден текст из пути: {preview}");
                                }
                            }

                            // 2. Информация о силе монстров
                            if (cellObject.MonsterPower.HasValue)
                            {
                                string powerText = $"Сила монстров: {cellObject.MonsterPower.Value}";
                                allTexts.Add(powerText);
                                System.Diagnostics.Debug.WriteLine($"  Найдена сила монстров: {powerText}");
                            }

                            // 3. Информация об уровне монстров
                            if (cellObject.MonsterLevel.HasValue)
                            {
                                string levelText = $"Уровень монстров: {cellObject.MonsterLevel.Value}";
                                allTexts.Add(levelText);
                                System.Diagnostics.Debug.WriteLine($"  Найден уровень монстров: {levelText}");
                            }

                            // 4. Информация о битвах с монстрами
                            foreach (var battle in cellObject.BattleMonsters)
                            {
                                string battleText;

                                if (battle.IsIndeterminate)
                                {
                                    battleText = $"Битва: {battle.MonsterName} x? (Random count)";
                                }
                                else
                                {
                                    battleText = $"Битва: {battle.MonsterName}";
                                }

                                allTexts.Add(battleText);
                                System.Diagnostics.Debug.WriteLine($"  Найдена битва: {battleText}");
                            }

                            // 5. Информация о частично определённых битвах
                            foreach (var partial in cellObject.PartiallyDefinedBattles)
                            {
                                var possibleMonsters = partial.GetPossibleMonsters();

                                if (possibleMonsters.Count == 1)
                                {
                                    string partialText = $"Частично определённая битва: {possibleMonsters[0].MonsterName}";
                                    allTexts.Add(partialText);
                                }
                                else if (possibleMonsters.Count > 0)
                                {
                                    string partialText = $"Частично определённая битва ({possibleMonsters.Count} вариантов)";
                                    allTexts.Add(partialText);
                                }
                            }

                            // 6. Полное описание битвы
                            string fullBattleDesc = cellObject.GetBattleDescription();
                            if (!string.IsNullOrEmpty(fullBattleDesc))
                            {
                                allTexts.Add(fullBattleDesc);
                            }
                        }

                        // 7. ЗАМЕТКИ ИЗ CENTRAL OPTIONS (после анализатора)
                        if (centralOptions != null && centralOptions.TryGetValue(cellPos, out string centralNote))
                        {
                            if (!string.IsNullOrEmpty(centralNote) && centralNote != "Случайная встреча")
                            {
                                foreach (string line in centralNote.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string trimmedLine = line.Trim();
                                    if (!string.IsNullOrEmpty(trimmedLine) && !allTexts.Contains(trimmedLine))
                                    {
                                        allTexts.Add(trimmedLine);
                                        System.Diagnostics.Debug.WriteLine($"  Найдена заметка из centralOptions: {trimmedLine}");
                                    }
                                }
                            }
                        }

                        // 8. ЗАМЕТКИ ИЗ existingNotes (notesPerCell)
                        if (existingNotes != null && existingNotes.TryGetValue(cellPos, out string notesNote))
                        {
                            if (!string.IsNullOrEmpty(notesNote))
                            {
                                foreach (string line in notesNote.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string trimmedLine = line.Trim();
                                    if (!string.IsNullOrEmpty(trimmedLine) && !allTexts.Contains(trimmedLine))
                                    {
                                        allTexts.Add(trimmedLine);
                                        System.Diagnostics.Debug.WriteLine($"  Найдена заметка из notesPerCell: {trimmedLine}");
                                    }
                                }
                            }
                        }

                        // Формируем итоговый текст
                        string actualText = allTexts.Count > 0
                            ? string.Join("\n", allTexts.OrderBy(t => t))
                            : "";

                        string actualPreview = string.IsNullOrEmpty(actualText)
                            ? "<пусто>"
                            : (actualText.Length > 50 ? actualText.Substring(0, 50) + "..." : actualText);
                        System.Diagnostics.Debug.WriteLine($"  Фактический текст: {actualPreview}");

                        // Проверяем соответствие ожиданию
                        bool passed = expectation.Matches(actualText);
                        System.Diagnostics.Debug.WriteLine($"  Результат: {(passed ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")}");

                        // Сохраняем результат для клетки
                        var cellResult = new CellCheckResult
                        {
                            Cell = cellPos,
                            Expected = expectation.GetDescription(),
                            Actual = actualText,
                            Passed = passed,
                            AnalysisTrace = logger.GetAnalysisTrace(cellPos.X, cellPos.Y)
                        };

                        result.CellResults.Add(cellResult);
                    }

                    // Тест пройден, если ВСЕ проверки успешны
                    result.Passed = result.CellResults.All(r => r.Passed);
                    System.Diagnostics.Debug.WriteLine($"\nИТОГ: пройдено {result.PassedChecks}/{result.TotalChecks}");
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.ErrorMessage = ex.ToString();
                System.Diagnostics.Debug.WriteLine($"ИСКЛЮЧЕНИЕ: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            return result;
        }

        /// <summary>
        /// Запустить несколько тестов
        /// </summary>
        /// <param name="testCases">Список тестовых случаев для запуска</param>
        /// <param name="existingNotes">Заметки из notesPerCell (необязательно)</param>
        /// <returns>Список результатов тестов</returns>
        public List<TestResult> RunTests(List<TestCase> testCases, Dictionary<Point, string> existingNotes = null)
        {
            var results = new List<TestResult>();

            System.Diagnostics.Debug.WriteLine($"\n==========================================");
            System.Diagnostics.Debug.WriteLine($"ЗАПУСК ТЕСТОВ: {testCases.Count} тестов");
            System.Diagnostics.Debug.WriteLine($"==========================================\n");

            int passedCount = 0;
            int failedCount = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (var testCase in testCases)
            {
                System.Diagnostics.Debug.WriteLine($"\n========================================");
                System.Diagnostics.Debug.WriteLine($"Запуск теста: {testCase.Name} ({testCase.Id})");
                System.Diagnostics.Debug.WriteLine($"========================================");

                // Запускаем тест
                var result = RunTest(testCase, null, existingNotes);
                results.Add(result);

                if (result.Passed)
                {
                    passedCount++;
                    System.Diagnostics.Debug.WriteLine($"\n✅ ТЕСТ ПРОЙДЕН: {testCase.Name}");
                }
                else
                {
                    failedCount++;
                    System.Diagnostics.Debug.WriteLine($"\n❌ ТЕСТ ПРОВАЛЕН: {testCase.Name}");
                }

                System.Diagnostics.Debug.WriteLine($"   Проверок: {result.TotalChecks}, пройдено: {result.PassedChecks}, провалено: {result.FailedChecks}");
                System.Diagnostics.Debug.WriteLine($"   Время: {result.Logger?.ExecutionTimeMs} мс");

                // Если есть ошибка, выводим её
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    System.Diagnostics.Debug.WriteLine($"   Ошибка: {result.ErrorMessage}");
                }

                // Выводим детали по проваленным клеткам
                foreach (var cellResult in result.CellResults.Where(r => !r.Passed))
                {
                    System.Diagnostics.Debug.WriteLine($"   ❌ Клетка ({cellResult.Cell.X},{cellResult.Cell.Y}):");
                    System.Diagnostics.Debug.WriteLine($"      Ожидаемый: {cellResult.Expected}");
                    System.Diagnostics.Debug.WriteLine($"      Фактический: {cellResult.Actual}");
                }
            }

            stopwatch.Stop();

            System.Diagnostics.Debug.WriteLine($"\n==========================================");
            System.Diagnostics.Debug.WriteLine($"ИТОГИ ТЕСТИРОВАНИЯ");
            System.Diagnostics.Debug.WriteLine($"==========================================");
            System.Diagnostics.Debug.WriteLine($"Всего тестов: {testCases.Count}");
            System.Diagnostics.Debug.WriteLine($"Пройдено: {passedCount}");
            System.Diagnostics.Debug.WriteLine($"Провалено: {failedCount}");
            System.Diagnostics.Debug.WriteLine($"Общее время: {stopwatch.ElapsedMilliseconds} мс");
            System.Diagnostics.Debug.WriteLine($"==========================================\n");

            return results;
        }

        /// <summary>
        /// Запустить несколько тестов (перегрузка без параметров)
        /// </summary>
        public List<TestResult> RunTests(List<TestCase> testCases)
        {
            return RunTests(testCases, null);
        }
    }
}