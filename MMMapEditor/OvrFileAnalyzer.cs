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
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    public class OvrFileAnalyzer
    {
        private readonly OvrFileConfig _config;

        // Конструктор принимает обязательную конфигурацию
        public OvrFileAnalyzer(OvrFileConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // Структуры для анализа путей
        private class AlternativePath
        {
            public int ObjectIndex { get; set; }
            public uint Address { get; set; }
            public string Condition { get; set; }
            public uint TargetAddress { get; set; }
            public bool Analyzed { get; set; }
            public int PathNumber { get; set; }
        }

        // Класс для отслеживания регистров (должен быть ИЗОЛИРОВАН для каждого пути)
        private class RegisterTracker
        {
            private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
            private Dictionary<string, string> registerSources = new Dictionary<string, string>();

            public void SetRegisterValue(string reg, ushort value, uint address, string instruction)
            {
                registers[reg.ToUpper()] = value;
                registerSources[reg.ToUpper()] = $"0x{value:X4} loaded at 0x{address:X4} via {instruction}";

                // Если устанавливаем значение для AX, также устанавливаем для AL и AH
                if (reg.ToUpper() == "AX")
                {
                    registers["AL"] = (byte)(value & 0xFF);
                    registers["AH"] = (byte)(value >> 8);
                }
                // Если устанавливаем значение для CH (часть CX)
                else if (reg.ToUpper() == "CH")
                {
                    if (registers.TryGetValue("CX", out ushort cxValue))
                    {
                        cxValue = (ushort)((cxValue & 0x00FF) | (value << 8));
                        registers["CX"] = cxValue;
                    }
                }
            }

            public bool TryGetRegisterValue(string reg, out ushort value)
            {
                return registers.TryGetValue(reg.ToUpper(), out value);
            }

            public void Clear()
            {
                registers.Clear();
                registerSources.Clear();
            }

            public void TrackPartialRegisterOperation(string fullReg, string partialReg, byte value, uint address, string instruction)
            {
                string fullRegUpper = fullReg.ToUpper();
                string partialRegUpper = partialReg.ToUpper();

                ushort currentValue = 0;
                if (registers.TryGetValue(fullRegUpper, out ushort existingValue))
                {
                    currentValue = existingValue;
                }

                if (partialRegUpper == "AL" || partialRegUpper == "AH")
                {
                    if (fullRegUpper == "AX")
                    {
                        if (partialRegUpper == "AL")
                        {
                            currentValue = (ushort)((currentValue & 0xFF00) | value);
                        }
                        else if (partialRegUpper == "AH")
                        {
                            currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                        }
                        registers[fullRegUpper] = currentValue;
                    }
                }
            }
        }

        // Класс для хранения результатов анализа пути
        private class PathAnalysisResult
        {
            public HashSet<string> FoundTexts { get; set; } = new HashSet<string>();
            public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
        }

        // Публичный статический метод для анализа OVR файла с конфигурацией
        public static List<OvrObject> AnalyzeOvrFile(string filename, OvrFileConfig config)
        {
            var analyzer = new OvrFileAnalyzer(config);
            return analyzer.InternalAnalyze(filename);
        }

        private List<OvrObject> InternalAnalyze(string filename)
        {
            var objects = new List<OvrObject>();

            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 0x400)
                {
                    throw new InvalidOperationException("File too small to be a valid overlay");
                }

                // Используем конфигурационный startAddress
                fs.Seek(_config.StartAddress, SeekOrigin.Begin);
                byte numObjects = br.ReadByte();

                // Чтение координат
                var coordinates = ReadCoordinates(br, numObjects);
                // Чтение направлений
                var directions = ReadDirections(br, numObjects);
                // Чтение patch-ключей
                var patchKeys = ReadPatchKeys(br, numObjects);

                for (int i = 0; i < numObjects; i++)
                {
                    var obj = ProcessObject(br, i + 1, coordinates[i], directions[i], patchKeys[i]);

                    // ФИЛЬТРАЦИЯ УНИКАЛЬНЫХ ПУТЕЙ
                    obj.PathTexts = FilterUniquePaths(obj.PathTexts);

                    objects.Add(obj);
                }
            }

            return objects;
        }

        private OvrObject ProcessObject(BinaryReader br, int objIndex, Tuple<byte, byte> coords,
                                       byte direction, ushort patchKey)
        {
            var ovrObject = new OvrObject
            {
                X = coords.Item1,
                Y = coords.Item2,
                DirectionByte = direction
            };

            uint patchAddress = CalculatePatchAddress(patchKey);

            // 1. Собираем все альтернативные пути из основного пути
            var alternativePaths = new List<AlternativePath>();
            ShowLinearDisassemblyAndCollectAlternativePaths(br, patchAddress, alternativePaths, objIndex);

            // 2. Анализируем основной путь (Path0) с ИЗОЛИРОВАННЫМ трекером регистров
            var mainRegisterTracker = new RegisterTracker();
            var mainPathAnalysis = AnalyzeMainPath(br, patchAddress, mainRegisterTracker, ovrObject);
            ovrObject.PathTexts[0] = mainPathAnalysis.FoundTexts;

            // Сохраняем информацию о монстрах из основного пути
            if (mainPathAnalysis.MonsterPower.HasValue)
                ovrObject.MonsterPower = mainPathAnalysis.MonsterPower.Value;
            if (mainPathAnalysis.MonsterLevel.HasValue)
                ovrObject.MonsterLevel = mainPathAnalysis.MonsterLevel.Value;

            // 3. Глобальный набор для отслеживания уже проанализированных путей
            var globallyAnalyzedPaths = new HashSet<string>();

            // 4. Анализируем все альтернативные пути с НОВЫМ трекером для каждого
            int currentPathNumber = 1;
            foreach (var path in alternativePaths)
            {
                if (path.Analyzed) continue;

                string globalPathKey = $"{patchAddress:X4}_{path.Address:X4}_{path.TargetAddress:X4}";
                if (!globallyAnalyzedPaths.Contains(globalPathKey))
                {
                    globallyAnalyzedPaths.Add(globalPathKey);

                    // СОЗДАЕМ НОВЫЙ трекер регистров для этого пути
                    var pathRegisterTracker = new RegisterTracker();

                    // Анализируем альтернативный путь
                    var pathResult = AnalyzeAlternativePath(br, patchAddress, path.Address, path.TargetAddress,
                        objIndex, currentPathNumber, new HashSet<string>(), pathRegisterTracker, 0);

                    // Сохраняем тексты этого пути
                    ovrObject.PathTexts[currentPathNumber] = pathResult.FoundTexts;

                    // Сохраняем информацию о монстрах из альтернативного пути (приоритет по порядку)
                    if (pathResult.MonsterPower.HasValue)
                        ovrObject.MonsterPower = pathResult.MonsterPower.Value;
                    if (pathResult.MonsterLevel.HasValue)
                        ovrObject.MonsterLevel = pathResult.MonsterLevel.Value;

                    // Сохраняем вложенные пути рекурсивно
                    SaveNestedPathsRecursively(ovrObject.PathTexts, pathResult.NestedPaths, ref currentPathNumber);

                    path.Analyzed = true;
                    currentPathNumber++;
                }
            }

            // 5. ФИЛЬТРУЕМ уникальные пути
            ovrObject.PathTexts = FilterUniquePaths(ovrObject.PathTexts);

            return ovrObject;
        }

        // Метод для анализа основного пути
        private PathAnalysisResult AnalyzeMainPath(BinaryReader br, uint patchAddress,
            RegisterTracker registerTracker, OvrObject currentObject)
        {
            var result = new PathAnalysisResult();

            // 1. Анализируем косвенные пути загрузки текста
            var indirectTexts = AnalyzeIndirectTextPatterns(br, patchAddress);
            foreach (var text in indirectTexts)
            {
                result.FoundTexts.Add(text);
            }

            // 2. Анализируем CALL инструкции
            var analyzedCalls = new HashSet<uint>();
            AnalyzeCallsWithFullDisassembly(br, patchAddress, analyzedCalls, result,
                registerTracker, currentObject, 0);

            return result;
        }

        // Метод для анализа альтернативного пути
        private PathAnalysisResult AnalyzeAlternativePath(BinaryReader br, uint patchAddress, uint jumpAddress,
            uint alternativeStartAddress, int objIndex, int pathIndex, HashSet<string> alreadyAnalyzedPaths,
            RegisterTracker registerTracker, int recursionDepth)
        {
            const int MAX_RECURSION_DEPTH = 3;
            var result = new PathAnalysisResult();

            if (recursionDepth > MAX_RECURSION_DEPTH) return result;

            long fileSize = br.BaseStream.Length;
            if (patchAddress >= fileSize) return result;

            // Проверяем, не анализировали ли мы уже этот путь
            string pathKey = $"{jumpAddress:X4}_{alternativeStartAddress:X4}_{recursionDepth}";
            if (alreadyAnalyzedPaths.Contains(pathKey)) return result;
            alreadyAnalyzedPaths.Add(pathKey);

            // 1. Собираем локальные альтернативные пути внутри этого пути
            var localAlternativePaths = new List<AlternativePath>();
            ShowLinearDisassemblyWithAlternativeBranch(br, patchAddress, jumpAddress, alternativeStartAddress,
                localAlternativePaths, objIndex, 0, true);

            // 2. Анализируем CALL инструкции в альтернативном пути
            var analyzedCalls = new HashSet<uint>();

            if (IsNestedPath(jumpAddress, alternativeStartAddress))
            {
                AnalyzeCallsWithNestedAlternativeBranch(br, patchAddress, jumpAddress, alternativeStartAddress,
                    analyzedCalls, result, registerTracker, 0, new HashSet<string>());
            }
            else
            {
                AnalyzeCallsWithAlternativeBranch(br, patchAddress, jumpAddress, alternativeStartAddress,
                    analyzedCalls, result, registerTracker, 0, 0);
            }

            // 3. Анализируем косвенные пути загрузки текста
            var indirectTexts = AnalyzeIndirectTextPatterns(br, patchAddress);
            foreach (var text in indirectTexts)
            {
                result.FoundTexts.Add(text);
            }

            // 4. Анализируем ВЛОЖЕННЫЕ альтернативные пути (Path11, Path12 и т.д.)
            if (localAlternativePaths.Count > 0 && recursionDepth < MAX_RECURSION_DEPTH)
            {
                for (int i = 0; i < localAlternativePaths.Count; i++)
                {
                    var nestedPath = localAlternativePaths[i];
                    if (nestedPath.Analyzed) continue;

                    // Проверяем доступность вложенного пути
                    if (IsTransitionReachableFromAlternativePath(br, patchAddress, jumpAddress,
                        alternativeStartAddress, nestedPath.Address))
                    {
                        // СОЗДАЕМ НОВЫЙ трекер регистров для вложенного пути
                        var nestedRegisterTracker = new RegisterTracker();

                        // Анализируем вложенный путь рекурсивно
                        var nestedResult = AnalyzeAlternativePath(br, patchAddress, nestedPath.Address,
                            nestedPath.TargetAddress, objIndex, pathIndex * 10 + i + 1,
                            new HashSet<string>(alreadyAnalyzedPaths), nestedRegisterTracker, recursionDepth + 1);

                        // Сохраняем вложенный путь как отдельный результат
                        result.NestedPaths[pathIndex * 10 + i + 1] = nestedResult;

                        nestedPath.Analyzed = true;
                    }
                }
            }

            return result;
        }

        // Метод для сохранения вложенных путей рекурсивно
        private void SaveNestedPathsRecursively(Dictionary<int, HashSet<string>> allPathTexts,
            Dictionary<int, PathAnalysisResult> nestedPaths, ref int nextPathNumber)
        {
            foreach (var kvp in nestedPaths.OrderBy(x => x.Key))
            {
                // Проверяем, не существует ли уже путь с таким номером
                if (!allPathTexts.ContainsKey(kvp.Key))
                {
                    allPathTexts[kvp.Key] = kvp.Value.FoundTexts;
                }

                // Рекурсивно сохраняем вложенные пути следующего уровня
                if (kvp.Value.NestedPaths.Count > 0)
                {
                    SaveNestedPathsRecursively(allPathTexts, kvp.Value.NestedPaths, ref nextPathNumber);
                }
            }
        }

        // Метод для фильтрации уникальных путей
        private Dictionary<int, HashSet<string>> FilterUniquePaths(Dictionary<int, HashSet<string>> allPathTexts)
        {
            var uniquePaths = new Dictionary<int, HashSet<string>>();
            var processedTextSets = new List<HashSet<string>>();

            // Сортируем пути по номеру
            var sortedPaths = allPathTexts.OrderBy(kvp => kvp.Key).ToList();

            foreach (var kvp in sortedPaths)
            {
                // Пропускаем пустые пути
                if (kvp.Value == null || kvp.Value.Count == 0)
                    continue;

                bool isUnique = true;

                // Проверяем, есть ли такой же набор текстов среди уже обработанных
                foreach (var existingSet in processedTextSets)
                {
                    if (AreTextSetsEqual(existingSet, kvp.Value))
                    {
                        isUnique = false;
                        break;
                    }
                }

                if (isUnique)
                {
                    uniquePaths[kvp.Key] = kvp.Value;
                    processedTextSets.Add(kvp.Value);
                }
            }

            return uniquePaths;
        }

        // Сравнение двух наборов текстов
        private bool AreTextSetsEqual(HashSet<string> set1, HashSet<string> set2)
        {
            if (set1 == null || set2 == null) return false;
            if (set1.Count != set2.Count) return false;

            var sorted1 = set1.OrderBy(t => t).ToList();
            var sorted2 = set2.OrderBy(t => t).ToList();

            for (int i = 0; i < sorted1.Count; i++)
            {
                if (sorted1[i] != sorted2[i])
                    return false;
            }

            return true;
        }

        // Проверка доступности вложенного перехода из альтернативного пути
        private bool IsTransitionReachableFromAlternativePath(BinaryReader br, uint patchAddress,
            uint mainJumpAddress, uint alternativeStartAddress, uint nestedJumpAddress)
        {
            try
            {
                using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
                {
                    capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                    uint currentAddress = alternativeStartAddress;
                    const int MAX_CHECK = 50;
                    int checks = 0;

                    while (currentAddress < nestedJumpAddress && checks < MAX_CHECK)
                    {
                        int bytesToRead = (int)Math.Min(32, nestedJumpAddress - currentAddress + 16);
                        byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                        var instructions = capstone.Disassemble(chunk, currentAddress);
                        if (instructions == null || instructions.Length == 0)
                            break;

                        foreach (var insn in instructions)
                        {
                            string mnemonicUpper = insn.Mnemonic.ToUpper();

                            // Если встретили безусловный переход или RET
                            if (mnemonicUpper == "JMP" || mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                            {
                                if (mnemonicUpper == "JMP")
                                {
                                    uint jumpTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                                    if (jumpTarget != nestedJumpAddress)
                                    {
                                        return false;
                                    }
                                }
                                else
                                {
                                    return false;
                                }
                            }

                            // Если достигли адреса перехода - он доступен
                            if ((uint)insn.Address >= nestedJumpAddress)
                            {
                                return true;
                            }

                            // Проверяем другие условные переходы
                            if (mnemonicUpper.StartsWith("J") && !mnemonicUpper.StartsWith("JMP"))
                            {
                                uint jumpTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                                if (jumpTarget != nestedJumpAddress && jumpTarget < nestedJumpAddress)
                                {
                                    return false;
                                }
                            }

                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            checks++;
                        }
                    }

                    return currentAddress >= nestedJumpAddress;
                }
            }
            catch
            {
                return true;
            }
        }

        private HashSet<string> AnalyzeIndirectTextPatterns(BinaryReader br, uint patchAddress)
        {
            var foundTexts = new HashSet<string>();
            var executionPath = ReconstructExecutionPath(br, patchAddress);

            if (executionPath.Count == 0) return foundTexts;

            for (int i = 0; i < executionPath.Count; i++)
            {
                var insn = executionPath[i];
                byte[] instructionBytes = insn.Bytes;

                if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB0)
                {
                    byte alValue = instructionBytes[1];

                    if (i + 1 < executionPath.Count)
                    {
                        var nextInsn = executionPath[i + 1];
                        byte[] nextBytes = nextInsn.Bytes;

                        if (nextBytes.Length >= 3 && nextBytes[0] == 0xBD)
                        {
                            ushort bpValue = BitConverter.ToUInt16(nextBytes, 1);
                            ushort combinedAddr = (ushort)((bpValue << 8) | alValue);

                            string text = ExtractText(br, combinedAddr);
                            if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                            {
                                string textEntry = $"Text at 0x{combinedAddr:X4}: {text}";
                                foundTexts.Add(textEntry);
                            }
                        }
                    }
                }
            }

            return foundTexts;
        }

        private void AnalyzeCallsWithFullDisassembly(BinaryReader br, uint address, HashSet<uint> analyzedAddresses,
            PathAnalysisResult result, RegisterTracker registerTracker, OvrObject currentObject, int depth)
        {
            if (depth > 5) return;
            if (analyzedAddresses.Contains(address)) return;
            analyzedAddresses.Add(address);

            if (depth == 0) registerTracker.Clear();

            long fileLength = br.BaseStream.Length;
            if (address >= fileLength) return;

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = address;
                const int MAX_INSTRUCTIONS = 50;
                int instructionsShown = 0;

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS)
                {
                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);
                    if (instructions == null || instructions.Length == 0) break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS) break;
                        instructionsShown++;

                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                AnalyzeCallsWithFullDisassembly(br, callTarget, analyzedAddresses,
                                    result, registerTracker, currentObject, depth + 1);
                            }
                        }

                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF") return;

                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget >= fileLength) return;
                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                currentAddress = jumpTarget;
                                break;
                            }
                        }

                        currentAddress = nextAddress;
                    }
                }
            }
        }

        private void AnalyzeCallsWithAlternativeBranch(BinaryReader br, uint patchAddress,
            uint jumpAddress, uint alternativeStartAddress, HashSet<uint> analyzedAddresses,
            PathAnalysisResult result, RegisterTracker registerTracker, int depth, int callDepth = 0)
        {
            const int MAX_CALL_DEPTH = 5;
            if (depth > MAX_CALL_DEPTH) return;
            if (analyzedAddresses.Contains(patchAddress)) return;
            analyzedAddresses.Add(patchAddress);

            long fileLength = br.BaseStream.Length;
            if (patchAddress >= fileLength) return;

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = patchAddress;
                const int MAX_INSTRUCTIONS = 50;
                int instructionsShown = 0;
                bool jumpTaken = false;
                bool shouldStop = false;

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS && !shouldStop)
                {
                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);
                    if (instructions == null || instructions.Length == 0) break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS || shouldStop) break;
                        instructionsShown++;

                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        if (insn.Address == jumpAddress && !jumpTaken)
                        {
                            jumpTaken = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0, analyzedAddresses,
                                    result, registerTracker, depth + 1, callDepth + 1);
                            }
                        }

                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            shouldStop = true;
                            break;
                        }

                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget >= fileLength)
                            {
                                shouldStop = true;
                                break;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                currentAddress = jumpTarget;
                                break;
                            }
                        }

                        currentAddress = nextAddress;
                    }
                }
            }
        }

        private void FindTextsInInstruction(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker,
            int depth, HashSet<string> output)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // 1. Прямая запись: MOV [3BD4], XXXX
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

            // 2. Запись через регистр: MOV [3BD4], AX
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

            // 3. Загрузка значения в 16-битный регистр
            else if (instructionBytes.Length >= 3 &&
                     (instructionBytes[0] & 0xF8) == 0xB8 &&
                     instructionBytes[0] != 0xBC && instructionBytes[0] != 0xBD)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                string text = ExtractText(br, immediateValue);
                if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                {
                    string textEntry = $"Text at 0x{immediateValue:X4}: {text}";
                    output.Add(textEntry);
                }
            }

            // 4. Загрузка значения в BP: MOV BP, imm16
            else if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBD)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                string text = ExtractText(br, immediateValue);
                if (!string.IsNullOrEmpty(text) && text != "(empty string)" && !text.StartsWith("Cannot locate"))
                {
                    string textEntry = $"Text at 0x{immediateValue:X4} (via BP): {text}";
                    output.Add(textEntry);
                }
            }
        }

        private void FindMonsterStatChanges(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker,
            int depth, PathAnalysisResult result)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // Проверка записи в [0xc96f] - сила монстров
            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                byte monsterPower = instructionBytes[4];
                result.MonsterPower = monsterPower;
            }

            // Проверка записи в [0xc961] - уровень монстров
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                byte monsterLevel = instructionBytes[4];
                result.MonsterLevel = monsterLevel;
            }

            // Проверка записи через регистры
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E)
            {
                ushort targetAddr = BitConverter.ToUInt16(instructionBytes, 2);

                if (targetAddr == 0xC96F || targetAddr == 0xC961)
                {
                    // Определяем, какой регистр используется
                    byte modRM = instructionBytes[1];
                    byte regField = (byte)((modRM >> 3) & 0x07);

                    if (regField == 5) // CH регистр
                    {
                        if (registerTracker.TryGetRegisterValue("CH", out ushort regValue))
                        {
                            if (targetAddr == 0xC96F)
                                result.MonsterPower = (byte)regValue;
                            else
                                result.MonsterLevel = (byte)regValue;
                        }
                    }
                }
            }

            // Дополнительные проверки для разных форм записи
            else if (instructionBytes.Length >= 3)
            {
                // MOV [C96F], AL
                if (instructionBytes[0] == 0xA2 &&
                    instructionBytes[1] == 0x6F && instructionBytes[2] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        result.MonsterPower = (byte)alValue;
                    }
                }
                // MOV [C961], AL
                else if (instructionBytes[0] == 0xA2 &&
                         instructionBytes[1] == 0x61 && instructionBytes[2] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        result.MonsterLevel = (byte)alValue;
                    }
                }
            }

            // Проверка записи через другие регистры (MOV [C96F], CL и т.д.)
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x0E)
            {
                ushort targetAddr = BitConverter.ToUInt16(instructionBytes, 2);

                if (targetAddr == 0xC96F || targetAddr == 0xC961)
                {
                    byte modRM = instructionBytes[1];
                    byte regField = (byte)((modRM >> 3) & 0x07);
                    string[] regNames = { "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                    if (regField >= 1 && regField <= 7) // CL, DL, BL, AH, CH, DH, BH
                    {
                        string regName = "";
                        if (regField == 1) regName = "CL";
                        else if (regField == 2) regName = "DL";
                        else if (regField == 3) regName = "BL";
                        else if (regField == 4) regName = "AH";
                        else if (regField == 5) regName = "CH";
                        else if (regField == 6) regName = "DH";
                        else if (regField == 7) regName = "BH";

                        if (!string.IsNullOrEmpty(regName) && registerTracker.TryGetRegisterValue(regName, out ushort regValue))
                        {
                            if (targetAddr == 0xC96F)
                                result.MonsterPower = (byte)regValue;
                            else
                                result.MonsterLevel = (byte)regValue;
                        }
                    }
                }
            }
        }

        private void TrackRegisterOperations(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker, int depth)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            // Загрузка непосредственного значения в 16-битный регистр
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
            // Загрузка непосредственного значения в 8-битный регистр
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

            // Отслеживание записей в [0xc96f] и [0xc961]
            if (instructionBytes.Length >= 4 &&
                ((instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                  instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9) ||  // MOV [C96F], CH
                 (instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                  instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9) ||  // MOV [C961], CH
                 (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                  instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9) ||  // MOV byte [C96F], imm8
                 (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                  instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)))   // MOV byte [C961], imm8
            {
                byte value = 0;
                string target = instructionBytes[2] == 0x6F ? "C96F" : "C961";

                // Если запись непосредственного значения
                if (instructionBytes[0] == 0xC6 && instructionBytes.Length >= 5)
                {
                    value = instructionBytes[4];
                    registerTracker.SetRegisterValue($"MEM_{target}", value, address,
                        $"MOV [{target}], {value}");
                }
                // Если запись из регистра
                else if (instructionBytes[0] == 0x88)
                {
                    // CH регистр имеет код 101 в modRM
                    if ((instructionBytes[1] & 0x38) >> 3 == 5) // CH
                    {
                        if (registerTracker.TryGetRegisterValue("CH", out ushort regValue))
                        {
                            value = (byte)regValue;
                            registerTracker.SetRegisterValue($"MEM_{target}", value, address,
                                $"MOV [{target}], CH (value: {value})");
                        }
                    }
                }
            }
        }

        private Tuple<byte, byte>[] ReadCoordinates(BinaryReader br, int count)
        {
            var coords = new Tuple<byte, byte>[count];
            for (int i = 0; i < count; i++)
            {
                byte coordByte = br.ReadByte();
                byte y = (byte)(coordByte >> 4);
                byte x = (byte)(coordByte & 0x0F);
                coords[i] = Tuple.Create(x, y);
            }
            return coords;
        }

        private byte[] ReadDirections(BinaryReader br, int count)
        {
            var directions = new byte[count];
            for (int i = 0; i < count; i++) directions[i] = br.ReadByte();
            return directions;
        }

        private ushort[] ReadPatchKeys(BinaryReader br, int count)
        {
            var keys = new ushort[count];
            for (int i = 0; i < count; i++) keys[i] = br.ReadUInt16();
            return keys;
        }

        // Используем конфигурационный PatchBase
        private uint CalculatePatchAddress(ushort key)
        {
            return (uint)(key + _config.PatchBase) & 0xFFFF;
        }

        private byte[] ReadBytesAt(BinaryReader br, long position, int count)
        {
            long originalPos = br.BaseStream.Position;
            br.BaseStream.Position = position;
            byte[] data = br.ReadBytes(count);
            br.BaseStream.Position = originalPos;
            return data;
        }

        private uint GetInstructionTargetAddress(X86Instruction insn, long fileLength)
        {
            string operand = insn.Operand ?? "";
            int hexIndex = operand.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (hexIndex >= 0)
            {
                string hexPart = operand.Substring(hexIndex + 2);
                hexPart = new string(hexPart.TakeWhile(c =>
                    (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')).ToArray());

                if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint targetAddr))
                {
                    return targetAddr;
                }
            }
            return 0;
        }

        // Используем конфигурационный TextBaseAddr
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

                // Декодируем и возвращаем как есть
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

        private void ShowLinearDisassemblyAndCollectAlternativePaths(BinaryReader br, uint startAddress,
            List<AlternativePath> alternativePaths, int objIndex)
        {
            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                long fileLength = br.BaseStream.Length;
                uint currentAddress = startAddress;
                int instructionsShown = 0;
                const int MAX_INSTRUCTIONS = 50;

                var processedAddresses = new Dictionary<uint, int>();

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS)
                {
                    // Проверяем, не посещали ли мы этот адрес слишком много раз
                    if (processedAddresses.ContainsKey(currentAddress))
                    {
                        processedAddresses[currentAddress]++;
                        if (processedAddresses[currentAddress] > 2)
                        {
                            break;
                        }
                    }
                    else
                    {
                        processedAddresses[currentAddress] = 1;
                    }

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0)
                        break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS)
                            break;

                        instructionsShown++;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        // В основном пути собираем ВСЕ альтернативные пути
                        if (mnemonicUpper.StartsWith("J") &&
                            !mnemonicUpper.StartsWith("JMP") && !mnemonicUpper.StartsWith("JECXZ"))
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
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

                                // Проверяем, нет ли уже такого пути в списке
                                bool alreadyExists = alternativePaths.Any(p =>
                                    p.Address == altPath.Address && p.TargetAddress == altPath.TargetAddress);

                                if (!alreadyExists)
                                {
                                    alternativePaths.Add(altPath);
                                }
                            }
                        }

                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            return;
                        }

                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget >= fileLength)
                            {
                                return;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                currentAddress = jumpTarget;
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
        }

        private void ShowLinearDisassemblyWithAlternativeBranch(BinaryReader br, uint patchAddress,
            uint jumpAddress, uint alternativeStartAddress, List<AlternativePath> localAlternativePaths,
            int objIndex, int depth = 0, bool isMainAlternativeAnalysis = false)
        {
            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                long fileLength = br.BaseStream.Length;
                uint currentAddress = patchAddress; // НАЧИНАЕМ С НАЧАЛА ПАТЧА!
                int instructionsShown = 0;
                const int MAX_INSTRUCTIONS = 100;

                // Улучшенная система отслеживания адресов для предотвращения циклов
                var processedAddresses = new Dictionary<uint, int>();
                bool jumpTaken = false;
                bool shouldStop = false;

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS && !shouldStop)
                {
                    // Проверяем, не посещали ли мы этот адрес слишком много раз
                    if (processedAddresses.ContainsKey(currentAddress))
                    {
                        processedAddresses[currentAddress]++;
                        if (processedAddresses[currentAddress] > 3) // Максимум 3 посещения
                        {
                            break;
                        }
                    }
                    else
                    {
                        processedAddresses[currentAddress] = 1;
                    }

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0)
                        break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS || shouldStop)
                            break;

                        instructionsShown++;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        // Если это тот самый условный переход - идем по альтернативной ветке
                        if (insn.Address == jumpAddress && !jumpTaken)
                        {
                            jumpTaken = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        // В альтернативном пути находим ВСЕ условные переходы и добавляем их как альтернативные пути
                        if (jumpTaken && mnemonicUpper.StartsWith("J") &&
                            !mnemonicUpper.StartsWith("JMP") && !mnemonicUpper.StartsWith("JECXZ") &&
                            insn.Address != jumpAddress)
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            if (jumpTarget != 0 && jumpTarget < fileLength)
                            {
                                var altPath = new AlternativePath
                                {
                                    ObjectIndex = objIndex,
                                    Address = (uint)insn.Address,
                                    Condition = $"{insn.Mnemonic} {insn.Operand} (внутри альтернативного пути)",
                                    TargetAddress = jumpTarget,
                                    Analyzed = false
                                };

                                if (isMainAlternativeAnalysis)
                                {
                                    bool alreadyExists = localAlternativePaths.Any(p =>
                                        p.Address == altPath.Address && p.TargetAddress == altPath.TargetAddress);

                                    if (!alreadyExists)
                                    {
                                        localAlternativePaths.Add(altPath);
                                    }
                                }
                                else
                                {
                                    localAlternativePaths.Add(altPath);
                                }
                            }
                        }

                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            shouldStop = true;
                            break;
                        }

                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            // Проверяем JMP на циклические ссылки
                            if (processedAddresses.ContainsKey(jumpTarget))
                            {
                                if (jumpTarget >= patchAddress && jumpTarget < currentAddress)
                                {
                                    shouldStop = true;
                                    break;
                                }
                            }

                            if (jumpTarget >= fileLength)
                            {
                                shouldStop = true;
                                break;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                currentAddress = jumpTarget;
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
        }

        private List<X86Instruction> ReconstructExecutionPath(BinaryReader br, uint startAddress)
        {
            var path = new List<X86Instruction>();
            var visitedAddresses = new HashSet<uint>();

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = startAddress;
                const int MAX_INSTRUCTIONS = 200;
                int instructionCount = 0;

                while (currentAddress < br.BaseStream.Length && instructionCount < MAX_INSTRUCTIONS)
                {
                    if (visitedAddresses.Contains(currentAddress))
                    {
                        // Обнаружен цикл
                        break;
                    }

                    visitedAddresses.Add(currentAddress);

                    int bytesToRead = (int)Math.Min(32, br.BaseStream.Length - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);
                    if (instructions == null || instructions.Length == 0)
                        break;

                    bool processedInstruction = false;
                    foreach (var insn in instructions)
                    {
                        if (instructionCount >= MAX_INSTRUCTIONS)
                            break;

                        path.Add(insn);
                        instructionCount++;
                        processedInstruction = true;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        // Обрабатываем переходы
                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                            if (jumpTarget != 0)
                            {
                                if (jumpTarget < br.BaseStream.Length)
                                {
                                    currentAddress = jumpTarget;
                                }
                                else
                                {
                                    // JMP за пределами файла - останавливаемся
                                    return path;
                                }
                            }
                            else
                            {
                                currentAddress = nextAddress;
                            }
                            break;
                        }
                        else if (mnemonicUpper.StartsWith("J") && !mnemonicUpper.StartsWith("JMP"))
                        {
                            // Для условных переходов - идем по основному пути (не берем альтернативный)
                            currentAddress = nextAddress;
                            break;
                        }
                        else if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            return path; // Конец подпрограммы
                        }
                        else if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                            if (callTarget != 0 && callTarget < br.BaseStream.Length)
                            {
                                // Анализируем подпрограмму, но ограничиваем глубину
                                if (path.Count < MAX_INSTRUCTIONS / 2)
                                {
                                    var subroutinePath = ReconstructExecutionPath(br, callTarget);
                                    path.AddRange(subroutinePath);
                                }
                            }
                            currentAddress = nextAddress;
                            break;
                        }
                        else
                        {
                            currentAddress = nextAddress;
                        }
                    }

                    if (!processedInstruction)
                        break;
                }
            }

            return path;
        }

        private bool IsNestedPath(uint jumpAddress, uint alternativeStartAddress)
        {
            return jumpAddress != 0 &&
                   alternativeStartAddress > jumpAddress &&
                   jumpAddress > 0x0090;
        }

        private void AnalyzeCallsWithNestedAlternativeBranch(BinaryReader br, uint patchAddress,
            uint jumpAddress, uint alternativeStartAddress, HashSet<uint> analyzedAddresses,
            PathAnalysisResult result, RegisterTracker registerTracker, int depth, HashSet<string> alreadyAnalyzedConditions = null)
        {
            if (depth > 5)
            {
                return;
            }

            // Инициализируем набор уже проанализированных условий, если его нет
            if (alreadyAnalyzedConditions == null)
            {
                alreadyAnalyzedConditions = new HashSet<string>();
            }

            long fileLength = br.BaseStream.Length;

            // Создаем ключ для этого конкретного пути анализа
            string pathKey = $"{patchAddress:X4}_{jumpAddress:X4}_{alternativeStartAddress:X4}_{depth}";
            if (alreadyAnalyzedConditions.Contains(pathKey))
            {
                return;
            }
            alreadyAnalyzedConditions.Add(pathKey);

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                // 1. Сначала анализируем, как достичь jumpAddress (с начала патча!)
                uint currentAddress = patchAddress; // НАЧИНАЕМ С НАЧАЛА ПАТЧА!
                bool reachedJumpAddress = false;
                int maxSteps = 100;
                int stepsTaken = 0;

                // Анализируем путь до jumpAddress с ограничением по количеству шагов
                while (currentAddress < jumpAddress && currentAddress < fileLength && stepsTaken < maxSteps)
                {
                    stepsTaken++;

                    // Создаем ключ для текущей позиции
                    string positionKey = $"{currentAddress:X4}_{jumpAddress:X4}";
                    if (alreadyAnalyzedConditions.Contains(positionKey))
                    {
                        break;
                    }
                    alreadyAnalyzedConditions.Add(positionKey);

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);
                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0)
                        break;

                    bool processedInstruction = false;
                    foreach (var insn in instructions)
                    {
                        if ((uint)insn.Address >= jumpAddress)
                        {
                            reachedJumpAddress = true;
                            break;
                        }

                        string mnemonic = insn.Mnemonic.ToUpper();

                        // Если это CALL - анализируем подпрограмму
                        if (mnemonic.StartsWith("CALL"))
                        {
                            // Ищем тексты в инструкции CALL
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                // Анализируем подпрограмму
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0,
                                    analyzedAddresses, result, registerTracker, depth + 1, 0);
                            }

                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            processedInstruction = true;
                            break;
                        }
                        // Если это условный переход, отмечаем его как необходимое условие
                        else if (mnemonic.StartsWith("J") && !mnemonic.StartsWith("JMP") && !mnemonic.StartsWith("JECXZ"))
                        {
                            // Ищем тексты в этом переходе
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            // Переходим по этому переходу (предполагаем, что он выполняется)
                            uint target = GetInstructionTargetAddress(insn, fileLength);

                            // Проверяем, не ведет ли переход к уже обработанному адресу
                            if (alreadyAnalyzedConditions.Contains($"{target:X4}_visited"))
                            {
                                // Вместо перехода продолжаем линейно
                                currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            }
                            else
                            {
                                // Отмечаем целевой адрес как посещенный
                                alreadyAnalyzedConditions.Add($"{target:X4}_visited");
                                currentAddress = target;
                            }

                            processedInstruction = true;
                            break;
                        }
                        else
                        {
                            // Обычные инструкции - ищем тексты
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            processedInstruction = true;
                            break;
                        }
                    }

                    if (!processedInstruction)
                        break;

                    if (reachedJumpAddress)
                        break;
                }

                // 2. Теперь анализируем код начиная с jumpAddress (если достигли его)
                if (reachedJumpAddress)
                {
                    // Анализируем выполнение от jumpAddress до конца
                    AnalyzeSpecificJumpExecutionWithFullCollection(br, jumpAddress, alternativeStartAddress,
                        analyzedAddresses, result, registerTracker, depth, "");
                }
                else
                {
                    // Попробуем анализировать напрямую от jumpAddress
                    AnalyzeSpecificJumpExecutionWithFullCollection(br, jumpAddress, alternativeStartAddress,
                        analyzedAddresses, result, registerTracker, depth, "");
                }
            }
        }

        // Вспомогательный метод для анализа конкретного перехода
        private void AnalyzeSpecificJumpExecutionWithFullCollection(BinaryReader br, uint jumpAddress, uint alternativeStartAddress,
            HashSet<uint> analyzedAddresses, PathAnalysisResult result, RegisterTracker registerTracker, int depth, string prefix)
        {
            if (analyzedAddresses.Contains(jumpAddress))
                return;

            analyzedAddresses.Add(jumpAddress);

            long fileLength = br.BaseStream.Length;

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = jumpAddress;
                const int MAX_INSTRUCTIONS = 50;
                int instructionsShown = 0;
                bool jumpExecuted = false;

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS)
                {
                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);
                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0)
                        break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS)
                            break;

                        instructionsShown++;

                        // Ищем тексты и добавляем их в список
                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        string mnemonic = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        // Если это наш целевой переход - выполняем его
                        if (insn.Address == jumpAddress && !jumpExecuted)
                        {
                            jumpExecuted = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        // Проверяем конец выполнения
                        if (mnemonic == "RET" || mnemonic == "RETF")
                        {
                            return;
                        }

                        if (mnemonic == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget >= fileLength)
                            {
                                return;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                currentAddress = jumpTarget;
                                break;
                            }
                        }

                        if (mnemonic.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                // Анализируем подпрограмму
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0,
                                    analyzedAddresses, result, registerTracker, depth + 1, 0);
                            }
                        }

                        currentAddress = nextAddress;
                    }
                }
            }
        }
    }
}