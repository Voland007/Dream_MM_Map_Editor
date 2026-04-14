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
            List<TextEntry> inheritedDisplayTexts,
            BinaryReader br,
            byte targetX, byte targetY, List<PathVariantInfo> allResults, OvrObject ovrObject,
            HashSet<(uint From, uint To)> processedBackEdges = null,
            bool invalidateReturnRegistersAfterExternalCall = false,
            HashSet<uint> reachableAddresses = null,
            PathAnalysisResult inheritedState = null,
            int inheritedProbabilityNumerator = 1,
            int inheritedProbabilityDenominator = 1,
            bool inheritedProbabilityApplicable = false,
            List<BranchChoice> inheritedBranchChoices = null)
        {
            processedBackEdges ??= new HashSet<(uint From, uint To)>();
            if (depth > 8) return;

            var sortedPaths = paths.OrderBy(p => p.Address).ToList();
            int localCounter = 1;
            foreach (var path in sortedPaths)
            {
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
                    path.PendingReturnAddresses == null ? new List<uint>() : new List<uint>(path.PendingReturnAddresses),
                    path.EmulatedMemory8 == null ? null : new Dictionary<ushort, byte>(path.EmulatedMemory8));
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
                bool currentBranchProbabilityApplicable = effectiveProbability.denominator > 1 &&
                    effectiveProbability.numerator > 0 &&
                    effectiveProbability.numerator < effectiveProbability.denominator;

                bool combinedProbabilityApplicable;
                int combinedProbabilityNumerator;
                int combinedProbabilityDenominator;

                if (inheritedProbabilityApplicable && currentBranchProbabilityApplicable)
                {
                    combinedProbabilityApplicable = true;
                    combinedProbabilityNumerator = inheritedProbabilityNumerator * Math.Max(0, effectiveProbability.numerator);
                    combinedProbabilityDenominator = inheritedProbabilityDenominator * Math.Max(1, effectiveProbability.denominator);
                }
                else if (inheritedProbabilityApplicable)
                {
                    combinedProbabilityApplicable = true;
                    combinedProbabilityNumerator = inheritedProbabilityNumerator;
                    combinedProbabilityDenominator = inheritedProbabilityDenominator;
                }
                else if (currentBranchProbabilityApplicable)
                {
                    combinedProbabilityApplicable = true;
                    combinedProbabilityNumerator = Math.Max(0, effectiveProbability.numerator);
                    combinedProbabilityDenominator = Math.Max(1, effectiveProbability.denominator);
                }
                else
                {
                    combinedProbabilityApplicable = false;
                    combinedProbabilityNumerator = 1;
                    combinedProbabilityDenominator = 1;
                }

                var currentBranchChoices = CloneBranchChoices(inheritedBranchChoices);
                var currentChoice = CreateBranchChoice(path);
                if (currentChoice != null)
                    currentBranchChoices.Add(currentChoice);

                // Добавляем путь в результаты
                allResults.Add(CreatePathVariant(currentPathId, pathTexts, isLeaf, effectivePathResult, combinedProbabilityNumerator, combinedProbabilityDenominator, currentBranchChoices));

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
                        combinedProbabilityApplicable,
                        currentBranchChoices);
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
            if (currentState.TeleportTargetXRange != null)
                merged.TeleportTargetXRange = new ValueRange8(currentState.TeleportTargetXRange.Min, currentState.TeleportTargetXRange.Max);
            if (currentState.TeleportTargetYRange != null)
                merged.TeleportTargetYRange = new ValueRange8(currentState.TeleportTargetYRange.Min, currentState.TeleportTargetYRange.Max);

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
            merged.TerminatedByRepeatedBackEdge = merged.TerminatedByRepeatedBackEdge || currentState.TerminatedByRepeatedBackEdge;
            merged.TerminatedByTerminalRet = merged.TerminatedByTerminalRet || currentState.TerminatedByTerminalRet;
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
            clone.IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump;
            clone.TeleportTargetX = source.TeleportTargetX;
            clone.TeleportTargetY = source.TeleportTargetY;
            clone.TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max);
            clone.TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max);
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
            clone.TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge;
            clone.TerminatedByTerminalRet = source.TerminatedByTerminalRet;

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
                        : new List<uint>(alt.PendingReturnAddresses),
                    EmulatedMemory8 = alt.EmulatedMemory8 == null
                        ? new Dictionary<ushort, byte>()
                        : new Dictionary<ushort, byte>(alt.EmulatedMemory8)
                });
            }

            return clone;
        }

        private PathVariantInfo CreatePathVariant(int pathId, List<TextEntry> pathTexts, bool isLeaf, PathAnalysisResult source, int probabilityNumerator, int probabilityDenominator, List<BranchChoice> branchChoices)
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
                IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump,
                TeleportTargetX = source.TeleportTargetX,
                TeleportTargetY = source.TeleportTargetY,
                TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max),
                TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max),
                BattleMonsterCount = source.BattleMonsterCount,
                BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate,
                BattleMonsters = CloneBattleMonsters(source),
                PartiallyDefinedBattles = ClonePartialBattles(source),
                HasAnyTableLoad = source.HasPartialBattlePattern,
                LoadedValues = CloneLoadedValues(source),
                ProbabilityNumerator = probabilityNumerator,
                ProbabilityDenominator = probabilityDenominator,
                TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge,
                TerminatedByTerminalRet = source.TerminatedByTerminalRet,
                BranchChoices = CloneBranchChoices(branchChoices)
            };
        }

        private List<BranchChoice> CloneBranchChoices(IEnumerable<BranchChoice> branchChoices)
        {
            return (branchChoices ?? Enumerable.Empty<BranchChoice>())
                .Where(choice => choice != null)
                .Select(choice => new BranchChoice
                {
                    Label = choice.Label,
                    Condition = choice.Condition,
                    CompareValue = choice.CompareValue,
                    CompareRegister = choice.CompareRegister,
                    IsLinear = choice.IsLinear
                })
                .ToList();
        }

        private BranchChoice CreateBranchChoice(AlternativePath path)
        {
            if (path == null)
                return null;

            bool isLinear = !string.IsNullOrWhiteSpace(path.Condition) &&
                path.Condition.StartsWith("LINEAR after ", StringComparison.OrdinalIgnoreCase);
            string mnemonic = ExtractBranchMnemonic(path.Condition);

            string label = null;
            if (path.IsInputChoiceBranch && path.CompareValue.HasValue)
            {
                bool branchRepresentsEquality =
                    (!isLinear && (mnemonic == "JE" || mnemonic == "JZ")) ||
                    (isLinear && (mnemonic == "JNE" || mnemonic == "JNZ"));

                if (branchRepresentsEquality)
                    label = ConvertChoiceValueToLabel(path.CompareValue.Value);
            }

            if (string.IsNullOrWhiteSpace(label) && TryExtractSplitAssignedValue(path.Condition, out byte splitValue))
                label = ConvertChoiceValueToLabel(splitValue);

            return new BranchChoice
            {
                Label = label,
                Condition = path.Condition,
                CompareValue = path.CompareValue,
                CompareRegister = path.CompareRegister,
                IsLinear = isLinear
            };
        }

        private string ConvertChoiceValueToLabel(byte value)
        {
            if (value == 0x1B)
                return "ESC";

            if (value >= 0x20 && value <= 0x7E)
                return ((char)value).ToString();

            return null;
        }

        private bool TryExtractSplitAssignedValue(string condition, out byte value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(condition))
                return false;

            const string marker = " = 0x";
            int markerIndex = condition.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return false;

            int hexStart = markerIndex + marker.Length;
            if (hexStart + 2 > condition.Length)
                return false;

            string hex = condition.Substring(hexStart, 2);
            return byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
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

        private bool HasAnyOutcome(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.MonsterPower.HasValue || variant.MonsterLevel.HasValue ||
                variant.MonsterBatchCount.HasValue || variant.DarkeningLevel.HasValue ||
                variant.RandomEncounterChance.HasValue)
                return true;

            if (variant.CallsRandomEncounter)
                return true;

            if (variant.TeleportTargetX.HasValue || variant.TeleportTargetY.HasValue)
                return true;

            if (variant.BattleMonsterCount.HasValue || variant.BattleMonsterCountRange != null || variant.IsBattleMonsterCountIndeterminate)
                return true;

            if (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0)
                return true;

            if (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0)
                return true;

            if (variant.HasAnyTableLoad)
                return true;

            if (variant.LoadedValues != null && variant.LoadedValues.Count > 0)
                return true;

            return false;
        }

        private bool IsPromptOnlyLeaf(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (HasAnyOutcome(variant))
                return false;

            return variant.Texts != null && variant.Texts.Count > 0;
        }

        private bool IsMeaningfulLeaf(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.TerminatedByRepeatedBackEdge)
                return false;

            if (variant.TerminatedByTerminalRet)
                return true;

            if (HasAnyOutcome(variant))
                return true;

            if (IsPureEmptyLeafVariant(variant))
                return true;

            // Любая leaf-ветка с собственным текстом считается осмысленной.
            // Иначе теряются реальные конечные варианты без явного outcome,
            // например вариант с одним сообщением после выбора в меню.
            if (variant.Texts != null && variant.Texts.Count > 0)
                return true;

            return false;
        }



        private int GetPathDepth(int pathId)
        {
            if (pathId <= 0)
                return 0;

            int depth = -1;
            while (pathId > 0)
            {
                depth++;
                pathId /= 10;
            }

            return depth;
        }

        private bool IsPureEmptyLeafVariant(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.Texts != null && variant.Texts.Count > 0)
                return false;

            if (HasAnyOutcome(variant))
                return false;

            return true;
        }

        public Dictionary<int, PathVariantInfo> BuildFinalPathVariants(List<PathVariantInfo> allResults)
        {
            var finalVariants = new Dictionary<int, PathVariantInfo>();

            var leafResults = allResults
                .Where(r => r.IsLeaf)
                .Where(IsMeaningfulLeaf)
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

            // Вероятности разрешены только для веток, где они были выставлены явно
            // реальным источником случайности (например, SUB_509A).
            // Ничего не вычисляем постфактум по диапазонам регистров, чтобы ветки,
            // зависящие от пользовательского ввода (SUB_5101), не получали probability.
            if (path.ProbabilityDenominator > 1 || path.ProbabilityNumerator == 0)
                return (path.ProbabilityNumerator, Math.Max(1, path.ProbabilityDenominator));

            return (1, 1);
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

            string statKey = $"{variant.MonsterPower}|{variant.MonsterLevel}|{variant.MonsterBatchCount}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.HasAnyTableLoad}";

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

            string statKey = $"{variant.MonsterPower}|{variant.MonsterLevel}|{variant.MonsterBatchCount}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}";

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

            string branchKey = variant.BranchChoices != null && variant.BranchChoices.Count > 0
                ? string.Join(";", variant.BranchChoices
                    .Where(c => c != null)
                    .Select(c => $"{c.Label}|{c.Condition}|{c.CompareRegister}|{c.CompareValue?.ToString() ?? string.Empty}|{c.IsLinear}"))
                : "<NO_BRANCHES>";

            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}||{branchKey}";
        }
    }
}
