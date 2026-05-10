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
using System.IO;
using System.Linq;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    /// <summary>
    /// Анализирует макросы и альтернативные пути в оверлее
    /// </summary>
    public class MacroAnalyzer
    {
        private readonly OvrFileConfig _config;

        public MacroAnalyzer(OvrFileConfig config)
        {
            _config = config;
        }

        public List<CoordinateComparison> FindAllCoordinateComparisons(BinaryReader br, List<X86Instruction> allInstructions)
        {
            var comparisons = new List<CoordinateComparison>();

            for (int i = 0; i < allInstructions.Count; i++)
            {
                var insn = allInstructions[i];

                if (IsCompareWithImmediate(insn, out byte immValue, out string reg))
                {
                    ushort? sourceAddr = FindMemorySourceForRegister(allInstructions, i, reg);
                    if (!sourceAddr.HasValue) continue;

                    CoordType coordType = GetCoordType(sourceAddr.Value);
                    if (coordType == CoordType.Unknown) continue;

                    var comparisonsForInsn = AnalyzeComparisonsChain(allInstructions, i,
                        insn, immValue, reg, sourceAddr.Value, coordType);
                    comparisons.AddRange(comparisonsForInsn);
                }
            }

            return comparisons;
        }

        /// <summary>
        /// Анализирует цепочку сравнений и переходов для конкретной инструкции сравнения
        /// </summary>
        private List<CoordinateComparison> AnalyzeComparisonsChain(List<X86Instruction> allInstructions,
            int startIndex, X86Instruction firstInsn, byte firstImmValue, string reg,
            ushort sourceAddr, CoordType coordType)
        {
            var result = new List<CoordinateComparison>();
            uint rootAddress = DetermineMacroEntryAddress(allInstructions, startIndex, reg);

            int j = startIndex + 1;
            byte? lastCompareValue = null;
            string lastJumpType = null;
            uint? lastJumpTarget = null;

            // Получаем информацию о предшествующем условии (если есть)
            var precedingInfo = FindPrecedingCondition(allInstructions, startIndex, reg);

            // Проходим по всем последующим условным переходам
            while (j < allInstructions.Count)
            {
                var nextInsn = allInstructions[j];

                // Проверяем, является ли инструкция условным переходом
                if (!IsConditionalJump(nextInsn, out uint jumpTarget))
                    break;

                // Сохраняем информацию о последнем условном переходе
                lastCompareValue = firstImmValue;
                lastJumpType = nextInsn.Mnemonic.ToUpper();
                lastJumpTarget = jumpTarget;

                // Добавляем условный переход в результаты
                result.Add(new CoordinateComparison
                {
                    CompareAddress = (uint)firstInsn.Address,
                    MemAddr = sourceAddr,
                    CoordType = coordType,
                    Value = firstImmValue,
                    JumpTarget = jumpTarget,
                    JumpType = nextInsn.Mnemonic.ToUpper(),
                    IsLinear = false,
                    MacroEntryAddress = rootAddress,
                    HasPrecedingCondition = precedingInfo.hasPreceding,
                    PrecedingCoordType = precedingInfo.coordType,
                    PrecedingValue = precedingInfo.value,
                    PrecedingJumpType = precedingInfo.jumpType,
                    PrecedingViaJumpTarget = precedingInfo.viaJumpTarget
                });

                AnalysisDebug.WriteLine($"  Найдено сравнение: 0x{firstInsn.Address:X4} [{sourceAddr:X4}]={firstImmValue} -> 0x{jumpTarget:X4} ({nextInsn.Mnemonic}) [{coordType}]");

                j++;
            }

            // Проверяем наличие линейного выполнения после условных переходов
            if (j < allInstructions.Count && lastCompareValue.HasValue && lastJumpType != null)
            {
                var nextInsn = allInstructions[j];

                // Если следующая инструкция - снова сравнение, пропускаем линейное выполнение
                if (IsCompareWithImmediate(nextInsn, out _, out _))
                {
                    AnalysisDebug.WriteLine($"  Пропуск линейного выполнения: следующая инструкция - сравнение по адресу 0x{nextInsn.Address:X4}");
                    return result;
                }

                // Рассчитываем диапазон для линейного выполнения
                var linearRange = CalculateLinearRange(lastCompareValue.Value, lastJumpType, precedingInfo);

                if (linearRange.HasValue)
                {
                    result.Add(new CoordinateComparison
                    {
                        CompareAddress = (uint)firstInsn.Address,
                        MemAddr = sourceAddr,
                        CoordType = coordType,
                        Value = lastCompareValue.Value,
                        JumpTarget = (uint)allInstructions[j].Address,
                        JumpType = "LINEAR",
                        IsLinear = true,
                        LinearStart = linearRange.Value.start,
                        LinearEnd = linearRange.Value.end,
                        MacroEntryAddress = rootAddress,
                        HasPrecedingCondition = precedingInfo.hasPreceding,
                        PrecedingCoordType = precedingInfo.coordType,
                        PrecedingValue = precedingInfo.value,
                        PrecedingJumpType = precedingInfo.jumpType,
                        PrecedingViaJumpTarget = precedingInfo.viaJumpTarget
                    });

                    AnalysisDebug.WriteLine($"  Найдено линейное выполнение: 0x{firstInsn.Address:X4} [{sourceAddr:X4}]={lastCompareValue} -> 0x{allInstructions[j].Address:X4} для значений [{linearRange.Value.start}-{linearRange.Value.end}] [{coordType}]");
                }
            }

            return result;
        }

        private (bool hasPreceding, byte value, string jumpType, bool viaJumpTarget, CoordType coordType) FindPrecedingCondition(
            List<X86Instruction> instructions, int currentIndex, string reg)
        {
            uint currentAddress = (uint)instructions[currentIndex].Address;

            for (int k = currentIndex - 1; k >= Math.Max(0, currentIndex - 10); k--)
            {
                var prevInsn = instructions[k];
                if (IsCompareWithImmediate(prevInsn, out byte prevValue, out string prevReg) && prevReg == reg)
                {
                    if (k + 1 < instructions.Count)
                    {
                        var nextAfterPrev = instructions[k + 1];
                        if (IsConditionalJump(nextAfterPrev, out uint jumpTarget))
                        {
                            uint fallthrough = (uint)(nextAfterPrev.Address + nextAfterPrev.Bytes.Length);
                            bool viaJumpTarget =
                                jumpTarget > fallthrough &&
                                currentAddress >= jumpTarget;
                            bool viaFallthrough =
                                currentAddress >= fallthrough &&
                                (!viaJumpTarget || currentAddress < jumpTarget);

                            if (viaJumpTarget || viaFallthrough)
                            {
                                ushort? prevSourceAddr = FindMemorySourceForRegister(instructions, k, reg);
                                CoordType prevCoordType = prevSourceAddr.HasValue
                                    ? GetCoordType(prevSourceAddr.Value)
                                    : CoordType.Unknown;
                                return (true, prevValue, nextAfterPrev.Mnemonic.ToUpper(), viaJumpTarget, prevCoordType);
                            }
                        }
                    }
                    break;
                }
            }
            return (false, 0, null, false, CoordType.Unknown);
        }

        private uint DetermineMacroEntryAddress(List<X86Instruction> instructions, int compareIndex, string compareRegister)
        {
            if (instructions == null || compareIndex < 0 || compareIndex >= instructions.Count)
                return 0;

            int rootIndex = ExpandBackwardToBasicBlockStart(instructions, compareIndex);
            bool changed;

            do
            {
                changed = false;
                int branchIndex = FindDominatingGuardJumpIndex(instructions, rootIndex);
                if (branchIndex < 0)
                    break;

                int flagsIndex = FindFlagsProducerIndex(instructions, branchIndex);
                if (flagsIndex < 0)
                    break;

                int expandedIndex = ExpandBackwardToRegisterPreparation(instructions, flagsIndex);
                expandedIndex = ExpandBackwardToBasicBlockStart(instructions, expandedIndex);

                if (expandedIndex < rootIndex)
                {
                    rootIndex = expandedIndex;
                    changed = true;
                }
            }
            while (changed);

            uint entryAddress = (uint)instructions[rootIndex].Address;
            if (entryAddress != (uint)instructions[compareIndex].Address)
            {
                AnalysisDebug.WriteLine($"  Root cause entry: 0x{entryAddress:X4} for compare 0x{instructions[compareIndex].Address:X4} ({compareRegister})");
            }

            return entryAddress;
        }

        private int FindDominatingGuardJumpIndex(List<X86Instruction> instructions, int rootIndex)
        {
            if (instructions == null || rootIndex <= 0 || rootIndex >= instructions.Count)
                return -1;

            uint rootAddress = (uint)instructions[rootIndex].Address;
            int lookBackStart = Math.Max(0, rootIndex - 48);
            for (int i = rootIndex - 1; i >= lookBackStart; i--)
            {
                if (!IsConditionalJump(instructions[i], out uint jumpTarget))
                    continue;

                uint fallthrough = (uint)(instructions[i].Address + instructions[i].Bytes.Length);

                // Доминирующий guard может вести в текущий блок как по fallthrough,
                // так и явным переходом в rootAddress. Второй вариант важен для
                // ранних проверок внешних флагов, которые прыгают прямо в начало
                // координатного decision-tree.
                if (fallthrough == rootAddress || jumpTarget == rootAddress)
                    return i;
            }

            return -1;
        }

        private int FindFlagsProducerIndex(List<X86Instruction> instructions, int branchIndex)
        {
            for (int i = branchIndex - 1; i >= Math.Max(0, branchIndex - 8); i--)
            {
                if (IsFlagProducer(instructions[i]))
                    return i;

                string mnemonic = instructions[i].Mnemonic?.ToUpperInvariant() ?? string.Empty;
                if (mnemonic == "JMP" || mnemonic.StartsWith("J", StringComparison.Ordinal) ||
                    mnemonic.StartsWith("RET", StringComparison.Ordinal) || mnemonic == "CALL")
                {
                    break;
                }
            }

            return -1;
        }

        private int ExpandBackwardToRegisterPreparation(List<X86Instruction> instructions, int flagsIndex)
        {
            if (flagsIndex <= 0)
                return flagsIndex;

            string comparedRegister = TryGetFlagProducerRegister(instructions[flagsIndex]);
            if (string.IsNullOrEmpty(comparedRegister))
                return flagsIndex;

            int candidate = flagsIndex;
            for (int i = flagsIndex - 1; i >= Math.Max(0, flagsIndex - 8); i--)
            {
                if (IsDirectRegisterPreparation(instructions[i], comparedRegister))
                {
                    candidate = i;
                    continue;
                }

                string mnemonic = instructions[i].Mnemonic?.ToUpperInvariant() ?? string.Empty;
                if (mnemonic == "NOP")
                    continue;

                if (mnemonic == "JMP" || mnemonic.StartsWith("J", StringComparison.Ordinal) ||
                    mnemonic.StartsWith("RET", StringComparison.Ordinal) || mnemonic == "CALL")
                {
                    break;
                }

                // Разрешаем немного подняться над подготовкой регистра, чтобы захватить
                // ранние guard-проверки и загрузки до CMP/TEST/OR внутри того же блока.
                candidate = i;
            }

            return candidate;
        }

        private int ExpandBackwardToBasicBlockStart(List<X86Instruction> instructions, int startIndex)
        {
            if (instructions == null || startIndex <= 0 || startIndex >= instructions.Count)
                return startIndex;

            var incomingTargets = new HashSet<uint>();
            for (int i = 0; i < instructions.Count; i++)
            {
                if (IsConditionalJump(instructions[i], out uint target))
                    incomingTargets.Add(target);
                else if (IsUnconditionalJump(instructions[i], out target))
                    incomingTargets.Add(target);
            }

            int candidate = startIndex;
            for (int i = startIndex - 1; i >= Math.Max(0, startIndex - 24); i--)
            {
                var current = instructions[i];
                var next = instructions[i + 1];
                string mnemonic = current.Mnemonic?.ToUpperInvariant() ?? string.Empty;

                if (incomingTargets.Contains((uint)next.Address))
                    break;

                if (mnemonic == "JMP" || mnemonic.StartsWith("RET", StringComparison.Ordinal) || mnemonic == "CALL")
                    break;

                candidate = i;
            }

            return candidate;
        }

        private bool IsFlagProducer(X86Instruction insn)
        {
            byte[] bytes = insn?.Bytes;
            if (bytes == null || bytes.Length == 0)
                return false;

            string mnemonic = insn.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            return mnemonic == "CMP" || mnemonic == "TEST" || mnemonic == "OR" || mnemonic == "AND";
        }

        private string TryGetFlagProducerRegister(X86Instruction insn)
        {
            byte[] bytes = insn?.Bytes;
            if (bytes == null || bytes.Length == 0)
                return null;

            if (IsCompareWithImmediate(insn, out _, out string reg))
                return reg;

            if (bytes.Length >= 2 && bytes[0] == 0x0A)
            {
                return DecodeReg8FromRmField(bytes[1]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0x84)
            {
                return DecodeReg8FromRmField(bytes[1]);
            }

            return null;
        }

        private bool IsDirectRegisterPreparation(X86Instruction insn, string reg)
        {
            byte[] bytes = insn?.Bytes;
            if (bytes == null || bytes.Length == 0 || string.IsNullOrEmpty(reg))
                return false;

            switch (reg.ToUpperInvariant())
            {
                case "AL":
                    return (bytes.Length >= 3 && bytes[0] == 0xA0) ||
                           (bytes.Length >= 2 && bytes[0] == 0xB0) ||
                           (bytes.Length >= 2 && bytes[0] == 0x8A && ((bytes[1] >> 3) & 0x07) == 0);
                case "AX":
                    return (bytes.Length >= 3 && bytes[0] == 0xA1) ||
                           (bytes.Length >= 3 && bytes[0] == 0xB8) ||
                           (bytes.Length >= 2 && bytes[0] == 0x8B && ((bytes[1] >> 3) & 0x07) == 0);
                case "BL":
                    return (bytes.Length >= 2 && bytes[0] == 0xB3) ||
                           (bytes.Length >= 2 && bytes[0] == 0x8A && ((bytes[1] >> 3) & 0x07) == 3);
                case "CL":
                    return (bytes.Length >= 2 && bytes[0] == 0xB1) ||
                           (bytes.Length >= 2 && bytes[0] == 0x8A && ((bytes[1] >> 3) & 0x07) == 1);
                case "DL":
                    return (bytes.Length >= 2 && bytes[0] == 0xB2) ||
                           (bytes.Length >= 2 && bytes[0] == 0x8A && ((bytes[1] >> 3) & 0x07) == 2);
                default:
                    return false;
            }
        }

        private string DecodeReg8FromRmField(byte modRm)
        {
            string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
            int rm = modRm & 0x07;
            return rm >= 0 && rm < regNames.Length ? regNames[rm] : null;
        }

        /// <summary>
        /// Рассчитывает диапазон для линейного выполнения
        /// </summary>
        private (byte start, byte end)? CalculateLinearRange(byte compareValue, string jumpType,
            (bool hasPreceding, byte value, string jumpType, bool viaJumpTarget, CoordType coordType) preceding)
        {
            byte linearStart = 0;
            byte linearEnd = 255;

            if (jumpType == "JB" || jumpType == "JC")
            {
                linearStart = compareValue;
                linearEnd = 255;
            }
            else if (jumpType == "JAE" || jumpType == "JNB" || jumpType == "JNC")
            {
                linearStart = 0;
                linearEnd = (byte)(compareValue - 1);
            }
            else
            {
                return null; // Возвращаем null для не-линейных jumpType
            }

            if (preceding.hasPreceding)
            {
                if (preceding.viaJumpTarget)
                {
                    if (preceding.jumpType == "JB" || preceding.jumpType == "JC")
                    {
                        linearEnd = Math.Min(linearEnd, (byte)(preceding.value - 1));
                        goto linearRangeAdjusted;
                    }

                    if (preceding.jumpType == "JAE" || preceding.jumpType == "JNB" || preceding.jumpType == "JNC")
                    {
                        linearStart = Math.Max(linearStart, preceding.value);
                        goto linearRangeAdjusted;
                    }
                }

                if (preceding.jumpType == "JB" || preceding.jumpType == "JC")
                {
                    linearStart = Math.Max(linearStart, preceding.value);
                }
                else if (preceding.jumpType == "JAE" || preceding.jumpType == "JNB" || preceding.jumpType == "JNC")
                {
                    linearEnd = Math.Min(linearEnd, (byte)(preceding.value - 1));
                }
                else if (preceding.jumpType == "JE" || preceding.jumpType == "JZ")
                {
                    // Специальный случай для конкретного паттерна
                    if (preceding.value == 3 && compareValue == 9)
                    {
                        linearStart = 4;
                        linearEnd = 8;
                    }
                }
            }

            // Проверяем, что диапазон корректен
        linearRangeAdjusted:
            if (linearStart <= linearEnd)
            {
                return (linearStart, linearEnd); // Возвращаем кортеж
            }

            return null; // Возвращаем null если диапазон некорректен
        }

        private CoordType GetCoordType(ushort address)
        {
            if (address == 0x3C38)
                return CoordType.XCoord;
            if (address == 0x3C39)
                return CoordType.YCoord;
            if (address == 0x3C3A)
                return CoordType.FullCoord;
            return CoordType.Unknown;
        }

        private bool IsCompareWithImmediate(X86Instruction insn, out byte immValue, out string reg)
        {
            immValue = 0;
            reg = "";

            byte[] bytes = insn.Bytes;

            // Обработка различных форм CMP
            if (bytes.Length >= 2 && bytes[0] == 0x3C)
            {
                immValue = bytes[1];
                reg = "AL";
                return true;
            }

            if (bytes.Length >= 3 && bytes[0] == 0x80)
            {
                switch (bytes[1])
                {
                    case 0xF9: immValue = bytes[2]; reg = "CL"; return true;
                    case 0xFA: immValue = bytes[2]; reg = "DL"; return true;
                    case 0xFB: immValue = bytes[2]; reg = "BL"; return true;
                    case 0xF8: immValue = bytes[2]; reg = "AL"; return true;
                }

                // CMP reg, imm8 для AH, CH, DH, BH
                if ((bytes[1] & 0xF8) == 0xF8)
                {
                    immValue = bytes[2];
                    byte regCode = (byte)(bytes[1] & 0x07);
                    reg = regCode switch
                    {
                        4 => "AH",
                        5 => "CH",
                        6 => "DH",
                        7 => "BH",
                        _ => ""
                    };
                    return !string.IsNullOrEmpty(reg);
                }
            }

            return false;
        }

        private bool IsUnconditionalJump(X86Instruction insn, out uint target)
        {
            target = 0;
            if (insn == null)
                return false;

            string mnemonic = insn.Mnemonic?.ToUpperInvariant() ?? string.Empty;
            if (mnemonic != "JMP")
                return false;

            byte[] bytes = insn.Bytes;
            if (bytes == null || bytes.Length == 0)
                return false;

            target = GetJumpTarget(insn);
            return target != 0;
        }

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
                target = GetJumpTarget(insn);
                return target != 0;
            }
            return false;
        }

        private uint GetJumpTarget(X86Instruction insn)
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

        private ushort? FindMemorySourceForRegister(List<X86Instruction> allInstructions, int currentIndex, string reg)
        {
            int start = Math.Max(0, currentIndex - 30);

            for (int i = currentIndex - 1; i >= start; i--)
            {
                var insn = allInstructions[i];
                byte[] bytes = insn.Bytes;

                if (reg == "AL" && bytes.Length >= 3 && bytes[0] == 0xA0)
                {
                    return BitConverter.ToUInt16(bytes, 1);
                }

                if (reg == "CL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x0E)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                if (reg == "DL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x16)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                if (reg == "BL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x1E)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                // MOV reg, [mem] для 16-битных регистров
                if (bytes.Length >= 4 && bytes[0] == 0x8B)
                {
                    byte modRM = bytes[1];
                    if ((modRM & 0xC0) == 0x00) // Прямая адресация
                    {
                        ushort memAddr = BitConverter.ToUInt16(bytes, 2);
                        byte destReg = (byte)((modRM >> 3) & 0x07);
                        string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                        if (destReg < regNames16.Length)
                        {
                            string destRegName = regNames16[destReg];
                            if ((reg == "AL" && destRegName == "AX") ||
                                (reg == "CL" && destRegName == "CX") ||
                                (reg == "DL" && destRegName == "DX") ||
                                (reg == "BL" && destRegName == "BX"))
                            {
                                return memAddr;
                            }
                        }
                    }
                }

                if (IsRegisterWrite(insn, reg))
                {
                    break;
                }
            }

            return null;
        }

        private bool IsRegisterWrite(X86Instruction insn, string reg)
        {
            byte[] bytes = insn.Bytes;

            switch (reg)
            {
                case "AL": return bytes.Length >= 2 && (bytes[0] & 0xF8) == 0xB0;
                case "CL": return bytes.Length >= 2 && bytes[0] == 0xB1;
                case "DL": return bytes.Length >= 2 && bytes[0] == 0xB2;
                case "BL": return bytes.Length >= 2 && bytes[0] == 0xB3;
                case "AH": return bytes.Length >= 2 && bytes[0] == 0xB4;
                case "CH": return bytes.Length >= 2 && bytes[0] == 0xB5;
                case "DH": return bytes.Length >= 2 && bytes[0] == 0xB6;
                case "BH": return bytes.Length >= 2 && bytes[0] == 0xB7;
                default: return false;
            }
        }

        public void CollectAlternativePaths(BinaryReader br, uint startAddress,
            List<AlternativePath> alternativePaths, int objIndex)
        {
            long fileLength = br.BaseStream.Length;
            uint currentAddress = startAddress;
            int instructionsShown = 0;
            const int MAX_INSTRUCTIONS = 100;

            var processedAddresses = new Dictionary<uint, int>();

            while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS)
            {
                if (processedAddresses.ContainsKey(currentAddress))
                {
                    processedAddresses[currentAddress]++;
                    if (processedAddresses[currentAddress] > 2) break;
                }
                else
                {
                    processedAddresses[currentAddress] = 1;
                }

                var chunk = ReadCodeBlock(br, currentAddress, out var instructions);
                if (instructions == null || instructions.Length == 0) break;

                foreach (var insn in instructions)
                {
                    if (instructionsShown >= MAX_INSTRUCTIONS) break;
                    instructionsShown++;

                    string mnemonicUpper = insn.Mnemonic.ToUpper();
                    uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                    if (IsConditionalJump(insn, out uint jumpTarget))
                    {
                        if (jumpTarget != 0 && jumpTarget < fileLength)
                        {
                            var altPath = new AlternativePath
                            {
                                ObjectIndex = objIndex,
                                Address = (uint)insn.Address,
                                Condition = $"{insn.Mnemonic} {insn.Operand}",
                                TargetAddress = jumpTarget,
                                Analyzed = false
                            };

                            bool alreadyExists = alternativePaths.Any(p =>
                                p.Address == altPath.Address && p.TargetAddress == altPath.TargetAddress);

                            if (!alreadyExists)
                                alternativePaths.Add(altPath);
                        }
                    }

                    if (IsReturnInstruction(mnemonicUpper)) return;

                    if (mnemonicUpper == "JMP")
                    {
                        uint jumpTarget2 = GetJumpTarget(insn);
                        if (jumpTarget2 >= fileLength) return;

                        if (jumpTarget2 < fileLength && jumpTarget2 != 0)
                        {
                            currentAddress = jumpTarget2;
                            break;
                        }
                    }
                    else
                    {
                        currentAddress = nextAddress;
                    }
                }
            }
        }

        private bool IsReturnInstruction(string mnemonic)
        {
            string upper = mnemonic?.ToUpper() ?? "";
            return upper == "RET" || upper == "RETF" || upper == "RETN" ||
                   upper == "IRET" || upper == "IRETD" || upper == "RETF" ||
                   upper == "RETN";
        }

        private byte[] ReadCodeBlock(BinaryReader br, uint address, out X86Instruction[] instructions)
        {
            long fileLength = br.BaseStream.Length;
            if (address >= fileLength)
            {
                instructions = null;
                return null;
            }

            int bytesToRead = (int)Math.Min(32, fileLength - address);
            byte[] chunk = ReadBytesAt(br, address, bytesToRead);

            instructions = CapstoneX86Disassembly.Disassemble(chunk, address);

            return chunk;
        }

        private byte[] ReadBytesAt(BinaryReader br, long position, int count)
        {
            long originalPos = br.BaseStream.Position;
            br.BaseStream.Position = position;
            byte[] data = br.ReadBytes(count);
            br.BaseStream.Position = originalPos;
            return data;
        }

        public uint? FindDefaultPathAfterTableLoop(List<X86Instruction> allInstructions)
        {
            AnalysisDebug.WriteLine("Поиск пути по умолчанию после табличного цикла...");

            uint? staticMapDispatchPath = FindPackedCoordinateStaticMapDispatchDefaultPath(allInstructions);
            if (staticMapDispatchPath.HasValue)
                return staticMapDispatchPath;

            for (int i = 0; i < allInstructions.Count - 10; i++)
            {
                // Ищем паттерн: MOV AL, [3C3A]
                if (i < allInstructions.Count &&
                    allInstructions[i].Bytes.Length >= 3 &&
                    allInstructions[i].Bytes[0] == 0xA0 &&
                    allInstructions[i].Bytes[1] == 0x3A &&
                    allInstructions[i].Bytes[2] == 0x3C)
                {
                    // Ищем MOV BX, 0
                    if (i + 1 < allInstructions.Count &&
                        allInstructions[i + 1].Bytes.Length >= 3 &&
                        allInstructions[i + 1].Bytes[0] == 0xBB &&
                        allInstructions[i + 1].Bytes[1] == 0x00 &&
                        allInstructions[i + 1].Bytes[2] == 0x00)
                    {
                        // Ищем CMP AL, [BX + C973]
                        if (i + 2 < allInstructions.Count &&
                            allInstructions[i + 2].Bytes.Length >= 4 &&
                            allInstructions[i + 2].Bytes[0] == 0x3A &&
                            allInstructions[i + 2].Bytes[1] == 0x87 &&
                            allInstructions[i + 2].Bytes[2] == 0x73 &&
                            allInstructions[i + 2].Bytes[3] == 0xC9)
                        {
                            AnalysisDebug.WriteLine($"  Найден паттерн начала табличного цикла по адресу 0x{allInstructions[i].Address:X4}");

                            // Ищем JC/JB для возврата в цикл
                            for (int j = i + 3; j < i + 15 && j < allInstructions.Count; j++)
                            {
                                if (IsConditionalJump(allInstructions[j], out uint target) &&
                                    (allInstructions[j].Mnemonic.ToUpper() == "JC" ||
                                     allInstructions[j].Mnemonic.ToUpper() == "JB" ||
                                     allInstructions[j].Mnemonic.ToUpper() == "JNAE"))
                                {
                                    AnalysisDebug.WriteLine($"  Найден переход цикла по адресу 0x{allInstructions[j].Address:X4}");

                                    // Ищем инструкцию после цикла (путь по умолчанию)
                                    for (int k = j + 1; k < j + 10 && k < allInstructions.Count; k++)
                                    {
                                        // Ищем MOV word ptr [0x3BD4], imm16
                                        if (allInstructions[k].Bytes.Length >= 6 &&
                                            allInstructions[k].Bytes[0] == 0xC7 &&
                                            allInstructions[k].Bytes[1] == 0x06 &&
                                            allInstructions[k].Bytes[2] == 0xD4 &&
                                            allInstructions[k].Bytes[3] == 0x3B)
                                        {
                                            uint defaultPathAddress = (uint)allInstructions[k].Address;
                                            ushort textAddr = BitConverter.ToUInt16(allInstructions[k].Bytes, 4);

                                            AnalysisDebug.WriteLine($"  Найден путь по умолчанию после табличного цикла: 0x{defaultPathAddress:X4}");
                                            AnalysisDebug.WriteLine($"    Загружается текст из 0x{textAddr:X4}");

                                            return defaultPathAddress;
                                        }
                                    }

                                    // Если не нашли паттерн с MOV, берем первую инструкцию после цикла
                                    if (j + 1 < allInstructions.Count)
                                    {
                                        uint defaultPathAddress = (uint)allInstructions[j + 1].Address;
                                        AnalysisDebug.WriteLine($"  Путь по умолчанию (первая инструкция после цикла): 0x{defaultPathAddress:X4}");
                                        return defaultPathAddress;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Альтернативный поиск
            for (int i = 0; i < allInstructions.Count - 5; i++)
            {
                if (allInstructions[i].Bytes.Length >= 6 &&
                    allInstructions[i].Bytes[0] == 0xC7 &&
                    allInstructions[i].Bytes[1] == 0x06 &&
                    allInstructions[i].Bytes[2] == 0xD4 &&
                    allInstructions[i].Bytes[3] == 0x3B)
                {
                    // Проверяем, есть ли перед этим цикл сравнений
                    bool hasComparisonsBefore = false;
                    for (int j = Math.Max(0, i - 20); j < i; j++)
                    {
                        if (allInstructions[j].Bytes.Length >= 4 &&
                            allInstructions[j].Bytes[0] == 0x3A &&
                            allInstructions[j].Bytes[1] == 0x87 &&
                            allInstructions[j].Bytes[2] == 0x73 &&
                            allInstructions[j].Bytes[3] == 0xC9)
                        {
                            hasComparisonsBefore = true;
                            break;
                        }
                    }

                    if (hasComparisonsBefore)
                    {
                        uint defaultPathAddress = (uint)allInstructions[i].Address;
                        ushort textAddr = BitConverter.ToUInt16(allInstructions[i].Bytes, 4);

                        AnalysisDebug.WriteLine($"  Найден путь по умолчанию (альтернативный поиск): 0x{defaultPathAddress:X4}");
                        AnalysisDebug.WriteLine($"    Загружается текст из 0x{textAddr:X4}");

                        return defaultPathAddress;
                    }
                }
            }

            AnalysisDebug.WriteLine("  Путь по умолчанию не найден");
            return null;
        }

        private uint? FindPackedCoordinateStaticMapDispatchDefaultPath(List<X86Instruction> allInstructions)
        {
            if (allInstructions == null || allInstructions.Count < 3)
                return null;

            for (int i = 0; i < allInstructions.Count - 2; i++)
            {
                if (!TryMatchPackedCoordinateStaticMapDispatchStart(allInstructions, i, out ushort mapBaseAddress))
                    continue;

                uint entryAddress = (uint)allInstructions[i].Address;
                if (!HasIncomingJumpFromEarlierInstruction(allInstructions, i, entryAddress))
                    continue;

                AnalysisDebug.WriteLine(
                    $"  Найден default-path как packed-coordinate static-map dispatch: 0x{entryAddress:X4} " +
                    $"([0x{mapBaseAddress:X4} + [0x3C3A]])");
                return entryAddress;
            }

            return null;
        }

        private bool TryMatchPackedCoordinateStaticMapDispatchStart(
            List<X86Instruction> allInstructions,
            int index,
            out ushort mapBaseAddress)
        {
            mapBaseAddress = 0;

            if (!TryDecodeMovReg16FromDirectMemory(
                    allInstructions[index],
                    out string indexRegister,
                    out ushort sourceAddress) ||
                sourceAddress != 0x3C3A)
            {
                return false;
            }

            int loadIndex = index + 1;
            if (loadIndex < allInstructions.Count &&
                IsAndReg16With00FF(allInstructions[loadIndex], indexRegister))
            {
                loadIndex++;
            }

            if (loadIndex >= allInstructions.Count)
                return false;

            return TryDecodeMovAlFromIndexedMemory(
                allInstructions[loadIndex],
                indexRegister,
                out mapBaseAddress) &&
                IsStaticMapLayerBaseAddress(mapBaseAddress);
        }

        private bool TryDecodeMovReg16FromDirectMemory(
            X86Instruction instruction,
            out string destRegister,
            out ushort sourceAddress)
        {
            destRegister = null;
            sourceAddress = 0;

            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0x8B)
                return false;

            byte modRm = bytes[1];
            if ((modRm & 0xC0) != 0x00 || (modRm & 0x07) != 0x06)
                return false;

            destRegister = DecodeReg16FromRegField(modRm);
            sourceAddress = BitConverter.ToUInt16(bytes, 2);
            return !string.IsNullOrWhiteSpace(destRegister);
        }

        private bool IsAndReg16With00FF(X86Instruction instruction, string register)
        {
            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0x81)
                return false;

            byte modRm = bytes[1];
            if ((modRm & 0xC0) != 0xC0 || ((modRm >> 3) & 0x07) != 4)
                return false;

            string targetRegister = DecodeReg16FromRmField(modRm);
            if (!string.Equals(targetRegister, register, StringComparison.OrdinalIgnoreCase))
                return false;

            ushort immediate = BitConverter.ToUInt16(bytes, 2);
            return immediate == 0x00FF;
        }

        private bool TryDecodeMovAlFromIndexedMemory(
            X86Instruction instruction,
            string indexRegister,
            out ushort displacement)
        {
            displacement = 0;

            byte[] bytes = instruction?.Bytes;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0x8A)
                return false;

            byte modRm = bytes[1];
            byte mod = (byte)((modRm >> 6) & 0x03);
            byte reg = (byte)((modRm >> 3) & 0x07);
            if (mod != 0x02 || reg != 0)
                return false;

            string baseRegister = DecodeSingleBaseRegisterFromRmField(modRm);
            if (!string.Equals(baseRegister, indexRegister, StringComparison.OrdinalIgnoreCase))
                return false;

            displacement = BitConverter.ToUInt16(bytes, 2);
            return true;
        }

        private bool HasIncomingJumpFromEarlierInstruction(
            List<X86Instruction> allInstructions,
            int targetIndex,
            uint targetAddress)
        {
            for (int i = 0; i < targetIndex; i++)
            {
                if (IsConditionalJump(allInstructions[i], out uint conditionalTarget) &&
                    conditionalTarget == targetAddress)
                {
                    return true;
                }

                if (IsUnconditionalJump(allInstructions[i], out uint unconditionalTarget) &&
                    unconditionalTarget == targetAddress)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStaticMapLayerBaseAddress(ushort address)
        {
            return address == 0x3CFA || address == 0x3DFA;
        }

        private string DecodeReg16FromRegField(byte modRm)
        {
            string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
            int reg = (modRm >> 3) & 0x07;
            return reg >= 0 && reg < regNames16.Length ? regNames16[reg] : null;
        }

        private string DecodeReg16FromRmField(byte modRm)
        {
            string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
            int rm = modRm & 0x07;
            return rm >= 0 && rm < regNames16.Length ? regNames16[rm] : null;
        }

        private string DecodeSingleBaseRegisterFromRmField(byte modRm)
        {
            switch (modRm & 0x07)
            {
                case 4: return "SI";
                case 5: return "DI";
                case 6: return "BP";
                case 7: return "BX";
                default: return null;
            }
        }
    }
}
