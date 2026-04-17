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

            result.AddRange(allPartyEffects);

            PartyConditionKind inferredCondition = InferPartyCondition(source.PendingPartyHpOperation, allPartyEffects);

            if (IsCompletedHpHalvingPattern(source.PendingPartyHpOperation))
            {
                var hpHalved = PartyEffectFactory.CreateHpHalvedEffect(
                    source.PendingPartyHpOperation,
                    source.LoopSemantic,
                    inferredCondition);

                if (hpHalved != null)
                    result.Add(hpHalved);
            }

            // Если есть loop-derived эффект с условием, он относится к части партии, а не ко всей партии.
            foreach (var effect in result)
            {
                if (effect == null)
                    continue;

                if (PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Hp &&
                    PartyEffectSemantics.IsLoopDerived(effect) &&
                    PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None)
                {
                    effect.Scope = PartyEffectScope.PartySubset;
                }

                if (PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Gender &&
                    PartyEffectSemantics.GetEffectiveOperation(effect) == PartyEffectOperation.Compare &&
                    PartyEffectSemantics.IsLoopDerived(effect) &&
                    PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None)
                {
                    effect.Scope = PartyEffectScope.PartySubset;
                }
            }

            result = RemoveRedundantHpWrittenEffects(result);

            return result
                .Where(e => e != null)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(g => g.First())
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();
        }

        private static PartyConditionKind InferPartyCondition(PendingPartyHpOperation pending, IEnumerable<PartyEffect> effects)
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

        private static bool IsCompletedHpHalvingPattern(PendingPartyHpOperation pending)
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

        private static List<PartyEffect> RemoveRedundantHpWrittenEffects(List<PartyEffect> effects)
        {
            if (effects == null || effects.Count == 0)
                return effects ?? new List<PartyEffect>();

            var hpHalved = effects
                .Where(e => e != null &&
                            PartyEffectSemantics.GetEffectiveField(e) == PartyFieldKind.Hp &&
                            PartyEffectSemantics.GetEffectiveOperation(e) == PartyEffectOperation.Halve)
                .ToList();

            if (hpHalved.Count == 0)
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

                    return !hpHalved.Any(h => MatchesSameTarget(effect, h));
                })
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
    }
}
