﻿﻿// Copyright (c) Voland007 2026. All rights reserved.
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
            RegisterTracker registerTracker, int depth, List<TextEntry> output,
            Func<ushort, byte?> tryReadMemory8 = null)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;
            int initialCount = output.Count;

            ProcessContainerTexts(address, instructionBytes, registerTracker, output);
            ProcessImplicitContainerTextForLoot(address, instructionBytes, output, tryReadMemory8);
            ProcessItemTexts(address, instructionBytes, registerTracker, output, tryReadMemory8);
            ProcessGemsTexts(address, instructionBytes, registerTracker, output);
            ProcessGoldTexts(address, instructionBytes, registerTracker, output, tryReadMemory8);
            ProcessLootDestructionPatterns(address, instructionBytes, registerTracker, output);
            ProcessSecretPassageToDoomCastleNote(address, instructionBytes, registerTracker, output, tryReadMemory8);
            ProcessRedDragonResurrectionNote(address, instructionBytes, registerTracker, output, tryReadMemory8);
            ProcessWaterMonsterResurrectionNote(address, instructionBytes, registerTracker, output, tryReadMemory8);
            ProcessGiantScorpionResurrectionNote(address, instructionBytes, registerTracker, output, tryReadMemory8);

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
                    AddOutputText(output, address, textEntry);
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
                        AddOutputText(output, address, textEntry);
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
                    if (!string.IsNullOrEmpty(text) &&
                        text != "(empty string)" &&
                        !text.StartsWith("Cannot locate") &&
                        IsLikelyInlineTextImmediate(br, immediateValue, text))
                    {
                        byte regIndex = (byte)(instructionBytes[0] - 0xB8);
                        string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
                        string regName = regIndex < regNames.Length ? regNames[regIndex] : "?";

                        string textEntry = $"Text at 0x{immediateValue:X4} (via {regName}): {text}";
                        AddOutputText(output, address, textEntry);
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
                    if (!string.IsNullOrEmpty(text) &&
                        text != "(empty string)" &&
                        !text.StartsWith("Cannot locate") &&
                        IsLikelyInlineTextImmediate(br, immediateValue, text))
                    {
                        string textEntry = $"Text at 0x{immediateValue:X4} (via BP): {text}";
                        AddOutputText(output, address, textEntry);
                    }
                }
            }

            if (output.Count > initialCount)
            {
                AnalysisDebug.WriteLine($"        FindTextsInInstruction: instruction 0x{address:X4} добавила {output.Count - initialCount} текст(ов)");
                foreach (var text in output.Skip(initialCount))
                    AnalysisDebug.WriteLine($"          -> {text.Text}");
            }
        }

        private static void AddOutputText(List<TextEntry> output, uint instructionAddress, string text,
            TextSemanticKind semanticKind = TextSemanticKind.Unknown, bool isInferred = false)
        {
            if (output == null || string.IsNullOrEmpty(text))
                return;

            if (output.Any(entry =>
                    entry != null &&
                    entry.Text == text &&
                    entry.SemanticKind == semanticKind &&
                    entry.IsInferred == isInferred))
            {
                return;
            }

            output.Add(new TextEntry
            {
                Text = text,
                Address = instructionAddress,
                SemanticKind = semanticKind,
                IsInferred = isInferred
            });
        }

        private bool IsLikelyInlineTextImmediate(BinaryReader br, ushort textAddress, string decodedText)
        {
            if (br == null ||
                string.IsNullOrEmpty(decodedText) ||
                textAddress < OvrFileConfig.OverlayTextStartAddress ||
                !TryReadOverlayTextBytes(br, textAddress, out var bytes) ||
                bytes.Count == 0)
            {
                return false;
            }

            int printableOrWhitespace = 0;
            int visiblePrintable = 0;

            foreach (byte value in bytes)
            {
                if (value >= 0x20 && value <= 0x7E)
                {
                    printableOrWhitespace++;
                    if (value != 0x20)
                        visiblePrintable++;
                    continue;
                }

                if (value == 0x0D || value == 0x0A || value == 0x09)
                {
                    printableOrWhitespace++;
                    continue;
                }

                return false;
            }

            return visiblePrintable > 0 &&
                   printableOrWhitespace == bytes.Count;
        }

        private bool TryReadOverlayTextBytes(BinaryReader br, ushort textAddress, out List<byte> bytes)
        {
            bytes = new List<byte>();

            if (!OvrOverlayAddressReader.TryMapOverlayAddressToFileOffset(br, _config, textAddress, out long fileOffset))
                return false;

            long originalPos = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset;
                const int maxLength = 250;

                while (br.BaseStream.Position < br.BaseStream.Length && bytes.Count < maxLength)
                {
                    byte value = br.ReadByte();
                    if (value == 0)
                        return true;

                    bytes.Add(value);
                }

                return bytes.Count > 0;
            }
            catch
            {
                bytes.Clear();
                return false;
            }
            finally
            {
                br.BaseStream.Position = originalPos;
            }
        }

        private void ProcessSecretPassageToDoomCastleNote(uint instructionAddress, byte[] instructionBytes,
            RegisterTracker registerTracker, List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            if (!TryGetByteWrittenToAddress(
                    instructionBytes,
                    registerTracker,
                    SpecialNoteTexts.SecretPassageToDoomCastleAddress,
                    tryReadMemory8,
                    out byte writtenValue) ||
                writtenValue == 0)
            {
                return;
            }

            AnalysisDebug.WriteLine(
                $"    SECRET PASSAGE: [0x{SpecialNoteTexts.SecretPassageToDoomCastleAddress:X4}] = 0x{writtenValue:X2} по адресу 0x{instructionAddress:X4}");
            AddOutputText(output, instructionAddress, SpecialNoteTexts.SecretPassageToDoomCastle);
        }

        private void ProcessRedDragonResurrectionNote(uint instructionAddress, byte[] instructionBytes,
            RegisterTracker registerTracker, List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            if (!TryGetByteWrittenToAddress(
                    instructionBytes,
                    registerTracker,
                    SpecialNoteTexts.RedDragonResurrectionAddress,
                    tryReadMemory8,
                    out byte writtenValue) ||
                writtenValue != 0)
            {
                return;
            }

            AnalysisDebug.WriteLine(
                $"    RED DRAGON RESURRECTION: [0x{SpecialNoteTexts.RedDragonResurrectionAddress:X4}] = 0x{writtenValue:X2} по адресу 0x{instructionAddress:X4}");
            AddOutputText(output, instructionAddress, SpecialNoteTexts.RedDragonResurrection);
        }

        private void ProcessWaterMonsterResurrectionNote(uint instructionAddress, byte[] instructionBytes,
            RegisterTracker registerTracker, List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            if (!TryGetByteWrittenToAddress(
                    instructionBytes,
                    registerTracker,
                    SpecialNoteTexts.WaterMonsterResurrectionAddress,
                    tryReadMemory8,
                    out byte writtenValue) ||
                writtenValue != 0)
            {
                return;
            }

            AnalysisDebug.WriteLine(
                $"    WATER MONSTER RESURRECTION: [0x{SpecialNoteTexts.WaterMonsterResurrectionAddress:X4}] = 0x{writtenValue:X2} по адресу 0x{instructionAddress:X4}");
            AddOutputText(output, instructionAddress, SpecialNoteTexts.WaterMonsterResurrection);
        }

        private void ProcessGiantScorpionResurrectionNote(uint instructionAddress, byte[] instructionBytes,
            RegisterTracker registerTracker, List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            if (!TryGetByteWrittenToAddress(
                    instructionBytes,
                    registerTracker,
                    SpecialNoteTexts.GiantScorpionResurrectionAddress,
                    tryReadMemory8,
                    out byte writtenValue) ||
                writtenValue != 0)
            {
                return;
            }

            AnalysisDebug.WriteLine(
                $"    GIANT SCORPION RESURRECTION: [0x{SpecialNoteTexts.GiantScorpionResurrectionAddress:X4}] = 0x{writtenValue:X2} по адресу 0x{instructionAddress:X4}");
            AddOutputText(output, instructionAddress, SpecialNoteTexts.GiantScorpionResurrection);
        }

        private bool TryGetByteWrittenToAddress(byte[] instructionBytes, RegisterTracker registerTracker,
            ushort targetAddress, Func<ushort, byte?> tryReadMemory8, out byte value)
        {
            value = 0;

            if (TryGetDirectByteWriteToAddress(instructionBytes, registerTracker, targetAddress, out value))
                return true;

            if (TryGetDirectWordMemoryWrite(instructionBytes, registerTracker, out ushort wordAddress, out ushort wordValue))
            {
                if (wordAddress == targetAddress)
                {
                    value = (byte)(wordValue & 0x00FF);
                    return true;
                }

                if (unchecked((ushort)(wordAddress + 1)) == targetAddress)
                {
                    value = (byte)((wordValue >> 8) & 0x00FF);
                    return true;
                }
            }

            if (TryGetIncDecByteWriteToAddress(instructionBytes, registerTracker, targetAddress, tryReadMemory8, out value))
                return true;

            return false;
        }

        private bool TryGetDirectByteWriteToAddress(byte[] instructionBytes, RegisterTracker registerTracker,
            ushort targetAddress, out byte value)
        {
            value = 0;

            if (instructionBytes == null || instructionBytes.Length < 2)
                return false;

            // MOV [moffs8], AL
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA2)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                return memAddr == targetAddress &&
                       registerTracker.TryGetByteRegisterValue("AL", out value);
            }

            byte modRm = instructionBytes[1];
            byte mod = (byte)((modRm >> 6) & 0x03);
            byte regField = (byte)((modRm >> 3) & 0x07);

            // MOV byte ptr r/m8, imm8
            if (instructionBytes[0] == 0xC6 &&
                mod != 0x03 &&
                regField == 0 &&
                TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort immTarget, out int decodedLength) &&
                immTarget == targetAddress &&
                instructionBytes.Length > decodedLength)
            {
                value = instructionBytes[decodedLength];
                return true;
            }

            // MOV byte ptr r/m8, reg8
            if (instructionBytes[0] == 0x88 &&
                mod != 0x03 &&
                TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort regTarget, out _) &&
                regTarget == targetAddress &&
                TryGetWrittenByteValue(instructionBytes, registerTracker, out value))
            {
                return true;
            }

            return false;
        }

        private bool TryGetIncDecByteWriteToAddress(byte[] instructionBytes, RegisterTracker registerTracker,
            ushort targetAddress, Func<ushort, byte?> tryReadMemory8, out byte value)
        {
            value = 0;

            if (tryReadMemory8 == null ||
                instructionBytes == null ||
                instructionBytes.Length < 2 ||
                instructionBytes[0] != 0xFE)
            {
                return false;
            }

            byte modRm = instructionBytes[1];
            byte mod = (byte)((modRm >> 6) & 0x03);
            byte regField = (byte)((modRm >> 3) & 0x07);
            if (mod == 0x03 || (regField != 0 && regField != 1))
                return false;

            if (!TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out ushort memAddr, out _) ||
                memAddr != targetAddress)
            {
                return false;
            }

            byte? currentValue = tryReadMemory8(targetAddress);
            if (!currentValue.HasValue)
                return false;

            int delta = regField == 0 ? 1 : -1;
            value = unchecked((byte)(currentValue.Value + delta));
            return true;
        }

        private void ProcessContainerTexts(uint instructionAddress, byte[] instructionBytes, RegisterTracker registerTracker,
            List<TextEntry> output)
        {
            // MOV byte ptr [0x3C79], imm8
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x79 && instructionBytes[3] == 0x3C)
            {
                AnalysisDebug.WriteLine($"    КОНТЕЙНЕР: прямая запись [3C79] = 0x{instructionBytes[4]:X2} по адресу 0x{instructionAddress:X4}");
                AddContainerText(instructionAddress, output, instructionBytes[4]);
                return;
            }

            // MOV byte ptr [0x3C79], reg8
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88 && instructionBytes[2] == 0x79 && instructionBytes[3] == 0x3C)
            {
                byte regField = (byte)((instructionBytes[1] >> 3) & 0x07);
                string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (regField < regNames.Length &&
                    registerTracker.TryGetByteRegisterValue(regNames[regField], out byte value))
                {
                    AnalysisDebug.WriteLine($"    КОНТЕЙНЕР: запись [3C79] из {regNames[regField]} = 0x{value:X2} по адресу 0x{instructionAddress:X4}");
                    AddContainerText(instructionAddress, output, value);
                }
                return;
            }

            // MOV [0x3C79], AL
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2 &&
                instructionBytes[1] == 0x79 && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    AnalysisDebug.WriteLine($"    КОНТЕЙНЕР: запись [3C79] из AL = 0x{alValue:X2} по адресу 0x{instructionAddress:X4}");
                    AddContainerText(instructionAddress, output, alValue);
                }
            }
        }

        private void AddContainerText(uint instructionAddress, List<TextEntry> output, byte containerIndex)
        {
            AddContainerText(instructionAddress, output, containerIndex, treatZeroAsDestroyed: true, isInferred: false);
        }

        private void AddContainerText(uint instructionAddress, List<TextEntry> output, byte containerIndex, bool treatZeroAsDestroyed, bool isInferred)
        {
            if (containerIndex == 0 && treatZeroAsDestroyed)
            {
                AnalysisDebug.WriteLine($"    КОНТЕЙНЕР УНИЧТОЖЕН: [3C79] = 0x00 (инструкция 0x{instructionAddress:X4})");
                AddOutputText(output, instructionAddress, "!!! Контейнер с лутом на полу уничтожен !!!", TextSemanticKind.LootPayload);
                return;
            }

            if (ContainerDatabase.TryGetContainerName(containerIndex, out string containerName))
            {
                AnalysisDebug.WriteLine($"    КОНТЕЙНЕР РАСПОЗНАН: индекс 0x{containerIndex:X2} -> {containerName} (инструкция 0x{instructionAddress:X4})");
                AddOutputText(output, instructionAddress, $"На ячейке находится {containerName} в котором лежит:", TextSemanticKind.LootContainerIntro, isInferred);
            }
            else
            {
                AnalysisDebug.WriteLine($"    КОНТЕЙНЕР НЕ РАСПОЗНАН: индекс 0x{containerIndex:X2} (инструкция 0x{instructionAddress:X4})");
                AddOutputText(output, instructionAddress, $"На ячейке находится контейнер #{containerIndex} в котором лежит:", TextSemanticKind.LootContainerIntro, isInferred);
            }
        }

        private void ProcessImplicitContainerTextForLoot(uint instructionAddress, byte[] instructionBytes,
            List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            if (tryReadMemory8 == null)
                return;

            bool hasExplicitContainerWrite = IsExplicitContainerWrite(instructionBytes);
            bool hasTrackedContainerValue = tryReadMemory8(0x3C79).HasValue;

            bool hasItem = (tryReadMemory8(0x3C7C) ?? 0) != 0;
            bool hasGold = (tryReadMemory8(0x3C7D) ?? 0) != 0 || (tryReadMemory8(0x3C7E) ?? 0) != 0;
            bool hasGems = (tryReadMemory8(0x3C7F) ?? 0) != 0;

            if (!hasExplicitContainerWrite && !hasTrackedContainerValue && (hasItem || hasGold || hasGems))
            {
                AnalysisDebug.WriteLine($"    КОНТЕЙНЕР: неявный контейнер по луту без явной записи [3C79], используем индекс 0x00 как обычный контейнер (инструкция 0x{instructionAddress:X4})");
                AddImplicitContainerText(instructionAddress, output);
            }
        }

        private bool IsExplicitContainerWrite(byte[] instructionBytes)
        {
            if (instructionBytes == null)
                return false;

            // MOV byte ptr [0x3C79], imm8
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x79 && instructionBytes[3] == 0x3C)
            {
                return true;
            }

            // MOV byte ptr [0x3C79], reg8
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88 &&
                instructionBytes[2] == 0x79 && instructionBytes[3] == 0x3C)
            {
                return true;
            }

            // MOV [0x3C79], AL
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2 &&
                instructionBytes[1] == 0x79 && instructionBytes[2] == 0x3C)
            {
                return true;
            }

            return false;
        }

        private void AddImplicitContainerText(uint instructionAddress, List<TextEntry> output)
        {
            AddContainerText(instructionAddress, output, 0x00, treatZeroAsDestroyed: false, isInferred: true);
        }

        private static bool IsItemAddress(ushort address)
        {
            return address == 0x3C7A || address == 0x3C7B || address == 0x3C7C;
        }

        private void ProcessItemTexts(uint instructionAddress, byte[] instructionBytes, RegisterTracker registerTracker,
            List<TextEntry> output, Func<ushort, byte?> tryReadMemory8)
        {
            // MOV byte ptr [disp16], imm8
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                if (IsItemAddress(memAddr))
                {
                    AnalysisDebug.WriteLine($"    ПРЕДМЕТ: прямая запись [0x{memAddr:X4}] = 0x{instructionBytes[4]:X2} по адресу 0x{instructionAddress:X4}");
                    AddImplicitContainerTextForItemLoot(instructionAddress, instructionBytes, output, tryReadMemory8, memAddr, instructionBytes[4]);
                    AddSingleItemText(instructionAddress, output, instructionBytes[4], memAddr);
                    return;
                }
            }

            // MOV byte ptr [disp16], reg8
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                if (IsItemAddress(memAddr))
                {
                    byte regField = (byte)((instructionBytes[1] >> 3) & 0x07);
                    string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                    if (regField < regNames.Length)
                    {
                        string regName = regNames[regField];
                        if (registerTracker.TryGetByteRegisterValue(regName, out byte value))
                        {
                            AnalysisDebug.WriteLine($"    ПРЕДМЕТ: запись [0x{memAddr:X4}] из {regName} = 0x{value:X2} по адресу 0x{instructionAddress:X4}");
                            AddImplicitContainerTextForItemLoot(instructionAddress, instructionBytes, output, tryReadMemory8, memAddr, value);
                            AddSingleItemText(instructionAddress, output, value, memAddr);
                        }
                        else if (registerTracker.TryGetRegisterRange(regName, out ValueRange8 range))
                        {
                            AnalysisDebug.WriteLine($"    ПРЕДМЕТ: запись [0x{memAddr:X4}] из {regName} в диапазоне 0x{range.Min:X2}-0x{range.Max:X2} по адресу 0x{instructionAddress:X4}");
                            AddImplicitContainerTextForItemLoot(instructionAddress, instructionBytes, output, tryReadMemory8, memAddr, range);
                            AddItemRangeText(instructionAddress, output, range, memAddr);
                        }
                    }
                    return;
                }
            }

            // MOV [moffs8], AL
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2)
            {
                ushort memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                if (IsItemAddress(memAddr))
                {
                    if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                    {
                        AnalysisDebug.WriteLine($"    ПРЕДМЕТ: запись [0x{memAddr:X4}] из AL = 0x{alValue:X2} по адресу 0x{instructionAddress:X4}");
                        AddImplicitContainerTextForItemLoot(instructionAddress, instructionBytes, output, tryReadMemory8, memAddr, alValue);
                        AddSingleItemText(instructionAddress, output, alValue, memAddr);
                    }
                    else if (registerTracker.TryGetRegisterRange("AL", out ValueRange8 range))
                    {
                        AnalysisDebug.WriteLine($"    ПРЕДМЕТ: запись [0x{memAddr:X4}] из AL в диапазоне 0x{range.Min:X2}-0x{range.Max:X2} по адресу 0x{instructionAddress:X4}");
                        AddImplicitContainerTextForItemLoot(instructionAddress, instructionBytes, output, tryReadMemory8, memAddr, range);
                        AddItemRangeText(instructionAddress, output, range, memAddr);
                    }
                }
            }
        }

        private void AddImplicitContainerTextForItemLoot(
            uint instructionAddress,
            byte[] instructionBytes,
            List<TextEntry> output,
            Func<ushort, byte?> tryReadMemory8,
            ushort itemAddress,
            byte itemValue)
        {
            if (itemValue == 0)
                return;

            AddImplicitContainerTextForItemLoot(
                instructionAddress,
                instructionBytes,
                output,
                tryReadMemory8,
                itemAddress);
        }

        private void AddImplicitContainerTextForItemLoot(
            uint instructionAddress,
            byte[] instructionBytes,
            List<TextEntry> output,
            Func<ushort, byte?> tryReadMemory8,
            ushort itemAddress,
            ValueRange8 itemRange)
        {
            if (itemRange == null || itemRange.Max == 0)
                return;

            AddImplicitContainerTextForItemLoot(
                instructionAddress,
                instructionBytes,
                output,
                tryReadMemory8,
                itemAddress);
        }

        private void AddImplicitContainerTextForItemLoot(
            uint instructionAddress,
            byte[] instructionBytes,
            List<TextEntry> output,
            Func<ushort, byte?> tryReadMemory8,
            ushort itemAddress)
        {
            if (tryReadMemory8 == null || itemAddress != 0x3C7C)
                return;

            if (IsExplicitContainerWrite(instructionBytes) || tryReadMemory8(0x3C79).HasValue)
                return;

            AnalysisDebug.WriteLine($"    КОНТЕЙНЕР: неявный контейнер по предмету без явной записи [3C79], используем индекс 0x00 как обычный контейнер (инструкция 0x{instructionAddress:X4})");
            AddImplicitContainerText(instructionAddress, output);
        }

        private void AddSingleItemText(uint instructionAddress, List<TextEntry> output, byte itemIndex, ushort itemAddress)
        {
            string itemText = ResolveItemText(itemIndex, out int databaseIndex, out string debugStatus);

            if (itemIndex == 0)
            {
                AnalysisDebug.WriteLine($"    ПРЕДМЕТ УНИЧТОЖЕН: [0x{itemAddress:X4}] = 0x00 (инструкция 0x{instructionAddress:X4})");
            }
            else if (itemText == "Нет предмета")
            {
                AnalysisDebug.WriteLine($"    ПРЕДМЕТ ОТСУТСТВУЕТ: индекс 0x{itemIndex:X2} -> запись #{databaseIndex} ({debugStatus}) через [0x{itemAddress:X4}] (инструкция 0x{instructionAddress:X4})");
            }
            else
            {
                AnalysisDebug.WriteLine($"    ПРЕДМЕТ РАСПОЗНАН: индекс 0x{itemIndex:X2} -> запись #{databaseIndex} -> {itemText} через [0x{itemAddress:X4}] (инструкция 0x{instructionAddress:X4})");
            }

            AddOutputText(output, instructionAddress, itemText, TextSemanticKind.LootPayload);
        }

        private void AddItemRangeText(uint instructionAddress, List<TextEntry> output, ValueRange8 range, ushort itemAddress)
        {
            int totalValues = range.Max - range.Min + 1;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int raw = range.Min; raw <= range.Max; raw++)
            {
                byte itemIndex = (byte)raw;
                string itemText = ResolveItemText(itemIndex, out int databaseIndex, out string debugStatus);

                if (itemText == "Нет предмета")
                    AnalysisDebug.WriteLine($"    ПРЕДМЕТ ДИАПАЗОН: [0x{itemAddress:X4}] 0x{itemIndex:X2} -> Нет предмета (запись #{databaseIndex}, {debugStatus})");
                else
                    AnalysisDebug.WriteLine($"    ПРЕДМЕТ ДИАПАЗОН: [0x{itemAddress:X4}] 0x{itemIndex:X2} -> {itemText} (запись #{databaseIndex})");

                if (!counts.ContainsKey(itemText))
                    counts[itemText] = 0;
                counts[itemText]++;
            }

            AnalysisDebug.WriteLine($"    ПРЕДМЕТ РАСПОЗНАН ПО ДИАПАЗОНУ: [0x{itemAddress:X4}] 0x{range.Min:X2}-0x{range.Max:X2} -> {counts.Count} итоговых вариантов (инструкция 0x{instructionAddress:X4})");

            AddOutputText(output, instructionAddress, "Случайный предмет:", TextSemanticKind.LootPayload);

            foreach (var pair in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                double probability = totalValues > 0 ? pair.Value * 100.0 / totalValues : 0.0;
                AddOutputText(output, instructionAddress, $"{pair.Key} ({FormatProbability(probability)})", TextSemanticKind.LootPayload);
            }
        }

        private string ResolveItemText(byte itemIndex, out int databaseIndex, out string debugStatus)
        {
            if (itemIndex == 0)
            {
                databaseIndex = -1;
                debugStatus = "destroyed";
                return "!!! Предмет на полу уничтожен !!!";
            }

            databaseIndex = itemIndex - 1;
            if (ItemDatabase.TryGetItemNameByGameItemCode(itemIndex, out string itemName) && !string.IsNullOrWhiteSpace(itemName))
            {
                debugStatus = "resolved";
                return itemName.Trim();
            }

            debugStatus = "not found in ItemDatabase";
            return "Нет предмета";
        }

        private string FormatProbability(double probability)
        {
            return ProbabilityFormatter.FormatPercent(probability) + "%";
        }

        private void ProcessGemsTexts(uint instructionAddress, byte[] instructionBytes, RegisterTracker registerTracker,
            List<TextEntry> output)
        {
            // MOV byte ptr [0x3C7F], imm8
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x7F && instructionBytes[3] == 0x3C)
            {
                AnalysisDebug.WriteLine($"    GEMS: прямая запись [3C7F] = 0x{instructionBytes[4]:X2} по адресу 0x{instructionAddress:X4}");
                AddGemsText(instructionAddress, output, instructionBytes[4]);
                return;
            }

            // MOV byte ptr [0x3C7F], reg8
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88 &&
                instructionBytes[2] == 0x7F && instructionBytes[3] == 0x3C)
            {
                byte regField = (byte)((instructionBytes[1] >> 3) & 0x07);
                string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (regField < regNames.Length &&
                    registerTracker.TryGetByteRegisterValue(regNames[regField], out byte value))
                {
                    AnalysisDebug.WriteLine($"    GEMS: запись [3C7F] из {regNames[regField]} = 0x{value:X2} по адресу 0x{instructionAddress:X4}");
                    AddGemsText(instructionAddress, output, value);
                }
                return;
            }

            // MOV [0x3C7F], AL
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2 &&
                instructionBytes[1] == 0x7F && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    AnalysisDebug.WriteLine($"    GEMS: запись [3C7F] из AL = 0x{alValue:X2} по адресу 0x{instructionAddress:X4}");
                    AddGemsText(instructionAddress, output, alValue);
                }
            }
        }

        private void AddGemsText(uint instructionAddress, List<TextEntry> output, byte gemsValue)
        {
            if (gemsValue == 0)
            {
                AnalysisDebug.WriteLine($"    GEMS УНИЧТОЖЕНЫ: [3C7F] = 0x00 (инструкция 0x{instructionAddress:X4})");
                AddOutputText(output, instructionAddress, "!!! GEMS на полу уничтожены !!!", TextSemanticKind.LootPayload);
                return;
            }

            AnalysisDebug.WriteLine($"    GEMS РАСПОЗНАНЫ: {gemsValue} GEMS (инструкция 0x{instructionAddress:X4})");
            AddOutputText(output, instructionAddress, $"{gemsValue} GEMS", TextSemanticKind.LootPayload);
        }

        private void ProcessGoldTexts(uint instructionAddress, byte[] instructionBytes, RegisterTracker registerTracker,
            List<TextEntry> output, Func<ushort, byte?> tryReadMemory8 = null)
        {
            if (TryGetDirectByteMemoryWrite(instructionBytes, registerTracker, out ushort memAddr, out byte writtenValue) &&
                (memAddr == 0x3C7D || memAddr == 0x3C7E))
            {
                byte? lowByte = memAddr == 0x3C7D ? writtenValue : tryReadMemory8?.Invoke(0x3C7D);
                byte? highByte = memAddr == 0x3C7E ? writtenValue : tryReadMemory8?.Invoke(0x3C7E);

                string partName = memAddr == 0x3C7D ? "low" : "high";
                AnalysisDebug.WriteLine($"    GOLD: обновлён {partName}-byte [{memAddr:X4}] = 0x{writtenValue:X2} по адресу 0x{instructionAddress:X4}");

                if (!lowByte.HasValue || !highByte.HasValue)
                {
                    AnalysisDebug.WriteLine($"    GOLD: ждём вторую половину значения: low={(lowByte.HasValue ? $"0x{lowByte.Value:X2}" : "??")}, high={(highByte.HasValue ? $"0x{highByte.Value:X2}" : "??")}");
                    return;
                }

                AddGoldText(instructionAddress, output, lowByte.Value, highByte.Value);
                return;
            }

            if (TryGetDirectWordMemoryWrite(instructionBytes, registerTracker, out ushort wordMemAddr, out ushort wordValue))
            {
                if (wordMemAddr == 0x3C7D)
                {
                    AnalysisDebug.WriteLine($"    GOLD: обновлено word-значение [3C7D:3C7E] = 0x{wordValue:X4} по адресу 0x{instructionAddress:X4}");
                    AddGoldText(instructionAddress, output, (byte)(wordValue & 0x00FF), (byte)(wordValue >> 8));
                    return;
                }

                if (wordMemAddr == 0x3C7E)
                {
                    byte lowByte = tryReadMemory8?.Invoke(0x3C7D) ?? 0;
                    byte highByte = (byte)(wordValue & 0x00FF);

                    AnalysisDebug.WriteLine($"    GOLD: word-запись начата с [3C7E], используем младший байт 0x{highByte:X2} и текущий [3C7D]=0x{lowByte:X2} по адресу 0x{instructionAddress:X4}");
                    AddGoldText(instructionAddress, output, lowByte, highByte);
                }
            }
        }

        private void AddGoldText(uint instructionAddress, List<TextEntry> output, byte lowByte, byte highByte)
        {
            ushort rawGoldValue = (ushort)(lowByte | (highByte << 8));
            int goldValue = rawGoldValue >> 1;

            if (rawGoldValue == 0)
            {
                AnalysisDebug.WriteLine($"    GOLD УНИЧТОЖЕНО: [3C7D:3C7E] = 0x0000 (инструкция 0x{instructionAddress:X4})");
                AddOutputText(output, instructionAddress, "!!! GOLD на полу уничтожено !!!", TextSemanticKind.LootPayload);
                return;
            }

            AnalysisDebug.WriteLine($"    GOLD РАСПОЗНАНО: raw=0x{rawGoldValue:X4} -> {goldValue} GOLD (инструкция 0x{instructionAddress:X4})");
            AddOutputText(output, instructionAddress, $"{goldValue} GOLD", TextSemanticKind.LootPayload);
        }

        private bool TryGetDirectByteMemoryWrite(byte[] instructionBytes, RegisterTracker registerTracker,
            out ushort memAddr, out byte value)
        {
            memAddr = 0;
            value = 0;

            // MOV byte ptr [disp16], imm8
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06)
            {
                memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                value = instructionBytes[4];
                return true;
            }

            // MOV byte ptr [disp16], reg8
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88 && instructionBytes[1] == 0x06)
            {
                string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                byte regField = (byte)((instructionBytes[1] >> 3) & 0x07);
                if (regField >= regNames.Length)
                    return false;

                memAddr = BitConverter.ToUInt16(instructionBytes, 2);
                return registerTracker.TryGetByteRegisterValue(regNames[regField], out value);
            }

            // MOV [moffs8], AL
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA2)
            {
                memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                return registerTracker.TryGetByteRegisterValue("AL", out value);
            }

            return false;
        }

        private bool TryGetDirectWordMemoryWrite(byte[] instructionBytes, RegisterTracker registerTracker,
            out ushort memAddr, out ushort value)
        {
            memAddr = 0;
            value = 0;

            // MOV word ptr r/m16, imm16
            if (instructionBytes.Length >= 4 && instructionBytes[0] == 0xC7)
            {
                byte modRm = instructionBytes[1];
                byte regField = (byte)((modRm >> 3) & 0x07);
                if (regField == 0 &&
                    TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out memAddr, out int decodedLength) &&
                    instructionBytes.Length >= decodedLength + 2)
                {
                    value = BitConverter.ToUInt16(instructionBytes, decodedLength);
                    return true;
                }
            }

            // MOV word ptr r/m16, reg16
            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0x89)
            {
                byte modRm = instructionBytes[1];
                byte mod = (byte)((modRm >> 6) & 0x03);
                if (mod != 0x03 &&
                    TryDecode16BitEffectiveAddress(instructionBytes, registerTracker, out memAddr, out _) &&
                    TryGetReg16ValueFromModRmRegField(modRm, registerTracker, out value))
                {
                    return true;
                }
            }

            // MOV [moffs16], AX
            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xA3)
            {
                memAddr = BitConverter.ToUInt16(instructionBytes, 1);
                return registerTracker.TryGetRegisterValue("AX", out value);
            }

            return false;
        }

        private bool TryDecode16BitEffectiveAddress(byte[] instructionBytes, RegisterTracker registerTracker,
            out ushort effectiveAddress, out int decodedLength)
        {
            effectiveAddress = 0;
            decodedLength = 0;

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

            if (!TryGet16BitAddressBase(registerTracker, rm, out ushort baseValue))
                return false;

            int signedDisp = dispSize == 1 ? disp8 : (dispSize == 2 ? disp16 : 0);
            effectiveAddress = unchecked((ushort)(baseValue + signedDisp));
            decodedLength = 2 + dispSize;
            return true;
        }

        private bool TryGet16BitAddressBase(RegisterTracker registerTracker, byte rm, out ushort baseValue)
        {
            baseValue = 0;

            switch (rm)
            {
                case 0x00:
                    return registerTracker.TryGetRegisterValue("BX", out ushort bx0) &&
                           registerTracker.TryGetRegisterValue("SI", out ushort si0) &&
                           TryCombineAddressParts(bx0, si0, out baseValue);
                case 0x01:
                    return registerTracker.TryGetRegisterValue("BX", out ushort bx1) &&
                           registerTracker.TryGetRegisterValue("DI", out ushort di1) &&
                           TryCombineAddressParts(bx1, di1, out baseValue);
                case 0x02:
                    return registerTracker.TryGetRegisterValue("BP", out ushort bp2) &&
                           registerTracker.TryGetRegisterValue("SI", out ushort si2) &&
                           TryCombineAddressParts(bp2, si2, out baseValue);
                case 0x03:
                    return registerTracker.TryGetRegisterValue("BP", out ushort bp3) &&
                           registerTracker.TryGetRegisterValue("DI", out ushort di3) &&
                           TryCombineAddressParts(bp3, di3, out baseValue);
                case 0x04:
                    return registerTracker.TryGetRegisterValue("SI", out baseValue);
                case 0x05:
                    return registerTracker.TryGetRegisterValue("DI", out baseValue);
                case 0x06:
                    return registerTracker.TryGetRegisterValue("BP", out baseValue);
                case 0x07:
                    return registerTracker.TryGetRegisterValue("BX", out baseValue);
                default:
                    return false;
            }
        }

        private bool TryCombineAddressParts(ushort a, ushort b, out ushort sum)
        {
            sum = unchecked((ushort)(a + b));
            return true;
        }

        private bool TryGetReg16ValueFromModRmRegField(byte modRm, RegisterTracker registerTracker, out ushort value)
        {
            value = 0;
            string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
            byte regField = (byte)((modRm >> 3) & 0x07);
            return regField < regNames.Length && registerTracker.TryGetRegisterValue(regNames[regField], out value);
        }



        private void ProcessLootDestructionPatterns(uint instructionAddress, byte[] instructionBytes,
            RegisterTracker registerTracker, List<TextEntry> output)
        {
            // MOV byte ptr [BX+3C77], AL / reg8
            if (instructionBytes.Length >= 4 && instructionBytes[0] == 0x88 && instructionBytes[2] == 0x77 && instructionBytes[3] == 0x3C)
            {
                if (!TryGetWrittenByteValue(instructionBytes, registerTracker, out byte value))
                    return;

                if (value != 0)
                    return;

                if (!registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                    bxValue = 0;

                if (bxValue == 0)
                {
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕН ВОЗМОЖНЫЙ ЦИКЛ ОЧИСТКИ ЛУТА 3C77-3C7F (инструкция 0x{instructionAddress:X4})");
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C79);
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C7A);
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C7B);
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C7C);
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C7D);
                    AddLootDestructionByAddress(instructionAddress, output, 0x3C7F);
                    return;
                }

                ushort effectiveAddress = (ushort)(0x3C77 + bxValue);
                AddLootDestructionByAddress(instructionAddress, output, effectiveAddress);
                return;
            }

            // MOV byte ptr [disp16+BX], AL / reg8 with direct loot offsets too
            if (instructionBytes.Length >= 4 && instructionBytes[0] == 0x88)
            {
                if (!TryGetWrittenByteValue(instructionBytes, registerTracker, out byte value))
                    return;

                if (value != 0)
                    return;

                ushort displacement = BitConverter.ToUInt16(instructionBytes, 2);
                AddLootDestructionByAddress(instructionAddress, output, displacement);
            }
        }

        private bool TryGetWrittenByteValue(byte[] instructionBytes, RegisterTracker registerTracker, out byte value)
        {
            value = 0;

            if (instructionBytes.Length < 2)
                return false;

            byte regField = (byte)((instructionBytes[1] >> 3) & 0x07);
            string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

            return regField < regNames.Length &&
                   registerTracker.TryGetByteRegisterValue(regNames[regField], out value);
        }

        private void AddLootDestructionByAddress(uint instructionAddress, List<TextEntry> output, ushort address)
        {
            switch (address)
            {
                case 0x3C79:
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕНО УНИЧТОЖЕНИЕ ЛУТА: [3C79] = 0x00 (инструкция 0x{instructionAddress:X4})");
                    AddOutputText(output, instructionAddress, "!!! Контейнер с лутом на полу уничтожен !!!", TextSemanticKind.LootPayload);
                    break;
                case 0x3C7A:
                case 0x3C7B:
                case 0x3C7C:
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕНО УНИЧТОЖЕНИЕ ЛУТА: [0x{address:X4}] = 0x00 (инструкция 0x{instructionAddress:X4})");
                    AddOutputText(output, instructionAddress, "!!! Предмет на полу уничтожен !!!", TextSemanticKind.LootPayload);
                    break;
                case 0x3C7D:
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕНО УНИЧТОЖЕНИЕ ЛУТА: [3C7D] = 0x00 (инструкция 0x{instructionAddress:X4})");
                    AddOutputText(output, instructionAddress, "!!! GOLD на полу уничтожено !!!", TextSemanticKind.LootPayload);
                    break;
                case 0x3C7F:
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕНО УНИЧТОЖЕНИЕ ЛУТА: [3C7F] = 0x00 (инструкция 0x{instructionAddress:X4})");
                    AddOutputText(output, instructionAddress, "!!! GEMS на полу уничтожены !!!", TextSemanticKind.LootPayload);
                    break;
            }
        }

        public void FindMonsterStatChanges(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, PathAnalysisResult result)
        {
            byte[] instructionBytes = insn.Bytes;

            // MOV byte ptr [C961], imm8 - максимальная сила случайных монстров
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                byte randomEncounterMonsterPowerCap = instructionBytes[4];
                result.RandomEncounterMonsterPowerCap = randomEncounterMonsterPowerCap;
                AnalysisDebug.WriteLine($"    УСТАНОВЛЕНА МАКСИМАЛЬНАЯ СИЛА СЛУЧАЙНЫХ МОНСТРОВ: {randomEncounterMonsterPowerCap}");
            }
            // MOV byte ptr [C96F], imm8 - максимальный уровень случайных монстров
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                byte randomEncounterMonsterLevelCap = instructionBytes[4];
                result.RandomEncounterMonsterLevelCap = randomEncounterMonsterLevelCap;
                AnalysisDebug.WriteLine($"    УСТАНОВЛЕН МАКСИМАЛЬНЫЙ УРОВЕНЬ СЛУЧАЙНЫХ МОНСТРОВ: {randomEncounterMonsterLevelCap}");
            }
            // MOV byte ptr [C96E], imm8 - флаги карты; bit 0 включает затемнение
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x6E && instructionBytes[3] == 0xC9)
            {
                byte mapFlags = instructionBytes[4];
                byte darkeningLevel = OvrMapFlags.GetDarknessValue(mapFlags);
                result.DarkeningLevel = darkeningLevel;
                AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ФЛАГ ЗАТЕМНЕНИЯ: {darkeningLevel} (raw 0x{mapFlags:X2})");
            }
            // MOV byte ptr [C962], imm8 - максимальное количество случайных монстров в группе
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x62 && instructionBytes[3] == 0xC9)
            {
                byte randomEncounterMonsterBatchCountCap = instructionBytes[4];
                result.RandomEncounterMonsterBatchCountCap = randomEncounterMonsterBatchCountCap;
                AnalysisDebug.WriteLine($"    УСТАНОВЛЕНО МАКСИМАЛЬНОЕ КОЛИЧЕСТВО СЛУЧАЙНЫХ МОНСТРОВ В ГРУППЕ: {randomEncounterMonsterBatchCountCap}");
            }
            // MOV [C961], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.RandomEncounterMonsterPowerCap = chValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕНА МАКСИМАЛЬНАЯ СИЛА СЛУЧАЙНЫХ МОНСТРОВ ИЗ CH: {chValue}");
                }
            }
            // MOV [C96F], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.RandomEncounterMonsterLevelCap = chValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН МАКСИМАЛЬНЫЙ УРОВЕНЬ СЛУЧАЙНЫХ МОНСТРОВ ИЗ CH: {chValue}");
                }
            }
            // MOV [C96E], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x6E && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    byte darkeningLevel = OvrMapFlags.GetDarknessValue(chValue);
                    result.DarkeningLevel = darkeningLevel;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ФЛАГ ЗАТЕМНЕНИЯ ИЗ CH: {darkeningLevel} (raw 0x{chValue:X2})");
                }
            }
            // MOV [C962], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x62 && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.RandomEncounterMonsterBatchCountCap = chValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕНО МАКСИМАЛЬНОЕ КОЛИЧЕСТВО СЛУЧАЙНЫХ МОНСТРОВ В ГРУППЕ ИЗ CH: {chValue}");
                }
            }
            // MOV [C961], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x61 && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.RandomEncounterMonsterPowerCap = alValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕНА МАКСИМАЛЬНАЯ СИЛА СЛУЧАЙНЫХ МОНСТРОВ ИЗ AL: {alValue}");
                }
            }
            // MOV [C96F], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x6F && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.RandomEncounterMonsterLevelCap = alValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН МАКСИМАЛЬНЫЙ УРОВЕНЬ СЛУЧАЙНЫХ МОНСТРОВ ИЗ AL: {alValue}");
                }
            }
            // MOV [C96E], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x6E && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    byte darkeningLevel = OvrMapFlags.GetDarknessValue(alValue);
                    result.DarkeningLevel = darkeningLevel;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ФЛАГ ЗАТЕМНЕНИЯ ИЗ AL: {darkeningLevel} (raw 0x{alValue:X2})");
                }
            }
            // MOV [C962], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x62 && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.RandomEncounterMonsterBatchCountCap = alValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕНО МАКСИМАЛЬНОЕ КОЛИЧЕСТВО СЛУЧАЙНЫХ МОНСТРОВ В ГРУППЕ ИЗ AL: {alValue}");
                }
            }
            // MOV byte ptr [C95D], imm8 - шанс случайной встречи
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x5D && instructionBytes[3] == 0xC9)
            {
                byte encounterChance = instructionBytes[4];
                result.RandomEncounterChance = encounterChance;
                AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ШАНС СЛУЧАЙНОЙ ВСТРЕЧИ: 0x{encounterChance:X2}");
            }
            // MOV [C95D], CH
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                     instructionBytes[2] == 0x5D && instructionBytes[3] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("CH", out byte chValue))
                {
                    result.RandomEncounterChance = chValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ШАНС СЛУЧАЙНОЙ ВСТРЕЧИ ИЗ CH: 0x{chValue:X2}");
                }
            }
            // MOV [C95D], AL
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x5D && instructionBytes[2] == 0xC9)
            {
                if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.RandomEncounterChance = alValue;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕН ШАНС СЛУЧАЙНОЙ ВСТРЕЧИ ИЗ AL: 0x{alValue:X2}");
                }
            }
        }

        public void FindMonsterBattleInfo(X86Instruction insn, BinaryReader br,
            RegisterTracker registerTracker, int depth, PathAnalysisResult result,
            byte targetX, byte targetY, Func<ushort, byte?> tryReadMemory8 = null)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // Обнаружение цикла по счётчику BL и лимиту в [3C1D]
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
            {
                result.IsInLoop = true;
                result.LoopSemantic = LoopSemanticKind.PartialBattle;
                result.LoopStartAddress = address;

                byte? knownLoopLimit = tryReadMemory8?.Invoke(0x3C1D);
                bool blKnown = registerTracker.TryGetByteRegisterValue("BL", out byte blValue);
                bool hasExactKnownLoopLimit = knownLoopLimit.HasValue && !result.IsBattleMonsterCountIndeterminate;
                bool hasKnownRangeLoopLimit = result.BattleMonsterCountRange != null && !result.IsBattleMonsterCountIndeterminate;

                if ((hasExactKnownLoopLimit || hasKnownRangeLoopLimit) && blKnown)
                {
                    result.IsIndeterminateLoop = false;
                    string limitText = hasExactKnownLoopLimit
                        ? $"0x{knownLoopLimit.Value:X2}"
                        : $"{result.BattleMonsterCountRange.Min}-{result.BattleMonsterCountRange.Max}";
                    AnalysisDebug.WriteLine($"    ОБНАРУЖЕН ЦИКЛ С ИЗВЕСТНОЙ ГРАНИЦЕЙ по адресу 0x{address:X4}, BL=0x{blValue:X2}, [3C1D]={limitText} -> random count не выставляется");
                    return;
                }

                result.IsIndeterminateLoop = true;

                int markedCount = 0;

                // Для полной битвы хвост неопределённого цикла формируется ПОСЛЕ уже записанной
                // итерации. К моменту CMP BL,[3C1D] в BL находится индекс следующей записи,
                // поэтому запись с индексом BL-1 — это последняя фактически добавленная запись,
                // которая уже принадлежит случайному хвосту.
                if (blKnown && blValue > 0)
                {
                    int lastWrittenIndex = blValue - 1;
                    if (result.BattleMonsterEntries.ContainsKey(lastWrittenIndex))
                    {
                        var entry = result.BattleMonsterEntries[lastWrittenIndex];
                        if (!entry.isIndeterminate)
                        {
                            result.BattleMonsterEntries[lastWrittenIndex] = (entry.val1, entry.val2, true);
                            markedCount++;
                        }
                    }
                }
                else
                {
                    // Fallback для старых/неполных случаев: помечаем неполные записи,
                    // как и раньше, чтобы не ломать существующую логику.
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
                }

                AnalysisDebug.WriteLine($"    ОБНАРУЖЕН НЕОПРЕДЕЛЁННЫЙ ЦИКЛ по адресу 0x{address:X4}, BL={(blKnown ? $"0x{blValue:X2}" : "??")}, [3C1D]={(knownLoopLimit.HasValue ? $"0x{knownLoopLimit.Value:X2}" : "??")}, помечено {markedCount} записей как неопределенные");
                return;
            }

            // MOV [3C1D], AL - количество монстров в полной битве
            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0xA2 &&
                instructionBytes[1] == 0x1D && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetRegisterRange("AL", out var countRange))
                {
                    result.BattleMonsterCountRange = new ValueRange8(countRange.Min, countRange.Max);
                    result.BattleMonsterCount = countRange.IsExact ? countRange.Min : (byte?)null;
                    result.IsBattleMonsterCountIndeterminate = false;
                    AnalysisDebug.WriteLine(countRange.IsExact
                        ? $"    УСТАНОВЛЕНО КОЛИЧЕСТВО МОНСТРОВ: {countRange.Min}"
                        : $"    УСТАНОВЛЕН ДИАПАЗОН КОЛИЧЕСТВА МОНСТРОВ: {countRange.Min}-{countRange.Max}");
                }
                else if (registerTracker.IsRegisterExternallyDerived("AX"))
                {
                    result.BattleMonsterCount = null;
                    result.BattleMonsterCountRange = null;
                    result.IsBattleMonsterCountIndeterminate = true;
                    AnalysisDebug.WriteLine("    КОЛИЧЕСТВО МОНСТРОВ НЕОПРЕДЕЛЕНО (AL зависит от арифметики с результатом внешнего CALL)");
                }
                else if (registerTracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    result.BattleMonsterCount = alValue;
                    result.BattleMonsterCountRange = new ValueRange8(alValue, alValue);
                    result.IsBattleMonsterCountIndeterminate = false;
                    AnalysisDebug.WriteLine($"    УСТАНОВЛЕНО КОЛИЧЕСТВО МОНСТРОВ: {alValue}");
                }
                else
                {
                    result.BattleMonsterCount = null;
                    result.BattleMonsterCountRange = null;
                    result.IsBattleMonsterCountIndeterminate = true;
                    AnalysisDebug.WriteLine("    КОЛИЧЕСТВО МОНСТРОВ НЕОПРЕДЕЛЕНО (AL неизвестен)");
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

            // ========== ТАБЛИЦЫ ДИСКРЕТНЫХ ШАБЛОНОВ БИТВ (CA7F+ И CA84+) ==========

            // Загрузка из таблицы CA7F+ (MOV AL, [BX+CA7F]) - первый индекс монстра
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8A &&
                     instructionBytes[1] == 0x87 &&
                     instructionBytes[2] == 0x7F && instructionBytes[3] == 0xCA)
            {
                ProcessLoadFromTableCA7F(instructionBytes, registerTracker, address, br);
                return;
            }

            // Загрузка из таблицы CA84+ (MOV BP, [BX+CA84]) - второй индекс монстра в младшем байте
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xAF &&
                     instructionBytes[2] == 0x84 && instructionBytes[3] == 0xCA)
            {
                ProcessLoadFromTableCA84(instructionBytes, registerTracker, address, br);
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
                ushort effectiveBx = bxValue;
                string debugInfo = "";

                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    debugInfo = $" (using targetX={targetX})";
                }

                ushort sourceAddr = (ushort)(0xCDBD + effectiveBx);
                byte actualValue = 0;
                bool readSuccess = false;

                if (bxValue == 0 && targetX != 0)
                    AnalysisDebug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {sourceAddr:X4}");

                try
                {
                    readSuccess = TryReadOverlayByte(br, sourceAddr, out actualValue);
                    if (readSuccess)
                        AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}]: 0x{actualValue:X2}{debugInfo}");
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV AL, [BX+CDBD] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4}, val={actualValue:X2})",
                    "CDBD",
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDBD+: AL = [BX+CDBD] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4})");
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
                ushort effectiveBx = bxValue;
                string debugInfo = "";

                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    debugInfo = $" (using targetX={targetX})";
                    AnalysisDebug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {(ushort)(0xCDB5 + effectiveBx):X4}");
                }

                ushort sourceAddr = (ushort)(0xCDB5 + effectiveBx);
                ushort actualValue = 0;
                bool readSuccess = false;

                try
                {
                    readSuccess = TryReadOverlayWord(br, sourceAddr, out actualValue);
                    if (readSuccess)
                    {
                        byte lowByte = (byte)(actualValue & 0xFF);
                        byte highByte = (byte)(actualValue >> 8);
                        AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}]: 0x{actualValue:X4}{debugInfo} (low=0x{lowByte:X2}, high=0x{highByte:X2})");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "BP",
                    readSuccess ? actualValue : (ushort)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV BP, [BX+CDB5] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X4") : "0")})",
                    "CDB5",
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB5+: BP = [BX+CDB5] (BX={bxValue}{debugInfo}, addr={sourceAddr:X4})");
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
                ushort effectiveBx = bxValue;
                string debugInfo = "";

                if (effectiveBx == 0 && targetX != 0)
                {
                    effectiveBx = targetX;
                    debugInfo = $" (using targetX={targetX})";
                    AnalysisDebug.WriteLine($"    BX=0, используем targetX={targetX} для вычисления адреса {(ushort)(0xCDB5 + effectiveBx):X4}");
                }

                ushort sourceAddr = (ushort)(0xCDB5 + effectiveBx);
                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayByte(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2}{debugInfo}");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    (ushort)effectiveBx,
                    true,
                    address,
                    $"MOV AL, [BX+CDB5] (BX={bxValue}{debugInfo}, effBX={effectiveBx}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDB5",
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB5+ (младший байт): AL = [BX+CDB5] (BX={bxValue}{debugInfo}, effBX={effectiveBx}, addr={sourceAddr:X4})");
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
                bool sourceIndexExternallyDerived = tracker.HasPendingExternalCallResult("BX") || tracker.IsRegisterExternallyDerived("BX");
                ushort sourceAddr = (ushort)(0xCDA9 + bxValue);
                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayByte(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2}");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV AL, [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDA9",
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDA9+: AL = [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4})");
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
                bool sourceIndexExternallyDerived = tracker.HasPendingExternalCallResult("BX") || tracker.IsRegisterExternallyDerived("BX");
                ushort sourceAddr = (ushort)(0xCDB1 + bxValue);
                ushort actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayWord(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X4}");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "BP",
                    readSuccess ? actualValue : (ushort)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV BP, [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X4") : "0")})",
                    "CDB1",
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                // УДАЛЕНО: автоматическая установка AL из младшего байта.
                // Младший байт будет доступен позже, если произойдет MOV CX, BP и затем использование CL.

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB1+: BP = [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4})");
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
                bool sourceIndexExternallyDerived = tracker.HasPendingExternalCallResult("BX") || tracker.IsRegisterExternallyDerived("BX");
                ushort sourceAddr = (ushort)(0xCDB1 + bxValue);
                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayByte(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2} (младший байт)");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV AL, [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CDB1",
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB1+ (младший байт): AL = [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка из таблицы CA7F+ (MOV AL, [BX+CA7F]) - первый индекс монстра
        /// для случайно выбираемого шаблона группы.
        /// </summary>
        private void ProcessLoadFromTableCA7F(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                bool sourceIndexExternallyDerived = tracker.HasPendingExternalCallResult("BX") || tracker.IsRegisterExternallyDerived("BX");
                ushort sourceAddr = (ushort)(0xCA7F + bxValue);
                byte actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayByte(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X2}");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "AL",
                    readSuccess ? actualValue : (byte)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV AL, [BX+CA7F] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})",
                    "CA7F",
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CA7F+: AL = [BX+CA7F] (BX={bxValue}, addr={sourceAddr:X4})");
            }
        }

        /// <summary>
        /// Загрузка из таблицы CA84+ (MOV BP, [BX+CA84]) - второй индекс монстра
        /// хранится в младшем байте и позже маскируется через AND BP,0xFF.
        /// </summary>
        private void ProcessLoadFromTableCA84(byte[] bytes, RegisterTracker tracker,
            uint address, BinaryReader br)
        {
            if (tracker.TryGetRegisterValue("BX", out ushort bxValue))
            {
                bool sourceIndexExternallyDerived = tracker.HasPendingExternalCallResult("BX") || tracker.IsRegisterExternallyDerived("BX");
                ushort sourceAddr = (ushort)(0xCA84 + bxValue);
                ushort actualValue = 0;
                bool readSuccess = false;

                try
                {
                    if (TryMapOverlayAddressToFileOffset(br, sourceAddr, out long fileOffset))
                    {
                        readSuccess = TryReadOverlayWord(br, sourceAddr, out actualValue);
                        if (readSuccess)
                            AnalysisDebug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X4}");
                    }
                    else
                    {
                        AnalysisDebug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: адрес не сопоставлен с файлом");
                    }
                }
                catch (Exception ex)
                {
                    AnalysisDebug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                }

                tracker.SetRegisterValueWithSource(
                    "BP",
                    readSuccess ? actualValue : (ushort)0,
                    sourceAddr,
                    bxValue,
                    true,
                    address,
                    $"MOV BP, [BX+CA84] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X4") : "0")})",
                    "CA84",
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BX") ?? tracker.GetSourceAddress("BX")
                );

                AnalysisDebug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CA84+: BP = [BX+CA84] (BX={bxValue}, addr={sourceAddr:X4})");
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
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("BP");
                tracker.TryGetRegisterValue("BP", out ushort bpValue);

                tracker.SetRegisterValueWithSource(
                    "CX",
                    bpValue,
                    sourceAddr ?? 0,
                    originalBx ?? 0,
                    true,
                    address,
                    $"MOV CX, BP (копирование из таблицы, val={bpValue:X4})",
                    sourceTable,
                    sourceIndexExternallyDerived: sourceIndexExternallyDerived,
                    sourceIndexProviderAddr: tracker.GetSourceIndexProviderAddress("BP")
                );

                // Обновляем частичные регистры
                byte clValue = (byte)(bpValue & 0xFF);
                byte chValue = (byte)(bpValue >> 8);

                tracker.TrackPartialRegisterOperation("CX", "CL", clValue, address, "MOV CL, low byte of BP", preserveSourceMetadata: true);
                tracker.TrackPartialRegisterOperation("CX", "CH", chValue, address, "MOV CH, high byte of BP", preserveSourceMetadata: true);

                AnalysisDebug.WriteLine($"    КОПИРОВАНИЕ ИЗ ТАБЛИЦЫ: CX = BP (source={sourceAddr:X4}, table={sourceTable}, originalBX={originalBx}, val={bpValue:X4})");
            }
            else
            {
                // Обычное копирование без источника из таблицы
                if (tracker.TryGetRegisterValue("BP", out ushort bpValue))
                {
                    tracker.SetRegisterValue("CX", bpValue, address, $"MOV CX, BP (0x{bpValue:X4})");

                    byte clValue = (byte)(bpValue & 0xFF);
                    byte chValue = (byte)(bpValue >> 8);
                    tracker.TrackPartialRegisterOperation("CX", "CL", clValue, address, "MOV CL, low byte of BP", preserveSourceMetadata: true);
                    tracker.TrackPartialRegisterOperation("CX", "CH", chValue, address, "MOV CH, high byte of BP", preserveSourceMetadata: true);

                    AnalysisDebug.WriteLine($"    КОПИРОВАНИЕ: CX = BP (0x{bpValue:X4})");
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

            // Прямое сохранение в [3C29] из CL (без BX)
            else if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x0E && bytes[2] == 0x29 && bytes[3] == 0x3C)
            {
                ProcessDirectSaveTo3C29FromCL(bytes, tracker, result, address);
                return;
            }
        }

        private static ushort? ComputeSourceTableBaseAddress(ushort? sourceAddr, ushort? originalBx)
        {
            if (!sourceAddr.HasValue)
                return null;

            int baseAddress = sourceAddr.Value - (originalBx ?? 0);
            if (baseAddress < 0 || baseAddress > ushort.MaxValue)
                return null;

            return (ushort)baseAddress;
        }

        private static BattleSourceIndexBehavior ResolveInitialBattleSourceIndexBehavior(
            ushort? originalBx,
            ushort? sourceIndexProviderAddr,
            bool sourceIndexExternallyDerived)
        {
            if (sourceIndexExternallyDerived)
                return BattleSourceIndexBehavior.ExternalRandom;

            if (sourceIndexProviderAddr.HasValue || originalBx.HasValue)
                return BattleSourceIndexBehavior.Fixed;

            return BattleSourceIndexBehavior.Unknown;
        }

        private void RecordPartialBattleSave(
            PathAnalysisResult result,
            int saveIndex,
            ushort destAddr,
            string srcReg,
            byte srcRegValue,
            ushort? sourceAddr,
            ushort? originalBx,
            string sourceTable,
            bool sourceIndexExternallyDerived,
            ushort? sourceIndexProviderAddr = null,
            byte? rangeEnd = null,
            bool isFromTable = true,
            ValueRange8 sourceIndexRange = null,
            IReadOnlyList<byte> sourceIndexValues = null)
        {
            result.PartialBattleInfo.Add(new PartialBattleInfo
            {
                BxIndex = saveIndex,
                DestAddr = destAddr,
                SrcReg = srcReg,
                SrcRegValue = srcRegValue,
                ValueMin = srcRegValue,
                ValueMax = rangeEnd ?? srcRegValue,
                IsFromTable = isFromTable,
                SourceTableAddr = sourceAddr,
                SourceTableBaseAddr = ComputeSourceTableBaseAddress(sourceAddr, originalBx),
                SourceTable = sourceTable,
                OriginalSourceIndex = originalBx,
                SourceIndexProviderAddr = sourceIndexProviderAddr,
                SourceIndexMin = sourceIndexRange?.Min,
                SourceIndexMax = sourceIndexRange?.Max,
                SourceIndexValues = sourceIndexValues == null ? new List<byte>() : sourceIndexValues.Distinct().OrderBy(v => v).ToList(),
                SourceIndexBehavior = ResolveInitialBattleSourceIndexBehavior(originalBx, sourceIndexProviderAddr, sourceIndexExternallyDerived),
                SourceIndexExternallyDerived = sourceIndexExternallyDerived
            });
            result.HasPartialBattlePattern = true;
        }

        private static bool TryGetBattleComponentRange(
            RegisterTracker tracker,
            string primaryReg,
            string secondaryReg,
            out ValueRange8 range)
        {
            range = null;
            if (tracker == null)
                return false;

            if (!string.IsNullOrWhiteSpace(primaryReg) &&
                tracker.TryGetRegisterRange(primaryReg, out range) &&
                range != null)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(secondaryReg) &&
                !string.Equals(primaryReg, secondaryReg, StringComparison.OrdinalIgnoreCase) &&
                tracker.TryGetRegisterRange(secondaryReg, out range) &&
                range != null)
            {
                return true;
            }

            return false;
        }

        private bool TryRecordRangedBattleSave(
            RegisterTracker tracker,
            PathAnalysisResult result,
            int saveIndex,
            ushort destAddr,
            string srcReg,
            string secondaryRangeReg,
            string destinationText,
            ushort? sourceAddr,
            ushort? originalBx,
            string sourceTable,
            bool sourceIndexExternallyDerived,
            ushort? sourceIndexProviderAddr)
        {
            if (!TryGetBattleComponentRange(tracker, srcReg, secondaryRangeReg, out var range) || range == null)
                return false;

            bool isFromTable =
                (!string.IsNullOrWhiteSpace(srcReg) && tracker.IsFromTable(srcReg)) ||
                (!string.IsNullOrWhiteSpace(secondaryRangeReg) && tracker.IsFromTable(secondaryRangeReg));

            ValueRange8 sourceIndexRange = null;
            if (!string.IsNullOrWhiteSpace(srcReg))
                tracker.TryGetSourceIndexRange(srcReg, out sourceIndexRange);
            if (sourceIndexRange == null && !string.IsNullOrWhiteSpace(secondaryRangeReg))
                tracker.TryGetSourceIndexRange(secondaryRangeReg, out sourceIndexRange);

            List<byte> sourceIndexValues = null;
            if (!string.IsNullOrWhiteSpace(srcReg))
                tracker.TryGetSourceIndexValues(srcReg, out sourceIndexValues);
            if ((sourceIndexValues == null || sourceIndexValues.Count == 0) &&
                !string.IsNullOrWhiteSpace(secondaryRangeReg))
            {
                tracker.TryGetSourceIndexValues(secondaryRangeReg, out sourceIndexValues);
            }

            RecordPartialBattleSave(
                result,
                saveIndex,
                destAddr,
                srcReg,
                range.Min,
                sourceAddr,
                originalBx,
                sourceTable,
                sourceIndexExternallyDerived,
                sourceIndexProviderAddr,
                range.Max,
                isFromTable,
                sourceIndexRange,
                sourceIndexValues);

            string valueText = range.IsExact
                ? $"0x{range.Min:X2}"
                : $"0x{range.Min:X2}-0x{range.Max:X2}";

            AnalysisDebug.WriteLine(
                $"    ЧАСТИЧНАЯ БИТВА (диапазон): {destinationText} = {srcReg} (saveIndex={saveIndex}, val={valueText})");

            return true;
        }

        private void UpsertFirstBattleMonsterComponent(
            PathAnalysisResult result,
            int saveIndex,
            byte value,
            string srcReg,
            ushort? sourceAddr,
            ushort? originalBx,
            string sourceTable,
            bool sourceIndexExternallyDerived,
            ushort? sourceIndexProviderAddr,
            bool isFromTable)
        {
            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
            {
                var existing = result.BattleMonsterEntries[saveIndex];
                result.BattleMonsterEntries[saveIndex] = (value, existing.val2, false);
                return;
            }

            result.BattleMonsterEntries[saveIndex] = (value, 0, false);
            RecordPartialBattleSave(
                result,
                saveIndex,
                0x3C58,
                srcReg,
                value,
                sourceAddr,
                originalBx,
                sourceTable,
                sourceIndexExternallyDerived,
                sourceIndexProviderAddr,
                value,
                isFromTable);
        }

        private void UpsertSecondBattleMonsterComponent(
            PathAnalysisResult result,
            int saveIndex,
            byte value,
            string srcReg,
            ushort? sourceAddr,
            ushort? originalBx,
            string sourceTable,
            bool sourceIndexExternallyDerived,
            ushort? sourceIndexProviderAddr,
            bool isFromTable)
        {
            if (result.BattleMonsterEntries.ContainsKey(saveIndex))
            {
                var existing = result.BattleMonsterEntries[saveIndex];
                result.BattleMonsterEntries[saveIndex] = (existing.val1, value, false);
                return;
            }

            result.BattleMonsterEntries[saveIndex] = (0, value, false);
            RecordPartialBattleSave(
                result,
                saveIndex,
                0x3C29,
                srcReg,
                value,
                sourceAddr,
                originalBx,
                sourceTable,
                sourceIndexExternallyDerived,
                sourceIndexProviderAddr,
                value,
                isFromTable);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("AL");
                ushort? sourceAddr = tracker.GetSourceAddress("AL");
                ushort? originalBx = tracker.GetOriginalBx("AL");
                string sourceTable = tracker.GetSourceTable("AL");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("AL");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("AL");

                if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра (полностью определённый)
                                UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val1={alValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в AL? Это нестандартная ситуация
                                // В оригинале такого не должно быть, но если случилось, сохраняем как val1
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val1={alValue:X2})");
                                UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDA9":
                            case "CDB1":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C58, "AL", alValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val={alValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = AL (BX={bxValue}, val1={alValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C58, "AL", "AX", "[BX+3C58]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("CL") || tracker.IsFromTable("CX");
                ushort? sourceAddr = tracker.GetSourceAddress("CL") ?? tracker.GetSourceAddress("CX");
                ushort? originalBx = tracker.GetOriginalBx("CL") ?? tracker.GetOriginalBx("CX");
                string sourceTable = tracker.GetSourceTable("CL") ?? tracker.GetSourceTable("CX");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("CL") || tracker.GetSourceIndexExternallyDerived("CX");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("CL") ?? tracker.GetSourceIndexProviderAddress("CX");

                if (tracker.TryGetByteRegisterValue("CL", out byte clValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра (полностью определённый)
                                UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val2={clValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в CL? Нестандартно
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val2={clValue:X2})");
                                UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDB1":
                            case "CDA9":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C29, "CL", clValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val={clValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = CL (BX={bxValue}, val2={clValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C29, "CL", "CX", "[BX+3C29]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("DL") || tracker.IsFromTable("DX");
                ushort? sourceAddr = tracker.GetSourceAddress("DL") ?? tracker.GetSourceAddress("DX");
                ushort? originalBx = tracker.GetOriginalBx("DL") ?? tracker.GetOriginalBx("DX");
                string sourceTable = tracker.GetSourceTable("DL") ?? tracker.GetSourceTable("DX");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("DL") || tracker.GetSourceIndexExternallyDerived("DX");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("DL") ?? tracker.GetSourceIndexProviderAddress("DX");

                if (tracker.TryGetByteRegisterValue("DL", out byte dlValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                                UpsertFirstBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val1={dlValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в DL? Нестандартно
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val1={dlValue:X2})");
                                UpsertFirstBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDA9":
                            case "CDB1":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C58, "DL", dlValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val={dlValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertFirstBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = DL (BX={bxValue}, val1={dlValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C58, "DL", "DX", "[BX+3C58]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("DL") || tracker.IsFromTable("DX");
                ushort? sourceAddr = tracker.GetSourceAddress("DL") ?? tracker.GetSourceAddress("DX");
                ushort? originalBx = tracker.GetOriginalBx("DL") ?? tracker.GetOriginalBx("DX");
                string sourceTable = tracker.GetSourceTable("DL") ?? tracker.GetSourceTable("DX");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("DL") || tracker.GetSourceIndexExternallyDerived("DX");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("DL") ?? tracker.GetSourceIndexProviderAddress("DX");

                if (tracker.TryGetByteRegisterValue("DL", out byte dlValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                                UpsertSecondBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val2={dlValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в DL? Нестандартно
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val2={dlValue:X2})");
                                UpsertSecondBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDB1":
                            case "CDA9":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C29, "DL", dlValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val={dlValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertSecondBattleMonsterComponent(result, saveIndex, dlValue, "DL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = DL (BX={bxValue}, val2={dlValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C29, "DL", "DX", "[BX+3C29]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("BL") || tracker.IsFromTable("BX");
                ushort? sourceAddr = tracker.GetSourceAddress("BL") ?? tracker.GetSourceAddress("BX");
                ushort? originalBx = tracker.GetOriginalBx("BL") ?? tracker.GetOriginalBx("BX");
                string sourceTable = tracker.GetSourceTable("BL") ?? tracker.GetSourceTable("BX");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("BL") || tracker.GetSourceIndexExternallyDerived("BX");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("BL") ?? tracker.GetSourceIndexProviderAddress("BX");

                if (tracker.TryGetByteRegisterValue("BL", out byte blValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                                UpsertFirstBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDBD): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val1={blValue:X2})");
                                break;

                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в BL? Нестандартно
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDB5 в 3C58? Нестандартно): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val1={blValue:X2})");
                                UpsertFirstBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDA9":
                            case "CDB1":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C58, "BL", blValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val={blValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertFirstBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C58] = BL (BX={bxValue}, val1={blValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C58, "BL", "BX", "[BX+3C58]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
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
                int saveIndex = bxValue;
                bool isFromTable = tracker.IsFromTable("BL") || tracker.IsFromTable("BX");
                ushort? sourceAddr = tracker.GetSourceAddress("BL") ?? tracker.GetSourceAddress("BX");
                ushort? originalBx = tracker.GetOriginalBx("BL") ?? tracker.GetOriginalBx("BX");
                string sourceTable = tracker.GetSourceTable("BL") ?? tracker.GetSourceTable("BX");
                bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("BL") || tracker.GetSourceIndexExternallyDerived("BX");
                ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("BL") ?? tracker.GetSourceIndexProviderAddress("BX");

                if (tracker.TryGetByteRegisterValue("BL", out byte blValue))
                {
                    if (isFromTable)
                    {
                        switch (sourceTable)
                        {
                            case "CDB5":
                                // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                                UpsertSecondBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (CDB5): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val2={blValue:X2})");
                                break;

                            case "CDBD":
                                // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в BL? Нестандартно
                                AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (CDBD в 3C29? Нестандартно): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val2={blValue:X2})");
                                UpsertSecondBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                                break;

                            case "CDB1":
                            case "CDA9":
                            case "CA7F":
                            case "CA84":
                            default:
                                RecordPartialBattleSave(result, saveIndex, 0x3C29, "BL", blValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                                AnalysisDebug.WriteLine($"    ЧАСТИЧНАЯ БИТВА ({sourceTable ?? "UNKNOWN"}): [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val={blValue:X2})");
                                break;
                        }
                    }
                    else
                    {
                        // Значение не из таблиц - полностью определённая битва
                        UpsertSecondBattleMonsterComponent(result, saveIndex, blValue, "BL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                        AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямое): [BX+3C29] = BL (BX={bxValue}, val2={blValue:X2})");
                    }
                }
                else
                {
                    TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C29, "BL", "BX", "[BX+3C29]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                }
            }
        }

        /// <summary>
        /// Прямое сохранение в [3C58] из AL (без BX)
        /// </summary>
        private void ProcessDirectSaveTo3C58FromAL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            int saveIndex = 0; // BX = 0 для прямых сохранений
            bool isFromTable = tracker.IsFromTable("AL");
            ushort? sourceAddr = tracker.GetSourceAddress("AL");
            ushort? originalBx = tracker.GetOriginalBx("AL");
            string sourceTable = tracker.GetSourceTable("AL");
            bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("AL");
            ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("AL");

            if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
            {
                if (isFromTable)
                {
                    switch (sourceTable)
                    {
                        case "CDBD":
                            // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС монстра
                            UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, из CDBD): [3C58] = AL (val1={alValue:X2})");
                            break;

                        case "CDB5":
                            // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС в AL? Нестандартно
                            AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (прямая, из CDB5 в 3C58? Нестандартно): [3C58] = AL (val1={alValue:X2})");
                            UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            break;

                        case "CDA9":
                        case "CDB1":
                        case "CA7F":
                        case "CA84":
                        default:
                            RecordPartialBattleSave(result, saveIndex, 0x3C58, "AL", alValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                            AnalysisDebug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ {sourceTable ?? "UNKNOWN"}+: [3C58] = AL (originalBX={originalBx}, val={alValue:X2})");
                            break;
                    }
                }
                else
                {
                    // Значение не из таблиц - полностью определённая битва
                    UpsertFirstBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                    AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C58] = AL (val1={alValue:X2})");
                }
            }
            else
            {
                TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C58, "AL", "AX", "[3C58]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            }
        }

        /// <summary>
        /// Прямое сохранение в [3C29] из AL (без BX)
        /// </summary>
        private void ProcessDirectSaveTo3C29FromAL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            int saveIndex = 0; // BX = 0 для прямых сохранений
            bool isFromTable = tracker.IsFromTable("AL");
            ushort? sourceAddr = tracker.GetSourceAddress("AL");
            ushort? originalBx = tracker.GetOriginalBx("AL");
            string sourceTable = tracker.GetSourceTable("AL");
            bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("AL");
            ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("AL");

            if (tracker.TryGetByteRegisterValue("AL", out byte alValue))
            {
                if (isFromTable)
                {
                    switch (sourceTable)
                    {
                        case "CDB5":
                            // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                            UpsertSecondBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, из CDB5): [3C29] = AL (val2={alValue:X2})");
                            break;

                        case "CDBD":
                            // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в AL? Нестандартно
                            AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (прямая, из CDBD в 3C29? Нестандартно): [3C29] = AL (val2={alValue:X2})");
                            UpsertSecondBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            break;

                        case "CDB1":
                        case "CDA9":
                        case "CA7F":
                        case "CA84":
                        default:
                            RecordPartialBattleSave(result, saveIndex, 0x3C29, "AL", alValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                            AnalysisDebug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ {sourceTable ?? "UNKNOWN"}+: [3C29] = AL (originalBX={originalBx}, val={alValue:X2})");
                            break;
                    }
                }
                else
                {
                    // Значение не из таблиц - полностью определённая битва
                    UpsertSecondBattleMonsterComponent(result, saveIndex, alValue, "AL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                    AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C29] = AL (val2={alValue:X2})");
                }
            }
            else
            {
                TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C29, "AL", "AX", "[3C29]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            }
        }

        /// <summary>
        /// Прямое сохранение в [3C29] из CL (без BX)
        /// </summary>
        private void ProcessDirectSaveTo3C29FromCL(byte[] bytes, RegisterTracker tracker,
    PathAnalysisResult result, uint address)
        {
            int saveIndex = 0; // BX = 0 для прямых сохранений
            bool isFromTable = tracker.IsFromTable("CL") || tracker.IsFromTable("CX");
            ushort? sourceAddr = tracker.GetSourceAddress("CL") ?? tracker.GetSourceAddress("CX");
            ushort? originalBx = tracker.GetOriginalBx("CL") ?? tracker.GetOriginalBx("CX");
            string sourceTable = tracker.GetSourceTable("CL") ?? tracker.GetSourceTable("CX");
            bool sourceIndexExternallyDerived = tracker.GetSourceIndexExternallyDerived("CL") || tracker.GetSourceIndexExternallyDerived("CX");
            ushort? sourceIndexProviderAddr = tracker.GetSourceIndexProviderAddress("CL") ?? tracker.GetSourceIndexProviderAddress("CX");

            if (tracker.TryGetByteRegisterValue("CL", out byte clValue))
            {
                if (isFromTable)
                {
                    switch (sourceTable)
                    {
                        case "CDB5":
                            // Из таблицы CDB5+ - ВТОРОЙ ИНДЕКС монстра
                            UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая, из CDB5): [3C29] = CL (val2={clValue:X2})");
                            break;

                        case "CDBD":
                            // Из таблицы CDBD+ - ПЕРВЫЙ ИНДЕКС в CL? Нестандартно
                            AnalysisDebug.WriteLine($"    [!] ПОЛНАЯ БИТВА (прямая, из CDBD в 3C29? Нестандартно): [3C29] = CL (val2={clValue:X2})");
                            UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, true);
                            break;

                        case "CDB1":
                        case "CDA9":
                        case "CA7F":
                        case "CA84":
                        default:
                            RecordPartialBattleSave(result, saveIndex, 0x3C29, "CL", clValue, sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
                            AnalysisDebug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ {sourceTable ?? "UNKNOWN"}+: [3C29] = CL (originalBX={originalBx}, val={clValue:X2})");
                            break;
                    }
                }
                else
                {
                    // Значение не из таблиц - полностью определённая битва
                    UpsertSecondBattleMonsterComponent(result, saveIndex, clValue, "CL", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr, false);
                    AnalysisDebug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C29] = CL (val2={clValue:X2})");
                }
            }
            else
            {
                TryRecordRangedBattleSave(tracker, result, saveIndex, 0x3C29, "CL", "CX", "[3C29]", sourceAddr, originalBx, sourceTable, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            }
        }

        private bool TryMapOverlayAddressToFileOffset(BinaryReader br, ushort memAddr, out long fileOffset)
        {
            return OvrOverlayAddressReader.TryMapOverlayAddressToFileOffset(br, _config, memAddr, out fileOffset);
        }

        private bool TryReadOverlayByte(BinaryReader br, ushort memAddr, out byte value)
        {
            return OvrOverlayAddressReader.TryReadByte(br, _config, memAddr, out value);
        }

        private bool TryReadOverlayWord(BinaryReader br, ushort memAddr, out ushort value)
        {
            return OvrOverlayAddressReader.TryReadWord(br, _config, memAddr, out value);
        }

        private string ExtractText(BinaryReader br, ushort textAddress)
        {
            return OvrOverlayAddressReader.ExtractText(br, _config, textAddress);
        }

        private string DecodeText(byte[] bytes)
        {
            return OvrOverlayAddressReader.DecodeText(bytes);
        }
    }
}
