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

using System.Collections.Generic;
using System.Linq;

namespace MMMapEditor
{
    public static class PartyEffectNormalizer
    {
        public static List<PartyEffect> Normalize(PathAnalysisResult source)
        {
            var result = new List<PartyEffect>();
            if (source == null)
                return result;

            var allPartyEffects = source.PartyEffects?
                .Where(e => e != null)
                .Select(e => e.Clone())
                .ToList() ?? new List<PartyEffect>();

            PromotePartyScanLoopEffects(source, allPartyEffects);
            result.AddRange(allPartyEffects);

            var pendingStatOperations = CollectPendingStatOperations(source);
            PartyConditionKind inferredCondition = InferPartyCondition(
                allPartyEffects,
                pendingStatOperations.ToArray());

            foreach (var pending in pendingStatOperations
                         .Where(p => p != null && p.Field != PartyFieldKind.Unknown)
                         .OrderBy(p => p.ExecutionOrder > 0 ? 0 : 1)
                         .ThenBy(p => p.ExecutionOrder > 0 ? p.ExecutionOrder : int.MaxValue)
                         .ThenBy(p => p.StartAddress))
            {
                var normalizedEffect = CreateNormalizedStatEffect(
                    pending,
                    source.LoopSemantic,
                    inferredCondition,
                    pending.Field);
                if (normalizedEffect != null)
                    result.Add(normalizedEffect);
            }

            // Если есть loop-derived эффект с условием, он относится к части партии, а не ко всей партии.
            foreach (var effect in result)
            {
                if (effect == null)
                    continue;

                if (PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Status &&
                    PartyEffectSemantics.IsStateChanging(effect) &&
                    PartyEffectSemantics.IsLoopDerived(effect) &&
                    PartyEffectSemantics.GetEffectiveCondition(effect) == PartyConditionKind.None &&
                    !PartyEffectSemantics.HasEffectiveGuardPredicates(effect) &&
                    inferredCondition != PartyConditionKind.None)
                {
                    effect.Condition = inferredCondition;
                }

                NormalizeLoopDerivedEffect(effect);
            }

            result = RemoveRedundantStatWrittenEffects(result);
            result = RemoveLoopGuardEffects(result);

            return AggregateEquivalentEffects(result)
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();
        }

        public static bool CanCreateNormalizedStatEffect(PendingPartyStatOperation pending, PartyFieldKind field)
        {
            return CreateNormalizedStatEffect(
                       pending,
                       LoopSemanticKind.None,
                       PartyConditionKind.None,
                       field) != null;
        }

        public static PartyEffect CreateNormalizedStatEffect(PendingPartyStatOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition, PartyFieldKind field)
        {
            if (IsCompletedStatHalvingPattern(pending))
            {
                return field == PartyFieldKind.Sp
                    ? PartyEffectFactory.CreateSpHalvedEffect(pending, loopSemantic, condition)
                    : PartyEffectFactory.CreateHpHalvedEffect(pending, loopSemantic, condition);
            }

            if (TryGetExactStatAdjustment(pending, out var operation, out ushort amount))
            {
                return field == PartyFieldKind.Sp
                    ? PartyEffectFactory.CreateSpAdjustedEffect(pending, loopSemantic, condition, operation, amount)
                    : PartyEffectFactory.CreateHpAdjustedEffect(pending, loopSemantic, condition, operation, amount);
            }

            if (IsHpRestoredToMaximumPattern(pending, field))
                return PartyEffectFactory.CreateHpRestoredToMaximumEffect(pending, loopSemantic, condition);

            if (TryGetExactStatWrite(pending, out ushort value))
            {
                return field == PartyFieldKind.Sp
                    ? PartyEffectFactory.CreateSpSetEffect(pending, loopSemantic, condition, value)
                    : PartyEffectFactory.CreateHpSetEffect(pending, loopSemantic, condition, value);
            }

            return null;
        }

