using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    public class OvrFileAnalyzer
    {
        private readonly OvrFileConfig _config;
        private readonly MacroAnalyzer _macroAnalyzer;
        private readonly InstructionAnalyzer _instructionAnalyzer;
        private readonly CodeExecutor _codeExecutor;
        private readonly PathAnalyzer _pathAnalyzer;

        public OvrFileAnalyzer(OvrFileConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _instructionAnalyzer = new InstructionAnalyzer(config);
            _codeExecutor = new CodeExecutor(config, _instructionAnalyzer);
            _pathAnalyzer = new PathAnalyzer(config, _codeExecutor);
            _macroAnalyzer = new MacroAnalyzer(config);
        }

        public static List<OvrObject> AnalyzeOvrFile(string filename, OvrFileConfig config,
            Dictionary<Point, string> existingCentralOptions)
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

                // Чтение табличных объектов
                var tableReachableAddresses = new HashSet<uint>();
                var tableObjects = ReadTableObjects(br, tableReachableAddresses);
                objects.AddRange(tableObjects);

                var tableObjectCoords = new HashSet<string>();
                foreach (var obj in tableObjects)
                {
                    string coordKey = $"{obj.X},{obj.Y}";
                    tableObjectCoords.Add(coordKey);
                }

                // Дизассемблирование всего файла
                fs.Seek(0, SeekOrigin.Begin);
                var allInstructions = DisassembleAll(br);

                // Поиск макросов
                var comparisons = _macroAnalyzer.FindAllCoordinateComparisons(br, allInstructions);
                AnalysisDebug.WriteLine($"Найдено сравнений координат: {comparisons.Count}");

                comparisons = FilterComparisonsOutsideTableCode(comparisons, tableReachableAddresses);
                AnalysisDebug.WriteLine($"Сравнений координат после исключения кода табличных объектов: {comparisons.Count}");

                // Обработка макросов (второй режим)
                var macroObjects = ProcessMacros(br, comparisons, tableObjectCoords, existingCentralOptions);
                objects.AddRange(macroObjects);

                // Обработка пути по умолчанию (третий режим)
                var defaultPathObjects = ProcessDefaultPath(br, allInstructions, tableObjectCoords, existingCentralOptions, objects);
                objects.AddRange(defaultPathObjects);

                AnalysisDebug.WriteLine($"\nВсего объектов после анализа: {objects.Count}");
            }

            return objects;
        }

        private List<CoordinateComparison> FilterComparisonsOutsideTableCode(
            List<CoordinateComparison> comparisons, HashSet<uint> tableReachableAddresses)
        {
            if (comparisons == null || comparisons.Count == 0 ||
                tableReachableAddresses == null || tableReachableAddresses.Count == 0)
            {
                return comparisons ?? new List<CoordinateComparison>();
            }

            var filtered = new List<CoordinateComparison>();

            foreach (var comparison in comparisons)
            {
                bool compareInTableCode = tableReachableAddresses.Contains(comparison.CompareAddress);
                bool targetInTableCode = tableReachableAddresses.Contains(comparison.JumpTarget);

                if (compareInTableCode || targetInTableCode)
                {
                    AnalysisDebug.WriteLine($"Пропускаем comparison 0x{comparison.CompareAddress:X4} -> 0x{comparison.JumpTarget:X4}: адрес относится к коду табличного объекта");
                    continue;
                }

                filtered.Add(comparison);
            }

            return filtered;
        }

        private List<OvrObject> ReadTableObjects(BinaryReader br, HashSet<uint> tableReachableAddresses)
        {
            var objects = new List<OvrObject>();

            br.BaseStream.Seek(_config.StartAddress, SeekOrigin.Begin);
            byte numObjects = br.ReadByte();

            var coordinates = ReadCoordinates(br, numObjects);
            var directions = ReadDirections(br, numObjects);
            var patchKeys = ReadPatchKeys(br, numObjects);

            for (int i = 0; i < numObjects; i++)
            {
                var obj = ProcessTableObject(br, i + 1, coordinates[i], directions[i], patchKeys[i], tableReachableAddresses);
                obj.IsFromTable = true;
                objects.Add(obj);
            }

            return objects;
        }

        private OvrObject ProcessTableObject(BinaryReader br, int objIndex, Tuple<byte, byte> coords,
            byte direction, ushort patchKey, HashSet<uint> tableReachableAddresses)
        {
            var ovrObject = new OvrObject
            {
                X = coords.Item1,
                Y = coords.Item2,
                DirectionByte = direction
            };

            using (AnalysisDebug.BeginCellScope(ovrObject))
            {
                uint patchAddress = CalculatePatchAddress(patchKey);
                bool debugMode = AnalysisDebug.IsEnabledFor(ovrObject);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n=== ОБРАБОТКА ОБЪЕКТА ({ovrObject.X},{ovrObject.Y}) ===");
                    AnalysisDebug.WriteLine($"PatchAddress: 0x{patchAddress:X4}");
                }

                // Анализ основного пути
                var mainRegisterTracker = new RegisterTracker();
                SetInitialRegistersFromCoordinates(mainRegisterTracker, ovrObject.X, ovrObject.Y, patchAddress);

                var processedBackEdges = new HashSet<(uint From, uint To)>();

                var mainPathResult = _codeExecutor.ExecuteCodeAtAddress(br, patchAddress, mainRegisterTracker,
                    new HashSet<uint>(), 0, 0, 0, ovrObject.X, ovrObject.Y,
                    processedBackEdges, invalidateReturnRegistersAfterExternalCall: true);

                foreach (var visitedAddress in mainPathResult.VisitedAddresses)
                    tableReachableAddresses.Add(visitedAddress);

                // Сбор результатов основного пути
                var (mainLocalTexts, mainContextTexts) = CollectTextsFromResult(mainPathResult);
                MergeMonsterInfo(mainPathResult, ovrObject);

                // Обработка альтернативных путей
                var allResults = new List<PathResult>();
                allResults.Add(CreatePathResult(1, mainLocalTexts, mainContextTexts, mainPathResult.AlternativePaths.Count == 0));

                if (mainPathResult.AlternativePaths.Count > 0)
                {
                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"\nСобрано путей из основного анализа: {mainPathResult.AlternativePaths.Count}");
                        foreach (var path in mainPathResult.AlternativePaths)
                        {
                            AnalysisDebug.WriteLine($"  Путь: 0x{path.Address:X4} -> 0x{path.TargetAddress:X4} ({path.Condition})");
                        }
                    }

                    _pathAnalyzer.ProcessPaths(mainPathResult.AlternativePaths, 1, 0,
                        mainContextTexts, mainLocalTexts,
                        mainPathResult.FirstLocalTextAddress, br,
                        ovrObject.X, ovrObject.Y, allResults, ovrObject, processedBackEdges,
                        invalidateReturnRegistersAfterExternalCall: true,
                        reachableAddresses: tableReachableAddresses);
                }

                // Формирование финальных путей
                var finalOrderedPaths = _pathAnalyzer.BuildFinalPaths(allResults);

                // Сохраняем оба представления
                ovrObject.PathTexts = new Dictionary<int, HashSet<string>>();
                ovrObject.PathTextsOrdered = new Dictionary<int, List<string>>();

                foreach (var kvp in finalOrderedPaths)
                {
                    ovrObject.PathTexts[kvp.Key] = new HashSet<string>(kvp.Value);
                    ovrObject.PathTextsOrdered[kvp.Key] = kvp.Value;
                }

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n    ИТОГО для клетки ({ovrObject.X},{ovrObject.Y}):");
                    AnalysisDebug.WriteLine($"      Всего путей: {ovrObject.PathTextsOrdered.Count}");
                    foreach (var kvp in ovrObject.PathTextsOrdered.OrderBy(k => k.Key))
                    {
                        AnalysisDebug.WriteLine($"      Path {kvp.Key}: {kvp.Value.Count} текстов");
                        foreach (var text in kvp.Value)
                        {
                            AnalysisDebug.WriteLine($"        {text}");
                        }
                    }
                }

                return ovrObject;
            }
        }

        private List<OvrObject> ProcessMacros(BinaryReader br, List<CoordinateComparison> comparisons,
    HashSet<string> tableObjectCoords, Dictionary<Point, string> existingCentralOptions)
        {
            var objects = new List<OvrObject>();

            foreach (var comparison in comparisons)
            {
                AnalysisDebug.WriteLine($"\nАнализ макроса по адресу 0x{comparison.JumpTarget:X4} для [{comparison.MemAddr:X4}]={comparison.Value} ({(comparison.IsLinear ? "линейный" : comparison.JumpType)})");

                bool isX = (comparison.CoordType == CoordType.XCoord);
                bool isY = (comparison.CoordType == CoordType.YCoord);
                bool isFull = (comparison.CoordType == CoordType.FullCoord);

                byte targetX = 0;
                byte targetY = 0;
                if (isFull)
                {
                    targetX = (byte)(comparison.Value & 0x0F);
                    targetY = (byte)(comparison.Value >> 4);
                    AnalysisDebug.WriteLine($"  Полная координата: X={targetX}, Y={targetY} (значение 0x{comparison.Value:X2})");
                }

                // Сбор альтернативных путей
                var alternativePaths = new List<AlternativePath>();
                _macroAnalyzer.CollectAlternativePaths(br, comparison.JumpTarget, alternativePaths, 0);

                // Анализ результатов
                var (allTexts, monsterPower, monsterLevel, lightingLevel, randomEncounterChance, battleMonsterCount, isBattleMonsterCountIndeterminate, battleMonsters, partialBattles, hasPartialBattlePattern) =
    AnalyzeMacroResults(br, comparison, alternativePaths, targetX, targetY);

                bool hasSignificantCode = allTexts.Count > 0 ||
                                          monsterPower.HasValue ||
                                          monsterLevel.HasValue ||
                                          lightingLevel.HasValue ||
                                          randomEncounterChance.HasValue ||
                                          battleMonsters.Count > 0 ||
                                          partialBattles.Count > 0 ||
                                          hasPartialBattlePattern;

                if (!hasSignificantCode && comparison.IsLinear &&
                    (comparison.JumpType.ToUpper() == "JE" || comparison.JumpType.ToUpper() == "JZ") && isFull)
                {
                    hasSignificantCode = true;
                    AnalysisDebug.WriteLine($"  Макрос JE/JZ для полной координаты считается значимым по умолчанию");
                }

                if (!hasSignificantCode)
                {
                    AnalysisDebug.WriteLine("  -> нет значимого кода, пропускаем");
                    continue;
                }

                // Определение целевых клеток
                var targetCells = GetTargetCells(comparison, isX, isY, isFull, targetX, targetY);

                // ИСПРАВЛЕНИЕ: Сначала фильтруем клетки, которые ЕСТЬ в таблице - они НЕ ДОЛЖНЫ обрабатываться макросами
                var cellsNotInTable = targetCells
                    .Where(p => !tableObjectCoords.Contains($"{p.X},{p.Y}"))
                    .ToList();

                // Затем из оставшихся выбираем только те, что помечены как случайные встречи
                var validCells = cellsNotInTable
                    .Where(p => existingCentralOptions.TryGetValue(p, out string option) && option == "Случайная встреча")
                    .ToList();

                if (validCells.Count == 0)
                {
                    AnalysisDebug.WriteLine("  Нет подходящих клеток для применения макроса.");
                    continue;
                }

                AnalysisDebug.WriteLine($"  -> создаём объекты для {validCells.Count} клеток");

                int objectsCreated = 0;
                foreach (var cellPos in validCells)
                {
                    var obj = CreateMacroObject(cellPos, allTexts, monsterPower, monsterLevel, lightingLevel, randomEncounterChance, battleMonsterCount,
                        isBattleMonsterCountIndeterminate, battleMonsters, partialBattles, hasPartialBattlePattern);
                    objects.Add(obj);
                    objectsCreated++;
                }

                AnalysisDebug.WriteLine($"  -> создано объектов из макроса: {objectsCreated}");
            }

            return objects;
        }

        private (HashSet<string> texts, byte? monsterPower, byte? monsterLevel, byte? lightingLevel, byte? randomEncounterChance, byte? battleMonsterCount,
            bool isBattleMonsterCountIndeterminate,
            Dictionary<int, (byte, byte, bool)> battleMonsters,
            List<PartiallyDefinedBattle> partialBattles, bool hasPartialBattlePattern)
            AnalyzeMacroResults(BinaryReader br, CoordinateComparison comparison,
                List<AlternativePath> alternativePaths, byte targetX, byte targetY)
        {
            var allTexts = new HashSet<string>();
            byte? monsterPower = null;
            byte? monsterLevel = null;
            byte? lightingLevel = null;
            byte? randomEncounterChance = null;
            byte? battleMonsterCount = null;
            bool isBattleMonsterCountIndeterminate = false;
            var battleMonsters = new Dictionary<int, (byte val1, byte val2, bool isIndeterminate)>();
            var partialBattles = new List<PartiallyDefinedBattle>();
            var partialBattleInfo = new List<PartialBattleInfo>();
            bool hasPartialBattlePattern = false;

            IDisposable debugScope = comparison.CoordType == CoordType.FullCoord
                ? AnalysisDebug.BeginCellScope(targetX, targetY)
                : null;
            try
            {

            // Анализ основного пути
            var mainRegisterTracker = new RegisterTracker();
            var macroProcessedBackEdges = new HashSet<(uint From, uint To)>();
            var mainPathResult = _codeExecutor.ExecuteCodeAtAddress(br, comparison.JumpTarget, mainRegisterTracker,
                new HashSet<uint>(), 0, 0, 0, targetX, targetY, macroProcessedBackEdges);

            MergeMacroResults(mainPathResult, allTexts, ref monsterPower, ref monsterLevel, ref lightingLevel, ref randomEncounterChance, ref battleMonsterCount,
                ref isBattleMonsterCountIndeterminate, battleMonsters, partialBattles, partialBattleInfo, ref hasPartialBattlePattern);

            // Анализ альтернативных путей
            var processedTargets = new HashSet<uint>();
            foreach (var path in alternativePaths)
            {
                if (processedTargets.Contains(path.TargetAddress))
                    continue;

                processedTargets.Add(path.TargetAddress);

                var pathRegisterTracker = new RegisterTracker();
                var pathResult = _codeExecutor.ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                    new HashSet<uint>(), 0, 0, 0, targetX, targetY);

                MergeMacroResults(pathResult, allTexts, ref monsterPower, ref monsterLevel, ref lightingLevel, ref randomEncounterChance, ref battleMonsterCount,
                    ref isBattleMonsterCountIndeterminate, battleMonsters, partialBattles, partialBattleInfo, ref hasPartialBattlePattern);
            }

            return (allTexts, monsterPower, monsterLevel, lightingLevel, randomEncounterChance, battleMonsterCount, isBattleMonsterCountIndeterminate, battleMonsters, partialBattles, hasPartialBattlePattern);
            }
            finally
            {
                debugScope?.Dispose();
            }
        }

        private List<OvrObject> ProcessDefaultPath(BinaryReader br, List<X86Instruction> allInstructions,
            HashSet<string> tableObjectCoords, Dictionary<Point, string> existingCentralOptions,
            List<OvrObject> existingObjects)
        {
            var objects = new List<OvrObject>();

            uint? defaultPathAddress = _macroAnalyzer.FindDefaultPathAfterTableLoop(allInstructions);

            if (!defaultPathAddress.HasValue)
                return objects;

            using (AnalysisDebug.BeginCellScope(0, 0))
            {
                AnalysisDebug.WriteLine($"\n=== ТРЕТИЙ РЕЖИМ: ОБРАБОТКА ПУТИ ПО УМОЛЧАНИЮ ===");
                AnalysisDebug.WriteLine($"Адрес: 0x{defaultPathAddress.Value:X4}");

                var defaultRegisterTracker = new RegisterTracker();
                var defaultPathResult = _codeExecutor.ExecuteCodeAtAddress(br, defaultPathAddress.Value, defaultRegisterTracker,
                    new HashSet<uint>(), 0, 0, 0, 0, 0);

                // Сбор текстов
                var defaultTexts = new HashSet<string>();
                foreach (var text in defaultPathResult.FoundTexts)
                    defaultTexts.Add(text);
                foreach (var text in defaultPathResult.ContextTexts)
                    defaultTexts.Add(text);

                bool hasSignificantCode = defaultTexts.Count > 0 ||
                                          defaultPathResult.MonsterPower.HasValue ||
                                          defaultPathResult.MonsterLevel.HasValue ||
                                          defaultPathResult.LightingLevel.HasValue ||
                                          defaultPathResult.RandomEncounterChance.HasValue ||
                                          defaultPathResult.BattleMonsterEntries.Count > 0 ||
                                          defaultPathResult.PartialBattles.Count > 0;

                if (!hasSignificantCode)
                {
                    AnalysisDebug.WriteLine($"  Путь по умолчанию не содержит значимого кода");
                    return objects;
                }

                AnalysisDebug.WriteLine($"  Найден значимый код в пути по умолчанию:");
                AnalysisDebug.WriteLine($"    Текстов: {defaultTexts.Count}");
                AnalysisDebug.WriteLine($"    MonsterPower: {defaultPathResult.MonsterPower}");
                AnalysisDebug.WriteLine($"    MonsterLevel: {defaultPathResult.MonsterLevel}");
                AnalysisDebug.WriteLine($"    LightingLevel: {defaultPathResult.LightingLevel}");
                AnalysisDebug.WriteLine($"    RandomEncounterChance: {defaultPathResult.RandomEncounterChance}");
                AnalysisDebug.WriteLine($"    BattleMonsters: {defaultPathResult.BattleMonsterEntries.Count}");

                // Создание объектов для всех подходящих клеток
                int objectsCreated = 0;
                for (byte x = 0; x < 16; x++)
                {
                    for (byte y = 0; y < 16; y++)
                    {
                        var cellPos = new Point(x, y);
                        string coordKey = $"{x},{y}";

                        if (tableObjectCoords.Contains(coordKey))
                            continue;

                        bool isRandomEncounter = existingCentralOptions.TryGetValue(cellPos, out string option) &&
                                                 option == "Случайная встреча";

                        if (!isRandomEncounter)
                            continue;

                        bool alreadyExists = existingObjects.Any(o => o.X == x && o.Y == y);
                        if (alreadyExists)
                            continue;

                        var obj = CreateDefaultObject(cellPos, defaultPathResult, defaultTexts);
                        objects.Add(obj);
                        objectsCreated++;
                    }
                }

                AnalysisDebug.WriteLine($"  Создано объектов из третьего режима: {objectsCreated}");
                return objects;
            }
        }

        // Вспомогательные методы
        /// <summary>
        /// Читает координаты объектов из таблицы
        /// </summary>
        private Tuple<byte, byte>[] ReadCoordinates(BinaryReader br, int count)
        {
            var coords = new Tuple<byte, byte>[count];
            for (int i = 0; i < count; i++)
            {
                byte coordByte = br.ReadByte();
                byte y = (byte)(coordByte >> 4);    // старшие 4 бита - Y
                byte x = (byte)(coordByte & 0x0F);  // младшие 4 бита - X
                coords[i] = Tuple.Create(x, y);
            }
            return coords;
        }

        /// <summary>
        /// Читает направления объектов из таблицы
        /// </summary>
        private byte[] ReadDirections(BinaryReader br, int count)
        {
            var directions = new byte[count];
            for (int i = 0; i < count; i++)
            {
                directions[i] = br.ReadByte();
            }
            return directions;
        }

        /// <summary>
        /// Читает ключи патчей из таблицы
        /// </summary>
        private ushort[] ReadPatchKeys(BinaryReader br, int count)
        {
            var keys = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = br.ReadUInt16();
            }
            return keys;
        }
        private uint CalculatePatchAddress(ushort key) => (uint)(key + _config.PatchBase) & 0xFFFF;

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

            AnalysisDebug.WriteLine($"Дизассемблировано инструкций: {instructions.Count}");
            return instructions;
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

            using (var capstone = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16))
            {
                capstone.DisassembleSyntax = DisassembleSyntax.Intel;
                instructions = capstone.Disassemble(chunk, address);
            }

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

        private void SetInitialRegistersFromCoordinates(RegisterTracker tracker, byte x, byte y, uint patchStartAddress)
        {
            tracker.SetRegisterValue("BL", x, patchStartAddress, "Initial X coordinate");
            tracker.SetRegisterValue("BH", 0, patchStartAddress, "Initial BH = 0");
            tracker.SetRegisterValue("BX", (ushort)x, patchStartAddress, $"Initial BX = X ({x})");
        }

        private (List<TextEntry> local, List<TextEntry> context) CollectTextsFromResult(PathAnalysisResult result)
        {
            var local = new List<TextEntry>();
            var context = new List<TextEntry>();
            int order = 0;

            foreach (var text in result.ContextTexts)
            {
                context.Add(new TextEntry
                {
                    Text = text,
                    Order = order++,
                    IsContextual = true,
                    Address = result.FirstLocalTextAddress
                });
            }

            foreach (var text in result.FoundTexts)
            {
                local.Add(new TextEntry
                {
                    Text = text,
                    Order = order++,
                    IsContextual = false,
                    Address = result.FirstLocalTextAddress
                });
            }

            return (local, context);
        }

        private PathResult CreatePathResult(int pathId, List<TextEntry> localTexts,
            List<TextEntry> contextTexts, bool isLeaf)
        {
            var combinedTexts = new List<TextEntry>();
            combinedTexts.AddRange(contextTexts);
            combinedTexts.AddRange(localTexts);

            return new PathResult
            {
                PathId = pathId,
                Texts = combinedTexts,
                IsLeaf = isLeaf
            };
        }

        private void MergeMonsterInfo(PathAnalysisResult source, OvrObject target)
        {
            if (source.MonsterPower.HasValue)
                target.MonsterPower = source.MonsterPower.Value;
            if (source.MonsterLevel.HasValue)
                target.MonsterLevel = source.MonsterLevel.Value;
            if (source.LightingLevel.HasValue)
                target.LightingLevel = source.LightingLevel.Value;
            if (source.RandomEncounterChance.HasValue)
                target.RandomEncounterChance = source.RandomEncounterChance.Value;

            if (source.IsBattleMonsterCountIndeterminate)
            {
                target.BattleMonsterCount = null;
                target.IsBattleMonsterCountIndeterminate = true;
            }
            else if (source.BattleMonsterCount.HasValue)
            {
                if (target.IsBattleMonsterCountIndeterminate)
                {
                    // Уже обнаружен хотя бы один неопределённый путь для табличного объекта.
                    // Конкретное значение из другой ветки не должно перетирать x?.
                }
                else if (target.BattleMonsterCount.HasValue && target.BattleMonsterCount.Value != source.BattleMonsterCount.Value)
                {
                    // Разные конкретные значения в разных ветках для табличного объекта считаем неопределёнными.
                    target.BattleMonsterCount = null;
                    target.IsBattleMonsterCountIndeterminate = true;
                }
                else
                {
                    target.BattleMonsterCount = source.BattleMonsterCount.Value;
                    target.IsBattleMonsterCountIndeterminate = false;
                }
            }

            foreach (var entry in source.BattleMonsterEntries)
            {
                if (entry.Value.val1 != 0 || entry.Value.val2 != 0)
                {
                    target.AddBattleMonster(
                        entry.Key,
                        entry.Value.val1,
                        entry.Value.val2,
                        entry.Value.isIndeterminate
                    );
                }
            }

            foreach (var partial in source.PartialBattles)
            {
                target.AddPartiallyDefinedBattle(
                    partial.BxIndex,
                    partial.RangeStart1,
                    partial.RangeEnd1,
                    partial.RangeStart2,
                    partial.RangeEnd2
                );
            }

            if (source.HasPartialBattlePattern)
            {
                target.HasAnyTableLoad = true;
                foreach (var info in source.PartialBattleInfo)
                {
                    target.AddLoadedValue(
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

        private void MergeMacroResults(PathAnalysisResult source, HashSet<string> allTexts,
            ref byte? monsterPower, ref byte? monsterLevel, ref byte? lightingLevel, ref byte? randomEncounterChance, ref byte? battleMonsterCount,
            ref bool isBattleMonsterCountIndeterminate,
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

            if (source.LightingLevel.HasValue)
                lightingLevel = source.LightingLevel;

            if (source.RandomEncounterChance.HasValue)
                randomEncounterChance = source.RandomEncounterChance;

            if (source.BattleMonsterCount.HasValue)
            {
                battleMonsterCount = source.BattleMonsterCount;
                isBattleMonsterCountIndeterminate = false;
            }
            else if (source.IsBattleMonsterCountIndeterminate && !battleMonsterCount.HasValue)
            {
                isBattleMonsterCountIndeterminate = true;
            }

            foreach (var entry in source.BattleMonsterEntries)
            {
                if (!battleMonsters.TryGetValue(entry.Key, out var existing))
                {
                    battleMonsters[entry.Key] = entry.Value;
                    continue;
                }

                bool existingDefined = existing.val1 != 0 && existing.val2 != 0 && !existing.isIndeterminate;
                bool newDefined = entry.Value.val1 != 0 && entry.Value.val2 != 0 && !entry.Value.isIndeterminate;

                if (!existingDefined && newDefined)
                {
                    battleMonsters[entry.Key] = entry.Value;
                }
                else if (existing.isIndeterminate && !entry.Value.isIndeterminate)
                {
                    battleMonsters[entry.Key] = entry.Value;
                }
                else if (!existing.isIndeterminate && entry.Value.isIndeterminate)
                {
                    // Оставляем существующую определённую запись.
                }
                else
                {
                    // При одинаковой степени определённости оставляем последнюю.
                    battleMonsters[entry.Key] = entry.Value;
                }
            }

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

        private List<Point> GetTargetCells(CoordinateComparison comparison, bool isX, bool isY, bool isFull, byte targetX, byte targetY)
        {
            var targetCells = new List<Point>();

            if (comparison.IsLinear)
            {
                AnalysisDebug.WriteLine($"  Линейный макрос для значений [{comparison.LinearStart}-{comparison.LinearEnd}] [{comparison.CoordType}]");

                if (isX)
                {
                    for (byte x = comparison.LinearStart; x <= comparison.LinearEnd && x < 16; x++)
                    {
                        for (byte y = 0; y < 16; y++)
                        {
                            targetCells.Add(new Point(x, y));
                        }
                    }
                }
                else if (isY)
                {
                    for (byte y = comparison.LinearStart; y <= comparison.LinearEnd && y < 16; y++)
                    {
                        for (byte x = 0; x < 16; x++)
                        {
                            targetCells.Add(new Point(x, y));
                        }
                    }
                }
                else if (isFull)
                {
                    // Для полной координаты линейный диапазон обычно не ожидается,
                    // но на всякий случай интерпретируем значение как диапазон packed-координат.
                    for (int value = comparison.LinearStart; value <= comparison.LinearEnd && value <= 0xFF; value++)
                    {
                        byte x = (byte)(value & 0x0F);
                        byte y = (byte)(value >> 4);
                        if (x < 16 && y < 16)
                        {
                            targetCells.Add(new Point(x, y));
                        }
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

            return targetCells;
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

        private OvrObject CreateMacroObject(Point cellPos, HashSet<string> texts,
            byte? monsterPower, byte? monsterLevel, byte? lightingLevel, byte? randomEncounterChance, byte? battleMonsterCount, bool isBattleMonsterCountIndeterminate,
            Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> battleMonsters,
            List<PartiallyDefinedBattle> partialBattles, bool hasPartialBattlePattern)
        {
            var obj = new OvrObject
            {
                X = (byte)cellPos.X,
                Y = (byte)cellPos.Y,
                DirectionByte = 0,
                MonsterPower = monsterPower,
                MonsterLevel = monsterLevel,
                LightingLevel = lightingLevel,
                RandomEncounterChance = randomEncounterChance,
                BattleMonsterCount = battleMonsterCount,
                IsBattleMonsterCountIndeterminate = isBattleMonsterCountIndeterminate,
                IsFromTable = false,
                HasAnyTableLoad = hasPartialBattlePattern
            };

            if (texts.Count > 0)
            {
                obj.PathTexts[0] = new HashSet<string>(texts);
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

            return obj;
        }

        private OvrObject CreateDefaultObject(Point cellPos, PathAnalysisResult result, HashSet<string> texts)
        {
            var obj = new OvrObject
            {
                X = (byte)cellPos.X,
                Y = (byte)cellPos.Y,
                DirectionByte = 0,
                MonsterPower = result.MonsterPower,
                MonsterLevel = result.MonsterLevel,
                LightingLevel = result.LightingLevel,
                RandomEncounterChance = result.RandomEncounterChance,
                BattleMonsterCount = result.BattleMonsterCount,
                IsBattleMonsterCountIndeterminate = result.IsBattleMonsterCountIndeterminate,
                IsFromTable = false
            };

            if (texts.Count > 0)
            {
                obj.PathTexts[0] = new HashSet<string>(texts);
            }

            foreach (var monster in result.BattleMonsterEntries)
            {
                obj.AddBattleMonster(monster.Key, monster.Value.val1,
                                    monster.Value.val2, monster.Value.isIndeterminate);
            }

            foreach (var partial in result.PartialBattles)
            {
                obj.AddPartiallyDefinedBattle(
                    partial.BxIndex,
                    partial.RangeStart1,
                    partial.RangeEnd1,
                    partial.RangeStart2,
                    partial.RangeEnd2
                );
            }

            obj.HasAnyTableLoad = result.HasPartialBattlePattern;
            return obj;
        }
    }
}