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
            int depth, int callDepth, OvrObject debugObject, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges = null)
        {
            processedBackEdges ??= new HashSet<(uint From, uint To)>();
            bool debugMode = debugObject != null || (pathId > 0 && debugObject?.X == 8 && debugObject?.Y == 0);

            if (depth > MAX_DEPTH || callDepth > MAX_CALL_DEPTH)
            {
                if (debugMode)
                    Debug.WriteLine($"      Достигнут лимит глубины ({depth}/{callDepth}), прекращаем анализ");
                return new PathAnalysisResult { IsTerminated = false };
            }

            var result = new PathAnalysisResult();
            long fileLength = br.BaseStream.Length;
            uint currentAddress = startAddress;
            int instructionCount = 0;
            var visitedInThisPath = new HashSet<uint>();

            // Для отслеживания косвенных текстов
            byte? lastAlValue = null;
            ushort? lastBpValue = null;
            uint lastTextAddress = 0;
            var foundTextsInThisPath = new HashSet<string>();

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                while (currentAddress < fileLength && instructionCount < MAX_INSTRUCTIONS_PER_PATH)
                {
                    if (visitedInThisPath.Contains(currentAddress))
                    {
                        if (debugMode)
                            Debug.WriteLine($"      Обнаружен цикл по адресу 0x{currentAddress:X4}");

                        if (IsTableLoadLoop(br, currentAddress, registerTracker))
                        {
                            if (debugMode)
                                Debug.WriteLine($"      Обнаружен цикл загрузки из таблиц");
                            result.HasPartialBattlePattern = true;
                            result.IsInLoop = true;
                            result.LoopStartAddress = currentAddress;
                        }

                        result.IsTerminated = true;
                        return result;
                    }
                    visitedInThisPath.Add(currentAddress);

                    if (!TryDisassembleNext(br, currentAddress, out X86Instruction insn))
                    {
                        if (debugMode)
                            Debug.WriteLine($"      Не удалось дизассемблировать по адресу 0x{currentAddress:X4}, пропускаем байт");
                        currentAddress++;
                        continue;
                    }

                    instructionCount++;
                    string mnemonicUpper = insn.Mnemonic?.ToUpper() ?? "";
                    uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                    if (debugMode)
                        Debug.WriteLine($"      Анализ инструкции по адресу 0x{insn.Address:X4}: {insn.Mnemonic} {insn.Operand}");

                    byte[] bytes = insn.Bytes;

                    // Отслеживание MOV AL, imm8
                    if (bytes.Length >= 2 && bytes[0] == 0xB0)
                    {
                        lastAlValue = bytes[1];
                        if (debugMode)
                            Debug.WriteLine($"        Запомнили AL = 0x{lastAlValue:X2}");
                    }

                    // Отслеживание MOV BP, imm16
                    if (bytes.Length >= 3 && bytes[0] == 0xBD)
                    {
                        lastBpValue = BitConverter.ToUInt16(bytes, 1);
                        if (debugMode)
                            Debug.WriteLine($"        Запомнили BP = 0x{lastBpValue:X4}");
                    }

                    // Поиск косвенных текстов (MOV AL, imm8 + MOV BP, imm16)
                    ProcessIndirectTexts(lastAlValue, lastBpValue, br, ref lastTextAddress,
                        foundTextsInThisPath, result, insn, debugMode);

                    // Поиск прямых текстов
                    var newTexts = new HashSet<string>();
                    _instructionAnalyzer.FindTextsInInstruction(insn, br, registerTracker, depth, newTexts);
                    ProcessFoundTexts(newTexts, foundTextsInThisPath, result, callDepth, insn, debugMode);

                    // Поиск изменений статистики монстров и информации о битвах
                    _instructionAnalyzer.FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                    _instructionAnalyzer.FindMonsterBattleInfo(insn, br, registerTracker, depth, result, targetX, targetY);
                    TrackRegisterOperations(insn, br, registerTracker, depth, debugMode);

                    // Анализ частичных битв
                    if (result.PartialBattleInfo.Count > 0)
                    {
                        AnalyzePartialBattleRanges(br, result);
                    }

                    // Обработка инструкций перехода и возврата
                    var handlingResult = HandleControlFlowInstructions(insn, br, registerTracker,
                        globallyAnalyzedAddresses, depth, callDepth, debugObject, pathId, targetX, targetY,
                        processedBackEdges, result, currentAddress, nextAddress, fileLength, debugMode);

                    if (handlingResult.ShouldReturn)
                        return handlingResult.Result;

                    currentAddress = handlingResult.NextAddress;
                }
            }

            // Финальный анализ частичных битв
            if (result.PartialBattleInfo.Count > 0)
            {
                AnalyzePartialBattleRanges(br, result);
            }

            FinalizeResult(result, instructionCount, currentAddress, fileLength, debugMode);
            return result;
        }

        /// <summary>
        /// Обрабатывает косвенные тексты (формируемые из AL и BP)
        /// </summary>
        private void ProcessIndirectTexts(byte? lastAlValue, ushort? lastBpValue, BinaryReader br,
            ref uint lastTextAddress, HashSet<string> foundTextsInThisPath, PathAnalysisResult result,
            X86Instruction insn, bool debugMode)
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
                            result.FoundTexts.Add(textEntry);

                            if (result.FirstLocalTextAddress == uint.MaxValue)
                            {
                                result.FirstLocalTextAddress = (uint)insn.Address;
                                if (debugMode)
                                    Debug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
                            }

                            if (debugMode)
                                Debug.WriteLine($"        Найден косвенный текст по адресу 0x{combinedAddr:X4}: {text}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Обрабатывает найденные прямые тексты
        /// </summary>
        private void ProcessFoundTexts(HashSet<string> newTexts, HashSet<string> foundTextsInThisPath,
            PathAnalysisResult result, int callDepth, X86Instruction insn, bool debugMode)
        {
            foreach (var text in newTexts)
            {
                if (!foundTextsInThisPath.Contains(text))
                {
                    foundTextsInThisPath.Add(text);

                    if (callDepth > 0)
                    {
                        result.ContextTexts.Add(text);
                        if (debugMode)
                            Debug.WriteLine($"        Найден контекстный текст (из CALL): {text}");
                    }
                    else
                    {
                        result.FoundTexts.Add(text);

                        if (result.FirstLocalTextAddress == uint.MaxValue)
                        {
                            result.FirstLocalTextAddress = (uint)insn.Address;
                            if (debugMode)
                                Debug.WriteLine($"        ЗАПОМИНАЕМ адрес первого локального текста: 0x{result.FirstLocalTextAddress:X4}");
                        }

                        if (debugMode)
                            Debug.WriteLine($"        Найден прямой текст: {text}");
                    }
                }
            }
        }

        /// <summary>
        /// Обрабатывает инструкции управления потоком
        /// </summary>
        private ControlFlowResult HandleControlFlowInstructions(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, OvrObject debugObject, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result, uint currentAddress, uint nextAddress, long fileLength, bool debugMode)
        {
            string mnemonicUpper = insn.Mnemonic?.ToUpper() ?? "";

            // RET - конец пути
            if (IsReturnInstruction(mnemonicUpper))
            {
                if (debugMode)
                    Debug.WriteLine($"      RET на 0x{insn.Address:X4} - конец пути");
                result.IsTerminated = true;
                result.HasSignificantCode = true;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            // JMP - безусловный переход
            if (mnemonicUpper == "JMP" || mnemonicUpper == "JMPF" || mnemonicUpper == "JMPL" ||
                mnemonicUpper == "JMPE" || mnemonicUpper == "JMPI")
            {
                return HandleJmp(insn, br, fileLength, debugMode, result, currentAddress);
            }

            // JMP по опкоду 0xE9
            if (insn.Bytes.Length >= 1 && insn.Bytes[0] == 0xE9)
            {
                return HandleJmpOpcodeE9(insn, br, fileLength, debugMode, result, currentAddress);
            }

            // Короткий JMP
            if (insn.Bytes.Length >= 2 && insn.Bytes[0] == 0xEB)
            {
                return HandleShortJmp(insn, br, fileLength, debugMode, result, currentAddress);
            }

            // CALL
            if (mnemonicUpper.StartsWith("CALL"))
            {
                return HandleCall(insn, br, registerTracker, globallyAnalyzedAddresses, depth, callDepth,
                    debugObject, pathId, targetX, targetY, processedBackEdges, result, nextAddress, debugMode);
            }

            // УСЛОВНЫЕ ПЕРЕХОДЫ
            if (IsConditionalJump(insn, out uint condJumpTarget))
            {
                return HandleConditionalJump(insn, condJumpTarget, nextAddress, fileLength,
                    debugMode, processedBackEdges, result, currentAddress);
            }

            // Обычная инструкция - продолжаем линейно
            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }

        /// <summary>
        /// Результат обработки инструкции управления потоком
        /// </summary>
        private class ControlFlowResult
        {
            public bool ShouldReturn { get; set; }
            public PathAnalysisResult Result { get; set; }
            public uint NextAddress { get; set; }
        }

        /// <summary>
        /// Обрабатывает инструкцию JMP
        /// </summary>
        private ControlFlowResult HandleJmp(X86Instruction insn, BinaryReader br, long fileLength,
            bool debugMode, PathAnalysisResult result, uint currentAddress)
        {
            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

            if (jumpTarget >= fileLength)
            {
                if (debugMode)
                    Debug.WriteLine($"      JMP за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                result.IsTerminated = true;
                result.HasSignificantCode = true;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            if (jumpTarget != 0 && jumpTarget < fileLength)
            {
                if (debugMode)
                    Debug.WriteLine($"      JMP к 0x{jumpTarget:X4}");
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

                if (jumpTarget >= fileLength)
                {
                    if (debugMode)
                        Debug.WriteLine($"      JMP (по опкоду 0xE9) за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                    result.IsTerminated = true;
                    return new ControlFlowResult { ShouldReturn = true, Result = result };
                }

                if (jumpTarget < fileLength && jumpTarget != 0)
                {
                    if (debugMode)
                        Debug.WriteLine($"      JMP (по опкоду 0xE9) к 0x{jumpTarget:X4}");
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
                    if (debugMode)
                        Debug.WriteLine($"      SHORT JMP к 0x{jumpTarget:X4}");
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
            int depth, int callDepth, OvrObject debugObject, int pathId, byte targetX, byte targetY,
            HashSet<(uint From, uint To)> processedBackEdges,
            PathAnalysisResult result, uint nextAddress, bool debugMode)
        {
            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);

            if (callTarget < br.BaseStream.Length && callTarget != 0)
            {
                if (debugMode)
                    Debug.WriteLine($"      CALL 0x{callTarget:X4} (возврат в 0x{nextAddress:X4})");

                var subroutineTracker = registerTracker.Clone();
                var subroutineResult = ExecuteCodeAtAddress(br, callTarget, subroutineTracker,
                    globallyAnalyzedAddresses, depth + 1, callDepth + 1, debugObject, pathId, targetX, targetY,
                    processedBackEdges);

                // Добавляем результаты из подпрограммы
                var foundTextsInThisPath = new HashSet<string>();
                foreach (var text in subroutineResult.FoundTexts)
                    if (!foundTextsInThisPath.Contains(text))
                    {
                        foundTextsInThisPath.Add(text);
                        result.ContextTexts.Add(text);
                    }
                foreach (var text in subroutineResult.ContextTexts)
                    if (!foundTextsInThisPath.Contains(text))
                    {
                        foundTextsInThisPath.Add(text);
                        result.ContextTexts.Add(text);
                    }

                if (subroutineResult.MonsterPower.HasValue && !result.MonsterPower.HasValue)
                    result.MonsterPower = subroutineResult.MonsterPower;

                if (subroutineResult.MonsterLevel.HasValue && !result.MonsterLevel.HasValue)
                    result.MonsterLevel = subroutineResult.MonsterLevel;

                foreach (var entry in subroutineResult.BattleMonsterEntries)
                    if (!result.BattleMonsterEntries.ContainsKey(entry.Key))
                        result.BattleMonsterEntries[entry.Key] = entry.Value;

                foreach (var partial in subroutineResult.PartialBattles)
                    if (!result.PartialBattles.Any(p => p.BxIndex == partial.BxIndex))
                        result.PartialBattles.Add(partial);

                foreach (var info in subroutineResult.PartialBattleInfo)
                    if (!result.PartialBattleInfo.Any(i => i.BxIndex == info.BxIndex && i.DestAddr == info.DestAddr))
                        result.PartialBattleInfo.Add(info);

                result.HasPartialBattlePattern = result.HasPartialBattlePattern || subroutineResult.HasPartialBattlePattern;
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
        }

        /// <summary>
        /// Обрабатывает условный переход
        /// </summary>
        private ControlFlowResult HandleConditionalJump(X86Instruction insn, uint condJumpTarget,
            uint nextAddress, long fileLength, bool debugMode,
            HashSet<(uint From, uint To)> processedBackEdges, PathAnalysisResult result, uint currentAddress)
        {
            if (condJumpTarget < fileLength && condJumpTarget != 0)
            {
                if (condJumpTarget < currentAddress)
                {
                    var backEdge = (From: currentAddress, To: condJumpTarget);

                    if (!processedBackEdges.Add(backEdge))
                    {
                        if (debugMode)
                            Debug.WriteLine($"      Повторный обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, продолжаем линейно");

                        return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
                    }

                    if (debugMode)
                        Debug.WriteLine($"      Первый обратный переход: 0x{currentAddress:X4} -> 0x{condJumpTarget:X4}, разрешаем одну развилку");
                }
                // Добавляем целевой адрес перехода как альтернативный путь
                var altPath = new AlternativePath
                {
                    Address = (uint)insn.Address,
                    TargetAddress = condJumpTarget,
                    Condition = $"{insn.Mnemonic} {insn.Operand}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1
                };

                if (!result.AlternativePaths.Any(p => p.Address == altPath.Address &&
                                                       p.TargetAddress == altPath.TargetAddress))
                {
                    result.AlternativePaths.Add(altPath);
                    if (debugMode)
                        Debug.WriteLine($"      Найден альтернативный путь: 0x{insn.Address:X4} -> 0x{condJumpTarget:X4}");
                }

                // Добавляем линейное продолжение как отдельный путь
                var linearPath = new AlternativePath
                {
                    Address = (uint)insn.Address,
                    TargetAddress = nextAddress,
                    Condition = $"LINEAR after {insn.Mnemonic}",
                    Analyzed = false,
                    PathNumber = result.AlternativePaths.Count + 1
                };

                if (!result.AlternativePaths.Any(p => p.Address == linearPath.Address &&
                                                       p.TargetAddress == linearPath.TargetAddress))
                {
                    result.AlternativePaths.Add(linearPath);
                    if (debugMode)
                        Debug.WriteLine($"      Добавлен линейный путь: 0x{insn.Address:X4} -> 0x{nextAddress:X4}");
                }

                // Завершаем текущий путь - не продолжаем выполнение
                if (debugMode)
                    Debug.WriteLine($"      Останавливаем анализ текущего пути после условного перехода");

                result.IsTerminated = true;
                result.HasSignificantCode = result.FoundTexts.Count > 0 ||
                                             result.ContextTexts.Count > 0 ||
                                             result.MonsterPower.HasValue ||
                                             result.MonsterLevel.HasValue ||
                                             result.BattleMonsterEntries.Count > 0 ||
                                             result.PartialBattles.Count > 0 ||
                                             result.HasPartialBattlePattern;
                return new ControlFlowResult { ShouldReturn = true, Result = result };
            }

            return new ControlFlowResult { ShouldReturn = false, NextAddress = nextAddress };
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
                long fileOffset = textAddress - _config.TextBaseAddr;

                if (fileOffset < 0 || fileOffset >= br.BaseStream.Length)
                {
                    return $"Cannot locate text (offset: 0x{fileOffset:X})";
                }

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

        /// <summary>
        /// Отслеживает операции с регистрами
        /// </summary>
        private void TrackRegisterOperations(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, bool debugMode)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

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

            Debug.WriteLine($"    АНАЛИЗ ЧАСТИЧНЫХ БИТВ: найдено {result.PartialBattleInfo.Count} записей");
            Debug.WriteLine($"    Состояние цикла: IsInLoop={result.IsInLoop}, LoopStartAddress=0x{result.LoopStartAddress:X4}, LoopIterationCount={result.LoopIterationCount}");

            // Группируем записи по BX индексу (индекс в массиве, куда сохраняются значения)
            var groupedByBx = result.PartialBattleInfo.GroupBy(p => p.BxIndex).OrderBy(g => g.Key);

            foreach (var group in groupedByBx)
            {
                int saveBxIndex = group.Key;
                var entries = group.ToList();

                Debug.WriteLine($"      Группа BX(сохранения)={saveBxIndex}: {entries.Count} записей");

                // Находим записи для 3C58 и 3C29
                var entry58 = entries.FirstOrDefault(e => e.DestAddr == 0x3C58);
                var entry29 = entries.FirstOrDefault(e => e.DestAddr == 0x3C29);

                if (entry58 == null || entry29 == null)
                {
                    Debug.WriteLine($"        -> НЕТ ПОЛНОЙ ПАРЫ ЗАПИСЕЙ для создания частичной битвы");
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
                        Debug.WriteLine($"        -> БИТВА УЖЕ ПОЛНОСТЬЮ ОПРЕДЕЛЕНА: val1={existing.val1:X2}, val2={existing.val2:X2}, пропускаем создание частичной");
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
                        Debug.WriteLine($"        Обнаружен цикл с {iterationCount} итерациями");
                    }
                    else
                    {
                        // Используем максимальный BX индекс для определения количества итераций
                        int maxSaveBx = groupedByBx.Max(g => g.Key);
                        if (maxSaveBx > 0)
                        {
                            // Количество итераций = максимальный BX индекс + 1
                            iterationCount = maxSaveBx + 1;
                            Debug.WriteLine($"        Количество итераций определено по BX сохранения: {iterationCount}");
                        }
                        else
                        {
                            // Если не можем определить, используем значение по умолчанию
                            iterationCount = 8;
                            Debug.WriteLine($"        Количество итераций неизвестно, предполагаем {iterationCount}");
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
                            Debug.WriteLine($"        -> БИТВА ДЛЯ BX={currentBxIndex} УЖЕ ОПРЕДЕЛЕНА, пропускаем");
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

                            Debug.WriteLine($"        ЧТЕНИЕ ИЗ ТАБЛИЦЫ ДЛЯ ИТЕРАЦИИ {iteration}:");
                            Debug.WriteLine($"          [{table1Addr:X4}] = 0x{iterVal1:X2}");
                            Debug.WriteLine($"          [{table2Addr:X4}] = 0x{iterVal2:X2} (младший байт из 0x{highByte:X2}{lowByte:X2})");
                        }

                        br.BaseStream.Position = originalPos;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"        ОШИБКА ЧТЕНИЯ ИЗ ТАБЛИЦЫ: {ex.Message}");
                    }

                    if (!readSuccess1 || !readSuccess2)
                    {
                        Debug.WriteLine($"        НЕ УДАЛОСЬ ПРОЧИТАТЬ ЗНАЧЕНИЯ ДЛЯ ИТЕРАЦИИ {iteration}, пропускаем");
                        continue;
                    }

                    byte rangeStart1 = iterVal1;
                    byte rangeEnd1 = iterVal1;
                    byte rangeStart2 = iterVal2;
                    byte rangeEnd2 = iterVal2;

                    if (rangeStart1 > rangeEnd1 || rangeStart2 > rangeEnd2)
                    {
                        Debug.WriteLine($"        -> НЕКОРРЕКТНЫЕ ДИАПАЗОНЫ ДЛЯ ИТЕРАЦИИ {iteration}: [{rangeStart1:X2}-{rangeEnd1:X2}] [{rangeStart2:X2}-{rangeEnd2:X2}]");
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
                        Debug.WriteLine($"        -> ЗНАЧЕНИЯ СОВПАДАЮТ С УЖЕ ОПРЕДЕЛЁННОЙ БИТВОЙ, пропускаем");
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
                        Debug.WriteLine($"        -> СОЗДАНА ЧАСТИЧНАЯ БИТВА: BX(сохранения)={currentBxIndex}, BX(загрузки)={loadBx}, {combinations} комбинация (итерация {iteration})");
                    }
                    else
                    {
                        Debug.WriteLine($"        -> ЧАСТИЧНАЯ БИТВА ДЛЯ BX={currentBxIndex} УЖЕ СУЩЕСТВУЕТ, пропускаем");
                    }
                }
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
                    Debug.WriteLine($"      Достигнут лимит инструкций ({MAX_INSTRUCTIONS_PER_PATH}) - путь прерван");
                result.IsTerminated = false;
            }
            else if (currentAddress >= fileLength)
            {
                if (debugMode)
                    Debug.WriteLine($"      Достигнут конец оверлея - конец пути");
                result.IsTerminated = true;
            }

            result.HasSignificantCode = result.FoundTexts.Count > 0 ||
                                         result.ContextTexts.Count > 0 ||
                                         result.MonsterPower.HasValue ||
                                         result.MonsterLevel.HasValue ||
                                         result.BattleMonsterEntries.Count > 0 ||
                                         result.PartialBattles.Count > 0 ||
                                         result.HasPartialBattlePattern;
        }
    }
}