        public static PartyConditionKind InferPartyCondition(IEnumerable<PartyEffect> effects, params PendingPartyStatOperation[] pendingOperations)
        {
            foreach (var effect in effects ?? Enumerable.Empty<PartyEffect>())
            {
                if (effect == null || !PartyEffectSemantics.IsGuardLike(effect))
                    continue;

                if (PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.sex)
                    continue;

                var condition = PartyEffectSemantics.GetEffectiveCondition(effect);
                if (condition != PartyConditionKind.None)
                    return condition;
            }

            foreach (var pending in pendingOperations ?? Enumerable.Empty<PendingPartyStatOperation>())
            {
                if (pending?.MaleOnly == true)
                    return PartyConditionKind.MaleOnly;

                if (pending?.FemaleOnly == true)
                    return PartyConditionKind.FemaleOnly;
            }

            return PartyConditionKind.None;
        }

        public static bool IsCompletedStatHalvingPattern(PendingPartyStatOperation pending)
        {
            if (pending == null)
                return false;

            // Для 16-битного деления значения пополам достаточно шаблона
            // SHR старшего байта + RCR младшего байта: SHR сам выставляет CF,
            // который затем потребляет RCR. Предварительный CLC может встречаться,
            // но не является обязательным признаком паттерна.
            return pending.SawReadHigh &&
                   pending.SawReadLow &&
                   pending.SawWriteHigh &&
                   pending.SawWriteLow &&
                   pending.SawShrHigh &&
                   pending.SawRcrLow;
        }

        public static bool TryGetExactStatAdjustment(PendingPartyStatOperation pending,
            out PartyEffectOperation operation, out ushort amount)
        {
            operation = PartyEffectOperation.Unknown;
            amount = 0;

            if (pending == null ||
                !pending.SawReadHigh ||
                !pending.SawReadLow ||
                !pending.SawWriteHigh ||
                !pending.SawWriteLow ||
                pending.LowByteArithmetic == null ||
                pending.HighByteArithmetic == null)
            {
                return false;
            }

            var low = pending.LowByteArithmetic;
            var high = pending.HighByteArithmetic;

            if (low.Operation != high.Operation ||
                (low.Operation != PartyEffectOperation.Increment && low.Operation != PartyEffectOperation.Decrement) ||
                !low.EffectiveImmediateValue.HasValue ||
                !high.EffectiveImmediateValue.HasValue ||
                !high.UsesCarryOpcode)
            {
                return false;
            }

            ushort total = (ushort)(low.EffectiveImmediateValue.Value + (high.EffectiveImmediateValue.Value << 8));
            if (total == 0)
                return false;

            operation = low.Operation;
            amount = total;
            return true;
        }

        private static bool IsHpRestoredToMaximumPattern(PendingPartyStatOperation pending, PartyFieldKind field)
        {
            return field == PartyFieldKind.Hp &&
                   pending != null &&
                   pending.SawWriteHigh &&
                   pending.SawWriteLow &&
                   pending.FinalWriteLowSourceField == PartyFieldKind.MaxHpLow &&
                   pending.FinalWriteHighSourceField == PartyFieldKind.MaxHpHigh;
        }

        public static bool TryGetExactStatWrite(PendingPartyStatOperation pending, out ushort value)
        {
            value = 0;

            if (pending == null ||
                !pending.SawWriteHigh ||
                !pending.SawWriteLow ||
                !pending.FinalWriteHighByteValue.HasValue ||
                !pending.FinalWriteLowByteValue.HasValue)
            {
                return false;
            }

            value = (ushort)(pending.FinalWriteLowByteValue.Value | (pending.FinalWriteHighByteValue.Value << 8));
            return true;
        }

