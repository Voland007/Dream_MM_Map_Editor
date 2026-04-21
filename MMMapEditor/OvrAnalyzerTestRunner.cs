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

﻿// Copyright (c) Voland007 2026. All rights reserved.
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
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;

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
        public string ViewMode { get; set; }
    }

    /// <summary>
    /// Основной класс для запуска тестов анализатора
    /// </summary>
    public class OvrAnalyzerTestRunner
    {
        private readonly Dictionary<string, OvrFileConfig> _configs;
        private static readonly Lazy<bool> VerboseTestLoggingEnabled = new Lazy<bool>(DetermineVerboseTestLoggingEnabled, LazyThreadSafetyMode.ExecutionAndPublication);

        public OvrAnalyzerTestRunner()
        {
            _configs = OvrFileConfigs.Configs;
        }

        private static bool DetermineVerboseTestLoggingEnabled()
        {
            string configuredValue = Environment.GetEnvironmentVariable("MMMAPEDITOR_TEST_VERBOSE_LOGS");
            if (string.IsNullOrWhiteSpace(configuredValue))
                return false;

            return string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVerboseTestLoggingEnabled()
        {
            return VerboseTestLoggingEnabled.Value;
        }

        private static void WriteVerboseTestLog(string message)
        {
            if (IsVerboseTestLoggingEnabled())
                System.Diagnostics.Debug.WriteLine(message);
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
                WriteVerboseTestLog($"Загружен JSON длиной: {json.Length} символов");

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
                    WriteVerboseTestLog($"Десериализовано тестов: {testCases.Count}");

                    if (IsVerboseTestLoggingEnabled())
                    {
                        foreach (var testCase in testCases)
                        {
                            System.Diagnostics.Debug.WriteLine($"Тест: {testCase.Name}, клеток: {testCase.ExpectedCellTexts?.Count ?? 0}");

                            if (testCase.ExpectedCellTexts == null)
                                continue;

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

        private static bool IsAnalysisDebugEnabledForTests()
        {
            string configuredValue = Environment.GetEnvironmentVariable("MMMAPEDITOR_TEST_ENABLE_ANALYSIS_DEBUG");
            if (string.IsNullOrWhiteSpace(configuredValue))
                return false;

            return string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "yes", StringComparison.OrdinalIgnoreCase);
        }

        public TestResult RunTest(
    TestCase testCase,
    Dictionary<Point, string> existingCentralOptions = null)
        {
            IDisposable analysisDebugSuppression = IsAnalysisDebugEnabledForTests()
                ? null
                : AnalysisDebug.Suppress();

            using (analysisDebugSuppression)
            {
                var result = new TestResult { TestCase = testCase };

                WriteVerboseTestLog($"\n=== ЗАПУСК ТЕСТА: {testCase.Name} ===");
                WriteVerboseTestLog($"Клеток для проверки: {testCase.ExpectedCellTexts?.Count ?? 0}");

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

                    WriteVerboseTestLog($"Файл существует: {Path.GetFileName(testCase.OvrFilePath)}");

                    // Проверяем наличие конфигурации
                    string configKey = testCase.OvrConfigName ?? Path.GetFileName(testCase.OvrFilePath).ToUpper();
                    if (!_configs.ContainsKey(configKey))
                    {
                        result.Passed = false;
                        result.ErrorMessage = $"Конфигурация {configKey} не найдена";
                        System.Diagnostics.Debug.WriteLine($"ОШИБКА: {result.ErrorMessage}");
                        return result;
                    }

                    WriteVerboseTestLog($"Конфигурация найдена: {configKey}");

                    using (var logger = new TestLogger(testCase.Id))
                    {
                        result.Logger = logger;

                        // Строим базовые centralOptions из самого OVR-файла
                        // по той же логике, что использует Draft_Laboratory / MainForm.
                        var centralOptions = BuildCentralOptionsFromOvr(testCase.OvrFilePath, configKey);

                        // Если были переданы внешние centralOptions, накладываем их сверху
                        if (existingCentralOptions != null)
                        {
                            foreach (var kvp in existingCentralOptions)
                            {
                                centralOptions[kvp.Key] = kvp.Value;
                                WriteVerboseTestLog(
                                    $"  external centralOptions[{kvp.Key.X},{kvp.Key.Y}] = \"{kvp.Value}\"");
                            }
                        }

                        if (testCase.ExpectedCellTexts != null && IsVerboseTestLoggingEnabled())
                        {
                            foreach (var kvp in testCase.ExpectedCellTexts)
                            {
                                var cellPos = kvp.Key;
                                var expectation = kvp.Value;

                                centralOptions.TryGetValue(cellPos, out var actualCentralOption);

                                System.Diagnostics.Debug.WriteLine(
                                    $"  centralOptions[{cellPos.X},{cellPos.Y}] из OVR = \"{actualCentralOption ?? "<нет>"}\"; " +
                                    $"ожидание пустоты: {expectation.ShouldBeEmpty}");
                            }
                        }

                        var analyzedObjects = OvrFileAnalyzer.AnalyzeOvrFile(
                            testCase.OvrFilePath,
                            _configs[configKey],
                            new Dictionary<Point, string>(centralOptions));

                        RunChecksForMode(
                            result,
                            logger,
                            testCase,
                            centralOptions,
                            analyzedObjects,
                            useHierarchical: false,
                            modeName: "Плоский");

                        RunChecksForMode(
                            result,
                            logger,
                            testCase,
                            centralOptions,
                            analyzedObjects,
                            useHierarchical: true,
                            modeName: "Иерархический");

                        result.Passed = result.CellResults.All(r => r.Passed);

                        WriteVerboseTestLog(
                            $"\n=== ТЕСТ ЗАВЕРШЁН: {(result.Passed ? "ПРОЙДЕН" : "ПРОВАЛЕН")} ===");
                        WriteVerboseTestLog(
                            $"Проверок: {result.TotalChecks}, пройдено: {result.PassedChecks}, провалено: {result.FailedChecks}");
                        WriteVerboseTestLog(
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
        }

        private void RunChecksForMode(
            TestResult result,
            TestLogger logger,
            TestCase testCase,
            Dictionary<Point, string> centralOptions,
            IReadOnlyList<OvrObject> preAnalyzedObjects,
            bool useHierarchical,
            string modeName)
        {
            WriteVerboseTestLog($"\n--- Проверка режима: {modeName} ---");

            var loadResult = OvrOverlayLoader.Load(
                testCase.OvrFilePath,
                new Dictionary<Point, string>(centralOptions),
                null,
                null,
                useHierarchical,
                preAnalyzedObjects);

            WriteVerboseTestLog(
                $"Load({modeName}) завершён. NotesPerCell={loadResult.NotesPerCell.Count}, " +
                $"CentralOptions={loadResult.CentralOptions.Count}, " +
                $"Objects: total={loadResult.TotalObjects}, table={loadResult.TableObjects}, spec={loadResult.SpecObjects}");

            if (testCase.ExpectedCellTexts == null)
                return;

            foreach (var kvp in testCase.ExpectedCellTexts)
            {
                var cellPos = kvp.Key;
                var expectation = kvp.Value;

                string actualText = "";
                if (loadResult.NotesPerCell.TryGetValue(cellPos, out string noteText))
                {
                    actualText = noteText ?? "";
                }

                bool passed = expectation.Matches(actualText, useHierarchical);

                var cellResult = new CellCheckResult
                {
                    Cell = cellPos,
                    Expected = expectation.GetFullTextForDisplay(useHierarchical),
                    Actual = actualText,
                    Passed = passed,
                    AnalysisTrace = passed ? string.Empty : logger.GetAnalysisTrace(cellPos.X, cellPos.Y),
                    Notes = string.IsNullOrWhiteSpace(expectation.Comment)
                        ? modeName
                        : $"{expectation.Comment} [{modeName}]",
                    ViewMode = modeName
                };

                result.CellResults.Add(cellResult);

                if (IsVerboseTestLoggingEnabled())
                {
                    string actualPreview = string.IsNullOrEmpty(actualText)
                        ? "<пусто>"
                        : (actualText.Length > 200 ? actualText.Substring(0, 200) + "..." : actualText);

                    System.Diagnostics.Debug.WriteLine($"\nПроверка клетки ({cellPos.X},{cellPos.Y}) [{modeName}]:");
                    System.Diagnostics.Debug.WriteLine($"  Ожидание: {expectation.GetDescription(useHierarchical)}");
                    System.Diagnostics.Debug.WriteLine($"  Фактический текст: {actualPreview}");
                    System.Diagnostics.Debug.WriteLine(
                        $"  Результат: {(passed ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")}");
                }
            }
        }


        private Dictionary<Point, string> BuildCentralOptionsFromOvr(string filename, string configKey = null)
        {
            var centralOptions = new Dictionary<Point, string>();

            string resolvedConfigKey = configKey ?? Path.GetFileName(filename).ToUpper();

            if (!_configs.TryGetValue(resolvedConfigKey, out var config))
                throw new InvalidOperationException($"Конфигурация {resolvedConfigKey} не найдена");

            string[] lines = new string[33];

            Array.Copy(config.First16Lines, 0, lines, 0, 16);
            Array.Copy(config.Second16Lines, 0, lines, 16, 16);

            byte[] fileData = File.ReadAllBytes(filename);
            int startAddress = config.StartAddress;

            if (fileData.Length < startAddress)
                throw new InvalidOperationException(
                    $"Файл слишком мал. Длина файла: {fileData.Length}, требуемый адрес: {startAddress}.");

            var dataLine = new StringBuilder();
            for (int i = startAddress; i < fileData.Length; i++)
            {
                if (i > startAddress) dataLine.Append(" ");
                dataLine.AppendFormat("{0:X2}", fileData[i]);
            }
            lines[32] = dataLine.ToString();

            for (int y = 0; y < 16; y++)
            {
                if (string.IsNullOrWhiteSpace(lines[y]) || string.IsNullOrWhiteSpace(lines[y + 16]))
                    continue;

                string[] cellValuesFirstLayer = lines[y].Split();
                string[] cellValuesSecondLayer = lines[y + 16].Split();

                if (cellValuesFirstLayer.Length != 16 || cellValuesSecondLayer.Length != 16)
                    continue;

                for (int x = 0; x < 16; x++)
                {
                    int secondDecimalValue = Convert.ToInt32(cellValuesSecondLayer[x], 16);
                    string secondBinaryRepresentation = Convert.ToString(secondDecimalValue, 2).PadLeft(16, '0');

                    Point pos = new Point(x, y);
                    centralOptions[pos] = secondBinaryRepresentation[^8] == '1'
                        ? "Случайная встреча"
                        : "Пустота";
                }
            }

            return centralOptions;
        }

        private static int DetermineParallelism(int testCount)
        {
            string configuredValue = Environment.GetEnvironmentVariable("MMMAPEDITOR_TEST_PARALLELISM");
            if (int.TryParse(configuredValue, out int configuredParallelism) && configuredParallelism > 0)
                return Math.Min(testCount, configuredParallelism);

            return Math.Min(testCount, Math.Min(Environment.ProcessorCount, 4));
        }

        private static void WarmUpSharedReferenceData()
        {
            _ = MonsterDatabase.Monsters.Count;
            _ = ItemDatabase.Items.Count;
            _ = ContainerDatabase.Containers.Count;
        }

        /// <summary>
        /// Запустить несколько тестов
        /// </summary>
        /// <param name="testCases">Список тестовых случаев для запуска</param>
        /// <returns>Список результатов тестов</returns>
        public List<TestResult> RunTests(List<TestCase> testCases)
        {
            if (testCases == null || testCases.Count == 0)
                return new List<TestResult>();

            System.Diagnostics.Debug.WriteLine($"\n==========================================");
            System.Diagnostics.Debug.WriteLine($"ЗАПУСК ТЕСТОВ: {testCases.Count} тестов");
            System.Diagnostics.Debug.WriteLine($"==========================================\n");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            WarmUpSharedReferenceData();

            int maxParallelism = DetermineParallelism(testCases.Count);
            System.Diagnostics.Debug.WriteLine(
                $"Режим запуска: {(maxParallelism > 1 ? $"параллельный, до {maxParallelism} тестов одновременно" : "последовательный")}");
            System.Diagnostics.Debug.WriteLine(
                $"AnalysisDebug в автотестах: {(IsAnalysisDebugEnabledForTests() ? "включён через MMMAPEDITOR_TEST_ENABLE_ANALYSIS_DEBUG" : "отключён")}");
            System.Diagnostics.Debug.WriteLine(
                $"Подробный лог автотестов: {(IsVerboseTestLoggingEnabled() ? "включён через MMMAPEDITOR_TEST_VERBOSE_LOGS" : "отключён")}");

            var resultsByIndex = new TestResult[testCases.Count];

            if (maxParallelism <= 1)
            {
                for (int i = 0; i < testCases.Count; i++)
                    resultsByIndex[i] = RunTest(testCases[i], null);
            }
            else
            {
                Parallel.ForEach(
                    Enumerable.Range(0, testCases.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                    index => { resultsByIndex[index] = RunTest(testCases[index], null); });
            }

            stopwatch.Stop();

            var results = resultsByIndex.ToList();
            int passedCount = 0;
            int failedCount = 0;

            for (int i = 0; i < testCases.Count; i++)
            {
                var testCase = testCases[i];
                var result = resultsByIndex[i];

                System.Diagnostics.Debug.WriteLine($"\n========================================");
                System.Diagnostics.Debug.WriteLine($"Результат теста: {testCase.Name} ({testCase.Id})");
                System.Diagnostics.Debug.WriteLine($"========================================");

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

                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    System.Diagnostics.Debug.WriteLine($"   Ошибка: {result.ErrorMessage}");

                foreach (var cellResult in result.CellResults.Where(r => !r.Passed))
                {
                    System.Diagnostics.Debug.WriteLine($"   ❌ Клетка ({cellResult.Cell.X},{cellResult.Cell.Y}):");
                    System.Diagnostics.Debug.WriteLine($"      Ожидаемый: {cellResult.Expected}");
                    System.Diagnostics.Debug.WriteLine($"      Фактический: {cellResult.Actual}");
                }
            }

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


    }
}
