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

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace MMMapEditor.Tests
{
    internal sealed class TestExpectationStore
    {
        private const int MaxHistoryEntries = 20;
        private const string TestsFileName = "OvrAnalyzerTests.json";
        private readonly string _testsFilePath;
        private readonly string _historyFilePath;

        public TestExpectationStore(string testsFilePath)
        {
            _testsFilePath = ResolveTestsFilePath(testsFilePath);
            string fileDirectory = Path.GetDirectoryName(_testsFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string historyFileName = $"{Path.GetFileNameWithoutExtension(_testsFilePath)}.history.json";
            _historyFilePath = Path.Combine(fileDirectory, historyFileName);
        }

        public string TestsFilePath => _testsFilePath;

        public static string ResolveTestsFilePath(string? testsFilePath = null)
        {
            if (!string.IsNullOrWhiteSpace(testsFilePath))
                return Path.GetFullPath(testsFilePath);

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestsFileName);
        }

        public int GetHistoryCount()
        {
            return LoadHistory().Count;
        }

        public TestExpectationUpdateResult AcceptActualAsExpected(
            string testId,
            Point cell,
            string? viewMode,
            string? actualText)
        {
            List<TestCase> testCases = LoadTestCases();
            TestCase? testCase = testCases.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, testId, StringComparison.Ordinal));

            if (testCase == null)
            {
                return TestExpectationUpdateResult.Failure(
                    $"Тест с ID '{testId}' не найден в '{_testsFilePath}'.");
            }

            if (testCase.ExpectedCellTexts == null)
                testCase.ExpectedCellTexts = new Dictionary<Point, CellExpectation>();

            testCase.ExpectedCellTexts.TryGetValue(cell, out CellExpectation? currentExpectation);

            bool cellHadExpectation = currentExpectation != null;
            CellExpectation? previousExpectation = CloneExpectation(currentExpectation);
            CellExpectation updatedExpectation = CloneExpectation(currentExpectation) ?? new CellExpectation();

            ApplyActualValue(updatedExpectation, viewMode, actualText);

            if (ExpectationsEqual(previousExpectation, updatedExpectation))
            {
                return TestExpectationUpdateResult.NoChanges(
                    testCase,
                    cell,
                    viewMode,
                    "Фактический результат уже сохранён как ожидаемый.");
            }

            testCase.ExpectedCellTexts[cell] = updatedExpectation;
            SaveTestCases(testCases);

            List<TestExpectationChangeEntry> history = LoadHistory();
            history.Add(new TestExpectationChangeEntry
            {
                ChangedAtUtc = DateTime.UtcNow,
                TestId = testCase.Id,
                TestName = testCase.Name,
                CellX = cell.X,
                CellY = cell.Y,
                ViewMode = viewMode,
                HadExpectation = cellHadExpectation,
                PreviousExpectation = previousExpectation,
                UpdatedExpectation = CloneExpectation(updatedExpectation)
            });
            TrimHistory(history);
            SaveHistory(history);

            return TestExpectationUpdateResult.CreateSuccess(
                testCase,
                cell,
                viewMode,
                $"Ожидание для клетки ({cell.X},{cell.Y}) [{viewMode}] обновлено.");
        }

        public TestExpectationUpdateResult RollbackLastChange()
        {
            List<TestExpectationChangeEntry> history = LoadHistory();
            if (history.Count == 0)
            {
                return TestExpectationUpdateResult.Failure(
                    "История изменений пуста. Откатывать нечего.");
            }

            TestExpectationChangeEntry lastChange = history[^1];
            List<TestCase> testCases = LoadTestCases();
            TestCase? testCase = testCases.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, lastChange.TestId, StringComparison.Ordinal));

            if (testCase == null)
            {
                return TestExpectationUpdateResult.Failure(
                    $"Не удалось откатить изменение: тест '{lastChange.TestId}' отсутствует в '{_testsFilePath}'.");
            }

            if (testCase.ExpectedCellTexts == null)
                testCase.ExpectedCellTexts = new Dictionary<Point, CellExpectation>();

            var cell = new Point(lastChange.CellX, lastChange.CellY);

            if (lastChange.HadExpectation)
            {
                testCase.ExpectedCellTexts[cell] = CloneExpectation(lastChange.PreviousExpectation) ?? new CellExpectation();
            }
            else
            {
                testCase.ExpectedCellTexts.Remove(cell);
            }

            SaveTestCases(testCases);

            history.RemoveAt(history.Count - 1);
            SaveHistory(history);

            string viewModeLabel = string.IsNullOrWhiteSpace(lastChange.ViewMode)
                ? "без режима"
                : lastChange.ViewMode;

            return TestExpectationUpdateResult.CreateSuccess(
                testCase,
                cell,
                lastChange.ViewMode,
                $"Последнее изменение для клетки ({cell.X},{cell.Y}) [{viewModeLabel}] откатилось.");
        }

        private List<TestCase> LoadTestCases()
        {
            if (!File.Exists(_testsFilePath))
                throw new FileNotFoundException("Файл с тестами не найден.", _testsFilePath);

            string json = File.ReadAllText(_testsFilePath, Encoding.UTF8);
            var settings = CreateTestCaseJsonSettings(Formatting.None);

            return JsonConvert.DeserializeObject<List<TestCase>>(json, settings) ?? new List<TestCase>();
        }

        private void SaveTestCases(List<TestCase> testCases)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_testsFilePath) ?? AppDomain.CurrentDomain.BaseDirectory);

            var settings = CreateTestCaseJsonSettings(Formatting.Indented);
            string json = JsonConvert.SerializeObject(testCases ?? new List<TestCase>(), settings);
            File.WriteAllText(_testsFilePath, json, Encoding.UTF8);
        }

        private List<TestExpectationChangeEntry> LoadHistory()
        {
            if (!File.Exists(_historyFilePath))
                return new List<TestExpectationChangeEntry>();

            string json = File.ReadAllText(_historyFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return new List<TestExpectationChangeEntry>();

            return JsonConvert.DeserializeObject<List<TestExpectationChangeEntry>>(json)
                ?? new List<TestExpectationChangeEntry>();
        }

        private void SaveHistory(List<TestExpectationChangeEntry> history)
        {
            if (history == null || history.Count == 0)
            {
                if (File.Exists(_historyFilePath))
                    File.Delete(_historyFilePath);

                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_historyFilePath) ?? AppDomain.CurrentDomain.BaseDirectory);
            string json = JsonConvert.SerializeObject(history, Formatting.Indented);
            File.WriteAllText(_historyFilePath, json, Encoding.UTF8);
        }

        private static JsonSerializerSettings CreateTestCaseJsonSettings(Formatting formatting)
        {
            return new JsonSerializerSettings
            {
                Formatting = formatting,
                Converters = new List<JsonConverter> { new PointDictionaryConverter() }
            };
        }

        private static void ApplyActualValue(CellExpectation expectation, string? viewMode, string? actualText)
        {
            expectation.ExpectedTexts ??= new List<string>();
            expectation.ExpectedTextsHierarch ??= new List<string>();

            if (string.IsNullOrWhiteSpace(actualText))
            {
                expectation.ShouldBeEmpty = true;
                expectation.ExpectedTexts.Clear();
                expectation.ExpectedTextsHierarch.Clear();
                return;
            }

            expectation.ShouldBeEmpty = false;
            var replacementValues = new List<string> { actualText };

            if (IsHierarchicalMode(viewMode))
                expectation.ExpectedTextsHierarch = replacementValues;
            else
                expectation.ExpectedTexts = replacementValues;
        }

        private static bool IsHierarchicalMode(string? viewMode)
        {
            return string.Equals(viewMode, "Иерархический", StringComparison.OrdinalIgnoreCase);
        }

        private static CellExpectation? CloneExpectation(CellExpectation? expectation)
        {
            if (expectation == null)
                return null;

            return new CellExpectation
            {
                ExpectedTexts = expectation.ExpectedTexts != null
                    ? new List<string>(expectation.ExpectedTexts)
                    : new List<string>(),
                ExpectedTextsHierarch = expectation.ExpectedTextsHierarch != null
                    ? new List<string>(expectation.ExpectedTextsHierarch)
                    : new List<string>(),
                ShouldBeEmpty = expectation.ShouldBeEmpty,
                Comment = expectation.Comment
            };
        }

        private static bool ExpectationsEqual(CellExpectation? left, CellExpectation? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return false;

            return left.ShouldBeEmpty == right.ShouldBeEmpty
                && string.Equals(left.Comment, right.Comment, StringComparison.Ordinal)
                && ListsEqual(left.ExpectedTexts, right.ExpectedTexts)
                && ListsEqual(left.ExpectedTextsHierarch, right.ExpectedTextsHierarch);
        }

        private static bool ListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return left == null && right == null;

            if (left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static void TrimHistory(List<TestExpectationChangeEntry> history)
        {
            if (history == null)
                return;

            int excessEntries = history.Count - MaxHistoryEntries;
            if (excessEntries <= 0)
                return;

            history.RemoveRange(0, excessEntries);
        }
    }

    internal sealed class TestExpectationChangeEntry
    {
        public DateTime ChangedAtUtc { get; set; }
        public string? TestId { get; set; }
        public string? TestName { get; set; }
        public int CellX { get; set; }
        public int CellY { get; set; }
        public string? ViewMode { get; set; }
        public bool HadExpectation { get; set; }
        public CellExpectation? PreviousExpectation { get; set; }
        public CellExpectation? UpdatedExpectation { get; set; }
    }

    internal sealed class TestExpectationUpdateResult
    {
        private TestExpectationUpdateResult()
        {
        }

        public bool Success { get; private set; }
        public bool Changed { get; private set; }
        public string? Message { get; private set; }
        public TestCase? TestCase { get; private set; }
        public Point Cell { get; private set; }
        public string? ViewMode { get; private set; }

        public static TestExpectationUpdateResult CreateSuccess(TestCase testCase, Point cell, string? viewMode, string message)
        {
            return new TestExpectationUpdateResult
            {
                Success = true,
                Changed = true,
                Message = message,
                TestCase = testCase,
                Cell = cell,
                ViewMode = viewMode
            };
        }

        public static TestExpectationUpdateResult NoChanges(TestCase testCase, Point cell, string? viewMode, string message)
        {
            return new TestExpectationUpdateResult
            {
                Success = true,
                Changed = false,
                Message = message,
                TestCase = testCase,
                Cell = cell,
                ViewMode = viewMode
            };
        }

        public static TestExpectationUpdateResult Failure(string message)
        {
            return new TestExpectationUpdateResult
            {
                Success = false,
                Changed = false,
                Message = message
            };
        }
    }
}