        private static List<PartyEffect> RemoveRedundantStatWrittenEffects(List<PartyEffect> effects)
        {
            if (effects == null || effects.Count == 0)
                return effects ?? new List<PartyEffect>();

            var normalizedStatEffects = effects
                .Where(e => e != null &&
                            IsSupportedStatField(PartyEffectSemantics.GetEffectiveField(e)) &&
                            (PartyEffectSemantics.GetEffectiveOperation(e) != PartyEffectOperation.Write ||
                             PartyEffectSemantics.GetEffectiveValueKnowledgeForDiagnostics(e) != PartyValueKnowledge.Unknown))
                .ToList();

            if (normalizedStatEffects.Count == 0)
                return effects;

            return effects
                .Where(effect =>
                {
                    if (effect == null)
                        return false;

                    var field = PartyEffectSemantics.GetEffectiveField(effect);
                    if (!IsSupportedStatField(field) ||
                        PartyEffectSemantics.GetEffectiveOperation(effect) != PartyEffectOperation.Write)
                    {
                        return true;
                    }

                    return !normalizedStatEffects.Any(h =>
                        !ReferenceEquals(h, effect) &&
                        PartyEffectSemantics.GetEffectiveField(h) == field &&
                        MatchesSameTarget(effect, h));
                })
                .ToList();
        }

        private static List<PendingPartyStatOperation> CollectPendingStatOperations(PathAnalysisResult source)
        {
            var pendingOperations = new List<PendingPartyStatOperation>();
            if (source == null)
                return pendingOperations;

            AddPendingStatOperation(pendingOperations, source.CompletedPartyStatOperations);
            AddPendingStatOperation(pendingOperations, source.PendingPartyHpOperation, PartyFieldKind.Hp);
            AddPendingStatOperation(pendingOperations, source.PendingPartySpOperation, PartyFieldKind.Sp);
            return pendingOperations;
        }

        private static void AddPendingStatOperation(
            List<PendingPartyStatOperation> target,
            IEnumerable<PendingPartyStatOperation> pendingOperations)
        {
            if (target == null || pendingOperations == null)
                return;

            foreach (var pending in pendingOperations)
            {
                if (pending == null)
                    continue;

                var clone = pending.Clone();
                if (clone.Field == PartyFieldKind.Unknown)
                    continue;

                target.Add(clone);
            }
        }

        private static void AddPendingStatOperation(
            List<PendingPartyStatOperation> target,
            PendingPartyStatOperation pending,
            PartyFieldKind fallbackField)
        {
            if (target == null || pending == null)
                return;

            var clone = pending.Clone();
            if (clone.Field == PartyFieldKind.Unknown)
                clone.Field = fallbackField;

            if (clone.Field == PartyFieldKind.Unknown)
                return;

            target.Add(clone);
        }

        private static List<PartyEffect> AggregateEquivalentEffects(IEnumerable<PartyEffect> effects)
        {
            return (effects ?? Enumerable.Empty<PartyEffect>())
                .Where(effect => effect != null)
                .GroupBy(PartyEffectSemantics.BuildAggregationKey)
                .Select(group =>
                {
                    var aggregated = group
                        .OrderBy(effect => effect.ExecutionOrder > 0 ? 0 : 1)
                        .ThenBy(effect => effect.ExecutionOrder > 0 ? effect.ExecutionOrder : int.MaxValue)
                        .ThenBy(effect => effect.InstructionAddress == 0 ? uint.MaxValue : effect.InstructionAddress)
                        .First()
                        .Clone();

                    aggregated.ApplicationCount = group.Sum(effect => effect?.ApplicationCount > 0 ? effect.ApplicationCount : 1);
                    aggregated.IsSynchronizedTemporaryMirror = group.All(effect => effect?.IsSynchronizedTemporaryMirror == true);
                    aggregated.Description = PartyEffectSemantics.BuildHumanDescription(aggregated);
                    return aggregated;
                })
                .ToList();
        }

        private static bool IsSupportedStatField(PartyFieldKind field)
        {
            return field == PartyFieldKind.Hp || field == PartyFieldKind.Sp;
        }

