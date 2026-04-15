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

﻿using System;

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
                MemberIndex = pending.Member?.MemberIndex,
                IsLoopDerived = loopSemantic == LoopSemanticKind.PartyMemberScan || pending.Member?.IsPartyLoopMember == true,
                ValueKnowledge = PartyValueKnowledge.Structural,
                InstructionAddress = pending.StartAddress,
                Description = BuildHpHalvedDescription(condition, loopSemantic, pending.Member)
            };
        }

        private static PartyEffectScope ResolveScope(PartyMemberReference member, LoopSemanticKind loopSemantic, PartyConditionKind condition)
        {
            if (loopSemantic == LoopSemanticKind.PartyMemberScan || member?.IsPartyLoopMember == true)
            {
                if (condition != PartyConditionKind.None)
                    return PartyEffectScope.PartySubset;

                return PartyEffectScope.CurrentLoopMember;
            }

            if (member?.MemberIndex.HasValue == true)
                return PartyEffectScope.SingleMember;

            return PartyEffectScope.Unknown;
        }

        private static string BuildHpHalvedDescription(PartyConditionKind condition, LoopSemanticKind loopSemantic, PartyMemberReference member)
        {
            if (condition == PartyConditionKind.MaleOnly)
                return "HP мужчин в партии уменьшается вдвое";

            if (condition == PartyConditionKind.FemaleOnly)
                return "HP женщин в партии уменьшается вдвое";

            if (loopSemantic == LoopSemanticKind.PartyMemberScan || member?.IsPartyLoopMember == true)
                return "HP членов партии уменьшается вдвое";

            if (member?.MemberIndex.HasValue == true)
                return $"HP персонажа #{member.MemberIndex.Value} уменьшается вдвое";

            return "HP персонажа уменьшается вдвое";
        }
    }
}
