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

            return string.Join("|",
                effect.Kind,
                GetEffectiveField(effect),
                GetEffectiveOperation(effect),
                GetEffectiveScope(effect),
                GetEffectiveCondition(effect),
                effect.MemberIndex?.ToString() ?? "-",
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

            if (field == PartyFieldKind.Hp && operation == PartyEffectOperation.Halve)
            {
                if (condition == PartyConditionKind.MaleOnly)
                    return "! HP мужчин в партии уменьшается вдвое !";
                if (condition == PartyConditionKind.FemaleOnly)
                    return "HP женщин в партии уменьшается вдвое";
                if (scope == PartyEffectScope.WholeParty || scope == PartyEffectScope.PartySubset || scope == PartyEffectScope.CurrentLoopMember)
                    return "HP членов партии уменьшается вдвое";
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

            return !string.IsNullOrWhiteSpace(effect.Description)
                ? effect.Description
                : $"{FormatFieldName(field)}: {subject}";
        }

        public static bool IsGuardLike(PartyEffect effect)
        {
            return effect != null && GetEffectiveOperation(effect) == PartyEffectOperation.Compare;
        }

        public static bool IsStateChanging(PartyEffect effect)
        {
            return effect != null && GetEffectiveOperation(effect) != PartyEffectOperation.Compare;
        }

        public static bool ShouldIncludeInNotes(PartyEffect effect, IEnumerable<PartyEffect> allEffects)
        {
            if (effect == null)
                return false;

            return IsStateChanging(effect);
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
                _ => PartyEffectOperation.Unknown
            };
        }

        private static PartyEffectScope GetEffectiveScope(PartyEffect effect)
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

        private static string FormatValue(PartyEffect effect, PartyValueKnowledge knowledge)
        {
            if (effect.ImmediateValue.HasValue)
                return effect.ImmediateValue.Value.ToString();

            if (effect.ImmediateRange != null)
                return $"range {effect.ImmediateRange.Min}-{effect.ImmediateRange.Max}";

            return knowledge.ToString();
        }

        private static string BuildSubject(PartyEffect effect, PartyFieldKind field, PartyEffectScope scope, PartyConditionKind condition)
        {
            string noun = field == PartyFieldKind.Gender ? "пол" : FormatFieldName(field);

            return scope switch
            {
                PartyEffectScope.SingleMember => effect.MemberIndex.HasValue
                    ? $"персонажа #{effect.MemberIndex.Value}"
                    : "персонажа партии",
                PartyEffectScope.CurrentLoopMember => "текущего члена партии",
                PartyEffectScope.PartySubset => condition switch
                {
                    PartyConditionKind.MaleOnly => field == PartyFieldKind.Hp ? "мужчин в партии" : "у мужчин в партии",
                    PartyConditionKind.FemaleOnly => field == PartyFieldKind.Hp ? "женщин в партии" : "у женщин в партии",
                    _ => field == PartyFieldKind.Hp ? "части партии" : "у части партии"
                },
                PartyEffectScope.WholeParty => field == PartyFieldKind.Hp ? "членов партии" : "у членов партии",
                _ => noun == "HP" ? "персонажа" : "персонажа партии"
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
                _ => field.ToString()
            };
        }
    }
}
