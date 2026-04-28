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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMMapEditor
{
    internal sealed class StyledInlineNoteText
    {
        public string Text { get; set; } = string.Empty;
        public List<NoteInlineStyleSpan> Styles { get; set; } = new List<NoteInlineStyleSpan>();
    }

    internal static class InlineNoteStyleCodec
    {
        private const string InverseTokenPrefix = "[[INV:";
        private const string AggregateTemporaryStatTokenPrefix = "[[TMPSTAT:";
        private const string AggregateTemporaryStatGroupText =
            "(INTELLECT/MIGHT/PERSONALITY/ENDURANCE/SPEED/ACCURANCY/LUCK/LEVEL)";

        public static bool TryDecodePrintableOverlayChar(byte rawValue, out char visibleChar, out bool isInverse)
        {
            byte visibleValue = (byte)(rawValue & 0x7F);
            visibleChar = (char)visibleValue;
            isInverse = (rawValue & 0x80) != 0;
            return visibleValue >= 0x20 && visibleValue <= 0x7E;
        }

        public static string EncodePrintableChar(char visibleChar, bool isInverse)
        {
            return isInverse
                ? $"{InverseTokenPrefix}{((byte)visibleChar):X2}]]"
                : visibleChar.ToString();
        }

        public static string EncodeAggregateTemporaryStatText(string visibleText)
        {
            if (string.IsNullOrEmpty(visibleText))
                return visibleText ?? string.Empty;

            return $"{AggregateTemporaryStatTokenPrefix}{visibleText}]]";
        }

        public static StyledInlineNoteText RenderTextWithStyles(string rawText)
        {
            var rendered = new StyledInlineNoteText();
            if (string.IsNullOrEmpty(rawText))
                return rendered;

            var visibleText = new StringBuilder(rawText.Length);
            for (int i = 0; i < rawText.Length;)
            {
                if (TryConsumeAggregateTemporaryStatToken(rawText, i, out string aggregateText, out int aggregateConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(aggregateText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = aggregateText.Length,
                        Kind = NoteInlineStyleKind.AggregateTemporaryStatHighlight
                    });
                    AppendAggregateTemporaryStatDetailStyles(rendered.Styles, start, aggregateText);
                    i += aggregateConsumedLength;
                    continue;
                }

                if (TryConsumeInverseToken(rawText, i, out char inverseChar, out int consumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(inverseChar);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = 1,
                        Kind = NoteInlineStyleKind.InverseVideo
                    });
                    i += consumedLength;
                    continue;
                }

                visibleText.Append(rawText[i]);
                i++;
            }

            rendered.Text = visibleText.ToString();
            return rendered;
        }

        private static void AppendAggregateTemporaryStatDetailStyles(
            List<NoteInlineStyleSpan> styles,
            int lineStart,
            string aggregateText)
        {
            if (styles == null || string.IsNullOrEmpty(aggregateText))
                return;

            int statGroupIndex = aggregateText.IndexOf(AggregateTemporaryStatGroupText, StringComparison.Ordinal);
            if (statGroupIndex >= 0)
            {
                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + statGroupIndex,
                    Length = AggregateTemporaryStatGroupText.Length,
                    Kind = NoteInlineStyleKind.AggregateTemporaryStatGroup
                });
            }

            foreach (Match temporaryWord in Regex.Matches(
                aggregateText,
                @"\bВРЕМЕННО\b|\bвременн\w*\b",
                RegexOptions.IgnoreCase))
            {
                if (!temporaryWord.Success || temporaryWord.Length <= 0)
                    continue;

                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + temporaryWord.Index,
                    Length = temporaryWord.Length,
                    Kind = NoteInlineStyleKind.AggregateTemporaryStatTemporaryWord
                });
            }

            Match valueMatch = Regex.Match(
                aggregateText,
                @"\b\d+\b(?!.*\b\d+\b)",
                RegexOptions.CultureInvariant);
            if (valueMatch.Success && valueMatch.Length > 0)
            {
                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + valueMatch.Index,
                    Length = valueMatch.Length,
                    Kind = NoteInlineStyleKind.AggregateTemporaryStatValue
                });
            }
        }

        private static bool TryConsumeAggregateTemporaryStatToken(
            string rawText,
            int startIndex,
            out string visibleText,
            out int consumedLength)
        {
            visibleText = string.Empty;
            consumedLength = 0;

            if (string.IsNullOrEmpty(rawText) ||
                startIndex < 0 ||
                startIndex + AggregateTemporaryStatTokenPrefix.Length + 2 > rawText.Length ||
                string.CompareOrdinal(
                    rawText,
                    startIndex,
                    AggregateTemporaryStatTokenPrefix,
                    0,
                    AggregateTemporaryStatTokenPrefix.Length) != 0)
            {
                return false;
            }

            int contentStart = startIndex + AggregateTemporaryStatTokenPrefix.Length;
            int closingIndex = rawText.IndexOf("]]", contentStart, StringComparison.Ordinal);
            if (closingIndex < 0)
                return false;

            visibleText = rawText.Substring(contentStart, closingIndex - contentStart);
            consumedLength = (closingIndex - startIndex) + 2;
            return true;
        }

        private static bool TryConsumeInverseToken(string rawText, int startIndex, out char inverseChar, out int consumedLength)
        {
            inverseChar = '\0';
            consumedLength = 0;

            if (string.IsNullOrEmpty(rawText) ||
                startIndex < 0 ||
                startIndex + InverseTokenPrefix.Length + 4 > rawText.Length ||
                string.CompareOrdinal(rawText, startIndex, InverseTokenPrefix, 0, InverseTokenPrefix.Length) != 0)
            {
                return false;
            }

            if (startIndex + 10 > rawText.Length)
                return false;

            ReadOnlySpan<char> hexSpan = rawText.AsSpan(startIndex + InverseTokenPrefix.Length, 2);
            ReadOnlySpan<char> closingSpan = rawText.AsSpan(startIndex + InverseTokenPrefix.Length + 2, 2);
            if (!closingSpan.SequenceEqual("]]".AsSpan()))
                return false;

            if (!byte.TryParse(hexSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                return false;

            inverseChar = (char)value;
            consumedLength = 10;
            return true;
        }
    }
}
