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

                var finalVariants = AnalyzeObjectVariants(
                    br,
                    patchAddress,
                    ovrObject.X,
                    ovrObject.Y,
                    predefinedAlternativePaths: null,
                    reachableAddresses: tableReachableAddresses,
                    initializeRegisters: tracker => SetInitialRegistersFromCoordinates(tracker, ovrObject.X, ovrObject.Y, patchAddress));

                PopulateObjectPathData(ovrObject, finalVariants);
                ApplyResolvedVariantInfoToObject(ovrObject);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n    ИТОГО для клетки ({ovrObject.X},{ovrObject.Y}):");
                    AnalysisDebug.WriteLine($"      Всего путей: {ovrObject.PathVariants.Count}");
                    foreach (var kvp in ovrObject.PathVariants.OrderBy(k => k.Key))
                    {
                        var variant = kvp.Value;
                        var texts = variant?.Texts ?? new List<string>();

                        AnalysisDebug.WriteLine($"      Path {kvp.Key}: {texts.Count} текстов");
                        foreach (var text in texts)
                        {
                            AnalysisDebug.WriteLine($"        {text}");
                        }

                        WriteVariantDebugSummary(variant, ovrObject.X, ovrObject.Y, ovrObject.DirectionByte);
                    }
                }

                return ovrObject;
            }
        }

        private List<OvrObject> ProcessMacros(BinaryReader br, List<CoordinateComparison> comparisons,
            HashSet<string> tableObjectCoords, Dictionary<Point, string> existingCentralOptions)
        {
            var objects = new List<OvrObject>();
            var processedMacroCells = new HashSet<string>();

            foreach (var comparison in comparisons)
            {
                uint macroStartAddress = comparison.MacroEntryAddress != 0
                    ? comparison.MacroEntryAddress
                    : comparison.JumpTarget;

                AnalysisDebug.WriteLine($"\nАнализ макроса по адресу 0x{comparison.JumpTarget:X4} (entry 0x{macroStartAddress:X4}) для [{comparison.MemAddr:X4}]={comparison.Value} ({(comparison.IsLinear ? "линейный" : comparison.JumpType)})");

                bool isX = comparison.CoordType == CoordType.XCoord;
                bool isY = comparison.CoordType == CoordType.YCoord;
                bool isFull = comparison.CoordType == CoordType.FullCoord;

                byte packedTargetX = 0;
                byte packedTargetY = 0;
                if (isFull)
                {
                    packedTargetX = (byte)(comparison.Value & 0x0F);
                    packedTargetY = (byte)(comparison.Value >> 4);
                    AnalysisDebug.WriteLine($"  Полная координата: X={packedTargetX}, Y={packedTargetY} (значение 0x{comparison.Value:X2})");
                }

                var targetCells = GetTargetCells(comparison, isX, isY, isFull, packedTargetX, packedTargetY);

                var cellsNotInTable = targetCells
                    .Where(p => !tableObjectCoords.Contains($"{p.X},{p.Y}"))
                    .ToList();

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
                    byte cellX = (byte)cellPos.X;
                    byte cellY = (byte)cellPos.Y;

                    using (AnalysisDebug.BeginCellScope(cellX, cellY))
                    {
                        string macroCellKey = $"{macroStartAddress:X4}:{cellX},{cellY}";
                        if (processedMacroCells.Contains(macroCellKey))
                        {
                            AnalysisDebug.WriteLine("  -> пропуск: клетка уже обработана для этого macro-entry");
                            continue;
                        }

                        AnalysisDebug.WriteLine($"  -> анализ результатов макроса для клетки ({cellX},{cellY})");

                        var finalVariants = AnalyzeObjectVariants(
                            br,
                            macroStartAddress,
                            cellX,
                            cellY,
                            predefinedAlternativePaths: null,
                            reachableAddresses: null,
                            initializeRegisters: tracker =>
                            {
                                tracker.SetRegisterValue("BL", cellX, macroStartAddress, $"Macro init BL = X ({cellX})");
                                tracker.SetRegisterValue("BH", 0, macroStartAddress, "Macro init BH = 0");
                                tracker.SetRegisterValue("BX", (ushort)cellX, macroStartAddress, $"Macro init BX = X ({cellX})");

                                // В macro-mode нельзя слепо подставлять AL=Y/AH=0 для YCoord.
                                // Иначе ранние guard-проверки по AL начинают ошибочно схлопываться
                                // как координатные, хотя реальный код ещё не загрузил координату в AL.
                                // Полную packed-координату в AX/AL инициализируем только для FullCoord.
                                if (isFull)
                                {
                                    ushort packed = (ushort)(((cellY & 0x0F) << 4) | (cellX & 0x0F));
                                    tracker.SetRegisterValue("AX", packed, macroStartAddress, $"Macro init AX = packed XY 0x{packed:X2}");
                                    tracker.SetRegisterValue("AL", packed, macroStartAddress, $"Macro init AL = packed XY 0x{packed:X2}");
                                }
                            });

                        bool debugMode = AnalysisDebug.IsEnabledFor(cellX, cellY);

                        if (debugMode)
                        {
                            AnalysisDebug.WriteLine($"    Вариантов после AnalyzeObjectVariants: {finalVariants?.Count ?? 0}");

                            if (finalVariants != null)
                            {
                                foreach (var kvp in finalVariants.OrderBy(k => k.Key))
                                {
                                    var variant = kvp.Value;
                                    var texts = variant?.Texts ?? new List<string>();

                                    AnalysisDebug.WriteLine($"      Variant {kvp.Key}: {texts.Count} текстов");
                                    foreach (var text in texts)
                                        AnalysisDebug.WriteLine($"        {text}");

                                    AnalysisDebug.WriteLine(
                                        $"        Significant flags: " +
                                        $"MP={variant?.MonsterPower?.ToString() ?? "-"}, " +
                                        $"ML={variant?.MonsterLevel?.ToString() ?? "-"}, " +
                                        $"MBC={variant?.MonsterBatchCount?.ToString() ?? "-"}, " +
                                        $"Dark={variant?.DarkeningLevel?.ToString() ?? "-"}, " +
                                        $"RE={variant?.RandomEncounterChance?.ToString() ?? "-"}, " +
                                        $"CallRE={variant?.CallsRandomEncounter ?? false}, " +
                                        $"BattleCount={variant?.BattleMonsterCount?.ToString() ?? "-"}, " +
                                        $"BattleRange={(variant?.BattleMonsterCountRange != null ? variant.BattleMonsterCountRange.ToString() : "-")}, " +
                                        $"Indeterminate={variant?.IsBattleMonsterCountIndeterminate ?? false}, " +
                                        $"BattleMonsters={variant?.BattleMonsters?.Count ?? 0}, " +
                                        $"PartialBattles={variant?.PartiallyDefinedBattles?.Count ?? 0}, " +
                                        $"HasAnyTableLoad={variant?.HasAnyTableLoad ?? false}, " +
                                        $"LoadedValues={variant?.LoadedValues?.Count ?? 0}, " +
                                        $"HasTeleport={variant?.HasTeleportTarget ?? false}");

                                    WriteVariantDebugSummary(variant, cellX, cellY, 0);
                                }
                            }
                        }

                        bool hasSignificantCode = HasSignificantVariants(finalVariants);

                        if (!hasSignificantCode && comparison.IsLinear &&
                            (comparison.JumpType.ToUpper() == "JE" || comparison.JumpType.ToUpper() == "JZ") && isFull)
                        {
                            hasSignificantCode = true;
                            AnalysisDebug.WriteLine("  Макрос JE/JZ для полной координаты считается значимым по умолчанию");
                        }

                        if (!hasSignificantCode)
                        {
                            AnalysisDebug.WriteLine($"  -> для клетки ({cellX},{cellY}) значимого кода не найдено, пропускаем");
                            continue;
                        }

                        var obj = new OvrObject
                        {
                            X = cellX,
                            Y = cellY,
                            DirectionByte = 0,
                            IsFromTable = false
                        };

                        PopulateObjectPathData(obj, finalVariants);
                        ApplyResolvedVariantInfoToObject(obj);

                        if (debugMode)
                        {
                            AnalysisDebug.WriteLine($"\n    ИТОГО для макро-клетки ({obj.X},{obj.Y}):");
                            AnalysisDebug.WriteLine($"      Всего путей: {obj.PathVariants.Count}");

                            foreach (var kvp in obj.PathVariants.OrderBy(k => k.Key))
                            {
                                var variant = kvp.Value;
                                var texts = variant?.Texts ?? new List<string>();

                                AnalysisDebug.WriteLine($"      Path {kvp.Key}: {texts.Count} текстов");
                                foreach (var text in texts)
                                    AnalysisDebug.WriteLine($"        {text}");

                                WriteVariantDebugSummary(variant, obj.X, obj.Y, obj.DirectionByte);
                            }
                        }

                        objects.Add(obj);
                        objectsCreated++;
                    }
                }

                AnalysisDebug.WriteLine($"  -> создано объектов из макроса: {objectsCreated}");
            }

            return objects;
        }

        private Dictionary<int, PathVariantInfo> AnalyzeObjectVariants(BinaryReader br, uint startAddress,
            byte targetX, byte targetY, List<AlternativePath> predefinedAlternativePaths,
            HashSet<uint> reachableAddresses, Action<RegisterTracker> initializeRegisters = null)
        {
            var registerTracker = new RegisterTracker();
            initializeRegisters?.Invoke(registerTracker);

            var processedBackEdges = new HashSet<(uint From, uint To)>();
            var mainPathResult = _codeExecutor.ExecuteCodeAtAddress(br, startAddress, registerTracker,
                new HashSet<uint>(), 0, 0, 0, targetX, targetY,
                processedBackEdges, invalidateReturnRegistersAfterExternalCall: true);

            if (reachableAddresses != null)
            {
                foreach (var visitedAddress in mainPathResult.VisitedAddresses)
                    reachableAddresses.Add(visitedAddress);
            }

            var (mainLocalTexts, mainContextTexts) = CollectTextsFromResult(mainPathResult);
            var mainDisplayTexts = BuildDisplayTexts(mainPathResult.OrderedTexts, mainContextTexts, mainLocalTexts);

            var allResults = new List<PathVariantInfo>
            {
                CreatePathVariant(1, mainDisplayTexts, mainPathResult.AlternativePaths.Count == 0, mainPathResult)
            };

            var effectiveAlternativePaths = (predefinedAlternativePaths ?? mainPathResult.AlternativePaths)
                .ToList();
            bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);

            // Если основной путь уже распознал partial-battle loop и развернул его по таблицам,
            // не анализируем обратные переходы, возвращающиеся внутрь этого же цикла.
            // Иначе возникают дубликаты partial battles со сдвигом BX.
            bool suppressLoopBackAlternatives =
                mainPathResult.HasPartialBattlePattern &&
                mainPathResult.IsInLoop &&
                mainPathResult.LoopStartAddress != 0;

            if (suppressLoopBackAlternatives)
            {
                effectiveAlternativePaths = effectiveAlternativePaths
                    .Where(p => !(p.TargetAddress < p.Address &&
                                  p.TargetAddress <= mainPathResult.LoopStartAddress))
                    .ToList();
            }

            if (effectiveAlternativePaths.Count > 0)
            {
                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\nСобрано путей из основного анализа: {effectiveAlternativePaths.Count}");
                    foreach (var path in effectiveAlternativePaths)
                    {
                        AnalysisDebug.WriteLine($"  Путь: 0x{path.Address:X4} -> 0x{path.TargetAddress:X4} ({path.Condition})");
                    }
                }

                _pathAnalyzer.ProcessPaths(effectiveAlternativePaths, 1, 0,
                    mainDisplayTexts,
                    br,
                    targetX, targetY, allResults, null, processedBackEdges,
                    invalidateReturnRegistersAfterExternalCall: true,
                    reachableAddresses: reachableAddresses,
                    inheritedState: mainPathResult);
            }

            return _pathAnalyzer.BuildFinalPathVariants(allResults);
        }

        private void PopulateObjectPathData(OvrObject obj, Dictionary<int, PathVariantInfo> finalVariants)
        {
            obj.PathVariants = finalVariants ?? new Dictionary<int, PathVariantInfo>();
            obj.PathTexts = new Dictionary<int, HashSet<string>>();
            obj.PathTextsOrdered = new Dictionary<int, List<string>>();

            foreach (var kvp in obj.PathVariants)
            {
                obj.PathTexts[kvp.Key] = new HashSet<string>(kvp.Value.Texts ?? new List<string>());
                obj.PathTextsOrdered[kvp.Key] = kvp.Value.Texts ?? new List<string>();
            }
        }

        private bool HasSignificantVariants(Dictionary<int, PathVariantInfo> variants)
        {
            if (variants == null || variants.Count == 0)
                return false;

            return variants.Values.Any(variant =>
                (variant.Texts?.Count ?? 0) > 0 ||
                variant.MonsterPower.HasValue ||
                variant.MonsterLevel.HasValue ||
                variant.MonsterBatchCount.HasValue ||
                variant.DarkeningLevel.HasValue ||
                variant.RandomEncounterChance.HasValue ||
                variant.CallsRandomEncounter ||
                variant.BattleMonsterCount.HasValue ||
                variant.BattleMonsterCountRange != null ||
                variant.IsBattleMonsterCountIndeterminate ||
                (variant.BattleMonsters?.Count ?? 0) > 0 ||
                (variant.PartiallyDefinedBattles?.Count ?? 0) > 0 ||
                variant.HasAnyTableLoad ||
                (variant.LoadedValues?.Count ?? 0) > 0 ||
                variant.HasTeleportTarget);
        }

        private List<OvrObject> ProcessDefaultPath(BinaryReader br, List<X86Instruction> allInstructions,
    HashSet<string> tableObjectCoords, Dictionary<Point, string> existingCentralOptions,
    List<OvrObject> existingObjects)
        {
            var objects = new List<OvrObject>();

            uint? defaultPathAddress = _macroAnalyzer.FindDefaultPathAfterTableLoop(allInstructions);

            if (!defaultPathAddress.HasValue)
                return objects;

            AnalysisDebug.WriteLine($"\n=== ТРЕТИЙ РЕЖИМ: ОБРАБОТКА ПУТИ ПО УМОЛЧАНИЮ ===");
            AnalysisDebug.WriteLine($"Адрес: 0x{defaultPathAddress.Value:X4}");

            int objectsCreated = 0;

            for (byte x = 0; x < 16; x++)
            {
                for (byte y = 0; y < 16; y++)
                {
                    var cellPos = new Point(x, y);
                    string coordKey = $"{x},{y}";

                    if (tableObjectCoords.Contains(coordKey))
                    {
                        using (AnalysisDebug.BeginCellScope(x, y))
                        {
                            AnalysisDebug.WriteLine("  -> пропуск: клетка уже обработана табличным объектом");
                        }
                        continue;
                    }

                    bool isRandomEncounter = existingCentralOptions.TryGetValue(cellPos, out string option) &&
                                             option == "Случайная встреча";

                    if (!isRandomEncounter)
                    {
                        using (AnalysisDebug.BeginCellScope(x, y))
                        {
                            AnalysisDebug.WriteLine("  -> пропуск: клетка не помечена как \"Случайная встреча\"");
                        }
                        continue;
                    }

                    bool alreadyExists = existingObjects.Any(o => o.X == x && o.Y == y);
                    if (alreadyExists)
                    {
                        using (AnalysisDebug.BeginCellScope(x, y))
                        {
                            AnalysisDebug.WriteLine("  -> пропуск: объект для клетки уже создан другим режимом");
                        }
                        continue;
                    }

                    using (AnalysisDebug.BeginCellScope(x, y))
                    {
                        AnalysisDebug.WriteLine($"  -> анализ default-path для клетки ({x},{y})");

                        var finalVariants = AnalyzeObjectVariants(
                            br,
                            defaultPathAddress.Value,
                            x,
                            y,
                            predefinedAlternativePaths: null,
                            reachableAddresses: null,
                            initializeRegisters: tracker =>
                                SetInitialRegistersFromCoordinates(tracker, x, y, defaultPathAddress.Value));

                        bool hasSignificantCode = HasSignificantVariants(finalVariants);
                        if (!hasSignificantCode)
                        {
                            AnalysisDebug.WriteLine($"  -> для клетки ({x},{y}) значимого кода не найдено, пропускаем");
                            continue;
                        }

                        var obj = new OvrObject
                        {
                            X = x,
                            Y = y,
                            DirectionByte = 0,
                            IsFromTable = false
                        };

                        PopulateObjectPathData(obj, finalVariants);
                        ApplyResolvedVariantInfoToObject(obj);

                        AnalysisDebug.WriteLine($"    Создан объект default-path для клетки ({obj.X},{obj.Y})");
                        AnalysisDebug.WriteLine($"    Всего путей: {obj.PathVariants.Count}");

                        foreach (var kvp in obj.PathVariants.OrderBy(k => k.Key))
                        {
                            var variant = kvp.Value;
                            var texts = variant?.Texts ?? new List<string>();

                            AnalysisDebug.WriteLine($"      Path {kvp.Key}: {texts.Count} текстов");
                            foreach (var text in texts)
                            {
                                AnalysisDebug.WriteLine($"        {text}");
                            }

                            WriteVariantDebugSummary(variant, obj.X, obj.Y, obj.DirectionByte);
                        }

                        objects.Add(obj);
                        objectsCreated++;
                    }
                }
            }

            AnalysisDebug.WriteLine($"  Создано объектов из третьего режима: {objectsCreated}");
            return objects;
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

            foreach (var text in result.OrderedTexts.OrderBy(t => t.Order))
            {
                var clone = text.Clone();
                clone.Order = order++;

                if (clone.IsContextual)
                    context.Add(clone);
                else
                    local.Add(clone);
            }

            return (local, context);
        }

        private List<TextEntry> BuildDisplayTexts(IEnumerable<TextEntry> orderedTexts, IEnumerable<TextEntry> contextTexts, IEnumerable<TextEntry> localTexts)
        {
            var result = new List<TextEntry>();
            var sourceTexts = orderedTexts?.Where(t => t != null).OrderBy(t => t.Order).ToList();
            if (sourceTexts == null || sourceTexts.Count == 0)
            {
                sourceTexts = new List<TextEntry>();
                sourceTexts.AddRange((contextTexts ?? Enumerable.Empty<TextEntry>()).Where(t => t != null).OrderBy(t => t.Order));
                sourceTexts.AddRange((localTexts ?? Enumerable.Empty<TextEntry>()).Where(t => t != null).OrderBy(t => t.Order));
            }

            int nextOrder = 0;
            foreach (var text in sourceTexts.Where(t => t.IsContextual).OrderBy(t => t.Order))
            {
                var clone = text.Clone();
                clone.Order = nextOrder++;
                result.Add(clone);
            }

            foreach (var text in sourceTexts.Where(t => !t.IsContextual).OrderBy(t => t.Order))
            {
                var clone = text.Clone();
                clone.Order = nextOrder++;
                result.Add(clone);
            }

            return result;
        }

        private PathVariantInfo CreatePathVariant(int pathId, List<TextEntry> pathTexts, bool isLeaf, PathAnalysisResult source)
        {
            var combinedTexts = (pathTexts ?? new List<TextEntry>())
                .Where(t => t != null && !string.IsNullOrEmpty(t.Text))
                .OrderBy(t => t.Order)
                .Select(t => t.Text)
                .ToList();

            return new PathVariantInfo
            {
                PathId = pathId,
                Texts = combinedTexts.Where(t => !string.IsNullOrEmpty(t)).ToList(),
                IsLeaf = isLeaf,
                MonsterPower = source.MonsterPower,
                MonsterLevel = source.MonsterLevel,
                MonsterBatchCount = source.MonsterBatchCount,
                DarkeningLevel = source.DarkeningLevel,
                RandomEncounterChance = source.RandomEncounterChance,
                CallsRandomEncounter = source.CallsRandomEncounter,
                IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump,
                TeleportTargetX = source.TeleportTargetX,
                TeleportTargetY = source.TeleportTargetY,
                TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max),
                TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max),
                BattleMonsterCount = source.BattleMonsterCount,
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                BattleMonsters = source.BattleMonsterEntries
                    .Where(entry => entry.Value.val1 != 0 || entry.Value.val2 != 0)
                    .OrderBy(entry => entry.Key)
                    .Select(entry => new BattleMonster
                    {
                        Index = entry.Key,
                        MonsterIndex1 = entry.Value.val1,
                        MonsterIndex2 = entry.Value.val2,
                        IsIndeterminate = entry.Value.isIndeterminate
                    })
                    .ToList(),
                PartiallyDefinedBattles = source.PartialBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(p => new PartiallyDefinedBattle
                    {
                        BxIndex = p.BxIndex,
                        RangeStart1 = p.RangeStart1,
                        RangeEnd1 = p.RangeEnd1,
                        RangeStart2 = p.RangeStart2,
                        RangeEnd2 = p.RangeEnd2
                    })
                    .ToList(),
                HasAnyTableLoad = source.HasPartialBattlePattern,
                LoadedValues = source.PartialBattleInfo
                    .OrderBy(i => i.BxIndex)
                    .ThenBy(i => i.SourceTableAddr ?? 0)
                    .ThenBy(i => i.SrcReg)
                    .Select(info => new OvrObject.LoadedValueInfo
                    {
                        BxIndex = info.BxIndex,
                        RegName = info.SrcReg,
                        Value = info.SrcRegValue,
                        SourceAddr = info.SourceTableAddr ?? 0,
                        IsFirstTable = info.IsFromTable,
                        IsSaved = false
                    })
                    .ToList()
            };
        }

        private void WriteVariantDebugSummary(PathVariantInfo variant, byte x, byte y, byte directionByte)
        {
            if (variant == null)
            {
                AnalysisDebug.WriteLine("        <variant is null>");
                return;
            }

            if (variant.HasProbabilityInfo)
            {
                double percent = 100.0 * variant.ProbabilityNumerator / Math.Max(1, variant.ProbabilityDenominator);
                string percentText = percent % 1.0 == 0.0 ? percent.ToString("0") : percent.ToString("0.##");
                AnalysisDebug.WriteLine($"        Probability: {percentText}% ({variant.ProbabilityNumerator}/{variant.ProbabilityDenominator})");
            }

            if (variant.HasTeleportTarget)
            {
                string xText = variant.TeleportTargetX.HasValue ? variant.TeleportTargetX.Value.ToString() : "?";
                string yText = variant.TeleportTargetY.HasValue ? variant.TeleportTargetY.Value.ToString() : "?";
                AnalysisDebug.WriteLine($"        TeleportTarget: X={xText}, Y={yText}");
            }

            if (variant.BattleMonsterCountRange != null)
            {
                if (variant.BattleMonsterCountRange.IsExact)
                    AnalysisDebug.WriteLine($"        BattleMonsterCountRange: {variant.BattleMonsterCountRange.Min}");
                else
                    AnalysisDebug.WriteLine($"        BattleMonsterCountRange: {variant.BattleMonsterCountRange.Min}-{variant.BattleMonsterCountRange.Max}");
            }
            else if (variant.BattleMonsterCount.HasValue)
            {
                AnalysisDebug.WriteLine($"        BattleMonsterCount: {variant.BattleMonsterCount.Value}");
            }
            else if (variant.IsBattleMonsterCountIndeterminate)
            {
                AnalysisDebug.WriteLine("        BattleMonsterCount: неопределён");
            }

            if (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0)
            {
                AnalysisDebug.WriteLine("        BattleMonsters:");
                foreach (var battleMonster in variant.BattleMonsters.OrderBy(m => m.Index))
                {
                    AnalysisDebug.WriteLine(
                        $"          BX={battleMonster.Index}: val1=0x{battleMonster.MonsterIndex1:X2}, val2=0x{battleMonster.MonsterIndex2:X2}, indeterminate={battleMonster.IsIndeterminate}");
                }
            }

            if (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0)
            {
                AnalysisDebug.WriteLine("        PartiallyDefinedBattles:");
                foreach (var partial in variant.PartiallyDefinedBattles.OrderBy(p => p.BxIndex))
                {
                    AnalysisDebug.WriteLine(
                        $"          BX={partial.BxIndex}: [{partial.RangeStart1:X2}-{partial.RangeEnd1:X2}] + [{partial.RangeStart2:X2}-{partial.RangeEnd2:X2}]");
                }
            }

            var variantObject = variant.ToOvrObject(x, y, directionByte);
            string battleDescription = variantObject.GetBattleDescription();
            if (!string.IsNullOrWhiteSpace(battleDescription))
            {
                AnalysisDebug.WriteLine("        BattleDescription:");
                foreach (var line in battleDescription.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AnalysisDebug.WriteLine($"          {line}");
                }
            }
        }

        private void ApplyResolvedVariantInfoToObject(OvrObject target)
        {
            target.MonsterPower = null;
            target.MonsterLevel = null;
            target.MonsterBatchCount = null;
            target.DarkeningLevel = null;
            target.RandomEncounterChance = null;
            target.CallsRandomEncounter = false;
            target.BattleMonsterCount = null;
            target.BattleMonsterCountRange = null;
            target.IsBattleMonsterCountIndeterminate = false;
            target.BattleMonsters.Clear();
            target.PartiallyDefinedBattles.Clear();
            target.HasAnyTableLoad = false;

            if (target.PathVariants == null || target.PathVariants.Count != 1)
                return;

            var variant = target.PathVariants.Values.First();
            target.MonsterPower = variant.MonsterPower;
            target.MonsterLevel = variant.MonsterLevel;
            target.MonsterBatchCount = variant.MonsterBatchCount;
            target.DarkeningLevel = variant.DarkeningLevel;
            target.RandomEncounterChance = variant.RandomEncounterChance;
            target.CallsRandomEncounter = variant.CallsRandomEncounter;
            target.BattleMonsterCount = variant.BattleMonsterCount;
            target.BattleMonsterCountRange = variant.BattleMonsterCountRange == null ? null : new ValueRange8(variant.BattleMonsterCountRange.Min, variant.BattleMonsterCountRange.Max);
            target.IsBattleMonsterCountIndeterminate = variant.IsBattleMonsterCountIndeterminate;
            target.HasAnyTableLoad = variant.HasAnyTableLoad;

            foreach (var battleMonster in variant.BattleMonsters)
            {
                target.AddBattleMonster(
                    battleMonster.Index,
                    battleMonster.MonsterIndex1,
                    battleMonster.MonsterIndex2,
                    battleMonster.IsIndeterminate);
            }

            foreach (var partial in variant.PartiallyDefinedBattles)
            {
                target.AddPartiallyDefinedBattle(
                    partial.BxIndex,
                    partial.RangeStart1,
                    partial.RangeEnd1,
                    partial.RangeStart2,
                    partial.RangeEnd2);
            }

            foreach (var loadedValue in variant.LoadedValues)
            {
                target.AddLoadedValue(
                    loadedValue.BxIndex,
                    loadedValue.RegName,
                    loadedValue.Value,
                    loadedValue.SourceAddr,
                    loadedValue.IsFirstTable,
                    loadedValue.IsSaved);
            }
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
            byte? monsterPower, byte? monsterLevel, byte? darkeningLevel, byte? randomEncounterChance, bool callsRandomEncounter, byte? battleMonsterCount, ValueRange8 battleMonsterCountRange, bool isBattleMonsterCountIndeterminate,
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
                DarkeningLevel = darkeningLevel,
                RandomEncounterChance = randomEncounterChance,
                CallsRandomEncounter = callsRandomEncounter,
                BattleMonsterCount = battleMonsterCount,
                BattleMonsterCountRange = battleMonsterCountRange == null ? null : new ValueRange8(battleMonsterCountRange.Min, battleMonsterCountRange.Max),
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
                DarkeningLevel = result.DarkeningLevel,
                RandomEncounterChance = result.RandomEncounterChance,
                CallsRandomEncounter = result.CallsRandomEncounter,
                TeleportTargetX = result.TeleportTargetX,
                TeleportTargetY = result.TeleportTargetY,
                TeleportTargetXRange = result.TeleportTargetXRange == null ? null : new ValueRange8(result.TeleportTargetXRange.Min, result.TeleportTargetXRange.Max),
                TeleportTargetYRange = result.TeleportTargetYRange == null ? null : new ValueRange8(result.TeleportTargetYRange.Min, result.TeleportTargetYRange.Max),
                BattleMonsterCount = result.BattleMonsterCount,
                BattleMonsterCountRange = result.BattleMonsterCountRange == null ? null : new ValueRange8(result.BattleMonsterCountRange.Min, result.BattleMonsterCountRange.Max),
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
