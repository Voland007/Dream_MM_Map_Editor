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
    public enum PartyFieldKind
    {
        Unknown = 0,
        sex = 1,
        Hp = 2,
        HpLow = 3,
        HpHigh = 4,
        Status = 5,
        Technical77 = 6,
        Sp = 7,
        SpLow = 8,
        SpHigh = 9,
        InnateAlignment = 10,
        CurrentAlignment = 11,
        Technical75 = 12,
        Technical76 = 13,
        Technical71 = 14
    }

    public enum PartyMemberSelectionKind
    {
        Exact = 0,
        Dynamic = 1,
        Random = 2
    }

    public sealed class PartyMemberReference
    {
        public int? MemberIndex { get; set; }
        public ushort? PointerAddress { get; set; }
        public ushort? PointerTableAddress { get; set; }
        public ushort? StructureAddress { get; set; }
        public string Source { get; set; }
        public bool IsPartyLoopMember { get; set; }
        public PartyMemberSelectionKind SelectionKind { get; set; } = PartyMemberSelectionKind.Exact;

        public PartyMemberReference Clone()
        {
            return new PartyMemberReference
            {
                MemberIndex = MemberIndex,
                PointerAddress = PointerAddress,
                PointerTableAddress = PointerTableAddress,
                StructureAddress = StructureAddress,
                Source = Source,
                IsPartyLoopMember = IsPartyLoopMember,
                SelectionKind = SelectionKind
            };
        }

        public static string FormatDisplayIndex(int memberIndex)
        {
            return $"#{memberIndex + 1}";
        }

        public override string ToString()
        {
            string memberText = MemberIndex.HasValue ? FormatDisplayIndex(MemberIndex.Value) : "?";
            string ptrText = PointerAddress.HasValue ? $"0x{PointerAddress.Value:X4}" : "?";
            string tableText = PointerTableAddress.HasValue ? $"0x{PointerTableAddress.Value:X4}" : "?";
            string structText = StructureAddress.HasValue ? $"0x{StructureAddress.Value:X4}" : "?";
            return $"PartyMember(Member={memberText}, Ptr={ptrText}, PtrTable={tableText}, Struct={structText}, Loop={IsPartyLoopMember}, Selection={SelectionKind}, Source={Source ?? "unknown"})";
        }
    }

    public sealed class PartyPointerByteReference
    {
        public PartyMemberReference Member { get; set; }
        public bool IsHighByte { get; set; }
        public ushort? SourceAddress { get; set; }
        public string Source { get; set; }

        public PartyPointerByteReference Clone()
        {
            return new PartyPointerByteReference
            {
                Member = Member?.Clone(),
                IsHighByte = IsHighByte,
                SourceAddress = SourceAddress,
                Source = Source
            };
        }

        public override string ToString()
        {
            string byteText = IsHighByte ? "High" : "Low";
            string sourceAddrText = SourceAddress.HasValue ? $"0x{SourceAddress.Value:X4}" : "?";
            return $"PartyPointerByte(Member={Member}, Byte={byteText}, SourceAddr={sourceAddrText}, Source={Source ?? "unknown"})";
        }
    }

    public sealed class PartyFieldBitTransform
    {
        private enum BitState
        {
            None = 0,
            Set = 1,
            Clear = 2,
            Toggle = 3
        }

        public byte SetMask { get; set; }
        public byte ClearMask { get; set; }
        public byte ToggleMask { get; set; }

        public bool IsIdentity => SetMask == 0 && ClearMask == 0 && ToggleMask == 0;

        public PartyFieldBitTransform Clone()
        {
            return new PartyFieldBitTransform
            {
                SetMask = SetMask,
                ClearMask = ClearMask,
                ToggleMask = ToggleMask
            };
        }

        public void ApplyOperation(PartyEffectOperation operation, byte immediateValue)
        {
            byte normalizedMask = operation switch
            {
                PartyEffectOperation.BitSet => immediateValue,
                PartyEffectOperation.BitClear => unchecked((byte)~immediateValue),
                PartyEffectOperation.BitToggle => immediateValue,
                _ => 0
            };

            if (normalizedMask == 0)
                return;

            for (int bit = 0; bit < 8; bit++)
            {
                byte bitMask = (byte)(1 << bit);
                if ((normalizedMask & bitMask) == 0)
                    continue;

                SetBitState(bitMask, Compose(GetBitState(bitMask), operation));
            }
        }

        public override string ToString()
        {
            return $"BitTransform(Set=0x{SetMask:X2}, Clear=0x{ClearMask:X2}, Toggle=0x{ToggleMask:X2})";
        }

        private BitState GetBitState(byte bitMask)
        {
            if ((SetMask & bitMask) != 0)
                return BitState.Set;

            if ((ClearMask & bitMask) != 0)
                return BitState.Clear;

            if ((ToggleMask & bitMask) != 0)
                return BitState.Toggle;

            return BitState.None;
        }

        private void SetBitState(byte bitMask, BitState state)
        {
            SetMask = (byte)(SetMask & ~bitMask);
            ClearMask = (byte)(ClearMask & ~bitMask);
            ToggleMask = (byte)(ToggleMask & ~bitMask);

            switch (state)
            {
                case BitState.Set:
                    SetMask = (byte)(SetMask | bitMask);
                    break;
                case BitState.Clear:
                    ClearMask = (byte)(ClearMask | bitMask);
                    break;
                case BitState.Toggle:
                    ToggleMask = (byte)(ToggleMask | bitMask);
                    break;
            }
        }

        private static BitState Compose(BitState current, PartyEffectOperation operation)
        {
            return operation switch
            {
                PartyEffectOperation.BitSet => BitState.Set,
                PartyEffectOperation.BitClear => BitState.Clear,
                PartyEffectOperation.BitToggle => current switch
                {
                    BitState.None => BitState.Toggle,
                    BitState.Set => BitState.Clear,
                    BitState.Clear => BitState.Set,
                    BitState.Toggle => BitState.None,
                    _ => BitState.None
                },
                _ => current
            };
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
        public PartyFieldBitTransform BitTransform { get; set; }
        public bool HasBitTransform => BitTransform != null && !BitTransform.IsIdentity;

        public void ApplyBitOperation(PartyEffectOperation operation, byte immediateValue)
        {
            if (operation != PartyEffectOperation.BitSet &&
                operation != PartyEffectOperation.BitClear &&
                operation != PartyEffectOperation.BitToggle)
            {
                return;
            }

            BitTransform ??= new PartyFieldBitTransform();
            BitTransform.ApplyOperation(operation, immediateValue);

            if (BitTransform.IsIdentity)
                BitTransform = null;
        }

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
                Kind = Kind,
                BitTransform = BitTransform?.Clone()
            };
        }

        public override string ToString()
        {
            string offsetText = $"0x{Offset:X2}";
            string effectiveText = EffectiveAddress.HasValue ? $"0x{EffectiveAddress.Value:X4}" : "?";
            string nameText = !string.IsNullOrWhiteSpace(FieldName) ? FieldName : Field.ToString();
            string access = IsCompare ? "Compare" : IsRead && IsWrite ? "ReadWrite" : IsRead ? "Read" : IsWrite ? "Write" : "UnknownAccess";
            string transformText = HasBitTransform ? $", Transform={BitTransform}" : string.Empty;
            return $"PartyField(Member={Member}, Field={nameText}, Kind={Kind}, Offset={offsetText}, Effective={effectiveText}, Access={access}, Source={Source ?? "unknown"}{transformText})";
        }
    }

    public sealed class PendingPartyStatOperation
    {
        public PartyMemberReference Member { get; set; }
        public bool MaleOnly { get; set; }
        public bool FemaleOnly { get; set; }
        public List<PartyPredicate> GuardPredicates { get; set; } = new List<PartyPredicate>();
        public bool SawReadHigh { get; set; }
        public bool SawReadLow { get; set; }
        public bool SawWriteHigh { get; set; }
        public bool SawWriteLow { get; set; }
        public byte? FinalWriteHighByteValue { get; set; }
        public byte? FinalWriteLowByteValue { get; set; }
        public bool SawClc { get; set; }
        public bool SawShrHigh { get; set; }
        public bool SawRcrLow { get; set; }
        public PendingPartyByteArithmetic LowByteArithmetic { get; set; }
        public PendingPartyByteArithmetic HighByteArithmetic { get; set; }
        public uint StartAddress { get; set; }
        public int ExecutionOrder { get; set; }

        public PendingPartyStatOperation Clone()
        {
            return new PendingPartyStatOperation
            {
                Member = Member?.Clone(),
                MaleOnly = MaleOnly,
                FemaleOnly = FemaleOnly,
                GuardPredicates = GuardPredicates?
                    .Select(predicate => predicate?.Clone())
                    .Where(predicate => predicate != null)
                    .ToList() ?? new List<PartyPredicate>(),
                SawReadHigh = SawReadHigh,
                SawReadLow = SawReadLow,
                SawWriteHigh = SawWriteHigh,
                SawWriteLow = SawWriteLow,
                FinalWriteHighByteValue = FinalWriteHighByteValue,
                FinalWriteLowByteValue = FinalWriteLowByteValue,
                SawClc = SawClc,
                SawShrHigh = SawShrHigh,
                SawRcrLow = SawRcrLow,
                LowByteArithmetic = LowByteArithmetic?.Clone(),
                HighByteArithmetic = HighByteArithmetic?.Clone(),
                StartAddress = StartAddress,
                ExecutionOrder = ExecutionOrder
            };
        }

        public override string ToString()
        {
            string finalLow = FinalWriteLowByteValue.HasValue ? $"0x{FinalWriteLowByteValue.Value:X2}" : "?";
            string finalHigh = FinalWriteHighByteValue.HasValue ? $"0x{FinalWriteHighByteValue.Value:X2}" : "?";
            return $"PendingPartyStat(Member={Member}, MaleOnly={MaleOnly}, FemaleOnly={FemaleOnly}, RH={SawReadHigh}, RL={SawReadLow}, WH={SawWriteHigh}, WL={SawWriteLow}, FinalLow={finalLow}, FinalHigh={finalHigh}, CLC={SawClc}, SHR={SawShrHigh}, RCR={SawRcrLow}, LowArith={LowByteArithmetic}, HighArith={HighByteArithmetic}, Start=0x{StartAddress:X4}, Order={ExecutionOrder})";
        }
    }

    public sealed class PendingPartyByteArithmetic
    {
        public PartyEffectOperation Operation { get; set; } = PartyEffectOperation.Unknown;
        public byte RawImmediateValue { get; set; }
        public ushort? EffectiveImmediateValue { get; set; }
        public bool UsesCarryOpcode { get; set; }
        public bool CarryInKnown { get; set; }
        public bool CarryInValue { get; set; }
        public uint InstructionAddress { get; set; }

        public PendingPartyByteArithmetic Clone()
        {
            return new PendingPartyByteArithmetic
            {
                Operation = Operation,
                RawImmediateValue = RawImmediateValue,
                EffectiveImmediateValue = EffectiveImmediateValue,
                UsesCarryOpcode = UsesCarryOpcode,
                CarryInKnown = CarryInKnown,
                CarryInValue = CarryInValue,
                InstructionAddress = InstructionAddress
            };
        }

        public override string ToString()
        {
            return $"PendingByteArith(Op={Operation}, Raw=0x{RawImmediateValue:X2}, Effective={(EffectiveImmediateValue.HasValue ? $"0x{EffectiveImmediateValue.Value:X4}" : "?")}, CarryOpcode={UsesCarryOpcode}, CarryKnown={CarryInKnown}, CarryIn={CarryInValue}, At=0x{InstructionAddress:X4})";
        }
    }
}
