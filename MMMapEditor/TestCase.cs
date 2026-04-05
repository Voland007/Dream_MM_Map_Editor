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


﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MMMapEditor.Tests
{
    /// <summary>
    /// Модель тестового случая для проверки анализатора оверлеев
    /// </summary>
    public class TestCase
    {
        /// <summary>
        /// Уникальный идентификатор теста
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Название теста
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Путь к файлу оверлея (.ovr)
        /// </summary>
        public string OvrFilePath { get; set; }

        /// <summary>
        /// Конфигурация оверлея (имя файла в верхнем регистре)
        /// </summary>
        public string OvrConfigName { get; set; }

        /// <summary>
        /// Описание теста
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Ожидаемые тексты для конкретных клеток
        /// </summary>
        [JsonConverter(typeof(PointDictionaryConverter))]
        public Dictionary<Point, CellExpectation> ExpectedCellTexts { get; set; } = new Dictionary<Point, CellExpectation>();
    }

    /// <summary>
    /// Ожидаемый результат для конкретной клетки
    /// </summary>
    public class CellExpectation
    {
        /// <summary>
        /// Ожидаемые тексты (можно указать несколько вариантов)
        /// </summary>
        public List<string> ExpectedTexts { get; set; } = new List<string>();

        /// <summary>
        /// Должен ли текст отсутствовать (пустая строка)
        /// </summary>
        public bool ShouldBeEmpty { get; set; }

        /// <summary>
        /// Комментарий к ожиданию
        /// </summary>
        public string Comment { get; set; }

        /// <summary>
        /// Декодировать escape-последовательности в строке
        /// </summary>
        private string DecodeString(string input)
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

        /// <summary>
        /// Нормализовать строку для сравнения (удалить лишние пробелы и т.д.)
        /// </summary>
        private string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Заменяем все виды переносов строк на \n для единообразия
            return input
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
        }

        /// <summary>
        /// Извлечь чистый текст из строки анализатора (удалить префикс "Text at 0xXXXX: ")
        /// </summary>
        private string ExtractCleanText(string analyzerText)
        {
            if (string.IsNullOrEmpty(analyzerText))
                return analyzerText;

            // Ищем паттерн "Text at 0xXXXX: "
            int colonIndex = analyzerText.IndexOf(": ");
            if (colonIndex >= 0 && analyzerText.StartsWith("Text at 0x"))
            {
                // Возвращаем всё после двоеточия с пробелом
                return analyzerText.Substring(colonIndex + 2).Trim();
            }

            return analyzerText;
        }

        /// <summary>
        /// Проверить, соответствует ли фактический текст ожиданию
        /// </summary>
        public bool Matches(string actualText)
        {
            string normalizedActual = NormalizeString(actualText ?? "");

            if (ShouldBeEmpty)
                return string.IsNullOrWhiteSpace(normalizedActual);

            if (ExpectedTexts == null || ExpectedTexts.Count == 0)
                return false;

            foreach (var expected in ExpectedTexts)
            {
                string decodedExpected = DecodeString(expected ?? "");
                string normalizedExpected = NormalizeString(decodedExpected);

                if (normalizedActual == normalizedExpected)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Получить краткое описание ожидания
        /// </summary>
        public string GetDescription()
        {
            if (ShouldBeEmpty)
                return "Пустой текст";

            if (ExpectedTexts != null && ExpectedTexts.Count > 0)
            {
                var descriptions = new List<string>();
                foreach (var text in ExpectedTexts)
                {
                    // Для краткого отображения показываем текст с реальными переносами,
                    // но ограничиваем длину превью
                    string displayText = DecodeString(text);
                    if (displayText.Length > 50)
                        displayText = displayText.Substring(0, 50) + "...";
                    descriptions.Add(displayText);
                }

                return string.Join(" ИЛИ ", descriptions);
            }

            return "Любой текст";
        }

        /// <summary>
        /// Получить полный ожидаемый текст для отображения без обрезки
        /// </summary>
        public string GetFullTextForDisplay()
        {
            if (ShouldBeEmpty)
                return "Пустой текст";

            if (ExpectedTexts == null || ExpectedTexts.Count == 0)
                return "Любой текст";

            return string.Join(
                Environment.NewLine + "ИЛИ" + Environment.NewLine,
                ExpectedTexts.Select(text => DecodeString(text ?? ""))
            );
        }
    }

}