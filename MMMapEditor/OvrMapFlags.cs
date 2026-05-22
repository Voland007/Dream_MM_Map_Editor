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
    internal static class OvrMapFlags
    {
        public const byte DarknessEnabled = 0x01;
        public const byte TeleportSpellDisabled = 0x02;

        public static byte GetDarknessValue(byte flags)
        {
            return (byte)(flags & DarknessEnabled);
        }

        public static bool IsDarknessEnabled(byte flags)
        {
            return (flags & DarknessEnabled) != 0;
        }

        public static bool IsTeleportSpellAllowed(byte flags)
        {
            return (flags & TeleportSpellDisabled) == 0;
        }
    }
}
