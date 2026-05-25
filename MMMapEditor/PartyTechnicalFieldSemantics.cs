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
        public const int RanalouJudgementScoreFieldOffset = 0x6E;
        public const string RanalouJudgementScoreFieldLabel = "счёт зачтённых узников RANALOU (+0x6E)";
        public const byte RanalouQuestStartedMask = 0x01;
        public const byte RanalouPrisonerProgressMask = 0x7E;
        public const int MainQuestCompletionFieldOffset = 0x7D;
        public const byte AstralProjectorsCompletedValue = 0x1F;
        public const byte ImposterDefeatedTransferReadyValue = 0x40;
        public const byte MainQuestCompletedThreshold = 0x80;
        public const string MainQuestCompletionFieldLabel = "поле завершения главного квеста (+0x7D)";

        public static bool IsTrackedField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Technical71 ||
                   field == PartyFieldKind.Technical6E ||
                   field == PartyFieldKind.Technical7D ||
                   PartyQuestLordFieldSemantics.IsQuestField(field);
        }

        public static bool IsMainQuestCompletionField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Technical7D;
        }

        public static bool IsRanalouPrisonerProgressMask(byte mask)
        {
            return (mask & RanalouPrisonerProgressMask) == mask &&
                   mask != 0 &&
                   (mask & (mask - 1)) == 0;
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Technical71 => RanalouQuestLineFieldLabel,
                PartyFieldKind.Technical6E => RanalouJudgementScoreFieldLabel,
                PartyFieldKind.Technical7D => MainQuestCompletionFieldLabel,
                _ when PartyQuestLordFieldSemantics.IsQuestField(field) => PartyQuestLordFieldSemantics.GetFieldLabel(field),
                _ => null
            };
        }

        public static byte GetRelevantMask(PartyFieldKind field, PartyEffectOperation operation, byte immediateValue)
        {
            if (!IsTrackedField(field))
                return 0;

            byte mask = operation switch
            {
                PartyEffectOperation.BitSet => immediateValue,
                PartyEffectOperation.BitClear => unchecked((byte)~immediateValue),
                PartyEffectOperation.BitToggle => immediateValue,
                _ => 0
            };

            return FilterRelevantMask(field, mask);
        }

        public static byte FilterRelevantMask(PartyFieldKind field, byte mask)
        {
            if (!IsTrackedField(field))
                return 0;

            if (IsMainQuestCompletionField(field))
                return (byte)(mask & MainQuestCompletedThreshold);

            return mask;
        }
    }
}
