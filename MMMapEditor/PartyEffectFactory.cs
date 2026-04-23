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
using System.Collections.Generic;
using System.Linq;

namespace MMMapEditor
{
    public static class PartyEffectFactory
    {
        public static PartyEffect CreateHpHalvedEffect(PendingPartyStatOperation pending, LoopSemanticKind loopSemantic)
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

        public static PartyEffect CreateHpHalvedEffect(PendingPartyStatOperation pending, LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            return CreateStatHalvedEffect(pending, loopSemantic, condition, PartyFieldKind.Hp);
        }

        public static PartyEffect CreateSpHalvedEffect(PendingPartyStatOperation pending, LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            return CreateStatHalvedEffect(pending, loopSemantic, condition, PartyFieldKind.Sp);
        }

        public static PartyEffect CreateStatHalvedEffect(PendingPartyStatOperation pending, LoopSemanticKind loopSemantic,
            PartyConditionKind condition, PartyFieldKind field)
        {
            if (pending == null)
                return null;

            return new PartyEffect
            {
                Kind = PartyEffectKind.HpHalved,
                Field = field,
                Operation = PartyEffectOperation.Halve,
                Scope = ResolveScope(pending.Member, loopSemantic, condition),
                Condition = condition,
                GuardPredicates = pending.GuardPredicates?
                    .Select(predicate => predicate?.Clone())
                    .Where(predicate => predicate != null)
                    .ToList() ?? new List<PartyPredicate>(),
                MemberIndex = IsLoopTarget(pending.Member, loopSemantic) ? null : pending.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(pending.Member, loopSemantic),
                ValueKnowledge = PartyValueKnowledge.Structural,
                InstructionAddress = pending.StartAddress,
                ExecutionOrder = pending.ExecutionOrder,
                Description = BuildStatHalvedDescription(field, condition, loopSemantic, pending.Member)
            };
        }

        public static PartyEffect CreateHpAdjustedEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, PartyEffectOperation operation, ushort amount)
        {
            return CreateStatAdjustedEffect(pending, loopSemantic, condition, operation, amount, PartyFieldKind.Hp);
        }

