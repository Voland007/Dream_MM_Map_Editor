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
    public static class PartyFoodSemantics
    {
        public const int FieldOffset = 0x3E;
        public const string FieldLabel = "FOOD";
        public const string DebugFieldLabel = "FOOD (+0x3E)";

        public static bool IsFoodField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Food;
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return IsFoodField(field) ? FieldLabel : null;
        }

        public static string FormatValue(ushort value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    }
}
