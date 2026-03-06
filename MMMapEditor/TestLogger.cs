using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Класс для захвата и анализа логов выполнения
    /// </summary>
    public class TestLogger : IDisposable
    {
        private readonly StringWriter _logWriter;
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalError;
        private readonly List<string> _capturedLogs;
        private readonly Stopwatch _stopwatch;
        private readonly string _testId;

        /// <summary>
        /// Захваченные логи
        /// </summary>
        public IReadOnlyList<string> CapturedLogs => _capturedLogs;

        /// <summary>
        /// Время выполнения теста в миллисекундах
        /// </summary>
        public long ExecutionTimeMs => _stopwatch.ElapsedMilliseconds;

        /// <summary>
        /// Создать логгер для теста
        /// </summary>
        public TestLogger(string testId)
        {
            _testId = testId;
            _capturedLogs = new List<string>();
            _logWriter = new StringWriter();
            _originalOut = Console.Out;
            _originalError = Console.Error;

            // Перенаправляем вывод
            Console.SetOut(new MultiTextWriter(_logWriter, _originalOut, _capturedLogs));
            Console.SetError(new MultiTextWriter(_logWriter, _originalError, _capturedLogs));

            // Также перенаправляем Debug.WriteLine
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new TextWriterTraceListener(new MultiTextWriter(_logWriter, _originalOut, _capturedLogs)));

            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Получить отфильтрованные логи по заданному критерию
        /// </summary>
        public List<string> GetFilteredLogs(Func<string, bool> filter)
        {
            return _capturedLogs.FindAll(l => filter(l));
        }

        /// <summary>
        /// Получить логи, относящиеся к конкретной клетке
        /// </summary>
        public List<string> GetLogsForCell(int x, int y)
        {
            return _capturedLogs.FindAll(l =>
                l.Contains($"({x},{y})") ||
                l.Contains($"X={x}") && l.Contains($"Y={y}"));
        }

        /// <summary>
        /// Получить путь анализа для конкретной клетки (трассировка выполнения)
        /// </summary>
        public string GetAnalysisTrace(int x, int y)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== ТРАССИРОВКА АНАЛИЗА ДЛЯ КЛЕТКИ ({x},{y}) ===");

            var relevantLogs = _capturedLogs.FindAll(l =>
                l.Contains($"({x},{y})") ||
                (l.Contains($"X={x}") && l.Contains($"Y={y}")) ||
                l.Contains($"для клетки ({x},{y})") ||
                (l.Contains("Анализ пути") && l.Contains($"{x},{y}")));

            foreach (var log in relevantLogs)
            {
                sb.AppendLine(log);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Сбросить логгер
        /// </summary>
        public void Dispose()
        {
            _stopwatch.Stop();

            // Восстанавливаем оригинальный вывод
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);

            // Восстанавливаем Trace
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new DefaultTraceListener());

            _logWriter.Dispose();
        }

        /// <summary>
        /// Поток, который пишет сразу в несколько мест
        /// </summary>
        private class MultiTextWriter : TextWriter
        {
            private readonly TextWriter _primary;
            private readonly TextWriter _secondary;
            private readonly List<string> _captured;

            public override Encoding Encoding => Encoding.UTF8;

            public MultiTextWriter(TextWriter primary, TextWriter secondary, List<string> captured)
            {
                _primary = primary;
                _secondary = secondary;
                _captured = captured;
            }

            public override void Write(char value)
            {
                _primary.Write(value);
                _secondary.Write(value);
                _captured.Add(value.ToString());
            }

            public override void Write(string value)
            {
                _primary.Write(value);
                _secondary.Write(value);

                if (!string.IsNullOrEmpty(value))
                {
                    foreach (var line in value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        _captured.Add(line);
                    }
                }
            }

            public override void WriteLine(string value)
            {
                _primary.WriteLine(value);
                _secondary.WriteLine(value);
                _captured.Add(value ?? "");
            }
        }
    }
}