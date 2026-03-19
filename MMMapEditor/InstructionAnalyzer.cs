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
using System.Text;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    /// <summary>
    /// Анализирует отдельные инструкции x86 для поиска текстов, монстров и битв
    /// </summary>
    public class InstructionAnalyzer
    {
        private readonly OvrFileConfig _config;

        public InstructionAnalyzer(OvrFileConfig config)
        {
            _config = config;
        }

        public void FindTextsInInstruction(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, HashSet<string> output)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // MOV word ptr [0x3BD4], imm16
            if (instructionBytes.Length >= 6 &&
                instructionBytes[0] == 0xC7 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0xD4 && instructionBytes[3] == 0x3B)
            {
                ushort textAddr = BitConverter.ToUInt16(instructionBytes, 4);
                string text = ExtractText(br, textAddr);
                if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                {
                    string textEntry = $"Text at 0x{textAddr:X4}: {text}";
                    output.Add(textEntry);
                }
            }
            // MOV [0x3BD4], reg
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x89 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0xD4 && instructionBytes[3] == 0x3B)
            {
                byte modRM = instructionBytes[1];
                byte regField = (byte)((modRM >> 3) & 0x07);
                string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (regField < regNames.Length && registerTracker.TryGetRegisterValue(regNames[regField], out ushort value))
                {
                    string text = ExtractText(br, value);
                    if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                    {
                        string textEntry = $"Text at 0x{value:X4} (via {regNames[regField]}): {text}";
                        output.Add(textEntry);
                    }
                }
            }
            // MOV reg, imm16 (для любого регистра)
            else if (instructionBytes.Length >= 3 &&
                     (instructionBytes[0] & 0xF8) == 0xB8 &&
                     instructionBytes[0] != 0xBC && instructionBytes[0] != 0xBD)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);

                // Проверяем, не является ли это значение адресом текста
                if (immediateValue >= 0xC000 && immediateValue <= 0xFFFF)
                {
                    string text = ExtractText(br, immediateValue);
                    if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                    {
                        byte regIndex = (byte)(instructionBytes[0] - 0xB8);
                        string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
                        string regName = regIndex < regNames.Length ? regNames[regIndex] : "?";

                        string textEntry = $"Text at 0x{immediateValue:X4} (via {regName}): {text}";
                        output.Add(textEntry);
                    }
                }
            }
            // MOV BP, imm16
            else if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBD)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);

                if (immediateValue >= 0xC000 && immediateValue <= 0xFFFF)
                {
                    string text = ExtractText(br, immediateValue);
                    if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                    {
                        string textEntry = $"Text at 0x{immediateValue:X4} (via BP): {text}";
                        output.Add(textEntry);
                    }
                }
            }
        }

        public void FindMonsterStatChanges(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, PathAnalysisResult result)
        {
            byte[] instructionBytes = insn.Bytes;

            // MOV byte ptr [C96F], imm8 - сила монстров
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                byte monsterPower = instructionBytes[4];
                result.MonsterPower = monsterPower;
                Debug.WriteLine($"    УСТАНОВЛЕНА СИЛА МОНСТРОВ: {monsterPower}");
            }
            // MOV byte ptr [C961], imm8 - уровень монстров
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                byte monsterLevel = instructionBytes[4];
                result.MonsterLevel = monsterLevel;
                Debug.WriteLine($"    УСТАНОВЛЕН УРОВЕНЬ МОНСТРОВ: {monsterLevel}");
            }
            // MOV [C96F], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.MonsterPower = chValue;
                    Debug.WriteLine($"    УСТАНОВЛЕНА СИЛА МОНСТРОВ ИЗ CH: {chValue}");
                }
            }
            // MOV [C961], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.MonsterLevel = chValue;
                    Debug.WriteLine($"    УСТАНОВЛЕН УРОВЕНЬ МОНСТРОВ ИЗ CH: {chValue}");
                }
            }
            // MOV [C96F], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x6F && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.MonsterPower = alValue;
                    Debug.WriteLine($"    УСТАНОВЛЕНА СИЛА МОНСТРОВ ИЗ AL: {alValue}");
                }
            }
            // MOV [C961], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x61 && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.MonsterLevel = alValue;
                    Debug.WriteLine($"    УСТАНОВЛЕН УРОВЕНЬ МОНСТРОВ ИЗ AL: {alValue}");
                }
            }
        }

        public void FindMonsterBattleInfo(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, PathAnalysisResult result,
            byte targetX, byte targetY)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // Обнаружение неопределенного цикла (инструкция CMP BL, [3C1D])
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
            {
                result.IsIndeterminateLoop = true;
                result.IsInLoop = true;
                result.LoopStartAddress = address;

                int markedCount = 0;
                foreach (var key in result.BattleMonsterEntries.Keys.ToList())
                {
                    var entry = result.BattleMonsterEntries[key];
                    bool isFullyDefined = entry.val1 != 0 && entry.val2 != 0;
                    if (!isFullyDefined)
                    {
                        result.BattleMonsterEntries[key] = (entry.val1, entry.val2, true);
                        markedCount++;
                    }
                }
                Debug.WriteLine($"    ОБНАРУЖЕН НЕОПРЕДЕЛЁННЫЙ ЦИКЛ по адресу 0x{address:X4}, помечено {markedCount} записей как неопределенные");
                return;
            }

            // MOV [3C1D], AL - количество монстров в полной битве
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2 &&
                instructionBytes[1] == 0x1D && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.BattleMonsterCount = alValue;
                    result.IsBattleMonsterCountIndeterminate = false;
                    Debug.WriteLine($"    УСТАНОВЛЕНО КОЛИЧЕСТВО МОНСТРОВ: {alValue}");
                }
                else
                {
                    result.BattleMonsterCount = null;
                    result.IsBattleMonsterCountIndeterminate = true;
                    Debug.WriteLine("    КОЛИЧЕСТВО МОНСТРОВ НЕОПРЕДЕЛЕНО (AL неизвестен)");
                }
                return;
            }

            // ========== ТАБЛИЦЫ КОНКРЕТНЫХ ЗНАЧЕНИЙ (CDBD+ И CDB5+) ==========

            // Загрузка из таблицы CDBD+ (MOV AL, [BX+CDBD])
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x8A &&
                instructionBytes[1] == 0x87 &&
                instructionBytes[2] == 0xBD && instructionBytes[3] == 0xCD)
            {
                ProcessLoadFromTableCDBD(instructionBytes, registerTracker, address, br, targetX, targetY);
                return;
            }

            // Загрузка из таблицы CDB5+ (MOV BP, [BX+CDB5])
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xAF &&
                     instructionBytes[2] == 0xB5 && instructionBytes[3] == 0xCD)
            {
                ProcessLoadFromTableCDB5(instructionBytes, registerTracker, address, br, targetX, targetY);
                return;
            }

            // Загрузка младшего байта из CDB5+ (MOV AL, [BX+CDB5])
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8A &&
                     instructionBytes[1] == 0x87 &&
                     instructionBytes[2] == 0xB5 && instructionBytes[3] == 0xCD)
            {
                ProcessLoadByteFromTableCDB5(instructionBytes, registerTracker, address, br, targetX, targetY);
                return;
            }

            // ========== ТАБЛИЦЫ ЧАСТИЧНЫХ ЗНАЧЕНИЙ (CDA9+ И CDB1+) ==========

            // Загрузка из таблицы CDA9+ (MOV AL, [BX+CDA9])
            else if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x8A &&
                instructionBytes[1] == 0x87 &&
                instructionBytes[2] == 0xA9 && instructionBytes[3] == 0xCD)
            {
                ProcessLoadFromTableCDA9(instructionBytes, registerTracker, address, br);
                return;
            }

            // Загрузка из таблицы CDB1+ (MOV BP, [BX+CDB1])
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xAF &&
                     instructionBytes[2] == 0xB1 && instructionBytes[3] == 0xCD)
            {
                ProcessLoadFromTableCDB1(instructionBytes, registerTracker, address, br);
                return;
            }

            // Загрузка младшего байта из CDB1+ (MOV AL, [BX+CDB1])
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8A &&
                     instructionBytes[1] == 0x87 &&
                     instructionBytes[2] == 0xB1 && instructionBytes[3] == 0xCD)
            {
                ProcessLoadByteFromTableCDB1(instructionBytes, registerTracker, address, br);
                return;
            }

            // ========== КОПИРОВАНИЕ МЕЖДУ РЕГИСТРАМИ ==========

            // Копирование BP в CX (MOV CX, BP)
            else if (instructionBytes.Length >= 2 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xCD)
            {
                ProcessCopyBPtoCX(instructionBytes, registerTracker, address);
                return;
            }

            // ========== СОХРАНЕНИЕ В [BX+3C58] И [BX+3C29] ==========

            ProcessSaveOperations(instructionBytes, registerTracker, result, br, address);
        }

        // Детальные методы обработки для каждого типа инструкций
        // (я сократил их для компактности, но в реальном файле нужно перенести всю логику)

        /// <summary>
        /// Загрузка из таблицы CDBD+ (MOV AL, [BX+CDBD]) - первый индекс монстра
        /// </summary>
        private void ProcessLoadFromTableCDBD(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br, byte targetX, byte targetY)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDBD + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                byte actualValue = 0;
                bool readSuccess = false;
                string debugInfo = "";

                ushort effectiveBx = bxValue;
                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    sourceAddr = (ushort)(0xCDBD + effectiveBx);
                    fileOffset = sourceAddr - _config.TextBaseAddr;
                    debugInfo = $" (using targetX={targetX})";
                    Debug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {sourceAddr:X4}");
                }

                try
                {
                    if (fileOffset >= 0 && fileOffset < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        actualValue = br.ReadByte();
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}]: 0x{actualValue:X2}{debugInfo}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV AL, [BX+CDBD] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4}, val={actualValue:X2})",
                    "CDBD"
                );

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDBD+: AL = [BX+CDBD] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка из таблицы CDB5+ (MOV BP, [BX+CDB5]) - слово с двумя индексами монстров
        /// </summary>
        private void ProcessLoadFromTableCDB5(byte[] bytes, RegisterTracker tracker,
    uint address, BinaryReader br, byte targetX, byte targetY)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDB5 + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                ushort actualValue = 0;
                bool readSuccess = false;
                string debugInfo = "";

                ushort effectiveBx = bxValue;
                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    sourceAddr = (ushort)(0xCDB5 + effectiveBx);
                    fileOffset = sourceAddr - _config.TextBaseAddr;
                    debugInfo = $" (using targetX={targetX})";
                    Debug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {sourceAddr:X4}");
                }

                try
                {
                    if (fileOffset >= 0 && fileOffset + 1 < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        byte lowByte = br.ReadByte();
                        byte highByte = br.ReadByte();
                        actualValue = (ushort)((highByte << 8) | lowByte);
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}]: 0x{actualValue:X4}{debugInfo} (low=0x{lowByte:X2}, high=0x{highByte:X2})");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                // Сохраняем полное слово только в BP. НЕ трогаем AL.
                tracker.SetRegisterValueWithSource(
                    "BP",
                    readSuccess ? actualValue : (ushort)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV BP, [BX+CDB5] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4}, val={actualValue:X4})",
                    "CDB5"
                );

                // УДАЛЕНО: автоматическая установка AL и AH.
                // Информация о high и low байтах будет доступна через BP и последующее копирование в CX.

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB5+: BP = [BX+CDB5] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка младшего байта из CDB5+ (MOV AL, [BX+CDB5]) - второй индекс монстра
        /// </summary>
        private void ProcessLoadByteFromTableCDB5(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br, byte targetX, byte targetY)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDB5 + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                byte actualValue = 0;
                bool readSuccess = false;
                string debugInfo = "";

                ushort effectiveBx = bxValue;
                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    sourceAddr = (ushort)(0xCDB5 + effectiveBx);
                    fileOffset = sourceAddr - _config.TextBaseAddr;
                    debugInfo = $" (using targetX={targetX})";
                    Debug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {sourceAddr:X4}");
                }

                try
                {
                    if (fileOffset >= 0 && fileOffset < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        actualValue = br.ReadByte();
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2}{debugInfo}");
                    }
                    else
                    {
                        Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV AL, [BX+CDB5] (BX={bxValue}{debugInfo}, effBX={effectiveBx}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDB5"
                );

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB5+ (младший байт): AL = [BX+CDB5] (BX={bxValue}{debugInfo}, effBX={effectiveBx}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка из таблицы CDA9+ (MOV AL, [BX+CDA9]) - сила/уровень монстра
        /// </summary>
        private void ProcessLoadFromTableCDA9(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDA9 + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (fileOffset >= 0 && fileOffset < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        actualValue = br.ReadByte();
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2}");
                    }
                    else
                    {
                        Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV AL, [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDA9"
                );

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDA9+: AL = [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка из таблицы CDB1+ (MOV BP, [BX+CDB1]) - слово с индексами монстров
        /// </summary>
        private void ProcessLoadFromTableCDB1(byte[] bytes, RegisterTracker tracker,
    uint address, BinaryReader br)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDB1 + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                ushort actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (fileOffset >= 0 && fileOffset + 1 < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        byte lowByte = br.ReadByte();
                        byte highByte = br.ReadByte();
                        actualValue = (ushort)((highByte << 8) | lowByte);
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X4}");
                    }
                    else
                    {
                        Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "BP",
                    readSuccess ? actualValue : (ushort)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV BP, [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X4") : "0")})",
                    "CDB1"
                );

                // УДАЛЕНО: автоматическая установка AL из младшего байта.
                // Младший байт будет доступен позже, если произойдет MOV CX, BP и затем использование CL.

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB1+: BP = [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка младшего байта из CDB1+ (MOV AL, [BX+CDB1]) - второй индекс монстра
        /// </summary>
        private void ProcessLoadByteFromTableCDB1(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                ushort sourceAddr = (ushort)(0xCDB1 + bxValue);
                long fileOffset = sourceAddr - _config.TextBaseAddr;

                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (fileOffset >= 0 && fileOffset < br.BaseStream.Length)
                    {
                        long originalPos = br.BaseStream.Position;
                        br.BaseStream.Position = fileOffset;
                        actualValue = br.ReadByte();
                        br.BaseStream.Position = originalPos;
                        readSuccess = true;
                        Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2} (младший байт)");
                    }
                    else
                    {
                        Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV AL, [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDB1"
                );

                Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB1+ (младший байт): AL = [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Копирование BP в CX (MOV CX, BP)
        /// </summary>
        private void ProcessCopyBPtoCX(byte[] bytes, RegisterTracker tracker, uint address)
        {
            if (tracker.IsFromTable("BP"))
            {
                ushort? sourceAddr = tracker.GetSourceAddress("BP");
                ushort? originalBx = tracker.GetOriginalBx("BP");
                string sourceTable = tracker.GetSourceTable("BP");
                tracker.TryGetRegisterValue("BP", out ushort bpValue);

                tracker.SetRegisterValueWithSource(
                    "CX",
                    bpValue,
                    sourceAddr ?? 0,
                    originalBx ?? 0,
                    true,
                    address,
                    $"MOV CX, BP (копирование из таблицы, val={bpValue:X4})",
                    sourceTable
                );

                // Обновляем частичные регистры
                byte clValue = (byte)(bpValue & 0xFF);
                byte chValue = (byte)(bpValue >> 8);

                tracker.TrackPartialRegisterOperation("CX", "CL", clValue, address, "MOV CL, low byte of BP");
                tracker.TrackPartialRegisterOperation("CX", "CH", chValue, address, "MOV CH, high byte of BP");

                Debug.WriteLine($"    КОПИРОВАНИЕ ИЗ ТАБЛИЦЫ: CX = BP (source={sourceAddr:X4}, table={sourceTable}, originalBX={originalBx}, val={bpValue:X4})");
            }
            else
            {
                // Обычное копирование без источника из таблицы
                if (tracker.TryGetRegisterValue("BP", out ushort bpValue))
                {
                    tracker.SetRegisterValue("CX", bpValue, address, $"MOV CX, BP (0x{bpValue:X4})");

                    byte clValue = (byte)(bpValue & 0xFF);
                    byte chValue = (byte)(bpValue >> 8);
                    tracker.TrackPartialRegisterOperation("CX", "CL", clValue, address, "MOV CL, low byte of BP");
                    tracker.TrackPartialRegisterOperation("CX", "CH", chValue, address, "MOV CH, high byte of BP");

                    Debug.WriteLine($"    КОПИРОВАНИЕ: CX = BP (0x{bpValue:X4})");
                }
            }
        }

        /// <summary>
        /// Обработка всех операций сохранения в память (основной метод)
        /// </summary>
        private void ProcessSaveOperations(byte[] bytes, RegisterTracker tracker,
            PathAnalysisResult result, BinaryReader br, uint address)
        {
            // Сохранение в [BX+3C58] из AL
            if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x87 && bytes[2] == 0x58 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C58FromAL(bytes, tracker, result, address);
                return;
            }

            // Сохранение в [BX+3C29] из CL
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x8F && bytes[2] == 0x29 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C29FromCL(bytes, tracker, result, address);
                return;
            }

            // Сохранение в [BX+3C58] из DL
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x97 && bytes[2] == 0x58 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C58FromDL(bytes, tracker, result, address);
                return;
            }

            // Сохранение в [BX+3C29] из DL
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x97 && bytes[2] == 0x29 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C29FromDL(bytes, tracker, result, address);
                return;
            }

            // Сохранение в [BX+3C58] из BL
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x9F && bytes[2] == 0x58 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C58FromBL(bytes, tracker, result, address);
                return;
            }

            // Сохранение в [BX+3C29] из BL
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x9F && bytes[2] == 0x29 && bytes[3] == 0x3C)
            {
                ProcessSaveTo3C29FromBL(bytes, tracker, result, address);
                return;
            }

            // Прямое сохранение в [3C58] из AL (без BX)
            else if (bytes.Length >= 3 && bytes[0] == 0xA2 && bytes[1] == 0x58 && bytes[2] == 0x3C)
            {
                ProcessDirectSaveTo3C58FromAL(bytes, tracker, result, address);
                return;
            }

            // Прямое сохранение в [3C29] из AL (без BX)
            else if (bytes.Length >= 3 && bytes[0] == 0xA2 && bytes[1] == 0x29 && bytes[2] == 0x3C)
            {
                ProcessDirectSaveTo3C29FromAL(bytes, tracker, result, address);
                return;
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C58] из AL
        /// </summary>
        // InstructionAnalyzer.cs
        private void ProcessSaveTo3C58FromAL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("AL");
                    ushort? sourceAddr = tracker.GetSourceAddress("AL");
                    ushort? originalBx = tracker.GetOriginalBx("AL");
                    string sourceTable = tracker.GetSourceTable("AL");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра (полностью определённый)
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val1={alValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в AL? Это нестандартная ситуация
                                // В оригинале такого не должно быть, но если случилось, сохраняем как val1
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val1={alValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                                }
                                break;

                            case "CDA9":
                            case "CDB1":
                                // Из таблиц CDA9/CDB1 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C58,
                                    SrcReg = "AL",
                                    SrcRegValue = alValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val={alValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C58] = AL (BX={bxValue}, val1={alValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = AL (BX={bxValue}, val1={alValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C29] из CL
        /// </summary>
        private void ProcessSaveTo3C29FromCL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("CL", out byte clValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("CL") || tracker.IsFromTable("CX");
                    ushort? sourceAddr = tracker.GetSourceAddress("CL") ?? tracker.GetSourceAddress("CX");
                    ushort? originalBx = tracker.GetOriginalBx("CL") ?? tracker.GetOriginalBx("CX");
                    string sourceTable = tracker.GetSourceTable("CL") ?? tracker.GetSourceTable("CX");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра (полностью определённый)
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, clValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, clValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val2={clValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в CL? Нестандартно
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val2={clValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, clValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, clValue, false);
                                }
                                break;

                            case "CDB1":
                            case "CDA9":
                                // Из таблиц CDB1/CDA9 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C29,
                                    SrcReg = "CL",
                                    SrcRegValue = clValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val={clValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, clValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, clValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C29] = CL (BX={bxValue}, val2={clValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (existing.val1, clValue, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (0, clValue, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = CL (BX={bxValue}, val2={clValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C58] из DL
        /// </summary>
        private void ProcessSaveTo3C58FromDL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("DL", out byte dlValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("DL") || tracker.IsFromTable("DX");
                    ushort? sourceAddr = tracker.GetSourceAddress("DL") ?? tracker.GetSourceAddress("DX");
                    ushort? originalBx = tracker.GetOriginalBx("DL") ?? tracker.GetOriginalBx("DX");
                    string sourceTable = tracker.GetSourceTable("DL") ?? tracker.GetSourceTable("DX");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val1={dlValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в DL? Нестандартно
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val1={dlValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, 0, false);
                                }
                                break;

                            case "CDA9":
                            case "CDB1":
                                // Из таблиц CDA9/CDB1 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C58,
                                    SrcReg = "DL",
                                    SrcRegValue = dlValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val={dlValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (dlValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C58] = DL (BX={bxValue}, val1={dlValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (dlValue, existing.val2, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (dlValue, 0, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = DL (BX={bxValue}, val1={dlValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C29] из DL
        /// </summary>
        private void ProcessSaveTo3C29FromDL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("DL", out byte dlValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("DL") || tracker.IsFromTable("DX");
                    ushort? sourceAddr = tracker.GetSourceAddress("DL") ?? tracker.GetSourceAddress("DX");
                    ushort? originalBx = tracker.GetOriginalBx("DL") ?? tracker.GetOriginalBx("DX");
                    string sourceTable = tracker.GetSourceTable("DL") ?? tracker.GetSourceTable("DX");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, dlValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, dlValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val2={dlValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в DL? Нестандартно
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val2={dlValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, dlValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, dlValue, false);
                                }
                                break;

                            case "CDB1":
                            case "CDA9":
                                // Из таблиц CDB1/CDA9 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C29,
                                    SrcReg = "DL",
                                    SrcRegValue = dlValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val={dlValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, dlValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, dlValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C29] = DL (BX={bxValue}, val2={dlValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (existing.val1, dlValue, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (0, dlValue, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = DL (BX={bxValue}, val2={dlValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C58] из BL
        /// </summary>
        private void ProcessSaveTo3C58FromBL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("BL", out byte blValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("BL") || tracker.IsFromTable("BX");
                    ushort? sourceAddr = tracker.GetSourceAddress("BL") ?? tracker.GetSourceAddress("BX");
                    ushort? originalBx = tracker.GetOriginalBx("BL") ?? tracker.GetOriginalBx("BX");
                    string sourceTable = tracker.GetSourceTable("BL") ?? tracker.GetSourceTable("BX");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (blValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (blValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val1={blValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в BL? Нестандартно
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val1={blValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (blValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (blValue, 0, false);
                                }
                                break;

                            case "CDA9":
                            case "CDB1":
                                // Из таблиц CDA9/CDB1 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C58,
                                    SrcReg = "BL",
                                    SrcRegValue = blValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val={blValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (blValue, existing.val2, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (blValue, 0, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C58] = BL (BX={bxValue}, val1={blValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (blValue, existing.val2, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (blValue, 0, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = BL (BX={bxValue}, val1={blValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Сохранение в [BX+3C29] из BL
        /// </summary>
        private void ProcessSaveTo3C29FromBL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                if (tracker.TryGetByteRegisterValue("BL", out byte blValue))
                {
                    int saveIndex = bxValue;
                    bool isFromTable = tracker.IsFromTable("BL") || tracker.IsFromTable("BX");
                    ushort? sourceAddr = tracker.GetSourceAddress("BL") ?? tracker.GetSourceAddress("BX");
                    ushort? originalBx = tracker.GetOriginalBx("BL") ?? tracker.GetOriginalBx("BX");
                    string sourceTable = tracker.GetSourceTable("BL") ?? tracker.GetSourceTable("BX");

                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, blValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, blValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val2={blValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в BL? Нестандартно
                                Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val2={blValue:X2})");
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, blValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, blValue, false);
                                }
                                break;

                            case "CDB1":
                            case "CDA9":
                                // Из таблиц CDB1/CDA9 - частично определённая битва
                                result.PartialBattleInfo.Add(new PartialBattleInfo
                                {
                                    BxIndex = originalBx ?? saveIndex,
                                    DestAddr = 0x3C29,
                                    SrcReg = "BL",
                                    SrcRegValue = blValue,
                                    IsFromTable = true,
                                    SourceTableAddr = sourceAddr,
                                    SourceTable = sourceTable
                                });
                                result.HasPartialBattlePattern = true;
                                Debug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable}): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val={blValue:X2})");
                                break;

                            default:
                                // Из неизвестной таблицы - считаем полностью определённой
                                if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                                {
                                    var existing = result.BattleMonsterEntries[saveIndex];
                                    result.BattleMonsterEntries[saveIndex] = (existing.val1, blValue, false);
                                }
                                else
                                {
                                    result.BattleMonsterEntries[saveIndex] = (0, blValue, false);
                                }
                                Debug.WriteLine($"    ПОЛНАЯ БИТВА (UNKNOWN): [BX+3C29] = BL (BX={bxValue}, val2={blValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                        {
                            var existing = result.BattleMonsterEntries[saveIndex];
                            result.BattleMonsterEntries[saveIndex] = (existing.val1, blValue, false);
                        }
                        else
                        {
                            result.BattleMonsterEntries[saveIndex] = (0, blValue, false);
                        }
                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = BL (BX={bxValue}, val2={blValue:X2})");
                    }
                }
            }
        }

        /// <summary>
        /// Прямое сохранение в [3C58] из AL (без BX)
        /// </summary>
        private void ProcessDirectSaveTo3C58FromAL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
            {
                int saveIndex = 0; // BX = 0 для прямых сохранений
                bool isFromTable = tracker.IsFromTable("AL");
                ushort? sourceAddr = tracker.GetSourceAddress("AL");
                ushort? originalBx = tracker.GetOriginalBx("AL");
                string sourceTable = tracker.GetSourceTable("AL");

                if (isFromTable)
                {
                    switch (sourceTable)
                    {
                        case "CDBD":
                            // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                            }
                            Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, из CDBD): [3C58] = AL (val1={alValue:X2})");
                            break;

                        case "CDB5":
                            // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в AL? Нестандартно
                            Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (прямая, из CDB5 в 3C58? Нестандартно): [3C58] = AL (val1={alValue:X2})");
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                            }
                            break;

                        case "CDA9":
                        case "CDB1":
                            // Из таблиц CDA9/CDB1 - частично определённая битва
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? saveIndex,
                                DestAddr = 0x3C58,
                                SrcReg = "AL",
                                SrcRegValue = alValue,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr,
                                SourceTable = sourceTable
                            });
                            result.HasPartialBattlePattern = true;
                            Debug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ {sourceTable}+: [3C58] = AL (originalBX={originalBx}, val={alValue:X2})");
                            break;

                        default:
                            // Из неизвестной таблицы - считаем полностью определённой
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                            }
                            Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, UNKNOWN): [3C58] = AL (val1={alValue:X2})");
                            break;
                    }
                }
                else
                {
                    // Значение не из таблиц - полностью определённая битва
                    if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                    {
                        var existing = result.BattleMonsterEntries[saveIndex];
                        result.BattleMonsterEntries[saveIndex] = (alValue, existing.val2, false);
                    }
                    else
                    {
                        result.BattleMonsterEntries[saveIndex] = (alValue, 0, false);
                    }
                    Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C58] = AL (val1={alValue:X2})");
                }
            }
        }

        /// <summary>
        /// Прямое сохранение в [3C29] из AL (без BX)
        /// </summary>
        private void ProcessDirectSaveTo3C29FromAL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
            {
                int saveIndex = 0; // BX = 0 для прямых сохранений
                bool isFromTable = tracker.IsFromTable("AL");
                ushort? sourceAddr = tracker.GetSourceAddress("AL");
                ushort? originalBx = tracker.GetOriginalBx("AL");
                string sourceTable = tracker.GetSourceTable("AL");

                if (isFromTable)
                {
                    switch (sourceTable)
                    {
                        case "CDB5":
                            // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (existing.val1, alValue, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (0, alValue, false);
                            }
                            Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, из CDB5): [3C29] = AL (val2={alValue:X2})");
                            break;

                        case "CDBD":
                            // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в AL? Нестандартно
                            Debug.WriteLine($"    [!] ПОЛНАЯ БИТВА (прямая, из CDBD в 3C29? Нестандартно): [3C29] = AL (val2={alValue:X2})");
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (existing.val1, alValue, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (0, alValue, false);
                            }
                            break;

                        case "CDB1":
                        case "CDA9":
                            // Из таблиц CDB1/CDA9 - частично определённая битва
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? saveIndex,
                                DestAddr = 0x3C29,
                                SrcReg = "AL",
                                SrcRegValue = alValue,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr,
                                SourceTable = sourceTable
                            });
                            result.HasPartialBattlePattern = true;
                            Debug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ {sourceTable}+: [3C29] = AL (originalBX={originalBx}, val={alValue:X2})");
                            break;

                        default:
                            // Из неизвестной таблицы - считаем полностью определённой
                            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                            {
                                var existing = result.BattleMonsterEntries[saveIndex];
                                result.BattleMonsterEntries[saveIndex] = (existing.val1, alValue, false);
                            }
                            else
                            {
                                result.BattleMonsterEntries[saveIndex] = (0, alValue, false);
                            }
                            Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, UNKNOWN): [3C29] = AL (val2={alValue:X2})");
                            break;
                    }
                }
                else
                {
                    // Значение не из таблиц - полностью определённая битва
                    if (result.BattleMonsterEntries.ContainsKey(saveIndex))
                    {
                        var existing = result.BattleMonsterEntries[saveIndex];
                        result.BattleMonsterEntries[saveIndex] = (existing.val1, alValue, false);
                    }
                    else
                    {
                        result.BattleMonsterEntries[saveIndex] = (0, alValue, false);
                    }
                    Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C29] = AL (val2={alValue:X2})");
                }
            }
        }

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
    }
}