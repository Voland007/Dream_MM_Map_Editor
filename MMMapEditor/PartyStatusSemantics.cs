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

namespace MMMapEditor
{
    public static class PartyStatusSemantics
    {
        public const int FieldOffset = 0x3F;
        public const byte GoodValue = 0x00;
        public const byte EradicatedValue = 0xFF;
        public const byte ParalyzedMask = 0x20;
        public const byte UnconsciousMask = 0x40;
        public const byte DeadMask = 0x80;
        public const byte TrackedMask = ParalyzedMask | UnconsciousMask | DeadMask;

        public static List<string> GetStatusNamesForExactValue(byte value)
        {
            if (value == GoodValue)
                return new List<string> { "GOOD" };

            if (value == EradicatedValue)
                return new List<string> { "ERADICATED" };

            return GetTrackedStatusNames(value);
        }

        public static List<string> GetTrackedStatusNames(byte value)
        {
            if (value == EradicatedValue)
                return new List<string> { "ERADICATED" };

            var result = new List<string>();

            if ((value & ParalyzedMask) != 0)
                result.Add("PARALYZED");

            if ((value & UnconsciousMask) != 0)
                result.Add("UNCONSCIOUS");

            if ((value & DeadMask) != 0)
                result.Add("DEAD");

            return result;
        }

        public static bool HasTrackedStatuses(byte value)
        {
            return value == GoodValue || value == EradicatedValue || (value & TrackedMask) != 0;
        }
    }
}
