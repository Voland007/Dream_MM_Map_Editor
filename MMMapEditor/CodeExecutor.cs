﻿// Copyright (c) Voland007 2026. All rights reserved.
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    /// <summary>
    /// Выполняет эмуляцию кода по заданному адресу
    /// </summary>
    public class CodeExecutor
    {
        private const ushort StaticMapFirstLayerBaseAddress = 0x3CFA;
        private const ushort StaticMapSecondLayerBaseAddress = 0x3DFA;
        private const int StaticMapLayerWidth = 16;
        private const int StaticMapLayerHeight = 16;
        private const int StaticMapLayerByteCount = StaticMapLayerWidth * StaticMapLayerHeight;

        private readonly OvrFileConfig _config;
        private readonly InstructionAnalyzer _instructionAnalyzer;
        private readonly Dictionary<ushort, byte> _emulatedMemory8 = new Dictionary<ushort, byte>();
        private readonly Dictionary<ushort, ValueRange8> _emulatedMemory8Ranges = new Dictionary<ushort, ValueRange8>();
        private readonly Dictionary<ushort, RegisterValueDistribution> _emulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>();
        private readonly Dictionary<ushort, PartyMemberReference> _emulatedPartyPointers = new Dictionary<ushort, PartyMemberReference>();
        private readonly Dictionary<ushort, PartyPointerByteReference> _emulatedPartyPointerBytes = new Dictionary<ushort, PartyPointerByteReference>();
        private readonly HashSet<ushort> _persistentEventStateAddresses = new HashSet<ushort>();
        private readonly Dictionary<uint, X86Instruction> _instructionCache = new Dictionary<uint, X86Instruction>();
        private readonly HashSet<uint> _invalidInstructionCache = new HashSet<uint>();

        private enum ShiftLoopTargetKind
        {
            Register,
            Memory
        }

        private enum ShiftLoopOperationKind
        {
            SetCarry,
            Shl,
            Shr,
            Rcl,
            Rcr
        }

        private sealed class ShiftLoopOperation
        {
            public ShiftLoopOperationKind Kind { get; set; }
            public ShiftLoopTargetKind TargetKind { get; set; }
            public string RegisterName { get; set; }
            public ushort MemoryAddress { get; set; }
            public bool CarryValue { get; set; }
        }

        private sealed class ShiftLoopSimulationState
        {
            public Dictionary<string, byte> Registers { get; set; } = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<ushort, byte> Memory { get; set; } = new Dictionary<ushort, byte>();
            public bool CarryKnown { get; set; }
            public bool Carry { get; set; }

            public ShiftLoopSimulationState Clone()
            {
                return new ShiftLoopSimulationState
                {
                    Registers = new Dictionary<string, byte>(Registers, StringComparer.OrdinalIgnoreCase),
                    Memory = new Dictionary<ushort, byte>(Memory),
                    CarryKnown = CarryKnown,
                    Carry = Carry
                };
            }
        }

        private const int MAX_DEPTH = 24;
        private const byte KEYBOARD_INPUT_MIN = 0x00;
        private const byte KEYBOARD_INPUT_MAX = 0x7F;
        private const ushort LEGACY_INPUT_INDEX_ADDRESS = 0x3BBA;
        private const ushort OVERLAY_INPUT_INDEX_ADDRESS = 0xC9BB;
        private const ushort INPUT_BUFFER_ADDRESS = 0x3CB8;
        private const ushort PARTY_POINTER_TABLE = 0x3CA8;
        private const ushort PARTY_COUNT_ADDRESS = 0x3BC0;
        private const ushort BATTLE_RANDOM_ENCOUNTER_RUBICON_ADDRESS = 0x3C1C;
        private const ushort BATTLE_MONSTER_COUNT_ADDRESS = 0x3C1D;
        private const ushort BATTLE_MONSTER_FIRST_TABLE_ADDRESS = 0x3C58;
        private const ushort BATTLE_MONSTER_SECOND_TABLE_ADDRESS = 0x3C29;
        private const ushort BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS = 0x3CA6;
        private const int BATTLE_MONSTER_TABLE_SLOT_COUNT = 0x0F;
        private const int PARTY_MEMBER_COUNT = 6;
        private const int PARTY_sex_OFFSET = 0x10;
        private const byte PARTY_sex_MALE_VALUE = 0x01;
        private const byte PARTY_sex_FEMALE_VALUE = 0x02;
        private const int PARTY_INNATE_ALIGNMENT_OFFSET = PartyAlignmentSemantics.InnateFieldOffset;
        private const int PARTY_CURRENT_ALIGNMENT_OFFSET = PartyAlignmentSemantics.CurrentFieldOffset;
        private const int PARTY_TEMP_INTELLECT_OFFSET = PartyTemporaryStatSemantics.TempIntellectFieldOffset;
        private const int PARTY_TEMP_MIGHT_OFFSET = PartyTemporaryStatSemantics.TempMightFieldOffset;
        private const int PARTY_TEMP_PERSONALITY_OFFSET = PartyTemporaryStatSemantics.TempPersonalityFieldOffset;
        private const int PARTY_TEMP_ENDURANCE_OFFSET = PartyTemporaryStatSemantics.TempEnduranceFieldOffset;
        private const int PARTY_TEMP_SPEED_OFFSET = PartyTemporaryStatSemantics.TempSpeedFieldOffset;
        private const int PARTY_TEMP_ACCURACY_OFFSET = PartyTemporaryStatSemantics.TempAccuracyFieldOffset;
        private const int PARTY_TEMP_LUCK_OFFSET = PartyTemporaryStatSemantics.TempLuckFieldOffset;
        private const int PARTY_TEMP_LEVEL_OFFSET = PartyTemporaryStatSemantics.TempLevelFieldOffset;
        private const int PARTY_SP_LOW_OFFSET = 0x2B;
        private const int PARTY_SP_HIGH_OFFSET = 0x2C;
        private const int PARTY_HP_LOW_OFFSET = 0x33;
        private const int PARTY_HP_HIGH_OFFSET = 0x34;
        private const int PARTY_MAX_HP_LOW_OFFSET = 0x35;
        private const int PARTY_MAX_HP_HIGH_OFFSET = 0x36;
        private const int PARTY_FOOD_OFFSET = PartyFoodSemantics.FieldOffset;
        private const int PARTY_STATUS_OFFSET = PartyStatusSemantics.FieldOffset;
        private const int PARTY_RANALOU_QUESTLINE_OFFSET = PartyTechnicalFieldSemantics.RanalouQuestLineFieldOffset;
        private const int PARTY_QUEST_LORD1_OFFSET = PartyQuestLordFieldSemantics.Lord1FieldOffset;
        private const int PARTY_QUEST_LORD2_OFFSET = PartyQuestLordFieldSemantics.Lord2FieldOffset;
        private const int PARTY_QUEST_LORD3_OFFSET = PartyQuestLordFieldSemantics.Lord3FieldOffset;
        private const int PARTY_MAIN_QUEST_COMPLETION_OFFSET = PartyTechnicalFieldSemantics.MainQuestCompletionFieldOffset;
        private const int MAX_CALL_DEPTH = 10;
        private const int MAX_INSTRUCTIONS_PER_PATH = 3000;
        private const uint DISPLAY_TEXT_ROUTINE_ADDRESS = 0x4FB5;
        private const uint CURRENT_MAP_EVENT_DISABLE_ROUTINE_ADDRESS = 0x4FC8;
        private const uint POSITIONED_TEXT_ROUTINE_ADDRESS = 0x4C60;
        private const ushort ACTIVE_TEXT_POINTER_ADDRESS = 0x3BD4;
        private const ushort TEXT_CURSOR_COLUMN_ADDRESS = 0x3BC4;
        private const int MAX_TEXT_POINTER_TABLE_OPTIONS = 16;

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
            Dictionary<ushort, PartyPointerByteReference> initialEmulatedPartyPointerBytes = null,
            HashSet<ushort> persistentStateAddresses = null,
            Dictionary<ushort, ValueRange8> initialEmulatedMemory8Ranges = null,
            Dictionary<ushort, RegisterValueDistribution> initialEmulatedMemory8RangeDistributions = null,
            Dictionary<ushort, PersistentCounterProgressionInfo> initialPendingPersistentCounterProgressions = null)
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
            var savedEmulatedMemory8Ranges = CloneRangeDictionary(_emulatedMemory8Ranges);
            var savedEmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions);
            var savedEmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone());
            var savedEmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone());
            var savedPersistentEventStateAddresses = new HashSet<ushort>(_persistentEventStateAddresses);
            _emulatedMemory8.Clear();
            _emulatedMemory8Ranges.Clear();
            _emulatedMemory8RangeDistributions.Clear();
            _emulatedPartyPointers.Clear();
            _emulatedPartyPointerBytes.Clear();
            if (persistentStateAddresses != null)
            {
                _persistentEventStateAddresses.Clear();
                foreach (ushort address in persistentStateAddresses)
                    _persistentEventStateAddresses.Add(address);
            }
            if (initialEmulatedMemory8 != null)
            {
                foreach (var kv in initialEmulatedMemory8)
                    _emulatedMemory8[kv.Key] = kv.Value;
            }
            if (initialEmulatedMemory8Ranges != null)
            {
                foreach (var kv in initialEmulatedMemory8Ranges)
                    _emulatedMemory8Ranges[kv.Key] = kv.Value == null ? null : new ValueRange8(kv.Value.Min, kv.Value.Max);
            }
            if (initialEmulatedMemory8RangeDistributions != null)
            {
                foreach (var kv in initialEmulatedMemory8RangeDistributions)
                    _emulatedMemory8RangeDistributions[kv.Key] = kv.Value;
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
                result.PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(initialPendingPersistentCounterProgressions);
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

                        if (result.PartialBattleInfo.Count > 0)
                        {
                            AnalyzePartialBattleRanges(br, result, isFinalPass: true);
                        }

                        result.IsTerminated = true;
                        return CaptureExitStateAndFinalizeResult(
                            result,
                            registerTracker,
                            instructionCount,
                            currentAddress,
                            fileLength,
                            debugMode,
                            currentPendingReturnAddresses,
                            currentCallDepth);
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
                        var newTexts = new List<TextEntry>();
                        _instructionAnalyzer.FindTextsInInstruction(insn, br, registerTracker, depth, newTexts,
                            address => TryGetCurrentMemory8Value(br, address));
                        ProcessFoundTexts(newTexts, foundTextsInThisPath, result, currentCallDepth, insn, debugMode, ref textOrderCounter);

                        // Поиск изменений статистики монстров и информации о битвах
                        _instructionAnalyzer.FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        _instructionAnalyzer.FindMonsterBattleInfo(insn, br, registerTracker, depth, result, targetX, targetY, TryGetEmulatedMemory8Value);
                        if (TrackRegisterOperations(insn, br, registerTracker, depth, debugMode, result, targetX, targetY,
                            currentCallDepth, currentPendingReturnAddresses, ref textOrderCounter))
                        {
                            return CaptureExitStateAndFinalizeResult(
                                result,
                                registerTracker,
                                instructionCount,
                                currentAddress,
                                fileLength,
                                debugMode,
                                currentPendingReturnAddresses,
                                currentCallDepth);
                        }

                        TryAddDisplayedTextFromExternalCall(insn, br, registerTracker, result, targetX, targetY,
                            currentCallDepth, debugMode, ref textOrderCounter);

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
                        {
                            List<uint> exitPendingReturnAddresses =
                                handlingResult.UpdatedPendingReturnAddresses ?? currentPendingReturnAddresses;
                            int exitCallDepth =
                                handlingResult.UpdatedCallDepth >= 0 ? handlingResult.UpdatedCallDepth : currentCallDepth;

                            return CaptureExitStateAndFinalizeResult(
                                handlingResult.Result,
                                registerTracker,
                                instructionCount,
                                currentAddress,
                                fileLength,
                                debugMode,
                                exitPendingReturnAddresses,
                                exitCallDepth);
                        }

                        currentAddress = handlingResult.NextAddress;
                        if (handlingResult.UpdatedPendingReturnAddresses != null)
                            currentPendingReturnAddresses = handlingResult.UpdatedPendingReturnAddresses;
                        if (handlingResult.UpdatedCallDepth >= 0)
                            currentCallDepth = handlingResult.UpdatedCallDepth;
                    }

                // Финальный анализ частичных битв
                if (result.PartialBattleInfo.Count > 0)
                {
                    AnalyzePartialBattleRanges(br, result, isFinalPass: true);
                }

                return CaptureExitStateAndFinalizeResult(
                    result,
                    registerTracker,
                    instructionCount,
                    currentAddress,
                    fileLength,
                    debugMode,
                    currentPendingReturnAddresses,
                    currentCallDepth);
            }
            finally
            {
                _emulatedMemory8.Clear();
                foreach (var kv in savedEmulatedMemory8)
                    _emulatedMemory8[kv.Key] = kv.Value;
                _emulatedMemory8Ranges.Clear();
                foreach (var kv in savedEmulatedMemory8Ranges)
                    _emulatedMemory8Ranges[kv.Key] = kv.Value == null ? null : new ValueRange8(kv.Value.Min, kv.Value.Max);
                _emulatedMemory8RangeDistributions.Clear();
                foreach (var kv in savedEmulatedMemory8RangeDistributions)
                    _emulatedMemory8RangeDistributions[kv.Key] = kv.Value;
                _emulatedPartyPointers.Clear();
                foreach (var kv in savedEmulatedPartyPointers)
                    _emulatedPartyPointers[kv.Key] = kv.Value?.Clone();
                _emulatedPartyPointerBytes.Clear();
                foreach (var kv in savedEmulatedPartyPointerBytes)
                    _emulatedPartyPointerBytes[kv.Key] = kv.Value?.Clone();
                _persistentEventStateAddresses.Clear();
                foreach (ushort address in savedPersistentEventStateAddresses)
                    _persistentEventStateAddresses.Add(address);
            }
        }

        private byte? TryGetEmulatedMemory8Value(ushort address)
        {
            return _emulatedMemory8.TryGetValue(address, out byte value)
                ? value
                : (byte?)null;
        }

        private static Dictionary<ushort, ValueRange8> CloneRangeDictionary(Dictionary<ushort, ValueRange8> source)
        {
            if (source == null || source.Count == 0)
                return new Dictionary<ushort, ValueRange8>();

            return source.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value == null ? null : new ValueRange8(kvp.Value.Min, kvp.Value.Max));
        }

        private bool TryGetEmulatedMemory8Range(ushort address, out ValueRange8 range, out RegisterValueDistribution distribution)
        {
            distribution = RegisterValueDistribution.Unknown;
            if (_emulatedMemory8Ranges.TryGetValue(address, out range) && range != null)
            {
                if (!_emulatedMemory8RangeDistributions.TryGetValue(address, out distribution))
                    distribution = RegisterValueDistribution.Unknown;

                range = new ValueRange8(range.Min, range.Max);
                return true;
            }

            range = null;
            return false;
        }

        private byte? TryGetCurrentMemory8Value(BinaryReader br, ushort address)
        {
            if (_emulatedMemory8.TryGetValue(address, out byte value))
                return value;

            if (_emulatedMemory8Ranges.TryGetValue(address, out var range) && range != null)
                return range.IsExact ? range.Min : (byte?)null;

            return TryReadOverlayByte(br, address, out value)
                ? value
                : (byte?)null;
        }

        private void CaptureExitEmulatedState(PathAnalysisResult result)
        {
            if (result == null)
                return;

            result.ExitEmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8);
            result.ExitEmulatedMemory8Ranges = CloneRangeDictionary(_emulatedMemory8Ranges);
            result.ExitEmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions);
        }

        private PathAnalysisResult CaptureExitStateAndFinalizeResult(PathAnalysisResult result,
            RegisterTracker registerTracker, int instructionCount, uint currentAddress,
            long fileLength, bool debugMode, List<uint> exitPendingReturnAddresses, int exitCallDepth)
        {
            if (result == null)
                return null;

            result.ExitPendingReturnAddresses = exitPendingReturnAddresses == null
                ? new List<uint>()
                : new List<uint>(exitPendingReturnAddresses);
            result.ExitCallDepth = exitCallDepth;
            CaptureExitEmulatedState(result);
            FinalizeResult(result, registerTracker, instructionCount, currentAddress, fileLength, debugMode, exitCallDepth);
            return result;
        }

        private void RegisterMemoryRead(PathAnalysisResult result, ushort memAddr, bool contributesToPersistentState)
        {
            if (result == null)
                return;

            result.MemoryReadAddresses.Add(memAddr);
            if (!result.PersistentMemoryFirstAccessKinds.ContainsKey(memAddr))
                result.PersistentMemoryFirstAccessKinds[memAddr] = PersistentMemoryFirstAccessKind.Read;

            if (contributesToPersistentState && !result.MemoryWrittenAddresses.Contains(memAddr))
                result.MemoryReadBeforeWriteAddresses.Add(memAddr);
        }

        private void RegisterMemoryWrite(PathAnalysisResult result, ushort memAddr)
        {
            if (result == null)
                return;

            result.MemoryWrittenAddresses.Add(memAddr);
            if (!result.PersistentMemoryFirstAccessKinds.ContainsKey(memAddr))
                result.PersistentMemoryFirstAccessKinds[memAddr] = PersistentMemoryFirstAccessKind.Write;
        }

        private void PropagateExternalCallInfluenceOnRegisterCopy(
            RegisterTracker registerTracker,
            string srcReg,
            string dstReg,
            bool debugMode)
        {
            if (registerTracker == null ||
                string.IsNullOrWhiteSpace(srcReg) ||
                string.IsNullOrWhiteSpace(dstReg))
            {
                return;
            }

            bool sourceDependsOnExternalCall =
                registerTracker.HasPendingExternalCallResult(srcReg) ||
                registerTracker.IsRegisterExternallyDerived(srcReg);

            if (!sourceDependsOnExternalCall)
                return;

            registerTracker.MaterializePendingExternalCallResult(srcReg);
            registerTracker.MarkRegisterAsExternallyDerived(srcReg);
            registerTracker.MarkRegisterAsExternallyDerived(dstReg);

            if (debugMode)
                AnalysisDebug.WriteLine($"        Копирование зависимости от внешнего CALL: {dstReg} <- {srcReg}");
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
                IsRandomLikeDistribution(rangedDistribution)
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

        private bool TryResolveDynamicPartyPointerByteFromMemoryOperand(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            out PartyPointerByteReference pointerByte)
        {
            pointerByte = null;
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

            if (rangedOffset == null || rangedOffset.Max >= PARTY_MEMBER_COUNT * 2)
                return false;

            bool isHighByte;
            if (baseContribution == PARTY_POINTER_TABLE)
            {
                isHighByte = false;
            }
            else if (baseContribution == PARTY_POINTER_TABLE + 1)
            {
                isHighByte = true;
            }
            else
            {
                return false;
            }

            PartyMemberSelectionKind selectionKind =
                IsRandomLikeDistribution(rangedDistribution)
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

            pointerByte = new PartyPointerByteReference
            {
                Member = new PartyMemberReference
                {
                    MemberIndex = memberIndex,
                    PointerTableAddress = pointerTableAddress,
                    SelectionKind = selectionKind,
                    Source = "PartyPointerTableByteRange"
                },
                IsHighByte = isHighByte,
                Source = "PartyPointerTableByteRange"
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

        private PartyFieldKind ResolvePartyFieldKind(int offset)
        {
            return offset switch
            {
                PARTY_sex_OFFSET => PartyFieldKind.sex,
                PARTY_INNATE_ALIGNMENT_OFFSET => PartyFieldKind.InnateAlignment,
                PARTY_CURRENT_ALIGNMENT_OFFSET => PartyFieldKind.CurrentAlignment,
                PARTY_TEMP_INTELLECT_OFFSET => PartyFieldKind.TempIntellect,
                PARTY_TEMP_MIGHT_OFFSET => PartyFieldKind.TempMight,
                PARTY_TEMP_PERSONALITY_OFFSET => PartyFieldKind.TempPersonality,
                PARTY_TEMP_ENDURANCE_OFFSET => PartyFieldKind.TempEndurance,
                PARTY_TEMP_SPEED_OFFSET => PartyFieldKind.TempSpeed,
                PARTY_TEMP_ACCURACY_OFFSET => PartyFieldKind.TempAccuracy,
                PARTY_TEMP_LUCK_OFFSET => PartyFieldKind.TempLuck,
                PARTY_TEMP_LEVEL_OFFSET => PartyFieldKind.TempLevel,
                PARTY_SP_LOW_OFFSET => PartyFieldKind.SpLow,
                PARTY_SP_HIGH_OFFSET => PartyFieldKind.SpHigh,
                PARTY_HP_LOW_OFFSET => PartyFieldKind.HpLow,
                PARTY_HP_HIGH_OFFSET => PartyFieldKind.HpHigh,
                PARTY_MAX_HP_LOW_OFFSET => PartyFieldKind.MaxHpLow,
                PARTY_MAX_HP_HIGH_OFFSET => PartyFieldKind.MaxHpHigh,
                PARTY_FOOD_OFFSET => PartyFieldKind.Food,
                PARTY_STATUS_OFFSET => PartyFieldKind.Status,
                PARTY_RANALOU_QUESTLINE_OFFSET => PartyFieldKind.Technical71,
                PARTY_QUEST_LORD1_OFFSET => PartyFieldKind.Technical75,
                PARTY_QUEST_LORD2_OFFSET => PartyFieldKind.Technical76,
                PARTY_QUEST_LORD3_OFFSET => PartyFieldKind.Technical77,
                PARTY_MAIN_QUEST_COMPLETION_OFFSET => PartyFieldKind.Technical7D,
                _ => PartyFieldKind.Unknown
            };
        }

        private static bool IsRandomLikeDistribution(RegisterValueDistribution distribution)
        {
            return distribution == RegisterValueDistribution.UniformDiscreteRange ||
                   distribution == RegisterValueDistribution.EvenDiscreteRange;
        }

        private bool TryResolvePartyFieldKindFromRange(int minOffset, int maxOffset,
            RegisterValueDistribution distribution, out PartyFieldKind field)
        {
            field = PartyFieldKind.Unknown;
            if (minOffset > maxOffset)
                return false;

            if (distribution != RegisterValueDistribution.UniformDiscreteRange &&
                distribution != RegisterValueDistribution.EvenDiscreteRange)
            {
                return false;
            }

            int step = distribution == RegisterValueDistribution.EvenDiscreteRange ? 2 : 1;
            int start = distribution == RegisterValueDistribution.EvenDiscreteRange && (minOffset & 1) != 0
                ? minOffset + 1
                : minOffset;

            var candidateFields = new HashSet<PartyFieldKind>();
            for (int offset = start; offset <= maxOffset; offset += step)
            {
                PartyFieldKind candidateField = ResolvePartyFieldKind(offset);
                if (candidateField == PartyFieldKind.Unknown)
                    return false;

                candidateFields.Add(candidateField);
            }

            if (candidateFields.Count == 0)
                return false;

            if (candidateFields.Count == 1)
            {
                field = candidateFields.First();
                return true;
            }

            if (candidateFields.All(PartyTemporaryStatSemantics.IsConcreteField))
            {
                field = PartyFieldKind.TempAnyStat;
                return true;
            }

            return false;
        }

        private bool TryResolvePartyMemberFieldAccess(byte[] instructionBytes, RegisterTracker tracker, ushort? effectiveAddress, out PartyFieldReference fieldRef)
        {
            return TryResolvePartyFieldAccessCore(instructionBytes, tracker, effectiveAddress, allowUnknownField: true, out fieldRef);
        }

        private bool TryResolvePartyFieldAccess(byte[] instructionBytes, RegisterTracker tracker, ushort? effectiveAddress, out PartyFieldReference fieldRef)
        {
            return TryResolvePartyFieldAccessCore(instructionBytes, tracker, effectiveAddress, allowUnknownField: false, out fieldRef);
        }

        private bool TryResolvePartyFieldAccessCore(byte[] instructionBytes, RegisterTracker tracker, ushort? effectiveAddress,
            bool allowUnknownField, out PartyFieldReference fieldRef)
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
            ValueRange8 rangedOffset = null;
            RegisterValueDistribution rangedDistribution = RegisterValueDistribution.Unknown;

            bool exact(string regName, out ushort regValue) => tracker.TryGetRegisterValue(regName, out regValue);

            bool ranged(string regName, out ValueRange8 rangeValue)
            {
                rangeValue = null;
                if (!tracker.TryGetRegisterRange(regName, out var rawRange) || rawRange == null)
                    return false;

                rangeValue = new ValueRange8(rawRange.Min, rawRange.Max);
                tracker.TryGetRegisterDistribution(regName, out rangedDistribution);
                return true;
            }

            switch (rm)
            {
                case 0x00: // BX+SI
                    if (tracker.TryGetPartyMemberBase("SI", out member) && exact("BX", out ushort bx0))
                        offset += bx0;
                    else if (tracker.TryGetPartyMemberBase("SI", out member) && ranged("BX", out var bx0Range))
                        rangedOffset = bx0Range;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && exact("SI", out ushort si0))
                        offset += si0;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && ranged("SI", out var si0Range))
                        rangedOffset = si0Range;
                    else
                        return false;
                    break;
                case 0x01: // BX+DI
                    if (tracker.TryGetPartyMemberBase("DI", out member) && exact("BX", out ushort bx1))
                        offset += bx1;
                    else if (tracker.TryGetPartyMemberBase("DI", out member) && ranged("BX", out var bx1Range))
                        rangedOffset = bx1Range;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && exact("DI", out ushort di1))
                        offset += di1;
                    else if (tracker.TryGetPartyMemberBase("BX", out member) && ranged("DI", out var di1Range))
                        rangedOffset = di1Range;
                    else
                        return false;
                    break;
                case 0x03: // BP+DI
                    if (tracker.TryGetPartyMemberBase("DI", out member) && exact("BP", out ushort bp))
                        offset += bp;
                    else if (tracker.TryGetPartyMemberBase("DI", out member) && ranged("BP", out var bpRange))
                        rangedOffset = bpRange;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && exact("DI", out ushort di))
                        offset += di;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && ranged("DI", out var diRange))
                        rangedOffset = diRange;
                    else
                        return false;
                    break;
                case 0x02: // BP+SI
                    if (tracker.TryGetPartyMemberBase("SI", out member) && exact("BP", out ushort bp2))
                        offset += bp2;
                    else if (tracker.TryGetPartyMemberBase("SI", out member) && ranged("BP", out var bp2Range))
                        rangedOffset = bp2Range;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && exact("SI", out ushort si2))
                        offset += si2;
                    else if (tracker.TryGetPartyMemberBase("BP", out member) && ranged("SI", out var si2Range))
                        rangedOffset = si2Range;
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

            PartyFieldKind field = PartyFieldKind.Unknown;
            int resolvedOffset = offset;
            ValueRange8 fieldOffsetRange = null;
            if (rangedOffset != null)
            {
                int minOffset = offset + rangedOffset.Min;
                int maxOffset = offset + rangedOffset.Max;
                if (minOffset >= byte.MinValue && maxOffset <= byte.MaxValue)
                    fieldOffsetRange = new ValueRange8((byte)minOffset, (byte)maxOffset);

                if (!TryResolvePartyFieldKindFromRange(minOffset, maxOffset, rangedDistribution, out field))
                {
                    if (!allowUnknownField)
                        return false;
                }
                else if (field == PartyFieldKind.TempAnyStat)
                {
                    resolvedOffset = -1;
                }
                else
                {
                    resolvedOffset = minOffset;
                }
            }
            else
            {
                field = ResolvePartyFieldKind(offset);
            }

            if (field == PartyFieldKind.Unknown && !allowUnknownField)
                return false;

            fieldRef = new PartyFieldReference
            {
                Member = member?.Clone(),
                Field = field,
                Offset = resolvedOffset,
                EffectiveAddress = effectiveAddress,
                FieldOffset = resolvedOffset >= byte.MinValue && resolvedOffset <= byte.MaxValue ? (byte)resolvedOffset : (byte?)null,
                FieldOffsetRange = fieldOffsetRange,
                FieldName = field != PartyFieldKind.Unknown
                    ? GetPartyFieldLabel(field)
                    : fieldOffsetRange != null && !fieldOffsetRange.IsExact
                        ? $"техническое байтовое поле персонажа +0x{fieldOffsetRange.Min:X2}..+0x{fieldOffsetRange.Max:X2}"
                    : resolvedOffset >= byte.MinValue && resolvedOffset <= byte.MaxValue
                        ? $"техническое байтовое поле персонажа +0x{resolvedOffset:X2}"
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

        private void ApplyPartyConditionToPending(PendingPartyStatOperation pending, PartyConditionKind condition)
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

        private void ApplyPartyPredicatesToPending(PendingPartyStatOperation pending, IEnumerable<PartyPredicate> predicates)
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

        private static bool IsPartyStatField(PartyFieldKind field)
        {
            return field == PartyFieldKind.HpLow ||
                   field == PartyFieldKind.HpHigh ||
                   field == PartyFieldKind.SpLow ||
                   field == PartyFieldKind.SpHigh;
        }

        private static bool IsPartyStatLowField(PartyFieldKind field)
        {
            return field == PartyFieldKind.HpLow || field == PartyFieldKind.SpLow;
        }

        private static bool IsPartyStatHighField(PartyFieldKind field)
        {
            return field == PartyFieldKind.HpHigh || field == PartyFieldKind.SpHigh;
        }

        private static PartyFieldKind GetPartyStatBaseField(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.HpLow or PartyFieldKind.HpHigh or PartyFieldKind.Hp => PartyFieldKind.Hp,
                PartyFieldKind.SpLow or PartyFieldKind.SpHigh or PartyFieldKind.Sp => PartyFieldKind.Sp,
                _ => PartyFieldKind.Unknown
            };
        }

        private PendingPartyStatOperation GetPendingPartyStatOperation(PathAnalysisResult result, PartyFieldKind field)
        {
            if (result == null)
                return null;

            return GetPartyStatBaseField(field) switch
            {
                PartyFieldKind.Hp => result.PendingPartyHpOperation,
                PartyFieldKind.Sp => result.PendingPartySpOperation,
                _ => null
            };
        }

        private void SetPendingPartyStatOperation(PathAnalysisResult result, PartyFieldKind field, PendingPartyStatOperation pending)
        {
            if (result == null)
                return;

            switch (GetPartyStatBaseField(field))
            {
                case PartyFieldKind.Hp:
                    result.PendingPartyHpOperation = pending;
                    break;
                case PartyFieldKind.Sp:
                    result.PendingPartySpOperation = pending;
                    break;
            }
        }

        private PendingPartyStatOperation EnsurePendingPartyStatOperation(PathAnalysisResult result,
            PartyFieldReference fieldRef, uint instructionAddress, int callDepth = 0,
            List<uint> pendingReturnAddresses = null)
        {
            if (result == null || fieldRef == null)
                return null;

            PartyFieldKind statField = GetPartyStatBaseField(fieldRef.Field);
            if (statField == PartyFieldKind.Unknown)
                return null;

            var pending = GetPendingPartyStatOperation(result, statField);
            if (pending != null)
            {
                pending.Field = statField;

                if (!MatchesPendingPartyTarget(pending.Member, fieldRef.Member) ||
                    ShouldRotateCompletedPendingPartyStatOperation(pending, instructionAddress))
                {
                    ArchiveCompletedPendingPartyStatOperation(result, pending, statField);
                    pending = null;
                    SetPendingPartyStatOperation(result, statField, null);
                }
            }

            if (pending == null)
            {
                pending = new PendingPartyStatOperation
                {
                    Field = statField,
                    Member = fieldRef.Member?.Clone(),
                    StartAddress = instructionAddress
                };
                CapturePendingPartyStatReturnBoundary(pending, callDepth, pendingReturnAddresses);
                SetPendingPartyStatOperation(result, statField, pending);
            }
            else
            {
                pending.Field = statField;
                CapturePendingPartyStatReturnBoundary(pending, callDepth, pendingReturnAddresses);

                if (pending.Member == null)
                    pending.Member = fieldRef.Member?.Clone();

                if (instructionAddress != 0 &&
                    (pending.StartAddress == 0 || instructionAddress < pending.StartAddress))
                {
                    pending.StartAddress = instructionAddress;
                }
            }

            return pending;
        }

        private static void CapturePendingPartyStatReturnBoundary(
            PendingPartyStatOperation pending,
            int callDepth,
            List<uint> pendingReturnAddresses)
        {
            if (pending == null ||
                pending.AwaitingReturnAddress.HasValue ||
                callDepth <= 0 ||
                pendingReturnAddresses == null ||
                pendingReturnAddresses.Count == 0)
            {
                return;
            }

            pending.AwaitingReturnAddress = pendingReturnAddresses[pendingReturnAddresses.Count - 1];
            pending.AwaitingCallDepth = callDepth;
        }

        private int EnsurePendingPartyStatExecutionOrder(PathAnalysisResult result, PendingPartyStatOperation pending)
        {
            if (result == null || pending == null)
                return 0;

            if (pending.ExecutionOrder <= 0)
                pending.ExecutionOrder = ++result.NextSpecialEventOrder;

            return pending.ExecutionOrder;
        }

        private static bool ShouldRotateCompletedPendingPartyStatOperation(
            PendingPartyStatOperation pending,
            uint instructionAddress)
        {
            if (pending == null || instructionAddress == 0)
                return false;

            if (pending.Member?.IsPartyLoopMember == true)
                return false;

            PartyFieldKind field = pending.Field;
            return field != PartyFieldKind.Unknown &&
                   PartyEffectNormalizer.CanCreateNormalizedStatEffect(pending, field);
        }

        private static void ArchiveCompletedPendingPartyStatOperation(
            PathAnalysisResult result,
            PendingPartyStatOperation pending,
            PartyFieldKind fallbackField)
        {
            if (result == null || pending == null)
                return;

            PartyFieldKind field = pending.Field != PartyFieldKind.Unknown
                ? pending.Field
                : fallbackField;
            if (field == PartyFieldKind.Unknown ||
                !PartyEffectNormalizer.CanCreateNormalizedStatEffect(pending, field))
            {
                return;
            }

            var archived = pending.Clone();
            archived.Field = field;
            result.CompletedPartyStatOperations.Add(archived);
        }

        private static PendingPartyStatOperation MoveCompletedPendingPartyStatOperationToHistory(
            PathAnalysisResult result,
            PendingPartyStatOperation pending,
            PartyFieldKind fallbackField)
        {
            if (pending == null)
                return null;

            PartyFieldKind field = pending.Field != PartyFieldKind.Unknown
                ? pending.Field
                : fallbackField;
            if (field == PartyFieldKind.Unknown ||
                !PartyEffectNormalizer.CanCreateNormalizedStatEffect(pending, field))
            {
                return pending;
            }

            ArchiveCompletedPendingPartyStatOperation(result, pending, field);
            return null;
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

            NormalizePendingPartyStatForLoopAggregation(result.PendingPartyHpOperation, result.LoopSemantic);
            NormalizePendingPartyStatForLoopAggregation(result.PendingPartySpOperation, result.LoopSemantic);

            PromoteEffectsCapturedBeforeLoopRecognition(result, loopBodyStartAddress, detectedLoopEnd);

            if (debugMode && firstDetection)
                AnalysisDebug.WriteLine($"    РАСПОЗНАН ЦИКЛ ОБХОДА ПАРТИИ: счётчик сравнивается с [0x{PARTY_COUNT_ADDRESS:X4}]");
        }

        private void NormalizePendingPartyStatForLoopAggregation(PendingPartyStatOperation pending, LoopSemanticKind loopSemantic)
        {
            if (pending?.Member != null)
                pending.Member = NormalizeMemberForLoopAggregation(pending.Member, loopSemantic);

            if (pending?.GuardPredicates != null && pending.GuardPredicates.Count > 0)
            {
                pending.GuardPredicates =
                    PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(pending.GuardPredicates);
            }
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
            return TryGetPartyMemberScanCounterRegister(registerTracker, out _);
        }

        private static bool TryGetPartyMemberScanCounterRegister(RegisterTracker registerTracker, out string counterRegister)
        {
            counterRegister = registerTracker?.LastFlagsRegister?.ToUpperInvariant();
            return registerTracker != null &&
                   registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareMemory &&
                   TryGetHighByteRegisterForLowByteRegister(counterRegister, out _) &&
                   registerTracker.LastComparedMemoryAddress == PARTY_COUNT_ADDRESS &&
                   registerTracker.LastFlagsInstructionAddress.HasValue;
        }

        private static bool IsByteRegisterName(string registerName)
        {
            string reg = registerName?.ToUpperInvariant();
            return reg == "AL" || reg == "CL" || reg == "DL" || reg == "BL" ||
                   reg == "AH" || reg == "CH" || reg == "DH" || reg == "BH";
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

            string comparedRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            PartyFieldReference comparedField = null;
            ushort compareValue = 0;

            switch (registerTracker.LastFlagsOrigin)
            {
                case RegisterTracker.FlagsOriginKind.CompareImmediate:
                    if (!registerTracker.LastCompareImmediate.HasValue)
                        return null;

                    comparedField = registerTracker.LastComparedPartyField?.Clone();
                    if (comparedField == null)
                    {
                        if (string.IsNullOrWhiteSpace(comparedRegister))
                            return null;

                        if (!registerTracker.TryGetPartyFieldValue(comparedRegister, out comparedField) || comparedField == null)
                            return null;
                    }

                    compareValue = registerTracker.LastCompareImmediate.Value;
                    break;

                case RegisterTracker.FlagsOriginKind.Test:
                    if (!TryInferSignTestPredicateContext(
                            registerTracker,
                            comparedRegister,
                            mnemonic,
                            out comparedField,
                            out compareValue))
                    {
                        return null;
                    }

                    break;

                default:
                    return null;
            }

            var comparison = ResolvePredicateComparisonForBranch(
                mnemonic,
                branchTaken,
                registerTracker.LastFlagsOrigin);
            if (comparison == PartyPredicateComparison.Unknown)
                return null;

            return new PartyPredicate
            {
                Field = comparedField.Field,
                Comparison = comparison,
                ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                ImmediateValue = compareValue,
                FieldOffset = comparedField.FieldOffset,
                FieldOffsetRange = comparedField.FieldOffsetRange == null ? null : new ValueRange8(comparedField.FieldOffsetRange.Min, comparedField.FieldOffsetRange.Max),
                InstructionAddress = instructionAddress,
                TargetMember = comparedField.Member?.Clone(),
                Description = BuildPartyPredicateDescription(comparedField.Field, comparison, compareValue)
            };
        }

        private bool TryInferSignTestPredicateContext(
            RegisterTracker registerTracker,
            string comparedRegister,
            string mnemonic,
            out PartyFieldReference comparedField,
            out ushort compareValue)
        {
            comparedField = null;
            compareValue = 0;

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            if (jump != "JS" && jump != "JNS")
                return false;

            if (!registerTracker.LastTestMask.HasValue || (registerTracker.LastTestMask.Value & 0x80) == 0)
                return false;

            if (string.IsNullOrWhiteSpace(comparedRegister))
                return false;

            if (!registerTracker.TryGetPartyFieldValue(comparedRegister, out comparedField) || comparedField == null)
                return false;

            compareValue = 0x80;
            return true;
        }

        private PartyPredicateComparison ResolvePredicateComparisonForBranch(
            string mnemonic,
            bool branchTaken,
            RegisterTracker.FlagsOriginKind flagsOrigin)
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
                "JS" when flagsOrigin == RegisterTracker.FlagsOriginKind.Test
                    => branchTaken ? PartyPredicateComparison.GreaterOrEqual : PartyPredicateComparison.LessThan,
                "JNS" when flagsOrigin == RegisterTracker.FlagsOriginKind.Test
                    => branchTaken ? PartyPredicateComparison.LessThan : PartyPredicateComparison.GreaterOrEqual,
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
            if (predicate?.Field != PartyFieldKind.sex || !predicate.ImmediateValue.HasValue)
                return PartyConditionKind.None;

            ushort immediateValue = predicate.ImmediateValue.Value;
            return predicate.Comparison switch
            {
                PartyPredicateComparison.Equal when immediateValue == PARTY_sex_MALE_VALUE => PartyConditionKind.MaleOnly,
                PartyPredicateComparison.NotEqual when immediateValue == PARTY_sex_MALE_VALUE => PartyConditionKind.FemaleOnly,
                PartyPredicateComparison.Equal when immediateValue == PARTY_sex_FEMALE_VALUE => PartyConditionKind.FemaleOnly,
                PartyPredicateComparison.NotEqual when immediateValue == PARTY_sex_FEMALE_VALUE => PartyConditionKind.MaleOnly,
                _ => PartyConditionKind.None
            };
        }

        private string BuildPartyPredicateDescription(PartyFieldKind field, PartyPredicateComparison comparison, ushort value)
        {
            string fieldText = field switch
            {
                PartyFieldKind.sex => "sex",
                PartyFieldKind.InnateAlignment => PartyAlignmentSemantics.InnateFieldLabel,
                PartyFieldKind.CurrentAlignment => PartyAlignmentSemantics.CurrentFieldLabel,
                PartyFieldKind.Hp => "HP",
                PartyFieldKind.HpLow => "HP low",
                PartyFieldKind.HpHigh => "HP high",
                PartyFieldKind.MaxHp => "Max HP",
                PartyFieldKind.MaxHpLow => "Max HP low",
                PartyFieldKind.MaxHpHigh => "Max HP high",
                PartyFieldKind.Sp => "SP",
                PartyFieldKind.SpLow => "SP low",
                PartyFieldKind.SpHigh => "SP high",
                PartyFieldKind.Food => PartyFoodSemantics.FieldLabel,
                PartyFieldKind.Status => "Status",
                _ when IsTrackedPartyField(field)
                    => GetPartyFieldLabel(field),
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

            string valueText = PartyAlignmentSemantics.IsAlignmentField(field)
                ? PartyAlignmentSemantics.FormatAlignmentValue(value)
                : field == PartyFieldKind.Status
                    ? FormatStatusPredicateValue(value)
                    : $"0x{value:X2}";

            return $"{fieldText} {comparisonText} {valueText}";
        }

        private static string FormatStatusPredicateValue(ushort value)
        {
            var statusNames = PartyStatusSemantics.GetStatusNamesForExactValue((byte)value);
            return statusNames.Count > 0
                ? string.Join(", ", statusNames)
                : $"0x{value:X2}";
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
                predicate.FieldOffset?.ToString("X2") ?? "-",
                predicate.FieldOffsetRange == null ? "-" : $"{predicate.FieldOffsetRange.Min:X2}-{predicate.FieldOffsetRange.Max:X2}",
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
            RegisterTracker registerTracker, int callDepth, List<uint> pendingReturnAddresses,
            uint instructionAddress = 0, byte? exactValue = null)
        {
            if (result == null || fieldRef == null)
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                FieldOffset = fieldRef.FieldOffset,
                FieldOffsetRange = fieldRef.FieldOffsetRange == null ? null : new ValueRange8(fieldRef.FieldOffsetRange.Min, fieldRef.FieldOffsetRange.Max),
                FieldName = fieldRef.FieldName,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true
            });

            var currentCondition = GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var currentPredicates = GetCurrentPartyPredicates(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);

            if (IsPartyStatField(fieldRef.Field))
            {
                var pendingFieldRef = fieldRef.Clone();
                pendingFieldRef.Member = NormalizeMemberForLoopAggregation(fieldRef.Member, result.LoopSemantic);
                var pending = EnsurePendingPartyStatOperation(
                    result,
                    pendingFieldRef,
                    instructionAddress,
                    callDepth,
                    pendingReturnAddresses);
                if (pending != null)
                {
                    if (IsPartyStatHighField(fieldRef.Field))
                        pending.SawReadHigh = true;
                    else
                        pending.SawReadLow = true;

                    ApplyPartyConditionToPending(pending, currentCondition);
                    ApplyPartyPredicatesToPending(pending, currentPredicates);
                }
            }
            else if (IsTrackedPartyField(fieldRef.Field))
            {
                AddResolvedPartyEffect(
                    result,
                    PartyEffectFactory.CreateTrackedTechnicalFieldReadEffect(
                        fieldRef.Member,
                        fieldRef.Field,
                        instructionAddress,
                        exactValue),
                    fieldRef.Member,
                    registerTracker,
                    currentCondition,
                    debugPrefix: GetPartyFieldDebugLabel(fieldRef.Field));
            }
            else if (IsRawTechnicalPartyField(fieldRef))
            {
                if (HasNonExactFieldOffsetRange(fieldRef))
                    return;

                AddResolvedPartyEffect(
                    result,
                    PartyEffectFactory.CreateRawTechnicalFieldReadEffect(
                        fieldRef.Member,
                        fieldRef.FieldOffset.Value,
                        instructionAddress,
                        exactValue),
                    fieldRef.Member,
                    registerTracker,
                    currentCondition,
                    debugPrefix: GetRawTechnicalPartyFieldLabel(fieldRef));
            }
        }

        private void RegisterPartyFieldWrite(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, int callDepth, List<uint> pendingReturnAddresses,
            uint instructionAddress, bool debugMode,
            byte? exactValue = null, PartyEffectOperation bitOperation = PartyEffectOperation.Unknown,
            byte? bitMask = null, PartyFieldReference sourceFieldValue = null, string sourceRegisterName = null)
        {
            if (result == null || fieldRef == null)
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                FieldOffset = fieldRef.FieldOffset,
                FieldOffsetRange = fieldRef.FieldOffsetRange == null ? null : new ValueRange8(fieldRef.FieldOffsetRange.Min, fieldRef.FieldOffsetRange.Max),
                FieldName = fieldRef.FieldName,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsWrite = true
            });

            var currentCondition = GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var currentPredicates = GetCurrentPartyPredicates(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            var effects = new List<PartyEffect>();
            bool isSynchronizedTemporaryMirror = IsSynchronizedTemporaryMirrorWrite(
                fieldRef,
                registerTracker,
                bitOperation,
                sourceRegisterName,
                exactValue);
            if (fieldRef.Field == PartyFieldKind.sex)
            {
                var effect = new PartyEffect
                {
                    Kind = PartyEffectKind.sexWritten,
                    Field = PartyFieldKind.sex,
                    Operation = PartyEffectOperation.Write,
                    ValueKnowledge = exactValue.HasValue ? PartyValueKnowledge.ExactImmediate : PartyValueKnowledge.Unknown,
                    ImmediateValue = exactValue.HasValue ? exactValue.Value : (ushort?)null,
                    InstructionAddress = instructionAddress
                };
                ApplyResolvedPartyEffectTarget(effect, fieldRef.Member, result.LoopSemantic, currentCondition);
                effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
                effects.Add(effect);
            }
            else if (PartyAlignmentSemantics.IsAlignmentField(fieldRef.Field))
            {
                effects.Add(PartyEffectFactory.CreateAlignmentWriteEffect(
                    fieldRef.Member,
                    fieldRef.Field,
                    instructionAddress,
                    exactValue,
                    sourceFieldValue));
            }
            else if (IsPartyStatField(fieldRef.Field))
            {
                PartyFieldKind statField = GetPartyStatBaseField(fieldRef.Field);
                var pendingStat = GetPendingPartyStatOperation(result, statField);
                var effect = new PartyEffect
                {
                    Kind = PartyEffectKind.HpWritten,
                    Field = statField,
                    InstructionAddress = instructionAddress
                };
                PartyConditionKind effectCondition = currentCondition != PartyConditionKind.None
                    ? currentCondition
                    : (IsPartyLoopTarget(fieldRef.Member, result.LoopSemantic) && pendingStat?.MaleOnly == true
                        ? PartyConditionKind.MaleOnly
                        : IsPartyLoopTarget(fieldRef.Member, result.LoopSemantic) && pendingStat?.FemaleOnly == true
                            ? PartyConditionKind.FemaleOnly
                            : PartyConditionKind.None);
                ApplyResolvedPartyEffectTarget(effect, fieldRef.Member, result.LoopSemantic, effectCondition);

                var pendingFieldRef = fieldRef.Clone();
                pendingFieldRef.Member = NormalizeMemberForLoopAggregation(fieldRef.Member, result.LoopSemantic);
                var pending = EnsurePendingPartyStatOperation(
                    result,
                    pendingFieldRef,
                    instructionAddress,
                    callDepth,
                    pendingReturnAddresses);
                if (pending != null)
                {
                    effect.ExecutionOrder = EnsurePendingPartyStatExecutionOrder(result, pending);
                    ApplyPartyConditionToPending(pending, currentCondition);
                    ApplyPartyPredicatesToPending(pending, currentPredicates);

                    if (IsPartyStatHighField(fieldRef.Field))
                    {
                        pending.SawWriteHigh = true;
                        pending.FinalWriteHighByteValue = exactValue;
                        pending.FinalWriteHighSourceField = sourceFieldValue?.Field ?? PartyFieldKind.Unknown;
                    }
                    else
                    {
                        pending.SawWriteLow = true;
                        pending.FinalWriteLowByteValue = exactValue;
                        pending.FinalWriteLowSourceField = sourceFieldValue?.Field ?? PartyFieldKind.Unknown;
                    }
                }

                effect.Description = PartyEffectSemantics.BuildHumanDescription(effect);
                effects.Add(effect);
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
            else if (IsTrackedPartyField(fieldRef.Field))
            {
                if (sourceFieldValue != null &&
                    sourceFieldValue.Field == fieldRef.Field &&
                    sourceFieldValue.BitTransform != null &&
                    !sourceFieldValue.BitTransform.IsIdentity)
                {
                    effects.AddRange(BuildTrackedTechnicalFieldEffectsFromBitTransform(
                        fieldRef.Member,
                        fieldRef.Field,
                        sourceFieldValue.BitTransform,
                        instructionAddress));
                }
                else if (bitMask.HasValue &&
                         (bitOperation == PartyEffectOperation.BitSet ||
                          bitOperation == PartyEffectOperation.BitClear ||
                          bitOperation == PartyEffectOperation.BitToggle))
                {
                    effects.Add(PartyEffectFactory.CreateTrackedTechnicalFieldBitEffect(
                        fieldRef.Member,
                        fieldRef.Field,
                        bitOperation,
                        bitMask.Value,
                        instructionAddress));
                }
                else
                {
                    effects.Add(PartyEffectFactory.CreateTrackedTechnicalFieldWriteEffect(
                        fieldRef.Member,
                        fieldRef.Field,
                        instructionAddress,
                        exactValue));
                }
            }
            else if (IsRawTechnicalPartyField(fieldRef) && !HasNonExactFieldOffsetRange(fieldRef))
            {
                bool sameRawSourceField =
                    sourceFieldValue != null &&
                    sourceFieldValue.Field == PartyFieldKind.Unknown &&
                    sourceFieldValue.FieldOffset.HasValue &&
                    sourceFieldValue.FieldOffset.Value == fieldRef.FieldOffset.Value;

                if (sameRawSourceField &&
                    sourceFieldValue.BitTransform != null &&
                    !sourceFieldValue.BitTransform.IsIdentity)
                {
                    effects.AddRange(BuildRawTechnicalFieldEffectsFromBitTransform(
                        fieldRef.Member,
                        fieldRef.FieldOffset.Value,
                        sourceFieldValue.BitTransform,
                        instructionAddress));
                }
                else if (bitMask.HasValue &&
                         (bitOperation == PartyEffectOperation.BitSet ||
                          bitOperation == PartyEffectOperation.BitClear ||
                          bitOperation == PartyEffectOperation.BitToggle))
                {
                    effects.Add(PartyEffectFactory.CreateRawTechnicalFieldBitEffect(
                        fieldRef.Member,
                        fieldRef.FieldOffset.Value,
                        bitOperation,
                        bitMask.Value,
                        instructionAddress));
                }
                else
                {
                    effects.Add(PartyEffectFactory.CreateRawTechnicalFieldWriteEffect(
                        fieldRef.Member,
                        fieldRef.FieldOffset.Value,
                        instructionAddress,
                        exactValue));
                }
            }

            foreach (var effect in effects.Where(e => e != null))
            {
                if (isSynchronizedTemporaryMirror)
                    effect.IsSynchronizedTemporaryMirror = true;

                string debugPrefix = fieldRef.Field switch
                {
                    PartyFieldKind.InnateAlignment => PartyAlignmentSemantics.InnateFieldLabel,
                    PartyFieldKind.CurrentAlignment => PartyAlignmentSemantics.CurrentFieldLabel,
                    PartyFieldKind.Status => "Статус персонажа",
                    _ when IsTrackedPartyField(fieldRef.Field)
                        => GetPartyFieldDebugLabel(fieldRef.Field),
                    _ when IsRawTechnicalPartyField(fieldRef)
                        => GetRawTechnicalPartyFieldLabel(fieldRef),
                    _ => null
                };

                AddResolvedPartyEffect(result, effect, fieldRef.Member, registerTracker, currentCondition, debugPrefix, debugMode);
            }

            RememberPartyByteWrite(
                registerTracker,
                fieldRef,
                bitOperation != PartyEffectOperation.Unknown ? bitOperation : PartyEffectOperation.Write,
                instructionAddress,
                sourceRegisterName,
                exactValue);
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

            if (effect.ExecutionOrder <= 0)
                effect.ExecutionOrder = ++result.NextSpecialEventOrder;

            string humanDescription = PartyEffectSemantics.BuildHumanDescription(effect);
            if (!string.IsNullOrWhiteSpace(humanDescription))
                effect.Description = humanDescription;

            string effectKey = PartyEffectSemantics.BuildSemanticKey(effect);
            var existingEffect = result.PartyEffects.FirstOrDefault(e => e != null && PartyEffectSemantics.BuildSemanticKey(e) == effectKey);
            if (existingEffect == null)
                result.PartyEffects.Add(effect);
            else if (existingEffect.IsSynchronizedTemporaryMirror && !effect.IsSynchronizedTemporaryMirror)
                existingEffect.IsSynchronizedTemporaryMirror = false;

            if (debugMode && !string.IsNullOrWhiteSpace(debugPrefix) && !string.IsNullOrWhiteSpace(effect.Description))
                AnalysisDebug.WriteLine($"        {debugPrefix}: {effect.Description}");
        }

        private bool IsSynchronizedTemporaryMirrorWrite(PartyFieldReference fieldRef, RegisterTracker registerTracker,
            PartyEffectOperation writeOperation, string sourceRegisterName, byte? exactValue)
        {
            if (fieldRef == null ||
                registerTracker?.LastPartyByteWrite == null ||
                !PartyTemporaryStatSemantics.IsConcreteField(fieldRef.Field) ||
                fieldRef.Offset < 0)
            {
                return false;
            }

            if (writeOperation != PartyEffectOperation.Unknown &&
                writeOperation != PartyEffectOperation.Write)
            {
                return false;
            }

            var previousWrite = registerTracker.LastPartyByteWrite;
            if (previousWrite.Operation != PartyEffectOperation.Write)
                return false;

            if (previousWrite.Offset != fieldRef.Offset - 1)
                return false;

            if (!MatchesPendingPartyTarget(previousWrite.Member, fieldRef.Member))
                return false;

            bool sameSourceRegister =
                !string.IsNullOrWhiteSpace(sourceRegisterName) &&
                !string.IsNullOrWhiteSpace(previousWrite.SourceRegisterName) &&
                string.Equals(sourceRegisterName, previousWrite.SourceRegisterName, StringComparison.OrdinalIgnoreCase);

            bool sameExactValue =
                exactValue.HasValue &&
                previousWrite.WrittenValue.HasValue &&
                exactValue.Value == previousWrite.WrittenValue.Value;

            return sameSourceRegister || sameExactValue;
        }

        private void RememberPartyByteWrite(RegisterTracker registerTracker, PartyFieldReference fieldRef,
            PartyEffectOperation operation, uint instructionAddress, string sourceRegisterName = null, byte? writtenValue = null)
        {
            if (registerTracker == null || fieldRef == null)
                return;

            registerTracker.LastPartyByteWrite = new PartyByteWriteTrace
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                Operation = operation,
                SourceRegisterName = string.IsNullOrWhiteSpace(sourceRegisterName)
                    ? null
                    : sourceRegisterName.ToUpperInvariant(),
                WrittenValue = writtenValue,
                InstructionAddress = instructionAddress
            };
        }

        private void RegisterTrackedTechnicalFieldCompareEffect(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress, byte compareValue, bool isBitMask, bool debugMode)
        {
            if (result == null || fieldRef == null || !IsTrackedPartyField(fieldRef.Field))
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                FieldOffset = fieldRef.FieldOffset,
                FieldOffsetRange = fieldRef.FieldOffsetRange == null ? null : new ValueRange8(fieldRef.FieldOffsetRange.Min, fieldRef.FieldOffsetRange.Max),
                FieldName = fieldRef.FieldName,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            if (HasNonExactFieldOffsetRange(fieldRef))
                return;

            AddResolvedPartyEffect(
                result,
                PartyEffectFactory.CreateTrackedTechnicalFieldCompareEffect(
                    fieldRef.Member,
                    fieldRef.Field,
                    instructionAddress,
                    compareValue,
                    isBitMask),
                fieldRef.Member,
                registerTracker,
                GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic),
                debugPrefix: GetPartyFieldDebugLabel(fieldRef.Field),
                debugMode: debugMode);
        }

        private void RegisterRawTechnicalFieldCompareEffect(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress, byte compareValue, bool isBitMask, bool debugMode)
        {
            if (result == null || fieldRef == null || !IsRawTechnicalPartyField(fieldRef))
                return;

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                FieldOffset = fieldRef.FieldOffset,
                FieldOffsetRange = fieldRef.FieldOffsetRange == null ? null : new ValueRange8(fieldRef.FieldOffsetRange.Min, fieldRef.FieldOffsetRange.Max),
                FieldName = fieldRef.FieldName,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            if (HasNonExactFieldOffsetRange(fieldRef))
                return;

            AddResolvedPartyEffect(
                result,
                PartyEffectFactory.CreateRawTechnicalFieldCompareEffect(
                    fieldRef.Member,
                    fieldRef.FieldOffset.Value,
                    instructionAddress,
                    compareValue,
                    isBitMask),
                fieldRef.Member,
                registerTracker,
                GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic),
                debugPrefix: GetRawTechnicalPartyFieldLabel(fieldRef),
                debugMode: debugMode);
        }

        private void RegisterPartyFieldCompareEffect(PathAnalysisResult result, PartyFieldReference fieldRef,
            RegisterTracker registerTracker, uint instructionAddress, byte compareValue, bool debugMode)
        {
            if (result == null || fieldRef == null ||
                (!IsComparablePartyField(fieldRef.Field) && !IsRawTechnicalPartyField(fieldRef)))
                return;

            if (IsTrackedPartyField(fieldRef.Field))
            {
                RegisterTrackedTechnicalFieldCompareEffect(
                    result,
                    fieldRef,
                    registerTracker,
                    instructionAddress,
                    compareValue,
                    isBitMask: false,
                    debugMode);
                return;
            }

            if (IsRawTechnicalPartyField(fieldRef))
            {
                RegisterRawTechnicalFieldCompareEffect(
                    result,
                    fieldRef,
                    registerTracker,
                    instructionAddress,
                    compareValue,
                    isBitMask: false,
                    debugMode);
                return;
            }

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = fieldRef.Member?.Clone(),
                Field = fieldRef.Field,
                Offset = fieldRef.Offset,
                EffectiveAddress = fieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            PartyEffect effect = fieldRef.Field switch
            {
                PartyFieldKind.sex => new PartyEffect
                {
                    Kind = PartyEffectKind.sexCompared,
                    Field = PartyFieldKind.sex,
                    Operation = PartyEffectOperation.Compare,
                    ValueKnowledge = PartyValueKnowledge.ExactImmediate,
                    ImmediateValue = compareValue,
                    InstructionAddress = instructionAddress
                },
                PartyFieldKind.InnateAlignment or PartyFieldKind.CurrentAlignment
                    => PartyEffectFactory.CreateAlignmentCompareEffect(fieldRef.Member, fieldRef.Field, instructionAddress, compareValue),
                _ => null
            };

            if (effect == null)
                return;

            PartyConditionKind currentCondition = GetCurrentPartyCondition(registerTracker, instructionAddress, fieldRef.Member, result.LoopSemantic);
            PartyConditionKind compareCondition = fieldRef.Field == PartyFieldKind.sex && compareValue == PARTY_sex_MALE_VALUE
                ? PartyConditionKind.MaleOnly
                : fieldRef.Field == PartyFieldKind.sex && compareValue == PARTY_sex_FEMALE_VALUE
                    ? PartyConditionKind.FemaleOnly
                    : currentCondition;

            AddResolvedPartyEffect(
                result,
                effect,
                fieldRef.Member,
                registerTracker,
                compareCondition,
                debugPrefix: GetPartyFieldDebugLabel(fieldRef.Field),
                debugMode: debugMode);
        }

        private void RegisterPartyFieldToFieldCompareEffect(
            PathAnalysisResult result,
            PartyFieldReference leftFieldRef,
            PartyFieldReference rightFieldRef,
            RegisterTracker registerTracker,
            uint instructionAddress,
            bool debugMode)
        {
            if (result == null ||
                leftFieldRef == null ||
                rightFieldRef == null ||
                !PartyAlignmentSemantics.IsAlignmentField(leftFieldRef.Field) ||
                !PartyAlignmentSemantics.IsAlignmentField(rightFieldRef.Field))
            {
                return;
            }

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = leftFieldRef.Member?.Clone(),
                Field = leftFieldRef.Field,
                Offset = leftFieldRef.Offset,
                EffectiveAddress = leftFieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            result.PartyFieldAccesses.Add(new PartyFieldReference
            {
                Member = rightFieldRef.Member?.Clone(),
                Field = rightFieldRef.Field,
                Offset = rightFieldRef.Offset,
                EffectiveAddress = rightFieldRef.EffectiveAddress,
                IsRead = true,
                IsCompare = true
            });

            PartyEffect effect = PartyEffectFactory.CreateAlignmentFieldCompareEffect(leftFieldRef, rightFieldRef, instructionAddress);
            if (effect == null)
                return;

            PartyFieldReference primaryFieldRef = effect.Field == leftFieldRef.Field
                ? leftFieldRef
                : rightFieldRef;

            AddResolvedPartyEffect(
                result,
                effect,
                primaryFieldRef.Member,
                registerTracker,
                GetCurrentPartyCondition(registerTracker, instructionAddress, primaryFieldRef.Member, result.LoopSemantic),
                debugPrefix: "Сравнение alignment",
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

        private static DynamicValueFormulaInfo CreateDynamicFormulaFromPartyField(
            PartyFieldReference fieldRef,
            uint instructionAddress,
            byte? exactValue = null)
        {
            if (fieldRef == null ||
                fieldRef.Field == PartyFieldKind.Unknown ||
                fieldRef.HasBitTransform)
            {
                return null;
            }

            return new DynamicValueFormulaInfo
            {
                SourceField = fieldRef.Clone(),
                Multiplier = 1,
                AdditiveOffset = 0,
                MinValue = exactValue,
                MaxValue = exactValue,
                SourceInstructionAddress = instructionAddress,
                LastTransformInstructionAddress = instructionAddress
            };
        }

        private void TrackAccumulatorImmediateLogicalOperation(RegisterTracker registerTracker,
            PartyEffectOperation operation, byte immediateValue, uint instructionAddress,
            bool updateAccumulatorValue)
        {
            if (registerTracker == null || operation == PartyEffectOperation.Unknown)
                return;

            string mnemonic = operation switch
            {
                PartyEffectOperation.BitSet => "OR",
                PartyEffectOperation.BitClear => "AND",
                PartyEffectOperation.BitToggle => "XOR",
                _ => "LOGIC"
            };

            if (updateAccumulatorValue &&
                TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte oldValue) &&
                TryApplyImmediateByteTransform(oldValue, immediateValue, operation, out byte newValue))
            {
                registerTracker.TrackPartialRegisterOperation(
                    "AX",
                    "AL",
                    newValue,
                    instructionAddress,
                    $"{mnemonic} AL, 0x{immediateValue:X2}");
                SetLogicalFlagsForByteResult(registerTracker, "AL", newValue, instructionAddress, immediateValue);
                return;
            }

            if (!updateAccumulatorValue &&
                TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte currentValue))
            {
                SetLogicalFlagsForByteResult(registerTracker, "AL", currentValue, instructionAddress, immediateValue);
                return;
            }

            if (registerTracker.TryGetRegisterRange("AL", out var _) ||
                (registerTracker.TryGetPartyFieldValue("AL", out var partyField) && partyField != null) ||
                (registerTracker.TryGetPartyPointerByteValue("AL", out var pointerByte) && pointerByte != null))
            {
                registerTracker.FlagsKnown = false;
                registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, instructionAddress);
                registerTracker.LastTestMask = immediateValue;
            }
        }

        private static void SetLogicalFlagsForByteResult(RegisterTracker registerTracker, string flagsRegister,
            byte resultValue, uint instructionAddress, byte? testMask = null)
        {
            if (registerTracker == null)
                return;

            registerTracker.ZeroFlag = resultValue == 0;
            registerTracker.SignFlag = (resultValue & 0x80) != 0;
            registerTracker.CarryFlag = false;
            registerTracker.OverflowFlag = false;
            registerTracker.FlagsKnown = true;
            registerTracker.SetFlagsMetadata(flagsRegister, RegisterTracker.FlagsOriginKind.Test, instructionAddress);
            registerTracker.LastTestMask = testMask;
        }

        private static bool IsTrackedPartyField(PartyFieldKind field)
        {
            return PartyFoodSemantics.IsFoodField(field) ||
                   PartyTechnicalFieldSemantics.IsTrackedField(field) ||
                   PartyTemporaryStatSemantics.IsTrackedField(field);
        }

        private static bool IsRawTechnicalPartyField(PartyFieldReference fieldRef)
        {
            return fieldRef?.Field == PartyFieldKind.Unknown &&
                   fieldRef.FieldOffset.HasValue;
        }

        private static bool HasNonExactFieldOffsetRange(PartyFieldReference fieldRef)
        {
            return fieldRef?.FieldOffsetRange != null &&
                   !fieldRef.FieldOffsetRange.IsExact;
        }

        private static bool IsTrackablePartyField(PartyFieldReference fieldRef)
        {
            return fieldRef != null &&
                   (fieldRef.Field != PartyFieldKind.Unknown || IsRawTechnicalPartyField(fieldRef));
        }

        private static string GetRawTechnicalPartyFieldLabel(PartyFieldReference fieldRef)
        {
            string itemSlotLabel = PartyInventorySemantics.GetSlotFieldLabel(fieldRef);
            if (!string.IsNullOrWhiteSpace(itemSlotLabel))
                return itemSlotLabel;

            if (fieldRef?.FieldOffsetRange != null && !fieldRef.FieldOffsetRange.IsExact)
                return $"техническое байтовое поле персонажа +0x{fieldRef.FieldOffsetRange.Min:X2}..+0x{fieldRef.FieldOffsetRange.Max:X2}";

            return fieldRef?.FieldOffset.HasValue == true
                ? $"техническое байтовое поле персонажа +0x{fieldRef.FieldOffset.Value:X2}"
                : null;
        }

        private static byte GetRawTechnicalPartyFieldRelevantMask(PartyEffectOperation operation, byte immediateValue)
        {
            return operation switch
            {
                PartyEffectOperation.BitSet => immediateValue,
                PartyEffectOperation.BitClear => unchecked((byte)~immediateValue),
                PartyEffectOperation.BitToggle => immediateValue,
                _ => 0
            };
        }

        private static byte GetTrackedPartyFieldRelevantMask(PartyFieldKind field,
            PartyEffectOperation operation, byte immediateValue)
        {
            if (PartyFoodSemantics.IsFoodField(field))
                return PartyFoodSemantics.GetRelevantMask(operation, immediateValue);

            if (PartyTemporaryStatSemantics.IsTrackedField(field))
                return PartyTemporaryStatSemantics.GetRelevantMask(operation, immediateValue);

            return PartyTechnicalFieldSemantics.GetRelevantMask(field, operation, immediateValue);
        }

        private static bool IsComparablePartyField(PartyFieldKind field)
        {
            return field == PartyFieldKind.sex ||
                   field == PartyFieldKind.Status ||
                   IsTrackedPartyField(field) ||
                   PartyAlignmentSemantics.IsAlignmentField(field);
        }

        private static string GetPartyFieldLabel(PartyFieldKind field)
        {
            return field switch
            {
                PartyFieldKind.Food => PartyFoodSemantics.FieldLabel,
                PartyFieldKind.MaxHp => "максимальный HP",
                PartyFieldKind.MaxHpLow => "младший байт максимального HP",
                PartyFieldKind.MaxHpHigh => "старший байт максимального HP",
                PartyFieldKind.InnateAlignment => PartyAlignmentSemantics.InnateFieldLabel,
                PartyFieldKind.CurrentAlignment => PartyAlignmentSemantics.CurrentFieldLabel,
                _ when PartyTemporaryStatSemantics.IsTrackedField(field) => PartyTemporaryStatSemantics.GetFieldLabel(field),
                _ when PartyTechnicalFieldSemantics.IsTrackedField(field) => PartyTechnicalFieldSemantics.GetFieldLabel(field),
                _ => null
            };
        }

        private static string GetPartyFieldDebugLabel(PartyFieldKind field)
        {
            return field == PartyFieldKind.Food
                ? PartyFoodSemantics.DebugFieldLabel
                : GetPartyFieldLabel(field);
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

        private List<PartyEffect> BuildTrackedTechnicalFieldEffectsFromBitTransform(PartyMemberReference member,
            PartyFieldKind field, PartyFieldBitTransform bitTransform, uint instructionAddress)
        {
            var effects = new List<PartyEffect>();
            if (!IsTrackedPartyField(field) ||
                bitTransform == null ||
                bitTransform.IsIdentity)
            {
                return effects;
            }

            void AddEffect(PartyEffectOperation operation, byte mask)
            {
                byte trackedMask = PartyTechnicalFieldSemantics.IsTrackedField(field)
                    ? PartyTechnicalFieldSemantics.FilterRelevantMask(field, mask)
                    : mask;

                if (trackedMask == 0)
                    return;

                PartyEffect effect = PartyEffectFactory.CreateTrackedTechnicalFieldBitEffect(
                    member,
                    field,
                    operation,
                    trackedMask,
                    instructionAddress);
                if (effect != null)
                    effects.Add(effect);
            }

            AddEffect(PartyEffectOperation.BitSet, bitTransform.SetMask);
            AddEffect(PartyEffectOperation.BitClear, bitTransform.ClearMask);
            AddEffect(PartyEffectOperation.BitToggle, bitTransform.ToggleMask);

            if (effects.Count == 0)
            {
                PartyEffect unknownEffect = PartyEffectFactory.CreateTrackedTechnicalFieldWriteEffect(
                    member,
                    field,
                    instructionAddress);
                if (unknownEffect != null)
                    effects.Add(unknownEffect);
            }

            return effects;
        }

        private List<PartyEffect> BuildRawTechnicalFieldEffectsFromBitTransform(PartyMemberReference member,
            byte fieldOffset, PartyFieldBitTransform bitTransform, uint instructionAddress)
        {
            var effects = new List<PartyEffect>();
            if (bitTransform == null || bitTransform.IsIdentity)
                return effects;

            void AddEffect(PartyEffectOperation operation, byte mask)
            {
                if (mask == 0)
                    return;

                PartyEffect effect = PartyEffectFactory.CreateRawTechnicalFieldBitEffect(
                    member,
                    fieldOffset,
                    operation,
                    mask,
                    instructionAddress);
                if (effect != null)
                    effects.Add(effect);
            }

            AddEffect(PartyEffectOperation.BitSet, bitTransform.SetMask);
            AddEffect(PartyEffectOperation.BitClear, bitTransform.ClearMask);
            AddEffect(PartyEffectOperation.BitToggle, bitTransform.ToggleMask);

            if (effects.Count == 0)
            {
                PartyEffect unknownEffect = PartyEffectFactory.CreateRawTechnicalFieldWriteEffect(
                    member,
                    fieldOffset,
                    instructionAddress);
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

        private void TrackPartyArithmeticInstruction(X86Instruction insn, RegisterTracker registerTracker,
            PathAnalysisResult result, int callDepth, List<uint> pendingReturnAddresses)
        {
            if (result == null || insn?.Bytes == null || insn.Bytes.Length == 0)
                return;

            byte[] bytes = insn.Bytes;

            if (bytes.Length == 1 && bytes[0] == 0xF8)
            {
                if (result.PendingPartyHpOperation != null)
                    result.PendingPartyHpOperation.SawClc = true;
                if (result.PendingPartySpOperation != null)
                    result.PendingPartySpOperation.SawClc = true;
                return;
            }

            if (bytes.Length >= 2 &&
                bytes[0] == 0xD0 &&
                bytes[1] == 0xE8 &&
                registerTracker.TryGetPartyFieldValue("AL", out var shrField) &&
                IsPartyStatHighField(shrField.Field))
            {
                var shrPending = GetPendingPartyStatOperation(result, shrField.Field);
                if (shrPending != null)
                    shrPending.SawShrHigh = true;
                return;
            }

            if (bytes.Length >= 2 &&
                bytes[0] == 0xD0 &&
                bytes[1] == 0xD8 &&
                registerTracker.TryGetPartyFieldValue("AL", out var rcrField) &&
                IsPartyStatLowField(rcrField.Field))
            {
                var rcrPending = GetPendingPartyStatOperation(result, rcrField.Field);
                if (rcrPending != null)
                    rcrPending.SawRcrLow = true;
                return;
            }

            if (!registerTracker.TryGetPartyFieldValue("AL", out var statFieldRef) ||
                !IsPartyStatField(statFieldRef.Field))
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
                effectiveImmediate = IsPartyStatLowField(statFieldRef.Field)
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
                effectiveImmediate = IsPartyStatLowField(statFieldRef.Field)
                    ? carryInKnown ? (ushort)(rawImmediate + (carryInValue ? 1 : 0)) : (ushort?)null
                    : rawImmediate;
            }

            if (operation == PartyEffectOperation.Unknown)
                return;

            var pendingField = statFieldRef.Clone();
            pendingField.Member = NormalizeMemberForLoopAggregation(statFieldRef.Member, result.LoopSemantic);

            var pending = EnsurePendingPartyStatOperation(
                result,
                pendingField,
                (uint)insn.Address,
                callDepth,
                pendingReturnAddresses);
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

            if (IsPartyStatLowField(statFieldRef.Field))
                pending.LowByteArithmetic = byteArithmetic;
            else
                pending.HighByteArithmetic = byteArithmetic;
        }
        private enum OrderedTextMergeOutcome
        {
            UnchangedExisting = 0,
            UpdatedExisting = 1,
            Added = 2
        }

        private static void AddLegacyText(PathAnalysisResult result, string text, bool isContextual)
        {
            if (result == null || string.IsNullOrEmpty(text))
                return;

            if (isContextual)
                result.ContextTexts.Add(text);
            else
                result.FoundTexts.Add(text);
        }

        private static void RemoveLegacyText(PathAnalysisResult result, string text, bool isContextual)
        {
            if (result == null || string.IsNullOrEmpty(text))
                return;

            if (isContextual)
                result.ContextTexts.Remove(text);
            else
                result.FoundTexts.Remove(text);
        }

        private static bool IsLootContainerIntroEntry(TextEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
                return false;

            if (entry.SemanticKind == TextSemanticKind.LootContainerIntro)
                return true;

            string trimmed = entry.Text.Trim();
            return trimmed.StartsWith("На ячейке находится ", StringComparison.OrdinalIgnoreCase) &&
                   trimmed.EndsWith("в котором лежит:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLootPayloadEntry(TextEntry entry)
        {
            return entry != null && entry.SemanticKind == TextSemanticKind.LootPayload;
        }

        private static bool PromoteOrderedTextMetadata(TextEntry existing, TextEntry candidate)
        {
            if (existing == null || candidate == null)
                return false;

            bool upgradedSemantic = existing.SemanticKind == TextSemanticKind.Unknown &&
                                    candidate.SemanticKind != TextSemanticKind.Unknown;
            bool upgradedEvidence = existing.IsInferred && !candidate.IsInferred;
            bool updated = false;

            if (upgradedSemantic)
            {
                existing.SemanticKind = candidate.SemanticKind;
                updated = true;
            }

            if (upgradedEvidence)
            {
                existing.IsInferred = false;
                updated = true;
            }

            if ((upgradedSemantic || upgradedEvidence) && candidate.Address != 0)
            {
                existing.Address = candidate.Address;
                updated = true;
            }

            return updated;
        }

        private static void ReplaceOrderedTextEntry(PathAnalysisResult result, TextEntry existing, TextEntry replacement,
            HashSet<string> foundTextsInThisPath)
        {
            if (result == null || existing == null || replacement == null)
                return;

            string previousText = existing.Text;
            RemoveLegacyText(result, previousText, existing.IsContextual);
            foundTextsInThisPath?.Remove(previousText);

            existing.Text = replacement.Text;
            existing.Address = replacement.Address != 0 ? replacement.Address : existing.Address;
            existing.SemanticKind = replacement.SemanticKind != TextSemanticKind.Unknown
                ? replacement.SemanticKind
                : existing.SemanticKind;
            existing.IsInferred = replacement.IsInferred;

            AddLegacyText(result, existing.Text, existing.IsContextual);
            foundTextsInThisPath?.Add(existing.Text);
        }

        private bool TryReplacePendingInferredLootContainerIntro(PathAnalysisResult result, TextEntry candidate,
            HashSet<string> foundTextsInThisPath, bool debugMode)
        {
            if (result?.OrderedTexts == null || candidate == null)
                return false;

            if (candidate.SemanticKind != TextSemanticKind.LootContainerIntro || candidate.IsInferred)
                return false;

            for (int i = result.OrderedTexts.Count - 1; i >= 0; i--)
            {
                var existing = result.OrderedTexts[i];
                if (existing == null)
                    continue;

                if (existing.IsContextual != candidate.IsContextual)
                    continue;

                if (IsLootContainerIntroEntry(existing))
                {
                    if (!existing.IsInferred)
                        return false;

                    string previousText = existing.Text;
                    ReplaceOrderedTextEntry(result, existing, candidate, foundTextsInThisPath);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"        Явный контейнер заменил ранее выведенный неявный контейнер: {previousText} -> {candidate.Text}");
                    }

                    return true;
                }

                if (!IsLootPayloadEntry(existing))
                    return false;
            }

            return false;
        }

        private OrderedTextMergeOutcome UpsertOrderedText(PathAnalysisResult result, string text, bool isContextual,
            uint instructionAddress, ref int textOrderCounter, TextSemanticKind semanticKind = TextSemanticKind.Unknown,
            bool isInferred = false, HashSet<string> foundTextsInThisPath = null, bool debugMode = false)
        {
            if (result == null || string.IsNullOrEmpty(text))
                return OrderedTextMergeOutcome.UnchangedExisting;

            var candidate = new TextEntry
            {
                Text = text,
                IsContextual = isContextual,
                Address = instructionAddress,
                SemanticKind = semanticKind,
                IsInferred = isInferred
            };

            var existingExact = result.OrderedTexts.FirstOrDefault(entry =>
                entry != null &&
                entry.IsContextual == isContextual &&
                string.Equals(entry.Text, text, StringComparison.Ordinal));

            if (existingExact != null)
            {
                bool updated = PromoteOrderedTextMetadata(existingExact, candidate);
                AddLegacyText(result, text, isContextual);
                foundTextsInThisPath?.Add(text);
                return updated ? OrderedTextMergeOutcome.UpdatedExisting : OrderedTextMergeOutcome.UnchangedExisting;
            }

            var existingEquivalentOverlayText = result.OrderedTexts.FirstOrDefault(entry =>
                entry != null &&
                IsEquivalentOverlayTextEntry(entry.Text, text));

            if (existingEquivalentOverlayText != null)
            {
                bool updated = PromoteOrderedTextMetadata(existingEquivalentOverlayText, candidate);
                AddLegacyText(result, text, isContextual);
                foundTextsInThisPath?.Add(text);
                return updated ? OrderedTextMergeOutcome.UpdatedExisting : OrderedTextMergeOutcome.UnchangedExisting;
            }

            if (TryReplacePendingInferredLootContainerIntro(result, candidate, foundTextsInThisPath, debugMode))
                return OrderedTextMergeOutcome.UpdatedExisting;

            AddLegacyText(result, text, isContextual);
            AddOrderedText(result, text, isContextual, instructionAddress, ref textOrderCounter, semanticKind, isInferred);
            foundTextsInThisPath?.Add(text);
            return OrderedTextMergeOutcome.Added;
        }

        private void AddOrderedText(PathAnalysisResult result, string text, bool isContextual, uint instructionAddress, ref int textOrderCounter)
        {
            AddOrderedText(result, text, isContextual, instructionAddress, ref textOrderCounter,
                TextSemanticKind.Unknown, isInferred: false);
        }

        private void AddOrderedText(PathAnalysisResult result, string text, bool isContextual, uint instructionAddress,
            ref int textOrderCounter, TextSemanticKind semanticKind, bool isInferred)
        {
            if (string.IsNullOrEmpty(text))
                return;

            result.OrderedTexts.Add(new TextEntry
            {
                Text = text,
                IsContextual = isContextual,
                Address = instructionAddress,
                Order = textOrderCounter++,
                SemanticKind = semanticKind,
                IsInferred = isInferred
            });
        }

        private void AddSyntheticOutcomeText(PathAnalysisResult result, string text, int callDepth,
            uint instructionAddress, bool debugMode, ref int textOrderCounter,
            TextSemanticKind semanticKind = TextSemanticKind.Unknown, bool isInferred = false)
        {
            if (result == null || string.IsNullOrEmpty(text))
                return;

            bool isContextual = callDepth > 0;
            var mergeOutcome = UpsertOrderedText(
                result,
                text,
                isContextual,
                instructionAddress,
                ref textOrderCounter,
                semanticKind,
                isInferred,
                foundTextsInThisPath: null,
                debugMode: debugMode);

            result.HasSignificantCode = true;

            if (mergeOutcome == OrderedTextMergeOutcome.Added &&
                !isContextual &&
                result.FirstLocalTextAddress == uint.MaxValue)
            {
                result.FirstLocalTextAddress = instructionAddress;
                if (debugMode)
                    AnalysisDebug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
            }

            if (debugMode && mergeOutcome == OrderedTextMergeOutcome.Added)
            {
                string scope = isContextual ? "контекстный" : "локальный";
                AnalysisDebug.WriteLine($"        Синтетически добавлен {scope} outcome-текст: {text}");
            }
        }

        private bool TryAddDisplayedTextFromExternalCall(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, PathAnalysisResult result, byte targetX, byte targetY, int callDepth,
            bool debugMode, ref int textOrderCounter)
        {
            string mnemonicUpper = insn?.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            if (!mnemonicUpper.StartsWith("CALL", StringComparison.Ordinal))
                return false;

            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
            if (!IsActiveTextDisplayRoutine(callTarget))
                return false;

            if (!TryResolveTrackedWordValue(br, ACTIVE_TEXT_POINTER_ADDRESS, result, targetX, targetY, out ushort textAddress))
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"        CALL 0x{callTarget:X4}: не удалось прочитать указатель текста из [0x{ACTIVE_TEXT_POINTER_ADDRESS:X4}]");

                return false;
            }

            string text = ExtractText(br, textAddress);
            if (string.IsNullOrEmpty(text) ||
                text == "(empty string)" ||
                text.StartsWith("Cannot locate", StringComparison.OrdinalIgnoreCase))
            {
                if (debugMode)
                    AnalysisDebug.WriteLine($"        CALL 0x{callTarget:X4}: указатель [0x{ACTIVE_TEXT_POINTER_ADDRESS:X4}] = 0x{textAddress:X4}, текст не найден");

                return false;
            }

            if (TryAddDisplayedTextPointerTableAlternatives(
                    br,
                    registerTracker,
                    result,
                    textAddress,
                    callTarget,
                    callDepth,
                    (uint)insn.Address,
                    debugMode,
                    ref textOrderCounter))
            {
                return true;
            }

            if (HasOverlayTextEntry(result, textAddress, text) ||
                callTarget == POSITIONED_TEXT_ROUTINE_ADDRESS && HasOverlayTextEntryWithSameVisibleText(result, text))
            {
                return false;
            }

            AddSyntheticOutcomeText(
                result,
                $"Text at 0x{textAddress:X4}: {text}",
                callDepth,
                (uint)insn.Address,
                debugMode,
                ref textOrderCounter);

            return true;
        }

        private bool TryAddDisplayedTextPointerTableAlternatives(BinaryReader br, RegisterTracker registerTracker,
            PathAnalysisResult result, ushort currentTextAddress, uint callTarget, int callDepth,
            uint instructionAddress, bool debugMode, ref int textOrderCounter)
        {
            if (!ShouldExpandDisplayedTextPointerTable(registerTracker, result) ||
                !TryGetActiveTextPointerTableBase(registerTracker, currentTextAddress, out ushort tableBase))
            {
                return false;
            }

            var options = ReadTextPointerTableOptions(br, tableBase);
            if (options.Count <= 1 || options.All(option => option.Address != currentTextAddress))
                return false;

            bool addedAny = false;
            foreach (var option in options)
            {
                if (HasOverlayTextEntry(result, option.Address, option.Text) ||
                    callTarget == POSITIONED_TEXT_ROUTINE_ADDRESS &&
                    HasOverlayTextEntryWithSameVisibleText(result, option.Text))
                {
                    continue;
                }

                AddSyntheticOutcomeText(
                    result,
                    $"Text at 0x{option.Address:X4}: {option.Text}",
                    callDepth,
                    instructionAddress,
                    debugMode,
                    ref textOrderCounter);
                addedAny = true;
            }

            if (addedAny && debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        CALL 0x{callTarget:X4}: раскрыта таблица текстовых указателей 0x{tableBase:X4}, вариантов: {options.Count}");
            }

            return addedAny;
        }

        private static bool ShouldExpandDisplayedTextPointerTable(RegisterTracker registerTracker, PathAnalysisResult result)
        {
            if (registerTracker == null)
                return false;

            if (registerTracker.IsFromTable("AX") ||
                registerTracker.GetSourceIndexExternallyDerived("AX") ||
                registerTracker.HasPendingExternalCallResult("AX") ||
                registerTracker.IsRegisterExternallyDerived("AX"))
            {
                return true;
            }

            return result?.InlineBranchChoices?.Any(choice =>
                choice != null &&
                string.Equals(choice.CompareRegister, "AL", StringComparison.OrdinalIgnoreCase) &&
                choice.CompareValue.HasValue &&
                choice.CompareValue.Value >= (byte)'0' &&
                choice.CompareValue.Value <= (byte)'9') == true;
        }

        private bool TryGetActiveTextPointerTableBase(RegisterTracker registerTracker,
            ushort currentTextAddress, out ushort tableBase)
        {
            tableBase = 0;

            if (registerTracker == null ||
                !registerTracker.TryGetRegisterValue("AX", out ushort axValue) ||
                axValue != currentTextAddress ||
                !registerTracker.IsFromTable("AX"))
            {
                return false;
            }

            ushort? sourceAddress = registerTracker.GetSourceAddress("AX");
            if (!sourceAddress.HasValue || sourceAddress.Value < OvrFileConfig.OverlayTextStartAddress)
                return false;

            ushort originalIndex = registerTracker.GetOriginalBx("AX") ?? 0;
            int baseAddress = sourceAddress.Value - originalIndex;
            if (baseAddress < OvrFileConfig.OverlayTextStartAddress || baseAddress > 0xFFFF)
                return false;

            tableBase = (ushort)baseAddress;
            return true;
        }

        private List<(ushort Address, string Text)> ReadTextPointerTableOptions(BinaryReader br, ushort tableBase)
        {
            var options = new List<(ushort Address, string Text)>();
            var seenAddresses = new HashSet<ushort>();

            for (int i = 0; i < MAX_TEXT_POINTER_TABLE_OPTIONS; i++)
            {
                ushort pointerAddress = unchecked((ushort)(tableBase + i * 2));
                if (!TryReadOverlayWord(br, pointerAddress, out ushort textAddress) ||
                    textAddress < OvrFileConfig.OverlayTextStartAddress ||
                    !seenAddresses.Add(textAddress))
                {
                    break;
                }

                string text = ExtractText(br, textAddress);
                if (string.IsNullOrEmpty(text) ||
                    text == "(empty string)" ||
                    text.StartsWith("Cannot locate", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                options.Add((textAddress, text));
            }

            return options;
        }

        private static bool IsActiveTextDisplayRoutine(uint callTarget)
        {
            return callTarget == DISPLAY_TEXT_ROUTINE_ADDRESS ||
                   callTarget == POSITIONED_TEXT_ROUTINE_ADDRESS;
        }

        private void AdvancePositionedTextCursorForExternalCall(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, PathAnalysisResult result, byte targetX, byte targetY, bool debugMode)
        {
            if (insn == null ||
                !TryResolveTrackedWordValue(br, ACTIVE_TEXT_POINTER_ADDRESS, result, targetX, targetY, out ushort textAddress))
            {
                return;
            }

            string text = ExtractText(br, textAddress);
            if (!TryGetSingleLineDisplayAdvance(text, out int advance) || advance <= 0)
                return;

            if (!TryResolveTrackedByteValue(br, TEXT_CURSOR_COLUMN_ADDRESS, result, targetX, targetY, out byte currentColumn))
                return;

            byte nextColumn = (byte)Math.Min(byte.MaxValue, currentColumn + advance);
            ApplyTrackedByteWrite(
                TEXT_CURSOR_COLUMN_ADDRESS,
                nextColumn,
                result,
                targetX,
                targetY,
                insn,
                debugMode,
                $"CALL 0x{POSITIONED_TEXT_ROUTINE_ADDRESS:X4}: advance text cursor by {advance}");

            registerTracker?.TrackPartialRegisterOperation(
                "AX",
                "AL",
                nextColumn,
                (uint)insn.Address,
                $"CALL 0x{POSITIONED_TEXT_ROUTINE_ADDRESS:X4}: cursor column");
        }

        private static bool TryGetSingleLineDisplayAdvance(string text, out int advance)
        {
            advance = 0;
            if (string.IsNullOrEmpty(text) ||
                text == "(empty string)" ||
                text.StartsWith("Cannot locate", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch != '\\')
                {
                    advance++;
                    continue;
                }

                if (i + 1 >= text.Length)
                    return false;

                char escaped = text[++i];
                switch (escaped)
                {
                    case 'r':
                    case 'n':
                        return false;
                    case 't':
                    case '"':
                    case '\\':
                        advance++;
                        break;
                    case 'x':
                        if (i + 2 >= text.Length ||
                            !IsHexDigit(text[i + 1]) ||
                            !IsHexDigit(text[i + 2]))
                        {
                            return false;
                        }

                        i += 2;
                        advance++;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        private static bool IsHexDigit(char ch)
        {
            return (ch >= '0' && ch <= '9') ||
                   (ch >= 'A' && ch <= 'F') ||
                   (ch >= 'a' && ch <= 'f');
        }

        private static bool HasOverlayTextEntry(PathAnalysisResult result, ushort textAddress, string text)
        {
            if (result?.OrderedTexts == null || string.IsNullOrEmpty(text))
                return false;

            string candidateText = $"Text at 0x{textAddress:X4}: {text}";

            return result.OrderedTexts.Any(entry =>
                entry != null &&
                IsEquivalentOverlayTextEntry(entry.Text, candidateText));
        }

        private static bool HasOverlayTextEntryWithSameVisibleText(PathAnalysisResult result, string text)
        {
            if (result?.OrderedTexts == null || string.IsNullOrEmpty(text))
                return false;

            return result.OrderedTexts.Any(entry =>
                entry != null &&
                TrySplitOverlayTextEntry(entry.Text, out _, out string existingText) &&
                string.Equals(existingText, text, StringComparison.Ordinal));
        }

        private static bool IsEquivalentOverlayTextEntry(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;

            if (string.Equals(left, right, StringComparison.Ordinal))
                return true;

            return TrySplitOverlayTextEntry(left, out ushort leftAddress, out string leftText) &&
                   TrySplitOverlayTextEntry(right, out ushort rightAddress, out string rightText) &&
                   leftAddress == rightAddress &&
                   string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        private static bool TrySplitOverlayTextEntry(string rawText, out ushort textAddress, out string visibleText)
        {
            textAddress = 0;
            visibleText = string.Empty;

            const string prefix = "Text at 0x";
            if (string.IsNullOrEmpty(rawText) ||
                !rawText.StartsWith(prefix, StringComparison.Ordinal) ||
                rawText.Length < prefix.Length + 4)
            {
                return false;
            }

            string addressText = rawText.Substring(prefix.Length, 4);
            if (!ushort.TryParse(addressText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out textAddress))
                return false;

            int separatorIndex = rawText.IndexOf(": ", prefix.Length + 4, StringComparison.Ordinal);
            if (separatorIndex < 0)
                return false;

            visibleText = rawText.Substring(separatorIndex + 2);
            return true;
        }

        private bool TryAddSyntheticRangeLootText(BinaryReader br, PathAnalysisResult result, ushort memAddr,
            ValueRange8 valueRange, byte targetX, byte targetY, uint instructionAddress, int callDepth,
            bool debugMode, ref int textOrderCounter)
        {
            if (result == null || valueRange == null)
                return false;

            string syntheticText = null;

            if (memAddr == 0x3C7F)
            {
                syntheticText = FormatRangeText(valueRange.Min, valueRange.Max, "GEMS");
            }
            else if (memAddr == 0x3C7D)
            {
                if (!TryResolveTrackedByteValue(br, 0x3C7E, result, targetX, targetY, out byte highByte))
                {
                    highByte = 0x00;
                    if (debugMode)
                        AnalysisDebug.WriteLine("        Для диапазонного GOLD не найден [0x3C7E], предполагаем старший байт 0x00");
                }

                ushort rawMin = (ushort)(valueRange.Min | (highByte << 8));
                ushort rawMax = (ushort)(valueRange.Max | (highByte << 8));
                syntheticText = FormatGoldRangeText(rawMin, rawMax);
            }
            else if (memAddr == 0x3C7E)
            {
                if (!TryResolveTrackedByteValue(br, 0x3C7D, result, targetX, targetY, out byte lowByte))
                {
                    lowByte = 0x00;
                    if (debugMode)
                        AnalysisDebug.WriteLine("        Для диапазонного GOLD не найден [0x3C7D], предполагаем младший байт 0x00");
                }

                ushort rawMin = (ushort)(lowByte | (valueRange.Min << 8));
                ushort rawMax = (ushort)(lowByte | (valueRange.Max << 8));
                syntheticText = FormatGoldRangeText(rawMin, rawMax);
            }

            if (string.IsNullOrEmpty(syntheticText))
                return false;

            // Range-loot для gems/gold может распознаться только здесь, уже после
            // стадии FindTextsInInstruction. Если путь заканчивается сразу после
            // этой записи, отдельная строка контейнера иначе не успевает появиться.
            AddImplicitContainerIntroForSyntheticLoot(
                result,
                instructionAddress,
                callDepth,
                debugMode,
                ref textOrderCounter);

            AddSyntheticOutcomeText(result, syntheticText, callDepth, instructionAddress, debugMode, ref textOrderCounter,
                TextSemanticKind.LootPayload);
            return true;
        }

        private void AddImplicitContainerIntroForSyntheticLoot(PathAnalysisResult result, uint instructionAddress,
            int callDepth, bool debugMode, ref int textOrderCounter)
        {
            if (result == null)
                return;

            if (_emulatedMemory8.ContainsKey(0x3C79))
                return;

            if (HasContainerIntroText(result))
                return;

            string containerText = BuildImplicitContainerIntroText();
            if (string.IsNullOrEmpty(containerText))
                return;

            AddSyntheticOutcomeText(result, containerText, callDepth, instructionAddress, debugMode, ref textOrderCounter,
                TextSemanticKind.LootContainerIntro, isInferred: true);
        }

        private static bool HasContainerIntroText(PathAnalysisResult result)
        {
            if (result?.OrderedTexts == null)
                return false;

            return result.OrderedTexts.Any(IsLootContainerIntroEntry);
        }

        private static string BuildImplicitContainerIntroText()
        {
            if (ContainerDatabase.TryGetContainerName(0x00, out string containerName))
                return $"На ячейке находится {containerName} в котором лежит:";

            return "На ячейке находится контейнер #0 в котором лежит:";
        }

        private static string FormatGoldRangeText(ushort rawMin, ushort rawMax)
        {
            int goldMin = rawMin >> 1;
            int goldMax = rawMax >> 1;
            return FormatRangeText(goldMin, goldMax, "GOLD");
        }

        private static string FormatRangeText(int minValue, int maxValue, string suffix)
        {
            return minValue == maxValue
                ? $"{minValue} {suffix}"
                : $"{minValue}-{maxValue} {suffix}";
        }

        private void MaterializePendingExactLootTexts(PathAnalysisResult result, uint instructionAddress,
            int callDepth, bool debugMode)
        {
            if (result == null)
                return;

            MaterializePendingExactGoldText(result, instructionAddress, callDepth, debugMode);
        }

        private void MaterializePendingExactGoldText(PathAnalysisResult result, uint instructionAddress,
            int callDepth, bool debugMode)
        {
            bool hasLowByte = _emulatedMemory8.TryGetValue(0x3C7D, out byte lowByte);
            bool hasHighByte = _emulatedMemory8.TryGetValue(0x3C7E, out byte highByte);

            if (!hasLowByte && !hasHighByte)
                return;

            ushort rawGoldValue = (ushort)((hasLowByte ? lowByte : (byte)0x00) |
                                           ((hasHighByte ? highByte : (byte)0x00) << 8));
            if (rawGoldValue == 0)
                return;

            if (HasRecognizedGoldOutcomeText(result))
                return;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Финализация GOLD по tracked-state: low={(hasLowByte ? $"0x{lowByte:X2}" : "0x00 (default)")}, high={(hasHighByte ? $"0x{highByte:X2}" : "0x00 (default)")} -> {rawGoldValue >> 1} GOLD");
            }

            int textOrderCounter = result.OrderedTexts.Count == 0
                ? 0
                : result.OrderedTexts.Max(t => t.Order) + 1;

            AddImplicitContainerIntroForSyntheticLoot(
                result,
                instructionAddress,
                callDepth,
                debugMode,
                ref textOrderCounter);

            AddSyntheticOutcomeText(
                result,
                FormatGoldRangeText(rawGoldValue, rawGoldValue),
                callDepth,
                instructionAddress,
                debugMode,
                ref textOrderCounter,
                TextSemanticKind.LootPayload);
        }

        private static bool HasRecognizedGoldOutcomeText(PathAnalysisResult result)
        {
            if (result?.OrderedTexts == null || result.OrderedTexts.Count == 0)
                return false;

            return result.OrderedTexts.Any(entry => IsGoldOutcomeText(entry?.Text));
        }

        private static bool IsGoldOutcomeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            if (trimmed.Equals("!!! GOLD на полу уничтожено !!!", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!trimmed.EndsWith(" GOLD", StringComparison.OrdinalIgnoreCase))
                return false;

            string numericPart = trimmed.Substring(0, trimmed.Length - " GOLD".Length).Trim();
            if (numericPart.Length == 0)
                return false;

            return numericPart.All(ch => char.IsDigit(ch) || ch == '-');
        }

        private void MergeOrderedTextsFromSubroutine(PathAnalysisResult result, PathAnalysisResult subroutineResult)
        {
            int nextOrder = result.OrderedTexts.Count == 0 ? 0 : result.OrderedTexts.Max(t => t.Order) + 1;

            foreach (var entry in subroutineResult.OrderedTexts.OrderBy(t => t.Order))
            {
                if (entry == null || string.IsNullOrEmpty(entry.Text))
                    continue;

                UpsertOrderedText(
                    result,
                    entry.Text,
                    entry.IsContextual,
                    entry.Address,
                    ref nextOrder,
                    entry.SemanticKind,
                    entry.IsInferred);
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
                        var mergeOutcome = UpsertOrderedText(
                            result,
                            textEntry,
                            false,
                            (uint)insn.Address,
                            ref textOrderCounter,
                            foundTextsInThisPath: foundTextsInThisPath,
                            debugMode: debugMode);

                        if (mergeOutcome == OrderedTextMergeOutcome.Added)
                        {
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
        private void ProcessFoundTexts(List<TextEntry> newTexts, HashSet<string> foundTextsInThisPath,
            PathAnalysisResult result, int callDepth, X86Instruction insn, bool debugMode, ref int textOrderCounter)
        {
            foreach (var entry in newTexts ?? Enumerable.Empty<TextEntry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.Text))
                    continue;

                bool isContextual = callDepth > 0;
                uint sourceAddress = entry.Address != 0 ? entry.Address : (uint)insn.Address;
                var mergeOutcome = UpsertOrderedText(
                    result,
                    entry.Text,
                    isContextual,
                    sourceAddress,
                    ref textOrderCounter,
                    entry.SemanticKind,
                    entry.IsInferred,
                    foundTextsInThisPath,
                    debugMode);

                if (mergeOutcome != OrderedTextMergeOutcome.Added)
                    continue;

                if (!isContextual && result.FirstLocalTextAddress == uint.MaxValue)
                {
                    result.FirstLocalTextAddress = sourceAddress;
                    if (debugMode)
                        AnalysisDebug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
                }

                if (debugMode)
                {
                    if (isContextual)
                        AnalysisDebug.WriteLine($"        Найден контекстный текст (из CALL): {entry.Text}");
                    else
                        AnalysisDebug.WriteLine($"        Найден прямой текст: {entry.Text}");
                }
            }
        }

        private static int GetPrintedCharTemplateInsertionIndex(string text)
        {
            if (string.IsNullOrEmpty(text))
                return -1;

            int payloadStart = 0;
            if (text.StartsWith("Text at 0x", StringComparison.Ordinal))
            {
                int colonIndex = text.IndexOf(": ", StringComparison.Ordinal);
                if (colonIndex >= 0)
                    payloadStart = colonIndex + 2;
            }

            for (int i = payloadStart; i < text.Length - 1; i++)
            {
                if (text[i] != '#')
                    continue;

                if (!char.IsWhiteSpace(text[i + 1]))
                    continue;

                return i + 1;
            }

            return -1;
        }

        private static bool ShouldInsertSpaceBeforePrintedChar(string previousText, char visibleChar, int insertionIndex)
        {
            if (insertionIndex >= 0 || !char.IsDigit(visibleChar) || string.IsNullOrEmpty(previousText))
                return false;

            int lastVisibleIndex = previousText.Length - 1;
            while (lastVisibleIndex >= 0 && char.IsWhiteSpace(previousText[lastVisibleIndex]))
                lastVisibleIndex--;

            if (lastVisibleIndex < 0)
                return false;

            return char.IsLetter(previousText[lastVisibleIndex]);
        }

        private void AppendPrintedCharToLastText(PathAnalysisResult result, RegisterTracker registerTracker,
            uint instructionAddress, bool debugMode)
        {
            if (result == null || registerTracker == null)
                return;

            if (!registerTracker.TryGetByteRegisterValue("AL", out byte charValue))
                return;

            if (!InlineNoteStyleCodec.TryDecodePrintableOverlayChar(charValue, out char visibleChar, out bool isInverse))
                return;

            var lastText = result.OrderedTexts
                .Where(t => t != null &&
                            !string.IsNullOrEmpty(t.Text) &&
                            TrySplitOverlayTextEntry(t.Text, out _, out _))
                .OrderBy(t => t.Order)
                .LastOrDefault();

            if (lastText == null)
                return;

            string previousText = lastText.Text;
            int insertionIndex = GetPrintedCharTemplateInsertionIndex(previousText);
            string encodedChar = InlineNoteStyleCodec.EncodePrintableChar(visibleChar, isInverse);
            string prefix = ShouldInsertSpaceBeforePrintedChar(previousText, visibleChar, insertionIndex) ? " " : string.Empty;
            string updatedText = insertionIndex >= 0
                ? previousText.Insert(insertionIndex, encodedChar)
                : previousText + prefix + encodedChar;

            lastText.Text = updatedText;
            lastText.Address = instructionAddress;

            RemoveLegacyText(result, previousText, lastText.IsContextual);
            AddLegacyText(result, updatedText, lastText.IsContextual);

            if (debugMode)
            {
                string action = insertionIndex >= 0
                    ? "Вставили символ"
                    : "Дописали символ";
                string target = insertionIndex >= 0
                    ? "в шаблон последнего текста"
                    : "к последнему тексту";
                string inverseSuffix = isInverse ? " [inverse]" : string.Empty;
                AnalysisDebug.WriteLine($"        {action} '{visibleChar}'{inverseSuffix} {target} -> {updatedText}");
            }
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

                    CompletePendingPartyStatOperationsForReturnAddress(result, returnAddress, callDepth);

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
                return HandleJmpOpcodeE9(insn, br, fileLength, debugMode, result, currentAddress, visitedInThisPath, registerTracker);

            if (insn.Bytes.Length >= 2 && insn.Bytes[0] == 0xEB)
                return HandleShortJmp(insn, br, fileLength, debugMode, result, currentAddress, visitedInThisPath, registerTracker);

            if (mnemonicUpper == "JMP" || mnemonicUpper == "JMPF" || mnemonicUpper == "JMPL" ||
                mnemonicUpper == "JMPE" || mnemonicUpper == "JMPI")
            {
                return HandleJmp(insn, br, fileLength, debugMode, result, currentAddress, visitedInThisPath, registerTracker);
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
                    targetX, targetY,
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

        private void MarkRandomEncounterJump(PathAnalysisResult result, uint instructionAddress, bool debugMode, string jumpKind)
        {
            if (result == null)
                return;

            bool hasFixedBattleSetup =
                result.BattleMonsterCount.HasValue ||
                result.BattleMonsterCountRange != null ||
                result.IsBattleMonsterCountIndeterminate ||
                (result.BattleMonsterEntries != null && result.BattleMonsterEntries.Values.Any(entry => (entry.val1 != 0 && entry.val2 != 0) || entry.isIndeterminate)) ||
                (result.PartialBattles != null && result.PartialBattles.Count > 0) ||
                result.HasPartialBattlePattern ||
                (result.PartialBattleInfo != null && result.PartialBattleInfo.Count > 0);

            bool hasOtherEffects =
                (result.OrderedTexts != null && result.OrderedTexts.Count > 0) ||
                result.RandomEncounterMonsterPowerCap.HasValue ||
                result.RandomEncounterMonsterLevelCap.HasValue ||
                result.RandomEncounterMonsterBatchCountCap.HasValue ||
                result.DarkeningLevel.HasValue ||
                result.RandomEncounterChance.HasValue ||
                result.RandomEncounterRubicon.HasValue ||
                result.HasTeleportTarget ||
                hasFixedBattleSetup ||
                (result.PartyEffects != null && result.PartyEffects.Count > 0);

            result.CallsRandomEncounter = true;
            result.IsOnlyRandomEncounterJump = !hasOtherEffects;
            if (result.RandomEncounterInstructionAddress == 0 || instructionAddress < result.RandomEncounterInstructionAddress)
                result.RandomEncounterInstructionAddress = instructionAddress;
            if (result.RandomEncounterExecutionOrder == 0)
                result.RandomEncounterExecutionOrder = ++result.NextSpecialEventOrder;
            result.HasSignificantCode = true;

            if (debugMode)
            {
                string suffix = result.IsOnlyRandomEncounterJump
                    ? " без иных эффектов"
                    : " с дополнительными эффектами";
                string routineDescription = hasFixedBattleSetup
                    ? "общей процедуры боя/encounter"
                    : "random encounter";
                AnalysisDebug.WriteLine($"      Обнаружен вызов {routineDescription} через {jumpKind} к 0x517C{suffix}");
            }
        }

        /// <summary>
        /// Обрабатывает инструкцию JMP
        /// </summary>
        private ControlFlowResult HandleJmp(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress, HashSet<uint> visitedInThisPath,
            RegisterTracker registerTracker)
        {
            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

            if (jumpTarget == 0x517C)
            {
                MarkRandomEncounterJump(result, currentAddress, debugMode, "JMP");
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
                if (TryTerminateRepeatedUnconditionalBackJump(
                        jumpTarget,
                        currentAddress,
                        visitedInThisPath,
                        result,
                        br,
                        fileLength,
                        debugMode,
                        "JMP",
                        registerTracker,
                        out var termination))
                {
                    return termination;
                }

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
            bool debugMode, PathAnalysisResult result, uint currentAddress, HashSet<uint> visitedInThisPath,
            RegisterTracker registerTracker)
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
                    MarkRandomEncounterJump(result, currentAddress, debugMode, "JMP");
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
                    if (TryTerminateRepeatedUnconditionalBackJump(
                            jumpTarget,
                            currentAddress,
                            visitedInThisPath,
                            result,
                            br,
                            fileLength,
                            debugMode,
                            "JMP",
                            registerTracker,
                            out var termination))
                    {
                        return termination;
                    }

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
            bool debugMode, PathAnalysisResult result, uint currentAddress, HashSet<uint> visitedInThisPath,
            RegisterTracker registerTracker)
        {
            if (insn.Bytes.Length >= 2)
            {
                sbyte jumpOffset = (sbyte)insn.Bytes[1];
                uint jumpTarget = (uint)(insn.Address + 2 + jumpOffset);

                if (jumpTarget < fileLength)
                {
                    if (jumpTarget == 0x517C)
                    {
                        MarkRandomEncounterJump(result, currentAddress, debugMode, "SHORT JMP");
                    }

                    if (TryTerminateRepeatedUnconditionalBackJump(
                            jumpTarget,
                            currentAddress,
                            visitedInThisPath,
                            result,
                            br,
                            fileLength,
                            debugMode,
                            "SHORT JMP",
                            registerTracker,
                            out var termination))
                    {
                        return termination;
                    }

                    if (debugMode)
                        AnalysisDebug.WriteLine($"      SHORT JMP к 0x{jumpTarget:X4}");
                    return new ControlFlowResult { ShouldReturn = false, NextAddress = jumpTarget };
                }
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = currentAddress + (uint)insn.Bytes.Length };
        }

        private bool TryTerminateRepeatedUnconditionalBackJump(
            uint jumpTarget,
            uint currentAddress,
            HashSet<uint> visitedInThisPath,
            PathAnalysisResult result,
            BinaryReader br,
            long fileLength,
            bool debugMode,
            string jumpKind,
            RegisterTracker registerTracker,
            out ControlFlowResult controlFlowResult)
        {
            controlFlowResult = null;

            bool returnsToVisitedAddress =
                visitedInThisPath != null &&
                visitedInThisPath.Contains(jumpTarget);
            bool returnsToPromptOrEarlier =
                result != null &&
                result.FirstLocalTextAddress != uint.MaxValue &&
                jumpTarget <= result.FirstLocalTextAddress;
            bool returnsToPromptLoop =
                returnsToPromptOrEarlier &&
                !returnsToVisitedAddress &&
                IsLikelyTextLoadInstructionAt(br, fileLength, jumpTarget);

            bool shouldTerminateAsRepeatedBackEdge =
                returnsToVisitedAddress ||
                returnsToPromptLoop;

            if (jumpTarget >= currentAddress ||
                !shouldTerminateAsRepeatedBackEdge)
            {
                return false;
            }

            if (IsFiniteInputValidationTableBackJump(br, fileLength, jumpTarget, currentAddress, registerTracker))
            {
                if (visitedInThisPath != null)
                {
                    foreach (var visitedAddress in visitedInThisPath
                        .Where(address => address >= jumpTarget && address <= currentAddress)
                        .ToList())
                    {
                        visitedInThisPath.Remove(visitedAddress);
                    }
                }

                if (result != null)
                {
                    result.IsInLoop = true;
                    if (result.LoopStartAddress == 0 || jumpTarget < result.LoopStartAddress)
                        result.LoopStartAddress = jumpTarget;
                    if (currentAddress > result.LoopEndAddress)
                        result.LoopEndAddress = currentAddress;
                }

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"      {jumpKind} validation-loop 0x{currentAddress:X4} -> 0x{jumpTarget:X4}: BL продвигается, разрешаем следующий символ");
                }

                return false;
            }

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      {jumpKind} назад к уже посещённому 0x{jumpTarget:X4}: завершаем текущий путь как повторный цикл");
            }

            if (result != null)
            {
                result.IsTerminated = true;
                result.HasSignificantCode = true;
                result.TerminatedByRepeatedBackEdge = true;
                result.TerminatedByPromptLoopBackEdge = returnsToPromptLoop;
            }

            controlFlowResult = new ControlFlowResult
            {
                ShouldReturn = true,
                Result = result
            };
            return true;
        }

        private bool IsFiniteInputValidationTableBackJump(BinaryReader br, long fileLength,
            uint jumpTarget, uint currentAddress, RegisterTracker registerTracker)
        {
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                currentAddress < 2 ||
                jumpTarget + 7 >= fileLength ||
                !registerTracker.TryGetByteRegisterValue("BL", out byte blValue) ||
                blValue > 0x20)
            {
                return false;
            }

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(currentAddress - 2, SeekOrigin.Begin);
                if (br.ReadByte() != 0xFE || br.ReadByte() != 0xC3)
                    return false;

                br.BaseStream.Seek(jumpTarget, SeekOrigin.Begin);
                if (br.ReadByte() != 0x8A)
                    return false;

                byte modRm = br.ReadByte();
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                if (mod != 0x02 || reg != 0x00 || rm != 0x07)
                    return false;

                br.BaseStream.Seek(jumpTarget + 4, SeekOrigin.Begin);
                return br.ReadByte() == 0x0A &&
                       br.ReadByte() == 0xC0;
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
        }

        private bool IsLikelyTextLoadInstructionAt(BinaryReader br, long fileLength, uint address)
        {
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                address + 2 >= fileLength)
            {
                return false;
            }

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(address, SeekOrigin.Begin);
                byte opcode = br.ReadByte();
                if (opcode != 0xB8)
                    return false;

                ushort textAddress = br.ReadUInt16();
                string text = ExtractText(br, textAddress);
                return !string.IsNullOrEmpty(text) &&
                       text != "(empty string)" &&
                       !text.StartsWith("Cannot locate", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
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
                    registerTracker.SetRegisterRandomUpperBound("AL", n);
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX", RegisterTracker.ExternalCallResultKind.Random);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознана SUB_509A: AL получает псевдослучайное значение в диапазоне 1..{n}");
                }
                else if (registerTracker.TryGetDynamicValueFormula("AL", out var upperBoundFormula) &&
                         upperBoundFormula?.MaxValue > 0)
                {
                    byte maxN = upperBoundFormula.MaxValue.Value;
                    registerTracker.SetRegisterRange("AL", 1, maxN, RegisterValueDistribution.Unknown);
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX", RegisterTracker.ExternalCallResultKind.Random);
                    TrackDynamicRandomBoundDependency(
                        result,
                        upperBoundFormula,
                        "AL",
                        (uint)insn.Address,
                        maxN,
                        debugMode);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"        Распознана SUB_509A: AL получает псевдослучайное значение в диапазоне 1..{upperBoundFormula.GetFormulaExpression()} (верхняя граница зависит от поля партии)");
                    }
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var nRange) &&
                         nRange != null &&
                         nRange.Max > 0)
                {
                    registerTracker.SetRegisterRange("AL", 1, nRange.Max, RegisterValueDistribution.UniformDiscreteRange);
                    registerTracker.SetRegisterRandomUpperBound("AL", nRange.Max);
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX");

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Распознана SUB_509A: AL получает псевдослучайное значение в диапазоне 1..{nRange.Max} (верхняя граница из диапазона)");
                }
                else
                {
                    registerTracker.InvalidateRegister("AX");
                    registerTracker.MarkRegisterAsPendingExternalCallResult("AX", RegisterTracker.ExternalCallResultKind.Random);

                    if (debugMode)
                        AnalysisDebug.WriteLine("        SUB_509A вызвана, но верхняя граница N в AL неизвестна");
                }

                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == 0x5101)
            {
                registerTracker.InvalidateRegister("AX");
                registerTracker.SetRegisterRange("AL", KEYBOARD_INPUT_MIN, KEYBOARD_INPUT_MAX, RegisterValueDistribution.Unknown);
                registerTracker.MarkRegisterAsPendingExternalCallResult("AX", RegisterTracker.ExternalCallResultKind.UserInput);

                if (debugMode)
                    AnalysisDebug.WriteLine("        Распознана SUB_5101: AL получает пользовательский код клавиши");

                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == CURRENT_MAP_EVENT_DISABLE_ROUTINE_ADDRESS)
            {
                result.DisablesCurrentMapEvent = true;
                result.HasSignificantCode = true;

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        "        Распознана SUB_4FC8: сбрасывает бит события текущей клетки во втором слое карты");
                }

                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == 0x4C55)
            {
                AppendPrintedCharToLastText(result, registerTracker, (uint)insn.Address, debugMode);
                return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
            }

            if (callTarget == POSITIONED_TEXT_ROUTINE_ADDRESS)
            {
                AdvancePositionedTextCursorForExternalCall(
                    insn,
                    br,
                    registerTracker,
                    result,
                    targetX,
                    targetY,
                    debugMode);

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
                    _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    initialEmulatedMemory8Ranges: CloneRangeDictionary(_emulatedMemory8Ranges),
                    initialEmulatedMemory8RangeDistributions: new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions),
                    initialPendingPersistentCounterProgressions: ClonePendingPersistentCounterProgressions(result.PendingPersistentCounterProgressions));

                // Добавляем результаты из подпрограммы
                foreach (var visitedAddress in subroutineResult.VisitedAddresses)
                    result.VisitedAddresses.Add(visitedAddress);

                // Переносим тексты из подпрограммы с сохранением порядка и признака контекстности.
                MergeOrderedTextsFromSubroutine(result, subroutineResult);

                if (subroutineResult.RandomEncounterMonsterPowerCap.HasValue && !result.RandomEncounterMonsterPowerCap.HasValue)
                    result.RandomEncounterMonsterPowerCap = subroutineResult.RandomEncounterMonsterPowerCap;

                if (subroutineResult.RandomEncounterMonsterLevelCap.HasValue && !result.RandomEncounterMonsterLevelCap.HasValue)
                    result.RandomEncounterMonsterLevelCap = subroutineResult.RandomEncounterMonsterLevelCap;

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
                if (!result.PartialBattles.Any(p => p.GetIdentityKey() == partial.GetIdentityKey()))
                    result.PartialBattles.Add(partial);

                foreach (var info in subroutineResult.PartialBattleInfo)
                if (!result.PartialBattleInfo.Any(i =>
                    i.BxIndex == info.BxIndex &&
                    i.DestAddr == info.DestAddr &&
                    i.SrcReg == info.SrcReg &&
                    i.SrcRegValue == info.SrcRegValue &&
                    i.ValueMin == info.ValueMin &&
                    i.ValueMax == info.ValueMax &&
                    i.IsFromTable == info.IsFromTable &&
                    i.SourceTableAddr == info.SourceTableAddr &&
                    i.SourceTableBaseAddr == info.SourceTableBaseAddr &&
                    i.SourceTable == info.SourceTable &&
                    i.OriginalSourceIndex == info.OriginalSourceIndex &&
                    i.SourceIndexProviderAddr == info.SourceIndexProviderAddr &&
                    i.SourceIndexBehavior == info.SourceIndexBehavior &&
                    i.SourceIndexExternallyDerived == info.SourceIndexExternallyDerived))
                    result.PartialBattleInfo.Add(info);

                result.HasPartialBattlePattern = result.HasPartialBattlePattern || subroutineResult.HasPartialBattlePattern;
                bool subroutineReturnedToCaller = HasReturnedFromCurrentCall(
                    subroutineResult,
                    pendingReturnAddresses,
                    nextAddress,
                    callDepth);
                MergeSubroutineAnalysisState(result, subroutineResult, subroutineReturnedToCaller);

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
                            CompareMemoryAddress = altPath.CompareMemoryAddress,
                            ComparedPartyField = altPath.ComparedPartyField?.Clone(),
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
                            EmulatedMemory8Ranges = CloneRangeDictionary(altPath.EmulatedMemory8Ranges),
                            EmulatedMemory8RangeDistributions = altPath.EmulatedMemory8RangeDistributions == null
                                ? new Dictionary<ushort, RegisterValueDistribution>()
                                : new Dictionary<ushort, RegisterValueDistribution>(altPath.EmulatedMemory8RangeDistributions),
                            EmulatedPartyPointers = altPath.EmulatedPartyPointers == null
                                ? new Dictionary<ushort, PartyMemberReference>()
                                : altPath.EmulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            EmulatedPartyPointerBytes = altPath.EmulatedPartyPointerBytes == null
                                ? new Dictionary<ushort, PartyPointerByteReference>()
                                : altPath.EmulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(altPath.PendingPersistentCounterProgressions),
                            BranchStateValueConstraints = CloneStateValueConstraints(altPath.BranchStateValueConstraints),
                            BranchLocallyMaterializedStateValueConstraintAddresses =
                                altPath.BranchLocallyMaterializedStateValueConstraintAddresses == null
                                    ? new HashSet<ushort>()
                                    : new HashSet<ushort>(altPath.BranchLocallyMaterializedStateValueConstraintAddresses),
                            BranchPartyCondition = altPath.BranchPartyCondition,
                            BranchPartyPredicate = altPath.BranchPartyPredicate?.Clone()
                        });
                    }
                }

                result.IsTerminated = subroutineResult.IsTerminated;
                result.HasSignificantCode = result.HasSignificantCode || subroutineResult.HasSignificantCode;
                result.IsOnlyRandomEncounterJump = result.IsOnlyRandomEncounterJump || subroutineResult.IsOnlyRandomEncounterJump;
                result.UsesInitialCoordinates = result.UsesInitialCoordinates || subroutineResult.UsesInitialCoordinates;
                result.UsesStaticMapData = result.UsesStaticMapData || subroutineResult.UsesStaticMapData;
                MergeStaticMapDataReads(result.StaticMapDataReads, subroutineResult.StaticMapDataReads);
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
                registerTracker.MarkRegisterAsPendingExternalCallResult("AX", RegisterTracker.ExternalCallResultKind.UserInput);

                if (debugMode)
                    AnalysisDebug.WriteLine("        Помечаем AX/AL как кандидата на зависимость от внешнего CALL для табличного объекта");
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }

        private static bool HasReturnedFromCurrentCall(
            PathAnalysisResult subroutineResult,
            List<uint> callerPendingReturnAddresses,
            uint currentReturnAddress,
            int callerCallDepth)
        {
            if (subroutineResult == null)
                return false;

            bool currentReturnStillPending =
                subroutineResult.ExitPendingReturnAddresses?.Contains(currentReturnAddress) == true;
            bool callDepthStillNested = subroutineResult.ExitCallDepth > callerCallDepth;

            return !currentReturnStillPending && !callDepthStillNested;
        }

        private void CompletePendingPartyStatOperationsForReturnAddress(
            PathAnalysisResult result,
            uint returnAddress,
            int callDepth)
        {
            if (result == null)
                return;

            result.PendingPartyHpOperation = CompletePendingPartyStatOperationForReturnAddress(
                result,
                result.PendingPartyHpOperation,
                PartyFieldKind.Hp,
                returnAddress,
                callDepth);

            result.PendingPartySpOperation = CompletePendingPartyStatOperationForReturnAddress(
                result,
                result.PendingPartySpOperation,
                PartyFieldKind.Sp,
                returnAddress,
                callDepth);
        }

        private PendingPartyStatOperation CompletePendingPartyStatOperationForReturnAddress(
            PathAnalysisResult result,
            PendingPartyStatOperation pending,
            PartyFieldKind fallbackField,
            uint returnAddress,
            int callDepth)
        {
            if (pending == null ||
                pending.AwaitingReturnAddress != returnAddress ||
                pending.AwaitingCallDepth != callDepth)
            {
                return pending;
            }

            var completedOrPending = MoveCompletedPendingPartyStatOperationToHistory(result, pending, fallbackField);
            if (completedOrPending != null)
            {
                completedOrPending.AwaitingReturnAddress = null;
                completedOrPending.AwaitingCallDepth = 0;
            }

            return completedOrPending;
        }

        private void MergeSubroutineAnalysisState(
            PathAnalysisResult target,
            PathAnalysisResult source,
            bool subroutineReturnedToCaller)
        {
            if (target == null || source == null)
                return;

            if (source.RandomEncounterMonsterBatchCountCap.HasValue && !target.RandomEncounterMonsterBatchCountCap.HasValue)
                target.RandomEncounterMonsterBatchCountCap = source.RandomEncounterMonsterBatchCountCap;

            if (source.DarkeningLevel.HasValue && !target.DarkeningLevel.HasValue)
                target.DarkeningLevel = source.DarkeningLevel;

            if (source.RandomEncounterChance.HasValue && !target.RandomEncounterChance.HasValue)
                target.RandomEncounterChance = source.RandomEncounterChance;

            if (source.RandomEncounterRubicon.HasValue && !target.RandomEncounterRubicon.HasValue)
                target.RandomEncounterRubicon = source.RandomEncounterRubicon;

            target.BattleMonsterStrengthAdjustment += source.BattleMonsterStrengthAdjustment;
            target.CallsRandomEncounter = target.CallsRandomEncounter || source.CallsRandomEncounter;
            target.DisablesCurrentMapEvent = target.DisablesCurrentMapEvent || source.DisablesCurrentMapEvent;
            MultiplyInlineProbability(target, source.InlineProbabilityNumerator, source.InlineProbabilityDenominator);

            if (source.RandomEncounterInstructionAddress != 0 &&
                (target.RandomEncounterInstructionAddress == 0 ||
                 source.RandomEncounterInstructionAddress < target.RandomEncounterInstructionAddress))
            {
                target.RandomEncounterInstructionAddress = source.RandomEncounterInstructionAddress;
            }

            if (source.RandomEncounterExecutionOrder != 0 &&
                (target.RandomEncounterExecutionOrder == 0 ||
                 source.RandomEncounterExecutionOrder < target.RandomEncounterExecutionOrder))
            {
                target.RandomEncounterExecutionOrder = source.RandomEncounterExecutionOrder;
            }

            if (source.NextSpecialEventOrder > target.NextSpecialEventOrder)
                target.NextSpecialEventOrder = source.NextSpecialEventOrder;

            if (source.TeleportTargetX.HasValue && !target.TeleportTargetX.HasValue)
                target.TeleportTargetX = source.TeleportTargetX;

            if (source.TeleportTargetY.HasValue && !target.TeleportTargetY.HasValue)
                target.TeleportTargetY = source.TeleportTargetY;

            if (source.TeleportTargetXRange != null && target.TeleportTargetXRange == null)
                target.TeleportTargetXRange = new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max);

            if (source.TeleportTargetYRange != null && target.TeleportTargetYRange == null)
                target.TeleportTargetYRange = new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max);

            foreach (var access in source.PartyFieldAccesses ?? Enumerable.Empty<PartyFieldReference>())
            {
                if (access == null)
                    continue;

                bool exists = target.PartyFieldAccesses.Any(a =>
                    a != null &&
                    a.Field == access.Field &&
                    a.Offset == access.Offset &&
                    a.EffectiveAddress == access.EffectiveAddress &&
                    a.IsRead == access.IsRead &&
                    a.IsWrite == access.IsWrite &&
                    a.IsCompare == access.IsCompare &&
                    ((a.Member?.MemberIndex) == (access.Member?.MemberIndex)) &&
                    ((a.Member?.PointerAddress) == (access.Member?.PointerAddress)));

                if (!exists)
                    target.PartyFieldAccesses.Add(access.Clone());
            }

            var mergedPendingHp = target.PendingPartyHpOperation;
            var mergedPendingSp = target.PendingPartySpOperation;
            var completedPartyStatOperations = PendingPartyStatOperationMerger.MergeCompletedContinuations(
                source.CompletedPartyStatOperations,
                ref mergedPendingHp,
                ref mergedPendingSp,
                MergePendingPartyStatOperation,
                MatchesPendingPartyTarget);
            target.CompletedPartyStatOperations.AddRange(completedPartyStatOperations);

            mergedPendingHp = MergePendingPartyStatOperation(
                mergedPendingHp,
                source.PendingPartyHpOperation);
            target.PendingPartyHpOperation = subroutineReturnedToCaller
                ? MoveCompletedPendingPartyStatOperationToHistory(
                    target,
                    mergedPendingHp,
                    PartyFieldKind.Hp)
                : mergedPendingHp;

            mergedPendingSp = MergePendingPartyStatOperation(
                mergedPendingSp,
                source.PendingPartySpOperation);
            target.PendingPartySpOperation = subroutineReturnedToCaller
                ? MoveCompletedPendingPartyStatOperationToHistory(
                    target,
                    mergedPendingSp,
                    PartyFieldKind.Sp)
                : mergedPendingSp;

            foreach (var effect in source.PartyEffects ?? Enumerable.Empty<PartyEffect>())
            {
                if (effect == null)
                    continue;

                string effectKey = PartyEffectSemantics.BuildSemanticKey(effect);
                var existingEffect = target.PartyEffects.FirstOrDefault(e => e != null && PartyEffectSemantics.BuildSemanticKey(e) == effectKey);
                if (existingEffect == null)
                    target.PartyEffects.Add(effect.Clone());
                else if (existingEffect.IsSynchronizedTemporaryMirror && !effect.IsSynchronizedTemporaryMirror)
                    existingEffect.IsSynchronizedTemporaryMirror = false;
            }

            if (target.LoopSemantic == LoopSemanticKind.None)
                target.LoopSemantic = source.LoopSemantic;
            else if (source.LoopSemantic != LoopSemanticKind.None)
                target.LoopSemantic = source.LoopSemantic;

            if (source.IsInLoop)
                target.IsInLoop = true;

            if (source.LoopStartAddress != 0 &&
                (target.LoopStartAddress == 0 || source.LoopStartAddress < target.LoopStartAddress))
            {
                target.LoopStartAddress = source.LoopStartAddress;
            }

            if (source.LoopEndAddress > target.LoopEndAddress)
                target.LoopEndAddress = source.LoopEndAddress;

            if (source.LoopIterationCount > target.LoopIterationCount)
                target.LoopIterationCount = source.LoopIterationCount;

            if (source.LoopIteration > target.LoopIteration)
                target.LoopIteration = source.LoopIteration;

            target.IsIndeterminateLoop = target.IsIndeterminateLoop || source.IsIndeterminateLoop;

            if (source.FirstLocalTextAddress < target.FirstLocalTextAddress)
                target.FirstLocalTextAddress = source.FirstLocalTextAddress;

            target.UsesStaticMapData = target.UsesStaticMapData || source.UsesStaticMapData;
            MergeStaticMapDataReads(target.StaticMapDataReads, source.StaticMapDataReads);
            target.MemoryReadAddresses.UnionWith(source.MemoryReadAddresses ?? Enumerable.Empty<ushort>());
            target.MemoryWrittenAddresses.UnionWith(source.MemoryWrittenAddresses ?? Enumerable.Empty<ushort>());
            target.AdjustedMemoryAddresses.UnionWith(source.AdjustedMemoryAddresses ?? Enumerable.Empty<ushort>());
            target.MemoryReadBeforeWriteAddresses.UnionWith(source.MemoryReadBeforeWriteAddresses ?? Enumerable.Empty<ushort>());
            foreach (var kvp in source.PendingPersistentCounterProgressions ?? Enumerable.Empty<KeyValuePair<ushort, PersistentCounterProgressionInfo>>())
                target.PendingPersistentCounterProgressions[kvp.Key] = kvp.Value?.Clone();
            foreach (var progression in source.PersistentCounterProgressions ?? Enumerable.Empty<PersistentCounterProgressionInfo>())
                AddOrReplacePersistentCounterProgression(target.PersistentCounterProgressions, progression);
            foreach (var dependency in source.DynamicRandomBoundDependencies ?? Enumerable.Empty<DynamicRandomBoundDependencyInfo>())
                AddOrReplaceDynamicRandomBoundDependency(target.DynamicRandomBoundDependencies, dependency);

            foreach (var kvp in source.PersistentMemoryFirstAccessKinds ?? Enumerable.Empty<KeyValuePair<ushort, PersistentMemoryFirstAccessKind>>())
            {
                if (!target.PersistentMemoryFirstAccessKinds.ContainsKey(kvp.Key))
                    target.PersistentMemoryFirstAccessKinds[kvp.Key] = kvp.Value;
            }

            MergeStateValueConstraints(target.StateValueConstraints, source.StateValueConstraints);
            target.LocallyMaterializedStateValueConstraintAddresses.UnionWith(
                source.LocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>());

            if (source.ExitEmulatedMemory8 != null && source.ExitEmulatedMemory8.Count > 0)
                target.ExitEmulatedMemory8 = new Dictionary<ushort, byte>(source.ExitEmulatedMemory8);
            if (source.ExitEmulatedMemory8Ranges != null && source.ExitEmulatedMemory8Ranges.Count > 0)
                target.ExitEmulatedMemory8Ranges = CloneRangeDictionary(source.ExitEmulatedMemory8Ranges);
            if (source.ExitEmulatedMemory8RangeDistributions != null && source.ExitEmulatedMemory8RangeDistributions.Count > 0)
                target.ExitEmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(source.ExitEmulatedMemory8RangeDistributions);
        }

        private PendingPartyStatOperation MergePendingPartyStatOperation(
            PendingPartyStatOperation inheritedPending,
            PendingPartyStatOperation currentPending)
        {
            if (inheritedPending == null)
                return currentPending?.Clone();

            if (currentPending == null)
                return inheritedPending.Clone();

            if (!MatchesPendingPartyTarget(inheritedPending.Member, currentPending.Member))
                return currentPending.Clone();

            var merged = inheritedPending.Clone();
            merged.Field = inheritedPending.Field != PartyFieldKind.Unknown
                ? inheritedPending.Field
                : currentPending.Field;
            merged.Member = MergePartyMemberReference(inheritedPending.Member, currentPending.Member);

            merged.MaleOnly = inheritedPending.MaleOnly || currentPending.MaleOnly;
            merged.FemaleOnly = inheritedPending.FemaleOnly || currentPending.FemaleOnly;
            merged.GuardPredicates = (inheritedPending.GuardPredicates ?? new List<PartyPredicate>())
                .Concat(currentPending.GuardPredicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Select(predicate => predicate.Clone())
                .GroupBy(PartyEffectSemantics.BuildPredicateKey)
                .Select(group => group.First())
                .OrderBy(PartyEffectSemantics.BuildPredicateKey)
                .ToList();

            if ((inheritedPending.MaleOnly && currentPending.FemaleOnly) ||
                (inheritedPending.FemaleOnly && currentPending.MaleOnly))
            {
                merged.MaleOnly = false;
                merged.FemaleOnly = false;
            }

            merged.SawReadHigh = inheritedPending.SawReadHigh || currentPending.SawReadHigh;
            merged.SawReadLow = inheritedPending.SawReadLow || currentPending.SawReadLow;
            merged.SawWriteHigh = inheritedPending.SawWriteHigh || currentPending.SawWriteHigh;
            merged.SawWriteLow = inheritedPending.SawWriteLow || currentPending.SawWriteLow;
            merged.FinalWriteHighByteValue = currentPending.SawWriteHigh
                ? currentPending.FinalWriteHighByteValue
                : inheritedPending.FinalWriteHighByteValue;
            merged.FinalWriteLowByteValue = currentPending.SawWriteLow
                ? currentPending.FinalWriteLowByteValue
                : inheritedPending.FinalWriteLowByteValue;
            merged.FinalWriteHighSourceField = currentPending.SawWriteHigh
                ? currentPending.FinalWriteHighSourceField
                : inheritedPending.FinalWriteHighSourceField;
            merged.FinalWriteLowSourceField = currentPending.SawWriteLow
                ? currentPending.FinalWriteLowSourceField
                : inheritedPending.FinalWriteLowSourceField;
            merged.SawClc = inheritedPending.SawClc || currentPending.SawClc;
            merged.SawShrHigh = inheritedPending.SawShrHigh || currentPending.SawShrHigh;
            merged.SawRcrLow = inheritedPending.SawRcrLow || currentPending.SawRcrLow;
            merged.LowByteArithmetic = MergePendingPartyByteArithmetic(
                inheritedPending.LowByteArithmetic,
                currentPending.LowByteArithmetic);
            merged.HighByteArithmetic = MergePendingPartyByteArithmetic(
                inheritedPending.HighByteArithmetic,
                currentPending.HighByteArithmetic);

            if (merged.StartAddress == 0 ||
                (currentPending.StartAddress != 0 && currentPending.StartAddress < merged.StartAddress))
            {
                merged.StartAddress = currentPending.StartAddress;
            }

            if (merged.ExecutionOrder <= 0 ||
                (currentPending.ExecutionOrder > 0 && currentPending.ExecutionOrder < merged.ExecutionOrder))
            {
                merged.ExecutionOrder = currentPending.ExecutionOrder;
            }

            if (currentPending.AwaitingReturnAddress.HasValue)
                merged.AwaitingReturnAddress = currentPending.AwaitingReturnAddress;

            if (currentPending.AwaitingCallDepth > 0)
                merged.AwaitingCallDepth = currentPending.AwaitingCallDepth;

            return merged;
        }

        private PartyMemberReference MergePartyMemberReference(
            PartyMemberReference inheritedMember,
            PartyMemberReference currentMember)
        {
            if (inheritedMember == null)
                return currentMember?.Clone();

            if (currentMember == null)
                return inheritedMember.Clone();

            var merged = inheritedMember.Clone();

            if (!merged.MemberIndex.HasValue)
                merged.MemberIndex = currentMember.MemberIndex;
            if (!merged.PointerAddress.HasValue)
                merged.PointerAddress = currentMember.PointerAddress;
            if (!merged.PointerTableAddress.HasValue)
                merged.PointerTableAddress = currentMember.PointerTableAddress;
            if (!merged.StructureAddress.HasValue)
                merged.StructureAddress = currentMember.StructureAddress;
            if (string.IsNullOrWhiteSpace(merged.Source))
                merged.Source = currentMember.Source;

            merged.IsPartyLoopMember = merged.IsPartyLoopMember || currentMember.IsPartyLoopMember;
            merged.SelectionKind = MergeSelectionKind(merged.SelectionKind, currentMember.SelectionKind);
            return merged;
        }

        private PartyMemberSelectionKind MergeSelectionKind(
            PartyMemberSelectionKind left,
            PartyMemberSelectionKind right)
        {
            if (left == PartyMemberSelectionKind.Random || right == PartyMemberSelectionKind.Random)
                return PartyMemberSelectionKind.Random;

            if (left == PartyMemberSelectionKind.Dynamic || right == PartyMemberSelectionKind.Dynamic)
                return PartyMemberSelectionKind.Dynamic;

            return PartyMemberSelectionKind.Exact;
        }

        private PendingPartyByteArithmetic MergePendingPartyByteArithmetic(
            PendingPartyByteArithmetic inheritedArithmetic,
            PendingPartyByteArithmetic currentArithmetic)
        {
            if (inheritedArithmetic == null)
                return currentArithmetic?.Clone();

            if (currentArithmetic == null)
                return inheritedArithmetic.Clone();

            int inheritedScore = GetPendingPartyByteArithmeticPrecisionScore(inheritedArithmetic);
            int currentScore = GetPendingPartyByteArithmeticPrecisionScore(currentArithmetic);

            if (currentScore > inheritedScore)
                return currentArithmetic.Clone();

            if (inheritedScore > currentScore)
                return inheritedArithmetic.Clone();

            return inheritedArithmetic.InstructionAddress != 0 &&
                   (currentArithmetic.InstructionAddress == 0 ||
                    inheritedArithmetic.InstructionAddress <= currentArithmetic.InstructionAddress)
                ? inheritedArithmetic.Clone()
                : currentArithmetic.Clone();
        }

        private int GetPendingPartyByteArithmeticPrecisionScore(PendingPartyByteArithmetic arithmetic)
        {
            if (arithmetic == null)
                return -1;

            int score = 0;
            if (arithmetic.Operation != PartyEffectOperation.Unknown)
                score += 4;
            if (arithmetic.EffectiveImmediateValue.HasValue)
                score += 8;
            if (arithmetic.UsesCarryOpcode)
                score += 2;
            if (arithmetic.CarryInKnown)
                score += 1;
            if (arithmetic.InstructionAddress != 0)
                score += 1;

            return score;
        }

        private Dictionary<ushort, StateValueConstraintInfo> CloneStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> source)
        {
            if (source == null || source.Count == 0)
                return new Dictionary<ushort, StateValueConstraintInfo>();

            return source.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.Clone() ?? new StateValueConstraintInfo());
        }

        private void MergeStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> target,
            Dictionary<ushort, StateValueConstraintInfo> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            foreach (var kvp in source)
            {
                if (!target.TryGetValue(kvp.Key, out var existing))
                {
                    target[kvp.Key] = kvp.Value?.Clone() ?? new StateValueConstraintInfo();
                    continue;
                }

                existing.MergeFrom(kvp.Value);
            }
        }

        private static void MergeStaticMapDataReads(Dictionary<ushort, byte> target, Dictionary<ushort, byte> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            foreach (var kvp in source)
                target[kvp.Key] = kvp.Value;
        }

        /// <summary>
        /// Обрабатывает условный переход
        /// </summary>
        private ControlFlowResult HandleConditionalJump(X86Instruction insn, BinaryReader br, uint condJumpTarget,
            uint nextAddress, long fileLength, bool debugMode,
            HashSet<(uint From, uint To)> processedBackEdges, PathAnalysisResult result, uint currentAddress,
            RegisterTracker registerTracker, byte targetX, byte targetY,
            List<uint> pendingReturnAddresses, int callDepth,
            HashSet<uint> visitedInThisPath, Dictionary<uint, byte> finiteLoopProgressByJumpAddress)
        {
            if (condJumpTarget < fileLength && condJumpTarget != 0)
            {
                bool? branchTaken = EvaluateConditionalJump(insn.Mnemonic, registerTracker);
                if (!branchTaken.HasValue &&
                    TryResolveFiniteInputValidationTerminatorBranch(insn, br, fileLength, currentAddress,
                        registerTracker, out bool resolvedInputTerminatorBranch))
                {
                    branchTaken = resolvedInputTerminatorBranch;
                }

                bool isInputChoiceBranch = IsInputChoiceBranch(registerTracker);
                if (branchTaken.HasValue)
                {
                    TrackStateValueConstraintForCurrentFlags(registerTracker, insn.Mnemonic, branchTaken.Value, result);
                    uint resolvedTarget = branchTaken.Value ? condJumpTarget : nextAddress;

                    bool isResolvedLoopBackEdge = IsStructuralLoopBackEdge(br, condJumpTarget, currentAddress);
                    if (isResolvedLoopBackEdge && branchTaken.Value)
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

                        if (TryAdvanceFiniteImmediateCounterLoop(insn.Mnemonic, registerTracker, result, currentAddress, condJumpTarget,
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
                            ApplyInlineBranchProbability(result, insn.Mnemonic, registerTracker, branchTaken: false, debugMode);
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

                if (TryCollapseFiniteBitCountLoopBranch(
                    insn, br, condJumpTarget, nextAddress, fileLength, debugMode, result,
                    currentAddress, registerTracker, targetX, targetY, pendingReturnAddresses, callDepth,
                    visitedInThisPath, out var collapsedBitCountLoopResult))
                {
                    return collapsedBitCountLoopResult;
                }

                if (TryCollapsePartyMemberScanExitBranch(
                    insn, br, condJumpTarget, nextAddress, fileLength, debugMode, result,
                    currentAddress, registerTracker, targetX, targetY, pendingReturnAddresses, callDepth,
                    visitedInThisPath, out var collapsedPartyScanResult))
                {
                    return collapsedPartyScanResult;
                }

                if (TryCollapseCountedShiftLoopBranch(
                    insn, br, condJumpTarget, nextAddress, fileLength, debugMode, result,
                    currentAddress, registerTracker, targetX, targetY, pendingReturnAddresses, callDepth,
                    visitedInThisPath, out var collapsedShiftLoopResult))
                {
                    return collapsedShiftLoopResult;
                }

                if (TryHandlePartyMemberScanBackEdge(
                    insn, condJumpTarget, nextAddress, debugMode, processedBackEdges, result,
                    currentAddress, registerTracker, pendingReturnAddresses, callDepth,
                    out var partyScanBackEdgeResult))
                {
                    return partyScanBackEdgeResult;
                }

                if (IsStructuralLoopBackEdge(br, condJumpTarget, currentAddress))
                {
                    if (IsInputRetryBackEdge(registerTracker, condJumpTarget, currentAddress, visitedInThisPath, result))
                    {
                        RecordInlineInputChoice(result, registerTracker, insn.Mnemonic, branchTaken: false, debugMode);
                        ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                            (uint)insn.Address, debugMode);

                        if (debugMode)
                        {
                            AnalysisDebug.WriteLine(
                                $"      Retry-переход ввода 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}: пропускаем повторный ввод и продолжаем по допустимой ветке");
                        }

                        return new ControlFlowResult
                        {
                            ShouldReturn = false,
                            NextAddress = nextAddress,
                            UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            UpdatedCallDepth = callDepth
                        };
                    }

                    if (IsPendingPartyMemberScanBackEdge(registerTracker))
                        MarkPartyMemberScanLoop(result, condJumpTarget, debugMode, currentAddress);

                    if (TrySkipRangedBattleCounterLoopBackEdge(
                        registerTracker,
                        result,
                        currentAddress,
                        condJumpTarget,
                        debugMode))
                    {
                        return new ControlFlowResult
                        {
                            ShouldReturn = false,
                            NextAddress = nextAddress,
                            UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            UpdatedCallDepth = callDepth
                        };
                    }

                    var backEdge = (From: currentAddress, To: condJumpTarget);

                    if (!processedBackEdges.Add(backEdge))
                    {
                        ApplyInlineBranchProbability(result, insn.Mnemonic, registerTracker, branchTaken: false, debugMode);
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

                if (IsRepeatedInputValidationFailureBranch(insn, br, fileLength, result, currentAddress, condJumpTarget) &&
                    processedBackEdges != null &&
                    !processedBackEdges.Add((currentAddress, condJumpTarget)))
                {
                    ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                        (uint)insn.Address, debugMode);
                    ClearInputValidationVisitedWindow(visitedInThisPath, currentAddress, nextAddress);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"      Повторная проверка символа ввода 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}: failure-исход уже учтён, продолжаем по совпадению");
                    }

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = nextAddress,
                        UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = callDepth
                    };
                }

                if (IsRepeatedInputCharacterStoreShortcutBranch(insn, br, fileLength, currentAddress, condJumpTarget) &&
                    processedBackEdges != null &&
                    !processedBackEdges.Add((currentAddress, condJumpTarget)))
                {
                    ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                        (uint)insn.Address, debugMode);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"      Повторная развилка ввода символа 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}: store-исход уже учтён, продолжаем через проверку диапазона");
                    }

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = nextAddress,
                        UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = callDepth
                    };
                }

                if (IsRepeatedInputSubmitBranch(insn, br, fileLength, currentAddress, condJumpTarget) &&
                    processedBackEdges != null &&
                    !processedBackEdges.Add((currentAddress, condJumpTarget)))
                {
                    ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                        (uint)insn.Address, debugMode);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"      Повторный submit-переход ввода 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}: исход Enter уже учтён, продолжаем набор");
                    }

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = nextAddress,
                        UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = callDepth
                    };
                }

                bool isPartyInventoryScanMatchBranch = TryGetPartyInventoryScanMatchSlotRange(
                    insn,
                    br,
                    fileLength,
                    currentAddress,
                    nextAddress,
                    condJumpTarget,
                    registerTracker,
                    out var partyInventoryScanSlotRange);

                if (isPartyInventoryScanMatchBranch &&
                    processedBackEdges != null &&
                    !processedBackEdges.Add((currentAddress, condJumpTarget)))
                {
                    ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                        (uint)insn.Address, debugMode);

                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine(
                            $"      Повторная проверка слота инвентаря 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}: found-исход уже учтён, продолжаем сканирование");
                    }

                    return new ControlFlowResult
                    {
                        ShouldReturn = false,
                        NextAddress = nextAddress,
                        UpdatedPendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        UpdatedCallDepth = callDepth
                    };
                }

                var takenProbability = EstimateBranchProbability(insn.Mnemonic, registerTracker, branchTaken: true);
                var notTakenProbability = EstimateBranchProbability(insn.Mnemonic, registerTracker, branchTaken: false);

                if (takenProbability.numerator > 0)
                {
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
                        CompareMemoryAddress = registerTracker.LastComparedMemoryAddress,
                        ComparedPartyField = registerTracker.LastComparedPartyField?.Clone(),
                        IsInputChoiceBranch = isInputChoiceBranch,
                        ProbabilityNumerator = takenProbability.numerator,
                        ProbabilityDenominator = takenProbability.denominator,
                        CallDepth = callDepth,
                        PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8),
                        EmulatedMemory8Ranges = CloneEmulatedMemoryRangesForBranch(registerTracker, insn.Mnemonic, branchTaken: true),
                        EmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions),
                        EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                        EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                        PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(result.PendingPersistentCounterProgressions),
                        BranchStateValueConstraints = BuildStateValueConstraintsForBranch(registerTracker, insn.Mnemonic, branchTaken: true),
                        BranchLocallyMaterializedStateValueConstraintAddresses =
                            BuildLocallyMaterializedStateValueConstraintAddressesForBranch(
                                result,
                                registerTracker,
                                insn.Mnemonic,
                                branchTaken: true),
                        BranchPartyCondition = InferPartyConditionForBranch(registerTracker, insn.Mnemonic, branchTaken: true),
                        BranchPartyPredicate = InferPartyPredicateForBranch(registerTracker, insn.Mnemonic, branchTaken: true, (uint)insn.Address)
                    };
                    ApplyPartyInventoryScanSlotRange(altPath, partyInventoryScanSlotRange);

                    if (!result.AlternativePaths.Any(p => p.Address == altPath.Address &&
                                                           p.TargetAddress == altPath.TargetAddress))
                    {
                        result.AlternativePaths.Add(altPath);
                        if (debugMode)
                            AnalysisDebug.WriteLine($"      Найден альтернативный путь: 0x{insn.Address:X4} -> 0x{condJumpTarget:X4}");
                    }
                }

                if (notTakenProbability.numerator > 0)
                {
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
                        CompareMemoryAddress = registerTracker.LastComparedMemoryAddress,
                        ComparedPartyField = registerTracker.LastComparedPartyField?.Clone(),
                        IsInputChoiceBranch = isInputChoiceBranch,
                        ProbabilityNumerator = notTakenProbability.numerator,
                        ProbabilityDenominator = notTakenProbability.denominator,
                        CallDepth = callDepth,
                        PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                        EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8),
                        EmulatedMemory8Ranges = CloneEmulatedMemoryRangesForBranch(registerTracker, insn.Mnemonic, branchTaken: false),
                        EmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions),
                        EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                        EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                        PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(result.PendingPersistentCounterProgressions),
                        BranchStateValueConstraints = BuildStateValueConstraintsForBranch(registerTracker, insn.Mnemonic, branchTaken: false),
                        BranchLocallyMaterializedStateValueConstraintAddresses =
                            BuildLocallyMaterializedStateValueConstraintAddressesForBranch(
                                result,
                                registerTracker,
                                insn.Mnemonic,
                                branchTaken: false),
                        BranchPartyCondition = InferPartyConditionForBranch(registerTracker, insn.Mnemonic, branchTaken: false),
                        BranchPartyPredicate = InferPartyPredicateForBranch(registerTracker, insn.Mnemonic, branchTaken: false, (uint)insn.Address)
                    };
                    ApplyPartyInventoryScanSlotRange(linearPath, partyInventoryScanSlotRange);

                    if (!result.AlternativePaths.Any(p => p.Address == linearPath.Address &&
                                                           p.TargetAddress == linearPath.TargetAddress))
                    {
                        result.AlternativePaths.Add(linearPath);
                        if (debugMode)
                            AnalysisDebug.WriteLine($"      Добавлен линейный путь: 0x{insn.Address:X4} -> 0x{nextAddress:X4}");
                    }
                }

                // Завершаем текущий путь – не продолжаем выполнение
                if (debugMode)
                    AnalysisDebug.WriteLine($"      Останавливаем анализ текущего пути после условного перехода");

                result.IsTerminated = true;
                result.HasSignificantCode = result.FoundTexts.Count > 0 ||
                                             result.ContextTexts.Count > 0 ||
                                             result.RandomEncounterMonsterPowerCap.HasValue ||
                                             result.RandomEncounterMonsterLevelCap.HasValue ||
                                             result.BattleMonsterEntries.Count > 0 ||
                                             result.PartialBattles.Count > 0 ||
                                             (result.DynamicRandomBoundDependencies != null && result.DynamicRandomBoundDependencies.Count > 0) ||
                                             result.HasPartialBattlePattern ||
                                             result.CallsRandomEncounter;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }

        private bool TryHandlePartyMemberScanBackEdge(
            X86Instruction insn,
            uint condJumpTarget,
            uint nextAddress,
            bool debugMode,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result,
            uint currentAddress,
            RegisterTracker registerTracker,
            List<uint> pendingReturnAddresses,
            int callDepth,
            out ControlFlowResult controlFlowResult)
        {
            controlFlowResult = null;

            if (insn == null ||
                condJumpTarget >= currentAddress ||
                processedBackEdges == null ||
                !IsCarrySetJump(insn.Mnemonic) ||
                !TryGetPartyMemberScanCounterRegister(registerTracker, out string counterRegister))
            {
                return false;
            }

            MarkPartyMemberScanLoop(result, condJumpTarget, debugMode, currentAddress);

            var backEdge = (From: currentAddress, To: condJumpTarget);
            if (processedBackEdges.Add(backEdge))
            {
                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"      Первый party-scan back-edge по {counterRegister}: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, разрешаем одну развилку");
                }

                return false;
            }

            ApplyInlineBranchProbability(result, insn.Mnemonic, registerTracker, branchTaken: false, debugMode);
            ApplyBranchConstraintInPlace(registerTracker, insn.Mnemonic, branchTaken: false,
                (uint)insn.Address, debugMode);

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Повторный party-scan back-edge по {counterRegister}: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, продолжаем после обхода партии");
            }

            controlFlowResult = new ControlFlowResult
            {
                ShouldReturn = false,
                NextAddress = nextAddress,
                UpdatedPendingReturnAddresses = pendingReturnAddresses == null
                    ? new List<uint>()
                    : new List<uint>(pendingReturnAddresses),
                UpdatedCallDepth = callDepth
            };
            return true;
        }

        private bool IsRepeatedInputValidationFailureBranch(X86Instruction insn, BinaryReader br, long fileLength,
            PathAnalysisResult result, uint currentAddress, uint condJumpTarget)
        {
            if (insn == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                condJumpTarget <= currentAddress)
            {
                return false;
            }

            string jump = insn.Mnemonic?.ToUpperInvariant();
            if (jump != "JNE" && jump != "JNZ")
                return false;

            if (!IsCmpAlAgainstBxIndexedMemoryImmediatelyBefore(br, fileLength, currentAddress))
                return false;

            return BranchTargetLooksLikeInputFailureText(br, fileLength, condJumpTarget);
        }

        private bool TryResolveFiniteInputValidationTerminatorBranch(X86Instruction insn, BinaryReader br, long fileLength,
            uint currentAddress, RegisterTracker registerTracker, out bool branchTaken)
        {
            branchTaken = false;

            if (insn == null ||
                registerTracker == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                currentAddress < 6 ||
                currentAddress + 2 >= fileLength ||
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.Test ||
                !string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte alValue))
            {
                return false;
            }

            string jump = insn.Mnemonic?.ToUpperInvariant();
            if (jump != "JE" && jump != "JZ" && jump != "JNE" && jump != "JNZ")
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(currentAddress - 2, SeekOrigin.Begin);
                if (br.ReadByte() != 0x0A || br.ReadByte() != 0xC0)
                    return false;

                br.BaseStream.Seek(currentAddress - 6, SeekOrigin.Begin);
                if (br.ReadByte() != 0x8A)
                    return false;

                byte modRm = br.ReadByte();
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                if (mod != 0x02 || reg != 0x00 || rm != 0x07)
                    return false;

                if (!WindowContainsCmpAlAgainstInputBuffer(br, fileLength, currentAddress + 2))
                    return false;

                bool isZero = alValue == 0;
                branchTaken = jump == "JE" || jump == "JZ"
                    ? isZero
                    : !isZero;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
        }

        private bool WindowContainsCmpAlAgainstInputBuffer(BinaryReader br, long fileLength, uint startAddress)
        {
            if (br?.BaseStream == null || !br.BaseStream.CanSeek || startAddress >= fileLength)
                return false;

            uint scanEnd = (uint)Math.Min(fileLength, startAddress + 0x18);
            for (uint address = startAddress; address + 3 < scanEnd; address++)
            {
                br.BaseStream.Seek(address, SeekOrigin.Begin);
                if (br.ReadByte() == 0x3A &&
                    br.ReadByte() == 0x87 &&
                    br.ReadUInt16() == 0x3CB8)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearInputValidationVisitedWindow(HashSet<uint> visitedInThisPath, uint currentAddress, uint nextAddress)
        {
            if (visitedInThisPath == null || visitedInThisPath.Count == 0)
                return;

            uint startAddress = currentAddress > 0x30
                ? currentAddress - 0x30
                : 0;
            uint endAddress = Math.Max(currentAddress + 4, nextAddress + 4);

            foreach (var visitedAddress in visitedInThisPath
                .Where(address => address >= startAddress && address <= endAddress)
                .ToList())
            {
                visitedInThisPath.Remove(visitedAddress);
            }
        }

        private bool IsRepeatedInputCharacterStoreShortcutBranch(X86Instruction insn, BinaryReader br, long fileLength,
            uint currentAddress, uint condJumpTarget)
        {
            if (insn == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                currentAddress < 2 ||
                condJumpTarget <= currentAddress ||
                condJumpTarget + 12 >= fileLength)
            {
                return false;
            }

            string jump = insn.Mnemonic?.ToUpperInvariant();
            if (jump != "JE" && jump != "JZ")
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(currentAddress - 2, SeekOrigin.Begin);
                if (br.ReadByte() != 0x3C || br.ReadByte() != 0x20)
                    return false;

                br.BaseStream.Seek(condJumpTarget, SeekOrigin.Begin);
                if (br.ReadByte() != 0x8A || br.ReadByte() != 0x1E)
                    return false;

                ushort indexAddress = br.ReadUInt16();
                if (indexAddress != LEGACY_INPUT_INDEX_ADDRESS &&
                    indexAddress != OVERLAY_INPUT_INDEX_ADDRESS)
                    return false;

                if (br.ReadByte() != 0xB7 || br.ReadByte() != 0x00)
                    return false;

                uint scanEnd = (uint)Math.Min(fileLength, condJumpTarget + 0x18);
                for (uint address = condJumpTarget + 6; address + 3 < scanEnd; address++)
                {
                    br.BaseStream.Seek(address, SeekOrigin.Begin);
                    if (br.ReadByte() == 0x88 &&
                        br.ReadByte() == 0x87 &&
                        br.ReadUInt16() == INPUT_BUFFER_ADDRESS)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }

            return false;
        }

        private bool IsRepeatedInputSubmitBranch(X86Instruction insn, BinaryReader br, long fileLength,
            uint currentAddress, uint condJumpTarget)
        {
            if (insn == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                currentAddress < 2 ||
                condJumpTarget <= currentAddress ||
                condJumpTarget + 8 >= fileLength)
            {
                return false;
            }

            string jump = insn.Mnemonic?.ToUpperInvariant();
            if (jump != "JE" && jump != "JZ")
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(currentAddress - 2, SeekOrigin.Begin);
                if (br.ReadByte() != 0x3C || br.ReadByte() != 0x0D)
                    return false;

                br.BaseStream.Seek(condJumpTarget, SeekOrigin.Begin);
                if (br.ReadByte() != 0xBB || br.ReadByte() != 0x00 || br.ReadByte() != 0x00)
                    return false;

                uint scanEnd = (uint)Math.Min(fileLength, condJumpTarget + 0x18);
                for (uint address = condJumpTarget + 3; address + 1 < scanEnd; address++)
                {
                    br.BaseStream.Seek(address, SeekOrigin.Begin);
                    byte opcode = br.ReadByte();
                    byte operand = br.ReadByte();
                    if (opcode == 0x0A && operand == 0xC0)
                        return true;

                    if (opcode == 0x3A &&
                        IsAlComparedWithBxIndexedMemoryModRm(operand))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }

            return false;
        }

        private static bool IsAlComparedWithBxIndexedMemoryModRm(byte modRm)
        {
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte reg = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);

            return mode == 0x02 &&
                   reg == 0x00 &&
                   rm == 0x07;
        }

        private bool IsRepeatedPartyInventoryScanMatchBranch(X86Instruction insn, BinaryReader br, long fileLength,
            uint currentAddress, uint nextAddress, uint condJumpTarget, RegisterTracker registerTracker)
        {
            return TryGetPartyInventoryScanMatchSlotRange(
                insn,
                br,
                fileLength,
                currentAddress,
                nextAddress,
                condJumpTarget,
                registerTracker,
                out _);
        }

        private bool TryGetPartyInventoryScanMatchSlotRange(X86Instruction insn, BinaryReader br, long fileLength,
            uint currentAddress, uint nextAddress, uint condJumpTarget, RegisterTracker registerTracker,
            out ValueRange8 slotRange)
        {
            slotRange = null;
            if (insn == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                registerTracker == null ||
                condJumpTarget <= currentAddress ||
                condJumpTarget >= fileLength ||
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate ||
                !string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.LastCompareImmediate.HasValue)
            {
                return false;
            }

            string jump = insn.Mnemonic?.ToUpperInvariant();
            if (jump != "JE" && jump != "JZ")
                return false;

            var comparedField = registerTracker.LastComparedPartyField;
            if (!IsRawTechnicalPartyField(comparedField) ||
                !PartyInventorySemantics.IsInventorySlotReference(comparedField.FieldOffset, comparedField.FieldOffsetRange))
            {
                return false;
            }

            if (!WindowContainsPartyInventoryScanExhaustion(
                    br,
                    fileLength,
                    nextAddress,
                    currentAddress,
                    condJumpTarget,
                    out byte? slotUpperExclusive))
            {
                return false;
            }

            byte start = comparedField.FieldOffsetRange?.Min ??
                         comparedField.FieldOffset ??
                         PartyInventorySemantics.FirstSlotOffset;
            byte end = comparedField.FieldOffsetRange?.Max ?? PartyInventorySemantics.LastBackpackSlotOffset;
            if (slotUpperExclusive.HasValue && slotUpperExclusive.Value > start)
                end = (byte)Math.Min(end, slotUpperExclusive.Value - 1);

            if (start < PartyInventorySemantics.FirstSlotOffset)
                start = PartyInventorySemantics.FirstSlotOffset;
            if (end > PartyInventorySemantics.LastBackpackSlotOffset)
                end = PartyInventorySemantics.LastBackpackSlotOffset;

            if (end < start)
                return false;

            slotRange = new ValueRange8(start, end);
            return true;
        }

        private static void ApplyPartyInventoryScanSlotRange(AlternativePath path, ValueRange8 slotRange)
        {
            if (path == null || slotRange == null || slotRange.IsExact)
                return;

            if (path.ComparedPartyField != null)
            {
                path.ComparedPartyField.FieldOffsetRange = new ValueRange8(slotRange.Min, slotRange.Max);
                path.ComparedPartyField.FieldOffset = slotRange.Min;
                path.ComparedPartyField.FieldName =
                    $"техническое байтовое поле персонажа +0x{slotRange.Min:X2}..+0x{slotRange.Max:X2}";
            }

            if (path.BranchPartyPredicate != null)
            {
                path.BranchPartyPredicate.FieldOffsetRange = new ValueRange8(slotRange.Min, slotRange.Max);
                path.BranchPartyPredicate.FieldOffset = slotRange.Min;
            }
        }

        private bool WindowContainsPartyInventoryScanExhaustion(BinaryReader br, long fileLength,
            uint scanStartAddress, uint matchBranchAddress, uint matchBranchTarget)
        {
            return WindowContainsPartyInventoryScanExhaustion(
                br,
                fileLength,
                scanStartAddress,
                matchBranchAddress,
                matchBranchTarget,
                out _);
        }

        private bool WindowContainsPartyInventoryScanExhaustion(BinaryReader br, long fileLength,
            uint scanStartAddress, uint matchBranchAddress, uint matchBranchTarget, out byte? slotUpperExclusive)
        {
            slotUpperExclusive = null;
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                scanStartAddress >= fileLength ||
                matchBranchTarget <= scanStartAddress)
            {
                return false;
            }

            uint scanEnd = (uint)Math.Min(fileLength, Math.Min(matchBranchTarget, scanStartAddress + 0x50));
            bool sawSlotBackEdge = false;
            bool sawPartyBackEdge = false;
            bool sawExhaustedTerminalWrite = false;
            uint address = scanStartAddress;
            var seenAddresses = new HashSet<uint>();

            while (address < scanEnd && seenAddresses.Add(address))
            {
                if (LooksLikeSimpleTerminalByteStateWrite(br, fileLength, address))
                    sawExhaustedTerminalWrite = true;

                if (!TryDisassembleNext(br, address, out X86Instruction instruction) ||
                    instruction?.Bytes == null ||
                    instruction.Bytes.Length == 0 ||
                    (uint)instruction.Address != address)
                {
                    break;
                }

                if (IsConditionalJump(instruction, out uint targetAddress) &&
                    targetAddress <= matchBranchAddress &&
                    IsCarrySetJump(instruction.Mnemonic))
                {
                    if (TryReadCmpClImmediateBeforeInstruction(br, address, out byte slotLimit))
                    {
                        sawSlotBackEdge = true;
                        slotUpperExclusive = slotLimit;
                    }
                    else if (InstructionImmediatelyFollowsCmpBlAgainstPartyCount(br, address))
                        sawPartyBackEdge = true;
                }

                uint nextAddress = (uint)(instruction.Address + instruction.Bytes.Length);
                if (nextAddress <= address)
                    break;

                address = nextAddress;
            }

            return sawSlotBackEdge && (sawPartyBackEdge || sawExhaustedTerminalWrite);
        }

        private bool LooksLikeSimpleTerminalByteStateWrite(BinaryReader br, long fileLength, uint address)
        {
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                address + 8 > fileLength)
            {
                return false;
            }

            byte[] bytes = ReadBytesAt(br, address, 8);
            return bytes.Length >= 8 &&
                   bytes[0] == 0xB0 &&
                   bytes[2] == 0xA2 &&
                   bytes[5] == 0xB0 &&
                   bytes[7] == 0xC3;
        }

        private bool InstructionImmediatelyFollowsCmpClImmediate(BinaryReader br, uint instructionAddress)
        {
            return TryReadCmpClImmediateBeforeInstruction(br, instructionAddress, out _);
        }

        private bool TryReadCmpClImmediateBeforeInstruction(BinaryReader br, uint instructionAddress, out byte immediate)
        {
            immediate = 0;
            if (instructionAddress < 3)
                return false;

            byte[] bytes = ReadBytesAt(br, instructionAddress - 3, 3);
            if (bytes.Length != 3 ||
                bytes[0] != 0x80 ||
                bytes[1] != 0xF9)
            {
                return false;
            }

            immediate = bytes[2];
            return true;
        }

        private bool InstructionImmediatelyFollowsCmpBlAgainstPartyCount(BinaryReader br, uint instructionAddress)
        {
            if (instructionAddress < 4)
                return false;

            byte[] bytes = ReadBytesAt(br, instructionAddress - 4, 4);
            return bytes.Length == 4 &&
                   bytes[0] == 0x3A &&
                   bytes[1] == 0x1E &&
                   bytes[2] == (byte)(PARTY_COUNT_ADDRESS & 0xFF) &&
                   bytes[3] == (byte)(PARTY_COUNT_ADDRESS >> 8);
        }

        private bool IsCmpAlAgainstBxIndexedMemoryImmediatelyBefore(BinaryReader br, long fileLength, uint currentAddress)
        {
            if (currentAddress < 4 || currentAddress > fileLength)
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(currentAddress - 4, SeekOrigin.Begin);
                byte opcode = br.ReadByte();
                byte modRm = br.ReadByte();
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);

                return opcode == 0x3A &&
                       mod == 0x02 &&
                       reg == 0x00 &&
                       rm == 0x07;
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
        }

        private bool BranchTargetLooksLikeInputFailureText(BinaryReader br, long fileLength, uint targetAddress)
        {
            if (targetAddress >= fileLength)
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                uint scanEnd = (uint)Math.Min(fileLength, targetAddress + 0x20);
                for (uint address = targetAddress; address + 2 < scanEnd; address++)
                {
                    br.BaseStream.Seek(address, SeekOrigin.Begin);
                    if (br.ReadByte() != 0xB8)
                        continue;

                    ushort textAddress = br.ReadUInt16();
                    string text = ExtractText(br, textAddress);
                    if (string.IsNullOrEmpty(text))
                        continue;

                    if (text.IndexOf("IMPROPER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("INCORRECT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("WRONG", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }

            return false;
        }

        private void ApplyInlineBranchProbability(PathAnalysisResult result, string mnemonic,
            RegisterTracker registerTracker, bool branchTaken, bool debugMode)
        {
            if (result == null)
                return;

            var probability = EstimateBranchProbability(mnemonic, registerTracker, branchTaken);
            if (probability.denominator <= 1 ||
                probability.numerator <= 0 ||
                probability.numerator >= probability.denominator)
            {
                return;
            }

            MultiplyInlineProbability(result, probability.numerator, probability.denominator);

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        Учтена вероятность вынужденной ветки после повторного back-edge: {probability.numerator}/{probability.denominator}");
            }
        }

        private bool IsInputChoiceBranch(RegisterTracker registerTracker)
        {
            if (registerTracker == null ||
                !string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.TryGetRegisterRange("AL", out var alInputRange) ||
                alInputRange == null)
            {
                return false;
            }

            bool isUserInput =
                registerTracker.TryGetPendingExternalCallResultKind("AL", out var externalKind) &&
                externalKind == RegisterTracker.ExternalCallResultKind.UserInput;

            return isUserInput || IsKeyboardInputPlaceholderRange(alInputRange);
        }

        private bool IsStructuralLoopBackEdge(BinaryReader br, uint targetAddress, uint currentAddress)
        {
            if (targetAddress >= currentAddress ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek)
            {
                return false;
            }

            return CanFlowLinearlyToAddress(br, targetAddress, currentAddress);
        }

        private bool CanFlowLinearlyToAddress(BinaryReader br, uint startAddress, uint targetAddress)
        {
            const int maxInstructions = 256;

            if (startAddress > targetAddress)
                return false;

            uint cursor = startAddress;
            var seen = new HashSet<uint>();

            for (int i = 0; i < maxInstructions; i++)
            {
                if (cursor == targetAddress)
                    return true;

                if (cursor > targetAddress || !seen.Add(cursor))
                    return false;

                if (!TryDisassembleNext(br, cursor, out X86Instruction instruction) ||
                    instruction?.Bytes == null ||
                    instruction.Bytes.Length == 0 ||
                    (uint)instruction.Address != cursor)
                {
                    return false;
                }

                uint nextAddress = (uint)(instruction.Address + instruction.Bytes.Length);
                if (nextAddress <= cursor)
                    return false;

                if (IsStructuralFlowTerminator(instruction))
                    return false;

                if (IsUnconditionalJump(instruction))
                {
                    if (!HasDirectImmediateJumpEncoding(instruction) ||
                        !TryGetDirectUnconditionalJumpTarget(instruction, out uint jumpTarget))
                    {
                        return false;
                    }

                    if (jumpTarget == targetAddress)
                        return true;

                    if (jumpTarget <= cursor || jumpTarget > targetAddress)
                        return false;

                    cursor = jumpTarget;
                    continue;
                }

                cursor = nextAddress;
            }

            return false;
        }

        private static bool IsUnconditionalJump(X86Instruction instruction)
        {
            string mnemonic = instruction?.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            return mnemonic == "JMP" ||
                   mnemonic == "JMPF" ||
                   mnemonic == "JMPL" ||
                   mnemonic == "JMPE" ||
                   mnemonic == "JMPI";
        }

        private static bool HasDirectImmediateJumpEncoding(X86Instruction instruction)
        {
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length == 0)
                return false;

            return bytes[0] == 0xE9 ||
                   bytes[0] == 0xEA ||
                   bytes[0] == 0xEB;
        }

        private static bool IsStructuralFlowTerminator(X86Instruction instruction)
        {
            string mnemonic = instruction?.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            return mnemonic == "RET" ||
                   mnemonic == "RETF" ||
                   mnemonic == "IRET" ||
                   mnemonic == "HLT";
        }

        private bool IsInputRetryBackEdge(
            RegisterTracker registerTracker,
            uint condJumpTarget,
            uint currentAddress,
            HashSet<uint> visitedInThisPath,
            PathAnalysisResult result)
        {
            bool returnsToVisitedAddress =
                visitedInThisPath != null &&
                visitedInThisPath.Contains(condJumpTarget);
            bool returnsToPromptOrEarlier =
                result != null &&
                result.FirstLocalTextAddress != uint.MaxValue &&
                condJumpTarget <= result.FirstLocalTextAddress;

            if (registerTracker == null ||
                condJumpTarget >= currentAddress ||
                (!returnsToVisitedAddress && !returnsToPromptOrEarlier) ||
                !string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.LastCompareImmediate.HasValue ||
                !registerTracker.TryGetRegisterRange("AL", out var alRange) ||
                alRange == null)
            {
                return false;
            }

            byte compare = registerTracker.LastCompareImmediate.Value;
            bool looksLikeKeyboardInput =
                registerTracker.HasPendingExternalCallResult("AL") ||
                (alRange.Min >= 0x1B && alRange.Max <= 0x7A && compare >= 0x1B && compare <= 0x7A);

            return looksLikeKeyboardInput;
        }

        private void RecordInlineInputChoice(
            PathAnalysisResult result,
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken,
            bool debugMode)
        {
            if (result == null ||
                registerTracker == null ||
                !string.Equals(registerTracker.LastFlagsRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.LastCompareImmediate.HasValue)
            {
                return;
            }

            if (!ShouldRecordInlineInputChoice(registerTracker, mnemonic, branchTaken))
                return;

            var choice = new BranchChoice
            {
                Label = branchTaken ? "InputChoiceBranch" : "InputChoiceLinear",
                Condition = branchTaken ? mnemonic : $"LINEAR after {mnemonic}",
                CompareRegister = registerTracker.LastFlagsRegister,
                CompareValue = registerTracker.LastCompareImmediate,
                CompareMemoryAddress = registerTracker.LastComparedMemoryAddress,
                IsLinear = !branchTaken
            };

            bool alreadyRecorded = result.InlineBranchChoices.Any(existing =>
                existing != null &&
                string.Equals(existing.GetIdentityKey(), choice.GetIdentityKey(), StringComparison.OrdinalIgnoreCase));

            if (alreadyRecorded)
                return;

            result.InlineBranchChoices.Add(choice);

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        Зафиксирован inline-выбор ввода: {choice.Label}, {choice.Condition}, AL=0x{choice.CompareValue.Value:X2}");
            }
        }

        private bool ShouldRecordInlineInputChoice(
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken)
        {
            if (registerTracker == null ||
                !registerTracker.LastCompareImmediate.HasValue ||
                string.IsNullOrWhiteSpace(mnemonic))
            {
                return false;
            }

            byte compare = registerTracker.LastCompareImmediate.Value;
            string normalizedMnemonic = mnemonic.Trim();

            if (!branchTaken)
            {
                if (string.Equals(normalizedMnemonic, "JNE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedMnemonic, "JNZ", StringComparison.OrdinalIgnoreCase))
                {
                    return IsDisplayableInputChoiceValue(compare);
                }

                if (string.Equals(normalizedMnemonic, "JE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedMnemonic, "JZ", StringComparison.OrdinalIgnoreCase))
                {
                    return IsBinaryInputChoiceValue(compare);
                }

                return false;
            }

            if (string.Equals(normalizedMnemonic, "JE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedMnemonic, "JZ", StringComparison.OrdinalIgnoreCase))
            {
                return IsDisplayableInputChoiceValue(compare);
            }

            return false;
        }

        private static bool IsDisplayableInputChoiceValue(byte value)
        {
            return value == 0x1B || (value >= 0x20 && value <= 0x7E);
        }

        private static bool IsBinaryInputChoiceValue(byte value)
        {
            char upper = char.ToUpperInvariant((char)value);
            return upper == 'Y' || upper == 'N';
        }

        private void MultiplyInlineProbability(PathAnalysisResult result, int numeratorFactor, int denominatorFactor)
        {
            if (result == null ||
                denominatorFactor <= 1 ||
                numeratorFactor <= 0 ||
                numeratorFactor >= denominatorFactor)
            {
                return;
            }

            long numerator = Math.Max(1, result.InlineProbabilityNumerator);
            long denominator = Math.Max(1, result.InlineProbabilityDenominator);

            numerator *= numeratorFactor;
            denominator *= denominatorFactor;

            result.InlineProbabilityNumerator = numerator > int.MaxValue ? int.MaxValue : (int)numerator;
            result.InlineProbabilityDenominator = denominator > int.MaxValue ? int.MaxValue : (int)denominator;
        }

        private void TrackStateValueConstraintForCurrentFlags(
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken,
            PathAnalysisResult result)
        {
            if (result == null)
                return;

            var constraints = BuildStateValueConstraintsForBranch(registerTracker, mnemonic, branchTaken);
            if (constraints.Count == 0)
                return;

            foreach (var kvp in constraints)
            {
                if (!result.StateValueConstraints.TryGetValue(kvp.Key, out var info))
                {
                    info = new StateValueConstraintInfo();
                    result.StateValueConstraints[kvp.Key] = info;
                }

                info.MergeFrom(kvp.Value);

                if (result.MemoryWrittenAddresses?.Contains(kvp.Key) == true)
                    result.LocallyMaterializedStateValueConstraintAddresses.Add(kvp.Key);
            }
        }

        private HashSet<ushort> BuildLocallyMaterializedStateValueConstraintAddressesForBranch(
            PathAnalysisResult result,
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken)
        {
            var constraints = BuildStateValueConstraintsForBranch(registerTracker, mnemonic, branchTaken);
            if (constraints.Count == 0 || result?.MemoryWrittenAddresses == null)
                return new HashSet<ushort>();

            return constraints.Keys
                .Where(address => result.MemoryWrittenAddresses.Contains(address))
                .ToHashSet();
        }

        private Dictionary<ushort, StateValueConstraintInfo> BuildStateValueConstraintsForBranch(
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken)
        {
            var result = new Dictionary<ushort, StateValueConstraintInfo>();
            if (registerTracker == null)
            {
                return result;
            }

            ushort? sourceAddress = null;
            if (!string.IsNullOrWhiteSpace(registerTracker.LastFlagsRegister))
                sourceAddress = registerTracker.GetSourceAddress(registerTracker.LastFlagsRegister);

            if (!sourceAddress.HasValue)
                sourceAddress = registerTracker.LastComparedMemoryAddress;

            if (!sourceAddress.HasValue ||
                sourceAddress.Value == 0x3C38 ||
                sourceAddress.Value == 0x3C39 ||
                sourceAddress.Value == 0x3C3A)
            {
                return result;
            }

            var info = new StateValueConstraintInfo();

            if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastCompareImmediate.HasValue)
            {
                var boundary = GetStateValueBoundaryForJump(mnemonic, branchTaken, registerTracker.LastCompareImmediate.Value);
                if (!boundary.HasValue)
                    return result;

                info.Add(boundary.Value.kind, boundary.Value.value);
            }
            else if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.Test &&
                     registerTracker.LastTestMask == 0xFF)
            {
                var zeroBoundary = GetStateValueBoundaryForZeroTestJump(mnemonic, branchTaken);
                if (!zeroBoundary.HasValue)
                    return result;

                info.Add(zeroBoundary.Value.kind, zeroBoundary.Value.value);
            }
            else
            {
                return result;
            }

            result[sourceAddress.Value] = info;
            return result;
        }

        private (StateValueBoundaryKind kind, byte value)? GetStateValueBoundaryForZeroTestJump(
            string mnemonic,
            bool branchTaken)
        {
            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            return jump switch
            {
                "JE" or "JZ" => branchTaken
                    ? (StateValueBoundaryKind.Exact, (byte)0)
                    : (StateValueBoundaryKind.Excluded, (byte)0),
                "JNE" or "JNZ" => branchTaken
                    ? (StateValueBoundaryKind.Excluded, (byte)0)
                    : (StateValueBoundaryKind.Exact, (byte)0),
                _ => ((StateValueBoundaryKind kind, byte value)?)null
            };
        }

        private (StateValueBoundaryKind kind, byte value)? GetStateValueBoundaryForJump(
            string mnemonic,
            bool branchTaken,
            byte compareValue)
        {
            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            return jump switch
            {
                "JE" or "JZ" => branchTaken
                    ? (StateValueBoundaryKind.Exact, compareValue)
                    : (StateValueBoundaryKind.Excluded, compareValue),
                "JNE" or "JNZ" => branchTaken
                    ? (StateValueBoundaryKind.Excluded, compareValue)
                    : (StateValueBoundaryKind.Exact, compareValue),
                "JB" or "JC" or "JNAE" => branchTaken
                    ? BuildUpperInclusiveBoundary(compareValue - 1)
                    : (StateValueBoundaryKind.LowerInclusive, compareValue),
                "JAE" or "JNB" or "JNC" => branchTaken
                    ? (StateValueBoundaryKind.LowerInclusive, compareValue)
                    : BuildUpperInclusiveBoundary(compareValue - 1),
                "JBE" or "JNA" => branchTaken
                    ? (StateValueBoundaryKind.UpperInclusive, compareValue)
                    : BuildLowerInclusiveBoundary(compareValue + 1),
                "JA" or "JNBE" => branchTaken
                    ? BuildLowerInclusiveBoundary(compareValue + 1)
                    : (StateValueBoundaryKind.UpperInclusive, compareValue),
                _ => ((StateValueBoundaryKind kind, byte value)?)null
            };
        }

        private (StateValueBoundaryKind kind, byte value)? BuildUpperInclusiveBoundary(int value)
        {
            if (value < 0 || value > 0xFF)
                return null;

            return (StateValueBoundaryKind.UpperInclusive, (byte)value);
        }

        private (StateValueBoundaryKind kind, byte value)? BuildLowerInclusiveBoundary(int value)
        {
            if (value < 0 || value > 0xFF)
                return null;

            return (StateValueBoundaryKind.LowerInclusive, (byte)value);
        }

        private bool TryCalculateBranchDiscreteValues(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            out string reg, out List<byte> values)
        {
            reg = null;
            values = null;

            if (registerTracker == null)
                return false;

            reg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(reg) ||
                !registerTracker.TryGetRegisterDiscreteValues(reg, out var possibleValues) ||
                possibleValues == null ||
                possibleValues.Count == 0)
            {
                return false;
            }

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            var filteredValues = new List<byte>();

            if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastCompareImmediate.HasValue)
            {
                byte imm = registerTracker.LastCompareImmediate.Value;
                foreach (byte value in possibleValues)
                {
                    if (!TryEvaluateUnsignedCompareImmediateJump(jump, value, imm, out bool currentBranchTaken))
                        return false;

                    if (currentBranchTaken == branchTaken)
                        filteredValues.Add(value);
                }
            }
            else if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.Test &&
                     registerTracker.LastTestMask == 0xFF)
            {
                foreach (byte value in possibleValues)
                {
                    if (!TryEvaluateTestZeroJump(jump, value, out bool currentBranchTaken))
                        return false;

                    if (currentBranchTaken == branchTaken)
                        filteredValues.Add(value);
                }
            }
            else
            {
                return false;
            }

            if (filteredValues.Count == 0)
                return false;

            values = filteredValues;
            return true;
        }

        private static string FormatDiscreteValues(IReadOnlyList<byte> values)
        {
            if (values == null || values.Count == 0)
                return "<empty>";

            if (values.Count <= 12)
                return string.Join(",", values.Select(value => value.ToString("X2")));

            return $"{values.First():X2}..{values.Last():X2} ({values.Count} values)";
        }



        private bool TryCalculateBranchConstraint(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            out string reg, out int min, out int max)
        {
            reg = null;
            min = 0;
            max = 0;

            if (registerTracker == null)
                return false;

            reg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(reg))
                return false;

            if (registerTracker.TryGetRegisterRange(reg, out var range) && range != null)
            {
                min = range.Min;
                max = range.Max;
            }
            else if ((registerTracker.TryGetPartyFieldValue(reg, out var partyField) && partyField != null && IsComparablePartyField(partyField.Field)) ||
                     (registerTracker.LastComparedPartyField != null && IsComparablePartyField(registerTracker.LastComparedPartyField.Field)))
            {
                min = 0;
                max = 0xFF;
            }
            else
            {
                return false;
            }

            int originalMin = min;
            int originalMax = max;
            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();

            if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.Test &&
                registerTracker.LastTestMask == 0xFF)
            {
                bool isZeroBranch = jump switch
                {
                    "JE" or "JZ" => branchTaken,
                    "JNE" or "JNZ" => !branchTaken,
                    _ => false
                };

                bool isNonZeroBranch = jump switch
                {
                    "JE" or "JZ" => !branchTaken,
                    "JNE" or "JNZ" => branchTaken,
                    _ => false
                };

                if (!isZeroBranch && !isNonZeroBranch)
                    return false;

                if (isZeroBranch)
                {
                    min = max = 0;
                }
                else
                {
                    min = Math.Max(min, 1);
                }

                min = Math.Max(min, originalMin);
                max = Math.Min(max, originalMax);
                return min <= max;
            }

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate ||
                !registerTracker.LastCompareImmediate.HasValue)
            {
                return false;
            }

            int imm = registerTracker.LastCompareImmediate.Value;

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
            min = Math.Max(min, originalMin);
            max = Math.Min(max, originalMax);
            return min <= max;
        }

        private void ApplyBranchConstraintInPlace(RegisterTracker registerTracker, string mnemonic, bool branchTaken,
            uint instructionAddress, bool debugMode)
        {
            if (TryCalculateBranchDiscreteValues(registerTracker, mnemonic, branchTaken, out string reg, out var discreteValues))
            {
                var distribution = GetExistingRegisterDistributionOrUnknown(registerTracker, reg);
                ushort? sourceAddress = registerTracker.GetSourceAddress(reg);
                registerTracker.TryGetDynamicValueFormula(reg, out var formulaBeforeConstraint);
                byte minValue = discreteValues.Min();
                byte maxValue = discreteValues.Max();

                if (sourceAddress.HasValue)
                {
                    registerTracker.SetRegisterDiscreteValuesWithSource(reg, discreteValues, distribution,
                        sourceAddress.Value, instructionAddress, $"branch constraint after {mnemonic}",
                        sourceIndexProviderAddr: sourceAddress.Value);
                    ConstrainEmulatedMemoryRange(sourceAddress.Value, minValue, maxValue);
                }
                else
                {
                    registerTracker.SetRegisterDiscreteValues(reg, discreteValues, distribution);
                }

                if (formulaBeforeConstraint != null)
                    registerTracker.SetDynamicValueFormula(reg, formulaBeforeConstraint.WithValueConstraint(minValue, maxValue));

                if (debugMode)
                {
                    string branchText = branchTaken ? "taken" : "linear";
                    AnalysisDebug.WriteLine(
                        $"      Уточнили варианты {reg} для ветки {branchText} после {mnemonic}: {FormatDiscreteValues(discreteValues)} (инстр. 0x{instructionAddress:X4})");
                }
            }
            else if (TryCalculateBranchConstraint(registerTracker, mnemonic, branchTaken, out reg, out int min, out int max))
            {
                var distribution = GetExistingRegisterDistributionOrUnknown(registerTracker, reg);
                ushort? sourceAddress = registerTracker.GetSourceAddress(reg);
                registerTracker.TryGetDynamicValueFormula(reg, out var formulaBeforeConstraint);
                if (sourceAddress.HasValue)
                {
                    registerTracker.SetRegisterRangeWithSource(reg, (byte)min, (byte)max, distribution,
                        sourceAddress.Value, instructionAddress, $"branch constraint after {mnemonic}",
                        sourceIndexProviderAddr: sourceAddress.Value);
                    ConstrainEmulatedMemoryRange(sourceAddress.Value, min, max);
                }
                else
                {
                    registerTracker.SetRegisterRange(reg, (byte)min, (byte)max, distribution);
                }

                if (formulaBeforeConstraint != null)
                    registerTracker.SetDynamicValueFormula(reg, formulaBeforeConstraint.WithValueConstraint((byte)min, (byte)max));

                if (debugMode)
                {
                    string branchText = branchTaken ? "taken" : "linear";
                    AnalysisDebug.WriteLine(
                        $"      Уточнили диапазон {reg} для ветки {branchText} после {mnemonic}: {min:X2}..{max:X2} (инстр. 0x{instructionAddress:X4})");
                }
            }

            ApplyKnownFlagsForResolvedBranch(registerTracker, mnemonic, branchTaken);
        }

        private RegisterTracker CloneRegisterStateForBranch(BinaryReader br, RegisterTracker registerTracker, string mnemonic,
            bool branchTaken, uint instructionAddress, uint nextAddress, uint branchTarget)
        {
            var clone = registerTracker?.Clone() ?? new RegisterTracker();

            if (TryCalculateBranchDiscreteValues(clone, mnemonic, branchTaken, out string reg, out var discreteValues))
            {
                var distribution = GetExistingRegisterDistributionOrUnknown(clone, reg);
                ushort? sourceAddress = clone.GetSourceAddress(reg);
                clone.TryGetDynamicValueFormula(reg, out var formulaBeforeConstraint);
                byte minValue = discreteValues.Min();
                byte maxValue = discreteValues.Max();

                if (sourceAddress.HasValue)
                {
                    clone.SetRegisterDiscreteValuesWithSource(reg, discreteValues, distribution,
                        sourceAddress.Value, instructionAddress, $"branch constraint after {mnemonic}",
                        sourceIndexProviderAddr: sourceAddress.Value);
                }
                else
                {
                    clone.SetRegisterDiscreteValues(reg, discreteValues, distribution);
                }

                if (formulaBeforeConstraint != null)
                    clone.SetDynamicValueFormula(reg, formulaBeforeConstraint.WithValueConstraint(minValue, maxValue));
            }
            else if (TryCalculateBranchConstraint(clone, mnemonic, branchTaken, out reg, out int min, out int max))
            {
                var distribution = GetExistingRegisterDistributionOrUnknown(clone, reg);
                ushort? sourceAddress = clone.GetSourceAddress(reg);
                clone.TryGetDynamicValueFormula(reg, out var formulaBeforeConstraint);
                if (sourceAddress.HasValue)
                {
                    clone.SetRegisterRangeWithSource(reg, (byte)min, (byte)max, distribution,
                        sourceAddress.Value, instructionAddress, $"branch constraint after {mnemonic}",
                        sourceIndexProviderAddr: sourceAddress.Value);
                }
                else
                {
                    clone.SetRegisterRange(reg, (byte)min, (byte)max, distribution);
                }

                if (formulaBeforeConstraint != null)
                    clone.SetDynamicValueFormula(reg, formulaBeforeConstraint.WithValueConstraint((byte)min, (byte)max));
            }

            ApplyKnownFlagsForResolvedBranch(clone, mnemonic, branchTaken);

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

        private Dictionary<ushort, ValueRange8> CloneEmulatedMemoryRangesForBranch(
            RegisterTracker registerTracker,
            string mnemonic,
            bool branchTaken)
        {
            var ranges = CloneRangeDictionary(_emulatedMemory8Ranges);

            string reg;
            int min;
            int max;

            if (registerTracker == null)
            {
                return ranges;
            }

            if (TryCalculateBranchDiscreteValues(registerTracker, mnemonic, branchTaken, out reg, out var discreteValues))
            {
                min = discreteValues.Min();
                max = discreteValues.Max();
            }
            else if (!TryCalculateBranchConstraint(registerTracker, mnemonic, branchTaken, out reg, out min, out max))
            {
                return ranges;
            }

            ushort? sourceAddress = registerTracker.GetSourceAddress(reg);
            if (!sourceAddress.HasValue ||
                !ranges.TryGetValue(sourceAddress.Value, out var existingRange) ||
                existingRange == null)
            {
                return ranges;
            }

            int constrainedMin = Math.Max(existingRange.Min, min);
            int constrainedMax = Math.Min(existingRange.Max, max);
            if (constrainedMin <= constrainedMax)
                ranges[sourceAddress.Value] = new ValueRange8((byte)constrainedMin, (byte)constrainedMax);

            return ranges;
        }

        private void ConstrainEmulatedMemoryRange(ushort memAddr, int min, int max)
        {
            if (!_emulatedMemory8Ranges.TryGetValue(memAddr, out var existingRange) || existingRange == null)
                return;

            int constrainedMin = Math.Max(existingRange.Min, min);
            int constrainedMax = Math.Min(existingRange.Max, max);
            if (constrainedMin <= constrainedMax)
                _emulatedMemory8Ranges[memAddr] = new ValueRange8((byte)constrainedMin, (byte)constrainedMax);
        }

        private void ApplyKnownFlagsForResolvedBranch(RegisterTracker registerTracker, string mnemonic, bool branchTaken)
        {
            if (registerTracker == null)
                return;

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            bool zeroKnown = false;
            bool zeroValue = registerTracker.ZeroFlag;
            bool carryKnown = false;
            bool carryValue = registerTracker.CarryFlag;
            bool existingZeroKnown = registerTracker.FlagsKnown &&
                (!registerTracker.LastFlagsFromBranchConstraint || registerTracker.BranchConstraintZeroFlagKnown);
            bool existingCarryKnown = registerTracker.FlagsKnown &&
                (!registerTracker.LastFlagsFromBranchConstraint || registerTracker.BranchConstraintCarryFlagKnown);
            bool existingZeroValue = registerTracker.ZeroFlag;
            bool existingCarryValue = registerTracker.CarryFlag;

            switch (jump)
            {
                case "JE":
                case "JZ":
                    zeroKnown = true;
                    zeroValue = branchTaken;
                    break;

                case "JNE":
                case "JNZ":
                    zeroKnown = true;
                    zeroValue = !branchTaken;
                    break;

                case "JB":
                case "JC":
                case "JNAE":
                    carryKnown = true;
                    carryValue = branchTaken;
                    if (branchTaken)
                    {
                        zeroKnown = true;
                        zeroValue = false;
                    }
                    break;

                case "JAE":
                case "JNB":
                case "JNC":
                    carryKnown = true;
                    carryValue = !branchTaken;
                    if (!branchTaken)
                    {
                        zeroKnown = true;
                        zeroValue = false;
                    }
                    break;

                case "JBE":
                case "JNA":
                    if (!branchTaken)
                    {
                        carryKnown = true;
                        carryValue = false;
                        zeroKnown = true;
                        zeroValue = false;
                    }
                    break;

                case "JA":
                case "JNBE":
                    if (branchTaken)
                    {
                        carryKnown = true;
                        carryValue = false;
                        zeroKnown = true;
                        zeroValue = false;
                    }
                    break;
            }

            if (!zeroKnown && existingZeroKnown)
            {
                zeroKnown = true;
                zeroValue = existingZeroValue;
            }

            if (!carryKnown && existingCarryKnown)
            {
                carryKnown = true;
                carryValue = existingCarryValue;
            }

            if (!zeroKnown && !carryKnown)
                return;

            if (zeroKnown)
                registerTracker.ZeroFlag = zeroValue;
            if (carryKnown)
                registerTracker.CarryFlag = carryValue;

            registerTracker.FlagsKnown = true;
            registerTracker.LastFlagsFromBranchConstraint = true;
            registerTracker.BranchConstraintZeroFlagKnown = zeroKnown;
            registerTracker.BranchConstraintCarryFlagKnown = carryKnown;
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

        private bool TryGetExactByteRegisterOrRange(RegisterTracker registerTracker, string reg, out byte value)
        {
            value = 0;
            if (registerTracker == null || string.IsNullOrWhiteSpace(reg))
                return false;

            if (registerTracker.TryGetByteRegisterValue(reg, out value))
                return true;

            if (registerTracker.TryGetRegisterRange(reg, out var range) && range?.IsExact == true)
            {
                value = range.Min;
                return true;
            }

            return false;
        }

        private void ApplyShiftRotateMemory8Operation(
            ushort memAddr,
            string eaText,
            byte operation,
            X86Instruction insn,
            BinaryReader br,
            RegisterTracker registerTracker,
            PathAnalysisResult result,
            byte targetX,
            byte targetY,
            bool debugMode)
        {
            if (!IsSupportedShiftRotateByteOperation(operation))
                return;

            var inputValues = new List<byte>();
            if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte exactValue))
            {
                inputValues.Add(exactValue);
            }
            else if (TryGetEmulatedMemory8Range(memAddr, out var range, out _))
            {
                if (range == null || range.Max < range.Min)
                    return;

                for (int value = range.Min; value <= range.Max; value++)
                    inputValues.Add((byte)value);
            }
            else
            {
                return;
            }

            bool needsCarryIn = operation == 0x02 || operation == 0x03;
            var carryInputs = needsCarryIn && !registerTracker.FlagsKnown
                ? new[] { false, true }
                : new[] { registerTracker.FlagsKnown && registerTracker.CarryFlag };

            var outputValues = new HashSet<byte>();
            bool carryOutputKnown = true;
            bool? carryOutput = null;

            foreach (byte inputValue in inputValues)
            {
                foreach (bool carryInput in carryInputs)
                {
                    byte outputValue = TransformShiftRotateByte(inputValue, operation, carryInput, out bool nextCarry);
                    outputValues.Add(outputValue);
                    if (!carryOutput.HasValue)
                        carryOutput = nextCarry;
                    else if (carryOutput.Value != nextCarry)
                        carryOutputKnown = false;
                }
            }

            if (outputValues.Count == 0)
                return;

            byte minValue = outputValues.Min();
            byte maxValue = outputValues.Max();
            string sourceDescription = $"{GetShiftRotateOperationName(operation)} byte ptr {eaText}, 1";

            if (minValue == maxValue)
            {
                ApplyTrackedByteWrite(memAddr, minValue, result, targetX, targetY, insn, debugMode, sourceDescription);
            }
            else
            {
                ApplyTrackedByteRangeWrite(memAddr, new ValueRange8(minValue, maxValue), RegisterValueDistribution.Unknown,
                    result, targetX, targetY, insn, debugMode, sourceDescription);
            }

            if (carryOutputKnown && carryOutput.HasValue)
            {
                registerTracker.CarryFlag = carryOutput.Value;
                registerTracker.FlagsKnown = true;
                registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.Arithmetic, (uint)insn.Address);
            }
            else
            {
                registerTracker.FlagsKnown = false;
                registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.Arithmetic, (uint)insn.Address);
            }
        }

        private static bool IsSupportedShiftRotateByteOperation(byte operation)
        {
            return operation == 0x02 || operation == 0x03 || operation == 0x04 || operation == 0x05;
        }

        private static string GetShiftRotateOperationName(byte operation)
        {
            return operation switch
            {
                0x02 => "RCL",
                0x03 => "RCR",
                0x04 => "SHL",
                0x05 => "SHR",
                _ => "SHIFT"
            };
        }

        private static ShiftLoopOperationKind MapShiftRotateOperationKind(byte operation)
        {
            return operation switch
            {
                0x02 => ShiftLoopOperationKind.Rcl,
                0x03 => ShiftLoopOperationKind.Rcr,
                0x04 => ShiftLoopOperationKind.Shl,
                0x05 => ShiftLoopOperationKind.Shr,
                _ => ShiftLoopOperationKind.Shl
            };
        }

        private static byte TransformShiftRotateByte(byte value, byte operation, bool carryIn, out bool carryOut)
        {
            switch (operation)
            {
                case 0x02: // RCL r/m8,1
                    carryOut = (value & 0x80) != 0;
                    return (byte)((value << 1) | (carryIn ? 1 : 0));
                case 0x03: // RCR r/m8,1
                    carryOut = (value & 0x01) != 0;
                    return (byte)((value >> 1) | (carryIn ? 0x80 : 0x00));
                case 0x04: // SHL/SAL r/m8,1
                    carryOut = (value & 0x80) != 0;
                    return (byte)(value << 1);
                case 0x05: // SHR r/m8,1
                    carryOut = (value & 0x01) != 0;
                    return (byte)(value >> 1);
                default:
                    carryOut = false;
                    return value;
            }
        }

        private RegisterValueDistribution GetShiftedRangeDistribution(RegisterValueDistribution distribution, byte operation)
        {
            return operation switch
            {
                4 when distribution == RegisterValueDistribution.UniformDiscreteRange => RegisterValueDistribution.EvenDiscreteRange,
                4 when distribution == RegisterValueDistribution.EvenDiscreteRange => RegisterValueDistribution.EvenDiscreteRange,
                5 when distribution == RegisterValueDistribution.EvenDiscreteRange => RegisterValueDistribution.UniformDiscreteRange,
                _ => distribution
            };
        }

        private RegisterValueDistribution GetAdditiveRangeDistribution(RegisterValueDistribution distribution, int delta)
        {
            return distribution switch
            {
                RegisterValueDistribution.EvenDiscreteRange when (delta & 1) == 0 => RegisterValueDistribution.EvenDiscreteRange,
                RegisterValueDistribution.EvenDiscreteRange => RegisterValueDistribution.Unknown,
                _ => distribution
            };
        }

        private (int numerator, int denominator) EstimateBranchProbability(string mnemonic, RegisterTracker registerTracker, bool branchTaken)
        {
            if (registerTracker == null)
                return (1, 1);

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.Test)
                return (1, 1);

            string compareRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(compareRegister))
                return (1, 1);

            bool hasDiscreteValues = registerTracker.TryGetRegisterDiscreteValues(compareRegister, out var discreteValues) &&
                discreteValues != null &&
                discreteValues.Count > 0;

            ValueRange8 range = null;
            int total;
            if (hasDiscreteValues)
            {
                total = discreteValues.Count;
            }
            else
            {
                if (!registerTracker.TryGetRegisterRange(compareRegister, out range) || range == null)
                    return (1, 1);

                total = range.Max - range.Min + 1;
            }

            if (total <= 0)
                return (1, 1);

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            int favorable;
            if (registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareImmediate)
            {
                if (!registerTracker.LastCompareImmediate.HasValue)
                    return (1, 1);

                favorable = hasDiscreteValues
                    ? CountFavorableValues(jump, discreteValues, registerTracker.LastCompareImmediate.Value, branchTaken)
                    : CountFavorableValues(jump, range, registerTracker.LastCompareImmediate.Value, branchTaken);
            }
            else if (registerTracker.LastTestMask == 0xFF)
            {
                favorable = hasDiscreteValues
                    ? CountFavorableTestZeroValues(jump, discreteValues, branchTaken)
                    : CountFavorableTestZeroValues(jump, range, branchTaken);
            }
            else
            {
                return (1, 1);
            }

            if (favorable < 0 || favorable > total)
                return (1, 1);

            if (favorable == 0)
                return (0, 1);

            if (favorable == total)
                return (1, 1);

            if (!registerTracker.TryGetRegisterDistribution(compareRegister, out var distribution) ||
                !IsRandomLikeDistribution(distribution))
            {
                return (1, 1);
            }

            return (favorable, total);
        }

        private int CountFavorableTestZeroValues(string mnemonic, IReadOnlyList<byte> values, bool branchTaken)
        {
            int count = 0;
            foreach (byte value in values)
            {
                if (!TryEvaluateTestZeroJump(mnemonic, value, out bool taken))
                    return -1;

                if (taken == branchTaken)
                    count++;
            }

            return count;
        }

        private int CountFavorableTestZeroValues(string mnemonic, ValueRange8 range, bool branchTaken)
        {
            int count = 0;
            for (int value = range.Min; value <= range.Max; value++)
            {
                bool taken = mnemonic switch
                {
                    "JE" or "JZ" => value == 0,
                    "JNE" or "JNZ" => value != 0,
                    _ => false
                };

                if (taken == branchTaken)
                    count++;
            }

            return count;
        }

        private int CountFavorableValues(string mnemonic, IReadOnlyList<byte> values, byte imm, bool branchTaken)
        {
            int count = 0;
            foreach (byte value in values)
            {
                if (!TryEvaluateUnsignedCompareImmediateJump(mnemonic, value, imm, out bool taken))
                    return -1;

                if (taken == branchTaken)
                    count++;
            }

            return count;
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
            if (registerTracker == null)
                return null;

            if (TryEvaluateConditionalJumpFromCompareImmediateRange(mnemonic, registerTracker, out bool rangeBranchResult))
                return rangeBranchResult;

            if (!registerTracker.FlagsKnown)
                return null;

            if (registerTracker.LastFlagsFromBranchConstraint)
            {
                if (TryEvaluateConditionalJumpFromKnownBranchFlags(mnemonic, registerTracker, out bool branchResult))
                    return branchResult;

                return null;
            }

            // Схлопываем только координатные проверки.
            // Внешние guard-условия вроде MOV AL,[0xCAFE] / OR AL,AL / JNE должны
            // оставаться развилкой, даже если текущее значение известно.
            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareMemory &&
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.Test)
                return null;

            string flagsReg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            bool hasFlagsRegister = !string.IsNullOrWhiteSpace(flagsReg);
            bool isDeterministicMemoryCompare = IsDeterministicMemoryCompareSource(registerTracker);

            bool allowDeterministicResolution =
                registerTracker.LastFlagsFromCoordinate ||
                (hasFlagsRegister && IsDeterministicFlagsSource(registerTracker, flagsReg)) ||
                isDeterministicMemoryCompare;

            if (!allowDeterministicResolution)
                return null;

            if (!hasFlagsRegister && !isDeterministicMemoryCompare)
                return null;

            if (hasFlagsRegister)
            {
                bool hasExactValue = registerTracker.TryGetRegisterValue(flagsReg, out _)
                    || TryGetExactByteRegisterOrRange(registerTracker, flagsReg, out _);

                if (!hasExactValue)
                    return null;
            }

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

        private bool TryEvaluateConditionalJumpFromCompareImmediateRange(string mnemonic, RegisterTracker registerTracker, out bool branchTaken)
        {
            branchTaken = false;
            if (registerTracker == null ||
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate ||
                !registerTracker.LastCompareImmediate.HasValue)
            {
                return false;
            }

            string compareRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(compareRegister) ||
                !registerTracker.TryGetRegisterRange(compareRegister, out var range) ||
                range == null)
            {
                return false;
            }

            if (registerTracker.TryGetPendingExternalCallResultKind(compareRegister, out var externalKind) &&
                externalKind == RegisterTracker.ExternalCallResultKind.UserInput)
            {
                return false;
            }

            if (IsKeyboardInputPlaceholderRange(range))
                return false;

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            bool? resolved = null;
            byte imm = registerTracker.LastCompareImmediate.Value;

            IEnumerable<byte> candidateValues;
            if (registerTracker.TryGetRegisterDiscreteValues(compareRegister, out var discreteValues) &&
                discreteValues != null &&
                discreteValues.Count > 0)
            {
                candidateValues = discreteValues;
            }
            else
            {
                candidateValues = Enumerable.Range(range.Min, range.Max - range.Min + 1)
                    .Select(value => (byte)value);
            }

            foreach (byte value in candidateValues)
            {
                if (!TryEvaluateUnsignedCompareImmediateJump(jump, value, imm, out bool current))
                    return false;

                if (!resolved.HasValue)
                    resolved = current;
                else if (resolved.Value != current)
                    return false;
            }

            if (!resolved.HasValue)
                return false;

            branchTaken = resolved.Value;
            return true;
        }

        private static bool IsKeyboardInputPlaceholderRange(ValueRange8 range)
        {
            return range != null && range.Min == KEYBOARD_INPUT_MIN && range.Max == KEYBOARD_INPUT_MAX;
        }

        private static bool TryEvaluateUnsignedCompareImmediateJump(string jump, byte value, byte imm, out bool branchTaken)
        {
            switch (jump)
            {
                case "JE":
                case "JZ":
                    branchTaken = value == imm;
                    return true;

                case "JNE":
                case "JNZ":
                    branchTaken = value != imm;
                    return true;

                case "JB":
                case "JC":
                case "JNAE":
                    branchTaken = value < imm;
                    return true;

                case "JBE":
                case "JNA":
                    branchTaken = value <= imm;
                    return true;

                case "JA":
                case "JNBE":
                    branchTaken = value > imm;
                    return true;

                case "JAE":
                case "JNB":
                case "JNC":
                    branchTaken = value >= imm;
                    return true;

                default:
                    branchTaken = false;
                    return false;
            }
        }

        private static bool TryEvaluateTestZeroJump(string jump, byte value, out bool branchTaken)
        {
            switch (jump)
            {
                case "JE":
                case "JZ":
                    branchTaken = value == 0;
                    return true;

                case "JNE":
                case "JNZ":
                    branchTaken = value != 0;
                    return true;

                default:
                    branchTaken = false;
                    return false;
            }
        }

        private bool TryEvaluateConditionalJumpFromKnownBranchFlags(string mnemonic, RegisterTracker registerTracker, out bool branchTaken)
        {
            branchTaken = false;
            if (registerTracker == null || !registerTracker.LastFlagsFromBranchConstraint)
                return false;

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            bool zeroKnown = registerTracker.BranchConstraintZeroFlagKnown;
            bool carryKnown = registerTracker.BranchConstraintCarryFlagKnown;
            bool zero = registerTracker.ZeroFlag;
            bool carry = registerTracker.CarryFlag;

            switch (jump)
            {
                case "JE":
                case "JZ":
                    if (!zeroKnown)
                        return false;
                    branchTaken = zero;
                    return true;

                case "JNE":
                case "JNZ":
                    if (!zeroKnown)
                        return false;
                    branchTaken = !zero;
                    return true;

                case "JB":
                case "JC":
                case "JNAE":
                    if (!carryKnown)
                        return false;
                    branchTaken = carry;
                    return true;

                case "JAE":
                case "JNB":
                case "JNC":
                    if (!carryKnown)
                        return false;
                    branchTaken = !carry;
                    return true;

                case "JBE":
                case "JNA":
                    if ((carryKnown && carry) || (zeroKnown && zero))
                    {
                        branchTaken = true;
                        return true;
                    }
                    if (carryKnown && zeroKnown)
                    {
                        branchTaken = carry || zero;
                        return true;
                    }
                    return false;

                case "JA":
                case "JNBE":
                    if ((carryKnown && carry) || (zeroKnown && zero))
                    {
                        branchTaken = false;
                        return true;
                    }
                    if (carryKnown && zeroKnown)
                    {
                        branchTaken = !carry && !zero;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private bool IsDeterministicFlagsSource(RegisterTracker registerTracker, string flagsReg)
        {
            if (registerTracker == null || string.IsNullOrWhiteSpace(flagsReg))
                return false;

            ushort? sourceAddress = registerTracker.GetSourceAddress(flagsReg);
            if (sourceAddress.HasValue && _emulatedMemory8.ContainsKey(sourceAddress.Value))
                return true;

            if (sourceAddress.HasValue && IsStaticMapDataAddress(sourceAddress.Value))
                return true;

            return false;
        }

        private bool IsDeterministicMemoryCompareSource(RegisterTracker registerTracker)
        {
            if (registerTracker == null ||
                (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareMemory &&
                 registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate) ||
                !registerTracker.LastComparedMemoryAddress.HasValue)
            {
                return false;
            }

            ushort memAddr = registerTracker.LastComparedMemoryAddress.Value;
            return _emulatedMemory8.ContainsKey(memAddr) ||
                   IsStaticMapDataAddress(memAddr) ||
                   memAddr == BATTLE_MONSTER_COUNT_ADDRESS;
        }

        private static bool ShouldTrackUnknownExternalStateGuardAddress(ushort memAddr)
        {
            return memAddr == 0x3C98 ||
                   memAddr == 0x3C9E ||
                   memAddr == 0x3CA1;
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

        private bool TryShiftTrackedSemanticByteRange(ushort memAddr, int delta, PathAnalysisResult result, bool debugMode)
        {
            if (result == null || delta == 0)
                return false;

            if (memAddr == BATTLE_MONSTER_COUNT_ADDRESS &&
                !result.IsBattleMonsterCountIndeterminate)
            {
                ValueRange8 sourceRange = result.BattleMonsterCountRange;
                if (sourceRange == null && result.BattleMonsterCount.HasValue)
                    sourceRange = new ValueRange8(result.BattleMonsterCount.Value, result.BattleMonsterCount.Value);

                if (sourceRange != null)
                {
                    int newMin = Math.Clamp(sourceRange.Min + delta, 0, 0xFF);
                    int newMax = Math.Clamp(sourceRange.Max + delta, 0, 0xFF);

                    result.BattleMonsterCountRange = new ValueRange8((byte)newMin, (byte)newMax);
                    result.BattleMonsterCount = newMin == newMax ? (byte)newMin : (byte?)null;
                    result.IsBattleMonsterCountIndeterminate = false;
                    result.HasSignificantCode = true;

                    if (debugMode)
                    {
                        string operation = delta > 0 ? "INC" : "DEC";
                        AnalysisDebug.WriteLine(
                            $"        {operation} [0x{BATTLE_MONSTER_COUNT_ADDRESS:X4}]: диапазон количества монстров {sourceRange.Min}-{sourceRange.Max} -> {newMin}-{newMax}");
                    }

                    return true;
                }
            }

            return false;
        }

        private static void TrackBattleMonsterStrengthAdjustment(
            PathAnalysisResult result,
            ushort memAddr,
            int delta,
            uint instructionAddress,
            bool debugMode)
        {
            if (result == null ||
                memAddr != BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS ||
                delta == 0)
            {
                return;
            }

            result.BattleMonsterStrengthAdjustment += delta;
            result.HasSignificantCode = true;

            if (debugMode)
            {
                string sign = delta > 0 ? "+" : string.Empty;
                AnalysisDebug.WriteLine(
                    $"        Семантика [0x{BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS:X4}]: модификатор силы монстров битвы {sign}{delta} (инструкция 0x{instructionAddress:X4})");
            }
        }

        private bool TryTrackBattleMonsterStrengthSet(
            BinaryReader br,
            ushort memAddr,
            byte newValue,
            PathAnalysisResult result,
            byte targetX,
            byte targetY,
            uint instructionAddress,
            bool debugMode)
        {
            if (memAddr != BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS)
            {
                return false;
            }

            byte previousValue = 0;
            TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out previousValue);
            TrackBattleMonsterStrengthAdjustment(
                result,
                memAddr,
                newValue - previousValue,
                instructionAddress,
                debugMode);
            return true;
        }

        private static bool TryTrackBattleMonsterStrengthDeltaFromRegisterWrite(
            RegisterTracker registerTracker,
            string registerName,
            ushort memAddr,
            PathAnalysisResult result,
            uint instructionAddress,
            bool debugMode)
        {
            if (registerTracker == null ||
                memAddr != BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS ||
                !registerTracker.TryGetMemoryByteDeltaSource(registerName, out ushort sourceAddr, out int delta) ||
                sourceAddr != memAddr ||
                delta == 0)
            {
                return false;
            }

            result?.AdjustedMemoryAddresses.Add(memAddr);
            TrackBattleMonsterStrengthAdjustment(result, memAddr, delta, instructionAddress, debugMode);
            registerTracker.ClearMemoryByteDeltaSourceForRegister(registerName);
            return true;
        }

        private bool TrySplitSemanticRangeByteRegisterWrite(
            ushort memAddr,
            string registerName,
            RegisterTracker registerTracker,
            PathAnalysisResult result,
            uint instructionAddress,
            int callDepth,
            List<uint> pendingReturnAddresses,
            bool debugMode)
        {
            if (result == null ||
                !ShouldSplitSemanticRangeByteWrite(memAddr) ||
                registerTracker == null ||
                string.IsNullOrWhiteSpace(registerName) ||
                !registerTracker.TryGetRegisterRange(registerName, out var range) ||
                range == null ||
                range.IsExact ||
                !registerTracker.TryGetRegisterDistribution(registerName, out var distribution) ||
                !IsRandomLikeDistribution(distribution))
            {
                return false;
            }

            var candidates = BuildSemanticRangeSplitCandidates(registerTracker, registerName, range, distribution);
            if (candidates.Count <= 1 || candidates.Count > 16)
                return false;

            string fullRegisterName = GetFullRegisterNameForByteRegister(registerName);
            if (string.IsNullOrWhiteSpace(fullRegisterName))
                return false;

            int denominator = candidates.Count;
            foreach (byte candidate in candidates)
            {
                var splitTracker = registerTracker.Clone();
                splitTracker.TrackPartialRegisterOperation(
                    fullRegisterName,
                    registerName,
                    candidate,
                    instructionAddress,
                    $"SPLIT {registerName}, 0x{candidate:X2}");

                result.AlternativePaths.Add(new AlternativePath
                {
                    Address = instructionAddress,
                    TargetAddress = instructionAddress,
                    Condition = $"SPLIT [0x{memAddr:X4}] = 0x{candidate:X2}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1,
                    RegisterState = splitTracker,
                    IsInputChoiceBranch = false,
                    ProbabilityNumerator = 1,
                    ProbabilityDenominator = denominator,
                    CallDepth = callDepth,
                    PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                    EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8),
                    EmulatedMemory8Ranges = CloneRangeDictionary(_emulatedMemory8Ranges),
                    EmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions),
                    EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(result.PendingPersistentCounterProgressions),
                    BranchStateValueConstraints = new Dictionary<ushort, StateValueConstraintInfo>
                    {
                        [memAddr] = new StateValueConstraintInfo
                        {
                            ExactValues = new HashSet<byte> { candidate }
                        }
                    },
                    BranchLocallyMaterializedStateValueConstraintAddresses = new HashSet<ushort> { memAddr }
                });
            }

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        Развилка по диапазону {registerName} для семантической записи в [0x{memAddr:X4}]: " +
                    string.Join("/", candidates.Select(value => $"0x{value:X2}")));
            }

            result.IsTerminated = true;
            result.HasSignificantCode = true;
            return true;
        }

        private static bool ShouldSplitSemanticRangeByteWrite(ushort memAddr)
        {
            return memAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS;
        }

        private static List<byte> BuildSemanticRangeSplitCandidates(
            RegisterTracker registerTracker,
            string registerName,
            ValueRange8 range,
            RegisterValueDistribution distribution)
        {
            if (registerTracker != null &&
                registerTracker.TryGetRegisterDiscreteValues(registerName, out var discreteValues) &&
                discreteValues != null &&
                discreteValues.Count > 0)
            {
                return discreteValues
                    .Where(value => value >= range.Min && value <= range.Max)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
            }

            var values = new List<byte>();
            for (int value = range.Min; value <= range.Max; value++)
            {
                if (distribution == RegisterValueDistribution.EvenDiscreteRange &&
                    (value & 1) != 0)
                {
                    continue;
                }

                values.Add((byte)value);
            }

            return values;
        }

        private static Dictionary<ushort, PersistentCounterProgressionInfo> ClonePendingPersistentCounterProgressions(
            Dictionary<ushort, PersistentCounterProgressionInfo> source)
        {
            return source == null
                ? new Dictionary<ushort, PersistentCounterProgressionInfo>()
                : source
                    .Where(kvp => kvp.Value != null)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone());
        }

        private static List<PersistentCounterProgressionInfo> ClonePersistentCounterProgressions(
            IEnumerable<PersistentCounterProgressionInfo> source)
        {
            return source?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<PersistentCounterProgressionInfo>();
        }

        private static List<DynamicRandomBoundDependencyInfo> CloneDynamicRandomBoundDependencies(
            IEnumerable<DynamicRandomBoundDependencyInfo> source)
        {
            return source?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<DynamicRandomBoundDependencyInfo>();
        }

        private static void AddOrReplacePersistentCounterProgression(
            List<PersistentCounterProgressionInfo> target,
            PersistentCounterProgressionInfo progression)
        {
            if (target == null || progression == null)
                return;

            string key = progression.GetIdentityKey();
            int existingIndex = target.FindIndex(item => item != null && item.GetIdentityKey() == key);
            if (existingIndex >= 0)
                target[existingIndex] = progression.Clone();
            else
                target.Add(progression.Clone());
        }

        private static void AddOrReplaceDynamicRandomBoundDependency(
            List<DynamicRandomBoundDependencyInfo> target,
            DynamicRandomBoundDependencyInfo dependency)
        {
            if (target == null || dependency == null)
                return;

            string key = dependency.GetIdentityKey();
            int existingIndex = target.FindIndex(item => item != null && item.GetIdentityKey() == key);
            if (existingIndex >= 0)
                target[existingIndex] = dependency.Clone();
            else
                target.Add(dependency.Clone());
        }

        private static void TrackDynamicRandomBoundDependency(
            PathAnalysisResult result,
            DynamicValueFormulaInfo upperBoundFormula,
            string resultRegisterName,
            uint callInstructionAddress,
            byte? maxObservedUpperBound,
            bool debugMode)
        {
            if (result == null || upperBoundFormula == null)
                return;

            var dependency = new DynamicRandomBoundDependencyInfo
            {
                UpperBoundFormula = upperBoundFormula.Clone(),
                TargetKind = DynamicRandomBoundTargetKind.RandomUpperBound,
                MaxObservedUpperBound = maxObservedUpperBound,
                ResultRegisterName = resultRegisterName,
                RandomCallInstructionAddress = callInstructionAddress
            };

            result.DynamicRandomBoundDependencies ??= new List<DynamicRandomBoundDependencyInfo>();
            AddOrReplaceDynamicRandomBoundDependency(result.DynamicRandomBoundDependencies, dependency);
            result.HasSignificantCode = true;

            if (debugMode)
                AnalysisDebug.WriteLine($"        Семантика динамической верхней границы random: {dependency.ToDebugString()}");
        }

        private static void TrackPersistentCounterAdjustment(
            PathAnalysisResult result,
            ushort counterAddress,
            byte oldValue,
            byte newValue,
            int delta,
            uint instructionAddress,
            bool debugMode)
        {
            if (result == null || delta == 0)
                return;

            result.PendingPersistentCounterProgressions ??= new Dictionary<ushort, PersistentCounterProgressionInfo>();
            if (!result.PendingPersistentCounterProgressions.TryGetValue(counterAddress, out var progression) ||
                progression == null)
            {
                progression = new PersistentCounterProgressionInfo
                {
                    CounterAddress = counterAddress,
                    InitialValue = oldValue,
                    CurrentValue = newValue,
                    Delta = delta,
                    AdjustmentInstructionAddress = instructionAddress
                };
                result.PendingPersistentCounterProgressions[counterAddress] = progression;
            }
            else
            {
                progression.CurrentValue = newValue;
                progression.Delta += delta;
                if (progression.AdjustmentInstructionAddress == 0)
                    progression.AdjustmentInstructionAddress = instructionAddress;
            }

            if (debugMode)
            {
                string opText = delta > 0 ? $"+{delta}" : delta.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AnalysisDebug.WriteLine(
                    $"        Семантика persistent counter: [0x{counterAddress:X4}] {oldValue}->{newValue} ({opText})");
            }
        }

        private static void TrackPersistentCounterCapFromCompare(
            PathAnalysisResult result,
            RegisterTracker registerTracker,
            string registerName,
            byte capValue,
            uint instructionAddress,
            bool debugMode)
        {
            if (result?.PendingPersistentCounterProgressions == null || registerTracker == null)
                return;

            ushort? sourceAddress = registerTracker.GetSourceAddress(registerName);
            if (!sourceAddress.HasValue ||
                !result.PendingPersistentCounterProgressions.TryGetValue(sourceAddress.Value, out var progression) ||
                progression == null)
            {
                return;
            }

            if (!progression.CapValue.HasValue || capValue < progression.CapValue.Value)
            {
                progression.CapValue = capValue;
                progression.CapInstructionAddress = instructionAddress;

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"        Семантика persistent counter: [0x{sourceAddress.Value:X4}] ограничен максимумом 0x{capValue:X2}");
                }
            }
        }

        private static void RefreshPendingPersistentCounterStoredValue(
            PathAnalysisResult result,
            ushort memAddr,
            byte value,
            uint instructionAddress)
        {
            if (result?.PendingPersistentCounterProgressions == null ||
                !result.PendingPersistentCounterProgressions.TryGetValue(memAddr, out var progression) ||
                progression == null)
            {
                return;
            }

            progression.CurrentValue = value;
            if (progression.CapValue.HasValue && value == progression.CapValue.Value && progression.CapInstructionAddress == 0)
                progression.CapInstructionAddress = instructionAddress;
        }

        private static void TryFinalizePersistentBattleCountProgression(
            PathAnalysisResult result,
            RegisterTracker registerTracker,
            string sourceRegisterName,
            ushort targetAddress,
            byte value,
            uint instructionAddress,
            bool debugMode)
        {
            if (result?.PendingPersistentCounterProgressions == null ||
                registerTracker == null ||
                targetAddress != BATTLE_MONSTER_COUNT_ADDRESS)
            {
                return;
            }

            ushort? sourceAddress = registerTracker.GetSourceAddress(sourceRegisterName);
            if (!sourceAddress.HasValue ||
                !result.PendingPersistentCounterProgressions.TryGetValue(sourceAddress.Value, out var pending) ||
                pending == null ||
                pending.Delta == 0)
            {
                return;
            }

            var finalized = pending.Clone();
            finalized.CurrentValue = value;
            finalized.TargetAddress = targetAddress;
            finalized.TargetKind = PersistentCounterProgressionTargetKind.BattleMonsterCount;
            finalized.TargetWriteInstructionAddress = instructionAddress;

            result.PersistentCounterProgressions ??= new List<PersistentCounterProgressionInfo>();
            AddOrReplacePersistentCounterProgression(result.PersistentCounterProgressions, finalized);
            result.HasSignificantCode = true;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        Семантика persistent counter -> BattleMonsterCount: {finalized.ToDebugString()}");
            }
        }

        private bool TryCollapseCountedShiftLoopBranch(
            X86Instruction branchInsn,
            BinaryReader br,
            uint condJumpTarget,
            uint nextAddress,
            long fileLength,
            bool debugMode,
            PathAnalysisResult result,
            uint currentAddress,
            RegisterTracker registerTracker,
            byte targetX,
            byte targetY,
            List<uint> pendingReturnAddresses,
            int callDepth,
            HashSet<uint> visitedInThisPath,
            out ControlFlowResult controlFlowResult)
        {
            controlFlowResult = null;

            string jump = branchInsn?.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            if (br?.BaseStream == null ||
                registerTracker == null ||
                result == null ||
                (jump != "JNE" && jump != "JNZ") ||
                !IsStructuralLoopBackEdge(br, condJumpTarget, currentAddress) ||
                condJumpTarget >= fileLength)
            {
                return false;
            }

            if (!TryCollectCountedShiftLoopBody(br, condJumpTarget, currentAddress, registerTracker,
                    out var operations, out string counterRegister, out uint decAddress) ||
                operations.Count == 0)
            {
                return false;
            }

            if (!TryGetByteRegisterValueRange(registerTracker, counterRegister, out int remainingMin, out int remainingMax) ||
                remainingMin < 0 ||
                remainingMax < remainingMin ||
                remainingMax > 32)
            {
                return false;
            }

            if (!TrySimulateShiftLoopRemainder(operations, registerTracker, remainingMin, remainingMax,
                    out var registerOutputs, out var memoryOutputs))
            {
                return false;
            }

            foreach (var kvp in registerOutputs)
            {
                string regName = kvp.Key;
                byte minValue = kvp.Value.Min;
                byte maxValue = kvp.Value.Max;
                string fullReg = GetFullRegisterNameForByteRegister(regName);
                if (string.IsNullOrWhiteSpace(fullReg))
                    continue;

                if (minValue == maxValue)
                {
                    registerTracker.TrackPartialRegisterOperation(
                        fullReg,
                        regName,
                        minValue,
                        currentAddress,
                        $"collapsed counted shift loop {regName}");
                }
                else
                {
                    registerTracker.SetRegisterRange(regName, minValue, maxValue, RegisterValueDistribution.Unknown);
                }
            }

            foreach (var kvp in memoryOutputs)
            {
                ushort memAddr = kvp.Key;
                byte minValue = kvp.Value.Min;
                byte maxValue = kvp.Value.Max;
                if (minValue == maxValue)
                {
                    ApplyTrackedByteWrite(memAddr, minValue, result, targetX, targetY, branchInsn, debugMode,
                        "схлопнутый counted shift loop");
                }
                else
                {
                    ApplyTrackedByteRangeWrite(memAddr, new ValueRange8(minValue, maxValue), RegisterValueDistribution.Unknown,
                        result, targetX, targetY, branchInsn, debugMode, "схлопнутый counted shift loop");
                }
            }

            string counterFullReg = GetFullRegisterNameForByteRegister(counterRegister);
            if (!string.IsNullOrWhiteSpace(counterFullReg))
            {
                registerTracker.TrackPartialRegisterOperation(
                    counterFullReg,
                    counterRegister,
                    0,
                    currentAddress,
                    $"collapsed counted shift loop {counterRegister}=0");
            }

            registerTracker.ZeroFlag = true;
            registerTracker.CarryFlag = false;
            registerTracker.FlagsKnown = true;
            registerTracker.SetFlagsMetadata(counterRegister, RegisterTracker.FlagsOriginKind.Arithmetic, currentAddress);

            foreach (var visitedAddress in visitedInThisPath
                .Where(address => address >= condJumpTarget && address <= currentAddress)
                .ToList())
            {
                visitedInThisPath.Remove(visitedAddress);
            }

            result.IsInLoop = true;
            if (result.LoopStartAddress == 0 || condJumpTarget < result.LoopStartAddress)
                result.LoopStartAddress = condJumpTarget;
            if (currentAddress > result.LoopEndAddress)
                result.LoopEndAddress = currentAddress;
            result.LoopIterationCount = Math.Max(result.LoopIterationCount, remainingMax + 1);
            result.LoopIteration = Math.Max(result.LoopIteration, remainingMax + 1);
            result.IsIndeterminateLoop = false;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Схлопнут counted shift loop 0x{condJumpTarget:X4}..0x{currentAddress:X4}: " +
                    $"{counterRegister}=0x{remainingMin:X2}..0x{remainingMax:X2} после DEC, выход 0x{nextAddress:X4}");
            }

            controlFlowResult = new ControlFlowResult
            {
                ShouldReturn = false,
                NextAddress = nextAddress,
                UpdatedPendingReturnAddresses = pendingReturnAddresses == null
                    ? new List<uint>()
                    : new List<uint>(pendingReturnAddresses),
                UpdatedCallDepth = callDepth
            };
            return true;
        }

        private bool TryCollectCountedShiftLoopBody(
            BinaryReader br,
            uint loopStartAddress,
            uint branchAddress,
            RegisterTracker registerTracker,
            out List<ShiftLoopOperation> operations,
            out string counterRegister,
            out uint decAddress)
        {
            operations = new List<ShiftLoopOperation>();
            counterRegister = null;
            decAddress = 0;

            uint address = loopStartAddress;
            while (address < branchAddress)
            {
                if (!TryDisassembleNext(br, address, out X86Instruction instruction) ||
                    instruction?.Bytes == null ||
                    instruction.Bytes.Length == 0)
                {
                    return false;
                }

                uint next = (uint)(instruction.Address + instruction.Bytes.Length);
                if (next <= address || next > branchAddress)
                    return false;

                if (next == branchAddress)
                {
                    if (!TryDecodeDecReg8(instruction, out counterRegister))
                        return false;

                    decAddress = (uint)instruction.Address;
                    return operations.Any(op => op.Kind != ShiftLoopOperationKind.SetCarry);
                }

                if (TryDecodeCarrySeedOperation(instruction, out bool carryValue))
                {
                    operations.Add(new ShiftLoopOperation
                    {
                        Kind = ShiftLoopOperationKind.SetCarry,
                        CarryValue = carryValue
                    });
                }
                else if (TryDecodeShiftRotateByteByOne(instruction, registerTracker, out var operation))
                {
                    operations.Add(operation);
                }
                else
                {
                    return false;
                }

                address = next;
            }

            return false;
        }

        private static bool TryDecodeCarrySeedOperation(X86Instruction instruction, out bool carryValue)
        {
            carryValue = false;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length != 1)
                return false;

            if (bytes[0] == 0xF8)
            {
                carryValue = false;
                return true;
            }

            if (bytes[0] == 0xF9)
            {
                carryValue = true;
                return true;
            }

            return false;
        }

        private bool TryDecodeShiftRotateByteByOne(
            X86Instruction instruction,
            RegisterTracker registerTracker,
            out ShiftLoopOperation operation)
        {
            operation = null;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 2 || bytes[0] != 0xD0)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte groupOperation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            if (!IsSupportedShiftRotateByteOperation(groupOperation))
                return false;

            operation = new ShiftLoopOperation
            {
                Kind = MapShiftRotateOperationKind(groupOperation)
            };

            if (mode == 0x03)
            {
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                if (rm >= regNames8.Length)
                    return false;

                operation.TargetKind = ShiftLoopTargetKind.Register;
                operation.RegisterName = regNames8[rm];
                return true;
            }

            if (!TryDecode16BitEffectiveAddress(bytes, registerTracker, out ushort memAddr, out _, out _))
                return false;

            operation.TargetKind = ShiftLoopTargetKind.Memory;
            operation.MemoryAddress = memAddr;
            return true;
        }

        private static bool TryDecodeDecReg8(X86Instruction instruction, out string registerName)
        {
            registerName = null;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 2 || bytes[0] != 0xFE)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte operation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            if (mode != 0x03 || operation != 0x01 || rm >= regNames8.Length)
                return false;

            registerName = regNames8[rm];
            return true;
        }

        private static bool TryGetByteRegisterValueRange(RegisterTracker registerTracker, string registerName,
            out int min, out int max)
        {
            min = 0;
            max = 0;
            if (registerTracker == null || string.IsNullOrWhiteSpace(registerName))
                return false;

            if (registerTracker.TryGetByteRegisterValue(registerName, out byte exactValue))
            {
                min = max = exactValue;
                return true;
            }

            if (registerTracker.TryGetRegisterRange(registerName, out var range) && range != null)
            {
                min = range.Min;
                max = range.Max;
                return true;
            }

            return false;
        }

        private bool TrySimulateShiftLoopRemainder(
            List<ShiftLoopOperation> operations,
            RegisterTracker registerTracker,
            int remainingMin,
            int remainingMax,
            out Dictionary<string, ValueRange8> registerOutputs,
            out Dictionary<ushort, ValueRange8> memoryOutputs)
        {
            registerOutputs = new Dictionary<string, ValueRange8>(StringComparer.OrdinalIgnoreCase);
            memoryOutputs = new Dictionary<ushort, ValueRange8>();

            if (!TryCreateInitialShiftLoopStates(operations, registerTracker, out var initialStates))
                return false;

            var registerValues = new Dictionary<string, List<byte>>(StringComparer.OrdinalIgnoreCase);
            var memoryValues = new Dictionary<ushort, List<byte>>();

            for (int remaining = remainingMin; remaining <= remainingMax; remaining++)
            {
                foreach (var initialState in initialStates)
                {
                    var states = new List<ShiftLoopSimulationState> { initialState.Clone() };
                    for (int iteration = 0; iteration < remaining; iteration++)
                    {
                        foreach (var operation in operations)
                        {
                            states = ApplyShiftLoopOperation(states, operation);
                            if (states.Count == 0 || states.Count > 4096)
                                return false;
                        }
                    }

                    foreach (var state in states)
                    {
                        foreach (var key in state.Registers.Keys.ToList())
                        {
                            if (!registerValues.TryGetValue(key, out var values))
                            {
                                values = new List<byte>();
                                registerValues[key] = values;
                            }
                            values.Add(state.Registers[key]);
                        }

                        foreach (var key in state.Memory.Keys.ToList())
                        {
                            if (!memoryValues.TryGetValue(key, out var values))
                            {
                                values = new List<byte>();
                                memoryValues[key] = values;
                            }
                            values.Add(state.Memory[key]);
                        }
                    }
                }
            }

            foreach (var kvp in registerValues)
                registerOutputs[kvp.Key] = new ValueRange8(kvp.Value.Min(), kvp.Value.Max());

            foreach (var kvp in memoryValues)
                memoryOutputs[kvp.Key] = new ValueRange8(kvp.Value.Min(), kvp.Value.Max());

            return registerOutputs.Count > 0 || memoryOutputs.Count > 0;
        }

        private bool TryCreateInitialShiftLoopStates(
            List<ShiftLoopOperation> operations,
            RegisterTracker registerTracker,
            out List<ShiftLoopSimulationState> states)
        {
            states = new List<ShiftLoopSimulationState>
            {
                new ShiftLoopSimulationState
                {
                    CarryKnown = registerTracker.FlagsKnown,
                    Carry = registerTracker.FlagsKnown && registerTracker.CarryFlag
                }
            };

            foreach (var operation in operations)
            {
                if (operation.Kind == ShiftLoopOperationKind.SetCarry)
                    continue;

                if (operation.TargetKind == ShiftLoopTargetKind.Register)
                {
                    if (states.All(state => state.Registers.ContainsKey(operation.RegisterName)))
                        continue;

                    if (!TryGetShiftLoopRegisterValues(registerTracker, operation.RegisterName, out var values))
                        return false;

                    states = ExpandShiftLoopStates(states, operation.RegisterName, values);
                }
                else
                {
                    if (states.All(state => state.Memory.ContainsKey(operation.MemoryAddress)))
                        continue;

                    if (!TryGetShiftLoopMemoryValues(operation.MemoryAddress, out var values))
                        return false;

                    states = ExpandShiftLoopStates(states, operation.MemoryAddress, values);
                }

                if (states.Count == 0 || states.Count > 4096)
                    return false;
            }

            return true;
        }

        private bool TryGetShiftLoopRegisterValues(RegisterTracker registerTracker, string registerName, out List<byte> values)
        {
            values = new List<byte>();
            if (registerTracker.TryGetByteRegisterValue(registerName, out byte exactValue))
            {
                values.Add(exactValue);
                return true;
            }

            if (registerTracker.TryGetRegisterRange(registerName, out var range) && range != null)
            {
                for (int value = range.Min; value <= range.Max; value++)
                    values.Add((byte)value);
                return values.Count > 0;
            }

            return false;
        }

        private bool TryGetShiftLoopMemoryValues(ushort memoryAddress, out List<byte> values)
        {
            values = new List<byte>();
            if (_emulatedMemory8.TryGetValue(memoryAddress, out byte exactValue))
            {
                values.Add(exactValue);
                return true;
            }

            if (TryGetEmulatedMemory8Range(memoryAddress, out var range, out _) && range != null)
            {
                for (int value = range.Min; value <= range.Max; value++)
                    values.Add((byte)value);
                return values.Count > 0;
            }

            return false;
        }

        private static List<ShiftLoopSimulationState> ExpandShiftLoopStates(
            List<ShiftLoopSimulationState> states,
            string registerName,
            List<byte> values)
        {
            var expanded = new List<ShiftLoopSimulationState>();
            foreach (var state in states)
            {
                foreach (byte value in values)
                {
                    var clone = state.Clone();
                    clone.Registers[registerName] = value;
                    expanded.Add(clone);
                }
            }

            return expanded;
        }

        private static List<ShiftLoopSimulationState> ExpandShiftLoopStates(
            List<ShiftLoopSimulationState> states,
            ushort memoryAddress,
            List<byte> values)
        {
            var expanded = new List<ShiftLoopSimulationState>();
            foreach (var state in states)
            {
                foreach (byte value in values)
                {
                    var clone = state.Clone();
                    clone.Memory[memoryAddress] = value;
                    expanded.Add(clone);
                }
            }

            return expanded;
        }

        private static List<ShiftLoopSimulationState> ApplyShiftLoopOperation(
            List<ShiftLoopSimulationState> states,
            ShiftLoopOperation operation)
        {
            var result = new List<ShiftLoopSimulationState>();
            foreach (var state in states)
            {
                if (operation.Kind == ShiftLoopOperationKind.SetCarry)
                {
                    var clone = state.Clone();
                    clone.CarryKnown = true;
                    clone.Carry = operation.CarryValue;
                    result.Add(clone);
                    continue;
                }

                byte currentValue = operation.TargetKind == ShiftLoopTargetKind.Register
                    ? state.Registers[operation.RegisterName]
                    : state.Memory[operation.MemoryAddress];

                var carryInputs = (operation.Kind == ShiftLoopOperationKind.Rcl ||
                                   operation.Kind == ShiftLoopOperationKind.Rcr) &&
                                  !state.CarryKnown
                    ? new[] { false, true }
                    : new[] { state.CarryKnown && state.Carry };

                foreach (bool carryInput in carryInputs)
                {
                    var clone = state.Clone();
                    byte operationCode = operation.Kind switch
                    {
                        ShiftLoopOperationKind.Rcl => (byte)0x02,
                        ShiftLoopOperationKind.Rcr => (byte)0x03,
                        ShiftLoopOperationKind.Shl => (byte)0x04,
                        ShiftLoopOperationKind.Shr => (byte)0x05,
                        _ => (byte)0x04
                    };

                    byte nextValue = TransformShiftRotateByte(currentValue, operationCode, carryInput, out bool carryOut);
                    if (operation.TargetKind == ShiftLoopTargetKind.Register)
                        clone.Registers[operation.RegisterName] = nextValue;
                    else
                        clone.Memory[operation.MemoryAddress] = nextValue;

                    clone.CarryKnown = true;
                    clone.Carry = carryOut;
                    result.Add(clone);
                }
            }

            return result;
        }

        private bool TryCollapseFiniteBitCountLoopBranch(
            X86Instruction branchInsn,
            BinaryReader br,
            uint condJumpTarget,
            uint nextAddress,
            long fileLength,
            bool debugMode,
            PathAnalysisResult result,
            uint currentAddress,
            RegisterTracker registerTracker,
            byte targetX,
            byte targetY,
            List<uint> pendingReturnAddresses,
            int callDepth,
            HashSet<uint> visitedInThisPath,
            out ControlFlowResult controlFlowResult)
        {
            controlFlowResult = null;

            if (branchInsn?.Bytes == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                registerTracker == null ||
                result == null ||
                currentAddress < 2 ||
                condJumpTarget <= nextAddress ||
                condJumpTarget >= fileLength ||
                !IsCarryClearJump(branchInsn.Mnemonic))
            {
                return false;
            }

            uint shrAddress = currentAddress - 2;
            byte[] shrBytes = ReadBytesAt(br, shrAddress, 2);
            if (!TryDecodeShrReg8ByOne(shrBytes, out string shiftedRegister))
                return false;

            if (!TryDisassembleNext(br, nextAddress, out X86Instruction incrementMemoryInsn) ||
                incrementMemoryInsn == null ||
                (uint)incrementMemoryInsn.Address != nextAddress ||
                (uint)(incrementMemoryInsn.Address + incrementMemoryInsn.Bytes.Length) != condJumpTarget ||
                !TryDecodeIncMemory8Direct(incrementMemoryInsn, out ushort countMemoryAddress))
            {
                return false;
            }

            if (!TryDisassembleNext(br, condJumpTarget, out X86Instruction incrementCounterInsn) ||
                incrementCounterInsn == null ||
                !TryDecodeIncReg8(incrementCounterInsn, out string counterRegister))
            {
                return false;
            }

            uint compareAddress = (uint)(incrementCounterInsn.Address + incrementCounterInsn.Bytes.Length);
            if (!TryDisassembleNext(br, compareAddress, out X86Instruction compareInsn) ||
                compareInsn == null ||
                !TryDecodeCmpReg8Imm8(compareInsn, counterRegister, out byte loopLimit))
            {
                return false;
            }

            uint backJumpAddress = (uint)(compareInsn.Address + compareInsn.Bytes.Length);
            if (!TryDisassembleNext(br, backJumpAddress, out X86Instruction backJumpInsn) ||
                backJumpInsn == null ||
                !IsCarrySetJump(backJumpInsn.Mnemonic) ||
                !IsConditionalJump(backJumpInsn, out uint loopStartAddress) ||
                loopStartAddress > shrAddress ||
                !IsSideEffectFreeBitCountLoopPrefix(br, loopStartAddress, shrAddress))
            {
                return false;
            }

            uint loopExitAddress = (uint)(backJumpInsn.Address + backJumpInsn.Bytes.Length);
            if (loopExitAddress <= backJumpAddress || loopExitAddress > fileLength)
                return false;

            if (loopLimit == 0 ||
                !registerTracker.TryGetByteRegisterValue(counterRegister, out byte counterValue) ||
                counterValue >= loopLimit)
            {
                return false;
            }

            int remainingIterations = loopLimit - counterValue;
            if (remainingIterations <= 0 || remainingIterations > 8)
                return false;

            if (!TryGetTrackedMemory8Range(countMemoryAddress, out ValueRange8 currentCountRange,
                    out RegisterValueDistribution countDistribution))
            {
                return false;
            }

            int maxCount = Math.Min(0xFF, currentCountRange.Max + remainingIterations);
            var finalCountRange = new ValueRange8(currentCountRange.Min, (byte)maxCount);

            ApplyTrackedByteRangeWrite(
                countMemoryAddress,
                finalCountRange,
                countDistribution,
                result,
                targetX,
                targetY,
                branchInsn,
                debugMode,
                "схлопнутый finite bit-count loop");

            FinalizeShiftedByteRegisterAfterBitCountLoop(
                registerTracker,
                shiftedRegister,
                remainingIterations,
                currentAddress,
                debugMode);

            string fullCounterRegister = GetFullRegisterNameForByteRegister(counterRegister);
            if (!string.IsNullOrWhiteSpace(fullCounterRegister))
            {
                registerTracker.TrackPartialRegisterOperation(
                    fullCounterRegister,
                    counterRegister,
                    loopLimit,
                    backJumpAddress,
                    $"collapsed bit-count loop {counterRegister}=0x{loopLimit:X2}");
            }

            SetArithmeticFlagsForSub8(
                registerTracker,
                loopLimit,
                loopLimit,
                0,
                counterRegister,
                RegisterTracker.FlagsOriginKind.CompareImmediate,
                (uint)compareInsn.Address);
            registerTracker.LastCompareImmediate = loopLimit;
            registerTracker.LastComparedMemoryAddress = null;

            if (visitedInThisPath != null)
            {
                foreach (var visitedAddress in visitedInThisPath
                    .Where(address => address >= loopStartAddress && address <= backJumpAddress)
                    .ToList())
                {
                    visitedInThisPath.Remove(visitedAddress);
                }
            }

            result.IsInLoop = true;
            if (result.LoopStartAddress == 0 || loopStartAddress < result.LoopStartAddress)
                result.LoopStartAddress = loopStartAddress;
            if (backJumpAddress > result.LoopEndAddress)
                result.LoopEndAddress = backJumpAddress;
            if (loopLimit > result.LoopIteration)
                result.LoopIteration = loopLimit;
            if (loopLimit > result.LoopIterationCount)
                result.LoopIterationCount = loopLimit;
            result.IsIndeterminateLoop = false;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Схлопнут finite bit-count loop 0x{loopStartAddress:X4}..0x{backJumpAddress:X4}: " +
                    $"{counterRegister}=0x{counterValue:X2}->0x{loopLimit:X2}, " +
                    $"[0x{countMemoryAddress:X4}]={currentCountRange.Min:X2}..{currentCountRange.Max:X2} -> " +
                    $"{finalCountRange.Min:X2}..{finalCountRange.Max:X2}");
            }

            controlFlowResult = new ControlFlowResult
            {
                ShouldReturn = false,
                NextAddress = loopExitAddress,
                UpdatedPendingReturnAddresses = pendingReturnAddresses == null
                    ? new List<uint>()
                    : new List<uint>(pendingReturnAddresses),
                UpdatedCallDepth = callDepth
            };
            return true;
        }

        private bool TryGetTrackedMemory8Range(ushort address, out ValueRange8 range,
            out RegisterValueDistribution distribution)
        {
            distribution = RegisterValueDistribution.Unknown;

            if (_emulatedMemory8.TryGetValue(address, out byte exactValue))
            {
                range = new ValueRange8(exactValue, exactValue);
                return true;
            }

            return TryGetEmulatedMemory8Range(address, out range, out distribution);
        }

        private void FinalizeShiftedByteRegisterAfterBitCountLoop(RegisterTracker registerTracker,
            string registerName, int remainingIterations, uint instructionAddress, bool debugMode)
        {
            if (registerTracker == null || string.IsNullOrWhiteSpace(registerName))
                return;

            int additionalShifts = remainingIterations - 1;
            if (additionalShifts <= 0)
                return;

            string fullRegister = GetFullRegisterNameForByteRegister(registerName);
            if (string.IsNullOrWhiteSpace(fullRegister))
                return;

            if (registerTracker.TryGetByteRegisterValue(registerName, out byte exactValue))
            {
                byte finalValue = (byte)(exactValue >> additionalShifts);
                registerTracker.TrackPartialRegisterOperation(
                    fullRegister,
                    registerName,
                    finalValue,
                    instructionAddress,
                    $"collapsed bit-count loop final SHR {registerName}");
                return;
            }

            if (registerTracker.TryGetRegisterRange(registerName, out ValueRange8 range) && range != null)
            {
                registerTracker.TryGetRegisterDistribution(registerName, out var distribution);
                for (int i = 0; i < additionalShifts; i++)
                    distribution = GetShiftedRangeDistribution(distribution, 5);

                int finalMin = range.Min >> additionalShifts;
                int finalMax = range.Max >> additionalShifts;
                registerTracker.SetRegisterRange(registerName, (byte)finalMin, (byte)finalMax, distribution);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"        Финализировали {registerName} после bit-count loop: " +
                        $"{range.Min}-{range.Max} >> {additionalShifts} -> {finalMin}-{finalMax}");
                }
            }
        }

        private bool IsSideEffectFreeBitCountLoopPrefix(BinaryReader br, uint loopStartAddress, uint shrAddress)
        {
            if (loopStartAddress == shrAddress)
                return true;

            if (loopStartAddress > shrAddress)
                return false;

            uint address = loopStartAddress;
            while (address < shrAddress)
            {
                if (!TryDisassembleNext(br, address, out X86Instruction instruction) ||
                    instruction?.Bytes == null ||
                    instruction.Bytes.Length == 0)
                {
                    return false;
                }

                string mnemonic = instruction.Mnemonic?.ToUpperInvariant() ?? string.Empty;
                if (mnemonic != "CLC" && mnemonic != "NOP")
                    return false;

                uint nextAddress = (uint)(instruction.Address + instruction.Bytes.Length);
                if (nextAddress <= address || nextAddress > shrAddress)
                    return false;

                address = nextAddress;
            }

            return address == shrAddress;
        }

        private static bool IsCarryClearJump(string mnemonic)
        {
            string jump = mnemonic?.ToUpperInvariant() ?? string.Empty;
            return jump == "JAE" || jump == "JNB" || jump == "JNC";
        }

        private static bool IsCarrySetJump(string mnemonic)
        {
            string jump = mnemonic?.ToUpperInvariant() ?? string.Empty;
            return jump == "JB" || jump == "JC" || jump == "JNAE";
        }

        private static bool TryDecodeShrReg8ByOne(byte[] bytes, out string registerName)
        {
            registerName = null;
            if (bytes == null || bytes.Length < 2 || bytes[0] != 0xD0)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte operation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            if (mode != 0x03 || operation != 0x05 || rm >= regNames8.Length)
                return false;

            registerName = regNames8[rm];
            return true;
        }

        private static bool TryDecodeIncMemory8Direct(X86Instruction instruction, out ushort memoryAddress)
        {
            memoryAddress = 0;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0xFE)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte operation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            if (mode != 0x00 || operation != 0x00 || rm != 0x06)
                return false;

            memoryAddress = BitConverter.ToUInt16(bytes, 2);
            return true;
        }

        private static bool TryDecodeIncReg8(X86Instruction instruction, out string registerName)
        {
            registerName = null;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 2 || bytes[0] != 0xFE)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte operation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            if (mode != 0x03 || operation != 0x00 || rm >= regNames8.Length)
                return false;

            registerName = regNames8[rm];
            return true;
        }

        private static bool TryDecodeCmpReg8Imm8(X86Instruction instruction, string expectedRegister, out byte immediate)
        {
            immediate = 0;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 3 || bytes[0] != 0x80)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte operation = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            if (mode != 0x03 || operation != 0x07 || rm >= regNames8.Length)
                return false;

            if (!string.Equals(regNames8[rm], expectedRegister, StringComparison.OrdinalIgnoreCase))
                return false;

            immediate = bytes[2];
            return true;
        }

        private bool TryCollapsePartyMemberScanExitBranch(
            X86Instruction branchInsn,
            BinaryReader br,
            uint condJumpTarget,
            uint nextAddress,
            long fileLength,
            bool debugMode,
            PathAnalysisResult result,
            uint currentAddress,
            RegisterTracker registerTracker,
            byte targetX,
            byte targetY,
            List<uint> pendingReturnAddresses,
            int callDepth,
            HashSet<uint> visitedInThisPath,
            out ControlFlowResult controlFlowResult)
        {
            controlFlowResult = null;

            if (branchInsn?.Bytes == null ||
                br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                registerTracker == null ||
                result == null ||
                !IsPendingPartyMemberScanBackEdge(registerTracker) ||
                !IsCarryClearJump(branchInsn.Mnemonic) ||
                condJumpTarget <= currentAddress ||
                condJumpTarget >= fileLength ||
                !registerTracker.LastFlagsInstructionAddress.HasValue)
            {
                return false;
            }

            uint compareAddress = registerTracker.LastFlagsInstructionAddress.Value;
            string counterRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (!TryDisassembleNext(br, compareAddress, out X86Instruction compareInsn) ||
                compareInsn == null ||
                (uint)(compareInsn.Address + compareInsn.Bytes.Length) != currentAddress ||
                string.IsNullOrWhiteSpace(counterRegister))
            {
                return false;
            }

            if (!registerTracker.TryGetByteRegisterValue(counterRegister, out byte currentCounterValue) ||
                currentCounterValue == 0 ||
                currentCounterValue > PARTY_MEMBER_COUNT)
            {
                return false;
            }

            bool hasCounterMemoryAddress = TryFindPartyScanCounterMemoryBeforeCompare(
                br,
                compareAddress,
                counterRegister,
                out ushort counterMemoryAddress);

            if (!TryDisassembleNext(br, nextAddress, out X86Instruction backJumpInsn) ||
                backJumpInsn == null ||
                (uint)(backJumpInsn.Address + backJumpInsn.Bytes.Length) != condJumpTarget ||
                !TryGetDirectUnconditionalJumpTarget(backJumpInsn, out uint loopBodyStartAddress) ||
                loopBodyStartAddress >= currentAddress)
            {
                return false;
            }

            MarkPartyMemberScanLoop(result, loopBodyStartAddress, debugMode, currentAddress);

            byte finalCounterMin = currentCounterValue;
            byte finalCounterMax = PARTY_MEMBER_COUNT;
            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(counterRegister);
            registerTracker.SetRegisterRange(counterRegister, finalCounterMin, finalCounterMax, RegisterValueDistribution.Unknown);

            byte? finalStoredMin = null;
            byte? finalStoredMax = null;
            if (hasCounterMemoryAddress)
            {
                finalStoredMin = (byte)Math.Max(0, currentCounterValue - 1);
                finalStoredMax = PARTY_MEMBER_COUNT - 1;
                ApplyTrackedByteRangeWrite(
                    counterMemoryAddress,
                    new ValueRange8(finalStoredMin.Value, finalStoredMax.Value),
                    RegisterValueDistribution.Unknown,
                    result,
                    targetX,
                    targetY,
                    branchInsn,
                    debugMode,
                    "схлопнутый party member scan loop");
            }

            registerTracker.ZeroFlag = true;
            registerTracker.CarryFlag = false;
            registerTracker.SignFlag = false;
            registerTracker.OverflowFlag = false;
            registerTracker.FlagsKnown = true;
            registerTracker.SetFlagsMetadata(counterRegister, RegisterTracker.FlagsOriginKind.CompareMemory, compareAddress);
            registerTracker.LastCompareImmediate = null;
            registerTracker.LastComparedMemoryAddress = PARTY_COUNT_ADDRESS;

            if (PARTY_MEMBER_COUNT > result.LoopIterationCount)
                result.LoopIterationCount = PARTY_MEMBER_COUNT;
            if (PARTY_MEMBER_COUNT > result.LoopIteration)
                result.LoopIteration = PARTY_MEMBER_COUNT;
            result.IsIndeterminateLoop = false;

            if (visitedInThisPath != null)
            {
                foreach (var visitedAddress in visitedInThisPath
                    .Where(address => address >= loopBodyStartAddress && address <= currentAddress)
                    .ToList())
                {
                    visitedInThisPath.Remove(visitedAddress);
                }
            }

            if (debugMode)
            {
                string counterStorageText = hasCounterMemoryAddress
                    ? $", [0x{counterMemoryAddress:X4}]=0x{finalStoredMin.Value:X2}..0x{finalStoredMax.Value:X2}"
                    : ", счётчик только в регистре";
                AnalysisDebug.WriteLine(
                    $"      Схлопнут party member scan loop 0x{loopBodyStartAddress:X4}..0x{currentAddress:X4}: " +
                    $"{counterRegister}=0x{currentCounterValue:X2}->0x{finalCounterMin:X2}..0x{finalCounterMax:X2}" +
                    $"{counterStorageText}, выход 0x{condJumpTarget:X4}");
            }

            controlFlowResult = new ControlFlowResult
            {
                ShouldReturn = false,
                NextAddress = condJumpTarget,
                UpdatedPendingReturnAddresses = pendingReturnAddresses == null
                    ? new List<uint>()
                    : new List<uint>(pendingReturnAddresses),
                UpdatedCallDepth = callDepth
            };
            return true;
        }

        private bool TryFindPartyScanCounterMemoryBeforeCompare(BinaryReader br, uint compareAddress,
            string counterRegister, out ushort counterMemoryAddress)
        {
            counterMemoryAddress = 0;
            if (compareAddress < 8 ||
                string.IsNullOrWhiteSpace(counterRegister) ||
                !TryGetHighByteRegisterForLowByteRegister(counterRegister, out string highRegister))
                return false;

            uint loadAddress = compareAddress - 8;
            if (!TryDisassembleNext(br, loadAddress, out X86Instruction loadCounterInsn) ||
                loadCounterInsn == null ||
                !TryDecodeMovReg8FromMemoryDirect(loadCounterInsn, counterRegister, out counterMemoryAddress))
            {
                return false;
            }

            uint zeroHighAddress = (uint)(loadCounterInsn.Address + loadCounterInsn.Bytes.Length);
            if (!TryDisassembleNext(br, zeroHighAddress, out X86Instruction zeroHighInsn) ||
                zeroHighInsn == null ||
                !TryDecodeMovReg8Immediate(zeroHighInsn, highRegister, 0))
            {
                return false;
            }

            uint incrementAddress = (uint)(zeroHighInsn.Address + zeroHighInsn.Bytes.Length);
            if (!TryDisassembleNext(br, incrementAddress, out X86Instruction incrementInsn) ||
                incrementInsn == null ||
                !TryDecodeIncReg8(incrementInsn, out string incrementRegister) ||
                !string.Equals(incrementRegister, counterRegister, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (uint)(incrementInsn.Address + incrementInsn.Bytes.Length) == compareAddress;
        }

        private static bool TryGetHighByteRegisterForLowByteRegister(string lowRegister, out string highRegister)
        {
            highRegister = lowRegister?.ToUpperInvariant() switch
            {
                "AL" => "AH",
                "CL" => "CH",
                "DL" => "DH",
                "BL" => "BH",
                _ => null
            };

            return highRegister != null;
        }

        private bool TryGetDirectUnconditionalJumpTarget(X86Instruction instruction, out uint target)
        {
            target = 0;
            string mnemonic = instruction?.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            if (mnemonic != "JMP" && mnemonic != "JMPF" && mnemonic != "JMPL" &&
                mnemonic != "JMPE" && mnemonic != "JMPI")
            {
                return false;
            }

            target = GetInstructionTargetAddress(instruction, 0xFFFF);
            return target != 0;
        }

        private static bool TryDecodeMovReg8FromMemoryDirect(X86Instruction instruction,
            string expectedRegister, out ushort memoryAddress)
        {
            memoryAddress = 0;
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0x8A)
                return false;

            byte modRm = bytes[1];
            byte mode = (byte)((modRm >> 6) & 0x03);
            byte reg = (byte)((modRm >> 3) & 0x07);
            byte rm = (byte)(modRm & 0x07);
            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            if (mode != 0x00 || rm != 0x06 || reg >= regNames8.Length)
                return false;

            if (!string.Equals(regNames8[reg], expectedRegister, StringComparison.OrdinalIgnoreCase))
                return false;

            memoryAddress = BitConverter.ToUInt16(bytes, 2);
            return true;
        }

        private static bool TryDecodeMovReg8Immediate(X86Instruction instruction, string expectedRegister, byte expectedValue)
        {
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 2 || bytes[0] < 0xB0 || bytes[0] > 0xB7)
                return false;

            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
            int regIndex = bytes[0] - 0xB0;
            return regIndex < regNames8.Length &&
                   string.Equals(regNames8[regIndex], expectedRegister, StringComparison.OrdinalIgnoreCase) &&
                   bytes[1] == expectedValue;
        }

        private bool TryAdvanceFiniteImmediateCounterLoop(
            string mnemonic,
            RegisterTracker registerTracker,
            PathAnalysisResult result,
            uint jumpAddress,
            uint loopBodyStartAddress,
            HashSet<uint> visitedInThisPath,
            Dictionary<uint, byte> finiteLoopProgressByJumpAddress,
            bool debugMode)
        {
            if (registerTracker == null ||
                result == null ||
                visitedInThisPath == null ||
                finiteLoopProgressByJumpAddress == null ||
                registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate ||
                !registerTracker.LastCompareImmediate.HasValue)
            {
                return false;
            }

            string jump = (mnemonic ?? string.Empty).ToUpperInvariant();
            if (jump != "JB" && jump != "JC" && jump != "JNAE")
                return false;

            string counterRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(counterRegister) ||
                !registerTracker.TryGetByteRegisterValue(counterRegister, out byte counterValue))
            {
                return false;
            }

            byte loopLimit = registerTracker.LastCompareImmediate.Value;
            if (loopLimit == 0 || counterValue >= loopLimit)
                return false;

            if (finiteLoopProgressByJumpAddress.TryGetValue(jumpAddress, out byte previousCounterValue) &&
                counterValue <= previousCounterValue)
            {
                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"      Конечный счётчиковый цикл не продвинулся: {counterRegister}=0x{counterValue:X2}, предыдущее=0x{previousCounterValue:X2}");
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
                    $"      Конечный счётчиковый цикл продолжается: {counterRegister}=0x{counterValue:X2}, limit=0x{loopLimit:X2}, повторно заходим в тело 0x{loopBodyStartAddress:X4}");
            }

            return true;
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

            bool hasObservedSequentialBattleProgress = HasObservedSequentialBattleProgress(result, registerTracker);

            bool comparesAgainstImmediateBattleLimit =
                registerTracker.LastFlagsOrigin == RegisterTracker.FlagsOriginKind.CompareImmediate &&
                registerTracker.LastCompareImmediate.HasValue &&
                hasObservedSequentialBattleProgress;

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

        private bool TrySkipRangedBattleCounterLoopBackEdge(
            RegisterTracker registerTracker,
            PathAnalysisResult result,
            uint jumpAddress,
            uint loopBodyStartAddress,
            bool debugMode)
        {
            if (registerTracker == null ||
                result == null ||
                result.IsBattleMonsterCountIndeterminate ||
                result.BattleMonsterCountRange == null ||
                result.BattleMonsterCountRange.IsExact)
            {
                return false;
            }

            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareMemory ||
                registerTracker.LastComparedMemoryAddress != BATTLE_MONSTER_COUNT_ADDRESS)
            {
                return false;
            }

            if (!HasObservedSequentialBattleProgress(result, registerTracker))
                return false;

            string counterRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (!string.Equals(counterRegister, "BL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.TryGetByteRegisterValue(counterRegister, out byte counterValue))
            {
                return false;
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

            PromoteRangedSequentialBattleFill(result, counterValue, result.BattleMonsterCountRange, debugMode);
            MaterializeBattleLoopExitCounterRange(registerTracker, result.BattleMonsterCountRange, debugMode);

            result.IsIndeterminateLoop = false;

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Диапазонный цикл по [0x{BATTLE_MONSTER_COUNT_ADDRESS:X4}] представлен шаблоном повторов {result.BattleMonsterCountRange.Min}-{result.BattleMonsterCountRange.Max}; продолжаем за пределами цикла");
            }

            return true;
        }

        private void MaterializeBattleLoopExitCounterRange(
            RegisterTracker registerTracker,
            ValueRange8 countRange,
            bool debugMode)
        {
            if (registerTracker == null || countRange == null)
                return;

            RegisterValueDistribution distribution =
                _emulatedMemory8RangeDistributions.TryGetValue(BATTLE_MONSTER_COUNT_ADDRESS, out var knownDistribution)
                    ? knownDistribution
                    : RegisterValueDistribution.Unknown;

            registerTracker.InvalidateRegister("BX");
            registerTracker.ClearRegisterRange("BL");
            registerTracker.ClearRegisterRange("BH");
            registerTracker.SetRegisterRange("BX", countRange.Min, countRange.Max, distribution);
            registerTracker.SetRegisterRange("BL", countRange.Min, countRange.Max, distribution);

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"        Диапазонный выход из battle-loop: BX/BL = {countRange.Min}-{countRange.Max}");
            }
        }

        private static void PromoteRangedSequentialBattleFill(
            PathAnalysisResult result,
            byte observedCounterValue,
            ValueRange8 countRange,
            bool debugMode)
        {
            if (result == null ||
                countRange == null ||
                countRange.IsExact ||
                observedCounterValue == 0 ||
                countRange.Max == 0)
            {
                return;
            }

            int observedCount = Math.Min((int)observedCounterValue, BATTLE_MONSTER_TABLE_SLOT_COUNT);
            int maxCount = Math.Min((int)countRange.Max, BATTLE_MONSTER_TABLE_SLOT_COUNT);
            int minCount = Math.Min((int)countRange.Min, maxCount);
            if (observedCount <= 0 || maxCount <= 0)
                return;

            byte? fillVal1 = null;
            byte? fillVal2 = null;

            for (int slot = 0; slot < observedCount; slot++)
            {
                if (!result.BattleMonsterEntries.TryGetValue(slot, out var entry) ||
                    entry.val1 == 0 ||
                    entry.val2 == 0 ||
                    !IsDirectSequentialBattleFillObservation(result, slot))
                {
                    return;
                }

                if (!fillVal1.HasValue)
                {
                    fillVal1 = entry.val1;
                    fillVal2 = entry.val2;
                }
                else if (fillVal1.Value != entry.val1 || fillVal2.Value != entry.val2)
                {
                    return;
                }
            }

            if (!fillVal1.HasValue || !fillVal2.HasValue)
                return;

            for (int slot = 0; slot < maxCount; slot++)
            {
                UpsertBattleMonsterEntry(
                    result,
                    slot,
                    fillVal1.Value,
                    fillVal2.Value,
                    slot >= minCount);
            }

            result.HasSignificantCode = true;

            if (debugMode)
            {
                string fixedText = minCount == 1 ? "1 обязательный слот" : $"{minCount} обязательных слотов";
                string optionalText = maxCount > minCount
                    ? $", {maxCount - minCount} условных"
                    : string.Empty;
                AnalysisDebug.WriteLine(
                    $"        Развёрнут диапазонный battle-fill: val1=0x{fillVal1.Value:X2}, val2=0x{fillVal2.Value:X2}, {fixedText}{optionalText}");
            }
        }

        private static bool IsDirectSequentialBattleFillObservation(PathAnalysisResult result, int slot)
        {
            if (result?.PartialBattleInfo == null)
                return true;

            var slotObservations = result.PartialBattleInfo
                .Where(info => info.BxIndex == slot &&
                               (info.DestAddr == BATTLE_MONSTER_FIRST_TABLE_ADDRESS ||
                                info.DestAddr == BATTLE_MONSTER_SECOND_TABLE_ADDRESS))
                .ToList();

            if (slotObservations.Count == 0)
                return true;

            return slotObservations.All(info =>
                !info.IsFromTable &&
                info.SourceIndexBehavior != BattleSourceIndexBehavior.AdvancesWithLoop &&
                info.SourceIndexBehavior != BattleSourceIndexBehavior.ExternalRandom);
        }

        private static bool HasObservedSequentialBattleProgress(PathAnalysisResult result, RegisterTracker registerTracker)
        {
            if (result == null || registerTracker == null)
                return false;

            string counterRegister = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (!string.Equals(counterRegister, "BL", StringComparison.OrdinalIgnoreCase) ||
                !registerTracker.TryGetByteRegisterValue(counterRegister, out byte counterValue) ||
                counterValue == 0)
            {
                return false;
            }

            int expectedLastBx = counterValue - 1;

            if (result.BattleMonsterEntries.ContainsKey(expectedLastBx))
                return true;

            return result.PartialBattleInfo.Any(info => info.BxIndex == expectedLastBx);
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
                "JS", "JNS",
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

            if (_instructionCache.TryGetValue(address, out var cachedInstruction))
            {
                instruction = cachedInstruction;
                return true;
            }

            if (_invalidInstructionCache.Contains(address))
                return false;

            int bytesToRead = (int)Math.Min(16, fileLength - address);
            byte[] chunk = ReadBytesAt(br, address, bytesToRead);

            var instructions = CapstoneX86Disassembly.Disassemble(chunk, address);
            if (instructions != null && instructions.Length > 0)
            {
                instruction = instructions[0];
                _instructionCache[address] = instruction;
                return true;
            }

            _invalidInstructionCache.Add(address);
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
            return OvrOverlayAddressReader.ExtractText(br, _config, textAddress);
        }

        private bool TryMapOverlayAddressToFileOffset(BinaryReader br, ushort memAddr, out long fileOffset)
        {
            return OvrOverlayAddressReader.TryMapOverlayAddressToFileOffset(br, _config, memAddr, out fileOffset);
        }

        /// <summary>
        /// Декодирует текст из байтов
        /// </summary>
        private string DecodeText(byte[] bytes)
        {
            return OvrOverlayAddressReader.DecodeText(bytes);
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
            int callDepth, List<uint> pendingReturnAddresses, ref int textOrderCounter)
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
                else if (opcode >= 0x38 && opcode <= 0x3D) // CMP
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
                        // Точные обработчики ниже заново выставят флаги; если обработчик не смог
                        // смоделировать источник, старые флаги не должны протекать в следующий Jcc.
                        if (!(opcode == 0x04))
                            invalidateFlags = true;
                    }
                }

                if (invalidateFlags)
                    InvalidateFlags(registerTracker);
            }

            TrackPartyArithmeticInstruction(insn, registerTracker, result, callDepth, pendingReturnAddresses);

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
                            CompareMemoryAddress = registerTracker.LastComparedMemoryAddress,
                            ComparedPartyField = registerTracker.LastComparedPartyField?.Clone(),
                            IsInputChoiceBranch = false,
                            ProbabilityNumerator = 1,
                            ProbabilityDenominator = 1,
                            CallDepth = callDepth,
                            PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            EmulatedMemory8 = splitMemory,
                            EmulatedMemory8Ranges = CloneRangeDictionary(_emulatedMemory8Ranges),
                            EmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(_emulatedMemory8RangeDistributions),
                            EmulatedPartyPointers = _emulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            EmulatedPartyPointerBytes = _emulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                            PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(result.PendingPersistentCounterProgressions),
                            BranchStateValueConstraints = new Dictionary<ushort, StateValueConstraintInfo>
                            {
                                [memAddr] = new StateValueConstraintInfo
                                {
                                    ExactValues = new HashSet<byte> { (byte)candidate }
                                }
                            },
                            BranchLocallyMaterializedStateValueConstraintAddresses = new HashSet<ushort> { memAddr }
                        });
                    }

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Развилка по диапазону AL для записи в [0x{memAddr:X4}]: {splitRange.Min:X2}..{splitRange.Max:X2}");

                    result.IsTerminated = true;
                    result.HasSignificantCode = true;
                    return true;
                }
                else if (TrySplitSemanticRangeByteRegisterWrite(
                             memAddr,
                             "AL",
                             registerTracker,
                             result,
                             address,
                             callDepth,
                             pendingReturnAddresses,
                             debugMode))
                {
                    return true;
                }
                else if (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteSemanticOnly))
                {
                    _emulatedMemory8.Remove(memAddr);
                    _emulatedMemory8Ranges.Remove(memAddr);
                    _emulatedMemory8RangeDistributions.Remove(memAddr);
                    _emulatedPartyPointers.Remove(memAddr);
                    _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));
                    ApplyTrackedPartyPointerByteWrite(memAddr, alPointerByteSemanticOnly);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        MOV [moffs8], AL: перенесли байтовую семантику указателя члена партии в [0x{memAddr:X4}] без точного значения");
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && alRange != null && !alRange.IsExact)
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
                        registerTracker.TryGetRegisterDistribution("AL", out var alDistribution);
                        ApplyTrackedByteRangeWrite(memAddr, alRange, alDistribution, result, targetX, targetY,
                            insn, debugMode, "MOV [moffs8], AL");
                        MirrorRangeRegisterToMemorySource(registerTracker, "AL", memAddr, alRange, alDistribution,
                            address, "MOV [moffs8], AL");
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
                        registerTracker.TryGetRegisterDistribution("AL", out var alDistribution);
                        ApplyTrackedByteRangeWrite(memAddr, alRange, alDistribution, result, targetX, targetY,
                            insn, debugMode, "MOV [moffs8], AL");
                        MirrorRangeRegisterToMemorySource(registerTracker, "AL", memAddr, alRange, alDistribution,
                            address, "MOV [moffs8], AL");
                    }
                    else if (TryAddSyntheticRangeLootText(br, result, memAddr, alRange, targetX, targetY,
                        address, callDepth, debugMode, ref textOrderCounter))
                    {
                    }
                    else
                    {
                        registerTracker.TryGetRegisterDistribution("AL", out var alDistribution);
                        ApplyTrackedByteRangeWrite(memAddr, alRange, alDistribution, result, targetX, targetY,
                            insn, debugMode, "MOV [moffs8], AL");
                        MirrorRangeRegisterToMemorySource(registerTracker, "AL", memAddr, alRange, alDistribution,
                            address, "MOV [moffs8], AL");
                    }
                }
                else if (TryTrackBattleMonsterStrengthDeltaFromRegisterWrite(
                             registerTracker,
                             "AL",
                             memAddr,
                             result,
                             address,
                             debugMode))
                {
                    _emulatedMemory8.Remove(memAddr);
                    _emulatedMemory8Ranges.Remove(memAddr);
                    _emulatedMemory8RangeDistributions.Remove(memAddr);
                }
                else if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    bool trackedBattleStrengthSet = TryTrackBattleMonsterStrengthDeltaFromRegisterWrite(
                        registerTracker,
                        "AL",
                        memAddr,
                        result,
                        address,
                        debugMode);
                    if (!trackedBattleStrengthSet)
                    {
                        trackedBattleStrengthSet = TryTrackBattleMonsterStrengthSet(
                            br,
                            memAddr,
                            alValue,
                            result,
                            targetX,
                            targetY,
                            address,
                            debugMode);
                    }
                    ApplyTrackedByteWrite(memAddr, alValue, result, targetX, targetY, insn, debugMode, "MOV [moffs8], AL",
                        trackBattleMonsterStrengthDelta: !trackedBattleStrengthSet);
                    TryFinalizePersistentBattleCountProgression(result, registerTracker, "AL", memAddr, alValue, address, debugMode);
                    if (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteExact))
                        ApplyTrackedPartyPointerByteWrite(memAddr, alPointerByteExact);
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
                        PartyFieldReference rawPartyFieldRef = null;
                        bool hasRawPartyFieldRef = TryResolvePartyMemberFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out rawPartyFieldRef);
                        PartyFieldReference partyFieldRef = hasRawPartyFieldRef && IsTrackablePartyField(rawPartyFieldRef)
                            ? rawPartyFieldRef
                            : null;
                        bool hasPartyFieldRef = partyFieldRef != null;

                        if (hasPartyFieldRef)
                        {
                            RegisterPartyFieldWrite(
                                result,
                                partyFieldRef,
                                registerTracker,
                                callDepth,
                                pendingReturnAddresses,
                                address,
                                debugMode,
                                immValue);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Распознана прямая запись поля персонажа {partyFieldRef.Field} = 0x{immValue:X2} через {eaText}");
                        }
                        else if (hasRawPartyFieldRef)
                        {
                            RememberPartyByteWrite(registerTracker, rawPartyFieldRef, PartyEffectOperation.Write, address, writtenValue: immValue);
                        }
                        else if (hasExactMemAddr)
                        {
                            bool trackedBattleStrengthSet = TryTrackBattleMonsterStrengthSet(
                                br,
                                memAddr,
                                immValue,
                                result,
                                targetX,
                                targetY,
                                address,
                                debugMode);
                            ApplyTrackedByteWrite(
                                memAddr,
                                immValue,
                                result,
                                targetX,
                                targetY,
                                insn,
                                debugMode,
                                $"MOV byte ptr {eaText}, 0x{immValue:X2}",
                                trackBattleMonsterTableWrite: true,
                                trackBattleMonsterStrengthDelta: !trackedBattleStrengthSet);
                        }
                        else if (TryApplyTrackedByteWriteToRangedBattleTable(
                                     instructionBytes,
                                     registerTracker,
                                     immValue,
                                     result,
                                     debugMode,
                                     $"MOV byte ptr {eaText}, 0x{immValue:X2}"))
                        {
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
                            {
                                registerTracker.SetPartyFieldValue(dstReg, copiedPartyField);
                                if (registerTracker.TryGetDynamicValueFormula(regNames8[reg], out var copiedFormula))
                                    registerTracker.SetDynamicValueFormula(dstReg, copiedFormula);
                            }

                            if (registerTracker.TryGetPartyPointerByteValue(regNames8[reg], out var copiedPointerByte))
                                registerTracker.SetPartyPointerByteValue(dstReg, copiedPointerByte);

                            if (registerTracker.TryGetMemoryByteDeltaSource(regNames8[reg], out ushort memoryDeltaSourceAddr, out int memoryDelta) &&
                                memoryDelta != 0)
                            {
                                registerTracker.SetMemoryByteDeltaSource(dstReg, memoryDeltaSourceAddr, memoryDelta);
                            }

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {regNames8[reg]} = 0x{srcValue:X2}");
                        }
                    }
                    else if (reg < regNames8.Length && rm < regNames8.Length)
                    {
                        string dstReg = regNames8[rm];
                        string srcReg = regNames8[reg];
                        registerTracker.TryGetPartyFieldValue(srcReg, out var semanticField88);
                        registerTracker.TryGetPartyPointerByteValue(srcReg, out var semanticPointerByte88);
                        bool hasMemoryDeltaSource88 = registerTracker.TryGetMemoryByteDeltaSource(
                            srcReg,
                            out ushort memoryDeltaSourceAddr88,
                            out int memoryDelta88) &&
                            memoryDelta88 != 0;

                        if (semanticField88 != null || semanticPointerByte88 != null || hasMemoryDeltaSource88)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);

                            if (semanticField88 != null)
                            {
                                registerTracker.SetPartyFieldValue(dstReg, semanticField88);
                                if (registerTracker.TryGetDynamicValueFormula(regNames8[reg], out var copiedFormula))
                                    registerTracker.SetDynamicValueFormula(dstReg, copiedFormula);
                                else
                                    registerTracker.SetDynamicValueFormula(dstReg, CreateDynamicFormulaFromPartyField(semanticField88, address));
                            }

                            if (semanticPointerByte88 != null)
                                registerTracker.SetPartyPointerByteValue(dstReg, semanticPointerByte88);

                            if (hasMemoryDeltaSource88)
                                registerTracker.SetMemoryByteDeltaSource(dstReg, memoryDeltaSourceAddr88, memoryDelta88);

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

                    PartyFieldReference rawPartyFieldRef = null;
                    bool hasRawPartyFieldRef = TryResolvePartyMemberFieldAccess(
                        instructionBytes,
                        registerTracker,
                        hasExactMemAddr ? memAddr : (ushort?)null,
                        out rawPartyFieldRef);
                    PartyFieldReference partyFieldRef = hasRawPartyFieldRef && IsTrackablePartyField(rawPartyFieldRef)
                        ? rawPartyFieldRef
                        : null;
                    bool hasPartyFieldRef = partyFieldRef != null;
                    PartyFieldReference sourcePartyFieldValue = null;
                    if (reg < regNames8.Length)
                        registerTracker.TryGetPartyFieldValue(regNames8[reg], out sourcePartyFieldValue);
                    if (TryGetReg8ValueFromModRmRegField(modRm, registerTracker, out byte regValue, out string regName))
                    {
                        if (hasPartyFieldRef)
                        {
                            RegisterPartyFieldWrite(
                                result,
                                partyFieldRef,
                                registerTracker,
                                callDepth,
                                pendingReturnAddresses,
                                address,
                                debugMode,
                                regValue,
                                sourceFieldValue: sourcePartyFieldValue,
                                sourceRegisterName: regName);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Распознана запись поля персонажа {partyFieldRef.Field} из {regName} через {eaText} (сырую эмулируемую память не обновляем)");
                        }
                        else if (hasRawPartyFieldRef)
                        {
                            RememberPartyByteWrite(registerTracker, rawPartyFieldRef, PartyEffectOperation.Write, address, regName, regValue);
                        }
                        else if (hasExactMemAddr)
                        {
                            bool trackedBattleStrengthSet = TryTrackBattleMonsterStrengthDeltaFromRegisterWrite(
                                registerTracker,
                                regName,
                                memAddr,
                                result,
                                address,
                                debugMode);
                            if (!trackedBattleStrengthSet)
                            {
                                trackedBattleStrengthSet = TryTrackBattleMonsterStrengthSet(
                                    br,
                                    memAddr,
                                    regValue,
                                    result,
                                    targetX,
                                    targetY,
                                    address,
                                    debugMode);
                            }
                            ApplyTrackedByteWrite(memAddr, regValue, result, targetX, targetY, insn, debugMode, $"MOV byte ptr {eaText}, {regName}",
                                trackBattleMonsterStrengthDelta: !trackedBattleStrengthSet);
                            TryFinalizePersistentBattleCountProgression(result, registerTracker, regName, memAddr, regValue, address, debugMode);

                            if (registerTracker.TryGetPartyPointerByteValue(regName, out var pointerByte))
                                ApplyTrackedPartyPointerByteWrite(memAddr, pointerByte);
                        }
                        else if (TryApplyTrackedByteWriteToRangedBattleTable(
                                     instructionBytes,
                                     registerTracker,
                                     regValue,
                                     result,
                                     debugMode,
                                     $"MOV byte ptr {eaText}, {regName}"))
                        {
                        }
                    }
                    else if (hasPartyFieldRef)
                    {
                        RegisterPartyFieldWrite(
                            result,
                            partyFieldRef,
                            registerTracker,
                            callDepth,
                            pendingReturnAddresses,
                            address,
                            debugMode,
                            sourceFieldValue: sourcePartyFieldValue,
                            sourceRegisterName: regNames8[reg]);

                        if (debugMode)
                        {
                            string detail = hasExactMemAddr
                                ? "без точного значения источника"
                                : "без точного абсолютного адреса";
                            AnalysisDebug.WriteLine($"        Распознана запись поля персонажа {partyFieldRef.Field} через {eaText} {detail}");
                        }
                    }
                    else if (hasRawPartyFieldRef && reg < regNames8.Length)
                    {
                        RememberPartyByteWrite(registerTracker, rawPartyFieldRef, PartyEffectOperation.Write, address, regNames8[reg]);
                    }
                    else if (hasExactMemAddr &&
                             reg < regNames8.Length &&
                             TryTrackBattleMonsterStrengthDeltaFromRegisterWrite(
                                 registerTracker,
                                 regNames8[reg],
                                 memAddr,
                                 result,
                                 address,
                                 debugMode))
                    {
                        _emulatedMemory8.Remove(memAddr);
                        _emulatedMemory8Ranges.Remove(memAddr);
                        _emulatedMemory8RangeDistributions.Remove(memAddr);
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
                result?.AdjustedMemoryAddresses.Add(memAddr);
                TrackBattleMonsterStrengthAdjustment(result, memAddr, delta, address, debugMode);
                if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte currentValue))
                {
                    byte newValue = unchecked((byte)(currentValue + delta));
                    string operation = delta > 0 ? "INC" : "DEC";
                    ApplyTrackedByteWrite(memAddr, newValue, result, targetX, targetY, insn, debugMode, $"{operation} byte ptr [disp16]",
                        trackBattleMonsterStrengthDelta: false);
                    TrackPersistentCounterAdjustment(result, memAddr, currentValue, newValue, delta, address, debugMode);
                }
                else if (TryShiftTrackedSemanticByteRange(memAddr, delta, result, debugMode))
                {
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
                        $"MOV AL, [0x{memAddr:X4}]",
                        sourceIndexProviderAddr: memAddr
                    );

                    if (hasPointerByteSemanticA0)
                        registerTracker.SetPartyPointerByteValue("AL", pointerByteSemanticA0);

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Загрузили AL из {(_emulatedMemory8.ContainsKey(memAddr) ? "эмулируемой памяти" : "файла")} [0x{memAddr:X4}] = 0x{value:X2}");
                }
                else if (TryGetEmulatedMemory8Range(memAddr, out var memRange, out var memDistribution))
                {
                    RegisterMemoryRead(result, memAddr, _persistentEventStateAddresses.Contains(memAddr));
                    registerTracker.SetRegisterRangeWithSource("AL", memRange.Min, memRange.Max, memDistribution,
                        memAddr, address, $"MOV AL, [0x{memAddr:X4}]", sourceIndexProviderAddr: memAddr);

                    if (debugMode)
                    {
                        string valueText = memRange.IsExact
                            ? $"0x{memRange.Min:X2}"
                            : $"0x{memRange.Min:X2}-0x{memRange.Max:X2}";
                        AnalysisDebug.WriteLine($"        Загрузили AL из диапазона эмулируемой памяти [0x{memAddr:X4}] = {valueText}");
                    }
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
                else if (ShouldTrackUnknownExternalStateGuardAddress(memAddr))
                {
                    registerTracker.ClearConcreteByteRegisterValueKeepSemantic("AL");
                    registerTracker.ClearPartyFieldValue("AL");
                    registerTracker.ClearPartyFieldValue("AX");
                    registerTracker.ClearPartyPointerByteValue("AL");
                    registerTracker.ClearPartyMemberBase("AX");
                    registerTracker.SetRegisterRangeWithSource(
                        "AL",
                        0,
                        byte.MaxValue,
                        RegisterValueDistribution.Unknown,
                        memAddr,
                        address,
                        $"MOV AL, [0x{memAddr:X4}]",
                        sourceIndexProviderAddr: memAddr);
                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Загрузили AL из неизвестного внешнего состояния [0x{memAddr:X4}] = 0x00..0xFF");
                }
                else
                {
                    registerTracker.ClearConcreteByteRegisterValueKeepSemantic("AL");
                    registerTracker.ClearPartyFieldValue("AL");
                    registerTracker.ClearPartyFieldValue("AX");
                    registerTracker.ClearPartyPointerByteValue("AL");
                    registerTracker.ClearPartyMemberBase("AX");
                    if (debugMode)
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
                            PropagateExternalCallInfluenceOnRegisterCopy(registerTracker, regNames8[rm], dstReg, debugMode);

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
                            PropagateExternalCallInfluenceOnRegisterCopy(registerTracker, srcReg, dstReg, debugMode);

                            if (debugMode)
                                AnalysisDebug.WriteLine($"        Копирование диапазона {dstReg} <- {srcReg} = {copiedRange8A.Min:X2}..{copiedRange8A.Max:X2}");
                        }
                        else if (semanticField8A != null || semanticPointerByte8A != null)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(dstReg);

                            if (semanticField8A != null)
                            {
                                registerTracker.SetPartyFieldValue(dstReg, semanticField8A);
                                if (registerTracker.TryGetDynamicValueFormula(srcReg, out var copiedFormula))
                                    registerTracker.SetDynamicValueFormula(dstReg, copiedFormula);
                                else
                                    registerTracker.SetDynamicValueFormula(dstReg, CreateDynamicFormulaFromPartyField(semanticField8A, address));
                            }

                            if (semanticPointerByte8A != null)
                                registerTracker.SetPartyPointerByteValue(dstReg, semanticPointerByte8A);

                            PropagateExternalCallInfluenceOnRegisterCopy(registerTracker, srcReg, dstReg, debugMode);

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
                    bool hasPartyFieldRef =
                        TryResolvePartyMemberFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out partyFieldRef) &&
                        IsTrackablePartyField(partyFieldRef);
                    PartyPointerByteReference pointerByteSemantic8A = null;
                    bool hasPointerByteSemantic8A = hasExactMemAddr
                        ? TryResolveTrackedPartyPointerByte(memAddr, out pointerByteSemantic8A)
                        : TryResolveDynamicPartyPointerByteFromMemoryOperand(instructionBytes, registerTracker, out pointerByteSemantic8A);

                    if (hasExactMemAddr && TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte value))
                    {
                        if (reg < regNames8.Length)
                        {
                            string regName = regNames8[reg];
                            string fullReg = GetFullRegisterNameForByteRegister(regName);
                            ushort indexedTableBx = 0;
                            bool isIndexedTableLoad = hasExactMemAddr &&
                                TryGetOverlayTableSourceFromBxIndexedOperand(instructionBytes, registerTracker, memAddr, out indexedTableBx);
                            bool indexedTableUsesExternalIndex = isIndexedTableLoad &&
                                (registerTracker.HasPendingExternalCallResult("BX") || registerTracker.IsRegisterExternallyDerived("BX"));
                            ushort? sourceIndexProviderAddr = isIndexedTableLoad
                                ? registerTracker.GetSourceIndexProviderAddress("BX") ?? registerTracker.GetSourceAddress("BX")
                                : memAddr;

                            if (!string.IsNullOrEmpty(fullReg))
                            {
                                registerTracker.SetByteRegisterValueWithSource(
                                    fullReg,
                                    regName,
                                    value,
                                    memAddr,
                                    address,
                                    $"MOV {regName}, byte ptr {eaText}",
                                    isIndexedTableLoad,
                                    isIndexedTableLoad ? indexedTableBx : (ushort)0,
                                    sourceIndexExternallyDerived: indexedTableUsesExternalIndex,
                                    sourceIndexProviderAddr: sourceIndexProviderAddr
                                );

                                if (hasPartyFieldRef)
                                {
                                    registerTracker.SetPartyFieldValue(regName, partyFieldRef);
                                    registerTracker.SetDynamicValueFormula(
                                        regName,
                                        CreateDynamicFormulaFromPartyField(partyFieldRef, address, value));
                                    registerTracker.ClearPartyPointerByteValue(regName);
                                    RegisterPartyFieldRead(
                                        result,
                                        partyFieldRef,
                                        registerTracker,
                                        callDepth,
                                        pendingReturnAddresses,
                                        address,
                                        value);
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
                    else if (hasExactMemAddr && TryGetEmulatedMemory8Range(memAddr, out var memRange8A, out var memDistribution8A))
                    {
                        RegisterMemoryRead(result, memAddr, _persistentEventStateAddresses.Contains(memAddr));
                        if (reg < regNames8.Length)
                        {
                            string regName = regNames8[reg];
                            registerTracker.SetRegisterRangeWithSource(regName, memRange8A.Min, memRange8A.Max, memDistribution8A,
                                memAddr, address, $"MOV {regName}, byte ptr {eaText}", sourceIndexProviderAddr: memAddr);

                            if (debugMode)
                            {
                                string valueText = memRange8A.IsExact
                                    ? $"0x{memRange8A.Min:X2}"
                                    : $"0x{memRange8A.Min:X2}-0x{memRange8A.Max:X2}";
                                AnalysisDebug.WriteLine($"        Загрузили {regName} из диапазона эмулируемой памяти {eaText} -> [0x{memAddr:X4}] = {valueText}");
                            }
                        }
                    }
                    else if (hasPartyFieldRef && reg < regNames8.Length)
                    {
                        string regName = regNames8[reg];
                        registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
                        registerTracker.SetPartyFieldValue(regName, partyFieldRef);
                        registerTracker.SetDynamicValueFormula(
                            regName,
                            CreateDynamicFormulaFromPartyField(partyFieldRef, address));
                        registerTracker.ClearPartyPointerByteValue(regName);
                        RegisterPartyFieldRead(
                            result,
                            partyFieldRef,
                            registerTracker,
                            callDepth,
                            pendingReturnAddresses,
                            address);

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
                    else if (hasExactMemAddr && reg < regNames8.Length)
                    {
                        string regName = regNames8[reg];
                        string fullReg = GetFullRegisterNameForByteRegister(regName);
                        registerTracker.ClearConcreteByteRegisterValueKeepSemantic(regName);
                        registerTracker.ClearPartyFieldValue(regName);
                        registerTracker.ClearPartyPointerByteValue(regName);
                        if (!string.IsNullOrEmpty(fullReg))
                            registerTracker.ClearPartyMemberBase(fullReg);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Не удалось загрузить {regName} из {eaText} -> [0x{memAddr:X4}] - адрес вне диапазона файла и эмулируемой памяти");
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
                        string srcReg = regNames16[rm];
                        bool sourceIsFromTable = registerTracker.IsFromTable(srcReg);
                        ushort? sourceAddr = registerTracker.GetSourceAddress(srcReg);
                        if (sourceIsFromTable && sourceAddr.HasValue)
                        {
                            registerTracker.SetRegisterValueWithSource(
                                dstReg,
                                srcValue,
                                sourceAddr.Value,
                                registerTracker.GetOriginalBx(srcReg) ?? 0,
                                true,
                                address,
                                $"MOV {dstReg}, {srcReg}",
                                registerTracker.GetSourceTable(srcReg),
                                sourceIndexExternallyDerived: registerTracker.GetSourceIndexExternallyDerived(srcReg),
                                sourceIndexProviderAddr: registerTracker.GetSourceIndexProviderAddress(srcReg));
                        }
                        else
                        {
                            registerTracker.SetRegisterValue(dstReg, srcValue, address, $"MOV {dstReg}, {srcReg}");
                        }

                        PropagateExternalCallInfluenceOnRegisterCopy(registerTracker, srcReg, dstReg, debugMode);
                        if (registerTracker.TryGetPartyMemberBase(srcReg, out var copiedPartyMember))
                            registerTracker.SetPartyMemberBase(dstReg, copiedPartyMember);
                        else
                            registerTracker.ClearPartyMemberBase(dstReg);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование {dstReg} <- {srcReg} = 0x{srcValue:X4}");
                    }
                    else if (reg < regNames16.Length && rm < regNames16.Length &&
                        registerTracker.TryGetRegisterRange(regNames16[rm], out var copiedRange16))
                    {
                        string dstReg = regNames16[reg];
                        registerTracker.TryGetRegisterDistribution(regNames16[rm], out var copiedDistribution16);
                        registerTracker.SetRegisterRange(dstReg, copiedRange16.Min, copiedRange16.Max, copiedDistribution16);
                        PropagateExternalCallInfluenceOnRegisterCopy(registerTracker, regNames16[rm], dstReg, debugMode);

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Копирование диапазона {dstReg} <- {regNames16[rm]} = {copiedRange16.Min:X2}..{copiedRange16.Max:X2}");
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
                            bool isIndexedTableLoad = TryGetOverlayTableSourceFromBxIndexedOperand(
                                instructionBytes,
                                registerTracker,
                                memAddr,
                                out ushort indexedTableBx);
                            bool indexedTableUsesExternalIndex = isIndexedTableLoad &&
                                (registerTracker.HasPendingExternalCallResult("BX") || registerTracker.IsRegisterExternallyDerived("BX"));
                            ushort? sourceIndexProviderAddr = isIndexedTableLoad
                                ? registerTracker.GetSourceIndexProviderAddress("BX") ?? registerTracker.GetSourceAddress("BX")
                                : memAddr;

                            if (isIndexedTableLoad)
                            {
                                registerTracker.SetRegisterValueWithSource(
                                    regName,
                                    value,
                                    memAddr,
                                    indexedTableBx,
                                    fromTable: true,
                                    address,
                                    $"MOV {regName}, word ptr {eaText}",
                                    sourceIndexExternallyDerived: indexedTableUsesExternalIndex,
                                    sourceIndexProviderAddr: sourceIndexProviderAddr);
                            }
                            else
                            {
                                registerTracker.SetRegisterValue(regName, value, address, $"MOV {regName}, word ptr {eaText}");
                            }

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

                bool hasAccumulatorPartyField =
                    registerTracker.TryGetPartyFieldValue("AL", out var accumulatorPartyField) &&
                    accumulatorPartyField != null;

                if (hasAccumulatorPartyField)
                {
                    TrackPartyFieldRegisterImmediateTransform(
                        result,
                        registerTracker,
                        "AL",
                        accumulatorOperation,
                        instructionBytes[1],
                        address,
                        debugMode);
                }

                TrackAccumulatorImmediateLogicalOperation(
                    registerTracker,
                    accumulatorOperation,
                    instructionBytes[1],
                    address,
                    updateAccumulatorValue: !hasAccumulatorPartyField);
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
                            rangeDistribution = GetShiftedRangeDistribution(rangeDistribution, operation);
                            break;
                        case 5: // SHR r/m8,1
                            newMin = oldRange.Min >> 1;
                            newMax = oldRange.Max >> 1;
                            rangeDistribution = GetShiftedRangeDistribution(rangeDistribution, operation);
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
                else if (mode != 0x03 &&
                         TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText))
                {
                    ApplyShiftRotateMemory8Operation(
                        memAddr,
                        string.IsNullOrWhiteSpace(eaText) ? $"[0x{memAddr:X4}]" : eaText,
                        operation,
                        insn,
                        br,
                        registerTracker,
                        result,
                        targetX,
                        targetY,
                        debugMode);
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xD1)
            {
                byte modRm = instructionBytes[1];
                byte mode = (byte)((modRm >> 6) & 0x03);
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);
                string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (mode == 0x03 &&
                    rm < regNames16.Length &&
                    registerTracker.TryGetRegisterValue(regNames16[rm], out ushort oldValue))
                {
                    string regName = regNames16[rm];
                    ushort newValue = oldValue;
                    string operationName = null;

                    switch (operation)
                    {
                        case 4: // SHL/SAL r/m16,1
                            operationName = "SHL";
                            newValue = (ushort)(oldValue << 1);
                            registerTracker.CarryFlag = (oldValue & 0x8000) != 0;
                            registerTracker.ZeroFlag = newValue == 0;
                            registerTracker.SignFlag = (newValue & 0x8000) != 0;
                            registerTracker.OverflowFlag = ((oldValue ^ newValue) & 0x8000) != 0;
                            registerTracker.FlagsKnown = true;
                            break;
                        case 5: // SHR r/m16,1
                            operationName = "SHR";
                            newValue = (ushort)(oldValue >> 1);
                            registerTracker.CarryFlag = (oldValue & 0x0001) != 0;
                            registerTracker.ZeroFlag = newValue == 0;
                            registerTracker.SignFlag = false;
                            registerTracker.OverflowFlag = (oldValue & 0x8000) != 0;
                            registerTracker.FlagsKnown = true;
                            break;
                    }

                    if (!string.IsNullOrEmpty(operationName))
                    {
                        registerTracker.SetRegisterValue(regName, newValue, address, $"{operationName} {regName}, 1");

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {operationName} {regName}, 1: 0x{oldValue:X4} -> 0x{newValue:X4}");
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
                    registerTracker.TryGetRegisterDistribution("AL", out var alDistribution);
                    registerTracker.TryGetDynamicValueFormula("AL", out var formulaBeforeArithmetic);
                    int delta = isAdd
                        ? immediateValue + carryIn
                        : -(immediateValue + carryIn);
                    if (isAdd)
                    {
                        int newMin = Math.Min(0xFF, alRange.Min + immediateValue + carryIn);
                        int newMax = Math.Min(0xFF, alRange.Max + immediateValue + carryIn);
                        registerTracker.SetRegisterRange("AL", (byte)newMin, (byte)newMax,
                            GetAdditiveRangeDistribution(alDistribution, immediateValue + carryIn));
                        if (formulaBeforeArithmetic != null)
                            registerTracker.SetDynamicValueFormula("AL", formulaBeforeArithmetic.WithAdditiveOffset(delta, address));

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {mnemonic} AL, 0x{immediateValue:X2}: диапазон {alRange.Min}-{alRange.Max} -> {newMin}-{newMax}");
                    }
                    else
                    {
                        int newMin = Math.Max(0, alRange.Min - immediateValue - carryIn);
                        int newMax = Math.Max(0, alRange.Max - immediateValue - carryIn);
                        registerTracker.SetRegisterRange("AL", (byte)newMin, (byte)newMax,
                            GetAdditiveRangeDistribution(alDistribution, -(immediateValue + carryIn)));
                        if (formulaBeforeArithmetic != null)
                            registerTracker.SetDynamicValueFormula("AL", formulaBeforeArithmetic.WithAdditiveOffset(delta, address));

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        {mnemonic} AL, 0x{immediateValue:X2}: диапазон {alRange.Min}-{alRange.Max} -> {newMin}-{newMax}");
                    }
                }
            }

            // ADD/ADC r8, r/m8. В AREAC4 усиление боя записано как
            // MOV AL,delta; CLC; ADC AL,[0x3CA6]; MOV [0x3CA6],AL.
            if (instructionBytes.Length >= 2 &&
                (instructionBytes[0] == 0x02 || instructionBytes[0] == 0x12))
            {
                byte opcode = instructionBytes[0];
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                bool usesCarry = opcode == 0x12;
                bool carryKnown = !usesCarry || registerTracker.FlagsKnown;
                int carryIn = usesCarry && registerTracker.FlagsKnown && registerTracker.CarryFlag ? 1 : 0;

                if (mod != 0x03 &&
                    carryKnown &&
                    reg < regNames8.Length &&
                    TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText))
                {
                    string destReg = regNames8[reg];
                    string fullReg = GetFullRegisterNameForByteRegister(destReg);
                    string opText = usesCarry ? "ADC" : "ADD";

                    if (!string.IsNullOrEmpty(fullReg) &&
                        registerTracker.TryGetByteRegisterValue(destReg, out byte destValue))
                    {
                        int delta = destValue + carryIn;
                        bool hasExactMemoryValue = TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue);

                        if (hasExactMemoryValue)
                        {
                            byte newValue = unchecked((byte)(memValue + delta));
                            registerTracker.TrackPartialRegisterOperation(
                                fullReg,
                                destReg,
                                newValue,
                                address,
                                $"{opText} {destReg}, byte ptr {eaText}");
                            SetArithmeticFlagsForAdd8(registerTracker, memValue, (byte)delta, newValue);

                            if (memAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS && delta != 0)
                                registerTracker.SetMemoryByteDeltaSource(destReg, memAddr, delta);
                        }
                        else if (memAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS && delta != 0)
                        {
                            registerTracker.ClearConcreteByteRegisterValueKeepSemantic(destReg);
                            registerTracker.SetMemoryByteDeltaSource(destReg, memAddr, delta);
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(destReg, RegisterTracker.FlagsOriginKind.Arithmetic, address);
                        }

                        if (debugMode &&
                            memAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS &&
                            delta != 0)
                        {
                            AnalysisDebug.WriteLine(
                                $"        {opText} {destReg}, byte ptr {eaText}: запомнили [0x{memAddr:X4}] {(delta > 0 ? "+" : string.Empty)}{delta}");
                        }
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x8B && instructionBytes[1] == 0xE8)
            {
                if (registerTracker.TryGetRegisterValue("AX", out ushort axValue))
                    registerTracker.SetRegisterValue("BP", axValue, address, "MOV BP, AX");
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && alRange != null)
                {
                    registerTracker.TryGetRegisterDistribution("AL", out var alDistribution);
                    registerTracker.SetRegisterRange("BP", alRange.Min, alRange.Max, alDistribution);
                }
            }

            if (instructionBytes.Length >= 4 && instructionBytes[0] == 0x81 && instructionBytes[1] == 0xE5 && instructionBytes[2] == 0xFF && instructionBytes[3] == 0x00)
            {
                if (registerTracker.TryGetRegisterValue("BP", out ushort bpValue))
                {
                    ushort maskedValue = (ushort)(bpValue & 0x00FF);
                    bool sourceIsFromTable = registerTracker.IsFromTable("BP");
                    ushort? sourceAddr = registerTracker.GetSourceAddress("BP");
                    if (sourceIsFromTable && sourceAddr.HasValue)
                    {
                        registerTracker.SetRegisterValueWithSource(
                            "BP",
                            maskedValue,
                            sourceAddr.Value,
                            registerTracker.GetOriginalBx("BP") ?? 0,
                            true,
                            address,
                            "AND BP, 0x00FF",
                            registerTracker.GetSourceTable("BP"),
                            sourceIndexExternallyDerived: registerTracker.GetSourceIndexExternallyDerived("BP"),
                            sourceIndexProviderAddr: registerTracker.GetSourceIndexProviderAddress("BP"));
                    }
                    else
                    {
                        registerTracker.SetRegisterValue("BP", maskedValue, address, "AND BP, 0x00FF");
                    }
                }
                else if (registerTracker.TryGetRegisterRange("BP", out var bpRange) && bpRange != null)
                {
                    registerTracker.TryGetRegisterDistribution("BP", out var bpDistribution);
                    byte maskedMin = (byte)(bpRange.Min & 0xFF);
                    byte maskedMax = (byte)(bpRange.Max & 0xFF);
                    bool sourceIsFromTable = registerTracker.IsFromTable("BP");
                    ushort? sourceAddr = registerTracker.GetSourceAddress("BP");
                    if (sourceIsFromTable && sourceAddr.HasValue)
                    {
                        registerTracker.SetRegisterRangeWithSource(
                            "BP",
                            maskedMin,
                            maskedMax,
                            bpDistribution,
                            sourceAddr.Value,
                            address,
                            "AND BP, 0x00FF",
                            fromTable: true,
                            originalBx: registerTracker.GetOriginalBx("BP") ?? 0,
                            sourceTable: registerTracker.GetSourceTable("BP"),
                            sourceIndexExternallyDerived: registerTracker.GetSourceIndexExternallyDerived("BP"),
                            sourceIndexProviderAddr: registerTracker.GetSourceIndexProviderAddress("BP"));
                    }
                    else
                    {
                        registerTracker.SetRegisterRange("BP", maskedMin, maskedMax, bpDistribution);
                    }
                }
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
                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                string compareRegName = reg < regNames8.Length ? regNames8[reg] : string.Empty;
                PartyFieldReference registerComparedField = null!;
                bool hasRegisterComparedField = !string.IsNullOrWhiteSpace(compareRegName) &&
                    registerTracker.TryGetPartyFieldValue(compareRegName, out registerComparedField) &&
                    registerComparedField != null;
                PartyFieldReference memoryComparedField = null!;
                bool hasMemoryComparedField = mod != 0x03 &&
                    TryResolvePartyMemberFieldAccess(
                        instructionBytes,
                        registerTracker,
                        hasExactMemAddress ? memAddr : (ushort?)null,
                        out memoryComparedField) &&
                    IsTrackablePartyField(memoryComparedField);

                bool comparesByteRegisterWithMemory =
                    mod != 0x03 &&
                    !string.IsNullOrWhiteSpace(compareRegName) &&
                    IsByteRegisterName(compareRegName);
                bool comparesBl =
                    comparesByteRegisterWithMemory &&
                    string.Equals(compareRegName, "BL", StringComparison.OrdinalIgnoreCase);

                bool comparesLowByteRegisterWithMemory =
                    comparesByteRegisterWithMemory &&
                    TryGetHighByteRegisterForLowByteRegister(compareRegName, out _);

                if (comparesLowByteRegisterWithMemory &&
                    hasExactMemAddress &&
                    memAddr == PARTY_COUNT_ADDRESS)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata(compareRegName, RegisterTracker.FlagsOriginKind.CompareMemory, address);
                    registerTracker.LastCompareImmediate = null;
                    registerTracker.LastComparedMemoryAddress = PARTY_COUNT_ADDRESS;
                    MarkPartyMemberScanLoop(result, address, debugMode);
                    handledSpecialCompare = true;
                }

                if (!handledSpecialCompare &&
                    comparesBl &&
                    hasExactMemAddress &&
                    memAddr == BATTLE_MONSTER_COUNT_ADDRESS &&
                    result?.BattleMonsterCountRange != null &&
                    !result.BattleMonsterCountRange.IsExact &&
                    !result.IsBattleMonsterCountIndeterminate)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("BL", RegisterTracker.FlagsOriginKind.CompareMemory, address);
                    registerTracker.LastCompareImmediate = null;
                    registerTracker.LastComparedMemoryAddress = BATTLE_MONSTER_COUNT_ADDRESS;
                    handledSpecialCompare = true;
                }

                if (!handledSpecialCompare &&
                    mod != 0x03 &&
                    reg < 8 &&
                    hasExactMemAddress &&
                    TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue))
                {
                    string regName = compareRegName;

                    if (TryGetExactByteRegisterOrRange(registerTracker, regName, out byte regValue))
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

                if (!handledSpecialCompare &&
                    hasRegisterComparedField &&
                    hasMemoryComparedField &&
                    PartyAlignmentSemantics.IsAlignmentField(registerComparedField.Field) &&
                    PartyAlignmentSemantics.IsAlignmentField(memoryComparedField.Field))
                {
                    PartyFieldReference leftField = instructionBytes[0] == 0x3A
                        ? registerComparedField
                        : memoryComparedField;
                    PartyFieldReference rightField = instructionBytes[0] == 0x3A
                        ? memoryComparedField
                        : registerComparedField;

                    RegisterPartyFieldToFieldCompareEffect(
                        result,
                        leftField,
                        rightField,
                        registerTracker,
                        address,
                        debugMode);
                }
            }

            // CMP AL, imm8 (opcode 3C ib)
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x3C)
            {
                byte immediateValue = instructionBytes[1];

                bool hasKnownAlForCompare = TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte alValue);
                bool hasComparedPartyField = registerTracker.TryGetPartyFieldValue("AL", out var comparedPartyField) &&
                    comparedPartyField != null;
                bool hasComparablePartyField = hasComparedPartyField &&
                    (IsComparablePartyField(comparedPartyField.Field) || IsRawTechnicalPartyField(comparedPartyField));

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
                else if (hasComparablePartyField)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }

                if (hasComparablePartyField)
                {
                    PartyFieldReference comparablePartyField = comparedPartyField!;
                    registerTracker.LastComparedPartyField = comparablePartyField.Clone();
                    RegisterPartyFieldCompareEffect(result, comparablePartyField, registerTracker, address, immediateValue, debugMode);
                }
                else
                {
                    registerTracker.LastComparedPartyField = null;
                }

                TrackPersistentCounterCapFromCompare(result, registerTracker, "AL", immediateValue, address, debugMode);
                registerTracker.LastCompareImmediate = immediateValue;
                registerTracker.LastComparedMemoryAddress = null;
            }

            // OR AL, AL / TEST AL, AL (точное выставление флагов для проверок на ноль)
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x0A && instructionBytes[1] == 0xC0)
            {
                if (TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte alValue))
                {
                    SetLogicalFlagsForByteResult(registerTracker, "AL", alValue, address, 0xFF);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _) ||
                         (registerTracker.TryGetPartyFieldValue("AL", out var alFieldForOr) && alFieldForOr != null) ||
                         (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteForOr) && alPointerByteForOr != null))
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                    registerTracker.LastTestMask = 0xFF;
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xA8)
            {
                byte immediateValue = instructionBytes[1];
                bool hasTrackedTechnicalFieldCompare = registerTracker.TryGetPartyFieldValue("AL", out var testedPartyField) &&
                    testedPartyField != null &&
                    (IsTrackedPartyField(testedPartyField.Field) || IsRawTechnicalPartyField(testedPartyField));

                if (TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte alValue))
                {
                    byte testResult = (byte)(alValue & immediateValue);
                    SetLogicalFlagsForByteResult(registerTracker, "AL", testResult, address, immediateValue);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _))
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                    registerTracker.LastTestMask = immediateValue;
                }
                else if (hasTrackedTechnicalFieldCompare)
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                    registerTracker.LastTestMask = immediateValue;
                }

                if (hasTrackedTechnicalFieldCompare)
                {
                    if (IsRawTechnicalPartyField(testedPartyField))
                    {
                        RegisterRawTechnicalFieldCompareEffect(
                            result,
                            testedPartyField,
                            registerTracker,
                            address,
                            immediateValue,
                            isBitMask: true,
                            debugMode);
                    }
                    else
                    {
                        RegisterTrackedTechnicalFieldCompareEffect(
                            result,
                            testedPartyField,
                            registerTracker,
                            address,
                            immediateValue,
                            isBitMask: true,
                            debugMode);
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x84 && instructionBytes[1] == 0xC0)
            {
                if (TryGetExactByteRegisterOrRange(registerTracker, "AL", out byte alValue))
                {
                    SetLogicalFlagsForByteResult(registerTracker, "AL", alValue, address, 0xFF);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _) ||
                         (registerTracker.TryGetPartyFieldValue("AL", out var alFieldForTest) && alFieldForTest != null) ||
                         (registerTracker.TryGetPartyPointerByteValue("AL", out var alPointerByteForTest) && alPointerByteForTest != null))
                {
                    registerTracker.FlagsKnown = false;
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.Test, address);
                    registerTracker.LastTestMask = 0xFF;
                }
            }

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0x80)
            {
                byte modRm = instructionBytes[1];
                byte operation = (byte)((modRm >> 3) & 0x07);
                byte mode = (byte)((modRm >> 6) & 0x03);
                byte regIndex = (byte)(modRm & 0x07);
                byte immediateValue = instructionBytes[2];
                if (mode != 0x03 &&
                    TryDecode16BitMemoryOperandSyntax(
                        instructionBytes,
                        out _,
                        out _,
                        out _,
                        out int decodedLengthForImmediate,
                        out _) &&
                    decodedLengthForImmediate >= 2 &&
                    decodedLengthForImmediate < instructionBytes.Length)
                {
                    immediateValue = instructionBytes[decodedLengthForImmediate];
                }

                if (mode == 0x03 && operation == 0x07)
                {
                    string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                    string regName = regIndex < regNames8.Length ? regNames8[regIndex] : null;
                    PartyFieldReference comparedPartyField = null;
                    bool hasComparablePartyField = !string.IsNullOrWhiteSpace(regName) &&
                        registerTracker.TryGetPartyFieldValue(regName, out comparedPartyField) &&
                        comparedPartyField != null &&
                        (IsComparablePartyField(comparedPartyField.Field) || IsRawTechnicalPartyField(comparedPartyField));

                    if (!string.IsNullOrWhiteSpace(regName) &&
                        TryGetExactByteRegisterOrRange(registerTracker, regName, out byte regValue))
                    {
                        byte cmpResult = (byte)(regValue - immediateValue);
                        SetArithmeticFlagsForSub8(registerTracker, regValue, immediateValue, cmpResult, regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                        registerTracker.LastComparedMemoryAddress = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetRegisterRange(regName, out var _))
                    {
                        registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                        registerTracker.LastComparedMemoryAddress = null;
                    }
                    else if (hasComparablePartyField)
                    {
                        registerTracker.FlagsKnown = false;
                        registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                        registerTracker.LastComparedMemoryAddress = null;
                    }

                    if (hasComparablePartyField)
                    {
                        PartyFieldReference comparablePartyField = comparedPartyField!;
                        registerTracker.LastComparedPartyField = comparablePartyField.Clone();
                        RegisterPartyFieldCompareEffect(result, comparablePartyField, registerTracker, address, immediateValue, debugMode);
                    }
                    else
                    {
                        registerTracker.LastComparedPartyField = null;
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
                        bool hasPartyFieldRef = TryResolvePartyMemberFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out comparedFieldRef) &&
                            IsTrackablePartyField(comparedFieldRef);

                        if (hasExactMemAddr &&
                            TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte memValue))
                        {
                            byte cmpResult = (byte)(memValue - immediateValue);
                            SetArithmeticFlagsForSub8(registerTracker, memValue, immediateValue, cmpResult, null, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        }
                        else if (hasExactMemAddr)
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        }
                        else if (hasPartyFieldRef &&
                                 comparedFieldRef != null &&
                                 (IsComparablePartyField(comparedFieldRef.Field) || IsRawTechnicalPartyField(comparedFieldRef)))
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        }

                        bool hasComparableComparedField = hasPartyFieldRef &&
                                                         comparedFieldRef != null &&
                                                         (IsComparablePartyField(comparedFieldRef.Field) || IsRawTechnicalPartyField(comparedFieldRef));
                        if (hasComparableComparedField)
                        {
                            PartyFieldReference comparableComparedField = comparedFieldRef!;
                            registerTracker.LastComparedPartyField = comparableComparedField.Clone();
                            RegisterPartyFieldCompareEffect(result, comparableComparedField, registerTracker, address, immediateValue, debugMode);
                        }
                        else
                        {
                            registerTracker.LastComparedPartyField = null;
                        }

                        registerTracker.LastCompareImmediate = immediateValue;
                        registerTracker.LastComparedMemoryAddress = hasExactMemAddr ? memAddr : null;
                    }

                    PartyEffectOperation memoryOperation = MapImmediateByteTransformOperation(operation);
                    if (memoryOperation != PartyEffectOperation.Unknown)
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText);
                        if (!hasExactMemAddr)
                            TryDecode16BitMemoryOperandSyntax(instructionBytes, out _, out _, out _, out _, out eaText);

                        PartyFieldReference partyFieldRef = null;
                        bool hasPartyFieldRef =
                            TryResolvePartyMemberFieldAccess(
                                instructionBytes,
                                registerTracker,
                                hasExactMemAddr ? memAddr : (ushort?)null,
                                out partyFieldRef) &&
                            IsTrackablePartyField(partyFieldRef);

                        if (hasPartyFieldRef &&
                            (partyFieldRef.Field == PartyFieldKind.Status ||
                             IsTrackedPartyField(partyFieldRef.Field) ||
                             IsRawTechnicalPartyField(partyFieldRef)))
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
                                : IsRawTechnicalPartyField(partyFieldRef)
                                    ? GetRawTechnicalPartyFieldRelevantMask(memoryOperation, immediateValue)
                                    : GetTrackedPartyFieldRelevantMask(partyFieldRef.Field, memoryOperation, immediateValue);

                            if (IsTrackedPartyField(partyFieldRef.Field) || IsRawTechnicalPartyField(partyFieldRef))
                                RegisterPartyFieldRead(
                                    result,
                                    partyFieldRef,
                                    registerTracker,
                                    callDepth,
                                    pendingReturnAddresses,
                                    address,
                                    currentByteValue);

                            RegisterPartyFieldWrite(
                                result,
                                partyFieldRef,
                                registerTracker,
                                callDepth,
                                pendingReturnAddresses,
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
                                    : IsRawTechnicalPartyField(partyFieldRef)
                                        ? GetRawTechnicalPartyFieldLabel(partyFieldRef)
                                        : GetPartyFieldDebugLabel(partyFieldRef.Field);
                                AnalysisDebug.WriteLine($"        Распознано изменение {fieldText} через byte ptr {eaText}, 0x{immediateValue:X2}{valueText}");
                            }
                        }
                    }

                    if ((operation == 0x00 || operation == 0x05) &&
                        TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort arithmeticMemAddr, out _, out string arithmeticEaText))
                    {
                        if (arithmeticMemAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS &&
                            TryResolveTrackedByteValue(br, arithmeticMemAddr, result, targetX, targetY, out byte currentValue))
                        {
                            int delta = operation == 0x00 ? immediateValue : -immediateValue;
                            result?.AdjustedMemoryAddresses.Add(arithmeticMemAddr);
                            TrackBattleMonsterStrengthAdjustment(result, arithmeticMemAddr, delta, address, debugMode);
                            byte newValue = unchecked((byte)(currentValue + delta));
                            string opText = operation == 0x00 ? "ADD" : "SUB";
                            ApplyTrackedByteWrite(arithmeticMemAddr, newValue, result, targetX, targetY, insn, debugMode,
                                $"{opText} byte ptr {arithmeticEaText}, 0x{immediateValue:X2}",
                                trackBattleMonsterStrengthDelta: false);
                        }
                        else if (arithmeticMemAddr == BATTLE_MONSTER_STRENGTH_ADJUSTMENT_ADDRESS)
                        {
                            int delta = operation == 0x00 ? immediateValue : -immediateValue;
                            result?.AdjustedMemoryAddresses.Add(arithmeticMemAddr);
                            TrackBattleMonsterStrengthAdjustment(result, arithmeticMemAddr, delta, address, debugMode);
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
                        bool hasTrackedTechnicalFieldCompare = !string.IsNullOrWhiteSpace(regName) &&
                            registerTracker.TryGetPartyFieldValue(regName, out comparedPartyField) &&
                            comparedPartyField != null &&
                            (IsTrackedPartyField(comparedPartyField.Field) || IsRawTechnicalPartyField(comparedPartyField));

                        if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetByteRegisterValue(regName, out byte regValue))
                        {
                            byte testResult = (byte)(regValue & immediateValue);
                            registerTracker.ZeroFlag = testResult == 0;
                            registerTracker.SignFlag = (testResult & 0x80) != 0;
                            registerTracker.CarryFlag = false;
                            registerTracker.OverflowFlag = false;
                            registerTracker.FlagsKnown = true;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                            registerTracker.LastTestMask = immediateValue;
                        }
                        else if (!string.IsNullOrWhiteSpace(regName) && registerTracker.TryGetRegisterRange(regName, out var _))
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                            registerTracker.LastTestMask = immediateValue;
                        }
                        else if (hasTrackedTechnicalFieldCompare)
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(regName, RegisterTracker.FlagsOriginKind.Test, address);
                            registerTracker.LastTestMask = immediateValue;
                        }

                        if (hasTrackedTechnicalFieldCompare)
                        {
                            if (IsRawTechnicalPartyField(comparedPartyField))
                            {
                                RegisterRawTechnicalFieldCompareEffect(
                                    result,
                                    comparedPartyField,
                                    registerTracker,
                                    address,
                                    immediateValue,
                                    isBitMask: true,
                                    debugMode);
                            }
                            else
                            {
                                RegisterTrackedTechnicalFieldCompareEffect(
                                    result,
                                    comparedPartyField,
                                    registerTracker,
                                    address,
                                    immediateValue,
                                    isBitMask: true,
                                    debugMode);
                            }
                        }
                    }
                    else
                    {
                        bool hasExactMemAddr = TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out _);
                        PartyFieldReference testedFieldRef = null;
                        bool hasPartyFieldRef = TryResolvePartyMemberFieldAccess(
                            instructionBytes,
                            registerTracker,
                            hasExactMemAddr ? memAddr : (ushort?)null,
                            out testedFieldRef) &&
                            IsTrackablePartyField(testedFieldRef);

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
                            registerTracker.LastTestMask = immediateValue;
                        }
                        else if (hasPartyFieldRef &&
                                 testedFieldRef != null &&
                                 (IsTrackedPartyField(testedFieldRef.Field) || IsRawTechnicalPartyField(testedFieldRef)))
                        {
                            registerTracker.FlagsKnown = false;
                            registerTracker.SetFlagsMetadata(null, RegisterTracker.FlagsOriginKind.Test, address);
                            registerTracker.LastTestMask = immediateValue;
                        }

                        if (hasPartyFieldRef &&
                            testedFieldRef != null &&
                            (IsTrackedPartyField(testedFieldRef.Field) || IsRawTechnicalPartyField(testedFieldRef)))
                        {
                            if (IsRawTechnicalPartyField(testedFieldRef))
                            {
                                RegisterRawTechnicalFieldCompareEffect(
                                    result,
                                    testedFieldRef,
                                    registerTracker,
                                    address,
                                    immediateValue,
                                    isBitMask: true,
                                    debugMode);
                            }
                            else
                            {
                                RegisterTrackedTechnicalFieldCompareEffect(
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
                else if (mod != 0x03 &&
                         (operation == 0x00 || operation == 0x01) &&
                         !(mod == 0x00 && rm == 0x06) &&
                         TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _, out string eaText))
                {
                    int delta = operation == 0x00 ? 1 : -1;
                    string opText = operation == 0x00 ? "INC" : "DEC";
                    result?.AdjustedMemoryAddresses.Add(memAddr);
                    TrackBattleMonsterStrengthAdjustment(result, memAddr, delta, address, debugMode);

                    if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte currentValue))
                    {
                        byte newValue = unchecked((byte)(currentValue + delta));
                        ApplyTrackedByteWrite(memAddr, newValue, result, targetX, targetY, insn, debugMode, $"{opText} byte ptr {eaText}",
                            trackBattleMonsterStrengthDelta: false);
                        TrackPersistentCounterAdjustment(result, memAddr, currentValue, newValue, delta, address, debugMode);
                    }
                    else if (TryShiftTrackedSemanticByteRange(memAddr, delta, result, debugMode))
                    {
                    }
                    else if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"        Не удалось определить текущее значение для {opText} {eaText} -> [0x{memAddr:X4}]");
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
        private void AnalyzePartialBattleRanges(BinaryReader br, PathAnalysisResult result, bool isFinalPass = false)
        {
            if (!result.HasPartialBattlePattern || result.PartialBattleInfo.Count == 0)
            {
                return;
            }

            AnalysisDebug.WriteLine($"    АНАЛИЗ ЧАСТИЧНЫХ БИТВ: найдено {result.PartialBattleInfo.Count} записей");
            AnalysisDebug.WriteLine($"    Состояние цикла: IsInLoop={result.IsInLoop}, LoopStartAddress=0x{result.LoopStartAddress:X4}, LoopIterationCount={result.LoopIterationCount}");

            var groupedByBx = result.PartialBattleInfo
                .GroupBy(p => p.BxIndex)
                .OrderBy(g => g.Key)
                .ToList();

            var promotedFullBattleIndices = PromoteSequentialTableCopiesToFullyDefinedBattles(br, result, groupedByBx, isFinalPass);
            var partialCandidateGroups = groupedByBx
                .Where(group => !promotedFullBattleIndices.Contains(group.Key))
                .ToList();

            if (partialCandidateGroups.Count == 0)
                return;

            if (TryAnalyzeUnifiedPartialBattleTemplates(br, result, partialCandidateGroups, isFinalPass))
                return;

            foreach (var group in partialCandidateGroups)
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

                if (ShouldSkipObservedPartialBattle(group))
                {
                    AnalysisDebug.WriteLine("        -> ПАРА ПОХОЖА НА ПОСЛЕДОВАТЕЛЬНОЕ КОПИРОВАНИЕ ПОЛНОЙ БИТВЫ, наблюдаемую частичную битву не создаём");
                    continue;
                }

                if (IsFullyDefinedBattleEntry(result, saveBxIndex))
                {
                    AnalysisDebug.WriteLine($"        -> БИТВА УЖЕ ПОЛНОСТЬЮ ОПРЕДЕЛЕНА ДЛЯ BX={saveBxIndex}, пропускаем частичный вариант");
                    continue;
                }

                var partialBattle = CreateObservedPartialBattle(result, saveBxIndex, entry58, entry29);

                string identityKey = partialBattle.GetIdentityKey();
                if (result.PartialBattles.Any(p => p.GetIdentityKey() == identityKey))
                {
                    AnalysisDebug.WriteLine($"        -> НАБЛЮДАЕМАЯ ЧАСТИЧНАЯ БИТВА ДЛЯ BX={saveBxIndex} УЖЕ СУЩЕСТВУЕТ, пропускаем");
                    continue;
                }

                result.PartialBattles.Add(partialBattle);
                AnalysisDebug.WriteLine(
                    $"        -> СОЗДАНА НАБЛЮДАЕМАЯ ЧАСТИЧНАЯ БИТВА: BX={saveBxIndex}, " +
                    $"val1={FormatObservedPartialBattleValue(entry58)}, val2={FormatObservedPartialBattleValue(entry29)}, " +
                    $"repeatCount={FormatPartialBattleRepeatCount(partialBattle)}");
            }
        }

        private static string FormatObservedPartialBattleValue(PartialBattleInfo info)
        {
            if (info == null)
                return "?";

            return info.HasExactValue
                ? $"0x{info.ValueMin:X2}"
                : $"0x{info.ValueMin:X2}-0x{info.ValueMax:X2}";
        }

        private PartiallyDefinedBattle CreateObservedPartialBattle(
            PathAnalysisResult result,
            int saveBxIndex,
            PartialBattleInfo entry58,
            PartialBattleInfo entry29)
        {
            var partialBattle = new PartiallyDefinedBattle
            {
                BxIndex = saveBxIndex,
                RepeatCount = GetPartialBattleRepeatCount(result),
                RepeatCountRange = GetPartialBattleRepeatCountRange(result)
            };

            if (entry58.HasExactValue && entry29.HasExactValue)
            {
                partialBattle.ExactOptions = new List<DiscreteBattleOption>
                {
                    new DiscreteBattleOption
                    {
                        Val1 = entry58.ValueMin,
                        Val2 = entry29.ValueMin
                    }
                };
            }
            else
            {
                partialBattle.RangeStart1 = entry58.ValueMin;
                partialBattle.RangeEnd1 = entry58.ValueMax;
                partialBattle.RangeStart2 = entry29.ValueMin;
                partialBattle.RangeEnd2 = entry29.ValueMax;
            }

            return partialBattle;
        }

        private sealed class PartialBattleTemplateObservation
        {
            public int SaveBxIndex { get; set; }
            public PartialBattleInfo Entry58 { get; set; } = null!;
            public PartialBattleInfo Entry29 { get; set; } = null!;
            public ushort FirstBaseAddr { get; set; }
            public ushort SecondBaseAddr { get; set; }
            public int FirstOffset { get; set; }
            public int SecondOffset { get; set; }
        }

        private PartialBattleTemplateObservation? CreatePartialBattleTemplateObservation(IGrouping<int, PartialBattleInfo> group)
        {
            if (group == null)
                return null;

            var entry58 = group.FirstOrDefault(entry => entry.DestAddr == 0x3C58);
            var entry29 = group.FirstOrDefault(entry => entry.DestAddr == 0x3C29);

            if (entry58 == null || entry29 == null)
                return null;

            if (!entry58.SourceTableBaseAddr.HasValue || !entry29.SourceTableBaseAddr.HasValue)
                return null;

            return new PartialBattleTemplateObservation
            {
                SaveBxIndex = group.Key,
                Entry58 = entry58,
                Entry29 = entry29,
                FirstBaseAddr = entry58.SourceTableBaseAddr.Value,
                SecondBaseAddr = entry29.SourceTableBaseAddr.Value,
                FirstOffset = entry58.SourceTableAddr.HasValue ? entry58.SourceTableAddr.Value - entry58.SourceTableBaseAddr.Value : 0,
                SecondOffset = entry29.SourceTableAddr.HasValue ? entry29.SourceTableAddr.Value - entry29.SourceTableBaseAddr.Value : 0
            };
        }

        private static bool IsKnownPartialBattleTemplateSource(string? sourceTable)
        {
            return string.Equals(sourceTable, "CDA9", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sourceTable, "CDB1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sourceTable, "CA7F", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sourceTable, "CA84", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAlignedSourceOffsets(PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return false;

            if (observation.FirstOffset < 0 || observation.SecondOffset < 0)
                return false;

            return observation.FirstOffset == observation.SecondOffset;
        }

        private static ushort? GetSharedSourceIndexProviderAddress(PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return null;

            ushort? firstProvider = observation.Entry58?.SourceIndexProviderAddr;
            ushort? secondProvider = observation.Entry29?.SourceIndexProviderAddr;

            if (firstProvider.HasValue && secondProvider.HasValue && firstProvider.Value != secondProvider.Value)
                return null;

            return firstProvider ?? secondProvider;
        }

        private static void ApplySourceIndexBehavior(PartialBattleTemplateObservation? observation, BattleSourceIndexBehavior behavior)
        {
            if (observation == null)
                return;

            if (observation.Entry58 != null)
                observation.Entry58.SourceIndexBehavior = behavior;

            if (observation.Entry29 != null)
                observation.Entry29.SourceIndexBehavior = behavior;
        }

        private static BattleSourceIndexBehavior InferSourceIndexBehavior(
            PathAnalysisResult result,
            PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return BattleSourceIndexBehavior.Unknown;

            if (observation.Entry58?.SourceIndexBehavior == BattleSourceIndexBehavior.ExternalRandom ||
                observation.Entry29?.SourceIndexBehavior == BattleSourceIndexBehavior.ExternalRandom ||
                IsTemplateSelectionExternallyDerived(observation))
            {
                return BattleSourceIndexBehavior.ExternalRandom;
            }

            ushort? sharedProviderAddr = GetSharedSourceIndexProviderAddress(observation);
            if (sharedProviderAddr.HasValue &&
                result?.AdjustedMemoryAddresses?.Contains(sharedProviderAddr.Value) == true)
            {
                return BattleSourceIndexBehavior.AdvancesWithLoop;
            }

            if (observation.Entry58?.SourceIndexBehavior == BattleSourceIndexBehavior.Fixed ||
                observation.Entry29?.SourceIndexBehavior == BattleSourceIndexBehavior.Fixed ||
                sharedProviderAddr.HasValue ||
                observation.Entry58?.OriginalSourceIndex.HasValue == true ||
                observation.Entry29?.OriginalSourceIndex.HasValue == true)
            {
                return BattleSourceIndexBehavior.Fixed;
            }

            return BattleSourceIndexBehavior.Unknown;
        }

        private static bool CanExpandSequentialBattleCopy(
            PathAnalysisResult result,
            PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return false;

            return InferSourceIndexBehavior(result, observation) == BattleSourceIndexBehavior.AdvancesWithLoop;
        }

        private static bool IsTemplateSelectionExternallyDerived(PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return false;

            return observation.Entry58?.SourceIndexExternallyDerived == true ||
                   observation.Entry29?.SourceIndexExternallyDerived == true;
        }

        private bool CanParticipateInUnifiedPartialBattleInference(PartialBattleTemplateObservation? observation)
        {
            if (observation == null)
                return false;

            if (IsTemplateSelectionExternallyDerived(observation))
                return true;

            if (IsKnownPartialBattleTemplateSource(observation.Entry58?.SourceTable) ||
                IsKnownPartialBattleTemplateSource(observation.Entry29?.SourceTable))
            {
                return true;
            }

            return false;
        }

        private static string BuildSequentialTableCopyPatternKey(PartialBattleTemplateObservation observation)
        {
            if (observation == null)
                return string.Empty;

            int firstDelta = observation.FirstOffset - observation.SaveBxIndex;
            int secondDelta = observation.SecondOffset - observation.SaveBxIndex;
            return $"{observation.FirstBaseAddr:X4}:{observation.SecondBaseAddr:X4}:{firstDelta}:{secondDelta}";
        }

        private static void UpsertBattleMonsterEntry(
            PathAnalysisResult result,
            int saveBxIndex,
            byte val1,
            byte val2,
            bool isIndeterminate)
        {
            if (result == null)
                return;

            if (result.BattleMonsterEntries.TryGetValue(saveBxIndex, out var existing) &&
                !existing.isIndeterminate &&
                isIndeterminate)
            {
                return;
            }

            result.BattleMonsterEntries[saveBxIndex] = (val1, val2, isIndeterminate);
        }

        private bool TryGetSequentialBattleCopyCountRange(
            PathAnalysisResult result,
            out int minCount,
            out int maxCount)
        {
            minCount = 0;
            maxCount = 0;

            if (result == null || result.IsBattleMonsterCountIndeterminate)
                return false;

            if (TryGetExactBattleMonsterCount(result, out byte exactCount) && exactCount > 0)
            {
                minCount = exactCount;
                maxCount = exactCount;
                return true;
            }

            if (result.BattleMonsterCountRange != null && result.BattleMonsterCountRange.Max > 0)
            {
                minCount = result.BattleMonsterCountRange.Min;
                maxCount = result.BattleMonsterCountRange.Max;
                return true;
            }

            if (result.BattleMonsterCount.HasValue && result.BattleMonsterCount.Value > 0)
            {
                minCount = result.BattleMonsterCount.Value;
                maxCount = result.BattleMonsterCount.Value;
                return true;
            }

            if (result.LoopIterationCount > 0)
            {
                minCount = result.LoopIterationCount;
                maxCount = result.LoopIterationCount;
                return true;
            }

            return false;
        }

        private bool TryPromoteSequentialTableCopyToBattleEntries(
            BinaryReader br,
            PathAnalysisResult result,
            PartialBattleTemplateObservation observation,
            out int expandedEntryCount,
            out string countText)
        {
            expandedEntryCount = 0;
            countText = string.Empty;

            if (br == null ||
                result == null ||
                observation == null ||
                !TryGetSequentialBattleCopyCountRange(result, out int minCount, out int maxCount) ||
                maxCount <= 0)
            {
                return false;
            }

            var stagedEntries = new List<(int SaveBxIndex, byte Val1, byte Val2, bool IsIndeterminate)>();

            for (int copyIndex = 0; copyIndex < maxCount; copyIndex++)
            {
                ushort firstAddr = (ushort)(observation.FirstBaseAddr + observation.FirstOffset + copyIndex);
                ushort secondAddr = (ushort)(observation.SecondBaseAddr + observation.SecondOffset + copyIndex);

                if (!TryReadOverlayByte(br, firstAddr, out byte val1) ||
                    !TryReadOverlayByte(br, secondAddr, out byte val2))
                {
                    return false;
                }

                stagedEntries.Add((
                    observation.SaveBxIndex + copyIndex,
                    val1,
                    val2,
                    copyIndex >= minCount));
            }

            foreach (var entry in stagedEntries)
            {
                UpsertBattleMonsterEntry(result, entry.SaveBxIndex, entry.Val1, entry.Val2, entry.IsIndeterminate);
            }

            expandedEntryCount = stagedEntries.Count;
            countText = minCount == maxCount ? minCount.ToString() : $"{minCount}-{maxCount}";
            return expandedEntryCount > 0;
        }

        private HashSet<int> PromoteSequentialTableCopiesToFullyDefinedBattles(
            BinaryReader br,
            PathAnalysisResult result,
            List<IGrouping<int, PartialBattleInfo>> groupedByBx,
            bool isFinalPass)
        {
            var promoted = new HashSet<int>();

            if (result == null || groupedByBx == null || groupedByBx.Count == 0)
                return promoted;

            bool hasSequentialContext = result.IsInLoop || isFinalPass;
            if (!hasSequentialContext)
                return promoted;

            var sequentialPatternGroups = groupedByBx
                .Select(CreatePartialBattleTemplateObservation)
                .Where(observation =>
                    observation != null &&
                    HasAlignedSourceOffsets(observation) &&
                    CanExpandSequentialBattleCopy(result, observation) &&
                    !IsKnownPartialBattleTemplateSource(observation.Entry58?.SourceTable) &&
                    !IsKnownPartialBattleTemplateSource(observation.Entry29?.SourceTable) &&
                    !IsTemplateSelectionExternallyDerived(observation))
                .GroupBy(BuildSequentialTableCopyPatternKey)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(observation => observation.SaveBxIndex).ToList());

            foreach (var group in groupedByBx)
            {
                if (promoted.Contains(group.Key))
                    continue;

                var observation = CreatePartialBattleTemplateObservation(group);
                if (observation == null || !HasAlignedSourceOffsets(observation))
                    continue;

                BattleSourceIndexBehavior sourceIndexBehavior = InferSourceIndexBehavior(result, observation);
                ApplySourceIndexBehavior(observation, sourceIndexBehavior);

                if (IsKnownPartialBattleTemplateSource(observation.Entry58?.SourceTable) ||
                    IsTemplateSelectionExternallyDerived(observation) ||
                    IsKnownPartialBattleTemplateSource(observation.Entry29?.SourceTable))
                {
                    continue;
                }

                string patternKey = BuildSequentialTableCopyPatternKey(observation);
                if (sourceIndexBehavior == BattleSourceIndexBehavior.AdvancesWithLoop &&
                    sequentialPatternGroups.TryGetValue(patternKey, out var patternObservations) &&
                    patternObservations.Count > 0 &&
                    patternObservations[0].SaveBxIndex == observation.SaveBxIndex &&
                    TryPromoteSequentialTableCopyToBattleEntries(br, result, observation, out int expandedEntryCount, out string countText))
                {
                    foreach (var patternObservation in patternObservations)
                    {
                        promoted.Add(patternObservation.SaveBxIndex);
                    }

                    AnalysisDebug.WriteLine(
                        $"        -> РАЗВЁРНУТО ПОСЛЕДОВАТЕЛЬНОЕ КОПИРОВАНИЕ ПОЛНОЙ БИТВЫ: startBX={observation.SaveBxIndex}, " +
                        $"entries={expandedEntryCount}, count={countText}, source=[{observation.FirstBaseAddr:X4}/{observation.SecondBaseAddr:X4}], " +
                        $"offsets={observation.FirstOffset}/{observation.SecondOffset}");
                    continue;
                }

                if (IsFullyDefinedBattleEntry(result, observation.SaveBxIndex))
                {
                    promoted.Add(observation.SaveBxIndex);
                    AnalysisDebug.WriteLine($"        -> BX={observation.SaveBxIndex} уже определён как полная битва, частичный анализ пропускаем");
                    continue;
                }

                UpsertBattleMonsterEntry(result, observation.SaveBxIndex, observation.Entry58.SrcRegValue, observation.Entry29.SrcRegValue, false);
                promoted.Add(observation.SaveBxIndex);

                AnalysisDebug.WriteLine(
                    $"        -> ПЕРЕВЕДЕНО В ПОЛНУЮ БИТВУ: BX={observation.SaveBxIndex}, " +
                    $"val1=0x{observation.Entry58.SrcRegValue:X2}, val2=0x{observation.Entry29.SrcRegValue:X2}, " +
                    $"source=[{observation.FirstBaseAddr:X4}/{observation.SecondBaseAddr:X4}], offsets={observation.FirstOffset}/{observation.SecondOffset}");
            }

            return promoted;
        }

        private bool ShouldSkipObservedPartialBattle(IGrouping<int, PartialBattleInfo> group)
        {
            var observation = CreatePartialBattleTemplateObservation(group);
            if (observation == null)
                return false;

            if (IsKnownPartialBattleTemplateSource(observation.Entry58?.SourceTable) ||
                IsKnownPartialBattleTemplateSource(observation.Entry29?.SourceTable))
            {
                return false;
            }

            if (IsTemplateSelectionExternallyDerived(observation))
                return false;

            return HasAlignedSourceOffsets(observation);
        }

        private bool ShouldDeferUnknownTemplateInference(
            PathAnalysisResult result,
            List<PartialBattleTemplateObservation> templateObservations,
            bool isFinalPass)
        {
            if (isFinalPass || result == null || templateObservations == null || templateObservations.Count != 1)
                return false;

            if (result.IsInLoop)
                return false;

            var sample = templateObservations[0];
            if (IsKnownPartialBattleTemplateSource(sample.Entry58?.SourceTable) ||
                IsKnownPartialBattleTemplateSource(sample.Entry29?.SourceTable))
            {
                return false;
            }

            if (IsTemplateSelectionExternallyDerived(sample))
                return false;

            return HasAlignedSourceOffsets(sample);
        }

        private bool TryAnalyzeUnifiedPartialBattleTemplates(
            BinaryReader br,
            PathAnalysisResult result,
            List<IGrouping<int, PartialBattleInfo>> groupedByBx,
            bool isFinalPass = false)
        {
            if (br == null || groupedByBx == null || groupedByBx.Count == 0)
                return false;

            var observations = groupedByBx
                .Select(CreatePartialBattleTemplateObservation)
                .OfType<PartialBattleTemplateObservation>()
                .Where(observation =>
                    !IsFullyDefinedBattleEntry(result, observation.SaveBxIndex) &&
                    CanParticipateInUnifiedPartialBattleInference(observation))
                .ToList();

            if (observations.Count == 0)
                return false;

            bool handledAny = false;

            foreach (var templateGroup in observations
                .GroupBy(observation => $"{observation.FirstBaseAddr:X4}:{observation.SecondBaseAddr:X4}")
                .OrderBy(group => group.Key))
            {
                var templateObservations = templateGroup
                    .OrderBy(observation => observation.SaveBxIndex)
                    .ToList();

                if (templateObservations.Count == 0)
                    continue;

                ushort firstBaseAddr = templateObservations[0].FirstBaseAddr;
                ushort secondBaseAddr = templateObservations[0].SecondBaseAddr;

                if (ShouldDeferUnknownTemplateInference(result, templateObservations, isFinalPass))
                {
                    AnalysisDebug.WriteLine($"        -> ОТКЛАДЫВАЕМ ИНТЕРПРЕТАЦИЮ UNKNOWN-шаблона [{firstBaseAddr:X4}/{secondBaseAddr:X4}] до завершения анализа пути");
                    continue;
                }

                handledAny = true;

                int optionCount = DeterminePartialBattleOptionCount(result, templateObservations, groupedByBx);
                int startOffset = 0;

                var exactOptions = ReadPartialBattleExactOptions(br, firstBaseAddr, secondBaseAddr, startOffset, optionCount);
                if (exactOptions.Count == 0)
                {
                    exactOptions = templateObservations
                        .Select(observation => new DiscreteBattleOption
                        {
                            Val1 = observation.Entry58.SrcRegValue,
                            Val2 = observation.Entry29.SrcRegValue
                        })
                        .ToList();
                }

                exactOptions = exactOptions
                    .Where(option => option != null)
                    .GroupBy(option => $"{option.Val1:X2}:{option.Val2:X2}")
                    .Select(group => group.First())
                    .OrderBy(option => option.Val1)
                    .ThenBy(option => option.Val2)
                    .ToList();

                if (exactOptions.Count == 0)
                    continue;

                var battle = new PartiallyDefinedBattle
                {
                    BxIndex = templateObservations.Min(observation => observation.SaveBxIndex),
                    RepeatCount = GetPartialBattleRepeatCount(result),
                    RepeatCountRange = GetPartialBattleRepeatCountRange(result),
                    ExactOptions = exactOptions
                };

                string identityKey = battle.GetIdentityKey();
                if (result.PartialBattles.Any(existing => existing.GetIdentityKey() == identityKey))
                {
                    AnalysisDebug.WriteLine($"        -> ЧАСТИЧНАЯ БИТВА ДЛЯ ШАБЛОНА [{firstBaseAddr:X4}/{secondBaseAddr:X4}] уже существует");
                    continue;
                }

                result.PartialBattles.Add(battle);
                AnalysisDebug.WriteLine($"        -> СОЗДАН УНИФИЦИРОВАННЫЙ ШАБЛОН ЧАСТИЧНОЙ БИТВЫ [{firstBaseAddr:X4}/{secondBaseAddr:X4}]: BX={battle.BxIndex}, options={exactOptions.Count}, repeatCount={FormatPartialBattleRepeatCount(battle)}");
            }

            return handledAny;
        }

        private bool IsFullyDefinedBattleEntry(PathAnalysisResult result, int bxIndex)
        {
            if (result == null || !result.BattleMonsterEntries.TryGetValue(bxIndex, out var entry))
                return false;

            return entry.val1 != 0 && entry.val2 != 0;
        }

        private ValueRange8 GetPartialBattleRepeatCountRange(PathAnalysisResult result)
        {
            if (result?.BattleMonsterCountRange != null &&
                !result.IsBattleMonsterCountIndeterminate &&
                result.BattleMonsterCountRange.Max > 0)
            {
                return new ValueRange8(result.BattleMonsterCountRange.Min, result.BattleMonsterCountRange.Max);
            }

            return null;
        }

        private int GetPartialBattleRepeatCount(PathAnalysisResult result)
        {
            if (TryGetExactBattleMonsterCount(result, out byte exactBattleMonsterCount) &&
                exactBattleMonsterCount > 0)
            {
                AnalysisDebug.WriteLine($"    Точное количество монстров для частичной битвы: {exactBattleMonsterCount}");
                return exactBattleMonsterCount;
            }

            return 1;
        }

        private static string FormatPartialBattleRepeatCount(PartiallyDefinedBattle battle)
        {
            if (battle == null)
                return "?";

            return battle.RepeatCountRange != null
                ? (battle.RepeatCountRange.IsExact
                    ? battle.RepeatCountRange.Min.ToString()
                    : $"{battle.RepeatCountRange.Min}-{battle.RepeatCountRange.Max}")
                : Math.Max(1, battle.RepeatCount).ToString();
        }

        private int DeterminePartialBattleIterationCount(
            PathAnalysisResult result,
            List<IGrouping<int, PartialBattleInfo>> groupedByBx)
        {
            if (result == null || groupedByBx == null || groupedByBx.Count == 0)
                return 1;

            if (!result.IsInLoop || result.LoopStartAddress == 0)
                return 1;

            if (result.LoopIterationCount > 0)
                return result.LoopIterationCount;

            int maxSaveBx = groupedByBx.Max(group => group.Key);
            return maxSaveBx > 0 ? maxSaveBx + 1 : 1;
        }

        private int DeterminePartialBattleOptionCount(
            PathAnalysisResult result,
            List<PartialBattleTemplateObservation> observations,
            List<IGrouping<int, PartialBattleInfo>> groupedByBx)
        {
            if (observations == null || observations.Count == 0)
                return 1;

            var offsets = observations
                .Select(observation => observation.FirstOffset)
                .Where(offset => offset >= 0)
                .Distinct()
                .OrderBy(offset => offset)
                .ToList();

            if (offsets.Count > 1)
            {
                bool sequential = true;
                for (int i = 1; i < offsets.Count; i++)
                {
                    if (offsets[i] != offsets[i - 1] + 1)
                    {
                        sequential = false;
                        break;
                    }
                }

                if (sequential)
                    return offsets[offsets.Count - 1] - offsets[0] + 1;

                return offsets.Count;
            }

            var sample = observations[0];
            int tableGap = sample.SecondBaseAddr - sample.FirstBaseAddr;
            if (tableGap > 1 && tableGap <= 32)
                return tableGap;

            int iterationCount = DeterminePartialBattleIterationCount(result, groupedByBx);
            if (iterationCount > 1)
                return iterationCount;

            return 1;
        }

        private List<DiscreteBattleOption> ReadPartialBattleExactOptions(
            BinaryReader br,
            ushort firstBaseAddr,
            ushort secondBaseAddr,
            int startOffset,
            int optionCount)
        {
            var exactOptions = new List<DiscreteBattleOption>();

            if (br == null || optionCount <= 0)
                return exactOptions;

            for (int optionIndex = 0; optionIndex < optionCount; optionIndex++)
            {
                ushort firstAddr = (ushort)(firstBaseAddr + startOffset + optionIndex);
                ushort secondAddr = (ushort)(secondBaseAddr + startOffset + optionIndex);

                bool readSuccess1 = TryReadOverlayByte(br, firstAddr, out byte val1);
                bool readSuccess2 = TryReadOverlayByte(br, secondAddr, out byte val2);

                AnalysisDebug.WriteLine(readSuccess1 && readSuccess2
                    ? $"        ШАБЛОН [{firstBaseAddr:X4}/{secondBaseAddr:X4}] option {optionIndex + 1} -> [{firstAddr:X4}] = 0x{val1:X2}, [{secondAddr:X4}] = 0x{val2:X2}"
                    : $"        ШАБЛОН [{firstBaseAddr:X4}/{secondBaseAddr:X4}] option {optionIndex + 1} -> read failed");

                if (!readSuccess1 || !readSuccess2)
                    continue;

                exactOptions.Add(new DiscreteBattleOption
                {
                    Val1 = val1,
                    Val2 = val2
                });
            }

            return exactOptions;
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

        private bool TryGetOverlayTableSourceFromBxIndexedOperand(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            ushort memAddr,
            out ushort originalBx)
        {
            originalBx = 0;

            if (instructionBytes == null || instructionBytes.Length < 2 || memAddr < 0xC000)
                return false;

            byte modRm = instructionBytes[1];
            byte mod = (byte)((modRm >> 6) & 0x03);
            byte rm = (byte)(modRm & 0x07);

            // Табличные паттерны битв используют адреса вида [BX+disp8/disp16].
            if (mod == 0x03 || mod == 0x00 || rm != 0x07)
                return false;

            return registerTracker.TryGetRegisterValue("BX", out originalBx);
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

        private bool TryDecode16BitEffectiveAddressRange(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            out ushort baseAddress,
            out ValueRange8 offsetRange,
            out RegisterValueDistribution distribution,
            out int decodedLength,
            out string eaText)
        {
            baseAddress = 0;
            offsetRange = null;
            distribution = RegisterValueDistribution.Unknown;
            decodedLength = 0;
            eaText = null;

            if (registerTracker == null ||
                !TryDecode16BitMemoryOperandSyntax(
                    instructionBytes,
                    out byte mod,
                    out byte rm,
                    out int signedDisp,
                    out decodedLength,
                    out eaText))
            {
                return false;
            }

            if (mod == 0x00 && rm == 0x06)
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
                    if (!AccumulateComponent("BP"))
                        return false;
                    break;
                case 0x07:
                    if (!AccumulateComponent("BX"))
                        return false;
                    break;
                default:
                    return false;
            }

            if (rangedOffset == null)
                return false;

            int minAddress = baseContribution + rangedOffset.Min;
            int maxAddress = baseContribution + rangedOffset.Max;
            if (baseContribution < 0 ||
                baseContribution > ushort.MaxValue ||
                minAddress < 0 ||
                maxAddress > ushort.MaxValue)
            {
                return false;
            }

            baseAddress = (ushort)baseContribution;
            offsetRange = rangedOffset;
            distribution = rangedDistribution;
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
            {
                RegisterMemoryRead(result, memAddr, _persistentEventStateAddresses.Contains(memAddr));
                return true;
            }

            if (TryGetEmulatedMemory8Range(memAddr, out var range, out _))
            {
                RegisterMemoryRead(result, memAddr, _persistentEventStateAddresses.Contains(memAddr));
                if (range.IsExact)
                {
                    value = range.Min;
                    return true;
                }

                value = 0;
                return false;
            }

            if (memAddr == 0x3C38)
            {
                value = result?.TeleportTargetX ?? targetX;
                RegisterMemoryRead(result, memAddr, false);
                return true;
            }

            if (memAddr == 0x3C39)
            {
                value = result?.TeleportTargetY ?? targetY;
                RegisterMemoryRead(result, memAddr, false);
                return true;
            }

            if (memAddr == 0x3C3A)
            {
                byte x = (byte)((result?.TeleportTargetX ?? targetX) & 0x0F);
                byte y = (byte)((result?.TeleportTargetY ?? targetY) & 0x0F);
                value = (byte)((y << 4) | x);
                RegisterMemoryRead(result, memAddr, false);
                if (result != null)
                    result.UsesInitialCoordinates = true;
                return true;
            }

            if (TryReadStaticMapDataByte(memAddr, out value, out _))
            {
                RegisterMemoryRead(result, memAddr, false);
                if (result != null)
                {
                    result.UsesInitialCoordinates = true;
                    result.UsesStaticMapData = true;
                    result.StaticMapDataReads[memAddr] = value;
                }

                return true;
            }

            if (TryReadOverlayByte(br, memAddr, out value))
            {
                RegisterMemoryRead(result, memAddr, true);
                return true;
            }

            return false;
        }

        private bool TryResolveTrackedWordValue(BinaryReader br, ushort memAddr, PathAnalysisResult result, byte targetX, byte targetY, out ushort value)
        {
            value = 0;

            if (memAddr == 0x3C3A)
            {
                byte x = (byte)((result?.TeleportTargetX ?? targetX) & 0x0F);
                byte y = (byte)((result?.TeleportTargetY ?? targetY) & 0x0F);
                value = (ushort)((y << 4) | x);
                RegisterMemoryRead(result, memAddr, false);
                if (result != null)
                    result.UsesInitialCoordinates = true;
                return true;
            }

            if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte low) &&
                TryResolveTrackedByteValue(br, unchecked((ushort)(memAddr + 1)), result, targetX, targetY, out byte high))
            {
                value = (ushort)(low | (high << 8));
                return true;
            }

            return false;
        }

        private bool TryReadStaticMapDataByte(ushort memAddr, out byte value, out string layerName)
        {
            value = 0;
            layerName = null;

            int firstLayerOffset = memAddr - StaticMapFirstLayerBaseAddress;
            if (firstLayerOffset >= 0 && firstLayerOffset < StaticMapLayerByteCount)
            {
                layerName = nameof(OvrFileConfig.First16Lines);
                return TryReadStaticMapLayerCell(_config?.First16Lines, firstLayerOffset, out value);
            }

            int secondLayerOffset = memAddr - StaticMapSecondLayerBaseAddress;
            if (secondLayerOffset >= 0 && secondLayerOffset < StaticMapLayerByteCount)
            {
                layerName = nameof(OvrFileConfig.Second16Lines);
                return TryReadStaticMapLayerCell(_config?.Second16Lines, secondLayerOffset, out value);
            }

            return false;
        }

        private static bool IsStaticMapDataAddress(ushort memAddr)
        {
            return IsAddressInStaticMapLayer(memAddr, StaticMapFirstLayerBaseAddress) ||
                   IsAddressInStaticMapLayer(memAddr, StaticMapSecondLayerBaseAddress);
        }

        private static bool IsAddressInStaticMapLayer(ushort memAddr, ushort layerBaseAddress)
        {
            int offset = memAddr - layerBaseAddress;
            return offset >= 0 && offset < StaticMapLayerByteCount;
        }

        private static bool TryReadStaticMapLayerCell(string[] lines, int offset, out byte value)
        {
            value = 0;

            if (lines == null || lines.Length < StaticMapLayerHeight ||
                offset < 0 || offset >= StaticMapLayerByteCount)
            {
                return false;
            }

            int y = offset / StaticMapLayerWidth;
            int x = offset % StaticMapLayerWidth;
            string line = lines[y];
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= x)
                return false;

            return byte.TryParse(tokens[x], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private bool IsTrackedWordInEmulatedMemory(ushort memAddr)
        {
            return _emulatedMemory8.ContainsKey(memAddr) ||
                   _emulatedMemory8.ContainsKey(unchecked((ushort)(memAddr + 1)));
        }

        private bool TryReadOverlayByte(BinaryReader br, ushort memAddr, out byte value)
        {
            return OvrOverlayAddressReader.TryReadByte(br, _config, memAddr, out value);
        }

        private bool TryReadOverlayWord(BinaryReader br, ushort memAddr, out ushort value)
        {
            return OvrOverlayAddressReader.TryReadWord(br, _config, memAddr, out value);
        }

        private static bool TryGetBattleMonsterSlot(ushort memAddr, ushort tableBaseAddress, out int slotIndex)
        {
            slotIndex = memAddr - tableBaseAddress;
            return slotIndex >= 0 && slotIndex < BATTLE_MONSTER_TABLE_SLOT_COUNT;
        }

        private static void UpsertTrackedBattleMonsterComponent(
            PathAnalysisResult result,
            int slotIndex,
            byte value,
            bool isFirstComponent)
        {
            if (result == null)
                return;

            result.BattleMonsterEntries.TryGetValue(slotIndex, out var existing);
            result.BattleMonsterEntries[slotIndex] = isFirstComponent
                ? (value, existing.val2, false)
                : (existing.val1, value, false);
            if (value != 0)
                result.HasSignificantCode = true;
        }

        private static void ApplyTrackedBattleMonsterTableWrite(
            ushort memAddr,
            byte value,
            PathAnalysisResult result,
            bool debugMode)
        {
            if (result == null)
                return;

            if (TryGetBattleMonsterSlot(memAddr, BATTLE_MONSTER_FIRST_TABLE_ADDRESS, out int firstSlot))
            {
                UpsertTrackedBattleMonsterComponent(result, firstSlot, value, true);
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Семантика [0x{BATTLE_MONSTER_FIRST_TABLE_ADDRESS:X4}+{firstSlot}]: первый индекс монстра = 0x{value:X2}");
            }
            else if (TryGetBattleMonsterSlot(memAddr, BATTLE_MONSTER_SECOND_TABLE_ADDRESS, out int secondSlot))
            {
                UpsertTrackedBattleMonsterComponent(result, secondSlot, value, false);
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Семантика [0x{BATTLE_MONSTER_SECOND_TABLE_ADDRESS:X4}+{secondSlot}]: второй индекс монстра = 0x{value:X2}");
            }
        }

        private bool TryApplyTrackedByteWriteToRangedBattleTable(
            byte[] instructionBytes,
            RegisterTracker registerTracker,
            byte value,
            PathAnalysisResult result,
            bool debugMode,
            string sourceDescription)
        {
            if (result == null ||
                !TryDecode16BitEffectiveAddressRange(
                    instructionBytes,
                    registerTracker,
                    out ushort baseAddress,
                    out ValueRange8 offsetRange,
                    out _,
                    out _,
                    out string eaText))
            {
                return false;
            }

            if (TryGetBattleMonsterSlotRange(
                    baseAddress,
                    offsetRange,
                    BATTLE_MONSTER_FIRST_TABLE_ADDRESS,
                    out int firstSlotMin,
                    out int firstSlotMax) &&
                TryGetAppendSlotForRangedBattleFill(result, offsetRange, firstSlotMax, out int firstSyntheticSlot))
            {
                ApplyRangedBattleTableComponentWrite(
                    result,
                    BATTLE_MONSTER_FIRST_TABLE_ADDRESS,
                    firstSlotMin,
                    firstSlotMax,
                    firstSyntheticSlot,
                    value,
                    isFirstComponent: true,
                    debugMode,
                    sourceDescription,
                    eaText);
                return true;
            }

            if (TryGetBattleMonsterSlotRange(
                    baseAddress,
                    offsetRange,
                    BATTLE_MONSTER_SECOND_TABLE_ADDRESS,
                    out int secondSlotMin,
                    out int secondSlotMax) &&
                TryGetAppendSlotForRangedBattleFill(result, offsetRange, secondSlotMax, out int secondSyntheticSlot))
            {
                ApplyRangedBattleTableComponentWrite(
                    result,
                    BATTLE_MONSTER_SECOND_TABLE_ADDRESS,
                    secondSlotMin,
                    secondSlotMax,
                    secondSyntheticSlot,
                    value,
                    isFirstComponent: false,
                    debugMode,
                    sourceDescription,
                    eaText);
                return true;
            }

            return false;
        }

        private static bool TryGetBattleMonsterSlotRange(
            ushort baseAddress,
            ValueRange8 offsetRange,
            ushort tableBaseAddress,
            out int slotMin,
            out int slotMax)
        {
            slotMin = 0;
            slotMax = 0;

            if (offsetRange == null)
                return false;

            int minAddress = baseAddress + offsetRange.Min;
            int maxAddress = baseAddress + offsetRange.Max;
            int tableEndAddress = tableBaseAddress + BATTLE_MONSTER_TABLE_SLOT_COUNT - 1;

            if (minAddress < tableBaseAddress || maxAddress > tableEndAddress)
                return false;

            slotMin = minAddress - tableBaseAddress;
            slotMax = maxAddress - tableBaseAddress;
            return slotMin >= 0 && slotMax >= slotMin && slotMax < BATTLE_MONSTER_TABLE_SLOT_COUNT;
        }

        private static bool TryGetAppendSlotForRangedBattleFill(
            PathAnalysisResult result,
            ValueRange8 offsetRange,
            int slotMax,
            out int syntheticSlot)
        {
            syntheticSlot = 0;

            if (result == null ||
                offsetRange == null ||
                result.BattleMonsterCountRange == null ||
                result.BattleMonsterCountRange.Min != offsetRange.Min ||
                result.BattleMonsterCountRange.Max != offsetRange.Max ||
                slotMax != offsetRange.Max ||
                slotMax >= BATTLE_MONSTER_TABLE_SLOT_COUNT)
            {
                return false;
            }

            for (int slot = 0; slot < slotMax; slot++)
            {
                if (!result.BattleMonsterEntries.ContainsKey(slot))
                    return false;
            }

            syntheticSlot = slotMax;
            return true;
        }

        private void ApplyRangedBattleTableComponentWrite(
            PathAnalysisResult result,
            ushort tableBaseAddress,
            int slotMin,
            int slotMax,
            int syntheticSlot,
            byte value,
            bool isFirstComponent,
            bool debugMode,
            string sourceDescription,
            string eaText)
        {
            for (int slot = slotMin; slot <= slotMax; slot++)
            {
                ushort possibleAddress = unchecked((ushort)(tableBaseAddress + slot));
                _emulatedMemory8.Remove(possibleAddress);
                _emulatedMemory8Ranges.Remove(possibleAddress);
                _emulatedMemory8RangeDistributions.Remove(possibleAddress);
                _emulatedPartyPointerBytes.Remove(possibleAddress);
                _emulatedPartyPointers.Remove(possibleAddress);
                _emulatedPartyPointers.Remove(unchecked((ushort)(possibleAddress - 1)));
                RegisterMemoryWrite(result, possibleAddress);
            }

            UpsertTrackedBattleMonsterComponent(result, syntheticSlot, value, isFirstComponent);
            result.HasSignificantCode = true;

            if (debugMode)
            {
                string componentText = isFirstComponent ? "первый индекс" : "второй индекс";
                AnalysisDebug.WriteLine(
                    $"        {sourceDescription}: диапазонная запись {eaText} затрагивает [{tableBaseAddress:X4}+{slotMin}..{slotMax}], " +
                    $"семантически добавлен хвостовой слот {syntheticSlot}: {componentText} = 0x{value:X2}");
            }
        }

        private void ApplyTrackedByteWrite(ushort memAddr, byte value, PathAnalysisResult result,
            byte targetX, byte targetY, X86Instruction insn, bool debugMode, string sourceDescription,
            bool trackBattleMonsterTableWrite = false,
            bool trackBattleMonsterStrengthDelta = true)
        {
            byte? previousExactValue = null;
            if (_emulatedMemory8.TryGetValue(memAddr, out byte previousValue))
                previousExactValue = previousValue;
            else if (_emulatedMemory8Ranges.TryGetValue(memAddr, out var previousRange) &&
                     previousRange?.IsExact == true)
                previousExactValue = previousRange.Min;

            _emulatedMemory8[memAddr] = value;
            _emulatedMemory8Ranges.Remove(memAddr);
            _emulatedMemory8RangeDistributions.Remove(memAddr);
            _emulatedPartyPointerBytes.Remove(memAddr);
            _emulatedPartyPointers.Remove(memAddr);
            _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));
            RegisterMemoryWrite(result, memAddr);
            RefreshPendingPersistentCounterStoredValue(result, memAddr, value, (uint)insn.Address);

            if (debugMode)
                AnalysisDebug.WriteLine($"        {sourceDescription}: записали 0x{value:X2} в эмулируемую память [0x{memAddr:X4}]");

            if (result == null)
                return;

            if (trackBattleMonsterStrengthDelta &&
                previousExactValue.HasValue &&
                previousExactValue.Value != value)
            {
                TrackBattleMonsterStrengthAdjustment(
                    result,
                    memAddr,
                    value - previousExactValue.Value,
                    (uint)insn.Address,
                    debugMode);
            }

            if (trackBattleMonsterTableWrite)
                ApplyTrackedBattleMonsterTableWrite(memAddr, value, result, debugMode);

            if (memAddr == BATTLE_RANDOM_ENCOUNTER_RUBICON_ADDRESS)
            {
                result.RandomEncounterRubicon = value;
                result.HasSignificantCode = true;

                if (debugMode)
                {
                    AnalysisDebug.WriteLine(
                        $"        Семантика [0x{BATTLE_RANDOM_ENCOUNTER_RUBICON_ADDRESS:X4}]: рубикон добора random encounter = {value}");
                }
            }
            else if (memAddr == BATTLE_MONSTER_COUNT_ADDRESS)
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

        private void ApplyTrackedByteRangeWrite(ushort memAddr, ValueRange8 range,
            RegisterValueDistribution distribution, PathAnalysisResult result,
            byte targetX, byte targetY, X86Instruction insn, bool debugMode, string sourceDescription)
        {
            if (range == null)
                return;

            _emulatedMemory8.Remove(memAddr);
            _emulatedMemory8Ranges[memAddr] = new ValueRange8(range.Min, range.Max);
            _emulatedMemory8RangeDistributions[memAddr] = distribution;
            _emulatedPartyPointerBytes.Remove(memAddr);
            _emulatedPartyPointers.Remove(memAddr);
            _emulatedPartyPointers.Remove(unchecked((ushort)(memAddr - 1)));
            RegisterMemoryWrite(result, memAddr);

            if (debugMode)
            {
                string valueText = range.IsExact
                    ? $"0x{range.Min:X2}"
                    : $"0x{range.Min:X2}-0x{range.Max:X2}";
                AnalysisDebug.WriteLine($"        {sourceDescription}: записали диапазон {valueText} в эмулируемую память [0x{memAddr:X4}]");
            }

            if (result == null)
                return;

            if (memAddr == BATTLE_MONSTER_COUNT_ADDRESS)
            {
                result.BattleMonsterCount = range.IsExact ? range.Min : (byte?)null;
                result.BattleMonsterCountRange = new ValueRange8(range.Min, range.Max);
                result.IsBattleMonsterCountIndeterminate = false;
                result.HasSignificantCode = true;
            }
            else if (memAddr == 0x3C38)
            {
                result.TeleportTargetX = range.IsExact ? range.Min : (byte?)null;
                result.TeleportTargetXRange = new ValueRange8(range.Min, range.Max);
                if (!result.TeleportTargetY.HasValue && result.TeleportTargetYRange == null)
                    result.TeleportTargetY = targetY;
                result.HasSignificantCode = true;
            }
            else if (memAddr == 0x3C39)
            {
                result.TeleportTargetY = range.IsExact ? range.Min : (byte?)null;
                result.TeleportTargetYRange = new ValueRange8(range.Min, range.Max);
                if (!result.TeleportTargetX.HasValue && result.TeleportTargetXRange == null)
                    result.TeleportTargetX = targetX;
                result.HasSignificantCode = true;
            }
        }

        private static void MirrorRangeRegisterToMemorySource(
            RegisterTracker registerTracker,
            string registerName,
            ushort memAddr,
            ValueRange8 range,
            RegisterValueDistribution distribution,
            uint address,
            string instruction)
        {
            if (registerTracker == null || range == null || string.IsNullOrWhiteSpace(registerName))
                return;

            registerTracker.SetRegisterRangeWithSource(
                registerName,
                range.Min,
                range.Max,
                distribution,
                memAddr,
                address,
                instruction,
                sourceIndexProviderAddr: memAddr);
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
        private void FinalizeResult(PathAnalysisResult result, RegisterTracker registerTracker, int instructionCount,
            uint currentAddress, long fileLength, bool debugMode, int exitCallDepth)
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

            // Однобайтовый GOLD вроде [3C7D]=0x78 может остаться без текста на
            // per-instruction стадии, если старший байт явно не записывается.
            // К моменту выхода из leaf-пути финальное значение уже определено.
            MaterializePendingExactLootTexts(result, currentAddress, exitCallDepth, debugMode);

            result.UsesInitialCoordinates =
                result.UsesInitialCoordinates ||
                (registerTracker?.HasObservedCoordinateSeedRead ?? false) ||
                result.MemoryReadAddresses.Contains(0x3C38) ||
                result.MemoryReadAddresses.Contains(0x3C39) ||
                result.MemoryReadAddresses.Contains(0x3C3A);

            result.HasSignificantCode = result.HasSignificantCode ||
                                         result.OrderedTexts.Count > 0 ||
                                         result.FoundTexts.Count > 0 ||
                                         result.ContextTexts.Count > 0 ||
                                         result.RandomEncounterMonsterPowerCap.HasValue ||
                                         result.RandomEncounterMonsterLevelCap.HasValue ||
                                         result.RandomEncounterRubicon.HasValue ||
                                         result.BattleMonsterStrengthAdjustment != 0 ||
                                         result.BattleMonsterEntries.Values.Any(entry => entry.val1 != 0 && entry.val2 != 0) ||
                                         result.PartialBattles.Count > 0 ||
                                         (result.DynamicRandomBoundDependencies != null && result.DynamicRandomBoundDependencies.Count > 0) ||
                                         result.HasPartialBattlePattern ||
                                         result.CallsRandomEncounter ||
                                         result.HasTeleportTarget ||
                                         (result.PartyEffects != null && result.PartyEffects.Count > 0);
        }
    }
}
