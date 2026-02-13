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
using System.Diagnostics;

namespace MMMapEditor
{
    public class OvrFileAnalyzer
    {
        private readonly OvrFileConfig _config;

        public OvrFileAnalyzer(OvrFileConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private class AlternativePath
        {
            public int ObjectIndex { get; set; }
            public uint Address { get; set; }
            public string Condition { get; set; }
            public uint TargetAddress { get; set; }
            public bool Analyzed { get; set; }
            public int PathNumber { get; set; }
        }

        private class RegisterTracker
        {
            private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
            private Dictionary<string, string> registerSources = new Dictionary<string, string>();

            public void SetRegisterValue(string reg, ushort value, uint address, string instruction)
            {
                string regUpper = reg.ToUpper();
                registers[regUpper] = value;
                registerSources[regUpper] = $"0x{value:X4} loaded at 0x{address:X4} via {instruction}";

                if (regUpper == "AX")
                {
                    registers["AL"] = (byte)(value & 0xFF);
                    registers["AH"] = (byte)(value >> 8);
                }
                else if (regUpper == "CX")
                {
                    registers["CL"] = (byte)(value & 0xFF);
                    registers["CH"] = (byte)(value >> 8);
                }
                else if (regUpper == "DX")
                {
                    registers["DL"] = (byte)(value & 0xFF);
                    registers["DH"] = (byte)(value >> 8);
                }
                else if (regUpper == "BX")
                {
                    registers["BL"] = (byte)(value & 0xFF);
                    registers["BH"] = (byte)(value >> 8);
                }
                else if (regUpper == "CH")
                {
                    if (registers.TryGetValue("CX", out ushort cxValue))
                    {
                        cxValue = (ushort)((cxValue & 0x00FF) | (value << 8));
                        registers["CX"] = cxValue;
                    }
                }
                else if (regUpper == "CL")
                {
                    if (registers.TryGetValue("CX", out ushort cxValue))
                    {
                        cxValue = (ushort)((cxValue & 0xFF00) | value);
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
                        registers[partialRegUpper] = value;
                    }
                }
                else if (partialRegUpper == "CL" || partialRegUpper == "CH")
                {
                    if (fullRegUpper == "CX")
                    {
                        if (partialRegUpper == "CL")
                        {
                            currentValue = (ushort)((currentValue & 0xFF00) | value);
                        }
                        else if (partialRegUpper == "CH")
                        {
                            currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                        }
                        registers[fullRegUpper] = currentValue;
                        registers[partialRegUpper] = value;
                    }
                }
                else if (partialRegUpper == "DL" || partialRegUpper == "DH")
                {
                    if (fullRegUpper == "DX")
                    {
                        if (partialRegUpper == "DL")
                        {
                            currentValue = (ushort)((currentValue & 0xFF00) | value);
                        }
                        else if (partialRegUpper == "DH")
                        {
                            currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                        }
                        registers[fullRegUpper] = currentValue;
                        registers[partialRegUpper] = value;
                    }
                }
                else if (partialRegUpper == "BL" || partialRegUpper == "BH")
                {
                    if (fullRegUpper == "BX")
                    {
                        if (partialRegUpper == "BL")
                        {
                            currentValue = (ushort)((currentValue & 0xFF00) | value);
                        }
                        else if (partialRegUpper == "BH")
                        {
                            currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                        }
                        registers[fullRegUpper] = currentValue;
                        registers[partialRegUpper] = value;
                    }
                }
            }
        }

        private class PathAnalysisResult
        {
            public HashSet<string> FoundTexts { get; set; } = new HashSet<string>();
            public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
            public byte? MonsterIndex1 { get; set; }
            public byte? MonsterIndex2 { get; set; }

            // Новые поля для анализа циклов
            public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
                = new Dictionary<int, (byte, byte, bool)>();

            public bool IsInLoop { get; set; } = false;        // Находимся ли мы внутри цикла
            public uint LoopStartAddress { get; set; } = 0;    // Адрес начала цикла
            public int LoopIteration { get; set; } = 0;        // Текущая итерация цикла
            public bool IsIndeterminateLoop { get; set; } = false; // Цикл с неизвестной границей
            public int LoopIterationCount { get; set; } = 0;
        }

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

                fs.Seek(_config.StartAddress, SeekOrigin.Begin);
                byte numObjects = br.ReadByte();

                var coordinates = ReadCoordinates(br, numObjects);
                var directions = ReadDirections(br, numObjects);
                var patchKeys = ReadPatchKeys(br, numObjects);

                for (int i = 0; i < numObjects; i++)
                {
                    var obj = ProcessObject(br, i + 1, coordinates[i], directions[i], patchKeys[i]);
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

            // 2. Анализируем основной путь (Path0)
            var mainRegisterTracker = new RegisterTracker();
            var mainPathAnalysis = AnalyzeMainPath(br, patchAddress, mainRegisterTracker, ovrObject);
            ovrObject.PathTexts[0] = mainPathAnalysis.FoundTexts;

            // Сохраняем информацию о монстрах из основного пути
            if (mainPathAnalysis.MonsterPower.HasValue)
                ovrObject.MonsterPower = mainPathAnalysis.MonsterPower.Value;
            if (mainPathAnalysis.MonsterLevel.HasValue)
                ovrObject.MonsterLevel = mainPathAnalysis.MonsterLevel.Value;

            // Сохраняем монстров из ОСНОВНОГО пути
            bool isMainPathIndeterminate = mainPathAnalysis.IsIndeterminateLoop;
            foreach (var entry in mainPathAnalysis.BattleMonsterEntries.OrderBy(e => e.Key))
            {
                if (entry.Value.val1 != 0 || entry.Value.val2 != 0)
                {
                    ovrObject.AddBattleMonster(
                        entry.Key,
                        entry.Value.val1,
                        entry.Value.val2,
                        entry.Key > 0 && (isMainPathIndeterminate || entry.Value.isIndeterminate)
                    );
                    Debug.WriteLine($"  [PROCESS] MAIN: BX={entry.Key}, val1={entry.Value.val1}, val2={entry.Value.val2}, indeterminate={entry.Key > 0 && (isMainPathIndeterminate || entry.Value.isIndeterminate)}");
                }
            }

            // 3. Глобальный набор для отслеживания уже проанализированных путей
            var globallyAnalyzedPaths = new HashSet<string>();

            // 4. Анализируем все альтернативные пути
            int currentPathNumber = 1;
            foreach (var path in alternativePaths)
            {
                if (path.Analyzed) continue;

                string globalPathKey = $"{patchAddress:X4}_{path.Address:X4}_{path.TargetAddress:X4}";
                if (!globallyAnalyzedPaths.Contains(globalPathKey))
                {
                    globallyAnalyzedPaths.Add(globalPathKey);

                    var pathRegisterTracker = new RegisterTracker();
                    var pathResult = AnalyzeAlternativePath(br, patchAddress, path.Address, path.TargetAddress,
                        objIndex, currentPathNumber, new HashSet<string>(), pathRegisterTracker, 0);

                    ovrObject.PathTexts[currentPathNumber] = pathResult.FoundTexts;

                    // Сохраняем информацию о монстрах из альтернативного пути
                    if (pathResult.MonsterPower.HasValue)
                        ovrObject.MonsterPower = pathResult.MonsterPower.Value;
                    if (pathResult.MonsterLevel.HasValue)
                        ovrObject.MonsterLevel = pathResult.MonsterLevel.Value;

                    // Сохраняем монстров из АЛЬТЕРНАТИВНОГО пути
                    bool isAltPathIndeterminate = pathResult.IsIndeterminateLoop;
                    foreach (var entry in pathResult.BattleMonsterEntries.OrderBy(e => e.Key))
                    {
                        if (entry.Value.val1 != 0 || entry.Value.val2 != 0)
                        {
                            // Проверяем, есть ли уже такой монстр
                            var existingMonster = ovrObject.BattleMonsters.FirstOrDefault(m => m.Index == entry.Key);

                            if (existingMonster != null)
                            {
                                Debug.WriteLine($"  [PROCESS] ALT SKIP: BX={entry.Key} уже существует, IsIndeterminate={existingMonster.IsIndeterminate}");
                            }
                            else
                            {
                                ovrObject.AddBattleMonster(
                                    entry.Key,
                                    entry.Value.val1,
                                    entry.Value.val2,
                                    entry.Key > 0 && (isAltPathIndeterminate || entry.Value.isIndeterminate)
                                );
                                Debug.WriteLine($"  [PROCESS] ALT ADD: BX={entry.Key}, val1={entry.Value.val1}, val2={entry.Value.val2}, indeterminate={entry.Key > 0 && (isAltPathIndeterminate || entry.Value.isIndeterminate)}");
                            }
                        }
                    }

                    SaveNestedPathsRecursively(ovrObject.PathTexts, pathResult.NestedPaths, ref currentPathNumber);
                    path.Analyzed = true;
                    currentPathNumber++;
                }
            }

            // 5. Фильтруем уникальные пути
            ovrObject.PathTexts = FilterUniquePaths(ovrObject.PathTexts);

            return ovrObject;
        }

        private PathAnalysisResult AnalyzeMainPath(BinaryReader br, uint patchAddress,
     RegisterTracker registerTracker, OvrObject currentObject)
        {
            // НОВЫЙ РЕЗУЛЬТАТ - ВСЕГДА С ЧИСТОГО ЛИСТА
            var result = new PathAnalysisResult();

            // ЯВНО СБРАСЫВАЕМ ВСЕ СОСТОЯНИЯ
            result.IsIndeterminateLoop = false;
            result.LoopStartAddress = 0;
            result.BattleMonsterEntries.Clear();
            result.FoundTexts.Clear();
            result.MonsterPower = null;
            result.MonsterLevel = null;
            result.MonsterIndex1 = null;
            result.MonsterIndex2 = null;

            // Анализируем косвенные пути загрузки текста
            var indirectTexts = AnalyzeIndirectTextPatterns(br, patchAddress);
            foreach (var text in indirectTexts)
            {
                result.FoundTexts.Add(text);
            }

            // Анализируем CALL инструкции
            var analyzedCalls = new HashSet<uint>();
            AnalyzeCallsWithFullDisassembly(br, patchAddress, analyzedCalls, result,
                registerTracker, currentObject, 0);

            return result;
        }

        private PathAnalysisResult AnalyzeAlternativePath(BinaryReader br, uint patchAddress, uint jumpAddress,
    uint alternativeStartAddress, int objIndex, int pathIndex, HashSet<string> alreadyAnalyzedPaths,
    RegisterTracker registerTracker, int recursionDepth)
        {
            const int MAX_RECURSION_DEPTH = 3;

            // НОВЫЙ РЕЗУЛЬТАТ - ВСЕГДА С ЧИСТОГО ЛИСТА
            var result = new PathAnalysisResult();

            // ЯВНО СБРАСЫВАЕМ ВСЕ СОСТОЯНИЯ
            result.IsIndeterminateLoop = false;
            result.LoopStartAddress = 0;
            result.BattleMonsterEntries.Clear();
            result.FoundTexts.Clear();
            result.MonsterPower = null;
            result.MonsterLevel = null;
            result.MonsterIndex1 = null;
            result.MonsterIndex2 = null;

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

            // 4. Анализируем ВЛОЖЕННЫЕ альтернативные пути
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

        private void SaveNestedPathsRecursively(Dictionary<int, HashSet<string>> allPathTexts,
            Dictionary<int, PathAnalysisResult> nestedPaths, ref int nextPathNumber)
        {
            foreach (var kvp in nestedPaths.OrderBy(x => x.Key))
            {
                if (!allPathTexts.ContainsKey(kvp.Key))
                {
                    allPathTexts[kvp.Key] = kvp.Value.FoundTexts;
                }

                if (kvp.Value.NestedPaths.Count > 0)
                {
                    SaveNestedPathsRecursively(allPathTexts, kvp.Value.NestedPaths, ref nextPathNumber);
                }
            }
        }

        private Dictionary<int, HashSet<string>> FilterUniquePaths(Dictionary<int, HashSet<string>> allPathTexts)
        {
            var uniquePaths = new Dictionary<int, HashSet<string>>();
            var processedTextSets = new List<HashSet<string>>();

            var sortedPaths = allPathTexts.OrderBy(kvp => kvp.Key).ToList();

            foreach (var kvp in sortedPaths)
            {
                if (kvp.Value == null || kvp.Value.Count == 0)
                    continue;

                bool isUnique = true;
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

                            if ((uint)insn.Address >= nestedJumpAddress)
                            {
                                return true;
                            }

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
                        FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
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
            const int MAX_LOOP_ITERATIONS = 8; // Разумный предел для моделирования цикла

            if (depth > MAX_CALL_DEPTH) return;
            if (analyzedAddresses.Contains(patchAddress)) return;
            analyzedAddresses.Add(patchAddress);

            long fileLength = br.BaseStream.Length;
            if (patchAddress >= fileLength) return;

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = patchAddress;
                const int MAX_INSTRUCTIONS = 100;
                int instructionsShown = 0;
                bool jumpTaken = false;
                bool shouldStop = false;

                // Счетчик итераций цикла для текущего пути
                int loopIterationCount = 0;

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

                        byte[] instructionBytes = insn.Bytes;
                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        // Поиск текстов, статистик монстров, информации о битве и отслеживание регистров
                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        // ============ ОБРАБОТКА УСЛОВНОГО ПЕРЕХОДА (ОСНОВНОЙ) ============
                        if (insn.Address == jumpAddress && !jumpTaken)
                        {
                            jumpTaken = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        // ============ ОБРАБОТКА ЦИКЛА С МОНСТРАМИ ============
                        // ВАЖНО: Реагируем ТОЛЬКО на CMP BL, [0x3c1d] - это цикл с монстрами
                        if ((mnemonicUpper == "JC" || mnemonicUpper == "JB") &&
                            instructionBytes.Length >= 4 &&
                            instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E && // CMP BL, [addr]
                            instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)   // [0x3c1d] - ТОЛЬКО ЭТОТ!
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            // Переход назад - обнаружен цикл с монстрами
                            if (jumpTarget < currentAddress)
                            {
                                // Устанавливаем флаг цикла с неизвестной границей
                                result.IsIndeterminateLoop = true;
                                result.LoopStartAddress = jumpTarget;

                                // Увеличиваем BL (имитируем INC BL в конце цикла)
                                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                                {
                                    byte newValue = (byte)(blValue + 1);
                                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                                        (uint)insn.Address, "INC BL (monster cycle)");

                                    loopIterationCount++;
                                    Debug.WriteLine($"  [MONSTER CYCLE] Итерация {loopIterationCount}, BL={blValue} -> {newValue}");
                                }

                                // Продолжаем моделировать цикл (но не бесконечно)
                                if (loopIterationCount < MAX_LOOP_ITERATIONS)
                                {
                                    currentAddress = jumpTarget;
                                    break;
                                }
                                else
                                {
                                    Debug.WriteLine($"  [MONSTER CYCLE] Достигнут лимит итераций ({MAX_LOOP_ITERATIONS})");
                                    shouldStop = true;
                                    break;
                                }
                            }
                        }

                        // ============ ОБРАБОТКА ДРУГИХ УСЛОВНЫХ ПЕРЕХОДОВ ============
                        // Сюда попадают ВСЕ остальные CMP/JC, включая CMP BL, [0x3bc0] из первого цикла
                        else if (mnemonicUpper.StartsWith("J") &&
                                 !mnemonicUpper.StartsWith("JMP") &&
                                 !mnemonicUpper.StartsWith("JECXZ"))
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget != 0 && jumpTarget < fileLength)
                            {
                                // Для остальных условных переходов просто продолжаем линейно
                                // НЕ устанавливаем флаг цикла
                                currentAddress = nextAddress;
                                break;
                            }
                        }

                        // ============ ОБРАБОТКА CALL ============
                        if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0, analyzedAddresses,
                                    result, registerTracker, depth + 1, callDepth + 1);
                            }
                        }

                        // ============ ОБРАБОТКА ВОЗВРАТА ============
                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            shouldStop = true;
                            break;
                        }

                        // ============ ОБРАБОТКА БЕЗУСЛОВНОГО ПЕРЕХОДА ============
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

                        // По умолчанию - переходим к следующей инструкции
                        currentAddress = nextAddress;
                    }
                }
            }
        }

        private void AnalyzeCallsWithNestedAlternativeBranch(BinaryReader br, uint patchAddress,
            uint jumpAddress, uint alternativeStartAddress, HashSet<uint> analyzedAddresses,
            PathAnalysisResult result, RegisterTracker registerTracker, int depth, HashSet<string> alreadyAnalyzedConditions = null)
        {
            if (depth > 5) return;

            if (alreadyAnalyzedConditions == null)
                alreadyAnalyzedConditions = new HashSet<string>();

            long fileLength = br.BaseStream.Length;

            string pathKey = $"{patchAddress:X4}_{jumpAddress:X4}_{alternativeStartAddress:X4}_{depth}";
            if (alreadyAnalyzedConditions.Contains(pathKey)) return;
            alreadyAnalyzedConditions.Add(pathKey);

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = patchAddress;
                bool reachedJumpAddress = false;
                int maxSteps = 100;
                int stepsTaken = 0;

                while (currentAddress < jumpAddress && currentAddress < fileLength && stepsTaken < maxSteps)
                {
                    stepsTaken++;

                    string positionKey = $"{currentAddress:X4}_{jumpAddress:X4}";
                    if (alreadyAnalyzedConditions.Contains(positionKey)) break;
                    alreadyAnalyzedConditions.Add(positionKey);

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);
                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0) break;

                    bool processedInstruction = false;
                    foreach (var insn in instructions)
                    {
                        if ((uint)insn.Address >= jumpAddress)
                        {
                            reachedJumpAddress = true;
                            break;
                        }

                        string mnemonic = insn.Mnemonic.ToUpper();

                        if (mnemonic.StartsWith("CALL"))
                        {
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0,
                                    analyzedAddresses, result, registerTracker, depth + 1, 0);
                            }

                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            processedInstruction = true;
                            break;
                        }
                        else if (mnemonic.StartsWith("J") && !mnemonic.StartsWith("JMP") && !mnemonic.StartsWith("JECXZ"))
                        {
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            uint target = GetInstructionTargetAddress(insn, fileLength);

                            if (alreadyAnalyzedConditions.Contains($"{target:X4}_visited"))
                            {
                                currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            }
                            else
                            {
                                alreadyAnalyzedConditions.Add($"{target:X4}_visited");
                                currentAddress = target;
                            }

                            processedInstruction = true;
                            break;
                        }
                        else
                        {
                            FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                            FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                            FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                            TrackRegisterOperations(insn, br, registerTracker, depth);

                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            processedInstruction = true;
                            break;
                        }
                    }

                    if (!processedInstruction) break;
                    if (reachedJumpAddress) break;
                }

                AnalyzeSpecificJumpExecutionWithFullCollection(br, jumpAddress, alternativeStartAddress,
                    analyzedAddresses, result, registerTracker, depth, "");
            }
        }

        private void AnalyzeSpecificJumpExecutionWithFullCollection(BinaryReader br, uint jumpAddress, uint alternativeStartAddress,
            HashSet<uint> analyzedAddresses, PathAnalysisResult result, RegisterTracker registerTracker, int depth, string prefix)
        {
            if (analyzedAddresses.Contains(jumpAddress)) return;
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

                    if (instructions == null || instructions.Length == 0) break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS) break;
                        instructionsShown++;

                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        string mnemonic = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        if (insn.Address == jumpAddress && !jumpExecuted)
                        {
                            jumpExecuted = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        if (mnemonic == "RET" || mnemonic == "RETF") return;

                        if (mnemonic == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget >= fileLength) return;
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
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0,
                                    analyzedAddresses, result, registerTracker, depth + 1, 0);
                            }
                        }

                        currentAddress = nextAddress;
                    }
                }
            }
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
                    if (processedAddresses.ContainsKey(currentAddress))
                    {
                        processedAddresses[currentAddress]++;
                        if (processedAddresses[currentAddress] > 2) break;
                    }
                    else
                    {
                        processedAddresses[currentAddress] = 1;
                    }

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0) break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS) break;
                        instructionsShown++;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

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

                                bool alreadyExists = alternativePaths.Any(p =>
                                    p.Address == altPath.Address && p.TargetAddress == altPath.TargetAddress);

                                if (!alreadyExists)
                                {
                                    alternativePaths.Add(altPath);
                                }
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
                uint currentAddress = patchAddress;
                int instructionsShown = 0;
                const int MAX_INSTRUCTIONS = 100;

                var processedAddresses = new Dictionary<uint, int>();
                bool jumpTaken = false;
                bool shouldStop = false;

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS && !shouldStop)
                {
                    if (processedAddresses.ContainsKey(currentAddress))
                    {
                        processedAddresses[currentAddress]++;
                        if (processedAddresses[currentAddress] > 3) break;
                    }
                    else
                    {
                        processedAddresses[currentAddress] = 1;
                    }

                    int bytesToRead = (int)Math.Min(32, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0) break;

                    foreach (var insn in instructions)
                    {
                        if (instructionsShown >= MAX_INSTRUCTIONS || shouldStop) break;
                        instructionsShown++;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

                        if (insn.Address == jumpAddress && !jumpTaken)
                        {
                            jumpTaken = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

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
                    if (visitedAddresses.Contains(currentAddress)) break;
                    visitedAddresses.Add(currentAddress);

                    int bytesToRead = (int)Math.Min(32, br.BaseStream.Length - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var instructions = capstone.Disassemble(chunk, currentAddress);
                    if (instructions == null || instructions.Length == 0) break;

                    bool processedInstruction = false;
                    foreach (var insn in instructions)
                    {
                        if (instructionCount >= MAX_INSTRUCTIONS) break;

                        path.Add(insn);
                        instructionCount++;
                        processedInstruction = true;

                        string mnemonicUpper = insn.Mnemonic.ToUpper();
                        uint nextAddress = (uint)(insn.Address + insn.Bytes.Length);

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
                            currentAddress = nextAddress;
                            break;
                        }
                        else if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            return path;
                        }
                        else if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                            if (callTarget != 0 && callTarget < br.BaseStream.Length)
                            {
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

                    if (!processedInstruction) break;
                }
            }

            return path;
        }

        private bool IsNestedPath(uint jumpAddress, uint alternativeStartAddress)
        {
            return jumpAddress != 0 && alternativeStartAddress > jumpAddress && jumpAddress > 0x0090;
        }

        private void FindTextsInInstruction(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker,
            int depth, HashSet<string> output)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

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

            if (instructionBytes.Length >= 5 &&
                instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
            {
                byte monsterPower = instructionBytes[4];
                result.MonsterPower = monsterPower;
            }
            else if (instructionBytes.Length >= 5 &&
                     instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                     instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
            {
                byte monsterLevel = instructionBytes[4];
                result.MonsterLevel = monsterLevel;
            }
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E)
            {
                ushort targetAddr = BitConverter.ToUInt16(instructionBytes, 2);

                if (targetAddr == 0xC96F || targetAddr == 0xC961)
                {
                    byte modRM = instructionBytes[1];
                    byte regField = (byte)((modRM >> 3) & 0x07);

                    if (regField == 5)
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
            else if (instructionBytes.Length >= 3)
            {
                if (instructionBytes[0] == 0xA2 &&
                    instructionBytes[1] == 0x6F && instructionBytes[2] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        result.MonsterPower = (byte)alValue;
                    }
                }
                else if (instructionBytes[0] == 0xA2 &&
                         instructionBytes[1] == 0x61 && instructionBytes[2] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        result.MonsterLevel = (byte)alValue;
                    }
                }
            }
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 && instructionBytes[1] == 0x0E)
            {
                ushort targetAddr = BitConverter.ToUInt16(instructionBytes, 2);

                if (targetAddr == 0xC96F || targetAddr == 0xC961)
                {
                    byte modRM = instructionBytes[1];
                    byte regField = (byte)((modRM >> 3) & 0x07);

                    if (regField >= 1 && regField <= 7)
                    {
                        string regName = regField switch
                        {
                            1 => "CL",
                            2 => "DL",
                            3 => "BL",
                            4 => "AH",
                            5 => "CH",
                            6 => "DH",
                            7 => "BH",
                            _ => ""
                        };

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

        private void FindMonsterBattleInfo(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker,
    int depth, PathAnalysisResult result)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;
            string mnemonicUpper = insn.Mnemonic.ToUpper();
            long fileLength = br.BaseStream.Length;

            // ============ ОБНАРУЖЕНИЕ ЦИКЛА С МОНСТРАМИ ============
            // Проверяем CMP BL, [0x3c1d] - это конкретный паттерн цикла с монстрами
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E && // CMP BL, [addr]
                instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)   // [0x3c1d]
            {
                result.IsIndeterminateLoop = true;
                result.LoopStartAddress = address;

                // ============ ВАЖНО: РЕТРОАКТИВНО ПОМЕЧАЕМ ВСЕ СУЩЕСТВУЮЩИЕ ЗАПИСИ ============
                // Проходим по всем уже сохранённым монстрам
                foreach (var key in result.BattleMonsterEntries.Keys.ToList())
                {
                    if (key > 0) // Только BX > 0
                    {
                        var entry = result.BattleMonsterEntries[key];
                        result.BattleMonsterEntries[key] = (entry.val1, entry.val2, true);
                        Debug.WriteLine($"  [BATTLE] РЕТРОАКТИВНО: BX={key} помечен как indeterminate");
                    }
                }
                // ========================================================================

                Debug.WriteLine($"  [BATTLE] ОБНАРУЖЕН ЦИКЛ МОНСТРОВ: CMP BL, [0x3c1d] по адресу 0x{address:X4}");
                Debug.WriteLine($"  [BATTLE] Все записи с BX>0 помечены как indeterminate");
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C58], AL ============
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x88 &&
                instructionBytes[1] == 0x87 &&
                instructionBytes[2] == 0x58 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        byte val1 = (byte)alValue;

                        // КЛЮЧЕВАЯ ЛОГИКА: 
                        // BX = 0 -> всегда determinate (запись ДО цикла)
                        // BX > 0 И цикл с неизвестной границей обнаружен -> indeterminate
                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C58] BX={bxValue}, AL={val1}, Indeterminate={isIndeterminate}, IsIndeterminateLoop={result.IsIndeterminateLoop}");
                    }
                }
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C29], CL ============
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x8F &&
                     instructionBytes[2] == 0x29 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("CL", out ushort clValue))
                    {
                        byte val2 = (byte)clValue;

                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C29] BX={bxValue}, CL={val2}, Indeterminate={isIndeterminate}, IsIndeterminateLoop={result.IsIndeterminateLoop}");
                    }
                }
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C58], DL ============
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x97 &&
                     instructionBytes[2] == 0x58 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("DL", out ushort dlValue))
                    {
                        byte val1 = (byte)dlValue;

                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C58] BX={bxValue}, DL={val1}, Indeterminate={isIndeterminate}");
                    }
                }
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C29], DL ============
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x97 &&
                     instructionBytes[2] == 0x29 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("DL", out ushort dlValue))
                    {
                        byte val2 = (byte)dlValue;

                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C29] BX={bxValue}, DL={val2}, Indeterminate={isIndeterminate}");
                    }
                }
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C58], BL ============
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x9F &&
                     instructionBytes[2] == 0x58 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                    {
                        byte val1 = (byte)blValue;

                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C58] BX={bxValue}, BL={val1}, Indeterminate={isIndeterminate}");
                    }
                }
            }

            // ============ ИНДЕКСИРОВАННАЯ ЗАПИСЬ: MOV [BX + 0x3C29], BL ============
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x88 &&
                     instructionBytes[1] == 0x9F &&
                     instructionBytes[2] == 0x29 && instructionBytes[3] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                    {
                        byte val2 = (byte)blValue;

                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                        }

                        Debug.WriteLine($"  [BATTLE] [BX+3C29] BX={bxValue}, BL={val2}, Indeterminate={isIndeterminate}");
                    }
                }
            }

            // ============ ПРЯМАЯ ЗАПИСЬ: MOV [3C58], AL ============
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x58 && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                {
                    result.MonsterIndex1 = (byte)alValue;

                    int bxValue = 0;
                    bool isIndeterminate = false; // Прямая запись - всегда BX=0, всегда determinate

                    if (result.BattleMonsterEntries.ContainsKey(bxValue))
                    {
                        var existing = result.BattleMonsterEntries[bxValue];
                        result.BattleMonsterEntries[bxValue] = ((byte)alValue, existing.val2, isIndeterminate);
                    }
                    else
                    {
                        result.BattleMonsterEntries[bxValue] = ((byte)alValue, 0, isIndeterminate);
                    }

                    Debug.WriteLine($"  [BATTLE] [3C58] AL={alValue} (прямая запись, BX=0, determinate)");
                }
            }

            // ============ ПРЯМАЯ ЗАПИСЬ: MOV [3C29], AL ============
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x29 && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                {
                    result.MonsterIndex2 = (byte)alValue;

                    int bxValue = 0;
                    bool isIndeterminate = false; // Прямая запись - всегда BX=0, всегда determinate

                    if (result.BattleMonsterEntries.ContainsKey(bxValue))
                    {
                        var existing = result.BattleMonsterEntries[bxValue];
                        result.BattleMonsterEntries[bxValue] = (existing.val1, (byte)alValue, isIndeterminate);
                    }
                    else
                    {
                        result.BattleMonsterEntries[bxValue] = (0, (byte)alValue, isIndeterminate);
                    }

                    Debug.WriteLine($"  [BATTLE] [3C29] AL={alValue} (прямая запись, BX=0, determinate)");
                }
            }
        }

        private void TrackRegisterOperations(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker, int depth)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            #region Загрузка непосредственных значений в регистры

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

            #endregion

            #region Специфические загрузки для конкретных регистров

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

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBE)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                registerTracker.SetRegisterValue("SI", immediateValue, address,
                    $"MOV SI, 0x{immediateValue:X4}");
            }

            if (instructionBytes.Length >= 3 && instructionBytes[0] == 0xBF)
            {
                ushort immediateValue = BitConverter.ToUInt16(instructionBytes, 1);
                registerTracker.SetRegisterValue("DI", immediateValue, address,
                    $"MOV DI, 0x{immediateValue:X4}");
            }

            #endregion

            #region Частичные загрузки в младшие/старшие байты регистров

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

            #endregion

            #region Арифметические операции с регистрами

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0xFE &&
                instructionBytes[1] == 0xC3)
            {
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    byte newValue = (byte)(blValue + 1);
                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                        address, "INC BL");
                }
            }

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0xFE &&
                instructionBytes[1] == 0xC7)
            {
                if (registerTracker.TryGetRegisterValue("BH", out ushort bhValue))
                {
                    byte newValue = (byte)(bhValue + 1);
                    registerTracker.TrackPartialRegisterOperation("BX", "BH", newValue,
                        address, "INC BH");
                }
            }

            if (instructionBytes.Length >= 1 &&
                instructionBytes[0] == 0x43)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    ushort newValue = (ushort)(bxValue + 1);
                    registerTracker.SetRegisterValue("BX", newValue, address,
                        $"INC BX: {bxValue} -> {newValue}");
                }
            }

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0xFE &&
                instructionBytes[1] == 0xCB)
            {
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    byte newValue = (byte)(blValue - 1);
                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                        address, "DEC BL");
                }
            }

            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0x80 &&
                instructionBytes[1] == 0xC3)
            {
                byte addValue = instructionBytes[2];
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    byte newValue = (byte)(blValue + addValue);
                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                        address, $"ADD BL, 0x{addValue:X2}");
                }
            }

            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0x80 &&
                instructionBytes[1] == 0xEB)
            {
                byte subValue = instructionBytes[2];
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    byte newValue = (byte)(blValue - subValue);
                    registerTracker.TrackPartialRegisterOperation("BX", "BL", newValue,
                        address, $"SUB BL, 0x{subValue:X2}");
                }
            }

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0xD0 &&
                instructionBytes[1] == 0xE0)
            {
                if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                {
                    byte newValue = (byte)(alValue << 1);
                    registerTracker.TrackPartialRegisterOperation("AX", "AL", newValue,
                        address, "SHL AL, 1");
                }
            }

            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x81 &&
                instructionBytes[1] == 0xE5 &&
                instructionBytes[2] == 0xFF &&
                instructionBytes[3] == 0x00)
            {
                if (registerTracker.TryGetRegisterValue("BP", out ushort bpValue))
                {
                    ushort newValue = (ushort)(bpValue & 0xFF);
                    registerTracker.SetRegisterValue("BP", newValue, address,
                        $"AND BP, 0xFF: {bpValue:X4} -> {newValue:X4}");
                }
            }

            #endregion

            #region Операции сравнения

            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0x3A &&
                instructionBytes[1] == 0x1E)
            {
                ushort addr = BitConverter.ToUInt16(instructionBytes, 2);
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    // Не меняем значение регистра, только логируем
                }
            }

            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0x80 &&
                instructionBytes[1] == 0xFB)
            {
                byte cmpValue = instructionBytes[2];
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    // Не меняем значение регистра, только логируем
                }
            }

            #endregion

            #region Отслеживание записей в память (сила и уровень монстров)

            if (instructionBytes.Length >= 4)
            {
                if (instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                    instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("CH", out ushort regValue))
                    {
                        registerTracker.SetRegisterValue("MEM_C96F", (byte)regValue, address,
                            $"MOV [C96F], CH (value: {regValue})");
                    }
                }
                else if (instructionBytes[0] == 0x88 && instructionBytes[1] == 0x2E &&
                         instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
                {
                    if (registerTracker.TryGetRegisterValue("CH", out ushort regValue))
                    {
                        registerTracker.SetRegisterValue("MEM_C961", (byte)regValue, address,
                            $"MOV [C961], CH (value: {regValue})");
                    }
                }
                else if (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                         instructionBytes[2] == 0x6F && instructionBytes[3] == 0xC9)
                {
                    if (instructionBytes.Length >= 5)
                    {
                        byte value = instructionBytes[4];
                        registerTracker.SetRegisterValue("MEM_C96F", value, address,
                            $"MOV [C96F], {value}");
                    }
                }
                else if (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                         instructionBytes[2] == 0x61 && instructionBytes[3] == 0xC9)
                {
                    if (instructionBytes.Length >= 5)
                    {
                        byte value = instructionBytes[4];
                        registerTracker.SetRegisterValue("MEM_C961", value, address,
                            $"MOV [C961], {value}");
                    }
                }
            }

            #endregion

            #region Отслеживание записей в память (индексы монстров для битвы)

            if (instructionBytes.Length >= 4)
            {
                if (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                    instructionBytes[2] == 0x58 && instructionBytes[3] == 0x3C)
                {
                    if (instructionBytes.Length >= 5)
                    {
                        byte value = instructionBytes[4];
                        registerTracker.SetRegisterValue("MEM_3C58", value, address,
                            $"MOV [3C58], {value}");
                    }
                }
                else if (instructionBytes[0] == 0xC6 && instructionBytes[1] == 0x06 &&
                         instructionBytes[2] == 0x29 && instructionBytes[3] == 0x3C)
                {
                    if (instructionBytes.Length >= 5)
                    {
                        byte value = instructionBytes[4];
                        registerTracker.SetRegisterValue("MEM_3C29", value, address,
                            $"MOV [3C29], {value}");
                    }
                }
                else if (instructionBytes[0] == 0xA2 &&
                         instructionBytes[1] == 0x58 && instructionBytes[2] == 0x3C)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        registerTracker.SetRegisterValue("MEM_3C58", (byte)alValue, address,
                            $"MOV [3C58], AL (value: {alValue})");
                    }
                }
                else if (instructionBytes[0] == 0xA2 &&
                         instructionBytes[1] == 0x29 && instructionBytes[2] == 0x3C)
                {
                    if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                    {
                        registerTracker.SetRegisterValue("MEM_3C29", (byte)alValue, address,
                            $"MOV [3C29], AL (value: {alValue})");
                    }
                }
            }

            #endregion

            #region Пересылки между регистрами

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0x8B &&
                (instructionBytes[1] & 0xC0) == 0xC0)
            {
                byte modRM = instructionBytes[1];
                byte destReg = (byte)((modRM >> 3) & 0x07);
                byte srcReg = (byte)(modRM & 0x07);

                string[] regNames16 = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (destReg < regNames16.Length && srcReg < regNames16.Length)
                {
                    if (registerTracker.TryGetRegisterValue(regNames16[srcReg], out ushort srcValue))
                    {
                        registerTracker.SetRegisterValue(regNames16[destReg], srcValue, address,
                            $"MOV {regNames16[destReg]}, {regNames16[srcReg]} (0x{srcValue:X4})");
                    }
                }
            }

            if (instructionBytes.Length >= 2 &&
                instructionBytes[0] == 0x8A &&
                (instructionBytes[1] & 0xC0) == 0xC0)
            {
                byte modRM = instructionBytes[1];
                byte destReg = (byte)((modRM >> 3) & 0x07);
                byte srcReg = (byte)(modRM & 0x07);

                string[] regNames8 = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };

                if (destReg < regNames8.Length && srcReg < regNames8.Length)
                {
                    if (registerTracker.TryGetRegisterValue(regNames8[srcReg], out ushort srcValue))
                    {
                        string destFullReg = regNames8[destReg] switch
                        {
                            "AL" or "AH" => "AX",
                            "CL" or "CH" => "CX",
                            "DL" or "DH" => "DX",
                            "BL" or "BH" => "BX",
                            _ => ""
                        };

                        if (!string.IsNullOrEmpty(destFullReg))
                        {
                            registerTracker.TrackPartialRegisterOperation(destFullReg, regNames8[destReg],
                                (byte)srcValue, address,
                                $"MOV {regNames8[destReg]}, {regNames8[srcReg]} (0x{srcValue:X2})");
                        }
                    }
                }
            }

            #endregion
        }
    }
}