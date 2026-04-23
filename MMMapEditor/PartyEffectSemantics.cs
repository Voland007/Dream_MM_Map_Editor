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
    public static class PartyEffectSemantics
    {
        private static string FormatMemberDisplay(int memberIndex)
        {
            return PartyMemberReference.FormatDisplayIndex(memberIndex);
        }

        public static string BuildSemanticKey(PartyEffect effect)
        {
            if (effect == null)
                return "<NULL_PARTY_EFFECT>";

            string range = effect.ImmediateRange == null
                ? "-"
                : $"{effect.ImmediateRange.Min}-{effect.ImmediateRange.Max}";

            PartyEffectScope scope = GetEffectiveScope(effect);
            string memberKey = scope == PartyEffectScope.SingleMember
                ? effect.MemberIndex?.ToString() ?? "-"
                : "-";

            return string.Join("|",
                effect.Kind,
                GetEffectiveField(effect),
                effect.ComparedField,
                GetEffectiveOperation(effect),
                scope,
                GetEffectiveCondition(effect),
                BuildGuardPredicatesKey(effect),
                memberKey,
                effect.ComparedMemberIndex?.ToString() ?? "-",
                IsLoopDerived(effect) ? "Loop" : "Direct",
                GetEffectiveValueKnowledge(effect),
                effect.ImmediateValue?.ToString() ?? "-",
                range);
        }

        public static string BuildDebugLine(PartyEffect effect)
        {
            if (effect == null)
                return "<null>";

            var parts = new List<string>
            {
                $"Kind={effect.Kind}",
                $"Field={GetEffectiveField(effect)}",
                $"Op={GetEffectiveOperation(effect)}",
                $"Scope={GetEffectiveScope(effect)}"
            };

            var condition = GetEffectiveCondition(effect);
            if (condition != PartyConditionKind.None)
                parts.Add($"Condition={condition}");

            string guardKey = BuildGuardPredicatesKey(effect);
            if (guardKey != "<NO_GUARDS>")
                parts.Add($"Guards={guardKey}");

            if (effect.MemberIndex.HasValue)
                parts.Add($"Member={FormatMemberDisplay(effect.MemberIndex.Value)}");
            else if (effect.ObservedMemberIndex.HasValue)
                parts.Add($"ObservedMember={FormatMemberDisplay(effect.ObservedMemberIndex.Value)}");

            if (effect.ComparedField != PartyFieldKind.Unknown)
                parts.Add($"ComparedField={effect.ComparedField}");

            if (effect.ComparedMemberIndex.HasValue)
                parts.Add($"ComparedMember={FormatMemberDisplay(effect.ComparedMemberIndex.Value)}");

            if (IsLoopDerived(effect))
                parts.Add("LoopDerived=True");

            var knowledge = GetEffectiveValueKnowledge(effect);
            if (knowledge != PartyValueKnowledge.Unknown)
                parts.Add($"Value={FormatValue(effect, knowledge)}");

            if (effect.InstructionAddress != 0)
                parts.Add($"At=0x{effect.InstructionAddress:X4}");

            if (!string.IsNullOrWhiteSpace(effect.Description))
                parts.Add($"Text={effect.Description}");

            return string.Join(", ", parts);
        }

        public static string BuildHumanDescription(PartyEffect effect)
        {
            if (effect == null)
                return null;

            var field = GetEffectiveField(effect);
            var operation = GetEffectiveOperation(effect);
            var scope = GetEffectiveScope(effect);
            var condition = GetEffectiveCondition(effect);

            if (PartyQuestLordFieldSemantics.IsQuestField(field))
                return BuildQuestLordFieldDescription(effect, field, scope, condition);

            if (PartyAlignmentSemantics.IsAlignmentField(field))
                return BuildAlignmentDescription(effect, field, operation, scope, condition);

            if (IsScalarPartyStatField(field) && operation == PartyEffectOperation.Halve)
            {
                string statLabel = GetScalarPartyStatLabel(field);
                if (condition == PartyConditionKind.MaleOnly)
                    return $"! {statLabel} каждого мужчины в партии уменьшается вдвое !";
                if (condition == PartyConditionKind.FemaleOnly)
                    return $"{statLabel} женщин в партии уменьшается вдвое";
                if (scope == PartyEffectScope.WholeParty)
                    return $"! {statLabel} каждого персонажа партии уменьшается вдвое !";
                if (scope == PartyEffectScope.PartySubset)
                    return $"{statLabel} части партии уменьшается вдвое";
                if (scope == PartyEffectScope.CurrentLoopMember)
                    return $"{statLabel} текущего члена партии уменьшается вдвое";
                if (scope == PartyEffectScope.RandomMember)
                    return $"{statLabel} случайного члена партии уменьшается вдвое";
                if (scope == PartyEffectScope.SelectedMember)
                    return $"{statLabel} выбранного члена партии уменьшается вдвое";
                return $"{statLabel} персонажа уменьшается вдвое";
            }

            if (IsScalarPartyStatField(field) &&
                (operation == PartyEffectOperation.Increment || operation == PartyEffectOperation.Decrement) &&
                effect.ImmediateValue.HasValue)
            {
                ushort amount = effect.ImmediateValue.Value;
                string verb = operation == PartyEffectOperation.Increment ? "добавляется" : "отнимается";
                string statLabel = GetScalarPartyStatLabel(field);

                if (condition == PartyConditionKind.MaleOnly)
                    return $"У мужчин в партии {verb} {amount} {statLabel}";

                if (condition == PartyConditionKind.FemaleOnly)
                    return $"У женщин в партии {verb} {amount} {statLabel}";

                if (scope == PartyEffectScope.WholeParty || scope == PartyEffectScope.CurrentLoopMember || IsLoopDerived(effect))
                {
                    return operation == PartyEffectOperation.Decrement
                        ? $"! У каждого персонажа партии {verb} {amount} {statLabel} !"
                        : $"У каждого персонажа партии {verb} {amount} {statLabel}";
                }

                if (scope == PartyEffectScope.PartySubset)
                    return $"У части партии {verb} {amount} {statLabel}";

                if (scope == PartyEffectScope.RandomMember)
                    return $"У случайного члена партии {verb} {amount} {statLabel}";

                if (scope == PartyEffectScope.SelectedMember)
                    return $"У выбранного члена партии {verb} {amount} {statLabel}";

                if (scope == PartyEffectScope.SingleMember && effect.MemberIndex.HasValue)
                    return $"У персонажа {FormatMemberDisplay(effect.MemberIndex.Value)} {verb} {amount} {statLabel}";

                return operation == PartyEffectOperation.Increment
                    ? $"{statLabel} персонажа увеличивается на {amount}"
                    : $"{statLabel} персонажа уменьшается на {amount}";
            }

            string subject = BuildSubject(effect, field, scope, condition);
            if (string.IsNullOrWhiteSpace(subject))
                return effect.Description;

            if (IsScalarPartyStatField(field) && operation == PartyEffectOperation.Write)
            {
                if (effect.ImmediateValue.HasValue)
                    return BuildExactStatWriteDescription(effect, field, scope, condition, effect.ImmediateValue.Value);

                return $"Изменяется {GetScalarPartyStatLabel(field)} {subject}";
            }

            if (field == PartyFieldKind.sex && operation == PartyEffectOperation.Write)
                return $"Меняется пол {subject}";

            if (field == PartyFieldKind.sex && operation == PartyEffectOperation.Compare)
                return $"Проверяется пол {subject}";

            if (field == PartyFieldKind.Status && effect.ImmediateValue.HasValue)
            {
                string statusesText = FormatStatusNames(effect.ImmediateValue.Value);
                if (!string.IsNullOrWhiteSpace(statusesText))
                {
                    string conditionStatusesText = BuildStatusChangeText(operation, statusesText);

                    if (scope == PartyEffectScope.PartySubset)
                    {
                        if (condition == PartyConditionKind.MaleOnly)
                            return $"CONDITION мужчин в партии изменяется на {conditionStatusesText}";

                        if (condition == PartyConditionKind.FemaleOnly)
                            return $"CONDITION женщин в партии изменяется на {conditionStatusesText}";

                        return $"CONDITION части партии изменяется на {conditionStatusesText}";
                    }

                    if (scope == PartyEffectScope.RandomMember &&
                        (operation == PartyEffectOperation.BitSet ||
                         operation == PartyEffectOperation.BitClear ||
                         operation == PartyEffectOperation.BitToggle ||
                         operation == PartyEffectOperation.Write))
                    {
                        return $"CONDITION случайного персонажа в партии изменяется на {conditionStatusesText}";
                    }

                    if (scope == PartyEffectScope.WholeParty &&
                        (operation == PartyEffectOperation.BitSet ||
                         operation == PartyEffectOperation.BitClear ||
                         operation == PartyEffectOperation.BitToggle ||
                         operation == PartyEffectOperation.Write))
                    {
                        return $"CONDITION всех персонажей в партии изменяется на {conditionStatusesText}";
                    }

                    string statusTarget = BuildStatusTarget(effect, scope, condition);
                    return operation switch
                    {
                        PartyEffectOperation.BitSet => $"{statusTarget}: {statusesText}",
                        PartyEffectOperation.BitClear => $"{statusTarget}: снятие {statusesText}",
                        PartyEffectOperation.BitToggle => $"{statusTarget}: переключение {statusesText}",
                        PartyEffectOperation.Write => $"{statusTarget}: {statusesText}",
                        _ => !string.IsNullOrWhiteSpace(effect.Description)
                            ? effect.Description
                            : $"{statusTarget}: {statusesText}"
                    };
                }
            }

            return !string.IsNullOrWhiteSpace(effect.Description)
                ? effect.Description
                : $"{FormatFieldName(field)}: {subject}";
        }

        public static bool IsGuardLike(PartyEffect effect)
        {
            return effect != null &&
                   GetEffectiveOperation(effect) == PartyEffectOperation.Compare;
        }

        public static bool IsStateChanging(PartyEffect effect)
        {
            if (effect == null)
                return false;

            PartyEffectOperation operation = GetEffectiveOperation(effect);
            return operation != PartyEffectOperation.Compare &&
                   operation != PartyEffectOperation.Read;
        }

        public static bool IsSemanticOutcomeEffect(PartyEffect effect)
        {
            if (effect == null)
                return false;

            if (PartyQuestLordFieldSemantics.IsQuestField(GetEffectiveField(effect)))
                return IsStateChanging(effect);

            if (IsStructuralFieldComparison(effect))
                return true;

            return IsStateChanging(effect);
        }

        public static bool ShouldIncludeInNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (effect == null)
                return false;

            if (PartyQuestLordFieldSemantics.IsQuestField(GetEffectiveField(effect)))
                return IsStateChanging(effect);

            if (IsStructuralFieldComparison(effect))
                return true;

            if (!IsStateChanging(effect))
                return false;

            if (IsRedundantStatImplementationDetailForNotes(effect, allEffects))
                return false;

            if (IsStatusDerivedFromHpLossForNotes(effect, allEffects))
                return false;

            return true;
        }

        public static IEnumerable<string> BuildDebugLines(IEnumerable<PartyEffect> effects)
        {
            return effects == null
                ? Enumerable.Empty<string>()
                : effects.Where(e => e != null)
                    .OrderBy(BuildSemanticKey)
                    .Select(BuildDebugLine);
        }

        public static PartyFieldKind GetEffectiveField(PartyEffect effect)
        {
            if (effect == null)
                return PartyFieldKind.Unknown;

            if (effect.Field != PartyFieldKind.Unknown)
                return effect.Field;

            return effect.Kind == PartyEffectKind.HpHalved || effect.Kind == PartyEffectKind.HpWritten
                ? PartyFieldKind.Hp
                : effect.Kind == PartyEffectKind.sexWritten || effect.Kind == PartyEffectKind.sexCompared
                    ? PartyFieldKind.sex
                    : effect.Kind == PartyEffectKind.AlignmentWritten || effect.Kind == PartyEffectKind.AlignmentCompared
                        ? effect.Field
                    : effect.Kind == PartyEffectKind.StatusWritten
                        ? PartyFieldKind.Status
                        : effect.Kind == PartyEffectKind.TechnicalFieldRead ||
                          effect.Kind == PartyEffectKind.TechnicalFieldWritten ||
                          effect.Kind == PartyEffectKind.TechnicalFieldCompared
                            ? PartyFieldKind.Technical77
                        : PartyFieldKind.Unknown;
        }

        public static PartyEffectOperation GetEffectiveOperation(PartyEffect effect)
        {
            if (effect == null)
                return PartyEffectOperation.Unknown;

            if (effect.Operation != PartyEffectOperation.Unknown)
                return effect.Operation;

            return effect.Kind switch
            {
                PartyEffectKind.HpHalved => PartyEffectOperation.Halve,
                PartyEffectKind.HpWritten => PartyEffectOperation.Write,
                PartyEffectKind.sexWritten => PartyEffectOperation.Write,
                PartyEffectKind.sexCompared => PartyEffectOperation.Compare,
                PartyEffectKind.AlignmentWritten => PartyEffectOperation.Write,
                PartyEffectKind.AlignmentCompared => PartyEffectOperation.Compare,
                PartyEffectKind.StatusWritten => PartyEffectOperation.Write,
                PartyEffectKind.TechnicalFieldRead => PartyEffectOperation.Read,
                PartyEffectKind.TechnicalFieldWritten => PartyEffectOperation.Write,
                PartyEffectKind.TechnicalFieldCompared => PartyEffectOperation.Compare,
                _ => PartyEffectOperation.Unknown
            };
        }

        public static PartyEffectScope GetEffectiveScope(PartyEffect effect)
        {
            if (effect == null)
                return PartyEffectScope.Unknown;

            if (effect.Scope != PartyEffectScope.Unknown)
                return effect.Scope;

            if (effect.AppliesToWholePartyLoop)
            {
                return GetEffectiveCondition(effect) != PartyConditionKind.None || HasEffectiveGuardPredicates(effect)
                    ? PartyEffectScope.PartySubset
                    : PartyEffectScope.WholeParty;
            }

            if (effect.MemberIndex.HasValue)
                return PartyEffectScope.SingleMember;

            return PartyEffectScope.Unknown;
        }

        public static PartyConditionKind GetEffectiveCondition(PartyEffect effect)
        {
            if (effect == null)
                return PartyConditionKind.None;

            if (effect.Condition != PartyConditionKind.None)
                return effect.Condition;

            if (effect.MaleOnly == true)
                return PartyConditionKind.MaleOnly;

            if (effect.FemaleOnly == true)
                return PartyConditionKind.FemaleOnly;

            return PartyConditionKind.None;
        }

        public static IReadOnlyList<PartyPredicate> GetEffectiveGuardPredicates(PartyEffect effect)
        {
            return effect?.GuardPredicates?
                .Where(predicate => predicate != null)
                .ToList() ?? (IReadOnlyList<PartyPredicate>)Array.Empty<PartyPredicate>();
        }

        public static bool HasEffectiveGuardPredicates(PartyEffect effect)
        {
            return GetEffectiveGuardPredicates(effect).Count > 0;
        }

        public static bool HaveEquivalentGuardPredicates(PartyEffect left, PartyEffect right)
        {
            return string.Equals(BuildGuardPredicatesKey(left), BuildGuardPredicatesKey(right), StringComparison.Ordinal);
        }

        public static PartyMemberReference NormalizeLoopTargetMember(PartyMemberReference member)
        {
            var normalized = member?.Clone() ?? new PartyMemberReference();
            normalized.IsPartyLoopMember = true;
            normalized.MemberIndex = null;
            normalized.PointerAddress = null;
            normalized.PointerTableAddress = null;
            normalized.StructureAddress = null;
            normalized.SelectionKind = PartyMemberSelectionKind.Dynamic;
            return normalized;
        }

        public static PartyPredicate NormalizePredicateForLoopAggregation(PartyPredicate predicate)
        {
            if (predicate == null)
                return null;

            var normalized = predicate.Clone();
            if (normalized.TargetMember != null)
                normalized.TargetMember = NormalizeLoopTargetMember(normalized.TargetMember);

            return normalized;
        }

        public static List<PartyPredicate> NormalizeGuardPredicatesForLoopAggregation(IEnumerable<PartyPredicate> predicates)
        {
            return predicates?
                .Where(predicate => predicate != null)
                .Select(NormalizePredicateForLoopAggregation)
                .Where(predicate => predicate != null)
                .GroupBy(BuildPredicateKey)
                .Select(group => group.First())
                .OrderBy(BuildPredicateKey)
                .ToList() ?? new List<PartyPredicate>();
        }

        public static string BuildGuardPredicatesKey(PartyEffect effect)
        {
            var predicates = GetEffectiveGuardPredicates(effect);
            if (predicates.Count == 0)
                return "<NO_GUARDS>";

            return string.Join("&", predicates
                .Select(BuildPredicateKey)
                .OrderBy(key => key));
        }

        public static string BuildPredicateKey(PartyPredicate predicate)
        {
            if (predicate == null)
                return "<NULL_PREDICATE>";

            string range = predicate.ImmediateRange == null
                ? "-"
                : $"{predicate.ImmediateRange.Min}-{predicate.ImmediateRange.Max}";

            return string.Join("|",
                predicate.Field,
                predicate.Comparison,
                predicate.ValueKnowledge,
                predicate.ImmediateValue?.ToString() ?? "-",
                range,
                BuildPredicateTargetKey(predicate.TargetMember));
        }

        internal static PartyValueKnowledge GetEffectiveValueKnowledgeForDiagnostics(PartyEffect effect)
        {
            if (effect == null)
                return PartyValueKnowledge.Unknown;

            if (effect.ValueKnowledge != PartyValueKnowledge.Unknown)
                return effect.ValueKnowledge;

            if (effect.ImmediateValue.HasValue)
                return PartyValueKnowledge.ExactImmediate;

            if (effect.ImmediateRange != null)
                return PartyValueKnowledge.Range;

            return GetEffectiveOperation(effect) == PartyEffectOperation.Halve
                ? PartyValueKnowledge.Structural
                : PartyValueKnowledge.Unknown;
        }

        private static PartyValueKnowledge GetEffectiveValueKnowledge(PartyEffect effect)
        {
            return GetEffectiveValueKnowledgeForDiagnostics(effect);
        }

        public static bool IsLoopDerived(PartyEffect effect)
        {
            if (effect == null)
                return false;

            if (effect.IsLoopDerived)
                return true;

            var scope = GetEffectiveScope(effect);
            return scope == PartyEffectScope.CurrentLoopMember ||
                   scope == PartyEffectScope.PartySubset ||
                   scope == PartyEffectScope.WholeParty;
        }

        private static bool IsRedundantGuardForNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (!IsGuardLike(effect))
                return false;

            if (GetEffectiveField(effect) != PartyFieldKind.sex)
                return false;

            var condition = GetEffectiveCondition(effect);
            if (condition == PartyConditionKind.None)
                return false;

            return (allEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, effect))
                .Any(candidate =>
                    IsScalarPartyStatField(GetEffectiveField(candidate)) &&
                    GetEffectiveOperation(candidate) == PartyEffectOperation.Halve &&
                    GetEffectiveCondition(candidate) == condition &&
                    candidate.MemberIndex == effect.MemberIndex &&
                    IsLoopDerived(candidate) == IsLoopDerived(effect));
        }

        private static bool IsStatusDerivedFromHpLossForNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (GetEffectiveField(effect) != PartyFieldKind.Status)
                return false;

            if (!IsLikelyHpLossConsequenceStatus(effect))
                return false;

            return (allEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, effect))
                .Any(candidate =>
                    IsHpLossEffect(candidate) &&
                    TargetsSamePartyMembers(effect, candidate) &&
                    IsLikelyConsequenceByInstructionOrder(effect, candidate));
        }

        private static bool IsRedundantStatImplementationDetailForNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (!IsScalarPartyStatField(GetEffectiveField(effect)))
                return false;

            if (GetEffectiveScope(effect) != PartyEffectScope.SingleMember || !effect.MemberIndex.HasValue)
                return false;

            return (allEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, effect))
                .Any(candidate => IsAggregatedStatSummaryForSingleMemberArtifact(effect, candidate));
        }

        private static bool IsLikelyHpLossConsequenceStatus(PartyEffect effect)
        {
            if (effect == null)
                return false;

            if (effect.ImmediateValue.HasValue)
            {
                byte rawValue = (byte)effect.ImmediateValue.Value;
                return (rawValue & PartyStatusSemantics.UnconsciousMask) != 0 ||
                       PartyStatusSemantics.IsDeadStatusValue(rawValue);
            }

            string description = effect.Description ?? string.Empty;
            return description.IndexOf("UNCONSCIOUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   description.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHpLossEffect(PartyEffect effect)
        {
            if (effect == null || GetEffectiveField(effect) != PartyFieldKind.Hp)
                return false;

            var operation = GetEffectiveOperation(effect);
            return operation == PartyEffectOperation.Halve ||
                   operation == PartyEffectOperation.Decrement;
        }

        private static bool TargetsSamePartyMembers(PartyEffect left, PartyEffect right)
        {
            if (left == null || right == null)
                return false;

            if (MatchesSpecificMemberAgainstLoopAggregate(left, right) ||
                MatchesSpecificMemberAgainstLoopAggregate(right, left))
            {
                return true;
            }

            return left.MemberIndex == right.MemberIndex &&
                   GetEffectiveScope(left) == GetEffectiveScope(right) &&
                   GetEffectiveCondition(left) == GetEffectiveCondition(right) &&
                   IsLoopDerived(left) == IsLoopDerived(right);
        }

        private static bool IsAggregatedStatSummaryForSingleMemberArtifact(PartyEffect specificEffect, PartyEffect aggregateEffect)
        {
            if (specificEffect == null || aggregateEffect == null)
                return false;

            if (GetEffectiveField(aggregateEffect) != GetEffectiveField(specificEffect) ||
                !IsScalarPartyStatField(GetEffectiveField(aggregateEffect)))
                return false;

            if (!IsPartyWideLoopEffect(aggregateEffect))
                return false;

            if (!ConditionsCompatibleForAggregate(specificEffect, aggregateEffect))
                return false;

            var specificOperation = GetEffectiveOperation(specificEffect);
            var aggregateOperation = GetEffectiveOperation(aggregateEffect);

            if (specificOperation == PartyEffectOperation.Write)
            {
                return aggregateOperation != PartyEffectOperation.Write &&
                       IsLikelyConsequenceByInstructionOrder(specificEffect, aggregateEffect);
            }

            if (specificOperation != aggregateOperation)
                return false;

            if (specificEffect.ImmediateValue.HasValue || aggregateEffect.ImmediateValue.HasValue)
            {
                if (specificEffect.ImmediateValue != aggregateEffect.ImmediateValue)
                    return false;
            }

            if (specificEffect.ImmediateRange != null || aggregateEffect.ImmediateRange != null)
            {
                if (specificEffect.ImmediateRange == null || aggregateEffect.ImmediateRange == null)
                    return false;

                if (specificEffect.ImmediateRange.Min != aggregateEffect.ImmediateRange.Min ||
                    specificEffect.ImmediateRange.Max != aggregateEffect.ImmediateRange.Max)
                {
                    return false;
                }
            }

            if (specificEffect.InstructionAddress != 0 &&
                aggregateEffect.InstructionAddress != 0 &&
                specificEffect.InstructionAddress != aggregateEffect.InstructionAddress)
            {
                return false;
            }

            return true;
        }

        private static bool MatchesSpecificMemberAgainstLoopAggregate(PartyEffect specificEffect, PartyEffect aggregateEffect)
        {
            if (specificEffect == null || aggregateEffect == null)
                return false;

            if (GetEffectiveScope(specificEffect) != PartyEffectScope.SingleMember || !specificEffect.MemberIndex.HasValue)
                return false;

            return IsPartyWideLoopEffect(aggregateEffect) &&
                   ConditionsCompatibleForAggregate(specificEffect, aggregateEffect);
        }

        private static bool IsPartyWideLoopEffect(PartyEffect effect)
        {
            if (effect == null)
                return false;

            var scope = GetEffectiveScope(effect);
            return IsLoopDerived(effect) &&
                   (scope == PartyEffectScope.WholeParty ||
                    scope == PartyEffectScope.PartySubset ||
                    scope == PartyEffectScope.CurrentLoopMember);
        }

        private static bool ConditionsCompatibleForAggregate(PartyEffect specificEffect, PartyEffect aggregateEffect)
        {
            var specificCondition = GetEffectiveCondition(specificEffect);
            var aggregateCondition = GetEffectiveCondition(aggregateEffect);

            return aggregateCondition == specificCondition &&
                   HaveEquivalentGuardPredicates(specificEffect, aggregateEffect);
        }

        private static string BuildPredicateTargetKey(PartyMemberReference member)
        {
            if (member == null)
                return "-";

            if (member.IsPartyLoopMember)
                return $"{PartyMemberSelectionKind.Dynamic}:Loop:-:-:-";

            return string.Join(":",
                member.SelectionKind,
                member.IsPartyLoopMember ? "Loop" : "Direct",
                member.MemberIndex?.ToString() ?? "-",
                member.PointerTableAddress?.ToString("X4") ?? "-",
                member.StructureAddress?.ToString("X4") ?? "-");
        }

        private static bool IsLikelyConsequenceByInstructionOrder(PartyEffect statusEffect, PartyEffect hpEffect)
        {
            if (statusEffect == null || hpEffect == null)
                return false;

            if (statusEffect.InstructionAddress == 0 || hpEffect.InstructionAddress == 0)
                return true;

            return statusEffect.InstructionAddress >= hpEffect.InstructionAddress;
        }

        private static string FormatValue(PartyEffect effect, PartyValueKnowledge knowledge)
        {
            if (effect.ImmediateValue.HasValue)
                return effect.ImmediateValue.Value.ToString();

            if (effect.ImmediateRange != null)
                return $"range {effect.ImmediateRange.Min}-{effect.ImmediateRange.Max}";

            return knowledge.ToString();
        }

        private static string FormatStatusNames(ushort rawValue)
        {
            var statusNames = PartyStatusSemantics.GetStatusNamesForExactValue((byte)rawValue);
            return statusNames.Count == 0 ? null : string.Join(", ", statusNames);
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

        private static string BuildQuestLordFieldDescription(
            PartyEffect effect,
            PartyFieldKind field,
            PartyEffectScope scope,
            PartyConditionKind condition)
        {
            if (effect == null || !PartyQuestLordFieldSemantics.IsQuestField(field))
                return null;

            string fieldLabel = PartyQuestLordFieldSemantics.GetFieldLabel(field);
            string lordLabel = PartyQuestLordFieldSemantics.GetLordLabel(field);
            string targetPrefix = BuildQuestLordTargetPrefix(effect, scope, condition);
            var operation = GetEffectiveOperation(effect);
            var knowledge = GetEffectiveValueKnowledge(effect);

            string body = operation switch
            {
                PartyEffectOperation.Read => effect.ImmediateValue.HasValue
                    ? ComposeQuestLordSentence(
                        targetPrefix,
                        $"Читается {fieldLabel} (=0x{effect.ImmediateValue.Value:X2})",
                        $"читается {fieldLabel} (=0x{effect.ImmediateValue.Value:X2})")
                    : ComposeQuestLordSentence(targetPrefix, $"Читается {fieldLabel}", $"читается {fieldLabel}"),
                PartyEffectOperation.Compare => effect.ImmediateValue.HasValue
                    ? knowledge == PartyValueKnowledge.ExactDerived
                        ? BuildQuestLordCompareDescription(targetPrefix, lordLabel, (byte)effect.ImmediateValue.Value)
                        : ComposeQuestLordSentence(
                            targetPrefix,
                            $"Проверяется {fieldLabel} на значение 0x{effect.ImmediateValue.Value:X2}",
                            $"проверяется {fieldLabel} на значение 0x{effect.ImmediateValue.Value:X2}")
                    : ComposeQuestLordSentence(targetPrefix, $"Проверяется {fieldLabel}", $"проверяется {fieldLabel}"),
                PartyEffectOperation.BitSet => effect.ImmediateValue.HasValue
                    ? BuildQuestLordBitDescription(targetPrefix, lordLabel, PartyEffectOperation.BitSet, (byte)effect.ImmediateValue.Value)
                    : ComposeQuestLordSentence(targetPrefix, $"Изменяется {fieldLabel}", $"изменяется {fieldLabel}"),
                PartyEffectOperation.BitClear => effect.ImmediateValue.HasValue
                    ? BuildQuestLordBitDescription(targetPrefix, lordLabel, PartyEffectOperation.BitClear, (byte)effect.ImmediateValue.Value)
                    : ComposeQuestLordSentence(targetPrefix, $"Изменяется {fieldLabel}", $"изменяется {fieldLabel}"),
                PartyEffectOperation.BitToggle => effect.ImmediateValue.HasValue
                    ? BuildQuestLordBitDescription(targetPrefix, lordLabel, PartyEffectOperation.BitToggle, (byte)effect.ImmediateValue.Value)
                    : ComposeQuestLordSentence(targetPrefix, $"Изменяется {fieldLabel}", $"изменяется {fieldLabel}"),
                PartyEffectOperation.Write => effect.ImmediateValue.HasValue
                    ? BuildQuestLordWriteDescription(targetPrefix, lordLabel, (byte)effect.ImmediateValue.Value)
                    : ComposeQuestLordSentence(targetPrefix, $"Изменяется {fieldLabel}", $"изменяется {fieldLabel}"),
                _ => !string.IsNullOrWhiteSpace(effect.Description)
                    ? effect.Description
                    : fieldLabel
            };

            return WrapTechnicalQuestLordNote(body);
        }

        private static string BuildQuestLordCompareDescription(string targetPrefix, string lordLabel, byte mask)
        {
            int? questNumber = PartyQuestLordFieldSemantics.TryGetSingleQuestNumber(mask);
            if (questNumber.HasValue)
            {
                return ComposeQuestLordSentence(
                    targetPrefix,
                    $"Проверяется, готов ли квест {questNumber.Value} у {lordLabel} к сдаче",
                    $"проверяется, готов ли квест {questNumber.Value} у {lordLabel} к сдаче");
            }

            string questNumbers = PartyQuestLordFieldSemantics.FormatQuestNumbers(mask);
            return ComposeQuestLordSentence(
                targetPrefix,
                $"Проверяется, готовы ли квесты {questNumbers} у {lordLabel} к сдаче",
                $"проверяется, готовы ли квесты {questNumbers} у {lordLabel} к сдаче");
        }

        private static string BuildQuestLordBitDescription(
            string targetPrefix,
            string lordLabel,
            PartyEffectOperation operation,
            byte mask)
        {
            int? questNumber = PartyQuestLordFieldSemantics.TryGetSingleQuestNumber(mask);
            string questNumbers = PartyQuestLordFieldSemantics.FormatQuestNumbers(mask);

            return operation switch
            {
                PartyEffectOperation.BitSet when questNumber.HasValue => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Квест {questNumber.Value} у {lordLabel} готов к сдаче",
                    $"квест {questNumber.Value} у {lordLabel} готов к сдаче"),
                PartyEffectOperation.BitSet => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Квесты {questNumbers} у {lordLabel} готовы к сдаче",
                    $"квесты {questNumbers} у {lordLabel} готовы к сдаче"),
                PartyEffectOperation.BitClear when questNumber.HasValue => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Сбрасывается готовность к сдаче квеста {questNumber.Value} у {lordLabel}",
                    $"сбрасывается готовность к сдаче квеста {questNumber.Value} у {lordLabel}"),
                PartyEffectOperation.BitClear => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Сбрасывается готовность к сдаче квестов {questNumbers} у {lordLabel}",
                    $"сбрасывается готовность к сдаче квестов {questNumbers} у {lordLabel}"),
                PartyEffectOperation.BitToggle when questNumber.HasValue => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Переключается готовность к сдаче квеста {questNumber.Value} у {lordLabel}",
                    $"переключается готовность к сдаче квеста {questNumber.Value} у {lordLabel}"),
                PartyEffectOperation.BitToggle => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Переключается готовность к сдаче квестов {questNumbers} у {lordLabel}",
                    $"переключается готовность к сдаче квестов {questNumbers} у {lordLabel}"),
                _ => ComposeQuestLordSentence(
                    targetPrefix,
                    $"Изменяется готовность к сдаче квестов у {lordLabel}",
                    $"изменяется готовность к сдаче квестов у {lordLabel}")
            };
        }

        private static string BuildQuestLordWriteDescription(string targetPrefix, string lordLabel, byte value)
        {
            if (value == 0)
            {
                return ComposeQuestLordSentence(
                    targetPrefix,
                    $"Сбрасывается готовность к сдаче квестов у {lordLabel}",
                    $"сбрасывается готовность к сдаче квестов у {lordLabel}");
            }

            int? questNumber = PartyQuestLordFieldSemantics.TryGetSingleQuestNumber(value);
            if (questNumber.HasValue)
            {
                return ComposeQuestLordSentence(
                    targetPrefix,
                    $"Квест {questNumber.Value} у {lordLabel} готов к сдаче",
                    $"квест {questNumber.Value} у {lordLabel} готов к сдаче");
            }

            string questNumbers = PartyQuestLordFieldSemantics.FormatQuestNumbers(value);
            return ComposeQuestLordSentence(
                targetPrefix,
                $"Квесты {questNumbers} у {lordLabel} готовы к сдаче",
                $"квесты {questNumbers} у {lordLabel} готовы к сдаче");
        }

        private static string BuildQuestLordTargetPrefix(PartyEffect effect, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"У персонажа {FormatMemberDisplay(effect.MemberIndex.Value)} "
                    : effect.ObservedMemberIndex.HasValue
                        ? $"У персонажа {FormatMemberDisplay(effect.ObservedMemberIndex.Value)} "
                        : string.Empty,
                PartyEffectScope.RandomMember => "У случайного члена партии ",
                PartyEffectScope.SelectedMember => "У выбранного члена партии ",
                PartyEffectScope.CurrentLoopMember => "У текущего члена партии ",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => "У мужчин в партии ",
                    PartyConditionKind.FemaleOnly => "У женщин в партии ",
                    _ => "У части партии "
                },
                PartyEffectScope.WholeParty => "У всех персонажей партии ",
                _ => effect.ObservedMemberIndex.HasValue
                    ? $"У персонажа {FormatMemberDisplay(effect.ObservedMemberIndex.Value)} "
                    : string.Empty
            };
        }

        private static string ComposeQuestLordSentence(string targetPrefix, string standaloneText, string prefixedText)
        {
            return string.IsNullOrWhiteSpace(targetPrefix)
                ? standaloneText
                : targetPrefix + prefixedText;
        }

        private static string WrapTechnicalQuestLordNote(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? text
                : $"-=*{text}*=-";
        }

        private static string BuildAlignmentDescription(
            PartyEffect effect,
            PartyFieldKind field,
            PartyEffectOperation operation,
            PartyEffectScope scope,
            PartyConditionKind condition)
        {
            if (effect == null)
                return null;

            string fieldLabel = FormatFieldName(field);
            string subject = BuildSubject(effect, field, scope, condition);
            string valueText = effect.ImmediateValue.HasValue
                ? PartyAlignmentSemantics.FormatAlignmentValue(effect.ImmediateValue.Value)
                : null;

            if (operation == PartyEffectOperation.Compare &&
                PartyAlignmentSemantics.IsAlignmentField(effect.ComparedField))
            {
                string comparedFieldLabel = FormatFieldName(effect.ComparedField);
                if (effect.ComparedMemberIndex.HasValue &&
                    effect.MemberIndex.HasValue &&
                    effect.ComparedMemberIndex.Value != effect.MemberIndex.Value)
                {
                    return $"ПРОВЕРКА УСЛОВИЯ: Сравнивается {fieldLabel} персонажа {FormatMemberDisplay(effect.MemberIndex.Value)} с {comparedFieldLabel} персонажа {FormatMemberDisplay(effect.ComparedMemberIndex.Value)}";
                }

                return $"ПРОВЕРКА УСЛОВИЯ: Проверяется, совпадают ли {fieldLabel} и {comparedFieldLabel} {subject}";
            }

            return operation switch
            {
                PartyEffectOperation.Read when !string.IsNullOrWhiteSpace(valueText)
                    => $"Читается {fieldLabel} {subject} (= {valueText})",
                PartyEffectOperation.Read
                    => $"Читается {fieldLabel} {subject}",
                PartyEffectOperation.Compare when !string.IsNullOrWhiteSpace(valueText)
                    => $"Проверяется {fieldLabel} {subject} на {valueText}",
                PartyEffectOperation.Compare
                    => $"Проверяется {fieldLabel} {subject}",
                PartyEffectOperation.Write when !string.IsNullOrWhiteSpace(valueText)
                    => $"{fieldLabel} {subject} становится {valueText}",
                PartyEffectOperation.Write
                    => $"Меняется {fieldLabel} {subject}",
                _ => !string.IsNullOrWhiteSpace(effect.Description)
                    ? effect.Description
                    : $"{fieldLabel}: {subject}"
            };
        }

        private static string BuildStatusTarget(PartyEffect effect, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"Персонаж {FormatMemberDisplay(effect.MemberIndex.Value)}"
                    : "Персонаж",
                PartyEffectScope.RandomMember => "Случайный член партии",
                PartyEffectScope.SelectedMember => "Выбранный член партии",
                PartyEffectScope.CurrentLoopMember => "Текущий член партии",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => "Мужчины в партии",
                    PartyConditionKind.FemaleOnly => "Женщины в партии",
                    _ => "Часть партии"
                },
                PartyEffectScope.WholeParty => "Члены партии",
                _ => "Персонаж"
            };
        }

        private static string BuildExactStatWriteDescription(
            PartyEffect effect,
            PartyFieldKind field,
            PartyEffectScope scope,
            PartyConditionKind condition,
            ushort value)
        {
            string statLabel = GetScalarPartyStatLabel(field);
            bool isZero = value == 0;

            if (condition == PartyConditionKind.MaleOnly)
            {
                return isZero
                    ? $"! {statLabel} каждого мужчины в партии обнуляется !"
                    : $"{statLabel} каждого мужчины в партии становится равным {value}";
            }

            if (condition == PartyConditionKind.FemaleOnly)
            {
                return isZero
                    ? $"{statLabel} женщин в партии обнуляется"
                    : $"{statLabel} женщин в партии становится равным {value}";
            }

            if (scope == PartyEffectScope.WholeParty)
            {
                return isZero
                    ? $"! {statLabel} каждого персонажа партии обнуляется !"
                    : $"{statLabel} каждого персонажа партии становится равным {value}";
            }

            if (scope == PartyEffectScope.PartySubset)
            {
                return isZero
                    ? $"{statLabel} части партии обнуляется"
                    : $"{statLabel} части партии становится равным {value}";
            }

            if (scope == PartyEffectScope.CurrentLoopMember)
            {
                return isZero
                    ? $"{statLabel} текущего члена партии обнуляется"
                    : $"{statLabel} текущего члена партии становится равным {value}";
            }

            if (scope == PartyEffectScope.RandomMember)
            {
                return isZero
                    ? $"{statLabel} случайного члена партии обнуляется"
                    : $"{statLabel} случайного члена партии становится равным {value}";
            }

            if (scope == PartyEffectScope.SelectedMember)
            {
                return isZero
                    ? $"{statLabel} выбранного члена партии обнуляется"
                    : $"{statLabel} выбранного члена партии становится равным {value}";
            }

            if (scope == PartyEffectScope.SingleMember && effect.MemberIndex.HasValue)
            {
                return isZero
                    ? $"{statLabel} персонажа {FormatMemberDisplay(effect.MemberIndex.Value)} обнуляется"
                    : $"{statLabel} персонажа {FormatMemberDisplay(effect.MemberIndex.Value)} становится равным {value}";
            }

            return isZero
                ? $"{statLabel} персонажа обнуляется"
                : $"{statLabel} персонажа становится равным {value}";
        }

        private static string BuildSubject(PartyEffect effect, PartyFieldKind field, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"персонажа {FormatMemberDisplay(effect.MemberIndex.Value)}"
                    : "персонажа партии",
                PartyEffectScope.RandomMember => IsScalarPartyStatField(field)
                    ? "случайного члена партии"
                    : "у случайного члена партии",
                PartyEffectScope.SelectedMember => IsScalarPartyStatField(field)
                    ? "выбранного члена партии"
                    : "у выбранного члена партии",
                PartyEffectScope.CurrentLoopMember => "текущего члена партии",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => IsScalarPartyStatField(field) ? "мужчин в партии" : "у мужчин в партии",
                    PartyConditionKind.FemaleOnly => IsScalarPartyStatField(field) ? "женщин в партии" : "у женщин в партии",
                    _ => IsScalarPartyStatField(field) ? "части партии" : "у части партии"
                },
                PartyEffectScope.WholeParty => IsScalarPartyStatField(field) ? "членов партии" : "у членов партии",
                _ => IsScalarPartyStatField(field) ? "персонажа" : "персонажа партии"
            };
        }

        private static string FormatFieldName(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Hp => "HP",
                PartyFieldKind.HpHigh => "старший байт HP",
                PartyFieldKind.HpLow => "младший байт HP",
                PartyFieldKind.Sp => "SP",
                PartyFieldKind.SpHigh => "старший байт SP",
                PartyFieldKind.SpLow => "младший байт SP",
                PartyFieldKind.sex => "пол",
                PartyFieldKind.InnateAlignment => PartyAlignmentSemantics.InnateFieldLabel,
                PartyFieldKind.CurrentAlignment => PartyAlignmentSemantics.CurrentFieldLabel,
                PartyFieldKind.Status => "status",
                PartyFieldKind.Technical75 or PartyFieldKind.Technical76 or PartyFieldKind.Technical77
                    => PartyQuestLordFieldSemantics.GetFieldLabel(field),
                _ => field.ToString()
            };
        }

        private static bool IsScalarPartyStatField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Hp || field == PartyFieldKind.Sp;
        }

        private static bool IsStructuralFieldComparison(PartyEffect effect)
        {
            return effect != null &&
                   GetEffectiveOperation(effect) == PartyEffectOperation.Compare &&
                   effect.ComparedField != PartyFieldKind.Unknown &&
                   GetEffectiveValueKnowledge(effect) == PartyValueKnowledge.Structural;
        }

        private static string GetScalarPartyStatLabel(PartyFieldKind field)
        {
            return field == PartyFieldKind.Sp ? "SP" : "HP";
        }
    }
}