        public static PartyEffect CreateSpAdjustedEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, PartyEffectOperation operation, ushort amount)
        {
            return CreateStatAdjustedEffect(pending, loopSemantic, condition, operation, amount, PartyFieldKind.Sp);
        }

        public static PartyEffect CreateHpSetEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, ushort value)
        {
            return CreateStatSetEffect(pending, loopSemantic, condition, value, PartyFieldKind.Hp);
        }

        public static PartyEffect CreateSpSetEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, ushort value)
        {
            return CreateStatSetEffect(pending, loopSemantic, condition, value, PartyFieldKind.Sp);
        }

        public static PartyEffect CreateStatAdjustedEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, PartyEffectOperation operation, ushort amount,
            PartyFieldKind field)
        {
            if (pending == null || amount == 0 ||
                (operation != PartyEffectOperation.Increment && operation != PartyEffectOperation.Decrement))
            {
                return null;
            }

            return new PartyEffect
            {
                Kind = PartyEffectKind.HpWritten,
                Field = field,
                Operation = operation,
                Scope = ResolveScope(pending.Member, loopSemantic, condition),
                Condition = condition,
                GuardPredicates = pending.GuardPredicates?
                    .Select(predicate => predicate?.Clone())
                    .Where(predicate => predicate != null)
                    .ToList() ?? new List<PartyPredicate>(),
                MemberIndex = IsLoopTarget(pending.Member, loopSemantic) ? null : pending.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(pending.Member, loopSemantic),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = amount,
                InstructionAddress = pending.StartAddress,
                ExecutionOrder = pending.ExecutionOrder,
                Description = BuildStatAdjustedDescription(field, condition, loopSemantic, pending.Member, operation, amount)
            };
        }

        public static PartyEffect CreateStatSetEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, ushort value, PartyFieldKind field)
        {
            if (pending == null)
                return null;

            var effect = new PartyEffect
            {
                Kind = PartyEffectKind.HpWritten,
                Field = field,
                Operation = PartyEffectOperation.Write,
                Scope = ResolveScope(pending.Member, loopSemantic, condition),
                Condition = condition,
                GuardPredicates = pending.GuardPredicates?
                    .Select(predicate => predicate?.Clone())
                    .Where(predicate => predicate != null)
                    .ToList() ?? new List<PartyPredicate>(),
                MemberIndex = IsLoopTarget(pending.Member, loopSemantic) ? null : pending.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(pending.Member, loopSemantic),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = value,
                InstructionAddress = pending.StartAddress,
                ExecutionOrder = pending.ExecutionOrder
            };

            effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
            return effect;
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

        public static PartyEffect CreateAlignmentWriteEffect(PartyMemberReference member,
            PartyFieldKind field, uint instructionAddress, byte? exactValue = null)
        {
            if (!PartyAlignmentSemantics.IsAlignmentField(field))
                return null;

            var effect = new PartyEffect
            {
                Kind = PartyEffectKind.AlignmentWritten,
                Field = field,
                Operation = PartyEffectOperation.Write,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                InstructionAddress = instructionAddress
            };

            effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
            return effect;
        }

        public static PartyEffect CreateAlignmentCompareEffect(PartyMemberReference member,
            PartyFieldKind field, uint instructionAddress, byte compareValue)
        {
            if (!PartyAlignmentSemantics.IsAlignmentField(field))
                return null;

            var effect = new PartyEffect
            {
                Kind = PartyEffectKind.AlignmentCompared,
                Field = field,
                Operation = PartyEffectOperation.Compare,
                Scope = ResolveScope(member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(member, LoopSemanticKind.None) ? null : member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(member, LoopSemanticKind.None),
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = compareValue,
                InstructionAddress = instructionAddress
            };

            effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
            return effect;
        }

        public static PartyEffect CreateAlignmentFieldCompareEffect(
            PartyFieldReference leftField,
            PartyFieldReference rightField,
            uint instructionAddress)
        {
            if (leftField == null ||
                rightField == null ||
                !PartyAlignmentSemantics.IsAlignmentField(leftField.Field) ||
                !PartyAlignmentSemantics.IsAlignmentField(rightField.Field))
            {
                return null;
            }

            PartyFieldReference primaryField = leftField;
            PartyFieldReference secondaryField = rightField;

            if (leftField.Field == PartyFieldKind.CurrentAlignment &&
                rightField.Field == PartyFieldKind.InnateAlignment)
            {
                primaryField = rightField;
                secondaryField = leftField;
            }

            var effect = new PartyEffect
            {
                Kind = PartyEffectKind.AlignmentCompared,
                Field = primaryField.Field,
                ComparedField = secondaryField.Field,
                Operation = PartyEffectOperation.Compare,
                Scope = ResolveScope(primaryField.Member, LoopSemanticKind.None, PartyConditionKind.None),
                MemberIndex = IsLoopTarget(primaryField.Member, LoopSemanticKind.None) ? null : primaryField.Member?.MemberIndex,
                ObservedMemberIndex = primaryField.Member?.MemberIndex,
                ComparedMemberIndex = secondaryField.Member?.MemberIndex,
                IsLoopDerived = IsLoopTarget(primaryField.Member, LoopSemanticKind.None),
                ValueKnowledge = PartyValueKnowledge.Structural,
                InstructionAddress = instructionAddress
            };

            effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
            return effect;
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

        private static string BuildStatHalvedDescription(PartyFieldKind field, PartyConditionKind condition,
            LoopSemanticKind loopSemantic, PartyMemberReference member)
        {
            string statLabel = GetStatLabel(field);
            if (condition == PartyConditionKind.MaleOnly)
                return $"! {statLabel} каждого мужчины в партии уменьшается вдвое !";

            if (condition == PartyConditionKind.FemaleOnly)
                return $"{statLabel} женщин в партии уменьшается вдвое";

            if (IsLoopTarget(member, loopSemantic))
                return $"! {statLabel} каждого персонажа партии уменьшается вдвое !";

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return $"{statLabel} случайного члена партии уменьшается вдвое";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return $"{statLabel} выбранного члена партии уменьшается вдвое";

            if (member?.MemberIndex.HasValue == true)
                return $"{statLabel} персонажа #{member.MemberIndex.Value} уменьшается вдвое";

            return $"{statLabel} персонажа уменьшается вдвое";
        }

        private static string BuildStatAdjustedDescription(PartyFieldKind field, PartyConditionKind condition,
            LoopSemanticKind loopSemantic,
            PartyMemberReference member, PartyEffectOperation operation, ushort amount)
        {
            string verb = operation == PartyEffectOperation.Increment ? "добавляется" : "отнимается";
            string statLabel = GetStatLabel(field);

            if (condition == PartyConditionKind.MaleOnly)
                return $"У мужчин в партии {verb} {amount} {statLabel}";

            if (condition == PartyConditionKind.FemaleOnly)
                return $"У женщин в партии {verb} {amount} {statLabel}";

            if (IsLoopTarget(member, loopSemantic))
            {
                return operation == PartyEffectOperation.Decrement
                    ? $"! У каждого персонажа партии {verb} {amount} {statLabel} !"
                    : $"У каждого персонажа партии {verb} {amount} {statLabel}";
            }

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return $"У случайного члена партии {verb} {amount} {statLabel}";

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return $"У выбранного члена партии {verb} {amount} {statLabel}";

            if (member?.MemberIndex.HasValue == true)
                return $"У персонажа #{member.MemberIndex.Value} {verb} {amount} {statLabel}";

            return operation == PartyEffectOperation.Increment
                ? $"{statLabel} персонажа увеличивается на {amount}"
                : $"{statLabel} персонажа уменьшается на {amount}";
        }

        private static string GetStatLabel(PartyFieldKind field)
        {
            return field == PartyFieldKind.Sp ? "SP" : "HP";
        }

        private static string BuildStatusWriteDescription(PartyMemberReference member, byte? exactValue)
        {
            string subject = BuildStatusSubject(member);
            if (!exactValue.HasValue)
                return $"{subject}: Состояние неопределено";

            var statusNames = PartyStatusSemantics.GetStatusNamesForExactValue(exactValue.Value);
            return statusNames.Count > 0
                ? $"{subject}: {string.Join(", ", statusNames)}"
                : $"{subject}: Состояние неопределено";
        }

        private static string BuildStatusBitDescription(PartyMemberReference member,
            PartyEffectOperation operation, byte mask)
        {
            string subject = BuildStatusSubject(member);
            if (mask == 0 || operation != PartyEffectOperation.BitSet)
                return $"{subject}: Состояние неопределено";

            var statusNames = PartyStatusSemantics.GetTrackedStatusNames(mask);
            return statusNames.Count > 0
                ? $"{subject}: {string.Join(", ", statusNames)}"
                : $"{subject}: Состояние неопределено";
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

            string statusesText = string.Join(", ", statusNames);
            string changeText = BuildStatusChangeText(operation, statusesText);

            return IsLoopTarget(member, LoopSemanticKind.None) || member?.SelectionKind == PartyMemberSelectionKind.Random
                ? BuildStatusChangeDescription(member, changeText)
                : $"{subject}: {changeText}";
        }

        private static string BuildStatusChangeDescription(PartyMemberReference member, string statusesText)
        {
            if (IsLoopTarget(member, LoopSemanticKind.None))
                return $"CONDITION всех персонажей в партии изменяется на {statusesText}";

            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return $"CONDITION случайного персонажа в партии изменяется на {statusesText}";

            string subject = BuildStatusSubject(member);
            return $"{subject}: {statusesText}";
        }

        private static string BuildStatusChangeText(PartyEffectOperation operation, string statusesText)
        {
            if (string.IsNullOrWhiteSpace(statusesText))
                return statusesText;

            return operation switch
            {
                PartyEffectOperation.BitClear => $"снятие {statusesText}",
                PartyEffectOperation.BitToggle => $"переключение {statusesText}",
                _ => statusesText
            };
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
            string body = exactValue.HasValue
                ? $"Читается поле +0x77 {target} (=0x{exactValue.Value:X2})"
                : $"Читается поле +0x77 {target}";

            return $"-=*Техническая(временная) заметка: {body}*=-";
        }

        private static string BuildTechnicalField77CompareDescription(PartyMemberReference member, byte compareValue)
        {
            string target = BuildTechnicalField77Target(member);
            return $"-=*Техническая(временная) заметка: Проверяется поле +0x77 {target} на значение 0x{compareValue:X2}*=-";
        }

        private static string BuildTechnicalField77BitReadDescription(PartyMemberReference member, byte mask)
        {
            string target = BuildTechnicalField77Target(member);
            return $"-=*Техническая(временная) заметка: Проверяются биты 0x{mask:X2} поля +0x77 {target}*=-";
        }

        private static string BuildTechnicalField77WriteDescription(PartyMemberReference member, byte? exactValue)
        {
            string target = BuildTechnicalField77Target(member);
            string body = exactValue.HasValue
                ? $"В поле +0x77 {target} записывается 0x{exactValue.Value:X2}"
                : $"Изменяется поле +0x77 {target}";

            return $"-=*Техническая(временная) заметка: {body}*=-";
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

            return $"-=*Техническая(временная) заметка: В поле +0x77 {target} {action} биты 0x{mask:X2}*=-";
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
