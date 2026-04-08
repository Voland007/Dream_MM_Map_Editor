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


﻿using System;
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
            List<TextEntry> inheritedDisplayTexts,
            BinaryReader br,
            byte targetX, byte targetY, List<PathVariantInfo> allResults, OvrObject ovrObject,
            HashSet<(uint From, uint To)> processedBackEdges = null,
            bool invalidateReturnRegistersAfterExternalCall = false,
            HashSet<uint> reachableAddresses = null,
            PathAnalysisResult inheritedState = null,
            int inheritedProbabilityNumerator = 1,
            int inheritedProbabilityDenominator = 1,
            bool inheritedProbabilityApplicable = true)
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
                    new HashSet<uint>(), depth + 1, path.CallDepth, currentPathId, targetX, targetY,
                    processedBackEdges, invalidateReturnRegistersAfterExternalCall,
                    path.PendingReturnAddresses == null ? new List<uint>() : new List<uint>(path.PendingReturnAddresses));
                var effectivePathResult = MergeAnalysisStates(inheritedState, pathResult);

                // Формируем тексты для этого пути с сохранением порядка
                if (reachableAddresses != null)
                {
                    foreach (var visitedAddress in pathResult.VisitedAddresses)
                        reachableAddresses.Add(visitedAddress);
                }

                var pathTexts = BuildPathTexts(pathResult, inheritedDisplayTexts);

                // Путь считается листовым, только если у него нет вложенных путей
                bool isLeaf = pathResult.AlternativePaths.Count == 0;

                var effectiveProbability = GetEffectivePathProbability(path);
                bool currentBranchProbabilityApplicable = effectiveProbability.denominator > 1;
                bool combinedProbabilityApplicable = inheritedProbabilityApplicable && currentBranchProbabilityApplicable;

                int combinedProbabilityNumerator = combinedProbabilityApplicable
                    ? inheritedProbabilityNumerator * Math.Max(0, effectiveProbability.numerator)
                    : 1;
                int combinedProbabilityDenominator = combinedProbabilityApplicable
                    ? inheritedProbabilityDenominator * Math.Max(1, effectiveProbability.denominator)
                    : 1;

                // Добавляем путь в результаты
                allResults.Add(CreatePathVariant(currentPathId, pathTexts, isLeaf, effectivePathResult, combinedProbabilityNumerator, combinedProbabilityDenominator));

                if (debugMode && pathTexts.Count > 0)
                {
                    AnalysisDebug.WriteLine($"      Найдено текстов в пути {currentPathId}: {pathTexts.Count} (листовой: {isLeaf})");
                    foreach (var text in pathTexts.OrderBy(t => t.Order))
                    {
                        AnalysisDebug.WriteLine($"        [{text.Order}] {(text.IsContextual ? "C" : "L")}: {text.Text}");
                    }
                }

                // Формируем набор текстов для наследования вложенными путями
                var newInheritedDisplayTexts = BuildInheritedTexts(inheritedDisplayTexts, pathResult);

                // Рекурсивно обрабатываем вложенные пути
                if (pathResult.AlternativePaths.Count > 0)
                {
                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"      Найдено {pathResult.AlternativePaths.Count} вложенных путей");
                    }
                    ProcessPaths(pathResult.AlternativePaths, currentPathId, depth + 1,
                        newInheritedDisplayTexts,
                        br, targetX, targetY,
                        allResults, ovrObject, processedBackEdges,
                        invalidateReturnRegistersAfterExternalCall, reachableAddresses,
                        effectivePathResult,
                        combinedProbabilityNumerator,
                        combinedProbabilityDenominator,
                        combinedProbabilityApplicable);
                }
            }
        }

        private List<TextEntry> BuildPathTexts(PathAnalysisResult pathResult,
            List<TextEntry> inheritedDisplayTexts)
        {
            var pathTexts = new List<TextEntry>();
            int nextOrder = 0;

            foreach (var text in inheritedDisplayTexts ?? Enumerable.Empty<TextEntry>())
            {
                pathTexts.Add(new TextEntry
                {
                    Text = text.Text,
                    Order = nextOrder++,
                    IsContextual = text.IsContextual,
                    Address = text.Address
                });
            }

            foreach (var text in ConvertSegmentTextsToDisplayOrder(pathResult?.OrderedTexts))
            {
                pathTexts.Add(new TextEntry
                {
                    Text = text.Text,
                    Order = nextOrder++,
                    IsContextual = text.IsContextual,
                    Address = text.Address
                });
            }

            return pathTexts;
        }

        private List<TextEntry> BuildInheritedTexts(
            List<TextEntry> inheritedDisplayTexts,
            PathAnalysisResult pathResult)
        {
            var result = new List<TextEntry>();

            foreach (var text in inheritedDisplayTexts ?? Enumerable.Empty<TextEntry>())
                result.Add(text.Clone());

            foreach (var text in ConvertSegmentTextsToDisplayOrder(pathResult?.OrderedTexts))
                result.Add(text.Clone());

            return result;
        }

        private List<TextEntry> ConvertSegmentTextsToDisplayOrder(IEnumerable<TextEntry> segmentTexts)
        {
            var result = new List<TextEntry>();
            if (segmentTexts == null)
                return result;

            int nextOrder = 0;

            foreach (var text in segmentTexts.Where(t => t != null && t.IsContextual).OrderBy(t => t.Order))
            {
                var clone = text.Clone();
                clone.Order = nextOrder++;
                result.Add(clone);
            }

            foreach (var text in segmentTexts.Where(t => t != null && !t.IsContextual).OrderBy(t => t.Order))
            {
                var clone = text.Clone();
                clone.Order = nextOrder++;
                result.Add(clone);
            }

            return result;
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
            if (currentState.TeleportTargetX.HasValue)
                merged.TeleportTargetX = currentState.TeleportTargetX;
            if (currentState.TeleportTargetY.HasValue)
                merged.TeleportTargetY = currentState.TeleportTargetY;

            if (currentState.IsBattleMonsterCountIndeterminate)
            {
                merged.BattleMonsterCount = null;
                merged.BattleMonsterCountRange = null;
                merged.IsBattleMonsterCountIndeterminate = true;
            }
            else if (currentState.BattleMonsterCountRange != null)
            {
                merged.BattleMonsterCountRange = new ValueRange8(currentState.BattleMonsterCountRange.Min, currentState.BattleMonsterCountRange.Max);
                merged.BattleMonsterCount = currentState.BattleMonsterCountRange.IsExact ? currentState.BattleMonsterCountRange.Min : (byte?)null;
                merged.IsBattleMonsterCountIndeterminate = false;
            }
            else if (currentState.BattleMonsterCount.HasValue)
            {
                merged.BattleMonsterCount = currentState.BattleMonsterCount;
                merged.BattleMonsterCountRange = new ValueRange8(currentState.BattleMonsterCount.Value, currentState.BattleMonsterCount.Value);
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
            clone.TeleportTargetX = source.TeleportTargetX;
            clone.TeleportTargetY = source.TeleportTargetY;
            clone.BattleMonsterCount = source.BattleMonsterCount;
            clone.BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max);
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

            clone.FoundTexts = new HashSet<string>(source.FoundTexts);
            clone.ContextTexts = new HashSet<string>(source.ContextTexts);
            clone.OrderedTexts = source.OrderedTexts.Select(t => t.Clone()).ToList();
            clone.VisitedAddresses = new HashSet<uint>(source.VisitedAddresses);
            clone.FirstLocalTextAddress = source.FirstLocalTextAddress;
            clone.ExitPendingReturnAddresses = source.ExitPendingReturnAddresses == null
                ? new List<uint>()
                : new List<uint>(source.ExitPendingReturnAddresses);
            clone.ExitCallDepth = source.ExitCallDepth;

            foreach (var alt in source.AlternativePaths)
            {
                clone.AlternativePaths.Add(new AlternativePath
                {
                    ObjectIndex = alt.ObjectIndex,
                    Address = alt.Address,
                    Condition = alt.Condition,
                    TargetAddress = alt.TargetAddress,
                    Analyzed = alt.Analyzed,
                    PathNumber = alt.PathNumber,
                    CompareValue = alt.CompareValue,
                    CompareRegister = alt.CompareRegister,
                    RegisterState = alt.RegisterState?.Clone(),
                    ProbabilityNumerator = alt.ProbabilityNumerator,
                    ProbabilityDenominator = alt.ProbabilityDenominator,
                    CallDepth = alt.CallDepth,
                    PendingReturnAddresses = alt.PendingReturnAddresses == null
                        ? new List<uint>()
                        : new List<uint>(alt.PendingReturnAddresses)
                });
            }

            return clone;
        }

        private PathVariantInfo CreatePathVariant(int pathId, List<TextEntry> pathTexts, bool isLeaf, PathAnalysisResult source, int probabilityNumerator, int probabilityDenominator)
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
                TeleportTargetX = source.TeleportTargetX,
                TeleportTargetY = source.TeleportTargetY,
                BattleMonsterCount = source.BattleMonsterCount,
                BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                BattleMonsters = CloneBattleMonsters(source),
                PartiallyDefinedBattles = ClonePartialBattles(source),
                HasAnyTableLoad = source.HasPartialBattlePattern,
                LoadedValues = CloneLoadedValues(source),
                ProbabilityNumerator = probabilityNumerator,
                ProbabilityDenominator = probabilityDenominator
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
                .Where(r => r.IsLeaf)
                .OrderBy(r => r.PathId)
                .ToList();

            var uniqueVariants = new Dictionary<string, PathVariantInfo>();

            foreach (var result in leafResults)
            {
                string exactKey = BuildVariantIdentityKey(result);
                if (!uniqueVariants.ContainsKey(exactKey))
                {
                    uniqueVariants[exactKey] = result;
                    continue;
                }

                var existingExact = uniqueVariants[exactKey];
                if (GetVariantPrecisionScore(result) > GetVariantPrecisionScore(existingExact))
                {
                    uniqueVariants[exactKey] = result;
                }
            }

            // Дополнительная дедупликация: один и тот же сценарий боя может порождать
            // несколько листовых путей с разной глубиной разворота цикла.
            // Для таких случаев оставляем наиболее информативный вариант.
            var semanticallyUnique = new Dictionary<string, PathVariantInfo>();
            foreach (var variant in uniqueVariants.Values.OrderBy(v => v.PathId))
            {
                string semanticKey = BuildSemanticVariantKey(variant);
                if (!semanticallyUnique.TryGetValue(semanticKey, out var existing))
                {
                    semanticallyUnique[semanticKey] = variant;
                    continue;
                }

                if (GetVariantPrecisionScore(variant) > GetVariantPrecisionScore(existing))
                {
                    semanticallyUnique[semanticKey] = variant;
                }
            }

            var orderedUniqueVariants = semanticallyUnique.Values
                .OrderBy(v => v.PathId)
                .ToList();

            for (int i = 0; i < orderedUniqueVariants.Count; i++)
            {
                int key = i == 0 ? 0 : i;
                finalVariants[key] = orderedUniqueVariants[i];
            }

            return finalVariants;
        }



        private (int numerator, int denominator) GetEffectivePathProbability(AlternativePath path)
        {
            if (path == null)
                return (1, 1);

            if (path.ProbabilityDenominator > 1 || path.ProbabilityNumerator == 0)
                return (path.ProbabilityNumerator, Math.Max(1, path.ProbabilityDenominator));

            if (path.RegisterState == null)
                return (1, 1);

            string compareRegister = path.CompareRegister?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(compareRegister) || !path.CompareValue.HasValue)
                return (1, 1);

            if (!path.RegisterState.TryGetRegisterRange(compareRegister, out var range) || range == null)
                return (1, 1);

            if (!path.RegisterState.TryGetRegisterDistribution(compareRegister, out var distribution) ||
                distribution != RegisterValueDistribution.UniformDiscreteRange)
                return (1, 1);

            string mnemonic = ExtractBranchMnemonic(path.Condition);
            if (string.IsNullOrEmpty(mnemonic))
                return (1, 1);

            bool branchTaken = !path.Condition.StartsWith("LINEAR", StringComparison.OrdinalIgnoreCase);
            int total = range.Max - range.Min + 1;
            if (total <= 0)
                return (1, 1);

            int favorable = 0;
            for (int value = range.Min; value <= range.Max; value++)
            {
                bool taken = mnemonic switch
                {
                    "JE" or "JZ" => value == path.CompareValue.Value,
                    "JNE" or "JNZ" => value != path.CompareValue.Value,
                    "JB" or "JC" or "JNAE" => value < path.CompareValue.Value,
                    "JAE" or "JNB" or "JNC" => value >= path.CompareValue.Value,
                    "JBE" or "JNA" => value <= path.CompareValue.Value,
                    "JA" or "JNBE" => value > path.CompareValue.Value,
                    _ => false
                };

                if (taken == branchTaken)
                    favorable++;
            }

            if (favorable < 0 || favorable > total)
                return (1, 1);

            int gcd = GreatestCommonDivisor(favorable, total);
            return (favorable / gcd, total / gcd);
        }

        private string ExtractBranchMnemonic(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                return null;

            string text = condition.Trim();
            if (text.StartsWith("LINEAR after ", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("LINEAR after ".Length);

            int spaceIndex = text.IndexOf(' ');
            return (spaceIndex >= 0 ? text.Substring(0, spaceIndex) : text).ToUpperInvariant();
        }

        private string BuildSemanticVariantKey(PathVariantInfo variant)
        {
            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.MonsterPower}|{variant.MonsterLevel}|{variant.MonsterBatchCount}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.HasAnyTableLoad}";

            string battleSkeleton = "<NO_BATTLE>";
            if (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0)
            {
                battleSkeleton = string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .GroupBy(m => $"{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}")
                    .Select(g => g.Key));
            }

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(p => $"{p.BxIndex}:{p.RangeStart1:X2}-{p.RangeEnd1:X2}:{p.RangeStart2:X2}-{p.RangeEnd2:X2}"))
                : "<NO_PARTIAL>";

            return $"{textKey}||{statKey}||{battleSkeleton}||{partialKey}";
        }



        private void SumVariantProbability(PathVariantInfo target, PathVariantInfo source)
        {
            if (target == null || source == null)
                return;

            int leftDen = Math.Max(1, target.ProbabilityDenominator);
            int rightDen = Math.Max(1, source.ProbabilityDenominator);
            int lcm = LeastCommonMultiple(leftDen, rightDen);
            int leftNum = target.ProbabilityNumerator * (lcm / leftDen);
            int rightNum = source.ProbabilityNumerator * (lcm / rightDen);
            int sum = leftNum + rightNum;
            int gcd = GreatestCommonDivisor(sum, lcm);
            target.ProbabilityNumerator = gcd == 0 ? sum : sum / gcd;
            target.ProbabilityDenominator = gcd == 0 ? lcm : lcm / gcd;
        }

        private int GreatestCommonDivisor(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        private int LeastCommonMultiple(int a, int b)
        {
            return Math.Abs(a / GreatestCommonDivisor(a, b) * b);
        }

        private int GetVariantPrecisionScore(PathVariantInfo variant)
        {
            int score = 0;

            if (variant.BattleMonsterCountRange != null)
            {
                score += variant.BattleMonsterCountRange.IsExact ? 20 : 50;
            }
            else if (variant.BattleMonsterCount.HasValue)
            {
                score += 20;
            }

            if (!variant.IsBattleMonsterCountIndeterminate)
                score += 10;

            if (variant.BattleMonsters != null)
            {
                score += variant.BattleMonsters.Count(m => !m.IsIndeterminate) * 4;
                score -= variant.BattleMonsters.Count(m => m.IsIndeterminate) * 10;
                score -= variant.BattleMonsters.Count;
            }

            if (variant.PartiallyDefinedBattles != null)
                score -= variant.PartiallyDefinedBattles.Count * 5;

            return score;
        }
        private string BuildVariantIdentityKey(PathVariantInfo variant)
        {
            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.MonsterPower}|{variant.MonsterLevel}|{variant.MonsterBatchCount}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}";

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