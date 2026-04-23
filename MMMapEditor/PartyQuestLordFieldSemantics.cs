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

using System.Collections.Generic;
using System.Linq;

namespace MMMapEditor
{
    public static class PartyQuestLordFieldSemantics
    {
        public const int Lord1FieldOffset = 0x75;
        public const int Lord2FieldOffset = 0x76;
        public const int Lord3FieldOffset = 0x77;

        public static bool IsQuestField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Technical75 ||
                   field == PartyFieldKind.Technical76 ||
                   field == PartyFieldKind.Technical77;
        }

        public static PartyFieldKind GetFieldByOffset(int offset)
        {
            return offset switch
            {
                Lord1FieldOffset => PartyFieldKind.Technical75,
                Lord2FieldOffset => PartyFieldKind.Technical76,
                Lord3FieldOffset => PartyFieldKind.Technical77,
                _ => PartyFieldKind.Unknown
            };
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Technical75 => "счётчик квестов Лорда1 (+0x75)",
                PartyFieldKind.Technical76 => "счётчик квестов Лорда2 (+0x76)",
                PartyFieldKind.Technical77 => "счётчик квестов Лорда3 (+0x77)",
                _ => null
            };
        }

        public static string GetLordLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Technical75 => "Лорда1",
                PartyFieldKind.Technical76 => "Лорда2",
                PartyFieldKind.Technical77 => "Лорда3",
                _ => null
            };
        }

        public static byte GetRelevantMask(PartyEffectOperation operation, byte immediateValue)
        {
            return operation switch
            {
                PartyEffectOperation.BitSet => immediateValue,
                PartyEffectOperation.BitClear => unchecked((byte)~immediateValue),
                PartyEffectOperation.BitToggle => immediateValue,
                _ => 0
            };
        }

        public static int? TryGetSingleQuestNumber(byte mask)
        {
            if (mask == 0 || (mask & (mask - 1)) != 0)
                return null;

            int questNumber = 1;
            while ((mask >>= 1) != 0)
                questNumber++;

            return questNumber;
        }

        public static IReadOnlyList<int> GetQuestNumbers(byte mask)
        {
            var quests = new List<int>();
            for (int bit = 0; bit < 8; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    quests.Add(bit + 1);
            }

            return quests;
        }

        public static string FormatQuestNumbers(byte mask)
        {
            return string.Join(", ", GetQuestNumbers(mask).Select(number => number.ToString()));
        }
    }
}
