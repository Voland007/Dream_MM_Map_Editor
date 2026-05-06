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


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    public class OvrFileAnalyzer
    {
        private enum RepeatedEventAnalysisMode
        {
            IncludeRepeatedOccurrences,
            SingleOccurrenceOnly
        }

        private readonly OvrFileConfig _config;
        private readonly MacroAnalyzer _macroAnalyzer;
        private readonly InstructionAnalyzer _instructionAnalyzer;
        private readonly CodeExecutor _codeExecutor;
        private readonly PathAnalyzer _pathAnalyzer;
        private readonly Dictionary<string, ObjectVariantAnalysisCacheEntry> _objectVariantAnalysisCache
            = new Dictionary<string, ObjectVariantAnalysisCacheEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, SingleOccurrenceAnalysisCacheEntry> _singleOccurrenceAnalysisCache
            = new Dictionary<string, SingleOccurrenceAnalysisCacheEntry>(StringComparer.Ordinal);

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
                ValidateOverlayStructure(br);

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

        private void ValidateOverlayStructure(BinaryReader br)
        {
            long fileLength = br.BaseStream.Length;
            if (fileLength <= _config.StartAddress)
            {
                throw new InvalidOperationException(
                    $"File is too small for overlay start address 0x{_config.StartAddress:X3}.");
            }

            long originalPosition = br.BaseStream.Position;

            try
            {
                br.BaseStream.Seek(_config.StartAddress, SeekOrigin.Begin);
                byte numObjects = br.ReadByte();
                long requiredLength = _config.StartAddress + 1L + numObjects + numObjects + (numObjects * 2L);

                if (fileLength < requiredLength)
                {
                    throw new InvalidOperationException(
                        $"File is too small for overlay object table. Length={fileLength}, required={requiredLength}.");
                }
            }
            finally
            {
                br.BaseStream.Position = originalPosition;
            }
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
                DirectionByte = direction,
                PatchAddress = CalculatePatchAddress(patchKey)
            };

            using (AnalysisDebug.BeginCellScope(ovrObject))
            {
                uint patchAddress = ovrObject.PatchAddress ?? CalculatePatchAddress(patchKey);
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
                    initializeRegisters: tracker => SetInitialRegistersFromCoordinates(tracker, ovrObject.X, ovrObject.Y, patchAddress),
                    analysisCacheScopeKey: "table");

                PopulateObjectPathData(ovrObject, finalVariants);
                ApplyResolvedVariantInfoToObject(ovrObject);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n    ИТОГО для клетки ({ovrObject.X},{ovrObject.Y}):");
                    AnalysisDebug.WriteLine($"      Канонических outcomes: {ovrObject.PathVariants.Count}");
                    foreach (var kvp in ovrObject.PathVariants.OrderBy(k => k.Key))
                    {
                        var variant = kvp.Value;
                        var texts = variant?.Texts ?? new List<string>();

                        AnalysisDebug.WriteLine($"      Outcome {kvp.Key}: {texts.Count} текстов");
                        foreach (var text in texts)
                        {
                            AnalysisDebug.WriteLine($"        {text}");
                        }

                        if (variant?.PartyEffects != null && variant.PartyEffects.Count > 0)
                        {
                            AnalysisDebug.WriteLine($"        PartyEffects={variant.PartyEffects.Count}");
                            foreach (var effect in variant.PartyEffects)
                                AnalysisDebug.WriteLine($"          - {PartyEffectSemantics.BuildDebugLine(effect)}");
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
                foreach (var cellPos in OrderCellsForCacheEvidence(validCells))
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
                                tracker.MarkRegisterAsCoordinateSeed("BX");

                                // В macro-mode нельзя слепо подставлять AL=Y/AH=0 для YCoord.
                                // Иначе ранние guard-проверки по AL начинают ошибочно схлопываться
                                // как координатные, хотя реальный код ещё не загрузил координату в AL.
                                // Полную packed-координату в AX/AL инициализируем только для FullCoord.
                                if (isFull)
                                {
                                    ushort packed = (ushort)(((cellY & 0x0F) << 4) | (cellX & 0x0F));
                                    tracker.SetRegisterValue("AX", packed, macroStartAddress, $"Macro init AX = packed XY 0x{packed:X2}");
                                    tracker.SetRegisterValue("AL", packed, macroStartAddress, $"Macro init AL = packed XY 0x{packed:X2}");
                                    tracker.MarkRegisterAsCoordinateSeed("AX");
                                }
                            },
                            analysisCacheScopeKey: BuildMacroAnalysisCacheScopeKey(comparison),
                            repeatedEventAnalysisMode: RepeatedEventAnalysisMode.SingleOccurrenceOnly);

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
                                        $"REPowerCap={variant?.RandomEncounterMonsterPowerCap?.ToString() ?? "-"}, " +
                                        $"RELevelCap={variant?.RandomEncounterMonsterLevelCap?.ToString() ?? "-"}, " +
                                        $"REBatchCountCap={variant?.RandomEncounterMonsterBatchCountCap?.ToString() ?? "-"}, " +
                                        $"Dark={variant?.DarkeningLevel?.ToString() ?? "-"}, " +
                                        $"RE={variant?.RandomEncounterChance?.ToString() ?? "-"}, " +
                                        $"CallRE={variant?.CallsRandomEncounter ?? false}, " +
                                        $"RERubicon={variant?.RandomEncounterRubicon?.ToString() ?? "-"}, " +
                                        $"BattleCount={variant?.BattleMonsterCount?.ToString() ?? "-"}, " +
                                        $"BattleRange={(variant?.BattleMonsterCountRange != null ? variant.BattleMonsterCountRange.ToString() : "-")}, " +
                                        $"Indeterminate={variant?.IsBattleMonsterCountIndeterminate ?? false}, " +
                                        $"BattleMonsters={variant?.BattleMonsters?.Count ?? 0}, " +
                                        $"PartialBattles={variant?.PartiallyDefinedBattles?.Count ?? 0}, " +
                                        $"CounterProgressions={variant?.PersistentCounterProgressions?.Count ?? 0}, " +
                                        $"DynamicRandomBounds={variant?.DynamicRandomBoundDependencies?.Count ?? 0}, " +
                                        $"HasAnyTableLoad={variant?.HasAnyTableLoad ?? false}, " +
                                        $"LoadedValues={variant?.LoadedValues?.Count ?? 0}, " +
                                        $"HasTeleport={variant?.HasTeleportTarget ?? false}, " +
                                        $"PartyEffects={variant?.PartyEffects?.Count ?? 0}");

                                    if (variant?.PartyEffects != null && variant.PartyEffects.Count > 0)
                                    {
                                        AnalysisDebug.WriteLine($"        PartyEffects={variant.PartyEffects.Count}");
                                        foreach (var effect in variant.PartyEffects)
                                            AnalysisDebug.WriteLine($"          - {PartyEffectSemantics.BuildDebugLine(effect)}");
                                    }
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
                            AnalysisDebug.WriteLine($"      Канонических outcomes: {obj.PathVariants.Count}");

                            foreach (var kvp in obj.PathVariants.OrderBy(k => k.Key))
                            {
                                var variant = kvp.Value;
                                var texts = variant?.Texts ?? new List<string>();

                                AnalysisDebug.WriteLine($"      Outcome {kvp.Key}: {texts.Count} текстов");
                                foreach (var text in texts)
                                    AnalysisDebug.WriteLine($"        {text}");

                                if (variant?.PartyEffects != null && variant.PartyEffects.Count > 0)
                                {
                                    AnalysisDebug.WriteLine($"        PartyEffects={variant.PartyEffects.Count}");
                                    foreach (var effect in variant.PartyEffects)
                                        AnalysisDebug.WriteLine($"          - {PartyEffectSemantics.BuildDebugLine(effect)}");
                                }
                                WriteVariantDebugSummary(variant, obj.X, obj.Y, obj.DirectionByte);
                            }
                        }

                        objects.Add(obj);
                        processedMacroCells.Add(macroCellKey);
                        objectsCreated++;
                    }
                }

                AnalysisDebug.WriteLine($"  -> создано объектов из макроса: {objectsCreated}");
            }

            return objects;
        }

        private const int MaxRepeatedEventOccurrences = 64;
        private const int MaxRepeatedEventRepresentativeOccurrences = 16;
        private const int MaxRepeatedEventStatesPerOccurrence = 32;
        private const int MaxRepeatedEventCanonicalOutcomes = 5;
        private const int RawLeafDebugSampleLimit = 12;
        private const int RawLeafDebugGroupLimit = 12;
        private const int RawLeafDebugTextSampleLimit = 3;
        private const int RawLeafDebugPartyEffectSampleLimit = 3;

        private sealed class SingleOccurrenceAnalysisResult
        {
            public Dictionary<int, PathVariantInfo> CanonicalVariants { get; set; } = new Dictionary<int, PathVariantInfo>();
            public List<PathVariantInfo> RawLeafVariants { get; set; } = new List<PathVariantInfo>();
            public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
            public bool UsesInitialCoordinates { get; set; }
        }

        private sealed class SingleOccurrenceAnalysisCacheEntry
        {
            public string SampleSignature { get; set; } = string.Empty;
            public bool VerificationFailed { get; set; }
            public bool IsVerified { get; set; }
            public HashSet<byte> MatchingOppositeAxisValues { get; set; } = new HashSet<byte>();
            public SingleOccurrenceAnalysisResult ReusableResult { get; set; }
        }

        private sealed class ObjectVariantAnalysisCacheEntry
        {
            public byte SampleX { get; set; }
            public byte SampleY { get; set; }
            public string SampleSignature { get; set; } = string.Empty;
            public string SampleVisitedKey { get; set; } = string.Empty;
            public bool SampleUsesInitialCoordinates { get; set; }
            public bool CoordinateSensitiveMismatchObserved { get; set; }
            public HashSet<string> MatchingCoordinatePairs { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<byte> MatchingXValues { get; set; } = new HashSet<byte>();
            public HashSet<byte> MatchingYValues { get; set; } = new HashSet<byte>();
            public Dictionary<int, PathVariantInfo> SampleVariants { get; set; } = new Dictionary<int, PathVariantInfo>();
            public HashSet<uint> SampleVisitedAddresses { get; set; } = new HashSet<uint>();
            public bool IsCoordinateIndependentVerified { get; set; }
            public Dictionary<int, PathVariantInfo> ReusableVariants { get; set; } = new Dictionary<int, PathVariantInfo>();
            public HashSet<uint> ReusableVisitedAddresses { get; set; } = new HashSet<uint>();
        }

        private sealed class RepeatedEventStateInfo
        {
            public Dictionary<ushort, byte> Memory { get; set; } = new Dictionary<ushort, byte>();
            public Dictionary<ushort, StateValueConstraintInfo> StateValueConstraints { get; set; } = new Dictionary<ushort, StateValueConstraintInfo>();
        }

        private sealed class RepeatedEventFastForwardPlan
        {
            public int CoverageEndOccurrence { get; set; }
            public bool IsOpenEnded { get; set; }
            public int? NextInterestingOccurrence { get; set; }
            public Dictionary<ushort, byte> ScheduledState { get; set; } = new Dictionary<ushort, byte>();
            public Dictionary<ushort, StateValueConstraintInfo> ScheduledConstraints { get; set; } = new Dictionary<ushort, StateValueConstraintInfo>();
        }

        private sealed class RepeatedEventProgressionPattern
        {
            public ushort Address { get; set; }
            public int Delta { get; set; }
            public string StateTemplateWithoutAddressKey { get; set; } = string.Empty;
            public string TargetVariantKey { get; set; } = string.Empty;
            public StateValueConstraintInfo ConstraintInfo { get; set; } = new StateValueConstraintInfo();
            public PathVariantInfo TargetVariant { get; set; }
        }

        private sealed class RepeatedEventStableStatePattern
        {
            public string StateKey { get; set; } = string.Empty;
            public string TargetVariantKey { get; set; } = string.Empty;
            public PathVariantInfo TargetVariant { get; set; }
        }

        private sealed class RepeatedEventStateTransition
        {
            public ushort Address { get; set; }
            public int Delta { get; set; }
            public StateValueConstraintInfo ConstraintInfo { get; set; } = new StateValueConstraintInfo();
        }

        private Dictionary<int, PathVariantInfo> AnalyzeObjectVariants(BinaryReader br, uint startAddress,
            byte targetX, byte targetY, List<AlternativePath> predefinedAlternativePaths,
            HashSet<uint> reachableAddresses, Action<RegisterTracker> initializeRegisters = null,
            string analysisCacheScopeKey = null,
            RepeatedEventAnalysisMode repeatedEventAnalysisMode = RepeatedEventAnalysisMode.IncludeRepeatedOccurrences)
        {
            bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);
            bool bypassCacheForTargetCell = AnalysisDebug.ShouldDisableCacheFor(targetX, targetY);
            string perfKey = string.IsNullOrWhiteSpace(analysisCacheScopeKey)
                ? $"<NO_SCOPE>|{startAddress:X4}"
                : $"{analysisCacheScopeKey}|{startAddress:X4}";

            if (!bypassCacheForTargetCell &&
                TryGetCachedObjectVariants(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    reachableAddresses,
                    out var cachedVariants))
            {
                OvrAnalysisPerfStats.RecordAnalyzeCall(perfKey, cacheHit: true);
                return cachedVariants;
            }

            OvrAnalysisPerfStats.RecordAnalyzeCall(perfKey, cacheHit: false);

            if (debugMode && bypassCacheForTargetCell && !string.IsNullOrWhiteSpace(analysisCacheScopeKey))
            {
                AnalysisDebug.WriteLine(
                    $"Кэш анализа отключён для target-клетки: {analysisCacheScopeKey}|0x{startAddress:X4}");
            }

            var scheduledStates = new SortedDictionary<int, Dictionary<string, RepeatedEventStateInfo>>();
            var progressionPatterns = new List<RepeatedEventProgressionPattern>();
            var progressionPatternKeys = new HashSet<string>(StringComparer.Ordinal);
            var stableStatePatterns = new List<RepeatedEventStableStatePattern>();
            var stableStatePatternKeys = new HashSet<string>(StringComparer.Ordinal);
            bool analysisTruncated = false;
            bool usedRepeatedEventAcceleration = false;
            bool analyzeRepeatedEventOccurrences =
                repeatedEventAnalysisMode == RepeatedEventAnalysisMode.IncludeRepeatedOccurrences;
            bool usesInitialCoordinates = false;
            var totalVisitedAddresses = new HashSet<uint>();
            ScheduleRepeatedEventState(
                scheduledStates,
                1,
                new RepeatedEventStateInfo(),
                debugMode,
                ref analysisTruncated,
                null);

            var collectedVariants = new List<PathVariantInfo>();
            int maxAnalyzedOccurrence = 0;
            int analyzedRepresentativeOccurrences = 0;
            Dictionary<int, PathVariantInfo> finalVariants = null;

            while (scheduledStates.Count > 0)
            {
                int occurrence = scheduledStates.First().Key;
                var currentStates = scheduledStates[occurrence];
                scheduledStates.Remove(occurrence);

                if (occurrence > MaxRepeatedEventOccurrences)
                {
                    analysisTruncated = true;
                    if (debugMode)
                        AnalysisDebug.WriteLine($"Достигнут лимит наступлений события ({MaxRepeatedEventOccurrences})");
                    break;
                }

                analyzedRepresentativeOccurrences++;
                if (analyzedRepresentativeOccurrences > MaxRepeatedEventRepresentativeOccurrences)
                {
                    analysisTruncated = true;
                    if (debugMode)
                        AnalysisDebug.WriteLine($"Достигнут лимит репрезентативных наступлений ({MaxRepeatedEventRepresentativeOccurrences})");
                    break;
                }

                maxAnalyzedOccurrence = Math.Max(maxAnalyzedOccurrence, occurrence);

                if (debugMode && occurrence > 1)
                {
                    AnalysisDebug.WriteLine($"\n=== ПОВТОРНОЕ НАСТУПЛЕНИЕ СОБЫТИЯ #{occurrence} ===");
                    AnalysisDebug.WriteLine($"Стартовых состояний памяти: {currentStates.Count}");
                    foreach (var scheduledState in currentStates)
                        AnalysisDebug.WriteLine($"  RepeatedState: {scheduledState.Key}");
                }

                var canonicalVariantsForOccurrence = new List<PathVariantInfo>();
                foreach (var currentStateInfo in currentStates.Values)
                {
                    OvrAnalysisPerfStats.RecordSingleOccurrencePass(perfKey);
                    var currentState = currentStateInfo.Memory;
                    var passResult = AnalyzeSingleOccurrenceVariants(
                        br,
                        startAddress,
                        targetX,
                        targetY,
                        analysisCacheScopeKey,
                        predefinedAlternativePaths,
                        reachableAddresses,
                        initializeRegisters,
                        currentState,
                        new HashSet<ushort>(currentState.Keys),
                        occurrence);

                    foreach (var variant in passResult.CanonicalVariants.Values)
                    {
                        MarkVariantOccurrenceRange(variant, occurrence, occurrence);
                        collectedVariants.Add(variant);
                        canonicalVariantsForOccurrence.Add(variant);
                    }

                    usesInitialCoordinates |= passResult.UsesInitialCoordinates;
                    totalVisitedAddresses.UnionWith(passResult.VisitedAddresses ?? Enumerable.Empty<uint>());

                    if (!analyzeRepeatedEventOccurrences)
                        continue;

                    foreach (var rawLeaf in passResult.RawLeafVariants)
                    {
                        var repeatedEventRelevantAddresses = BuildRepeatedEventRelevantAddresses(rawLeaf);
                        var carryOverState = BuildCarryOverState(
                            br,
                            currentState,
                            rawLeaf,
                            repeatedEventRelevantAddresses);
                        var nextConstraints = FilterStateValueConstraintsForRepeatedEvent(
                            MergeStateValueConstraints(
                                currentStateInfo.StateValueConstraints,
                                rawLeaf.StateValueConstraints),
                            repeatedEventRelevantAddresses);

                        var canonicalVariant = _pathAnalyzer.FindCanonicalVariantForLeaf(
                            rawLeaf,
                            passResult.CanonicalVariants.Values);
                        if (canonicalVariant != null &&
                            rawLeaf.StateValueConstraints != null &&
                            rawLeaf.StateValueConstraints.Any(kvp =>
                                repeatedEventRelevantAddresses.Contains(kvp.Key) &&
                                kvp.Value != null &&
                                !kvp.Value.IsEmpty))
                        {
                            canonicalVariant.HasRepeatedEventOccurrenceSensitivity = true;
                        }

                        RegisterRepeatedEventProgressionPattern(
                            progressionPatterns,
                            progressionPatternKeys,
                            br,
                            currentState,
                            carryOverState,
                            rawLeaf,
                            canonicalVariant);

                        if (TryBuildRepeatedEventFastForwardPlan(
                            br,
                            occurrence,
                            currentStateInfo,
                            carryOverState,
                            nextConstraints,
                            rawLeaf,
                            perfKey,
                            out var fastForwardPlan))
                        {
                            usedRepeatedEventAcceleration |=
                                fastForwardPlan.IsOpenEnded ||
                                fastForwardPlan.CoverageEndOccurrence > occurrence + 1 ||
                                (fastForwardPlan.NextInterestingOccurrence.HasValue &&
                                 fastForwardPlan.NextInterestingOccurrence.Value > occurrence + 1);

                            if (canonicalVariant != null)
                            {
                                AddOccurrenceCoverage(canonicalVariant, occurrence + 1, fastForwardPlan.CoverageEndOccurrence, fastForwardPlan.IsOpenEnded);
                            }

                            if (!fastForwardPlan.IsOpenEnded && fastForwardPlan.NextInterestingOccurrence.HasValue)
                            {
                                ScheduleRepeatedEventState(
                                    scheduledStates,
                                    fastForwardPlan.NextInterestingOccurrence.Value,
                                    new RepeatedEventStateInfo
                                    {
                                        Memory = fastForwardPlan.ScheduledState,
                                        StateValueConstraints = fastForwardPlan.ScheduledConstraints
                                    },
                                    debugMode,
                                    ref analysisTruncated,
                                    occurrence);
                            }

                            if (analysisTruncated)
                                break;

                            continue;
                        }

                        if (TryApplyStableSelfLoopOptimization(
                            passResult,
                            occurrence,
                            currentState,
                            carryOverState,
                            canonicalVariant,
                            stableStatePatterns,
                            stableStatePatternKeys))
                        {
                            usedRepeatedEventAcceleration = true;
                            continue;
                        }

                        if (TryApplyStableConstraintStateOptimization(
                            occurrence,
                            currentStateInfo,
                            carryOverState,
                            nextConstraints,
                            canonicalVariant,
                            stableStatePatterns,
                            stableStatePatternKeys))
                        {
                            usedRepeatedEventAcceleration = true;
                            continue;
                        }

                        if (TryApplyStoredStableStatePattern(
                            stableStatePatterns,
                            occurrence,
                            carryOverState,
                            nextConstraints,
                            out var stableVariant))
                        {
                            usedRepeatedEventAcceleration = true;
                            if (stableVariant != null)
                                AddOccurrenceCoverage(stableVariant, occurrence + 1, occurrence + 1, isOpenEnded: true);

                            continue;
                        }

                        if (TryApplyStoredProgressionPattern(
                            progressionPatterns,
                            occurrence,
                            carryOverState,
                            out var bridgedVariant,
                            out var bridgedPlan))
                        {
                            usedRepeatedEventAcceleration |=
                                bridgedPlan.IsOpenEnded ||
                                bridgedPlan.CoverageEndOccurrence > occurrence + 1 ||
                                (bridgedPlan.NextInterestingOccurrence.HasValue &&
                                 bridgedPlan.NextInterestingOccurrence.Value > occurrence + 1);

                            if (bridgedVariant != null)
                                AddOccurrenceCoverage(bridgedVariant, occurrence + 1, bridgedPlan.CoverageEndOccurrence, bridgedPlan.IsOpenEnded);

                            if (!bridgedPlan.IsOpenEnded && bridgedPlan.NextInterestingOccurrence.HasValue)
                            {
                                ScheduleRepeatedEventState(
                                    scheduledStates,
                                    bridgedPlan.NextInterestingOccurrence.Value,
                                    new RepeatedEventStateInfo
                                    {
                                        Memory = bridgedPlan.ScheduledState,
                                        StateValueConstraints = bridgedPlan.ScheduledConstraints
                                    },
                                    debugMode,
                                    ref analysisTruncated,
                                    occurrence);
                            }

                            if (analysisTruncated)
                                break;

                            continue;
                        }

                        if (carryOverState.Count == 0 && nextConstraints.Count == 0)
                            continue;

                        ScheduleRepeatedEventState(
                            scheduledStates,
                            occurrence + 1,
                            new RepeatedEventStateInfo
                            {
                                Memory = carryOverState,
                                StateValueConstraints = nextConstraints
                            },
                            debugMode,
                            ref analysisTruncated,
                            occurrence);

                        if (analysisTruncated)
                            break;
                    }

                    if (analysisTruncated)
                        break;
                }

                if (analyzeRepeatedEventOccurrences &&
                    !analysisTruncated &&
                    TryApplyStableRepeatedStateSetOptimization(
                        occurrence,
                        currentStates,
                        scheduledStates,
                        canonicalVariantsForOccurrence,
                        debugMode))
                {
                    usedRepeatedEventAcceleration = true;
                    maxAnalyzedOccurrence = Math.Max(maxAnalyzedOccurrence, occurrence + 1);
                    finalVariants = _pathAnalyzer.BuildFinalPathVariants(collectedVariants);
                    break;
                }

                finalVariants = _pathAnalyzer.BuildFinalPathVariants(collectedVariants);
                if (finalVariants.Count >= MaxRepeatedEventCanonicalOutcomes)
                {
                    if (debugMode)
                        AnalysisDebug.WriteLine($"Достигнут лимит канонических outcomes ({MaxRepeatedEventCanonicalOutcomes})");
                    break;
                }

                if (analysisTruncated)
                    break;
            }

            if (finalVariants == null)
            {
                finalVariants = _pathAnalyzer.BuildFinalPathVariants(collectedVariants);
            }
            ApplyOccurrenceDescriptions(finalVariants.Values, maxAnalyzedOccurrence, analysisTruncated, usedRepeatedEventAcceleration);
            RegisterCachedObjectVariants(
                analysisCacheScopeKey,
                startAddress,
                targetX,
                targetY,
                finalVariants,
                totalVisitedAddresses,
                usesInitialCoordinates,
                requireVisitedAddressEquivalence: reachableAddresses != null);

            if (debugMode)
                AnalysisDebug.WriteLine($"Канонических outcomes после схлопывания: {finalVariants.Count}");

            return finalVariants;
        }

        private SingleOccurrenceAnalysisResult AnalyzeSingleOccurrenceVariants(
            BinaryReader br,
            uint startAddress,
            byte targetX,
            byte targetY,
            string analysisCacheScopeKey,
            List<AlternativePath> predefinedAlternativePaths,
            HashSet<uint> reachableAddresses,
            Action<RegisterTracker> initializeRegisters,
            Dictionary<ushort, byte> initialEmulatedMemory8,
            HashSet<ushort> persistentStateAddresses,
            int repeatedEventOccurrence)
        {
            bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);
            bool summarizeRepeatedEventPass = debugMode && repeatedEventOccurrence > 1;

            if (TryGetCachedSingleOccurrenceAnalysis(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    initialEmulatedMemory8,
                    out var cachedResult))
            {
                if (reachableAddresses != null)
                    reachableAddresses.UnionWith(cachedResult.VisitedAddresses ?? Enumerable.Empty<uint>());

                if (summarizeRepeatedEventPass)
                    WriteSingleOccurrenceBriefDebug(repeatedEventOccurrence, cachedResult, cacheHit: true);

                return cachedResult;
            }

            var registerTracker = new RegisterTracker();
            initializeRegisters?.Invoke(registerTracker);
            var effectivePersistentStateAddresses = persistentStateAddresses == null
                ? new HashSet<ushort>((initialEmulatedMemory8 ?? new Dictionary<ushort, byte>()).Keys)
                : new HashSet<ushort>(persistentStateAddresses);
            var localVisitedAddresses = new HashSet<uint>();

            var processedBackEdges = new HashSet<(uint From, uint To)>();
            PathAnalysisResult mainPathResult;
            using (summarizeRepeatedEventPass ? AnalysisDebug.Suppress() : null)
            {
                mainPathResult = _codeExecutor.ExecuteCodeAtAddress(
                    br,
                    startAddress,
                    registerTracker,
                    new HashSet<uint>(),
                    0,
                    0,
                    0,
                    targetX,
                    targetY,
                    processedBackEdges,
                    invalidateReturnRegistersAfterExternalCall: true,
                    initialEmulatedMemory8: initialEmulatedMemory8 == null
                        ? null
                        : new Dictionary<ushort, byte>(initialEmulatedMemory8),
                    persistentStateAddresses: effectivePersistentStateAddresses);
            }
            localVisitedAddresses.UnionWith(mainPathResult.VisitedAddresses ?? Enumerable.Empty<uint>());

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

            bool suppressLoopBackAlternatives =
                mainPathResult.IsInLoop &&
                mainPathResult.LoopStartAddress != 0 &&
                mainPathResult.LoopSemantic != LoopSemanticKind.None;

            if (suppressLoopBackAlternatives)
            {
                effectiveAlternativePaths = effectiveAlternativePaths
                    .Where(p => !(p.TargetAddress < p.Address &&
                                  p.TargetAddress <= mainPathResult.LoopStartAddress))
                    .ToList();
            }

            if (effectiveAlternativePaths.Count > 0)
            {
                if (debugMode && !summarizeRepeatedEventPass)
                {
                    AnalysisDebug.WriteLine($"\nСобрано путей из основного анализа: {effectiveAlternativePaths.Count}");
                    foreach (var path in effectiveAlternativePaths)
                    {
                        AnalysisDebug.WriteLine($"  Путь: 0x{path.Address:X4} -> 0x{path.TargetAddress:X4} ({path.Condition})");
                    }
                }

                using (summarizeRepeatedEventPass ? AnalysisDebug.Suppress() : null)
                {
                    _pathAnalyzer.ProcessPaths(effectiveAlternativePaths, 1, 0,
                        mainDisplayTexts,
                        br,
                        targetX, targetY, allResults, null, processedBackEdges,
                        invalidateReturnRegistersAfterExternalCall: true,
                        reachableAddresses: localVisitedAddresses,
                        inheritedState: mainPathResult,
                        persistentStateAddresses: effectivePersistentStateAddresses);
                }
            }

            if (reachableAddresses != null)
                reachableAddresses.UnionWith(localVisitedAddresses);

            var rawLeafVariants = allResults
                .Where(variant => variant != null && variant.IsLeaf)
                .OrderBy(GetPathOrderKey)
                .ToList();

            if (debugMode && !summarizeRepeatedEventPass)
            {
                WriteRawLeafVariantDebugSummary(rawLeafVariants);
            }

            var result = new SingleOccurrenceAnalysisResult
            {
                CanonicalVariants = _pathAnalyzer.BuildFinalPathVariants(allResults),
                RawLeafVariants = rawLeafVariants,
                VisitedAddresses = localVisitedAddresses,
                UsesInitialCoordinates =
                    mainPathResult.UsesInitialCoordinates ||
                    allResults.Any(variant => variant?.UsesInitialCoordinates ?? false)
            };

            RegisterSingleOccurrenceAnalysis(
                analysisCacheScopeKey,
                startAddress,
                targetX,
                targetY,
                initialEmulatedMemory8,
                result);

            if (summarizeRepeatedEventPass)
                WriteSingleOccurrenceBriefDebug(repeatedEventOccurrence, result, cacheHit: false);

            return result;
        }

        private void WriteSingleOccurrenceBriefDebug(
            int occurrence,
            SingleOccurrenceAnalysisResult result,
            bool cacheHit)
        {
            if (result == null)
                return;

            var rawLeafVariants = (result.RawLeafVariants ?? new List<PathVariantInfo>())
                .Where(variant => variant != null)
                .ToList();
            int rawWithTexts = rawLeafVariants.Count(variant => (variant.Texts?.Count ?? 0) > 0);
            int rawOutcomeGroups = rawLeafVariants
                .Select(BuildRawLeafDebugGroupKey)
                .Distinct(StringComparer.Ordinal)
                .Count();

            AnalysisDebug.WriteLine(
                $"  Повторное наступление #{occurrence}: " +
                $"детальная трасса {(cacheHit ? "взята из кэша" : "свернута")}; " +
                $"rawLeafs={rawLeafVariants.Count}, rawWithTexts={rawWithTexts}, " +
                $"rawOutcomeGroups={rawOutcomeGroups}, canonical={result.CanonicalVariants?.Count ?? 0}, " +
                $"visited={result.VisitedAddresses?.Count ?? 0}");
        }

        private static decimal GetPathOrderKey(PathVariantInfo variant)
        {
            if (variant == null)
                return decimal.MaxValue;

            return variant.PathOrderKey > 0
                ? variant.PathOrderKey
                : variant.PathId;
        }

        private void WriteRawLeafVariantDebugSummary(List<PathVariantInfo> rawLeafVariants)
        {
            var variants = (rawLeafVariants ?? new List<PathVariantInfo>())
                .Where(variant => variant != null)
                .OrderBy(GetPathOrderKey)
                .ToList();

            AnalysisDebug.WriteLine($"\nСырые leaf-paths до канонизации: {variants.Count}");
            if (variants.Count == 0)
                return;

            int withTexts = variants.Count(variant => (variant.Texts?.Count ?? 0) > 0);
            int withPartyEffects = variants.Count(variant => (variant.PartyEffects?.Count ?? 0) > 0);
            int withBattles = variants.Count(variant =>
                variant.BattleMonsterCount.HasValue ||
                variant.BattleMonsterCountRange != null ||
                variant.IsBattleMonsterCountIndeterminate ||
                (variant.PersistentCounterProgressions?.Count ?? 0) > 0 ||
                (variant.DynamicRandomBoundDependencies?.Count ?? 0) > 0 ||
                (variant.BattleMonsters?.Count ?? 0) > 0 ||
                (variant.PartiallyDefinedBattles?.Count ?? 0) > 0);
            int withTeleport = variants.Count(variant => variant.HasTeleportTarget);
            int uniqueTextSets = variants
                .Select(BuildRawLeafTextKey)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var outcomeGroups = variants
                .GroupBy(BuildRawLeafDebugGroupKey, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Min(GetPathOrderKey))
                .ToList();

            AnalysisDebug.WriteLine(
                $"  Summary: withTexts={withTexts}, uniqueTextSets={uniqueTextSets}, " +
                $"withPartyEffects={withPartyEffects}, withBattles={withBattles}, " +
                $"withTeleport={withTeleport}, outcomeGroups={outcomeGroups.Count}");

            int sampleCount = Math.Min(RawLeafDebugSampleLimit, variants.Count);
            AnalysisDebug.WriteLine($"  Примеры RawPath: первые {sampleCount} из {variants.Count}");
            foreach (var rawVariant in variants.Take(sampleCount))
                WriteRawLeafVariantDebugSample(rawVariant, "    ");

            if (variants.Count > sampleCount)
            {
                AnalysisDebug.WriteLine(
                    $"  ... не показано {variants.Count - sampleCount} RawPath; " +
                    "ниже сгруппированный summary по видимому исходу.");
            }

            AnalysisDebug.WriteLine($"  Группы raw leaf-paths по видимому исходу: {outcomeGroups.Count}");
            int groupIndex = 1;
            foreach (var group in outcomeGroups.Take(RawLeafDebugGroupLimit))
            {
                var sample = group.OrderBy(GetPathOrderKey).First();
                AnalysisDebug.WriteLine(
                    $"    Group {groupIndex}: count={group.Count()}, pathIds={BuildPathIdSample(group)}");
                WriteRawLeafVariantDebugSample(sample, "      sample ");
                groupIndex++;
            }

            if (outcomeGroups.Count > RawLeafDebugGroupLimit)
            {
                int hiddenVariants = outcomeGroups
                    .Skip(RawLeafDebugGroupLimit)
                    .Sum(group => group.Count());
                AnalysisDebug.WriteLine(
                    $"    ... не показано групп: {outcomeGroups.Count - RawLeafDebugGroupLimit}, " +
                    $"RawPath в них: {hiddenVariants}");
            }
        }

        private void WriteRawLeafVariantDebugSample(PathVariantInfo rawVariant, string indent)
        {
            if (rawVariant == null)
                return;

            var texts = rawVariant.Texts ?? new List<string>();
            var partyEffects = rawVariant.PartyEffects ?? new List<PartyEffect>();
            AnalysisDebug.WriteLine(
                $"{indent}RawPath {rawVariant.PathId}: texts={texts.Count}, " +
                $"partyEffects={partyEffects.Count}, " +
                $"battleMonsters={rawVariant.BattleMonsters?.Count ?? 0}, " +
                $"partialBattles={rawVariant.PartiallyDefinedBattles?.Count ?? 0}, " +
                $"counterProgressions={rawVariant.PersistentCounterProgressions?.Count ?? 0}, " +
                $"dynamicRandomBounds={rawVariant.DynamicRandomBoundDependencies?.Count ?? 0}, " +
                $"battleCount={BuildRawLeafBattleCountText(rawVariant)}, " +
                $"teleport={BuildRawLeafTeleportText(rawVariant)}, " +
                $"callsRandomEncounter={rawVariant.CallsRandomEncounter}");

            foreach (var text in texts.Take(RawLeafDebugTextSampleLimit))
                AnalysisDebug.WriteLine($"{indent}  Text: {text}");
            if (texts.Count > RawLeafDebugTextSampleLimit)
                AnalysisDebug.WriteLine($"{indent}  ... ещё текстов: {texts.Count - RawLeafDebugTextSampleLimit}");

            foreach (var effect in partyEffects
                .Where(effect => effect != null)
                .Take(RawLeafDebugPartyEffectSampleLimit))
            {
                AnalysisDebug.WriteLine($"{indent}  PartyEffect: {PartyEffectSemantics.BuildDebugLine(effect)}");
            }
            if (partyEffects.Count > RawLeafDebugPartyEffectSampleLimit)
            {
                AnalysisDebug.WriteLine(
                    $"{indent}  ... ещё party effects: {partyEffects.Count - RawLeafDebugPartyEffectSampleLimit}");
            }

            foreach (var progression in rawVariant.PersistentCounterProgressions ?? Enumerable.Empty<PersistentCounterProgressionInfo>())
                AnalysisDebug.WriteLine($"{indent}  CounterProgression: {progression.ToDebugString()}");

            foreach (var dependency in rawVariant.DynamicRandomBoundDependencies ?? Enumerable.Empty<DynamicRandomBoundDependencyInfo>())
                AnalysisDebug.WriteLine($"{indent}  DynamicRandomBound: {dependency.ToDebugString()}");
        }

        private string BuildRawLeafDebugGroupKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            return string.Join("||", new[]
            {
                BuildRawLeafTextKey(variant),
                BuildRawLeafPartyEffectKey(variant),
                BuildRawLeafBattleKey(variant),
                BuildRawLeafTeleportKey(variant),
                $"random:{variant.RandomEncounterMonsterPowerCap}-{variant.RandomEncounterMonsterLevelCap}-{variant.RandomEncounterMonsterBatchCountCap}-{variant.DarkeningLevel}-{variant.RandomEncounterChance}-{variant.RandomEncounterRubicon}",
                $"calls:{variant.CallsRandomEncounter}/{variant.IsOnlyRandomEncounterJump}",
                $"table:{variant.HasAnyTableLoad}",
                $"loaded:{variant.LoadedValues?.Count ?? 0}",
                $"term:{variant.TerminatedByRepeatedBackEdge}/{variant.TerminatedByTerminalRet}",
                $"branchContribution:{variant.HasBranchSpecificContribution}"
            });
        }

        private string BuildRawLeafTextKey(PathVariantInfo variant)
        {
            return variant?.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";
        }

        private string BuildRawLeafPartyEffectKey(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null && variant.PartyEffects.Count > 0
                ? string.Join(";", variant.PartyEffects
                    .Where(effect => effect != null)
                    .Select(PartyEffectSemantics.BuildSemanticKey)
                    .OrderBy(key => key))
                : "<NO_PARTY>";
        }

        private string BuildRawLeafBattleKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NO_BATTLE>";

            string battleMonsters = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(monster => monster.Index)
                    .Select(monster =>
                        $"{monster.Index}:{monster.MonsterIndex1:X2}:{monster.MonsterIndex2:X2}:{monster.IsIndeterminate}"))
                : "<NO_MONSTERS>";
            string counterProgressions = variant.PersistentCounterProgressions != null && variant.PersistentCounterProgressions.Count > 0
                ? string.Join(";", variant.PersistentCounterProgressions
                    .Where(info => info != null)
                    .Select(info => info.GetIdentityKey())
                    .OrderBy(key => key))
                : "<NO_COUNTER_PROGRESSIONS>";
            string dynamicRandomBounds = variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0
                ? string.Join(";", variant.DynamicRandomBoundDependencies
                    .Where(info => info != null)
                    .Select(info => info.GetIdentityKey())
                    .OrderBy(key => key))
                : "<NO_DYNAMIC_RANDOM_BOUNDS>";

            return string.Join("|", new[]
            {
                BuildRawLeafBattleCountText(variant),
                $"indeterminate:{variant.IsBattleMonsterCountIndeterminate}",
                $"monsters:{battleMonsters}",
                $"progression:{counterProgressions}",
                $"dynamicRandomBounds:{dynamicRandomBounds}",
                $"partial:{variant.PartiallyDefinedBattles?.Count ?? 0}"
            });
        }

        private string BuildRawLeafBattleCountText(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL>";

            if (variant.BattleMonsterCountRange != null)
                return $"{variant.BattleMonsterCountRange.Min}-{variant.BattleMonsterCountRange.Max}";

            return variant.BattleMonsterCount.HasValue
                ? variant.BattleMonsterCount.Value.ToString()
                : "<none>";
        }

        private string BuildRawLeafTeleportKey(PathVariantInfo variant)
        {
            if (variant == null || !variant.HasTeleportTarget)
                return "<NO_TELEPORT>";

            return $"x:{BuildRawLeafValueOrRangeKey(variant.TeleportTargetX, variant.TeleportTargetXRange)};" +
                   $"y:{BuildRawLeafValueOrRangeKey(variant.TeleportTargetY, variant.TeleportTargetYRange)}";
        }

        private string BuildRawLeafTeleportText(PathVariantInfo variant)
        {
            if (variant == null || !variant.HasTeleportTarget)
                return "<none>";

            return $"X={BuildRawLeafValueOrRangeKey(variant.TeleportTargetX, variant.TeleportTargetXRange)}, " +
                   $"Y={BuildRawLeafValueOrRangeKey(variant.TeleportTargetY, variant.TeleportTargetYRange)}";
        }

        private string BuildRawLeafValueOrRangeKey(byte? value, ValueRange8 range)
        {
            if (range != null)
                return $"{range.Min}-{range.Max}";

            return value.HasValue
                ? value.Value.ToString()
                : "?";
        }

        private string BuildPathIdSample(IEnumerable<PathVariantInfo> variants)
        {
            var ids = (variants ?? Enumerable.Empty<PathVariantInfo>())
                .Where(variant => variant != null)
                .Select(variant => variant.PathId)
                .OrderBy(id => id)
                .ToList();

            const int sampleLimit = 8;
            string sample = string.Join(", ", ids.Take(sampleLimit));
            if (ids.Count > sampleLimit)
                sample += $", ... (+{ids.Count - sampleLimit})";

            return sample;
        }

        private void ScheduleRepeatedEventState(
            SortedDictionary<int, Dictionary<string, RepeatedEventStateInfo>> scheduledStates,
            int occurrence,
            RepeatedEventStateInfo stateInfo,
            bool debugMode,
            ref bool analysisTruncated,
            int? sourceOccurrence)
        {
            if (scheduledStates == null || stateInfo == null || occurrence <= 0)
                return;

            if (occurrence > MaxRepeatedEventOccurrences)
                return;

            if (!scheduledStates.TryGetValue(occurrence, out var statesForOccurrence))
            {
                statesForOccurrence = new Dictionary<string, RepeatedEventStateInfo>(StringComparer.Ordinal);
                scheduledStates[occurrence] = statesForOccurrence;
            }

            string stateKey = BuildMemoryStateKey(stateInfo.Memory, stateInfo.StateValueConstraints);
            if (statesForOccurrence.ContainsKey(stateKey))
                return;

            if (statesForOccurrence.Count >= MaxRepeatedEventStatesPerOccurrence)
            {
                analysisTruncated = true;
                if (debugMode)
                    AnalysisDebug.WriteLine($"Достигнут лимит состояний повторного анализа ({MaxRepeatedEventStatesPerOccurrence})");
                return;
            }

            statesForOccurrence[stateKey] = new RepeatedEventStateInfo
            {
                Memory = stateInfo.Memory == null
                    ? new Dictionary<ushort, byte>()
                    : new Dictionary<ushort, byte>(stateInfo.Memory),
                StateValueConstraints = CloneStateValueConstraints(stateInfo.StateValueConstraints)
            };

            if (debugMode && sourceOccurrence.HasValue && occurrence > sourceOccurrence.Value + 1)
                AnalysisDebug.WriteLine($"      Быстрый переход по повторному сценарию: следующее репрезентативное наступление #{occurrence}");
        }

        private void RegisterRepeatedEventProgressionPattern(
            List<RepeatedEventProgressionPattern> progressionPatterns,
            HashSet<string> progressionPatternKeys,
            BinaryReader br,
            Dictionary<ushort, byte> currentState,
            Dictionary<ushort, byte> carryOverState,
            PathVariantInfo rawLeaf,
            PathVariantInfo canonicalVariant)
        {
            if (progressionPatterns == null ||
                progressionPatternKeys == null ||
                br == null ||
                currentState == null ||
                carryOverState == null ||
                rawLeaf == null ||
                canonicalVariant == null)
            {
                return;
            }

            if (rawLeaf.StateValueConstraints == null || rawLeaf.StateValueConstraints.Count == 0)
                return;

            var changedAddresses = carryOverState.Keys
                .Where(addr =>
                    TryGetRepeatedEventStateValue(br, currentState, addr, out byte previousValue) &&
                    previousValue != carryOverState[addr])
                .ToList();

            if (changedAddresses.Count != 1)
                return;

            ushort address = changedAddresses[0];
            if (!rawLeaf.StateValueConstraints.TryGetValue(address, out var constraintInfo) ||
                constraintInfo == null ||
                constraintInfo.IsEmpty)
            {
                return;
            }

            if (!TryGetRepeatedEventStateValue(br, currentState, address, out byte currentValueByte))
                return;

            int currentValue = currentValueByte;
            int nextValue = carryOverState[address];
            int delta = nextValue - currentValue;
            if (delta == 0)
                return;

            string stateTemplateKey = BuildMemoryStateKeyExcludingAddress(carryOverState, address);
            string targetVariantKey = BuildRepeatedEventVariantTargetKey(canonicalVariant);
            string patternKey = string.Join("|",
                address.ToString("X4"),
                delta.ToString(),
                stateTemplateKey,
                BuildConstraintOnlyToken(constraintInfo),
                targetVariantKey);

            if (!progressionPatternKeys.Add(patternKey))
                return;

            progressionPatterns.Add(new RepeatedEventProgressionPattern
            {
                Address = address,
                Delta = delta,
                StateTemplateWithoutAddressKey = stateTemplateKey,
                TargetVariantKey = targetVariantKey,
                ConstraintInfo = constraintInfo.Clone(),
                TargetVariant = canonicalVariant
            });

        }

        private bool TryApplyStableSelfLoopOptimization(
            SingleOccurrenceAnalysisResult passResult,
            int occurrence,
            Dictionary<ushort, byte> currentState,
            Dictionary<ushort, byte> carryOverState,
            PathVariantInfo canonicalVariant,
            List<RepeatedEventStableStatePattern> stableStatePatterns,
            HashSet<string> stableStatePatternKeys)
        {
            if (passResult == null ||
                occurrence <= 0 ||
                canonicalVariant == null ||
                passResult.RawLeafVariants == null ||
                passResult.CanonicalVariants == null)
            {
                return false;
            }

            if (passResult.RawLeafVariants.Count != 1 || passResult.CanonicalVariants.Count != 1)
                return false;

            if (!AreStatesEqual(currentState, carryOverState))
                return false;

            AddOccurrenceCoverage(canonicalVariant, occurrence + 1, occurrence + 1, isOpenEnded: true);
            RegisterRepeatedEventStableStatePattern(
                stableStatePatterns,
                stableStatePatternKeys,
                carryOverState,
                new Dictionary<ushort, StateValueConstraintInfo>(),
                canonicalVariant);
            return true;
        }

        private bool TryApplyStableConstraintStateOptimization(
            int occurrence,
            RepeatedEventStateInfo currentStateInfo,
            Dictionary<ushort, byte> carryOverState,
            Dictionary<ushort, StateValueConstraintInfo> nextConstraints,
            PathVariantInfo canonicalVariant,
            List<RepeatedEventStableStatePattern> stableStatePatterns,
            HashSet<string> stableStatePatternKeys)
        {
            if (occurrence <= 0 ||
                currentStateInfo == null ||
                canonicalVariant == null)
            {
                return false;
            }

            string currentStateKey = BuildMemoryStateKey(
                currentStateInfo.Memory,
                currentStateInfo.StateValueConstraints);
            string nextStateKey = BuildMemoryStateKey(carryOverState, nextConstraints);

            if (!string.Equals(currentStateKey, nextStateKey, StringComparison.Ordinal))
                return false;

            AddOccurrenceCoverage(canonicalVariant, occurrence + 1, occurrence + 1, isOpenEnded: true);
            RegisterRepeatedEventStableStatePattern(
                stableStatePatterns,
                stableStatePatternKeys,
                carryOverState,
                nextConstraints,
                canonicalVariant);
            return true;
        }

        private bool TryApplyStableRepeatedStateSetOptimization(
            int occurrence,
            Dictionary<string, RepeatedEventStateInfo> currentStates,
            SortedDictionary<int, Dictionary<string, RepeatedEventStateInfo>> scheduledStates,
            IEnumerable<PathVariantInfo> canonicalVariantsForOccurrence,
            bool debugMode)
        {
            if (occurrence <= 0 ||
                currentStates == null ||
                currentStates.Count == 0 ||
                scheduledStates == null ||
                scheduledStates.Count != 1)
            {
                return false;
            }

            var nextOccurrence = scheduledStates.First();
            if (nextOccurrence.Key != occurrence + 1 ||
                nextOccurrence.Value == null ||
                nextOccurrence.Value.Count != currentStates.Count)
            {
                return false;
            }

            foreach (string stateKey in currentStates.Keys)
            {
                if (!nextOccurrence.Value.ContainsKey(stateKey))
                    return false;
            }

            foreach (var variant in canonicalVariantsForOccurrence
                .Where(variant => variant != null))
            {
                AddOccurrenceCoverage(variant, occurrence + 1, occurrence + 1, isOpenEnded: true);
            }

            scheduledStates.Clear();

            if (debugMode)
            {
                AnalysisDebug.WriteLine(
                    $"      Обнаружен стабильный цикл набора состояний, прекращаем повторный прогон после наступления #{occurrence}");
            }

            return true;
        }

        private void RegisterRepeatedEventStableStatePattern(
            List<RepeatedEventStableStatePattern> stableStatePatterns,
            HashSet<string> stableStatePatternKeys,
            Dictionary<ushort, byte> state,
            Dictionary<ushort, StateValueConstraintInfo> constraints,
            PathVariantInfo canonicalVariant)
        {
            if (stableStatePatterns == null || stableStatePatternKeys == null || canonicalVariant == null)
                return;

            string stateKey = BuildMemoryStateKey(state, constraints);
            if (string.IsNullOrWhiteSpace(stateKey))
                return;

            string targetVariantKey = BuildRepeatedEventVariantTargetKey(canonicalVariant);
            string patternKey = $"{stateKey}||{targetVariantKey}";
            if (!stableStatePatternKeys.Add(patternKey))
                return;

            stableStatePatterns.Add(new RepeatedEventStableStatePattern
            {
                StateKey = stateKey,
                TargetVariantKey = targetVariantKey,
                TargetVariant = canonicalVariant
            });
        }

        private bool TryApplyStoredStableStatePattern(
            List<RepeatedEventStableStatePattern> stableStatePatterns,
            int occurrence,
            Dictionary<ushort, byte> carryOverState,
            Dictionary<ushort, StateValueConstraintInfo> nextConstraints,
            out PathVariantInfo targetVariant)
        {
            targetVariant = null;

            if (stableStatePatterns == null ||
                stableStatePatterns.Count == 0 ||
                carryOverState == null ||
                carryOverState.Count == 0)
            {
                return false;
            }

            string stateKey = BuildMemoryStateKey(carryOverState, nextConstraints);
            if (string.IsNullOrWhiteSpace(stateKey))
                return false;

            var matchedPattern = stableStatePatterns
                .AsEnumerable()
                .Reverse()
                .FirstOrDefault(pattern =>
                    pattern != null &&
                    string.Equals(pattern.StateKey, stateKey, StringComparison.Ordinal) &&
                    pattern.TargetVariant != null);

            if (matchedPattern == null)
                return false;

            targetVariant = matchedPattern.TargetVariant;
            return true;
        }

        private bool TryApplyStoredProgressionPattern(
            List<RepeatedEventProgressionPattern> progressionPatterns,
            int occurrence,
            Dictionary<ushort, byte> carryOverState,
            out PathVariantInfo targetVariant,
            out RepeatedEventFastForwardPlan plan)
        {
            targetVariant = null;
            plan = null;

            if (progressionPatterns == null || progressionPatterns.Count == 0 || carryOverState == null)
                return false;

            var stateTemplateKeyCache = new Dictionary<ushort, string>();
            foreach (var pattern in progressionPatterns.AsEnumerable().Reverse())
            {
                if (pattern == null ||
                    pattern.TargetVariant == null ||
                    pattern.ConstraintInfo == null ||
                    pattern.ConstraintInfo.IsEmpty)
                {
                    continue;
                }

                if (!carryOverState.TryGetValue(pattern.Address, out byte currentValue))
                    continue;

                if (!stateTemplateKeyCache.TryGetValue(pattern.Address, out string currentStateTemplateKey))
                {
                    currentStateTemplateKey = BuildMemoryStateKeyExcludingAddress(carryOverState, pattern.Address);
                    stateTemplateKeyCache[pattern.Address] = currentStateTemplateKey;
                }

                if (!string.Equals(currentStateTemplateKey, pattern.StateTemplateWithoutAddressKey, StringComparison.Ordinal))
                    continue;

                if (!TryBuildRepeatedEventPlanFromConstraint(
                        occurrence + 1,
                        carryOverState,
                        pattern.Address,
                        pattern.Delta,
                        pattern.ConstraintInfo,
                        out var bridgedPlan))
                {
                    continue;
                }

                targetVariant = pattern.TargetVariant;
                plan = bridgedPlan;
                plan.ScheduledConstraints = new Dictionary<ushort, StateValueConstraintInfo>
                {
                    [pattern.Address] = pattern.ConstraintInfo.Clone()
                };
                return true;
            }

            return false;
        }

        private void MarkVariantOccurrenceRange(PathVariantInfo variant, int startOccurrence, int endOccurrence)
        {
            AddOccurrenceCoverage(variant, startOccurrence, endOccurrence, isOpenEnded: false);
        }

        private void AddOccurrenceCoverage(PathVariantInfo variant, int startOccurrence, int endOccurrence, bool isOpenEnded)
        {
            if (variant == null || startOccurrence <= 0)
                return;

            int normalizedEnd = isOpenEnded
                ? Math.Max(startOccurrence, endOccurrence)
                : Math.Max(startOccurrence, endOccurrence);

            variant.OccurrenceRanges ??= new List<OccurrenceRangeInfo>();
            variant.OccurrenceRanges.Add(new OccurrenceRangeInfo
            {
                Start = startOccurrence,
                End = normalizedEnd,
                IsOpenEnded = isOpenEnded
            });

            NormalizeOccurrenceCoverage(variant);
            variant.OccurrenceDescription = null;
        }

        private void NormalizeOccurrenceCoverage(PathVariantInfo variant)
        {
            if (variant == null)
                return;

            var mergedRanges = NormalizeOccurrenceRanges(
                (variant.OccurrenceRanges ?? new List<OccurrenceRangeInfo>())
                    .Concat((variant.OccurrenceIndices ?? new List<int>())
                        .Where(index => index > 0)
                        .Select(index => new OccurrenceRangeInfo
                        {
                            Start = index,
                            End = index,
                            IsOpenEnded = false
                        })));

            variant.OccurrenceRanges = mergedRanges;
            variant.OccurrenceIndices = ExpandOccurrenceIndices(mergedRanges);
        }

        private List<OccurrenceRangeInfo> NormalizeOccurrenceRanges(IEnumerable<OccurrenceRangeInfo> ranges)
        {
            var ordered = (ranges ?? Enumerable.Empty<OccurrenceRangeInfo>())
                .Where(range => range != null && range.Start > 0)
                .Select(range => new OccurrenceRangeInfo
                {
                    Start = range.Start,
                    End = Math.Max(range.Start, range.End),
                    IsOpenEnded = range.IsOpenEnded
                })
                .OrderBy(range => range.Start)
                .ThenBy(range => range.IsOpenEnded ? int.MaxValue : range.End)
                .ToList();

            if (ordered.Count == 0)
                return new List<OccurrenceRangeInfo>();

            var merged = new List<OccurrenceRangeInfo> { ordered[0] };
            foreach (var range in ordered.Skip(1))
            {
                var current = merged[merged.Count - 1];
                int currentEnd = current.IsOpenEnded ? int.MaxValue : Math.Max(current.Start, current.End);
                bool overlapsOrTouches = current.IsOpenEnded || range.Start <= currentEnd || range.Start == currentEnd + 1;

                if (overlapsOrTouches)
                {
                    current.End = Math.Max(current.End, range.End);
                    current.IsOpenEnded = current.IsOpenEnded || range.IsOpenEnded;
                    if (current.IsOpenEnded)
                        current.End = Math.Max(current.End, range.Start);
                    continue;
                }

                merged.Add(range);
            }

            return merged;
        }

        private List<int> ExpandOccurrenceIndices(IEnumerable<OccurrenceRangeInfo> ranges)
        {
            var result = new List<int>();
            foreach (var range in ranges ?? Enumerable.Empty<OccurrenceRangeInfo>())
            {
                if (range == null || range.Start <= 0)
                    continue;

                if (range.IsOpenEnded)
                {
                    result.Add(range.Start);
                    continue;
                }

                int end = Math.Max(range.Start, range.End);
                for (int value = range.Start; value <= end; value++)
                    result.Add(value);
            }

            return result.Distinct().OrderBy(value => value).ToList();
        }

        private bool TryBuildRepeatedEventFastForwardPlan(
            BinaryReader br,
            int occurrence,
            RepeatedEventStateInfo currentStateInfo,
            Dictionary<ushort, byte> carryOverState,
            Dictionary<ushort, StateValueConstraintInfo> nextConstraints,
            PathVariantInfo rawLeaf,
            string perfKey,
            out RepeatedEventFastForwardPlan plan)
        {
            plan = null;
            if (occurrence <= 0 ||
                br == null ||
                currentStateInfo == null ||
                carryOverState == null ||
                nextConstraints == null ||
                rawLeaf == null)
            {
                OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "other");
                return false;
            }

            var currentState = currentStateInfo.Memory ?? new Dictionary<ushort, byte>();
            var changedAddresses = carryOverState.Keys
                .Where(addr =>
                    TryGetRepeatedEventStateValue(br, currentState, addr, out byte previousValue) &&
                    previousValue != carryOverState[addr])
                .OrderBy(addr => addr)
                .ToList();

            if (changedAddresses.Count == 0)
            {
                OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "multi");
                return false;
            }

            var transitions = new List<RepeatedEventStateTransition>();
            foreach (ushort address in changedAddresses)
            {
                if (!(rawLeaf.MemoryReadBeforeWriteAddresses?.Contains(address) ?? false) &&
                    !nextConstraints.ContainsKey(address))
                {
                    OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "other");
                    return false;
                }

                if (!TryGetRepeatedEventStateValue(br, currentState, address, out byte currentValueByte))
                {
                    OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "other");
                    return false;
                }

                int currentValue = currentValueByte;
                int nextValue = carryOverState[address];
                int delta = nextValue - currentValue;
                if (delta == 0)
                {
                    OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "other");
                    return false;
                }

                if ((delta > 0 && nextValue <= currentValue) ||
                    (delta < 0 && nextValue >= currentValue))
                {
                    OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "other");
                    return false;
                }

                StateValueConstraintInfo constraintInfo = null;
                rawLeaf.StateValueConstraints?.TryGetValue(address, out constraintInfo);

                if ((constraintInfo == null || constraintInfo.IsEmpty) &&
                    !nextConstraints.TryGetValue(address, out constraintInfo))
                {
                    constraintInfo = null;
                }

                if (constraintInfo == null || constraintInfo.IsEmpty)
                {
                    OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "constraint");
                    return false;
                }

                transitions.Add(new RepeatedEventStateTransition
                {
                    Address = address,
                    Delta = delta,
                    ConstraintInfo = constraintInfo.Clone()
                });
            }

            int nextOccurrence = occurrence + 1;
            if (nextOccurrence > MaxRepeatedEventOccurrences)
            {
                OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "plan");
                return false;
            }

            if (!TryBuildRepeatedEventPlanFromConstraints(
                    nextOccurrence,
                    carryOverState,
                    transitions,
                    out plan))
            {
                OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "plan");
                return false;
            }

            if (plan.CoverageEndOccurrence < nextOccurrence &&
                !plan.IsOpenEnded &&
                !plan.NextInterestingOccurrence.HasValue)
            {
                OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "plan");
                return false;
            }

            plan.ScheduledConstraints = CloneStateValueConstraints(nextConstraints);
            OvrAnalysisPerfStats.RecordFastForwardDecision(perfKey, "success");
            return true;
        }

        private bool TryBuildRepeatedEventPlanFromConstraints(
            int occurrence,
            Dictionary<ushort, byte> stateAtOccurrenceStart,
            IEnumerable<RepeatedEventStateTransition> transitions,
            out RepeatedEventFastForwardPlan plan)
        {
            plan = null;

            var transitionList = (transitions ?? Enumerable.Empty<RepeatedEventStateTransition>())
                .Where(transition =>
                    transition != null &&
                    transition.Address != 0 &&
                    transition.Delta != 0 &&
                    transition.ConstraintInfo != null &&
                    !transition.ConstraintInfo.IsEmpty)
                .ToList();

            if (occurrence <= 0 ||
                stateAtOccurrenceStart == null ||
                transitionList.Count == 0)
            {
                return false;
            }

            var probeValues = new Dictionary<ushort, int>();
            foreach (var transition in transitionList)
            {
                if (!stateAtOccurrenceStart.TryGetValue(transition.Address, out byte currentValueByte))
                    return false;

                int nextValue = currentValueByte + transition.Delta;
                if (nextValue < 0 || nextValue > 0xFF || !AllowsStateValue(transition.ConstraintInfo, nextValue))
                    return false;

                probeValues[transition.Address] = nextValue;
            }

            int contiguousEndOccurrence = occurrence;
            int probeOccurrence = occurrence;

            while (true)
            {
                bool allTransitionsStillValid = true;
                foreach (var transition in transitionList)
                {
                    int currentProbeValue = probeValues[transition.Address];
                    int nextProbeValue = currentProbeValue + transition.Delta;
                    if (nextProbeValue < 0 ||
                        nextProbeValue > 0xFF ||
                        !AllowsStateValue(transition.ConstraintInfo, nextProbeValue))
                    {
                        allTransitionsStillValid = false;
                        break;
                    }
                }

                if (!allTransitionsStillValid)
                    break;

                foreach (var transition in transitionList)
                    probeValues[transition.Address] += transition.Delta;

                probeOccurrence++;
                contiguousEndOccurrence = probeOccurrence;
            }

            int immediateNextOccurrence = contiguousEndOccurrence + 1;
            int coverageEndOccurrence = Math.Min(MaxRepeatedEventOccurrences, contiguousEndOccurrence);
            bool isOpenEnded = contiguousEndOccurrence > MaxRepeatedEventOccurrences;

            plan = new RepeatedEventFastForwardPlan
            {
                CoverageEndOccurrence = Math.Max(occurrence, coverageEndOccurrence),
                IsOpenEnded = isOpenEnded
            };

            if (isOpenEnded)
                return true;

            plan.NextInterestingOccurrence = immediateNextOccurrence;
            plan.ScheduledState = new Dictionary<ushort, byte>(stateAtOccurrenceStart);

            foreach (var transition in transitionList)
            {
                if (!stateAtOccurrenceStart.TryGetValue(transition.Address, out byte currentValueByte))
                    return false;

                int scheduledValue = currentValueByte + transition.Delta * Math.Max(0, immediateNextOccurrence - occurrence);
                if (scheduledValue < 0 || scheduledValue > 0xFF)
                    return false;

                plan.ScheduledState[transition.Address] = (byte)scheduledValue;
            }

            return true;
        }

        private bool TryBuildRepeatedEventPlanFromConstraint(
            int occurrence,
            Dictionary<ushort, byte> stateAtOccurrenceStart,
            ushort address,
            int delta,
            StateValueConstraintInfo constraintInfo,
            out RepeatedEventFastForwardPlan plan)
        {
            plan = null;
            if (occurrence <= 0 ||
                stateAtOccurrenceStart == null ||
                delta == 0 ||
                constraintInfo == null ||
                constraintInfo.IsEmpty ||
                !stateAtOccurrenceStart.TryGetValue(address, out byte currentValueByte))
            {
                return false;
            }

            int currentValue = currentValueByte;
            int nextValue = currentValue + delta;
            if (nextValue < 0 || nextValue > 0xFF)
                return false;

            if (!AllowsStateValue(constraintInfo, nextValue))
                return false;

            int contiguousEndOccurrence = occurrence;
            int contiguousEndValue = nextValue;
            int probeValue = nextValue;
            int probeOccurrence = occurrence;

            while (true)
            {
                int nextProbeValue = probeValue + delta;
                if (nextProbeValue < 0 || nextProbeValue > 0xFF)
                    break;

                int nextProbeOccurrence = probeOccurrence + 1;
                if (!AllowsStateValue(constraintInfo, nextProbeValue))
                    break;

                probeValue = nextProbeValue;
                probeOccurrence = nextProbeOccurrence;
                contiguousEndOccurrence = probeOccurrence;
                contiguousEndValue = probeValue;
            }

            int immediateNextValue = contiguousEndValue + delta;
            int immediateNextOccurrence = contiguousEndOccurrence + 1;

            int coverageEndOccurrence = Math.Min(MaxRepeatedEventOccurrences, contiguousEndOccurrence);
            bool isOpenEnded = contiguousEndOccurrence > MaxRepeatedEventOccurrences;

            plan = new RepeatedEventFastForwardPlan
            {
                CoverageEndOccurrence = Math.Max(occurrence, coverageEndOccurrence),
                IsOpenEnded = isOpenEnded
            };

            if (isOpenEnded)
                return true;

            if (immediateNextValue < 0 || immediateNextValue > 0xFF)
                return true;

            plan.NextInterestingOccurrence = immediateNextOccurrence;

            int scheduledValue = currentValue + delta * Math.Max(0, immediateNextOccurrence - occurrence);
            if (scheduledValue < 0 || scheduledValue > 0xFF)
                return false;

            plan.ScheduledState = new Dictionary<ushort, byte>(stateAtOccurrenceStart)
            {
                [address] = (byte)scheduledValue
            };

            return true;
        }

        private bool AllowsStateValue(StateValueConstraintInfo constraintInfo, int value)
        {
            if (constraintInfo == null || constraintInfo.IsEmpty || value < 0 || value > 0xFF)
                return false;

            byte byteValue = (byte)value;

            bool hasBroadConstraint =
                (constraintInfo.LowerInclusiveValues?.Count ?? 0) > 0 ||
                (constraintInfo.UpperInclusiveValues?.Count ?? 0) > 0;

            bool broadMatch = true;
            if (hasBroadConstraint)
            {
                if ((constraintInfo.LowerInclusiveValues?.Count ?? 0) > 0)
                    broadMatch &= value >= constraintInfo.LowerInclusiveValues.Max();

                if ((constraintInfo.UpperInclusiveValues?.Count ?? 0) > 0)
                    broadMatch &= value <= constraintInfo.UpperInclusiveValues.Min();

                if ((constraintInfo.ExcludedValues?.Count ?? 0) > 0)
                    broadMatch &= !constraintInfo.ExcludedValues.Contains(byteValue);
            }

            bool exactMatch =
                (constraintInfo.ExactValues?.Count ?? 0) > 0 &&
                constraintInfo.ExactValues.Contains(byteValue);

            if (hasBroadConstraint)
                return broadMatch || exactMatch;

            if (exactMatch)
                return true;

            if ((constraintInfo.ExcludedValues?.Count ?? 0) > 0)
                return !constraintInfo.ExcludedValues.Contains(byteValue);

            return false;
        }

        private bool TryGetRepeatedEventStateValue(
            BinaryReader br,
            Dictionary<ushort, byte> currentState,
            ushort address,
            out byte value)
        {
            value = 0;
            if (currentState != null && currentState.TryGetValue(address, out value))
                return true;

            return TryReadOverlayByte(br, address, out value);
        }

        private HashSet<ushort> BuildRepeatedEventRelevantAddresses(PathVariantInfo rawLeafVariant)
        {
            if (rawLeafVariant == null)
                return new HashSet<ushort>();

            return BuildRepeatedEventRelevantAddresses(new[] { rawLeafVariant });
        }

        private HashSet<ushort> BuildRepeatedEventRelevantAddresses(IEnumerable<PathVariantInfo> rawLeafVariants)
        {
            var relevantAddresses = new HashSet<ushort>();
            foreach (var rawLeaf in rawLeafVariants ?? Enumerable.Empty<PathVariantInfo>())
            {
                var firstAccessKinds = rawLeaf.PersistentMemoryFirstAccessKinds;
                if (firstAccessKinds != null && firstAccessKinds.Count > 0)
                {
                    relevantAddresses.UnionWith(firstAccessKinds
                        .Where(kvp => kvp.Value == PersistentMemoryFirstAccessKind.Read)
                        .Select(kvp => kvp.Key));
                    continue;
                }

                relevantAddresses.UnionWith(rawLeaf.MemoryReadBeforeWriteAddresses ?? Enumerable.Empty<ushort>());
            }

            return relevantAddresses;
        }

        private Dictionary<ushort, StateValueConstraintInfo> FilterStateValueConstraintsForRepeatedEvent(
            Dictionary<ushort, StateValueConstraintInfo> constraints,
            HashSet<ushort> relevantAddresses)
        {
            if (constraints == null || constraints.Count == 0)
                return new Dictionary<ushort, StateValueConstraintInfo>();

            if (relevantAddresses == null || relevantAddresses.Count == 0)
                return new Dictionary<ushort, StateValueConstraintInfo>();

            return constraints
                .Where(kvp =>
                    relevantAddresses.Contains(kvp.Key) &&
                    kvp.Value != null &&
                    !kvp.Value.IsEmpty)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone());
        }

        private Dictionary<ushort, byte> BuildCarryOverState(
            BinaryReader br,
            Dictionary<ushort, byte> currentState,
            PathVariantInfo leafVariant,
            HashSet<ushort> relevantAddresses)
        {
            var result = new Dictionary<ushort, byte>();
            if (leafVariant == null)
                return result;

            var addressesToCarry = new HashSet<ushort>(relevantAddresses ?? Enumerable.Empty<ushort>());

            foreach (var addr in addressesToCarry.OrderBy(value => value))
            {
                if (addr == 0x3C38 || addr == 0x3C39)
                    continue;

                if (leafVariant.ExitEmulatedMemory8 != null &&
                    leafVariant.ExitEmulatedMemory8.TryGetValue(addr, out byte value))
                {
                    if (TryReadOverlayByte(br, addr, out byte overlayValue) && overlayValue == value)
                        continue;

                    result[addr] = value;
                    continue;
                }

                if (currentState != null && currentState.TryGetValue(addr, out byte currentValue))
                    result[addr] = currentValue;
            }

            return result;
        }

        private Dictionary<ushort, byte> BuildStateWithoutAddress(
            Dictionary<ushort, byte> state,
            ushort excludedAddress)
        {
            return (state ?? new Dictionary<ushort, byte>())
                .Where(kvp => kvp.Key != excludedAddress)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private string BuildMemoryStateKeyExcludingAddress(
            Dictionary<ushort, byte> state,
            ushort excludedAddress)
        {
            if (state == null || state.Count == 0)
                return "<EMPTY>";

            var remainingAddresses = state.Keys
                .Where(address => address != excludedAddress)
                .OrderBy(address => address)
                .ToList();

            if (remainingAddresses.Count == 0)
                return "<EMPTY>";

            return string.Join(";",
                remainingAddresses.Select(address => $"{address:X4}={state[address]:X2}"));
        }

        private bool AreStatesEqual(
            Dictionary<ushort, byte> left,
            Dictionary<ushort, byte> right)
        {
            left ??= new Dictionary<ushort, byte>();
            right ??= new Dictionary<ushort, byte>();

            if (left.Count != right.Count)
                return false;

            foreach (var kvp in left)
            {
                if (!right.TryGetValue(kvp.Key, out byte otherValue) || otherValue != kvp.Value)
                    return false;
            }

            return true;
        }

        private string BuildMemoryStateKey(
            Dictionary<ushort, byte> state,
            Dictionary<ushort, StateValueConstraintInfo> stateValueConstraints = null)
        {
            var stateAddresses = state?.Keys ?? Enumerable.Empty<ushort>();
            var constraintAddresses = (stateValueConstraints ?? new Dictionary<ushort, StateValueConstraintInfo>())
                .Where(kvp => kvp.Value != null && !kvp.Value.IsEmpty)
                .Select(kvp => kvp.Key);

            var allAddresses = stateAddresses
                .Concat(constraintAddresses)
                .Distinct()
                .OrderBy(address => address)
                .ToList();

            if (allAddresses.Count == 0)
                return "<EMPTY>";

            return string.Join(";",
                allAddresses.Select(address =>
                    {
                        byte value = 0;
                        StateValueConstraintInfo constraintInfo = null;
                        state?.TryGetValue(address, out value);
                        stateValueConstraints?.TryGetValue(address, out constraintInfo);

                        string canonicalValue = state != null && state.ContainsKey(address)
                            ? BuildCanonicalStateValueToken(value, constraintInfo)
                            : BuildConstraintOnlyToken(constraintInfo);

                        return $"{address:X4}={canonicalValue}";
                    }));
        }

        private Dictionary<ushort, StateValueConstraintInfo> MergeStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> current,
            Dictionary<ushort, StateValueConstraintInfo> next)
        {
            var merged = CloneStateValueConstraints(current);
            if (next == null || next.Count == 0)
                return merged;

            foreach (var kvp in next)
            {
                if (!merged.TryGetValue(kvp.Key, out var existing))
                {
                    merged[kvp.Key] = kvp.Value?.Clone() ?? new StateValueConstraintInfo();
                    continue;
                }

                existing.MergeFrom(kvp.Value);
            }

            return merged;
        }

        private Dictionary<ushort, StateValueConstraintInfo> CloneStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> source)
        {
            if (source == null || source.Count == 0)
                return new Dictionary<ushort, StateValueConstraintInfo>();

            return source.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.Clone() ?? new StateValueConstraintInfo());
        }

        private string BuildCanonicalStateValueToken(byte value, StateValueConstraintInfo constraintInfo)
        {
            if (constraintInfo == null || constraintInfo.IsEmpty)
                return value.ToString("X2");

            if (constraintInfo.ExactValues.Contains(value))
                return value.ToString("X2");

            if (constraintInfo.LowerInclusiveValues != null && constraintInfo.LowerInclusiveValues.Count > 0)
            {
                byte lowerTailStart = constraintInfo.LowerInclusiveValues.Max();
                bool excluded = constraintInfo.ExcludedValues != null && constraintInfo.ExcludedValues.Contains(value);
                if (value >= lowerTailStart && !excluded)
                    return $"{lowerTailStart:X2}-FF";
            }

            if (constraintInfo.UpperInclusiveValues != null && constraintInfo.UpperInclusiveValues.Count > 0)
            {
                byte upperTailEnd = constraintInfo.UpperInclusiveValues.Min();
                bool excluded = constraintInfo.ExcludedValues != null && constraintInfo.ExcludedValues.Contains(value);
                if (value <= upperTailEnd && !excluded)
                    return $"00-{upperTailEnd:X2}";
            }

            return value.ToString("X2");
        }

        private string BuildConstraintOnlyToken(StateValueConstraintInfo constraintInfo)
        {
            var normalizedConstraint = NormalizeConstraintInfoForStateKey(constraintInfo);
            if (normalizedConstraint == null || normalizedConstraint.IsEmpty)
                return "??";

            var parts = new List<string>();

            if (normalizedConstraint.ExactValues != null && normalizedConstraint.ExactValues.Count > 0)
                parts.Add("EQ:" + string.Join(",", normalizedConstraint.ExactValues.OrderBy(value => value).Select(value => value.ToString("X2"))));

            if (normalizedConstraint.LowerInclusiveValues != null && normalizedConstraint.LowerInclusiveValues.Count > 0)
                parts.Add("GE:" + normalizedConstraint.LowerInclusiveValues.Max().ToString("X2"));

            if (normalizedConstraint.UpperInclusiveValues != null && normalizedConstraint.UpperInclusiveValues.Count > 0)
                parts.Add("LE:" + normalizedConstraint.UpperInclusiveValues.Min().ToString("X2"));

            if (normalizedConstraint.ExcludedValues != null && normalizedConstraint.ExcludedValues.Count > 0)
                parts.Add("NE:" + string.Join(",", normalizedConstraint.ExcludedValues.OrderBy(value => value).Select(value => value.ToString("X2"))));

            return parts.Count == 0 ? "??" : string.Join("|", parts);
        }

        private StateValueConstraintInfo NormalizeConstraintInfoForStateKey(StateValueConstraintInfo constraintInfo)
        {
            if (constraintInfo == null || constraintInfo.IsEmpty)
                return null;

            var normalized = constraintInfo.Clone();

            if (normalized.LowerInclusiveValues.Count > 1)
            {
                byte lowerBound = normalized.LowerInclusiveValues.Max();
                normalized.LowerInclusiveValues.Clear();
                normalized.LowerInclusiveValues.Add(lowerBound);
            }

            if (normalized.UpperInclusiveValues.Count > 1)
            {
                byte upperBound = normalized.UpperInclusiveValues.Min();
                normalized.UpperInclusiveValues.Clear();
                normalized.UpperInclusiveValues.Add(upperBound);
            }

            if (normalized.ExactValues.Count > 0)
            {
                if (normalized.LowerInclusiveValues.Count == 0 &&
                    normalized.UpperInclusiveValues.Count == 0)
                {
                    normalized.ExcludedValues.Clear();
                }
                else if (normalized.ExcludedValues.Count > 0)
                {
                    normalized.ExcludedValues.ExceptWith(normalized.ExactValues);
                }
            }

            return normalized;
        }

        private void ApplyOccurrenceDescriptions(
            IEnumerable<PathVariantInfo> variants,
            int maxAnalyzedOccurrence,
            bool analysisTruncated,
            bool usedRepeatedEventAcceleration)
        {
            var variantList = (variants ?? Enumerable.Empty<PathVariantInfo>())
                .Where(variant => variant != null)
                .ToList();

            var suppressOccurrenceDescriptionFor = new HashSet<PathVariantInfo>();
            foreach (var group in variantList.GroupBy(BuildOccurrenceInsensitiveLoopFallbackKey))
            {
                if (!group.Any(HasConditionalLoopSubsetOutcomeEffectsForOccurrenceSuppression))
                    continue;

                foreach (var variant in group.Where(IsPureEmptyNoOpVariantForOccurrenceSuppression))
                    suppressOccurrenceDescriptionFor.Add(variant);
            }

            foreach (var variant in variantList)
            {
                if (variant == null)
                    continue;

                NormalizeOccurrenceCoverage(variant);
                variant.OccurrenceDescription = suppressOccurrenceDescriptionFor.Contains(variant)
                    ? null
                    : usedRepeatedEventAcceleration &&
                      !variant.HasRepeatedEventOccurrenceSensitivity &&
                      HasRepresentativeOccurrenceGaps(variant.OccurrenceRanges, maxAnalyzedOccurrence)
                        ? null
                    : BuildOccurrenceDescription(
                        variant.OccurrenceRanges,
                        maxAnalyzedOccurrence,
                        analysisTruncated);
            }
        }

        private bool HasRepresentativeOccurrenceGaps(
            List<OccurrenceRangeInfo> occurrenceRanges,
            int maxAnalyzedOccurrence)
        {
            var ranges = NormalizeOccurrenceRanges(occurrenceRanges);
            if (ranges.Count == 0 || maxAnalyzedOccurrence <= 1)
                return false;

            if (ranges.Count > 1)
                return true;

            var range = ranges[0];
            int end = Math.Max(range.Start, range.End);
            return range.Start > 1 || (!range.IsOpenEnded && end < maxAnalyzedOccurrence);
        }

        private bool IsPureEmptyNoOpVariantForOccurrenceSuppression(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.Texts != null && variant.Texts.Count > 0)
                return false;

            if (variant.PartyEffects != null && variant.PartyEffects.Count > 0)
                return false;

            return !variant.RandomEncounterMonsterPowerCap.HasValue &&
                   !variant.RandomEncounterMonsterLevelCap.HasValue &&
                   !variant.RandomEncounterMonsterBatchCountCap.HasValue &&
                   !variant.DarkeningLevel.HasValue &&
                   !variant.RandomEncounterChance.HasValue &&
                   !variant.RandomEncounterRubicon.HasValue &&
                   !variant.CallsRandomEncounter &&
                   !variant.TeleportTargetX.HasValue &&
                   !variant.TeleportTargetY.HasValue &&
                   variant.TeleportTargetXRange == null &&
                   variant.TeleportTargetYRange == null &&
                   !variant.BattleMonsterCount.HasValue &&
                   variant.BattleMonsterCountRange == null &&
                   !variant.IsBattleMonsterCountIndeterminate &&
                   (variant.PersistentCounterProgressions == null || variant.PersistentCounterProgressions.Count == 0) &&
                   (variant.DynamicRandomBoundDependencies == null || variant.DynamicRandomBoundDependencies.Count == 0) &&
                   !variant.HasAnyTableLoad &&
                   (variant.BattleMonsters == null || variant.BattleMonsters.Count == 0) &&
                   (variant.PartiallyDefinedBattles == null || variant.PartiallyDefinedBattles.Count == 0) &&
                   (variant.LoadedValues == null || variant.LoadedValues.Count == 0);
        }

        private bool HasConditionalLoopSubsetOutcomeEffectsForOccurrenceSuppression(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Any(IsConditionalLoopSubsetOutcomeEffectForOccurrenceSuppression);
        }

        private bool IsConditionalLoopSubsetOutcomeEffectForOccurrenceSuppression(PartyEffect effect)
        {
            return effect != null &&
                   PartyEffectSemantics.IsStateChanging(effect) &&
                   PartyEffectSemantics.IsLoopDerived(effect) &&
                   PartyEffectSemantics.GetEffectiveScope(effect) == PartyEffectScope.PartySubset &&
                   (PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None ||
                    PartyEffectSemantics.HasEffectiveGuardPredicates(effect));
        }

        private string BuildOccurrenceInsensitiveLoopFallbackKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string progressionKey = variant.PersistentCounterProgressions != null && variant.PersistentCounterProgressions.Count > 0
                ? string.Join(";", variant.PersistentCounterProgressions
                    .Where(info => info != null)
                    .Select(info => info.GetIdentityKey())
                    .OrderBy(key => key))
                : "<NO_COUNTER_PROGRESSIONS>";
            string dynamicBoundKey = variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0
                ? string.Join(";", variant.DynamicRandomBoundDependencies
                    .Where(info => info != null)
                    .Select(info => info.GetIdentityKey())
                    .OrderBy(key => key))
                : "<NO_DYNAMIC_RANDOM_BOUNDS>";
            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}|{progressionKey}|{dynamicBoundKey}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(monster => monster.Index)
                    .Select(monster => $"{monster.Index}:{monster.MonsterIndex1:X2}:{monster.MonsterIndex2:X2}:{monster.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(battle => battle.BxIndex)
                    .Select(battle => battle.GetIdentityKey()))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(value => value.BxIndex)
                    .ThenBy(value => value.SourceAddr)
                    .ThenBy(value => value.RegName)
                    .Select(value => $"{value.BxIndex}:{value.RegName}:{value.Value:X2}:{value.SourceAddr:X4}:{value.IsFirstTable}:{value.IsSaved}"))
                : "<NO_LOADS>";

            return $"{statKey}||{battleKey}||{partialKey}||{loadKey}||{variant.ProbabilityNumerator}/{variant.ProbabilityDenominator}";
        }

        private string BuildRepeatedEventVariantTargetKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string partyKey = variant.PartyEffects != null && variant.PartyEffects.Count > 0
                ? string.Join(";", variant.PartyEffects
                    .Where(effect => effect != null)
                    .Select(PartyEffectSemantics.BuildSemanticKey)
                    .OrderBy(key => key))
                : "<NO_PARTY>";

            string branchKey = variant.BranchChoices != null && variant.BranchChoices.Count > 0
                ? string.Join(";", variant.BranchChoices
                    .Where(choice => choice != null)
                    .Select(choice => choice.GetIdentityKey()))
                : "<NO_BRANCHES>";

            return $"{textKey}||{BuildOccurrenceInsensitiveLoopFallbackKey(variant)}||{partyKey}||{branchKey}";
        }

        private bool TryGetCachedObjectVariants(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            HashSet<uint> reachableAddresses,
            out Dictionary<int, PathVariantInfo> cachedVariants)
        {
            cachedVariants = null;
            if (string.IsNullOrWhiteSpace(analysisCacheScopeKey))
                return false;

            string cacheKey = $"{analysisCacheScopeKey}|{startAddress:X4}";
            if (!_objectVariantAnalysisCache.TryGetValue(cacheKey, out var entry) ||
                entry == null ||
                !entry.IsCoordinateIndependentVerified ||
                entry.ReusableVariants == null ||
                entry.ReusableVariants.Count == 0)
            {
                return false;
            }

            if (reachableAddresses != null)
                reachableAddresses.UnionWith(entry.ReusableVisitedAddresses ?? Enumerable.Empty<uint>());

            AnalysisDebug.WriteLine(
                $"    CACHE HIT object-variants для клетки ({targetX},{targetY}): {cacheKey}");
            cachedVariants = CloneFinalVariants(entry.ReusableVariants);
            return true;
        }

        private void RegisterCachedObjectVariants(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            Dictionary<int, PathVariantInfo> finalVariants,
            HashSet<uint> visitedAddresses,
            bool usesInitialCoordinates,
            bool requireVisitedAddressEquivalence)
        {
            if (AnalysisDebug.ShouldDisableCacheFor(targetX, targetY))
                return;

            if (string.IsNullOrWhiteSpace(analysisCacheScopeKey) ||
                finalVariants == null ||
                finalVariants.Count == 0)
            {
                return;
            }

            if (ShouldSkipReusableObjectVariantCache(analysisCacheScopeKey, finalVariants))
                return;

            string cacheKey = $"{analysisCacheScopeKey}|{startAddress:X4}";
            string signature = BuildFinalVariantsCacheSignature(finalVariants);
            string visitedKey = BuildVisitedAddressesKey(visitedAddresses);

            if (!_objectVariantAnalysisCache.TryGetValue(cacheKey, out var entry) || entry == null)
            {
                _objectVariantAnalysisCache[cacheKey] = new ObjectVariantAnalysisCacheEntry
                {
                    SampleX = targetX,
                    SampleY = targetY,
                    SampleSignature = signature,
                    SampleVisitedKey = requireVisitedAddressEquivalence ? visitedKey : string.Empty,
                    SampleUsesInitialCoordinates = usesInitialCoordinates,
                    MatchingCoordinatePairs = new HashSet<string>(StringComparer.Ordinal)
                    {
                        BuildCoordinatePairKey(targetX, targetY)
                    },
                    MatchingXValues = new HashSet<byte> { targetX },
                    MatchingYValues = new HashSet<byte> { targetY },
                    SampleVariants = CloneFinalVariants(finalVariants),
                    SampleVisitedAddresses = new HashSet<uint>(visitedAddresses ?? Enumerable.Empty<uint>())
                };
                return;
            }

            if (entry.IsCoordinateIndependentVerified)
                return;

            bool coordinateSensitiveComparison = entry.SampleUsesInitialCoordinates || usesInitialCoordinates;
            if (entry.CoordinateSensitiveMismatchObserved)
                return;

            if (entry.SampleX == targetX && entry.SampleY == targetY)
                return;

            bool signatureMismatch =
                !string.Equals(entry.SampleSignature, signature, StringComparison.Ordinal);
            bool visitedMismatch =
                requireVisitedAddressEquivalence &&
                !string.Equals(entry.SampleVisitedKey, visitedKey, StringComparison.Ordinal);

            if (signatureMismatch || visitedMismatch)
            {
                if (coordinateSensitiveComparison)
                    entry.CoordinateSensitiveMismatchObserved = true;
                return;
            }

            if (coordinateSensitiveComparison)
            {
                entry.SampleUsesInitialCoordinates = true;
                entry.MatchingCoordinatePairs.Add(BuildCoordinatePairKey(targetX, targetY));
                entry.MatchingXValues.Add(targetX);
                entry.MatchingYValues.Add(targetY);

                bool hasEnoughCoordinateEvidence = HasEnoughCoordinateEvidence(cacheKey, entry);

                if (!hasEnoughCoordinateEvidence)
                {
                    OvrAnalysisPerfStats.RecordCoordinateUsageCacheRejection(cacheKey);
                    return;
                }
            }

            entry.IsCoordinateIndependentVerified = true;
            entry.ReusableVariants = CloneFinalVariants(finalVariants);
            entry.ReusableVisitedAddresses = new HashSet<uint>(
                (entry.SampleVisitedAddresses ?? Enumerable.Empty<uint>())
                    .Concat(visitedAddresses ?? Enumerable.Empty<uint>()));
        }

        private static bool ShouldSkipReusableObjectVariantCache(
            string analysisCacheScopeKey,
            Dictionary<int, PathVariantInfo> finalVariants)
        {
            if (string.IsNullOrWhiteSpace(analysisCacheScopeKey) ||
                finalVariants == null ||
                finalVariants.Count == 0)
            {
                return false;
            }

            return finalVariants.Values.Any(IsCoordinateSensitiveBattleTableVariant);
        }

        private static bool IsCoordinateSensitiveBattleTableVariant(PathVariantInfo variant)
        {
            if (variant == null || !variant.UsesInitialCoordinates)
                return false;

            return ((variant.BattleMonsters?.Count ?? 0) > 0) ||
                   ((variant.PartiallyDefinedBattles?.Count ?? 0) > 0) ||
                   variant.HasAnyTableLoad ||
                   ((variant.LoadedValues?.Count ?? 0) > 0);
        }

        private string BuildCoordinatePairKey(byte x, byte y)
        {
            return $"{x},{y}";
        }

        private bool HasEnoughCoordinateEvidence(string cacheKey, ObjectVariantAnalysisCacheEntry entry)
        {
            if (entry == null)
                return false;

            if (entry.MatchingCoordinatePairs.Count < 4)
                return false;

            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                if (cacheKey.Contains("|XCoord|", StringComparison.Ordinal))
                    return entry.MatchingXValues.Count >= 2;

                if (cacheKey.Contains("|YCoord|", StringComparison.Ordinal))
                    return entry.MatchingYValues.Count >= 2;
            }

            return entry.MatchingXValues.Count >= 2 &&
                   entry.MatchingYValues.Count >= 2;
        }

        private string BuildFinalVariantsCacheSignature(Dictionary<int, PathVariantInfo> finalVariants)
        {
            if (finalVariants == null || finalVariants.Count == 0)
                return "<NO_VARIANTS>";

            return string.Join("##", finalVariants
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var variant = kvp.Value;
                    string occurrenceKey = variant?.GetOccurrenceDescription() ?? string.Empty;
                    return $"{kvp.Key}:{BuildRepeatedEventVariantTargetKey(variant)}:{occurrenceKey}";
                }));
        }

        private string BuildMacroAnalysisCacheScopeKey(CoordinateComparison comparison)
        {
            if (comparison == null)
                return "macro:<NULL>";

            string branchDescriptor = comparison.IsLinear
                ? $"linear:{comparison.LinearStart:X2}-{comparison.LinearEnd:X2}"
                : $"value:{comparison.Value:X2}";

            var parts = new List<string>
            {
                "macro",
                comparison.CoordType.ToString(),
                $"cmp:{comparison.CompareAddress:X4}",
                $"jmp:{comparison.JumpTarget:X4}",
                $"src:{comparison.MemAddr:X4}",
                $"kind:{comparison.JumpType ?? "<NULL>"}",
                branchDescriptor
            };

            if (comparison.HasPrecedingCondition &&
                comparison.PrecedingCoordType != CoordType.Unknown &&
                !string.IsNullOrWhiteSpace(comparison.PrecedingJumpType))
            {
                parts.Add($"prev:{comparison.PrecedingCoordType}");
                parts.Add($"prevcmp:{comparison.PrecedingValue:X2}");
                parts.Add($"prevkind:{comparison.PrecedingJumpType}");
                parts.Add(comparison.PrecedingViaJumpTarget ? "prevmode:jump" : "prevmode:fall");
            }

            return string.Join("|", parts);
        }

        private bool TryGetCachedSingleOccurrenceAnalysis(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            Dictionary<ushort, byte> initialEmulatedMemory8,
            out SingleOccurrenceAnalysisResult cachedResult)
        {
            cachedResult = null;

            if (AnalysisDebug.ShouldDisableCacheFor(targetX, targetY))
                return false;

            if (TryBuildExactSingleOccurrenceCacheKey(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    initialEmulatedMemory8,
                    out string exactCacheKey) &&
                TryGetReusableSingleOccurrenceAnalysis(exactCacheKey, allowCoordinateSensitive: true, out cachedResult))
            {
                AnalysisDebug.WriteLine(
                    $"    CACHE HIT single-occurrence exact для клетки ({targetX},{targetY}): {exactCacheKey}");
                return true;
            }

            if (!TryBuildSingleOccurrenceCacheKey(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    initialEmulatedMemory8,
                    out string cacheKey,
                    out _))
            {
                return false;
            }

            if (!TryGetReusableSingleOccurrenceAnalysis(cacheKey, allowCoordinateSensitive: false, out cachedResult))
                return false;

            AnalysisDebug.WriteLine(
                $"    CACHE HIT single-occurrence для клетки ({targetX},{targetY}): {cacheKey}");
            return true;
        }

        private void RegisterSingleOccurrenceAnalysis(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            Dictionary<ushort, byte> initialEmulatedMemory8,
            SingleOccurrenceAnalysisResult result)
        {
            if (AnalysisDebug.ShouldDisableCacheFor(targetX, targetY) || result == null)
                return;

            string signature = BuildSingleOccurrenceAnalysisSignature(result);
            var reusableResult = CloneSingleOccurrenceAnalysisResult(result);

            if (TryBuildExactSingleOccurrenceCacheKey(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    initialEmulatedMemory8,
                    out string exactCacheKey))
            {
                _singleOccurrenceAnalysisCache[exactCacheKey] = new SingleOccurrenceAnalysisCacheEntry
                {
                    SampleSignature = signature,
                    IsVerified = true,
                    ReusableResult = CloneSingleOccurrenceAnalysisResult(reusableResult)
                };
            }

            // Широкое переиспользование на уровне macro X/Y-scope разрешаем
            // только для результатов, не зависящих от стартовых координат.
            if (result.UsesInitialCoordinates)
                return;

            if (!TryBuildSingleOccurrenceCacheKey(
                    analysisCacheScopeKey,
                    startAddress,
                    targetX,
                    targetY,
                    initialEmulatedMemory8,
                    out string cacheKey,
                    out byte oppositeAxisValue))
            {
                return;
            }

            if (!_singleOccurrenceAnalysisCache.TryGetValue(cacheKey, out var entry) || entry == null)
            {
                _singleOccurrenceAnalysisCache[cacheKey] = new SingleOccurrenceAnalysisCacheEntry
                {
                    SampleSignature = signature,
                    MatchingOppositeAxisValues = new HashSet<byte> { oppositeAxisValue }
                };
                return;
            }

            if (entry.VerificationFailed || entry.IsVerified || entry.MatchingOppositeAxisValues.Contains(oppositeAxisValue))
                return;

            if (!string.Equals(entry.SampleSignature, signature, StringComparison.Ordinal))
            {
                entry.VerificationFailed = true;
                return;
            }

            entry.MatchingOppositeAxisValues.Add(oppositeAxisValue);
            if (entry.MatchingOppositeAxisValues.Count < 2)
                return;

            entry.IsVerified = true;
            entry.ReusableResult = CloneSingleOccurrenceAnalysisResult(reusableResult);
        }

        private bool TryBuildExactSingleOccurrenceCacheKey(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            Dictionary<ushort, byte> initialEmulatedMemory8,
            out string cacheKey)
        {
            cacheKey = null;

            if (string.IsNullOrWhiteSpace(analysisCacheScopeKey))
                return false;

            string stateKey = BuildMemoryStateKey(initialEmulatedMemory8);
            cacheKey = $"single-exact|{analysisCacheScopeKey}|{startAddress:X4}|x:{targetX:X2}|y:{targetY:X2}|state:{stateKey}";
            return true;
        }

        private bool TryGetReusableSingleOccurrenceAnalysis(
            string cacheKey,
            bool allowCoordinateSensitive,
            out SingleOccurrenceAnalysisResult cachedResult)
        {
            cachedResult = null;

            if (!_singleOccurrenceAnalysisCache.TryGetValue(cacheKey, out var entry) ||
                entry == null ||
                !entry.IsVerified ||
                entry.ReusableResult == null)
            {
                return false;
            }

            if (!allowCoordinateSensitive && entry.ReusableResult.UsesInitialCoordinates)
                return false;

            cachedResult = CloneSingleOccurrenceAnalysisResult(entry.ReusableResult);
            return true;
        }

        private bool TryBuildSingleOccurrenceCacheKey(
            string analysisCacheScopeKey,
            uint startAddress,
            byte targetX,
            byte targetY,
            Dictionary<ushort, byte> initialEmulatedMemory8,
            out string cacheKey,
            out byte oppositeAxisValue)
        {
            cacheKey = null;
            oppositeAxisValue = 0;

            if (string.IsNullOrWhiteSpace(analysisCacheScopeKey))
                return false;

            string stateKey = BuildMemoryStateKey(initialEmulatedMemory8);
            if (analysisCacheScopeKey.Contains("|XCoord|", StringComparison.Ordinal))
            {
                cacheKey = $"single|{analysisCacheScopeKey}|{startAddress:X4}|x:{targetX:X2}|state:{stateKey}";
                oppositeAxisValue = targetY;
                return true;
            }

            if (analysisCacheScopeKey.Contains("|YCoord|", StringComparison.Ordinal))
            {
                cacheKey = $"single|{analysisCacheScopeKey}|{startAddress:X4}|y:{targetY:X2}|state:{stateKey}";
                oppositeAxisValue = targetX;
                return true;
            }

            return false;
        }

        private Dictionary<int, PathVariantInfo> CloneFinalVariants(Dictionary<int, PathVariantInfo> source)
        {
            if (source == null)
                return new Dictionary<int, PathVariantInfo>();

            return source.ToDictionary(kvp => kvp.Key, kvp => CloneVariantInfo(kvp.Value));
        }

        private SingleOccurrenceAnalysisResult CloneSingleOccurrenceAnalysisResult(SingleOccurrenceAnalysisResult source)
        {
            if (source == null)
                return null;

            return new SingleOccurrenceAnalysisResult
            {
                CanonicalVariants = CloneFinalVariants(source.CanonicalVariants),
                RawLeafVariants = source.RawLeafVariants?
                    .Where(variant => variant != null)
                    .Select(CloneVariantInfo)
                    .ToList() ?? new List<PathVariantInfo>(),
                VisitedAddresses = new HashSet<uint>(source.VisitedAddresses ?? Enumerable.Empty<uint>()),
                UsesInitialCoordinates = source.UsesInitialCoordinates
            };
        }

        private string BuildSingleOccurrenceAnalysisSignature(SingleOccurrenceAnalysisResult result)
        {
            if (result == null)
                return "<NULL_SINGLE_OCCURRENCE>";

            string canonicalKey = result.CanonicalVariants == null || result.CanonicalVariants.Count == 0
                ? "<NO_CANONICAL>"
                : string.Join("##", result.CanonicalVariants
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}:{BuildSingleOccurrenceVariantSignature(kvp.Value)}"));

            string rawLeafKey = result.RawLeafVariants == null || result.RawLeafVariants.Count == 0
                ? "<NO_RAW_LEAFS>"
                : string.Join("##", result.RawLeafVariants
                    .Where(variant => variant != null)
                    .OrderBy(BuildSingleOccurrenceVariantSignature)
                    .Select(BuildSingleOccurrenceVariantSignature));

            return $"{canonicalKey}||{rawLeafKey}||coord:{result.UsesInitialCoordinates}";
        }

        private string BuildSingleOccurrenceVariantSignature(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string exitMemoryKey = variant.ExitEmulatedMemory8 == null || variant.ExitEmulatedMemory8.Count == 0
                ? "<NO_EXIT_MEM>"
                : string.Join(";",
                    variant.ExitEmulatedMemory8
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"{kvp.Key:X4}={kvp.Value:X2}"));

            string readKey = variant.MemoryReadBeforeWriteAddresses == null || variant.MemoryReadBeforeWriteAddresses.Count == 0
                ? "<NO_READ_BEFORE_WRITE>"
                : string.Join(",",
                    variant.MemoryReadBeforeWriteAddresses
                        .OrderBy(address => address)
                        .Select(address => address.ToString("X4")));

            string firstAccessKey = variant.PersistentMemoryFirstAccessKinds == null || variant.PersistentMemoryFirstAccessKinds.Count == 0
                ? "<NO_FIRST_ACCESS>"
                : string.Join(";",
                    variant.PersistentMemoryFirstAccessKinds
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"{kvp.Key:X4}:{kvp.Value}"));

            string constraintKey = variant.StateValueConstraints == null || variant.StateValueConstraints.Count == 0
                ? "<NO_CONSTRAINTS>"
                : string.Join(";",
                    variant.StateValueConstraints
                        .Where(kvp => kvp.Value != null && !kvp.Value.IsEmpty)
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"{kvp.Key:X4}:{BuildConstraintOnlyToken(kvp.Value)}"));

            return string.Join("||", new[]
            {
                BuildRepeatedEventVariantTargetKey(variant),
                $"leaf:{variant.IsLeaf}",
                $"exit:{exitMemoryKey}",
                $"read:{readKey}",
                $"first:{firstAccessKey}",
                $"constraints:{constraintKey}",
                $"coord:{variant.UsesInitialCoordinates}",
                $"repeat:{variant.HasRepeatedEventOccurrenceSensitivity}",
                $"term:{variant.TerminatedByRepeatedBackEdge}/{variant.TerminatedByTerminalRet}",
                $"branch:{variant.HasBranchSpecificContribution}"
            });
        }

        private string BuildVisitedAddressesKey(IEnumerable<uint> visitedAddresses)
        {
            var ordered = (visitedAddresses ?? Enumerable.Empty<uint>())
                .Distinct()
                .OrderBy(address => address)
                .ToList();

            if (ordered.Count == 0)
                return "<NO_VISITS>";

            return string.Join("|", ordered.Select(address => address.ToString("X4")));
        }

        private PathVariantInfo CloneVariantInfo(PathVariantInfo source)
        {
            if (source == null)
                return null;

            return new PathVariantInfo
            {
                PathId = source.PathId,
                PathOrderKey = source.PathOrderKey,
                IsLeaf = source.IsLeaf,
                Texts = source.Texts?.ToList() ?? new List<string>(),
                BranchChoices = source.BranchChoices?
                    .Where(choice => choice != null)
                    .Select(choice => choice.Clone())
                    .ToList() ?? new List<BranchChoice>(),
                RandomEncounterMonsterPowerCap = source.RandomEncounterMonsterPowerCap,
                RandomEncounterMonsterLevelCap = source.RandomEncounterMonsterLevelCap,
                RandomEncounterMonsterBatchCountCap = source.RandomEncounterMonsterBatchCountCap,
                DarkeningLevel = source.DarkeningLevel,
                RandomEncounterChance = source.RandomEncounterChance,
                RandomEncounterRubicon = source.RandomEncounterRubicon,
                CallsRandomEncounter = source.CallsRandomEncounter,
                IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump,
                RandomEncounterInstructionAddress = source.RandomEncounterInstructionAddress,
                RandomEncounterExecutionOrder = source.RandomEncounterExecutionOrder,
                TeleportTargetX = source.TeleportTargetX,
                TeleportTargetY = source.TeleportTargetY,
                TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max),
                TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max),
                BattleMonsterCount = source.BattleMonsterCount,
                BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                PersistentCounterProgressions = source.PersistentCounterProgressions?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<PersistentCounterProgressionInfo>(),
                DynamicRandomBoundDependencies = source.DynamicRandomBoundDependencies?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<DynamicRandomBoundDependencyInfo>(),
                BattleMonsters = source.BattleMonsters?
                    .Select(m => new BattleMonster
                    {
                        Index = m.Index,
                        MonsterIndex1 = m.MonsterIndex1,
                        MonsterIndex2 = m.MonsterIndex2,
                        IsIndeterminate = m.IsIndeterminate
                    })
                    .ToList() ?? new List<BattleMonster>(),
                PartiallyDefinedBattles = source.PartiallyDefinedBattles?
                    .Select(p => p.Clone())
                    .ToList() ?? new List<PartiallyDefinedBattle>(),
                HasAnyTableLoad = source.HasAnyTableLoad,
                LoadedValues = source.LoadedValues?
                    .Select(v => new OvrObject.LoadedValueInfo
                    {
                        BxIndex = v.BxIndex,
                        RegName = v.RegName,
                        Value = v.Value,
                        SourceAddr = v.SourceAddr,
                        IsFirstTable = v.IsFirstTable,
                        IsSaved = v.IsSaved
                    })
                    .ToList() ?? new List<OvrObject.LoadedValueInfo>(),
                PartyEffects = source.PartyEffects?
                    .Where(e => e != null)
                    .Select(e => e.Clone())
                    .ToList() ?? new List<PartyEffect>(),
                ExitEmulatedMemory8 = source.ExitEmulatedMemory8 == null
                    ? new Dictionary<ushort, byte>()
                    : new Dictionary<ushort, byte>(source.ExitEmulatedMemory8),
                MemoryReadBeforeWriteAddresses = source.MemoryReadBeforeWriteAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.MemoryReadBeforeWriteAddresses),
                PersistentMemoryFirstAccessKinds = source.PersistentMemoryFirstAccessKinds == null
                    ? new Dictionary<ushort, PersistentMemoryFirstAccessKind>()
                    : new Dictionary<ushort, PersistentMemoryFirstAccessKind>(source.PersistentMemoryFirstAccessKinds),
                StateValueConstraints = CloneStateValueConstraints(source.StateValueConstraints),
                HasRepeatedEventOccurrenceSensitivity = source.HasRepeatedEventOccurrenceSensitivity,
                UsesInitialCoordinates = source.UsesInitialCoordinates,
                OccurrenceIndices = source.OccurrenceIndices?
                    .Where(index => index > 0)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToList() ?? new List<int>(),
                OccurrenceRanges = source.OccurrenceRanges?
                    .Where(range => range != null && range.Start > 0)
                    .Select(range => range.Clone())
                    .ToList() ?? new List<OccurrenceRangeInfo>(),
                OccurrenceDescription = source.OccurrenceDescription,
                ProbabilityNumerator = source.ProbabilityNumerator,
                ProbabilityDenominator = source.ProbabilityDenominator,
                TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge,
                TerminatedByPromptLoopBackEdge = source.TerminatedByPromptLoopBackEdge,
                TerminatedByTerminalRet = source.TerminatedByTerminalRet,
                HasBranchSpecificContribution = source.HasBranchSpecificContribution
            };
        }

        private string BuildOccurrenceDescription(
            List<OccurrenceRangeInfo> occurrenceRanges,
            int maxAnalyzedOccurrence,
            bool analysisTruncated)
        {
            var ranges = NormalizeOccurrenceRanges(occurrenceRanges);
            if (ranges.Count == 0)
                return null;

            if (maxAnalyzedOccurrence <= 1)
                return null;

            bool coversAllAnalyzedOccurrences =
                ranges.Count == 1 &&
                ranges[0].Start == 1 &&
                (ranges[0].IsOpenEnded || Math.Max(ranges[0].Start, ranges[0].End) >= maxAnalyzedOccurrence);

            if (coversAllAnalyzedOccurrences)
                return null;

            return $"только при {FormatOccurrenceRanges(ranges)} наступлениях";
        }

        private string FormatOccurrenceOrdinal(int occurrence)
        {
            return $"{occurrence}-м";
        }

        private string FormatOccurrenceRanges(List<OccurrenceRangeInfo> ranges)
        {
            var parts = (ranges ?? Enumerable.Empty<OccurrenceRangeInfo>())
                .Where(range => range != null && range.Start > 0)
                .Select(range =>
                {
                    int end = Math.Max(range.Start, range.End);
                    if (range.IsOpenEnded)
                        return $"{FormatOccurrenceOrdinal(range.Start)} и последующих";

                    if (range.Start == end)
                        return FormatOccurrenceOrdinal(range.Start);

                    return $"{FormatOccurrenceOrdinal(range.Start)}..{FormatOccurrenceOrdinal(end)}";
                })
                .ToList();

            if (parts.Count == 0)
                return string.Empty;

            if (parts.Count == 1)
                return parts[0];

            if (parts.Count == 2)
                return $"{parts[0]} и {parts[1]}";

            return string.Join(", ", parts.Take(parts.Count - 1)) + " и " + parts.Last();
        }

        private bool TryReadOverlayByte(BinaryReader br, ushort memAddr, out byte value)
        {
            value = 0;

            if (br == null)
                return false;

            if (!OvrOverlayAddressReader.TryMapOverlayAddressToFileOffset(br, _config, memAddr, out long fileOffset))
                return false;

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset;
                value = br.ReadByte();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
            finally
            {
                br.BaseStream.Position = originalPosition;
            }
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
                variant.RandomEncounterMonsterPowerCap.HasValue ||
                variant.RandomEncounterMonsterLevelCap.HasValue ||
                variant.RandomEncounterMonsterBatchCountCap.HasValue ||
                variant.DarkeningLevel.HasValue ||
                variant.RandomEncounterChance.HasValue ||
                variant.RandomEncounterRubicon.HasValue ||
                variant.CallsRandomEncounter ||
                variant.BattleMonsterCount.HasValue ||
                variant.BattleMonsterCountRange != null ||
                variant.IsBattleMonsterCountIndeterminate ||
                (variant.PersistentCounterProgressions?.Count ?? 0) > 0 ||
                (variant.DynamicRandomBoundDependencies?.Count ?? 0) > 0 ||
                (variant.BattleMonsters?.Count ?? 0) > 0 ||
                (variant.PartiallyDefinedBattles?.Count ?? 0) > 0 ||
                variant.HasAnyTableLoad ||
                (variant.LoadedValues?.Count ?? 0) > 0 ||
                (variant.PartyEffects?.Count ?? 0) > 0 ||
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
                                SetInitialRegistersFromCoordinates(tracker, x, y, defaultPathAddress.Value),
                            analysisCacheScopeKey: "default");

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
                        AnalysisDebug.WriteLine($"    Канонических outcomes: {obj.PathVariants.Count}");

                        foreach (var kvp in obj.PathVariants.OrderBy(k => k.Key))
                        {
                            var variant = kvp.Value;
                            var texts = variant?.Texts ?? new List<string>();

                            AnalysisDebug.WriteLine($"      Outcome {kvp.Key}: {texts.Count} текстов");
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
        private uint CalculatePatchAddress(ushort key) => (uint)(key + OvrFileConfig.PatchBase) & 0xFFFF;

        private List<X86Instruction> DisassembleAll(BinaryReader br)
        {
            var instructions = new List<X86Instruction>();

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

        private void SetInitialRegistersFromCoordinates(RegisterTracker tracker, byte x, byte y, uint patchStartAddress)
        {
            tracker.SetRegisterValue("BL", x, patchStartAddress, "Initial X coordinate");
            tracker.SetRegisterValue("BH", 0, patchStartAddress, "Initial BH = 0");
            tracker.SetRegisterValue("BX", (ushort)x, patchStartAddress, $"Initial BX = X ({x})");
            tracker.MarkRegisterAsCoordinateSeed("BX");
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
                PathOrderKey = pathId,
                Texts = combinedTexts.Where(t => !string.IsNullOrEmpty(t)).ToList(),
                IsLeaf = isLeaf,
                RandomEncounterMonsterPowerCap = source.RandomEncounterMonsterPowerCap,
                RandomEncounterMonsterLevelCap = source.RandomEncounterMonsterLevelCap,
                RandomEncounterMonsterBatchCountCap = source.RandomEncounterMonsterBatchCountCap,
                DarkeningLevel = source.DarkeningLevel,
                RandomEncounterChance = source.RandomEncounterChance,
                RandomEncounterRubicon = source.RandomEncounterRubicon,
                CallsRandomEncounter = source.CallsRandomEncounter,
                IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump,
                RandomEncounterInstructionAddress = source.RandomEncounterInstructionAddress,
                RandomEncounterExecutionOrder = source.RandomEncounterExecutionOrder,
                TeleportTargetX = source.TeleportTargetX,
                TeleportTargetY = source.TeleportTargetY,
                TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max),
                TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max),
                BattleMonsterCount = source.BattleMonsterCount,
                BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                PersistentCounterProgressions = source.PersistentCounterProgressions?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<PersistentCounterProgressionInfo>(),
                DynamicRandomBoundDependencies = source.DynamicRandomBoundDependencies?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<DynamicRandomBoundDependencyInfo>(),
                BattleMonsters = source.BattleMonsterEntries
                    .Where(entry => entry.Value.val1 != 0 && entry.Value.val2 != 0)
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
                    .Select(p => p.Clone())
                    .ToList(),
                HasAnyTableLoad = source.PartialBattleInfo.Any(ShouldExposeLoadedValue),
                LoadedValues = source.PartialBattleInfo
                    .Where(ShouldExposeLoadedValue)
                    .OrderBy(i => i.BxIndex)
                    .ThenBy(i => i.SourceTableAddr ?? 0)
                    .ThenBy(i => i.SrcReg)
                    .Select(info => new OvrObject.LoadedValueInfo
                    {
                        BxIndex = info.BxIndex,
                        RegName = info.SrcReg,
                        Value = info.SrcRegValue,
                        SourceAddr = info.SourceTableAddr ?? 0,
                        IsFirstTable = info.DestAddr == 0x3C58,
                        IsSaved = false
                    })
                    .ToList(),
                PartyEffects = BuildNormalizedPartyEffects(source),
                ExitEmulatedMemory8 = source.ExitEmulatedMemory8 == null
                    ? new Dictionary<ushort, byte>()
                    : new Dictionary<ushort, byte>(source.ExitEmulatedMemory8),
                MemoryReadBeforeWriteAddresses = source.MemoryReadBeforeWriteAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.MemoryReadBeforeWriteAddresses),
                PersistentMemoryFirstAccessKinds = source.PersistentMemoryFirstAccessKinds == null
                    ? new Dictionary<ushort, PersistentMemoryFirstAccessKind>()
                    : new Dictionary<ushort, PersistentMemoryFirstAccessKind>(source.PersistentMemoryFirstAccessKinds),
                UsesInitialCoordinates = source.UsesInitialCoordinates
            };
        }

        private static bool ShouldExposeLoadedValue(PartialBattleInfo info)
        {
            return info != null && info.IsFromTable;
        }

        private List<PartyEffect> BuildNormalizedPartyEffects(PathAnalysisResult source)
        {
            return PartyEffectNormalizer.Normalize(source)
                .Where(PartyEffectSemantics.IsSemanticOutcomeEffect)
                .ToList();
        }

        private void WriteVariantDebugSummary(PathVariantInfo variant, byte x, byte y, byte directionByte)
        {
            if (variant == null)
            {
                AnalysisDebug.WriteLine("        <variant is null>");
                return;
            }

            string occurrence = variant.GetOccurrenceDescription();
            if (!string.IsNullOrWhiteSpace(occurrence))
                AnalysisDebug.WriteLine($"        Occurrence: {occurrence}");

            var guards = variant.GetGuardPredicates();
            string guardsText = PartyEffectSemantics.BuildPredicateListDisplayText(guards);
            if (!string.IsNullOrWhiteSpace(guardsText))
                AnalysisDebug.WriteLine($"        Guards: {guardsText}");

            if (variant.HasProbabilityInfo)
            {
                AnalysisDebug.WriteLine($"        {variant.GetProbabilityDescription()}");
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

            if (variant.PersistentCounterProgressions != null && variant.PersistentCounterProgressions.Count > 0)
            {
                AnalysisDebug.WriteLine("        PersistentCounterProgressions:");
                foreach (var progression in variant.PersistentCounterProgressions
                    .Where(info => info != null)
                    .OrderBy(info => info.CounterAddress)
                    .ThenBy(info => info.TargetWriteInstructionAddress))
                {
                    AnalysisDebug.WriteLine($"          {progression.ToDebugString()}");
                }
            }

            if (variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0)
            {
                AnalysisDebug.WriteLine("        DynamicRandomBoundDependencies:");
                foreach (var dependency in variant.DynamicRandomBoundDependencies
                    .Where(info => info != null)
                    .OrderBy(info => info.RandomCallInstructionAddress)
                    .ThenBy(info => info.TargetInstructionAddress))
                {
                    AnalysisDebug.WriteLine($"          {dependency.ToDebugString()}");
                }
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
                    string exactOptions = partial.HasExactOptions
                        ? string.Join(", ", partial.ExactOptions.Select(option => $"{option.Val1:X2}/{option.Val2:X2}"))
                        : "<range>";
                    string repeatCount = partial.RepeatCountRange != null
                        ? (partial.RepeatCountRange.IsExact
                            ? partial.RepeatCountRange.Min.ToString()
                            : $"{partial.RepeatCountRange.Min}-{partial.RepeatCountRange.Max}")
                        : Math.Max(1, partial.RepeatCount).ToString();
                    AnalysisDebug.WriteLine(
                        $"          BX={partial.BxIndex}: [{partial.RangeStart1:X2}-{partial.RangeEnd1:X2}] + [{partial.RangeStart2:X2}-{partial.RangeEnd2:X2}], repeatCount={repeatCount}, exactOptions={exactOptions}");
                }
            }

            if (variant.LoadedValues != null && variant.LoadedValues.Count > 0)
            {
                AnalysisDebug.WriteLine("        LoadedValues:");
                foreach (var loadedValue in variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.IsFirstTable ? 0 : 1)
                    .ThenBy(v => v.SourceAddr))
                {
                    AnalysisDebug.WriteLine($"          BX={loadedValue.BxIndex}: {loadedValue}");
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
            target.RandomEncounterMonsterPowerCap = null;
            target.RandomEncounterMonsterLevelCap = null;
            target.RandomEncounterMonsterBatchCountCap = null;
            target.DarkeningLevel = null;
            target.RandomEncounterChance = null;
            target.RandomEncounterRubicon = null;
            target.CallsRandomEncounter = false;
            target.RandomEncounterInstructionAddress = 0;
            target.RandomEncounterExecutionOrder = 0;
            target.BattleMonsterCount = null;
            target.BattleMonsterCountRange = null;
            target.IsBattleMonsterCountIndeterminate = false;
            target.PersistentCounterProgressions.Clear();
            target.DynamicRandomBoundDependencies.Clear();
            target.BattleMonsters.Clear();
            target.PartiallyDefinedBattles.Clear();
            target.HasAnyTableLoad = false;
            target.PartyEffects.Clear();

            if (target.PathVariants == null || target.PathVariants.Count != 1)
                return;

            var variant = target.PathVariants.Values.First();
            target.RandomEncounterMonsterPowerCap = variant.RandomEncounterMonsterPowerCap;
            target.RandomEncounterMonsterLevelCap = variant.RandomEncounterMonsterLevelCap;
            target.RandomEncounterMonsterBatchCountCap = variant.RandomEncounterMonsterBatchCountCap;
            target.DarkeningLevel = variant.DarkeningLevel;
            target.RandomEncounterChance = variant.RandomEncounterChance;
            target.RandomEncounterRubicon = variant.RandomEncounterRubicon;
            target.CallsRandomEncounter = variant.CallsRandomEncounter;
            target.RandomEncounterInstructionAddress = variant.RandomEncounterInstructionAddress;
            target.RandomEncounterExecutionOrder = variant.RandomEncounterExecutionOrder;
            target.BattleMonsterCount = variant.BattleMonsterCount;
            target.BattleMonsterCountRange = variant.BattleMonsterCountRange == null ? null : new ValueRange8(variant.BattleMonsterCountRange.Min, variant.BattleMonsterCountRange.Max);
            target.IsBattleMonsterCountIndeterminate = variant.IsBattleMonsterCountIndeterminate;
            target.PersistentCounterProgressions = variant.PersistentCounterProgressions?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<PersistentCounterProgressionInfo>();
            target.DynamicRandomBoundDependencies = variant.DynamicRandomBoundDependencies?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<DynamicRandomBoundDependencyInfo>();
            target.HasAnyTableLoad = variant.HasAnyTableLoad;
            target.PartyEffects = variant.PartyEffects?.Select(e => e?.Clone()).Where(e => e != null).ToList() ?? new List<PartyEffect>();

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
                target.AddPartiallyDefinedBattle(partial);
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

            if (!HasPrecedingCoordinateConstraint(comparison))
                return targetCells;

            var filteredCells = targetCells
                .Where(cell => MatchesPrecedingCondition((byte)cell.X, (byte)cell.Y, comparison))
                .ToList();

            if (filteredCells.Count != targetCells.Count)
            {
                AnalysisDebug.WriteLine(
                    $"  Учтено предшествующее условие: {targetCells.Count} -> {filteredCells.Count} клеток");
            }

            return filteredCells;
        }

        private bool HasPrecedingCoordinateConstraint(CoordinateComparison comparison)
        {
            return comparison != null &&
                   comparison.HasPrecedingCondition &&
                   comparison.PrecedingCoordType != CoordType.Unknown &&
                   !string.IsNullOrWhiteSpace(comparison.PrecedingJumpType);
        }

        private bool MatchesPrecedingCondition(byte x, byte y, CoordinateComparison comparison)
        {
            if (!HasPrecedingCoordinateConstraint(comparison))
                return true;

            byte coordinateValue = comparison.PrecedingCoordType switch
            {
                CoordType.XCoord => x,
                CoordType.YCoord => y,
                CoordType.FullCoord => (byte)(((y & 0x0F) << 4) | (x & 0x0F)),
                _ => 0
            };

            bool jumpConditionMatches = CheckCondition(
                coordinateValue,
                comparison.PrecedingValue,
                comparison.PrecedingJumpType);

            return comparison.PrecedingViaJumpTarget
                ? jumpConditionMatches
                : !jumpConditionMatches;
        }

        private IEnumerable<Point> OrderCellsForCacheEvidence(List<Point> cells)
        {
            if (cells == null || cells.Count <= 2)
                return cells ?? Enumerable.Empty<Point>();

            var remaining = new List<Point>(cells);
            var ordered = new List<Point>(cells.Count);
            var seenX = new HashSet<int>();
            var seenY = new HashSet<int>();
            Point? last = null;

            while (remaining.Count > 0)
            {
                int bestIndex = 0;
                int bestScore = int.MinValue;

                for (int i = 0; i < remaining.Count; i++)
                {
                    Point candidate = remaining[i];
                    int score = 0;

                    if (!seenX.Contains(candidate.X))
                        score += 1000;
                    if (!seenY.Contains(candidate.Y))
                        score += 1000;

                    if (last.HasValue)
                    {
                        score += Math.Abs(candidate.X - last.Value.X) * 10;
                        score += Math.Abs(candidate.Y - last.Value.Y) * 10;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                Point chosen = remaining[bestIndex];
                remaining.RemoveAt(bestIndex);
                ordered.Add(chosen);
                seenX.Add(chosen.X);
                seenY.Add(chosen.Y);
                last = chosen;
            }

            return ordered;
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
            byte? randomEncounterMonsterPowerCap, byte? randomEncounterMonsterLevelCap, byte? darkeningLevel, byte? randomEncounterChance, bool callsRandomEncounter, byte? battleMonsterCount, ValueRange8 battleMonsterCountRange, bool isBattleMonsterCountIndeterminate,
            Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> battleMonsters,
            List<PartiallyDefinedBattle> partialBattles, bool hasPartialBattlePattern)
        {
            var obj = new OvrObject
            {
                X = (byte)cellPos.X,
                Y = (byte)cellPos.Y,
                DirectionByte = 0,
                RandomEncounterMonsterPowerCap = randomEncounterMonsterPowerCap,
                RandomEncounterMonsterLevelCap = randomEncounterMonsterLevelCap,
                DarkeningLevel = darkeningLevel,
                RandomEncounterChance = randomEncounterChance,
                CallsRandomEncounter = callsRandomEncounter,
                RandomEncounterInstructionAddress = 0,
                RandomEncounterExecutionOrder = 0,
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
                obj.AddPartiallyDefinedBattle(partial);
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
                RandomEncounterMonsterPowerCap = result.RandomEncounterMonsterPowerCap,
                RandomEncounterMonsterLevelCap = result.RandomEncounterMonsterLevelCap,
                DarkeningLevel = result.DarkeningLevel,
                RandomEncounterChance = result.RandomEncounterChance,
                RandomEncounterRubicon = result.RandomEncounterRubicon,
                CallsRandomEncounter = result.CallsRandomEncounter,
                RandomEncounterInstructionAddress = result.RandomEncounterInstructionAddress,
                RandomEncounterExecutionOrder = result.RandomEncounterExecutionOrder,
                TeleportTargetX = result.TeleportTargetX,
                TeleportTargetY = result.TeleportTargetY,
                TeleportTargetXRange = result.TeleportTargetXRange == null ? null : new ValueRange8(result.TeleportTargetXRange.Min, result.TeleportTargetXRange.Max),
                TeleportTargetYRange = result.TeleportTargetYRange == null ? null : new ValueRange8(result.TeleportTargetYRange.Min, result.TeleportTargetYRange.Max),
                BattleMonsterCount = result.BattleMonsterCount,
                BattleMonsterCountRange = result.BattleMonsterCountRange == null ? null : new ValueRange8(result.BattleMonsterCountRange.Min, result.BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = result.IsBattleMonsterCountIndeterminate,
                PersistentCounterProgressions = result.PersistentCounterProgressions?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<PersistentCounterProgressionInfo>(),
                DynamicRandomBoundDependencies = result.DynamicRandomBoundDependencies?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<DynamicRandomBoundDependencyInfo>(),
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
                obj.AddPartiallyDefinedBattle(partial);
            }

            obj.HasAnyTableLoad = result.PartialBattleInfo.Any(ShouldExposeLoadedValue);
            return obj;
        }
    }

}
