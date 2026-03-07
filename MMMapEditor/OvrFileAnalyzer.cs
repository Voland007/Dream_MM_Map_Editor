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
        private class JumpCondition
        {
            public string Type { get; set; }
            public uint Target { get; set; }
        }

        private readonly OvrFileConfig _config;

        public OvrFileAnalyzer(OvrFileConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // Вспомогательный класс для альтернативных путей
        private class AlternativePath
        {
            public int ObjectIndex { get; set; }
            public uint Address { get; set; }
            public string Condition { get; set; }
            public uint TargetAddress { get; set; }
            public bool Analyzed { get; set; }
            public int PathNumber { get; set; }
            public byte? CompareValue { get; set; }
            public string CompareRegister { get; set; }
        }

        private enum CoordType
        {
            Unknown,
            XCoord,
            YCoord,
            FullCoord
        }

        // Вспомогательный класс для отслеживания регистров
        private class RegisterTracker
        {
            private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
            private Dictionary<string, string> registerSources = new Dictionary<string, string>();
            private Dictionary<string, (ushort addr, bool fromTable, ushort originalBx)> registerSources2 =
                new Dictionary<string, (ushort, bool, ushort)>();

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

            public void SetRegisterValueWithSource(string reg, ushort value, ushort sourceAddr, ushort originalBx, bool fromTable, uint address, string instruction)
            {
                SetRegisterValue(reg, value, address, instruction);
                registerSources2[reg.ToUpper()] = (sourceAddr, fromTable, originalBx);
            }

            public bool IsFromTable(string reg)
            {
                string regUpper = reg.ToUpper();

                if (registerSources2.TryGetValue(regUpper, out var src) && src.fromTable)
                    return true;

                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc) && cxSrc.fromTable)
                    return true;

                if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc) && axSrc.fromTable)
                    return true;

                return false;
            }

            public ushort? GetSourceAddress(string reg)
            {
                string regUpper = reg.ToUpper();

                if (registerSources2.TryGetValue(regUpper, out var src))
                    return src.addr;

                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                    return cxSrc.addr;

                if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                    return axSrc.addr;

                return null;
            }

            public ushort? GetOriginalBx(string reg)
            {
                string regUpper = reg.ToUpper();

                if (registerSources2.TryGetValue(regUpper, out var src))
                    return src.originalBx;

                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                    return cxSrc.originalBx;

                if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                    return axSrc.originalBx;

                return null;
            }

            public bool TryGetRegisterValue(string reg, out ushort value)
            {
                return registers.TryGetValue(reg.ToUpper(), out value);
            }

            public void Clear()
            {
                registers.Clear();
                registerSources.Clear();
                registerSources2.Clear();
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

            public RegisterTracker Clone()
            {
                var clone = new RegisterTracker();
                foreach (var kvp in registers)
                {
                    clone.registers[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in registerSources)
                {
                    clone.registerSources[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in registerSources2)
                {
                    clone.registerSources2[kvp.Key] = kvp.Value;
                }
                return clone;
            }
        }

        private class PathAnalysisResult
        {
            public HashSet<string> FoundTexts { get; set; } = new HashSet<string>();
            public HashSet<string> ContextTexts { get; set; } = new HashSet<string>(); // тексты из вызывающего кода
            public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
            public byte? MonsterIndex1 { get; set; }
            public byte? MonsterIndex2 { get; set; }
            public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
                = new Dictionary<int, (byte, byte, bool)>();
            public bool HasPartialBattlePattern { get; set; } = false;
            public List<PartialBattleInfo> PartialBattleInfo { get; set; } = new List<PartialBattleInfo>();
            public List<PartiallyDefinedBattle> PartialBattles { get; set; } = new List<PartiallyDefinedBattle>();
            public bool IsInLoop { get; set; } = false;
            public uint LoopStartAddress { get; set; } = 0;
            public int LoopIteration { get; set; } = 0;
            public bool IsIndeterminateLoop { get; set; } = false;
            public int LoopIterationCount { get; set; } = 0;
            public bool IsTerminated { get; set; } = false;
            public bool HasSignificantCode { get; set; } = false;
            public List<AlternativePath> AlternativePaths { get; set; } = new List<AlternativePath>();

            // Адрес первой инструкции, которая загрузила локальный текст
            public uint FirstLocalTextAddress { get; set; } = uint.MaxValue;
        }

        private class PartialBattleInfo
        {
            public int BxIndex { get; set; }
            public ushort DestAddr { get; set; }
            public string SrcReg { get; set; }
            public byte SrcRegValue { get; set; }
            public bool IsFromTable { get; set; }
            public ushort? SourceTableAddr { get; set; }
        }

        private class MemoryAccess
        {
            public uint Address { get; set; }
            public string Instruction { get; set; }
            public string Operand { get; set; }
            public ushort MemoryAddr { get; set; }
            public byte? ImmediateValue { get; set; }
            public string Register { get; set; }
            public AccessType Type { get; set; }
        }

        private enum AccessType
        {
            Read,
            Write,
            Compare,
            Unknown
        }

        private class MacroExecutionResult
        {
            public Dictionary<ConditionRange, CodeEmulationResult> BranchResults { get; set; } = new Dictionary<ConditionRange, CodeEmulationResult>();
            public CodeEmulationResult CommonResult { get; set; } = new CodeEmulationResult();
        }

        private class ConditionRange
        {
            public byte Min { get; set; }
            public byte Max { get; set; }
            public bool IsX { get; set; }

            public bool Matches(byte value)
            {
                return value >= Min && value <= Max;
            }

            public override string ToString()
            {
                return $"{(IsX ? "X" : "Y")} ∈ [{Min}, {Max}]";
            }

            public override bool Equals(object obj)
            {
                if (obj is ConditionRange other)
                {
                    return Min == other.Min && Max == other.Max && IsX == other.IsX;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return (Min << 16) | (Max << 8) | (IsX ? 1 : 0);
            }
        }

        private class ExecutionPath
        {
            public uint StartAddress { get; set; }
            public X86Instruction Condition { get; set; }
            public uint SourceAddress { get; set; }
        }

        private class ExecutionState
        {
            public Dictionary<string, ushort> Registers { get; set; } = new Dictionary<string, ushort>();
            public Stack<uint> CallStack { get; set; } = new Stack<uint>();
            public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
            public CodeEmulationResult CurrentResult { get; set; } = new CodeEmulationResult();
            public ConditionRange CurrentCondition { get; set; }
        }

        private class CodeEmulationResult
        {
            public HashSet<string> Texts { get; set; } = new HashSet<string>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
            public List<(int Index, byte Val1, byte Val2, bool IsIndeterminate)> BattleMonsters { get; set; } = new List<(int, byte, byte, bool)>();
            public byte DirectionByte { get; set; }
            public bool HasSignificantCode { get; set; }
        }

        private class CoordinateComparison
        {
            public uint CompareAddress { get; set; }
            public ushort MemAddr { get; set; }
            public byte Value { get; set; }
            public uint JumpTarget { get; set; }
            public string JumpType { get; set; }
            public bool IsLinear { get; set; }
            public byte LinearStart { get; set; }
            public byte LinearEnd { get; set; }
            public CoordType CoordType { get; set; } = CoordType.Unknown;
        }

        // ========== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ==========

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

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;
                instructions = capstone.Disassemble(chunk, address);
            }

            return chunk;
        }

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

        private uint GetJumpTarget(X86Instruction insn)
        {
            return GetInstructionTargetAddress(insn, 0xFFFF);
        }

        private bool IsReturnInstruction(string mnemonic)
        {
            string upper = mnemonic?.ToUpper() ?? "";
            return upper == "RET" || upper == "RETF" || upper == "RETN" ||
                   upper == "IRET" || upper == "IRETD";
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

        private bool IsCompareWithImmediate(X86Instruction insn, out byte immValue, out string reg)
        {
            immValue = 0;
            reg = "";

            byte[] bytes = insn.Bytes;

            if (bytes.Length >= 2 && bytes[0] == 0x3C)
            {
                immValue = bytes[1];
                reg = "AL";
                return true;
            }

            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xF9)
            {
                immValue = bytes[2];
                reg = "CL";
                return true;
            }

            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xFA)
            {
                immValue = bytes[2];
                reg = "DL";
                return true;
            }

            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xFB)
            {
                immValue = bytes[2];
                reg = "BL";
                return true;
            }

            return false;
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

        private void MergeResults(PathAnalysisResult target, PathAnalysisResult source)
        {
            if (source == null) return;

            foreach (var text in source.FoundTexts)
                target.FoundTexts.Add(text);
            foreach (var text in source.ContextTexts)
                target.ContextTexts.Add(text);

            // Сохраняем минимальный адрес первого локального текста
            if (source.FirstLocalTextAddress < target.FirstLocalTextAddress)
                target.FirstLocalTextAddress = source.FirstLocalTextAddress;

            if (source.MonsterPower.HasValue && !target.MonsterPower.HasValue)
                target.MonsterPower = source.MonsterPower;

            if (source.MonsterLevel.HasValue && !target.MonsterLevel.HasValue)
                target.MonsterLevel = source.MonsterLevel;

            foreach (var entry in source.BattleMonsterEntries)
                target.BattleMonsterEntries[entry.Key] = entry.Value;

            target.PartialBattles.AddRange(source.PartialBattles);
            target.PartialBattleInfo.AddRange(source.PartialBattleInfo);
            target.HasPartialBattlePattern = target.HasPartialBattlePattern || source.HasPartialBattlePattern;
            target.AlternativePaths.AddRange(source.AlternativePaths);
        }

        private bool CheckCondition(byte value, byte compareValue, string jumpType)
        {
            switch (jumpType.ToUpper())
            {
                case "JE":
                case "JZ":
                    return value == compareValue;
                case "JNE":
                case "JNZ":
                    return value != compareValue;
                case "JB":
                case "JC":
                    return value < compareValue;
                case "JBE":
                case "JNA":
                    return value <= compareValue;
                case "JA":
                case "JNBE":
                    return value > compareValue;
                case "JAE":
                case "JNB":
                case "JNC":
                    return value >= compareValue;
                default:
                    return false;
            }
        }

        private string GetConditionSymbol(string jumpType)
        {
            switch (jumpType.ToUpper())
            {
                case "JE": case "JZ": return "==";
                case "JNE": case "JNZ": return "!=";
                case "JB": case "JC": return "<";
                case "JBE": case "JNA": return "<=";
                case "JA": case "JNBE": return ">";
                case "JAE": case "JNB": case "JNC": return ">=";
                default: return "?";
            }
        }

        // ========== ОСНОВНОЙ МЕТОД АНАЛИЗА ==========

        public static List<OvrObject> AnalyzeOvrFile(string filename, OvrFileConfig config, Dictionary<Point, string> existingCentralOptions)
        {
            var analyzer = new OvrFileAnalyzer(config);
            return analyzer.InternalAnalyze(filename, existingCentralOptions);
        }

        private List<OvrObject> InternalAnalyze(string filename, Dictionary<Point, string> existingCentralOptions)
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

                var tableObjectCoords = new HashSet<string>();

                for (int i = 0; i < numObjects; i++)
                {
                    var obj = ProcessObject(br, i + 1, coordinates[i], directions[i], patchKeys[i]);
                    obj.IsFromTable = true;
                    objects.Add(obj);

                    string coordKey = $"{obj.X},{obj.Y}";
                    tableObjectCoords.Add(coordKey);
                    Debug.WriteLine($"  Табличный объект добавлен: ({obj.X},{obj.Y})");
                }

                Debug.WriteLine($"\nОбъекты из таблицы [C973+]:");
                foreach (var obj in objects.Where(o => o.IsFromTable))
                {
                    Debug.WriteLine($"  Объект X={obj.X}, Y={obj.Y}: {obj.PathTexts.Count} путей (IsFromTable={obj.IsFromTable})");
                }

                fs.Seek(0, SeekOrigin.Begin);

                var allInstructions = DisassembleAll(br);
                var comparisons = FindAllCoordinateComparisons(br, allInstructions);

                Debug.WriteLine($"Найдено сравнений координат: {comparisons.Count}");

                foreach (var comparison in comparisons)
                {
                    Debug.WriteLine($"\nАнализ макроса по адресу 0x{comparison.JumpTarget:X4} для [{comparison.MemAddr:X4}]={comparison.Value} ({(comparison.IsLinear ? "линейный" : comparison.JumpType)})");

                    bool isX = (comparison.CoordType == CoordType.XCoord);
                    bool isY = (comparison.CoordType == CoordType.YCoord);
                    bool isFull = (comparison.CoordType == CoordType.FullCoord);

                    byte targetX = 0;
                    byte targetY = 0;
                    if (isFull)
                    {
                        targetX = (byte)(comparison.Value & 0x0F);
                        targetY = (byte)(comparison.Value >> 4);
                        Debug.WriteLine($"  Полная координата: X={targetX}, Y={targetY} (значение 0x{comparison.Value:X2})");
                    }

                    Debug.WriteLine($"  Тип макроса: {(isFull ? "ПОЛНЫЕ КООРДИНАТЫ" : isX ? "X-координата" : "Y-координата")}");

                    // Собираем все альтернативные пути макроса
                    var alternativePaths = new List<AlternativePath>();
                    ShowLinearDisassemblyAndCollectAlternativePaths(br, comparison.JumpTarget, alternativePaths, 0);

                    var allTexts = new HashSet<string>();
                    byte? monsterPower = null;
                    byte? monsterLevel = null;
                    var battleMonsters = new Dictionary<int, (byte val1, byte val2, bool isIndeterminate)>();
                    var partialBattles = new List<PartiallyDefinedBattle>();
                    var partialBattleInfo = new List<PartialBattleInfo>();
                    bool hasPartialBattlePattern = false;

                    // Анализируем основной путь
                    var mainRegisterTracker = new RegisterTracker();
                    var mainPathResult = ExecuteCodeAtAddress(br, comparison.JumpTarget, mainRegisterTracker,
                        new HashSet<uint>(), 0, 0, null, 0);

                    MergeMacroResults(mainPathResult, allTexts, ref monsterPower, ref monsterLevel,
                        battleMonsters, partialBattles, partialBattleInfo, ref hasPartialBattlePattern);

                    // Анализируем все альтернативные пути
                    var processedTargets = new HashSet<uint>();
                    foreach (var path in alternativePaths)
                    {
                        if (processedTargets.Contains(path.TargetAddress))
                            continue;

                        processedTargets.Add(path.TargetAddress);

                        var pathRegisterTracker = new RegisterTracker();
                        var pathResult = ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                            new HashSet<uint>(), 0, 0, null, 0);

                        MergeMacroResults(pathResult, allTexts, ref monsterPower, ref monsterLevel,
                            battleMonsters, partialBattles, partialBattleInfo, ref hasPartialBattlePattern);
                    }

                    bool hasSignificantCode = allTexts.Count > 0 ||
                                              monsterPower.HasValue ||
                                              monsterLevel.HasValue ||
                                              battleMonsters.Count > 0 ||
                                              partialBattles.Count > 0 ||
                                              hasPartialBattlePattern ||
                                              partialBattleInfo.Count > 0;

                    if (!comparison.IsLinear && (comparison.JumpType.ToUpper() == "JE" || comparison.JumpType.ToUpper() == "JZ"))
                    {
                        if (isFull)
                        {
                            hasSignificantCode = true;
                            Debug.WriteLine($"  Макрос JE/JZ для полной координаты считается значимым по умолчанию");
                        }
                    }

                    if (!hasSignificantCode)
                    {
                        Debug.WriteLine("  -> нет значимого кода, пропускаем");
                        continue;
                    }

                    Debug.WriteLine($"  -> найден значимый код: {allTexts.Count} текстов, {battleMonsters.Count} полностью определённых, {partialBattles.Count} частично определённых, паттерн={hasPartialBattlePattern}, загрузок из таблиц={partialBattleInfo.Count}");

                    var targetCells = new List<Point>();

                    if (comparison.IsLinear)
                    {
                        Debug.WriteLine($"  Линейный макрос для значений [{comparison.LinearStart}-{comparison.LinearEnd}]");
                        for (byte y = comparison.LinearStart; y <= comparison.LinearEnd && y < 16; y++)
                        {
                            for (byte x = 0; x < 16; x++)
                            {
                                targetCells.Add(new Point(x, y));
                            }
                        }
                    }
                    else
                    {
                        for (byte x = 0; x < 16; x++)
                        {
                            for (byte y = 0; y < 16; y++)
                            {
                                bool matches = false;

                                if (isFull)
                                {
                                    if (x == targetX && y == targetY)
                                    {
                                        string jumpType = comparison.JumpType.ToUpper();
                                        if (jumpType == "JE" || jumpType == "JZ")
                                        {
                                            matches = true;
                                            Debug.WriteLine($"  Совпадение полной координаты: клетка ({x},{y}) подходит для макроса JE/JZ");
                                        }
                                    }
                                }
                                else if (isX)
                                {
                                    matches = CheckCondition(x, comparison.Value, comparison.JumpType);
                                }
                                else if (isY)
                                {
                                    matches = CheckCondition(y, comparison.Value, comparison.JumpType);
                                }

                                if (matches)
                                {
                                    targetCells.Add(new Point(x, y));
                                }
                            }
                        }
                    }

                    var validCells = targetCells
                        .Where(p =>
                        {
                            string coordKey = $"{p.X},{p.Y}";
                            bool isInTable = tableObjectCoords.Contains(coordKey);
                            bool isRandomEncounter = existingCentralOptions.TryGetValue(p, out string option) && option == "Случайная встреча";

                            if (isInTable)
                            {
                                Debug.WriteLine($"  Пропуск клетки ({p.X},{p.Y}): объект уже есть в таблице.");
                            }
                            else if (!isRandomEncounter)
                            {
                                Debug.WriteLine($"  Пропуск клетки ({p.X},{p.Y}): центральная опция '{option ?? "null"}' не 'Случайная встреча'.");
                            }

                            return !isInTable && isRandomEncounter;
                        })
                        .ToList();

                    if (validCells.Count == 0)
                    {
                        Debug.WriteLine("  Нет подходящих клеток для применения макроса.");
                        continue;
                    }

                    Debug.WriteLine($"  -> создаём объекты для {validCells.Count} клеток");

                    int objectsCreated = 0;
                    foreach (var cellPos in validCells)
                    {
                        var obj = new OvrObject
                        {
                            X = (byte)cellPos.X,
                            Y = (byte)cellPos.Y,
                            DirectionByte = 0,
                            MonsterPower = monsterPower,
                            MonsterLevel = monsterLevel,
                            IsFromTable = false
                        };

                        obj.HasAnyTableLoad = hasPartialBattlePattern;

                        if (allTexts.Count > 0)
                        {
                            obj.PathTexts[0] = new HashSet<string>(allTexts);
                        }

                        foreach (var monster in battleMonsters)
                        {
                            obj.AddBattleMonster(monster.Key, monster.Value.val1, monster.Value.val2, monster.Value.isIndeterminate);
                        }

                        foreach (var partial in partialBattles)
                        {
                            obj.AddPartiallyDefinedBattle(
                                partial.BxIndex,
                                partial.RangeStart1,
                                partial.RangeEnd1,
                                partial.RangeStart2,
                                partial.RangeEnd2
                            );
                        }

                        objects.Add(obj);
                        objectsCreated++;
                        Debug.WriteLine($"  -> СОЗДАН AnyObjectSpec на ({cellPos.X},{cellPos.Y}) из макроса");
                    }

                    Debug.WriteLine($"  -> создано объектов из макроса: {objectsCreated}");
                }

                Debug.WriteLine($"\nВсего объектов после анализа: {objects.Count}");

                int tableObjects = objects.Count(o => o.IsFromTable);
                int specObjects = objects.Count(o => !o.IsFromTable);
                int partialBattleObjects = objects.Count(o => o.HasPartiallyDefinedBattles);
                int tableLoadObjects = objects.Count(o => o.HasAnyTableLoad && o.LoadedValues.Count > 0);

                Debug.WriteLine($"Объектов из таблицы: {tableObjects}, AnyObjectSpec: {specObjects}, с частичными битвами: {partialBattleObjects}, с загрузкой из таблиц: {tableLoadObjects}");

                foreach (var obj in objects.OrderBy(o => o.Y).ThenBy(o => o.X))
                {
                    Debug.WriteLine($"  Объект X={obj.X}, Y={obj.Y}: FromTable={obj.IsFromTable}, Power={obj.MonsterPower}, Level={obj.MonsterLevel}, BattleMonsters={obj.BattleMonsters.Count}, PartialBattles={obj.PartiallyDefinedBattles.Count}, TableLoad={obj.HasAnyTableLoad}");
                }
            }

            return objects;
        }

        // Вспомогательный метод для слияния результатов анализа путей макроса
        private void MergeMacroResults(PathAnalysisResult source,
            HashSet<string> allTexts,
            ref byte? monsterPower,
            ref byte? monsterLevel,
            Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> battleMonsters,
            List<PartiallyDefinedBattle> partialBattles,
            List<PartialBattleInfo> partialBattleInfo,
            ref bool hasPartialBattlePattern)
        {
            if (source == null) return;

            foreach (var text in source.FoundTexts)
                allTexts.Add(text);
            foreach (var text in source.ContextTexts)
                allTexts.Add(text);

            if (source.MonsterPower.HasValue)
                monsterPower = source.MonsterPower;

            if (source.MonsterLevel.HasValue)
                monsterLevel = source.MonsterLevel;

            foreach (var entry in source.BattleMonsterEntries)
                battleMonsters[entry.Key] = entry.Value;

            foreach (var partial in source.PartialBattles)
            {
                if (!partialBattles.Any(p => p.BxIndex == partial.BxIndex))
                    partialBattles.Add(partial);
            }

            foreach (var info in source.PartialBattleInfo)
            {
                if (!partialBattleInfo.Any(i => i.BxIndex == info.BxIndex &&
                                                 i.DestAddr == info.DestAddr))
                    partialBattleInfo.Add(info);
            }

            hasPartialBattlePattern = hasPartialBattlePattern || source.HasPartialBattlePattern;
        }

        // ========== МЕТОДЫ ДЛЯ ОРИГИНАЛЬНОЙ ЛОГИКИ (TABLET OBJECTS) ==========

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

        // ОСНОВНОЙ МЕТОД ОБРАБОТКИ ОБЪЕКТА ИЗ ТАБЛИЦЫ
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

            bool debugMode = (ovrObject.X == 12 && ovrObject.Y == 2) ||
                             (ovrObject.X == 5 && ovrObject.Y == 8) ||
                             (ovrObject.X == 8 && ovrObject.Y == 5);

            if (debugMode)
            {
                Debug.WriteLine($"\n=== ОБРАБОТКА ОБЪЕКТА ({ovrObject.X},{ovrObject.Y}) ===");
                Debug.WriteLine($"PatchAddress: 0x{patchAddress:X4}");
            }

            // ========== 1. Анализируем основной путь ==========
            var mainRegisterTracker = new RegisterTracker();
            var mainPathResult = ExecuteCodeAtAddress(br, patchAddress, mainRegisterTracker,
                new HashSet<uint>(), 0, 0, debugMode ? ovrObject : null, 0);

            // Сохраняем результаты основного пути
            HashSet<string> mainLocalTexts = new HashSet<string>();
            HashSet<string> mainContextTexts = new HashSet<string>();

            if (mainPathResult.FoundTexts.Count > 0)
                mainLocalTexts = new HashSet<string>(mainPathResult.FoundTexts);
            if (mainPathResult.ContextTexts.Count > 0)
                mainContextTexts = new HashSet<string>(mainPathResult.ContextTexts);

            if (debugMode)
            {
                Debug.WriteLine($"Основной путь: локальных текстов: {mainLocalTexts.Count}, контекстных: {mainContextTexts.Count}");
                Debug.WriteLine($"FirstLocalTextAddress: 0x{mainPathResult.FirstLocalTextAddress:X4}");
                foreach (var text in mainLocalTexts)
                    Debug.WriteLine($"  Локальный: {text}");
                foreach (var text in mainContextTexts)
                    Debug.WriteLine($"  Контекстный: {text}");
            }

            if (mainPathResult.MonsterPower.HasValue)
                ovrObject.MonsterPower = mainPathResult.MonsterPower.Value;
            if (mainPathResult.MonsterLevel.HasValue)
                ovrObject.MonsterLevel = mainPathResult.MonsterLevel.Value;

            bool isMainPathIndeterminate = mainPathResult.IsIndeterminateLoop;
            if (mainPathResult.BattleMonsterEntries.Count > 0)
            {
                // Все записи обрабатываются одинаково, без разделения на одиночные и множественные
                foreach (var entry in mainPathResult.BattleMonsterEntries)
                {
                    if (entry.Value.val1 != 0 || entry.Value.val2 != 0)
                    {
                        ovrObject.AddBattleMonster(
                            entry.Key,
                            entry.Value.val1,
                            entry.Value.val2,
                            isMainPathIndeterminate || entry.Value.isIndeterminate  // Просто объединяем флаги
                        );
                    }
                }

                if (debugMode && mainPathResult.BattleMonsterEntries.Count > 0)
                {
                    Debug.WriteLine($"      Добавлено {mainPathResult.BattleMonsterEntries.Count} записей монстров в объект");
                }
            }

            foreach (var partial in mainPathResult.PartialBattles)
            {
                ovrObject.AddPartiallyDefinedBattle(
                    partial.BxIndex,
                    partial.RangeStart1,
                    partial.RangeEnd1,
                    partial.RangeStart2,
                    partial.RangeEnd2
                );
            }

            if (mainPathResult.HasPartialBattlePattern)
            {
                ovrObject.HasAnyTableLoad = true;
                foreach (var info in mainPathResult.PartialBattleInfo)
                {
                    ovrObject.AddLoadedValue(
                        info.BxIndex,
                        info.SrcReg,
                        info.SrcRegValue,
                        info.SourceTableAddr ?? 0,
                        info.IsFromTable,
                        false
                    );
                }
            }

            // ========== 2. Собираем все результаты ==========
            var allResults = new List<(int pathId, HashSet<string> texts, bool isLeaf)>();
            var processedTargets = new HashSet<uint>();

            // Добавляем основной путь, если в нём есть локальные тексты
            if (mainLocalTexts.Count > 0)
            {
                var mainCombinedTexts = new HashSet<string>();
                foreach (var text in mainLocalTexts)
                    mainCombinedTexts.Add(text);
                foreach (var text in mainContextTexts)
                    mainCombinedTexts.Add(text);

                bool isMainLeaf = mainPathResult.AlternativePaths.Count == 0;
                allResults.Add((1, mainCombinedTexts, isMainLeaf));

                if (debugMode)
                {
                    Debug.WriteLine($"Добавлен основной путь с {mainLocalTexts.Count} локальными текстами (листовой: {isMainLeaf})");
                }
            }

            // Рекурсивная функция для обработки альтернативных путей
            void ProcessPaths(List<AlternativePath> paths, int basePathId, int depth,
                              HashSet<string> inheritedContextTexts, HashSet<string> inheritedLocalTexts,
                              uint firstLocalTextAddress)
            {
                if (depth > 5) return;

                var sortedPaths = paths.OrderBy(p => p.Address).ToList();
                int localCounter = 1;

                foreach (var path in sortedPaths)
                {
                    if (processedTargets.Contains(path.TargetAddress))
                        continue;

                    processedTargets.Add(path.TargetAddress);
                    int currentPathId = basePathId * 10 + localCounter;
                    localCounter++;

                    if (debugMode)
                    {
                        Debug.WriteLine($"\n  Анализ пути {currentPathId} (глубина {depth}) -> 0x{path.TargetAddress:X4} ({path.Condition})");
                    }

                    // Создаём НОВЫЙ RegisterTracker для каждого пути
                    var pathRegisterTracker = new RegisterTracker();
                    var pathResult = ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                        new HashSet<uint>(), depth + 1, 0, debugMode ? ovrObject : null, currentPathId);

                    // Формируем тексты для этого пути
                    var pathTexts = new HashSet<string>();

                    // 1. Наследуем контекстные тексты от родителя (ВСЕГДА)
                    foreach (var text in inheritedContextTexts)
                    {
                        pathTexts.Add(text);
                    }

                    // 2. Наследуем локальные тексты от родителя, если:
                    //    - Это LINEAR путь (продолжение выполнения)
                    //    - ИЛИ локальный текст был загружен ДО точки ветвления (path.Address > firstLocalTextAddress)
                    bool shouldInheritLocal = path.Condition.StartsWith("LINEAR") ||
                                              (firstLocalTextAddress != uint.MaxValue &&
                                               path.Address > firstLocalTextAddress);

                    if (shouldInheritLocal)
                    {
                        foreach (var text in inheritedLocalTexts)
                        {
                            pathTexts.Add(text);
                        }
                    }

                    // 3. Добавляем новые контекстные тексты из этого пути
                    foreach (var text in pathResult.ContextTexts)
                    {
                        pathTexts.Add(text);
                    }

                    // 4. Добавляем новые локальные тексты из этого пути
                    foreach (var text in pathResult.FoundTexts)
                    {
                        pathTexts.Add(text);
                    }

                    // Путь считается листовым, только если у него нет вложенных путей
                    bool isLeaf = pathResult.AlternativePaths.Count == 0;

                    // ВСЕГДА добавляем путь в результаты
                    allResults.Add((currentPathId, pathTexts, isLeaf));

                    if (debugMode && pathTexts.Count > 0)
                    {
                        Debug.WriteLine($"      Найдено текстов в пути {currentPathId}: {pathTexts.Count} (листовой: {isLeaf})");
                        foreach (var text in pathTexts)
                        {
                            Debug.WriteLine($"        {text}");
                        }
                    }

                    // Добавляем информацию о монстрах
                    if (pathResult.MonsterPower.HasValue && !ovrObject.MonsterPower.HasValue)
                        ovrObject.MonsterPower = pathResult.MonsterPower.Value;

                    if (pathResult.MonsterLevel.HasValue && !ovrObject.MonsterLevel.HasValue)
                        ovrObject.MonsterLevel = pathResult.MonsterLevel.Value;

                    foreach (var entry in pathResult.BattleMonsterEntries)
                    {
                        if (!ovrObject.BattleMonsters.Any(m => m.Index == entry.Key))
                        {
                            ovrObject.AddBattleMonster(
                                entry.Key,
                                entry.Value.val1,
                                entry.Value.val2,
                                entry.Value.isIndeterminate
                            );
                        }
                    }

                    foreach (var partial in pathResult.PartialBattles)
                    {
                        if (!ovrObject.PartiallyDefinedBattles.Any(p => p.BxIndex == partial.BxIndex))
                        {
                            ovrObject.AddPartiallyDefinedBattle(
                                partial.BxIndex,
                                partial.RangeStart1,
                                partial.RangeEnd1,
                                partial.RangeStart2,
                                partial.RangeEnd2
                            );
                        }
                    }

                    if (pathResult.HasPartialBattlePattern)
                    {
                        ovrObject.HasAnyTableLoad = true;
                        foreach (var info in pathResult.PartialBattleInfo)
                        {
                            if (!ovrObject.LoadedValues.Any(v => v.BxIndex == info.BxIndex &&
                                                                  v.RegName == info.SrcReg &&
                                                                  v.SourceAddr == (info.SourceTableAddr ?? 0)))
                            {
                                ovrObject.AddLoadedValue(
                                    info.BxIndex,
                                    info.SrcReg,
                                    info.SrcRegValue,
                                    info.SourceTableAddr ?? 0,
                                    info.IsFromTable,
                                    false
                                );
                            }
                        }
                    }

                    // Формируем набор текстов для наследования вложенными путями
                    var newInheritedContextTexts = new HashSet<string>(inheritedContextTexts);
                    foreach (var text in pathResult.ContextTexts)
                    {
                        newInheritedContextTexts.Add(text);
                    }

                    var newInheritedLocalTexts = new HashSet<string>();

                    // Для вложенных путей наследуем локальные тексты по тому же правилу
                    bool shouldPassLocalToChildren = path.Condition.StartsWith("LINEAR") ||
                                                    (firstLocalTextAddress != uint.MaxValue &&
                                                     path.Address > firstLocalTextAddress);

                    if (shouldPassLocalToChildren)
                    {
                        foreach (var text in inheritedLocalTexts)
                        {
                            newInheritedLocalTexts.Add(text);
                        }
                    }

                    // Всегда добавляем новые локальные тексты из этого пути
                    foreach (var text in pathResult.FoundTexts)
                    {
                        newInheritedLocalTexts.Add(text);
                    }

                    // Рекурсивно обрабатываем вложенные пути
                    if (pathResult.AlternativePaths.Count > 0)
                    {
                        if (debugMode)
                        {
                            Debug.WriteLine($"      Найдено {pathResult.AlternativePaths.Count} вложенных путей");
                        }
                        ProcessPaths(pathResult.AlternativePaths, currentPathId, depth + 1,
                                    newInheritedContextTexts, newInheritedLocalTexts,
                                    firstLocalTextAddress);
                    }
                }
            }

            // Запускаем обработку альтернативных путей
            if (mainPathResult.AlternativePaths.Count > 0)
            {
                if (debugMode)
                {
                    Debug.WriteLine($"\nСобрано путей из основного анализа: {mainPathResult.AlternativePaths.Count}");
                    foreach (var path in mainPathResult.AlternativePaths)
                    {
                        Debug.WriteLine($"  Путь: 0x{path.Address:X4} -> 0x{path.TargetAddress:X4} ({path.Condition})");
                    }
                }

                ProcessPaths(mainPathResult.AlternativePaths, 1, 0,
                            mainContextTexts, mainLocalTexts,
                            mainPathResult.FirstLocalTextAddress);
            }

            // ========== 3. Формируем финальный словарь путей ==========
            var finalPaths = new Dictionary<int, HashSet<string>>();

            // Оставляем только листовые пути (не имеющие вложенных путей)
            var leafResults = allResults
                .Where(r => r.isLeaf && r.texts.Count > 0)
                .OrderBy(r => r.pathId)
                .ToList();

            // Группируем по уникальному содержимому текста
            var uniqueTextGroups = new Dictionary<string, (int pathId, HashSet<string> texts)>();

            foreach (var result in leafResults)
            {
                // Создаём ключ из отсортированных текстов для сравнения
                var sortedTexts = result.texts.OrderBy(t => t).ToList();
                string key = string.Join("|", sortedTexts);

                // Если такой набор текстов ещё не встречался, добавляем его
                if (!uniqueTextGroups.ContainsKey(key))
                {
                    uniqueTextGroups[key] = (result.pathId, result.texts);
                }
            }

            // Преобразуем в список и сортируем по pathId
            var uniqueResults = uniqueTextGroups.Values
                .OrderBy(r => r.pathId)
                .ToList();

            if (uniqueResults.Count > 0)
            {
                // Первый путь всегда сохраняем с ключом 0
                finalPaths[0] = uniqueResults[0].texts;

                // Остальные пути сохраняем с ключами 1,2,3...
                for (int i = 1; i < uniqueResults.Count; i++)
                {
                    finalPaths[i] = uniqueResults[i].texts;
                }
            }

            ovrObject.PathTexts = finalPaths;

            if (debugMode)
            {
                Debug.WriteLine($"\n    ИТОГО для клетки ({ovrObject.X},{ovrObject.Y}):");
                Debug.WriteLine($"      Всего путей: {ovrObject.PathTexts.Count}");
                foreach (var kvp in ovrObject.PathTexts.OrderBy(k => k.Key))
                {
                    Debug.WriteLine($"      Path {kvp.Key}: {kvp.Value.Count} текстов");
                    foreach (var text in kvp.Value)
                    {
                        Debug.WriteLine($"        {text}");
                    }
                }
            }

            return ovrObject;
        }

        // ========== НОВЫЙ ОСНОВНОЙ МЕТОД ВЫПОЛНЕНИЯ КОДА ==========

        private PathAnalysisResult ExecuteCodeAtAddress(BinaryReader br, uint startAddress,
            RegisterTracker registerTracker, HashSet<uint> globallyAnalyzedAddresses,
            int depth, int callDepth, OvrObject debugObject, int pathId = 0)
        {
            const int MAX_DEPTH = 10;
            const int MAX_CALL_DEPTH = 5;
            const int MAX_INSTRUCTIONS_PER_PATH = 1000;

            if (depth > MAX_DEPTH || callDepth > MAX_CALL_DEPTH)
            {
                Debug.WriteLine($"      Достигнут лимит глубины ({depth}/{callDepth}), прекращаем анализ");
                return new PathAnalysisResult { IsTerminated = false };
            }

            var result = new PathAnalysisResult();
            long fileLength = br.BaseStream.Length;
            uint currentAddress = startAddress;
            int instructionCount = 0;
            var visitedInThisPath = new HashSet<uint>();
            bool debugMode = debugObject != null || (pathId > 0 && debugObject?.X == 5 && debugObject?.Y == 8);

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
                            Debug.WriteLine($"      Обнаружен цикл по адресу 0x{currentAddress:X4}, прекращаем анализ этого пути.");
                        result.IsTerminated = false;
                        break;
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

                    // Отслеживаем значения регистров для косвенных текстов
                    byte[] bytes = insn.Bytes;

                    // MOV AL, imm8
                    if (bytes.Length >= 2 && bytes[0] == 0xB0)
                    {
                        lastAlValue = bytes[1];
                        if (debugMode)
                            Debug.WriteLine($"        Запомнили AL = 0x{lastAlValue:X2}");
                    }

                    // MOV BP, imm16
                    if (bytes.Length >= 3 && bytes[0] == 0xBD)
                    {
                        lastBpValue = BitConverter.ToUInt16(bytes, 1);
                        if (debugMode)
                            Debug.WriteLine($"        Запомнили BP = 0x{lastBpValue:X4}");
                    }

                    // Если есть и AL, и BP, вычисляем потенциальный адрес текста
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

                                    // Текст из текущего пути - локальный
                                    result.FoundTexts.Add(textEntry);

                                    // ЗАПОМИНАЕМ АДРЕС ПЕРВОГО ЛОКАЛЬНОГО ТЕКСТА
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

                    // Стандартный поиск текстов
                    var newTexts = new HashSet<string>();
                    FindTextsInInstruction(insn, br, registerTracker, depth, newTexts);
                    foreach (var text in newTexts)
                    {
                        if (!foundTextsInThisPath.Contains(text))
                        {
                            foundTextsInThisPath.Add(text);

                            if (callDepth > 0)
                            {
                                // Текст из подпрограммы - контекстный
                                result.ContextTexts.Add(text);
                                if (debugMode)
                                    Debug.WriteLine($"        Найден контекстный текст (из CALL): {text}");
                            }
                            else
                            {
                                // Текст из текущего пути - локальный
                                result.FoundTexts.Add(text);

                                // ЗАПОМИНАЕМ АДРЕС ПЕРВОГО ЛОКАЛЬНОГО ТЕКСТА
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

                    FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                    FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                    TrackRegisterOperations(insn, br, registerTracker, depth);

                    // Анализируем частичные битвы после сбора информации
                    if (result.PartialBattleInfo.Count > 0)
                    {
                        AnalyzePartialBattleRanges(br, result);
                    }

                    // ========== ОБРАБОТКА ИНСТРУКЦИЙ ==========

                    // RET - конец пути
                    if (IsReturnInstruction(mnemonicUpper))
                    {
                        if (debugMode)
                            Debug.WriteLine($"      RET на 0x{insn.Address:X4} - конец пути");
                        result.IsTerminated = true;
                        result.HasSignificantCode = result.FoundTexts.Count > 0 ||
                                                     result.ContextTexts.Count > 0 ||
                                                     result.MonsterPower.HasValue ||
                                                     result.MonsterLevel.HasValue ||
                                                     result.BattleMonsterEntries.Count > 0 ||
                                                     result.PartialBattles.Count > 0 ||
                                                     result.HasPartialBattlePattern;
                        return result;
                    }

                    // JMP - безусловный переход
                    if (mnemonicUpper == "JMP" || mnemonicUpper == "JMPF" || mnemonicUpper == "JMPL" ||
                        mnemonicUpper == "JMPE" || mnemonicUpper == "JMPI")
                    {
                        uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                        if (jumpTarget >= fileLength)
                        {
                            if (debugMode)
                                Debug.WriteLine($"      JMP за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                            result.IsTerminated = true;
                            result.HasSignificantCode = true;
                            return result;
                        }

                        if (jumpTarget != 0 && jumpTarget < fileLength)
                        {
                            if (debugMode)
                                Debug.WriteLine($"      JMP к 0x{jumpTarget:X4}");
                            currentAddress = jumpTarget;
                            continue;
                        }
                    }

                    // Ручное распознавание JMP по опкоду 0xE9
                    if (insn.Bytes.Length >= 1 && insn.Bytes[0] == 0xE9)
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
                                return result;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                if (debugMode)
                                    Debug.WriteLine($"      JMP (по опкоду 0xE9) к 0x{jumpTarget:X4}");
                                currentAddress = jumpTarget;
                                continue;
                            }
                        }
                    }

                    // CALL - вызов подпрограммы
                    if (mnemonicUpper.StartsWith("CALL"))
                    {
                        uint callTarget = GetInstructionTargetAddress(insn, fileLength);

                        if (callTarget < fileLength && callTarget != 0)
                        {
                            if (debugMode)
                                Debug.WriteLine($"      CALL 0x{callTarget:X4} (возврат в 0x{nextAddress:X4})");

                            // Создаём копию трекера для подпрограммы
                            var subroutineTracker = registerTracker.Clone();

                            // Анализируем подпрограмму
                            var subroutineResult = ExecuteCodeAtAddress(br, callTarget, subroutineTracker,
                                globallyAnalyzedAddresses, depth + 1, callDepth + 1, debugObject, pathId);

                            // Добавляем найденные тексты из подпрограммы
                            // Тексты из подпрограммы становятся контекстными для текущего пути
                            foreach (var text in subroutineResult.FoundTexts)
                            {
                                if (!foundTextsInThisPath.Contains(text))
                                {
                                    foundTextsInThisPath.Add(text);
                                    result.ContextTexts.Add(text);
                                }
                            }
                            foreach (var text in subroutineResult.ContextTexts)
                            {
                                if (!foundTextsInThisPath.Contains(text))
                                {
                                    foundTextsInThisPath.Add(text);
                                    result.ContextTexts.Add(text);
                                }
                            }

                            // Добавляем информацию о монстрах из подпрограммы
                            if (subroutineResult.MonsterPower.HasValue && !result.MonsterPower.HasValue)
                                result.MonsterPower = subroutineResult.MonsterPower;

                            if (subroutineResult.MonsterLevel.HasValue && !result.MonsterLevel.HasValue)
                                result.MonsterLevel = subroutineResult.MonsterLevel;

                            foreach (var entry in subroutineResult.BattleMonsterEntries)
                            {
                                if (!result.BattleMonsterEntries.ContainsKey(entry.Key))
                                    result.BattleMonsterEntries[entry.Key] = entry.Value;
                            }

                            // Добавляем частичные битвы
                            foreach (var partial in subroutineResult.PartialBattles)
                            {
                                if (!result.PartialBattles.Any(p => p.BxIndex == partial.BxIndex))
                                    result.PartialBattles.Add(partial);
                            }

                            foreach (var info in subroutineResult.PartialBattleInfo)
                            {
                                if (!result.PartialBattleInfo.Any(i => i.BxIndex == info.BxIndex &&
                                                                        i.DestAddr == info.DestAddr))
                                    result.PartialBattleInfo.Add(info);
                            }

                            result.HasPartialBattlePattern = result.HasPartialBattlePattern || subroutineResult.HasPartialBattlePattern;
                        }
                        else
                        {
                            if (debugMode)
                                Debug.WriteLine($"      CALL за пределы оверлея (0x{callTarget:X4}) - игнорируем");
                        }

                        currentAddress = nextAddress;
                        continue;
                    }

                    // ========== ВАЖНО: Обработка условных переходов ==========
                    if (IsConditionalJump(insn, out uint condJumpTarget))
                    {
                        if (condJumpTarget < fileLength && condJumpTarget != 0)
                        {
                            // Добавляем целевой адрес перехода как альтернативный путь
                            var altPath = new AlternativePath
                            {
                                Address = (uint)insn.Address,
                                TargetAddress = condJumpTarget,
                                Condition = $"{insn.Mnemonic} {insn.Operand}",
                                Analyzed = false,
                                PathNumber = pathId * 10 + result.AlternativePaths.Count + 1
                            };

                            if (!result.AlternativePaths.Any(p => p.Address == altPath.Address &&
                                                                   p.TargetAddress == altPath.TargetAddress))
                            {
                                result.AlternativePaths.Add(altPath);
                                if (debugMode)
                                    Debug.WriteLine($"      Найден альтернативный путь {altPath.PathNumber}: 0x{insn.Address:X4} -> 0x{condJumpTarget:X4}");
                            }

                            // Добавляем линейное продолжение как отдельный путь
                            var linearPath = new AlternativePath
                            {
                                Address = (uint)insn.Address,
                                TargetAddress = nextAddress,
                                Condition = $"LINEAR after {insn.Mnemonic}",
                                Analyzed = false,
                                PathNumber = pathId * 10 + result.AlternativePaths.Count + 1
                            };

                            if (!result.AlternativePaths.Any(p => p.Address == linearPath.Address &&
                                                                   p.TargetAddress == linearPath.TargetAddress))
                            {
                                result.AlternativePaths.Add(linearPath);
                                if (debugMode)
                                    Debug.WriteLine($"      Добавлен линейный путь: 0x{insn.Address:X4} -> 0x{nextAddress:X4}");
                            }
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
                        return result;
                    }

                    // Обычная инструкция - продолжаем линейно
                    currentAddress = nextAddress;
                }
            }

            // Финальный анализ частичных битв после завершения пути
            if (result.PartialBattleInfo.Count > 0)
            {
                AnalyzePartialBattleRanges(br, result);
            }

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

            return result;
        }

        // ========== ОСТАЛЬНЫЕ МЕТОДЫ ==========

        // метод для анализа диапазонов CDA9-CDB0 и CDB1-CDB8
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
                        int maxSaveBx = groupedByBx.Max(g => g.Key);
                        if (maxSaveBx > 0)
                        {
                            iterationCount = maxSaveBx + 1;
                            Debug.WriteLine($"        Количество итераций определено по BX сохранения: {iterationCount}");
                        }
                        else
                        {
                            iterationCount = 8;
                            Debug.WriteLine($"        Количество итераций неизвестно, предполагаем {iterationCount}");
                        }
                    }
                }

                // СОЗДАЁМ ЧАСТИЧНЫЕ БИТВЫ ДЛЯ КАЖДОЙ ИТЕРАЦИИ
                for (int iteration = 0; iteration < iterationCount; iteration++)
                {
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

                    var partialBattle = new PartiallyDefinedBattle
                    {
                        BxIndex = saveBxIndex + iteration,
                        RangeStart1 = rangeStart1,
                        RangeEnd1 = rangeEnd1,
                        RangeStart2 = rangeStart2,
                        RangeEnd2 = rangeEnd2
                    };

                    result.PartialBattles.Add(partialBattle);

                    int combinations = (rangeEnd1 - rangeStart1 + 1) * (rangeEnd2 - rangeStart2 + 1);
                    Debug.WriteLine($"        -> СОЗДАНА ЧАСТИЧНАЯ БИТВА: BX(сохранения)={saveBxIndex + iteration}, BX(загрузки)={loadBx}, {combinations} комбинация (итерация {iteration})");
                }
            }
        }

        private List<X86Instruction> DisassembleAll(BinaryReader br)
        {
            var instructions = new List<X86Instruction>();

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                long fileLength = br.BaseStream.Length;
                uint currentAddress = 0;

                while (currentAddress < fileLength)
                {
                    var chunk = ReadCodeBlock(br, currentAddress, out var disassembled);
                    if (disassembled == null || disassembled.Length == 0)
                    {
                        currentAddress++;
                        continue;
                    }

                    instructions.AddRange(disassembled);
                    currentAddress = (uint)(disassembled.Last().Address + disassembled.Last().Bytes.Length);
                }
            }

            Debug.WriteLine($"Дизассемблировано инструкций: {instructions.Count}");
            return instructions;
        }

        private List<CoordinateComparison> FindAllCoordinateComparisons(BinaryReader br, List<X86Instruction> allInstructions)
        {
            var comparisons = new List<CoordinateComparison>();

            for (int i = 0; i < allInstructions.Count; i++)
            {
                var insn = allInstructions[i];

                if (IsCompareWithImmediate(insn, out byte immValue, out string reg))
                {
                    ushort? sourceAddr = FindMemorySourceForRegister(allInstructions, i, reg);
                    if (!sourceAddr.HasValue) continue;

                    CoordType coordType = CoordType.Unknown;
                    if (sourceAddr.Value == 0x3C38)
                        coordType = CoordType.XCoord;
                    else if (sourceAddr.Value == 0x3C39)
                        coordType = CoordType.YCoord;
                    else if (sourceAddr.Value == 0x3C3A)
                        coordType = CoordType.FullCoord;
                    else
                        continue;

                    int j = i + 1;
                    byte? lastCompareValue = null;
                    uint? lastJumpTarget = null;
                    string lastJumpType = null;

                    bool hasPrecedingCondition = false;
                    byte precedingValue = 0;
                    string precedingJumpType = null;

                    if (i > 0)
                    {
                        for (int k = i - 1; k >= Math.Max(0, i - 10); k--)
                        {
                            var prevInsn = allInstructions[k];
                            if (IsCompareWithImmediate(prevInsn, out byte prevValue, out string prevReg) && prevReg == reg)
                            {
                                hasPrecedingCondition = true;
                                precedingValue = prevValue;
                                if (k + 1 < allInstructions.Count)
                                {
                                    var nextAfterPrev = allInstructions[k + 1];
                                    if (IsConditionalJump(nextAfterPrev, out uint _))
                                    {
                                        precedingJumpType = nextAfterPrev.Mnemonic.ToUpper();
                                    }
                                }
                                break;
                            }
                        }
                    }

                    while (j < allInstructions.Count)
                    {
                        var nextInsn = allInstructions[j];
                        if (!IsConditionalJump(nextInsn, out uint jumpTarget))
                            break;

                        comparisons.Add(new CoordinateComparison
                        {
                            CompareAddress = (uint)insn.Address,
                            MemAddr = sourceAddr.Value,
                            CoordType = coordType,
                            Value = immValue,
                            JumpTarget = jumpTarget,
                            JumpType = nextInsn.Mnemonic.ToUpper(),
                            IsLinear = false
                        });

                        Debug.WriteLine($"  Найдено сравнение: 0x{insn.Address:X4} [{sourceAddr.Value:X4}]={immValue} -> 0x{jumpTarget:X4} ({nextInsn.Mnemonic}) [{coordType}]");

                        lastCompareValue = immValue;
                        lastJumpTarget = jumpTarget;
                        lastJumpType = nextInsn.Mnemonic.ToUpper();
                        j++;
                    }

                    if (j < allInstructions.Count && lastCompareValue.HasValue)
                    {
                        var nextInsn = allInstructions[j];
                        if (IsCompareWithImmediate(nextInsn, out _, out _))
                        {
                            Debug.WriteLine($"  Пропуск линейного выполнения: следующая инструкция - сравнение по адресу 0x{nextInsn.Address:X4}");
                            continue;
                        }

                        byte linearStart = 0;
                        byte linearEnd = 255;

                        if (lastJumpType == "JB" || lastJumpType == "JC")
                        {
                            linearStart = lastCompareValue.Value;
                            linearEnd = 255;
                        }
                        else if (lastJumpType == "JAE" || lastJumpType == "JNB" || lastJumpType == "JNC")
                        {
                            linearStart = 0;
                            linearEnd = (byte)(lastCompareValue.Value - 1);
                        }

                        if (hasPrecedingCondition)
                        {
                            if (precedingJumpType == "JB" || precedingJumpType == "JC")
                            {
                                linearStart = Math.Max(linearStart, precedingValue);
                            }
                            else if (precedingJumpType == "JAE" || precedingJumpType == "JNB" || precedingJumpType == "JNC")
                            {
                                linearEnd = Math.Min(linearEnd, (byte)(precedingValue - 1));
                            }
                            else if (precedingJumpType == "JE" || precedingJumpType == "JZ")
                            {
                                if (precedingValue == 3 && lastCompareValue == 9)
                                {
                                    linearStart = 4;
                                    linearEnd = 8;
                                }
                            }
                        }

                        if (linearStart <= linearEnd)
                        {
                            comparisons.Add(new CoordinateComparison
                            {
                                CompareAddress = (uint)insn.Address,
                                MemAddr = sourceAddr.Value,
                                CoordType = coordType,
                                Value = lastCompareValue.Value,
                                JumpTarget = (uint)allInstructions[j].Address,
                                JumpType = "LINEAR",
                                IsLinear = true,
                                LinearStart = linearStart,
                                LinearEnd = linearEnd
                            });

                            Debug.WriteLine($"  Найдено линейное выполнение: 0x{insn.Address:X4} [{sourceAddr.Value:X4}]={lastCompareValue} -> 0x{allInstructions[j].Address:X4} для значений [{linearStart}-{linearEnd}] [{coordType}]");
                        }
                    }
                }
            }

            return comparisons;
        }

        private ushort? FindMemorySourceForRegister(List<X86Instruction> allInstructions, int currentIndex, string reg)
        {
            int start = Math.Max(0, currentIndex - 20);

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
                default: return false;
            }
        }

        private void SaveNestedPathsRecursively(Dictionary<int, HashSet<string>> allPathTexts,
            Dictionary<int, PathAnalysisResult> nestedPaths, ref int nextPathNumber)
        {
            foreach (var kvp in nestedPaths.OrderBy(x => x.Key))
            {
                if (kvp.Value.FoundTexts.Count > 0 && !allPathTexts.ContainsKey(nextPathNumber))
                {
                    allPathTexts[nextPathNumber] = kvp.Value.FoundTexts;

                    if (kvp.Value.NestedPaths.Count > 0)
                    {
                        int nestedNumber = nextPathNumber * 10;
                        SaveNestedPathsRecursively(allPathTexts, kvp.Value.NestedPaths, ref nestedNumber);
                    }

                    nextPathNumber++;
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
                    foreach (var existingSet in processedTextSets)
                    {
                        if (kvp.Value.Count < existingSet.Count && existingSet.IsSupersetOf(kvp.Value))
                        {
                            isUnique = false;
                            break;
                        }
                        if (kvp.Value.Count > existingSet.Count && kvp.Value.IsSupersetOf(existingSet))
                        {
                            var keyToRemove = uniquePaths.FirstOrDefault(x => x.Value == existingSet).Key;
                            if (keyToRemove != 0)
                            {
                                uniquePaths.Remove(keyToRemove);
                                processedTextSets.Remove(existingSet);
                            }
                        }
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

                    var chunk = ReadCodeBlock(br, currentAddress, out var instructions);
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
                            uint jumpTarget = GetJumpTarget(insn);
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
                        else if (IsReturnInstruction(mnemonicUpper))
                        {
                            return path;
                        }
                        else if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetJumpTarget(insn);
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

                    var chunk = ReadCodeBlock(br, currentAddress, out var instructions);
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
                            uint jumpTarget = GetJumpTarget(insn);
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

                        if (IsReturnInstruction(mnemonicUpper)) return;

                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetJumpTarget(insn);
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

        // ========== МЕТОДЫ ПОИСКА ИНФОРМАЦИИ В ИНСТРУКЦИЯХ ==========

        private void FindTextsInInstruction(X86Instruction insn, BinaryReader br, RegisterTracker registerTracker,
            int depth, HashSet<string> output)
        {
            byte[] instructionBytes = insn.Bytes;
            uint address = (uint)insn.Address;

            bool isTextFound = false;
            string textFound = null;
            string sourceType = null;

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
                    isTextFound = true;
                    textFound = text;
                    sourceType = "C7 06 D4 3B";
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
                        isTextFound = true;
                        textFound = text;
                        sourceType = $"89 06 via {regNames[regField]}";
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
                    isTextFound = true;
                    textFound = text;
                    sourceType = "MOV reg, imm16";
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
                    isTextFound = true;
                    textFound = text;
                    sourceType = "MOV BP, imm16";
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

            // Обнаружение неопределенного цикла (инструкция CMP BL, [3C1D])
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
            {
                result.IsIndeterminateLoop = true;
                result.IsInLoop = true;
                result.LoopStartAddress = address;

                // Определяем, есть ли в результате записи с BX > 0
                bool hasBxGreaterThanZero = result.BattleMonsterEntries.Any(kvp => kvp.Key > 0);

                int markedCount = 0;
                foreach (var key in result.BattleMonsterEntries.Keys.ToList())
                {
                    // Если есть записи с BX > 0, значит BX меняется в цикле,
                    // и только они неопределены. Если нет записей с BX > 0,
                    // значит цикл выполняется с одним и тем же BX, и даже BX=0 неопределен.
                    bool shouldMark = hasBxGreaterThanZero ? key > 0 : true;

                    if (shouldMark)
                    {
                        var entry = result.BattleMonsterEntries[key];
                        result.BattleMonsterEntries[key] = (entry.val1, entry.val2, true);
                        markedCount++;
                    }
                }

                Debug.WriteLine($"    ОБНАРУЖЕН НЕОПРЕДЕЛЁННЫЙ ЦИКЛ по адресу 0x{address:X4}, помечено {markedCount} записей как неопределенные");
                return;
            }

            // Загрузка из таблицы CDA9+ (MOV AL, [BX+CDA9])
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x8A &&
                instructionBytes[1] == 0x87 &&
                instructionBytes[2] == 0xA9 && instructionBytes[3] == 0xCD)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
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
                            Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла (длина файла: 0x{br.BaseStream.Length:X})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                    }

                    registerTracker.SetRegisterValueWithSource(
                        "AL",
                        readSuccess ? actualValue : (byte)0,
                        sourceAddr,
                        bxValue,
                        true,
                        address,
                        $"MOV AL, [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X2") : "0")})"
                    );

                    Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDA9+: AL = [BX+CDA9] (BX={bxValue}, addr={sourceAddr:X4})");
                    result.HasPartialBattlePattern = true;
                }
            }
            // Загрузка из таблицы CDB1+ (MOV BP, [BX+CDB1])
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xAF &&
                     instructionBytes[2] == 0xB1 && instructionBytes[3] == 0xCD)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
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

                            Debug.WriteLine($"    ФАКТИЧЕСКОЕ ЗНАЧЕНИЕ ИЗ [{sourceAddr:X4}] (offset 0x{fileOffset:X}): 0x{actualValue:X4} (младший байт: 0x{lowByte:X2}, старший: 0x{highByte:X2})");
                        }
                        else
                        {
                            Debug.WriteLine($"    НЕВОЗМОЖНО ПРОЧИТАТЬ [{sourceAddr:X4}]: offset 0x{fileOffset:X} вне файла (длина файла: 0x{br.BaseStream.Length:X})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"    ОШИБКА ЧТЕНИЯ ИЗ [{sourceAddr:X4}]: {ex.Message}");
                    }

                    registerTracker.SetRegisterValueWithSource(
                        "BP",
                        readSuccess ? actualValue : (ushort)0,
                        sourceAddr,
                        bxValue,
                        true,
                        address,
                        $"MOV BP, [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4}, val={(readSuccess ? actualValue.ToString("X4") : "0")})"
                    );

                    Debug.WriteLine($"    ЗАГРУЗКА ИЗ ТАБЛИЦЫ CDB1+: BP = [BX+CDB1] (BX={bxValue}, addr={sourceAddr:X4})");
                    result.HasPartialBattlePattern = true;
                }
            }
            // Копирование BP в CX (MOV CX, BP)
            else if (instructionBytes.Length >= 2 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xCD)
            {
                if (registerTracker.IsFromTable("BP"))
                {
                    ushort? sourceAddr = registerTracker.GetSourceAddress("BP");
                    ushort? originalBx = registerTracker.GetOriginalBx("BP");
                    ushort bpValue = 0;
                    registerTracker.TryGetRegisterValue("BP", out bpValue);

                    registerTracker.SetRegisterValueWithSource(
                        "CX",
                        bpValue,
                        sourceAddr ?? 0,
                        originalBx ?? 0,
                        true,
                        address,
                        "MOV CX, BP (копирование из таблицы)"
                    );

                    registerTracker.TrackPartialRegisterOperation("CX", "CL", (byte)bpValue, address, "MOV CL, low byte of BP");

                    Debug.WriteLine($"    КОПИРОВАНИЕ ИЗ ТАБЛИЦЫ: CX = BP (source={sourceAddr:X4}, originalBX={originalBx}, val={bpValue:X4})");
                }
            }

            // Сохранение в [BX+3C58] из AL
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
                        // Флаг неопределенности будет установлен позже при обнаружении цикла
                        // Пока всегда false
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("AL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("AL");
                            ushort? originalBx = registerTracker.GetOriginalBx("AL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C58,
                                SrcReg = "AL",
                                SrcRegValue = val1,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C58] = AL (BX={bxValue}, originalBX={originalBx}, val={val1:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = AL (BX={bxValue}, val1={val1:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Сохранение в [BX+3C29] из CL
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
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("CL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("CL");
                            ushort? originalBx = registerTracker.GetOriginalBx("CL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C29,
                                SrcReg = "CL",
                                SrcRegValue = val2,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C29] = CL (BX={bxValue}, originalBX={originalBx}, val={val2:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = CL (BX={bxValue}, val2={val2:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Сохранение в [BX+3C58] из DL
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
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("DL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("DL");
                            ushort? originalBx = registerTracker.GetOriginalBx("DL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C58,
                                SrcReg = "DL",
                                SrcRegValue = val1,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C58] = DL (BX={bxValue}, originalBX={originalBx}, val={val1:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = DL (BX={bxValue}, val1={val1:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Сохранение в [BX+3C29] из DL
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
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("DL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("DL");
                            ushort? originalBx = registerTracker.GetOriginalBx("DL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C29,
                                SrcReg = "DL",
                                SrcRegValue = val2,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C29] = DL (BX={bxValue}, originalBX={originalBx}, val={val2:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = DL (BX={bxValue}, val2={val2:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Сохранение в [BX+3C58] из BL
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
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("BL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("BL");
                            ushort? originalBx = registerTracker.GetOriginalBx("BL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C58,
                                SrcReg = "BL",
                                SrcRegValue = val1,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C58] = BL (BX={bxValue}, originalBX={originalBx}, val={val1:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = BL (BX={bxValue}, val1={val1:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Сохранение в [BX+3C29] из BL
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
                        bool isIndeterminate = false;

                        if (registerTracker.IsFromTable("BL"))
                        {
                            ushort? sourceAddr = registerTracker.GetSourceAddress("BL");
                            ushort? originalBx = registerTracker.GetOriginalBx("BL");

                            result.HasPartialBattlePattern = true;
                            result.PartialBattleInfo.Add(new PartialBattleInfo
                            {
                                BxIndex = originalBx ?? bxValue,
                                DestAddr = 0x3C29,
                                SrcReg = "BL",
                                SrcRegValue = val2,
                                IsFromTable = true,
                                SourceTableAddr = sourceAddr
                            });

                            Debug.WriteLine($"    СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [BX+3C29] = BL (BX={bxValue}, originalBX={originalBx}, val={val2:X2})");
                        }
                        else
                        {
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = BL (BX={bxValue}, val2={val2:X2}, isIndeterminate={isIndeterminate})");
                        }
                    }
                }
            }
            // Прямое сохранение в [3C58] из AL (без BX)
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x58 && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                {
                    int bxValue = 0;
                    bool isIndeterminate = false;

                    if (registerTracker.IsFromTable("AL"))
                    {
                        ushort? sourceAddr = registerTracker.GetSourceAddress("AL");
                        ushort? originalBx = registerTracker.GetOriginalBx("AL");

                        result.HasPartialBattlePattern = true;
                        result.PartialBattleInfo.Add(new PartialBattleInfo
                        {
                            BxIndex = originalBx ?? bxValue,
                            DestAddr = 0x3C58,
                            SrcReg = "AL",
                            SrcRegValue = (byte)alValue,
                            IsFromTable = true,
                            SourceTableAddr = sourceAddr
                        });

                        Debug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [3C58] = AL (originalBX={originalBx}, val={alValue:X2})");
                    }
                    else
                    {
                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = ((byte)alValue, existing.val2, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = ((byte)alValue, 0, isIndeterminate);
                        }

                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C58] = AL (val1={alValue:X2}, isIndeterminate={isIndeterminate})");
                    }
                }
            }
            // Прямое сохранение в [3C29] из AL (без BX)
            else if (instructionBytes.Length >= 3 &&
                     instructionBytes[0] == 0xA2 &&
                     instructionBytes[1] == 0x29 && instructionBytes[2] == 0x3C)
            {
                if (registerTracker.TryGetRegisterValue("AL", out ushort alValue))
                {
                    int bxValue = 0;
                    bool isIndeterminate = false;

                    if (registerTracker.IsFromTable("AL"))
                    {
                        ushort? sourceAddr = registerTracker.GetSourceAddress("AL");
                        ushort? originalBx = registerTracker.GetOriginalBx("AL");

                        result.HasPartialBattlePattern = true;
                        result.PartialBattleInfo.Add(new PartialBattleInfo
                        {
                            BxIndex = originalBx ?? bxValue,
                            DestAddr = 0x3C29,
                            SrcReg = "AL",
                            SrcRegValue = (byte)alValue,
                            IsFromTable = true,
                            SourceTableAddr = sourceAddr
                        });

                        Debug.WriteLine($"    ПРЯМОЕ СОХРАНЕНИЕ ИЗ ТАБЛИЦЫ: [3C29] = AL (originalBX={originalBx}, val={alValue:X2})");
                    }
                    else
                    {
                        if (result.BattleMonsterEntries.ContainsKey(bxValue))
                        {
                            var existing = result.BattleMonsterEntries[bxValue];
                            result.BattleMonsterEntries[bxValue] = (existing.val1, (byte)alValue, isIndeterminate);
                        }
                        else
                        {
                            result.BattleMonsterEntries[bxValue] = (0, (byte)alValue, isIndeterminate);
                        }

                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C29] = AL (val2={alValue:X2}, isIndeterminate={isIndeterminate})");
                    }
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

                    if (address >= 0x0210 && address <= 0x02F0)
                    {
                        Debug.WriteLine($"      *** ЗАГРУЗКА В РЕГИСТР В ОБЩЕМ КОДЕ ***");
                        Debug.WriteLine($"        Адрес: 0x{address:X4}");
                        Debug.WriteLine($"        {regNames[regIndex]} = 0x{immediateValue:X4}");
                    }
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

                        if (address >= 0x0210 && address <= 0x02F0)
                        {
                            Debug.WriteLine($"      *** ЗАГРУЗКА В 8-БИТНЫЙ РЕГИСТР В ОБЩЕМ КОДЕ ***");
                            Debug.WriteLine($"        Адрес: 0x{address:X4}");
                            Debug.WriteLine($"        {regName} = 0x{immediateValue:X2}");

                            if (registerTracker.TryGetRegisterValue(fullReg, out ushort fullValue))
                            {
                                Debug.WriteLine($"        {fullReg} теперь = 0x{fullValue:X4} (старший: 0x{fullValue >> 8:X2}, младший: 0x{fullValue & 0xFF:X2})");
                            }
                        }
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

                if (address >= 0x0210 && address <= 0x02F0)
                {
                    Debug.WriteLine($"      *** ЗАГРУЗКА BX В ОБЩЕМ КОДЕ ***");
                    Debug.WriteLine($"        Адрес: 0x{address:X4}");
                    Debug.WriteLine($"        BX = 0x{immediateValue:X4} (BH=0x{immediateValue >> 8:X2}, BL=0x{immediateValue & 0xFF:X2})");
                }
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

                if (address >= 0x0210 && address <= 0x02F0)
                {
                    Debug.WriteLine($"      *** ЗАГРУЗКА BL В ОБЩЕМ КОДЕ ***");
                    Debug.WriteLine($"        Адрес: 0x{address:X4}");
                    Debug.WriteLine($"        BL = 0x{immediateValue:X2}");
                    if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                    {
                        Debug.WriteLine($"        BX теперь = 0x{bxValue:X4} (BH=0x{bxValue >> 8:X2}, BL=0x{bxValue & 0xFF:X2})");
                    }
                }
            }

            if (instructionBytes.Length >= 2 && instructionBytes[0] == 0xB7)
            {
                byte immediateValue = instructionBytes[1];
                registerTracker.TrackPartialRegisterOperation("BX", "BH", immediateValue,
                    address, $"MOV BH, 0x{immediateValue:X2}");

                if (address >= 0x0210 && address <= 0x02F0)
                {
                    Debug.WriteLine($"      *** ЗАГРУЗКА BH В ОБЩЕМ КОДЕ ***");
                    Debug.WriteLine($"        Адрес: 0x{address:X4}");
                    Debug.WriteLine($"        BH = 0x{immediateValue:X2}");
                    if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                    {
                        Debug.WriteLine($"        BX теперь = 0x{bxValue:X4} (BH=0x{bxValue >> 8:X2}, BL=0x{bxValue & 0xFF:X2})");
                    }
                }
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

                    if (address >= 0x0210 && address <= 0x02F0)
                    {
                        Debug.WriteLine($"      *** INC BL В ОБЩЕМ КОДЕ ***");
                        Debug.WriteLine($"        Адрес: 0x{address:X4}");
                        Debug.WriteLine($"        BL: 0x{blValue:X2} -> 0x{newValue:X2}");
                        if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                        {
                            Debug.WriteLine($"        BX теперь = 0x{bxValue:X4} (BH=0x{bxValue >> 8:X2}, BL=0x{bxValue & 0xFF:X2})");
                        }
                    }
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
                    if (address >= 0x0210 && address <= 0x02F0)
                    {
                        Debug.WriteLine($"      *** СРАВНЕНИЕ В ОБЩЕМ КОДЕ ***");
                        Debug.WriteLine($"        Адрес: 0x{address:X4}");
                        Debug.WriteLine($"        CMP BL, [0x{addr:X4}] (BL=0x{blValue:X2})");

                        long fileOffset = addr - _config.TextBaseAddr;
                        if (fileOffset >= 0 && fileOffset < br.BaseStream.Length)
                        {
                            long originalPos = br.BaseStream.Position;
                            br.BaseStream.Position = fileOffset;
                            byte memValue = br.ReadByte();
                            br.BaseStream.Position = originalPos;
                            Debug.WriteLine($"        [0x{addr:X4}] = 0x{memValue:X2}");
                        }
                    }
                }
            }

            if (instructionBytes.Length >= 3 &&
                instructionBytes[0] == 0x80 &&
                instructionBytes[1] == 0xFB)
            {
                byte cmpValue = instructionBytes[2];
                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                {
                    if (address >= 0x0210 && address <= 0x02F0)
                    {
                        Debug.WriteLine($"      *** СРАВНЕНИЕ В ОБЩЕМ КОДЕ ***");
                        Debug.WriteLine($"        Адрес: 0x{address:X4}");
                        Debug.WriteLine($"        CMP BL, 0x{cmpValue:X2} (BL=0x{blValue:X2})");
                    }
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

                        if (address >= 0x0210 && address <= 0x02F0)
                        {
                            Debug.WriteLine($"      *** ПЕРЕСЫЛКА МЕЖДУ РЕГИСТРАМИ В ОБЩЕМ КОДЕ ***");
                            Debug.WriteLine($"        Адрес: 0x{address:X4}");
                            Debug.WriteLine($"        {regNames16[destReg]} = {regNames16[srcReg]} (0x{srcValue:X4})");
                        }
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

                            if (address >= 0x0210 && address <= 0x02F0)
                            {
                                Debug.WriteLine($"      *** ПЕРЕСЫЛКА 8-БИТНЫХ РЕГИСТРОВ В ОБЩЕМ КОДЕ ***");
                                Debug.WriteLine($"        Адрес: 0x{address:X4}");
                                Debug.WriteLine($"        {regNames8[destReg]} = {regNames8[srcReg]} (0x{srcValue:X2})");

                                if (registerTracker.TryGetRegisterValue(destFullReg, out ushort fullValue))
                                {
                                    Debug.WriteLine($"        {destFullReg} теперь = 0x{fullValue:X4}");
                                }
                            }
                        }
                    }
                }
            }

            #endregion
        }
    }
}