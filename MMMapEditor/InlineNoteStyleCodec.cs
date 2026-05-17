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
        private const string RandomEncounterRubiconWarningTokenPrefix = "[[RERUBICON:";
        private const string PersistentCounterProgressionTokenPrefix = "[[PCOUNTER:";
        private const string DynamicRandomBoundDependencyTokenPrefix = "[[DYNBOUND:";
        private const string MutedParentheticalNoteTokenPrefix = "[[MUTED:";
        private const string SubtleMechanicsNoteTokenPrefix = "[[SUBTLEMECH:";
        private const string WheelRewardExplanationTokenPrefix = "[[WHEELREWARD:";
        private const string RepeatedBattleWarningTokenPrefix = "[[REPEATBATTLE:";
        private const string BattleMonsterStrengthIncreaseTokenPrefix = "[[BATTLEPOWERUP:";
        private const string BattleMonsterStrengthDecreaseTokenPrefix = "[[BATTLEPOWERDOWN:";
        private const string HpRestoredToMaximumTokenPrefix = "[[HPRESTORE:";
        private const string ItemNameTokenPrefix = "[[ITEMNAME:";
        public const string RepeatedBattleWarningText =
            "!! ВНИМАНИЕ! Битва запускается два раза; результат каждого (победа или побег) не влияет на запуск следующего !!";
        private const string RandomEncounterRubiconWarningPrefix =
            "Внимание: Если сумма уровней активной партии ";
        private const string RandomEncounterRubiconWarningSuffix =
            " или больше, то к битве будут ещё добавлены случайные монстры";
        private const string AggregateTemporaryStatGroupText =
            "(INTELLECT/MIGHT/PERSONALITY/ENDURANCE/SPEED/ACCURANCY/LUCK/LEVEL)";
        private static readonly string[] ConcreteTemporaryStatGroupTexts =
        {
            "(INTELLECT)",
            "(MIGHT)",
            "(PERSONALITY)",
            "(ENDURANCE)",
            "(SPEED)",
            "(ACCURANCY)",
            "(LUCK)",
            "(LEVEL)"
        };

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
            return EncodeTextStyleToken(AggregateTemporaryStatTokenPrefix, visibleText);
        }

        public static string EncodeRandomEncounterRubiconWarning(int partyLevelSumThreshold)
        {
            return $"{RandomEncounterRubiconWarningTokenPrefix}{partyLevelSumThreshold.ToString(CultureInfo.InvariantCulture)}]]";
        }

        public static string EncodePersistentCounterProgressionText(string visibleText)
        {
            return EncodeTextStyleToken(PersistentCounterProgressionTokenPrefix, visibleText);
        }

        public static string EncodeDynamicRandomBoundDependencyText(string visibleText)
        {
            return EncodeTextStyleToken(DynamicRandomBoundDependencyTokenPrefix, visibleText);
        }

        public static string EncodeMutedParentheticalNoteText(string visibleText)
        {
            return EncodeTextStyleToken(MutedParentheticalNoteTokenPrefix, visibleText);
        }

        public static string EncodeSubtleMechanicsNoteText(string visibleText)
        {
            return EncodeTextStyleToken(SubtleMechanicsNoteTokenPrefix, visibleText);
        }

        public static string EncodeWheelRewardExplanationText(string visibleText)
        {
            return EncodeTextStyleToken(WheelRewardExplanationTokenPrefix, visibleText);
        }

        public static string EncodeRepeatedBattleWarningText(string visibleText)
        {
            return EncodeTextStyleToken(RepeatedBattleWarningTokenPrefix, visibleText);
        }

        public static string EncodeBattleMonsterStrengthIncreaseText(string visibleText)
        {
            return EncodeTextStyleToken(BattleMonsterStrengthIncreaseTokenPrefix, visibleText);
        }

        public static string EncodeBattleMonsterStrengthDecreaseText(string visibleText)
        {
            return EncodeTextStyleToken(BattleMonsterStrengthDecreaseTokenPrefix, visibleText);
        }

        public static string EncodeHpRestoredToMaximumText(string visibleText)
        {
            return EncodeTextStyleToken(HpRestoredToMaximumTokenPrefix, visibleText);
        }

        public static string EncodeItemNameText(string visibleText)
        {
            return EncodeTextStyleToken(ItemNameTokenPrefix, visibleText);
        }

        public static StyledInlineNoteText RenderTextWithStyles(string rawText)
        {
            var rendered = new StyledInlineNoteText();
            if (string.IsNullOrEmpty(rawText))
                return rendered;

            var visibleText = new StringBuilder(rawText.Length);
            for (int i = 0; i < rawText.Length;)
            {
                if (TryConsumeRandomEncounterRubiconWarningToken(
                    rawText,
                    i,
                    out string warningText,
                    out int thresholdStart,
                    out int thresholdLength,
                    out int warningConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(warningText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = warningText.Length,
                        Kind = NoteInlineStyleKind.RandomEncounterRubiconWarning
                    });
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start + thresholdStart,
                        Length = thresholdLength,
                        Kind = NoteInlineStyleKind.RandomEncounterRubiconThreshold
                    });
                    i += warningConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    PersistentCounterProgressionTokenPrefix,
                    out string progressionText,
                    out int progressionConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(progressionText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = progressionText.Length,
                        Kind = NoteInlineStyleKind.PersistentCounterProgressionNote
                    });
                    AppendPersistentCounterProgressionDetailStyles(rendered.Styles, start, progressionText);
                    i += progressionConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    DynamicRandomBoundDependencyTokenPrefix,
                    out string dynamicBoundText,
                    out int dynamicBoundConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(dynamicBoundText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = dynamicBoundText.Length,
                        Kind = NoteInlineStyleKind.DynamicRandomBoundDependencyNote
                    });
                    AppendDynamicRandomBoundDependencyDetailStyles(rendered.Styles, start, dynamicBoundText);
                    i += dynamicBoundConsumedLength;
                    continue;
                }

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

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    WheelRewardExplanationTokenPrefix,
                    out string wheelRewardText,
                    out int wheelRewardConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(wheelRewardText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = wheelRewardText.Length,
                        Kind = NoteInlineStyleKind.WheelRewardExplanation
                    });
                    i += wheelRewardConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    RepeatedBattleWarningTokenPrefix,
                    out string repeatedBattleWarningText,
                    out int repeatedBattleWarningConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(repeatedBattleWarningText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = repeatedBattleWarningText.Length,
                        Kind = NoteInlineStyleKind.RepeatedBattleWarning
                    });
                    i += repeatedBattleWarningConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    BattleMonsterStrengthIncreaseTokenPrefix,
                    out string strengthIncreaseText,
                    out int strengthIncreaseConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(strengthIncreaseText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = strengthIncreaseText.Length,
                        Kind = NoteInlineStyleKind.BattleMonsterStrengthIncrease
                    });
                    i += strengthIncreaseConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    BattleMonsterStrengthDecreaseTokenPrefix,
                    out string strengthDecreaseText,
                    out int strengthDecreaseConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(strengthDecreaseText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = strengthDecreaseText.Length,
                        Kind = NoteInlineStyleKind.BattleMonsterStrengthDecrease
                    });
                    i += strengthDecreaseConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    HpRestoredToMaximumTokenPrefix,
                    out string hpRestoredText,
                    out int hpRestoredConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(hpRestoredText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = hpRestoredText.Length,
                        Kind = NoteInlineStyleKind.HpRestoredToMaximum
                    });
                    AppendAlignmentRestoreKeywordStyles(rendered.Styles, start, hpRestoredText);
                    i += hpRestoredConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    MutedParentheticalNoteTokenPrefix,
                    out string mutedText,
                    out int mutedConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(mutedText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = mutedText.Length,
                        Kind = NoteInlineStyleKind.MutedParentheticalNote
                    });
                    i += mutedConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    SubtleMechanicsNoteTokenPrefix,
                    out string subtleMechanicsText,
                    out int subtleMechanicsConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(subtleMechanicsText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = subtleMechanicsText.Length,
                        Kind = NoteInlineStyleKind.SubtleMechanicsNote
                    });
                    i += subtleMechanicsConsumedLength;
                    continue;
                }

                if (TryConsumeTextStyleToken(
                    rawText,
                    i,
                    ItemNameTokenPrefix,
                    out string itemNameText,
                    out int itemNameConsumedLength))
                {
                    int start = visibleText.Length;
                    visibleText.Append(itemNameText);
                    rendered.Styles.Add(new NoteInlineStyleSpan
                    {
                        Start = start,
                        Length = itemNameText.Length,
                        Kind = NoteInlineStyleKind.ItemName
                    });
                    i += itemNameConsumedLength;
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

        private static string EncodeTextStyleToken(string tokenPrefix, string visibleText)
        {
            if (string.IsNullOrEmpty(visibleText))
                return visibleText ?? string.Empty;

            return $"{tokenPrefix}{visibleText}]]";
        }

        private static bool TryConsumeRandomEncounterRubiconWarningToken(
            string rawText,
            int startIndex,
            out string visibleText,
            out int thresholdStart,
            out int thresholdLength,
            out int consumedLength)
        {
            visibleText = string.Empty;
            thresholdStart = 0;
            thresholdLength = 0;
            consumedLength = 0;

            if (string.IsNullOrEmpty(rawText) ||
                startIndex < 0 ||
                startIndex + RandomEncounterRubiconWarningTokenPrefix.Length + 3 > rawText.Length ||
                string.CompareOrdinal(
                    rawText,
                    startIndex,
                    RandomEncounterRubiconWarningTokenPrefix,
                    0,
                    RandomEncounterRubiconWarningTokenPrefix.Length) != 0)
            {
                return false;
            }

            int contentStart = startIndex + RandomEncounterRubiconWarningTokenPrefix.Length;
            int closingIndex = rawText.IndexOf("]]", contentStart, StringComparison.Ordinal);
            if (closingIndex < 0)
                return false;

            string thresholdText = rawText.Substring(contentStart, closingIndex - contentStart);
            if (string.IsNullOrWhiteSpace(thresholdText))
                return false;

            thresholdText = thresholdText.Trim();
            visibleText = RandomEncounterRubiconWarningPrefix + thresholdText + RandomEncounterRubiconWarningSuffix;
            thresholdStart = RandomEncounterRubiconWarningPrefix.Length;
            thresholdLength = thresholdText.Length;
            consumedLength = (closingIndex - startIndex) + 2;
            return true;
        }

        private static void AppendPersistentCounterProgressionDetailStyles(
            List<NoteInlineStyleSpan> styles,
            int lineStart,
            string progressionText)
        {
            if (styles == null || string.IsNullOrEmpty(progressionText))
                return;

            foreach (Match numericValue in Regex.Matches(
                progressionText,
                @"(?<![\p{L}\p{N}_])x?\d+\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (!numericValue.Success || numericValue.Length <= 0)
                    continue;

                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + numericValue.Index,
                    Length = numericValue.Length,
                    Kind = NoteInlineStyleKind.PersistentCounterProgressionValue
                });
            }
        }

        private static void AppendDynamicRandomBoundDependencyDetailStyles(
            List<NoteInlineStyleSpan> styles,
            int lineStart,
            string dependencyText)
        {
            if (styles == null || string.IsNullOrEmpty(dependencyText))
                return;

            int formulaSeparator = dependencyText.LastIndexOf(": ", StringComparison.Ordinal);
            int formulaStart = formulaSeparator >= 0
                ? formulaSeparator + 2
                : -1;

            if (formulaStart < 0 || formulaStart >= dependencyText.Length)
                return;

            styles.Add(new NoteInlineStyleSpan
            {
                Start = lineStart + formulaStart,
                Length = dependencyText.Length - formulaStart,
                Kind = NoteInlineStyleKind.DynamicRandomBoundDependencyFormula
            });
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

            foreach (string concreteGroupText in ConcreteTemporaryStatGroupTexts)
            {
                int concreteGroupIndex = aggregateText.IndexOf(concreteGroupText, StringComparison.Ordinal);
                if (concreteGroupIndex < 0)
                    continue;

                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + concreteGroupIndex,
                    Length = concreteGroupText.Length,
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

        private static void AppendAlignmentRestoreKeywordStyles(
            List<NoteInlineStyleSpan> styles,
            int lineStart,
            string text)
        {
            if (styles == null || string.IsNullOrEmpty(text))
                return;

            foreach (Match match in Regex.Matches(
                text,
                @"\b(?:[Тт]екущ(?:ий|его)|врожд[её]нн(?:ый|ого))\b",
                RegexOptions.CultureInvariant))
            {
                if (!match.Success || match.Length <= 0)
                    continue;

                styles.Add(new NoteInlineStyleSpan
                {
                    Start = lineStart + match.Index,
                    Length = match.Length,
                    Kind = NoteInlineStyleKind.AlignmentRestoreKeyword
                });
            }
        }

        private static bool TryConsumeAggregateTemporaryStatToken(
            string rawText,
            int startIndex,
            out string visibleText,
            out int consumedLength)
        {
            return TryConsumeTextStyleToken(
                rawText,
                startIndex,
                AggregateTemporaryStatTokenPrefix,
                out visibleText,
                out consumedLength);
        }

        private static bool TryConsumeTextStyleToken(
            string rawText,
            int startIndex,
            string tokenPrefix,
            out string visibleText,
            out int consumedLength)
        {
            visibleText = string.Empty;
            consumedLength = 0;

            if (string.IsNullOrEmpty(rawText) ||
                string.IsNullOrEmpty(tokenPrefix) ||
                startIndex < 0 ||
                startIndex + tokenPrefix.Length + 2 > rawText.Length ||
                string.CompareOrdinal(rawText, startIndex, tokenPrefix, 0, tokenPrefix.Length) != 0)
            {
                return false;
            }

            int contentStart = startIndex + tokenPrefix.Length;
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
