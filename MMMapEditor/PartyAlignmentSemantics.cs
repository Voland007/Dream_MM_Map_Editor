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

namespace MMMapEditor
{
    public static class PartyAlignmentSemantics
    {
        public const int InnateFieldOffset = 0x11;
        public const int CurrentFieldOffset = 0x12;

        public const byte EvilValue = 0x01;
        public const byte NeutralValue = 0x02;
        public const byte GoodValue = 0x03;

        public const string InnateFieldLabel = "врождённый ALIGNMENT";
        public const string CurrentFieldLabel = "текущий ALIGNMENT";
        public const string InnateFieldGenitiveLabel = "врождённого ALIGNMENT";
        public const string CurrentFieldGenitiveLabel = "текущего ALIGNMENT";

        public static bool IsAlignmentField(PartyFieldKind field)
        {
            return field == PartyFieldKind.InnateAlignment ||
                   field == PartyFieldKind.CurrentAlignment;
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.InnateAlignment => InnateFieldLabel,
                PartyFieldKind.CurrentAlignment => CurrentFieldLabel,
                _ => field.ToString()
            };
        }

        public static string GetFieldGenitiveLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.InnateAlignment => InnateFieldGenitiveLabel,
                PartyFieldKind.CurrentAlignment => CurrentFieldGenitiveLabel,
                _ => GetFieldLabel(field)
            };
        }

        public static bool TryGetAlignmentName(byte value, out string alignmentName)
        {
            alignmentName = value switch
            {
                EvilValue => "EVIL",
                NeutralValue => "NEUTRAL",
                GoodValue => "GOOD",
                _ => null
            };

            return alignmentName != null;
        }

        public static string FormatAlignmentValue(ushort value)
        {
            if (value <= byte.MaxValue && TryGetAlignmentName((byte)value, out string alignmentName))
                return alignmentName;

            return value <= byte.MaxValue
                ? $"0x{value:X2}"
                : $"0x{value:X4}";
        }
    }
}
