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

        public TestResult RunTest(
    TestCase testCase,
    Dictionary<Point, string> existingCentralOptions = null,
    Dictionary<Point, string> existingNotes = null)
        {
            var result = new TestResult { TestCase = testCase };

            System.Diagnostics.Debug.WriteLine($"\n=== ЗАПУСК ТЕСТА: {testCase.Name} ===");
            System.Diagnostics.Debug.WriteLine($"Клеток для проверки: {testCase.ExpectedCellTexts?.Count ?? 0}");

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

                using (var logger = new TestLogger(testCase.Id))
                {
                    result.Logger = logger;

                    // Готовим centralOptions только для тестируемых клеток:
                    // для непустых ожиданий ставим "Случайная встреча",
                    // чтобы builder построил текст так же, как в боевом коде.
                    var centralOptions = new Dictionary<Point, string>();

                    if (testCase.ExpectedCellTexts != null)
                    {
                        foreach (var kvp in testCase.ExpectedCellTexts)
                        {
                            var cellPos = kvp.Key;
                            var expectation = kvp.Value;

                            if (!expectation.ShouldBeEmpty)
                            {
                                centralOptions[cellPos] = "Случайная встреча";
                                System.Diagnostics.Debug.WriteLine(
                                    $"  centralOptions[{cellPos.X},{cellPos.Y}] = \"Случайная встреча\"");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"  Клетка ({cellPos.X},{cellPos.Y}) должна быть пустой");
                            }
                        }
                    }

                    // Если были переданы внешние centralOptions, накладываем их сверху
                    if (existingCentralOptions != null)
                    {
                        foreach (var kvp in existingCentralOptions)
                            centralOptions[kvp.Key] = kvp.Value;
                    }

                    System.Diagnostics.Debug.WriteLine("Запуск OvrNotesBuilder.BuildNotes(...)...");

                    // КЛЮЧЕВОЙ МОМЕНТ:
                    // строим боевые заметки тем же кодом, что использует MainForm
                    var buildResult = OvrNotesBuilder.BuildNotes(
                        testCase.OvrFilePath,
                        centralOptions,
                        existingNotes,
                        null);

                    System.Diagnostics.Debug.WriteLine(
                        $"BuildNotes завершён. NotesPerCell={buildResult.NotesPerCell.Count}, " +
                        $"CentralOptions={buildResult.CentralOptions.Count}, " +
                        $"Objects: total={buildResult.TotalObjects}, table={buildResult.TableObjects}, spec={buildResult.SpecObjects}");

                    // Проверяем каждую ожидаемую клетку
                    if (testCase.ExpectedCellTexts != null)
                    {
                        foreach (var kvp in testCase.ExpectedCellTexts)
                        {
                            var cellPos = kvp.Key;
                            var expectation = kvp.Value;

                            System.Diagnostics.Debug.WriteLine($"\nПроверка клетки ({cellPos.X},{cellPos.Y}):");
                            System.Diagnostics.Debug.WriteLine($"  Ожидание: {expectation.GetDescription()}");

                            string actualText = "";
                            if (buildResult.NotesPerCell.TryGetValue(cellPos, out string noteText))
                            {
                                actualText = noteText ?? "";
                            }

                            string actualPreview = string.IsNullOrEmpty(actualText)
                                ? "<пусто>"
                                : (actualText.Length > 200 ? actualText.Substring(0, 200) + "..." : actualText);

                            System.Diagnostics.Debug.WriteLine($"  Фактический текст: {actualPreview}");

                            bool passed = expectation.Matches(actualText);

                            System.Diagnostics.Debug.WriteLine(
                                $"  Результат: {(passed ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")}");

                            var cellResult = new CellCheckResult
                            {
                                Cell = cellPos,
                                Expected = expectation.GetDescription(),
                                Actual = actualText,
                                Passed = passed,
                                AnalysisTrace = logger.GetAnalysisTrace(cellPos.X, cellPos.Y),
                                Notes = expectation.Comment
                            };

                            result.CellResults.Add(cellResult);
                        }
                    }

                    result.Passed = result.CellResults.All(r => r.Passed);

                    System.Diagnostics.Debug.WriteLine(
                        $"\n=== ТЕСТ ЗАВЕРШЁН: {(result.Passed ? "ПРОЙДЕН" : "ПРОВАЛЕН")} ===");
                    System.Diagnostics.Debug.WriteLine(
                        $"Проверок: {result.TotalChecks}, пройдено: {result.PassedChecks}, провалено: {result.FailedChecks}");
                    System.Diagnostics.Debug.WriteLine(
                        $"Время выполнения: {logger.ExecutionTimeMs} мс");
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.ErrorMessage = ex.Message;
                System.Diagnostics.Debug.WriteLine($"ОШИБКА ПРИ ВЫПОЛНЕНИИ ТЕСТА: {ex.Message}");
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