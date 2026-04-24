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
    public static class PartyTechnicalFieldSemantics
    {
        public const int RanalouQuestLineFieldOffset = 0x71;
        public const string RanalouQuestLineFieldLabel = "поле прогресса линейки квестов волшебника RANALOU (+0x71)";

        public static bool IsTrackedField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Technical71 ||
                   PartyQuestLordFieldSemantics.IsQuestField(field);
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Technical71 => RanalouQuestLineFieldLabel,
                _ when PartyQuestLordFieldSemantics.IsQuestField(field) => PartyQuestLordFieldSemantics.GetFieldLabel(field),
                _ => null
            };
        }

        public static byte GetRelevantMask(PartyFieldKind field, PartyEffectOperation operation, byte immediateValue)
        {
            if (!IsTrackedField(field))
                return 0;

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
