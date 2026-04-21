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
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Класс для захвата и анализа логов выполнения.
    /// </summary>
    public class TestLogger : IDisposable
    {
        private static readonly object ListenerSync = new object();
        private static readonly AsyncLocal<TestLogger> CurrentLogger = new AsyncLocal<TestLogger>();
        private static bool _captureListenerInstalled;

        private readonly List<string> _capturedLogs;
        private readonly object _capturedLogsSync = new object();
        private readonly StringBuilder _lineBuffer = new StringBuilder();
        private readonly Stopwatch _stopwatch;
        private readonly TestLogger _previousLogger;

        /// <summary>
        /// Захваченные логи.
        /// </summary>
        public IReadOnlyList<string> CapturedLogs => GetCapturedLogsSnapshot();

        /// <summary>
        /// Время выполнения теста в миллисекундах.
        /// </summary>
        public long ExecutionTimeMs => _stopwatch.ElapsedMilliseconds;

        /// <summary>
        /// Создать логгер для теста.
        /// </summary>
        public TestLogger(string testId)
        {
            _capturedLogs = new List<string>();
            EnsureCaptureListenerInstalled();

            _previousLogger = CurrentLogger.Value;
            CurrentLogger.Value = this;

            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Получить отфильтрованные логи по заданному критерию.
        /// </summary>
        public List<string> GetFilteredLogs(Func<string, bool> filter)
        {
            return GetCapturedLogsSnapshot().Where(filter).ToList();
        }

        /// <summary>
        /// Получить логи, относящиеся к конкретной клетке.
        /// </summary>
        public List<string> GetLogsForCell(int x, int y)
        {
            return GetCapturedLogsSnapshot().Where(l =>
                l.Contains($"({x},{y})") ||
                (l.Contains($"X={x}") && l.Contains($"Y={y}"))).ToList();
        }

        /// <summary>
        /// Получить путь анализа для конкретной клетки.
        /// </summary>
        public string GetAnalysisTrace(int x, int y)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== ТРАССИРОВКА АНАЛИЗА ДЛЯ КЛЕТКИ ({x},{y}) ===");

            var relevantLogs = GetCapturedLogsSnapshot().Where(l =>
                l.Contains($"({x},{y})") ||
                (l.Contains($"X={x}") && l.Contains($"Y={y}")) ||
                l.Contains($"для клетки ({x},{y})") ||
                (l.Contains("Анализ пути") && l.Contains($"{x},{y}"))).ToList();

            foreach (var log in relevantLogs)
                sb.AppendLine(log);

            return sb.ToString();
        }

        /// <summary>
        /// Явно добавить строку в лог теста.
        /// </summary>
        public void LogLine(string message)
        {
            lock (_capturedLogsSync)
            {
                _capturedLogs.Add(message ?? string.Empty);
            }
        }

        /// <summary>
        /// Явно добавить несколько строк в лог теста.
        /// </summary>
        public void LogLines(IEnumerable<string> messages)
        {
            if (messages == null)
                return;

            lock (_capturedLogsSync)
            {
                foreach (var message in messages)
                    _capturedLogs.Add(message ?? string.Empty);
            }
        }

        /// <summary>
        /// Сбросить логгер.
        /// </summary>
        public void Dispose()
        {
            _stopwatch.Stop();
            FlushBufferedLine();

            if (ReferenceEquals(CurrentLogger.Value, this))
                CurrentLogger.Value = _previousLogger;
        }

        private static void EnsureCaptureListenerInstalled()
        {
            lock (ListenerSync)
            {
                if (_captureListenerInstalled)
                    return;

                Trace.Listeners.Add(new CaptureTraceListener());
                _captureListenerInstalled = true;
            }
        }

        private List<string> GetCapturedLogsSnapshot()
        {
            lock (_capturedLogsSync)
            {
                var snapshot = new List<string>(_capturedLogs);
                if (_lineBuffer.Length > 0)
                    snapshot.Add(_lineBuffer.ToString());

                return snapshot;
            }
        }

        private void CaptureWrite(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            lock (_capturedLogsSync)
            {
                foreach (char ch in value)
                {
                    if (ch == '\r')
                        continue;

                    if (ch == '\n')
                    {
                        _capturedLogs.Add(_lineBuffer.ToString());
                        _lineBuffer.Clear();
                        continue;
                    }

                    _lineBuffer.Append(ch);
                }
            }
        }

        private void CaptureWriteLine(string value)
        {
            CaptureWrite((value ?? string.Empty) + Environment.NewLine);
        }

        private void FlushBufferedLine()
        {
            lock (_capturedLogsSync)
            {
                if (_lineBuffer.Length == 0)
                    return;

                _capturedLogs.Add(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
        }

        private sealed class CaptureTraceListener : TraceListener
        {
            public override void Write(string? message)
            {
                CurrentLogger.Value?.CaptureWrite(message);
            }

            public override void WriteLine(string? message)
            {
                CurrentLogger.Value?.CaptureWriteLine(message);
            }
        }
    }
}
