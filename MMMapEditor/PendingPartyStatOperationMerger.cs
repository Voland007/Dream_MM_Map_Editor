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
    internal static class PendingPartyStatOperationMerger
    {
        public static List<PendingPartyStatOperation> MergeCompletedContinuations(
            IEnumerable<PendingPartyStatOperation> completedOperations,
            ref PendingPartyStatOperation pendingHp,
            ref PendingPartyStatOperation pendingSp,
            Func<PendingPartyStatOperation, PendingPartyStatOperation, PendingPartyStatOperation> mergePending,
            Func<PartyMemberReference, PartyMemberReference, bool> matchesTarget)
        {
            var result = new List<PendingPartyStatOperation>();

            foreach (var completed in completedOperations ?? Enumerable.Empty<PendingPartyStatOperation>())
            {
                if (completed == null)
                    continue;

                var mergedCompleted = completed.Clone();
                switch (NormalizeField(mergedCompleted.Field))
                {
                    case PartyFieldKind.Hp:
                        mergedCompleted = TryConsumePending(
                            ref pendingHp,
                            mergedCompleted,
                            mergePending,
                            matchesTarget);
                        break;
                    case PartyFieldKind.Sp:
                        mergedCompleted = TryConsumePending(
                            ref pendingSp,
                            mergedCompleted,
                            mergePending,
                            matchesTarget);
                        break;
                }

                result.Add(mergedCompleted);
            }

            return result;
        }

        private static PendingPartyStatOperation TryConsumePending(
            ref PendingPartyStatOperation pending,
            PendingPartyStatOperation completed,
            Func<PendingPartyStatOperation, PendingPartyStatOperation, PendingPartyStatOperation> mergePending,
            Func<PartyMemberReference, PartyMemberReference, bool> matchesTarget)
        {
            if (!CanCompletedOperationConsumePending(pending, completed, matchesTarget))
                return completed;

            var merged = mergePending?.Invoke(pending, completed);
            pending = null;
            return merged ?? completed;
        }

        private static bool CanCompletedOperationConsumePending(
            PendingPartyStatOperation pending,
            PendingPartyStatOperation completed,
            Func<PartyMemberReference, PartyMemberReference, bool> matchesTarget)
        {
            if (pending == null || completed == null)
                return false;

            PartyFieldKind pendingField = NormalizeField(pending.Field);
            PartyFieldKind completedField = NormalizeField(completed.Field);
            if (completedField == PartyFieldKind.Unknown)
                return false;

            if (pendingField != PartyFieldKind.Unknown && pendingField != completedField)
                return false;

            if (!MatchesReturnBoundary(pending, completed))
                return false;

            return matchesTarget?.Invoke(pending.Member, completed.Member) ?? false;
        }

        private static bool MatchesReturnBoundary(
            PendingPartyStatOperation pending,
            PendingPartyStatOperation completed)
        {
            bool pendingHasBoundary = HasReturnBoundary(pending);
            bool completedHasBoundary = HasReturnBoundary(completed);

            if (!pendingHasBoundary && !completedHasBoundary)
                return true;

            if (pendingHasBoundary != completedHasBoundary)
                return false;

            return pending.AwaitingReturnAddress == completed.AwaitingReturnAddress &&
                   pending.AwaitingCallDepth == completed.AwaitingCallDepth;
        }

        private static bool HasReturnBoundary(PendingPartyStatOperation operation)
        {
            return operation != null &&
                   (operation.AwaitingReturnAddress.HasValue || operation.AwaitingCallDepth > 0);
        }

        private static PartyFieldKind NormalizeField(PartyFieldKind field)
        {
            switch (field)
            {
                case PartyFieldKind.Hp:
                case PartyFieldKind.HpLow:
                case PartyFieldKind.HpHigh:
                    return PartyFieldKind.Hp;
                case PartyFieldKind.Sp:
                case PartyFieldKind.SpLow:
                case PartyFieldKind.SpHigh:
                    return PartyFieldKind.Sp;
                default:
                    return field;
            }
        }
    }
}
