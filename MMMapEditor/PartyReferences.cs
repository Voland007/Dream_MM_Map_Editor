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
    public enum PartyFieldKind
    {
        Unknown = 0,
        Gender = 1,
        Hp = 2,
        HpLow = 3,
        HpHigh = 4
    }

    public sealed class PartyMemberReference
    {
        public int? MemberIndex { get; set; }
        public ushort? PointerAddress { get; set; }
        public ushort? PointerTableAddress { get; set; }
        public ushort? StructureAddress { get; set; }
        public string Source { get; set; }
        public bool IsPartyLoopMember { get; set; }

        public PartyMemberReference Clone()
        {
            return new PartyMemberReference
            {
                MemberIndex = MemberIndex,
                PointerAddress = PointerAddress,
                PointerTableAddress = PointerTableAddress,
                StructureAddress = StructureAddress,
                Source = Source,
                IsPartyLoopMember = IsPartyLoopMember
            };
        }

        public override string ToString()
        {
            string memberText = MemberIndex.HasValue ? $"#{MemberIndex.Value}" : "?";
            string ptrText = PointerAddress.HasValue ? $"0x{PointerAddress.Value:X4}" : "?";
            string tableText = PointerTableAddress.HasValue ? $"0x{PointerTableAddress.Value:X4}" : "?";
            string structText = StructureAddress.HasValue ? $"0x{StructureAddress.Value:X4}" : "?";
            return $"PartyMember(Member={memberText}, Ptr={ptrText}, PtrTable={tableText}, Struct={structText}, Loop={IsPartyLoopMember}, Source={Source ?? "unknown"})";
        }
    }

    public sealed class PartyFieldReference
    {
        public PartyMemberReference Member { get; set; }
        public PartyFieldKind Field { get; set; } = PartyFieldKind.Unknown;
        public int Offset { get; set; }
        public ushort? EffectiveAddress { get; set; }
        public bool IsRead { get; set; }
        public bool IsWrite { get; set; }
        public bool IsCompare { get; set; }

        public string FieldName { get; set; }
        public byte? FieldOffset { get; set; }
        public string Source { get; set; }
        public PartyFieldKind Kind { get; set; } = PartyFieldKind.Unknown;

        public PartyFieldReference Clone()
        {
            return new PartyFieldReference
            {
                Member = Member?.Clone(),
                Field = Field,
                Offset = Offset,
                EffectiveAddress = EffectiveAddress,
                IsRead = IsRead,
                IsWrite = IsWrite,
                IsCompare = IsCompare,
                FieldName = FieldName,
                FieldOffset = FieldOffset,
                Source = Source,
                Kind = Kind
            };
        }

        public override string ToString()
        {
            string offsetText = $"0x{Offset:X2}";
            string effectiveText = EffectiveAddress.HasValue ? $"0x{EffectiveAddress.Value:X4}" : "?";
            string nameText = !string.IsNullOrWhiteSpace(FieldName) ? FieldName : Field.ToString();
            string access = IsCompare ? "Compare" : IsRead && IsWrite ? "ReadWrite" : IsRead ? "Read" : IsWrite ? "Write" : "UnknownAccess";
            return $"PartyField(Member={Member}, Field={nameText}, Kind={Kind}, Offset={offsetText}, Effective={effectiveText}, Access={access}, Source={Source ?? "unknown"})";
        }
    }

    public sealed class PendingPartyHpOperation
    {
        public PartyMemberReference Member { get; set; }
        public bool MaleOnly { get; set; }
        public bool FemaleOnly { get; set; }
        public bool SawReadHigh { get; set; }
        public bool SawReadLow { get; set; }
        public bool SawWriteHigh { get; set; }
        public bool SawWriteLow { get; set; }
        public bool SawClc { get; set; }
        public bool SawShrHigh { get; set; }
        public bool SawRcrLow { get; set; }
        public uint StartAddress { get; set; }

        public PendingPartyHpOperation Clone()
        {
            return new PendingPartyHpOperation
            {
                Member = Member?.Clone(),
                MaleOnly = MaleOnly,
                FemaleOnly = FemaleOnly,
                SawReadHigh = SawReadHigh,
                SawReadLow = SawReadLow,
                SawWriteHigh = SawWriteHigh,
                SawWriteLow = SawWriteLow,
                SawClc = SawClc,
                SawShrHigh = SawShrHigh,
                SawRcrLow = SawRcrLow,
                StartAddress = StartAddress
            };
        }

        public override string ToString()
        {
            return $"PendingPartyHp(Member={Member}, MaleOnly={MaleOnly}, FemaleOnly={FemaleOnly}, RH={SawReadHigh}, RL={SawReadLow}, WH={SawWriteHigh}, WL={SawWriteLow}, CLC={SawClc}, SHR={SawShrHigh}, RCR={SawRcrLow}, Start=0x{StartAddress:X4})";
        }
    }
}
