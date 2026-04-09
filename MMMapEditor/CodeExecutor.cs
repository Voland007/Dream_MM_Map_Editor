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


﻿using System;
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

        private const int MAX_DEPTH = 12;
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
            Dictionary<ushort, byte> initialEmulatedMemory8 = null)
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
            _emulatedMemory8.Clear();
            if (initialEmulatedMemory8 != null)
            {
                foreach (var kv in initialEmulatedMemory8)
                    _emulatedMemory8[kv.Key] = kv.Value;
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
                    _instructionAnalyzer.FindTextsInInstruction(insn, br, registerTracker, depth, newTexts);
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
                        invalidateReturnRegistersAfterExternalCall, currentPendingReturnAddresses);

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
            }
        }

        private byte? TryGetEmulatedMemory8Value(ushort address)
        {
            return _emulatedMemory8.TryGetValue(address, out byte value)
                ? value
                : (byte?)null;
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

        /// <summary>
        /// Обрабатывает инструкции управления потоком
        /// </summary>
        private ControlFlowResult HandleControlFlowInstructions(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result, uint currentAddress, uint nextAddress, long fileLength, bool debugMode,
            bool invalidateReturnRegistersAfterExternalCall, List<uint> pendingReturnAddresses)
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
                return HandleConditionalJump(insn, condJumpTarget, nextAddress, fileLength,
                    debugMode, processedBackEdges, result, currentAddress, registerTracker, pendingReturnAddresses, callDepth);
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

        /// <summary>
        /// Обрабатывает инструкцию JMP
        /// </summary>
        private ControlFlowResult HandleJmp(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress)
        {
            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

            if (jumpTarget == 0x517C)
            {
                result.CallsRandomEncounter = true;
                result.HasSignificantCode = true;

                if (debugMode)
                    AnalysisDebug.WriteLine("      Обнаружен вызов random encounter через JMP к 0x517C");

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
                    result.CallsRandomEncounter = true;
                    result.HasSignificantCode = true;

                    if (debugMode)
                        AnalysisDebug.WriteLine("      Обнаружен вызов random encounter через JMP к 0x517C");

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
                        result.CallsRandomEncounter = true;
                        result.HasSignificantCode = true;
                        if (debugMode)
                            AnalysisDebug.WriteLine("      Обнаружен вызов random encounter через SHORT JMP к 0x517C");
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
                    new Dictionary<ushort, byte>(_emulatedMemory8));

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
                            RegisterState = altPath.RegisterState?.Clone(),
                            ProbabilityNumerator = altPath.ProbabilityNumerator,
                            ProbabilityDenominator = altPath.ProbabilityDenominator,
                            CallDepth = altPath.CallDepth,
                            PendingReturnAddresses = altPath.PendingReturnAddresses == null
                                ? new List<uint>()
                                : new List<uint>(altPath.PendingReturnAddresses),
                            EmulatedMemory8 = altPath.EmulatedMemory8 == null
                                ? new Dictionary<ushort, byte>()
                                : new Dictionary<ushort, byte>(altPath.EmulatedMemory8)
                        });
                    }
                }

                result.IsTerminated = subroutineResult.IsTerminated;
                result.HasSignificantCode = result.HasSignificantCode || subroutineResult.HasSignificantCode;
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
        private ControlFlowResult HandleConditionalJump(X86Instruction insn, uint condJumpTarget,
    uint nextAddress, long fileLength, bool debugMode,
    HashSet<(uint From, uint To)> processedBackEdges, PathAnalysisResult result, uint currentAddress,
    RegisterTracker registerTracker, List<uint> pendingReturnAddresses, int callDepth)
        {
            if (condJumpTarget < fileLength && condJumpTarget != 0)
            {
                bool? branchTaken = EvaluateConditionalJump(insn.Mnemonic, registerTracker);
                if (branchTaken.HasValue)
                {
                    uint resolvedTarget = branchTaken.Value ? condJumpTarget : nextAddress;

                    if (condJumpTarget < currentAddress && branchTaken.Value)
                    {
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

                // Добавляем целевой адрес перехода как альтернативный путь
                var altPath = new AlternativePath
                {
                    Address = (uint)insn.Address,
                    TargetAddress = condJumpTarget,
                    Condition = $"{insn.Mnemonic} {insn.Operand}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1,
                    RegisterState = CloneRegisterStateForBranch(registerTracker, insn.Mnemonic, branchTaken: true),
                    CompareRegister = registerTracker.LastFlagsRegister,
                    CompareValue = registerTracker.LastCompareImmediate,
                    ProbabilityNumerator = takenProbability.numerator,
                    ProbabilityDenominator = takenProbability.denominator,
                    CallDepth = callDepth,
                    PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                    EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8)
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
                    RegisterState = CloneRegisterStateForBranch(registerTracker, insn.Mnemonic, branchTaken: false),
                    CompareRegister = registerTracker.LastFlagsRegister,
                    CompareValue = registerTracker.LastCompareImmediate,
                    ProbabilityNumerator = notTakenProbability.numerator,
                    ProbabilityDenominator = notTakenProbability.denominator,
                    CallDepth = callDepth,
                    PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                    EmulatedMemory8 = new Dictionary<ushort, byte>(_emulatedMemory8)
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

            registerTracker.SetRegisterRange(reg, (byte)min, (byte)max, RegisterValueDistribution.UniformDiscreteRange);

            if (debugMode)
            {
                string branchText = branchTaken ? "taken" : "linear";
                AnalysisDebug.WriteLine(
                    $"      Уточнили диапазон {reg} для ветки {branchText} после {mnemonic}: {min:X2}..{max:X2} (инстр. 0x{instructionAddress:X4})");
            }
        }

        private RegisterTracker CloneRegisterStateForBranch(RegisterTracker registerTracker, string mnemonic, bool branchTaken)
        {
            var clone = registerTracker?.Clone() ?? new RegisterTracker();

            if (!TryCalculateBranchConstraint(clone, mnemonic, branchTaken, out string reg, out int min, out int max))
                return clone;

            clone.SetRegisterRange(reg, (byte)min, (byte)max, RegisterValueDistribution.UniformDiscreteRange);
            return clone;
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

            // Разрешаем схлопывание условного перехода только для координатных сравнений.
            // Это сохраняет фикс для веток по X/Y и не ломает внутренние циклы объектов.
            string flagsReg = registerTracker.LastFlagsRegister?.ToUpperInvariant();
            if (registerTracker.LastFlagsOrigin != RegisterTracker.FlagsOriginKind.CompareImmediate ||
                (flagsReg != "BL" && flagsReg != "BH"))
            {
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
                if (registerTracker.TryGetRegisterRange("AL", out var alRange))
                {
                    if (TryParseImmediate8(insn, out byte imm))
                    {
                        byte newMin = (byte)(alRange.Min + imm);
                        byte newMax = (byte)(alRange.Max + imm);
                        registerTracker.SetRegisterRange("AL", newMin, newMax);
                        registerTracker.MaterializePendingExternalCallResult("AX");
                        registerTracker.MarkRegisterAsExternallyDerived("AX");

                        if (debugMode)
                            AnalysisDebug.WriteLine($"        Сдвигаем диапазон AL: {alRange.Min}-{alRange.Max} -> {newMin}-{newMax}");
                    }
                }
                else if (registerTracker.HasPendingExternalCallResult("AX") || registerTracker.IsRegisterExternallyDerived("AX"))
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

            // Сохраняем изменения в эмулируемую память (runtime state)
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA2)
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
                            ProbabilityNumerator = 1,
                            ProbabilityDenominator = 1,
                            CallDepth = callDepth,
                            PendingReturnAddresses = pendingReturnAddresses == null ? new List<uint>() : new List<uint>(pendingReturnAddresses),
                            EmulatedMemory8 = splitMemory
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
                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Не сохраняем точное значение AL в эмулируемую память [0x{memAddr:X4}], так как AL имеет диапазон {alRange.Min}-{alRange.Max}");
                }
                else if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    ApplyTrackedByteWrite(memAddr, alValue, result, targetX, targetY, insn, debugMode, "MOV [moffs8], AL");
                }
            }
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 &&
                     instructionBytes[1] == 0x06)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                byte immValue = instructionBytes[4];
                ApplyTrackedByteWrite(memAddr, immValue, result, targetX, targetY, insn, debugMode, "MOV byte ptr [disp16], imm8");
            }
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x06)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                if (TryGetReg8ValueFromModRmRegField(instructionBytes[1], registerTracker, out byte regValue, out string regName))
                {
                    ApplyTrackedByteWrite(memAddr, regValue, result, targetX, targetY, insn, debugMode, $"MOV byte ptr [disp16], {regName}");
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
                if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte value))
                {
                    registerTracker.TrackPartialRegisterOperation(
                        "AX",
                        "AL",
                        value,
                        address,
                        $"MOV AL, [0x{memAddr:X4}]"
                    );

                    if (debugMode)
                        AnalysisDebug.WriteLine($"        Загрузили AL из {( _emulatedMemory8.ContainsKey(memAddr) ? "эмулируемой памяти" : "файла")} [0x{memAddr:X4}] = 0x{value:X2}");
                }
                else if (debugMode)
                {
                    AnalysisDebug.WriteLine($"        Не удалось загрузить AL из [0x{memAddr:X4}] - адрес вне диапазона файла и эмулируемой памяти");
                }
            }
            // MOV reg8, [BP + disp16] / [BP + SI + disp16] / [BP + DI + disp16]
            else if (instructionBytes.Length >= 4 && instructionBytes[0] == 0x8A)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                byte reg = (byte)((modRm >> 3) & 0x07);
                byte rm = (byte)(modRm & 0x07);

                if ((mod == 0x02 || (mod == 0x00 && rm == 0x06)) && instructionBytes.Length >= 4)
                {
                    short disp = (short)BitConverter.ToUInt16(instructionBytes, 2);
                    ushort? baseAddr = null;

                    switch (rm)
                    {
                        case 0x02: // [BP + SI]
                            if (registerTracker.TryGetRegisterValue("BP", out ushort bp2) && registerTracker.TryGetRegisterValue("SI", out ushort si))
                                baseAddr = (ushort)(bp2 + si);
                            break;
                        case 0x03: // [BP + DI]
                            if (registerTracker.TryGetRegisterValue("BP", out ushort bp3) && registerTracker.TryGetRegisterValue("DI", out ushort di))
                                baseAddr = (ushort)(bp3 + di);
                            break;
                        case 0x06: // [BP] for mod=01/10, [disp16] for mod=00
                            if (mod == 0x00)
                                baseAddr = 0;
                            else if (registerTracker.TryGetRegisterValue("BP", out ushort bp6))
                                baseAddr = bp6;
                            break;
                    }

                    if (baseAddr.HasValue)
                    {
                        ushort memAddr = (ushort)(baseAddr.Value + disp);
                        if (TryResolveTrackedByteValue(br, memAddr, result, targetX, targetY, out byte value))
                        {
                            string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                            if (reg < regNames8.Length)
                            {
                                string regName = regNames8[reg];
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
                                    registerTracker.TrackPartialRegisterOperation(
                                        fullReg,
                                        regName,
                                        value,
                                        address,
                                        $"MOV {regName}, byte ptr [0x{memAddr:X4}]"
                                    );

                                    if (debugMode)
                                        AnalysisDebug.WriteLine($"        Загрузили {regName} из {( _emulatedMemory8.ContainsKey(memAddr) ? "эмулируемой памяти" : "файла")} [0x{memAddr:X4}] = 0x{value:X2}");
                                }
                            }
                        }
                        else if (debugMode)
                        {
                            AnalysisDebug.WriteLine($"        Не удалось загрузить reg8 из [0x{memAddr:X4}] - адрес вне диапазона файла и эмулируемой памяти");
                        }
                    }
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

            // Арифметические операции
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x04)
            {
                byte immediateValue = instructionBytes[1];
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte newValue = (byte)(alValue + immediateValue);
                    registerTracker.TrackPartialRegisterOperation("AX", "AL", newValue,
                        address, $"ADD AL, 0x{immediateValue:X2}");
                    SetArithmeticFlagsForAdd8(registerTracker, alValue, immediateValue, newValue);
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x1C)
            {
                byte immediateValue = instructionBytes[1];
                int borrow = registerTracker.FlagsKnown && registerTracker.CarryFlag ? 1 : 0;
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte newValue = (byte)(alValue - immediateValue - borrow);
                    registerTracker.TrackPartialRegisterOperation("AX", "AL", newValue, address, $"SBB AL, 0x{immediateValue:X2}");
                    SetArithmeticFlagsForSub8(registerTracker, alValue, (byte)(immediateValue + borrow), newValue);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var alRange) && alRange != null)
                {
                    int newMin = Math.Max(0, alRange.Min - immediateValue - borrow);
                    int newMax = Math.Max(0, alRange.Max - immediateValue - borrow);
                    registerTracker.SetRegisterRange("AL", (byte)newMin, (byte)newMax, RegisterValueDistribution.UniformDiscreteRange);
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

            // CMP AL, imm8 (opcode 3C ib)
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x3C)
            {
                byte immediateValue = instructionBytes[1];

                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte cmpResult = (byte)(alValue - immediateValue);
                    SetArithmeticFlagsForSub8(registerTracker, alValue, immediateValue, cmpResult,
                        "AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }
                else if (registerTracker.TryGetRegisterRange("AL", out var _))
                {
                    registerTracker.SetFlagsMetadata("AL", RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                }

                registerTracker.LastCompareImmediate = immediateValue;
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
                    if (regIndex < regNames8.Length && registerTracker.TryGetByteRegisterValue(regNames8[regIndex], out byte regValue))
                    {
                        byte cmpResult = (byte)(regValue - immediateValue);
                        SetArithmeticFlagsForSub8(registerTracker, regValue, immediateValue, cmpResult, regNames8[regIndex], RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                    }
                    else if (regIndex < regNames8.Length && registerTracker.TryGetRegisterRange(regNames8[regIndex], out var _))
                    {
                        registerTracker.SetFlagsMetadata(regNames8[regIndex], RegisterTracker.FlagsOriginKind.CompareImmediate, address);
                        registerTracker.LastCompareImmediate = immediateValue;
                    }
                }
            }

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0xFE &&
                instructionBytes[1] == 0xC3)
            {
                if (registerTracker.TryGetByteRegisterValue("BL", out byte blValue))
                {
                    byte newValue = (byte)(blValue + 1);
                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                        address, "INC BL");
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
                        long fileOffset1 = table1Addr - _config.TextBaseAddr;
                        long fileOffset2 = table2Addr - _config.TextBaseAddr;

                        long originalPos = br.BaseStream.Position;

                        if (fileOffset1 >= 0 && fileOffset1 < br.BaseStream.Length)
                        {
                            br.BaseStream.Position = fileOffset1;
                            iterVal1 = br.ReadByte();
                            readSuccess1 = true;
                        }

                        if (fileOffset2 >= 0 && fileOffset2 + 1 < br.BaseStream.Length)
                        {
                            br.BaseStream.Position = fileOffset2;
                            byte lowByte = br.ReadByte();
                            byte highByte = br.ReadByte();
                            iterVal2 = lowByte;
                            readSuccess2 = true;

                            AnalysisDebug.WriteLine($"        ЧТЕНИЕ ИЗ ТАБЛИЦЫ ДЛЯ ИТЕРАЦИИ {iteration}:");
                            AnalysisDebug.WriteLine($"          [{table1Addr:X4}] = 0x{iterVal1:X2}");
                            AnalysisDebug.WriteLine($"          [{table2Addr:X4}] = 0x{iterVal2:X2} (младший байт из 0x{highByte:X2}{lowByte:X2})");
                        }

                        br.BaseStream.Position = originalPos;
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

        private bool TryReadOverlayByte(BinaryReader br, ushort memAddr, out byte value)
        {
            value = 0;

            if (br == null)
                return false;

            long? fileOffset = null;

            long textBaseOffset = memAddr - _config.TextBaseAddr;
            if (textBaseOffset >= 0 && textBaseOffset < br.BaseStream.Length)
                fileOffset = textBaseOffset;
            else if (memAddr >= 0xC972)
            {
                long overlayMappedOffset = (long)_config.StartAddress + (memAddr - 0xC972);
                if (overlayMappedOffset >= 0 && overlayMappedOffset < br.BaseStream.Length)
                    fileOffset = overlayMappedOffset;
            }

            if (!fileOffset.HasValue)
                return false;

            long originalPos = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset.Value;
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

            if (debugMode)
                AnalysisDebug.WriteLine($"        {sourceDescription}: записали 0x{value:X2} в эмулируемую память [0x{memAddr:X4}]");

            if (result == null)
                return;

            if (memAddr == 0x3C38)
            {
                result.TeleportTargetX = value;
                if (!result.TeleportTargetY.HasValue)
                    result.TeleportTargetY = targetY;
                result.HasSignificantCode = true;
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Обнаружен телепорт по X: новая координата X = {value}, Y = {result.TeleportTargetY} (инструкция 0x{insn.Address:X4})");
            }
            else if (memAddr == 0x3C39)
            {
                result.TeleportTargetY = value;
                if (!result.TeleportTargetX.HasValue)
                    result.TeleportTargetX = targetX;
                result.HasSignificantCode = true;
                if (debugMode)
                    AnalysisDebug.WriteLine($"        Обнаружен телепорт по Y: новая координата X = {result.TeleportTargetX}, Y = {value} (инструкция 0x{insn.Address:X4})");
            }
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
                                         result.HasTeleportTarget;
        }
    }
}