        private static void PromotePartyScanLoopEffects(PathAnalysisResult source, List<PartyEffect> effects)
        {
            if (source == null ||
                source.LoopSemantic != LoopSemanticKind.PartyMemberScan ||
                source.LoopStartAddress == 0 ||
                effects == null ||
                effects.Count == 0)
            {
                return;
            }

            foreach (var effect in effects.Where(effect => ShouldPromotePartyScanLoopEffect(
                effect,
                source.LoopStartAddress,
                source.LoopEndAddress)))
            {
                effect.IsLoopDerived = true;
                NormalizeLoopDerivedEffect(effect);
            }
        }

        private static bool ShouldPromotePartyScanLoopEffect(PartyEffect effect, uint loopStartAddress, uint loopEndAddress)
        {
            if (effect == null || effect.IsLoopDerived)
                return false;

            if (!effect.MemberIndex.HasValue || effect.InstructionAddress == 0)
                return false;

            if (effect.Scope == PartyEffectScope.RandomMember || effect.Scope == PartyEffectScope.SelectedMember)
                return false;

            if (effect.InstructionAddress < loopStartAddress)
                return false;

            return loopEndAddress == 0 || effect.InstructionAddress <= loopEndAddress;
        }

        private static List<PartyEffect> RemoveLoopGuardEffects(List<PartyEffect> effects)
        {
            if (effects == null || effects.Count == 0)
                return effects ?? new List<PartyEffect>();

            return effects
                .Where(effect => effect != null &&
                                 !(PartyEffectSemantics.IsGuardLike(effect) &&
                                   PartyEffectSemantics.IsLoopDerived(effect)))
                .ToList();
        }

        private static bool MatchesSameTarget(PartyEffect candidate, PartyEffect normalized)
        {
            if (candidate == null || normalized == null)
                return false;

            if (candidate.MemberIndex != normalized.MemberIndex)
                return false;

            if (PartyEffectSemantics.GetEffectiveCondition(candidate) !=
                PartyEffectSemantics.GetEffectiveCondition(normalized))
                return false;

            if (!PartyEffectSemantics.HaveEquivalentGuardPredicates(candidate, normalized))
                return false;

            if (PartyEffectSemantics.IsLoopDerived(candidate) !=
                PartyEffectSemantics.IsLoopDerived(normalized))
                return false;

            return true;
        }

        private static void NormalizeLoopDerivedEffect(PartyEffect effect)
        {
            if (effect == null || !PartyEffectSemantics.IsLoopDerived(effect))
                return;

            effect.GuardPredicates =
                PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(effect.GuardPredicates);

            PartyEffectScope scope = PartyEffectSemantics.GetEffectiveScope(effect);
            if (scope == PartyEffectScope.RandomMember || scope == PartyEffectScope.SelectedMember)
                return;

            PartyEffectOperation operation = PartyEffectSemantics.GetEffectiveOperation(effect);
            PartyConditionKind condition = PartyEffectSemantics.GetEffectiveCondition(effect);

            if (PartyEffectSemantics.IsStateChanging(effect))
            {
                effect.Scope = condition != PartyConditionKind.None || PartyEffectSemantics.HasEffectiveGuardPredicates(effect)
                    ? PartyEffectScope.PartySubset
                    : PartyEffectScope.WholeParty;
            }
            else
            {
                effect.Scope = operation == PartyEffectOperation.Compare
                    ? PartyEffectScope.CurrentLoopMember
                    : PartyEffectScope.WholeParty;
            }

            if (effect.Scope != PartyEffectScope.SingleMember)
                effect.MemberIndex = null;

            string humanDescription = PartyEffectSemantics.BuildHumanDescription(effect);
            if (!string.IsNullOrWhiteSpace(humanDescription))
                effect.Description = humanDescription;
        }
    }
}
