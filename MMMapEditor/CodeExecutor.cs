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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    /// <summary>
    /// Выполняет эмуляцию кода по заданному адресу
    /// </summary>
    public class CodeExecutor
    {
        private readonly OvrFileConfig _config;
        private readonly InstructionAnalyzer _instructionAnalyzer;
        private readonly Dictionary<ushort, byte> _emulatedMemory8 = new Dictionary<ushort, byte>();
        private readonly Dictionary<ushort, PartyMemberReference> _emulatedPartyPointers = new Dictionary<ushort, PartyMemberReference>();
        private readonly Dictionary<ushort, PartyPointerByteReference> _emulatedPartyPointerBytes = new Dictionary<ushort, PartyPointerByteReference>();

        private const int MAX_DEPTH = 12;
        private const ushort PARTY_POINTER_TABLE = 0x3CA8;
        private const ushort PARTY_COUNT_ADDRESS = 0x3BC0;
        private const ushort BATTLE_MONSTER_COUNT_ADDRESS = 0x3C1D;
        private const int PARTY_MEMBER_COUNT = 6;
        private const int PARTY_GENDER_OFFSET = 0x10;
        private const int PARTY_HP_LOW_OFFSET = 0x33;
        private const int PARTY_HP_HIGH_OFFSET = 0x34;
        private const int PARTY_STATUS_OFFSET = PartyStatusSemantics.FieldOffset;
        private const int PARTY_TECHNICAL_77_OFFSET = PartyTechnicalField77Semantics.FieldOffset;
        private const int MAX_CALL_DEPTH = 10;
        private const int MAX_INSTRUCTIONS_PER_PATH = 3000;

        public CodeExecutor(OvrFileConfig config, InstructionAnalyzer instructionAnalyzer)
        {
            _config = config;
            _instructionAnalyzer = instructionAnalyzer;
        }

        public PathAnalysisResult ExecuteCodeAtAddress(BinaryReader br, uint startAddress,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges = null,
            bool invalidateReturnRegistersAfterExternalCall = false,
            List<uint> pendingReturnAddresses = null,
            Dictionary<ushort, byte> initialEmulatedMemory8 = null,
            Dictionary<ushort, PartyMemberReference> initialEmulatedPartyPointers = null,
            Dictionary<ushort, PartyPointerByteReference> initialEmulatedPartyPointerBytes = null)
        {
            processedBackEdges ??= new HashSet<(uint From, uint To)>();
            bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);

            if (depth > MAX_DEPTH || callDepth > MAX_CALL_DEPTH)
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"      Достигнут лимит глубины ({depth}/{callDepth}), прекращаем анализ");
                return new PathAnalysisResult { IsTerminated = false };
            }

            var savedEmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8);
            var savedEmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone());
            var savedEmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone());
            _emulatedMemory8.Clear();
            _emulatedPartyPointers.Clear();
            _emulatedPartyPointerBytes.Clear();
            if (initialEmulatedMemory8 != null)
            {
                foreach (var kv in initialEmulatedMemory8)
                    _emulatedMemory8[kv.Key] = kv.Value;
            }
            if (initialEmulatedPartyPointers != null)
            {
                foreach (var kv in initialEmulatedPartyPointers)
                    _emulatedPartyPointers[kv.Key] = kv.Value?.Clone();
            }
            if (initialEmulatedPartyPointerBytes != null)
            {
                foreach (var kv in initialEmulatedPartyPointerBytes)
                    _emulatedPartyPointerBytes[kv.Key] = kv.Value?.Clone();
            }

            try
            {
                var result = new PathAnalysisResult();
                long fileLength = br.BaseStream.Length;
                uint currentAddress = startAddress;
                var currentPendingReturnAddresses = pendingReturnAddresses != null ? new List<uint>(pendingReturnAddresses) : new List<uint>();
                int currentCallDepth = callDepth;
                int instructionCount = 0;
                var visitedInThisPath = new HashSet<uint>();
                var finiteLoopProgressByJumpAddress = new Dictionary<uint, byte>();

                // Для отслеживания косвенных текстов
                byte? lastAlValue = null;
                ushort? lastBpValue = null;
                uint lastTextAddress = 0;
                var foundTextsInThisPath = new HashSet<string>();
                int textOrderCounter = 0;

                using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
                {
                    capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                    while (currentAddress < fileLength && instructionCount < MAX_INSTRUCTIONS_PER_PATH)
                    {
                        if (visitedInThisPath.Contains(currentAddress))
                        {
                            if (debugMode)
                                AnalysisDebug.WriteLine($"      Обнаружен цикл по адресу 0x{currentAddress:X4}");

                            if (IsTableLoadLoop(br, currentAddress, registerTracker))
                            {
                                if (debugMode)
                                    AnalysisDebug.WriteLine($"      Обнаружен цикл загрузки из таблиц");
                                result.HasPartialBattlePattern = true;
                                result.IsInLoop = true;
                                result.LoopStartAddress = currentAddress;
                            }

                            result.IsTerminated = true;
                            return result;
                        }

                        visitedInThisPath.Add(currentAddress);
                        result.VisitedAddresses.Add(currentAddress);

                        if (!TryDisassembleNext(br, currentAddress, out X86Instruction insn))
                        {
                            if (debugMode)
                                AnalysisDebug.WriteLine($"      Не удалось дизассемблировать по адресу 0x{currentAddress:X4}, пропускаем байт");
                            currentAddress++;
                            continue;
                        }

                        instructionCount++;
                        string mnemonicUpper = insn.Mnemonic?.ToUpper() ?? "";

                        if (IsPopReg16(insn))
                        {
                            if (currentPendingReturnAddresses.Count > 0)
                            {
                                uint poppedReturn = currentPendingReturnAddresses[currentPendingReturnAddresses.Count - 1];
                                currentPendingReturnAddresses.RemoveAt(currentPendingReturnAddresses.Count - 1);

                                if (debugMode)
                                    AnalysisDebug.WriteLine($"        POP {GetPopRegisterName(insn)}: сняли адрес возврата 0x{poppedReturn:X4} из эмулируемого стека");
                            }
                            else if (debugMode)
                            {
                                AnalysisDebug.WriteLine($"        POP {GetPopRegisterName(insn)}: значение в эмулируемом стеке неизвестно");
                            }
                        }

                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"      Анализ инструкции по адресу 0x{insn.Address:X4}: {insn.Mnemonic} {insn.Operand}");

                        byte[] bytes = insn.Bytes;

                        // Отслеживание MOV AL, imm8
                        if (bytes.Length >= 2 && bytes[0] == 0xB0)
                        {
                            lastAlValue = bytes[1];
                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Запомнили AL = 0x{lastAlValue:X2}");
                        }

                        // Отслеживание MOV BP, imm16
                        if (bytes.Length >= 3 && bytes[0] == 0xBD)
                        {
                            lastBpValue = BitConverter.ToUInt16(bytes, 1);
                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Запомнили BP = 0x{lastBpValue:X4}");
                        }

                        // Поиск косвенных текстов (MOV AL, imm8 + MOV BP, imm16)
                        ProcessIndirectTexts(lastAlValue, lastBpValue, br, ref lastTextAddress,
                            foundTextsInThisPath, result, insn, debugMode, ref textOrderCounter);

                        // Поиск прямых текстов
                        var newTexts = new HashSet<string>();
                        _instructionAnalyzer.FindTextsInInstruction(insn, br, registerTracker, depth, newTexts, TryGetEmulatedMemory8Value);
                        ProcessFoundTexts(newTexts, foundTextsInThisPath, result, currentCallDepth, insn, debugMode, ref textOrderCounter);

                        // Поиск изменений статистики монстров и информации о битвах
                        _instructionAnalyzer.FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        _instructionAnalyzer.FindMonsterBattleInfo(insn, br, registerTracker, depth, result, targetX, targetY, TryGetEmulatedMemory8Value);
                        if (TrackRegisterOperations(insn, br, registerTracker, depth, debugMode, result, targetX, targetY,
                            currentCallDepth, currentPendingReturnAddresses))
                        {
                            return result;
                        }

                        // Анализ частичных битв
                        if (result.PartialBattleInfo.Count > 0)
                        {
                            AnalyzePartialBattleRanges(br, result);
                        }

                        // Обработка инструкций перехода и возврата
                        var handlingResult = HandleControlFlowInstructions(insn, br, registerTracker,
                            globallyAnalyzedAddresses, depth, currentCallDepth, pathId, targetX, targetY,
                            processedBackEdges, result, currentAddress, nextAddress, fileLength, debugMode,
                            invalidateReturnRegistersAfterExternalCall, currentPendingReturnAddresses,
                            visitedInThisPath, finiteLoopProgressByJumpAddress);

                        if (handlingResult.ShouldReturn)
                            return handlingResult.Result;

                        currentAddress = handlingResult.NextAddress;
                        if (handlingResult.UpdatedPendingReturnAddresses != null)
                            currentPendingReturnAddresses = handlingResult.UpdatedPendingReturnAddresses;
                        if (handlingResult.UpdatedCallDepth >= 0)
                            currentCallDepth = handlingResult.UpdatedCallDepth;
                    }
                }

                // Финальный анализ частичных битв
                if (result.PartialBattleInfo.Count > 0)
                {
                    AnalyzePartialBattleRanges(br, result);
                }

                result.ExitPendingReturnAddresses = new List<uint>(currentPendingReturnAddresses);
                result.ExitCallDepth = currentCallDepth;
                FinalizeResult(result, instructionCount, currentAddress, fileLength, debugMode);
                return result;
            }
            finally
            {
                _emulatedMemory8.Clear();
                foreach (var kv in savedEmulatedMemory8)
                    _emulatedMemory8[kv.Key] = kv.Value;
                _emulatedPartyPointers.Clear();
                foreach (var kv in savedEmulatedPartyPointers)
                    _emulatedPartyPointers[kv.Key] = kv.Value?.Clone();
                _emulatedPartyPointerBytes.Clear();
                foreach (var kv in savedEmulatedPartyPointerBytes)
                    _emulatedPartyPointerBytes[kv.Key] = kv.Value?.Clone();
            }
        }

        private byte? TryGetEmulatedMemory8Value(ushort address)
        {
            return _emulatedMemory8.TryGetValue(address, out byte value)
                ? value
                : (byte?)null;
        }
        private bool TryGetEmulatedPartyPointer(ushort address, out PartyMemberReference member)
        {
            member = null;
            if (!_emulatedPartyPointers.TryGetValue(address, out var existing) || existing == null)
                return false;

            member = existing.Clone();
            return true;
        }

        private bool TryGetEmulatedPartyPointerByte(ushort address, out PartyPointerByteReference pointerByte)
        {
            pointerByte = null;
            if (!_emulatedPartyPointerBytes.TryGetValue(address, out var existing) || existing == null)
                return false;

            pointerByte = existing.Clone();
            return true;
        }

        private void ApplyTrackedPartyPointerWrite(ushort memAddr, PartyMemberReference member)
        {
            if (member == null)
            {
                _emulatedPartyPointers.Remove(memAddr);
                ApplyTrackedPartyPointerByteWrite(memAddr, null);
                ApplyTrackedPartyPointerByteWrite(unchecked((ushort)(memAddr + 1)), null);
                return;
            }

            var clone = member.Clone();
            _emulatedPartyPointers[memAddr] = clone;
            ApplyTrackedPartyPointerByteWrite(memAddr, new PartyPointerByteReference
            {
                Member = clone.Clone(),
                IsHighByte = false,
                SourceAddress = memAddr,
                Source = "TrackedWordWrite"
            });
            ApplyTrackedPartyPointerByteWrite(unchecked((ushort)(memAddr + 1)), new PartyPointerByteReference
            {
                Member = clone.Clone(),
                IsHighByte = true,
                SourceAddress = unchecked((ushort)(memAddr + 1)),
                Source = "TrackedWordWrite"
            });
        }

        private void ApplyTrackedPartyPointerByteWrite(ushort memAddr, PartyPointerByteReference pointerByte)
        {
            if (pointerByte == null)
            {
                _emulatedPartyPointerBytes.Remove(memAddr);
                return;
            }

            _emulatedPartyPointerBytes[memAddr] = pointerByte.Clone();
        }

        private bool TryResolvePartyPointerTableAddress(ushort effectiveAddress, out int memberIndex)
        {
            memberIndex = -1;

            if (effectiveAddress < PARTY_POINTER_TABLE || effectiveAddress >= PARTY_POINTER_TABLE + PARTY_MEMBER_COUNT * 2)
                return false;

            int delta = effectiveAddress - PARTY_POINTER_TABLE;
            if ((delta & 1) != 0)
                return false;

            memberIndex = delta / 2;
            return memberIndex >= 0 && memberIndex < PARTY_MEMBER_COUNT;
        }

        private bool TryResolvePartyPointerByteTableAddress(ushort effectiveAddress, out PartyPointerByteReference pointerByte)
        {
            pointerByte = null;

            if (effectiveAddress < PARTY_POINTER_TABLE || effectiveAddress >= PARTY_POINTER_TABLE + PARTY_MEMBER_COUNT * 2)
                return false;

            int delta = effectiveAddress - PARTY_POINTER_TABLE;
            int memberIndex = delta / 2;
            if (memberIndex < 0 || memberIndex >= PARTY_MEMBER_COUNT)
                return false;

            ushort tableBase = (ushort)(PARTY_POINTER_TABLE + memberIndex * 2);
            pointerByte = new PartyPointerByteReference
            {
                Member = new PartyMemberReference
                {
                    MemberIndex = memberIndex,
                    PointerTableAddress = tableBase,
                    SelectionKind = PartyMemberSelectionKind.Exact,
                    Source = "PartyPointerTableByte"
                },
                IsHighByte = (delta & 1) != 0,
                SourceAddress = effectiveAddress,
                Source = "PartyPointerTableByte"
            };

            return true;
        }

        private bool TryResolveTrackedPartyPointerByte(ushort memAddr, out PartyPointerByteReference pointerByte)
        {
            if (TryResolvePartyPointerByteTableAddress(memAddr, out pointerByte))
                return true;

            return TryGetEmulatedPartyPointerByte(memAddr, out pointerByte);
        }

        private bool TryResolveTrackedPartyPointer(ushort memAddr, out PartyMemberReference member)
        {
            member = null;

            if (TryResolvePartyPointerTableAddress(memAddr, out int memberIndex))
            {
                member = new PartyMemberReference
                {
                    MemberIndex = memberIndex,
                    PointerTableAddress = memAddr,
                    SelectionKind = PartyMemberSelectionKind.Exact,
                    Source = "PartyPointerTableWord"
                };
                return true;
            }

            if (TryGetEmulatedPartyPointer(memAddr, out member))
                return true;

            if (!TryResolveTrackedPartyPointerByte(memAddr, out var lowByte) ||
                !TryResolveTrackedPartyPointerByte(unchecked((ushort)(memAddr + 1)), out var highByte) ||
                lowByte == null ||
                highByte == null ||
                lowByte.IsHighByte ||
                !highByte.IsHighByte)
            {
                return false;
            }

            if (!RegisterTrackerCanCombinePointerBytes(lowByte, highByte))
                return false;

            member = lowByte.Member?.Clone();
            if (member != null && !member.PointerTableAddress.HasValue && lowByte.Member?.PointerTableAddress.HasValue == true)
                member.PointerTableAddress = lowByte.Member.PointerTableAddress;
            return member != null;
        }

        private bool TryResolveDynamicPartyPointerFromMemoryOperand(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            out PartyMemberReference member)
        {
            member = null;
            if (registerTracker == null)
                return false;

            if (!TryDecode16BitMemoryOperandSyntax(instructionBytes, out byte mod, out byte rm, out int signedDisp, out _, out _))
                return false;

            int baseContribution = signedDisp;
            ValueRange8 rangedOffset = null;
            RegisterValueDistribution rangedDistribution = RegisterValueDistribution.Unknown;

            bool AccumulateComponent(string regName)
            {
                if (registerTracker.TryGetRegisterValue(regName, out ushort exactValue))
                {
                    baseContribution += exactValue;
                    return true;
                }

                if (registerTracker.TryGetRegisterRange(regName, out var rangeValue) && rangeValue != null)
                {
                    if (rangedOffset != null)
                        return false;

                    rangedOffset = new ValueRange8(rangeValue.Min, rangeValue.Max);
                    registerTracker.TryGetRegisterDistribution(regName, out rangedDistribution);
                    return true;
                }

                return false;
            }

            switch (rm)
            {
                case 0x00:
                    if (!AccumulateComponent("BX") || !AccumulateComponent("SI"))
                        return false;
                    break;
                case 0x01:
                    if (!AccumulateComponent("BX") || !AccumulateComponent("DI"))
                        return false;
                    break;
                case 0x02:
                    if (!AccumulateComponent("BP") || !AccumulateComponent("SI"))
                        return false;
                    break;
                case 0x03:
                    if (!AccumulateComponent("BP") || !AccumulateComponent("DI"))
                        return false;
                    break;
                case 0x04:
                    if (!AccumulateComponent("SI"))
                        return false;
                    break;
                case 0x05:
                    if (!AccumulateComponent("DI"))
                        return false;
                    break;
                case 0x06:
                    if (mod == 0x00 || !AccumulateComponent("BP"))
                        return false;
                    break;
                case 0x07:
                    if (!AccumulateComponent("BX"))
                        return false;
                    break;
                default:
                    return false;
            }

            if (rangedOffset == null || baseContribution != PARTY_POINTER_TABLE)
                return false;

            if (rangedOffset.Max >= PARTY_MEMBER_COUNT * 2)
                return false;

            PartyMemberSelectionKind selectionKind =
                rangedDistribution == RegisterValueDistribution.UniformDiscreteRange
                    ? PartyMemberSelectionKind.Random
                    : PartyMemberSelectionKind.Dynamic;

            int? memberIndex = null;
            ushort? pointerTableAddress = PARTY_POINTER_TABLE;
            if (rangedOffset.IsExact && (rangedOffset.Min & 1) == 0)
            {
                int resolvedIndex = rangedOffset.Min / 2;
                if (resolvedIndex >= 0 && resolvedIndex < PARTY_MEMBER_COUNT)
                {
                    memberIndex = resolvedIndex;
                    pointerTableAddress = (ushort)(PARTY_POINTER_TABLE + rangedOffset.Min);
                    selectionKind = PartyMemberSelectionKind.Exact;
                }
            }

            member = new PartyMemberReference
            {
                MemberIndex = memberIndex,
                PointerTableAddress = pointerTableAddress,
                SelectionKind = selectionKind,
                Source = "PartyPointerTableRange"
            };

            return true;
        }

        private static bool RegisterTrackerCanCombinePointerBytes(PartyPointerByteReference lowByte, PartyPointerByteReference highByte)
        {
            var lowMember = lowByte?.Member;
            var highMember = highByte?.Member;
            if (lowMember == null || highMember == null)
                return false;

            if (lowMember.IsPartyLoopMember && highMember.IsPartyLoopMember)
                return true;

            if (lowMember.MemberIndex.HasValue && highMember.MemberIndex.HasValue)
                return lowMember.MemberIndex.Value == highMember.MemberIndex.Value;

            if (lowMember.PointerTableAddress.HasValue && highMember.PointerTableAddress.HasValue)
                return lowMember.PointerTableAddress.Value == highMember.PointerTableAddress.Value;

            if (lowMember.PointerAddress.HasValue && highMember.PointerAddress.HasValue)
                return lowMember.PointerAddress.Value == highMember.PointerAddress.Value;

            return false;
        }

        private bool TryResolvePartyFieldAccess(byte[] instructionBytes, RegisterTracker tracker, ushort? effectiveAddress, out PartyFieldReference fieldRef)
        {
            fieldRef = null;
            if (tracker == null)
                return false;

            if (!TryDecode16BitMemoryOperandSyntax(instructionBytes, out byte mod, out byte rm, out int signedDisp, out _, out _))
                return false;

            if (mod == 0x00 && rm == 0x06)
                return false;

            PartyMemberReference member = null;
            int offset = signedDisp;

            bool exact(string regName, out ushort regValue) => tracker.TryGetRegisterValue(regName, out regValue);

            switch (rm)
            {
                case 0x00: // BX+SI
                    if (tracker.TryGetPartyMemberBase("SI", out member) && exact("BX", out ushort bx0))
                        offset += bx0;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && exact("SI", out ushort si0))
                        offset += si0;
                    else
                        return false;
                    break;
                case 0x01: // BX+DI
                    if (tracker.TryGetPartyMemberBase("DI", out member) && exact("BX", out ushort bx1))
                        offset += bx1;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && exact("DI", out ushort di1))
                        offset += di1;
                    else
                        return false;
                    break;
                case 0x03: // BP+DI
                    if (tracker.TryGetPartyMemberBase("DI", out member) && exact("BP", out ushort bp))
                        offset += bp;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && exact("DI", out ushort di))
                        offset += di;
                    else
                        return false;
                    break;
                case 0x02: // BP+SI
                    if (tracker.TryGetPartyMemberBase("SI", out member) && exact("BP", out ushort bp2))
                        offset += bp2;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && exact("SI", out ushort si2))
                        offset += si2;
                    else
                        return false;
                    break;
                case 0x05: // DI
                    if (!tracker.TryGetPartyMemberBase("DI", out member))
                        return false;
                    break;
                case 0x04: // SI
                    if (!tracker.TryGetPartyMemberBase("SI", out member))
                        return false;
                    break;
                case 0x06: // BP
                    if (!tracker.TryGetPartyMemberBase("BP", out member))
                        return false;
                    break;
                case 0x07: // BX
                    if (!tracker.TryGetPartyMemberBase("BX", out member))
                        return false;
                    break;
                default:
                    return false;
            }

            PartyFieldKind field = offset switch
            {
                PARTY_GENDER_OFFSET => PartyFieldKind.Gender,
                PARTY_HP_LOW_OFFSET => PartyFieldKind.HpLow,
                PARTY_HP_HIGH_OFFSET => PartyFieldKind.HpHigh,
                PARTY_STATUS_OFFSET => PartyFieldKind.Status,
                PARTY_TECHNICAL_77_OFFSET => PartyFieldKind.Technical77,
                _ => PartyFieldKind.Unknown
            };

            if (field == PartyFieldKind.Unknown)
                return false;

            fieldRef = new PartyFieldReference
            {
                Member = member?.Clone(),
                Field = field,
                Offset = offset,
                EffectiveAddress = effectiveAddress,
                FieldOffset = (byte)offset,
                FieldName = field == PartyFieldKind.Technical77
                    ? PartyTechnicalField77Semantics.FieldLabel
                    : null
            };

            return true;
        }

        private PartyConditionKind GetCurrentPartyCondition(RegisterTracker registerTracker, uint instructionAddress,
            PartyMemberReference member = null, LoopSemanticKind loopSemantic = LoopSemanticKind.None)
        {
            if (registerTracker?.ActivePartyConditionWindows != null)
            {
                foreach (var window in registerTracker.ActivePartyConditionWindows
                             .Where(window => window != null && window.IsActiveAt(instructionAddress))
                             .OrderByDescending(window => window.StartAddress))
                {
                    if (MatchesPartyConditionTarget(window, member, loopSemantic))
                        return window.Condition;
                }
            }

            foreach (var predicate in GetCurrentPartyPredicates(registerTracker, instructionAddress, member, loopSemantic))
            {
                PartyConditionKind legacyCondition = InferPartyConditionForPredicate(predicate);
                if (legacyCondition != PartyConditionKind.None)
                    return legacyCondition;
            }

            return PartyConditionKind.None;
        }

        private List<PartyPredicate> GetCurrentPartyPredicates(RegisterTracker registerTracker, uint instructionAddress,
            PartyMemberReference member = null, LoopSemanticKind loopSemantic = LoopSemanticKind.None)
        {
            if (registerTracker?.ActivePartyPredicateWindows == null || registerTracker.ActivePartyPredicateWindows.Count == 0)
                return new List<PartyPredicate>();

            return registerTracker.ActivePartyPredicateWindows
                .Where(window => window != null &&
                                 window.IsActiveAt(instructionAddress) &&
                                 MatchesPartyPredicateTarget(window, member, loopSemantic))
                .OrderByDescending(window => window.StartAddress)
                .Select(window => NormalizePartyPredicateForLoopAggregation(window.Predicate, loopSemantic))
                .Where(predicate => predicate != null)
                .GroupBy(BuildPartyPredicateKey)
                .Select(group => group.First())
                .ToList();
        }

        private bool MatchesPartyConditionTarget(PartyConditionWindow window, PartyMemberReference member, LoopSemanticKind loopSemantic)
        {
            if (window?.TargetMember == null)
                return true;

            if (window.TargetMember.IsPartyLoopMember)
                return IsPartyLoopTarget(member, loopSemantic);

            if (window.TargetMember.SelectionKind == PartyMemberSelectionKind.Random)
                return member?.SelectionKind == PartyMemberSelectionKind.Random;

            if (window.TargetMember.SelectionKind == PartyMemberSelectionKind.Dynamic &&
                !window.TargetMember.MemberIndex.HasValue)
            {
                return member?.SelectionKind == PartyMemberSelectionKind.Dynamic ||
                       IsPartyLoopTarget(member, loopSemantic);
            }

            if (window.TargetMember.MemberIndex.HasValue)
                return member?.MemberIndex == window.TargetMember.MemberIndex;

            if (window.TargetMember.PointerTableAddress.HasValue && member?.PointerTableAddress.HasValue == true)
                return window.TargetMember.PointerTableAddress.Value == member.PointerTableAddress.Value;

            if (window.TargetMember.StructureAddress.HasValue && member?.StructureAddress.HasValue == true)
                return window.TargetMember.StructureAddress.Value == member.StructureAddress.Value;

            return true;
        }

        private bool MatchesPartyPredicateTarget(PartyPredicateWindow window, PartyMemberReference member, LoopSemanticKind loopSemantic)
        {
            if (window?.Predicate?.TargetMember == null)
                return true;

            return MatchesPartyConditionTarget(
                new PartyConditionWindow { TargetMember = window.Predicate.TargetMember },
                member,
                loopSemantic);
        }

        private PartyConditionWindow TryBuildBranchConditionWindow(BinaryReader br, RegisterTracker registerTracker,
            string mnemonic, bool branchTaken, uint instructionAddress, uint nextAddress, uint branchTarget)
        {
            var branchPredicate = InferPartyPredicateForBranch(registerTracker, mnemonic, branchTaken, instructionAddress);
            var branchCondition = InferPartyConditionForPredicate(branchPredicate);
            if (branchCondition == PartyConditionKind.None)
                return null;

            if (!TryGetBranchWindowBounds(br, branchTaken, nextAddress, branchTarget, out uint startAddress, out uint endAddress))
                return null;

            return new PartyConditionWindow
            {
                Condition = branchCondition,
                StartAddress = startAddress,
                EndAddress = endAddress,
                TargetMember = branchPredicate?.TargetMember?.Clone()
            };
        }

        private PartyPredicateWindow TryBuildBranchPredicateWindow(BinaryReader br, RegisterTracker registerTracker,
            string mnemonic, bool branchTaken, uint instructionAddress, uint nextAddress, uint branchTarget)
        {
            var branchPredicate = InferPartyPredicateForBranch(registerTracker, mnemonic, branchTaken, instructionAddress);
            if (branchPredicate == null)
                return null;

            if (!TryGetBranchWindowBounds(br, branchTaken, nextAddress, branchTarget, out uint startAddress, out uint endAddress))
                return null;

            return new PartyPredicateWindow
            {
                Predicate = branchPredicate,
                StartAddress = startAddress,
                EndAddress = endAddress
            };
        }

        private bool TryGetBranchWindowBounds(BinaryReader br, bool branchTaken, uint nextAddress, uint branchTarget,
            out uint startAddress, out uint endAddress)
        {
            startAddress = 0;
            endAddress = 0;

            if (branchTarget <= nextAddress)
                return false;

            if (!branchTaken)
            {
                startAddress = nextAddress;
                endAddress = branchTarget;
                return true;
            }

            return TryInferForwardJumpBranchWindow(br, nextAddress, branchTarget, out startAddress, out endAddress);
        }

        private bool TryInferForwardJumpBranchWindow(BinaryReader br, uint fallthroughStart, uint branchTarget,
            out uint startAddress, out uint endAddress)
        {
            startAddress = 0;
            endAddress = 0;

            if (br == null || branchTarget <= fallthroughStart)
                return false;

            X86Instruction lastInstruction = null;
            uint cursor = fallthroughStart;
            while (cursor < branchTarget)
            {
                if (!TryDisassembleNext(br, cursor, out var instruction) || instruction == null)
                    return false;

                uint instructionEnd = (uint)(instruction.Address + instruction.Bytes.Length);
                if (instructionEnd > branchTarget)
                    return false;

                lastInstruction = instruction;
                cursor = instructionEnd;
            }

            if (cursor != branchTarget || lastInstruction == null)
                return false;

            if (!TryGetUnconditionalForwardJumpTarget(lastInstruction, out uint joinAddress) || joinAddress <= branchTarget)
                return false;

            startAddress = branchTarget;
            endAddress = joinAddress;
            return true;
        }

        private bool TryGetUnconditionalForwardJumpTarget(X86Instruction instruction, out uint targetAddress)
        {
            targetAddress = 0;
            if (instruction == null)
                return false;

            string mnemonic = instruction.Mnemonic?.ToUpperInvariant();
            if (mnemonic != "JMP")
                return false;

            targetAddress = GetInstructionTargetAddress(instruction, long.MaxValue);
            return targetAddress != 0;
        }

        private void ApplyPartyConditionToPending(PendingPartyHpOperation pending, PartyConditionKind condition)
        {
            if (pending == null)
                return;

            if (condition == PartyConditionKind.MaleOnly)
            {
                pending.MaleOnly = true;
                pending.FemaleOnly = false;
            }
            else if (condition == PartyConditionKind.FemaleOnly)
            {
                pending.FemaleOnly = true;
                pending.MaleOnly = false;
            }
        }

        private void ApplyPartyPredicatesToPending(PendingPartyHpOperation pending, IEnumerable<PartyPredicate> predicates)
        {
            if (pending == null)
                return;

            pending.GuardPredicates = (pending.GuardPredicates ?? new List<PartyPredicate>())
                .Concat(predicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Select(predicate => predicate.Clone())
                .GroupBy(BuildPartyPredicateKey)
                .Select(group => group.First())
                .OrderBy(BuildPartyPredicateKey)
                .ToList();
        }

        private PendingPartyHpOperation EnsurePendingPartyHpOperation(PathAnalysisResult result,
            PartyFieldReference fieldRef, uint instructionAddress)
        {
            if (result == null || fieldRef == null)
                return null;

            if (result.PendingPartyHpOperation != null &&
                !MatchesPendingPartyTarget(result.PendingPartyHpOperation.Member, fieldRef.Member))
            {
                result.PendingPartyHpOperation = null;
            }

            if (result.PendingPartyHpOperation == null)
            {
                result.PendingPartyHpOperation = new PendingPartyHpOperation
                {
                    Member = fieldRef.Member?.Clone(),
                    StartAddress = instructionAddress
                };
            }
            else
            {
                if (result.PendingPartyHpOperation.Member == null)
                    result.PendingPartyHpOperation.Member = fieldRef.Member?.Clone();

                if (instructionAddress != 0 &&
                    (result.PendingPartyHpOperation.StartAddress == 0 || instructionAddress < result.PendingPartyHpOperation.StartAddress))
                {
                    result.PendingPartyHpOperation.StartAddress = instructionAddress;
                }
            }

            return result.PendingPartyHpOperation;
        }

        private bool IsPartyLoopTarget(PartyMemberReference member, LoopSemanticKind loopSemantic)
        {
            return loopSemantic == LoopSemanticKind.PartyMemberScan || member?.IsPartyLoopMember == true;
        }

        private PartyMemberReference NormalizeMemberForLoopAggregation(PartyMemberReference member, LoopSemanticKind loopSemantic)
        {
            if (!IsPartyLoopTarget(member, loopSemantic))
                return member?.Clone();

            return PartyEffectSemantics.NormalizeLoopTargetMember(member);
        }

        private void MarkPartyMemberScanLoop(PathAnalysisResult result, uint loopBodyStartAddress, bool debugMode, uint? loopDetectionAddress = null)
        {
            if (result == null)
                return;

            bool firstDetection = result.LoopSemantic != LoopSemanticKind.PartyMemberScan;
            result.IsInLoop = true;
            result.LoopSemantic = LoopSemanticKind.PartyMemberScan;

            if (result.LoopStartAddress == 0 || loopBodyStartAddress < result.LoopStartAddress)
                result.LoopStartAddress = loopBodyStartAddress;

            uint detectedLoopEnd = loopDetectionAddress ?? loopBodyStartAddress;
            if (detectedLoopEnd > result.LoopEndAddress)
                result.LoopEndAddress = detectedLoopEnd;

            if (result.PendingPartyHpOperation?.Member != null)
                result.PendingPartyHpOperation.Member =
                    NormalizeMemberForLoopAggregation(result.PendingPartyHpOperation.Member, result.LoopSemantic);

            if (result.PendingPartyHpOperation?.GuardPredicates != null &&
                result.PendingPartyHpOperation.GuardPredicates.Count > 0)
            {
                result.PendingPartyHpOperation.GuardPredicates =
                    PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(
                        result.PendingPartyHpOperation.GuardPredicates);
            }

            PromoteEffectsCapturedBeforeLoopRecognition(result, loopBodyStartAddress, detectedLoopEnd);

            if (debugMode && firstDetection)
                AnalysisDebug.WriteLine($"    РАСПОЗНАН ЦИКЛ ОБХОДА ПАРТИИ: счётчик сравнивается с [0x{PARTY_COUNT_ADDRESS:X4}]");
        }

        private void PromoteEffectsCapturedBeforeLoopRecognition(PathAnalysisResult result, uint loopBodyStartAddress, uint loopDetectionAddress)
        {
            if (result?.PartyEffects == null || result.PartyEffects.Count == 0)
                return;

            foreach (var effect in result.PartyEffects.Where(e => ShouldPromoteEffectToLoopCurrentMember(e, loopBodyStartAddress, loopDetectionAddress)))
            {
                effect.IsLoopDerived = true;
                effect.GuardPredicates = PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(effect.GuardPredicates);

                if (PartyEffectSemantics.IsStateChanging(effect))
                {
                    effect.Scope = PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None ||
                                   PartyEffectSemantics.HasEffectiveGuardPredicates(effect)
                        ? PartyEffectScope.PartySubset
                        : PartyEffectScope.WholeParty;
                }
                else
                {
                    effect.Scope = PartyEffectScope.CurrentLoopMember;
                }

                effect.MemberIndex = null;

                string humanDescription = PartyEffectSemantics.BuildHumanDescription(effect);
                if (!string.IsNullOrWhiteSpace(humanDescription))
                    effect.Description = humanDescription;
            }
        }

        private static bool ShouldPromoteEffectToLoopCurrentMember(PartyEffect effect, uint loopBodyStartAddress, uint loopDetectionAddress)
        {
            if (effect == null || effect.IsLoopDerived)
                return false;

            if (!effect.MemberIndex.HasValue || effect.InstructionAddress == 0)
                return false;

            if (effect.Scope == PartyEffectScope.RandomMember || effect.Scope == PartyEffectScope.SelectedMember)
                return false;

            return effect.InstructionAddress >= loopBodyStartAddress &&
                   effect.InstructionAddress <= loopDetectionAddress;
        }

        private static bool IsPendingPartyMemberScanBackEdge(RegisterTracker registerTracker)
        {
            return registerTracker != null &&
                   registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareMemory &&
                   string.Equals(registerTracker.LastFlagsRegister, "BL", StringComparison.OrdinalIgnoreCase) &&
                   registerTracker.LastFlagsInstructionAddress.HasValue;
        }

        private PartyEffectScope ResolveDirectPartyEffectScope(
            PartyMemberReference member,
            LoopSemanticKind loopSemantic,
            PartyConditionKind condition)
        {
            if (member?.SelectionKind == PartyMemberSelectionKind.Random)
                return PartyEffectScope.RandomMember;

            if (member?.SelectionKind == PartyMemberSelectionKind.Dynamic)
                return PartyEffectScope.SelectedMember;

            if (member?.MemberIndex.HasValue == true)
                return PartyEffectScope.SingleMember;

            if (IsPartyLoopTarget(member, loopSemantic))
                return condition != PartyConditionKind.None
                    ? PartyEffectScope.PartySubset
                    : PartyEffectScope.WholeParty;

            return PartyEffectScope.Unknown;
        }

        private void ApplyResolvedPartyEffectTarget(
            PartyEffect effect,
            PartyMemberReference member,
            LoopSemanticKind loopSemantic,
            PartyConditionKind condition)
        {
            if (effect == null)
                return;

            PartyEffectScope scope = ResolveDirectPartyEffectScope(member, loopSemantic, condition);
            effect.Scope = scope;
            effect.Condition = condition;
            effect.IsLoopDerived = IsPartyLoopTarget(member, loopSemantic);
            if (!effect.ObservedMemberIndex.HasValue && member?.MemberIndex.HasValue == true)
                effect.ObservedMemberIndex = member.MemberIndex;
            effect.MemberIndex = scope == PartyEffectScope.SingleMember
                ? member?.MemberIndex
                : null;
        }

        private static bool MatchesPendingPartyTarget(PartyMemberReference left, PartyMemberReference right)
        {
            if (left == null || right == null)
                return true;

            if (left.IsPartyLoopMember && right.IsPartyLoopMember)
                return true;

            if (left.MemberIndex.HasValue && right.MemberIndex.HasValue)
                return left.MemberIndex.Value == right.MemberIndex.Value;

            if (left.PointerTableAddress.HasValue && right.PointerTableAddress.HasValue)
            {
                if (left.PointerTableAddress.Value != right.PointerTableAddress.Value)
                    return false;

                if (left.SelectionKind != PartyMemberSelectionKind.Exact ||
                    right.SelectionKind != PartyMemberSelectionKind.Exact)
                {
                    return left.SelectionKind == right.SelectionKind;
                }

                return true;
            }

            if (left.PointerAddress.HasValue && right.PointerAddress.HasValue)
                return left.PointerAddress.Value == right.PointerAddress.Value;

            if (left.StructureAddress.HasValue && right.StructureAddress.HasValue)
                return left.StructureAddress.Value == right.StructureAddress.Value;

            return true;
        }

        private bool TryDecode16BitMemoryOperandSyntax(byte[] instructionBytes, out byte mod, out byte rm,
            out int signedDisp, out int decodedLength, out string eaText)
        {
            mod = 0;
            rm = 0;
            signedDisp = 0;
            decodedLength = 0;
            eaText = null;

            if (instructionBytes == null || instructionBytes.Length < 2)
                return false;

            byte modRm = instructionBytes[1];
            mod = (byte)((modRm >> 6) & 0x03);
            rm = (byte)(modRm & 0x07);

            if (mod == 0x03)
                return false;

            switch (mod)
            {
                case 0x00:
                    if (rm == 0x06)
                    {
                        if (instructionBytes.Length < 4)
                            return false;

                        ushort directAddr = BitConverter.ToUInt16(instructionBytes, 2);
                        decodedLength = 4;
                        eaText = $"[0x{directAddr:X4}]";
                        signedDisp = directAddr;
                        return true;
                    }

                    decodedLength = 2;
                    break;

                case 0x01:
                    if (instructionBytes.Length < 3)
                        return false;

                    signedDisp = unchecked((sbyte)instructionBytes[2]);
                    decodedLength = 3;
                    break;

                case 0x02:
                    if (instructionBytes.Length < 4)
                        return false;

                    signedDisp = unchecked((short)BitConverter.ToUInt16(instructionBytes, 2));
                    decodedLength = 4;
                    break;

                default:
                    return false;
            }

            string baseText = rm switch
            {
                0x00 => "BX+SI",
                0x01 => "BX+DI",
                0x02 => "BP+SI",
                0x03 => "BP+DI",
                0x04 => "SI",
                0x05 => "DI",
                0x06 => "BP",
                0x07 => "BX",
                _ => null
            };

            if (string.IsNullOrEmpty(baseText))
                return false;

            if (signedDisp == 0)
                eaText = $"[{baseText}]";
            else if (signedDisp > 0)
                eaText = $"[{baseText}+0x{signedDisp:X}]";
            else
                eaText = $"[{baseText}-0x{(-signedDisp):X}]";

            return true;
        }

        private PartyPredicate InferPartyPredicateForBranch(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            uint instructionAddress)
        {
            if (registerTracker == null)
                return null;

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate)
                return null;

            string comparedRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(comparedRegister) || !registerTracker.LastCompareImmediate.HasValue)
                return null;

            if (!registerTracker.TryGetPartyFieldValue(comparedRegister, out var comparedField) || comparedField == null)
                return null;

            var comparison = ResolvePredicateComparisonForBranch(mnemonic, branchTaken);
            if (comparison == PartyPredicateComparison.Unknown)
                return null;

            return new PartyPredicate
            {
                Field = comparedField.Field,
                Comparison = comparison,
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = registerTracker.LastCompareImmediate.Value,
                InstructionAddress = instructionAddress,
                TargetMember = comparedField.Member?.Clone(),
                Description = BuildPartyPredicateDescription(comparedField.Field, comparison, registerTracker.LastCompareImmediate.Value)
            };
        }

        private PartyPredicateComparison ResolvePredicateComparisonForBranch(string mnemonic, bool branchTaken)
        {
            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            return jump switch
            {
                "JE" or "JZ" => branchTaken ? PartyPredicateComparison.Equal : PartyPredicateComparison.NotEqual,
                "JNE" or "JNZ" => branchTaken ? PartyPredicateComparison.NotEqual : PartyPredicateComparison.Equal,
                "JB" or "JC" or "JNAE" => branchTaken ? PartyPredicateComparison.LessThan : PartyPredicateComparison.GreaterOrEqual,
                "JBE" or "JNA" => branchTaken ? PartyPredicateComparison.LessOrEqual : PartyPredicateComparison.GreaterThan,
                "JA" or "JNBE" => branchTaken ? PartyPredicateComparison.GreaterThan : PartyPredicateComparison.LessOrEqual,
                "JAE" or "JNB" or "JNC" => branchTaken ? PartyPredicateComparison.GreaterOrEqual : PartyPredicateComparison.LessThan,
                _ => PartyPredicateComparison.Unknown
            };
        }

        private PartyConditionKind InferPartyConditionForBranch(RegisterTracker registerTracker, string mnemonic, bool branchTaken)
        {
            return InferPartyConditionForPredicate(
                InferPartyPredicateForBranch(registerTracker, mnemonic, branchTaken, registerTracker?.LastFlagsInstructionAddress ?? 0));
        }

        private PartyConditionKind InferPartyConditionForPredicate(PartyPredicate predicate)
        {
            if (predicate?.Field != PartyFieldKind.Gender || !predicate.ImmediateValue.HasValue)
                return PartyConditionKind.None;

            ushort immediateValue = predicate.ImmediateValue.Value;
            return predicate.Comparison switch
            {
                PartyPredicateComparison.Equal when immediateValue == 1 => PartyConditionKind.MaleOnly,
                PartyPredicateComparison.NotEqual when immediateValue == 1 => PartyConditionKind.FemaleOnly,
                PartyPredicateComparison.Equal when immediateValue == 2 => PartyConditionKind.FemaleOnly,
                PartyPredicateComparison.NotEqual when immediateValue == 2 => PartyConditionKind.MaleOnly,
                _ => PartyConditionKind.None
            };
        }

        private string BuildPartyPredicateDescription(PartyFieldKind field, PartyPredicateComparison comparison, ushort value)
        {
            string fieldText = field switch
            {
                PartyFieldKind.Gender => "Gender",
                PartyFieldKind.Hp => "HP",
                PartyFieldKind.HpLow => "HP low",
                PartyFieldKind.HpHigh => "HP high",
                PartyFieldKind.Status => "Status",
                PartyFieldKind.Technical77 => PartyTechnicalField77Semantics.FieldLabel,
                _ => field.ToString()
            };

            string comparisonText = comparison switch
            {
                PartyPredicateComparison.Equal => "==",
                PartyPredicateComparison.NotEqual => "!=",
                PartyPredicateComparison.LessThan => "<",
                PartyPredicateComparison.LessOrEqual => "<=",
                PartyPredicateComparison.GreaterThan => ">",
                PartyPredicateComparison.GreaterOrEqual => ">=",
                _ => "?"
            };

            return $"{fieldText} {comparisonText} 0x{value:X2}";
        }

        private string BuildPartyPredicateKey(PartyPredicate predicate)
        {
            if (predicate == null)
                return "<NULL_PREDICATE>";

            string range = predicate.ImmediateRange == null
                ? "-"
                : $"{predicate.ImmediateRange.Min}-{predicate.ImmediateRange.Max}";

            string targetKey = predicate.TargetMember == null
                ? "-"
                : string.Join(":",
                    predicate.TargetMember.SelectionKind,
                    predicate.TargetMember.IsPartyLoopMember ? "Loop" : "Direct",
                    predicate.TargetMember.MemberIndex?.ToString() ?? "-",
                    predicate.TargetMember.PointerTableAddress?.ToString("X4") ?? "-",
                    predicate.TargetMember.StructureAddress?.ToString("X4") ?? "-");

            return string.Join("|",
                predicate.Field,
                predicate.Comparison,
                predicate.ValueKnowledge,
                predicate.ImmediateValue?.ToString() ?? "-",
                range,
                targetKey);
        }

        private PartyPredicate NormalizePartyPredicateForLoopAggregation(PartyPredicate predicate, LoopSemanticKind loopSemantic)
        {
            if (predicate == null)
                return null;

            if (predicate.TargetMember == null || !IsPartyLoopTarget(predicate.TargetMember, loopSemantic))
                return predicate.Clone();

            return PartyEffectSemantics.NormalizePredicateForLoopAggregation(predicate);
        }

        private void RegisterPartyFieldRead(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress = 0, byte? exactValue = null)
        {
            if (result == null || fieldRef == null)
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true
            });

            var currentCondition = GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var currentPredicates = GetCurrentPartyPredicates(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);

            if (fieldRef.Field == PartyFieldKind.HpHigh || fieldRef.Field == PartyFieldKind.HpLow)
            {
                var pendingFieldRef = fieldRef.Clone();
                pendingFieldRef.Member = NormalizeMemberForLoopAggregation(fieldRef.Member, result.LoopSemantic);
                var pending = EnsurePendingPartyHpOperation(result, pendingFieldRef, instructionAddress);
                if (pending != null)
                {
                    pending.MaleOnly = pending.MaleOnly || result.PendingPartyHpOperation?.MaleOnly == true;
                    pending.FemaleOnly = pending.FemaleOnly || result.PendingPartyHpOperation?.FemaleOnly == true;

                    if (fieldRef.Field == PartyFieldKind.HpHigh)
                        pending.SawReadHigh = true;
                    else
                        pending.SawReadLow = true;

                    ApplyPartyConditionToPending(pending, currentCondition);
                    ApplyPartyPredicatesToPending(pending, currentPredicates);
                }
            }
            else if (fieldRef.Field == PartyFieldKind.Technical77)
            {
                AddResolvedPartyEffect(
                    result,
                    PartyEffectFactory.CreateTechnicalField77ReadEffect(fieldRef.Member, instructionAddress, exactValue),
                    fieldRef.Member,
                    registerTracker,
                    currentCondition,
                    debugPrefix: "Техническое поле персонажа");
            }
        }

        private void RegisterPartyFieldWrite(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress, bool debugMode,
            byte? exactValue = null, PartyEffectOperation bitOperation = PartyEffectOperation.Unknown,
            byte? bitMask = null, PartyFieldReference sourceFieldValue = null)
        {
            if (result == null || fieldRef == null)
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsWrite = true
            });

            var currentCondition = GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var currentPredicates = GetCurrentPartyPredicates(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var effects = new List<PartyEffect>();
            if (fieldRef.Field == PartyFieldKind.Gender)
            {
                var effect = new PartyEffect
                {
                    Kind = PartyEffectKind.GenderWritten,
                    Field = PartyFieldKind.Gender,
                    Operation = PartyEffectOperation.Write,
                    ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                    ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                    InstructionAddress = instructionAddress
                };
                ApplyResolvedPartyEffectTarget(effect, fieldRef.Member, result.LoopSemantic, currentCondition);
                effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
                effects.Add(effect);
            }
            else if (fieldRef.Field == PartyFieldKind.HpHigh || fieldRef.Field == PartyFieldKind.HpLow)
            {
                var effect = new PartyEffect
                {
                    Kind = PartyEffectKind.HpWritten,
                    InstructionAddress = instructionAddress
                };
                PartyConditionKind effectCondition = currentCondition != PartyConditionKind.None
                    ? currentCondition
                    : (IsPartyLoopTarget(fieldRef.Member, result.LoopSemantic) && result.PendingPartyHpOperation?.MaleOnly == true
                        ? PartyConditionKind.MaleOnly
                        : IsPartyLoopTarget(fieldRef.Member, result.LoopSemantic) && result.PendingPartyHpOperation?.FemaleOnly == true
                            ? PartyConditionKind.FemaleOnly
                            : PartyConditionKind.None);
                ApplyResolvedPartyEffectTarget(effect, fieldRef.Member, result.LoopSemantic, effectCondition);
                effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
                effects.Add(effect);

                var pendingFieldRef = fieldRef.Clone();
                pendingFieldRef.Member = NormalizeMemberForLoopAggregation(fieldRef.Member, result.LoopSemantic);
                var pending = EnsurePendingPartyHpOperation(result, pendingFieldRef, instructionAddress);
                if (pending != null)
                {
                    ApplyPartyConditionToPending(pending, currentCondition);
                    ApplyPartyPredicatesToPending(pending, currentPredicates);

                    if (fieldRef.Field == PartyFieldKind.HpHigh)
                        pending.SawWriteHigh = true;
                    else
                        pending.SawWriteLow = true;
                }
            }
            else if (fieldRef.Field == PartyFieldKind.Status)
            {
                if (exactValue.HasValue)
                {
                    effects.Add(PartyEffectFactory.CreateStatusWriteEffect(fieldRef.Member, instructionAddress, exactValue.Value));
                }
                else if (sourceFieldValue?.Field == PartyFieldKind.Status &&
                         sourceFieldValue.BitTransform != null &&
                         !sourceFieldValue.BitTransform.IsIdentity)
                {
                    effects.AddRange(BuildStatusEffectsFromBitTransform(fieldRef.Member, sourceFieldValue.BitTransform, instructionAddress));
                }
                else if (bitMask.HasValue)
                {
                    effects.Add(PartyEffectFactory.CreateStatusBitEffect(fieldRef.Member, bitOperation, bitMask.Value, instructionAddress));
                }
                else
                {
                    effects.Add(PartyEffectFactory.CreateStatusWriteEffect(fieldRef.Member, instructionAddress));
                }
            }
            else if (fieldRef.Field == PartyFieldKind.Technical77)
            {
                if (sourceFieldValue?.Field == PartyFieldKind.Technical77 &&
                    sourceFieldValue.BitTransform != null &&
                    !sourceFieldValue.BitTransform.IsIdentity)
                {
                    effects.AddRange(BuildTechnicalField77EffectsFromBitTransform(fieldRef.Member, sourceFieldValue.BitTransform, instructionAddress));
                }
                else if (bitMask.HasValue &&
                         (bitOperation == PartyEffectOperation.BitSet ||
                          bitOperation == PartyEffectOperation.BitClear ||
                          bitOperation == PartyEffectOperation.BitToggle))
                {
                    effects.Add(PartyEffectFactory.CreateTechnicalField77BitEffect(fieldRef.Member, bitOperation, bitMask.Value, instructionAddress));
                }
                else
                {
                    effects.Add(PartyEffectFactory.CreateTechnicalField77WriteEffect(fieldRef.Member, instructionAddress, exactValue));
                }
            }

            foreach (var effect in effects.Where(e => e != null))
            {
                string debugPrefix = fieldRef.Field switch
                {
                    PartyFieldKind.Status => "Статус персонажа",
                    PartyFieldKind.Technical77 => "Техническое поле персонажа",
                    _ => null
                };

                AddResolvedPartyEffect(result, effect, fieldRef.Member, registerTracker, currentCondition, debugPrefix, debugMode);
            }
        }

        private void AddResolvedPartyEffect(PathAnalysisResult result, PartyEffect effect,
            PartyMemberReference member, RegisterTracker registerTracker, PartyConditionKind currentCondition,
            string debugPrefix = null, bool debugMode = false)
        {
            if (result == null || effect == null)
                return;

            ApplyResolvedPartyEffectTarget(effect, member, result.LoopSemantic, currentCondition);
            effect.GuardPredicates = GetMergedGuardPredicates(
                effect.GuardPredicates,
                GetCurrentPartyPredicates(
                    registerTracker,
                    effect.InstructionAddress,
                    member,
                    result.LoopSemantic));

            string humanDescription = PartyEffectSemantics.BuildHumanDescription(effect);
            if (!string.IsNullOrWhiteSpace(humanDescription))
                effect.Description = humanDescription;

            string effectKey = PartyEffectSemantics.BuildSemanticKey(effect);
            if (!result.PartyEffects.Any(e => e != null && PartyEffectSemantics.BuildSemanticKey(e) == effectKey))
                result.PartyEffects.Add(effect);

            if (debugMode && !string.IsNullOrWhiteSpace(debugPrefix) && !string.IsNullOrWhiteSpace(effect.Description))
                AnalysisDebug.WriteLine($"        {debugPrefix}: {effect.Description}");
        }

        private void RegisterTechnicalField77CompareEffect(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress, byte compareValue, bool isBitMask, bool debugMode)
        {
            if (result == null || fieldRef?.Field != PartyFieldKind.Technical77)
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            AddResolvedPartyEffect(
                result,
                PartyEffectFactory.CreateTechnicalField77CompareEffect(fieldRef.Member, instructionAddress, compareValue, isBitMask),
                fieldRef.Member,
                registerTracker,
                GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic),
                debugPrefix: "Техническое поле персонажа",
                debugMode: debugMode);
        }

        private List<PartyPredicate> GetMergedGuardPredicates(IEnumerable<PartyPredicate> existing,
            IEnumerable<PartyPredicate> inferred)
        {
            return (existing ?? Enumerable.Empty<PartyPredicate>())
                .Concat(inferred ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Select(predicate => predicate.Clone())
                .GroupBy(BuildPartyPredicateKey)
                .Select(group => group.First())
                .OrderBy(BuildPartyPredicateKey)
                .ToList();
        }

        private PartyEffectOperation MapImmediateByteTransformOperation(byte groupOperation)
        {
            return groupOperation switch
            {
                0x01 => PartyEffectOperation.BitSet,
                0x04 => PartyEffectOperation.BitClear,
                0x06 => PartyEffectOperation.BitToggle,
                _ => PartyEffectOperation.Unknown
            };
        }

        private byte GetRelevantStatusMask(PartyEffectOperation operation, byte immediateValue)
        {
            return operation switch
            {
                PartyEffectOperation.BitSet => (byte)(immediateValue & PartyStatusSemantics.TrackedMask),
                PartyEffectOperation.BitClear => (byte)(unchecked((byte)~immediateValue) & PartyStatusSemantics.TrackedMask),
                PartyEffectOperation.BitToggle => (byte)(immediateValue & PartyStatusSemantics.TrackedMask),
                _ => 0
            };
        }

        private bool TryApplyImmediateByteTransform(byte oldValue, byte immediateValue,
            PartyEffectOperation operation, out byte newValue)
        {
            newValue = oldValue;

            switch (operation)
            {
                case PartyEffectOperation.BitSet:
                    newValue = (byte)(oldValue | immediateValue);
                    return true;
                case PartyEffectOperation.BitClear:
                    newValue = (byte)(oldValue & immediateValue);
                    return true;
                case PartyEffectOperation.BitToggle:
                    newValue = (byte)(oldValue ^ immediateValue);
                    return true;
                default:
                    return false;
            }
        }

        private List<PartyEffect> BuildStatusEffectsFromBitTransform(PartyMemberReference member,
            PartyFieldBitTransform bitTransform, uint instructionAddress)
        {
            var effects = new List<PartyEffect>();
            if (bitTransform == null || bitTransform.IsIdentity)
                return effects;

            void AddEffect(PartyEffectOperation operation, byte mask)
            {
                byte trackedMask = (byte)(mask & PartyStatusSemantics.TrackedMask);
                if (trackedMask == 0)
                    return;

                PartyEffect effect = PartyEffectFactory.CreateStatusBitEffect(member, operation, trackedMask, instructionAddress);
                if (effect != null)
                    effects.Add(effect);
            }

            AddEffect(PartyEffectOperation.BitSet, bitTransform.SetMask);
            AddEffect(PartyEffectOperation.BitClear, bitTransform.ClearMask);
            AddEffect(PartyEffectOperation.BitToggle, bitTransform.ToggleMask);

            if (effects.Count == 0)
            {
                PartyEffect unknownEffect = PartyEffectFactory.CreateStatusWriteEffect(member, instructionAddress);
                if (unknownEffect != null)
                    effects.Add(unknownEffect);
            }

            return effects;
        }

        private List<PartyEffect> BuildTechnicalField77EffectsFromBitTransform(PartyMemberReference member,
            PartyFieldBitTransform bitTransform, uint instructionAddress)
        {
            var effects = new List<PartyEffect>();
            if (bitTransform == null || bitTransform.IsIdentity)
                return effects;

            void AddEffect(PartyEffectOperation operation, byte mask)
            {
                if (mask == 0)
                    return;

                PartyEffect effect = PartyEffectFactory.CreateTechnicalField77BitEffect(member, operation, mask, instructionAddress);
                if (effect != null)
                    effects.Add(effect);
            }

            AddEffect(PartyEffectOperation.BitSet, bitTransform.SetMask);
            AddEffect(PartyEffectOperation.BitClear, bitTransform.ClearMask);
            AddEffect(PartyEffectOperation.BitToggle, bitTransform.ToggleMask);

            if (effects.Count == 0)
            {
                PartyEffect unknownEffect = PartyEffectFactory.CreateTechnicalField77WriteEffect(member, instructionAddress);
                if (unknownEffect != null)
                    effects.Add(unknownEffect);
            }

            return effects;
        }

        private void TrackPartyFieldRegisterMemoryTransform(RegisterTracker registerTracker, string regName,
            PartyEffectOperation operation, byte? sourceValue, uint instructionAddress, string mnemonic,
            string sourceDescription, bool debugMode)
        {
            if (registerTracker == null || string.IsNullOrWhiteSpace(regName))
                return;

            string fullReg = GetFullRegisterNameForByteRegister(regName);
            if (string.IsNullOrWhiteSpace(fullReg))
                return;

            string instructionText = string.IsNullOrWhiteSpace(sourceDescription)
                ? $"{mnemonic} {regName}"
                : $"{mnemonic} {regName}, {sourceDescription}";

            if (sourceValue.HasValue &&
                registerTracker.TryGetByteRegisterValue(regName, out byte oldValue) &&
                TryApplyImmediateByteTransform(oldValue, sourceValue.Value, operation, out byte newValue))
            {
                registerTracker.TrackPartialRegisterOperation(
                    fullReg,
                    regName,
                    newValue,
                    instructionAddress,
                    instructionText);
            }
            else
            {
                registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
            }

            if (registerTracker.TryGetPartyFieldValue(regName, out var partyField) &&
                partyField != null)
            {
                if (sourceValue.HasValue)
                    partyField.ApplyBitOperation(operation, sourceValue.Value);
                else
                    partyField.BitTransform = null;

                registerTracker.SetPartyFieldValue(regName, partyField);
            }

            registerTracker.ClearPartyPointerByteValue(regName);

            if (debugMode)
            {
                string valueText = sourceValue.HasValue
                    ? $"0x{sourceValue.Value:X2}"
                    : "неизвестное значение";
                AnalysisDebug.WriteLine($"        Обновили регистр {regName} через {mnemonic} с байтом {valueText} из {sourceDescription}");
            }
        }

        private void TrackPartyFieldRegisterImmediateTransform(PathAnalysisResult result, RegisterTracker registerTracker, string regName,
            PartyEffectOperation operation, byte immediateValue, uint instructionAddress, bool debugMode)
        {
            if (registerTracker == null || string.IsNullOrWhiteSpace(regName))
                return;

            if (!registerTracker.TryGetPartyFieldValue(regName, out var partyField) ||
                partyField == null)
            {
                return;
            }

            string fullReg = GetFullRegisterNameForByteRegister(regName);
            if (string.IsNullOrWhiteSpace(fullReg))
                return;

            if (registerTracker.TryGetByteRegisterValue(regName, out byte oldValue) &&
                TryApplyImmediateByteTransform(oldValue, immediateValue, operation, out byte newValue))
            {
                registerTracker.TrackPartialRegisterOperation(
                    fullReg,
                    regName,
                    newValue,
                    instructionAddress,
                    $"{operation} {regName}, 0x{immediateValue:X2}");
            }
            else
            {
                registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
            }

            partyField.ApplyBitOperation(operation, immediateValue);
            registerTracker.SetPartyFieldValue(regName, partyField);
            registerTracker.ClearPartyPointerByteValue(regName);

            if (debugMode)
                AnalysisDebug.WriteLine($"        Обновили регистр {regName} как байт поля персонажа {partyField.Field} через {operation} 0x{immediateValue:X2}");
        }

        private void TrackPartyArithmeticInstruction(X86Instruction insn, RegisterTracker registerTracker, PathAnalysisResult result)
        {
            if (result?.PendingPartyHpOperation == null || insn?.Bytes == null || insn.Bytes.Length == 0)
                return;

            byte[] bytes = insn.Bytes;

            if (bytes.Length == 1 && bytes[0] == 0xF8)
            {
                result.PendingPartyHpOperation.SawClc = true;
                return;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xD0 && bytes[1] == 0xE8 && registerTracker.TryGetPartyFieldValue("AL", out var shrField) && shrField.Field == PartyFieldKind.HpHigh)
            {
                result.PendingPartyHpOperation.SawShrHigh = true;
                return;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xD0 && bytes[1] == 0xD8 && registerTracker.TryGetPartyFieldValue("AL", out var rcrField) && rcrField.Field == PartyFieldKind.HpLow)
            {
                result.PendingPartyHpOperation.SawRcrLow = true;
                return;
            }

            if (!registerTracker.TryGetPartyFieldValue("AL", out var hpField) ||
                (hpField.Field != PartyFieldKind.HpLow && hpField.Field != PartyFieldKind.HpHigh))
            {
                return;
            }

            PartyEffectOperation operation = PartyEffectOperation.Unknown;
            bool usesCarryOpcode = false;
            bool carryInKnown = true;
            bool carryInValue = false;
            byte rawImmediate = 0;
            ushort? effectiveImmediate = null;

            if (bytes.Length >= 2 && bytes[0] == 0x04)
            {
                operation = PartyEffectOperation.Increment;
                rawImmediate = bytes[1];
                effectiveImmediate = rawImmediate;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0x14)
            {
                operation = PartyEffectOperation.Increment;
                usesCarryOpcode = true;
                rawImmediate = bytes[1];
                carryInKnown = registerTracker.FlagsKnown;
                carryInValue = carryInKnown && registerTracker.CarryFlag;
                effectiveImmediate = hpField.Field == PartyFieldKind.HpLow
                    ? carryInKnown ? (ushort)(rawImmediate + (carryInValue ? 1 : 0)) : (ushort?)null
                    : rawImmediate;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0x2C)
            {
                operation = PartyEffectOperation.Decrement;
                rawImmediate = bytes[1];
                effectiveImmediate = rawImmediate;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0x1C)
            {
                operation = PartyEffectOperation.Decrement;
                usesCarryOpcode = true;
                rawImmediate = bytes[1];
                carryInKnown = registerTracker.FlagsKnown;
                carryInValue = carryInKnown && registerTracker.CarryFlag;
                effectiveImmediate = hpField.Field == PartyFieldKind.HpLow
                    ? carryInKnown ? (ushort)(rawImmediate + (carryInValue ? 1 : 0)) : (ushort?)null
                    : rawImmediate;
            }

            if (operation == PartyEffectOperation.Unknown)
                return;

            var pendingField = hpField.Clone();
            pendingField.Member = NormalizeMemberForLoopAggregation(hpField.Member, result.LoopSemantic);

            var pending = EnsurePendingPartyHpOperation(result, pendingField, (uint)insn.Address);
            if (pending == null)
                return;

            var byteArithmetic = new PendingPartyByteArithmetic
            {
                Operation = operation,
                RawImmediateValue = rawImmediate,
                EffectiveImmediateValue = effectiveImmediate,
                UsesCarryOpcode = usesCarryOpcode,
                CarryInKnown = carryInKnown,
                CarryInValue = carryInValue,
                InstructionAddress = (uint)insn.Address
            };

            if (hpField.Field == PartyFieldKind.HpLow)
                pending.LowByteArithmetic = byteArithmetic;
            else
                pending.HighByteArithmetic = byteArithmetic;
        }


        private void AddOrderedText(PathAnalysisResult result, string text, bool isContextual, uint instructionAddress, ref int textOrderCounter)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (isContextual)
                result.ContextTexts.Add(text);
            else
                result.FoundTexts.Add(text);

            if (!result.OrderedTexts.Any(t => t.Text == text && t.IsContextual == isContextual))
            {
                result.OrderedTexts.Add(new TextEntry
                {
                    Text = text,
                    IsContextual = isContextual,
                    Address = instructionAddress,
                    Order = textOrderCounter++
                });
            }
        }

        private void MergeOrderedTextsFromSubroutine(PathAnalysisResult result, PathAnalysisResult subroutineResult)
        {
            int nextOrder = result.OrderedTexts.Count == 0 ? 0 : result.OrderedTexts.Max(t => t.Order) + 1;

            foreach (var entry in subroutineResult.OrderedTexts.OrderBy(t => t.Order))
            {
                if (entry == null || string.IsNullOrEmpty(entry.Text))
                    continue;

                if (result.OrderedTexts.Any(t => t.Text == entry.Text && t.IsContextual == entry.IsContextual))
                    continue;

                var clone = entry.Clone();
                clone.Order = nextOrder++;
                result.OrderedTexts.Add(clone);

                if (clone.IsContextual)
                    result.ContextTexts.Add(clone.Text);
                else
                    result.FoundTexts.Add(clone.Text);
            }
        }

        /// <summary>
        /// Обрабатывает косвенные тексты (формируемые из AL и BP)
        /// </summary>
        private void ProcessIndirectTexts(byte? lastAlValue, ushort? lastBpValue, BinaryReader br,
            ref uint lastTextAddress, HashSet<string> foundTextsInThisPath, PathAnalysisResult result,
            X86Instruction insn, bool debugMode, ref int textOrderCounter)
        {
            if (lastAlValue.HasValue && lastBpValue.HasValue)
            {
                ushort combinedAddr = (ushort)((lastBpValue.Value << 8) | lastAlValue.Value);
                if (combinedAddr != lastTextAddress)
                {
                    lastTextAddress = combinedAddr;
                    string text = ExtractText(br, combinedAddr);
                    if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                    {
                        string textEntry = $"Text at 0x{combinedAddr:X4}: {text}";
                        if (!foundTextsInThisPath.Contains(textEntry))
                        {
                            foundTextsInThisPath.Add(textEntry);
                            AddOrderedText(result, textEntry, false, (uint)insn.Address, ref textOrderCounter);

                            if (result.FirstLocalTextAddress == uint.MaxValue)
                            {
                                result.FirstLocalTextAddress = (uint)insn.Address;
                                if (debugMode)
                                    AnalysisDebug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
                            }

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Найден косвенный текст по адресу 0x{combinedAddr:X4}: {text}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Обрабатывает найденные прямые тексты
        /// </summary>
        private void ProcessFoundTexts(HashSet<string> newTexts, HashSet<string> foundTextsInThisPath,
            PathAnalysisResult result, int callDepth, X86Instruction insn, bool debugMode, ref int textOrderCounter)
        {
            foreach (var text in newTexts)
            {
                if (!foundTextsInThisPath.Contains(text))
                {
                    foundTextsInThisPath.Add(text);

                    bool isContextual = callDepth > 0;
                    AddOrderedText(result, text, isContextual, (uint)insn.Address, ref textOrderCounter);

                    if (!isContextual && result.FirstLocalTextAddress == uint.MaxValue)
                    {
                        result.FirstLocalTextAddress = (uint)insn.Address;
                        if (debugMode)
                            AnalysisDebug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
                    }

                    if (debugMode)
                    {
                        if (isContextual)
                            AnalysisDebug.WriteLine($"        Найден контекстный текст (из CALL): {text}");
                        else
                            AnalysisDebug.WriteLine($"        Найден прямой текст: {text}");
                    }
                }
            }
        }

        private void AppendPrintedCharToLastText(PathAnalysisResult result, RegisterTracker registerTracker,
            uint instructionAddress, bool debugMode)
        {
            if (result == null || registerTracker == null)
                return;

            if (!registerTracker.TryGetByteRegisterValue("AL", out byte charValue))
                return;

            if (charValue < 0x20 || charValue > 0x7E)
                return;

            var lastText = result.OrderedTexts
                .Where(t => t != null && !string.IsNullOrEmpty(t.Text) && !t.IsContextual)
                .OrderBy(t => t.Order)
                .LastOrDefault();

            if (lastText == null)
                return;

            string previousText = lastText.Text;
            string appendedText = previousText + (char)charValue;
            lastText.Text = appendedText;
            lastText.Address = instructionAddress;

            if (result.FoundTexts.Contains(previousText))
                result.FoundTexts.Remove(previousText);
            result.FoundTexts.Add(appendedText);

            if (debugMode)
                AnalysisDebug.WriteLine($"        Дописали символ '{(char)charValue}' к последнему тексту -> {appendedText}");
        }

        /// <summary>
        /// Обрабатывает инструкции управления потоком
        /// </summary>
        private ControlFlowResult HandleControlFlowInstructions(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result, uint currentAddress, uint nextAddress, long fileLength, bool debugMode,
            bool invalidateReturnRegistersAfterExternalCall, List<uint> pendingReturnAddresses,
            HashSet<uint> visitedInThisPath, Dictionary<uint, byte> finiteLoopProgressByJumpAddress)
        {
            string mnemonicUpper = insn.Mnemonic?.ToUpper() ?? "";

            if (IsReturnInstruction(mnemonicUpper))
            {
                if (pendingReturnAddresses != null && pendingReturnAddresses.Count > 0)
                {
                    uint returnAddress = pendingReturnAddresses[pendingReturnAddresses.Count - 1];
                    pendingReturnAddresses.RemoveAt(pendingReturnAddresses.Count - 1);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"      RET на 0x{insn.Address:X4} - возвращаемся к 0x{returnAddress:X4}");

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = returnAddress,
                        UpdatedPendingReturnAddresses = new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = Math.Max(0, callDepth - 1)
                    };
                }

                if (debugMode)
                    AnalysisDebug.WriteLine($"      RET на 0x{insn.Address:X4} - конец пути");

                result.IsTerminated = true;
                result.HasSignificantCode = true;
                result.TerminatedByTerminalRet = true;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            if (insn.Bytes.Length >= 1 && insn.Bytes[0] == 0xE9)
                return HandleJmpOpcodeE9(insn, br, fileLength, debugMode, result, currentAddress);

            if (insn.Bytes.Length >= 2 && insn.Bytes[0] == 0xEB)
                return HandleShortJmp(insn, br, fileLength, debugMode, result, currentAddress);

            if (mnemonicUpper == "JMP" || mnemonicUpper == "JMPF" || mnemonicUpper == "JMPL" ||
                mnemonicUpper == "JMPE" || mnemonicUpper == "JMPI")
            {
                return HandleJmp(insn, br, fileLength, debugMode, result, currentAddress);
            }

            if (mnemonicUpper.StartsWith("CALL"))
            {
                return HandleCall(insn, br, registerTracker, globallyAnalyzedAddresses, depth, callDepth,
                    pathId, targetX, targetY, processedBackEdges, result, nextAddress, debugMode,
                    invalidateReturnRegistersAfterExternalCall, pendingReturnAddresses);
            }

            if (IsConditionalJump(insn, out uint condJumpTarget))
            {
                return HandleConditionalJump(insn, br, condJumpTarget, nextAddress, fileLength,
                    debugMode, processedBackEdges, result, currentAddress, registerTracker,
                    pendingReturnAddresses, callDepth, visitedInThisPath, finiteLoopProgressByJumpAddress);
            }

            return new ControlFlowResult
            {
                ShouldReturn = false,
                NextAddress = nextAddress,
                UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                UpdatedCallDepth = callDepth
            };
        }

        /// <summary>
        /// Результат обработки инструкции управления потоком
        /// </summary>
        private class ControlFlowResult
        {
            public bool ShouldReturn { get; set; }
            public PathAnalysisResult Result { get; set; }
            public uint NextAddress { get; set; }
            public List<uint> UpdatedPendingReturnAddresses { get; set; }
            public int UpdatedCallDepth { get; set; } = -1;
        }

        private void MarkRandomEncounterJump(PathAnalysisResult result, bool debugMode, string jumpKind)
        {
            if (result == null)
                return;

            bool hasOtherEffects =
                (result.OrderedTexts != null && result.OrderedTexts.Count > 0) ||
                result.MonsterPower.HasValue ||
                result.MonsterLevel.HasValue ||
                result.MonsterBatchCount.HasValue ||
                result.DarkeningLevel.HasValue ||
                result.RandomEncounterChance.HasValue ||
                result.HasTeleportTarget ||
                result.BattleMonsterCount.HasValue ||
                result.BattleMonsterCountRange != null ||
                result.IsBattleMonsterCountIndeterminate ||
                (result.BattleMonsterEntries != null && result.BattleMonsterEntries.Values.Any(entry => entry.val1 != 0 || entry.val2 != 0 || entry.isIndeterminate)) ||
                (result.PartialBattles != null && result.PartialBattles.Count > 0) ||
                result.HasPartialBattlePattern ||
                (result.PartialBattleInfo != null && result.PartialBattleInfo.Count > 0) ||
                (result.PartyEffects != null && result.PartyEffects.Count > 0);

            result.CallsRandomEncounter = true;
            result.IsOnlyRandomEncounterJump = !hasOtherEffects;
            result.HasSignificantCode = true;

            if (debugMode)
            {
                string suffix = result.IsOnlyRandomEncounterJump
                    ? " без иных эффектов"
                    : " с дополнительными эффектами";
                AnalysisDebug.WriteLine($"      Обнаружен вызов random encounter через {jumpKind} к 0x517C{suffix}");
            }
        }

        /// <summary>
        /// Обрабатывает инструкцию JMP
        /// </summary>
        private ControlFlowResult HandleJmp(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress)
        {
            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

            if (jumpTarget == 0x517C)
            {
                MarkRandomEncounterJump(result, debugMode, "JMP");
                result.IsTerminated = true;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            if (jumpTarget >= fileLength)
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"      JMP за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                result.IsTerminated = true;
                result.HasSignificantCode = true;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            if (jumpTarget != 0 && jumpTarget < fileLength)
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"      JMP к 0x{jumpTarget:X4}");
                return new ControlFlowResult { ShouldReturn = false, NextAddress = jumpTarget };
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = currentAddress + (uint)insn.Bytes.Length };
        }

        /// <summary>
        /// Обрабатывает JMP по опкоду 0xE9
        /// </summary>
        private ControlFlowResult HandleJmpOpcodeE9(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress)
        {
            if (insn.Bytes.Length >= 3)
            {
                ushort jumpOffset = (ushort)(insn.Bytes[1] | (insn.Bytes[2] << 8));
                uint jumpTarget = (uint)(insn.Address + 3 + (short)jumpOffset);

                // Специальный случай: JMP на 0x517C означает вызов random encounter.
                // Эту проверку нужно делать ДО проверки выхода за пределы оверлея,
                // потому что адрес 0x517C обычно находится вне текущего overlay-файла.
                if (jumpTarget == 0x517C)
                {
                    MarkRandomEncounterJump(result, debugMode, "JMP");
                    result.IsTerminated = true;
                    return new ControlFlowResult { ShouldReturn = true, Result = result };
                }

                if (jumpTarget >= fileLength)
                {
                    if (debugMode)
                        AnalysisDebug.WriteLine($"      JMP (по опкоду 0xE9) за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                    result.IsTerminated = true;
                    result.HasSignificantCode = true;
                    return new ControlFlowResult { ShouldReturn = true, Result = result };
                }

                if (jumpTarget != 0)
                {
                    if (debugMode)
                        AnalysisDebug.WriteLine($"      JMP (по опкоду 0xE9) к 0x{jumpTarget:X4}");
                    return new ControlFlowResult { ShouldReturn = false, NextAddress = jumpTarget };
                }
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = currentAddress + (uint)insn.Bytes.Length };
        }

        /// <summary>
        /// Обрабатывает короткий JMP
        /// </summary>
        private ControlFlowResult HandleShortJmp(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress)
        {
            if (insn.Bytes.Length >= 2)
            {
                sbyte jumpOffset = (sbyte)insn.Bytes[1];
                uint jumpTarget = (uint)(insn.Address + 2 + jumpOffset);

                if (jumpTarget < fileLength)
                {
                    if (jumpTarget == 0x517C)
                    {
                        MarkRandomEncounterJump(result, debugMode, "SHORT JMP");
                    }

                    if (debugMode)
                        AnalysisDebug.WriteLine($"      SHORT JMP к 0x{jumpTarget:X4}");
                    return new ControlFlowResult { ShouldReturn = false, NextAddress = jumpTarget };
                }
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = currentAddress + (uint)insn.Bytes.Length };
        }

        /// <summary>
        /// Обрабатывает инструкцию CALL
        /// </summary>
        private ControlFlowResult HandleCall(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result, uint nextAddress, bool debugMode,
            bool invalidateReturnRegistersAfterExternalCall, List<uint> pendingReturnAddresses)
        {
            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
            bool isInternalCall = callTarget < br.BaseStream.Length && callTarget != 0;

            if (debugMode)
                AnalysisDebug.WriteLine($"      CALL 0x{callTarget:X4} (возврат в 0x{nextAddress:X4})");

            // SUB_509A находится вне overlay, поэтому её нужно распознавать
            // ДО проверки isInternalCall. Она возвращает в AL псевдослучайное
            // значение в диапазоне 1..N, где N уже лежит в AL на входе.
            if (callTarget == 0x509A)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte n) && n > 0)
                {
                    registerTracker.SetRegisterRange("AL", 1, n, RegisterValueDistribution.UniformDiscreteRange);
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознана SUB_509A: AL получает псевдослучайное значение в диапазоне 1..{n}");
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var nRange) &&
                         nRange != null &&
                         nRange.Max > 0)
                {
                    registerTracker.SetRegisterRange("AL", 1, nRange.Max, RegisterValueDistribution.UniformDiscreteRange);
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознана SUB_509A: AL получает псевдослучайное значение в диапазоне 1..{nRange.Max} (верхняя граница из диапазона)");
                }
                else
                {
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                    if (debugMode)
                        AnalysisDebug.WriteLine("        SUB_509A вызвана, но верхняя граница N в AL неизвестна");
                }

                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == 0x5101)
            {
                registerTracker.InvalidateRegister("AX");
                registerTracker.SetRegisterRange("AL", 0x1B, 0x35, RegisterValueDistribution.Unknown);
                registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                if (debugMode)
                    AnalysisDebug.WriteLine("        Распознана SUB_5101: AL получает код клавиши в диапазоне ESC..'5'");

                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == 0x4C55)
            {
                AppendPrintedCharToLastText(result, registerTracker, (uint)insn.Address, debugMode);
                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (isInternalCall)
            {
                var subroutineTracker = registerTracker.Clone();
                var subroutinePendingReturnAddresses = pendingReturnAddresses != null
                    ? new List<uint>(pendingReturnAddresses)
                    : new List<uint>();
                subroutinePendingReturnAddresses.Add(nextAddress);

                var subroutineResult = ExecuteCodeAtAddress(br, callTarget, subroutineTracker,
                    globallyAnalyzedAddresses, depth + 1, callDepth + 1, pathId, targetX, targetY,
                    processedBackEdges, invalidateReturnRegistersAfterExternalCall, subroutinePendingReturnAddresses,
                    new Dictionary<ushort, byte>(_emulatedMemory8),
                    _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()));

                // Добавляем результаты из подпрограммы
                foreach (var visitedAddress in subroutineResult.VisitedAddresses)
                    result.VisitedAddresses.Add(visitedAddress);

                // Переносим тексты из подпрограммы с сохранением порядка и признака контекстности.
                MergeOrderedTextsFromSubroutine(result, subroutineResult);

                if (subroutineResult.MonsterPower.HasValue && !result.MonsterPower.HasValue)
                    result.MonsterPower = subroutineResult.MonsterPower;

                if (subroutineResult.MonsterLevel.HasValue && !result.MonsterLevel.HasValue)
                    result.MonsterLevel = subroutineResult.MonsterLevel;

                if (subroutineResult.BattleMonsterCountRange != null && result.BattleMonsterCountRange == null)
                    result.BattleMonsterCountRange = new ValueRange8(subroutineResult.BattleMonsterCountRange.Min, subroutineResult.BattleMonsterCountRange.Max);

                if (subroutineResult.BattleMonsterCount.HasValue && !result.BattleMonsterCount.HasValue)
                    result.BattleMonsterCount = subroutineResult.BattleMonsterCount;

                result.IsBattleMonsterCountIndeterminate =
                    result.IsBattleMonsterCountIndeterminate || subroutineResult.IsBattleMonsterCountIndeterminate;

                foreach (var entry in subroutineResult.BattleMonsterEntries)
                {
                    if (!result.BattleMonsterEntries.TryGetValue(entry.Key, out var existing))
                    {
                        result.BattleMonsterEntries[entry.Key] = entry.Value;
                        continue;
                    }

                    bool existingDefined = existing.val1 != 0 && existing.val2 != 0 && !existing.isIndeterminate;
                    bool newDefined = entry.Value.val1 != 0 && entry.Value.val2 != 0 && !entry.Value.isIndeterminate;

                    if (!existingDefined && newDefined)
                    {
                        result.BattleMonsterEntries[entry.Key] = entry.Value;
                    }
                    else if (existing.isIndeterminate && !entry.Value.isIndeterminate)
                    {
                        result.BattleMonsterEntries[entry.Key] = entry.Value;
                    }
                    else if (!existing.isIndeterminate && entry.Value.isIndeterminate)
                    {
                        // Оставляем existing.
                    }
                    else
                    {
                        // Одинаковая степень определённости: оставляем более позднюю запись.
                        result.BattleMonsterEntries[entry.Key] = entry.Value;
                    }
                }

                foreach (var partial in subroutineResult.PartialBattles)
                    if (!result.PartialBattles.Any(p => p.BxIndex == partial.BxIndex))
                        result.PartialBattles.Add(partial);

                foreach (var info in subroutineResult.PartialBattleInfo)
                    if (!result.PartialBattleInfo.Any(i => i.BxIndex == info.BxIndex && i.DestAddr == info.DestAddr))
                        result.PartialBattleInfo.Add(info);

                result.HasPartialBattlePattern = result.HasPartialBattlePattern || subroutineResult.HasPartialBattlePattern;

                foreach (var altPath in subroutineResult.AlternativePaths)
                {
                    if (!result.AlternativePaths.Any(p => p.Address == altPath.Address &&
                                                         p.TargetAddress == altPath.TargetAddress &&
                                                         p.Condition == altPath.Condition))
                    {
                        result.AlternativePaths.Add(new AlternativePath
                        {
                            ObjectIndex = altPath.ObjectIndex,
                            Address = altPath.Address,
                            Condition = altPath.Condition,
                            TargetAddress = altPath.TargetAddress,
                            Analyzed = altPath.Analyzed,
                            PathNumber = result.AlternativePaths.Count + 1,
                            CompareValue = altPath.CompareValue,
                            CompareRegister = altPath.CompareRegister,
                            IsInputChoiceBranch = altPath.IsInputChoiceBranch,
                            RegisterState = altPath.RegisterState?.Clone(),
                            ProbabilityNumerator = altPath.ProbabilityNumerator,
                            ProbabilityDenominator = altPath.ProbabilityDenominator,
                            CallDepth = altPath.CallDepth,
                            PendingReturnAddresses = altPath.PendingReturnAddresses == null
                                ? new List<uint>()
                                : new List<uint>(altPath.PendingReturnAddresses),
                            EmulatedMemory8 = altPath.EmulatedMemory8 == null
                                ? new Dictionary<ushort, byte>()
                                : new Dictionary<ushort, byte>(altPath.EmulatedMemory8),
                            EmulatedPartyPointers = altPath.EmulatedPartyPointers == null
                                ? new Dictionary<ushort, PartyMemberReference>()
                                : altPath.EmulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            EmulatedPartyPointerBytes = altPath.EmulatedPartyPointerBytes == null
                                ? new Dictionary<ushort, PartyPointerByteReference>()
                                : altPath.EmulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            BranchPartyCondition = altPath.BranchPartyCondition,
                            BranchPartyPredicate = altPath.BranchPartyPredicate?.Clone()
                        });
                    }
                }

                result.IsTerminated = subroutineResult.IsTerminated;
                result.HasSignificantCode = result.HasSignificantCode || subroutineResult.HasSignificantCode;
                result.IsOnlyRandomEncounterJump = result.IsOnlyRandomEncounterJump || subroutineResult.IsOnlyRandomEncounterJump;
                result.ExitPendingReturnAddresses = subroutineResult.ExitPendingReturnAddresses == null
                    ? new List<uint>()
                    : new List<uint>(subroutineResult.ExitPendingReturnAddresses);
                result.ExitCallDepth = subroutineResult.ExitCallDepth;

                return new ControlFlowResult
                {
                    ShouldReturn = true,
                    Result = result,
                    UpdatedPendingReturnAddresses = result.ExitPendingReturnAddresses,
                    UpdatedCallDepth = result.ExitCallDepth
                };
            }
            else if (invalidateReturnRegistersAfterExternalCall)
            {
                // Не инвалидируем AX/AL глобально. Только помечаем, что значение AX/AL
                // потенциально зависит от результата внешней функции. Эта пометка будет
                // учтена позже только при вычислении количества монстров в полной битве.
                registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                if (debugMode)
                    AnalysisDebug.WriteLine("        Помечаем AX/AL как кандидата на зависимость от внешнего CALL для табличного объекта");
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }

        /// <summary>
        /// Обрабатывает условный переход
        /// </summary>
        private ControlFlowResult HandleConditionalJump(X86Instruction insn, BinaryReader br, uint condJumpTarget,
            uint nextAddress, long fileLength, bool debugMode,
            HashSet<(uint From, uint To)> processedBackEdges, PathAnalysisResult result, uint currentAddress,
            RegisterTracker registerTracker, List<uint> pendingReturnAddresses, int callDepth,
            HashSet<uint> visitedInThisPath, Dictionary<uint, byte> finiteLoopProgressByJumpAddress)
        {
            if (condJumpTarget < fileLength && condJumpTarget != 0)
            {
                bool? branchTaken = EvaluateConditionalJump(insn.Mnemonic, registerTracker);
                if (branchTaken.HasValue)
                {
                    uint resolvedTarget = branchTaken.Value ? condJumpTarget : nextAddress;

                    if (condJumpTarget < currentAddress && branchTaken.Value)
                    {
                        if (TryAdvanceFiniteBattleCounterLoop(registerTracker, result, currentAddress, condJumpTarget,
                            visitedInThisPath, finiteLoopProgressByJumpAddress, debugMode))
                        {
                            return new ControlFlowResult
                            {
                                ShouldReturn = false,
                                NextAddress = condJumpTarget,
                                UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                                UpdatedCallDepth = callDepth
                            };
                        }

                        if (IsPendingPartyMemberScanBackEdge(registerTracker))
                            MarkPartyMemberScanLoop(result, condJumpTarget, debugMode, currentAddress);

                        var backEdge = (From: currentAddress, To: condJumpTarget);

                        if (!processedBackEdges.Add(backEdge))
                        {
                            ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                                (uint)insn.Address, debugMode);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"      Повторный обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, продолжаем линейно");
                            return new ControlFlowResult
                            {
                                ShouldReturn = false,
                                NextAddress = nextAddress,
                                UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                                UpdatedCallDepth = callDepth
                            };
                        }

                        if (debugMode)
                            AnalysisDebug.WriteLine($"      Первый обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, переход определён по флагам");
                    }

                    ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken.Value,
                        (uint)insn.Address, debugMode);

                    if (debugMode)
                    {
                        string branchText = branchTaken.Value ? $"0x{condJumpTarget:X4}" : $"0x{nextAddress:X4}";
                        AnalysisDebug.WriteLine($"      Условный переход {insn.Mnemonic} разрешён по известным флагам -> {branchText}");
                    }

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = resolvedTarget,
                        UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = callDepth
                    };
                }

                if (condJumpTarget < currentAddress)
                {
                    if (IsPendingPartyMemberScanBackEdge(registerTracker))
                        MarkPartyMemberScanLoop(result, condJumpTarget, debugMode, currentAddress);

                    var backEdge = (From: currentAddress, To: condJumpTarget);

                    if (!processedBackEdges.Add(backEdge))
                    {
                        ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                            (uint)insn.Address, debugMode);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"      Повторный обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, продолжаем линейно");
                        return new ControlFlowResult
                        {
                            ShouldReturn = false,
                            NextAddress = nextAddress,
                            UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            UpdatedCallDepth = callDepth
                        };
                    }

                    if (debugMode)
                        AnalysisDebug.WriteLine($"      Первый обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, разрешаем одну развилку");
                }

                var takenProbability = EstimateBranchProbability(insn.Mnemonic, registerTracker, branchTaken: true);
                var notTakenProbability = EstimateBranchProbability(insn.Mnemonic, registerTracker, branchTaken: false);

                bool isInputChoiceBranch = false;
                if (string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) &&
                    registerTracker.TryGetRegisterRange("AL", out var alInputRange) && alInputRange != null)
                {
                    isInputChoiceBranch = alInputRange.Min == 0x1B && alInputRange.Max == 0x35;
                }

                // Добавляем целевой адрес перехода как альтернативный путь
                var altPath = new AlternativePath
                {
                    Address = (uint)insn.Address,
                    TargetAddress = condJumpTarget,
                    Condition = $"{insn.Mnemonic} {insn.Operand}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1,
                    RegisterState = CloneRegisterStateForBranch(br, registerTracker, insn.Mnemonic, branchTaken: true,
                        (uint)insn.Address, nextAddress, condJumpTarget),
                    CompareRegister = registerTracker.LastFlagsRegister,
                    CompareValue = registerTracker.LastCompareImmediate,
                    IsInputChoiceBranch = isInputChoiceBranch,
                    ProbabilityNumerator = takenProbability.numerator,
                    ProbabilityDenominator = takenProbability.denominator,
                    CallDepth = callDepth,
                    PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                    EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8),
                    EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    BranchPartyCondition = InferPartyConditionForBranch(registerTracker, insn.Mnemonic, branchTaken: true),
                    BranchPartyPredicate = InferPartyPredicateForBranch(registerTracker, insn.Mnemonic, branchTaken: true, (uint)insn.Address)
                };

                if (!result.AlternativePaths.Any(p => p.Address == altPath.Address &&
                                                       p.TargetAddress == altPath.TargetAddress))
                {
                    result.AlternativePaths.Add(altPath);
                    if (debugMode)
                        AnalysisDebug.WriteLine($"      Найден альтернативный путь: 0x{insn.Address:X4} -> 0x{condJumpTarget:X4}");
                }

                // Добавляем линейное продолжение как отдельный путь
                var linearPath = new AlternativePath
                {
                    Address = (uint)insn.Address,
                    TargetAddress = nextAddress,
                    Condition = $"LINEAR after {insn.Mnemonic}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1,
                    RegisterState = CloneRegisterStateForBranch(br, registerTracker, insn.Mnemonic, branchTaken: false,
                        (uint)insn.Address, nextAddress, condJumpTarget),
                    CompareRegister = registerTracker.LastFlagsRegister,
                    CompareValue = registerTracker.LastCompareImmediate,
                    IsInputChoiceBranch = isInputChoiceBranch,
                    ProbabilityNumerator = notTakenProbability.numerator,
                    ProbabilityDenominator = notTakenProbability.denominator,
                    CallDepth = callDepth,
                    PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                    EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8),
                    EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    BranchPartyCondition = InferPartyConditionForBranch(registerTracker, insn.Mnemonic, branchTaken: false),
                    BranchPartyPredicate = InferPartyPredicateForBranch(registerTracker, insn.Mnemonic, branchTaken: false, (uint)insn.Address)
                };

                if (!result.AlternativePaths.Any(p => p.Address == linearPath.Address &&
                                                       p.TargetAddress == linearPath.TargetAddress))
                {
                    result.AlternativePaths.Add(linearPath);
                    if (debugMode)
                        AnalysisDebug.WriteLine($"      Добавлен линейный путь: 0x{insn.Address:X4} -> 0x{nextAddress:X4}");
                }

                // Завершаем текущий путь – не продолжаем выполнение
                if (debugMode)
                    AnalysisDebug.WriteLine($"      Останавливаем анализ текущего пути после условного перехода");

                result.IsTerminated = true;
                result.HasSignificantCode = result.FoundTexts.Count > 0 ||
                                             result.ContextTexts.Count > 0 ||
                                             result.MonsterPower.HasValue ||
                                             result.MonsterLevel.HasValue ||
                                             result.BattleMonsterEntries.Count > 0 ||
                                             result.PartialBattles.Count > 0 ||
                                             result.HasPartialBattlePattern ||
                                             result.CallsRandomEncounter;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }



        private bool TryCalculateBranchConstraint(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            out string reg, out int min, out int max)
        {
            reg = null;
            min = 0;
            max = 0;

            if (registerTracker == null)
                return false;

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate)
                return false;

            reg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(reg) || !registerTracker.LastCompareImmediate.HasValue)
                return false;

            if (!registerTracker.TryGetRegisterRange(reg, out var range) || range == null)
                return false;

            min = range.Min;
            max = range.Max;
            int imm = registerTracker.LastCompareImmediate.Value;
            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();

            if (branchTaken)
            {
                switch (jump)
                {
                    case "JE":
                    case "JZ":
                        min = max = imm;
                        break;

                    case "JNE":
                    case "JNZ":
                        if (min == imm) min = imm + 1;
                        else if (max == imm) max = imm - 1;
                        else return false;
                        break;

                    case "JB":
                    case "JC":
                    case "JNAE":
                        max = Math.Min(max, imm - 1);
                        break;

                    case "JBE":
                    case "JNA":
                        max = Math.Min(max, imm);
                        break;

                    case "JA":
                    case "JNBE":
                        min = Math.Max(min, imm + 1);
                        break;

                    case "JAE":
                    case "JNB":
                    case "JNC":
                        min = Math.Max(min, imm);
                        break;

                    default:
                        return false;
                }
            }
            else
            {
                switch (jump)
                {
                    case "JE":
                    case "JZ":
                        if (min == imm) min = imm + 1;
                        else if (max == imm) max = imm - 1;
                        else return false;
                        break;

                    case "JNE":
                    case "JNZ":
                        min = max = imm;
                        break;

                    case "JB":
                    case "JC":
                    case "JNAE":
                        min = Math.Max(min, imm);
                        break;

                    case "JBE":
                    case "JNA":
                        min = Math.Max(min, imm + 1);
                        break;

                    case "JA":
                    case "JNBE":
                        max = Math.Min(max, imm);
                        break;

                    case "JAE":
                    case "JNB":
                    case "JNC":
                        max = Math.Min(max, imm - 1);
                        break;

                    default:
                        return false;
                }
            }

            if (min < 0) min = 0;
            if (max > 0xFF) max = 0xFF;
            return min <= max;
        }

        private void ApplyBranchConstraintInPlace(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            uint instructionAddress, bool debugMode)
        {
            if (!TryCalculateBranchConstraint(registerTracker, mnemonic, branchTaken, out string reg, out int min, out int max))
                return;

            var distribution = GetExistingRegisterDistributionOrUnknown(registerTracker, reg);
            registerTracker.SetRegisterRange(reg, (byte)min, (byte)max, distribution);

            if (debugMode)
            {
                string branchText = branchTaken ? "taken" : "linear";
                AnalysisDebug.WriteLine(
                    $"      Уточнили диапазон {reg} для ветки {branchText} после {mnemonic}: {min:X2}..{max:X2} (инстр. 0x{instructionAddress:X4})");
            }
        }

        private RegisterTracker CloneRegisterStateForBranch(BinaryReader br, RegisterTracker registerTracker, string mnemonic,
            bool branchTaken, uint instructionAddress, uint nextAddress, uint branchTarget)
        {
            var clone = registerTracker?.Clone() ?? new RegisterTracker();

            if (TryCalculateBranchConstraint(clone, mnemonic, branchTaken, out string reg, out int min, out int max))
            {
                var distribution = GetExistingRegisterDistributionOrUnknown(clone, reg);
                clone.SetRegisterRange(reg, (byte)min, (byte)max, distribution);
            }

            var conditionWindow = TryBuildBranchConditionWindow(br, registerTracker, mnemonic, branchTaken,
                instructionAddress, nextAddress, branchTarget);
            if (conditionWindow != null)
            {
                clone.ActivePartyConditionWindows ??= new List<PartyConditionWindow>();
                clone.ActivePartyConditionWindows.Add(conditionWindow);
            }

            var predicateWindow = TryBuildBranchPredicateWindow(br, registerTracker, mnemonic, branchTaken,
                instructionAddress, nextAddress, branchTarget);
            if (predicateWindow != null)
            {
                clone.ActivePartyPredicateWindows ??= new List<PartyPredicateWindow>();
                clone.ActivePartyPredicateWindows.Add(predicateWindow);
            }

            return clone;
        }

        private RegisterValueDistribution GetExistingRegisterDistributionOrUnknown(RegisterTracker registerTracker, string reg)
        {
            if (registerTracker != null &&
                !string.IsNullOrWhiteSpace(reg) &&
                registerTracker.TryGetRegisterDistribution(reg, out var distribution))
            {
                return distribution;
            }

            return RegisterValueDistribution.Unknown;
        }

        private (int numerator, int denominator) EstimateBranchProbability(string mnemonic, RegisterTracker registerTracker, bool branchTaken)
        {
            if (registerTracker == null)
                return (1, 1);

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate)
                return (1, 1);

            string compareRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(compareRegister))
                return (1, 1);

            if (!registerTracker.TryGetRegisterRange(compareRegister, out var range) || range == null)
                return (1, 1);

            if (!registerTracker.TryGetRegisterDistribution(compareRegister, out var distribution) ||
                distribution != RegisterValueDistribution.UniformDiscreteRange)
                return (1, 1);

            if (!registerTracker.LastCompareImmediate.HasValue)
                return (1, 1);

            byte imm = registerTracker.LastCompareImmediate.Value;
            int total = range.Max - range.Min + 1;
            if (total <= 0)
                return (1, 1);

            int favorable = CountFavorableValues((mnemonic ?? string.Empty).ToUpperInvariant(), range, imm, branchTaken);
            if (favorable < 0 || favorable > total)
                return (1, 1);

            return (favorable, total);
        }

        private int CountFavorableValues(string mnemonic, ValueRange8 range, byte imm, bool branchTaken)
        {
            int count = 0;
            for (int value = range.Min; value <= range.Max; value++)
            {
                bool taken = mnemonic switch
                {
                    "JE" or "JZ" => value == imm,
                    "JNE" or "JNZ" => value != imm,
                    "JB" or "JC" or "JNAE" => value < imm,
                    "JBE" or "JNA" => value <= imm,
                    "JA" or "JNBE" => value > imm,
                    "JAE" or "JNB" or "JNC" => value >= imm,
                    _ => false
                };

                if (taken == branchTaken)
                    count++;
            }

            return count;
        }

        private bool? EvaluateConditionalJump(string mnemonic, RegisterTracker registerTracker)
        {
            if (registerTracker == null || !registerTracker.FlagsKnown)
                return null;

            // Схлопываем только координатные проверки.
            // Внешние guard-условия вроде MOV AL,[0xCAFE] / OR AL,AL / JNE должны
            // оставаться развилкой, даже если текущее значение известно.
            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareMemory &&
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.Test)
                return null;

            string flagsReg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(flagsReg))
                return null;

            bool allowDeterministicResolution =
                registerTracker.LastFlagsFromCoordinate ||
                IsDeterministicFlagsSource(registerTracker, flagsReg) ||
                IsDeterministicMemoryCompareSource(registerTracker);

            if (!allowDeterministicResolution)
                return null;

            bool hasExactValue = registerTracker.TryGetRegisterValue(flagsReg, out _)
                || (flagsReg.Length == 2 && flagsReg[1] == 'L' && registerTracker.TryGetByteRegisterValue(flagsReg, out _))
                || (flagsReg.Length == 2 && flagsReg[1] == 'H' && registerTracker.TryGetByteRegisterValue(flagsReg, out _));

            if (!hasExactValue)
                return null;

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            return jump switch
            {
                "JE" or "JZ" => registerTracker.ZeroFlag,
                "JNE" or "JNZ" => !registerTracker.ZeroFlag,
                "JB" or "JC" or "JNAE" => registerTracker.CarryFlag,
                "JAE" or "JNB" or "JNC" => !registerTracker.CarryFlag,
                "JBE" or "JNA" => registerTracker.CarryFlag || registerTracker.ZeroFlag,
                "JA" or "JNBE" => !registerTracker.CarryFlag && !registerTracker.ZeroFlag,
                _ => (bool?)null
            };
        }

        private bool IsDeterministicFlagsSource(RegisterTracker registerTracker, string flagsReg)
        {
            if (registerTracker == null || string.IsNullOrWhiteSpace(flagsReg))
                return false;

            ushort? sourceAddress = registerTracker.GetSourceAddress(flagsReg);
            return sourceAddress.HasValue && _emulatedMemory8.ContainsKey(sourceAddress.Value);
        }

        private bool IsDeterministicMemoryCompareSource(RegisterTracker registerTracker)
        {
            if (registerTracker == null ||
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareMemory ||
                !registerTracker.LastComparedMemoryAddress.HasValue)
            {
                return false;
            }

            ushort memAddr = registerTracker.LastComparedMemoryAddress.Value;
            return _emulatedMemory8.ContainsKey(memAddr) || memAddr == BATTLE_MONSTER_COUNT_ADDRESS;
        }

        private bool TryGetExactBattleMonsterCount(PathAnalysisResult result, out byte battleMonsterCount)
        {
            if (_emulatedMemory8.TryGetValue(BATTLE_MONSTER_COUNT_ADDRESS, out battleMonsterCount))
                return true;

            if (result?.BattleMonsterCountRange?.IsExact == true)
            {
                battleMonsterCount = result.BattleMonsterCountRange.Min;
                return true;
            }

            if (result?.BattleMonsterCount.HasValue == true)
            {
                battleMonsterCount = result.BattleMonsterCount.Value;
                return true;
            }

            battleMonsterCount = 0;
            return false;
        }

        private bool TryAdvanceFiniteBattleCounterLoop(
            RegisterTracker registerTracker,
            PathAnalysisResult result,
            uint jumpAddress,
            uint loopBodyStartAddress,
            HashSet<uint> visitedInThisPath,
            Dictionary<uint, byte> finiteLoopProgressByJumpAddress,
            bool debugMode)
        {
            if (registerTracker == null ||
                visitedInThisPath == null ||
                finiteLoopProgressByJumpAddress == null)
            {
                return false;
            }

            bool comparesAgainstBattleMonsterCount =
                registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareMemory &&
                registerTracker.LastComparedMemoryAddress == BATTLE_MONSTER_COUNT_ADDRESS;

            bool comparesAgainstImmediateBattleLimit =
                registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastCompareImmediate.HasValue &&
                result.BattleMonsterEntries.Count > 0;

            if (!comparesAgainstBattleMonsterCount && !comparesAgainstImmediateBattleLimit)
                return false;

            string counterRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(counterRegister) ||
                !registerTracker.TryGetByteRegisterValue(counterRegister, out byte counterValue))
            {
                return false;
            }

            byte loopLimit;
            string limitSourceText;

            if (comparesAgainstBattleMonsterCount)
            {
                if (!TryGetExactBattleMonsterCount(result, out loopLimit))
                    return false;

                limitSourceText = $"[0x{BATTLE_MONSTER_COUNT_ADDRESS:X4}]";
            }
            else
            {
                if (!string.Equals(counterRegister, "BL", StringComparison.OrdinalIgnoreCase))
                    return false;

                loopLimit = registerTracker.LastCompareImmediate.Value;
                limitSourceText = $"imm8 0x{loopLimit:X2}";
            }

            if (loopLimit == 0 || counterValue >= loopLimit)
                return false;

            if (finiteLoopProgressByJumpAddress.TryGetValue(jumpAddress, out byte previousCounterValue) &&
                counterValue <= previousCounterValue)
            {
                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"      Конечный цикл по {limitSourceText} не продвинулся: {counterRegister}=0x{counterValue:X2}, предыдущее=0x{previousCounterValue:X2}");
                }

                return false;
            }

            finiteLoopProgressByJumpAddress[jumpAddress] = counterValue;

            foreach (var visitedAddress in visitedInThisPath
                .Where(address => address >= loopBodyStartAddress && address <= jumpAddress)
                .ToList())
            {
                visitedInThisPath.Remove(visitedAddress);
            }

            result.IsInLoop = true;
            result.LoopSemantic = result.LoopSemantic == LoopSemanticKind.None
                ? LoopSemanticKind.PartialBattle
                : result.LoopSemantic;

            if (result.LoopStartAddress == 0 || loopBodyStartAddress < result.LoopStartAddress)
                result.LoopStartAddress = loopBodyStartAddress;

            if (jumpAddress > result.LoopEndAddress)
                result.LoopEndAddress = jumpAddress;

            if (counterValue > result.LoopIteration)
                result.LoopIteration = counterValue;

            if (loopLimit > result.LoopIterationCount)
                result.LoopIterationCount = loopLimit;

            result.IsIndeterminateLoop = false;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Конечный цикл по {limitSourceText} продолжается: {counterRegister}=0x{counterValue:X2}, limit=0x{loopLimit:X2}, повторно заходим в тело 0x{loopBodyStartAddress:X4}");
            }

            return true;
        }

        private static void SetArithmeticFlagsForAdd8(RegisterTracker registerTracker, byte left, byte right, byte result)
        {
            int wideResult = left + right;
            registerTracker.CarryFlag = wideResult > 0xFF;
            registerTracker.ZeroFlag = result == 0;
            registerTracker.SignFlag = (result & 0x80) != 0;
            registerTracker.OverflowFlag = (((left ^ result) & (right ^ result) & 0x80) != 0);
            registerTracker.FlagsKnown = true;
            registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Arithmetic);
        }

        private static void SetArithmeticFlagsForSub8(RegisterTracker registerTracker, byte left, byte right, byte result, string comparedRegister = null, RegisterTracker.FlagsOriginKind origin = RegisterTracker.FlagsOriginKind.Arithmetic, uint? instructionAddress = null)
        {
            registerTracker.CarryFlag = left < right;
            registerTracker.ZeroFlag = result == 0;
            registerTracker.SignFlag = (result & 0x80) != 0;
            registerTracker.OverflowFlag = (((left ^ right) & (left ^ result) & 0x80) != 0);
            registerTracker.FlagsKnown = true;
            registerTracker.SetFlagsMetadata(comparedRegister, origin, instructionAddress);
        }

        private static void InvalidateFlags(RegisterTracker registerTracker)
        {
            registerTracker.FlagsKnown = false;
            registerTracker.ClearFlagsMetadata();
        }


        /// <summary>
        /// Проверяет, является ли инструкция возвратом
        /// </summary>
        private bool IsReturnInstruction(string mnemonic)
        {
            string upper = mnemonic?.ToUpper() ?? "";
            return upper == "RET" || upper == "RETF" || upper == "RETN" ||
                   upper == "IRET" || upper == "IRETD";
        }

        /// <summary>
        /// Проверяет, является ли инструкция условным переходом
        /// </summary>
        private bool IsConditionalJump(X86Instruction insn, out uint target)
        {
            target = 0;
            string mnemonic = insn.Mnemonic?.ToUpper() ?? "";

            string[] conditionalJumps = {
                "JZ", "JE", "JNZ", "JNE", "JB", "JNAE", "JC",
                "JNB", "JAE", "JNC", "JBE", "JNA", "JA", "JNBE",
                "JL", "JNGE", "JGE", "JNL", "JLE", "JNG", "JG", "JNLE",
                "JP", "JPE", "JNP", "JPO", "JCXZ", "JECXZ"
            };

            if (conditionalJumps.Contains(mnemonic))
            {
                target = GetInstructionTargetAddress(insn, 0xFFFF);
                return target != 0;
            }
            return false;
        }

        /// <summary>
        /// Получает целевой адрес перехода из инструкции
        /// </summary>
        private uint GetInstructionTargetAddress(X86Instruction insn, long fileLength)
        {
            string operand = insn.Operand ?? "";
            int hexIndex = operand.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (hexIndex >= 0)
            {
                string hexPart = operand.Substring(hexIndex + 2);
                hexPart = new string(hexPart.TakeWhile(c =>
                    (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')).ToArray());

                if (uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint targetAddr))
                {
                    return targetAddr;
                }
            }
            return 0;
        }

        /// <summary>
        /// Пытается дизассемблировать следующую инструкцию
        /// </summary>
        private bool TryDisassembleNext(BinaryReader br, uint address, out X86Instruction instruction)
        {
            instruction = null;
            long fileLength = br.BaseStream.Length;
            if (address >= fileLength) return false;

            int bytesToRead = (int)Math.Min(16, fileLength - address);
            byte[] chunk = ReadBytesAt(br, address, bytesToRead);

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;
                var instructions = capstone.Disassemble(chunk, address);
                if (instructions != null && instructions.Length > 0)
                {
                    instruction = instructions[0];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Читает байты из файла по указанному адресу
        /// </summary>
        private byte[] ReadBytesAt(BinaryReader br, long position, int count)
        {
            long originalPos = br.BaseStream.Position;
            br.BaseStream.Position = position;
            byte[] data = br.ReadBytes(count);
            br.BaseStream.Position = originalPos;
            return data;
        }

        /// <summary>
        /// Проверяет, является ли цикл циклом загрузки из таблиц
        /// </summary>
        private bool IsTableLoadLoop(BinaryReader br, uint address, RegisterTracker registerTracker)
        {
            // Проверяем, не является ли это циклом загрузки из таблиц CDA9/CDB1
            byte[] chunk = ReadBytesAt(br, address, Math.Min(32, (int)(br.BaseStream.Length - address)));

            if (chunk.Length >= 8)
            {
                // Ищем паттерны загрузки из таблиц
                for (int i = 0; i < chunk.Length - 7; i++)
                {
                    // MOV AL, [BX+CDA9]
                    if (chunk[i] == 0x8A && chunk[i + 1] == 0x87 &&
                        chunk[i + 2] == 0xA9 && chunk[i + 3] == 0xCD)
                    {
                        return true;
                    }
                    // MOV BP, [BX+CDB1]
                    if (chunk[i] == 0x8B && chunk[i + 1] == 0xAF &&
                        chunk[i + 2] == 0xB1 && chunk[i + 3] == 0xCD)
                    {
                        return true;
                    }
                    // MOV AL, [BX+CDB1] (word as byte)
                    if (chunk[i] == 0x8A && chunk[i + 1] == 0x87 &&
                        chunk[i + 2] == 0xB1 && chunk[i + 3] == 0xCD)
                    {
                        return true;
                    }
                    // Проверяем также паттерны с инкрементом BX после загрузки
                    if (i + 4 < chunk.Length && chunk[i] == 0x43) // INC BX
                    {
                        // Если перед INC BX была загрузка из таблицы
                        for (int j = Math.Max(0, i - 8); j < i; j++)
                        {
                            if (j + 3 < chunk.Length)
                            {
                                if ((chunk[j] == 0x8A && chunk[j + 1] == 0x87 &&
                                     (chunk[j + 2] == 0xA9 || chunk[j + 2] == 0xB1) && chunk[j + 3] == 0xCD) ||
                                    (chunk[j] == 0x8B && chunk[j + 1] == 0xAF && chunk[j + 2] == 0xB1 && chunk[j + 3] == 0xCD))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Извлекает текст по адресу
        /// </summary>
        private string ExtractText(BinaryReader br, ushort textAddress)
        {
            try
            {
                if (!TryMapOverlayAddressToFileOffset(br, textAddress, out long fileOffset))
                    return $"Cannot locate text at 0x{textAddress:X4}";

                long originalPos = br.BaseStream.Position;
                br.BaseStream.Position = fileOffset;

                var bytes = new List<byte>();
                byte b;
                int maxLength = 250;

                while ((b = br.ReadByte()) != 0 && bytes.Count < maxLength)
                {
                    bytes.Add(b);
                }

                br.BaseStream.Position = originalPos;

                if (bytes.Count == 0) return "(empty string)";

                return DecodeText(bytes.ToArray());
            }
            catch (Exception ex)
            {
                return $"Error reading text: {ex.Message}";
            }
        }

        private bool TryMapOverlayAddressToFileOffset(BinaryReader br, ushort memAddr, out long fileOffset)
        {
            fileOffset = -1;

            if (br == null)
                return false;

            long textBaseOffset = memAddr - _config.TextBaseAddr;
            if (textBaseOffset >= 0 && textBaseOffset < br.BaseStream.Length)
            {
                fileOffset = textBaseOffset;
                return true;
            }

            if (memAddr >= 0xC972)
            {
                long overlayMappedOffset = (long)_config.StartAddress + (memAddr - 0xC972);
                if (overlayMappedOffset >= 0 && overlayMappedOffset < br.BaseStream.Length)
                {
                    fileOffset = overlayMappedOffset;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Декодирует текст из байтов
        /// </summary>
        private string DecodeText(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                if (b == 0x0D) sb.Append("\\r");
                else if (b == 0x0A) sb.Append("\\n");
                else if (b == 0x09) sb.Append("\\t");
                else if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
                else if (b == 0x22) sb.Append("\\\"");
                else if (b == 0x5C) sb.Append("\\\\");
                else sb.Append($"\\x{b:X2}");
            }
            return sb.ToString();
        }

        private bool TryParseImmediate8(X86Instruction insn, out byte value)
        {
            value = 0;
            var bytes = insn.Bytes;
            if (bytes == null || bytes.Length < 2)
                return false;

            if ((bytes[0] == 0x04 || bytes[0] == 0x14) && bytes.Length >= 2)
            {
                value = bytes[1];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Отслеживает операции с регистрами
        /// </summary>
        private bool TrackRegisterOperations(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, bool debugMode,
            PathAnalysisResult result, byte targetX, byte targetY,
            int callDepth, List<uint> pendingReturnAddresses)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;
            string mnemonicUpper = insn.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            string operandUpper = insn.Operand?.ToUpperInvariant() ?? string.Empty;
            uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

            if ((mnemonicUpper == "ADD" || mnemonicUpper == "ADC") && operandUpper.StartsWith("AL,"))
            {
                if (registerTracker.HasPendingExternalCallResult("AX") || registerTracker.IsRegisterExternallyDerived("AX"))
                {
                    registerTracker.MaterializePendingExternalCallResult("AX");
                    registerTracker.MarkRegisterAsExternallyDerived("AX");

                    if (debugMode)
                        AnalysisDebug.WriteLine("        Материализуем зависимость AX/AL от внешнего CALL из-за арифметики ADD/ADC");
                }
            }
            else if ((mnemonicUpper == "ADD" || mnemonicUpper == "ADC") &&
                operandUpper.StartsWith("AX,") &&
                (registerTracker.HasPendingExternalCallResult("AX") || registerTracker.IsRegisterExternallyDerived("AX")))
            {
                registerTracker.MaterializePendingExternalCallResult("AX");
                registerTracker.MarkRegisterAsExternallyDerived("AX");

                if (debugMode)
                    AnalysisDebug.WriteLine("        Материализуем зависимость AX/AL от внешнего CALL из-за арифметики ADD/ADC");
            }

            // Инструкции, меняющие флаги, но не моделируемые точно, делают состояние флагов недостоверным
            if (instructionBytes.Length >= 1)
            {
                byte opcode = instructionBytes[0];
                bool invalidateFlags = false;

                if ((opcode >= 0x20 && opcode <= 0x23) || opcode == 0x24 || opcode == 0x25) // AND
                    invalidateFlags = true;
                else if ((opcode >= 0x08 && opcode <= 0x0D)) // OR
                    invalidateFlags = true;
                else if ((opcode >= 0x30 && opcode <= 0x35)) // XOR
                    invalidateFlags = true;
                else if (opcode == 0x84 || opcode == 0x85 || opcode == 0xA8 || opcode == 0xA9) // TEST
                    invalidateFlags = true;
                else if ((opcode >= 0x40 && opcode <= 0x4F)) // INC/DEC reg16
                    invalidateFlags = true;
                else if (opcode == 0xD0 || opcode == 0xD1 || opcode == 0xD2 || opcode == 0xD3) // shifts/rotates
                    invalidateFlags = true;
                else if (opcode == 0xFE || opcode == 0xFF) // INC/DEC r/m
                    invalidateFlags = true;
                else if (opcode == 0x80 || opcode == 0x81 || opcode == 0x83)
                {
                    if (instructionBytes.Length >= 2)
                    {
                        byte modRm = instructionBytes[1];
                        byte operation = (byte)((modRm >> 3) & 0x07);
                        // Невалидируем только для точно поддержанных нами ADD/CMP
                        if (!(opcode == 0x80 && operation == 0x07 && ((modRm >> 6) & 0x03) == 0x03) &&
                            !(opcode == 0x04))
                        {
                            invalidateFlags = true;
                        }
                    }
                }

                if (invalidateFlags)
                    InvalidateFlags(registerTracker);
            }

            TrackPartyArithmeticInstruction(insn, registerTracker, result);

            // Сохраняем изменения в эмулируемую память (runtime state)
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA3)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                if (registerTracker.TryGetRegisterValue("AX", out ushort axValue))
                {
                    ApplyTrackedWordWrite(memAddr, axValue, result, targetX, targetY, insn, debugMode, "MOV [moffs16], AX");
                }

                if (registerTracker.TryGetPartyMemberBase("AX", out var axPartyMember))
                    ApplyTrackedPartyPointerWrite(memAddr, axPartyMember);
                else
                    ApplyTrackedPartyPointerWrite(memAddr, null);
            }
            else if (instructionBytes.Length >= 4 && instructionBytes[0] == 0xC7)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte regField = (byte)((modRm >> 3) & 0x07);

                if (mod != 0x03 && regField == 0 &&
                    TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out int decodedLength, out string eaText) &&
                    instructionBytes.Length >= decodedLength + 2)
                {
                    ushort immValue = BitConverter.ToUInt16(instructionBytes, decodedLength);
                    ApplyTrackedWordWrite(memAddr, immValue, result, targetX, targetY, insn, debugMode, $"MOV word ptr {eaText}, 0x{immValue:X4}");
                }
            }
            else if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA2)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 1);

                // Если в AL сейчас не точное значение, а диапазон, нельзя материализовывать
                // его в память как конкретный байт. Иначе последующий CMP BL,[3C1D] увидит
                // ложный точный предел и логика random count сломается.
                if (memAddr == 0xCC3E && registerTracker.TryGetRegisterRange("AL", out var splitRange) && splitRange != null && !splitRange.IsExact &&
                    splitRange.Min <= splitRange.Max && (splitRange.Max - splitRange.Min) <= 8)
                {
                    for (int candidate = splitRange.Min; candidate <= splitRange.Max; candidate++)
                    {
                        var splitTracker = registerTracker.Clone();
                        splitTracker.TrackPartialRegisterOperation("AX", "AL", (byte)candidate, address, $"MOV AL, 0x{candidate:X2} (split)");
                        var splitMemory = new Dictionary<ushort, byte>(_emulatedMemory8);
                        splitMemory[memAddr] = (byte)candidate;

                        result.AlternativePaths.Add(new AlternativePath
                        {
                            Address = address,
                            TargetAddress = nextAddress,
                            Condition = $"SPLIT [0x{memAddr:X4}] = 0x{candidate:X2}",
                            Analyzed = false,
                            PathNumber = result.AlternativePaths.Count + 1,
                            RegisterState = splitTracker,
                            CompareRegister = registerTracker.LastFlagsRegister,
                            CompareValue = registerTracker.LastCompareImmediate,
                            IsInputChoiceBranch = false,
                            ProbabilityNumerator = 1,
                            ProbabilityDenominator = 1,
                            CallDepth = callDepth,
                            PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            EmulatedMemory8 = splitMemory,
                            EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone())
                        });
                    }

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Развилка по диапазону AL для записи в [0x{memAddr:X4}]: {splitRange.Min:X2}..{splitRange.Max:X2}");

                    result.IsTerminated = true;
                    result.HasSignificantCode = true;
                    return true;
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && !alRange.IsExact)
                {
                    if (memAddr == 0x3C38)
                    {
                        result.TeleportTargetX = null;
                        result.TeleportTargetXRange = new ValueRange8(alRange.Min, alRange.Max);
                        if (!result.TeleportTargetY.HasValue && result.TeleportTargetYRange == null)
                            result.TeleportTargetY = targetY;
                        result.HasSignificantCode = true;
                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Обнаружен случайный телепорт по X: диапазон X = {alRange.Min}..{alRange.Max}, Y = {(result.TeleportTargetY.HasValue ? result.TeleportTargetY.Value.ToString() : result.TeleportTargetYRange != null ? $"{result.TeleportTargetYRange.Min}..{result.TeleportTargetYRange.Max}" : "?")} (инструкция 0x{insn.Address:X4})");
                    }
                    else if (memAddr == 0x3C39)
                    {
                        result.TeleportTargetY = null;
                        result.TeleportTargetYRange = new ValueRange8(alRange.Min, alRange.Max);
                        if (!result.TeleportTargetX.HasValue && result.TeleportTargetXRange == null)
                            result.TeleportTargetX = targetX;
                        result.HasSignificantCode = true;
                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Обнаружен случайный телепорт по Y: X = {(result.TeleportTargetX.HasValue ? result.TeleportTargetX.Value.ToString() : result.TeleportTargetXRange != null ? $"{result.TeleportTargetXRange.Min}..{result.TeleportTargetXRange.Max}" : "?")}, диапазон Y = {alRange.Min}..{alRange.Max} (инструкция 0x{insn.Address:X4})");
                    }
                    else if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"        Не сохраняем точное значение AL в эмулируемую память [0x{memAddr:X4}], так как AL имеет диапазон {alRange.Min}-{alRange.Max}");
                    }
                }
                else if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    ApplyTrackedByteWrite(memAddr, alValue, result, targetX, targetY, insn, debugMode, "MOV [moffs8], AL");
                    if (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteExact))
                        ApplyTrackedPartyPointerByteWrite(memAddr, alPointerByteExact);
                }
                else if (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteSemanticOnly))
                {
                    _emulatedPartyPointers.Remove(memAddr);
                    _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));
                    ApplyTrackedPartyPointerByteWrite(memAddr, alPointerByteSemanticOnly);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        MOV [moffs8], AL: перенесли байтовую семантику указателя члена партии в [0x{memAddr:X4}] без точного значения");
                }
            }
            else if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xC6)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte regField = (byte)((modRm >> 3) & 0x07);

                if (mod != 0x03 && regField == 0)
                {
                    bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out int decodedLength, out string eaText);
                    if (!hasExactMemAddr)
                        TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out decodedLength, out eaText);

                    if (decodedLength > 0 && instructionBytes.Length > decodedLength)
                    {
                        byte immValue = instructionBytes[decodedLength];
                        PartyFieldReference partyFieldRef = null;
                        bool hasPartyFieldRef = TryResolvePartyFieldAccess(instructionBytes, registerTracker, hasExactMemAddr ? memAddr : (ushort?)null, out partyFieldRef);

                        if (hasPartyFieldRef)
                        {
                            RegisterPartyFieldWrite(result, partyFieldRef, registerTracker, address, debugMode, immValue);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Распознана прямая запись поля персонажа {partyFieldRef.Field} = 0x{immValue:X2} через {eaText}");
                        }
                        else if (hasExactMemAddr)
                        {
                            ApplyTrackedByteWrite(memAddr, immValue, result, targetX, targetY, insn, debugMode, $"MOV byte ptr {eaText}, 0x{immValue:X2}");
                        }
                    }
                }
            }
            else if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x88)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (mod == 0x03)
                {
                    if (reg < regNames8.Length && rm < regNames8.Length &&
                        registerTracker.TryGetByteRegisterValue(regNames8[reg], out byte srcValue))
                    {
                        string dstReg = regNames8[rm];
                        string dstFullReg = GetFullRegisterNameForByteRegister(dstReg);

                        if (!string.IsNullOrEmpty(dstFullReg))
                        {
                            registerTracker.TrackPartialRegisterOperation(
                                dstFullReg,
                                dstReg,
                                srcValue,
                                address,
                                $"MOV {dstReg}, {regNames8[reg]}"
                            );

                            if (registerTracker.TryGetPartyFieldValue(regNames8[reg], out var copiedPartyField))
                                registerTracker.SetPartyFieldValue(dstReg, copiedPartyField);

                            if (registerTracker.TryGetPartyPointerByteValue(regNames8[reg], out var copiedPointerByte))
                                registerTracker.SetPartyPointerByteValue(dstReg, copiedPointerByte);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {regNames8[reg]} = 0x{srcValue:X2}");
                        }
                    }
                    else if (reg < regNames8.Length && rm < regNames8.Length)
                    {
                        string dstReg = regNames8[rm];
                        registerTracker.TryGetPartyFieldValue(regNames8[reg], out var semanticField88);
                        registerTracker.TryGetPartyPointerByteValue(regNames8[reg], out var semanticPointerByte88);

                        if (semanticField88 != null || semanticPointerByte88 != null)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);

                            if (semanticField88 != null)
                                registerTracker.SetPartyFieldValue(dstReg, semanticField88);

                            if (semanticPointerByte88 != null)
                                registerTracker.SetPartyPointerByteValue(dstReg, semanticPointerByte88);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование семантики {dstReg} <- {regNames8[reg]} без точного значения байта");
                        }
                    }
                }
                else
                {
                    bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                    if (!hasExactMemAddr)
                        TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                    PartyFieldReference partyFieldRef = null;
                    bool hasPartyFieldRef = TryResolvePartyFieldAccess(instructionBytes, registerTracker, hasExactMemAddr ? memAddr : (ushort?)null, out partyFieldRef);
                    PartyFieldReference sourcePartyFieldValue = null;
                    if (reg < regNames8.Length)
                        registerTracker.TryGetPartyFieldValue(regNames8[reg], out sourcePartyFieldValue);
                    if (TryGetReg8ValueFromModRmRegField(modRm, registerTracker, out byte regValue, out string regName))
                    {
                        if (hasPartyFieldRef)
                        {
                            RegisterPartyFieldWrite(result, partyFieldRef, registerTracker, address, debugMode, regValue, sourceFieldValue: sourcePartyFieldValue);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Распознана запись поля персонажа {partyFieldRef.Field} из {regName} через {eaText} (сырую эмулируемую память не обновляем)");
                        }
                        else if (hasExactMemAddr)
                        {
                            ApplyTrackedByteWrite(memAddr, regValue, result, targetX, targetY, insn, debugMode, $"MOV byte ptr {eaText}, {regName}");

                            if (registerTracker.TryGetPartyPointerByteValue(regName, out var pointerByte))
                                ApplyTrackedPartyPointerByteWrite(memAddr, pointerByte);
                        }
                    }
                    else if (hasPartyFieldRef)
                    {
                        RegisterPartyFieldWrite(result, partyFieldRef, registerTracker, address, debugMode, sourceFieldValue: sourcePartyFieldValue);

                        if (debugMode)
                        {
                            string detail = hasExactMemAddr
                                ? "без точного значения источника"
                                : "без точного абсолютного адреса";
                            AnalysisDebug.WriteLine($"        Распознана запись поля персонажа {partyFieldRef.Field} через {eaText} {detail}");
                        }
                    }
                    else if (hasExactMemAddr &&
                             TryGetReg8ValueFromModRmRegField(modRm, registerTracker, out _, out string regNameWithoutValue) &&
                             registerTracker.TryGetPartyPointerByteValue(regNameWithoutValue, out var pointerByteWithoutValue))
                    {
                        _emulatedPartyPointers.Remove(memAddr);
                        _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));
                        ApplyTrackedPartyPointerByteWrite(memAddr, pointerByteWithoutValue);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Перенесли байтовую семантику указателя члена партии в {eaText} из {regNameWithoutValue} без точного значения");
                    }
                }
            }
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0xFE &&
                     (instructionBytes[1] == 0x06 || instructionBytes[1] == 0x0E))
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                int delta = instructionBytes[1] == 0x06 ? 1 : -1;
                if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte currentValue))
                {
                    byte newValue = unchecked((byte)(currentValue + delta));
                    string operation = delta > 0 ? "INC" : "DEC";
                    ApplyTrackedByteWrite(memAddr, newValue, result, targetX, targetY, insn, debugMode, $"{operation} byte ptr [disp16]");
                }
                else if (debugMode)
                {
                    AnalysisDebug.WriteLine($"        Не удалось определить текущее значение для {(delta > 0 ? "INC" : "DEC")} [0x{memAddr:X4}]");
                }
            }


            // INC/DEC reg16 (в частности важно для DEC BP перед доступом к HP low)
            if (instructionBytes.Length >= 1 && instructionBytes[0] >= 0x40 && instructionBytes[0] <= 0x4F)
            {
                byte opcode = instructionBytes[0];
                bool isInc = opcode < 0x48;
                int regIndex = isInc ? opcode - 0x40 : opcode - 0x48;
                string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (regIndex >= 0 && regIndex < regNames16.Length)
                {
                    string regName = regNames16[regIndex];
                    if (registerTracker.TryGetRegisterValue(regName, out ushort regValue))
                    {
                        ushort newValue = unchecked((ushort)(regValue + (isInc ? 1 : -1)));
                        registerTracker.SetRegisterValue(regName, newValue, address, $"{(isInc ? "INC" : "DEC")} {regName}");

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {(isInc ? "Увеличили" : "Уменьшили")} {regName} -> 0x{newValue:X4}");
                    }
                }
            }

            // Загрузка непосредственных значений в регистры
            if (instructionBytes.Length >= 3 && (instructionBytes[0] & 0xF8) == 0xB8)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                byte opcode = instructionBytes[0];
                byte regIndex = (byte)(opcode - 0xB8);

                string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
                if (regIndex < regNames.Length)
                {
                    registerTracker.SetRegisterValue(regNames[regIndex], immediateValue, address,
                        $"MOV {regNames[regIndex]}, 0x{immediateValue:X4}");
                }
            }
            else if (instructionBytes.Length >= 2 && (instructionBytes[0] & 0xF8) == 0xB0)
            {
                byte immediateValue = instructionBytes[1];
                byte opcode = instructionBytes[0];
                byte regIndex = (byte)(opcode - 0xB0);

                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                if (regIndex < regNames8.Length)
                {
                    string regName = regNames8[regIndex];
                    string fullReg = regName switch
                    {
                        "AL" or "AH" => "AX",
                        "CL" or "CH" => "CX",
                        "DL" or "DH" => "DX",
                        "BL" or "BH" => "BX",
                        _ => ""
                    };

                    if (!string.IsNullOrEmpty(fullReg))
                    {
                        registerTracker.TrackPartialRegisterOperation(fullReg, regName, immediateValue,
                            address, $"MOV {regName}, 0x{immediateValue:X2}");
                    }
                }
            }
            // MOV AL, [moffs8]  (opcode A0)
            else if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA0)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                bool hasPointerByteSemanticA0 = TryResolveTrackedPartyPointerByte(memAddr, out var pointerByteSemanticA0);
                if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte value))
                {
                    registerTracker.SetByteRegisterValueWithSource(
                        "AX",
                        "AL",
                        value,
                        memAddr,
                        address,
                        $"MOV AL, [0x{memAddr:X4}]"
                    );

                    if (hasPointerByteSemanticA0)
                        registerTracker.SetPartyPointerByteValue("AL", pointerByteSemanticA0);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Загрузили AL из {(_emulatedMemory8.ContainsKey(memAddr) ? "эмулируемой памяти" : "файла")} [0x{memAddr:X4}] = 0x{value:X2}");
                }
                else if (hasPointerByteSemanticA0)
                {
                    registerTracker.ClearConcreteByteRegisterValueKeepSemantic("AL");
                    registerTracker.ClearPartyFieldValue("AL");
                    registerTracker.ClearPartyFieldValue("AX");
                    registerTracker.SetPartyPointerByteValue("AL", pointerByteSemanticA0);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознана загрузка байта указателя на члена партии в AL из [0x{memAddr:X4}] без точного значения");
                }
                else if (memAddr == PARTY_COUNT_ADDRESS)
                {
                    registerTracker.ClearConcreteByteRegisterValueKeepSemantic("AL");
                    registerTracker.ClearPartyFieldValue("AL");
                    registerTracker.ClearPartyFieldValue("AX");
                    registerTracker.ClearPartyPointerByteValue("AL");
                    registerTracker.ClearPartyMemberBase("AX");
                    registerTracker.SetRegisterRange("AL", 1, PARTY_MEMBER_COUNT, RegisterValueDistribution.Unknown);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознано текущее число членов партии в AL как диапазон 1..{PARTY_MEMBER_COUNT}");
                }
                else if (debugMode)
                {
                    AnalysisDebug.WriteLine($"        Не удалось загрузить AL из [0x{memAddr:X4}] - адрес вне диапазона файла и эмулируемой памяти");
                }
            }
            // MOV reg8, r/m8
            else if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x8A)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (mod == 0x03)
                {
                    if (reg < regNames8.Length && rm < regNames8.Length &&
                        registerTracker.TryGetByteRegisterValue(regNames8[rm], out byte srcValue))
                    {
                        string dstReg = regNames8[reg];
                        string fullReg = GetFullRegisterNameForByteRegister(dstReg);

                        if (!string.IsNullOrEmpty(fullReg))
                        {
                            registerTracker.TrackPartialRegisterOperation(
                                fullReg,
                                dstReg,
                                srcValue,
                                address,
                                $"MOV {dstReg}, {regNames8[rm]}"
                            );

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {regNames8[rm]} = 0x{srcValue:X2}");
                        }
                    }
                    else if (reg < regNames8.Length && rm < regNames8.Length)
                    {
                        string dstReg = regNames8[reg];
                        string srcReg = regNames8[rm];
                        string fullReg = GetFullRegisterNameForByteRegister(dstReg);
                        registerTracker.TryGetRegisterRange(srcReg, out var copiedRange8A);
                        registerTracker.TryGetRegisterDistribution(srcReg, out var copiedDistribution8A);
                        registerTracker.TryGetPartyFieldValue(regNames8[rm], out var semanticField8A);
                        registerTracker.TryGetPartyPointerByteValue(regNames8[rm], out var semanticPointerByte8A);

                        if (copiedRange8A != null)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);
                            registerTracker.ClearPartyFieldValue(dstReg);
                            registerTracker.ClearPartyPointerByteValue(dstReg);
                            if (!string.IsNullOrEmpty(fullReg))
                                registerTracker.ClearPartyMemberBase(fullReg);
                            registerTracker.SetRegisterRange(dstReg, copiedRange8A.Min, copiedRange8A.Max, copiedDistribution8A);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование диапазона {dstReg} <- {srcReg} = {copiedRange8A.Min:X2}..{copiedRange8A.Max:X2}");
                        }
                        else if (semanticField8A != null || semanticPointerByte8A != null)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);

                            if (semanticField8A != null)
                                registerTracker.SetPartyFieldValue(dstReg, semanticField8A);

                            if (semanticPointerByte8A != null)
                                registerTracker.SetPartyPointerByteValue(dstReg, semanticPointerByte8A);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование семантики {dstReg} <- {regNames8[rm]} без точного значения байта");
                        }
                        else
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);
                            registerTracker.ClearPartyFieldValue(dstReg);
                            registerTracker.ClearPartyPointerByteValue(dstReg);
                            if (!string.IsNullOrEmpty(fullReg))
                                registerTracker.ClearPartyMemberBase(fullReg);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Источник {srcReg} неизвестен, сбрасываем точное значение {dstReg}");
                        }
                    }
                }
                else
                {
                    bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                    if (!hasExactMemAddr)
                        TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                    PartyFieldReference partyFieldRef = null;
                    bool hasPartyFieldRef = TryResolvePartyFieldAccess(instructionBytes, registerTracker, hasExactMemAddr ? memAddr : (ushort?)null, out partyFieldRef);
                    PartyPointerByteReference pointerByteSemantic8A = null;
                    bool hasPointerByteSemantic8A = hasExactMemAddr &&
                        TryResolveTrackedPartyPointerByte(memAddr, out pointerByteSemantic8A);

                    if (hasExactMemAddr && TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte value))
                    {
                        if (reg < regNames8.Length)
                        {
                            string regName = regNames8[reg];
                            string fullReg = GetFullRegisterNameForByteRegister(regName);

                            if (!string.IsNullOrEmpty(fullReg))
                            {
                                registerTracker.SetByteRegisterValueWithSource(
                                    fullReg,
                                    regName,
                                    value,
                                    memAddr,
                                    address,
                                    $"MOV {regName}, byte ptr {eaText}"
                                );

                                if (hasPartyFieldRef)
                                {
                                    registerTracker.SetPartyFieldValue(regName, partyFieldRef);
                                    registerTracker.ClearPartyPointerByteValue(regName);
                                    RegisterPartyFieldRead(result, partyFieldRef, registerTracker, address, value);
                                }
                                else if (hasPointerByteSemantic8A)
                                {
                                    registerTracker.ClearPartyFieldValue(regName);
                                    registerTracker.SetPartyPointerByteValue(regName, pointerByteSemantic8A);
                                }

                                if (debugMode)
                                    AnalysisDebug.WriteLine($"        Загрузили {regName} из {(_emulatedMemory8.ContainsKey(memAddr) ? "эмулируемой памяти" : "файла")} {eaText} -> [0x{memAddr:X4}] = 0x{value:X2}");
                            }
                        }
                    }
                    else if (hasPartyFieldRef && reg < regNames8.Length)
                    {
                        string regName = regNames8[reg];
                        registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
                        registerTracker.SetPartyFieldValue(regName, partyFieldRef);
                        registerTracker.ClearPartyPointerByteValue(regName);
                        RegisterPartyFieldRead(result, partyFieldRef, registerTracker, address);

                        if (debugMode)
                        {
                            string detail = hasExactMemAddr
                                ? "без точного байтового значения"
                                : "без точного абсолютного адреса";
                            AnalysisDebug.WriteLine($"        Распознано чтение поля персонажа {partyFieldRef.Field} из {eaText} {detail}; точное значение {regName} сброшено");
                        }
                    }
                    else if (hasPointerByteSemantic8A && reg < regNames8.Length)
                    {
                        string regName = regNames8[reg];
                        registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
                        registerTracker.ClearPartyFieldValue(regName);
                        registerTracker.SetPartyPointerByteValue(regName, pointerByteSemantic8A);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Распознан байт указателя на члена партии в {regName} из {eaText} без точного значения");
                    }
                    else if (hasExactMemAddr && memAddr == PARTY_COUNT_ADDRESS && reg < regNames8.Length)
                    {
                        string regName = regNames8[reg];
                        string fullReg = GetFullRegisterNameForByteRegister(regName);
                        registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
                        registerTracker.ClearPartyFieldValue(regName);
                        registerTracker.ClearPartyPointerByteValue(regName);
                        if (!string.IsNullOrEmpty(fullReg))
                            registerTracker.ClearPartyMemberBase(fullReg);
                        registerTracker.SetRegisterRange(regName, 1, PARTY_MEMBER_COUNT, RegisterValueDistribution.Unknown);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Распознано текущее число членов партии в {regName} как диапазон 1..{PARTY_MEMBER_COUNT}");
                    }
                    else if (hasExactMemAddr && debugMode)
                    {
                        AnalysisDebug.WriteLine($"        Не удалось загрузить reg8 из {eaText} -> [0x{memAddr:X4}] - адрес вне диапазона файла и эмулируемой памяти");
                    }
                }
            }
            // MOV reg16, r/m16
            else if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x8B)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (mod == 0x03)
                {
                    if (reg < regNames16.Length && rm < regNames16.Length &&
                        registerTracker.TryGetRegisterValue(regNames16[rm], out ushort srcValue))
                    {
                        string dstReg = regNames16[reg];
                        registerTracker.SetRegisterValue(dstReg, srcValue, address, $"MOV {dstReg}, {regNames16[rm]}");
                        if (registerTracker.TryGetPartyMemberBase(regNames16[rm], out var copiedPartyMember))
                            registerTracker.SetPartyMemberBase(dstReg, copiedPartyMember);
                        else
                            registerTracker.ClearPartyMemberBase(dstReg);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {regNames16[rm]} = 0x{srcValue:X4}");
                    }
                    else if (reg < regNames16.Length && rm < regNames16.Length &&
                        registerTracker.TryGetPartyMemberBase(regNames16[rm], out var copiedPartyMember))
                    {
                        string dstReg = regNames16[reg];
                        registerTracker.InvalidateRegister(dstReg);
                        registerTracker.SetPartyMemberBase(dstReg, copiedPartyMember);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование семантики указателя {dstReg} <- {regNames16[rm]} без точного значения");
                    }
                }
                else
                {
                    bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                    if (!hasExactMemAddr)
                        TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                    bool loadedAnySemantic = false;
                    if (reg < regNames16.Length)
                    {
                        string regName = regNames16[reg];

                        if (hasExactMemAddr &&
                            TryResolveTrackedWordValue(br, memAddr, result, targetX, targetY, out ushort value))
                        {
                            registerTracker.SetRegisterValue(regName, value, address, $"MOV {regName}, word ptr {eaText}");

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Загрузили {regName} из {(IsTrackedWordInEmulatedMemory(memAddr) ? "эмулируемой памяти" : "файла")} {eaText} -> [0x{memAddr:X4}] = 0x{value:X4}");
                        }

                        if (hasExactMemAddr && TryResolveTrackedPartyPointer(memAddr, out var partyPtr))
                        {
                            if (registerTracker.TryGetRegisterValue(regName, out ushort structureAddr))
                                partyPtr.StructureAddress = structureAddr;

                            registerTracker.SetPartyMemberBase(regName, partyPtr);
                            loadedAnySemantic = true;
                            if (debugMode)
                            {
                                string semanticSource = memAddr >= PARTY_POINTER_TABLE && memAddr < PARTY_POINTER_TABLE + PARTY_MEMBER_COUNT * 2
                                    ? "таблицы членов партии"
                                    : "эмулируемой памяти";
                                AnalysisDebug.WriteLine($"        Восстановлен указатель на члена партии в {regName} из {semanticSource} [0x{memAddr:X4}]");
                            }
                        }
                        else
                        {
                            registerTracker.ClearPartyMemberBase(regName);
                        }

                        if (!loadedAnySemantic &&
                            !hasExactMemAddr &&
                            TryResolveDynamicPartyPointerFromMemoryOperand(instructionBytes, registerTracker, out var rangedPartyPtr))
                        {
                            registerTracker.InvalidateRegister(regName);
                            registerTracker.SetPartyMemberBase(regName, rangedPartyPtr);
                            loadedAnySemantic = true;

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Восстановлен {rangedPartyPtr.SelectionKind.ToString().ToLowerInvariant()} указатель на члена партии в {regName} из таблицы членов партии по диапазону смещения");
                        }
                    }

                    if (!loadedAnySemantic &&
                        (!hasExactMemAddr || !TryResolveTrackedWordValue(br, memAddr, result, targetX, targetY, out _ )) &&
                        debugMode)
                    {
                        string addrText = hasExactMemAddr ? $" -> [0x{memAddr:X4}]" : string.Empty;
                        AnalysisDebug.WriteLine($"        Не удалось загрузить reg16 из {eaText}{addrText} - адрес вне диапазона файла и эмулируемой памяти");
                    }
                }
            }
            // MOV r/m16, reg16
            else if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x89)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (mod == 0x03)
                {
                    if (reg < regNames16.Length && rm < regNames16.Length &&
                        registerTracker.TryGetRegisterValue(regNames16[reg], out ushort srcValue))
                    {
                        string dstReg = regNames16[rm];
                        registerTracker.SetRegisterValue(dstReg, srcValue, address, $"MOV {dstReg}, {regNames16[reg]}");

                        if (registerTracker.TryGetPartyMemberBase(regNames16[reg], out var copiedPartyMember))
                            registerTracker.SetPartyMemberBase(dstReg, copiedPartyMember);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {regNames16[reg]} = 0x{srcValue:X4}");
                    }
                    else if (reg < regNames16.Length && rm < regNames16.Length &&
                        registerTracker.TryGetPartyMemberBase(regNames16[reg], out var copiedPartyMember))
                    {
                        string dstReg = regNames16[rm];
                        registerTracker.InvalidateRegister(dstReg);
                        registerTracker.SetPartyMemberBase(dstReg, copiedPartyMember);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование семантики указателя {dstReg} <- {regNames16[reg]} без точного значения");
                    }
                }
                else if (TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText))
                {
                    if (TryGetReg16ValueFromModRmRegField(modRm, registerTracker, out ushort regValue, out string regName))
                    {
                        ApplyTrackedWordWrite(memAddr, regValue, result, targetX, targetY, insn, debugMode, $"MOV word ptr {eaText}, {regName}");
                    }

                    string[] regNames16Local = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
                    byte regFieldLocal = (byte)((modRm >> 3) & 0x07);
                    if (regFieldLocal < regNames16Local.Length && registerTracker.TryGetPartyMemberBase(regNames16Local[regFieldLocal], out var partyMemberSrc))
                        ApplyTrackedPartyPointerWrite(memAddr, partyMemberSrc);
                    else
                        ApplyTrackedPartyPointerWrite(memAddr, null);
                }
            }

            // Специфические загрузки для конкретных регистров
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBB)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                registerTracker.SetRegisterValue("BX", immediateValue, address,
                    $"MOV BX, 0x{immediateValue:X4}");
            }

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBD)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                registerTracker.SetRegisterValue("BP", immediateValue, address,
                    $"MOV BP, 0x{immediateValue:X4}");
            }

            // Частичные загрузки в младшие/старшие байты регистров
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB3)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("BX", "BL", immediateValue,
                    address, $"MOV BL, 0x{immediateValue:X2}");
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB7)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("BX", "BH", immediateValue,
                    address, $"MOV BH, 0x{immediateValue:X2}");
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB1)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("CX", "CL", immediateValue,
                    address, $"MOV CL, 0x{immediateValue:X2}");
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB5)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("CX", "CH", immediateValue,
                    address, $"MOV CH, 0x{immediateValue:X2}");
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB0)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("AX", "AL", immediateValue,
                    address, $"MOV AL, 0x{immediateValue:X2}");
            }

            if (instructionBytes.Length >= 2 &&
                (instructionBytes[0] == 0x0C || instructionBytes[0] == 0x24 || instructionBytes[0] == 0x34))
            {
                PartyEffectOperation accumulatorOperation = instructionBytes[0] switch
                {
                    0x0C => PartyEffectOperation.BitSet,
                    0x24 => PartyEffectOperation.BitClear,
                    0x34 => PartyEffectOperation.BitToggle,
                    _ => PartyEffectOperation.Unknown
                };

                TrackPartyFieldRegisterImmediateTransform(
                    result,
                    registerTracker,
                    "AL",
                    accumulatorOperation,
                    instructionBytes[1],
                    address,
                    debugMode);
            }

            if (instructionBytes.Length >= 2 &&
                (instructionBytes[0] == 0x0A || instructionBytes[0] == 0x22 || instructionBytes[0] == 0x32))
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (mod != 0x03 && reg < regNames8.Length)
                {
                    PartyEffectOperation registerOperation = instructionBytes[0] switch
                    {
                        0x0A => PartyEffectOperation.BitSet,
                        0x22 => PartyEffectOperation.BitClear,
                        0x32 => PartyEffectOperation.BitToggle,
                        _ => PartyEffectOperation.Unknown
                    };

                    if (registerOperation != PartyEffectOperation.Unknown)
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                        if (!hasExactMemAddr)
                            TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                        byte? sourceValue = null;
                        if (hasExactMemAddr &&
                            TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte resolvedByteValue))
                        {
                            sourceValue = resolvedByteValue;
                        }

                        string mnemonic = registerOperation switch
                        {
                            PartyEffectOperation.BitSet => "OR",
                            PartyEffectOperation.BitClear => "AND",
                            PartyEffectOperation.BitToggle => "XOR",
                            _ => "LOGIC"
                        };

                        TrackPartyFieldRegisterMemoryTransform(
                            registerTracker,
                            regNames8[reg],
                            registerOperation,
                            sourceValue,
                            address,
                            mnemonic,
                            string.IsNullOrWhiteSpace(eaText) ? "r/m8" : eaText,
                            debugMode);
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xD0)
            {
                byte modRm = instructionBytes[1];
                byte mode = (byte)((modRm >> 6) & 0x03);
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (mode == 0x03 && rm < regNames8.Length && registerTracker.TryGetByteRegisterValue(regNames8[rm], out byte oldValue))
                {
                    string regName = regNames8[rm];
                    string fullReg = GetFullRegisterNameForByteRegister(regName);
                    byte newValue = oldValue;
                    bool handled = true;

                    switch (operation)
                    {
                        case 4: // SHL/SAL r/m8,1
                            newValue = (byte)(oldValue << 1);
                            registerTracker.CarryFlag = (oldValue & 0x80) != 0;
                            registerTracker.ZeroFlag = newValue == 0;
                            registerTracker.SignFlag = (newValue & 0x80) != 0;
                            registerTracker.OverflowFlag = ((oldValue ^ newValue) & 0x80) != 0;
                            registerTracker.FlagsKnown = true;
                            break;
                        case 5: // SHR r/m8,1
                            newValue = (byte)(oldValue >> 1);
                            registerTracker.CarryFlag = (oldValue & 0x01) != 0;
                            registerTracker.ZeroFlag = newValue == 0;
                            registerTracker.SignFlag = false;
                            registerTracker.OverflowFlag = (oldValue & 0x80) != 0;
                            registerTracker.FlagsKnown = true;
                            break;
                        case 3: // RCR r/m8,1
                            {
                                int carryIn = registerTracker.FlagsKnown && registerTracker.CarryFlag ? 0x80 : 0x00;
                                bool newCarry = (oldValue & 0x01) != 0;
                                newValue = (byte)((oldValue >> 1) | carryIn);
                                registerTracker.CarryFlag = newCarry;
                                registerTracker.ZeroFlag = newValue == 0;
                                registerTracker.SignFlag = (newValue & 0x80) != 0;
                                registerTracker.OverflowFlag = ((newValue ^ (newValue << 1)) & 0x80) != 0;
                                registerTracker.FlagsKnown = true;
                            }
                            break;
                        default:
                            handled = false;
                            break;
                    }

                    if (handled && !string.IsNullOrEmpty(fullReg))
                    {
                        registerTracker.TrackPartialRegisterOperation(fullReg, regName, newValue, address, $"{mnemonicUpper} {regName}, 1");
                    }
                }
                else if (mode == 0x03 && rm < regNames8.Length &&
                         registerTracker.TryGetRegisterRange(regNames8[rm], out var oldRange) &&
                         oldRange != null)
                {
                    string regName = regNames8[rm];
                    registerTracker.TryGetRegisterDistribution(regName, out var rangeDistribution);
                    bool handled = true;
                    int newMin = oldRange.Min;
                    int newMax = oldRange.Max;

                    switch (operation)
                    {
                        case 4: // SHL/SAL r/m8,1
                            newMin = Math.Min(0xFF, oldRange.Min << 1);
                            newMax = Math.Min(0xFF, oldRange.Max << 1);
                            break;
                        case 5: // SHR r/m8,1
                            newMin = oldRange.Min >> 1;
                            newMax = oldRange.Max >> 1;
                            break;
                        default:
                            handled = false;
                            break;
                    }

                    if (handled)
                    {
                        registerTracker.SetRegisterRange(regName, (byte)newMin, (byte)newMax, rangeDistribution);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {mnemonicUpper} {regName}, 1: диапазон {oldRange.Min}-{oldRange.Max} -> {newMin}-{newMax}");
                    }
                }
            }

            // Арифметические операции
            if (instructionBytes.Length >= 2 &&
                (instructionBytes[0] == 0x04 || instructionBytes[0] == 0x14 || instructionBytes[0] == 0x2C || instructionBytes[0] == 0x1C))
            {
                byte opcode = instructionBytes[0];
                byte immediateValue = instructionBytes[1];
                bool isAdd = opcode == 0x04 || opcode == 0x14;
                bool usesCarry = opcode == 0x14 || opcode == 0x1C;
                int carryIn = usesCarry && registerTracker.FlagsKnown && registerTracker.CarryFlag ? 1 : 0;
                string mnemonic = opcode switch
                {
                    0x04 => "ADD",
                    0x14 => "ADC",
                    0x2C => "SUB",
                    0x1C => "SBB",
                    _ => "ALU"
                };

                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte newValue;
                    if (isAdd)
                    {
                        newValue = (byte)(alValue + immediateValue + carryIn);
                        registerTracker.TrackPartialRegisterOperation("AX", "AL", newValue, address, $"{mnemonic} AL, 0x{immediateValue:X2}");
                        SetArithmeticFlagsForAdd8(registerTracker, alValue, (byte)(immediateValue + carryIn), newValue);
                    }
                    else
                    {
                        newValue = (byte)(alValue - immediateValue - carryIn);
                        registerTracker.TrackPartialRegisterOperation("AX", "AL", newValue, address, $"{mnemonic} AL, 0x{immediateValue:X2}");
                        SetArithmeticFlagsForSub8(registerTracker, alValue, (byte)(immediateValue + carryIn), newValue);
                    }
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && alRange != null)
                {
                    if (isAdd)
                    {
                        int newMin = Math.Min(0xFF, alRange.Min + immediateValue + carryIn);
                        int newMax = Math.Min(0xFF, alRange.Max + immediateValue + carryIn);
                        registerTracker.SetRegisterRange("AL", (byte)newMin, (byte)newMax, RegisterValueDistribution.UniformDiscreteRange);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {mnemonic} AL, 0x{immediateValue:X2}: диапазон {alRange.Min}-{alRange.Max} -> {newMin}-{newMax}");
                    }
                    else
                    {
                        int newMin = Math.Max(0, alRange.Min - immediateValue - carryIn);
                        int newMax = Math.Max(0, alRange.Max - immediateValue - carryIn);
                        registerTracker.SetRegisterRange("AL", (byte)newMin, (byte)newMax, RegisterValueDistribution.UniformDiscreteRange);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {mnemonic} AL, 0x{immediateValue:X2}: диапазон {alRange.Min}-{alRange.Max} -> {newMin}-{newMax}");
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x8B && instructionBytes[1] == 0xE8)
            {
                if (registerTracker.TryGetRegisterValue("AX", out ushort axValue))
                    registerTracker.SetRegisterValue("BP", axValue, address, "MOV BP, AX");
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && alRange != null)
                    registerTracker.SetRegisterRange("BP", alRange.Min, alRange.Max, RegisterValueDistribution.UniformDiscreteRange);
            }

            if (instructionBytes.Length >= 4 && instructionBytes[0] == 0x81 && instructionBytes[1] == 0xE5 && instructionBytes[2] == 0xFF && instructionBytes[3] == 0x00)
            {
                if (registerTracker.TryGetRegisterValue("BP", out ushort bpValue))
                    registerTracker.SetRegisterValue("BP", (ushort)(bpValue & 0x00FF), address, "AND BP, 0x00FF");
                else if (registerTracker.TryGetRegisterRange("BP", out var bpRange) && bpRange != null)
                    registerTracker.SetRegisterRange("BP", (byte)(bpRange.Min & 0xFF), (byte)(bpRange.Max & 0xFF), RegisterValueDistribution.UniformDiscreteRange);
            }

            if (instructionBytes.Length == 1 && instructionBytes[0] == 0xF8)
            {
                registerTracker.CarryFlag = false;
                registerTracker.FlagsKnown = true;
            }

            if (instructionBytes.Length == 1 && instructionBytes[0] == 0xF9)
            {
                registerTracker.CarryFlag = true;
                registerTracker.FlagsKnown = true;
            }

            if (instructionBytes.Length == 1 && instructionBytes[0] == 0xF5)
            {
                if (registerTracker.FlagsKnown)
                    registerTracker.CarryFlag = !registerTracker.CarryFlag;
            }

            // CMP BL, [partyCount] / CMP [partyCount], BL
            // Это общий паттерн обхода партии по счётчику слота.
            if (instructionBytes.Length >= 2 &&
                (instructionBytes[0] == 0x3A || instructionBytes[0] == 0x38))
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                bool handledSpecialCompare = false;
                bool hasExactMemAddress = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out _);

                bool comparesBl =
                    (instructionBytes[0] == 0x3A && reg == 0x03 && mod != 0x03) ||
                    (instructionBytes[0] == 0x38 && rm == 0x03 && mod != 0x03);

                if (comparesBl &&
                    hasExactMemAddress &&
                    memAddr == PARTY_COUNT_ADDRESS)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("BL", RegisterTracker.FlagsOriginKind.CompareMemory, address);
                    registerTracker.LastCompareImmediate = null;
                    registerTracker.LastComparedMemoryAddress = PARTY_COUNT_ADDRESS;
                    MarkPartyMemberScanLoop(result, address, debugMode);
                    handledSpecialCompare = true;
                }

                if (!handledSpecialCompare &&
                    mod != 0x03 &&
                    reg < 8 &&
                    hasExactMemAddress &&
                    TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue))
                {
                    string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                    string regName = regNames8[reg];

                    if (registerTracker.TryGetByteRegisterValue(regName, out byte regValue))
                    {
                        if (instructionBytes[0] == 0x3A)
                        {
                            byte cmpResult = (byte)(regValue - memValue);
                            SetArithmeticFlagsForSub8(registerTracker, regValue, memValue, cmpResult,
                                regName, RegisterTracker.FlagsOriginKind.CompareMemory, address);
                        }
                        else
                        {
                            byte cmpResult = (byte)(memValue - regValue);
                            SetArithmeticFlagsForSub8(registerTracker, memValue, regValue, cmpResult,
                                regName, RegisterTracker.FlagsOriginKind.CompareMemory, address);
                        }
                    }
                    else
                    {
                        registerTracker.FlagsKnown = false;
                        registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.CompareMemory, address);
                    }

                    registerTracker.LastCompareImmediate = null;
                    registerTracker.LastComparedMemoryAddress = memAddr;

                    if (memAddr == BATTLE_MONSTER_COUNT_ADDRESS)
                    {
                        result.BattleMonsterCount = memValue;
                        result.BattleMonsterCountRange = new ValueRange8(memValue, memValue);
                        result.IsBattleMonsterCountIndeterminate = false;
                    }
                }
            }

            // CMP AL, imm8 (opcode 3C ib)
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x3C)
            {
                byte immediateValue = instructionBytes[1];

                bool hasKnownAlForCompare = registerTracker.TryGetByteRegisterValue("AL", out byte alValue);
                bool hasComparedPartyField = registerTracker.TryGetPartyFieldValue("AL", out var comparedPartyField) &&
                    comparedPartyField != null;
                bool hasPartyGenderCompare = hasComparedPartyField &&
                    comparedPartyField.Field == PartyFieldKind.Gender;
                bool hasTechnicalFieldCompare = hasComparedPartyField &&
                    comparedPartyField.Field == PartyFieldKind.Technical77;

                if (hasKnownAlForCompare)
                {
                    byte cmpResult = (byte)(alValue - immediateValue);
                    SetArithmeticFlagsForSub8(registerTracker, alValue, immediateValue, cmpResult,
                        "AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _))
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }
                else if (hasPartyGenderCompare || hasTechnicalFieldCompare)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }

                registerTracker.LastCompareImmediate = immediateValue;

                if (hasPartyGenderCompare)
                {
                    result.PartyFieldAccesses.Add(new PartyFieldReference
                    {
                        Member = comparedPartyField.Member?.Clone(),
                        Field = comparedPartyField.Field,
                        Offset = comparedPartyField.Offset,
                        EffectiveAddress = comparedPartyField.EffectiveAddress,
                        IsRead = true,
                        IsCompare = true
                    });

                    if (immediateValue == 1 || immediateValue == 2)
                    {
                        var compareEffect = new PartyEffect
                        {
                            Kind = PartyEffectKind.GenderCompared,
                            Field = PartyFieldKind.Gender,
                            Operation = PartyEffectOperation.Compare,
                            ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                            ImmediateValue = immediateValue,
                            InstructionAddress = address
                        };
                        PartyConditionKind compareCondition = immediateValue == 1
                            ? PartyConditionKind.MaleOnly
                            : PartyConditionKind.FemaleOnly;

                        AddResolvedPartyEffect(
                            result,
                            compareEffect,
                            comparedPartyField.Member,
                            registerTracker,
                            compareCondition);
                    }
                }

                if (hasTechnicalFieldCompare)
                {
                    RegisterTechnicalField77CompareEffect(
                        result,
                        comparedPartyField,
                        registerTracker,
                        address,
                        immediateValue,
                        isBitMask: false,
                        debugMode);
                }
            }

            // OR AL, AL / TEST AL, AL (точное выставление флагов для проверок на ноль)
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x0A && instructionBytes[1] == 0xC0)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    registerTracker.ZeroFlag = alValue == 0;
                    registerTracker.SignFlag = (alValue & 0x80) != 0;
                    registerTracker.CarryFlag = false;
                    registerTracker.OverflowFlag = false;
                    registerTracker.FlagsKnown = true;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xA8)
            {
                byte immediateValue = instructionBytes[1];
                bool hasTechnicalFieldCompare = registerTracker.TryGetPartyFieldValue("AL", out var testedPartyField) &&
                    testedPartyField?.Field == PartyFieldKind.Technical77;

                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte testResult = (byte)(alValue & immediateValue);
                    registerTracker.ZeroFlag = testResult == 0;
                    registerTracker.SignFlag = (testResult & 0x80) != 0;
                    registerTracker.CarryFlag = false;
                    registerTracker.OverflowFlag = false;
                    registerTracker.FlagsKnown = true;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _))
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                }
                else if (hasTechnicalFieldCompare)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                }

                if (hasTechnicalFieldCompare)
                {
                    RegisterTechnicalField77CompareEffect(
                        result,
                        testedPartyField,
                        registerTracker,
                        address,
                        immediateValue,
                        isBitMask: true,
                        debugMode);
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x84 && instructionBytes[1] == 0xC0)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    registerTracker.ZeroFlag = alValue == 0;
                    registerTracker.SignFlag = (alValue & 0x80) != 0;
                    registerTracker.CarryFlag = false;
                    registerTracker.OverflowFlag = false;
                    registerTracker.FlagsKnown = true;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                }
            }

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0x80)
            {
                byte modRm = instructionBytes[1];
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte mode = (byte)((modRm >> 6) & 0x03);
                byte regIndex = (byte)(modRm & 0x07);
                byte immediateValue = instructionBytes[2];

                if (mode == 0x03 && operation == 0x07)
                {
                    string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                    string regName = regIndex < regNames8.Length ? regNames8[regIndex] : null;
                    PartyFieldReference comparedPartyField = null;
                    bool hasTechnicalFieldCompare = !string.IsNullOrWhiteSpace(regName) &&
                        registerTracker.TryGetPartyFieldValue(regName, out comparedPartyField) &&
                        comparedPartyField?.Field == PartyFieldKind.Technical77;

                    if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetByteRegisterValue(regName, out byte regValue))
                    {
                        byte cmpResult = (byte)(regValue - immediateValue);
                        SetArithmeticFlagsForSub8(registerTracker, regValue, immediateValue, cmpResult, regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                    }
                    else if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetRegisterRange(regName, out var _))
                    {
                        registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                    }
                    else if (hasTechnicalFieldCompare)
                    {
                        registerTracker.FlagsKnown = false;
                        registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                    }

                    if (hasTechnicalFieldCompare)
                    {
                        RegisterTechnicalField77CompareEffect(
                            result,
                            comparedPartyField,
                            registerTracker,
                            address,
                            immediateValue,
                            isBitMask: false,
                            debugMode);
                    }
                }
                else if (mode == 0x03)
                {
                    string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                    PartyEffectOperation registerOperation = MapImmediateByteTransformOperation(operation);
                    if (registerOperation != PartyEffectOperation.Unknown && regIndex < regNames8.Length)
                    {
                        TrackPartyFieldRegisterImmediateTransform(
                            result,
                            registerTracker,
                            regNames8[regIndex],
                            registerOperation,
                            immediateValue,
                            address,
                            debugMode);
                    }
                }
                else
                {
                    if (operation == 0x07)
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out _);
                        PartyFieldReference comparedFieldRef = null;
                        bool hasPartyFieldRef = TryResolvePartyFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out comparedFieldRef);

                        if (hasExactMemAddr &&
                            TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue))
                        {
                            byte cmpResult = (byte)(memValue - immediateValue);
                            SetArithmeticFlagsForSub8(registerTracker, memValue, immediateValue, cmpResult, null, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        }
                        else if (hasPartyFieldRef && comparedFieldRef?.Field == PartyFieldKind.Technical77)
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        }

                        registerTracker.LastCompareImmediate = immediateValue;

                        if (hasPartyFieldRef && comparedFieldRef?.Field == PartyFieldKind.Technical77)
                        {
                            RegisterTechnicalField77CompareEffect(
                                result,
                                comparedFieldRef,
                                registerTracker,
                                address,
                                immediateValue,
                                isBitMask: false,
                                debugMode);
                        }
                    }

                    PartyEffectOperation memoryOperation = MapImmediateByteTransformOperation(operation);
                    if (memoryOperation != PartyEffectOperation.Unknown)
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                        if (!hasExactMemAddr)
                            TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                        PartyFieldReference partyFieldRef = null;
                        bool hasPartyFieldRef = TryResolvePartyFieldAccess(instructionBytes, registerTracker, hasExactMemAddr ? memAddr : (ushort?)null, out partyFieldRef);

                        if (hasPartyFieldRef && (partyFieldRef.Field == PartyFieldKind.Status || partyFieldRef.Field == PartyFieldKind.Technical77))
                        {
                            byte? exactByteValue = null;
                            byte? currentByteValue = null;
                            if (hasExactMemAddr &&
                                TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte resolvedByteValue))
                            {
                                currentByteValue = resolvedByteValue;
                                if (TryApplyImmediateByteTransform(resolvedByteValue, immediateValue, memoryOperation, out byte transformedStatusValue))
                                    exactByteValue = transformedStatusValue;
                            }

                            byte relevantMask = partyFieldRef.Field == PartyFieldKind.Status
                                ? GetRelevantStatusMask(memoryOperation, immediateValue)
                                : PartyTechnicalField77Semantics.GetRelevantMask(memoryOperation, immediateValue);

                            if (partyFieldRef.Field == PartyFieldKind.Technical77)
                                RegisterPartyFieldRead(result, partyFieldRef, registerTracker, address, currentByteValue);

                            RegisterPartyFieldWrite(
                                result,
                                partyFieldRef,
                                registerTracker,
                                address,
                                debugMode,
                                exactByteValue,
                                memoryOperation,
                                relevantMask != 0 ? relevantMask : (byte?)null);

                            if (debugMode)
                            {
                                string valueText = exactByteValue.HasValue
                                    ? $" -> 0x{exactByteValue.Value:X2}"
                                    : string.Empty;
                                string fieldText = partyFieldRef.Field == PartyFieldKind.Status
                                    ? "статуса персонажа"
                                    : PartyTechnicalField77Semantics.FieldLabel;
                                AnalysisDebug.WriteLine($"        Распознано изменение {fieldText} через byte ptr {eaText}, 0x{immediateValue:X2}{valueText}");
                            }
                        }
                    }
                }
            }

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xF6)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                byte immediateValue = instructionBytes[2];

                if (operation == 0x00)
                {
                    if (mod == 0x03)
                    {
                        string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                        string regName = rm < regNames8.Length ? regNames8[rm] : null;
                        PartyFieldReference comparedPartyField = null;
                        bool hasTechnicalFieldCompare = !string.IsNullOrWhiteSpace(regName) &&
                            registerTracker.TryGetPartyFieldValue(regName, out comparedPartyField) &&
                            comparedPartyField?.Field == PartyFieldKind.Technical77;

                        if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetByteRegisterValue(regName, out byte regValue))
                        {
                            byte testResult = (byte)(regValue & immediateValue);
                            registerTracker.ZeroFlag = testResult == 0;
                            registerTracker.SignFlag = (testResult & 0x80) != 0;
                            registerTracker.CarryFlag = false;
                            registerTracker.OverflowFlag = false;
                            registerTracker.FlagsKnown = true;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                        }
                        else if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetRegisterRange(regName, out var _))
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                        }
                        else if (hasTechnicalFieldCompare)
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                        }

                        if (hasTechnicalFieldCompare)
                        {
                            RegisterTechnicalField77CompareEffect(
                                result,
                                comparedPartyField,
                                registerTracker,
                                address,
                                immediateValue,
                                isBitMask: true,
                                debugMode);
                        }
                    }
                    else
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out _);
                        PartyFieldReference testedFieldRef = null;
                        bool hasPartyFieldRef = TryResolvePartyFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out testedFieldRef);

                        if (hasExactMemAddr &&
                            TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue))
                        {
                            byte testResult = (byte)(memValue & immediateValue);
                            registerTracker.ZeroFlag = testResult == 0;
                            registerTracker.SignFlag = (testResult & 0x80) != 0;
                            registerTracker.CarryFlag = false;
                            registerTracker.OverflowFlag = false;
                            registerTracker.FlagsKnown = true;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.Test, address);
                        }
                        else if (hasPartyFieldRef && testedFieldRef?.Field == PartyFieldKind.Technical77)
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.Test, address);
                        }

                        if (hasPartyFieldRef && testedFieldRef?.Field == PartyFieldKind.Technical77)
                        {
                            RegisterTechnicalField77CompareEffect(
                                result,
                                testedFieldRef,
                                registerTracker,
                                address,
                                immediateValue,
                                isBitMask: true,
                                debugMode);
                        }
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xFE)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (mod == 0x03 &&
                    rm < regNames8.Length &&
                    (operation == 0x00 || operation == 0x01))
                {
                    string regName = regNames8[rm];
                    string fullReg = GetFullRegisterNameForByteRegister(regName);
                    int delta = operation == 0x00 ? 1 : -1;
                    string opText = operation == 0x00 ? "INC" : "DEC";

                    if (registerTracker.TryGetByteRegisterValue(regName, out byte regValue) &&
                        !string.IsNullOrEmpty(fullReg))
                    {
                        byte newValue = unchecked((byte)(regValue + delta));
                        registerTracker.TrackPartialRegisterOperation(fullReg, regName, newValue, address, $"{opText} {regName}");
                    }
                    else if (registerTracker.TryGetRegisterRange(regName, out var regRange) && regRange != null)
                    {
                        registerTracker.TryGetRegisterDistribution(regName, out var rangeDistribution);
                        int newMin = Math.Max(0, regRange.Min + delta);
                        int newMax = Math.Min(0xFF, regRange.Max + delta);
                        registerTracker.SetRegisterRange(regName, (byte)newMin, (byte)newMax, rangeDistribution);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {opText} {regName}: диапазон {regRange.Min}-{regRange.Max} -> {newMin}-{newMax}");
                    }
                }
            }

            if (instructionBytes.Length >= 1 && instructionBytes[0] == 0x43)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    ushort newValue = (ushort)(bxValue + 1);
                    registerTracker.SetRegisterValue("BX", newValue, address,
                        $"INC BX: {bxValue} -> {newValue}");
                }
            }

            return false;
        }

        private bool IsPopReg16(X86Instruction insn)
        {
            return insn?.Bytes != null && insn.Bytes.Length >= 1 && insn.Bytes[0] >= 0x58 && insn.Bytes[0] <= 0x5F;
        }

        private string GetPopRegisterName(X86Instruction insn)
        {
            if (insn?.Bytes == null || insn.Bytes.Length == 0)
                return "REG";

            string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
            int index = insn.Bytes[0] - 0x58;
            return index >= 0 && index < regNames.Length ? regNames[index] : "REG";
        }

        /// <summary>
        /// Анализирует диапазоны для частичных битв
        /// </summary>
        private void AnalyzePartialBattleRanges(BinaryReader br, PathAnalysisResult result)
        {
            if (!result.HasPartialBattlePattern || result.PartialBattleInfo.Count == 0)
            {
                return;
            }

            AnalysisDebug.WriteLine($"    АНАЛИЗ ЧАСТИЧНЫХ БИТВ: найдено {result.PartialBattleInfo.Count} записей");
            AnalysisDebug.WriteLine($"    Состояние цикла: IsInLoop={result.IsInLoop}, LoopStartAddress=0x{result.LoopStartAddress:X4}, LoopIterationCount={result.LoopIterationCount}");

            // Группируем записи по BX индексу (индекс в массиве, куда сохраняются значения)
            var groupedByBx = result.PartialBattleInfo.GroupBy(p => p.BxIndex).OrderBy(g => g.Key);

            foreach (var group in groupedByBx)
            {
                int saveBxIndex = group.Key;
                var entries = group.ToList();

                AnalysisDebug.WriteLine($"      Группа BX(сохранения)={saveBxIndex}: {entries.Count} записей");

                // Находим записи для 3C58 и 3C29
                var entry58 = entries.FirstOrDefault(e => e.DestAddr == 0x3C58);
                var entry29 = entries.FirstOrDefault(e => e.DestAddr == 0x3C29);

                if (entry58 == null || entry29 == null)
                {
                    AnalysisDebug.WriteLine($"        -> НЕТ ПОЛНОЙ ПАРЫ ЗАПИСЕЙ для создания частичной битвы");
                    continue;
                }

                // ПРОВЕРКА: не были ли эти значения уже определены как конкретные?
                bool alreadyDefined = false;

                // Проверяем, есть ли уже полностью определённая битва для этого BX индекса
                if (result.BattleMonsterEntries.ContainsKey(saveBxIndex))
                {
                    var existing = result.BattleMonsterEntries[saveBxIndex];
                    // Если оба значения не нулевые, значит битва уже полностью определена
                    if (existing.val1 != 0 || existing.val2 != 0)
                    {
                        AnalysisDebug.WriteLine($"        -> БИТВА УЖЕ ПОЛНОСТЬЮ ОПРЕДЕЛЕНА: val1={existing.val1:X2}, val2={existing.val2:X2}, пропускаем создание частичной");
                        alreadyDefined = true;
                    }
                }

                if (alreadyDefined)
                {
                    continue;
                }

                // Определяем количество итераций цикла
                int iterationCount = 1;

                if (result.IsInLoop && result.LoopStartAddress != 0)
                {
                    if (result.LoopIterationCount > 0)
                    {
                        iterationCount = result.LoopIterationCount;
                        AnalysisDebug.WriteLine($"        Обнаружен цикл с {iterationCount} итерациями");
                    }
                    else
                    {
                        // Используем максимальный BX индекс для определения количества итераций
                        int maxSaveBx = groupedByBx.Max(g => g.Key);
                        if (maxSaveBx > 0)
                        {
                            // Количество итераций = максимальный BX индекс + 1
                            iterationCount = maxSaveBx + 1;
                            AnalysisDebug.WriteLine($"        Количество итераций определено по BX сохранения: {iterationCount}");
                        }
                        else
                        {
                            // Если не можем определить, используем значение по умолчанию
                            iterationCount = 8;
                            AnalysisDebug.WriteLine($"        Количество итераций неизвестно, предполагаем {iterationCount}");
                        }
                    }
                }

                // СОЗДАЁМ ЧАСТИЧНЫЕ БИТВЫ ТОЛЬКО ДЛЯ ИТЕРАЦИЙ, КОТОРЫЕ ЕЩЁ НЕ ОПРЕДЕЛЕНЫ
                for (int iteration = 0; iteration < iterationCount; iteration++)
                {
                    int currentBxIndex = saveBxIndex + iteration;

                    // Проверяем, не определена ли уже битва для этого индекса
                    if (result.BattleMonsterEntries.ContainsKey(currentBxIndex))
                    {
                        var existing = result.BattleMonsterEntries[currentBxIndex];
                        if (existing.val1 != 0 || existing.val2 != 0)
                        {
                            AnalysisDebug.WriteLine($"        -> БИТВА ДЛЯ BX={currentBxIndex} УЖЕ ОПРЕДЕЛЕНА, пропускаем");
                            continue;
                        }
                    }

                    int loadBx = iteration;

                    ushort table1Addr = (ushort)(0xCDA9 + loadBx);
                    ushort table2Addr = (ushort)(0xCDB1 + loadBx);

                    byte iterVal1 = 0;
                    byte iterVal2 = 0;
                    bool readSuccess1 = false;
                    bool readSuccess2 = false;

                    try
                    {
                        readSuccess1 = TryReadOverlayByte(br, table1Addr, out iterVal1);

                        ushort iterWord2 = 0;
                        if (TryReadOverlayByte(br, table2Addr, out byte lowByte2) &&
                            TryReadOverlayByte(br, unchecked((ushort)(table2Addr + 1)), out byte highByte2))
                        {
                            iterWord2 = (ushort)(lowByte2 | (highByte2 << 8));
                            iterVal2 = lowByte2;
                            readSuccess2 = true;
                        }

                        AnalysisDebug.WriteLine($"        ЧТЕНИЕ ИЗ ТАБЛИЦЫ ДЛЯ ИТЕРАЦИИ {iteration}:");
                        AnalysisDebug.WriteLine(readSuccess1
                            ? $"          [{table1Addr:X4}] = 0x{iterVal1:X2}"
                            : $"          [{table1Addr:X4}] = <read failed>");
                        AnalysisDebug.WriteLine(readSuccess2
                            ? $"          [{table2Addr:X4}] = 0x{iterVal2:X2} (младший байт из 0x{iterWord2:X4})"
                            : $"          [{table2Addr:X4}] = <read failed>");
                    }
                    catch (Exception ex)
                    {
                        AnalysisDebug.WriteLine($"        ОШИБКА ЧТЕНИЯ ИЗ ТАБЛИЦЫ: {ex.Message}");
                    }

                    if (!readSuccess1 || !readSuccess2)
                    {
                        AnalysisDebug.WriteLine($"        НЕ УДАЛОСЬ ПРОЧИТАТЬ ЗНАЧЕНИЯ ДЛЯ ИТЕРАЦИИ {iteration}, пропускаем");
                        continue;
                    }

                    byte rangeStart1 = iterVal1;
                    byte rangeEnd1 = iterVal1;
                    byte rangeStart2 = iterVal2;
                    byte rangeEnd2 = iterVal2;

                    if (rangeStart1 > rangeEnd1 || rangeStart2 > rangeEnd2)
                    {
                        AnalysisDebug.WriteLine($"        -> НЕКОРРЕКТНЫЕ ДИАПАЗОНЫ ДЛЯ ИТЕРАЦИИ {iteration}: [{rangeStart1:X2}-{rangeEnd1:X2}] [{rangeStart2:X2}-{rangeEnd2:X2}]");
                        continue;
                    }

                    // Проверяем, не совпадают ли эти значения с уже определёнными конкретными значениями
                    bool matchesExisting = false;
                    if (result.BattleMonsterEntries.ContainsKey(currentBxIndex))
                    {
                        var existing = result.BattleMonsterEntries[currentBxIndex];
                        if (existing.val1 == rangeStart1 && existing.val2 == rangeStart2)
                        {
                            matchesExisting = true;
                        }
                    }

                    if (matchesExisting)
                    {
                        AnalysisDebug.WriteLine($"        -> ЗНАЧЕНИЯ СОВПАДАЮТ С УЖЕ ОПРЕДЕЛЁННОЙ БИТВОЙ, пропускаем");
                        continue;
                    }

                    var partialBattle = new PartiallyDefinedBattle
                    {
                        BxIndex = currentBxIndex,
                        RangeStart1 = rangeStart1,
                        RangeEnd1 = rangeEnd1,
                        RangeStart2 = rangeStart2,
                        RangeEnd2 = rangeEnd2
                    };

                    // Добавляем только если такой частичной битвы ещё нет
                    if (!result.PartialBattles.Any(p => p.BxIndex == partialBattle.BxIndex))
                    {
                        result.PartialBattles.Add(partialBattle);
                        int combinations = (rangeEnd1 - rangeStart1 + 1) * (rangeEnd2 - rangeStart2 + 1);
                        AnalysisDebug.WriteLine($"        -> СОЗДАНА ЧАСТИЧНАЯ БИТВА: BX(сохранения)={currentBxIndex}, BX(загрузки)={loadBx}, {combinations} комбинация (итерация {iteration})");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"        -> ЧАСТИЧНАЯ БИТВА ДЛЯ BX={currentBxIndex} УЖЕ СУЩЕСТВУЕТ, пропускаем");
                    }
                }
            }
        }


        private string GetFullRegisterNameForByteRegister(string regName)
        {
            return regName?.ToUpperInvariant() switch
            {
                "AL" or "AH" => "AX",
                "BL" or "BH" => "BX",
                "CL" or "CH" => "CX",
                "DL" or "DH" => "DX",
                _ => string.Empty
            };
        }

        private bool TryDecode16BitEffectiveAddress(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            out ushort effectiveAddress,
            out int decodedLength,
            out string eaText)
        {
            effectiveAddress = 0;
            decodedLength = 0;
            eaText = null;

            if (instructionBytes == null || instructionBytes.Length < 2)
                return false;

            byte modRm = instructionBytes[1];
            byte mod = (byte)((modRm >> 6) & 0x03);
            byte rm = (byte)(modRm & 0x07);

            if (mod == 0x03)
                return false;

            sbyte disp8 = 0;
            short disp16 = 0;
            int dispSize;

            switch (mod)
            {
                case 0x00:
                    if (rm == 0x06)
                    {
                        if (instructionBytes.Length < 4)
                            return false;

                        effectiveAddress = BitConverter.ToUInt16(instructionBytes, 2);
                        decodedLength = 4;
                        eaText = $"[0x{effectiveAddress:X4}]";
                        return true;
                    }

                    dispSize = 0;
                    break;

                case 0x01:
                    if (instructionBytes.Length < 3)
                        return false;

                    disp8 = unchecked((sbyte)instructionBytes[2]);
                    dispSize = 1;
                    break;

                case 0x02:
                    if (instructionBytes.Length < 4)
                        return false;

                    disp16 = unchecked((short)BitConverter.ToUInt16(instructionBytes, 2));
                    dispSize = 2;
                    break;

                default:
                    return false;
            }

            if (!TryGet16BitAddressBase(registerTracker, rm, out ushort baseValue, out string baseText))
                return false;

            int signedDisp = dispSize switch
            {
                1 => disp8,
                2 => disp16,
                _ => 0
            };

            effectiveAddress = unchecked((ushort)(baseValue + signedDisp));
            decodedLength = 2 + dispSize;

            if (signedDisp == 0)
                eaText = $"[{baseText}]";
            else if (signedDisp > 0)
                eaText = $"[{baseText}+0x{signedDisp:X}]";
            else
                eaText = $"[{baseText}-0x{(-signedDisp):X}]";

            return true;
        }

        private bool TryGet16BitAddressBase(RegisterTracker registerTracker, byte rm, out ushort baseValue, out string baseText)
        {
            baseValue = 0;
            baseText = null;

            switch (rm)
            {
                case 0x00:
                    if (registerTracker.TryGetRegisterValue("BX", out ushort bx0) &&
                        registerTracker.TryGetRegisterValue("SI", out ushort si0))
                    {
                        baseValue = (ushort)(bx0 + si0);
                        baseText = "BX+SI";
                        return true;
                    }
                    return false;

                case 0x01:
                    if (registerTracker.TryGetRegisterValue("BX", out ushort bx1) &&
                        registerTracker.TryGetRegisterValue("DI", out ushort di1))
                    {
                        baseValue = (ushort)(bx1 + di1);
                        baseText = "BX+DI";
                        return true;
                    }
                    return false;

                case 0x02:
                    if (registerTracker.TryGetRegisterValue("BP", out ushort bp2) &&
                        registerTracker.TryGetRegisterValue("SI", out ushort si2))
                    {
                        baseValue = (ushort)(bp2 + si2);
                        baseText = "BP+SI";
                        return true;
                    }
                    return false;

                case 0x03:
                    if (registerTracker.TryGetRegisterValue("BP", out ushort bp3) &&
                        registerTracker.TryGetRegisterValue("DI", out ushort di3))
                    {
                        baseValue = (ushort)(bp3 + di3);
                        baseText = "BP+DI";
                        return true;
                    }
                    return false;

                case 0x04:
                    if (registerTracker.TryGetRegisterValue("SI", out ushort si4))
                    {
                        baseValue = si4;
                        baseText = "SI";
                        return true;
                    }
                    return false;

                case 0x05:
                    if (registerTracker.TryGetRegisterValue("DI", out ushort di5))
                    {
                        baseValue = di5;
                        baseText = "DI";
                        return true;
                    }
                    return false;

                case 0x06:
                    if (registerTracker.TryGetRegisterValue("BP", out ushort bp6))
                    {
                        baseValue = bp6;
                        baseText = "BP";
                        return true;
                    }
                    return false;

                case 0x07:
                    if (registerTracker.TryGetRegisterValue("BX", out ushort bx7))
                    {
                        baseValue = bx7;
                        baseText = "BX";
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private bool TryGetReg8ValueFromModRmRegField(byte modRm, RegisterTracker registerTracker, out byte value, out string regName)
        {
            value = 0;
            regName = null;

            string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
            byte regField = (byte)((modRm >> 3) & 0x07);
            if (regField >= regNames.Length)
                return false;

            regName = regNames[regField];
            return registerTracker.TryGetByteRegisterValue(regName, out value);
        }

        private bool TryGetReg16ValueFromModRmRegField(byte modRm, RegisterTracker registerTracker, out ushort value, out string regName)
        {
            value = 0;
            regName = null;

            string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
            byte regField = (byte)((modRm >> 3) & 0x07);
            if (regField >= regNames.Length)
                return false;

            regName = regNames[regField];
            return registerTracker.TryGetRegisterValue(regName, out value);
        }

        private bool TryResolveTrackedByteValue(BinaryReader br, ushort memAddr, PathAnalysisResult result, byte targetX, byte targetY, out byte value)
        {
            if (_emulatedMemory8.TryGetValue(memAddr, out value))
                return true;

            if (memAddr == 0x3C38)
            {
                value = result?.TeleportTargetX ?? targetX;
                return true;
            }

            if (memAddr == 0x3C39)
            {
                value = result?.TeleportTargetY ?? targetY;
                return true;
            }

            return TryReadOverlayByte(br, memAddr, out value);
        }

        private bool TryResolveTrackedWordValue(BinaryReader br, ushort memAddr, PathAnalysisResult result, byte targetX, byte targetY, out ushort value)
        {
            value = 0;

            if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte low) &&
                TryResolveTrackedByteValue(br, unchecked((ushort)(memAddr + 1)), result, targetX, targetY, out byte high))
            {
                value = (ushort)(low | (high << 8));
                return true;
            }

            return false;
        }

        private bool IsTrackedWordInEmulatedMemory(ushort memAddr)
        {
            return _emulatedMemory8.ContainsKey(memAddr) ||
                   _emulatedMemory8.ContainsKey(unchecked((ushort)(memAddr + 1)));
        }

        private bool TryReadOverlayByte(BinaryReader br, ushort memAddr, out byte value)
        {
            value = 0;

            if (br == null || !TryMapOverlayAddressToFileOffset(br, memAddr, out long fileOffset))
                return false;

            long originalPos = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset;
                value = br.ReadByte();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
            finally
            {
                br.BaseStream.Position = originalPos;
            }
        }

        private void ApplyTrackedByteWrite(ushort memAddr, byte value, PathAnalysisResult result,
            byte targetX, byte targetY, X86Instruction insn, bool debugMode, string sourceDescription)
        {
            _emulatedMemory8[memAddr] = value;
            _emulatedPartyPointerBytes.Remove(memAddr);
            _emulatedPartyPointers.Remove(memAddr);
            _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));

            if (debugMode)
                AnalysisDebug.WriteLine($"        {sourceDescription}: записали 0x{value:X2} в эмулируемую память [0x{memAddr:X4}]");

            if (result == null)
                return;

            if (memAddr == BATTLE_MONSTER_COUNT_ADDRESS)
            {
                result.BattleMonsterCount = value;
                result.BattleMonsterCountRange = new ValueRange8(value, value);
                result.IsBattleMonsterCountIndeterminate = false;
                result.HasSignificantCode = true;

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"        Семантика [0x{BATTLE_MONSTER_COUNT_ADDRESS:X4}]: точное количество монстров = {value}");
                }
            }
            else if (memAddr == 0x3C38)
            {
                result.TeleportTargetX = value;
                result.TeleportTargetXRange = new ValueRange8(value, value);
                if (!result.TeleportTargetY.HasValue && result.TeleportTargetYRange == null)
                    result.TeleportTargetY = targetY;
                result.HasSignificantCode = true;
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Обнаружен телепорт по X: новая координата X = {value}, Y = {result.TeleportTargetY} (инструкция 0x{insn.Address:X4})");
            }
            else if (memAddr == 0x3C39)
            {
                result.TeleportTargetY = value;
                result.TeleportTargetYRange = new ValueRange8(value, value);
                if (!result.TeleportTargetX.HasValue && result.TeleportTargetXRange == null)
                    result.TeleportTargetX = targetX;
                result.HasSignificantCode = true;
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Обнаружен телепорт по Y: новая координата X = {result.TeleportTargetX}, Y = {value} (инструкция 0x{insn.Address:X4})");
            }
        }

        private void ApplyTrackedWordWrite(ushort memAddr, ushort value, PathAnalysisResult result,
            byte targetX, byte targetY, X86Instruction insn, bool debugMode, string sourceDescription)
        {
            byte low = (byte)(value & 0x00FF);
            byte high = (byte)((value >> 8) & 0x00FF);

            ApplyTrackedByteWrite(memAddr, low, result, targetX, targetY, insn, debugMode, sourceDescription + " (low)");
            ApplyTrackedByteWrite(unchecked((ushort)(memAddr + 1)), high, result, targetX, targetY, insn, debugMode, sourceDescription + " (high)");
        }

        /// <summary>
        /// Завершает анализ и устанавливает флаг наличия значимого кода
        /// </summary>
        private void FinalizeResult(PathAnalysisResult result, int instructionCount,
            uint currentAddress, long fileLength, bool debugMode)
        {
            if (instructionCount >= MAX_INSTRUCTIONS_PER_PATH)
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"      Достигнут лимит инструкций ({MAX_INSTRUCTIONS_PER_PATH}) - путь прерван");
                result.IsTerminated = false;
            }
            else if (currentAddress >= fileLength)
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"      Достигнут конец оверлея - конец пути");
                result.IsTerminated = true;
            }

            result.HasSignificantCode = result.OrderedTexts.Count > 0 ||
                                         result.FoundTexts.Count > 0 ||
                                         result.ContextTexts.Count > 0 ||
                                         result.MonsterPower.HasValue ||
                                         result.MonsterLevel.HasValue ||
                                         result.BattleMonsterEntries.Count > 0 ||
                                         result.PartialBattles.Count > 0 ||
                                         result.HasPartialBattlePattern ||
                                         result.CallsRandomEncounter ||
                                         result.HasTeleportTarget ||
                                         (result.PartyEffects != null && result.PartyEffects.Count > 0);
        }
    }
}
