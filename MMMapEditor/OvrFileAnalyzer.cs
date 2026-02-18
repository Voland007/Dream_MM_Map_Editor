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

        // Вспомогательный класс для альтернативных путей
        private class AlternativePath
        {
            public int ObjectIndex { get; set; }
            public uint Address { get; set; }
            public string Condition { get; set; }
            public uint TargetAddress { get; set; }
            public bool Analyzed { get; set; }
            public int PathNumber { get; set; }
        }

        // Вспомогательный класс для отслеживания регистров
        private class RegisterTracker
        {
            private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
            private Dictionary<string, string> registerSources = new Dictionary<string, string>();

            // НОВОЕ: отслеживание источников значений с сохранением исходного BX
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

            // НОВОЕ: установка значения с информацией об источнике и исходном BX
            public void SetRegisterValueWithSource(string reg, ushort value, ushort sourceAddr, ushort originalBx, bool fromTable, uint address, string instruction)
            {
                SetRegisterValue(reg, value, address, instruction);
                registerSources2[reg.ToUpper()] = (sourceAddr, fromTable, originalBx);
            }

            // НОВОЕ: проверка, загружено ли значение из таблицы (с учётом цепочек)
            public bool IsFromTable(string reg)
            {
                string regUpper = reg.ToUpper();

                // Прямая проверка
                if (registerSources2.TryGetValue(regUpper, out var src) && src.fromTable)
                    return true;

                // Для CL проверяем также CX
                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc) && cxSrc.fromTable)
                    return true;

                // Для AL проверяем также AX
                if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc) && axSrc.fromTable)
                    return true;

                return false;
            }

            // НОВОЕ: получение адреса источника (с учётом цепочек)
            public ushort? GetSourceAddress(string reg)
            {
                string regUpper = reg.ToUpper();

                if (registerSources2.TryGetValue(regUpper, out var src))
                    return src.addr;

                // Для CL проверяем CX
                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                    return cxSrc.addr;

                // Для AL проверяем AX
                if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                    return axSrc.addr;

                return null;
            }

            // НОВОЕ: получение исходного BX, с которым было загружено значение (с учётом цепочек)
            public ushort? GetOriginalBx(string reg)
            {
                string regUpper = reg.ToUpper();

                if (registerSources2.TryGetValue(regUpper, out var src))
                    return src.originalBx;

                // Для CL проверяем CX
                if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                    return cxSrc.originalBx;

                // Для AL проверяем AX
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
        }

        // Вспомогательный класс для результатов анализа пути
        private class PathAnalysisResult
        {
            public HashSet<string> FoundTexts { get; set; } = new HashSet<string>();
            public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
            public byte? MonsterIndex1 { get; set; }
            public byte? MonsterIndex2 { get; set; }
            public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
                = new Dictionary<int, (byte, byte, bool)>();

            // НОВОЕ: для частично определённых битв
            public bool HasPartialBattlePattern { get; set; } = false;
            public List<PartialBattleInfo> PartialBattleInfo { get; set; } = new List<PartialBattleInfo>();
            public List<PartiallyDefinedBattle> PartialBattles { get; set; } = new List<PartiallyDefinedBattle>();

            public bool IsInLoop { get; set; } = false;
            public uint LoopStartAddress { get; set; } = 0;
            public int LoopIteration { get; set; } = 0;
            public bool IsIndeterminateLoop { get; set; } = false;
            public int LoopIterationCount { get; set; } = 0;
        }

        // НОВОЕ: класс для хранения информации о частичной битве
        private class PartialBattleInfo
        {
            public int BxIndex { get; set; }
            public ushort DestAddr { get; set; } // 0x3C58 или 0x3C29
            public string SrcReg { get; set; }
            public byte SrcRegValue { get; set; }
            public bool IsFromTable { get; set; } // true если загружено из CDA9/CDB1
            public ushort? SourceTableAddr { get; set; } // адрес в таблице, если известно
        }

        // Класс для хранения информации об обращении к памяти
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

        // Класс для хранения результата выполнения целого макроса
        private class MacroExecutionResult
        {
            public Dictionary<ConditionRange, CodeEmulationResult> BranchResults { get; set; } = new Dictionary<ConditionRange, CodeEmulationResult>();
            public CodeEmulationResult CommonResult { get; set; } = new CodeEmulationResult();
        }

        // Класс для описания диапазона условия
        private class ConditionRange
        {
            public byte Min { get; set; }
            public byte Max { get; set; }
            public bool IsX { get; set; } // true для X, false для Y

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

        // Класс для описания пути выполнения
        private class ExecutionPath
        {
            public uint StartAddress { get; set; }
            public X86Instruction Condition { get; set; }
            public uint SourceAddress { get; set; }
        }

        // Класс для хранения состояния выполнения
        private class ExecutionState
        {
            public Dictionary<string, ushort> Registers { get; set; } = new Dictionary<string, ushort>();
            public Stack<uint> CallStack { get; set; } = new Stack<uint>();
            public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
            public CodeEmulationResult CurrentResult { get; set; } = new CodeEmulationResult();
            public ConditionRange CurrentCondition { get; set; }
        }

        // Класс для результата эмуляции кода
        private class CodeEmulationResult
        {
            public HashSet<string> Texts { get; set; } = new HashSet<string>();
            public byte? MonsterPower { get; set; }
            public byte? MonsterLevel { get; set; }
            public List<(int Index, byte Val1, byte Val2, bool IsIndeterminate)> BattleMonsters { get; set; } = new List<(int, byte, byte, bool)>();
            public byte DirectionByte { get; set; }
            public bool HasSignificantCode { get; set; }
        }

        public static List<OvrObject> AnalyzeOvrFile(string filename, OvrFileConfig config, Dictionary<Point, string> existingCentralOptions)
        {
            var analyzer = new OvrFileAnalyzer(config);
            return analyzer.InternalAnalyze(filename, existingCentralOptions);
        }

        // ========== ОСНОВНОЙ МЕТОД АНАЛИЗА ==========

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

                // === 1. ОРИГИНАЛЬНАЯ ЛОГИКА: объекты из таблицы [C973+] ===
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

                // === 2. НОВАЯ ЛОГИКА: поиск макросов в коде ===
                fs.Seek(0, SeekOrigin.Begin);

                // Дизассемблируем всё для анализа
                var allInstructions = DisassembleAll(br);

                // Находим все сравнения координат
                var comparisons = FindAllCoordinateComparisons(br, allInstructions);

                Debug.WriteLine($"Найдено сравнений координат: {comparisons.Count}");

                // Создаём HashSet существующих координат для быстрой проверки
                var existingCoords = new HashSet<string>();
                foreach (var obj in objects)
                {
                    existingCoords.Add($"{obj.X},{obj.Y}");
                }

                // Для каждого сравнения анализируем код по адресу перехода
                foreach (var comparison in comparisons)
                {
                    Debug.WriteLine($"\nАнализ макроса по адресу 0x{comparison.JumpTarget:X4} для [{comparison.MemAddr:X4}]={comparison.Value} ({(comparison.IsLinear ? "линейный" : comparison.JumpType)})");

                    // Создаём временный объект для сбора данных
                    var tempObject = new OvrObject
                    {
                        X = 0,
                        Y = 0,
                        DirectionByte = 0
                    };

                    // Находим все альтернативные пути
                    var alternativePaths = new List<AlternativePath>();
                    ShowLinearDisassemblyAndCollectAlternativePaths(br, comparison.JumpTarget, alternativePaths, 0);

                    // СПИСОК ДЛЯ ХРАНЕНИЯ РЕЗУЛЬТАТОВ ВСЕХ ПУТЕЙ
                    var allPathResults = new List<PathAnalysisResult>();

                    // Анализируем основной путь
                    var registerTracker = new RegisterTracker();
                    var mainPathAnalysis = AnalyzeMainPath(br, comparison.JumpTarget, registerTracker, tempObject);
                    allPathResults.Add(mainPathAnalysis);

                    bool mainPathHasPattern = mainPathAnalysis.HasPartialBattlePattern;

                    // Собираем все тексты и монстров
                    var allTexts = new HashSet<string>(mainPathAnalysis.FoundTexts);
                    byte? monsterPower = mainPathAnalysis.MonsterPower;
                    byte? monsterLevel = mainPathAnalysis.MonsterLevel;
                    var battleMonsters = new Dictionary<int, (byte val1, byte val2, bool isIndeterminate)>();
                    var partialBattles = new List<PartiallyDefinedBattle>();
                    bool anyPathHasPattern = mainPathHasPattern;

                    // Данные о загрузке из таблиц для всех путей
                    var allPartialBattleInfo = new List<PartialBattleInfo>();

                    foreach (var entry in mainPathAnalysis.BattleMonsterEntries)
                    {
                        battleMonsters[entry.Key] = (entry.Value.val1, entry.Value.val2, entry.Value.isIndeterminate);
                    }

                    foreach (var partial in mainPathAnalysis.PartialBattles)
                    {
                        partialBattles.Add(partial);
                    }

                    foreach (var info in mainPathAnalysis.PartialBattleInfo)
                    {
                        allPartialBattleInfo.Add(info);
                    }

                    // Анализируем альтернативные пути
                    int pathNumber = 1;
                    var analyzedPaths = new HashSet<string>();

                    foreach (var path in alternativePaths)
                    {
                        if (path.Analyzed) continue;

                        string pathKey = $"{comparison.JumpTarget:X4}_{path.Address:X4}_{path.TargetAddress:X4}";
                        if (analyzedPaths.Contains(pathKey)) continue;
                        analyzedPaths.Add(pathKey);

                        Debug.WriteLine($"  Анализ альтернативного пути {pathNumber} -> 0x{path.TargetAddress:X4}");

                        var pathRegisterTracker = new RegisterTracker();
                        var pathResult = AnalyzeAlternativePath(br, comparison.JumpTarget, path.Address, path.TargetAddress,
                            0, pathNumber, new HashSet<string>(), pathRegisterTracker, 0);

                        // ДОБАВЛЯЕМ РЕЗУЛЬТАТ АЛЬТЕРНАТИВНОГО ПУТИ В ОБЩИЙ СПИСОК
                        allPathResults.Add(pathResult);

                        // Обновляем флаг, если в альтернативном пути есть паттерн
                        if (pathResult.HasPartialBattlePattern)
                        {
                            anyPathHasPattern = true;
                        }

                        // Добавляем найденные тексты
                        foreach (var text in pathResult.FoundTexts)
                        {
                            allTexts.Add(text);
                            Debug.WriteLine($"    Найден текст: {text}");
                        }

                        // Добавляем силу монстров
                        if (pathResult.MonsterPower.HasValue)
                        {
                            monsterPower = pathResult.MonsterPower;
                            Debug.WriteLine($"    Сила монстров: {pathResult.MonsterPower}");
                        }

                        // Добавляем уровень монстров
                        if (pathResult.MonsterLevel.HasValue)
                        {
                            monsterLevel = pathResult.MonsterLevel;
                            Debug.WriteLine($"    Уровень монстров: {pathResult.MonsterLevel}");
                        }

                        // Добавляем полностью определённых монстров
                        foreach (var entry in pathResult.BattleMonsterEntries)
                        {
                            if (!battleMonsters.ContainsKey(entry.Key))
                            {
                                battleMonsters[entry.Key] = (entry.Value.val1, entry.Value.val2, entry.Value.isIndeterminate);
                                Debug.WriteLine($"    Монстр: index={entry.Key}, val1={entry.Value.val1}, val2={entry.Value.val2}");
                            }
                        }

                        // Добавляем частично определённых монстров
                        foreach (var partial in pathResult.PartialBattles)
                        {
                            if (!partialBattles.Any(p => p.BxIndex == partial.BxIndex))
                            {
                                partialBattles.Add(partial);
                                Debug.WriteLine($"    Частично определённая битва: BX={partial.BxIndex}, диапазоны [{partial.RangeStart1:X2}-{partial.RangeEnd1:X2}] + [{partial.RangeStart2:X2}-{partial.RangeEnd2:X2}]");
                            }
                        }

                        // Добавляем информацию о загрузке из таблиц
                        foreach (var info in pathResult.PartialBattleInfo)
                        {
                            // Проверяем, не добавляли ли уже такую информацию
                            if (!allPartialBattleInfo.Any(i => i.BxIndex == info.BxIndex &&
                                                               i.SrcReg == info.SrcReg &&
                                                               i.SourceTableAddr == info.SourceTableAddr))
                            {
                                allPartialBattleInfo.Add(info);
                                Debug.WriteLine($"    ДОБАВЛЕНО ЗНАЧЕНИЕ В ОБЪЕКТ: BX={info.BxIndex}, {info.SrcReg}=0x{info.SrcRegValue:X2} из [{info.SourceTableAddr:X4}]");
                            }
                        }

                        path.Analyzed = true;
                        pathNumber++;
                    }

                    // Определяем, есть ли значимый код (включая наличие паттерна)
                    bool hasSignificantCode = allTexts.Count > 0 ||
                                              monsterPower.HasValue ||
                                              monsterLevel.HasValue ||
                                              battleMonsters.Count > 0 ||
                                              partialBattles.Count > 0 ||
                                              anyPathHasPattern ||
                                              allPartialBattleInfo.Count > 0;

                    if (!hasSignificantCode)
                    {
                        Debug.WriteLine("  -> нет значимого кода, пропускаем");
                        continue;
                    }

                    Debug.WriteLine($"  -> найден значимый код: {allTexts.Count} текстов, {battleMonsters.Count} полностью определённых, {partialBattles.Count} частично определённых, паттерн={anyPathHasPattern}, загрузок из таблиц={allPartialBattleInfo.Count}");

                    // Определяем, какая координата проверяется
                    bool isX = (comparison.MemAddr == 0x3C38);
                    bool isY = (comparison.MemAddr == 0x3C39);
                    bool isFull = (comparison.MemAddr == 0x3C3A);

                    // Создаём объекты для всех клеток, попадающих под условие
                    int objectsCreated = 0;

                    if (comparison.IsLinear)
                    {
                        // Линейный макрос - применяется к диапазону значений
                        Debug.WriteLine($"  Линейный макрос для значений [{comparison.LinearStart}-{comparison.LinearEnd}]");

                        for (byte y = comparison.LinearStart; y <= comparison.LinearEnd && y < 16; y++)
                        {
                            for (byte x = 0; x < 16; x++)
                            {
                                Point pos = new Point(x, y);
                                string coordKey = $"{x},{y}";

                                // Проверяем, что на клетке уже есть "Случайная встреча"
                                if (existingCentralOptions.TryGetValue(pos, out string existingOption))
                                {
                                    if (existingOption == "Случайная встреча")
                                    {
                                        // ПРОВЕРЯЕМ, НЕТ ЛИ УЖЕ ОБЪЕКТА ANYOBJECT ДЛЯ ЭТОЙ КЛЕТКИ
                                        var existingAnyObject = objects.FirstOrDefault(o => o.X == x && o.Y == y);

                                        // !!! ИСПРАВЛЕНИЕ: AnyObject имеет приоритет !!!
                                        if (existingAnyObject != null)
                                        {
                                            // Пропускаем создание AnyObjectSpec, так как AnyObject важнее
                                            Debug.WriteLine($"  -> Клетка ({x},{y}) УЖЕ ИМЕЕТ AnyObject - AnyObjectSpec ИГНОРИРУЕТСЯ");
                                            continue;
                                        }

                                        Debug.WriteLine($"  Клетка ({x},{y}) имеет 'Случайная встреча' и НЕТ AnyObject - создаём AnyObjectSpec");

                                        // СОЗДАЁМ НОВЫЙ ОБЪЕКТ AnyObjectSpec
                                        var obj = new OvrObject
                                        {
                                            X = x,
                                            Y = y,
                                            DirectionByte = 0,
                                            MonsterPower = monsterPower,
                                            MonsterLevel = monsterLevel
                                        };

                                        obj.HasAnyTableLoad = anyPathHasPattern;

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

                                        int loadedValuesCount = 0;
                                        foreach (var info in allPartialBattleInfo)
                                        {
                                            obj.AddLoadedValue(
                                                info.BxIndex,
                                                info.SrcReg,
                                                info.SrcRegValue,
                                                info.SourceTableAddr ?? 0,
                                                info.DestAddr == 0x3C58,
                                                true
                                            );
                                            loadedValuesCount++;
                                        }

                                        Debug.WriteLine($"    После добавления: HasAnyTableLoad={obj.HasAnyTableLoad}, LoadedValues={obj.LoadedValues.Count}, добавлено={loadedValuesCount}");

                                        objects.Add(obj);
                                        existingCoords.Add(coordKey);
                                        objectsCreated++;
                                        Debug.WriteLine($"  -> СОЗДАН AnyObjectSpec на ({x},{y}) из линейного макроса");
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"  -> Клетка ({x},{y}) имеет '{existingOption}' - НЕ создаём AnyObjectSpec");
                                        continue;
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine($"  -> Клетка ({x},{y}) пуста - НЕ создаём AnyObjectSpec");
                                    continue;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Обычный условный переход - применяется согласно типу перехода
                        for (byte x = 0; x < 16; x++)
                        {
                            for (byte y = 0; y < 16; y++)
                            {
                                bool matches = false;

                                if (isFull)
                                {
                                    // Полные координаты: конкретная клетка
                                    byte targetX = (byte)(comparison.Value & 0x0F);
                                    byte targetY = (byte)(comparison.Value >> 4);
                                    matches = (x == targetX && y == targetY);
                                }
                                else if (isX)
                                {
                                    // Проверка X
                                    string jumpType = comparison.JumpType.ToUpper();
                                    if (jumpType == "JE" || jumpType == "JZ")
                                        matches = (x == comparison.Value);
                                    else if (jumpType == "JNE" || jumpType == "JNZ")
                                        matches = (x != comparison.Value);
                                    else if (jumpType == "JB" || jumpType == "JC")
                                        matches = (x < comparison.Value);
                                    else if (jumpType == "JBE" || jumpType == "JNA")
                                        matches = (x <= comparison.Value);
                                    else if (jumpType == "JA" || jumpType == "JNBE")
                                        matches = (x > comparison.Value);
                                    else if (jumpType == "JAE" || jumpType == "JNB" || jumpType == "JNC")
                                        matches = (x >= comparison.Value);
                                }
                                else if (isY)
                                {
                                    // Проверка Y
                                    string jumpType = comparison.JumpType.ToUpper();
                                    if (jumpType == "JE" || jumpType == "JZ")
                                        matches = (y == comparison.Value);
                                    else if (jumpType == "JNE" || jumpType == "JNZ")
                                        matches = (y != comparison.Value);
                                    else if (jumpType == "JB" || jumpType == "JC")
                                        matches = (y < comparison.Value);
                                    else if (jumpType == "JBE" || jumpType == "JNA")
                                        matches = (y <= comparison.Value);
                                    else if (jumpType == "JA" || jumpType == "JNBE")
                                        matches = (y > comparison.Value);
                                    else if (jumpType == "JAE" || jumpType == "JNB" || jumpType == "JNC")
                                        matches = (y >= comparison.Value);
                                }

                                if (!matches) continue;

                                Point pos = new Point(x, y);
                                string coordKey = $"{x},{y}";

                                // Проверяем, что на клетке уже есть "Случайная встреча"
                                if (existingCentralOptions.TryGetValue(pos, out string existingOption))
                                {
                                    if (existingOption == "Случайная встреча")
                                    {
                                        // !!! ИСПРАВЛЕНИЕ: AnyObject имеет приоритет !!!
                                        var existingAnyObject = objects.FirstOrDefault(o => o.X == x && o.Y == y);

                                        if (existingAnyObject != null)
                                        {
                                            Debug.WriteLine($"  -> Клетка ({x},{y}) УЖЕ ИМЕЕТ AnyObject - AnyObjectSpec ИГНОРИРУЕТСЯ");
                                            continue;
                                        }

                                        Debug.WriteLine($"  Клетка ({x},{y}) имеет 'Случайная встреча' и НЕТ AnyObject - создаём AnyObjectSpec");

                                        // СОЗДАЁМ НОВЫЙ ОБЪЕКТ AnyObjectSpec
                                        var obj = new OvrObject
                                        {
                                            X = x,
                                            Y = y,
                                            DirectionByte = 0,
                                            MonsterPower = monsterPower,
                                            MonsterLevel = monsterLevel
                                        };

                                        obj.HasAnyTableLoad = anyPathHasPattern;

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

                                        int loadedValuesCount = 0;
                                        foreach (var info in allPartialBattleInfo)
                                        {
                                            obj.AddLoadedValue(
                                                info.BxIndex,
                                                info.SrcReg,
                                                info.SrcRegValue,
                                                info.SourceTableAddr ?? 0,
                                                info.DestAddr == 0x3C58,
                                                true
                                            );
                                            loadedValuesCount++;
                                        }

                                        objects.Add(obj);
                                        existingCoords.Add(coordKey);
                                        objectsCreated++;
                                        Debug.WriteLine($"  -> СОЗДАН AnyObjectSpec на ({x},{y})");
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"  -> Клетка ({x},{y}) имеет '{existingOption}' - НЕ создаём AnyObjectSpec");
                                        continue;
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine($"  -> Клетка ({x},{y}) пуста - НЕ создаём AnyObjectSpec");
                                    continue;
                                }
                            }
                        }
                    }

                    Debug.WriteLine($"  -> создано/объединено объектов: {objectsCreated}");
                }

                Debug.WriteLine($"\nВсего объектов после анализа: {objects.Count}");

                // Выводим статистику по типам объектов
                int tableObjects = objects.Count(o => o.PathTexts.ContainsKey(0) &&
                                                      o.PathTexts[0] != null &&
                                                      o.PathTexts[0].Any(t => t.Contains("Table object")));
                int specObjects = objects.Count - tableObjects;
                int partialBattleObjects = objects.Count(o => o.HasPartiallyDefinedBattles);
                int tableLoadObjects = objects.Count(o => o.HasAnyTableLoad && o.LoadedValues.Count > 0);

                Debug.WriteLine($"Объектов из таблицы: {tableObjects}, AnyObjectSpec: {specObjects}, с частичными битвами: {partialBattleObjects}, с загрузкой из таблиц: {tableLoadObjects}");

                // Выводим список всех созданных объектов для отладки
                foreach (var obj in objects.OrderBy(o => o.Y).ThenBy(o => o.X))
                {
                    Debug.WriteLine($"  Объект X={obj.X}, Y={obj.Y}: Power={obj.MonsterPower}, Level={obj.MonsterLevel}, BattleMonsters={obj.BattleMonsters.Count}, PartialBattles={obj.PartiallyDefinedBattles.Count}, TableLoad={obj.HasAnyTableLoad}");
                }
            }

            return objects;
        }

        // Вспомогательный класс для хранения информации о сравнении
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
        }

        // Метод для поиска всех сравнений координат
        private List<CoordinateComparison> FindAllCoordinateComparisons(BinaryReader br, List<X86Instruction> allInstructions)
        {
            var comparisons = new List<CoordinateComparison>();

            for (int i = 0; i < allInstructions.Count; i++)
            {
                var insn = allInstructions[i];

                // Ищем сравнения с непосредственным значением
                if (IsCompareWithImmediate(insn, out byte immValue, out string reg))
                {
                    // Определяем, откуда взялось значение в регистре
                    ushort? sourceAddr = FindMemorySourceForRegister(allInstructions, i, reg);
                    if (!sourceAddr.HasValue) continue;

                    // Проверяем, что это один из наших адресов
                    if (sourceAddr.Value != 0x3C38 && sourceAddr.Value != 0x3C39 && sourceAddr.Value != 0x3C3A)
                        continue;

                    // Смотрим следующие инструкции - может быть несколько условных переходов подряд
                    int j = i + 1;
                    byte? lastCompareValue = null;
                    uint? lastJumpTarget = null;
                    string lastJumpType = null;

                    // Проверяем, было ли предыдущее сравнение с тем же регистром
                    bool hasPrecedingCondition = false;
                    byte precedingValue = 0;
                    string precedingJumpType = null;

                    if (i > 0)
                    {
                        // Ищем предыдущее сравнение (до 10 инструкций назад)
                        for (int k = i - 1; k >= Math.Max(0, i - 10); k--)
                        {
                            var prevInsn = allInstructions[k];
                            if (IsCompareWithImmediate(prevInsn, out byte prevValue, out string prevReg) && prevReg == reg)
                            {
                                hasPrecedingCondition = true;
                                precedingValue = prevValue;
                                // Смотрим следующий за ним переход
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
                            Value = immValue,
                            JumpTarget = jumpTarget,
                            JumpType = nextInsn.Mnemonic.ToUpper(),
                            IsLinear = false
                        });

                        Debug.WriteLine($"  Найдено сравнение: 0x{insn.Address:X4} [{sourceAddr.Value:X4}]={immValue} -> 0x{jumpTarget:X4} ({nextInsn.Mnemonic})");

                        lastCompareValue = immValue;
                        lastJumpTarget = jumpTarget;
                        lastJumpType = nextInsn.Mnemonic.ToUpper();
                        j++;
                    }

                    // Если после условных переходов есть линейный код, добавляем запись для него
                    if (j < allInstructions.Count && lastCompareValue.HasValue)
                    {
                        // Проверяем, что следующая инструкция - не новое сравнение
                        var nextInsn = allInstructions[j];
                        if (IsCompareWithImmediate(nextInsn, out _, out _))
                        {
                            // Это новое сравнение, а не линейный код
                            // Не создаём линейный макрос
                            Debug.WriteLine($"  Пропуск линейного выполнения: следующая инструкция - сравнение по адресу 0x{nextInsn.Address:X4}");
                            continue;
                        }

                        // Определяем базовый диапазон для линейного выполнения
                        byte linearStart = 0;
                        byte linearEnd = 255;

                        if (lastJumpType == "JB" || lastJumpType == "JC")
                        {
                            // После JB идут значения >= compareValue
                            linearStart = lastCompareValue.Value;
                            linearEnd = 255;
                        }
                        else if (lastJumpType == "JAE" || lastJumpType == "JNB" || lastJumpType == "JNC")
                        {
                            // После JAE идут значения < compareValue
                            linearStart = 0;
                            linearEnd = (byte)(lastCompareValue.Value - 1);
                        }

                        // Если это второе сравнение и было предыдущее условие
                        if (hasPrecedingCondition)
                        {
                            if (precedingJumpType == "JB" || precedingJumpType == "JC")
                            {
                                // Предыдущее условие: Y < X
                                // Значит, линейный код выполняется для Y >= X
                                linearStart = Math.Max(linearStart, precedingValue);
                            }
                            else if (precedingJumpType == "JAE" || precedingJumpType == "JNB" || precedingJumpType == "JNC")
                            {
                                // Предыдущее условие: Y >= X
                                // Значит, линейный код выполняется для Y < X
                                linearEnd = Math.Min(linearEnd, (byte)(precedingValue - 1));
                            }
                            else if (precedingJumpType == "JE" || precedingJumpType == "JZ")
                            {
                                // Предыдущее условие: Y == X
                                // Линейный код выполняется для Y != X, но это сложнее
                                // В нашем случае для Y > 3 мы уже знаем
                                if (precedingValue == 3 && lastCompareValue == 9)
                                {
                                    linearStart = 4;
                                    linearEnd = 8;
                                }
                            }
                        }

                        // Убеждаемся, что диапазон корректен
                        if (linearStart <= linearEnd)
                        {
                            comparisons.Add(new CoordinateComparison
                            {
                                CompareAddress = (uint)insn.Address,
                                MemAddr = sourceAddr.Value,
                                Value = lastCompareValue.Value,
                                JumpTarget = (uint)allInstructions[j].Address,
                                JumpType = "LINEAR",
                                IsLinear = true,
                                LinearStart = linearStart,
                                LinearEnd = linearEnd
                            });

                            Debug.WriteLine($"  Найдено линейное выполнение: 0x{insn.Address:X4} [{sourceAddr.Value:X4}]={lastCompareValue} -> 0x{allInstructions[j].Address:X4} для значений [{linearStart}-{linearEnd}]");
                        }
                    }
                }
            }

            return comparisons;
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

            var alternativePaths = new List<AlternativePath>();
            ShowLinearDisassemblyAndCollectAlternativePaths(br, patchAddress, alternativePaths, objIndex);

            var mainRegisterTracker = new RegisterTracker();
            var mainPathAnalysis = AnalyzeMainPath(br, patchAddress, mainRegisterTracker, ovrObject);
            ovrObject.PathTexts[0] = mainPathAnalysis.FoundTexts;

            if (mainPathAnalysis.MonsterPower.HasValue)
                ovrObject.MonsterPower = mainPathAnalysis.MonsterPower.Value;
            if (mainPathAnalysis.MonsterLevel.HasValue)
                ovrObject.MonsterLevel = mainPathAnalysis.MonsterLevel.Value;

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
                }
            }

            // НОВОЕ: добавляем частично определённые битвы из основного пути
            foreach (var partial in mainPathAnalysis.PartialBattles)
            {
                ovrObject.AddPartiallyDefinedBattle(
                    partial.BxIndex,
                    partial.RangeStart1,
                    partial.RangeEnd1,
                    partial.RangeStart2,
                    partial.RangeEnd2
                );
            }

            var globallyAnalyzedPaths = new HashSet<string>();
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

                    if (pathResult.MonsterPower.HasValue)
                        ovrObject.MonsterPower = pathResult.MonsterPower.Value;
                    if (pathResult.MonsterLevel.HasValue)
                        ovrObject.MonsterLevel = pathResult.MonsterLevel.Value;

                    bool isAltPathIndeterminate = pathResult.IsIndeterminateLoop;
                    foreach (var entry in pathResult.BattleMonsterEntries.OrderBy(e => e.Key))
                    {
                        if (entry.Value.val1 != 0 || entry.Value.val2 != 0)
                        {
                            var existingMonster = ovrObject.BattleMonsters.FirstOrDefault(m => m.Index == entry.Key);

                            if (existingMonster == null)
                            {
                                ovrObject.AddBattleMonster(
                                    entry.Key,
                                    entry.Value.val1,
                                    entry.Value.val2,
                                    entry.Key > 0 && (isAltPathIndeterminate || entry.Value.isIndeterminate)
                                );
                            }
                        }
                    }

                    // НОВОЕ: добавляем частично определённые битвы из альтернативных путей
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

                    SaveNestedPathsRecursively(ovrObject.PathTexts, pathResult.NestedPaths, ref currentPathNumber);
                    path.Analyzed = true;
                    currentPathNumber++;
                }
            }

            ovrObject.PathTexts = FilterUniquePaths(ovrObject.PathTexts);
            return ovrObject;
        }

        private PathAnalysisResult AnalyzeMainPath(BinaryReader br, uint patchAddress,
     RegisterTracker registerTracker, OvrObject currentObject)
        {
            var result = new PathAnalysisResult();

            result.IsIndeterminateLoop = false;
            result.IsInLoop = false;  // Явно инициализируем
            result.LoopStartAddress = 0;
            result.BattleMonsterEntries.Clear();
            result.PartialBattleInfo.Clear();
            result.PartialBattles.Clear();
            result.FoundTexts.Clear();
            result.MonsterPower = null;
            result.MonsterLevel = null;
            result.MonsterIndex1 = null;
            result.MonsterIndex2 = null;

            var indirectTexts = AnalyzeIndirectTextPatterns(br, patchAddress);
            foreach (var text in indirectTexts)
            {
                result.FoundTexts.Add(text);
            }

            var analyzedCalls = new HashSet<uint>();
            AnalyzeCallsWithFullDisassembly(br, patchAddress, analyzedCalls, result,
                registerTracker, currentObject, 0);

            // НОВОЕ: анализируем частично определённые битвы
            AnalyzePartialBattleRanges(br, result);

            return result;
        }

        private PathAnalysisResult AnalyzeAlternativePath(BinaryReader br, uint patchAddress, uint jumpAddress,
            uint alternativeStartAddress, int objIndex, int pathIndex, HashSet<string> alreadyAnalyzedPaths,
            RegisterTracker registerTracker, int recursionDepth)
        {
            const int MAX_RECURSION_DEPTH = 3;

            var result = new PathAnalysisResult();

            result.IsIndeterminateLoop = false;
            result.LoopStartAddress = 0;
            result.BattleMonsterEntries.Clear();
            result.PartialBattleInfo.Clear();
            result.PartialBattles.Clear();
            result.FoundTexts.Clear();
            result.MonsterPower = null;
            result.MonsterLevel = null;
            result.MonsterIndex1 = null;
            result.MonsterIndex2 = null;

            if (recursionDepth > MAX_RECURSION_DEPTH) return result;

            long fileSize = br.BaseStream.Length;
            if (patchAddress >= fileSize) return result;

            string pathKey = $"{jumpAddress:X4}_{alternativeStartAddress:X4}_{recursionDepth}";
            if (alreadyAnalyzedPaths.Contains(pathKey)) return result;
            alreadyAnalyzedPaths.Add(pathKey);

            var localAlternativePaths = new List<AlternativePath>();
            ShowLinearDisassemblyWithAlternativeBranch(br, patchAddress, jumpAddress, alternativeStartAddress,
                localAlternativePaths, objIndex, 0, true);

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

            var indirectTexts = AnalyzeIndirectTextPatterns(br, patchAddress);
            foreach (var text in indirectTexts)
            {
                result.FoundTexts.Add(text);
            }

            // НОВОЕ: анализируем частично определённые битвы
            AnalyzePartialBattleRanges(br, result);

            if (localAlternativePaths.Count > 0 && recursionDepth < MAX_RECURSION_DEPTH)
            {
                Debug.WriteLine($"    Найдено {localAlternativePaths.Count} вложенных путей, глубина={recursionDepth}");

                for (int i = 0; i < localAlternativePaths.Count; i++)
                {
                    var nestedPath = localAlternativePaths[i];
                    if (nestedPath.Analyzed)
                    {
                        Debug.WriteLine($"      Путь {i + 1} уже проанализирован");
                        continue;
                    }

                    Debug.WriteLine($"      Анализ вложенного пути {i + 1}: адрес=0x{nestedPath.Address:X4}, target=0x{nestedPath.TargetAddress:X4}");

                    // Убрана избыточная проверка IsTransitionReachableFromAlternativePath()
                    // Просто анализируем путь, если он ещё не analysed

                    var nestedRegisterTracker = new RegisterTracker();
                    var nestedResult = AnalyzeAlternativePath(br, patchAddress, nestedPath.Address,
                        nestedPath.TargetAddress, objIndex, pathIndex * 10 + i + 1,
                        new HashSet<string>(alreadyAnalyzedPaths), nestedRegisterTracker, recursionDepth + 1);

                    result.NestedPaths[pathIndex * 10 + i + 1] = nestedResult;
                    nestedPath.Analyzed = true;

                    Debug.WriteLine($"      Вложенный путь {i + 1} проанализирован, найдено текстов: {nestedResult.FoundTexts.Count}");
                }
            }

            return result;
        }

        // метод для анализа диапазонов CDA9-CDB0 и CDB1-CDB8

        private void AnalyzePartialBattleRanges(BinaryReader br, PathAnalysisResult result)
        {
            if (!result.HasPartialBattlePattern || result.PartialBattleInfo.Count == 0)
            {
                Debug.WriteLine($"    АНАЛИЗ ЧАСТИЧНЫХ БИТВ: нет данных (HasPattern={result.HasPartialBattlePattern}, InfoCount={result.PartialBattleInfo.Count})");
                return;
            }

            Debug.WriteLine($"    АНАЛИЗ ЧАСТИЧНЫХ БИТВ: найдено {result.PartialBattleInfo.Count} записей");
            Debug.WriteLine($"    Состояние цикла: IsInLoop={result.IsInLoop}, LoopStartAddress=0x{result.LoopStartAddress:X4}, LoopIterationCount={result.LoopIterationCount}");

            // Группируем записи по BX индексу (индекс в массиве, куда сохраняются значения)
            var groupedByBx = result.PartialBattleInfo.GroupBy(p => p.BxIndex).OrderBy(g => g.Key);

            foreach (var group in groupedByBx)
            {
                int saveBxIndex = group.Key; // BX при сохранении (0, 1, 2, ...)
                var entries = group.ToList();

                Debug.WriteLine($"      Группа BX(сохранения)={saveBxIndex}: {entries.Count} записей");

                // Для отладки выводим все записи в группе
                foreach (var info in entries)
                {
                    string sourceInfo = info.SourceTableAddr.HasValue
                        ? $"из [{info.SourceTableAddr:X4}]"
                        : "источник неизвестен";

                    Debug.WriteLine($"        {info.DestAddr:X4} = {info.SrcReg}({info.SrcRegValue:X2}) {sourceInfo}");
                }

                // Находим записи для 3C58 и 3C29
                var entry58 = entries.FirstOrDefault(e => e.DestAddr == 0x3C58);
                var entry29 = entries.FirstOrDefault(e => e.DestAddr == 0x3C29);

                if (entry58 == null || entry29 == null)
                {
                    Debug.WriteLine($"        -> НЕТ ПОЛНОЙ ПАРЫ ЗАПИСЕЙ для создания частичной битвы");
                    continue;
                }

                // Определяем количество итераций цикла
                int iterationCount = 1; // По умолчанию 1 итерация (без цикла)

                if (result.IsInLoop && result.LoopStartAddress != 0)
                {
                    if (result.LoopIterationCount > 0)
                    {
                        iterationCount = result.LoopIterationCount;
                        Debug.WriteLine($"        Обнаружен цикл с {iterationCount} итерациями");
                    }
                    else
                    {
                        // Если количество итераций неизвестно, пытаемся определить по максимальному BX сохранения
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
                // ВАЖНО: BX при загрузке должен быть iteration (0..7), а НЕ baseLoadBx + iteration!
                for (int iteration = 0; iteration < iterationCount; iteration++)
                {
                    // BX для загрузки из таблиц = iteration (0, 1, 2, ...)
                    int loadBx = iteration;

                    // Адреса в таблицах для этой итерации
                    ushort table1Addr = (ushort)(0xCDA9 + loadBx);  // CDA9, CDAA, CDAB, CDAC, CDAD, CDAE, CDAF, CDB0
                    ushort table2Addr = (ushort)(0xCDB1 + loadBx);  // CDB1, CDB2, CDB3, CDB4, CDB5, CDB6, CDB7, CDB8

                    // ЧИТАЕМ РЕАЛЬНЫЕ ЗНАЧЕНИЯ ИЗ ТАБЛИЦ ПО ЭТИМ АДРЕСАМ
                    byte iterVal1 = 0;
                    byte iterVal2 = 0;
                    bool readSuccess1 = false;
                    bool readSuccess2 = false;

                    try
                    {
                        // Вычисляем смещения в файле
                        long fileOffset1 = table1Addr - _config.TextBaseAddr;
                        long fileOffset2 = table2Addr - _config.TextBaseAddr;

                        long originalPos = br.BaseStream.Position;

                        // Читаем первый байт
                        if (fileOffset1 >= 0 && fileOffset1 < br.BaseStream.Length)
                        {
                            br.BaseStream.Position = fileOffset1;
                            iterVal1 = br.ReadByte();
                            readSuccess1 = true;
                        }

                        // Читаем второй байт (для CDB1+ читаем 16 бит, но нам нужен младший байт)
                        if (fileOffset2 >= 0 && fileOffset2 + 1 < br.BaseStream.Length)
                        {
                            br.BaseStream.Position = fileOffset2;
                            byte lowByte = br.ReadByte();
                            byte highByte = br.ReadByte();
                            // Для частичной битвы нам нужен младший байт (он пойдёт в CL)
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

                    // Диапазон состоит только из одного значения (текущая итерация)
                    byte rangeStart1 = iterVal1;
                    byte rangeEnd1 = iterVal1;
                    byte rangeStart2 = iterVal2;
                    byte rangeEnd2 = iterVal2;

                    // Проверяем корректность диапазонов
                    if (rangeStart1 > rangeEnd1 || rangeStart2 > rangeEnd2)
                    {
                        Debug.WriteLine($"        -> НЕКОРРЕКТНЫЕ ДИАПАЗОНЫ ДЛЯ ИТЕРАЦИИ {iteration}: [{rangeStart1:X2}-{rangeEnd1:X2}] [{rangeStart2:X2}-{rangeEnd2:X2}]");
                        continue;
                    }

                    // Создаем объект частично определённой битвы для этой итерации
                    var partialBattle = new PartiallyDefinedBattle
                    {
                        BxIndex = saveBxIndex + iteration, // BX при сохранении: 0, 1, 2, ...
                        RangeStart1 = rangeStart1,
                        RangeEnd1 = rangeEnd1,
                        RangeStart2 = rangeStart2,
                        RangeEnd2 = rangeEnd2
                    };

                    // Добавляем в результат
                    result.PartialBattles.Add(partialBattle);

                    int combinations = (rangeEnd1 - rangeStart1 + 1) * (rangeEnd2 - rangeStart2 + 1);
                    Debug.WriteLine($"        -> СОЗДАНА ЧАСТИЧНАЯ БИТВА: BX(сохранения)={saveBxIndex + iteration}, BX(загрузки)={loadBx}, {combinations} комбинация (итерация {iteration})");
                    Debug.WriteLine($"           Значения: val1=0x{iterVal1:X2} из [{table1Addr:X4}], val2=0x{iterVal2:X2} из [{table2Addr:X4}]");

                    // Генерируем и выводим монстра для этой итерации
                    var monsters = partialBattle.GetPossibleMonsters();

                    if (monsters.Count > 0)
                    {
                        Debug.WriteLine($"           Монстр ID={monsters[0].MonsterId}");
                    }
                }

                Debug.WriteLine($"      ИТОГО: создано {iterationCount} частично определённых битв для BX(сохранения)={saveBxIndex}");
            }

            // Итоговая статистика
            Debug.WriteLine($"      ИТОГО ВСЕГО: создано {result.PartialBattles.Count} частично определённых битв");
        }

        // Вспомогательный метод для очистки имени монстра
        private string CleanMonsterName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return new string(name.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
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
                int loopIterationCount = 0;

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

                        byte[] instructionBytes = insn.Bytes;
                        string mnemonicUpper = insn.Mnemonic.ToUpper();

                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        // НОВОЕ: обнаружение цикла
                        if ((mnemonicUpper == "JC" || mnemonicUpper == "JB") &&
                            instructionBytes.Length >= 4 &&
                            instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                            instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            if (jumpTarget < currentAddress)
                            {
                                if (!result.IsIndeterminateLoop)
                                {
                                    result.IsIndeterminateLoop = true;
                                    result.IsInLoop = true;  // Устанавливаем флаг!
                                    result.LoopStartAddress = jumpTarget;
                                }

                                loopIterationCount++;
                                result.LoopIterationCount = loopIterationCount;

                                Debug.WriteLine($"    ОБНАРУЖЕН ЦИКЛ в основном пути, итерация {loopIterationCount}");
                            }
                        }

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
            const int MAX_LOOP_ITERATIONS = 8;

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

                int loopIterationCount = 0;
                bool loopDetected = false;

                // Убрана HashSet для отслеживания посещённых адресов в этом методе,
                // так как это блокировало вложенные пути
                var visitedInThisPath = new HashSet<uint>();

                while (currentAddress < fileLength && instructionsShown < MAX_INSTRUCTIONS && !shouldStop)
                {
                    // Смягчена проверка: если адрес уже посещался, но это не цикл, всё равно анализируем
                    if (visitedInThisPath.Contains(currentAddress) && !loopDetected)
                    {
                        // Просто логируем, но не прерываем
                        Debug.WriteLine($"    Повторное посещение адреса 0x{currentAddress:X4}, продолжаем анализ...");
                    }
                    visitedInThisPath.Add(currentAddress);

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

                        // Поиск текстов, изменений статистики монстров и информации о битве
                        FindTextsInInstruction(insn, br, registerTracker, depth, result.FoundTexts);
                        FindMonsterStatChanges(insn, br, registerTracker, depth, result);
                        FindMonsterBattleInfo(insn, br, registerTracker, depth, result);
                        TrackRegisterOperations(insn, br, registerTracker, depth);

                        // Если дошли до адреса условного перехода и переход ещё не был выполнен
                        if (insn.Address == jumpAddress && !jumpTaken)
                        {
                            Debug.WriteLine($"      Достигнут целевой переход по адресу 0x{insn.Address:X4}, переходим на 0x{alternativeStartAddress:X4}");
                            jumpTaken = true;
                            currentAddress = alternativeStartAddress;
                            break;
                        }

                        // ===== ОБНАРУЖЕНИЕ ЦИКЛА =====
                        if ((mnemonicUpper == "JC" || mnemonicUpper == "JB") &&
                            instructionBytes.Length >= 4 &&
                            instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                            instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            // Проверяем, что это переход назад (цикл)
                            if (jumpTarget < currentAddress)
                            {
                                if (!result.IsIndeterminateLoop)
                                {
                                    result.IsIndeterminateLoop = true;
                                    result.IsInLoop = true;
                                    result.LoopStartAddress = jumpTarget;
                                    loopDetected = true;
                                }

                                loopIterationCount++;
                                result.LoopIterationCount = loopIterationCount;

                                string blValueStr = "unknown";
                                if (registerTracker.TryGetRegisterValue("BL", out ushort blValue))
                                {
                                    blValueStr = $"0x{blValue:X2}";
                                }

                                Debug.WriteLine($"    ИТЕРАЦИЯ ЦИКЛА {loopIterationCount} по адресу 0x{insn.Address:X4} (BL={blValueStr})");

                                if (loopIterationCount < MAX_LOOP_ITERATIONS)
                                {
                                    currentAddress = jumpTarget;
                                    break;
                                }
                                else
                                {
                                    Debug.WriteLine($"    Достигнут лимит итераций цикла ({MAX_LOOP_ITERATIONS}), останавливаемся");
                                    shouldStop = true;
                                    break;
                                }
                            }
                        }
                        else if (mnemonicUpper.StartsWith("J") &&
                                 !mnemonicUpper.StartsWith("JMP") &&
                                 !mnemonicUpper.StartsWith("JECXZ"))
                        {
                            // Обычный условный переход - не следуем по нему, продолжаем линейно
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (jumpTarget != 0 && jumpTarget < fileLength)
                            {
                                // Просто логируем
                                Debug.WriteLine($"      Условный переход {mnemonicUpper} к 0x{jumpTarget:X4} (игнорируем, продолжаем линейно)");
                            }
                            currentAddress = nextAddress;
                            break;
                        }

                        // Обработка вызовов подпрограмм
                        if (mnemonicUpper.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, fileLength);
                            if (callTarget < fileLength && callTarget != 0 && !analyzedAddresses.Contains(callTarget))
                            {
                                Debug.WriteLine($"      Анализ подпрограммы по адресу 0x{callTarget:X4}");
                                AnalyzeCallsWithAlternativeBranch(br, callTarget, 0, 0, analyzedAddresses,
                                    result, registerTracker, depth + 1, callDepth + 1);
                            }
                            currentAddress = nextAddress;
                            break;
                        }

                        // Возврат из подпрограммы
                        if (mnemonicUpper == "RET" || mnemonicUpper == "RETF")
                        {
                            Debug.WriteLine($"      RET на 0x{insn.Address:X4} - конец пути");
                            shouldStop = true;
                            break;
                        }

                        // Безусловный переход
                        if (mnemonicUpper == "JMP")
                        {
                            uint jumpTarget = GetInstructionTargetAddress(insn, fileLength);

                            if (jumpTarget >= fileLength)
                            {
                                Debug.WriteLine($"      JMP за пределы оверлея (0x{jumpTarget:X4}) - конец пути");
                                shouldStop = true;
                                break;
                            }

                            if (jumpTarget < fileLength && jumpTarget != 0)
                            {
                                Debug.WriteLine($"      JMP к 0x{jumpTarget:X4}");
                                currentAddress = jumpTarget;
                                break;
                            }
                        }

                        // По умолчанию - переходим к следующей инструкции
                        currentAddress = nextAddress;
                    }
                }

                if (loopDetected)
                {
                    Debug.WriteLine($"    ЦИКЛ ЗАВЕРШЁН: всего итераций = {loopIterationCount}");
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
            return jumpAddress != 0 && alternativeStartAddress > jumpAddress;
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

            // ===== ОБНАРУЖЕНИЕ ЦИКЛА (ИСПРАВЛЕНО) =====
            // Инструкция CMP [0x3C1D], ... или другие паттерны сравнения с адресом памяти
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x3A && instructionBytes[1] == 0x1E &&
                instructionBytes[2] == 0x1D && instructionBytes[3] == 0x3C)
            {
                result.IsIndeterminateLoop = true;
                result.IsInLoop = true; // <--- ВАЖНО: устанавливаем оба флага!
                result.LoopStartAddress = address;

                // Помечаем всех существующих монстров как неопределённых (из цикла)
                foreach (var key in result.BattleMonsterEntries.Keys.ToList())
                {
                    if (key > 0)
                    {
                        var entry = result.BattleMonsterEntries[key];
                        result.BattleMonsterEntries[key] = (entry.val1, entry.val2, true);
                    }
                }

                Debug.WriteLine($"    ОБНАРУЖЕН НЕОПРЕДЕЛЁННЫЙ ЦИКЛ по адресу 0x{address:X4}");
            }

            // ===== ЗАГРУЗКА ИЗ ТАБЛИЦ CDA9+ / CDB1+ =====

            // MOV AL, [BX + CDA9] (загрузка первого байта из таблицы CDA9+)
            if (instructionBytes.Length >= 4 &&
                instructionBytes[0] == 0x8A &&
                instructionBytes[1] == 0x87 &&
                instructionBytes[2] == 0xA9 && instructionBytes[3] == 0xCD)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    ushort sourceAddr = (ushort)(0xCDA9 + bxValue);

                    // Вычисляем смещение в файле, используя TextBaseAddr
                    long fileOffset = sourceAddr - _config.TextBaseAddr;

                    byte actualValue = 0;
                    bool readSuccess = false;

                    // Пытаемся прочитать фактическое значение из файла
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

                    // Сохраняем значение в регистр (если прочитали успешно - actualValue, иначе 0)
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

            // MOV BP, [BX + CDB1] (загрузка второго байта в BP из таблицы CDB1+)
            else if (instructionBytes.Length >= 4 &&
                     instructionBytes[0] == 0x8B &&
                     instructionBytes[1] == 0xAF &&
                     instructionBytes[2] == 0xB1 && instructionBytes[3] == 0xCD)
            {
                if (registerTracker.TryGetRegisterValue("BX", out ushort bxValue))
                {
                    ushort sourceAddr = (ushort)(0xCDB1 + bxValue);

                    // Вычисляем смещение в файле, используя TextBaseAddr
                    long fileOffset = sourceAddr - _config.TextBaseAddr;

                    ushort actualValue = 0;
                    bool readSuccess = false;

                    // Пытаемся прочитать фактическое значение из файла (16 бит)
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

                    // Сохраняем значение в регистр
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

            // MOV CX, BP (копирование из BP в CX)
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

            // ===== ПОЛНОСТЬЮ ОПРЕДЕЛЁННЫЕ БИТВЫ =====

            // MOV [BX + 0x3C58], AL
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
                        bool isIndeterminate = (bxValue > 0 && result.IsIndeterminateLoop);

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
                            // Обычная полная битва
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (val1, existing.val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (val1, 0, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = AL (BX={bxValue}, val1={val1:X2})");
                        }
                    }
                }
            }

            // MOV [BX + 0x3C29], CL
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
                            // Обычная полная битва
                            if (result.BattleMonsterEntries.ContainsKey(bxValue))
                            {
                                var existing = result.BattleMonsterEntries[bxValue];
                                result.BattleMonsterEntries[bxValue] = (existing.val1, val2, isIndeterminate);
                            }
                            else
                            {
                                result.BattleMonsterEntries[bxValue] = (0, val2, isIndeterminate);
                            }

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = CL (BX={bxValue}, val2={val2:X2})");
                        }
                    }
                }
            }

            // MOV [BX + 0x3C58], DL
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

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = DL (BX={bxValue}, val1={val1:X2})");
                        }
                    }
                }
            }

            // MOV [BX + 0x3C29], DL
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

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = DL (BX={bxValue}, val2={val2:X2})");
                        }
                    }
                }
            }

            // MOV [BX + 0x3C58], BL
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

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C58] = BL (BX={bxValue}, val1={val1:X2})");
                        }
                    }
                }
            }

            // MOV [BX + 0x3C29], BL
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

                            Debug.WriteLine($"    ПОЛНАЯ БИТВА: [BX+3C29] = BL (BX={bxValue}, val2={val2:X2})");
                        }
                    }
                }
            }

            // MOV [0x3C58], AL
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

                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C58] = AL (val1={alValue:X2})");
                    }
                }
            }

            // MOV [0x3C29], AL
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

                        Debug.WriteLine($"    ПОЛНАЯ БИТВА (прямая): [3C29] = AL (val2={alValue:X2})");
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

        // ========== МЕТОДЫ ДЛЯ АНАЛИЗА МАКРОСОВ ==========

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
                    int bytesToRead = (int)Math.Min(64, fileLength - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);

                    var disassembled = capstone.Disassemble(chunk, currentAddress);
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

        private List<(uint CompareAddress, ushort MemAddr, byte Value, uint JumpTarget, MacroExecutionResult MacroResult)>
    FindAllSignificantMacros(BinaryReader br, List<X86Instruction> allInstructions)
        {
            var significantMacros = new List<(uint, ushort, byte, uint, MacroExecutionResult)>();

            for (int i = 0; i < allInstructions.Count; i++)
            {
                var insn = allInstructions[i];

                // Ищем сравнения с непосредственным значением
                if (IsCompareWithImmediate(insn, out byte immValue, out string reg))
                {
                    // Определяем, откуда взялось значение в регистре
                    ushort? sourceAddr = FindMemorySourceForRegister(allInstructions, i, reg);
                    if (!sourceAddr.HasValue) continue;

                    // Проверяем, что это один из наших адресов
                    if (sourceAddr.Value != 0x3C38 && sourceAddr.Value != 0x3C39 && sourceAddr.Value != 0x3C3A)
                        continue;

                    // Смотрим следующую инструкцию - должен быть условный переход
                    if (i + 1 >= allInstructions.Count) continue;

                    var nextInsn = allInstructions[i + 1];
                    if (!IsConditionalJump(nextInsn, out uint jumpTarget)) continue;

                    Debug.WriteLine($"  Анализ макроса: 0x{insn.Address:X4} [{sourceAddr.Value:X4}]={immValue} -> 0x{jumpTarget:X4}");

                    // Эмулируем ВЕСЬ макрос, начиная с адреса перехода
                    var macroResult = EmulateMacro(br, jumpTarget, sourceAddr.Value, immValue);

                    // Проверяем, есть ли значимый код в любой из веток
                    bool hasSignificantCode = macroResult.CommonResult.HasSignificantCode;
                    int totalTexts = macroResult.CommonResult.Texts.Count;
                    int totalMonsters = macroResult.CommonResult.BattleMonsters.Count;

                    // Проходим по всем веткам и собираем результаты
                    foreach (var branch in macroResult.BranchResults)
                    {
                        if (branch.Value.HasSignificantCode)
                        {
                            hasSignificantCode = true;
                            totalTexts += branch.Value.Texts.Count;
                            totalMonsters += branch.Value.BattleMonsters.Count;

                            // Добавляем результат ветки к общему результату
                            macroResult.CommonResult = MergeResults(macroResult.CommonResult, branch.Value);

                            Debug.WriteLine($"    Ветка {branch.Key} имеет значимый код: {branch.Value.Texts.Count} текстов, {branch.Value.BattleMonsters.Count} монстров");
                        }
                    }

                    // Убеждаемся, что CommonResult.HasSignificantCode отражает наличие значимого кода
                    if (hasSignificantCode)
                    {
                        macroResult.CommonResult.HasSignificantCode = true;
                        significantMacros.Add(((uint)insn.Address, sourceAddr.Value, immValue, jumpTarget, macroResult));
                        Debug.WriteLine($"    -> ЗНАЧИМЫЙ МАКРОС (всего {totalTexts} текстов, {totalMonsters} монстров)");
                    }
                    else
                    {
                        Debug.WriteLine($"    -> незначимый макрос");
                    }
                }
            }

            return significantMacros;
        }

        private MacroExecutionResult EmulateMacro(BinaryReader br, uint startAddress, ushort memAddr, byte compareValue)
        {
            var macroResult = new MacroExecutionResult();

            // Находим все возможные пути выполнения
            var paths = FindAllExecutionPaths(br, startAddress);

            // Группируем пути по целевому адресу
            var pathsByTarget = paths.GroupBy(p => p.StartAddress).ToDictionary(g => g.Key, g => g.ToList());

            // Для каждого уникального целевого адреса эмулируем путь один раз
            foreach (var targetAddr in pathsByTarget.Keys)
            {
                var path = pathsByTarget[targetAddr].First();

                // Определяем все условия, которые ведут на этот адрес
                var conditions = pathsByTarget[targetAddr]
                    .Where(p => p.Condition != null)
                    .Select(p => DeterminePathCondition(p, compareValue, memAddr, p.SourceAddress))
                    .Where(c => c != null)
                    .ToList();

                // Эмулируем выполнение пути
                var state = new ExecutionState();
                var result = ExecutePath(br, targetAddr, state);

                // Если есть условия, сохраняем результат для каждого условия
                if (conditions.Count > 0)
                {
                    foreach (var condition in conditions)
                    {
                        macroResult.BranchResults[condition] = result;
                        Debug.WriteLine($"      Ветка {condition} -> {result.Texts.Count} текстов, {result.BattleMonsters.Count} монстров");
                    }
                }
                else
                {
                    // Если нет условий, добавляем к общему результату
                    macroResult.CommonResult = MergeResults(macroResult.CommonResult, result);
                }
            }

            // Также эмулируем основной путь (линейное выполнение от startAddress)
            var mainState = new ExecutionState();
            var mainResult = ExecutePath(br, startAddress, mainState);
            macroResult.CommonResult = MergeResults(macroResult.CommonResult, mainResult);

            return macroResult;
        }

        private ConditionRange DeterminePathCondition(ExecutionPath path, byte compareValue, ushort memAddr, uint sourceAddress)
        {
            if (path.Condition == null) return null;

            string mnemonic = path.Condition.Mnemonic.ToUpper();

            // Для данного конкретного случая определяем точные диапазоны по адресам
            if (sourceAddress == 0x02F9) // JZ после CMP AL, 0x3
            {
                return new ConditionRange { Min = 3, Max = 3, IsX = false }; // только X=3
            }
            else if (sourceAddress == 0x02FB) // JC после CMP AL, 0x3
            {
                return new ConditionRange { Min = 0, Max = 2, IsX = false }; // X < 3
            }
            else if (sourceAddress == 0x02FF) // JNC после CMP AL, 0x9
            {
                return new ConditionRange { Min = 9, Max = 255, IsX = false }; // X >= 9
            }

            // Базовая логика на основе мнемоники для остальных случаев
            switch (mnemonic)
            {
                case "JZ":
                case "JE":
                    return new ConditionRange { Min = compareValue, Max = compareValue, IsX = false };

                case "JNZ":
                case "JNE":
                    return new ConditionRange { Min = 0, Max = 255, IsX = false };

                case "JB":
                case "JNAE":
                case "JC":
                    return new ConditionRange { Min = 0, Max = (byte)(compareValue - 1), IsX = false };

                case "JBE":
                case "JNA":
                    return new ConditionRange { Min = 0, Max = compareValue, IsX = false };

                case "JA":
                case "JNBE":
                    return new ConditionRange { Min = (byte)(compareValue + 1), Max = 255, IsX = false };

                case "JAE":
                case "JNB":
                case "JNC":
                    return new ConditionRange { Min = compareValue, Max = 255, IsX = false };

                default:
                    return null;
            }
        }

        private List<ExecutionPath> FindAllExecutionPaths(BinaryReader br, uint startAddress)
        {
            var paths = new List<ExecutionPath>();
            var visitedAddresses = new HashSet<uint>();
            var workList = new Queue<uint>();
            var pathSources = new Dictionary<uint, uint>(); // адрес -> адрес источника (условного перехода)

            workList.Enqueue(startAddress);
            pathSources[startAddress] = 0;

            while (workList.Count > 0)
            {
                uint currentAddress = workList.Dequeue();

                if (visitedAddresses.Contains(currentAddress))
                    continue;

                using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
                {
                    capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                    uint addr = currentAddress;
                    while (addr < br.BaseStream.Length)
                    {
                        if (visitedAddresses.Contains(addr))
                            break;
                        visitedAddresses.Add(addr);

                        int bytesToRead = (int)Math.Min(32, br.BaseStream.Length - addr);
                        byte[] chunk = ReadBytesAt(br, addr, bytesToRead);
                        var instructions = capstone.Disassemble(chunk, addr);

                        if (instructions == null || instructions.Length == 0)
                        {
                            addr++;
                            continue;
                        }

                        foreach (var insn in instructions)
                        {
                            string mnemonic = insn.Mnemonic.ToUpper();

                            // Нашли условный переход - добавляем целевую ветку в очередь
                            if (IsConditionalJump(insn, out uint jumpTarget))
                            {
                                if (!visitedAddresses.Contains(jumpTarget) && !workList.Contains(jumpTarget))
                                {
                                    workList.Enqueue(jumpTarget);
                                    pathSources[jumpTarget] = (uint)insn.Address;
                                    paths.Add(new ExecutionPath
                                    {
                                        StartAddress = jumpTarget,
                                        Condition = insn,
                                        SourceAddress = (uint)insn.Address
                                    });
                                }

                                // Продолжаем линейно
                                addr = (uint)(insn.Address + insn.Bytes.Length);
                                break;
                            }

                            // Безусловный переход
                            if (mnemonic == "JMP")
                            {
                                uint target = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                                if (target < br.BaseStream.Length)
                                {
                                    if (!visitedAddresses.Contains(target) && !workList.Contains(target))
                                    {
                                        workList.Enqueue(target);
                                        pathSources[target] = (uint)insn.Address;
                                        paths.Add(new ExecutionPath
                                        {
                                            StartAddress = target,
                                            Condition = null,
                                            SourceAddress = (uint)insn.Address
                                        });
                                    }
                                }
                                addr = (uint)(insn.Address + insn.Bytes.Length);
                                break;
                            }

                            // RET - конец пути
                            if (mnemonic == "RET" || mnemonic == "RETF")
                            {
                                addr = (uint)(insn.Address + insn.Bytes.Length);
                                break;
                            }

                            addr = (uint)(insn.Address + insn.Bytes.Length);
                        }
                    }
                }
            }

            return paths;
        }

        private ConditionRange DeterminePathCondition(ExecutionPath path, byte compareValue, ushort memAddr)
        {
            if (path.Condition == null) return null;

            string mnemonic = path.Condition.Mnemonic.ToUpper();
            uint sourceAddr = path.SourceAddress;

            // Анализируем контекст вокруг условного перехода
            // Для данного конкретного случая:
            // 0x02F7: CMP AL, 0x3
            // 0x02F9: JZ 0x0348  (ветка для X=3)
            // 0x02FB: JC 0x0342  (ветка для X<3)
            // 0x02FD: CMP AL, 0x9
            // 0x02FF: JNC 0x0348 (ветка для X>=9)

            // Определяем, какое это сравнение по счету
            if (sourceAddr == 0x02F9) // JZ после первого сравнения
            {
                return new ConditionRange { Min = compareValue, Max = compareValue };
            }
            else if (sourceAddr == 0x02FB) // JC после первого сравнения
            {
                return new ConditionRange { Min = 0, Max = (byte)(compareValue - 1) };
            }
            else if (sourceAddr == 0x02FF) // JNC после второго сравнения
            {
                return new ConditionRange { Min = 9, Max = 255 }; // для X >= 9
            }

            // Базовая логика на основе мнемоники
            switch (mnemonic)
            {
                case "JZ":
                case "JE":
                    return new ConditionRange { Min = compareValue, Max = compareValue };

                case "JNZ":
                case "JNE":
                    return new ConditionRange { Min = 0, Max = 255 };

                case "JB":
                case "JNAE":
                case "JC":
                    return new ConditionRange { Min = 0, Max = (byte)(compareValue - 1) };

                case "JBE":
                case "JNA":
                    return new ConditionRange { Min = 0, Max = compareValue };

                case "JA":
                case "JNBE":
                    return new ConditionRange { Min = (byte)(compareValue + 1), Max = 255 };

                case "JAE":
                case "JNB":
                case "JNC":
                    return new ConditionRange { Min = compareValue, Max = 255 };

                default:
                    return null;
            }
        }


        private CodeEmulationResult ExecutePath(BinaryReader br, uint startAddress, ExecutionState state)
        {
            var result = new CodeEmulationResult();
            bool hasSignificantCodeInPath = false;
            int instructionCount = 0;

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;

                uint currentAddress = startAddress;
                int maxInstructions = 1000;
                int insnCount = 0;

                while (currentAddress < br.BaseStream.Length && insnCount < maxInstructions)
                {
                    if (state.VisitedAddresses.Contains(currentAddress))
                        break;
                    state.VisitedAddresses.Add(currentAddress);

                    int bytesToRead = (int)Math.Min(32, br.BaseStream.Length - currentAddress);
                    byte[] chunk = ReadBytesAt(br, currentAddress, bytesToRead);
                    var instructions = capstone.Disassemble(chunk, currentAddress);

                    if (instructions == null || instructions.Length == 0)
                    {
                        currentAddress++;
                        continue;
                    }

                    foreach (var insn in instructions)
                    {
                        insnCount++;
                        instructionCount++;

                        // Любая инструкция - это уже значимый код
                        hasSignificantCodeInPath = true;

                        // Отслеживаем регистры
                        TrackRegisters(insn, state.Registers);

                        // Извлекаем действия
                        if (IsTextStore(insn, state.Registers, out string text))
                        {
                            result.Texts.Add(text);
                            Debug.WriteLine($"      Текст: {text}");
                        }

                        if (IsMonsterStatChange(insn, state.Registers, out byte? power, out byte? level))
                        {
                            if (power.HasValue)
                            {
                                result.MonsterPower = power;
                                Debug.WriteLine($"      Сила монстров: {power}");
                            }
                            if (level.HasValue)
                            {
                                result.MonsterLevel = level;
                                Debug.WriteLine($"      Уровень монстров: {level}");
                            }
                        }

                        if (IsBattleMonsterStore(insn, state.Registers, out int index, out byte val1, out byte val2, out bool isIndeterminate))
                        {
                            result.BattleMonsters.Add((index, val1, val2, isIndeterminate));
                            Debug.WriteLine($"      Монстр: index={index}, val1={val1}, val2={val2}, indeterminate={isIndeterminate}");
                        }

                        if (IsDirectionByteStore(insn, state.Registers, out byte dirByte))
                        {
                            result.DirectionByte = dirByte;
                            Debug.WriteLine($"      DirectionByte: 0x{dirByte:X2}");
                        }

                        string mnemonic = insn.Mnemonic.ToUpper();

                        // RET - конец пути
                        if (mnemonic == "RET" || mnemonic == "RETF")
                        {
                            Debug.WriteLine($"      RET на 0x{insn.Address:X4} - конец пути");
                            result.HasSignificantCode = hasSignificantCodeInPath;
                            return result;
                        }

                        // CALL - игнорируем, продолжаем после возврата
                        if (mnemonic.StartsWith("CALL"))
                        {
                            uint callTarget = GetInstructionTargetAddress(insn, br.BaseStream.Length);
                            Debug.WriteLine($"      CALL 0x{callTarget:X4} - игнорируем");
                            currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                            break;
                        }

                        // JMP
                        if (mnemonic == "JMP")
                        {
                            uint target = GetInstructionTargetAddress(insn, br.BaseStream.Length);

                            // JMP за пределы оверлея - конец пути
                            if (target >= br.BaseStream.Length)
                            {
                                Debug.WriteLine($"      JMP за пределы оверлея (0x{target:X4}) - конец пути");
                                result.HasSignificantCode = hasSignificantCodeInPath;
                                return result;
                            }

                            // JMP внутри оверлея - продолжаем по целевому адресу
                            Debug.WriteLine($"      JMP to 0x{target:X4}");
                            currentAddress = target;
                            break;
                        }

                        // Условные переходы - не следуем по ним, продолжаем линейно
                        if (mnemonic.StartsWith("J") && !mnemonic.StartsWith("JMP") && !mnemonic.StartsWith("JECXZ"))
                        {
                            // Просто продолжаем линейно
                        }

                        currentAddress = (uint)(insn.Address + insn.Bytes.Length);
                    }
                }

                // Если дошли до конца оверлея
                if (currentAddress >= br.BaseStream.Length)
                {
                    Debug.WriteLine($"      Достигнут конец оверлея - конец пути");
                }
            }

            result.HasSignificantCode = hasSignificantCodeInPath && instructionCount > 0;
            return result;
        }

        private JumpCondition DetermineJumpCondition(X86Instruction insn)
        {
            string mnemonic = insn.Mnemonic.ToUpper();

            // Пытаемся найти предыдущую инструкцию CMP
            // В реальном коде нужно анализировать контекст

            return new JumpCondition
            {
                Type = mnemonic,
                Target = GetInstructionTargetAddress(insn, 0xFFFF)
            };
        }

        private class JumpCondition
        {
            public string Type { get; set; }
            public uint Target { get; set; }
        }

        private CodeEmulationResult MergeResults(CodeEmulationResult r1, CodeEmulationResult r2)
        {
            if (r2 == null) return r1;
            if (r1 == null) return r2;

            var merged = new CodeEmulationResult();

            // Объединяем тексты
            foreach (var text in r1.Texts) merged.Texts.Add(text);
            foreach (var text in r2.Texts) merged.Texts.Add(text);

            // Берем максимальные значения силы/уровня
            if (r1.MonsterPower.HasValue || r2.MonsterPower.HasValue)
                merged.MonsterPower = Math.Max(r1.MonsterPower ?? 0, r2.MonsterPower ?? 0);

            if (r1.MonsterLevel.HasValue || r2.MonsterLevel.HasValue)
                merged.MonsterLevel = Math.Max(r1.MonsterLevel ?? 0, r2.MonsterLevel ?? 0);

            // Объединяем монстров
            foreach (var m in r1.BattleMonsters) merged.BattleMonsters.Add(m);
            foreach (var m in r2.BattleMonsters) merged.BattleMonsters.Add(m);

            // OR для DirectionByte
            merged.DirectionByte = (byte)(r1.DirectionByte | r2.DirectionByte);

            merged.HasSignificantCode = r1.HasSignificantCode || r2.HasSignificantCode;

            return merged;
        }

        private bool IsCompareWithImmediate(X86Instruction insn, out byte immValue, out string reg)
        {
            immValue = 0;
            reg = "";

            byte[] bytes = insn.Bytes;

            // CMP AL, imm8 (3C imm)
            if (bytes.Length >= 2 && bytes[0] == 0x3C)
            {
                immValue = bytes[1];
                reg = "AL";
                return true;
            }

            // CMP CL, imm8 (80 F9 imm)
            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xF9)
            {
                immValue = bytes[2];
                reg = "CL";
                return true;
            }

            // CMP DL, imm8 (80 FA imm)
            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xFA)
            {
                immValue = bytes[2];
                reg = "DL";
                return true;
            }

            // CMP BL, imm8 (80 FB imm)
            if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == 0xFB)
            {
                immValue = bytes[2];
                reg = "BL";
                return true;
            }

            return false;
        }

        private ushort? FindMemorySourceForRegister(List<X86Instruction> allInstructions, int currentIndex, string reg)
        {
            // Ищем максимум 20 инструкций назад
            int start = Math.Max(0, currentIndex - 20);

            for (int i = currentIndex - 1; i >= start; i--)
            {
                var insn = allInstructions[i];
                byte[] bytes = insn.Bytes;

                // MOV reg, [addr]
                if (reg == "AL" && bytes.Length >= 3 && bytes[0] == 0xA0)
                {
                    return BitConverter.ToUInt16(bytes, 1);
                }

                // MOV CL, [addr] (8A 0E addr)
                if (reg == "CL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x0E)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                // MOV DL, [addr] (8A 16 addr)
                if (reg == "DL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x16)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                // MOV BL, [addr] (8A 1E addr)
                if (reg == "BL" && bytes.Length >= 4 && bytes[0] == 0x8A && bytes[1] == 0x1E)
                {
                    return BitConverter.ToUInt16(bytes, 2);
                }

                // Если встретили другую запись в этот регистр, останавливаемся
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
                case "AL": return bytes.Length >= 2 && (bytes[0] & 0xF8) == 0xB0; // MOV AL, imm
                case "CL": return bytes.Length >= 2 && bytes[0] == 0xB1; // MOV CL, imm
                case "DL": return bytes.Length >= 2 && bytes[0] == 0xB2; // MOV DL, imm
                case "BL": return bytes.Length >= 2 && bytes[0] == 0xB3; // MOV BL, imm
                default: return false;
            }
        }

        private bool IsConditionalJump(X86Instruction insn, out uint target)
        {
            target = 0;
            string mnemonic = insn.Mnemonic.ToUpper();

            // Список условных переходов
            string[] jumps = { "JZ", "JE", "JNZ", "JNE", "JB", "JNAE", "JC",
                               "JNB", "JAE", "JNC", "JBE", "JNA", "JA", "JNBE" };

            if (jumps.Contains(mnemonic))
            {
                target = GetInstructionTargetAddress(insn, 0xFFFF);
                return target != 0;
            }
            return false;
        }

        private void TrackRegisters(X86Instruction insn, Dictionary<string, ushort> registers)
        {
            byte[] bytes = insn.Bytes;

            // MOV reg, imm16
            if (bytes.Length >= 3 && (bytes[0] & 0xF8) == 0xB8)
            {
                ushort value = BitConverter.ToUInt16(bytes, 1);
                byte regIndex = (byte)(bytes[0] - 0xB8);
                string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };
                if (regIndex < regNames.Length)
                {
                    registers[regNames[regIndex]] = value;
                }
            }

            // MOV reg, imm8
            if (bytes.Length >= 2 && (bytes[0] & 0xF8) == 0xB0)
            {
                byte value = bytes[1];
                byte regIndex = (byte)(bytes[0] - 0xB0);
                string[] regNames = { "AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH" };
                if (regIndex < regNames.Length)
                {
                    registers[regNames[regIndex]] = value;
                }
            }
        }

        private bool IsTextStore(X86Instruction insn, Dictionary<string, ushort> registers, out string text)
        {
            text = null;
            byte[] bytes = insn.Bytes;

            // MOV [0x3BD4], imm16
            if (bytes.Length >= 6 && bytes[0] == 0xC7 && bytes[1] == 0x06 &&
                bytes[2] == 0xD4 && bytes[3] == 0x3B)
            {
                ushort textAddr = BitConverter.ToUInt16(bytes, 4);
                text = $"Text at 0x{textAddr:X4}";
                return true;
            }

            // MOV [0x3BD4], reg
            if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x06 &&
                bytes[2] == 0xD4 && bytes[3] == 0x3B)
            {
                byte modRM = bytes[1];
                byte regField = (byte)((modRM >> 3) & 0x07);
                string[] regNames = { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" };

                if (regField < regNames.Length && registers.TryGetValue(regNames[regField], out ushort value))
                {
                    text = $"Text at 0x{value:X4} (via {regNames[regField]})";
                    return true;
                }
            }

            return false;
        }

        private bool IsMonsterStatChange(X86Instruction insn, Dictionary<string, ushort> registers, out byte? power, out byte? level)
        {
            power = null;
            level = null;
            byte[] bytes = insn.Bytes;

            // MOV [0xC96F], imm (сила)
            if (bytes.Length >= 5 && bytes[0] == 0xC6 && bytes[1] == 0x06 &&
                bytes[2] == 0x6F && bytes[3] == 0xC9)
            {
                power = bytes[4];
                return true;
            }

            // MOV [0xC961], imm (уровень)
            if (bytes.Length >= 5 && bytes[0] == 0xC6 && bytes[1] == 0x06 &&
                bytes[2] == 0x61 && bytes[3] == 0xC9)
            {
                level = bytes[4];
                return true;
            }

            return false;
        }

        private bool IsBattleMonsterStore(X86Instruction insn, Dictionary<string, ushort> registers, out int index, out byte val1, out byte val2, out bool isIndeterminate)
        {
            index = 0;
            val1 = 0;
            val2 = 0;
            isIndeterminate = false;

            byte[] bytes = insn.Bytes;

            // MOV [BX + 0x3C58], AL
            if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x87 &&
                bytes[2] == 0x58 && bytes[3] == 0x3C)
            {
                if (registers.TryGetValue("BX", out ushort bxValue) &&
                    registers.TryGetValue("AL", out ushort alValue))
                {
                    index = bxValue;
                    val1 = (byte)alValue;
                    return true;
                }
            }

            // MOV [BX + 0x3C29], CL
            if (bytes.Length >= 4 && bytes[0] == 0x88 && bytes[1] == 0x8F &&
                bytes[2] == 0x29 && bytes[3] == 0x3C)
            {
                if (registers.TryGetValue("BX", out ushort bxValue) &&
                    registers.TryGetValue("CL", out ushort clValue))
                {
                    index = bxValue;
                    val2 = (byte)clValue;
                    return true;
                }
            }

            return false;
        }

        private bool IsDirectionByteStore(X86Instruction insn, Dictionary<string, ushort> registers, out byte dirByte)
        {
            dirByte = 0;
            // Пока заглушка - в реальности нужно анализировать конкретные паттерны
            return false;
        }
    }
}