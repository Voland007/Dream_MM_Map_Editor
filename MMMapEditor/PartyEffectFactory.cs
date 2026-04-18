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

using System;

namespace MMMapEditor
{
    public static class PartyEffectFactory
    {
        public static PartyEffect CreateHpHalvedEffect(PendingPartyHpOperation pending, LoopSemanticKind loopSemantic)
        {
            PartyConditionKind condition = pending == null
                ? PartyConditionKind.None
                : pending.MaleOnly
                    ? PartyConditionKind.MaleOnly
                    : pending.FemaleOnly
                        ? PartyConditionKind.FemaleOnly
                        : PartyConditionKind.None;

            return CreateHpHalvedEffect(pending, loopSemantic, condition);
        }

        public static PartyEffect CreateHpHalvedEffect(PendingPartyHpOperation pending, LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            if (pending == null)
                return null;

            return new PartyEffect
            {
                Kind = PartyEffectKind.HpHalved,
                Field = PartyFieldKind.Hp,
                Operation = PartyEffectOperation.Halve,
                Scope = ResolveScope(pending.Member, loopSemantic, condition),
                Condition = condition,
                MemberIndex = IsLoopTarget(pending.Member, loopSemantic) ? null : pending.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(pending.Member, loopSemantic),
                ValueKnowledge = PartyValueKnowledge.Structural,
                InstructionAddress = pending.StartAddress,
                Description = BuildHpHalvedDescription(condition, loopSemantic, pending.Member)
            };
        }

        public static PartyEffect CreateHpAdjustedEffect(PendingPartyHpOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, PartyEffectOperation operation, ushort amount)
        {
            if (pending == null || amount == 0 ||
                (operation != PartyEffectOperation.Increment && operation != PartyEffectOperation.Decrement))
            {
                return null;
            }

            return new PartyEffect
            {
                Kind = PartyEffectKind.HpWritten,
                Field = PartyFieldKind.Hp,
                Operation = operation,
                Scope = ResolveScope(pending.Member, loopSemantic, condition),
                Condition = condition,
                MemberIndex = IsLoopTarget(pending.Member, loopSemantic) ? null : pending.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(pending.Member, loopSemantic),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = amount,
                InstructionAddress = pending.StartAddress,
                Description = BuildHpAdjustedDescription(condition, loopSemantic, pending.Member, operation, amount)
            };
        }

        public static PartyEffect CreateStatusWriteEffect(PartyMemberReference member, uint instructionAddress, byte? exactValue = null)
        {
            return new PartyEffect
            {
                Kind = PartyEffectKind.StatusWritten,
                Field = PartyFieldKind.Status,
                Operation = PartyEffectOperation.Write,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                InstructionAddress = instructionAddress,
                Description = BuildStatusWriteDescriptionFromBits(member, exactValue)
            };
        }

        public static PartyEffect CreateStatusBitEffect(PartyMemberReference member,
            PartyEffectOperation operation, byte mask, uint instructionAddress)
        {
            if ((operation != PartyEffectOperation.BitSet &&
                 operation != PartyEffectOperation.BitClear &&
                 operation != PartyEffectOperation.BitToggle) ||
                mask == 0)
            {
                return null;
            }

            return new PartyEffect
            {
                Kind = PartyEffectKind.StatusWritten,
                Field = PartyFieldKind.Status,
                Operation = operation,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = mask,
                InstructionAddress = instructionAddress,
                Description = BuildStatusBitDescriptionFromBits(member, operation, mask)
            };
        }

        private static PartyEffectScope ResolveScope(PartyMemberReference member, LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            if (IsLoopTarget(member, loopSemantic))
            {
                if (condition != PartyConditionKind.None)
                    return PartyEffectScope.PartySubset;

                return PartyEffectScope.WholeParty;
            }

            if (member?.MemberIndex.HasValue == true)
                return PartyEffectScope.SingleMember;

            return PartyEffectScope.Unknown;
        }

        private static bool IsLoopTarget(PartyMemberReference member, LoopSemanticKind loopSemantic)
        {
            return loopSemantic == LoopSemanticKind.PartyMemberScan || member?.IsPartyLoopMember == true;
        }

        private static string BuildHpHalvedDescription(PartyConditionKind condition, LoopSemanticKind loopSemantic, PartyMemberReference member)
        {
            if (condition == PartyConditionKind.MaleOnly)
                return "! HP мужчин в партии уменьшается вдвое !";

            if (condition == PartyConditionKind.FemaleOnly)
                return "HP женщин в партии уменьшается вдвое";

            if (IsLoopTarget(member, loopSemantic))
                return "HP членов партии уменьшается вдвое";

            if (member?.MemberIndex.HasValue == true)
                return $"HP персонажа #{member.MemberIndex.Value} уменьшается вдвое";

            return "HP персонажа уменьшается вдвое";
        }

