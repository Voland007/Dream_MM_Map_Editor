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
        private const ushort PartyCountAddress = 0x3BC0;
        private const int MaxPathProcessingDepth = 24;
        private const int MaxPathVariantsPerSingleOccurrence = 3072;

        private readonly OvrFileConfig _config;
        private readonly CodeExecutor _codeExecutor;

        public PathAnalyzer(OvrFileConfig config, CodeExecutor codeExecutor)
        {
            _config = config;
            _codeExecutor = codeExecutor;
        }

        public void ProcessPaths(List<AlternativePath> paths, decimal basePathOrderKey, int depth,
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
            List<BranchChoice> inheritedBranchChoices = null,
            HashSet<ushort> persistentStateAddresses = null)
        {
            processedBackEdges ??= new HashSet<(uint From, uint To)>();
            if (depth > MaxPathProcessingDepth) return;
            if (HasReachedPathVariantBudget(allResults, targetX, targetY)) return;

            var sortedPaths = paths.OrderBy(p => p.Address).ToList();
            int localCounter = 1;
            foreach (var path in sortedPaths)
            {
                if (HasReachedPathVariantBudget(allResults, targetX, targetY)) return;

                decimal currentPathOrderKey = basePathOrderKey * 10 + localCounter;
                int currentPathId = ToDebugPathId(currentPathOrderKey);
                localCounter++;

                bool debugMode = AnalysisDebug.IsEnabledFor(targetX, targetY);

                if (debugMode)
                {
                    AnalysisDebug.WriteLine($"\n  Анализ пути {FormatPathOrderKey(currentPathOrderKey)} (глубина {depth}) -> 0x{path.TargetAddress:X4} ({path.Condition})");
                }

                // Продолжаем путь с тем состоянием регистров, которое было в точке ветвления
                var pathRegisterTracker = path.RegisterState?.Clone() ?? new RegisterTracker();
                var pathResult = _codeExecutor.ExecuteCodeAtAddress(br, path.TargetAddress, pathRegisterTracker,
                    new HashSet<uint>(), depth + 1, path.CallDepth, currentPathId, targetX, targetY,
                    processedBackEdges, invalidateReturnRegistersAfterExternalCall,
                    path.PendingReturnAddresses == null ? new List<uint>() : new List<uint>(path.PendingReturnAddresses),
                    path.EmulatedMemory8 == null ? null : new Dictionary<ushort, byte>(path.EmulatedMemory8),
                    path.EmulatedPartyPointers == null ? null : path.EmulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    path.EmulatedPartyPointerBytes == null ? null : path.EmulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    persistentStateAddresses == null ? null : new HashSet<ushort>(persistentStateAddresses),
                    path.EmulatedMemory8Ranges == null ? null : CloneRangeDictionary(path.EmulatedMemory8Ranges),
                    path.EmulatedMemory8RangeDistributions == null
                        ? null
                        : new Dictionary<ushort, RegisterValueDistribution>(path.EmulatedMemory8RangeDistributions),
                    initialPendingPersistentCounterProgressions: ClonePendingPersistentCounterProgressions(path.PendingPersistentCounterProgressions));
                var effectivePathResult = MergeAnalysisStates(inheritedState, pathResult);
                MergeStateValueConstraints(effectivePathResult.StateValueConstraints, path.BranchStateValueConstraints);
                effectivePathResult.LocallyMaterializedStateValueConstraintAddresses.UnionWith(
                    path.BranchLocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>());

                // Формируем тексты для этого пути с сохранением порядка
                if (reachableAddresses != null)
                {
                    foreach (var visitedAddress in pathResult.VisitedAddresses)
                        reachableAddresses.Add(visitedAddress);
                }

                var pathTexts = BuildPathTexts(pathResult, inheritedDisplayTexts);

                // Путь считается листовым, только если у него нет вложенных путей
                bool reachedPathBudget = WillReachPathVariantBudget(allResults);
                bool reachedDepthBudget = depth + 1 >= MaxPathProcessingDepth;
                bool isLeaf = pathResult.AlternativePaths.Count == 0 ||
                              reachedPathBudget ||
                              reachedDepthBudget;

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

                ApplyInlinePathProbability(pathResult, ref combinedProbabilityApplicable,
                    ref combinedProbabilityNumerator, ref combinedProbabilityDenominator);
                NormalizeProbabilityForStorage(ref combinedProbabilityNumerator, ref combinedProbabilityDenominator);

                var currentBranchChoices = CloneBranchChoices(inheritedBranchChoices);
                var currentChoice = CreateBranchChoice(path);
                if (currentChoice != null)
                    currentBranchChoices.Add(currentChoice);
                AppendBranchChoices(currentBranchChoices, pathResult.InlineBranchChoices);

                // Добавляем путь в результаты
                allResults.Add(CreatePathVariant(currentPathId, currentPathOrderKey, pathTexts, isLeaf, effectivePathResult, combinedProbabilityNumerator, combinedProbabilityDenominator, currentBranchChoices));

                if (debugMode && pathTexts.Count > 0)
                {
                    AnalysisDebug.WriteLine($"      Найдено текстов в пути {FormatPathOrderKey(currentPathOrderKey)}: {pathTexts.Count} (листовой: {isLeaf})");
                    foreach (var text in pathTexts.OrderBy(t => t.Order))
                    {
                        AnalysisDebug.WriteLine($"        [{text.Order}] {(text.IsContextual ? "C" : "L")}: {text.Text}");
                    }
                }

                // Формируем набор текстов для наследования вложенными путями
                var newInheritedDisplayTexts = BuildInheritedTexts(inheritedDisplayTexts, pathResult);

                // Рекурсивно обрабатываем вложенные пути
                if (!reachedPathBudget && !reachedDepthBudget && pathResult.AlternativePaths.Count > 0)
                {
                    if (debugMode)
                    {
                        AnalysisDebug.WriteLine($"      Найдено {pathResult.AlternativePaths.Count} вложенных путей");
                    }
                    ProcessPaths(pathResult.AlternativePaths, currentPathOrderKey, depth + 1,
                        newInheritedDisplayTexts,
                        br, targetX, targetY,
                        allResults, ovrObject, processedBackEdges,
                        invalidateReturnRegistersAfterExternalCall, reachableAddresses,
                        effectivePathResult,
                        combinedProbabilityNumerator,
                        combinedProbabilityDenominator,
                        combinedProbabilityApplicable,
                        currentBranchChoices,
                        persistentStateAddresses);
                }
            }
        }

        private static bool HasReachedPathVariantBudget(List<PathVariantInfo> allResults, byte targetX, byte targetY)
        {
            if (!IsAtPathVariantBudget(allResults))
                return false;

            if (AnalysisDebug.IsEnabledFor(targetX, targetY))
            {
                AnalysisDebug.WriteLine(
                    $"      Достигнут лимит вариантов пути ({MaxPathVariantsPerSingleOccurrence}), дальнейшие ветки свернуты");
            }

            return true;
        }

        private static bool IsAtPathVariantBudget(List<PathVariantInfo> allResults)
        {
            return allResults != null && allResults.Count >= MaxPathVariantsPerSingleOccurrence;
        }

        private static bool WillReachPathVariantBudget(List<PathVariantInfo> allResults)
        {
            return allResults != null && allResults.Count + 1 >= MaxPathVariantsPerSingleOccurrence;
        }

        private void ApplyInlinePathProbability(PathAnalysisResult pathResult,
            ref bool probabilityApplicable,
            ref int probabilityNumerator,
            ref int probabilityDenominator)
        {
            if (pathResult == null ||
                pathResult.InlineProbabilityDenominator <= 1 ||
                pathResult.InlineProbabilityNumerator <= 0 ||
                pathResult.InlineProbabilityNumerator >= pathResult.InlineProbabilityDenominator)
            {
                return;
            }

            if (probabilityApplicable)
            {
                long numerator = (long)probabilityNumerator * pathResult.InlineProbabilityNumerator;
                long denominator = (long)probabilityDenominator * Math.Max(1, pathResult.InlineProbabilityDenominator);
                probabilityNumerator = numerator > int.MaxValue ? int.MaxValue : (int)numerator;
                probabilityDenominator = denominator > int.MaxValue ? int.MaxValue : (int)denominator;
            }
            else
            {
                probabilityApplicable = true;
                probabilityNumerator = pathResult.InlineProbabilityNumerator;
                probabilityDenominator = Math.Max(1, pathResult.InlineProbabilityDenominator);
            }
        }

        private void NormalizeProbabilityForStorage(ref int numerator, ref int denominator)
        {
            numerator = Math.Max(0, numerator);
            denominator = Math.Max(1, denominator);

            if (numerator == 0)
            {
                denominator = 1;
                return;
            }

            if (numerator >= denominator)
            {
                numerator = 1;
                denominator = 1;
                return;
            }

            // Сохраняем исходную дробь пути: 1/6, 2/12 и 3/18 имеют один процент,
            // но описывают разные вложенные случайные ветки и не должны схлопываться
            // в один отображаемый outcome.
        }

        private static int ToDebugPathId(decimal pathOrderKey)
        {
            if (pathOrderKey <= 0)
                return 0;

            return pathOrderKey > int.MaxValue
                ? int.MaxValue
                : (int)pathOrderKey;
        }

        private static string FormatPathOrderKey(decimal pathOrderKey)
        {
            return pathOrderKey.ToString("0");
        }

        private static decimal GetPathOrderKey(PathVariantInfo variant)
        {
            if (variant == null)
                return decimal.MaxValue;

            return variant.PathOrderKey > 0
                ? variant.PathOrderKey
                : variant.PathId;
        }

        private static Dictionary<ushort, ValueRange8> CloneRangeDictionary(Dictionary<ushort, ValueRange8> source)
        {
            if (source == null || source.Count == 0)
                return new Dictionary<ushort, ValueRange8>();

            return source.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value == null ? null : new ValueRange8(kvp.Value.Min, kvp.Value.Max));
        }

        private static Dictionary<ushort, byte> CloneStaticMapDataReads(Dictionary<ushort, byte> source)
        {
            return source == null || source.Count == 0
                ? new Dictionary<ushort, byte>()
                : new Dictionary<ushort, byte>(source);
        }

        private static void MergeStaticMapDataReads(Dictionary<ushort, byte> target, Dictionary<ushort, byte> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            foreach (var kvp in source)
                target[kvp.Key] = kvp.Value;
        }

        private static string BuildStaticMapDataReadKey(PathVariantInfo variant)
        {
            return variant?.StaticMapDataReads == null || variant.StaticMapDataReads.Count == 0
                ? "<NO_STATIC_MAP_READS>"
                : string.Join(";",
                    variant.StaticMapDataReads
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"{kvp.Key:X4}:{kvp.Value:X2}"));
        }

        private void NormalizeProbability(ref long numerator, ref long denominator)
        {
            if (denominator <= 0)
                denominator = 1;

            long gcd = GreatestCommonDivisor(numerator, denominator);
            if (gcd > 1)
            {
                numerator /= gcd;
                denominator /= gcd;
            }
        }

        private long GreatestCommonDivisor(long a, long b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                long remainder = a % b;
                a = b;
                b = remainder;
            }

            return a == 0 ? 1 : a;
        }

        private List<BranchChoice> CloneBranchChoices(List<BranchChoice> source)
        {
            if (source == null || source.Count == 0)
                return new List<BranchChoice>();

            return source
                .Where(choice => choice != null)
                .Select(choice => choice.Clone())
                .ToList();
        }

        private void AppendBranchChoices(List<BranchChoice> target, IEnumerable<BranchChoice> source)
        {
            if (target == null || source == null)
                return;

            foreach (var choice in source.Where(choice => choice != null))
            {
                bool exists = target.Any(existing =>
                    existing != null &&
                    string.Equals(existing.GetIdentityKey(), choice.GetIdentityKey(), StringComparison.OrdinalIgnoreCase));

                if (exists)
                    continue;

                target.Add(choice.Clone());
            }
        }

        private BranchChoice CreateBranchChoice(AlternativePath path)
        {
            if (path == null)
                return null;

            bool isLinear = !string.IsNullOrWhiteSpace(path.Condition) &&
                            path.Condition.StartsWith("LINEAR after ", StringComparison.OrdinalIgnoreCase);
            string technicalLabel = path.IsInputChoiceBranch
                ? (isLinear ? "InputChoiceLinear" : "InputChoiceBranch")
                : (isLinear ? "Linear" : "Branch");

            return new BranchChoice
            {
                Label = technicalLabel,
                Condition = path.Condition,
                CompareValue = path.CompareValue,
                CompareRegister = path.CompareRegister,
                CompareMemoryAddress = path.CompareMemoryAddress,
                IsLinear = isLinear,
                GuardPredicate = path.BranchPartyPredicate?.Clone()
            };
        }

        private List<TextEntry> BuildPathTexts(PathAnalysisResult pathResult,
            List<TextEntry> inheritedDisplayTexts)
        {
            return ComposeDisplayTexts(
                inheritedDisplayTexts,
                ConvertSegmentTextsToDisplayOrder(pathResult?.OrderedTexts));
        }

        private List<TextEntry> BuildInheritedTexts(
            List<TextEntry> inheritedDisplayTexts,
            PathAnalysisResult pathResult)
        {
            var result = new List<TextEntry>();

            return ComposeDisplayTexts(
                inheritedDisplayTexts,
                ConvertSegmentTextsToDisplayOrder(pathResult?.OrderedTexts));
        }

        private List<TextEntry> ComposeDisplayTexts(params IEnumerable<TextEntry>[] textGroups)
        {
            var result = new List<TextEntry>();

            foreach (var group in textGroups ?? Array.Empty<IEnumerable<TextEntry>>())
            {
                foreach (var text in group ?? Enumerable.Empty<TextEntry>())
                    AppendMergedDisplayText(result, text);
            }

            for (int i = 0; i < result.Count; i++)
                result[i].Order = i;

            return result;
        }

        private void AppendMergedDisplayText(List<TextEntry> result, TextEntry text)
        {
            if (result == null || text == null || string.IsNullOrEmpty(text.Text))
                return;

            var candidate = text.Clone();
            var existingExact = result.FirstOrDefault(entry =>
                entry != null &&
                entry.IsContextual == candidate.IsContextual &&
                string.Equals(entry.Text, candidate.Text, StringComparison.Ordinal));

            if (existingExact != null)
            {
                PromoteDisplayTextMetadata(existingExact, candidate);
                return;
            }

            if (TryReplacePendingInferredLootContainerIntro(result, candidate))
                return;

            result.Add(candidate);
        }

        private static bool PromoteDisplayTextMetadata(TextEntry existing, TextEntry candidate)
        {
            if (existing == null || candidate == null)
                return false;

            bool updated = false;
            bool upgradedSemantic = existing.SemanticKind == TextSemanticKind.Unknown &&
                candidate.SemanticKind != TextSemanticKind.Unknown;
            bool upgradedEvidence = existing.IsInferred && !candidate.IsInferred;

            if (upgradedSemantic)
            {
                existing.SemanticKind = candidate.SemanticKind;
                updated = true;
            }

            if (upgradedEvidence)
            {
                existing.IsInferred = false;
                updated = true;
            }

            if ((upgradedSemantic || upgradedEvidence) && candidate.Address != 0)
            {
                existing.Address = candidate.Address;
                updated = true;
            }

            return updated;
        }

        private static bool TryReplacePendingInferredLootContainerIntro(List<TextEntry> result, TextEntry candidate)
        {
            if (result == null || candidate == null)
                return false;

            if (candidate.SemanticKind != TextSemanticKind.LootContainerIntro || candidate.IsInferred)
                return false;

            for (int i = result.Count - 1; i >= 0; i--)
            {
                var existing = result[i];
                if (existing == null)
                    continue;

                if (existing.IsContextual != candidate.IsContextual)
                    continue;

                if (IsLootContainerIntroEntry(existing))
                {
                    if (!existing.IsInferred)
                        return false;

                    existing.Text = candidate.Text;
                    existing.Address = candidate.Address != 0 ? candidate.Address : existing.Address;
                    existing.SemanticKind = candidate.SemanticKind != TextSemanticKind.Unknown
                        ? candidate.SemanticKind
                        : existing.SemanticKind;
                    existing.IsInferred = false;
                    return true;
                }

                if (!IsLootPayloadEntry(existing))
                    return false;
            }

            return false;
        }

        private static bool IsLootContainerIntroEntry(TextEntry entry)
        {
            return entry?.SemanticKind == TextSemanticKind.LootContainerIntro;
        }

        private static bool IsLootPayloadEntry(TextEntry entry)
        {
            return entry?.SemanticKind == TextSemanticKind.LootPayload;
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

            currentState = RebaseSpecialEventOrders(currentState, inheritedState.NextSpecialEventOrder);

            var merged = ClonePathAnalysisResult(inheritedState);
            merged.ExitEmulatedMemory8 = currentState.ExitEmulatedMemory8 == null
                ? new Dictionary<ushort, byte>()
                : new Dictionary<ushort, byte>(currentState.ExitEmulatedMemory8);
            merged.ExitEmulatedMemory8Ranges = CloneRangeDictionary(currentState.ExitEmulatedMemory8Ranges);
            merged.ExitEmulatedMemory8RangeDistributions = currentState.ExitEmulatedMemory8RangeDistributions == null
                ? new Dictionary<ushort, RegisterValueDistribution>()
                : new Dictionary<ushort, RegisterValueDistribution>(currentState.ExitEmulatedMemory8RangeDistributions);

            if (currentState.RandomEncounterMonsterPowerCap.HasValue)
                merged.RandomEncounterMonsterPowerCap = currentState.RandomEncounterMonsterPowerCap;
            if (currentState.RandomEncounterMonsterLevelCap.HasValue)
                merged.RandomEncounterMonsterLevelCap = currentState.RandomEncounterMonsterLevelCap;
            if (currentState.RandomEncounterMonsterBatchCountCap.HasValue)
                merged.RandomEncounterMonsterBatchCountCap = currentState.RandomEncounterMonsterBatchCountCap;
            if (currentState.DarkeningLevel.HasValue)
                merged.DarkeningLevel = currentState.DarkeningLevel;
            if (currentState.RandomEncounterChance.HasValue)
                merged.RandomEncounterChance = currentState.RandomEncounterChance;
            if (currentState.RandomEncounterRubicon.HasValue)
                merged.RandomEncounterRubicon = currentState.RandomEncounterRubicon;
            merged.CallsRandomEncounter = merged.CallsRandomEncounter || currentState.CallsRandomEncounter;
            merged.DisablesCurrentMapEvent = merged.DisablesCurrentMapEvent || currentState.DisablesCurrentMapEvent;
            if (currentState.RandomEncounterInstructionAddress != 0 &&
                (merged.RandomEncounterInstructionAddress == 0 ||
                 currentState.RandomEncounterInstructionAddress < merged.RandomEncounterInstructionAddress))
            {
                merged.RandomEncounterInstructionAddress = currentState.RandomEncounterInstructionAddress;
            }
            if (currentState.RandomEncounterExecutionOrder != 0 &&
                (merged.RandomEncounterExecutionOrder == 0 ||
                 currentState.RandomEncounterExecutionOrder < merged.RandomEncounterExecutionOrder))
            {
                merged.RandomEncounterExecutionOrder = currentState.RandomEncounterExecutionOrder;
            }
            if (currentState.NextSpecialEventOrder > merged.NextSpecialEventOrder)
                merged.NextSpecialEventOrder = currentState.NextSpecialEventOrder;
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

            foreach (var kvp in currentState.PendingPersistentCounterProgressions ?? Enumerable.Empty<KeyValuePair<ushort, PersistentCounterProgressionInfo>>())
                merged.PendingPersistentCounterProgressions[kvp.Key] = kvp.Value?.Clone();

            foreach (var progression in currentState.PersistentCounterProgressions ?? Enumerable.Empty<PersistentCounterProgressionInfo>())
            {
                if (progression == null)
                    continue;

                string key = progression.GetIdentityKey();
                if (!merged.PersistentCounterProgressions.Any(existing => existing != null && existing.GetIdentityKey() == key))
                    merged.PersistentCounterProgressions.Add(progression.Clone());
            }

            foreach (var dependency in currentState.DynamicRandomBoundDependencies ?? Enumerable.Empty<DynamicRandomBoundDependencyInfo>())
            {
                if (dependency == null)
                    continue;

                string key = dependency.GetIdentityKey();
                if (!merged.DynamicRandomBoundDependencies.Any(existing => existing != null && existing.GetIdentityKey() == key))
                    merged.DynamicRandomBoundDependencies.Add(dependency.Clone());
            }

            foreach (var entry in currentState.BattleMonsterEntries)
                merged.BattleMonsterEntries[entry.Key] = entry.Value;

            foreach (var partial in currentState.PartialBattles)
            {
                string partialKey = partial.GetIdentityKey();
                if (!merged.PartialBattles.Any(p => p.GetIdentityKey() == partialKey))
                {
                    merged.PartialBattles.Add(partial.Clone());
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
                    i.SourceTableBaseAddr == info.SourceTableBaseAddr &&
                    i.SourceTable == info.SourceTable &&
                    i.OriginalSourceIndex == info.OriginalSourceIndex &&
                    i.SourceIndexProviderAddr == info.SourceIndexProviderAddr &&
                    i.SourceIndexBehavior == info.SourceIndexBehavior &&
                    i.SourceIndexExternallyDerived == info.SourceIndexExternallyDerived))
                {
                    merged.PartialBattleInfo.Add(new PartialBattleInfo
                    {
                        BxIndex = info.BxIndex,
                        DestAddr = info.DestAddr,
                        SrcReg = info.SrcReg,
                        SrcRegValue = info.SrcRegValue,
                        ValueMin = info.ValueMin,
                        ValueMax = info.ValueMax,
                        IsFromTable = info.IsFromTable,
                        SourceTableAddr = info.SourceTableAddr,
                        SourceTableBaseAddr = info.SourceTableBaseAddr,
                        SourceTable = info.SourceTable,
                        OriginalSourceIndex = info.OriginalSourceIndex,
                        SourceIndexProviderAddr = info.SourceIndexProviderAddr,
                        SourceIndexBehavior = info.SourceIndexBehavior,
                        SourceIndexExternallyDerived = info.SourceIndexExternallyDerived
                    });
                }
            }

            merged.HasPartialBattlePattern = merged.HasPartialBattlePattern || currentState.HasPartialBattlePattern;

            foreach (var access in currentState.PartyFieldAccesses ?? Enumerable.Empty<PartyFieldReference>())
            {
                if (access == null)
                    continue;

                bool exists = merged.PartyFieldAccesses.Any(a =>
                    a != null &&
                    a.Field == access.Field &&
                    a.Offset == access.Offset &&
                    a.EffectiveAddress == access.EffectiveAddress &&
                    a.IsRead == access.IsRead &&
                    a.IsWrite == access.IsWrite &&
                    a.IsCompare == access.IsCompare &&
                    ((a.Member?.MemberIndex) == (access.Member?.MemberIndex)) &&
                    ((a.Member?.PointerAddress) == (access.Member?.PointerAddress)));

                if (!exists)
                    merged.PartyFieldAccesses.Add(access.Clone());
            }

            var mergedPendingHp = merged.PendingPartyHpOperation;
            var mergedPendingSp = merged.PendingPartySpOperation;
            var completedPartyStatOperations = PendingPartyStatOperationMerger.MergeCompletedContinuations(
                currentState.CompletedPartyStatOperations,
                ref mergedPendingHp,
                ref mergedPendingSp,
                MergePendingPartyStatOperation,
                MatchesPendingPartyTarget);
            merged.CompletedPartyStatOperations.AddRange(completedPartyStatOperations);

            merged.PendingPartyHpOperation = MergePendingPartyStatOperation(
                mergedPendingHp,
                currentState.PendingPartyHpOperation);

            merged.PendingPartySpOperation = MergePendingPartyStatOperation(
                mergedPendingSp,
                currentState.PendingPartySpOperation);

            foreach (var effect in currentState.PartyEffects ?? Enumerable.Empty<PartyEffect>())
            {
                if (effect == null)
                    continue;

                string effectKey = PartyEffectSemantics.BuildSemanticKey(effect);
                var existingEffect = merged.PartyEffects.FirstOrDefault(e => e != null && PartyEffectSemantics.BuildSemanticKey(e) == effectKey);
                if (existingEffect == null)
                    merged.PartyEffects.Add(effect.Clone());
                else if (existingEffect.IsSynchronizedTemporaryMirror && !effect.IsSynchronizedTemporaryMirror)
                    existingEffect.IsSynchronizedTemporaryMirror = false;
            }

            if (merged.LoopSemantic == LoopSemanticKind.None)
                merged.LoopSemantic = currentState.LoopSemantic;
            else if (currentState.LoopSemantic != LoopSemanticKind.None)
                merged.LoopSemantic = currentState.LoopSemantic;

            if (currentState.IsInLoop)
                merged.IsInLoop = true;

            if (currentState.LoopStartAddress != 0 &&
                (merged.LoopStartAddress == 0 || currentState.LoopStartAddress < merged.LoopStartAddress))
            {
                merged.LoopStartAddress = currentState.LoopStartAddress;
            }

            if (currentState.LoopEndAddress > merged.LoopEndAddress)
                merged.LoopEndAddress = currentState.LoopEndAddress;

            if (currentState.LoopIterationCount > merged.LoopIterationCount)
                merged.LoopIterationCount = currentState.LoopIterationCount;

            if (currentState.LoopIteration > merged.LoopIteration)
                merged.LoopIteration = currentState.LoopIteration;

            merged.IsIndeterminateLoop = merged.IsIndeterminateLoop || currentState.IsIndeterminateLoop;
            merged.TerminatedByRepeatedBackEdge = merged.TerminatedByRepeatedBackEdge || currentState.TerminatedByRepeatedBackEdge;
            merged.TerminatedByPromptLoopBackEdge = merged.TerminatedByPromptLoopBackEdge || currentState.TerminatedByPromptLoopBackEdge;
            merged.TerminatedByTerminalRet = merged.TerminatedByTerminalRet || currentState.TerminatedByTerminalRet;
            merged.UsesInitialCoordinates = merged.UsesInitialCoordinates || inheritedState.UsesInitialCoordinates || currentState.UsesInitialCoordinates;
            merged.UsesStaticMapData = merged.UsesStaticMapData || inheritedState.UsesStaticMapData || currentState.UsesStaticMapData;
            MergeStaticMapDataReads(merged.StaticMapDataReads, currentState.StaticMapDataReads);
            AppendBranchChoices(merged.InlineBranchChoices, currentState.InlineBranchChoices);
            merged.MemoryReadAddresses.UnionWith(currentState.MemoryReadAddresses ?? Enumerable.Empty<ushort>());
            merged.MemoryWrittenAddresses.UnionWith(currentState.MemoryWrittenAddresses ?? Enumerable.Empty<ushort>());
            merged.AdjustedMemoryAddresses.UnionWith(currentState.AdjustedMemoryAddresses ?? Enumerable.Empty<ushort>());

            var currentReadBeforeWrite = new HashSet<ushort>(
                currentState.MemoryReadBeforeWriteAddresses ?? Enumerable.Empty<ushort>());
            currentReadBeforeWrite.ExceptWith(inheritedState.MemoryWrittenAddresses ?? Enumerable.Empty<ushort>());
            merged.MemoryReadBeforeWriteAddresses.UnionWith(currentReadBeforeWrite);
            foreach (var kvp in currentState.PersistentMemoryFirstAccessKinds ?? Enumerable.Empty<KeyValuePair<ushort, PersistentMemoryFirstAccessKind>>())
            {
                if (!merged.PersistentMemoryFirstAccessKinds.ContainsKey(kvp.Key))
                    merged.PersistentMemoryFirstAccessKinds[kvp.Key] = kvp.Value;
            }

            MergeStateValueConstraints(merged.StateValueConstraints, currentState.StateValueConstraints);
            merged.LocallyMaterializedStateValueConstraintAddresses.UnionWith(
                currentState.LocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>());

            if (currentState.ExitEmulatedMemory8 != null && currentState.ExitEmulatedMemory8.Count > 0)
                merged.ExitEmulatedMemory8 = new Dictionary<ushort, byte>(currentState.ExitEmulatedMemory8);
            if (currentState.ExitEmulatedMemory8Ranges != null && currentState.ExitEmulatedMemory8Ranges.Count > 0)
                merged.ExitEmulatedMemory8Ranges = CloneRangeDictionary(currentState.ExitEmulatedMemory8Ranges);
            if (currentState.ExitEmulatedMemory8RangeDistributions != null && currentState.ExitEmulatedMemory8RangeDistributions.Count > 0)
                merged.ExitEmulatedMemory8RangeDistributions = new Dictionary<ushort, RegisterValueDistribution>(currentState.ExitEmulatedMemory8RangeDistributions);

            return merged;
        }

        private PathAnalysisResult RebaseSpecialEventOrders(PathAnalysisResult source, int baseExecutionOrder)
        {
            var rebased = ClonePathAnalysisResult(source);
            if (rebased == null || baseExecutionOrder <= 0)
                return rebased;

            if (rebased.RandomEncounterExecutionOrder > 0)
                rebased.RandomEncounterExecutionOrder += baseExecutionOrder;

            if (rebased.PendingPartyHpOperation != null &&
                rebased.PendingPartyHpOperation.ExecutionOrder > 0)
            {
                rebased.PendingPartyHpOperation.ExecutionOrder += baseExecutionOrder;
            }

            if (rebased.PendingPartySpOperation != null &&
                rebased.PendingPartySpOperation.ExecutionOrder > 0)
            {
                rebased.PendingPartySpOperation.ExecutionOrder += baseExecutionOrder;
            }

            foreach (var pending in rebased.CompletedPartyStatOperations ?? Enumerable.Empty<PendingPartyStatOperation>())
            {
                if (pending != null && pending.ExecutionOrder > 0)
                    pending.ExecutionOrder += baseExecutionOrder;
            }

            foreach (var effect in rebased.PartyEffects ?? Enumerable.Empty<PartyEffect>())
            {
                if (effect != null && effect.ExecutionOrder > 0)
                    effect.ExecutionOrder += baseExecutionOrder;
            }

            if (rebased.NextSpecialEventOrder > 0)
                rebased.NextSpecialEventOrder += baseExecutionOrder;

            return rebased;
        }

        private PendingPartyStatOperation MergePendingPartyStatOperation(
            PendingPartyStatOperation inheritedPending,
            PendingPartyStatOperation currentPending)
        {
            if (inheritedPending == null)
                return currentPending?.Clone();

            if (currentPending == null)
                return inheritedPending.Clone();

            if (!MatchesPendingPartyTarget(inheritedPending.Member, currentPending.Member))
                return currentPending.Clone();

            var merged = inheritedPending.Clone();
            merged.Field = inheritedPending.Field != PartyFieldKind.Unknown
                ? inheritedPending.Field
                : currentPending.Field;
            merged.Member = MergePartyMemberReference(inheritedPending.Member, currentPending.Member);

            merged.MaleOnly = inheritedPending.MaleOnly || currentPending.MaleOnly;
            merged.FemaleOnly = inheritedPending.FemaleOnly || currentPending.FemaleOnly;
            merged.GuardPredicates = (inheritedPending.GuardPredicates ?? new List<PartyPredicate>())
                .Concat(currentPending.GuardPredicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Select(predicate => predicate.Clone())
                .GroupBy(PartyEffectSemantics.BuildPredicateKey)
                .Select(group => group.First())
                .OrderBy(PartyEffectSemantics.BuildPredicateKey)
                .ToList();
            if ((inheritedPending.MaleOnly && currentPending.FemaleOnly) ||
                (inheritedPending.FemaleOnly && currentPending.MaleOnly))
            {
                merged.MaleOnly = false;
                merged.FemaleOnly = false;
            }

            merged.SawReadHigh = inheritedPending.SawReadHigh || currentPending.SawReadHigh;
            merged.SawReadLow = inheritedPending.SawReadLow || currentPending.SawReadLow;
            merged.SawWriteHigh = inheritedPending.SawWriteHigh || currentPending.SawWriteHigh;
            merged.SawWriteLow = inheritedPending.SawWriteLow || currentPending.SawWriteLow;
            merged.FinalWriteHighByteValue = currentPending.SawWriteHigh
                ? currentPending.FinalWriteHighByteValue
                : inheritedPending.FinalWriteHighByteValue;
            merged.FinalWriteLowByteValue = currentPending.SawWriteLow
                ? currentPending.FinalWriteLowByteValue
                : inheritedPending.FinalWriteLowByteValue;
            merged.SawClc = inheritedPending.SawClc || currentPending.SawClc;
            merged.SawShrHigh = inheritedPending.SawShrHigh || currentPending.SawShrHigh;
            merged.SawRcrLow = inheritedPending.SawRcrLow || currentPending.SawRcrLow;
            merged.LowByteArithmetic = MergePendingPartyByteArithmetic(
                inheritedPending.LowByteArithmetic,
                currentPending.LowByteArithmetic);
            merged.HighByteArithmetic = MergePendingPartyByteArithmetic(
                inheritedPending.HighByteArithmetic,
                currentPending.HighByteArithmetic);

            if (merged.StartAddress == 0 ||
                (currentPending.StartAddress != 0 && currentPending.StartAddress < merged.StartAddress))
            {
                merged.StartAddress = currentPending.StartAddress;
            }

            if (merged.ExecutionOrder <= 0 ||
                (currentPending.ExecutionOrder > 0 && currentPending.ExecutionOrder < merged.ExecutionOrder))
            {
                merged.ExecutionOrder = currentPending.ExecutionOrder;
            }

            return merged;
        }

        private PartyMemberReference MergePartyMemberReference(
            PartyMemberReference inheritedMember,
            PartyMemberReference currentMember)
        {
            if (inheritedMember == null)
                return currentMember?.Clone();

            if (currentMember == null)
                return inheritedMember.Clone();

            var merged = inheritedMember.Clone();

            if (!merged.MemberIndex.HasValue)
                merged.MemberIndex = currentMember.MemberIndex;
            if (!merged.PointerAddress.HasValue)
                merged.PointerAddress = currentMember.PointerAddress;
            if (!merged.PointerTableAddress.HasValue)
                merged.PointerTableAddress = currentMember.PointerTableAddress;
            if (!merged.StructureAddress.HasValue)
                merged.StructureAddress = currentMember.StructureAddress;
            if (string.IsNullOrWhiteSpace(merged.Source))
                merged.Source = currentMember.Source;

            merged.IsPartyLoopMember = merged.IsPartyLoopMember || currentMember.IsPartyLoopMember;
            merged.SelectionKind = MergeSelectionKind(merged.SelectionKind, currentMember.SelectionKind);
            return merged;
        }

        private PartyMemberSelectionKind MergeSelectionKind(
            PartyMemberSelectionKind left,
            PartyMemberSelectionKind right)
        {
            if (left == PartyMemberSelectionKind.Random || right == PartyMemberSelectionKind.Random)
                return PartyMemberSelectionKind.Random;

            if (left == PartyMemberSelectionKind.Dynamic || right == PartyMemberSelectionKind.Dynamic)
                return PartyMemberSelectionKind.Dynamic;

            return PartyMemberSelectionKind.Exact;
        }

        private PendingPartyByteArithmetic MergePendingPartyByteArithmetic(
            PendingPartyByteArithmetic inheritedArithmetic,
            PendingPartyByteArithmetic currentArithmetic)
        {
            if (inheritedArithmetic == null)
                return currentArithmetic?.Clone();

            if (currentArithmetic == null)
                return inheritedArithmetic.Clone();

            int inheritedScore = GetPendingPartyByteArithmeticPrecisionScore(inheritedArithmetic);
            int currentScore = GetPendingPartyByteArithmeticPrecisionScore(currentArithmetic);

            if (currentScore > inheritedScore)
                return currentArithmetic.Clone();

            if (inheritedScore > currentScore)
                return inheritedArithmetic.Clone();

            return inheritedArithmetic.InstructionAddress != 0 &&
                   (currentArithmetic.InstructionAddress == 0 ||
                    inheritedArithmetic.InstructionAddress <= currentArithmetic.InstructionAddress)
                ? inheritedArithmetic.Clone()
                : currentArithmetic.Clone();
        }

        private int GetPendingPartyByteArithmeticPrecisionScore(PendingPartyByteArithmetic arithmetic)
        {
            if (arithmetic == null)
                return -1;

            int score = 0;
            if (arithmetic.Operation != PartyEffectOperation.Unknown)
                score += 4;
            if (arithmetic.EffectiveImmediateValue.HasValue)
                score += 8;
            if (arithmetic.UsesCarryOpcode)
                score += 2;
            if (arithmetic.CarryInKnown)
                score += 1;
            if (arithmetic.InstructionAddress != 0)
                score += 1;

            return score;
        }

        private bool MatchesPendingPartyTarget(PartyMemberReference left, PartyMemberReference right)
        {
            if (left == null || right == null)
                return true;

            if (left.IsPartyLoopMember && right.IsPartyLoopMember)
                return true;

            if (left.MemberIndex.HasValue && right.MemberIndex.HasValue)
                return left.MemberIndex.Value == right.MemberIndex.Value;

            if (left.PointerTableAddress.HasValue && right.PointerTableAddress.HasValue)
                return left.PointerTableAddress.Value == right.PointerTableAddress.Value;

            if (left.PointerAddress.HasValue && right.PointerAddress.HasValue)
                return left.PointerAddress.Value == right.PointerAddress.Value;

            if (left.StructureAddress.HasValue && right.StructureAddress.HasValue)
                return left.StructureAddress.Value == right.StructureAddress.Value;

            return true;
        }

        private PathAnalysisResult ClonePathAnalysisResult(PathAnalysisResult source)
        {
            var clone = new PathAnalysisResult();
            if (source == null)
                return clone;

            clone.RandomEncounterMonsterPowerCap = source.RandomEncounterMonsterPowerCap;
            clone.RandomEncounterMonsterLevelCap = source.RandomEncounterMonsterLevelCap;
            clone.RandomEncounterMonsterBatchCountCap = source.RandomEncounterMonsterBatchCountCap;
            clone.DarkeningLevel = source.DarkeningLevel;
            clone.RandomEncounterChance = source.RandomEncounterChance;
            clone.RandomEncounterRubicon = source.RandomEncounterRubicon;
            clone.CallsRandomEncounter = source.CallsRandomEncounter;
            clone.IsOnlyRandomEncounterJump = source.IsOnlyRandomEncounterJump;
            clone.RandomEncounterInstructionAddress = source.RandomEncounterInstructionAddress;
            clone.RandomEncounterExecutionOrder = source.RandomEncounterExecutionOrder;
            clone.TeleportTargetX = source.TeleportTargetX;
            clone.TeleportTargetY = source.TeleportTargetY;
            clone.TeleportTargetXRange = source.TeleportTargetXRange == null ? null : new ValueRange8(source.TeleportTargetXRange.Min, source.TeleportTargetXRange.Max);
            clone.TeleportTargetYRange = source.TeleportTargetYRange == null ? null : new ValueRange8(source.TeleportTargetYRange.Min, source.TeleportTargetYRange.Max);
            clone.BattleMonsterCount = source.BattleMonsterCount;
            clone.BattleMonsterCountRange = source.BattleMonsterCountRange == null ? null : new ValueRange8(source.BattleMonsterCountRange.Min, source.BattleMonsterCountRange.Max);
            clone.IsBattleMonsterCountIndeterminate = source.IsBattleMonsterCountIndeterminate;
            clone.PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(source.PendingPersistentCounterProgressions);
            clone.PersistentCounterProgressions = source.PersistentCounterProgressions?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<PersistentCounterProgressionInfo>();
            clone.DynamicRandomBoundDependencies = source.DynamicRandomBoundDependencies?
                .Where(info => info != null)
                .Select(info => info.Clone())
                .ToList() ?? new List<DynamicRandomBoundDependencyInfo>();
            clone.HasPartialBattlePattern = source.HasPartialBattlePattern;
            clone.IsInLoop = source.IsInLoop;
            clone.LoopSemantic = source.LoopSemantic;
            clone.LoopStartAddress = source.LoopStartAddress;
            clone.LoopEndAddress = source.LoopEndAddress;
            clone.LoopIteration = source.LoopIteration;
            clone.IsIndeterminateLoop = source.IsIndeterminateLoop;
            clone.LoopIterationCount = source.LoopIterationCount;
            clone.IsTerminated = source.IsTerminated;
            clone.HasSignificantCode = source.HasSignificantCode;
            clone.UsesInitialCoordinates = source.UsesInitialCoordinates;
            clone.UsesStaticMapData = source.UsesStaticMapData;
            clone.StaticMapDataReads = CloneStaticMapDataReads(source.StaticMapDataReads);
            clone.InlineProbabilityNumerator = source.InlineProbabilityNumerator;
            clone.InlineProbabilityDenominator = source.InlineProbabilityDenominator;
            clone.InlineBranchChoices = CloneBranchChoices(source.InlineBranchChoices);

            foreach (var entry in source.BattleMonsterEntries)
                clone.BattleMonsterEntries[entry.Key] = entry.Value;

            foreach (var partial in source.PartialBattles)
            {
                clone.PartialBattles.Add(partial.Clone());
            }

            foreach (var info in source.PartialBattleInfo)
            {
                clone.PartialBattleInfo.Add(new PartialBattleInfo
                {
                    BxIndex = info.BxIndex,
                    DestAddr = info.DestAddr,
                    SrcReg = info.SrcReg,
                    SrcRegValue = info.SrcRegValue,
                    ValueMin = info.ValueMin,
                    ValueMax = info.ValueMax,
                    IsFromTable = info.IsFromTable,
                    SourceTableAddr = info.SourceTableAddr,
                    SourceTableBaseAddr = info.SourceTableBaseAddr,
                    SourceTable = info.SourceTable,
                    OriginalSourceIndex = info.OriginalSourceIndex,
                    SourceIndexProviderAddr = info.SourceIndexProviderAddr,
                    SourceIndexBehavior = info.SourceIndexBehavior,
                    SourceIndexExternallyDerived = info.SourceIndexExternallyDerived
                });
            }

            clone.FoundTexts = new HashSet<string>(source.FoundTexts);
            clone.ContextTexts = new HashSet<string>(source.ContextTexts);
            clone.OrderedTexts = source.OrderedTexts.Select(t => t.Clone()).ToList();
            clone.MemoryReadAddresses = new HashSet<ushort>(source.MemoryReadAddresses ?? Enumerable.Empty<ushort>());
            clone.MemoryWrittenAddresses = new HashSet<ushort>(source.MemoryWrittenAddresses ?? Enumerable.Empty<ushort>());
            clone.AdjustedMemoryAddresses = new HashSet<ushort>(source.AdjustedMemoryAddresses ?? Enumerable.Empty<ushort>());
            clone.MemoryReadBeforeWriteAddresses = new HashSet<ushort>(source.MemoryReadBeforeWriteAddresses ?? Enumerable.Empty<ushort>());
            clone.PersistentMemoryFirstAccessKinds = source.PersistentMemoryFirstAccessKinds == null
                ? new Dictionary<ushort, PersistentMemoryFirstAccessKind>()
                : new Dictionary<ushort, PersistentMemoryFirstAccessKind>(source.PersistentMemoryFirstAccessKinds);
            clone.StateValueConstraints = CloneStateValueConstraints(source.StateValueConstraints);
            clone.LocallyMaterializedStateValueConstraintAddresses =
                new HashSet<ushort>(source.LocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>());
            clone.ExitEmulatedMemory8 = source.ExitEmulatedMemory8 == null
                ? new Dictionary<ushort, byte>()
                : new Dictionary<ushort, byte>(source.ExitEmulatedMemory8);
            clone.ExitEmulatedMemory8Ranges = CloneRangeDictionary(source.ExitEmulatedMemory8Ranges);
            clone.ExitEmulatedMemory8RangeDistributions = source.ExitEmulatedMemory8RangeDistributions == null
                ? new Dictionary<ushort, RegisterValueDistribution>()
                : new Dictionary<ushort, RegisterValueDistribution>(source.ExitEmulatedMemory8RangeDistributions);
            clone.VisitedAddresses = new HashSet<uint>(source.VisitedAddresses);
            clone.FirstLocalTextAddress = source.FirstLocalTextAddress;
            clone.NextSpecialEventOrder = source.NextSpecialEventOrder;
            clone.ExitPendingReturnAddresses = source.ExitPendingReturnAddresses == null
                ? new List<uint>()
                : new List<uint>(source.ExitPendingReturnAddresses);
            clone.ExitCallDepth = source.ExitCallDepth;
            clone.TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge;
            clone.TerminatedByPromptLoopBackEdge = source.TerminatedByPromptLoopBackEdge;
            clone.TerminatedByTerminalRet = source.TerminatedByTerminalRet;
            clone.DisablesCurrentMapEvent = source.DisablesCurrentMapEvent;
            clone.PartyFieldAccesses = source.PartyFieldAccesses?.Select(a => a?.Clone()).Where(a => a != null).ToList() ?? new List<PartyFieldReference>();
            clone.PendingPartyHpOperation = source.PendingPartyHpOperation?.Clone();
            clone.PendingPartySpOperation = source.PendingPartySpOperation?.Clone();
            clone.CompletedPartyStatOperations = source.CompletedPartyStatOperations?.Select(p => p?.Clone()).Where(p => p != null).ToList()
                ?? new List<PendingPartyStatOperation>();
            clone.PartyEffects = source.PartyEffects?.Select(e => e?.Clone()).Where(e => e != null).ToList() ?? new List<PartyEffect>();

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
                    CompareMemoryAddress = alt.CompareMemoryAddress,
                    IsInputChoiceBranch = alt.IsInputChoiceBranch,
                    RegisterState = alt.RegisterState?.Clone(),
                    ProbabilityNumerator = alt.ProbabilityNumerator,
                    ProbabilityDenominator = alt.ProbabilityDenominator,
                    CallDepth = alt.CallDepth,
                    PendingReturnAddresses = alt.PendingReturnAddresses == null
                        ? new List<uint>()
                        : new List<uint>(alt.PendingReturnAddresses),
                    EmulatedMemory8 = alt.EmulatedMemory8 == null
                        ? new Dictionary<ushort, byte>()
                        : new Dictionary<ushort, byte>(alt.EmulatedMemory8),
                    EmulatedMemory8Ranges = CloneRangeDictionary(alt.EmulatedMemory8Ranges),
                    EmulatedMemory8RangeDistributions = alt.EmulatedMemory8RangeDistributions == null
                        ? new Dictionary<ushort, RegisterValueDistribution>()
                        : new Dictionary<ushort, RegisterValueDistribution>(alt.EmulatedMemory8RangeDistributions),
                    EmulatedPartyPointers = alt.EmulatedPartyPointers == null
                        ? new Dictionary<ushort, PartyMemberReference>()
                        : alt.EmulatedPartyPointers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    EmulatedPartyPointerBytes = alt.EmulatedPartyPointerBytes == null
                        ? new Dictionary<ushort, PartyPointerByteReference>()
                        : alt.EmulatedPartyPointerBytes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                    PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(alt.PendingPersistentCounterProgressions),
                    BranchStateValueConstraints = CloneStateValueConstraints(alt.BranchStateValueConstraints),
                    BranchLocallyMaterializedStateValueConstraintAddresses =
                        alt.BranchLocallyMaterializedStateValueConstraintAddresses == null
                            ? new HashSet<ushort>()
                            : new HashSet<ushort>(alt.BranchLocallyMaterializedStateValueConstraintAddresses),
                    BranchPartyCondition = alt.BranchPartyCondition,
                    BranchPartyPredicate = alt.BranchPartyPredicate?.Clone()
                });
            }

            return clone;
        }

        private static Dictionary<ushort, PersistentCounterProgressionInfo> ClonePendingPersistentCounterProgressions(
            Dictionary<ushort, PersistentCounterProgressionInfo> source)
        {
            return source == null
                ? new Dictionary<ushort, PersistentCounterProgressionInfo>()
                : source
                    .Where(kvp => kvp.Value != null)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone());
        }

        private PathVariantInfo CreatePathVariant(int pathId, decimal pathOrderKey, List<TextEntry> pathTexts, bool isLeaf, PathAnalysisResult source, int probabilityNumerator, int probabilityDenominator, List<BranchChoice> branchChoices)
        {
            NormalizeProbabilityForStorage(ref probabilityNumerator, ref probabilityDenominator);

            var normalizedPartyEffects = PartyEffectNormalizer.Normalize(source) ?? new List<PartyEffect>();
            var semanticPartyEffects = normalizedPartyEffects
                .Where(PartyEffectSemantics.IsSemanticOutcomeEffect)
                .Where(effect => !PartyEffectSemantics.IsImplicitHpLossConsequenceOutcome(effect, normalizedPartyEffects))
                .ToList();

            return new PathVariantInfo
            {
                PathId = pathId,
                PathOrderKey = pathOrderKey,
                IsLeaf = isLeaf,
                Texts = pathTexts
                    .OrderBy(t => t.Order)
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList(),
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
                PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(source.PendingPersistentCounterProgressions),
                PersistentCounterProgressions = source.PersistentCounterProgressions?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<PersistentCounterProgressionInfo>(),
                DynamicRandomBoundDependencies = source.DynamicRandomBoundDependencies?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<DynamicRandomBoundDependencyInfo>(),
                BattleMonsters = CloneBattleMonsters(source),
                PartiallyDefinedBattles = ClonePartialBattles(source),
                HasAnyTableLoad = source.PartialBattleInfo.Any(ShouldExposeLoadedValue),
                LoadedValues = CloneLoadedValues(source),
                PartyEffects = semanticPartyEffects,
                ExitEmulatedMemory8 = source.ExitEmulatedMemory8 == null
                    ? new Dictionary<ushort, byte>()
                    : new Dictionary<ushort, byte>(source.ExitEmulatedMemory8),
                AdjustedMemoryAddresses = source.AdjustedMemoryAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.AdjustedMemoryAddresses),
                MemoryReadBeforeWriteAddresses = source.MemoryReadBeforeWriteAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.MemoryReadBeforeWriteAddresses),
                PersistentMemoryFirstAccessKinds = source.PersistentMemoryFirstAccessKinds == null
                    ? new Dictionary<ushort, PersistentMemoryFirstAccessKind>()
                    : new Dictionary<ushort, PersistentMemoryFirstAccessKind>(source.PersistentMemoryFirstAccessKinds),
                StateValueConstraints = CloneStateValueConstraints(source.StateValueConstraints),
                LocallyMaterializedStateValueConstraintAddresses =
                    new HashSet<ushort>(source.LocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>()),
                DisablesCurrentMapEvent = source.DisablesCurrentMapEvent,
                UsesInitialCoordinates = source.UsesInitialCoordinates,
                UsesStaticMapData = source.UsesStaticMapData,
                StaticMapDataReads = CloneStaticMapDataReads(source.StaticMapDataReads),
                ProbabilityNumerator = probabilityNumerator,
                ProbabilityDenominator = probabilityDenominator,
                TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge,
                TerminatedByPromptLoopBackEdge = source.TerminatedByPromptLoopBackEdge,
                TerminatedByTerminalRet = source.TerminatedByTerminalRet,
                BranchChoices = CloneBranchChoices(branchChoices)
            };
        }

        private List<BattleMonster> CloneBattleMonsters(PathAnalysisResult source)
        {
            return source.BattleMonsterEntries
                .Where(entry => entry.Value.val1 != 0 && entry.Value.val2 != 0)
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
                .Select(p => p.Clone())
                .ToList();
        }

        private string BuildPartialBattleKey(PartiallyDefinedBattle battle)
        {
            return battle?.GetIdentityKey() ?? "<NULL_PARTIAL>";
        }

        private List<OvrObject.LoadedValueInfo> CloneLoadedValues(PathAnalysisResult source)
        {
            return source.PartialBattleInfo
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
                .ToList();
        }

        private static bool ShouldExposeLoadedValue(PartialBattleInfo info)
        {
            return info != null && info.IsFromTable;
        }

        private bool HasAnyOutcome(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.RandomEncounterMonsterPowerCap.HasValue || variant.RandomEncounterMonsterLevelCap.HasValue ||
                variant.RandomEncounterMonsterBatchCountCap.HasValue || variant.DarkeningLevel.HasValue ||
                variant.RandomEncounterChance.HasValue || variant.RandomEncounterRubicon.HasValue)
                return true;

            if (variant.CallsRandomEncounter)
                return true;

            if (variant.TeleportTargetX.HasValue || variant.TeleportTargetY.HasValue)
                return true;

            if (variant.BattleMonsterCount.HasValue || variant.BattleMonsterCountRange != null || variant.IsBattleMonsterCountIndeterminate)
                return true;

            if (variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0)
                return true;

            if (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0)
                return true;

            if (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0)
                return true;

            if (variant.HasAnyTableLoad)
                return true;

            if (variant.LoadedValues != null && variant.LoadedValues.Count > 0)
                return true;

            if (variant.PartyEffects != null && variant.PartyEffects.Any(PartyEffectSemantics.IsSemanticOutcomeEffect))
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
            {
                if (variant.TerminatedByPromptLoopBackEdge)
                    return HasAnyOutcome(variant);

                if (HasAnyOutcome(variant))
                    return true;

                return variant.Texts != null && variant.Texts.Count > 0;
            }

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

        private int GetVariantOutcomeDisplayPriority(
            PathVariantInfo variant,
            bool preferPromptOnlyBeforeMixedPartyScan = false)
        {
            if (variant == null)
                return int.MaxValue;

            if (preferPromptOnlyBeforeMixedPartyScan && IsPromptOnlyLeaf(variant))
                return -1;

            if (variant.HasTeleportTarget && (variant.Texts == null || variant.Texts.Count == 0))
                return 0;

            if (IsPureEmptyLeafVariant(variant))
                return 1;

            if (VariantTextsContain(variant, "PULL IT (Y/N)?") ||
                VariantTextsContain(variant, "INCORRECT SETTINGS") ||
                VariantTextsContain(variant, "YOU HAVE MASTERED THE MAGIC SQUARE"))
            {
                if (VariantTextsContain(variant, "INCORRECT SETTINGS"))
                    return 2;

                if (VariantTextsContain(variant, "YOU HAVE MASTERED THE MAGIC SQUARE"))
                    return 4;

                return 3;
            }

            if (VariantTextsContain(variant, "THANK YOU") ||
                VariantTextsContain(variant, "FIELDS DEACTIVATED"))
            {
                return 1;
            }

            if (VariantTextsContain(variant, "TOWN GUARDS: YOU'RE UNDER ARREST!") &&
                variant.CallsRandomEncounter &&
                ((variant.BattleMonsters != null && variant.BattleMonsters.Count > 0) ||
                 (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0)))
            {
                return 2;
            }

            if (VariantTextsContain(variant, "IMPROPER") ||
                VariantTextsContain(variant, "INCORRECT") ||
                VariantTextsContain(variant, "WRONG"))
            {
                return 2;
            }

            return 1;
        }

        private bool VariantTextsContain(PathVariantInfo variant, string text)
        {
            if (variant?.Texts == null || string.IsNullOrWhiteSpace(text))
                return false;

            return variant.Texts.Any(line =>
                line?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
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
                .OrderBy(GetPathOrderKey)
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
                MergeVariantOccurrences(existingExact, result);
                if (ShouldPreferVariant(result, existingExact))
                {
                    MergeVariantOccurrences(result, existingExact);
                    uniqueVariants[exactKey] = result;
                }
            }

            // Дополнительная дедупликация: один и тот же сценарий боя может порождать
            // несколько листовых путей с разной глубиной разворота цикла.
            // Для таких случаев оставляем наиболее информативный вариант.
            var semanticallyUnique = new Dictionary<string, PathVariantInfo>();
            foreach (var variant in uniqueVariants.Values.OrderBy(GetPathOrderKey))
            {
                string semanticKey = BuildSemanticVariantKey(variant);
                if (!semanticallyUnique.TryGetValue(semanticKey, out var existing))
                {
                    semanticallyUnique[semanticKey] = variant;
                    continue;
                }

                MergeVariantOccurrences(existing, variant);
                if (ShouldPreferVariant(variant, existing))
                {
                    MergeVariantOccurrences(variant, existing);
                    semanticallyUnique[semanticKey] = variant;
                }
            }

            // Дополнительная branch-insensitive дедупликация для циклов перебора партии:
            // одинаковые тексты и одинаковые party-effects, различающиеся только историей
            // прохода по итерациям цикла, считаем одним вариантом.
            var branchInsensitiveUnique = new Dictionary<string, PathVariantInfo>();
            foreach (var variant in semanticallyUnique.Values.OrderBy(GetPathOrderKey))
            {
                string key = BuildVariantIdentityKey(variant);
                if (!branchInsensitiveUnique.TryGetValue(key, out var existing))
                {
                    branchInsensitiveUnique[key] = variant;
                    continue;
                }

                MergeVariantOccurrences(existing, variant);
                if (ShouldPreferVariant(variant, existing))
                {
                    MergeVariantOccurrences(variant, existing);
                    branchInsensitiveUnique[key] = variant;
                }
            }

            var collapsedVariants = AddMixedPartyScanSubsetOutcomeVariants(
                CollapseConditionalLoopSubsetOutcomeVariants(
                    CollapseShadowedConditionalLoopSubsetCoverageVariants(
                        CollapsePromptOnlyVariantsShadowedByLoopEffects(
                            CollapseGuardOnlyPartyLoopVariants(
                                CollapseRedundantPartyLoopTextVariants(branchInsensitiveUnique.Values))))))
                .ToList();

            bool preferPromptOnlyBeforeMixedPartyScan = collapsedVariants.Any(IsMixedPartyScanSubsetSummaryVariant);

            var orderedUniqueVariants = collapsedVariants
                .OrderBy(variant => GetVariantOutcomeDisplayPriority(variant, preferPromptOnlyBeforeMixedPartyScan))
                .ThenBy(GetPathOrderKey)
                .ToList();

            for (int i = 0; i < orderedUniqueVariants.Count; i++)
            {
                int key = i == 0 ? 0 : i;
                finalVariants[key] = orderedUniqueVariants[i];
            }

            return finalVariants;
        }

        public PathVariantInfo FindCanonicalVariantForLeaf(
            PathVariantInfo leafVariant,
            IEnumerable<PathVariantInfo> canonicalVariants)
        {
            if (leafVariant == null || canonicalVariants == null)
                return null;

            string exactKey = BuildVariantIdentityKey(leafVariant);
            var canonicalList = canonicalVariants
                .Where(variant => variant != null)
                .ToList();

            var exactMatch = canonicalList.FirstOrDefault(variant =>
                string.Equals(BuildVariantIdentityKey(variant), exactKey, StringComparison.Ordinal));
            if (exactMatch != null)
                return exactMatch;

            string semanticKey = BuildSemanticVariantKey(leafVariant);
            return canonicalList.FirstOrDefault(variant =>
                string.Equals(BuildSemanticVariantKey(variant), semanticKey, StringComparison.Ordinal));
        }

        private IEnumerable<PathVariantInfo> CollapseRedundantPartyLoopTextVariants(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = new List<PathVariantInfo>();

            foreach (var group in variants
                .OrderBy(GetPathOrderKey)
                .GroupBy(BuildPartyLoopTextCollapseKey))
            {
                var groupItems = group.ToList();
                if (!ShouldCollapsePartyLoopTextGroup(groupItems))
                {
                    result.AddRange(groupItems);
                    continue;
                }

                result.Add(SelectPreferredPartyLoopTextVariant(groupItems));
            }

            return result;
        }

        private IEnumerable<PathVariantInfo> CollapseConditionalLoopSubsetOutcomeVariants(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = new List<PathVariantInfo>();

            foreach (var group in variants
                .OrderBy(GetPathOrderKey)
                .GroupBy(BuildConditionalLoopSubsetCollapseKey))
            {
                var groupItems = group.ToList();
                if (!ShouldCollapseConditionalLoopSubsetGroup(groupItems))
                {
                    result.AddRange(groupItems);
                    continue;
                }

                result.Add(MergeConditionalLoopSubsetGroup(groupItems));
            }

            return result;
        }

        private IEnumerable<PathVariantInfo> AddMixedPartyScanSubsetOutcomeVariants(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = variants
                .Where(variant => variant != null)
                .OrderBy(GetPathOrderKey)
                .ToList();

            if (result.Count < 2)
                return result;

            var existingKeys = result
                .Select(BuildVariantIdentityKey)
                .ToHashSet(StringComparer.Ordinal);
            var additions = new List<PathVariantInfo>();

            foreach (var group in result
                .Where(HasConditionalLoopSubsetOutcomeEffects)
                .GroupBy(BuildMixedPartyScanSubsetCombinationKey))
            {
                var groupItems = group
                    .Where(HasConditionalLoopSubsetOutcomeEffects)
                    .OrderBy(GetPathOrderKey)
                    .ToList();

                if (groupItems.Count < 2)
                    continue;

                var conditionalEffects = groupItems
                    .SelectMany(variant => variant.PartyEffects ?? Enumerable.Empty<PartyEffect>())
                    .Where(IsConditionalLoopSubsetOutcomeEffect)
                    .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                    .Select(effectGroup => effectGroup.First().Clone())
                    .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                    .ToList();

                if (!CanBuildMixedPartyScanSubsetOutcome(conditionalEffects))
                    continue;

                var combined = BuildMixedPartyScanSubsetOutcomeVariant(groupItems, conditionalEffects);
                if (combined == null)
                    continue;

                string identityKey = BuildVariantIdentityKey(combined);
                if (!existingKeys.Add(identityKey))
                    continue;

                additions.Add(combined);
            }

            result.AddRange(additions);
            return result;
        }

        private IEnumerable<PathVariantInfo> CollapseShadowedConditionalLoopSubsetCoverageVariants(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = new List<PathVariantInfo>();

            foreach (var group in variants
                .OrderBy(GetPathOrderKey)
                .GroupBy(BuildConditionalLoopSubsetCoverageShadowKey))
            {
                var groupItems = group.ToList();
                if (groupItems.Count < 2 || !groupItems.Any(HasConditionalLoopSubsetOutcomeEffects))
                {
                    result.AddRange(groupItems);
                    continue;
                }

                var keyedItems = groupItems
                    .Select(variant => new
                    {
                        Variant = variant,
                        EffectKeys = BuildConditionalLoopSubsetEffectKeySet(variant),
                        ShadowBranchHistoryKey = BuildConditionalLoopSubsetShadowBranchHistoryKey(variant)
                    })
                    .ToList();

                var survivors = keyedItems
                    .Where(current => !keyedItems.Any(other =>
                        !ReferenceEquals(other, current) &&
                        string.Equals(other.ShadowBranchHistoryKey, current.ShadowBranchHistoryKey, StringComparison.Ordinal) &&
                        StrictlyContainsConditionalLoopSubsetEffects(other.EffectKeys, current.EffectKeys)))
                    .Select(item => item.Variant)
                    .ToList();

                result.AddRange(survivors.Count > 0 ? survivors : groupItems);
            }

            return result;
        }

        private IEnumerable<PathVariantInfo> CollapseGuardOnlyPartyLoopVariants(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = new List<PathVariantInfo>();

            foreach (var group in variants
                .OrderBy(GetPathOrderKey)
                .GroupBy(BuildPartyLoopGuardCollapseKey))
            {
                var groupItems = group.ToList();
                bool hasLoopStateChanging = groupItems.Any(HasLoopDerivedStateChangingPartyEffects);
                bool hasLoopGuardOnly = groupItems.Any(HasOnlyLoopGuardLikePartyEffects);

                if (!hasLoopStateChanging || !hasLoopGuardOnly)
                {
                    result.AddRange(groupItems);
                    continue;
                }

                result.AddRange(groupItems.Where(v => !HasOnlyLoopGuardLikePartyEffects(v)));
            }

            return result;
        }

        private IEnumerable<PathVariantInfo> CollapsePromptOnlyVariantsShadowedByLoopEffects(IEnumerable<PathVariantInfo> variants)
        {
            if (variants == null)
                return Enumerable.Empty<PathVariantInfo>();

            var result = new List<PathVariantInfo>();

            foreach (var group in variants
                .OrderBy(GetPathOrderKey)
                .GroupBy(BuildPartyLoopGuardCollapseKey))
            {
                var groupItems = group.ToList();
                bool hasLoopStateChanging = groupItems.Any(HasLoopDerivedStateChangingPartyEffects);
                bool hasPromptOnly = groupItems.Any(IsPromptOnlyLeaf);

                if (!hasLoopStateChanging || !hasPromptOnly)
                {
                    result.AddRange(groupItems);
                    continue;
                }

                var survivors = groupItems
                    .Where(v => !IsPromptOnlyLeaf(v) ||
                                !IsPromptOnlyVariantShadowedByLoopEffect(v, groupItems))
                    .ToList();

                result.AddRange(survivors.Count > 0 ? survivors : groupItems);
            }

            return result;
        }

        private bool IsPromptOnlyVariantShadowedByLoopEffect(
            PathVariantInfo promptOnlyVariant,
            IEnumerable<PathVariantInfo> groupItems)
        {
            if (!IsPromptOnlyLeaf(promptOnlyVariant) || groupItems == null)
                return false;

            var stateChangingSiblings = groupItems
                .Where(other => !ReferenceEquals(other, promptOnlyVariant) &&
                                HasLoopDerivedStateChangingPartyEffects(other))
                .ToList();

            if (stateChangingSiblings.Count == 0)
                return false;

            if (stateChangingSiblings.Any(HasConditionalLoopSubsetOutcomeEffects))
                return true;

            var promptBranchKeys = BuildPromptShadowBranchChoiceKeySet(promptOnlyVariant);

            return stateChangingSiblings.Any(other =>
                IsBranchHistoryCoveredBy(
                    promptBranchKeys,
                    BuildPromptShadowBranchChoiceKeySet(other)));
        }

        private HashSet<string> BuildPromptShadowBranchChoiceKeySet(PathVariantInfo variant)
        {
            return variant?.BranchChoices?
                .Where(choice => choice != null &&
                                 !choice.HasGuardPredicate &&
                                 !IsLoopLocalBranchChoiceForIdentity(choice))
                .Select(choice => choice.GetIdentityKey())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        }

        private bool IsBranchHistoryCoveredBy(HashSet<string> requiredKeys, HashSet<string> candidateKeys)
        {
            if (requiredKeys == null || requiredKeys.Count == 0)
                return true;

            if (candidateKeys == null || candidateKeys.Count == 0)
                return false;

            return requiredKeys.All(candidateKeys.Contains);
        }

        private bool ShouldCollapsePartyLoopTextGroup(List<PathVariantInfo> variants)
        {
            if (variants == null || variants.Count < 2)
                return false;

            if (variants.Any(v => !HasOnlyTextsAndPartyEffects(v)))
                return false;

            if (variants.Any(v => v.PartyEffects == null || v.PartyEffects.Count == 0))
                return false;

            if (variants.All(v => !v.PartyEffects.Any(e => e != null && PartyEffectSemantics.IsLoopDerived(e))))
                return false;

            bool hasTextVariant = variants.Any(HasText);
            bool hasEmptyVariant = variants.Any(v => !HasText(v));
            if (!hasTextVariant || !hasEmptyVariant)
                return false;

            var nonEmptyTextKeys = variants
                .Where(HasText)
                .Select(v => string.Join("|", v.Texts))
                .Distinct()
                .ToList();

            return nonEmptyTextKeys.Count == 1;
        }

        private PathVariantInfo SelectPreferredPartyLoopTextVariant(List<PathVariantInfo> variants)
        {
            if (variants == null || variants.Count == 0)
                return null;

            var preferred = variants.AsEnumerable();

            if (variants.Any(HasStateChangingPartyEffects))
            {
                var withText = variants.Where(HasText).ToList();
                if (withText.Count > 0)
                    preferred = withText;
            }
            else if (variants.All(HasOnlyGuardLikePartyEffects))
            {
                var withoutText = variants.Where(v => !HasText(v)).ToList();
                if (withoutText.Count > 0)
                    preferred = withoutText;
            }

            var selected = preferred
                .OrderByDescending(GetVariantPrecisionScore)
                .ThenBy(GetPathOrderKey)
                .First();

            foreach (var variant in variants)
            {
                if (!ReferenceEquals(selected, variant))
                    MergeVariantOccurrences(selected, variant);
            }

            return selected;
        }

        private string BuildPartyLoopTextCollapseKey(PathVariantInfo variant)
        {
            if (!HasOnlyTextsAndPartyEffects(variant) ||
                variant?.PartyEffects == null ||
                variant.PartyEffects.Count == 0)
            {
                return $"<NO_PARTY_LOOP_COLLAPSE>|{BuildVariantIdentityKey(variant)}";
            }

            return $"{BuildPartyEffectsKey(variant)}|{BuildNormalizedProbabilityKey(variant)}";
        }

        private bool HasOnlyTextsAndPartyEffects(PathVariantInfo variant)
        {
            if (variant == null)
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
                   (variant.BattleMonsters == null || variant.BattleMonsters.Count == 0) &&
                   (variant.PartiallyDefinedBattles == null || variant.PartiallyDefinedBattles.Count == 0) &&
                   !variant.HasAnyTableLoad &&
                   (variant.LoadedValues == null || variant.LoadedValues.Count == 0);
        }

        private bool HasStateChangingPartyEffects(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Any(e => e != null && PartyEffectSemantics.IsStateChanging(e));
        }

        private bool HasLoopDerivedStateChangingPartyEffects(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Any(e => e != null &&
                                                PartyEffectSemantics.IsStateChanging(e) &&
                                                PartyEffectSemantics.IsLoopDerived(e));
        }

        private bool HasConditionalLoopSubsetOutcomeEffects(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Any(IsConditionalLoopSubsetOutcomeEffect);
        }

        private bool IsMixedPartyScanSubsetSummaryVariant(PathVariantInfo variant)
        {
            var conditionalEffects = variant?.PartyEffects?
                .Where(IsConditionalLoopSubsetOutcomeEffect)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(group => group.First().Clone())
                .ToList();

            return conditionalEffects != null &&
                   conditionalEffects.Count >= 2 &&
                   CanBuildMixedPartyScanSubsetOutcome(conditionalEffects);
        }

        private bool HasOnlyGuardLikePartyEffects(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Count > 0 &&
                   variant.PartyEffects.All(e => e != null && PartyEffectSemantics.IsGuardLike(e));
        }

        private bool HasOnlyLoopGuardLikePartyEffects(PathVariantInfo variant)
        {
            return variant?.PartyEffects != null &&
                   variant.PartyEffects.Count > 0 &&
                   variant.PartyEffects.All(e => e != null && PartyEffectSemantics.IsGuardLike(e)) &&
                   variant.PartyEffects.Any(e => e != null && PartyEffectSemantics.IsLoopDerived(e));
        }

        private bool HasText(PathVariantInfo variant)
        {
            return variant?.Texts != null && variant.Texts.Count > 0;
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

            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.HasAnyTableLoad}|{BuildPersistentCounterProgressionKey(variant)}|{BuildDynamicRandomBoundDependencyKey(variant)}";

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
                    .Select(BuildPartialBattleKey))
                : "<NO_PARTIAL>";

            string partyKey = BuildPartyEffectsKey(variant);
            string probabilityKey = BuildRawProbabilityKey(variant);
            string staticMapKey = BuildStaticMapDataReadKey(variant);

            return $"{textKey}||{statKey}||{battleSkeleton}||{partialKey}||{partyKey}||{staticMapKey}||{probabilityKey}";
        }

        private string BuildRawProbabilityKey(PathVariantInfo variant)
        {
            int numerator = Math.Max(0, variant?.ProbabilityNumerator ?? 1);
            int denominator = Math.Max(1, variant?.ProbabilityDenominator ?? 1);
            return $"{numerator}/{denominator}";
        }

        private string BuildNormalizedProbabilityKey(PathVariantInfo variant)
        {
            int numerator = Math.Max(0, variant?.ProbabilityNumerator ?? 1);
            int denominator = Math.Max(1, variant?.ProbabilityDenominator ?? 1);
            int gcd = GreatestCommonDivisor(numerator, denominator);
            return $"{numerator / gcd}/{denominator / gcd}";
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


        private bool ShouldNormalizeLoopLocalBranchHistoryForIdentity(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (IsPureEmptyLeafVariant(variant))
                return false;

            if (variant.PartyEffects == null || variant.PartyEffects.Count == 0)
                return false;

            return variant.PartyEffects.Any(e =>
            {
                if (e == null)
                    return false;

                string semanticKey = PartyEffectSemantics.BuildSemanticKey(e);
                bool partyWide = semanticKey.Contains($"|{PartyEffectScope.PartySubset}|") ||
                                 semanticKey.Contains($"|{PartyEffectScope.WholeParty}|") ||
                                 semanticKey.Contains($"|{PartyEffectScope.CurrentLoopMember}|");
                bool compareOnly = semanticKey.Contains($"|{PartyEffectOperation.Compare}|");
                return partyWide && !compareOnly;
            });
        }

        private string BuildPartyLoopGuardCollapseKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}|{BuildPersistentCounterProgressionKey(variant)}|{BuildDynamicRandomBoundDependencyKey(variant)}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .Select(m => $"{m.Index}:{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}:{m.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(BuildPartialBattleKey))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName)
                    .Select(v => $"{v.BxIndex}:{v.RegName}:{v.Value:X2}:{v.SourceAddr:X4}:{v.IsFirstTable}:{v.IsSaved}"))
                : "<NO_LOADS>";

            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}||{BuildNormalizedProbabilityKey(variant)}";
        }

        private string BuildPartyEffectsKey(PathVariantInfo variant)
        {
            return BuildPartyEffectsKey(variant, effect => true);
        }

        private string BuildPartyEffectsKey(PathVariantInfo variant, Func<PartyEffect, bool> includePredicate)
        {
            if (variant?.PartyEffects == null || variant.PartyEffects.Count == 0)
                return "<NO_PARTY>";

            return string.Join(";", variant.PartyEffects
                .Where(e => e != null && (includePredicate?.Invoke(e) ?? true))
                .Select(PartyEffectSemantics.BuildSemanticKey)
                .OrderBy(x => x));
        }

        private string BuildPersistentCounterProgressionKey(PathVariantInfo variant)
        {
            if (variant?.PersistentCounterProgressions == null || variant.PersistentCounterProgressions.Count == 0)
                return "<NO_COUNTER_PROGRESSION>";

            return string.Join(";", variant.PersistentCounterProgressions
                .Where(info => info != null)
                .Select(info => info.GetIdentityKey())
                .OrderBy(x => x));
        }

        private string BuildDynamicRandomBoundDependencyKey(PathVariantInfo variant)
        {
            if (variant?.DynamicRandomBoundDependencies == null || variant.DynamicRandomBoundDependencies.Count == 0)
                return "<NO_DYNAMIC_RANDOM_BOUND>";

            return string.Join(";", variant.DynamicRandomBoundDependencies
                .Where(info => info != null)
                .Select(info => info.GetIdentityKey())
                .OrderBy(x => x));
        }

        private bool IsConditionalLoopSubsetOutcomeEffect(PartyEffect effect)
        {
            return effect != null &&
                   PartyEffectSemantics.IsStateChanging(effect) &&
                   PartyEffectSemantics.IsLoopDerived(effect) &&
                   PartyEffectSemantics.GetEffectiveScope(effect) == PartyEffectScope.PartySubset &&
                   (PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None ||
                    PartyEffectSemantics.HasEffectiveGuardPredicates(effect));
        }

        private bool ShouldCollapseConditionalLoopSubsetGroup(List<PathVariantInfo> variants)
        {
            if (variants == null || variants.Count < 2)
                return false;

            if (!variants.Any(HasConditionalLoopSubsetOutcomeEffects))
                return false;

            string distinctFullPartyKeys = string.Join("\n", variants
                .Select(BuildPartyEffectsKey)
                .Distinct()
                .OrderBy(x => x));

            string distinctBasePartyKeys = string.Join("\n", variants
                .Select(BuildPartyEffectsKeyExcludingConditionalLoopSubsetOutcomes)
                .Distinct()
                .OrderBy(x => x));

            if (string.IsNullOrWhiteSpace(distinctFullPartyKeys) ||
                string.IsNullOrWhiteSpace(distinctBasePartyKeys))
            {
                return false;
            }

            return variants.Select(BuildPartyEffectsKey).Distinct().Count() > 1 &&
                   variants.Select(BuildPartyEffectsKeyExcludingConditionalLoopSubsetOutcomes).Distinct().Count() == 1;
        }

        private string BuildConditionalLoopSubsetCollapseKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}|{BuildPersistentCounterProgressionKey(variant)}|{BuildDynamicRandomBoundDependencyKey(variant)}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .Select(m => $"{m.Index}:{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}:{m.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(BuildPartialBattleKey))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName)
                    .Select(v => $"{v.BxIndex}:{v.RegName}:{v.Value:X2}:{v.SourceAddr:X4}:{v.IsFirstTable}:{v.IsSaved}"))
                : "<NO_LOADS>";

            string partyKey = BuildPartyEffectsKeyExcludingConditionalLoopSubsetOutcomes(variant);
            string partitionFamilyKey = BuildConditionalLoopSubsetPartitionFamilyKey(variant);

            string branchKey = BuildBranchHistoryKeyForIdentity(variant);

            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}||{partyKey}||{partitionFamilyKey}||{branchKey}||{BuildRawProbabilityKey(variant)}";
        }

        private string BuildConditionalLoopSubsetCoverageShadowKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}|{BuildPersistentCounterProgressionKey(variant)}|{BuildDynamicRandomBoundDependencyKey(variant)}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .Select(m => $"{m.Index}:{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}:{m.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(BuildPartialBattleKey))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName)
                    .Select(v => $"{v.BxIndex}:{v.RegName}:{v.Value:X2}:{v.SourceAddr:X4}:{v.IsFirstTable}:{v.IsSaved}"))
                : "<NO_LOADS>";

            string partyKey = BuildPartyEffectsKeyExcludingConditionalLoopSubsetOutcomes(variant);

            // Для suppress-only шага нужно сравнивать варианты с одинаковой базовой семантикой
            // даже если у части из них пока нет ни одного conditional subset effect.
            // Иначе "пустой" outcome (без сработавшего subset-эффекта) никогда не увидит
            // более содержательный sibling и не будет отброшен как shadowed.
            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}||{partyKey}||{BuildRawProbabilityKey(variant)}";
        }

        private string BuildPartyEffectsKeyExcludingConditionalLoopSubsetOutcomes(PathVariantInfo variant)
        {
            return BuildPartyEffectsKey(variant, effect => !IsConditionalLoopSubsetOutcomeEffect(effect));
        }

        private string BuildConditionalLoopSubsetShadowBranchHistoryKey(PathVariantInfo variant)
        {
            return BuildBranchHistoryKey(variant, ignorePureEmptyLeaf: false, filterLoopLocalChoices: true);
        }

        private string BuildBranchHistoryKeyForIdentity(PathVariantInfo variant)
        {
            bool ignorePureEmptyLeaf = IsPureEmptyLeafVariant(variant);
            bool filterLoopLocalChoices = ShouldNormalizeLoopLocalBranchHistoryForIdentity(variant);
            return BuildBranchHistoryKey(variant, ignorePureEmptyLeaf, filterLoopLocalChoices);
        }

        private string BuildBranchHistoryKey(PathVariantInfo variant, bool ignorePureEmptyLeaf, bool filterLoopLocalChoices)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            if (ignorePureEmptyLeaf)
                return "<IGNORED_BRANCHES>";

            if (variant.BranchChoices == null || variant.BranchChoices.Count == 0)
                return "<NO_BRANCHES>";

            var relevantChoices = variant.BranchChoices
                .Where(choice => choice != null && (!filterLoopLocalChoices || !IsLoopLocalBranchChoiceForIdentity(choice)))
                .Select(choice => choice.GetIdentityKey())
                .ToList();

            return relevantChoices.Count == 0
                ? "<NO_BRANCHES>"
                : string.Join(";", relevantChoices);
        }

        private string BuildConditionalLoopSubsetPartitionFamilyKey(PathVariantInfo variant)
        {
            if (variant?.PartyEffects == null || variant.PartyEffects.Count == 0)
                return "<NO_PARTITIONS>";

            var families = variant.PartyEffects
                .Where(IsConditionalLoopSubsetOutcomeEffect)
                .Select(BuildConditionalLoopSubsetEffectFamilyKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct()
                .OrderBy(key => key)
                .ToList();

            return families.Count == 0
                ? "<NO_PARTITIONS>"
                : string.Join(";", families);
        }

        private string BuildConditionalLoopSubsetEffectFamilyKey(PartyEffect effect)
        {
            if (effect == null)
                return null;

            return string.Join("|",
                PartyEffectSemantics.GetEffectiveField(effect),
                PartyEffectSemantics.GetEffectiveOperation(effect),
                PartyEffectSemantics.GetEffectiveScope(effect),
                PartyEffectSemantics.IsLoopDerived(effect) ? "Loop" : "Direct");
        }

        private HashSet<string> BuildConditionalLoopSubsetEffectKeySet(PathVariantInfo variant)
        {
            return variant?.PartyEffects?
                .Where(IsConditionalLoopSubsetOutcomeEffect)
                .Select(PartyEffectSemantics.BuildSemanticKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        }

        private bool IsLoopLocalBranchChoiceForIdentity(BranchChoice choice)
        {
            return IsConditionalLoopSubsetLocalBranchChoice(choice) ||
                   IsPartyLoopTraversalBranchChoice(choice);
        }

        private bool IsConditionalLoopSubsetLocalBranchChoice(BranchChoice choice)
        {
            string mnemonic = ExtractBranchMnemonic(choice);
            return string.Equals(mnemonic, "JS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JNS", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPartyLoopTraversalBranchChoice(BranchChoice choice)
        {
            string mnemonic = ExtractBranchMnemonic(choice);
            if (!IsLowByteRegisterName(choice?.CompareRegister) ||
                choice?.CompareMemoryAddress != PartyCountAddress)
                return false;

            return string.Equals(mnemonic, "JB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JNAE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JAE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JNB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mnemonic, "JNC", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowByteRegisterName(string registerName)
        {
            string reg = registerName?.ToUpperInvariant();
            return reg == "AL" || reg == "CL" || reg == "DL" || reg == "BL";
        }

        private string ExtractBranchMnemonic(BranchChoice choice)
        {
            string condition = choice?.Condition?.Trim();
            if (string.IsNullOrWhiteSpace(condition))
                return string.Empty;

            const string linearPrefix = "LINEAR after ";
            if (condition.StartsWith(linearPrefix, StringComparison.OrdinalIgnoreCase))
                condition = condition.Substring(linearPrefix.Length).TrimStart();

            int separatorIndex = condition.IndexOf(' ');
            return separatorIndex >= 0
                ? condition.Substring(0, separatorIndex).Trim()
                : condition;
        }

        private bool StrictlyContainsConditionalLoopSubsetEffects(HashSet<string> left, HashSet<string> right)
        {
            if (left == null || right == null)
                return false;

            if (left.Count <= right.Count)
                return false;

            return right.All(left.Contains);
        }

        private bool CanBuildMixedPartyScanSubsetOutcome(List<PartyEffect> conditionalEffects)
        {
            if (conditionalEffects == null || conditionalEffects.Count < 2)
                return false;

            int distinctPredicatePartitions = conditionalEffects
                .Select(BuildMixedPartyScanEffectPredicatePartitionKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (distinctPredicatePartitions < 2)
                return false;

            return !HasConflictingMixedPartyScanSubsetEffects(conditionalEffects);
        }

        private bool HasConflictingMixedPartyScanSubsetEffects(List<PartyEffect> conditionalEffects)
        {
            if (conditionalEffects == null || conditionalEffects.Count < 2)
                return false;

            return conditionalEffects
                .GroupBy(BuildMixedPartyScanEffectExclusiveSlotKey)
                .Any(group => group
                    .Select(PartyEffectSemantics.BuildSemanticKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count() > 1);
        }

        private string BuildMixedPartyScanEffectPredicatePartitionKey(PartyEffect effect)
        {
            if (effect == null)
                return null;

            return string.Join("|",
                PartyEffectSemantics.GetEffectiveCondition(effect),
                PartyEffectSemantics.BuildGuardPredicatesKey(effect));
        }

        private string BuildMixedPartyScanEffectExclusiveSlotKey(PartyEffect effect)
        {
            if (effect == null)
                return null;

            return string.Join("|",
                effect.Kind,
                PartyEffectSemantics.GetEffectiveField(effect),
                effect.ComparedField,
                PartyEffectSemantics.GetEffectiveOperation(effect),
                PartyEffectSemantics.GetEffectiveScope(effect),
                PartyEffectSemantics.GetEffectiveCondition(effect),
                PartyEffectSemantics.BuildGuardPredicatesKey(effect),
                effect.ComparedMemberIndex?.ToString() ?? "-",
                PartyEffectSemantics.IsLoopDerived(effect) ? "Loop" : "Direct");
        }

        private string BuildMixedPartyScanSubsetCombinationKey(PathVariantInfo variant)
        {
            if (variant == null)
                return "<NULL_VARIANT>";

            if (!HasConditionalLoopSubsetOutcomeEffects(variant))
                return $"<NO_MIXED_PARTY_SCAN>|{BuildVariantIdentityKey(variant)}";

            return string.Join("||",
                BuildConditionalLoopSubsetCoverageShadowKey(variant),
                BuildRawProbabilityKey(variant),
                BuildMixedPartyScanBranchHistoryKey(variant));
        }

        private string BuildMixedPartyScanBranchHistoryKey(PathVariantInfo variant)
        {
            if (variant?.BranchChoices == null || variant.BranchChoices.Count == 0)
                return "<NO_BRANCHES>";

            var relevantChoices = variant.BranchChoices
                .Where(choice => choice != null && !IsMixedPartyScanLocalBranchChoice(variant, choice))
                .Select(choice => choice.GetIdentityKey())
                .ToList();

            return relevantChoices.Count == 0
                ? "<NO_BRANCHES>"
                : string.Join(";", relevantChoices);
        }

        private bool IsMixedPartyScanLocalBranchChoice(PathVariantInfo variant, BranchChoice choice)
        {
            return IsLoopLocalBranchChoiceForIdentity(choice) ||
                   IsPartyLoopGuardBranchChoice(variant, choice);
        }

        private bool IsPartyLoopGuardBranchChoice(PathVariantInfo variant, BranchChoice choice)
        {
            if (choice?.GuardPredicate == null ||
                variant?.PartyEffects == null ||
                variant.PartyEffects.Count == 0)
            {
                return false;
            }

            string choicePredicateKey = BuildLoopNormalizedPredicateKey(choice.GuardPredicate);
            if (string.IsNullOrWhiteSpace(choicePredicateKey))
                return false;

            return variant.PartyEffects
                .Where(IsConditionalLoopSubsetOutcomeEffect)
                .SelectMany(effect => PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                .Select(PartyEffectSemantics.BuildPredicateKey)
                .Any(key => string.Equals(key, choicePredicateKey, StringComparison.Ordinal));
        }

        private string BuildLoopNormalizedPredicateKey(PartyPredicate predicate)
        {
            var normalized = PartyEffectSemantics
                .NormalizeGuardPredicatesForLoopAggregation(new[] { predicate })
                .FirstOrDefault();

            return normalized == null
                ? null
                : PartyEffectSemantics.BuildPredicateKey(normalized);
        }

        private PathVariantInfo BuildMixedPartyScanSubsetOutcomeVariant(
            List<PathVariantInfo> variants,
            List<PartyEffect> conditionalEffects)
        {
            if (variants == null || variants.Count == 0 ||
                conditionalEffects == null || conditionalEffects.Count < 2)
            {
                return null;
            }

            var preferred = variants
                .OrderByDescending(variant =>
                    variant.PartyEffects?.Count(IsConditionalLoopSubsetOutcomeEffect) ?? 0)
                .ThenByDescending(GetVariantPrecisionScore)
                .ThenBy(GetPathOrderKey)
                .FirstOrDefault();

            if (preferred == null)
                return null;

            var combined = CloneVariantInfo(preferred);
            combined.PathId = variants.Min(variant => variant.PathId);
            combined.PathOrderKey = variants.Max(GetPathOrderKey) + 0.0001m;
            combined.BranchChoices = BuildMixedPartyScanSummaryBranchChoices(preferred);
            combined.PartyEffects = variants
                .SelectMany(variant => variant.PartyEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(effect => effect != null && !IsConditionalLoopSubsetOutcomeEffect(effect))
                .Concat(conditionalEffects)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(effectGroup => effectGroup.First().Clone())
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();

            foreach (var effect in combined.PartyEffects.Where(effect =>
                         effect != null &&
                         PartyEffectSemantics.IsLoopDerived(effect) &&
                         PartyEffectSemantics.GetEffectiveScope(effect) != PartyEffectScope.SingleMember))
            {
                effect.ObservedMemberIndex = null;
            }

            foreach (var variant in variants)
                MergeVariantOccurrences(combined, variant);

            return combined;
        }

        private List<BranchChoice> BuildMixedPartyScanSummaryBranchChoices(PathVariantInfo preferred)
        {
            return preferred?.BranchChoices?
                .Where(choice => choice != null && !IsMixedPartyScanLocalBranchChoice(preferred, choice))
                .Select(choice => choice.Clone())
                .ToList() ?? new List<BranchChoice>();
        }

        private PathVariantInfo MergeConditionalLoopSubsetGroup(List<PathVariantInfo> variants)
        {
            var preferred = variants
                .OrderByDescending(v => v.PartyEffects?.Count ?? 0)
                .ThenByDescending(GetVariantPrecisionScore)
                .ThenBy(GetPathOrderKey)
                .First();

            var merged = CloneVariantInfo(preferred);
            merged.PathId = variants.Min(v => v.PathId);
            merged.PathOrderKey = variants.Min(GetPathOrderKey);
            merged.PartyEffects = variants
                .SelectMany(v => v.PartyEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(e => e != null)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(g => g.First().Clone())
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();

            foreach (var effect in merged.PartyEffects.Where(effect =>
                         effect != null &&
                         PartyEffectSemantics.IsLoopDerived(effect) &&
                         PartyEffectSemantics.GetEffectiveScope(effect) != PartyEffectScope.SingleMember))
            {
                effect.ObservedMemberIndex = null;
            }

            foreach (var variant in variants)
            {
                if (!ReferenceEquals(variant, merged))
                    MergeVariantOccurrences(merged, variant);
            }

            return merged;
        }

        private PathVariantInfo CloneVariantInfo(PathVariantInfo source)
        {
            if (source == null)
                return null;

            int probabilityNumerator = source.ProbabilityNumerator;
            int probabilityDenominator = source.ProbabilityDenominator;
            NormalizeProbabilityForStorage(ref probabilityNumerator, ref probabilityDenominator);

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
                PendingPersistentCounterProgressions = ClonePendingPersistentCounterProgressions(source.PendingPersistentCounterProgressions),
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
                AdjustedMemoryAddresses = source.AdjustedMemoryAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.AdjustedMemoryAddresses),
                MemoryReadBeforeWriteAddresses = source.MemoryReadBeforeWriteAddresses == null
                    ? new HashSet<ushort>()
                    : new HashSet<ushort>(source.MemoryReadBeforeWriteAddresses),
                PersistentMemoryFirstAccessKinds = source.PersistentMemoryFirstAccessKinds == null
                    ? new Dictionary<ushort, PersistentMemoryFirstAccessKind>()
                    : new Dictionary<ushort, PersistentMemoryFirstAccessKind>(source.PersistentMemoryFirstAccessKinds),
                StateValueConstraints = CloneStateValueConstraints(source.StateValueConstraints),
                LocallyMaterializedStateValueConstraintAddresses =
                    new HashSet<ushort>(source.LocallyMaterializedStateValueConstraintAddresses ?? Enumerable.Empty<ushort>()),
                DisablesCurrentMapEvent = source.DisablesCurrentMapEvent,
                HasRepeatedEventOccurrenceSensitivity = source.HasRepeatedEventOccurrenceSensitivity,
                SuppressRepeatedEventOccurrenceDescription = source.SuppressRepeatedEventOccurrenceDescription,
                UsesInitialCoordinates = source.UsesInitialCoordinates,
                UsesStaticMapData = source.UsesStaticMapData,
                StaticMapDataReads = CloneStaticMapDataReads(source.StaticMapDataReads),
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
                ProbabilityNumerator = probabilityNumerator,
                ProbabilityDenominator = probabilityDenominator,
                TerminatedByRepeatedBackEdge = source.TerminatedByRepeatedBackEdge,
                TerminatedByPromptLoopBackEdge = source.TerminatedByPromptLoopBackEdge,
                TerminatedByTerminalRet = source.TerminatedByTerminalRet,
                HasBranchSpecificContribution = source.HasBranchSpecificContribution
            };
        }

        private void MergeVariantOccurrences(PathVariantInfo target, PathVariantInfo source)
        {
            if (target == null || source == null)
                return;

            var merged = (target.OccurrenceIndices ?? new List<int>())
                .Concat(source.OccurrenceIndices ?? Enumerable.Empty<int>())
                .Where(index => index > 0)
                .Distinct()
                .OrderBy(index => index)
                .ToList();

            target.OccurrenceIndices = merged;
            target.OccurrenceRanges = MergeOccurrenceRanges(
                target.OccurrenceRanges,
                source.OccurrenceRanges);
            target.HasRepeatedEventOccurrenceSensitivity |= source.HasRepeatedEventOccurrenceSensitivity;
            target.SuppressRepeatedEventOccurrenceDescription |= source.SuppressRepeatedEventOccurrenceDescription;
            target.UsesInitialCoordinates |= source.UsesInitialCoordinates;
            target.UsesStaticMapData |= source.UsesStaticMapData;
            MergeStaticMapDataReads(target.StaticMapDataReads, source.StaticMapDataReads);

            foreach (var kvp in source.PendingPersistentCounterProgressions ?? Enumerable.Empty<KeyValuePair<ushort, PersistentCounterProgressionInfo>>())
            {
                if (kvp.Value != null)
                    target.PendingPersistentCounterProgressions[kvp.Key] = kvp.Value.Clone();
            }

            foreach (var progression in source.PersistentCounterProgressions ?? Enumerable.Empty<PersistentCounterProgressionInfo>())
            {
                if (progression == null)
                    continue;

                string key = progression.GetIdentityKey();
                if (!target.PersistentCounterProgressions.Any(existing => existing != null && existing.GetIdentityKey() == key))
                    target.PersistentCounterProgressions.Add(progression.Clone());
            }

            foreach (var dependency in source.DynamicRandomBoundDependencies ?? Enumerable.Empty<DynamicRandomBoundDependencyInfo>())
            {
                if (dependency == null)
                    continue;

                string key = dependency.GetIdentityKey();
                if (!target.DynamicRandomBoundDependencies.Any(existing => existing != null && existing.GetIdentityKey() == key))
                    target.DynamicRandomBoundDependencies.Add(dependency.Clone());
            }

            if (string.IsNullOrWhiteSpace(target.OccurrenceDescription))
                target.OccurrenceDescription = source.OccurrenceDescription;
        }

        private List<OccurrenceRangeInfo> MergeOccurrenceRanges(
            IEnumerable<OccurrenceRangeInfo> left,
            IEnumerable<OccurrenceRangeInfo> right)
        {
            var ranges = (left ?? Enumerable.Empty<OccurrenceRangeInfo>())
                .Concat(right ?? Enumerable.Empty<OccurrenceRangeInfo>())
                .Where(range => range != null && range.Start > 0)
                .Select(range => range.Clone())
                .OrderBy(range => range.Start)
                .ThenBy(range => range.IsOpenEnded ? int.MaxValue : range.End)
                .ToList();

            if (ranges.Count == 0)
                return new List<OccurrenceRangeInfo>();

            var merged = new List<OccurrenceRangeInfo> { ranges[0] };
            foreach (var range in ranges.Skip(1))
            {
                var current = merged[merged.Count - 1];
                int currentEnd = current.IsOpenEnded ? int.MaxValue : Math.Max(current.Start, current.End);
                int rangeEnd = range.IsOpenEnded ? int.MaxValue : Math.Max(range.Start, range.End);
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

        private Dictionary<ushort, StateValueConstraintInfo> CloneStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> source)
        {
            if (source == null || source.Count == 0)
                return new Dictionary<ushort, StateValueConstraintInfo>();

            return source.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.Clone() ?? new StateValueConstraintInfo());
        }

        private void MergeStateValueConstraints(
            Dictionary<ushort, StateValueConstraintInfo> target,
            Dictionary<ushort, StateValueConstraintInfo> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            foreach (var kvp in source)
            {
                if (!target.TryGetValue(kvp.Key, out var existing))
                {
                    target[kvp.Key] = kvp.Value?.Clone() ?? new StateValueConstraintInfo();
                    continue;
                }

                existing.MergeFrom(kvp.Value);
            }
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

            if (variant.PersistentCounterProgressions != null)
                score += variant.PersistentCounterProgressions.Count * 25;

            if (variant.DynamicRandomBoundDependencies != null)
                score += variant.DynamicRandomBoundDependencies.Count * 20;

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

        private bool ShouldPreferVariant(PathVariantInfo candidate, PathVariantInfo existing)
        {
            if (candidate == null)
                return false;

            if (existing == null)
                return true;

            int candidateScore = GetVariantPrecisionScore(candidate);
            int existingScore = GetVariantPrecisionScore(existing);
            if (candidateScore != existingScore)
                return candidateScore > existingScore;

            if (candidate.HasProbabilityInfo != existing.HasProbabilityInfo)
                return candidate.HasProbabilityInfo;

            return false;
        }

        private string BuildVariantIdentityKey(PathVariantInfo variant)
        {
            string textKey = variant.Texts != null && variant.Texts.Count > 0
                ? string.Join("|", variant.Texts)
                : "<NO_TEXT>";

            string statKey = $"{variant.RandomEncounterMonsterPowerCap}|{variant.RandomEncounterMonsterLevelCap}|{variant.RandomEncounterMonsterBatchCountCap}|{variant.DarkeningLevel}|{variant.RandomEncounterChance}|{variant.CallsRandomEncounter}|{variant.RandomEncounterRubicon}|{variant.TeleportTargetX}|{variant.TeleportTargetY}|{variant.TeleportTargetXRange?.Min}-{variant.TeleportTargetXRange?.Max}|{variant.TeleportTargetYRange?.Min}-{variant.TeleportTargetYRange?.Max}|{variant.BattleMonsterCount}|{variant.BattleMonsterCountRange?.Min}-{variant.BattleMonsterCountRange?.Max}|{variant.IsBattleMonsterCountIndeterminate}|{variant.HasAnyTableLoad}|{BuildPersistentCounterProgressionKey(variant)}|{BuildDynamicRandomBoundDependencyKey(variant)}";

            string battleKey = variant.BattleMonsters != null && variant.BattleMonsters.Count > 0
                ? string.Join(";", variant.BattleMonsters
                    .OrderBy(m => m.Index)
                    .Select(m => $"{m.Index}:{m.MonsterIndex1:X2}:{m.MonsterIndex2:X2}:{m.IsIndeterminate}"))
                : "<NO_BATTLE>";

            string partialKey = variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0
                ? string.Join(";", variant.PartiallyDefinedBattles
                    .OrderBy(p => p.BxIndex)
                    .Select(BuildPartialBattleKey))
                : "<NO_PARTIAL>";

            string loadKey = variant.LoadedValues != null && variant.LoadedValues.Count > 0
                ? string.Join(";", variant.LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName)
                    .Select(v => $"{v.BxIndex}:{v.RegName}:{v.Value:X2}:{v.SourceAddr:X4}:{v.IsFirstTable}:{v.IsSaved}"))
                : "<NO_LOADS>";

            string partyKey = BuildPartyEffectsKey(variant);

            string branchKey = BuildBranchHistoryKeyForIdentity(variant);
            string staticMapKey = BuildStaticMapDataReadKey(variant);

            return $"{textKey}||{statKey}||{battleKey}||{partialKey}||{loadKey}||{partyKey}||{branchKey}||{staticMapKey}||{BuildRawProbabilityKey(variant)}";
        }
    }
}
