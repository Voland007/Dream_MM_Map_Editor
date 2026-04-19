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
                GetEffectiveOperation(effect),
                scope,
                GetEffectiveCondition(effect),
                memberKey,
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

            if (effect.MemberIndex.HasValue)
                parts.Add($"Member=#{effect.MemberIndex.Value}");
            else if (effect.ObservedMemberIndex.HasValue)
                parts.Add($"ObservedMember=#{effect.ObservedMemberIndex.Value}");

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

            if (field == PartyFieldKind.Technical77)
                return BuildTechnicalField77Description(effect, scope, condition);

            if (field == PartyFieldKind.Hp && operation == PartyEffectOperation.Halve)
            {
                if (condition == PartyConditionKind.MaleOnly)
                    return "! HP мужчин в партии уменьшается вдвое !";
                if (condition == PartyConditionKind.FemaleOnly)
                    return "HP женщин в партии уменьшается вдвое";
                if (scope == PartyEffectScope.WholeParty || scope == PartyEffectScope.PartySubset || scope == PartyEffectScope.CurrentLoopMember)
                    return "HP членов партии уменьшается вдвое";
                if (scope == PartyEffectScope.RandomMember)
                    return "HP случайного члена партии уменьшается вдвое";
                if (scope == PartyEffectScope.SelectedMember)
                    return "HP выбранного члена партии уменьшается вдвое";
                return "HP персонажа уменьшается вдвое";
            }

            if (field == PartyFieldKind.Hp &&
                (operation == PartyEffectOperation.Increment || operation == PartyEffectOperation.Decrement) &&
                effect.ImmediateValue.HasValue)
            {
                ushort amount = effect.ImmediateValue.Value;
                string verb = operation == PartyEffectOperation.Increment ? "добавляется" : "отнимается";

                if (condition == PartyConditionKind.MaleOnly)
                    return $"У мужчин в партии {verb} {amount} HP";

                if (condition == PartyConditionKind.FemaleOnly)
                    return $"У женщин в партии {verb} {amount} HP";

                if (scope == PartyEffectScope.WholeParty || scope == PartyEffectScope.CurrentLoopMember || IsLoopDerived(effect))
                {
                    return operation == PartyEffectOperation.Decrement
                        ? $"! У каждого персонажа партии {verb} {amount} HP !"
                        : $"У каждого персонажа партии {verb} {amount} HP";
                }

                if (scope == PartyEffectScope.PartySubset)
                    return $"У части партии {verb} {amount} HP";

                if (scope == PartyEffectScope.RandomMember)
                    return $"У случайного члена партии {verb} {amount} HP";

                if (scope == PartyEffectScope.SelectedMember)
                    return $"У выбранного члена партии {verb} {amount} HP";

                if (scope == PartyEffectScope.SingleMember && effect.MemberIndex.HasValue)
                    return $"У персонажа #{effect.MemberIndex.Value} {verb} {amount} HP";

                return operation == PartyEffectOperation.Increment
                    ? $"HP персонажа увеличивается на {amount}"
                    : $"HP персонажа уменьшается на {amount}";
            }

            string subject = BuildSubject(effect, field, scope, condition);
            if (string.IsNullOrWhiteSpace(subject))
                return effect.Description;

            if (field == PartyFieldKind.Hp && operation == PartyEffectOperation.Write)
                return $"Изменяется HP {subject}";

            if (field == PartyFieldKind.Gender && operation == PartyEffectOperation.Write)
                return $"Меняется пол {subject}";

            if (field == PartyFieldKind.Gender && operation == PartyEffectOperation.Compare)
                return $"Проверяется пол {subject}";

            if (field == PartyFieldKind.Status && effect.ImmediateValue.HasValue)
            {
                string statusesText = FormatStatusNames(effect.ImmediateValue.Value);
                if (!string.IsNullOrWhiteSpace(statusesText))
                {
                    if (scope == PartyEffectScope.PartySubset)
                    {
                        if (condition == PartyConditionKind.MaleOnly)
                            return $"CONDITION мужчин в партии изменяется на {statusesText}";

                        if (condition == PartyConditionKind.FemaleOnly)
                            return $"CONDITION женщин в партии изменяется на {statusesText}";

                        return $"CONDITION части партии изменяется на {statusesText}";
                    }

                    if (scope == PartyEffectScope.RandomMember &&
                        (operation == PartyEffectOperation.BitSet || operation == PartyEffectOperation.Write))
                    {
                        return $"CONDITION случайного персонажа в партии изменяется на {statusesText}";
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
                   GetEffectiveOperation(effect) == PartyEffectOperation.Compare &&
                   GetEffectiveField(effect) != PartyFieldKind.Technical77;
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

            if (GetEffectiveField(effect) == PartyFieldKind.Technical77)
                return true;

            return IsStateChanging(effect);
        }

        public static bool ShouldIncludeInNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (effect == null)
                return false;

            if (GetEffectiveField(effect) == PartyFieldKind.Technical77)
                return true;

            if (!IsStateChanging(effect))
                return false;

            if (IsRedundantHpImplementationDetailForNotes(effect, allEffects))
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
                : effect.Kind == PartyEffectKind.GenderWritten || effect.Kind == PartyEffectKind.GenderCompared
                    ? PartyFieldKind.Gender
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
                PartyEffectKind.GenderWritten => PartyEffectOperation.Write,
                PartyEffectKind.GenderCompared => PartyEffectOperation.Compare,
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
                return GetEffectiveCondition(effect) != PartyConditionKind.None
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

        private static PartyValueKnowledge GetEffectiveValueKnowledge(PartyEffect effect)
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

            if (GetEffectiveField(effect) != PartyFieldKind.Gender)
                return false;

            var condition = GetEffectiveCondition(effect);
            if (condition == PartyConditionKind.None)
                return false;

            return (allEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, effect))
                .Any(candidate =>
                    GetEffectiveField(candidate) == PartyFieldKind.Hp &&
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

        private static bool IsRedundantHpImplementationDetailForNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (GetEffectiveField(effect) != PartyFieldKind.Hp)
                return false;

            if (GetEffectiveScope(effect) != PartyEffectScope.SingleMember || !effect.MemberIndex.HasValue)
                return false;

            return (allEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(candidate => candidate != null && !ReferenceEquals(candidate, effect))
                .Any(candidate => IsAggregatedHpSummaryForSingleMemberArtifact(effect, candidate));
        }

        private static bool IsLikelyHpLossConsequenceStatus(PartyEffect effect)
        {
            if (effect == null)
                return false;

            if (effect.ImmediateValue.HasValue)
            {
                byte rawValue = (byte)effect.ImmediateValue.Value;
                return (rawValue & (PartyStatusSemantics.UnconsciousMask | PartyStatusSemantics.DeadMask)) != 0;
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

        private static bool IsAggregatedHpSummaryForSingleMemberArtifact(PartyEffect specificEffect, PartyEffect aggregateEffect)
        {
            if (specificEffect == null || aggregateEffect == null)
                return false;

            if (GetEffectiveField(aggregateEffect) != PartyFieldKind.Hp)
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

            return aggregateCondition == specificCondition;
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

        private static string BuildTechnicalField77Description(PartyEffect effect, PartyEffectScope scope, PartyConditionKind condition)
        {
            if (effect == null)
                return null;

            string target = BuildTechnicalField77Target(effect, scope, condition);
            string label = PartyTechnicalField77Semantics.FieldLabel;
            var operation = GetEffectiveOperation(effect);
            var knowledge = GetEffectiveValueKnowledge(effect);

            return operation switch
            {
                PartyEffectOperation.Read => effect.ImmediateValue.HasValue
                    ? $"Читается {label} {target} (=0x{effect.ImmediateValue.Value:X2})"
                    : $"Читается {label} {target}",
                PartyEffectOperation.Compare => effect.ImmediateValue.HasValue
                    ? knowledge == PartyValueKnowledge.ExactDerived
                        ? $"Проверяются биты 0x{effect.ImmediateValue.Value:X2} {label} {target}"
                        : $"Проверяется {label} {target} на значение 0x{effect.ImmediateValue.Value:X2}"
                    : $"Проверяется {label} {target}",
                PartyEffectOperation.BitSet => effect.ImmediateValue.HasValue
                    ? $"В {label} {target} устанавливаются биты 0x{effect.ImmediateValue.Value:X2}"
                    : $"Изменяется {label} {target}",
                PartyEffectOperation.BitClear => effect.ImmediateValue.HasValue
                    ? $"В {label} {target} сбрасываются биты 0x{effect.ImmediateValue.Value:X2}"
                    : $"Изменяется {label} {target}",
                PartyEffectOperation.BitToggle => effect.ImmediateValue.HasValue
                    ? $"В {label} {target} переключаются биты 0x{effect.ImmediateValue.Value:X2}"
                    : $"Изменяется {label} {target}",
                PartyEffectOperation.Write => effect.ImmediateValue.HasValue
                    ? $"В {label} {target} записывается 0x{effect.ImmediateValue.Value:X2}"
                    : $"Изменяется {label} {target}",
                _ => !string.IsNullOrWhiteSpace(effect.Description)
                    ? effect.Description
                    : $"{label}: {target}"
            };
        }

        private static string BuildTechnicalField77Target(PartyEffect effect, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"у персонажа #{effect.MemberIndex.Value}"
                    : effect.ObservedMemberIndex.HasValue
                        ? $"у персонажа #{effect.ObservedMemberIndex.Value}"
                        : "у персонажа",
                PartyEffectScope.RandomMember => "у случайного члена партии",
                PartyEffectScope.SelectedMember => "у выбранного члена партии",
                PartyEffectScope.CurrentLoopMember => "у текущего члена партии",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => "у мужчин в партии",
                    PartyConditionKind.FemaleOnly => "у женщин в партии",
                    _ => "у части партии"
                },
                PartyEffectScope.WholeParty => "у каждого члена партии",
                _ => effect.ObservedMemberIndex.HasValue
                    ? $"у персонажа #{effect.ObservedMemberIndex.Value}"
                    : "у персонажа"
            };
        }

        private static string BuildStatusTarget(PartyEffect effect, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"Персонаж #{effect.MemberIndex.Value}"
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

        private static string BuildSubject(PartyEffect effect, PartyFieldKind field, PartyEffectScope scope, PartyConditionKind condition)
        {
            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"персонажа #{effect.MemberIndex.Value}"
                    : "персонажа партии",
                PartyEffectScope.RandomMember => field == PartyFieldKind.Hp
                    ? "случайного члена партии"
                    : "у случайного члена партии",
                PartyEffectScope.SelectedMember => field == PartyFieldKind.Hp
                    ? "выбранного члена партии"
                    : "у выбранного члена партии",
                PartyEffectScope.CurrentLoopMember => "текущего члена партии",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => field == PartyFieldKind.Hp ? "мужчин в партии" : "у мужчин в партии",
                    PartyConditionKind.FemaleOnly => field == PartyFieldKind.Hp ? "женщин в партии" : "у женщин в партии",
                    _ => field == PartyFieldKind.Hp ? "части партии" : "у части партии"
                },
                PartyEffectScope.WholeParty => field == PartyFieldKind.Hp ? "членов партии" : "у членов партии",
                _ => field == PartyFieldKind.Hp ? "персонажа" : "персонажа партии"
            };
        }

        private static string FormatFieldName(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Hp => "HP",
                PartyFieldKind.HpHigh => "старший байт HP",
                PartyFieldKind.HpLow => "младший байт HP",
                PartyFieldKind.Gender => "пол",
                PartyFieldKind.Status => "status",
                PartyFieldKind.Technical77 => PartyTechnicalField77Semantics.FieldLabel,
                _ => field.ToString()
            };
        }
    }
}
