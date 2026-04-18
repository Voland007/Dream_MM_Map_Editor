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

            PartyConditionKind inferredCondition = InferPartyCondition(source.PendingPartyHpOperation, allPartyEffects);
            var normalizedHpEffect = CreateNormalizedHpEffect(source.PendingPartyHpOperation, source.LoopSemantic, inferredCondition);
            if (normalizedHpEffect != null)
                result.Add(normalizedHpEffect);

            // Если есть loop-derived эффект с условием, он относится к части партии, а не ко всей партии.
            foreach (var effect in result)
            {
                if (effect == null)
                    continue;

                if (PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Status &&
                    PartyEffectSemantics.IsStateChanging(effect) &&
                    PartyEffectSemantics.IsLoopDerived(effect) &&
                    PartyEffectSemantics.GetEffectiveCondition(effect) == PartyConditionKind.None &&
                    inferredCondition != PartyConditionKind.None)
                {
                    effect.Condition = inferredCondition;
                }

                NormalizeLoopDerivedEffect(effect);
            }

            result = RemoveRedundantHpWrittenEffects(result);
            result = RemoveLoopGuardEffects(result);

            return result
                .Where(e => e != null)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(g => g.First())
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();
        }

        public static PartyEffect CreateNormalizedHpEffect(PendingPartyHpOperation pending,
            LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            if (IsCompletedHpHalvingPattern(pending))
                return PartyEffectFactory.CreateHpHalvedEffect(pending, loopSemantic, condition);

            if (TryGetExactHpAdjustment(pending, out var operation, out ushort amount))
                return PartyEffectFactory.CreateHpAdjustedEffect(pending, loopSemantic, condition, operation, amount);

            return null;
        }

        public static PartyConditionKind InferPartyCondition(PendingPartyHpOperation pending, IEnumerable<PartyEffect> effects)
        {
            foreach (var effect in effects ?? Enumerable.Empty<PartyEffect>())
            {
                if (effect == null || !PartyEffectSemantics.IsGuardLike(effect))
                    continue;

                if (PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Gender)
                    continue;

                var condition = PartyEffectSemantics.GetEffectiveCondition(effect);
                if (condition != PartyConditionKind.None)
                    return condition;
            }

            if (pending?.MaleOnly == true)
                return PartyConditionKind.MaleOnly;

            if (pending?.FemaleOnly == true)
                return PartyConditionKind.FemaleOnly;

            return PartyConditionKind.None;
        }

        public static bool IsCompletedHpHalvingPattern(PendingPartyHpOperation pending)
        {
            if (pending == null)
                return false;

            return pending.SawReadHigh &&
                   pending.SawReadLow &&
                   pending.SawWriteHigh &&
                   pending.SawWriteLow &&
                   pending.SawClc &&
                   pending.SawShrHigh &&
                   pending.SawRcrLow;
        }

        public static bool TryGetExactHpAdjustment(PendingPartyHpOperation pending,
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

        private static List<PartyEffect> RemoveRedundantHpWrittenEffects(List<PartyEffect> effects)
        {
            if (effects == null || effects.Count == 0)
                return effects ?? new List<PartyEffect>();

            var normalizedHpEffects = effects
                .Where(e => e != null &&
                            PartyEffectSemantics.GetEffectiveField(e) == PartyFieldKind.Hp &&
                            PartyEffectSemantics.GetEffectiveOperation(e) != PartyEffectOperation.Write)
                .ToList();

            if (normalizedHpEffects.Count == 0)
                return effects;

            return effects
                .Where(effect =>
                {
                    if (effect == null)
                        return false;

                    if (PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Hp ||
                        PartyEffectSemantics.GetEffectiveOperation(effect) != PartyEffectOperation.Write)
                    {
                        return true;
                    }

                    return !normalizedHpEffects.Any(h => MatchesSameTarget(effect, h));
                })
                .ToList();
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

            if (PartyEffectSemantics.IsLoopDerived(candidate) !=
                PartyEffectSemantics.IsLoopDerived(normalized))
                return false;

            return true;
        }

        private static void NormalizeLoopDerivedEffect(PartyEffect effect)
        {
            if (effect == null || !PartyEffectSemantics.IsLoopDerived(effect))
                return;

            PartyEffectScope scope = PartyEffectSemantics.GetEffectiveScope(effect);
            if (scope == PartyEffectScope.RandomMember || scope == PartyEffectScope.SelectedMember)
                return;

            PartyEffectOperation operation = PartyEffectSemantics.GetEffectiveOperation(effect);
            PartyConditionKind condition = PartyEffectSemantics.GetEffectiveCondition(effect);

            if (PartyEffectSemantics.IsStateChanging(effect))
            {
                effect.Scope = condition != PartyConditionKind.None
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
