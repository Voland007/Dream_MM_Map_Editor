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
            uint firstLocalTextAddress, BinaryReader br, OvrObject debugObject,
            byte targetX, byte targetY, List<PathResult> allResults, OvrObject ovrObject)
        {
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

                bool debugMode = debugObject != null;

                if (debugMode)
                {
                    Debug.WriteLine($"\n  Анализ пути {currentPathId} (глубина {depth}) -> 0x{path.TargetAddress:X4} ({path.Condition})");
                }

                // Создаём НОВЫЙ RegisterTracker для каждого пути
                var pathRegisterTracker = new RegisterTracker();
                var pathResult = _codeExecutor.ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                    new HashSet<uint>(), depth + 1, 0, debugObject, currentPathId, targetX, targetY);

                // Формируем тексты для этого пути с сохранением порядка
                var pathTexts = BuildPathTexts(path, pathResult, inheritedContextTexts,
                    inheritedLocalTexts, firstLocalTextAddress);

                // Путь считается листовым, только если у него нет вложенных путей
                bool isLeaf = pathResult.AlternativePaths.Count == 0;

                // Добавляем путь в результаты
                allResults.Add(new PathResult
                {
                    PathId = currentPathId,
                    Texts = pathTexts,
                    IsLeaf = isLeaf
                });

                if (debugMode && pathTexts.Count > 0)
                {
                    Debug.WriteLine($"      Найдено текстов в пути {currentPathId}: {pathTexts.Count} (листовой: {isLeaf})");
                    foreach (var text in pathTexts.OrderBy(t => t.Order))
                    {
                        Debug.WriteLine($"        [{text.Order}] {(text.IsContextual ? "C" : "L")}: {text.Text}");
                    }
                }

                // Добавляем информацию о монстрах
                MergeMonsterInfo(pathResult, ovrObject);

                // Формируем набор текстов для наследования вложенными путями
                var (newInheritedContextTexts, newInheritedLocalTexts) = BuildInheritedTexts(
                    path, pathResult, inheritedContextTexts, inheritedLocalTexts, firstLocalTextAddress);

                // Рекурсивно обрабатываем вложенные пути
                if (pathResult.AlternativePaths.Count > 0)
                {
                    if (debugMode)
                    {
                        Debug.WriteLine($"      Найдено {pathResult.AlternativePaths.Count} вложенных путей");
                    }
                    ProcessPaths(pathResult.AlternativePaths, currentPathId, depth + 1,
                        newInheritedContextTexts, newInheritedLocalTexts,
                        firstLocalTextAddress, br, debugObject, targetX, targetY,
                        allResults, ovrObject);
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

        private void MergeMonsterInfo(PathAnalysisResult source, OvrObject target)
        {
            if (source.MonsterPower.HasValue && !target.MonsterPower.HasValue)
                target.MonsterPower = source.MonsterPower.Value;

            if (source.MonsterLevel.HasValue && !target.MonsterLevel.HasValue)
                target.MonsterLevel = source.MonsterLevel.Value;

            foreach (var entry in source.BattleMonsterEntries)
            {
                if (!target.BattleMonsters.Any(m => m.Index == entry.Key))
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
                if (!target.PartiallyDefinedBattles.Any(p => p.BxIndex == partial.BxIndex))
                {
                    target.AddPartiallyDefinedBattle(
                        partial.BxIndex,
                        partial.RangeStart1,
                        partial.RangeEnd1,
                        partial.RangeStart2,
                        partial.RangeEnd2
                    );
                }
            }

            if (source.HasPartialBattlePattern)
            {
                target.HasAnyTableLoad = true;
                foreach (var info in source.PartialBattleInfo)
                {
                    if (!target.LoadedValues.Any(v => v.BxIndex == info.BxIndex &&
                                                      v.RegName == info.SrcReg &&
                                                      v.SourceAddr == (info.SourceTableAddr ?? 0)))
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
        }

        public Dictionary<int, List<string>> BuildFinalPaths(List<PathResult> allResults)
        {
            var finalOrderedPaths = new Dictionary<int, List<string>>();

            // Оставляем только листовые пути
            var leafResults = allResults
                .Where(r => r.IsLeaf && r.Texts.Count > 0)
                .OrderBy(r => r.PathId)
                .ToList();

            // Группируем по уникальному содержимому текста, но с сохранением порядка
            var uniqueTextGroups = new Dictionary<string, (int pathId, List<string> texts)>();

            foreach (var result in leafResults)
            {
                // Преобразуем TextEntry в список строк, сохраняя порядок
                var orderedTexts = result.Texts
                    .OrderBy(t => t.Order)
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                if (orderedTexts.Count == 0)
                    continue;

                // Создаём ключ из текстов для сравнения (порядок важен!)
                string key = string.Join("|", orderedTexts);

                // Если такой набор текстов ещё не встречался, добавляем его
                if (!uniqueTextGroups.ContainsKey(key))
                {
                    uniqueTextGroups[key] = (result.PathId, orderedTexts);
                }
            }

            // Преобразуем в список и сортируем по pathId
            var uniqueResults = uniqueTextGroups.Values
                .OrderBy(r => r.pathId)
                .ToList();

            if (uniqueResults.Count > 0)
            {
                // Первый путь всегда сохраняем с ключом 0
                finalOrderedPaths[0] = uniqueResults[0].texts;

                // Остальные пути сохраняем с ключами 1,2,3...
                for (int i = 1; i < uniqueResults.Count; i++)
                {
                    finalOrderedPaths[i] = uniqueResults[i].texts;
                }
            }

            return finalOrderedPaths;
        }
    }
}