        private static string BuildHpAdjustedDescription(PartyConditionKind condition, LoopSemanticKind loopSemantic,
            PartyMemberReference member, PartyEffectOperation operation, ushort amount)
        {
            string verb = operation == PartyEffectOperation.Increment ? "добавляется" : "отнимается";

            if (condition == PartyConditionKind.MaleOnly)
                return $"У мужчин в партии {verb} {amount} HP";

            if (condition == PartyConditionKind.FemaleOnly)
                return $"У женщин в партии {verb} {amount} HP";

            if (IsLoopTarget(member, loopSemantic))
            {
                return operation == PartyEffectOperation.Decrement
                    ? $"! У каждого персонажа партии {verb} {amount} HP !"
                    : $"У каждого персонажа партии {verb} {amount} HP";
            }

            if (member?.MemberIndex.HasValue == true)
                return $"У персонажа #{member.MemberIndex.Value} {verb} {amount} HP";

            return operation == PartyEffectOperation.Increment
                ? $"HP персонажа увеличивается на {amount}"
                : $"HP персонажа уменьшается на {amount}";
        }
        private static string BuildStatusWriteDescription(PartyMemberReference member, byte? exactValue)
        {
            string subject = BuildStatusSubject(member);
            if (!exactValue.HasValue)
                return $"{subject}: Состояние неопределено";

            return exactValue.Value switch
            {
                PartyStatusSemantics.ParalyzedMask => $"{subject}: PARALYZED",
                PartyStatusSemantics.UnconsciousMask => $"{subject}: UNCONSCIOUS",
                PartyStatusSemantics.DeadMask => $"{subject}: DEAD",
                _ => $"{subject}: Состояние неопределено"
            };
        }

        private static string BuildStatusBitDescription(PartyMemberReference member,
            PartyEffectOperation operation, byte mask)
        {
            string subject = BuildStatusSubject(member);
            if (mask == 0 || operation != PartyEffectOperation.BitSet)
                return $"{subject}: Состояние неопределено";

            return mask switch
            {
                PartyStatusSemantics.ParalyzedMask => $"{subject}: PARALYZED",
                PartyStatusSemantics.UnconsciousMask => $"{subject}: UNCONSCIOUS",
                PartyStatusSemantics.DeadMask => $"{subject}: DEAD",
                _ => $"{subject}: Состояние неопределено"
            };
        }

        private static string BuildStatusWriteDescriptionFromBits(PartyMemberReference member, byte? exactValue)
        {
            string subject = BuildStatusSubject(member);
            if (!exactValue.HasValue)
                return $"{subject}: РЎРѕСЃС‚РѕСЏРЅРёРµ РЅРµРѕРїСЂРµРґРµР»РµРЅРѕ";

            var statusNames = PartyStatusSemantics.GetTrackedStatusNames(exactValue.Value);
            return statusNames.Count > 0
                ? $"{subject}: {string.Join(", ", statusNames)}"
                : $"{subject}: РЎРѕСЃС‚РѕСЏРЅРёРµ РЅРµРѕРїСЂРµРґРµР»РµРЅРѕ";
        }

        private static string BuildStatusBitDescriptionFromBits(PartyMemberReference member,
            PartyEffectOperation operation, byte mask)
        {
            string subject = BuildStatusSubject(member);
            var statusNames = PartyStatusSemantics.GetTrackedStatusNames(mask);
            if (statusNames.Count == 0)
                return $"{subject}: РЎРѕСЃС‚РѕСЏРЅРёРµ РЅРµРѕРїСЂРµРґРµР»РµРЅРѕ";

            string action = operation switch
            {
                PartyEffectOperation.BitSet => null,
                PartyEffectOperation.BitClear => "clear",
                PartyEffectOperation.BitToggle => "toggle",
                _ => "change"
            };

            string statusesText = string.Join(", ", statusNames);
            return string.IsNullOrWhiteSpace(action)
                ? $"{subject}: {statusesText}"
                : $"{subject}: {action} {statusesText}";
        }

        private static string BuildStatusSubject(PartyMemberReference member)
        {
            if (IsLoopTarget(member, LoopSemanticKind.None))
                return "Члены партии";

            if (member?.MemberIndex.HasValue == true)
                return $"Персонаж #{member.MemberIndex.Value}";

            return "Персонаж";
        }
    }
}
