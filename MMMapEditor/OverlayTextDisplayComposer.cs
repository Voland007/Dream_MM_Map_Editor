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
using System.Linq;

namespace MMMapEditor
{
    internal static class OverlayTextDisplayComposer
    {
        public static List<TextEntry> CloneTextEntries(IEnumerable<TextEntry> entries)
        {
            return (entries ?? Enumerable.Empty<TextEntry>())
                .Where(entry => entry != null)
                .OrderBy(entry => entry.Order)
                .Select(entry => entry.Clone())
                .ToList();
        }

        public static List<string> ComposeRawTexts(IEnumerable<TextEntry> entries)
        {
            return ComposeTextEntries(entries)
                .Select(entry => entry.Text)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();
        }

        public static List<TextEntry> ComposeTextEntries(IEnumerable<TextEntry> entries)
        {
            var result = new List<TextEntry>();

            foreach (var sourceEntry in entries ?? Enumerable.Empty<TextEntry>())
            {
                if (sourceEntry == null || string.IsNullOrEmpty(sourceEntry.Text))
                    continue;

                var entry = sourceEntry.Clone();
                var previous = result.LastOrDefault();
                if (CanMergeOverlayText(previous, entry))
                {
                    MergeOverlayText(previous, entry);
                    continue;
                }

                result.Add(entry);
            }

            return result;
        }

        private static bool CanMergeOverlayText(TextEntry previous, TextEntry current)
        {
            if (previous == null ||
                current == null ||
                previous.ScreenLineBreakAfter)
            {
                return false;
            }

            if (current.ScreenTextContinuesPrevious ||
                !string.IsNullOrEmpty(previous.ScreenTextSeparatorAfter))
            {
                return true;
            }

            return TrySplitOverlayTextEntry(previous.Text, out _, out string previousPayload) &&
                   TrySplitOverlayTextEntry(current.Text, out _, out _) &&
                   EndsWithOpenQuote(previousPayload);
        }

        private static void MergeOverlayText(TextEntry target, TextEntry source)
        {
            if (target == null || source == null)
                return;

            string separator = target.ScreenTextSeparatorAfter ?? string.Empty;
            target.Text = MergeScreenText(target.Text, source.Text, separator);
            target.ScreenLineBreakAfter = source.ScreenLineBreakAfter;
            target.ScreenTextSeparatorAfter = source.ScreenTextSeparatorAfter;

            if (target.DisplayRoutine == OverlayTextDisplayRoutineKind.Unknown)
                target.DisplayRoutine = source.DisplayRoutine;

            if (target.DisplayInstructionAddress == 0)
                target.DisplayInstructionAddress = source.DisplayInstructionAddress;

            if (target.SemanticKind == TextSemanticKind.Unknown)
                target.SemanticKind = source.SemanticKind;

            target.IsInferred &= source.IsInferred;
        }

        private static string MergeScreenText(string first, string second, string separator)
        {
            bool firstIsOverlay = TrySplitOverlayTextEntry(first, out _, out string firstPayload, out int firstPayloadIndex);
            bool secondIsOverlay = TrySplitOverlayTextEntry(second, out _, out string secondPayload);

            if (firstIsOverlay && secondIsOverlay)
                return first.Substring(0, firstPayloadIndex) + firstPayload + separator + secondPayload;

            if (firstIsOverlay)
                return first.Substring(0, firstPayloadIndex) + firstPayload + separator + second;

            if (secondIsOverlay)
                return first + separator + secondPayload;

            return first + separator + second;
        }

        private static bool EndsWithOpenQuote(string encodedPayload)
        {
            string decoded = DecodeTextString(encodedPayload);
            if (string.IsNullOrEmpty(decoded))
                return false;

            decoded = decoded.TrimEnd(' ', '\t');
            if (decoded.Length == 0 || decoded[decoded.Length - 1] != '"')
                return false;

            int quoteIndex = decoded.Length - 1;
            while (quoteIndex > 0 && (decoded[quoteIndex - 1] == ' ' || decoded[quoteIndex - 1] == '\t'))
                quoteIndex--;

            return quoteIndex == 0 ||
                   decoded[quoteIndex - 1] == '\r' ||
                   decoded[quoteIndex - 1] == '\n';
        }

        private static bool TrySplitOverlayTextEntry(string rawText, out ushort textAddress, out string payload)
        {
            return TrySplitOverlayTextEntry(rawText, out textAddress, out payload, out _);
        }

        private static bool TrySplitOverlayTextEntry(
            string rawText,
            out ushort textAddress,
            out string payload,
            out int payloadIndex)
        {
            textAddress = 0;
            payload = string.Empty;
            payloadIndex = -1;

            const string prefix = "Text at 0x";
            if (string.IsNullOrEmpty(rawText) ||
                !rawText.StartsWith(prefix, StringComparison.Ordinal) ||
                rawText.Length < prefix.Length + 4)
            {
                return false;
            }

            string addressText = rawText.Substring(prefix.Length, 4);
            if (!ushort.TryParse(addressText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out textAddress))
                return false;

            int separatorIndex = rawText.IndexOf(": ", prefix.Length + 4, StringComparison.Ordinal);
            if (separatorIndex < 0)
                return false;

            payloadIndex = separatorIndex + 2;
            payload = rawText.Substring(payloadIndex);
            return true;
        }

        private static string DecodeTextString(string encodedText)
        {
            if (string.IsNullOrEmpty(encodedText))
                return string.Empty;

            return encodedText
                .Replace("\\r", "\r")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }
    }
}
