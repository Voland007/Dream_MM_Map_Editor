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
    public static class PartyTemporaryStatSemantics
    {
        public const int TempIntellectFieldOffset = 0x16;
        public const int TempMightFieldOffset = 0x18;
        public const int TempPersonalityFieldOffset = 0x1A;
        public const int TempEnduranceFieldOffset = 0x1C;
        public const int TempSpeedFieldOffset = 0x1E;
        public const int TempAccuracyFieldOffset = 0x20;
        public const int TempLuckFieldOffset = 0x22;
        public const int TempLevelFieldOffset = 0x24;
        public const string TempAnyStatFieldLabel = "одно из временных полей характеристик (INTELLECT/MIGHT/PERSONALITY/ENDURANCE/SPEED/ACCURANCY/LUCK/LEVEL)";

        public const string TempIntellectFieldLabel = "временный интеллект (INTELLECT, +0x16)";
        public const string TempMightFieldLabel = "временная сила (MIGHT)";
        public const string TempPersonalityFieldLabel = "временная харизма (PERSONALITY, +0x1A)";
        public const string TempEnduranceFieldLabel = "временная выносливость (ENDURANCE, +0x1C)";
        public const string TempSpeedFieldLabel = "временная скорость (SPEED, +0x1E)";
        public const string TempAccuracyFieldLabel = "временная точность (ACCURANCY, +0x20)";
        public const string TempLuckFieldLabel = "временная удача (LUCK, +0x22)";
        public const string TempLevelFieldLabel = "временный уровень (LEVEL, +0x24)";

        public static bool IsAggregateField(PartyFieldKind field)
        {
            return field == PartyFieldKind.TempAnyStat;
        }

        public static bool IsConcreteField(PartyFieldKind field)
        {
            return field == PartyFieldKind.TempIntellect ||
                   field == PartyFieldKind.TempMight ||
                   field == PartyFieldKind.TempPersonality ||
                   field == PartyFieldKind.TempEndurance ||
                   field == PartyFieldKind.TempSpeed ||
                   field == PartyFieldKind.TempAccuracy ||
                   field == PartyFieldKind.TempLuck ||
                   field == PartyFieldKind.TempLevel;
        }

        public static bool IsTrackedField(PartyFieldKind field)
        {
            return IsConcreteField(field) || IsAggregateField(field);
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.TempIntellect => TempIntellectFieldLabel,
                PartyFieldKind.TempMight => TempMightFieldLabel,
                PartyFieldKind.TempPersonality => TempPersonalityFieldLabel,
                PartyFieldKind.TempEndurance => TempEnduranceFieldLabel,
                PartyFieldKind.TempSpeed => TempSpeedFieldLabel,
                PartyFieldKind.TempAccuracy => TempAccuracyFieldLabel,
                PartyFieldKind.TempLuck => TempLuckFieldLabel,
                PartyFieldKind.TempLevel => TempLevelFieldLabel,
                PartyFieldKind.TempAnyStat => TempAnyStatFieldLabel,
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
    }
}
