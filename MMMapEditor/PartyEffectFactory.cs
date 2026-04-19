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

        public static PartyEffect CreateTechnicalField77ReadEffect(PartyMemberReference member,
            uint instructionAddress, byte? exactValue = null)
        {
            return new PartyEffect
            {
                Kind = PartyEffectKind.TechnicalFieldRead,
                Field = PartyFieldKind.Technical77,
                Operation = PartyEffectOperation.Read,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                InstructionAddress = instructionAddress,
                Description = BuildTechnicalField77ReadDescription(member, exactValue)
            };
        }

        public static PartyEffect CreateTechnicalField77CompareEffect(PartyMemberReference member,
            uint instructionAddress, byte compareValue, bool isBitMask)
        {
            return new PartyEffect
            {
                Kind = PartyEffectKind.TechnicalFieldCompared,
                Field = PartyFieldKind.Technical77,
                Operation = PartyEffectOperation.Compare,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = isBitMask ? PartyValueKnowledge.ExactDerived : PartyValueKnowledge.ExactImmediate,
                ImmediateValue = compareValue,
                InstructionAddress = instructionAddress,
                Description = isBitMask
                    ? BuildTechnicalField77BitReadDescription(member, compareValue)
                    : BuildTechnicalField77CompareDescription(member, compareValue)
            };
        }

        public static PartyEffect CreateTechnicalField77WriteEffect(PartyMemberReference member,
            uint instructionAddress, byte? exactValue = null)
        {
            return new PartyEffect
            {
                Kind = PartyEffectKind.TechnicalFieldWritten,
                Field = PartyFieldKind.Technical77,
                Operation = PartyEffectOperation.Write,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                InstructionAddress = instructionAddress,
                Description = BuildTechnicalField77WriteDescription(member, exactValue)
            };
        }

        public static PartyEffect CreateTechnicalField77BitEffect(PartyMemberReference member,
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
                Kind = PartyEffectKind.TechnicalFieldWritten,
                Field = PartyFieldKind.Technical77,
                Operation = operation,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = mask,
                InstructionAddress = instructionAddress,
                Description = BuildTechnicalField77BitWriteDescription(member, operation, mask)
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

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return PartyEffectScope.RandomMember;

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return PartyEffectScope.SelectedMember;

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

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return "HP случайного члена партии уменьшается вдвое";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return "HP выбранного члена партии уменьшается вдвое";

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

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return $"У случайного члена партии {verb} {amount} HP";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return $"У выбранного члена партии {verb} {amount} HP";

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
                PartyStatusSemantics.GoodValue => $"{subject}: GOOD",
                PartyStatusSemantics.EradicatedValue => $"{subject}: ERADICATED",
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
                PartyStatusSemantics.EradicatedValue => $"{subject}: ERADICATED",
                PartyStatusSemantics.ParalyzedMask => $"{subject}: PARALYZED",
                PartyStatusSemantics.UnconsciousMask => $"{subject}: UNCONSCIOUS",
                PartyStatusSemantics.DeadMask => $"{subject}: DEAD",
                _ => $"{subject}: Состояние неопределено"
            };
        }

        private static string BuildStatusWriteDescriptionFromBits(PartyMemberReference member, byte? exactValue)
        {
            if (!exactValue.HasValue)
                return BuildStatusChangeDescription(member, "Состояние неопределено");

            var statusNames = PartyStatusSemantics.GetStatusNamesForExactValue(exactValue.Value);
            return statusNames.Count > 0
                ? BuildStatusChangeDescription(member, string.Join(", ", statusNames))
                : BuildStatusChangeDescription(member, "Состояние неопределено");
        }

        private static string BuildStatusBitDescriptionFromBits(PartyMemberReference member,
            PartyEffectOperation operation, byte mask)
        {
            string subject = BuildStatusSubject(member);
            var statusNames = PartyStatusSemantics.GetTrackedStatusNames(mask);
            if (statusNames.Count == 0)
                return BuildStatusChangeDescription(member, "Состояние неопределено");

            string action = operation switch
            {
                PartyEffectOperation.BitSet => null,
                PartyEffectOperation.BitClear => "снятие",
                PartyEffectOperation.BitToggle => "переключение",
                _ => "изменение"
            };

            string statusesText = string.Join(", ", statusNames);
            return string.IsNullOrWhiteSpace(action)
                ? BuildStatusChangeDescription(member, statusesText)
                : $"{subject}: {action} {statusesText}";
        }

        private static string BuildStatusChangeDescription(PartyMemberReference member, string statusesText)
        {
            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return $"CONDITION случайного персонажа в партии изменяется на {statusesText}";

            string subject = BuildStatusSubject(member);
            return $"{subject}: {statusesText}";
        }

        private static string BuildStatusSubject(PartyMemberReference member)
        {
            if (IsLoopTarget(member, LoopSemanticKind.None))
                return "Члены партии";

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return "Случайный член партии";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return "Выбранный член партии";

            if (member?.MemberIndex.HasValue == true)
                return $"Персонаж #{member.MemberIndex.Value}";

            return "Персонаж";
        }

        private static string BuildTechnicalField77ReadDescription(PartyMemberReference member, byte? exactValue)
        {
            string target = BuildTechnicalField77Target(member);
            return exactValue.HasValue
                ? $"Читается {PartyTechnicalField77Semantics.FieldLabel} {target} (=0x{exactValue.Value:X2})"
                : $"Читается {PartyTechnicalField77Semantics.FieldLabel} {target}";
        }

        private static string BuildTechnicalField77CompareDescription(PartyMemberReference member, byte compareValue)
        {
            string target = BuildTechnicalField77Target(member);
            return $"Проверяется {PartyTechnicalField77Semantics.FieldLabel} {target} на значение 0x{compareValue:X2}";
        }

        private static string BuildTechnicalField77BitReadDescription(PartyMemberReference member, byte mask)
        {
            string target = BuildTechnicalField77Target(member);
            return $"Проверяются биты 0x{mask:X2} {PartyTechnicalField77Semantics.FieldLabel} {target}";
        }

        private static string BuildTechnicalField77WriteDescription(PartyMemberReference member, byte? exactValue)
        {
            string target = BuildTechnicalField77Target(member);
            return exactValue.HasValue
                ? $"В {PartyTechnicalField77Semantics.FieldLabel} {target} записывается 0x{exactValue.Value:X2}"
                : $"Изменяется {PartyTechnicalField77Semantics.FieldLabel} {target}";
        }

        private static string BuildTechnicalField77BitWriteDescription(PartyMemberReference member,
            PartyEffectOperation operation, byte mask)
        {
            string target = BuildTechnicalField77Target(member);
            string action = operation switch
            {
                PartyEffectOperation.BitSet => "устанавливаются",
                PartyEffectOperation.BitClear => "сбрасываются",
                PartyEffectOperation.BitToggle => "переключаются",
                _ => "изменяются"
            };

            return $"В {PartyTechnicalField77Semantics.FieldLabel} {target} {action} биты 0x{mask:X2}";
        }

        private static string BuildTechnicalField77Target(PartyMemberReference member)
        {
            if (IsLoopTarget(member, LoopSemanticKind.None))
                return "у членов партии";

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return "у случайного члена партии";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return "у выбранного члена партии";

            if (member?.MemberIndex.HasValue == true)
                return $"у персонажа #{member.MemberIndex.Value}";

            return "у персонажа";
        }
    }
}
