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
    public static class PartyPermanentStatSemantics
    {
        public const int PermanentIntellectFieldOffset = 0x15;
        public const int PermanentMightFieldOffset = 0x17;
        public const int PermanentPersonalityFieldOffset = 0x19;
        public const int PermanentEnduranceFieldOffset = 0x1B;
        public const int PermanentSpeedFieldOffset = 0x1D;
        public const int PermanentAccuracyFieldOffset = 0x1F;
        public const int PermanentLuckFieldOffset = 0x21;
        public const int RaiseFlagsFieldOffset = 0x7B;
        public const byte TrackedRaiseFlagsMask = 0x7F;

        public const string RaiseFlagResetHint =
            "условие может быть сброшено при выполнении соответствующего квеста";

        public static bool IsTrackedField(PartyFieldKind field)
        {
            return IsPermanentStatField(field) || IsRaiseFlagsField(field);
        }

        public static bool IsPermanentStatField(PartyFieldKind field)
        {
            return field == PartyFieldKind.PermanentIntellect ||
                   field == PartyFieldKind.PermanentMight ||
                   field == PartyFieldKind.PermanentPersonality ||
                   field == PartyFieldKind.PermanentEndurance ||
                   field == PartyFieldKind.PermanentSpeed ||
                   field == PartyFieldKind.PermanentAccuracy ||
                   field == PartyFieldKind.PermanentLuck;
        }

        public static bool IsRaiseFlagsField(PartyFieldKind field)
        {
            return field == PartyFieldKind.PermanentStatRaiseFlags;
        }

        public static PartyFieldKind ResolveFieldOffset(int offset)
        {
            return offset switch
            {
                PermanentIntellectFieldOffset => PartyFieldKind.PermanentIntellect,
                PermanentMightFieldOffset => PartyFieldKind.PermanentMight,
                PermanentPersonalityFieldOffset => PartyFieldKind.PermanentPersonality,
                PermanentEnduranceFieldOffset => PartyFieldKind.PermanentEndurance,
                PermanentSpeedFieldOffset => PartyFieldKind.PermanentSpeed,
                PermanentAccuracyFieldOffset => PartyFieldKind.PermanentAccuracy,
                PermanentLuckFieldOffset => PartyFieldKind.PermanentLuck,
                RaiseFlagsFieldOffset => PartyFieldKind.PermanentStatRaiseFlags,
                _ => PartyFieldKind.Unknown
            };
        }

        public static bool TryGetRaiseFlagMask(PartyFieldKind field, out byte mask)
        {
            mask = field switch
            {
                PartyFieldKind.PermanentEndurance => 0x01,
                PartyFieldKind.PermanentPersonality => 0x02,
                PartyFieldKind.PermanentIntellect => 0x04,
                PartyFieldKind.PermanentMight => 0x08,
                PartyFieldKind.PermanentAccuracy => 0x10,
                PartyFieldKind.PermanentSpeed => 0x20,
                PartyFieldKind.PermanentLuck => 0x40,
                _ => (byte)0
            };

            return mask != 0;
        }

        public static bool TryGetRaiseFlagStatName(byte mask, out string statName)
        {
            statName = mask switch
            {
                0x01 => "ENDURANCE",
                0x02 => "PERSONALITY",
                0x04 => "INTELLECT",
                0x08 => "MIGHT",
                0x10 => "ACCURACY",
                0x20 => "SPEED",
                0x40 => "LUCK",
                _ => null
            };

            return statName != null;
        }

        public static string GetFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.PermanentIntellect => "постоянный интеллект (INTELLECT)",
                PartyFieldKind.PermanentMight => "постоянная сила (MIGHT)",
                PartyFieldKind.PermanentPersonality => "постоянная харизма (PERSONALITY)",
                PartyFieldKind.PermanentEndurance => "постоянная выносливость (ENDURANCE)",
                PartyFieldKind.PermanentSpeed => "постоянная скорость (SPEED)",
                PartyFieldKind.PermanentAccuracy => "постоянная точность (ACCURACY)",
                PartyFieldKind.PermanentLuck => "постоянная удача (LUCK)",
                PartyFieldKind.PermanentStatRaiseFlags => "флаги одноразовых повышений характеристик (+0x7B)",
                _ => null
            };
        }

        public static byte GetRelevantMask(PartyFieldKind field, PartyEffectOperation operation, byte immediateValue)
        {
            if (!IsRaiseFlagsField(field))
                return 0;

            byte mask = operation switch
            {
                PartyEffectOperation.BitSet => immediateValue,
                PartyEffectOperation.BitClear => unchecked((byte)~immediateValue),
                PartyEffectOperation.BitToggle => immediateValue,
                _ => 0
            };

            return (byte)(mask & TrackedRaiseFlagsMask);
        }
    }
}
