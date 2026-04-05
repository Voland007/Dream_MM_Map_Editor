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
    /// Анализирует пути выполнения и альтернативные ветки
    /// </summary>
    public class PathAnalyzer
    {
        private readonly OvrFileConfig _config;
        private readonly CodeExecutor _codeExecutor;

        public PathAnalyzer(OvrFileConfig config, CodeExecutor codeExecutor)
        {
            _config = config;
            _codeExecutor = codeExecutor;
        }

        public void ProcessPaths(List<AlternativePath> paths, int basePathId, int depth,
            List<TextEntry> inheritedContextTexts, List<TextEntry> inheritedLocalTexts,
            uint firstLocalTextAddress, BinaryReader br,
            byte targetX, byte targetY, List<PathVariantInfo> allResults, OvrObject ovrObject,
            HashSet<(uint From, uint To)> processedBackEdges = null,
            bool invalidateReturnRegistersAfterExternalCall = false,
            HashSet<uint> reachableAddresses = null,
            PathAnalysisResult inheritedState = null)
        {
            processedBackEdges ??= new HashSet<(uint From, uint To)>();
            if (depth > 8) return;

            var sortedPaths = paths.OrderBy(p => p.Address).ToList();
            int localCounter = 1;
            var processedTargets = new HashSet<uint>();

            foreach (var path in sortedPaths)
            {
                if (processedTargets.Contains(path.TargetAddress))
                    continue;

                processedTargets.Add(path.TargetAddress);
                int currentPathId = basePathId * 10 + localCounter;
                localCounter++;

                bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n  Анализ пути {currentPathId} (глубина {depth}) -> 0x{path.TargetAddress:X4} ({path.Condition})");
                }

                // Продолжаем путь с тем состоянием регистров, которое было в точке ветвления
                var pathRegisterTracker = path.RegisterState?.Clone() ?? new RegisterTracker();
                var pathResult = _codeExecutor.ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                    new HashSet<uint>(), depth + 1, 0, currentPathId, targetX, targetY,
                    processedBackEdges, invalidateReturnRegistersAfterExternalCall);
                var effectivePathResult = MergeAnalysisStates(inheritedState, pathResult);

                // Формируем тексты для этого пути с сохранением порядка
                if (reachableAddresses != null)
                {
                    foreach (var visitedAddress in pathResult.VisitedAddresses)
                        reachableAddresses.Add(visitedAddress);
                }

                var pathTexts = BuildPathTexts(path, pathResult, inheritedContextTexts,
                    inheritedLocalTexts, firstLocalTextAddress);

                // Путь считается листовым, только если у него нет вложенных путей
                bool isLeaf = pathResult.AlternativePaths.Count == 0;

                // Добавляем путь в результаты
                allResults.Add(CreatePathVariant(currentPathId, pathTexts, isLeaf, effectivePathResult));

                if (debugMode && pathTexts.Count > 0)
                {
                    AnalysisDebug.WriteLine($"      Найдено текстов в пути {currentPathId}: {pathTexts.Count} (листовой: {isLeaf})");
                    foreach (var text in pathTexts.OrderBy(t => t.Order))
                    {
                        AnalysisDebug.WriteLine($"        [{text.Order}] {(text.IsContextual ? "C" : "L")}: {text.Text}");
                    }
                }

                // Формируем набор текстов для наследования вложенными путями
                var (newInheritedContextTexts, newInheritedLocalTexts) = BuildInheritedTexts(
                    path, pathResult, inheritedContextTexts, inheritedLocalTexts, firstLocalTextAddress);

                // Рекурсивно обрабатываем вложенные пути
                if (pathResult.AlternativePaths.Count > 0)
                {
                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"      Найдено {pathResult.AlternativePaths.Count} вложенных путей");
                    }
                    ProcessPaths(pathResult.AlternativePaths, currentPathId, depth + 1,
                        newInheritedContextTexts, newInheritedLocalTexts,
                        firstLocalTextAddress, br, targetX, targetY,
                        allResults, ovrObject, processedBackEdges,
                        invalidateReturnRegistersAfterExternalCall, reachableAddresses,
                        effectivePathResult);
                }
            }
        }

        private List<TextEntry> BuildPathTexts(AlternativePath path, PathAnalysisResult pathResult,
            List<TextEntry> inheritedContextTexts, List<TextEntry> inheritedLocalTexts,
            uint firstLocalTextAddress)
        {
            var pathTexts = new List<TextEntry>();
            int nextOrder = 0;

            // 1. Сначала наследуем контекстные тексты от родителя
            foreach (var text in inheritedContextTexts)
            {
                pathTexts.Add(new TextEntry
                {
                    Text = text.Text,
                    Order = nextOrder++,
                    IsContextual = true,
                    Address = text.Address
                });
            }

            // 2. Потом добавляем новые контекстные тексты из этого пути
            foreach (var text in pathResult.ContextTexts)
            {
                pathTexts.Add(new TextEntry
                {
                    Text = text,
                    Order = nextOrder++,
                    IsContextual = true,
                    Address = pathResult.FirstLocalTextAddress
                });
            }

            // 3. Определяем, нужно ли наследовать локальные тексты
            bool shouldInheritLocal = path.Condition.StartsWith("LINEAR") ||
                                      (firstLocalTextAddress != uint.MaxValue &&
                                       path.Address > firstLocalTextAddress);

            // 4. Если нужно, наследуем локальные тексты от родителя
            if (shouldInheritLocal)
            {
                foreach (var text in inheritedLocalTexts)
                {
                    pathTexts.Add(new TextEntry
                    {
                        Text = text.Text,
                        Order = nextOrder++,
                        IsContextual = false,
                        Address = text.Address
                    });
                }
            }

            // 5. В самом конце добавляем новые локальные тексты из этого пути
            foreach (var text in pathResult.FoundTexts)
            {
                pathTexts.Add(new TextEntry
                {
                    Text = text,
                    Order = nextOrder++,
                    IsContextual = false,
                    Address = pathResult.FirstLocalTextAddress
                });
            }

            return pathTexts;
        }

        private (List<TextEntry> context, List<TextEntry> local) BuildInheritedTexts(
            AlternativePath path, PathAnalysisResult pathResult,
            List<TextEntry> inheritedContextTexts, List<TextEntry> inheritedLocalTexts,
            uint firstLocalTextAddress)
        {
            var newInheritedContextTexts = new List<TextEntry>(inheritedContextTexts);
            foreach (var text in pathResult.ContextTexts)
            {
                newInheritedContextTexts.Add(new TextEntry
                {
                    Text = text,
                    Order = 0,
                    IsContextual = true,
                    Address = pathResult.FirstLocalTextAddress
                });
            }

            var newInheritedLocalTexts = new List<TextEntry>();

            bool shouldInheritLocal = path.Condition.StartsWith("LINEAR") ||
                                      (firstLocalTextAddress != uint.MaxValue &&
                                       path.Address > firstLocalTextAddress);

            if (shouldInheritLocal)
            {
                foreach (var text in inheritedLocalTexts)
                {
                    newInheritedLocalTexts.Add(text);
                }
            }

            foreach (var text in pathResult.FoundTexts)
            {
                newInheritedLocalTexts.Add(new TextEntry
                {
                    Text = text,
                    Order = 0,
                    IsContextual = false,
                    Address = pathResult.FirstLocalTextAddress
                });
            }

            return (newInheritedContextTexts, newInheritedLocalTexts);
        }


        private PathAnalysisResult MergeAnalysisStates(PathAnalysisResult inheritedState, PathAnalysisResult currentState)
        {
            if (inheritedState == null)
                return ClonePathAnalysisResult(currentState);

            if (currentState == null)
                return ClonePathAnalysisResult(inheritedState);

            var merged = ClonePathAnalysisResult(inheritedState);

            if (currentState.MonsterPower.HasValue)
                merged.MonsterPower = currentState.MonsterPower;
            if (currentState.MonsterLevel.HasValue)
                merged.MonsterLevel = currentState.MonsterLevel;
            if (currentState.MonsterBatchCount.HasValue)
                merged.MonsterBatchCount = currentState.MonsterBatchCount;
            if (currentState.DarkeningLevel.HasValue)
                merged.DarkeningLevel = currentState.DarkeningLevel;
            if (currentState.RandomEncounterChance.HasValue)
                merged.RandomEncounterChance = currentState.RandomEncounterChance;
            merged.CallsRandomEncounter = merged.CallsRandomEncounter || currentState.CallsRandomEncounter;

            if (currentState.IsBattleMonsterCountIndeterminate)
            {
                merged.BattleMonsterCount = null;
                merged.IsBattleMonsterCountIndeterminate = true;
            }
            else if (currentState.BattleMonsterCount.HasValue)
            {
                merged.BattleMonsterCount = currentState.BattleMonsterCount;
                merged.IsBattleMonsterCountIndeterminate = false;
            }

            foreach (var entry in currentState.BattleMonsterEntries)
                merged.BattleMonsterEntries[entry.Key] = entry.Value;

            foreach (var partial in currentState.PartialBattles)
            {
                if (!merged.PartialBattles.Any(p =>
                    p.BxIndex == partial.BxIndex &&
                    p.RangeStart1 == partial.RangeStart1 &&
                    p.RangeEnd1 == partial.RangeEnd1 &&
                    p.RangeStart2 == partial.RangeStart2 &&
                    p.RangeEnd2 == partial.RangeEnd2))
                {
                    merged.PartialBattles.Add(new PartiallyDefinedBattle
                    {
                        BxIndex = partial.BxIndex,
                        RangeStart1 = partial.RangeStart1,
                        RangeEnd1 = partial.RangeEnd1,
                        RangeStart2 = partial.RangeStart2,
                        RangeEnd2 = partial.RangeEnd2
                    });
                }
            }

            foreach (var info in currentState.PartialBattleInfo)
            {
                if (!merged.PartialBattleInfo.Any(i =>
                    i.BxIndex == info.BxIndex &&
                    i.DestAddr == info.DestAddr &&
                    i.SrcReg == info.SrcReg &&
                    i.SrcRegValue == info.SrcRegValue &&
                    i.IsFromTable == info.IsFromTable &&
                    i.SourceTableAddr == info.SourceTableAddr &&
                    i.SourceTable == info.SourceTable))
                {
                    merged.PartialBattleInfo.Add(new PartialBattleInfo
                    {
                        BxIndex = info.BxIndex,
                        DestAddr = info.DestAddr,
                        SrcReg = info.SrcReg,
                        SrcRegValue = info.SrcRegValue,
                        IsFromTable = info.IsFromTable,
                        SourceTableAddr = info.SourceTableAddr,
                        SourceTable = info.SourceTable
                    });
                }
            }

            merged.HasPartialBattlePattern = merged.HasPartialBattlePattern || currentState.HasPartialBattlePattern;
            return merged;
        }

        private PathAnalysisResult ClonePathAnalysisResult(PathAnalysisResult source)
        {
            var clone = new PathAnalysisResult();
            if (source == null)
                return clone;

            clone.MonsterPower = source.MonsterPower;
            clone.MonsterLevel = source.MonsterLevel;
            clone.MonsterBatchCount = source.MonsterBatchCount;
            clone.DarkeningLevel = source.DarkeningLevel;
            clone.RandomEncounterChance = source.RandomEncounterChance;
            clone.CallsRandomEncounter = source.CallsRandomEncounter;
            clone.BattleMonsterCount = source.BattleMonsterCount;
            clone.IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate;
            clone.HasPartialBattlePattern = source.HasPartialBattlePattern;

            foreach (var entry in source.BattleMonsterEntries)
                clone.BattleMonsterEntries[entry.Key] = entry.Value;

            foreach (var partial in source.PartialBattles)
            {
                clone.PartialBattles.Add(new PartiallyDefinedBattle
                {
                    BxIndex = partial.BxIndex,
                    RangeStart1 = partial.RangeStart1,
                    RangeEnd1 = partial.RangeEnd1,
                    RangeStart2 = partial.RangeStart2,
                    RangeEnd2 = partial.RangeEnd2
                });
            }

            foreach (var info in source.PartialBattleInfo)
            {
                clone.PartialBattleInfo.Add(new PartialBattleInfo
                {
                    BxIndex = info.BxIndex,
                    DestAddr = info.DestAddr,
                    SrcReg = info.SrcReg,
                    SrcRegValue = info.SrcRegValue,
                    IsFromTable = info.IsFromTable,
                    SourceTableAddr = info.SourceTableAddr,
                    SourceTable = info.SourceTable
                });
            }

            return clone;
        }

        private PathVariantInfo CreatePathVariant(int pathId, List<TextEntry> pathTexts, bool isLeaf, PathAnalysisResult source)
        {
            return new PathVariantInfo
            {
                PathId = pathId,
                IsLeaf = isLeaf,
                Texts = pathTexts
                    .OrderBy(t => t.Order)
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList(),
                MonsterPower = source.MonsterPower,
                MonsterLevel = source.MonsterLevel,
                MonsterBatchCount = source.MonsterBatchCount,
                DarkeningLevel = source.DarkeningLevel,
                RandomEncounterChance = source.RandomEncounterChance,
                CallsRandomEncounter = source.CallsRandomEncounter,
                BattleMonsterCount = source.BattleMonsterCount,
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                BattleMonsters = CloneBattleMonsters(source),
                PartiallyDefinedBattles = ClonePartialBattles(source),
                HasAnyTableLoad = source.HasPartialBattlePattern,
                LoadedValues = CloneLoadedValues(source)
            };
        }

        private List<BattleMonster> CloneBattleMonsters(PathAnalysisResult source)
        {
            return source.BattleMonsterEntries
                .Where(entry => entry.Value.val1 != 0 || entry.Value.val2 != 0)
                .OrderBy(entry => entry.Key)
                .Select(entry => new BattleMonster
                {
                    Index = entry.Key,
                    MonsterIndex1 = entry.Value.val1,
                    MonsterIndex2 = entry.Value.val2,
                    IsIndeterminate = entry.Value.isIndeterminate
                })
                .ToList();
        }

        private List<PartiallyDefinedBattle> ClonePartialBattles(PathAnalysisResult source)
        {
            return source.PartialBattles
                .OrderBy(p => p.BxIndex)
                .Select(p => new PartiallyDefinedBattle
                {
                    BxIndex = p.BxIndex,
                    RangeStart1 = p.RangeStart1,
                    RangeEnd1 = p.RangeEnd1,
                    RangeStart2 = p.RangeStart2,
                    RangeEnd2 = p.RangeEnd2
                })
                .ToList();
        }

        private List<OvrObject.LoadedValueInfo> CloneLoadedValues(PathAnalysisResult source)
        {
            return source.PartialBattleInfo
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
                .ToList();
        }

        public Dictionary<int, PathVariantInfo> BuildFinalPathVariants(List<PathVariantInfo> allResults)
        {
            var finalVariants = new Dictionary<int, PathVariantInfo>();

            var leafResults = allResults
                .Where(r => r.IsLeaf && r.HasAnyInfo)
                .OrderBy(r => r.PathId)
                .ToList();

            var uniqueVariants = new Dictionary<string, PathVariantInfo>();

            foreach (var result in leafResults)
            {
                string key = BuildVariantIdentityKey(result);
                if (!uniqueVariants.ContainsKey(key))
                    uniqueVariants[key] = result;
            }

            var orderedUniqueVariants = uniqueVariants.Values
                .OrderBy(v => v.PathId)
                .ToList();

            for (int i = 0; i < orderedUniqueVariants.Count; i++)
            {
                int key = i == 0 ? 0 : i;
                finalVariants[key] = orderedUniqueVariants[i];
            }

            return finalVariants;
        }

        private string BuildVariantIdentityKey(PathVariantInfo variant)
        {
            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.MonsterPower}|{variant.MonsterLevel}|{variant.MonsterBatchCount}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.BattleMonsterCount}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .Select(m => $"{m.Index}:{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}:{m.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(p => $"{p.BxIndex}:{p.RangeStart1:X2}-{p.RangeEnd1:X2}:{p.RangeStart2:X2}-{p.RangeEnd2:X2}"))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName)
                    .Select(v => $"{v.BxIndex}:{v.RegName}:{v.Value:X2}:{v.SourceAddr:X4}:{v.IsFirstTable}:{v.IsSaved}"))
                : "<NO_LOADS>";

            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}";
        }
    }
}