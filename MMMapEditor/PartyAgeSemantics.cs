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
    public static class PartyAgeSemantics
    {
        public const int FieldOffset = 0x25;
        public const string FieldLabel = "возраст (AGE)";
        public const string FieldTitleLabel = "Возраст (AGE)";
        public const string DebugFieldLabel = "возраст (AGE, +0x25)";

        public static bool IsAgeField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Age;
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return IsAgeField(field) ? FieldLabel : null;
        }

        public static string FormatValue(ushort value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string FormatYears(ushort value)
        {
            return $"{FormatValue(value)} {GetYearWord(value)}";
        }

        public static string FormatYearRange(int minValue, int maxValue)
        {
            if (minValue == maxValue)
                return FormatYears((ushort)maxValue);

            return $"{minValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{FormatYears((ushort)maxValue)}";
        }

        public static string FormatAdjustmentSummary(PartyEffectOperation operation, ushort amount)
        {
            return operation switch
            {
                PartyEffectOperation.Increment => $"+{FormatYears(amount)} (стареют)",
                PartyEffectOperation.Decrement => $"-{FormatYears(amount)} (молодеют)",
                _ => null
            };
        }

        public static string FormatAssignmentSummary(ushort value)
        {
            return $"= {FormatYears(value)}";
        }

        private static string GetYearWord(ushort value)
        {
            int lastTwo = value % 100;
            if (lastTwo >= 11 && lastTwo <= 14)
                return "лет";

            return (value % 10) switch
            {
                1 => "год",
                2 => "года",
                3 => "года",
                4 => "года",
                _ => "лет"
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
    }
}
