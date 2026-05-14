﻿
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

namespace MMMapEditor
{
    public sealed class OvrNotesBuildResult
    {
        public Dictionary<Point, string> NotesPerCell { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, List<NoteInlineStyleSpan>> NoteStyleSpansPerCell { get; set; }
            = new Dictionary<Point, List<NoteInlineStyleSpan>>();
        public Dictionary<Point, string> CentralOptions { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, Directions<bool>> MessageStates { get; set; }
            = new Dictionary<Point, Directions<bool>>();

        public int TotalObjects { get; set; }
        public int TableObjects { get; set; }
        public int SpecObjects { get; set; }
    }

    public static class OvrNotesBuilder
    {
        private static readonly Lazy<HashSet<string>> KnownLootItemNames =
            new Lazy<HashSet<string>>(BuildKnownLootItemNames);
        private const string SpoilerAnswerLinePrefix = "[ !!! ВНИМАНИЕ СПОЙЛЕР !!! ] ПРАВИЛЬНЫЙ ОТВЕТ: ";
        private const string RiddleAnswerPrompt = "ANSWER:>";
        private const string WholePartyConditionChangePrefix = "CONDITION персонажа(ей) в партии изменяется на ";
        private const string LegacyWholePartyConditionChangePrefix = "CONDITION всех персонажей в партии изменяется на ";
        private const string CurrentPartyMemberConditionChangePrefix = "CONDITION текущего персонажа партии изменяется на ";
        private const ushort PartyCountAddress = 0x3BC0;
        private const decimal VariantOutcomeOrderStride = 10000000000000000000000000m;
        private const int FlatSemanticVariantKeyBase = int.MinValue / 2;
        private const int FlatSemanticVariantKeyStride = 10000;

        public static OvrNotesBuildResult BuildNotes(
            string filename,
            Dictionary<Point, string> existingCentralOptions,
            Dictionary<Point, string> existingNotes = null,
            Dictionary<Point, Directions<bool>> existingMessageStates = null,
            bool? useHierarchicalView = null,
            IReadOnlyList<OvrObject> preAnalyzedObjects = null)
        {
            var result = new OvrNotesBuildResult
            {
                CentralOptions = existingCentralOptions != null
                    ? new Dictionary<Point, string>(existingCentralOptions)
                    : new Dictionary<Point, string>(),

                NotesPerCell = existingNotes != null
                    ? new Dictionary<Point, string>(existingNotes)
                    : new Dictionary<Point, string>(),

                MessageStates = existingMessageStates != null
                    ? existingMessageStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone() ?? DirectionUtilities.Filled(false))
                    : new Dictionary<Point, Directions<bool>>()
            };

            string fileNameOnly = Path.GetFileName(filename).ToUpper();

            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
                throw new InvalidOperationException($"Конфигурация для файла {fileNameOnly} не найдена.");

            var config = OvrFileConfigs.Configs[fileNameOnly];

            byte defaultRandomEncounterMonsterPowerCap = 0;
            byte defaultRandomEncounterMonsterLevelCap = 0;
            byte defaultRandomEncounterMonsterBatchCountCap = 0;
            byte defaultDarkeningLevel = 0;
            byte defaultRandomEncounterChance = 0;

            try
            {
                byte[] fileData = File.ReadAllBytes(filename);
                if (config.RandomEncounterMonsterPowerCap < fileData.Length)
                    defaultRandomEncounterMonsterPowerCap = fileData[config.RandomEncounterMonsterPowerCap];
                if (config.RandomEncounterMonsterLevelCap < fileData.Length)
                    defaultRandomEncounterMonsterLevelCap = fileData[config.RandomEncounterMonsterLevelCap];
                if (config.RandomEncounterMonsterBatchCountCap < fileData.Length)
                    defaultRandomEncounterMonsterBatchCountCap = fileData[config.RandomEncounterMonsterBatchCountCap];
                if (config.DarkeningLevel < fileData.Length)
                    defaultDarkeningLevel = fileData[config.DarkeningLevel];
                if (config.RandomEncounterChance < fileData.Length)
                    defaultRandomEncounterChance = fileData[config.RandomEncounterChance];
            }
            catch
            {
            }

            bool useHierarchical = useHierarchicalView
                ?? MainForm.GetBooleanSetting("OvrLoadSettings", "Hierarchical", true);

            var allObjects = preAnalyzedObjects?.ToList()
                ?? OvrFileAnalyzer.AnalyzeOvrFile(filename, config, result.CentralOptions);

            result.TotalObjects = allObjects.Count;
            result.TableObjects = allObjects.Count(o => o.IsFromTable);
            result.SpecObjects = allObjects.Count(o => !o.IsFromTable);

            var tableObjectCoords = new HashSet<string>(
                allObjects.Where(obj => obj.IsFromTable).Select(obj => $"{obj.X},{obj.Y}")
            );

            foreach (var obj in allObjects)
            {
                Point pos = new Point(obj.X, obj.Y);
                string coordKey = $"{obj.X},{obj.Y}";

                if (!result.CentralOptions.TryGetValue(pos, out string existingOption))
                    continue;

                if (obj.IsFromTable && !(tableObjectCoords.Contains(coordKey) && existingOption == "Случайная встреча"))
                    continue;

                string existingCellNotes = result.NotesPerCell.TryGetValue(pos, out var notes)
                    ? notes
                    : "";
                var existingInlineStyles = result.NoteStyleSpansPerCell.TryGetValue(pos, out var priorInlineStyles)
                    ? (priorInlineStyles ?? new List<NoteInlineStyleSpan>())
                        .Where(span => span != null && span.Length > 0 && span.Start >= 0)
                        .Select(span => span.Clone())
                        .ToList()
                    : new List<NoteInlineStyleSpan>();
                var rawOverlayTexts = CollectRawOverlayVisibleTexts(obj);

                if (obj.IsFromTable)
                {
                    result.CentralOptions[pos] = "AnyObject";
                }
                else if (!obj.ShouldKeepOriginalCentralOption)
                {
                    result.CentralOptions[pos] = "AnyObjectSpec";
                }

                var directionsWithMessages = obj.GetDirectionsWithMessages();

                var currentMessages = result.MessageStates.TryGetValue(pos, out var prev)
                    ? prev.Clone()
                    : DirectionUtilities.Filled(false);

                DirectionUtilities.MergeMessages(currentMessages, directionsWithMessages);

                result.MessageStates[pos] = currentMessages;

                string specialSpoilerLine = TryBuildSpecialSpoilerLine(
                    filename,
                    fileNameOnly,
                    config,
                    obj);

                string inlineSpecialSpoilerLine = TryBuildInlineSpecialSpoilerLine(
                    filename,
                    fileNameOnly,
                    config,
                    obj);
                string specialPlayerExplanation = TryBuildSpecialPlayerExplanation(
                    fileNameOnly,
                    obj);
                string trailingRepeatedBattleWarningLine = BuildTrailingRepeatedBattleWarningLine(obj);

                string hierarchicalNotes = useHierarchical
                    ? BuildHierarchicalVariantNotes(
                        obj,
                        defaultRandomEncounterMonsterPowerCap,
                        defaultRandomEncounterMonsterLevelCap,
                        defaultRandomEncounterMonsterBatchCountCap,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        specialSpoilerLine,
                        inlineSpecialSpoilerLine)
                    : string.Empty;
                string flatSemanticNotes = !useHierarchical
                    ? BuildFlatSemanticVariantNotes(
                        obj,
                        defaultRandomEncounterMonsterPowerCap,
                        defaultRandomEncounterMonsterLevelCap,
                        defaultRandomEncounterMonsterBatchCountCap,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        specialSpoilerLine,
                        inlineSpecialSpoilerLine)
                    : string.Empty;

                StringBuilder newNotes = new StringBuilder();
                var inlineStyles = new List<NoteInlineStyleSpan>();

                if (!useHierarchical && !string.IsNullOrWhiteSpace(flatSemanticNotes))
                {
                    AppendRenderedText(newNotes, inlineStyles, flatSemanticNotes);
                }
                else if (useHierarchical && !string.IsNullOrWhiteSpace(hierarchicalNotes))
                {
                    AppendRenderedText(newNotes, inlineStyles, hierarchicalNotes);
                }
                else
                {
                    Dictionary<int, List<string>> variantContents = BuildVariantContents(
                        obj,
                        defaultRandomEncounterMonsterPowerCap,
                        defaultRandomEncounterMonsterLevelCap,
                        defaultRandomEncounterMonsterBatchCountCap,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        inlineSpecialSpoilerLine);

                    foreach (var key in variantContents.Keys.ToList())
                        variantContents[key] = NumberLootBlockIfNeeded(variantContents[key]);

                    bool hasExplicitPathVariants = obj.PathVariants != null && obj.PathVariants.Count > 0;
                    variantContents = hasExplicitPathVariants
                        ? DeduplicateDisplayedVariantContents(obj, variantContents)
                        : DeduplicateVariantContents(variantContents);

                    AppendLineToFirstMeaningfulVariant(variantContents, specialSpoilerLine);

                    if (variantContents.Count == 0)
                    {
                        AppendExistingRenderedText(newNotes, inlineStyles, existingCellNotes, existingInlineStyles);
                    }
                    else if (TryBuildCollapsedCappedRandomPartialBattleNote(obj, variantContents, out string collapsedVariantNotes))
                    {
                        AppendRenderedText(newNotes, inlineStyles, collapsedVariantNotes);
                    }
                    else if (variantContents.Count == 1)
                    {
                        var singleVariant = variantContents.First().Value;
                        foreach (var line in singleVariant)
                            AppendRenderedText(newNotes, inlineStyles, line + "\n");
                    }
                    else
                    {
                        AppendRenderedText(newNotes, inlineStyles, "Эта ячейка содержит различные варианты текста:\n");

                        var sortedVariants = variantContents.OrderBy(v => v.Key).ToList();
                        for (int i = 0; i < sortedVariants.Count; i++)
                        {
                            var variant = sortedVariants[i];
                            string variantHeader = BuildVariantHeader(obj, variant.Key, i + 1);
                            bool headerContainsProbability = VariantHeaderContainsProbability(variantHeader);
                            AppendRenderedText(newNotes, inlineStyles, $"{variantHeader}:\n");

                            foreach (var line in variant.Value)
                                AppendRenderedText(newNotes, inlineStyles, FormatVariantLine(line, headerContainsProbability) + "\n");

                            if (i < sortedVariants.Count - 1)
                                AppendRenderedText(newNotes, inlineStyles, "\n");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(specialPlayerExplanation))
                {
                    if (newNotes.Length > 0)
                        AppendRenderedText(newNotes, inlineStyles, "\n\n");

                    AppendRenderedText(newNotes, inlineStyles, specialPlayerExplanation);
                }

                if (!string.IsNullOrWhiteSpace(trailingRepeatedBattleWarningLine))
                {
                    if (newNotes.Length > 0)
                        AppendRenderedText(newNotes, inlineStyles, "\n");

                    AppendRenderedText(newNotes, inlineStyles, trailingRepeatedBattleWarningLine);
                }

                string finalNoteText = newNotes.Length > 0
                    ? newNotes.ToString().TrimEnd('\r', '\n')
                    : existingCellNotes;

                string normalizedFinalNoteText = NormalizeNoteLineEndings(finalNoteText);
                NormalizeInlineStyleSpansForLineEndings(finalNoteText, normalizedFinalNoteText, inlineStyles);
                finalNoteText = normalizedFinalNoteText;

                AppendRawOverlayTextSpans(finalNoteText, inlineStyles, rawOverlayTexts);

                result.NotesPerCell[pos] = finalNoteText;
                result.NoteStyleSpansPerCell[pos] = inlineStyles
                    .Where(span => span != null && span.Length > 0 && span.Start >= 0)
                    .Select(span => span.Clone())
                    .ToList();
            }

            return result;
        }

        private static void AppendExistingRenderedText(
            StringBuilder target,
            List<NoteInlineStyleSpan> inlineStyles,
            string plainText,
            List<NoteInlineStyleSpan> existingStyles)
        {
            if (target == null || string.IsNullOrEmpty(plainText))
                return;

            int startIndex = target.Length;
            target.Append(plainText);

            if (inlineStyles == null || existingStyles == null || existingStyles.Count == 0)
                return;

            foreach (var span in existingStyles)
            {
                if (span == null || span.Length <= 0 || span.Start < 0)
                    continue;

                inlineStyles.Add(new NoteInlineStyleSpan
                {
                    Start = startIndex + span.Start,
                    Length = span.Length,
                    Kind = span.Kind
                });
            }
        }

        private static void AppendRenderedText(
            StringBuilder target,
            List<NoteInlineStyleSpan> inlineStyles,
            string rawText)
        {
            if (target == null || string.IsNullOrEmpty(rawText))
                return;

            var rendered = InlineNoteStyleCodec.RenderTextWithStyles(rawText);
            int startIndex = target.Length;
            target.Append(rendered.Text);

            if (inlineStyles == null || rendered.Styles == null || rendered.Styles.Count == 0)
                return;

            foreach (var span in rendered.Styles)
            {
                if (span == null || span.Length <= 0)
                    continue;

                inlineStyles.Add(new NoteInlineStyleSpan
                {
                    Start = startIndex + span.Start,
                    Length = span.Length,
                    Kind = span.Kind
                });
            }
        }

        private static List<string> CollectRawOverlayVisibleTexts(OvrObject obj)
        {
            var result = new List<string>();
            if (obj == null)
                return result;

            foreach (string rawText in EnumerateRawTextEntries(obj))
            {
                if (!TryExtractRawOverlayVisibleText(rawText, out string visibleText))
                    continue;

                AddRawOverlayVisibleText(result, visibleText);
                foreach (string line in Regex.Split(visibleText, @"\r\n|\r|\n"))
                    AddRawOverlayVisibleText(result, line);
            }

            return result;
        }

        private static void AddRawOverlayVisibleText(List<string> result, string visibleText)
        {
            if (result == null || string.IsNullOrWhiteSpace(visibleText))
                return;

            string normalized = visibleText.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (!result.Contains(normalized, StringComparer.Ordinal))
                result.Add(normalized);
        }

        private static string NormalizeNoteLineEndings(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static void NormalizeInlineStyleSpansForLineEndings(
            string sourceText,
            string normalizedText,
            List<NoteInlineStyleSpan> inlineStyles)
        {
            if (string.IsNullOrEmpty(sourceText) ||
                sourceText == normalizedText ||
                inlineStyles == null ||
                inlineStyles.Count == 0)
            {
                return;
            }

            int[] indexMap = BuildLineEndingNormalizationIndexMap(sourceText);
            foreach (var span in inlineStyles)
            {
                if (span == null || span.Length <= 0 || span.Start < 0 || span.Start >= sourceText.Length)
                    continue;

                int sourceStart = Math.Min(span.Start, sourceText.Length);
                int sourceEnd = Math.Min(span.Start + span.Length, sourceText.Length);
                int normalizedStart = indexMap[sourceStart];
                int normalizedEnd = indexMap[sourceEnd];

                span.Start = normalizedStart;
                span.Length = Math.Max(0, normalizedEnd - normalizedStart);
            }
        }

        private static int[] BuildLineEndingNormalizationIndexMap(string sourceText)
        {
            var indexMap = new int[sourceText.Length + 1];
            int sourceIndex = 0;
            int normalizedIndex = 0;

            while (sourceIndex < sourceText.Length)
            {
                indexMap[sourceIndex] = normalizedIndex;

                if (sourceText[sourceIndex] == '\r')
                {
                    if (sourceIndex + 1 < sourceText.Length && sourceText[sourceIndex + 1] == '\n')
                    {
                        indexMap[sourceIndex + 1] = normalizedIndex;
                        sourceIndex += 2;
                    }
                    else
                    {
                        sourceIndex++;
                    }

                    normalizedIndex++;
                }
                else
                {
                    sourceIndex++;
                    normalizedIndex++;
                }
            }

            indexMap[sourceText.Length] = normalizedIndex;
            return indexMap;
        }

        private static IEnumerable<string> EnumerateRawTextEntries(OvrObject obj)
        {
            if (obj == null)
                yield break;

            foreach (var text in obj.PathTextsOrdered?
                .OrderBy(kvp => kvp.Key)
                .SelectMany(kvp => kvp.Value ?? new List<string>()) ?? Enumerable.Empty<string>())
            {
                yield return text;
            }

            foreach (var text in obj.PathTexts?
                .OrderBy(kvp => kvp.Key)
                .SelectMany(kvp => kvp.Value ?? new HashSet<string>()) ?? Enumerable.Empty<string>())
            {
                yield return text;
            }

            foreach (var text in obj.PathVariants?
                .OrderBy(kvp => kvp.Key)
                .SelectMany(kvp => kvp.Value?.Texts ?? new List<string>()) ?? Enumerable.Empty<string>())
            {
                yield return text;
            }
        }

        private static bool TryExtractRawOverlayVisibleText(string rawText, out string visibleText)
        {
            visibleText = string.Empty;

            if (!IsRawOverlayTextEntry(rawText))
                return false;

            string decodedText = ExtractNoteText(rawText);
            if (string.IsNullOrEmpty(decodedText))
                return false;

            visibleText = InlineNoteStyleCodec.RenderTextWithStyles(decodedText).Text;
            return !string.IsNullOrEmpty(visibleText);
        }

        private static void AppendRawOverlayTextSpans(
            string noteText,
            List<NoteInlineStyleSpan> inlineStyles,
            List<string> rawOverlayTexts)
        {
            if (string.IsNullOrEmpty(noteText) ||
                inlineStyles == null ||
                rawOverlayTexts == null ||
                rawOverlayTexts.Count == 0)
            {
                return;
            }

            var seenSpans = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawOverlayText in rawOverlayTexts
                .Where(text => !string.IsNullOrEmpty(text))
                .OrderByDescending(text => text.Length))
            {
                int searchStart = 0;
                while (searchStart < noteText.Length)
                {
                    int matchIndex = noteText.IndexOf(rawOverlayText, searchStart, StringComparison.Ordinal);
                    if (matchIndex < 0)
                        break;

                    string key = $"{matchIndex}:{rawOverlayText.Length}";
                    if (seenSpans.Add(key))
                    {
                        inlineStyles.Add(new NoteInlineStyleSpan
                        {
                            Start = matchIndex,
                            Length = rawOverlayText.Length,
                            Kind = NoteInlineStyleKind.RawOverlayText
                        });
                    }

                    searchStart = matchIndex + rawOverlayText.Length;
                }
            }
        }

        private static Dictionary<int, List<string>> BuildVariantContents(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            if (obj.PathVariants != null && obj.PathVariants.Count > 0)
            {
                return BuildVariantContentsFromPathVariants(
                    obj,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);
            }

            return BuildVariantContentsFromObjectTexts(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromPathVariants(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var variantContents = BuildRawVariantContentsFromPathVariants(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            if (CanBuildCollapsedCappedRandomPartialBattleNote(obj, variantContents))
                return variantContents;

            var semanticTreeContents = BuildFlatSemanticTreeVariantContents(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine,
                requirePartyScan: false);
            if (semanticTreeContents != null)
                return semanticTreeContents;

            return variantContents;
        }

        private static Dictionary<int, List<string>> BuildRawVariantContentsFromPathVariants(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var variantContents = new Dictionary<int, List<string>>();

            foreach (var kvp in obj.PathVariants.OrderBy(p => p.Key))
            {
                var variant = kvp.Value;
                if (variant == null)
                    continue;

                int variantNumber = kvp.Key;
                OvrObject variantObject = variant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte);

                var lines = BuildVariantLines(
                    variantObject,
                    variant.Texts,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);

                if (lines.Count == 0)
                    lines.Add("Ничего не происходит (не выполнены условия для наступления ни одного варианта)");

                variantContents[variantNumber] = lines;
            }

            return variantContents;
        }

        private static Dictionary<int, List<string>> BuildFlatSemanticTreeVariantContents(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            bool requirePartyScan)
        {
            var items = BuildVariantRenderItems(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            if (items.Count <= 1)
                return null;

            items = ApplySingleMemberPartyScanDisplayModel(items);
            if (items.Count <= 1)
                return null;

            items = DeduplicateDisplayedVariantItems(items);
            items = ApplySingleMemberPartyScanDisplayModel(items);
            if (items.Count <= 1)
                return null;

            var sourceItems = items
                .Select(CloneVariantRenderItemForSourceMatch)
                .Where(item => item != null)
                .ToList();
            var sourceItemsByKey = BuildSourceVariantItemMap(obj, sourceItems);

            var groups = BuildTopLevelVariantGroups(obj, items);
            if (groups.Count == 0)
                return null;

            bool hasPromptBranching = groups.Any(group =>
                TreeContainsPromptBranching(group?.TreeRoot));
            bool hasMeaningfulChoiceHierarchy = items.Any(item =>
                GetRelevantBranchChoices(item?.Variant).Any());
            bool hasCommonPrefixHierarchy = groups.Any(group =>
                (group?.Items?.Count ?? 0) > 1 &&
                (group.TreeRoot?.CommonLines?.Any(line => !string.IsNullOrWhiteSpace(line)) ?? false));

            bool containsPartyScan = false;
            var flatVariants = new List<FlatVariantRenderItem>();
            foreach (var group in groups)
            {
                var lines = BuildFlatVariantLinesFromTree(group.TreeRoot, out bool groupContainsPartyScan);
                containsPartyScan |= groupContainsPartyScan;
                flatVariants.AddRange(lines);
            }

            if (requirePartyScan && !containsPartyScan)
                return null;

            if (!requirePartyScan &&
                !containsPartyScan &&
                !hasPromptBranching &&
                !hasMeaningfulChoiceHierarchy &&
                !hasCommonPrefixHierarchy)
            {
                return null;
            }

            if (flatVariants.Count == 0)
                return null;

            var mappedFlatVariants = new List<(FlatVariantRenderItem Variant, int OriginalIndex, int? SourceVariantKey)>();
            var usedSourceVariantKeys = new HashSet<int>();
            for (int i = 0; i < flatVariants.Count; i++)
            {
                int? sourceVariantKey = TryResolveFlatVariantSourceKey(
                    obj,
                    sourceItems,
                    flatVariants[i],
                    usedSourceVariantKeys,
                    out int resolvedVariantKey)
                    ? resolvedVariantKey
                    : null;

                if (sourceVariantKey.HasValue)
                    usedSourceVariantKeys.Add(sourceVariantKey.Value);

                mappedFlatVariants.Add((flatVariants[i], i, sourceVariantKey));
            }

            if (containsPartyScan)
            {
                foreach (var sourceItem in sourceItems)
                {
                    if (!TryFindPathVariantKey(obj, sourceItem?.Variant, out int sourceKey) ||
                        usedSourceVariantKeys.Contains(sourceKey) ||
                        !IsSingleMemberPartyScanPromptOnlySourceItem(sourceItem))
                    {
                        continue;
                    }

                    mappedFlatVariants.Add((
                        new FlatVariantRenderItem
                        {
                            Variant = sourceItem.Variant,
                            Lines = sourceItem.Lines?.ToList() ?? new List<string>()
                        },
                        -1,
                        sourceKey));
                    usedSourceVariantKeys.Add(sourceKey);
                }
            }

            var orderedFlatVariants = mappedFlatVariants
                .OrderBy(item => item.SourceVariantKey ?? int.MaxValue)
                .ThenBy(item => item.OriginalIndex)
                .ToList();

            var result = new Dictionary<int, List<string>>();
            int displayIndex = 0;
            foreach (var flatVariant in orderedFlatVariants)
            {
                var normalizedLines = (flatVariant.Variant?.Lines ?? new List<string>())
                    .Where(line => line != null)
                    .ToList();
                if (flatVariant.SourceVariantKey.HasValue &&
                    sourceItemsByKey.TryGetValue(flatVariant.SourceVariantKey.Value, out var sourceItem))
                {
                    normalizedLines = RestoreSourceWhitespaceOnlyLines(normalizedLines, sourceItem.Lines);
                }

                if (!normalizedLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                    normalizedLines.Add("Ничего не происходит");

                int key = BuildFlatSemanticVariantKey(displayIndex++, flatVariant.SourceVariantKey);
                result[key] = normalizedLines;
            }

            return result;
        }

        private static bool IsSingleMemberPartyScanPromptOnlySourceItem(VariantRenderItem item)
        {
            var variant = item?.Variant;
            if (variant == null ||
                HasPartyScanAnswerText(item) ||
                !HasInputChoiceBranch(variant) ||
                HasNonTextOutcome(variant))
            {
                return false;
            }

            return (item.Lines ?? new List<string>())
                .Any(IsPromptLine);
        }

        private static bool HasInputChoiceBranch(PathVariantInfo variant)
        {
            return variant?.BranchChoices?.Any(choice =>
                choice != null &&
                (HasExplicitInputChoiceHint(choice.Label) ||
                 string.Equals(choice.CompareRegister, "AL", StringComparison.OrdinalIgnoreCase) &&
                 choice.CompareValue.HasValue)) == true;
        }

        private static bool HasNonTextOutcome(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            return variant.RandomEncounterMonsterPowerCap.HasValue ||
                   variant.RandomEncounterMonsterLevelCap.HasValue ||
                   variant.RandomEncounterMonsterBatchCountCap.HasValue ||
                   variant.DarkeningLevel.HasValue ||
                   variant.RandomEncounterChance.HasValue ||
                   variant.RandomEncounterRubicon.HasValue ||
                   variant.BattleMonsterStrengthAdjustment != 0 ||
                   variant.CallsRandomEncounter ||
                   variant.HasTeleportTarget ||
                   variant.BattleMonsterCount.HasValue ||
                   variant.BattleMonsterCountRange != null ||
                   variant.IsBattleMonsterCountIndeterminate ||
                   (variant.PersistentCounterProgressions != null && variant.PersistentCounterProgressions.Count > 0) ||
                   (variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0) ||
                   (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0) ||
                   (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0) ||
                   variant.HasAnyTableLoad ||
                   (variant.LoadedValues != null && variant.LoadedValues.Count > 0) ||
                   (variant.PartyEffects != null && variant.PartyEffects.Count > 0);
        }

        private static Dictionary<int, VariantRenderItem> BuildSourceVariantItemMap(
            OvrObject obj,
            IEnumerable<VariantRenderItem> sourceItems)
        {
            var result = new Dictionary<int, VariantRenderItem>();
            foreach (var item in sourceItems ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (!TryFindPathVariantKey(obj, item?.Variant, out int key) ||
                    result.ContainsKey(key))
                {
                    continue;
                }

                result[key] = item;
            }

            return result;
        }

        private static VariantRenderItem CloneVariantRenderItemForSourceMatch(VariantRenderItem item)
        {
            if (item == null)
                return null;

            return new VariantRenderItem
            {
                Variant = item.Variant,
                Lines = item.Lines?.ToList() ?? new List<string>(),
                NarrativeLines = item.NarrativeLines?.ToList() ?? new List<string>()
            };
        }

        private static List<string> RestoreSourceWhitespaceOnlyLines(
            List<string> displayLines,
            List<string> sourceLines)
        {
            var result = displayLines?.ToList() ?? new List<string>();
            if (sourceLines == null || sourceLines.Count == 0 ||
                !sourceLines.Any(line => line != null && string.IsNullOrWhiteSpace(line)))
            {
                return result;
            }

            for (int i = 0; i < sourceLines.Count;)
            {
                if (sourceLines[i] == null || !string.IsNullOrWhiteSpace(sourceLines[i]))
                {
                    i++;
                    continue;
                }

                int groupStart = i;
                var whitespaceLines = new List<string>();
                while (i < sourceLines.Count &&
                       sourceLines[i] != null &&
                       string.IsNullOrWhiteSpace(sourceLines[i]))
                {
                    whitespaceLines.Add(sourceLines[i]);
                    i++;
                }

                var sourceToDisplay = BuildSourceToDisplayLineMap(sourceLines, result);
                int? previousDisplayIndex = FindMappedMeaningfulSourceLineIndex(
                    sourceLines,
                    sourceToDisplay,
                    groupStart - 1,
                    -1);
                int? nextDisplayIndex = FindMappedMeaningfulSourceLineIndex(
                    sourceLines,
                    sourceToDisplay,
                    i,
                    1);

                int insertIndex;
                int existingWhitespaceCount;
                if (previousDisplayIndex.HasValue &&
                    nextDisplayIndex.HasValue &&
                    previousDisplayIndex.Value < nextDisplayIndex.Value)
                {
                    insertIndex = nextDisplayIndex.Value;
                    existingWhitespaceCount = CountWhitespaceOnlyLines(
                        result,
                        previousDisplayIndex.Value + 1,
                        nextDisplayIndex.Value);
                }
                else if (previousDisplayIndex.HasValue)
                {
                    insertIndex = previousDisplayIndex.Value + 1;
                    existingWhitespaceCount = CountWhitespaceOnlyLines(result, insertIndex, insertIndex + whitespaceLines.Count);
                }
                else if (nextDisplayIndex.HasValue)
                {
                    insertIndex = nextDisplayIndex.Value;
                    existingWhitespaceCount = CountWhitespaceOnlyLines(result, Math.Max(0, insertIndex - whitespaceLines.Count), insertIndex);
                }
                else
                {
                    continue;
                }

                int missingCount = whitespaceLines.Count - existingWhitespaceCount;
                if (missingCount <= 0)
                    continue;

                result.InsertRange(insertIndex, whitespaceLines.Take(missingCount));
            }

            return result;
        }

        private static Dictionary<int, int> BuildSourceToDisplayLineMap(
            List<string> sourceLines,
            List<string> displayLines)
        {
            var result = new Dictionary<int, int>();
            int displaySearchStart = 0;
            for (int sourceIndex = 0; sourceIndex < (sourceLines?.Count ?? 0); sourceIndex++)
            {
                string sourceLine = sourceLines[sourceIndex];
                if (string.IsNullOrWhiteSpace(sourceLine))
                    continue;

                int displayIndex = FindDisplayLineIndex(displayLines, sourceLine, displaySearchStart);
                if (displayIndex < 0)
                    continue;

                result[sourceIndex] = displayIndex;
                displaySearchStart = displayIndex + 1;
            }

            return result;
        }

        private static int? FindMappedMeaningfulSourceLineIndex(
            List<string> sourceLines,
            Dictionary<int, int> sourceToDisplay,
            int startIndex,
            int step)
        {
            for (int index = startIndex; index >= 0 && index < (sourceLines?.Count ?? 0); index += step)
            {
                if (string.IsNullOrWhiteSpace(sourceLines[index]))
                    continue;

                if (sourceToDisplay != null && sourceToDisplay.TryGetValue(index, out int displayIndex))
                    return displayIndex;
            }

            return null;
        }

        private static int FindDisplayLineIndex(List<string> displayLines, string sourceLine, int startIndex)
        {
            string normalizedSource = NormalizeLineForWhitespaceRestore(sourceLine);
            for (int i = Math.Max(0, startIndex); i < (displayLines?.Count ?? 0); i++)
            {
                if (string.Equals(
                    NormalizeLineForWhitespaceRestore(displayLines[i]),
                    normalizedSource,
                    StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountWhitespaceOnlyLines(List<string> lines, int startIndex, int endIndex)
        {
            int count = 0;
            int start = Math.Max(0, startIndex);
            int end = Math.Min(lines?.Count ?? 0, Math.Max(start, endIndex));
            for (int i = start; i < end; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    count++;
            }

            return count;
        }

        private static string NormalizeLineForWhitespaceRestore(string line)
        {
            return line?.TrimEnd() ?? string.Empty;
        }

        private static int BuildFlatSemanticVariantKey(int displayIndex, int? sourceVariantKey)
        {
            int sourceKeyComponent = 0;
            if (sourceVariantKey.HasValue &&
                sourceVariantKey.Value >= 0 &&
                sourceVariantKey.Value < FlatSemanticVariantKeyStride - 1)
            {
                sourceKeyComponent = sourceVariantKey.Value + 1;
            }

            return FlatSemanticVariantKeyBase +
                   Math.Max(0, displayIndex) * FlatSemanticVariantKeyStride +
                   sourceKeyComponent;
        }

        private static bool TryDecodeFlatSemanticVariantKey(int displayKey, out int sourceVariantKey)
        {
            sourceVariantKey = 0;
            if (displayKey < FlatSemanticVariantKeyBase || displayKey >= 0)
                return false;

            int encoded = displayKey - FlatSemanticVariantKeyBase;
            int sourceKeyComponent = encoded % FlatSemanticVariantKeyStride;
            if (sourceKeyComponent <= 0)
                return false;

            sourceVariantKey = sourceKeyComponent - 1;
            return true;
        }

        private static bool TryFindPathVariantKey(
            OvrObject obj,
            PathVariantInfo variant,
            out int variantKey)
        {
            variantKey = 0;
            if (obj?.PathVariants == null || variant == null)
                return false;

            foreach (var kvp in obj.PathVariants.OrderBy(kvp => kvp.Key))
            {
                if (ReferenceEquals(kvp.Value, variant))
                {
                    variantKey = kvp.Key;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveFlatVariantSourceKey(
            OvrObject obj,
            IEnumerable<VariantRenderItem> sourceItems,
            FlatVariantRenderItem flatVariant,
            HashSet<int> usedSourceVariantKeys,
            out int variantKey)
        {
            variantKey = 0;

            if (TryFindPathVariantKey(obj, flatVariant?.Variant, out variantKey))
                return true;

            if (sourceItems == null || flatVariant == null)
                return false;

            string targetKey = BuildFlatVariantLineMatchKey(flatVariant.Lines);
            if (string.IsNullOrEmpty(targetKey))
                return false;

            foreach (var item in sourceItems
                .Where(item => item?.Variant != null)
                .OrderBy(item =>
                    TryFindPathVariantKey(obj, item.Variant, out int key)
                        ? key
                        : int.MaxValue))
            {
                if (!TryFindPathVariantKey(obj, item.Variant, out int candidateKey))
                    continue;

                if (usedSourceVariantKeys != null && usedSourceVariantKeys.Contains(candidateKey))
                    continue;

                string candidateLinesKey = BuildFlatVariantLineMatchKey(item.Lines);
                if (!string.Equals(candidateLinesKey, targetKey, StringComparison.Ordinal))
                    continue;

                variantKey = candidateKey;
                return true;
            }

            string targetMultisetKey = BuildFlatVariantLineMultisetMatchKey(flatVariant.Lines);
            if (string.IsNullOrEmpty(targetMultisetKey))
                return false;

            foreach (var item in sourceItems
                .Where(item => item?.Variant != null)
                .OrderBy(item =>
                    TryFindPathVariantKey(obj, item.Variant, out int key)
                        ? key
                        : int.MaxValue))
            {
                if (!TryFindPathVariantKey(obj, item.Variant, out int candidateKey))
                    continue;

                if (usedSourceVariantKeys != null && usedSourceVariantKeys.Contains(candidateKey))
                    continue;

                string candidateLinesKey = BuildFlatVariantLineMultisetMatchKey(item.Lines);
                if (!string.Equals(candidateLinesKey, targetMultisetKey, StringComparison.Ordinal))
                    continue;

                variantKey = candidateKey;
                return true;
            }

            return false;
        }

        private static string BuildFlatVariantLineMatchKey(IEnumerable<string> lines)
        {
            return string.Join("\n", (lines ?? Enumerable.Empty<string>())
                .Select(line => line?.TrimEnd() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        private static string BuildFlatVariantLineMultisetMatchKey(IEnumerable<string> lines)
        {
            return string.Join("\n", (lines ?? Enumerable.Empty<string>())
                .Select(line => line?.TrimEnd() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .GroupBy(line => line, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Count()}:{group.Key}"));
        }

        private static bool TryResolvePathVariantForDisplayKey(
            OvrObject obj,
            int displayKey,
            out PathVariantInfo variant)
        {
            variant = null;
            if (obj?.PathVariants == null)
                return false;

            if (obj.PathVariants.TryGetValue(displayKey, out variant))
                return true;

            return TryDecodeFlatSemanticVariantKey(displayKey, out int sourceVariantKey) &&
                   obj.PathVariants.TryGetValue(sourceVariantKey, out variant);
        }

        private static bool TreeContainsPromptBranching(VariantTreeNode node)
        {
            if (node == null)
                return false;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(node, renderableChildren.Count);
            int renderableDirectVariantCount = (node.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Count(v => ShouldRenderDirectVariant(
                    v,
                    siblingDirectVariantCount,
                    renderableChildren.Count,
                    suppressEmptyPromptTerminalDirectVariants));

            if (HasPromptOptionLabels(node.CommonLines) &&
                renderableChildren.Count + renderableDirectVariantCount > 1)
            {
                return true;
            }

            return renderableChildren.Any(TreeContainsPromptBranching);
        }

        private static List<FlatVariantRenderItem> BuildFlatVariantLinesFromTree(
            VariantTreeNode node,
            out bool containsPartyScan)
        {
            return BuildFlatVariantLinesFromTree(node, new List<string>(), out containsPartyScan);
        }

        private static List<FlatVariantRenderItem> BuildFlatVariantLinesFromTree(
            VariantTreeNode node,
            List<string> inheritedLines,
            out bool containsPartyScan)
        {
            containsPartyScan = false;
            var result = new List<FlatVariantRenderItem>();
            if (!IsRenderableStructuralNode(node))
                return result;

            var prefix = new List<string>(inheritedLines ?? new List<string>());
            prefix.AddRange(node.CommonLines ?? new List<string>());

            if (TryBuildFlatPartyScanOutcomeLines(node, prefix, out var partyScanLines))
            {
                containsPartyScan = true;
                return partyScanLines;
            }

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(node, renderableChildren.Count);
            var renderableDirectVariants = OrderDirectVariants(node.DirectVariants)
                .Where(v => ShouldRenderDirectVariant(
                    v,
                    siblingDirectVariantCount,
                    renderableChildren.Count,
                    suppressEmptyPromptTerminalDirectVariants))
                .ToList();

            if (renderableChildren.Count == 0 && renderableDirectVariants.Count == 0)
            {
                if (prefix.Any(line => !string.IsNullOrWhiteSpace(line)))
                    result.Add(new FlatVariantRenderItem { Lines = prefix });
                return result;
            }

            var syntheticChoiceLabels = BuildSyntheticChoiceLabels(node, renderableDirectVariants, renderableChildren);
            foreach (var entry in BuildOrderedRenderEntries(
                node,
                renderableChildren,
                renderableDirectVariants,
                syntheticChoiceLabels))
            {
                if (entry.ChildNode != null)
                {
                    var childLines = BuildFlatVariantLinesFromTree(
                        entry.ChildNode,
                        prefix,
                        out bool childContainsPartyScan);
                    containsPartyScan |= childContainsPartyScan;
                    result.AddRange(childLines);
                    continue;
                }

                if (entry.DirectVariant == null)
                    continue;

                var variantLines = new List<string>(prefix);
                variantLines.AddRange(entry.DirectVariant.Lines ?? new List<string>());
                if (variantLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                {
                    result.Add(new FlatVariantRenderItem
                    {
                        Variant = entry.DirectVariant.Variant,
                        Lines = variantLines
                    });
                }
            }

            return result;
        }

        private static bool TryBuildFlatPartyScanOutcomeLines(
            VariantTreeNode node,
            List<string> prefix,
            out List<FlatVariantRenderItem> result)
        {
            result = new List<FlatVariantRenderItem>();

            var scanNode = FindImmediatePartyScanPromptNode(node);
            if (scanNode == null || !PartyScanSiblingsAreAggregateOnly(node, scanNode))
                return false;

            var branches = CollectPartyScanOutcomeBranches(scanNode);
            if (branches.Count < 2)
                return false;

            var summaries = BuildPartyScanOutcomeSummaries(branches);
            if (summaries.Count < 2)
                return false;

            var aggregateLines = BuildPartyScanAggregateLines(node, scanNode, summaries);
            if (TryAttachPartyScanAggregateLinesToOutcome(summaries, aggregateLines))
                aggregateLines = new List<string>();

            result.AddRange(BuildPartyScanNoChoiceSiblingFlatVariants(node, scanNode, prefix));

            foreach (var summary in summaries
                .OrderBy(summary => summary.OrderKey)
                .ThenBy(summary => summary.Label ?? string.Empty, StringComparer.Ordinal))
            {
                var lines = new List<string>(prefix ?? new List<string>());
                lines.AddRange(scanNode.CommonLines ?? new List<string>());
                if (!string.IsNullOrWhiteSpace(summary.Label))
                    lines.Add(summary.Label);
                lines.AddRange(summary.Lines ?? new List<string>());
                result.Add(new FlatVariantRenderItem
                {
                    Variant = summary.Variant,
                    Lines = lines
                });
            }

            if (aggregateLines.Count > 0)
            {
                foreach (var flatVariant in result)
                    flatVariant.Lines.AddRange(aggregateLines);
            }

            return result.Count > 0;
        }

        private static List<FlatVariantRenderItem> BuildPartyScanNoChoiceSiblingFlatVariants(
            VariantTreeNode node,
            VariantTreeNode scanNode,
            List<string> prefix)
        {
            var result = new List<FlatVariantRenderItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in OrderDirectVariants(node?.DirectVariants)
                .Where(ShouldRenderAsNoChoiceVariant))
            {
                AddPartyScanNoChoiceFlatVariant(result, seen, prefix, item, null);
            }

            foreach (var child in node?.Children ?? new List<VariantTreeNode>())
            {
                if (ReferenceEquals(child, scanNode) ||
                    !IsPartyScanAggregateOnlyNode(child))
                {
                    continue;
                }

                var item = FindFirstNoChoiceVariant(child);
                if (item == null)
                    continue;

                var childLines = new List<string>();
                CollectPartyScanNoChoiceSiblingLines(child, childLines);
                AddPartyScanNoChoiceFlatVariant(result, seen, prefix, item, childLines);
            }

            return result;
        }

        private static void AddPartyScanNoChoiceFlatVariant(
            List<FlatVariantRenderItem> result,
            HashSet<string> seen,
            List<string> prefix,
            VariantRenderItem item,
            List<string> extraLines)
        {
            if (result == null || item == null)
                return;

            var lines = new List<string>(prefix ?? new List<string>());
            if (extraLines != null && extraLines.Count > 0)
            {
                lines.AddRange(extraLines);
            }
            else if (!IsNoOpOnly(item.Lines))
            {
                lines.AddRange(item.Lines ?? new List<string>());
            }

            if (!lines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return;

            string key = string.Join("\n", lines);
            if (seen != null && !seen.Add(key))
                return;

            result.Add(new FlatVariantRenderItem
            {
                Variant = item.Variant,
                Lines = lines
            });
        }

        private static VariantRenderItem FindFirstNoChoiceVariant(VariantTreeNode node)
        {
            if (node == null)
                return null;

            var direct = OrderDirectVariants(node.DirectVariants)
                .FirstOrDefault(ShouldRenderAsNoChoiceVariant);
            if (direct != null)
                return direct;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                var nested = FindFirstNoChoiceVariant(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static void CollectPartyScanNoChoiceSiblingLines(
            VariantTreeNode node,
            List<string> lines)
        {
            if (node == null || lines == null)
                return;

            foreach (var line in node.CommonLines ?? new List<string>())
            {
                if (!IsIgnorablePartyScanAggregateLine(line))
                    lines.Add(line);
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                CollectPartyScanNoChoiceSiblingLines(child, lines);
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromObjectTexts(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var variantContents = BuildVariantContentsFromRawTexts(obj);
            if (!string.IsNullOrWhiteSpace(inlineSpecialSpoilerLine))
            {
                foreach (var key in variantContents.Keys.ToList())
                    InsertInlineSpoilerAfterAnswerPrompt(variantContents[key], inlineSpecialSpoilerLine);
            }

            var monsterStatLines = GetMonsterStatLines(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance);
            var battleLines = GetBattleLines(obj);
            var specialNoteLines = GetSpecialNoteLines(obj);

            MergeObjectMetaIntoVariants(variantContents, monsterStatLines, battleLines, specialNoteLines);
            return variantContents;
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromRawTexts(OvrObject obj)
        {
            var variantContents = new Dictionary<int, List<string>>();

            if (obj.PathTextsOrdered != null && obj.PathTextsOrdered.Count > 0)
            {
                foreach (var kvp in obj.PathTextsOrdered.OrderBy(p => p.Key))
                {
                    var decodedTexts = DecodeNoteTexts(kvp.Value);
                    if (decodedTexts.Count > 0)
                        variantContents[kvp.Key] = decodedTexts;
                }

                return variantContents;
            }

            if (obj.PathTexts != null && obj.PathTexts.Count > 0)
            {
                foreach (var kvp in obj.PathTexts.OrderBy(p => p.Key))
                {
                    var decodedTexts = DecodeNoteTexts(kvp.Value);
                    if (decodedTexts.Count > 0)
                        variantContents[kvp.Key] = decodedTexts;
                }
            }

            return variantContents;
        }

        private static List<string> BuildVariantLines(
            OvrObject variantObject,
            IEnumerable<string> rawTexts,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var lines = new List<string>();
            var narrativeLines = RemoveAdjacentDuplicatePromptLines(DecodeNoteTexts(rawTexts));
            InsertInlineSpoilerAfterAnswerPrompt(narrativeLines, inlineSpecialSpoilerLine);
            var monsterStatLines = GetMonsterStatLines(
                variantObject,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance);
            var specialNoteLines = GetSpecialNoteLines(variantObject);
            var promotedConditionLines = GetPromotedFlatConditionLines(variantObject);

            if (narrativeLines.Count > 0 && promotedConditionLines.Count > 0)
            {
                lines.Add(narrativeLines[0]);
                lines.AddRange(promotedConditionLines);
                lines.AddRange(narrativeLines.Skip(1));
                specialNoteLines = RemoveLineOccurrences(specialNoteLines, promotedConditionLines);
            }
            else
            {
                lines.AddRange(narrativeLines);
            }

            lines.AddRange(monsterStatLines);
            lines.AddRange(specialNoteLines);
            lines.AddRange(GetBattleLines(variantObject));

            if (lines.Count == 0)
                lines.Add("Ничего не происходит");

            return lines;
        }

        private static List<string> GetPromotedFlatConditionLines(OvrObject obj)
        {
            if (obj?.PartyEffects == null || obj.PartyEffects.Count == 0)
                return new List<string>();

            return GetOrderedDisplayablePartyEffects(
                    obj.PartyEffects.Where(effect => effect != null).ToList(),
                    effect => effect != null && PartyEffectSemantics.IsGuardLike(effect))
                .Select(PartyEffectSemantics.BuildHumanDescription)
                .Where(IsPromotableFlatConditionLine)
                .Distinct()
                .ToList();
        }

        private static List<string> RemoveAdjacentDuplicatePromptLines(List<string> lines)
        {
            if (lines == null || lines.Count < 2)
                return lines ?? new List<string>();

            var result = new List<string>();
            foreach (var line in lines)
            {
                if (result.Count > 0 &&
                    IsPromptLine(line) &&
                    string.Equals(result[result.Count - 1], line, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(line);
            }

            return result;
        }

        private static bool IsPromptLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return ExtractPromptOptionLabels(line).Count >= 2;
        }

        private static List<string> BuildVariantLinesForHierarchy(
            OvrObject variantObject,
            IEnumerable<string> rawTexts,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var lines = new List<string>();
            var narrativeLines = RemoveAdjacentDuplicatePromptLines(DecodeNoteTexts(rawTexts));
            InsertInlineSpoilerAfterAnswerPrompt(narrativeLines, inlineSpecialSpoilerLine);
            var specialNoteLines = GetSpecialNoteLines(variantObject);

            lines.AddRange(narrativeLines);
            lines.AddRange(GetMonsterStatLines(
                variantObject,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance));
            lines.AddRange(specialNoteLines);
            lines.AddRange(GetBattleLines(variantObject));

            if (lines.Count == 0)
                lines.Add("Ничего не происходит");

            return lines;
        }

        private static string BuildVariantHeader(OvrObject obj, int variantKey, int displayVariantNumber)
        {
            string header = $"Вариант {displayVariantNumber}";

            if (TryResolvePathVariantForDisplayKey(obj, variantKey, out var pathVariant))
            {
                var annotations = BuildFlatVariantHeaderAnnotations(obj, pathVariant);
                string annotationText = BuildVariantHeaderAnnotationText(annotations);
                if (!string.IsNullOrWhiteSpace(annotationText))
                    header += $" ({annotationText})";
            }

            return header;
        }

        private static bool VariantHeaderContainsProbability(string variantHeader)
        {
            return !string.IsNullOrEmpty(variantHeader)
                && (variantHeader.IndexOf("вероятность наступления", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    variantHeader.IndexOf("вероятность при выполнении условий", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    variantHeader.IndexOf("при условии:", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatVariantLine(string line, bool headerContainsProbability)
        {
            if (string.Equals(line, "Ничего не происходит", StringComparison.Ordinal) && !headerContainsProbability)
                return "Ничего не происходит (не выполнены условия для наступления ни одного варианта)";

            return line;
        }

        private static bool TryBuildCollapsedCappedRandomPartialBattleNote(
            OvrObject obj,
            Dictionary<int, List<string>> variantContents,
            out string notes)
        {
            notes = null;

            if (obj?.PathVariants == null ||
                obj.PathVariants.Count < 3 ||
                variantContents == null ||
                variantContents.Count < 3)
            {
                return false;
            }

            var variants = obj.PathVariants
                .Where(kvp => kvp.Value != null)
                .OrderBy(kvp => kvp.Key)
                .ToList();

            var noOpCandidates = variants
                .Where(kvp => IsDisplayedNoOpVariant(kvp.Value, variantContents.TryGetValue(kvp.Key, out var lines) ? lines : null))
                .ToList();

            if (noOpCandidates.Count != 1)
                return false;

            var noOpEntry = noOpCandidates[0];
            var noOpVariant = noOpEntry.Value;
            if (!noOpVariant.HasProbabilityInfo ||
                noOpVariant.ProbabilityNumerator <= 0 ||
                noOpVariant.ProbabilityNumerator >= noOpVariant.ProbabilityDenominator)
            {
                return false;
            }

            var battleEntries = variants
                .Where(kvp => IsPlainPartialBattleVariant(kvp.Value))
                .ToList();

            if (battleEntries.Count < 2)
            {
                return false;
            }

            var battleEntryKeys = new HashSet<int>(battleEntries.Select(kvp => kvp.Key));
            var passthroughEntries = variants
                .Where(kvp => kvp.Key != noOpEntry.Key && !battleEntryKeys.Contains(kvp.Key))
                .ToList();

            if (passthroughEntries.Any(kvp => !CanRenderBesideCollapsedPartialBattle(
                    kvp.Value,
                    variantContents.TryGetValue(kvp.Key, out var lines) ? lines : null)))
            {
                return false;
            }

            var battleVariants = battleEntries
                .Select(kvp => kvp.Value)
                .ToList();

            var dynamicDependencies = battleVariants
                .SelectMany(variant => variant.DynamicRandomBoundDependencies ?? new List<DynamicRandomBoundDependencyInfo>())
                .Where(dependency => dependency?.UpperBoundFormula != null)
                .ToList();

            if (dynamicDependencies.Count == 0)
                return false;

            var dynamicDependency = dynamicDependencies[0];
            string dynamicDependencyKey = dynamicDependency.GetIdentityKey();
            if (dynamicDependencies.Any(dependency =>
                !string.Equals(dependency.GetIdentityKey(), dynamicDependencyKey, StringComparison.Ordinal)))
            {
                return false;
            }

            if (!IsLevelPlusOffsetFormula(dynamicDependency?.UpperBoundFormula))
                return false;

            var battleOptions = BuildCollapsedPartialBattleOptions(battleVariants);
            if (battleOptions.Count < 2)
                return false;

            if (!TryBuildCollapsedPartialBattleProbability(
                    noOpVariant,
                    passthroughEntries.Select(kvp => kvp.Value),
                    out int encounterNumerator,
                    out int encounterDenominator))
            {
                return false;
            }

            string encounterProbability = BuildProbabilityHeaderAnnotation(
                BuildStandaloneProbabilityLine(encounterNumerator, encounterDenominator));
            if (string.IsNullOrWhiteSpace(encounterProbability))
                return false;

            string formulaText = BuildDiceFormulaExpression(dynamicDependency.UpperBoundFormula);
            string dynamicVariantCountText =
                $"Количество доступных вариантов рассчитывается по формуле: {formulaText}, но не более {battleOptions.Count}";
            string dynamicVariantCountLine = InlineNoteStyleCodec.EncodeMutedParentheticalNoteText(
                $"({dynamicVariantCountText})");

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            int displayVariantNumber = 1;

            sb.AppendLine($"{BuildVariantHeader(obj, noOpEntry.Key, displayVariantNumber++)}:");
            AppendLines(sb, variantContents.TryGetValue(noOpEntry.Key, out var noOpLines)
                ? noOpLines
                : new List<string> { "Ничего не происходит" });

            foreach (var passthroughEntry in passthroughEntries.OrderBy(kvp => GetPathOrderKey(kvp.Value)))
            {
                sb.AppendLine();
                sb.AppendLine($"{BuildVariantHeader(obj, passthroughEntry.Key, displayVariantNumber++)}:");
                AppendLines(sb, variantContents.TryGetValue(passthroughEntry.Key, out var passthroughLines)
                    ? passthroughLines
                    : new List<string>());
            }

            sb.AppendLine();
            sb.AppendLine($"Вариант {displayVariantNumber} ({encounterProbability}):");
            sb.AppendLine($"Частично определённая битва. {battleOptions.Count} вариант(ов):");
            sb.AppendLine(dynamicVariantCountLine);

            for (int i = 0; i < battleOptions.Count; i++)
            {
                var option = battleOptions[i];
                string optionText = string.IsNullOrWhiteSpace(option.CountDisplay)
                    ? option.MonsterName
                    : $"{option.MonsterName} x{option.CountDisplay}";
                sb.AppendLine($"  • Вариант {i + 1}: {optionText}");
            }

            string rubiconWarning = BuildSharedRandomEncounterRubiconWarning(battleVariants);
            if (!string.IsNullOrWhiteSpace(rubiconWarning))
                sb.AppendLine(rubiconWarning);

            notes = sb.ToString().TrimEnd('\r', '\n');
            return true;
        }

        private static bool CanBuildCollapsedCappedRandomPartialBattleNote(
            OvrObject obj,
            Dictionary<int, List<string>> variantContents)
        {
            return TryBuildCollapsedCappedRandomPartialBattleNote(
                obj,
                variantContents,
                out _);
        }

        private static bool CanRenderBesideCollapsedPartialBattle(PathVariantInfo variant, List<string> lines)
        {
            if (variant == null || IsPlainPartialBattleVariant(variant))
                return false;

            bool hasDisplayableLines = (lines ?? new List<string>())
                .Any(line => !string.IsNullOrWhiteSpace(line));
            if (!hasDisplayableLines)
                return false;

            bool hasSelfContainedOutcome =
                (variant.Texts != null && variant.Texts.Count > 0) ||
                variant.HasTeleportTarget ||
                (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0) ||
                (variant.PartyEffects != null && variant.PartyEffects.Count > 0);

            if (!hasSelfContainedOutcome)
                return false;

            return !variant.RandomEncounterMonsterPowerCap.HasValue &&
                   !variant.RandomEncounterMonsterLevelCap.HasValue &&
                   !variant.RandomEncounterMonsterBatchCountCap.HasValue &&
                   !variant.DarkeningLevel.HasValue &&
                   !variant.RandomEncounterChance.HasValue &&
                   variant.BattleMonsterStrengthAdjustment == 0 &&
                   (variant.PartiallyDefinedBattles == null || variant.PartiallyDefinedBattles.Count == 0) &&
                   (variant.DynamicRandomBoundDependencies == null || variant.DynamicRandomBoundDependencies.Count == 0) &&
                   (variant.PersistentCounterProgressions == null || variant.PersistentCounterProgressions.Count == 0) &&
                   !variant.HasAnyTableLoad &&
                   (variant.LoadedValues == null || variant.LoadedValues.Count == 0) &&
                   !variant.HasStateGuardInfo;
        }

        private static bool TryBuildCollapsedPartialBattleProbability(
            PathVariantInfo noOpVariant,
            IEnumerable<PathVariantInfo> passthroughVariants,
            out int numerator,
            out int denominator)
        {
            numerator = 1;
            denominator = 1;

            var excludedVariants = new List<PathVariantInfo> { noOpVariant };
            excludedVariants.AddRange(passthroughVariants ?? Enumerable.Empty<PathVariantInfo>());

            foreach (var variant in excludedVariants)
            {
                if (!TryGetVariantProbabilityFraction(variant, out int variantNumerator, out int variantDenominator))
                    return false;

                if (!TrySubtractProbability(
                        numerator,
                        denominator,
                        variantNumerator,
                        variantDenominator,
                        out numerator,
                        out denominator))
                {
                    return false;
                }
            }

            if (numerator <= 0)
                return false;

            ReduceFraction(ref numerator, ref denominator);
            return true;
        }

        private static bool TryGetVariantProbabilityFraction(
            PathVariantInfo variant,
            out int numerator,
            out int denominator)
        {
            numerator = 0;
            denominator = 1;

            if (variant == null || !variant.HasProbabilityInfo || variant.ProbabilityDenominator <= 0)
                return false;

            numerator = Math.Max(0, variant.ProbabilityNumerator);
            denominator = Math.Max(1, variant.ProbabilityDenominator);
            if (numerator >= denominator && numerator > 0)
            {
                numerator = 1;
                denominator = 1;
            }

            ReduceFraction(ref numerator, ref denominator);
            return true;
        }

        private static bool TrySubtractProbability(
            int leftNumerator,
            int leftDenominator,
            int rightNumerator,
            int rightDenominator,
            out int numerator,
            out int denominator)
        {
            numerator = 0;
            denominator = 1;

            if (leftDenominator <= 0 || rightDenominator <= 0)
                return false;

            long gcd = GreatestCommonDivisor(Math.Abs(leftDenominator), Math.Abs(rightDenominator));
            long lcm = (long)leftDenominator / gcd * rightDenominator;
            if (lcm <= 0 || lcm > int.MaxValue)
                return false;

            long adjustedNumerator =
                (long)leftNumerator * (lcm / leftDenominator) -
                (long)rightNumerator * (lcm / rightDenominator);

            if (adjustedNumerator < int.MinValue || adjustedNumerator > int.MaxValue)
                return false;

            numerator = (int)adjustedNumerator;
            denominator = (int)lcm;
            ReduceFraction(ref numerator, ref denominator);
            return true;
        }

        private static bool IsDisplayedNoOpVariant(PathVariantInfo variant, List<string> lines)
        {
            if (variant == null)
                return false;

            bool hasNoBattlePayload =
                (variant.Texts == null || variant.Texts.Count == 0) &&
                (variant.BattleMonsters == null || variant.BattleMonsters.Count == 0) &&
                (variant.PartiallyDefinedBattles == null || variant.PartiallyDefinedBattles.Count == 0) &&
                (variant.DynamicRandomBoundDependencies == null || variant.DynamicRandomBoundDependencies.Count == 0) &&
                (variant.PartyEffects == null || variant.PartyEffects.Count == 0) &&
                !variant.HasTeleportTarget &&
                !variant.HasAnyTableLoad &&
                variant.BattleMonsterStrengthAdjustment == 0 &&
                !variant.CallsRandomEncounter;

            if (!hasNoBattlePayload)
                return false;

            var meaningfulLines = (lines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            return meaningfulLines.Count == 0 ||
                   meaningfulLines.All(line => line.StartsWith("Ничего не происходит", StringComparison.Ordinal));
        }

        private static bool IsPlainPartialBattleVariant(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            return (variant.Texts == null || variant.Texts.Count == 0) &&
                   (variant.BattleMonsters == null || variant.BattleMonsters.Count == 0) &&
                   variant.PartiallyDefinedBattles != null &&
                   variant.PartiallyDefinedBattles.Count > 0 &&
                   !variant.RandomEncounterMonsterPowerCap.HasValue &&
                   !variant.RandomEncounterMonsterLevelCap.HasValue &&
                   !variant.RandomEncounterMonsterBatchCountCap.HasValue &&
                   !variant.DarkeningLevel.HasValue &&
                   !variant.RandomEncounterChance.HasValue &&
                   variant.BattleMonsterStrengthAdjustment == 0 &&
                   (variant.PersistentCounterProgressions == null || variant.PersistentCounterProgressions.Count == 0) &&
                   (variant.PartyEffects == null || variant.PartyEffects.Count == 0) &&
                   !variant.HasTeleportTarget &&
                   !variant.HasAnyTableLoad;
        }

        private static bool IsLevelPlusOffsetFormula(DynamicValueFormulaInfo formula)
        {
            if (formula?.SourceField == null)
                return false;

            return formula.SourceField.Field == PartyFieldKind.TempLevel &&
                   formula.Multiplier == 1 &&
                   formula.AdditiveOffset > 0;
        }

        private static List<(string OptionKey, string MonsterName, string CountDisplay)> BuildCollapsedPartialBattleOptions(
            List<PathVariantInfo> battleVariants)
        {
            var result = new List<(string OptionKey, string MonsterName, string CountDisplay)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var variant in battleVariants ?? new List<PathVariantInfo>())
            {
                foreach (var partial in (variant.PartiallyDefinedBattles ?? new List<PartiallyDefinedBattle>())
                    .OrderBy(battle => battle.BxIndex))
                {
                    string countDisplay = partial.GetRepeatCountDisplay();
                    foreach (var monster in partial.GetPossibleMonsters())
                    {
                        string optionKey = $"{monster.Val1:X2}:{monster.Val2:X2}";
                        if (!seen.Add(optionKey))
                            continue;

                        string monsterName = CleanMonsterNameForCollapsedDisplay(monster.MonsterName);
                        if (string.IsNullOrWhiteSpace(monsterName))
                            continue;

                        result.Add((optionKey, monsterName, countDisplay));
                    }
                }
            }

            return result;
        }

        private static string CleanMonsterNameForCollapsedDisplay(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var clean = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == ' ' || c == '-' || c == '\'' || c == '.')
                {
                    clean.Append(c);
                }
            }

            return Regex.Replace(clean.ToString().Trim(), @"\s+", " ");
        }

        private static string BuildSharedRandomEncounterRubiconWarning(List<PathVariantInfo> battleVariants)
        {
            var thresholds = (battleVariants ?? new List<PathVariantInfo>())
                .Where(variant => variant != null && variant.CallsRandomEncounter && variant.RandomEncounterRubicon.HasValue)
                .Select(variant => 2 * (variant.RandomEncounterRubicon.Value + 1))
                .Distinct()
                .ToList();

            return thresholds.Count == 1
                ? InlineNoteStyleCodec.EncodeRandomEncounterRubiconWarning(thresholds[0])
                : null;
        }

        private static string BuildDiceFormulaExpression(DynamicValueFormulaInfo formula)
        {
            if (formula == null)
                return "значение";

            if (formula.SourceField?.Field == PartyFieldKind.TempLevel)
            {
                string expression = "временный LEVEL";
                string memberText = BuildDiceFormulaMemberText(formula.SourceField.Member);
                if (!string.IsNullOrWhiteSpace(memberText))
                    expression += " " + memberText;

                if (formula.Multiplier != 1)
                    expression = $"{formula.Multiplier}*{expression}";

                if (formula.AdditiveOffset > 0)
                    expression += $" + {formula.AdditiveOffset}";
                else if (formula.AdditiveOffset < 0)
                    expression += $" - {Math.Abs(formula.AdditiveOffset)}";

                return expression;
            }

            return formula.GetFormulaExpression();
        }

        private static string BuildDiceFormulaMemberText(PartyMemberReference member)
        {
            if (member == null)
                return null;

            if (member.MemberIndex == 0)
                return "первого героя";

            if (member.MemberIndex.HasValue)
                return $"персонажа {PartyMemberReference.FormatDisplayIndex(member.MemberIndex.Value)}";

            if (member.IsPartyLoopMember)
                return "текущего персонажа";

            return null;
        }

        private static string BuildStandaloneProbabilityLine(int numerator, int denominator)
        {
            if (denominator <= 1)
                return null;

            string percentText = ProbabilityFormatter.FormatPercent(numerator, denominator);

            return $"Вероятность: {percentText}% ({numerator}/{denominator})";
        }

        private static void ReduceFraction(ref int numerator, ref int denominator)
        {
            if (denominator <= 0)
            {
                denominator = 1;
                return;
            }

            int divisor = GreatestCommonDivisor(Math.Abs(numerator), Math.Abs(denominator));
            if (divisor <= 1)
                return;

            numerator /= divisor;
            denominator /= divisor;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                int temp = a % b;
                a = b;
                b = temp;
            }

            return Math.Max(1, a);
        }

        private static void AppendLines(StringBuilder sb, IEnumerable<string> lines)
        {
            foreach (var line in lines ?? Enumerable.Empty<string>())
                sb.AppendLine(line);
        }

        private static IEnumerable<string> SplitDisplayLines(string text)
        {
            if (text == null)
                yield break;

            string normalized = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');

            foreach (var part in normalized.Split('\n'))
                yield return part;
        }

        private static void AppendIndentedDisplayLine(
            StringBuilder sb,
            string indent,
            string line,
            bool headerContainsProbability)
        {
            foreach (var part in SplitDisplayLines(FormatVariantLine(line, headerContainsProbability)))
                sb.AppendLine(indent + part);
        }

        private static void AppendIndentedDisplayLines(
            StringBuilder sb,
            string indent,
            IEnumerable<string> lines,
            bool headerContainsProbability)
        {
            foreach (var line in lines ?? Enumerable.Empty<string>())
                AppendIndentedDisplayLine(sb, indent, line, headerContainsProbability);
        }

        private static string PrefixProbabilityWordInParentheses(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (text.IndexOf("вероятность наступления", StringComparison.OrdinalIgnoreCase) >= 0)
                return text;

            var regex = new System.Text.RegularExpressions.Regex(
                @"(\d+(?:[.,]\d+)?\s*%)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            if (!regex.IsMatch(text))
                return text;

            return regex.Replace(text, "вероятность наступления $1", 1);
        }

        private static string BuildProbabilityLine(PathVariantInfo variant)
        {
            if (variant == null)
                return null;

            return variant.GetProbabilityDescription();
        }

        private static string BuildOccurrenceLine(PathVariantInfo variant)
        {
            if (variant == null)
                return null;

            return variant.GetOccurrenceDescription();
        }

        private static string BuildTrailingRepeatedBattleWarningLine(OvrObject obj)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count != 1)
                return null;

            var variant = obj.PathVariants.Values.FirstOrDefault();
            if (!ShouldShowRepeatedBattleWarning(variant))
                return null;

            return InlineNoteStyleCodec.EncodeRepeatedBattleWarningText(InlineNoteStyleCodec.RepeatedBattleWarningText);
        }

        private static bool ShouldShowRepeatedBattleWarning(PathVariantInfo variant)
        {
            return HasExactlyFirstTwoOccurrenceCoverage(variant) &&
                   HasBattleLaunchOutcome(variant);
        }

        private static bool HasExactlyFirstTwoOccurrenceCoverage(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            bool hasCoverage = false;
            var covered = new HashSet<int>();

            foreach (var range in variant.OccurrenceRanges ?? Enumerable.Empty<OccurrenceRangeInfo>())
            {
                if (range == null || range.Start <= 0)
                    continue;

                hasCoverage = true;
                if (range.IsOpenEnded)
                    return false;

                int end = Math.Max(range.Start, range.End);
                if (range.Start < 1 || end > 2)
                    return false;

                for (int occurrence = range.Start; occurrence <= end; occurrence++)
                    covered.Add(occurrence);
            }

            foreach (int occurrence in variant.OccurrenceIndices ?? Enumerable.Empty<int>())
            {
                if (occurrence <= 0)
                    continue;

                hasCoverage = true;
                if (occurrence < 1 || occurrence > 2)
                    return false;

                covered.Add(occurrence);
            }

            return hasCoverage &&
                   covered.Count == 2 &&
                   covered.Contains(1) &&
                   covered.Contains(2);
        }

        private static bool HasBattleLaunchOutcome(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            return variant.CallsRandomEncounter ||
                   variant.BattleMonsterCount.HasValue ||
                   variant.BattleMonsterCountRange != null ||
                   variant.IsBattleMonsterCountIndeterminate ||
                   (variant.BattleMonsters?.Count ?? 0) > 0 ||
                   (variant.PartiallyDefinedBattles?.Count ?? 0) > 0;
        }

        private static string BuildProbabilityHeaderAnnotation(
            string probabilityLine,
            bool conditionAlreadyVisible = false)
        {
            if (string.IsNullOrWhiteSpace(probabilityLine))
                return null;

            const string conditionalPrefix = "Вероятность при выполнении условий: ";
            if (probabilityLine.StartsWith(conditionalPrefix, StringComparison.Ordinal))
            {
                string probabilityText = probabilityLine.Substring(conditionalPrefix.Length);
                return conditionAlreadyVisible
                    ? PrefixProbabilityWordInParentheses(probabilityText)
                    : "вероятность при выполнении условий " + probabilityText;
            }

            if (probabilityLine.StartsWith("Вероятность: ", StringComparison.Ordinal))
                probabilityLine = probabilityLine.Substring("Вероятность: ".Length);

            return PrefixProbabilityWordInParentheses(probabilityLine);
        }

        private static List<PartyPredicate> GetDisplayGuardPredicates(PathVariantInfo variant)
        {
            if (!ShouldDisplayGuardCondition(variant))
                return new List<PartyPredicate>();

            var structurallyRenderedPredicateKeys = BuildStructurallyRenderedPredicateKeySet(variant);

            return variant?.GetGuardPredicates()?
                .Where(predicate => predicate != null)
                .Where(predicate =>
                    !structurallyRenderedPredicateKeys.Contains(PartyEffectSemantics.BuildPredicateKey(predicate)))
                .ToList() ?? new List<PartyPredicate>();
        }

        private static HashSet<string> BuildStructurallyRenderedPredicateKeySet(PathVariantInfo variant)
        {
            return GetRelevantBranchChoices(variant)
                .Where(choice => choice?.GuardPredicate != null)
                .Where(choice => PartyInventorySemantics.TryBuildItemPresenceChoiceLabel(choice, out _))
                .Select(choice => PartyEffectSemantics.BuildPredicateKey(choice.GuardPredicate))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool ShouldDisplayGuardCondition(PathVariantInfo variant)
        {
            return variant?.HasStateGuardInfo == true && variant.HasProbabilityInfo;
        }

        private static string BuildGuardConditionKey(PathVariantInfo variant)
        {
            return BuildGuardConditionKey(GetDisplayGuardPredicates(variant));
        }

        private static string BuildGuardConditionKey(IEnumerable<PartyPredicate> predicates)
        {
            var keys = (predicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Select(PartyEffectSemantics.BuildPredicateKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            return keys.Count == 0
                ? null
                : string.Join("&", keys);
        }

        private static string BuildGuardHeaderAnnotation(PathVariantInfo variant, string suppressedGuardKey = null)
        {
            var predicates = GetDisplayGuardPredicates(variant);
            string guardKey = BuildGuardConditionKey(predicates);
            if (string.IsNullOrWhiteSpace(guardKey))
                return null;

            if (!string.IsNullOrWhiteSpace(suppressedGuardKey) &&
                string.Equals(guardKey, suppressedGuardKey, StringComparison.Ordinal))
            {
                return null;
            }

            return BuildGuardHeaderAnnotation(predicates);
        }

        private static string BuildGuardHeaderAnnotation(IEnumerable<PartyPredicate> predicates)
        {
            string text = PartyEffectSemantics.BuildPredicateListDisplayText(predicates);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : "при условии: " + text;
        }

        private static bool IsGuardConditionVisibleInScope(PathVariantInfo variant, string suppressedGuardKey)
        {
            if (string.IsNullOrWhiteSpace(suppressedGuardKey))
                return false;

            string guardKey = BuildGuardConditionKey(variant);
            return !string.IsNullOrWhiteSpace(guardKey) &&
                   string.Equals(guardKey, suppressedGuardKey, StringComparison.Ordinal);
        }

        private static List<string> BuildVariantHeaderAnnotations(
            PathVariantInfo variant,
            string suppressedProbabilityLine = null,
            string suppressedGuardKey = null)
        {
            var annotations = new List<string>();

            string occurrence = BuildOccurrenceLine(variant);
            if (!string.IsNullOrWhiteSpace(occurrence) &&
                !ShouldSuppressSelfDisableOccurrenceHeaderAnnotation(variant, occurrence))
            {
                annotations.Add(occurrence);
            }

            string guard = BuildGuardHeaderAnnotation(variant, suppressedGuardKey);
            if (!string.IsNullOrWhiteSpace(guard))
                annotations.Add(guard);

            string probability = BuildProbabilityLine(variant);
            if (!string.IsNullOrEmpty(suppressedProbabilityLine) &&
                string.Equals(probability, suppressedProbabilityLine, StringComparison.Ordinal))
            {
                probability = null;
            }

            bool conditionVisible = !string.IsNullOrWhiteSpace(guard) ||
                                    IsGuardConditionVisibleInScope(variant, suppressedGuardKey);
            string probabilityAnnotation = BuildProbabilityHeaderAnnotation(probability, conditionVisible);
            if (!string.IsNullOrWhiteSpace(probabilityAnnotation))
                annotations.Add(probabilityAnnotation);

            return annotations;
        }

        private static List<string> BuildFlatVariantHeaderAnnotations(
            OvrObject obj,
            PathVariantInfo variant)
        {
            var annotations = new List<string>();
            AddDistinctHeaderAnnotations(
                annotations,
                BuildInventoryPresenceHeaderAnnotationsForFlat(obj, variant));
            AddDistinctHeaderAnnotations(
                annotations,
                BuildVariantHeaderAnnotations(variant));
            return annotations;
        }

        private static void AddDistinctHeaderAnnotations(
            List<string> target,
            IEnumerable<string> annotations)
        {
            if (target == null || annotations == null)
                return;

            foreach (string annotation in annotations)
            {
                string normalized = NormalizeHeaderAnnotation(annotation);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (!target.Contains(normalized, StringComparer.Ordinal))
                    target.Add(normalized);
            }
        }

        private static string BuildVariantHeaderAnnotationText(IEnumerable<string> annotations)
        {
            var displayAnnotations = (annotations ?? Enumerable.Empty<string>())
                .Select(NormalizeUserVisibleHeaderAnnotation)
                .Where(annotation => !string.IsNullOrWhiteSpace(annotation))
                .Select(EncodeVariantHeaderAnnotationForDisplay)
                .ToList();

            return displayAnnotations.Count == 0
                ? null
                : string.Join("; ", displayAnnotations);
        }

        private static string EncodeVariantHeaderAnnotationForDisplay(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            if (TrySplitInventoryPresenceHeaderAnnotation(
                    normalized,
                    out string itemName,
                    out string presenceLabel))
            {
                return $"{InlineNoteStyleCodec.EncodeItemNameText(itemName)} {presenceLabel}";
            }

            return normalized;
        }

        private static List<string> BuildInventoryPresenceHeaderAnnotationsForFlat(
            OvrObject obj,
            PathVariantInfo variant)
        {
            var explicitAnnotations = BuildInventoryPresenceHeaderAnnotationsForVariant(variant);
            if (explicitAnnotations.Count > 0)
                return explicitAnnotations;

            return InferComplementaryInventoryPresenceHeaderAnnotations(obj, variant);
        }

        private static List<string> BuildInventoryPresenceHeaderAnnotationsForVariant(
            PathVariantInfo variant)
        {
            return GetInventoryPresenceChoiceInfos(variant)
                .Select(info => info.HeaderAnnotation)
                .Where(annotation => !string.IsNullOrWhiteSpace(annotation))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(annotation => annotation, StringComparer.Ordinal)
                .ToList();
        }

        private static List<string> InferComplementaryInventoryPresenceHeaderAnnotations(
            OvrObject obj,
            PathVariantInfo variant)
        {
            if (obj?.PathVariants == null || variant == null)
                return new List<string>();

            if (GetInventoryPresenceChoiceInfos(variant).Count > 0)
                return new List<string>();

            var candidates = obj.PathVariants.Values
                .Where(pathVariant => pathVariant != null && !ReferenceEquals(pathVariant, variant))
                .SelectMany(GetInventoryPresenceChoiceInfos)
                .GroupBy(info => info.ItemName, StringComparer.Ordinal)
                .Select(group =>
                {
                    var presenceLabels = group
                        .Select(info => info.PresenceLabel)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    return new
                    {
                        ItemName = group.Key,
                        PresenceLabels = presenceLabels
                    };
                })
                .Where(candidate => candidate.PresenceLabels.Count == 1)
                .ToList();

            if (candidates.Count != 1 ||
                !TryGetOppositeInventoryPresenceLabel(candidates[0].PresenceLabels[0], out string oppositePresence))
            {
                return new List<string>();
            }

            return new List<string> { $"{candidates[0].ItemName} {oppositePresence}" };
        }

        private static bool ShouldSuppressSelfDisableOccurrenceHeaderAnnotation(
            PathVariantInfo variant,
            string occurrence)
        {
            if (variant?.DisablesCurrentMapEvent != true ||
                string.IsNullOrWhiteSpace(occurrence))
            {
                return false;
            }

            return HasFirstOccurrenceOnlyCoverage(variant) ||
                   string.Equals(occurrence, "только при 1-м наступлениях", StringComparison.Ordinal);
        }

        private static bool HasFirstOccurrenceOnlyCoverage(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            bool hasCoverage = false;
            var covered = new HashSet<int>();

            foreach (var range in variant.OccurrenceRanges ?? Enumerable.Empty<OccurrenceRangeInfo>())
            {
                if (range == null || range.Start <= 0)
                    continue;

                hasCoverage = true;
                if (range.IsOpenEnded)
                    return false;

                int end = Math.Max(range.Start, range.End);
                if (range.Start != 1 || end != 1)
                    return false;

                covered.Add(1);
            }

            foreach (int occurrenceIndex in variant.OccurrenceIndices ?? Enumerable.Empty<int>())
            {
                if (occurrenceIndex <= 0)
                    continue;

                hasCoverage = true;
                if (occurrenceIndex != 1)
                    return false;

                covered.Add(1);
            }

            return hasCoverage && covered.Count == 1 && covered.Contains(1);
        }

        private static void MergeObjectMetaIntoVariants(
            Dictionary<int, List<string>> variantContents,
            List<string> monsterStatLines,
            List<string> battleLines,
            List<string> specialNoteLines)
        {
            bool hasBattle = battleLines.Count > 0;
            bool hasMonsterStats = monsterStatLines.Count > 0;
            bool hasSpecialNotes = specialNoteLines.Count > 0;

            if (hasBattle)
            {
                var battleVariantLines = new List<string>();
                battleVariantLines.AddRange(monsterStatLines);
                battleVariantLines.AddRange(specialNoteLines);
                battleVariantLines.AddRange(battleLines);

                if (variantContents.Count == 0)
                {
                    variantContents[0] = battleVariantLines;
                }
                else if (variantContents.Count == 1)
                {
                    int firstVariant = variantContents.Keys.Min();
                    variantContents[firstVariant].AddRange(battleVariantLines);
                }
                else
                {
                    int maxVariant = variantContents.Keys.Max();
                    variantContents[maxVariant + 1] = battleVariantLines;
                }

                return;
            }

            if ((hasMonsterStats || hasSpecialNotes) && variantContents.Count > 0)
            {
                int firstVariant = variantContents.Keys.Min();
                if (hasMonsterStats)
                    variantContents[firstVariant].InsertRange(0, monsterStatLines);
                if (hasSpecialNotes)
                    variantContents[firstVariant].AddRange(specialNoteLines);
                return;
            }

            if (hasMonsterStats || hasSpecialNotes)
            {
                var metaLines = new List<string>();
                metaLines.AddRange(monsterStatLines);
                metaLines.AddRange(specialNoteLines);
                variantContents[0] = metaLines;
            }
        }

        private sealed class VariantRenderItem
        {
            public PathVariantInfo Variant { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
            public List<string> NarrativeLines { get; set; } = new List<string>();
        }

        private sealed class FlatVariantRenderItem
        {
            public PathVariantInfo Variant { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
        }

        private sealed class VariantTreeNode
        {
            public string SegmentKey { get; set; }
            public string Label { get; set; }
            public string HeaderAnnotation { get; set; }
            public List<string> CommonLines { get; set; } = new List<string>();
            public List<VariantRenderItem> DirectVariants { get; set; } = new List<VariantRenderItem>();
            public List<VariantTreeNode> Children { get; set; } = new List<VariantTreeNode>();
            public bool PreserveAsStructuralGroup { get; set; }
        }

        private sealed class TopLevelVariantGroup
        {
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public VariantTreeNode TreeRoot { get; set; }
            public string Label { get; set; }
            public string HeaderAnnotation { get; set; }
            public string ConsumedTopChoiceKey { get; set; }
            public bool GroupedByChoice { get; set; }
            public int SourceOrderKey { get; set; } = int.MaxValue;
        }

        private sealed class OrderedRenderEntry
        {
            public int OccurrenceOrderKey { get; set; }
            public int DisplayPriority { get; set; }
            public decimal PathOrderKey { get; set; }
            public int ChoiceOrderKey { get; set; } = int.MaxValue;
            public bool HasChoiceOrderKey { get; set; }
            public VariantTreeNode ChildNode { get; set; }
            public VariantRenderItem DirectVariant { get; set; }
        }

        private sealed class BranchChoiceDisplayCandidate
        {
            public BranchChoice Raw { get; set; }
            public BranchChoice Normalized { get; set; }
        }

        private sealed class InventoryPresenceChoiceInfo
        {
            public string ItemName { get; set; }
            public string PresenceLabel { get; set; }
            public string HeaderAnnotation { get; set; }
        }

        private sealed class SharedPartyHoistBranch
        {
            public VariantTreeNode ChildNode { get; set; }
            public VariantRenderItem DirectVariant { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
        }

        private sealed class SharedPromptContentGroup
        {
            public List<string> Lines { get; set; } = new List<string>();
            public List<VariantTreeNode> Children { get; set; } = new List<VariantTreeNode>();
            public int FirstChildIndex { get; set; } = int.MaxValue;
        }

        private sealed class PartyScanOutcomeBranch
        {
            public VariantRenderItem Variant { get; set; }
            public string Label { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
            public decimal OrderKey { get; set; }
            public bool HasAdjustedMemory { get; set; }
        }

        private sealed class PartyScanOutcomeSummary
        {
            public PathVariantInfo Variant { get; set; }
            public string Label { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
            public List<string> AggregateLines { get; set; } = new List<string>();
            public decimal OrderKey { get; set; }
            public bool HasCorrectText { get; set; }
            public bool HasAdjustedMemory { get; set; }
        }

private static string BuildHierarchicalVariantNotes(
    OvrObject obj,
    byte defaultRandomEncounterMonsterPowerCap,
    byte defaultRandomEncounterMonsterLevelCap,
    byte defaultRandomEncounterMonsterBatchCountCap,
    byte defaultDarkeningLevel,
    byte defaultRandomEncounterChance,
    string specialSpoilerLine,
    string inlineSpecialSpoilerLine)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count <= 1)
                return null;

            var rawVariantContents = BuildRawVariantContentsFromPathVariants(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
            if (CanBuildCollapsedCappedRandomPartialBattleNote(obj, rawVariantContents))
                return null;

            var items = BuildVariantRenderItems(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            if (items.Count <= 1)
                return null;

            items = ApplySingleMemberPartyScanDisplayModel(items);
            if (items.Count <= 1)
                return null;

            items = DeduplicateDisplayedVariantItems(items);
            items = ApplySingleMemberPartyScanDisplayModel(items);

            if (items.Count <= 1)
                return null;

            AppendLineToFirstMeaningfulVariantItem(items, specialSpoilerLine);

            if (TryBuildInventoryResourceAttritionRandomOutcomeHierarchy(items, out string resourceAttritionHierarchy))
                return resourceAttritionHierarchy;

            var groups = BuildTopLevelVariantGroups(obj, items);
            if (groups.Count == 0)
                return null;

            // Иерархический вывод нужен либо когда есть реальные choice-ветки,
            // либо когда хотя бы внутри одной смысловой группы остаётся
            // нетривиальный общий текстовый префикс. Это позволяет не терять
            // иерархию в случаях вида "no-op + несколько родственных исходов".
            bool hasMeaningfulChoiceHierarchy = items.Any(item =>
                GetRelevantBranchChoices(item?.Variant).Any());

            bool hasCommonPrefixHierarchy = groups.Any(group =>
                (group?.Items?.Count ?? 0) > 1 &&
                (group.TreeRoot?.CommonLines?.Any(line => !string.IsNullOrWhiteSpace(line)) ?? false));

            if (!hasMeaningfulChoiceHierarchy && !hasCommonPrefixHierarchy)
                return null;

            bool hasRealMultiplicity =
                groups.Count > 1 ||
                groups.Any(group =>
                    (group?.TreeRoot?.Children?.Any(IsRenderableStructuralNode) ?? false) ||
                    (CountRenderableDirectVariants(group?.TreeRoot) > 1));

            if (!hasRealMultiplicity)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            for (int i = 0; i < groups.Count; i++)
            {
                RenderTopLevelGroup(groups[i], sb, i + 1);
                if (i < groups.Count - 1)
                    sb.AppendLine();
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildFlatSemanticVariantNotes(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string specialSpoilerLine,
            string inlineSpecialSpoilerLine)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count <= 1)
                return null;

            var rawVariantContents = BuildRawVariantContentsFromPathVariants(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
            if (CanBuildCollapsedCappedRandomPartialBattleNote(obj, rawVariantContents))
                return null;

            var items = BuildVariantRenderItems(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            if (items.Count <= 1)
                return null;

            items = ApplySingleMemberPartyScanDisplayModel(items);
            if (items.Count <= 1)
                return null;

            items = DeduplicateDisplayedVariantItems(items);
            items = ApplySingleMemberPartyScanDisplayModel(items);

            if (items.Count <= 1)
                return null;

            AppendLineToFirstMeaningfulVariantItem(items, specialSpoilerLine);

            return TryBuildInventoryResourceAttritionRandomOutcomeModel(items, out var resourceAttritionModel)
                ? BuildInventoryResourceAttritionFlatNotes(resourceAttritionModel)
                : null;
        }

        private static List<VariantRenderItem> BuildVariantRenderItems(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var items = new List<VariantRenderItem>();
            if (obj?.PathVariants == null || obj.PathVariants.Count == 0)
                return items;

            foreach (var variant in obj.PathVariants.Values
                .Where(v => v != null)
                .OrderBy(GetPathOrderKey))
            {
                var variantObject = variant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte);
                var narrativeLines = DecodeNoteTexts(variant.Texts);
                var lines = BuildVariantLinesForHierarchy(
                    variantObject,
                    variant.Texts,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);

                lines = NumberLootBlockIfNeeded(lines) ?? new List<string>();

                if (ShouldSkipHierarchicalVariant(lines, narrativeLines))
                    continue;

                items.Add(new VariantRenderItem
                {
                    Variant = variant,
                    Lines = lines,
                    NarrativeLines = narrativeLines
                });
            }

            return items;
        }

        private static List<VariantRenderItem> ApplySingleMemberPartyScanDisplayModel(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();

            if (source.Count <= 1 || !HasPartyScanQuestionOutcomeContext(source))
                return source;

            var filtered = source
                .Where(item => !ShouldSuppressMultiMemberPartyScanAggregateVariant(item))
                .ToList();

            return filtered.Count == 0 ? source : filtered;
        }

        private static bool HasPartyScanQuestionOutcomeContext(IEnumerable<VariantRenderItem> items)
        {
            var list = (items ?? Enumerable.Empty<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();

            return list.Any(HasPartyScanAnswerText) &&
                   list.Any(item => item?.Variant?.PartyEffects?.Any(effect =>
                       effect != null && PartyEffectSemantics.IsLoopDerived(effect)) == true);
        }

        private static bool ShouldSuppressMultiMemberPartyScanAggregateVariant(VariantRenderItem item)
        {
            if (item?.Variant == null ||
                !HasPositivePostPartyScanStateGuard(item.Variant))
            {
                return false;
            }

            return !HasCorrectPartyScanOutcome(item);
        }

        private static bool HasPositivePostPartyScanStateGuard(PathVariantInfo variant)
        {
            return variant?.BranchChoices?.Any(IsPositivePostPartyScanStateGuardChoice) == true;
        }

        private static bool IsPositivePostPartyScanStateGuardChoice(BranchChoice choice)
        {
            if (choice == null)
                return false;

            string annotation = NormalizeHeaderAnnotation(choice.DisplayHeaderAnnotation);
            if (string.IsNullOrWhiteSpace(annotation))
                return false;

            return Regex.IsMatch(
                annotation,
                @"\]\s*(?:>|>=)\s*(?:0|0x00)\b|\]\s*!=\s*(?:0|0x00)\b",
                RegexOptions.IgnoreCase);
        }

        private static bool HasPartyScanAnswerText(VariantRenderItem item)
        {
            return GetPartyScanOutcomeLines(item)
                .Any(IsPartyScanAnswerLine);
        }

        private static bool HasCorrectPartyScanOutcome(VariantRenderItem item)
        {
            return HasCorrectPartyScanOutcome(null, GetPartyScanOutcomeLines(item));
        }

        private static IEnumerable<string> GetPartyScanOutcomeLines(VariantRenderItem item)
        {
            if (item == null)
                return Enumerable.Empty<string>();

            return (item.Lines ?? new List<string>())
                .Concat(item.NarrativeLines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line));
        }

        private static bool IsPartyScanAnswerLine(string line)
        {
            return IsCorrectPartyScanAnswerLine(line) ||
                   IsWrongPartyScanAnswerLine(line);
        }

        private static bool IsCorrectPartyScanAnswerLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return Regex.IsMatch(
                line.TrimStart(),
                @"^CORRECT\b",
                RegexOptions.IgnoreCase);
        }

        private static bool IsWrongPartyScanAnswerLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return Regex.IsMatch(
                line.TrimStart(),
                @"^WRONG\b",
                RegexOptions.IgnoreCase);
        }

        private static List<TopLevelVariantGroup> BuildTopLevelVariantGroups(
            OvrObject obj,
            List<VariantRenderItem> items)
        {
            var groups = items
                .GroupBy(item => BuildTopLevelGroupKey(item))
                .Select(g =>
                {
                    var ordered = g.OrderBy(GetVariantRenderOrderKey).ToList();
                    var first = ordered.FirstOrDefault();
                    var firstChoice = GetRelevantBranchChoices(first?.Variant).FirstOrDefault();
                    string firstChoiceKey = BuildChoiceDisplayKey(firstChoice);
                    string firstNarrativeLine = GetNarrativeRootLine(first);
                    bool groupedByChoice = string.IsNullOrWhiteSpace(firstNarrativeLine) &&
                        !string.IsNullOrWhiteSpace(firstChoiceKey);

                    return new TopLevelVariantGroup
                    {
                        Items = ordered,
                        Label = groupedByChoice ? NormalizeChoiceLabel(firstChoice?.Label) : null,
                        HeaderAnnotation = groupedByChoice ? NormalizeHeaderAnnotation(firstChoice?.DisplayHeaderAnnotation) : null,
                        ConsumedTopChoiceKey = groupedByChoice ? firstChoiceKey : null,
                        GroupedByChoice = groupedByChoice,
                        SourceOrderKey = GetTopLevelGroupSourceOrderKey(obj, ordered)
                    };
                })
                .ToList();

            foreach (var group in groups)
            {
                var root = BuildVariantTree(group.Items, group.GroupedByChoice ? group.ConsumedTopChoiceKey : null);
                IntroduceComplementaryInventoryPresenceBranches(root);
                ComputeCommonLines(root);
                IntroduceSharedLineHierarchy(root);
                AttachChoiceChildrenToSiblingPromptParents(root);
                IntroduceSharedPromptHierarchyFromChoiceChildContent(root);
                IntroduceSharedPromptHierarchyAcrossChoiceChildren(root);
                HoistSharedCommonPartyNotes(root);
                PromoteConditionalPartyNotesBeforeBattle(root);
                RemoveRedundantInheritedLines(root);
                MarkTransparentMixedSiblingGroups(root);
                CollapseTransparentDirectVariantWrappers(root);
                IntroduceSharedLineHierarchyAcrossHiddenTechnicalChildren(root);
                MarkTransparentMixedSiblingGroups(root);
                CollapseTransparentDirectVariantWrappers(root);
                RemoveRedundantInheritedLines(root);
                group.TreeRoot = PruneDecorativeChoiceLeaves(
                    FlattenTransparentStructuralWrappers(
                        SimplifyGenericChoiceTree(
                            CompressVariantTree(root))));
            }

            groups = groups
                .Where(HasRenderableTopLevelContent)
                .OrderByDescending(HasVisibleGroupedChoiceLabel)
                .ThenBy(g => g.SourceOrderKey)
                .ThenBy(g => g.Items.Min(GetVariantRenderOrderKey))
                .ToList();

            return groups;
        }

        private static bool HasVisibleGroupedChoiceLabel(TopLevelVariantGroup group)
        {
            return group?.GroupedByChoice == true &&
                   !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(group.Label));
        }

        private static int GetTopLevelGroupSourceOrderKey(
            OvrObject obj,
            IEnumerable<VariantRenderItem> items)
        {
            int minKey = int.MaxValue;

            foreach (var item in items ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (TryFindPathVariantKey(obj, item?.Variant, out int key))
                    minKey = Math.Min(minKey, key);
            }

            return minKey;
        }

        private enum ResourceAttritionKind
        {
            Food = 0,
            Endurance = 1,
            Dead = 2
        }

        private enum ResourceRandomOutcomeKind
        {
            Nothing = 0,
            RandomEncounter = 1,
            Whirlwind = 2,
            Sandstorm = 3
        }

        private sealed class InventoryResourceGroup
        {
            public string Label { get; set; }
            public string HeaderAnnotation { get; set; }
            public PartyPredicateComparison Comparison { get; set; }
            public List<string> NarrativePrefixLines { get; set; } = new List<string>();
            public Dictionary<ResourceAttritionKind, ResourceAttritionGroup> Resources { get; set; }
                = new Dictionary<ResourceAttritionKind, ResourceAttritionGroup>();
        }

        private sealed class ResourceAttritionGroup
        {
            public ResourceAttritionKind Kind { get; set; }
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public Dictionary<ResourceRandomOutcomeKind, ResourceRandomOutcomeGroup> Outcomes { get; set; }
                = new Dictionary<ResourceRandomOutcomeKind, ResourceRandomOutcomeGroup>();
        }

        private sealed class ResourceRandomOutcomeGroup
        {
            public ResourceRandomOutcomeKind Kind { get; set; }
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public decimal Probability { get; set; }
        }

        private sealed class InventoryResourceAttritionNoteModel
        {
            public List<string> RootLines { get; set; } = new List<string>();
            public List<InventoryResourceGroup> Groups { get; set; } = new List<InventoryResourceGroup>();
            public List<ResourceRandomOutcomeKind> OutcomeKinds { get; set; } = new List<ResourceRandomOutcomeKind>();
        }

        private static bool TryBuildInventoryResourceAttritionRandomOutcomeHierarchy(
            List<VariantRenderItem> items,
            out string hierarchy)
        {
            hierarchy = null;
            if (!TryBuildInventoryResourceAttritionRandomOutcomeModel(items, out var model))
                return false;

            var rootLines = model.RootLines;
            var groups = model.Groups;

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            sb.AppendLine("Вариант 1:");
            AppendIndentedDisplayLines(sb, "   ", rootLines, headerContainsProbability: false);
            sb.AppendLine();

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                string groupHeader = $"   Вариант 1.{groupIndex + 1}";
                string groupAnnotationText = BuildVariantHeaderAnnotationText(new[] { group.HeaderAnnotation });
                if (!string.IsNullOrWhiteSpace(groupAnnotationText))
                    groupHeader += $" ({groupAnnotationText})";
                string groupLabel = NormalizeUserVisibleChoiceLabel(group.Label);
                if (!string.IsNullOrWhiteSpace(groupLabel))
                    sb.AppendLine($"{groupHeader}: {groupLabel}");
                else
                    sb.AppendLine($"{groupHeader}:");

                var groupOnlyNarrativeLines = group.NarrativePrefixLines
                    .Skip(rootLines.Count)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (groupOnlyNarrativeLines.Count > 0)
                {
                    AppendIndentedDisplayLines(sb, "      ", groupOnlyNarrativeLines, headerContainsProbability: false);
                    sb.AppendLine();
                }

                int resourceIndex = 1;
                foreach (var resourceKind in GetResourceAttritionRenderOrder())
                {
                    var resourceGroup = group.Resources[resourceKind];
                    string resourceHeader = $"      Вариант 1.{groupIndex + 1}.{resourceIndex}";
                    string resourceGuardAnnotation = BuildResourceAttritionGuardHeaderAnnotation(resourceGroup);
                    if (!string.IsNullOrWhiteSpace(resourceGuardAnnotation))
                        resourceHeader += $" ({resourceGuardAnnotation})";
                    sb.AppendLine($"{resourceHeader}:");

                    var resourceLines = BuildResourceAttritionDisplayLines(resourceGroup);
                    var suppressedResourceLines = BuildAllResourceAttritionDisplayLines(resourceGroup);
                    AppendIndentedDisplayLines(sb, "         ", resourceLines, headerContainsProbability: false);

                    if (resourceLines.Count > 0)
                        sb.AppendLine();

                    var printedBeforeOutcome = new List<string>();
                    printedBeforeOutcome.AddRange(rootLines);
                    printedBeforeOutcome.AddRange(groupOnlyNarrativeLines);
                    printedBeforeOutcome.AddRange(suppressedResourceLines);

                    int outcomeIndex = 1;
                    var outcomeKinds = GetModelOutcomeKinds(model, resourceGroup);
                    int outcomeCount = outcomeKinds.Count;
                    foreach (var outcomeKind in outcomeKinds)
                    {
                        if (!resourceGroup.Outcomes.TryGetValue(outcomeKind, out var outcomeGroup))
                            continue;

                        string outcomeHeader = $"         Вариант 1.{groupIndex + 1}.{resourceIndex}.{outcomeIndex}";
                        string probabilityAnnotation = BuildRelativeOutcomeProbabilityHeaderAnnotation(resourceGroup, outcomeGroup);
                        if (!string.IsNullOrWhiteSpace(probabilityAnnotation))
                            outcomeHeader += $" ({probabilityAnnotation})";
                        outcomeHeader += ":";

                        sb.AppendLine(outcomeHeader);
                        bool headerContainsProbability = VariantHeaderContainsProbability(outcomeHeader);
                        var outcomeLines = BuildInventoryResourceOutcomeDisplayLines(outcomeGroup, printedBeforeOutcome);
                        AppendIndentedDisplayLines(sb, "            ", outcomeLines, headerContainsProbability);
                        outcomeIndex++;
                        if (outcomeIndex <= outcomeCount)
                            sb.AppendLine();
                    }

                    resourceIndex++;
                    if (resourceIndex <= 3)
                        sb.AppendLine();
                }

                if (groupIndex < groups.Count - 1)
                    sb.AppendLine();
            }

            hierarchy = sb.ToString().TrimEnd('\r', '\n');
            return true;
        }

        private static bool TryBuildInventoryResourceAttritionRandomOutcomeModel(
            List<VariantRenderItem> items,
            out InventoryResourceAttritionNoteModel model)
        {
            model = null;
            if (items == null || items.Count == 0)
                return false;

            var grouped = new Dictionary<string, InventoryResourceGroup>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!TryGetInventoryPresenceChoice(
                        item?.Variant,
                        out var itemChoice,
                        out string itemName,
                        out string presenceLabel))
                {
                    continue;
                }

                if (!TryClassifyResourceAttrition(item, out var resourceKind))
                    continue;

                string groupKey = $"{itemName}\n{presenceLabel}";
                var outcomeKind = ClassifyResourceRandomOutcome(item);
                if (!grouped.TryGetValue(groupKey, out var group))
                {
                    group = new InventoryResourceGroup
                    {
                        Label = null,
                        HeaderAnnotation = $"{itemName} {presenceLabel}",
                        Comparison = itemChoice.GuardPredicate?.Comparison ?? PartyPredicateComparison.Unknown,
                        NarrativePrefixLines = item?.NarrativeLines?.ToList() ?? new List<string>()
                    };
                    grouped[groupKey] = group;
                }
                else
                {
                    group.NarrativePrefixLines = CommonPrefix(
                        group.NarrativePrefixLines,
                        item?.NarrativeLines ?? new List<string>());
                }

                if (!group.Resources.TryGetValue(resourceKind, out var resourceGroup))
                {
                    resourceGroup = new ResourceAttritionGroup
                    {
                        Kind = resourceKind
                    };
                    group.Resources[resourceKind] = resourceGroup;
                }

                resourceGroup.Items.Add(item);

                if (!resourceGroup.Outcomes.TryGetValue(outcomeKind, out var outcomeGroup))
                {
                    outcomeGroup = new ResourceRandomOutcomeGroup
                    {
                        Kind = outcomeKind
                    };
                    resourceGroup.Outcomes[outcomeKind] = outcomeGroup;
                }

                decimal probability = GetVariantProbability(item?.Variant);
                outcomeGroup.Probability += probability;
                outcomeGroup.Items.Add(item);
            }

            var groups = grouped.Values
                .OrderBy(group => group.Comparison == PartyPredicateComparison.Equal ? 0 : 1)
                .ThenBy(group => group.HeaderAnnotation ?? group.Label, StringComparer.Ordinal)
                .ToList();

            if (groups.Count != 2 ||
                !TryGetSharedResourceAttritionOutcomeKinds(groups, out var outcomeKinds))
            {
                return false;
            }

            var rootLines = CommonPrefix(items
                .Select(item => item?.NarrativeLines ?? new List<string>())
                .ToList());
            if (rootLines.Count == 0)
                return false;

            model = new InventoryResourceAttritionNoteModel
            {
                RootLines = rootLines,
                Groups = groups,
                OutcomeKinds = outcomeKinds
            };
            return true;
        }

        private static string BuildInventoryResourceAttritionFlatNotes(InventoryResourceAttritionNoteModel model)
        {
            if (model == null || model.Groups == null || model.Groups.Count == 0)
                return null;

            var rootLines = model.RootLines ?? new List<string>();
            if (rootLines.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");

            int displayVariantNumber = 1;
            foreach (var group in model.Groups)
            {
                if (group == null)
                    continue;

                var groupOnlyNarrativeLines = (group.NarrativePrefixLines ?? new List<string>())
                    .Skip(rootLines.Count)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                foreach (var resourceKind in GetResourceAttritionRenderOrder())
                {
                    if (!group.Resources.TryGetValue(resourceKind, out var resourceGroup))
                        continue;

                    var resourceLines = BuildResourceAttritionDisplayLines(resourceGroup);
                    var suppressedResourceLines = BuildAllResourceAttritionDisplayLines(resourceGroup);
                    var printedBeforeOutcome = new List<string>();
                    printedBeforeOutcome.AddRange(rootLines);
                    printedBeforeOutcome.AddRange(groupOnlyNarrativeLines);
                    printedBeforeOutcome.AddRange(suppressedResourceLines);

                    foreach (var outcomeKind in GetModelOutcomeKinds(model, resourceGroup))
                    {
                        if (!resourceGroup.Outcomes.TryGetValue(outcomeKind, out var outcomeGroup))
                            continue;

                        string header = $"Вариант {displayVariantNumber++}";
                        var annotations = new List<string>();
                        if (!string.IsNullOrWhiteSpace(group.HeaderAnnotation))
                            annotations.Add(group.HeaderAnnotation);

                        string resourceGuardAnnotation = BuildResourceAttritionGuardHeaderAnnotation(resourceGroup);
                        if (!string.IsNullOrWhiteSpace(resourceGuardAnnotation))
                            annotations.Add(resourceGuardAnnotation);

                        string probabilityAnnotation = BuildRelativeOutcomeProbabilityHeaderAnnotation(resourceGroup, outcomeGroup);
                        if (!string.IsNullOrWhiteSpace(probabilityAnnotation))
                            annotations.Add(probabilityAnnotation);

                        string annotationText = BuildVariantHeaderAnnotationText(annotations);
                        if (!string.IsNullOrWhiteSpace(annotationText))
                            header += $" ({annotationText})";
                        string groupLabel = NormalizeUserVisibleChoiceLabel(group.Label);
                        if (!string.IsNullOrWhiteSpace(groupLabel))
                            header += $": {groupLabel}";
                        else
                            header += ":";

                        sb.AppendLine(header);
                        bool headerContainsProbability = VariantHeaderContainsProbability(header);

                        var lines = new List<string>();
                        lines.AddRange(rootLines);
                        lines.AddRange(groupOnlyNarrativeLines);
                        lines.AddRange(resourceLines);
                        lines.AddRange(BuildInventoryResourceOutcomeDisplayLines(outcomeGroup, printedBeforeOutcome));

                        AppendIndentedDisplayLines(sb, string.Empty, lines, headerContainsProbability);

                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static bool TryGetInventoryPresenceChoice(
            PathVariantInfo variant,
            out BranchChoice itemChoice,
            out string itemName,
            out string presenceLabel)
        {
            itemChoice = null;
            itemName = null;
            presenceLabel = null;
            itemChoice = GetRelevantBranchChoices(variant)
                .FirstOrDefault(choice => PartyInventorySemantics.TryBuildItemPresenceChoiceParts(choice, out _, out _));

            return itemChoice != null &&
                   PartyInventorySemantics.TryBuildItemPresenceChoiceParts(itemChoice, out itemName, out presenceLabel);
        }

        private static List<ResourceRandomOutcomeKind> GetModelOutcomeKinds(
            InventoryResourceAttritionNoteModel model,
            ResourceAttritionGroup resourceGroup)
        {
            if (model?.OutcomeKinds != null && model.OutcomeKinds.Count > 0)
                return model.OutcomeKinds;

            var presentKinds = new HashSet<ResourceRandomOutcomeKind>(
                (resourceGroup?.Outcomes ?? new Dictionary<ResourceRandomOutcomeKind, ResourceRandomOutcomeGroup>())
                .Where(kvp => kvp.Value != null && kvp.Value.Probability > 0 && kvp.Value.Items.Count > 0)
                .Select(kvp => kvp.Key));

            return GetResourceRandomOutcomeRenderOrder()
                .Where(presentKinds.Contains)
                .ToList();
        }

        private static bool TryClassifyResourceAttrition(
            VariantRenderItem item,
            out ResourceAttritionKind resourceKind)
        {
            resourceKind = ResourceAttritionKind.Food;
            var effects = item?.Variant?.PartyEffects ?? new List<PartyEffect>();

            if (effects.Any(IsDeadStatusOutcomeEffect))
            {
                resourceKind = ResourceAttritionKind.Dead;
                return true;
            }

            if (effects.Any(effect => PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.TempEndurance))
            {
                resourceKind = ResourceAttritionKind.Endurance;
                return true;
            }

            if (effects.Any(effect => PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Food))
            {
                resourceKind = ResourceAttritionKind.Food;
                return true;
            }

            return false;
        }

        private static bool IsDeadStatusOutcomeEffect(PartyEffect effect)
        {
            if (effect == null ||
                PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Status)
            {
                return false;
            }

            return (effect.Description?.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   (PartyEffectSemantics.BuildHumanDescription(effect)?.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private static ResourceRandomOutcomeKind ClassifyResourceRandomOutcome(VariantRenderItem item)
        {
            var lines = (item?.Lines ?? new List<string>())
                .Concat(item?.NarrativeLines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (lines.Any(line => line.IndexOf("SANDSTORM", StringComparison.OrdinalIgnoreCase) >= 0) ||
                item?.Variant?.PartyEffects?.Any(effect => PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Hp) == true)
            {
                return ResourceRandomOutcomeKind.Sandstorm;
            }

            if (lines.Any(line => line.IndexOf("WHIRLWIND", StringComparison.OrdinalIgnoreCase) >= 0) ||
                item?.Variant?.HasTeleportTarget == true)
            {
                return ResourceRandomOutcomeKind.Whirlwind;
            }

            if (item?.Variant?.CallsRandomEncounter == true ||
                lines.Any(line => line.IndexOf("random encounter", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return ResourceRandomOutcomeKind.RandomEncounter;
            }

            return ResourceRandomOutcomeKind.Nothing;
        }

        private static bool TryGetSharedResourceAttritionOutcomeKinds(
            List<InventoryResourceGroup> groups,
            out List<ResourceRandomOutcomeKind> outcomeKinds)
        {
            outcomeKinds = null;
            if (groups == null || groups.Count == 0)
                return false;

            HashSet<ResourceRandomOutcomeKind> sharedKinds = null;
            foreach (var group in groups)
            {
                if (!TryGetGroupResourceAttritionOutcomeKinds(group, out var groupKinds))
                    return false;

                if (sharedKinds == null)
                {
                    sharedKinds = groupKinds;
                    continue;
                }

                if (!sharedKinds.SetEquals(groupKinds))
                    return false;
            }

            if (sharedKinds == null || sharedKinds.Count == 0)
                return false;

            outcomeKinds = GetResourceRandomOutcomeRenderOrder()
                .Where(sharedKinds.Contains)
                .ToList();

            return outcomeKinds.Count > 0;
        }

        private static bool TryGetGroupResourceAttritionOutcomeKinds(
            InventoryResourceGroup group,
            out HashSet<ResourceRandomOutcomeKind> outcomeKinds)
        {
            outcomeKinds = null;
            if (group == null)
                return false;

            foreach (var resourceKind in GetResourceAttritionRenderOrder())
            {
                if (!group.Resources.TryGetValue(resourceKind, out var resourceGroup) ||
                    resourceGroup == null)
                {
                    return false;
                }

                var resourceOutcomeKinds = new HashSet<ResourceRandomOutcomeKind>(
                    resourceGroup.Outcomes
                        .Where(kvp =>
                            kvp.Value != null &&
                            kvp.Value.Probability > 0 &&
                            kvp.Value.Items.Count > 0)
                        .Select(kvp => kvp.Key));

                if (resourceOutcomeKinds.Count == 0)
                    return false;

                if (outcomeKinds == null)
                {
                    outcomeKinds = resourceOutcomeKinds;
                    continue;
                }

                if (!outcomeKinds.SetEquals(resourceOutcomeKinds))
                    return false;
            }

            return outcomeKinds != null && outcomeKinds.Count > 0;
        }

        private static IEnumerable<ResourceAttritionKind> GetResourceAttritionRenderOrder()
        {
            yield return ResourceAttritionKind.Food;
            yield return ResourceAttritionKind.Endurance;
            yield return ResourceAttritionKind.Dead;
        }

        private static IEnumerable<ResourceRandomOutcomeKind> GetResourceRandomOutcomeRenderOrder()
        {
            yield return ResourceRandomOutcomeKind.Nothing;
            yield return ResourceRandomOutcomeKind.RandomEncounter;
            yield return ResourceRandomOutcomeKind.Whirlwind;
            yield return ResourceRandomOutcomeKind.Sandstorm;
        }

        private static string BuildRelativeOutcomeProbabilityHeaderAnnotation(
            ResourceAttritionGroup resourceGroup,
            ResourceRandomOutcomeGroup outcomeGroup)
        {
            if (resourceGroup == null || outcomeGroup == null)
                return null;

            decimal total = resourceGroup.Outcomes.Values.Sum(outcome => outcome?.Probability ?? 0m);
            if (total <= 0)
                return null;

            decimal percent = 100m * outcomeGroup.Probability / total;
            string percentText = FormatSemanticRandomOutcomeProbability(percent);
            return PrefixProbabilityWordInParentheses(percentText + "%");
        }

        private static string BuildResourceAttritionGuardHeaderAnnotation(ResourceAttritionGroup resourceGroup)
        {
            if (resourceGroup == null)
                return null;

            var predicates = resourceGroup.Items
                .SelectMany(item => GetResourceAttritionEffects(item, resourceGroup.Kind))
                .SelectMany(effect => PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                .Where(predicate => predicate != null)
                .Where(predicate => !PartyInventorySemantics.IsInventoryItemPresencePredicate(predicate))
                .Where(predicate => IsResourceAttritionGuardPredicate(predicate, resourceGroup.Kind))
                .ToList();

            if (predicates.Count == 0)
                return null;

            return BuildGuardHeaderAnnotation(
                PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(predicates));
        }

        private static bool IsResourceAttritionGuardPredicate(
            PartyPredicate predicate,
            ResourceAttritionKind resourceKind)
        {
            if (predicate == null)
                return false;

            return resourceKind switch
            {
                ResourceAttritionKind.Food =>
                    predicate.Field == PartyFieldKind.Food,
                ResourceAttritionKind.Endurance =>
                    predicate.Field == PartyFieldKind.Food ||
                    predicate.Field == PartyFieldKind.TempEndurance,
                ResourceAttritionKind.Dead =>
                    predicate.Field == PartyFieldKind.Food ||
                    predicate.Field == PartyFieldKind.TempEndurance,
                _ => false
            };
        }

        private static List<string> BuildResourceAttritionDisplayLines(ResourceAttritionGroup resourceGroup)
        {
            if (resourceGroup == null)
                return new List<string>();

            string semanticLine = BuildResourceAttritionSemanticDisplayLine(resourceGroup.Kind);
            if (!string.IsNullOrWhiteSpace(semanticLine))
                return new List<string> { semanticLine };

            var lines = BuildAllResourceAttritionDisplayLines(resourceGroup);
            return ChooseResourceAttritionSummaryLines(lines);
        }

        private static string BuildResourceAttritionSemanticDisplayLine(ResourceAttritionKind resourceKind)
        {
            return resourceKind switch
            {
                ResourceAttritionKind.Food =>
                    "У партии персонажей уменьшается FOOD на 1",
                ResourceAttritionKind.Endurance =>
                    InlineNoteStyleCodec.EncodeAggregateTemporaryStatText(
                        "У партии персонажей уменьшается временная выносливость (ENDURANCE) на 1"),
                ResourceAttritionKind.Dead =>
                    "CONDITION персонажа(ей) в партии изменяется на DEAD",
                _ => null
            };
        }

        private static List<string> BuildAllResourceAttritionDisplayLines(ResourceAttritionGroup resourceGroup)
        {
            if (resourceGroup == null)
                return new List<string>();

            return resourceGroup.Items
                .SelectMany(item => GetResourceAttritionEffectLines(item, resourceGroup.Kind))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .GroupBy(line => line, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
        }

        private static List<string> ChooseResourceAttritionSummaryLines(List<string> lines)
        {
            if (lines == null || lines.Count <= 1)
                return lines ?? new List<string>();

            string aggregateLine = lines.FirstOrDefault(line =>
                line.IndexOf("всех персонажей", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(aggregateLine))
                return new List<string> { aggregateLine };

            string nonSingleMemberLine = lines.FirstOrDefault(line =>
                line.IndexOf("персонаж #", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("персонажа #", StringComparison.OrdinalIgnoreCase) < 0);
            if (!string.IsNullOrWhiteSpace(nonSingleMemberLine))
                return new List<string> { nonSingleMemberLine };

            return new List<string> { lines[0] };
        }

        private static List<string> GetResourceAttritionEffectLines(
            VariantRenderItem item,
            ResourceAttritionKind resourceKind)
        {
            var effects = (item?.Variant?.PartyEffects ?? new List<PartyEffect>())
                .Where(effect => IsResourceAttritionEffect(effect, resourceKind))
                .ToList();

            return GetOrderedDisplayablePartyEffects(effects)
                .Select(effect => BuildPartyEffectDisplayDescription(effect, item?.Variant))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<PartyEffect> GetResourceAttritionEffects(
            VariantRenderItem item,
            ResourceAttritionKind resourceKind)
        {
            return (item?.Variant?.PartyEffects ?? new List<PartyEffect>())
                .Where(effect => IsResourceAttritionEffect(effect, resourceKind));
        }

        private static bool IsResourceAttritionEffect(PartyEffect effect, ResourceAttritionKind resourceKind)
        {
            if (effect == null || !PartyEffectSemantics.IsStateChanging(effect))
                return false;

            return resourceKind switch
            {
                ResourceAttritionKind.Food =>
                    PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Food,
                ResourceAttritionKind.Endurance =>
                    PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.TempEndurance,
                ResourceAttritionKind.Dead =>
                    IsDeadStatusOutcomeEffect(effect),
                _ => false
            };
        }

        private static List<string> BuildInventoryResourceOutcomeDisplayLines(
            ResourceRandomOutcomeGroup outcomeGroup,
            List<string> printedBeforeOutcome)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in OrderDirectVariants(outcomeGroup?.Items ?? new List<VariantRenderItem>()))
            {
                foreach (var line in RemoveAlreadyDisplayedLines(item?.Lines, printedBeforeOutcome))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (seen.Add(line))
                        result.Add(line);
                }
            }

            if (result.Count == 0)
                result.Add("Ничего не происходит");

            return result;
        }

        private static List<string> RemoveAlreadyDisplayedLines(
            IEnumerable<string> lines,
            IEnumerable<string> displayedLines)
        {
            var pendingCounts = BuildLineCounts(
                (displayedLines ?? Enumerable.Empty<string>())
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
            var result = new List<string>();

            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(line) &&
                    pendingCounts.TryGetValue(line, out int remaining) &&
                    remaining > 0)
                {
                    if (remaining == 1)
                        pendingCounts.Remove(line);
                    else
                        pendingCounts[line] = remaining - 1;
                    continue;
                }

                result.Add(line);
            }

            return result;
        }

        private static string FormatSemanticRandomOutcomeProbability(decimal percent)
        {
            if (percent <= 0)
                return "0";

            decimal rounded = Math.Round(percent, 1, MidpointRounding.AwayFromZero);
            if (rounded <= 0)
                return ProbabilityFormatter.FormatPercent((double)percent);

            return ProbabilityFormatter.FormatPercent((double)rounded);
        }

        private static decimal GetVariantProbability(PathVariantInfo variant)
        {
            if (variant == null)
                return 0m;

            int numerator = Math.Max(0, variant.ProbabilityNumerator);
            int denominator = Math.Max(1, variant.ProbabilityDenominator);
            return numerator / (decimal)denominator;
        }

        private static List<string> CommonPrefix(List<string> left, List<string> right)
        {
            var result = new List<string>();
            if (left == null || right == null)
                return result;

            int count = Math.Min(left.Count, right.Count);
            for (int i = 0; i < count; i++)
            {
                string leftLine = left[i] ?? string.Empty;
                string rightLine = right[i] ?? string.Empty;
                if (!string.Equals(leftLine, rightLine, StringComparison.Ordinal))
                    break;

                result.Add(leftLine);
            }

            return result;
        }

        private static List<string> CommonPrefix(List<List<string>> lineSets)
        {
            if (lineSets == null || lineSets.Count == 0)
                return new List<string>();

            var result = lineSets[0]?.ToList() ?? new List<string>();
            foreach (var lines in lineSets.Skip(1))
                result = CommonPrefix(result, lines ?? new List<string>());

            return result;
        }


        private static bool HasRenderableTopLevelContent(TopLevelVariantGroup group)
        {
            return IsRenderableStructuralNode(group?.TreeRoot);
        }

        private static VariantRenderItem GetSingleLeafVariantItem(VariantTreeNode node)
        {
            var variants = GetAllVariants(node)
                .Where(item => item != null)
                .ToList();

            return variants.Count == 1
                ? variants[0]
                : null;
        }

        private static bool IsRenderableStructuralNode(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)))
                return true;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node.HeaderAnnotation)))
                return true;

            if ((node.CommonLines?.Count ?? 0) > 0)
                return true;

            if (node.Children != null && node.Children.Any(IsRenderableStructuralNode))
                return true;

            return node.DirectVariants != null && node.DirectVariants.Any(IsRenderableDirectVariant);
        }

        private static bool IsRenderableDirectVariant(VariantRenderItem item)
        {
            if (item == null)
                return false;

            if ((item.Lines?.Count ?? 0) > 0)
                return true;

            if (item.Variant?.HasProbabilityInfo == true)
                return true;

            if (ShouldDisplayGuardCondition(item.Variant))
                return true;

            return false;
        }

        private static bool IsEmptyOrNoOpVariant(VariantRenderItem item)
        {
            if (item == null)
                return false;

            return item.Lines == null || item.Lines.Count == 0 || IsNoOpOnly(item.Lines);
        }

        private static bool ShouldRenderDirectVariant(
            VariantRenderItem item,
            int siblingDirectVariantCount,
            int renderableChildCount,
            bool suppressEmptyPromptTerminalDirectVariants = false)
        {
            if (item == null)
                return false;

            if (IsRenderableDirectVariant(item))
                return true;

            if (!IsEmptyOrNoOpVariant(item))
                return false;

            if (suppressEmptyPromptTerminalDirectVariants)
                return false;

            return renderableChildCount > 0 || siblingDirectVariantCount > 1;
        }

        private static bool ShouldSuppressEmptyPromptTerminalDirectVariants(
            VariantTreeNode node,
            int renderableChildCount)
        {
            if (node == null || renderableChildCount <= 0)
                return false;

            return HasMultiNumericPromptOptions(node.CommonLines);
        }

        private static bool HasMultiNumericPromptOptions(IEnumerable<string> lines)
        {
            var optionTokens = ExtractPromptOptionLabels(string.Join("\n", lines ?? Enumerable.Empty<string>()))
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label.EndsWith(")", StringComparison.Ordinal)
                    ? label.Substring(0, label.Length - 1).Trim()
                    : label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return optionTokens.Count >= 3 &&
                   optionTokens.All(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        }

        private static int CountRenderableDirectVariants(VariantTreeNode node)
        {
            if (node == null)
                return 0;

            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            int renderableChildCount = node.Children?.Count(IsRenderableStructuralNode) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(node, renderableChildCount);

            return (node.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Count(v => ShouldRenderDirectVariant(
                    v,
                    siblingDirectVariantCount,
                    renderableChildCount,
                    suppressEmptyPromptTerminalDirectVariants));
        }

        private static bool IsNoOpTopLevelGroup(TopLevelVariantGroup group)
        {
            if (group?.TreeRoot == null)
                return false;

            bool hasChildren = (group.TreeRoot.Children?.Count ?? 0) > 0;
            bool hasCommon = (group.TreeRoot.CommonLines?.Count ?? 0) > 0;
            if (hasChildren || hasCommon)
                return false;

            var variants = group.TreeRoot.DirectVariants ?? new List<VariantRenderItem>();
            if (variants.Count != 1)
                return false;

            return IsNoOpOnly(variants[0]?.Lines);
        }

        private static bool IsNoOpOnly(List<string> lines)
        {
            if (lines == null)
                return false;

            var normalized = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToList();

            return normalized.Count == 1 &&
                   string.Equals(normalized[0], "Ничего не происходит (не выполнены условия для наступления ни одного варианта)", StringComparison.Ordinal);
        }

        private static string BuildTopLevelGroupKey(VariantRenderItem item)
        {
            string narrativeRootLine = GetNarrativeRootLine(item);
            if (!string.IsNullOrWhiteSpace(narrativeRootLine))
                return "LINE|" + narrativeRootLine;

            var firstChoice = GetRelevantBranchChoices(item?.Variant).FirstOrDefault();
            string firstChoiceKey = BuildChoiceDisplayKey(firstChoice);
            if (!string.IsNullOrWhiteSpace(firstChoiceKey))
                return "CHOICE|" + firstChoiceKey;

            if (item?.Lines == null || item.Lines.Count == 0)
                return "<NO_LINES>";

            return "LINE|" + string.Join("\n---\n", item.Lines.Take(1));
        }

        private static string GetNarrativeRootLine(VariantRenderItem item)
        {
            if (item?.NarrativeLines == null)
                return null;

            return item.NarrativeLines
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                ?.Trim();
        }

        private static VariantTreeNode BuildVariantTree(List<VariantRenderItem> items, string consumedTopChoiceKey = null)
        {
            var root = new VariantTreeNode();

            foreach (var item in items)
            {
                var current = root;
                var choices = GetRelevantBranchChoices(item?.Variant).ToList();
                if (!string.IsNullOrWhiteSpace(consumedTopChoiceKey) &&
                    choices.Count > 0 &&
                    string.Equals(BuildChoiceDisplayKey(choices[0]), consumedTopChoiceKey, StringComparison.Ordinal))
                {
                    choices = choices.Skip(1).ToList();
                }

                for (int i = 0; i < choices.Count; i++)
                {
                    var choice = choices[i];
                    string key = BuildChoiceKey(choice, i);
                    var child = current.Children.FirstOrDefault(c => c.SegmentKey == key);
                    if (child == null)
                    {
                        child = new VariantTreeNode
                        {
                            SegmentKey = key,
                            Label = NormalizeChoiceLabel(choice?.Label),
                            HeaderAnnotation = NormalizeHeaderAnnotation(choice?.DisplayHeaderAnnotation)
                        };
                        current.Children.Add(child);
                    }
                    else if (string.IsNullOrWhiteSpace(child.Label))
                    {
                        child.Label = NormalizeChoiceLabel(choice?.Label);
                    }

                    if (string.IsNullOrWhiteSpace(child.HeaderAnnotation))
                        child.HeaderAnnotation = NormalizeHeaderAnnotation(choice?.DisplayHeaderAnnotation);

                    current = child;
                }

                current.DirectVariants.Add(item);
            }

            return root;
        }

        private static void IntroduceComplementaryInventoryPresenceBranches(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children?.ToList() ?? new List<VariantTreeNode>())
                IntroduceComplementaryInventoryPresenceBranches(child);

            if ((node.DirectVariants?.Count ?? 0) == 0 ||
                (node.Children?.Count ?? 0) == 0)
            {
                return;
            }

            if (!TryFindSingleInventoryPresenceChildSide(
                    node,
                    out string itemName,
                    out string observedPresenceLabel))
            {
                return;
            }

            if (!TryGetOppositeInventoryPresenceLabel(observedPresenceLabel, out string oppositePresenceLabel))
                return;

            var directVariantsToMove = node.DirectVariants
                .Where(item => item != null && !VariantHasInventoryPresenceChoiceForItem(item.Variant, itemName))
                .ToList();

            if (directVariantsToMove.Count == 0)
                return;

            string inferredHeaderAnnotation = NormalizeHeaderAnnotation($"{itemName} {oppositePresenceLabel}");
            var complementNode = node.Children.FirstOrDefault(child =>
                child != null &&
                string.IsNullOrWhiteSpace(child.Label) &&
                string.Equals(
                    NormalizeHeaderAnnotation(child.HeaderAnnotation),
                    inferredHeaderAnnotation,
                    StringComparison.Ordinal));

            if (complementNode == null)
            {
                complementNode = new VariantTreeNode
                {
                    SegmentKey = $"INVENTORY_COMPLEMENT|{itemName}|{oppositePresenceLabel}",
                    HeaderAnnotation = inferredHeaderAnnotation
                };
                node.Children.Add(complementNode);
            }

            foreach (var item in directVariantsToMove)
                complementNode.DirectVariants.Add(item);

            var moved = new HashSet<VariantRenderItem>(directVariantsToMove);
            node.DirectVariants = node.DirectVariants
                .Where(item => item != null && !moved.Contains(item))
                .ToList();
        }

        private static bool TryFindSingleInventoryPresenceChildSide(
            VariantTreeNode node,
            out string itemName,
            out string presenceLabel)
        {
            itemName = null;
            presenceLabel = null;

            var candidates = (node?.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .Select(child =>
                    TrySplitInventoryPresenceHeaderAnnotation(
                        child?.HeaderAnnotation,
                        out string childItemName,
                        out string childPresenceLabel)
                        ? new { ItemName = childItemName, PresenceLabel = childPresenceLabel }
                        : null)
                .Where(candidate => candidate != null)
                .GroupBy(candidate => candidate.ItemName, StringComparer.Ordinal)
                .Select(group =>
                {
                    var presenceLabels = group
                        .Select(candidate => candidate.PresenceLabel)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    return new
                    {
                        ItemName = group.Key,
                        PresenceLabels = presenceLabels
                    };
                })
                .Where(candidate => candidate.PresenceLabels.Count == 1)
                .ToList();

            if (candidates.Count != 1)
                return false;

            itemName = candidates[0].ItemName;
            presenceLabel = candidates[0].PresenceLabels[0];
            return !string.IsNullOrWhiteSpace(itemName) &&
                   !string.IsNullOrWhiteSpace(presenceLabel);
        }

        private static bool VariantHasInventoryPresenceChoiceForItem(
            PathVariantInfo variant,
            string itemName)
        {
            if (variant == null || string.IsNullOrWhiteSpace(itemName))
                return false;

            return GetInventoryPresenceChoiceInfos(variant)
                .Any(info => string.Equals(info.ItemName, itemName, StringComparison.Ordinal));
        }

        private static IEnumerable<BranchChoice> GetRelevantBranchChoices(PathVariantInfo variant)
        {
            if (variant?.BranchChoices == null)
                yield break;

            var candidates = new List<BranchChoiceDisplayCandidate>();
            foreach (var choice in variant.BranchChoices)
            {
                var normalized = NormalizeBranchChoiceForDisplay(choice);
                if (normalized != null)
                {
                    candidates.Add(new BranchChoiceDisplayCandidate
                    {
                        Raw = choice,
                        Normalized = normalized
                    });
                }
            }

            var seenInventoryPresenceItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in CollapseFlagPropagatedInputChoiceDuplicates(candidates))
            {
                if (candidate?.Normalized == null)
                    continue;

                if (TryBuildInventoryPresenceChoiceInfo(candidate.Normalized, out var itemPresenceInfo) &&
                    !seenInventoryPresenceItems.Add(itemPresenceInfo.ItemName))
                {
                    continue;
                }

                yield return candidate.Normalized;
            }
        }

        private static List<InventoryPresenceChoiceInfo> GetInventoryPresenceChoiceInfos(PathVariantInfo variant)
        {
            return GetRelevantBranchChoices(variant)
                .Select(choice => TryBuildInventoryPresenceChoiceInfo(choice, out var info) ? info : null)
                .Where(info => info != null)
                .GroupBy(info => info.HeaderAnnotation, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(info => info.ItemName, StringComparer.Ordinal)
                .ThenBy(info => info.PresenceLabel, StringComparer.Ordinal)
                .ToList();
        }

        private static bool TryBuildInventoryPresenceChoiceInfo(
            BranchChoice choice,
            out InventoryPresenceChoiceInfo info)
        {
            info = null;
            if (!PartyInventorySemantics.TryBuildItemPresenceChoiceParts(
                    choice,
                    out string itemName,
                    out string presenceLabel))
            {
                return false;
            }

            itemName = itemName?.Trim();
            presenceLabel = presenceLabel?.Trim();
            if (string.IsNullOrWhiteSpace(itemName) ||
                string.IsNullOrWhiteSpace(presenceLabel))
            {
                return false;
            }

            info = new InventoryPresenceChoiceInfo
            {
                ItemName = itemName,
                PresenceLabel = presenceLabel,
                HeaderAnnotation = NormalizeHeaderAnnotation($"{itemName} {presenceLabel}")
            };
            return true;
        }

        private static bool TrySplitInventoryPresenceHeaderAnnotation(
            string annotation,
            out string itemName,
            out string presenceLabel)
        {
            itemName = null;
            presenceLabel = null;

            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            foreach (string label in new[] { "есть", "отсутствует" })
            {
                string suffix = " " + label;
                if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                itemName = normalized.Substring(0, normalized.Length - suffix.Length).Trim();
                presenceLabel = label;
                return !string.IsNullOrWhiteSpace(itemName);
            }

            return false;
        }

        private static bool TryGetOppositeInventoryPresenceLabel(
            string presenceLabel,
            out string oppositePresenceLabel)
        {
            oppositePresenceLabel = null;
            if (string.Equals(presenceLabel, "есть", StringComparison.Ordinal))
            {
                oppositePresenceLabel = "отсутствует";
                return true;
            }

            if (string.Equals(presenceLabel, "отсутствует", StringComparison.Ordinal))
            {
                oppositePresenceLabel = "есть";
                return true;
            }

            return false;
        }

        private static List<BranchChoiceDisplayCandidate> CollapseFlagPropagatedInputChoiceDuplicates(
            List<BranchChoiceDisplayCandidate> choices)
        {
            var result = new List<BranchChoiceDisplayCandidate>();

            foreach (var choice in choices ?? new List<BranchChoiceDisplayCandidate>())
            {
                if (choice?.Normalized == null)
                    continue;

                if (result.Count > 0 &&
                    ShouldReplacePreviousInputChoiceWithFlagChoice(result[result.Count - 1], choice))
                {
                    result[result.Count - 1] = choice;
                    continue;
                }

                result.Add(choice);
            }

            return result;
        }

        private static bool ShouldReplacePreviousInputChoiceWithFlagChoice(
            BranchChoiceDisplayCandidate previous,
            BranchChoiceDisplayCandidate current)
        {
            if (previous?.Raw == null || previous.Normalized == null ||
                current?.Raw == null || current.Normalized == null)
            {
                return false;
            }

            if (!HasExplicitInputChoiceHint(previous.Raw.Label))
                return false;

            if (HasExplicitInputChoiceHint(current.Raw.Label) ||
                !IsGenericTechnicalChoiceLabel(current.Raw.Label))
            {
                return false;
            }

            if (!IsBinaryChoiceDisplayLabel(previous.Normalized.Label) ||
                !IsBinaryChoiceDisplayLabel(current.Normalized.Label))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(previous.Normalized.CompareRegister) ||
                string.IsNullOrWhiteSpace(current.Normalized.CompareRegister) ||
                !string.Equals(previous.Normalized.CompareRegister, current.Normalized.CompareRegister, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!previous.Normalized.CompareValue.HasValue ||
                !current.Normalized.CompareValue.HasValue ||
                previous.Normalized.CompareValue.Value != current.Normalized.CompareValue.Value)
            {
                return false;
            }

            string previousMnemonic = ExtractChoiceMnemonic(previous.Normalized.Condition);
            string currentMnemonic = ExtractChoiceMnemonic(current.Normalized.Condition);
            return !string.IsNullOrWhiteSpace(previousMnemonic) &&
                   string.Equals(previousMnemonic, currentMnemonic, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBinaryChoiceDisplayLabel(string label)
        {
            string normalized = NormalizeChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(normalized) || !normalized.EndsWith(")", StringComparison.Ordinal))
                return false;

            string token = normalized.Substring(0, normalized.Length - 1).Trim();
            return string.Equals(token, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "N", StringComparison.OrdinalIgnoreCase);
        }

        private static BranchChoice NormalizeBranchChoiceForDisplay(BranchChoice choice)
        {
            if (choice == null)
                return null;

            string rawLabel = string.IsNullOrWhiteSpace(choice.Label)
                ? null
                : choice.Label.Trim();

            bool rawLabelIsTechnical = IsGenericTechnicalChoiceLabel(rawLabel);

            string displayHeaderAnnotation = NormalizeHeaderAnnotation(choice.DisplayHeaderAnnotation);
            string inferredLabel;
            if (PartyInventorySemantics.TryBuildItemPresenceChoiceParts(
                    choice,
                    out string itemPresenceName,
                    out string itemPresenceLabel))
            {
                inferredLabel = null;
                displayHeaderAnnotation = $"{itemPresenceName} {itemPresenceLabel}";
            }
            else
            {
                inferredLabel = InferChoiceLabel(choice);
            }

            string label = rawLabelIsTechnical
                ? inferredLabel
                : (string.IsNullOrWhiteSpace(rawLabel) ? inferredLabel : rawLabel);

            if (string.IsNullOrWhiteSpace(label) &&
                string.IsNullOrWhiteSpace(displayHeaderAnnotation))
            {
                return null;
            }

            return new BranchChoice
            {
                Label = label,
                DisplayHeaderAnnotation = NormalizeHeaderAnnotation(displayHeaderAnnotation),
                Condition = choice.Condition,
                CompareValue = choice.CompareValue,
                CompareRegister = choice.CompareRegister,
                CompareMemoryAddress = choice.CompareMemoryAddress,
                IsLinear = choice.IsLinear,
                ComparedPartyField = choice.ComparedPartyField?.Clone(),
                GuardPredicate = choice.GuardPredicate?.Clone()
            };
        }

        private static string InferChoiceLabel(BranchChoice choice)
        {
            if (choice == null)
                return null;

            string splitLabel = InferSplitChoiceLabel(choice.Condition);
            if (!string.IsNullOrWhiteSpace(splitLabel))
                return splitLabel;

            if (!string.Equals(choice.CompareRegister, "AL", StringComparison.OrdinalIgnoreCase) ||
                !choice.CompareValue.HasValue)
            {
                return null;
            }

            string mnemonic = ExtractChoiceMnemonic(choice.Condition);
            byte value = choice.CompareValue.Value;
            bool hasExplicitInputChoiceHint = HasExplicitInputChoiceHint(choice.Label);
            bool isBinaryChoiceValue = value == (byte)'Y' || value == (byte)'N';

            if (!choice.IsLinear)
            {
                if (string.Equals(mnemonic, "JE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mnemonic, "JZ", StringComparison.OrdinalIgnoreCase))
                {
                    return hasExplicitInputChoiceHint || isBinaryChoiceValue
                        ? ConvertChoiceValueToLabel(value)
                        : null;
                }

                return null;
            }

            if (string.Equals(mnemonic, "JNE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mnemonic, "JNZ", StringComparison.OrdinalIgnoreCase))
            {
                // LINEAR after JNE/JNZ = ветка равенства, то есть выбран именно CompareValue.
                return hasExplicitInputChoiceHint || isBinaryChoiceValue
                    ? ConvertChoiceValueToLabel(value)
                    : null;
            }

            if (string.Equals(mnemonic, "JE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mnemonic, "JZ", StringComparison.OrdinalIgnoreCase))
            {
                // LINEAR after JE/JZ = ветка неравенства.
                // Осмысленную метку восстанавливаем только для бинарных Y/N вопросов.
                return GetBinaryOppositeChoiceLabel(value);
            }

            return null;
        }

        private static string InferSplitChoiceLabel(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                return null;

            var match = Regex.Match(
                condition.Trim(),
                @"^SPLIT\s+\[[^\]]+\]\s*=\s*0x([0-9A-Fa-f]{1,2})$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            if (!byte.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                return null;

            return ConvertChoiceValueToLabel(value);
        }

        private static bool HasExplicitInputChoiceHint(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string normalized = label.Trim();
            if (normalized.EndsWith(")", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            return string.Equals(normalized, "InputChoiceBranch", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "InputChoiceLinear", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetBinaryOppositeChoiceLabel(byte value)
        {
            switch (char.ToUpperInvariant((char)value))
            {
                case 'Y':
                    return "N";
                case 'N':
                    return "Y";
                default:
                    return null;
            }
        }

        private static string ExtractChoiceMnemonic(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                return null;

            string trimmed = condition.Trim();
            const string linearPrefix = "LINEAR after ";
            if (trimmed.StartsWith(linearPrefix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(linearPrefix.Length).TrimStart();

            int spaceIndex = trimmed.IndexOf(' ');
            return spaceIndex >= 0 ? trimmed.Substring(0, spaceIndex).Trim() : trimmed;
        }

        private static string ConvertChoiceValueToLabel(byte value)
        {
            if (value == 0x1B)
                return "ESC";

            if (value >= 0x20 && value <= 0x7E)
                return ((char)value).ToString();

            return null;
        }

        private static string BuildChoiceKey(BranchChoice choice, int index)
        {
            if (choice == null)
                return $"{index}:<null>";

            return string.Join("|",
                index,
                choice.DisplayHeaderAnnotation ?? string.Empty,
                choice.Label ?? string.Empty,
                choice.CompareRegister ?? string.Empty,
                choice.CompareValue?.ToString() ?? string.Empty);
        }

        private static string BuildChoiceDisplayKey(BranchChoice choice)
        {
            if (choice == null)
                return null;

            string annotation = NormalizeHeaderAnnotation(choice.DisplayHeaderAnnotation);
            string label = NormalizeChoiceLabel(choice.Label);
            if (string.IsNullOrWhiteSpace(annotation) && string.IsNullOrWhiteSpace(label))
                return null;

            return string.Join("|", annotation ?? string.Empty, label ?? string.Empty);
        }

        private static string BuildUserVisibleChoiceDisplayKey(BranchChoice choice)
        {
            if (choice == null)
                return null;

            string annotation = NormalizeUserVisibleHeaderAnnotation(choice.DisplayHeaderAnnotation);
            string label = NormalizeUserVisibleChoiceLabel(choice.Label);
            if (string.IsNullOrWhiteSpace(annotation) && string.IsNullOrWhiteSpace(label))
                return null;

            return string.Join("|", annotation ?? string.Empty, label ?? string.Empty);
        }


        private static VariantTreeNode SimplifyGenericChoiceTree(VariantTreeNode node)
        {
            if (node == null)
                return null;

            for (int i = 0; i < node.Children.Count; i++)
                node.Children[i] = SimplifyGenericChoiceTree(node.Children[i]);

            node.Children = node.Children
                .Where(IsRenderableStructuralNode)
                .ToList();

            if (IsGenericTechnicalChoiceLabel(node.Label))
                node.Label = null;

            while (MergeRedundantSameLabelChildren(node))
            {
                node.Children = node.Children
                    .Where(IsRenderableStructuralNode)
                    .ToList();
            }

            while (node.Children.Count == 1 &&
                   node.DirectVariants.Count == 0 &&
                   node.CommonLines.Count == 0 &&
                   string.IsNullOrWhiteSpace(node.Label))
            {
                node = node.Children[0];
            }

            while (node.Children.Count == 1 &&
                   node.DirectVariants.Count == 0 &&
                   node.CommonLines.Count == 0 &&
                   !string.IsNullOrWhiteSpace(node.Label) &&
                   string.Equals(node.Children[0]?.Label, node.Label, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       NormalizeHeaderAnnotation(node.Children[0]?.HeaderAnnotation) ?? string.Empty,
                       NormalizeHeaderAnnotation(node.HeaderAnnotation) ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase))
            {
                node = node.Children[0];
            }

            return node;
        }

        private static VariantTreeNode PruneDecorativeChoiceLeaves(VariantTreeNode node)
        {
            if (node == null)
                return null;

            for (int i = 0; i < node.Children.Count; i++)
                node.Children[i] = PruneDecorativeChoiceLeaves(node.Children[i]);

            node.Children = node.Children
                .Where(IsRenderableStructuralNode)
                .ToList();

            if (node.Children.Count > 0 &&
                !HasIntrinsicRenderableDirectVariant(node) &&
                node.Children.All(IsDecorativeChoicePlaceholderLeaf))
            {
                node.Children.Clear();
            }

            return node;
        }

        private static VariantTreeNode FlattenTransparentStructuralWrappers(VariantTreeNode node)
        {
            if (node == null)
                return null;

            for (int i = 0; i < node.Children.Count; i++)
                node.Children[i] = FlattenTransparentStructuralWrappers(node.Children[i]);

            var flattenedChildren = new List<VariantTreeNode>();
            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (IsTransparentStructuralWrapper(child))
                {
                    flattenedChildren.AddRange(
                        (child.Children ?? new List<VariantTreeNode>())
                        .Where(c => c != null));
                    continue;
                }

                flattenedChildren.Add(child);
            }

            node.Children = flattenedChildren;
            return node;
        }

        private static bool IsTransparentStructuralWrapper(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)))
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node.HeaderAnnotation)))
                return false;

            if ((node.CommonLines?.Any(line => !string.IsNullOrWhiteSpace(line)) ?? false))
                return false;

            if ((node.DirectVariants?.Any(variant => variant != null) ?? false))
                return false;

            return node.Children != null && node.Children.Any(IsRenderableStructuralNode);
        }

        private static bool HasIntrinsicRenderableDirectVariant(VariantTreeNode node)
        {
            return node?.DirectVariants != null &&
                   node.DirectVariants.Any(IsRenderableDirectVariant);
        }

        private static bool IsDecorativeChoicePlaceholderLeaf(VariantTreeNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Label))
                return false;

            if (!string.IsNullOrWhiteSpace(node.HeaderAnnotation))
                return false;

            if ((node.CommonLines?.Count ?? 0) > 0)
                return false;

            if (node.Children != null && node.Children.Any(IsRenderableStructuralNode))
                return false;

            return CountRenderableDirectVariants(node) == 0;
        }

        private static bool MergeRedundantSameLabelChildren(VariantTreeNode node)
        {
            if (node == null || node.Children == null || node.Children.Count == 0)
                return false;

            bool changed = false;
            var mergedChildren = new List<VariantTreeNode>();

            foreach (var child in node.Children)
            {
                if (!CanMergeRedundantSameLabelChild(node, child))
                {
                    mergedChildren.Add(child);
                    continue;
                }

                changed = true;

                if (child.DirectVariants != null && child.DirectVariants.Count > 0)
                    node.DirectVariants.AddRange(child.DirectVariants.Where(v => v != null));

                if (child.Children != null && child.Children.Count > 0)
                    mergedChildren.AddRange(child.Children.Where(c => c != null));
            }

            if (changed)
                node.Children = mergedChildren;

            return changed;
        }

        private static bool CanMergeRedundantSameLabelChild(VariantTreeNode parent, VariantTreeNode child)
        {
            if (parent == null || child == null)
                return false;

            if (string.IsNullOrWhiteSpace(parent.Label) || string.IsNullOrWhiteSpace(child.Label))
                return false;

            if (!string.Equals(parent.Label, child.Label, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(
                    NormalizeHeaderAnnotation(parent.HeaderAnnotation) ?? string.Empty,
                    NormalizeHeaderAnnotation(child.HeaderAnnotation) ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Повтор того же выбора без собственного текста у дочернего узла обычно
            // появляется из-за цикла валидации ввода (например, повторных cmp/jcc для Y/N).
            // Такой узел не несёт новой развилки сам по себе, поэтому поднимаем его
            // потомков и листы на уровень выше, сохраняя реальное содержимое.
            return (child.CommonLines?.Count ?? 0) == 0;
        }

        private static bool IsGenericTechnicalChoiceLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return true;

            string normalized = label.Trim();
            if (normalized.EndsWith(")", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            return string.Equals(normalized, "Ветка", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Branch", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Linear", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "InputChoiceBranch", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "InputChoiceLinear", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<VariantTreeNode, string> BuildSyntheticChildLabels(
            VariantTreeNode node,
            List<VariantTreeNode> renderableChildren,
            IEnumerable<VariantRenderItem> directVariants,
            Dictionary<VariantRenderItem, string> syntheticDirectLabels)
        {
            var result = new Dictionary<VariantTreeNode, string>();
            if (node == null || renderableChildren == null || renderableChildren.Count == 0)
                return result;

            string promptText = string.Join("\n", node.CommonLines ?? new List<string>());
            if (string.IsNullOrWhiteSpace(promptText))
                return result;

            var optionLabels = ExtractPromptOptionLabels(promptText)
                .Select(NormalizeChoiceLabel)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (optionLabels.Count == 0)
                return result;

            if (ShouldSuppressSyntheticPromptChoiceLabels(node, optionLabels))
                return result;

            var consumedDirectLabels = new HashSet<string>(
                (syntheticDirectLabels ?? new Dictionary<VariantRenderItem, string>())
                    .Values
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(NormalizeChoiceLabel),
                StringComparer.OrdinalIgnoreCase);

            var consumedChildLabels = new HashSet<string>(
                renderableChildren
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Label))
                    .Select(c => NormalizeChoiceLabel(c.Label)),
                StringComparer.OrdinalIgnoreCase);

            optionLabels = optionLabels
                .Where(l => !consumedDirectLabels.Contains(l) && !consumedChildLabels.Contains(l))
                .ToList();

            if (optionLabels.Count == 0)
                return result;

            var unlabeledChildren = renderableChildren
                .Where(c => c != null && string.IsNullOrWhiteSpace(c.Label))
                .ToList();

            if (unlabeledChildren.Count == 0)
                return result;

            int escIndex = optionLabels.FindIndex(l => l.Equals("ESC)", StringComparison.OrdinalIgnoreCase));
            if (escIndex >= 0)
            {
                var escChild = unlabeledChildren.FirstOrDefault(IsLikelyCancelNode) ?? unlabeledChildren.First();
                result[escChild] = "ESC)";
                unlabeledChildren.Remove(escChild);
                optionLabels.RemoveAt(escIndex);
            }

            int count = Math.Min(unlabeledChildren.Count, optionLabels.Count);
            for (int i = 0; i < count; i++)
                result[unlabeledChildren[i]] = optionLabels[i];

            return result;
        }

        private static bool IsLikelyCancelNode(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if ((node.Children?.Any(IsRenderableStructuralNode) ?? false))
                return false;

            if ((node.CommonLines?.Count ?? 0) > 0)
                return false;

            var variants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(v => v != null)
                .ToList();

            if (variants.Count == 0)
                return true;

            return variants.All(IsLikelyPromptCancelVariant);
        }

        private static Dictionary<VariantRenderItem, string> BuildSyntheticChoiceLabels(
            VariantTreeNode node,
            List<VariantRenderItem> directVariants,
            List<VariantTreeNode> renderableChildren)
        {
            var result = new Dictionary<VariantRenderItem, string>();
            if (node == null || directVariants == null || directVariants.Count <= 1)
                return result;

            if (renderableChildren != null && renderableChildren.Count > 0)
                return result;

            string promptText = string.Join("\n", node.CommonLines ?? new List<string>());
            if (string.IsNullOrWhiteSpace(promptText))
                return result;

            var optionLabels = ExtractPromptOptionLabels(promptText);
            var normalizedOptionLabels = optionLabels
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ShouldSuppressSyntheticPromptChoiceLabels(node, normalizedOptionLabels))
                return result;

            bool hasEscOption = promptText.IndexOf("ESC", StringComparison.OrdinalIgnoreCase) >= 0;

            var ordered = directVariants
                .Where(v => v != null)
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();

            if (ordered.Count <= 1)
                return result;

            var noOpVariants = ordered
                .Where(IsLikelyPromptCancelVariant)
                .ToList();

            if (optionLabels.Count == ordered.Count)
            {
                for (int i = 0; i < ordered.Count; i++)
                    result[ordered[i]] = NormalizeChoiceLabel(optionLabels[i]);

                return result;
            }

            if (hasEscOption && optionLabels.Count + 1 == ordered.Count && noOpVariants.Count == 1)
            {
                result[noOpVariants[0]] = "ESC)";

                var remainingVariants = ordered
                    .Where(v => !ReferenceEquals(v, noOpVariants[0]))
                    .ToList();

                for (int i = 0; i < optionLabels.Count && i < remainingVariants.Count; i++)
                    result[remainingVariants[i]] = NormalizeChoiceLabel(optionLabels[i]);

                return result;
            }

            return new Dictionary<VariantRenderItem, string>();
        }

        private static bool ShouldSuppressSyntheticPromptChoiceLabels(
            VariantTreeNode node,
            IEnumerable<string> promptOptionLabels)
        {
            if (node == null)
                return false;

            if (HasMultiNumericPromptOptions(node.CommonLines))
                return true;

            if (string.IsNullOrWhiteSpace(node.Label))
                return false;

            string normalizedNodeLabel = NormalizeChoiceLabel(node.Label);
            if (string.IsNullOrWhiteSpace(normalizedNodeLabel))
                return false;

            // Если текущий узел уже помечен как выбранный ответ из prompt'а
            // (например, Y)/N)/ESC)), то дочерние исходы принадлежат этой ветке,
            // а не самому prompt'у. Повторная синтетическая маркировка иначе
            // создаёт ложный вложенный уровень выбора.
            return (promptOptionLabels ?? Enumerable.Empty<string>())
                .Select(NormalizeChoiceLabel)
                .Any(label =>
                    !string.IsNullOrWhiteSpace(label) &&
                    string.Equals(label, normalizedNodeLabel, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> ExtractPromptOptionLabels(string promptText)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(promptText))
                return result;

            string normalized = promptText
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (normalized.IndexOf("ESC", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add("ESC");

            var matches = Regex.Matches(normalized, @"\(([^()]*)\)");
            foreach (Match match in matches)
            {
                string token = match.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var rangeMatch = Regex.Match(token, @"^\s*(\d+)\s*[-–—]\s*(\d+)(?:\s+[^()]*)?\s*$");
                if (rangeMatch.Success &&
                    int.TryParse(rangeMatch.Groups[1].Value, out int from) &&
                    int.TryParse(rangeMatch.Groups[2].Value, out int to) &&
                    from <= to &&
                    from >= 0 &&
                    to - from <= 20)
                {
                    for (int i = from; i <= to; i++)
                        result.Add(i.ToString());

                    continue;
                }

                var slashParts = token
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => Regex.IsMatch(p, @"^[A-Za-z0-9]+$"))
                    .ToList();

                if (slashParts.Count >= 2)
                {
                    result.AddRange(slashParts);
                    continue;
                }
            }

            var inlineChoiceMatches = Regex.Matches(
                normalized,
                @"(?<![A-Za-z0-9])([A-Za-z0-9]|ESC)\)\s*(?=\S)",
                RegexOptions.IgnoreCase);
            foreach (Match match in inlineChoiceMatches)
            {
                string token = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    result.Add(token);
            }

            return result
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsLikelyPromptCancelVariant(VariantRenderItem item)
        {
            if (item == null)
                return false;

            var meaningfulLines = (item.Lines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            if (meaningfulLines.Count == 0)
                return true;

            return meaningfulLines.Count == 1 &&
                   string.Equals(meaningfulLines[0], "Ничего не происходит (не выполнены условия для наступления ни одного варианта)", StringComparison.Ordinal);
        }

        private static bool ShouldSkipHierarchicalVariant(List<string> lines, List<string> narrativeLines)
        {
            return false;
        }

        private static string NormalizeChoiceLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            label = label.Trim();
            if (label.EndsWith(")", StringComparison.Ordinal))
                return label;

            return IsCompactInputChoiceLabel(label)
                ? label + ")"
                : label;
        }

        private static string NormalizeHeaderAnnotation(string annotation)
        {
            return string.IsNullOrWhiteSpace(annotation)
                ? null
                : annotation.Trim();
        }

        private static string NormalizeUserVisibleChoiceLabel(string label)
        {
            string normalized = NormalizeChoiceLabel(label);
            return IsTechnicalChoiceLabel(normalized)
                ? null
                : normalized;
        }

        private static string NormalizeUserVisibleHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            return IsTechnicalHeaderAnnotation(normalized)
                ? null
                : normalized;
        }

        private static bool IsTechnicalChoiceLabel(string label)
        {
            string normalized = NormalizeChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            string token = normalized.EndsWith(")", StringComparison.Ordinal)
                ? normalized.Substring(0, normalized.Length - 1).Trim()
                : normalized.Trim();

            return Regex.IsMatch(
                token,
                @"^RND(?:\(\d+\))?\s*=",
                RegexOptions.IgnoreCase);
        }

        private static bool IsTechnicalHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"^при условии:\s*\[0x[0-9A-Fa-f]+\]\s*(?:=|!=|>|<|>=|<=)",
                RegexOptions.IgnoreCase);
        }

        private static bool IsCompactInputChoiceLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string trimmed = label.Trim();
            return string.Equals(trimmed, "ESC", StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(trimmed, @"^[A-Za-z0-9]$");
        }

        private static List<VariantRenderItem> GetAllVariants(VariantTreeNode node)
        {
            var result = new List<VariantRenderItem>();
            if (node == null)
                return result;

            result.AddRange(node.DirectVariants);
            foreach (var child in node.Children)
                result.AddRange(GetAllVariants(child));

            return result;
        }

        private static string GetSharedProbabilityLine(
            VariantTreeNode node,
            string inheritedProbabilityLine = null)
        {
            if (node == null)
                return null;

            var variants = GetAllVariants(node)
                .Where(variant => variant?.Variant != null)
                .ToList();

            if (variants.Count < 2)
                return null;

            var probabilityLines = variants
                .Select(variant => BuildProbabilityLine(variant.Variant))
                .ToList();

            string firstProbability = probabilityLines.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstProbability))
                return null;

            if (probabilityLines.Any(line => !string.Equals(line, firstProbability, StringComparison.Ordinal)))
                return null;

            if (!string.IsNullOrEmpty(inheritedProbabilityLine) &&
                string.Equals(firstProbability, inheritedProbabilityLine, StringComparison.Ordinal))
            {
                return null;
            }

            return firstProbability;
        }

        private static List<PartyPredicate> GetSharedGuardPredicates(
            VariantTreeNode node,
            string inheritedGuardKey = null)
        {
            if (node == null)
                return null;

            var variants = GetAllVariants(node)
                .Where(variant => variant?.Variant != null)
                .ToList();

            if (variants.Count < 2)
                return null;

            var firstPredicates = GetDisplayGuardPredicates(variants[0].Variant);
            string firstGuardKey = BuildGuardConditionKey(firstPredicates);
            if (string.IsNullOrWhiteSpace(firstGuardKey))
                return null;

            if (!string.IsNullOrWhiteSpace(inheritedGuardKey) &&
                string.Equals(firstGuardKey, inheritedGuardKey, StringComparison.Ordinal))
            {
                return null;
            }

            return variants
                .Select(variant => BuildGuardConditionKey(variant.Variant))
                .All(key => string.Equals(key, firstGuardKey, StringComparison.Ordinal))
                ? firstPredicates
                : null;
        }

        private static bool HasInheritedGuardForAllVariants(VariantTreeNode node, string inheritedGuardKey)
        {
            if (node == null || string.IsNullOrWhiteSpace(inheritedGuardKey))
                return false;

            var variants = GetAllVariants(node)
                .Where(variant => variant?.Variant != null)
                .ToList();

            return variants.Count > 0 &&
                   variants.All(variant =>
                       string.Equals(
                           BuildGuardConditionKey(variant.Variant),
                           inheritedGuardKey,
                           StringComparison.Ordinal));
        }

        private static void ComputeCommonLines(VariantTreeNode node)
        {
            if (node == null)
                return;

            var descendants = GetAllVariants(node);
            if (descendants.Count > 0)
            {
                var distinctNarrativeRoots = descendants
                    .Select(d => d.NarrativeLines?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (distinctNarrativeRoots.Count <= 1)
                {
                    var commonLines = GetCommonPrefix(descendants.Select(v => v.Lines).ToList());
                    if (commonLines.Count > 0 &&
                        !ShouldKeepSharedPartyEffectPrefixInline(descendants, commonLines))
                    {
                        node.CommonLines = commonLines;
                        foreach (var item in descendants)
                            item.Lines = item.Lines.Skip(commonLines.Count).ToList();
                    }
                }
            }

            foreach (var child in node.Children)
                ComputeCommonLines(child);
        }

        private static bool ShouldKeepSharedPartyEffectPrefixInline(
            List<VariantRenderItem> descendants,
            List<string> commonLines)
        {
            if (descendants == null || descendants.Count < 2 || commonLines == null || commonLines.Count == 0)
                return false;

            var sharedPartyLines = GetSharedPartyEffectLines(descendants);
            if (sharedPartyLines.Count == 0)
                return false;

            if (commonLines.Any(line => !sharedPartyLines.Contains(line, StringComparer.Ordinal)))
                return false;

            return descendants.Any(item =>
            {
                var remainingLines = (item?.Lines ?? new List<string>())
                    .Skip(commonLines.Count)
                    .ToList();

                return !remainingLines.Any(line => !string.IsNullOrWhiteSpace(line));
            });
        }

        private static void IntroduceSharedLineHierarchy(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children)
                IntroduceSharedLineHierarchy(child);

            if (node.DirectVariants == null || node.DirectVariants.Count <= 1)
                return;

            var orderedVariants = node.DirectVariants
                .Where(variant => variant != null)
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();

            var candidateGroups = orderedVariants
                .Where(variant => HasMeaningfulLines(variant.Lines))
                .GroupBy(variant => variant.Lines[0] ?? string.Empty, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
                .Select(group => group.OrderBy(GetVariantRenderOrderKey).ToList())
                .OrderBy(group => group.Min(GetVariantRenderOrderKey))
                .ToList();

            if (candidateGroups.Count == 0)
                return;

            var consumedVariants = new HashSet<VariantRenderItem>();
            var syntheticChildren = new List<VariantTreeNode>();

            foreach (var group in candidateGroups)
            {
                var commonLines = GetCommonPrefix(group.Select(variant => variant.Lines).ToList());
                if (!commonLines.Any(line => !string.IsNullOrWhiteSpace(line)) ||
                    ShouldKeepSharedPartyEffectPrefixInline(group, commonLines))
                    continue;

                var childVariants = new List<VariantRenderItem>();
                foreach (var variant in group)
                {
                    consumedVariants.Add(variant);
                    variant.Lines = variant.Lines
                        .Skip(commonLines.Count)
                        .ToList();
                    childVariants.Add(variant);
                }

                var childNode = new VariantTreeNode
                {
                    CommonLines = commonLines,
                    DirectVariants = childVariants
                };

                IntroduceSharedLineHierarchy(childNode);
                syntheticChildren.Add(childNode);
            }

            if (syntheticChildren.Count == 0)
                return;

            node.DirectVariants = orderedVariants
                .Where(variant => !consumedVariants.Contains(variant))
                .ToList();

            node.Children.AddRange(syntheticChildren);
        }

        private static void IntroduceSharedLineHierarchyAcrossHiddenTechnicalChildren(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                IntroduceSharedLineHierarchyAcrossHiddenTechnicalChildren(child);

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();
            if (children.Count < 2)
                return;

            var candidateGroups = children
                .Select((child, index) => new { Child = child, Index = index })
                .Where(entry => IsHiddenTechnicalChildWithCommonLines(entry.Child))
                .GroupBy(entry => GetFirstMeaningfulLine(entry.Child.CommonLines) ?? string.Empty, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
                .Select(group => group.OrderBy(entry => entry.Index).ToList())
                .OrderBy(group => group[0].Index)
                .ToList();

            if (candidateGroups.Count == 0)
                return;

            var childToSyntheticParent = new Dictionary<VariantTreeNode, VariantTreeNode>();
            foreach (var group in candidateGroups)
            {
                if (group.Any(entry => childToSyntheticParent.ContainsKey(entry.Child)))
                    continue;

                var sharedLines = GetCommonPrefix(group.Select(entry => entry.Child.CommonLines).ToList());
                if (!sharedLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                    continue;

                var syntheticParent = new VariantTreeNode
                {
                    CommonLines = sharedLines.ToList()
                };

                foreach (var entry in group)
                {
                    entry.Child.CommonLines = entry.Child.CommonLines
                        .Skip(sharedLines.Count)
                        .ToList();
                    syntheticParent.Children.Add(entry.Child);
                    childToSyntheticParent[entry.Child] = syntheticParent;
                }
            }

            if (childToSyntheticParent.Count == 0)
                return;

            var emittedParents = new HashSet<VariantTreeNode>();
            var rebuiltChildren = new List<VariantTreeNode>();
            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (child != null && childToSyntheticParent.TryGetValue(child, out var syntheticParent))
                {
                    if (emittedParents.Add(syntheticParent))
                        rebuiltChildren.Add(syntheticParent);
                    continue;
                }

                rebuiltChildren.Add(child);
            }

            node.Children = rebuiltChildren;
        }

        private static bool IsHiddenTechnicalChildWithCommonLines(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(node))
                return false;

            return node.CommonLines != null &&
                   node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)) &&
                   GetAllVariants(node).Any(item => item != null);
        }

        private static string GetFirstMeaningfulLine(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static void CollapseTransparentDirectVariantWrappers(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                CollapseTransparentDirectVariantWrappers(child);

            var promotedDirectVariants = new List<VariantRenderItem>();
            var rebuiltChildren = new List<VariantTreeNode>();
            bool changed = false;
            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (IsTransparentDirectVariantWrapper(child))
                {
                    promotedDirectVariants.AddRange(
                        (child.DirectVariants ?? new List<VariantRenderItem>())
                        .Where(item => item != null));
                    rebuiltChildren.AddRange(
                        (child.Children ?? new List<VariantTreeNode>())
                        .Where(grandChild => grandChild != null));
                    changed = true;
                    continue;
                }

                rebuiltChildren.Add(child);
            }

            if (!changed)
                return;

            node.Children = rebuiltChildren;
            if (promotedDirectVariants.Count > 0)
            {
                node.DirectVariants ??= new List<VariantRenderItem>();
                node.DirectVariants.AddRange(promotedDirectVariants);
            }
        }

        private static void MarkTransparentMixedSiblingGroups(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                MarkTransparentMixedSiblingGroups(child);

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (ShouldPreserveTransparentMixedSiblingGroup(node, child))
                    child.PreserveAsStructuralGroup = true;
            }
        }

        private static bool ShouldPreserveTransparentMixedSiblingGroup(
            VariantTreeNode parent,
            VariantTreeNode child)
        {
            if (parent == null || child == null)
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(child))
                return false;

            if (child.CommonLines != null && child.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return false;

            bool hasDirectOutcome = child.DirectVariants != null &&
                                    child.DirectVariants.Any(IsRenderableDirectVariant);
            bool hasNestedOutcome = child.Children != null &&
                                    child.Children.Any(IsRenderableStructuralNode);
            if (!hasDirectOutcome || !hasNestedOutcome)
                return false;

            return HasRenderableSiblingEntry(parent, child);
        }

        private static bool HasRenderableSiblingEntry(VariantTreeNode parent, VariantTreeNode child)
        {
            if (parent == null)
                return false;

            if ((parent.DirectVariants ?? new List<VariantRenderItem>())
                .Any(IsRenderableDirectVariant))
            {
                return true;
            }

            return (parent.Children ?? new List<VariantTreeNode>())
                .Any(sibling => !ReferenceEquals(sibling, child) &&
                                IsRenderableStructuralNode(sibling));
        }

        private static bool IsTransparentDirectVariantWrapper(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (node.PreserveAsStructuralGroup)
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(node))
                return false;

            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return false;

            bool hasDirectVariant = node.DirectVariants != null &&
                                    node.DirectVariants.Any(item => item != null);
            bool hasRenderableChild = node.Children != null &&
                                      node.Children.Any(IsRenderableStructuralNode);

            return hasDirectVariant || hasRenderableChild;
        }

        private static bool HasUserVisibleNodeLabelOrAnnotation(VariantTreeNode node)
        {
            return !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node?.Label)) ||
                   !string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node?.HeaderAnnotation));
        }

        private static void AttachChoiceChildrenToSiblingPromptParents(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                AttachChoiceChildrenToSiblingPromptParents(child);

            if (node.Children == null || node.Children.Count < 2)
                return;

            var children = node.Children.ToList();
            var promptChildren = children
                .Where(IsPromptParentCandidate)
                .OrderByDescending(child => child.CommonLines?.Count ?? 0)
                .ToList();

            if (promptChildren.Count == 0)
                return;

            var childToPromptParent = new Dictionary<VariantTreeNode, VariantTreeNode>();

            foreach (var child in children)
            {
                if (child == null || promptChildren.Contains(child))
                    continue;

                if (string.IsNullOrWhiteSpace(child.Label) || !IsChoiceLikeLabel(child.Label))
                    continue;

                var promptParent = promptChildren.FirstOrDefault(prompt =>
                    !ReferenceEquals(prompt, child) &&
                    NodeContentStartsWithLines(child, prompt.CommonLines));

                if (promptParent == null ||
                    !TryRemoveLeadingLinesFromNodeContent(child, promptParent.CommonLines))
                {
                    continue;
                }

                promptParent.Children.Add(child);
                childToPromptParent[child] = promptParent;
            }

            if (childToPromptParent.Count == 0)
                return;

            node.Children = children
                .Where(child => child == null || !childToPromptParent.ContainsKey(child))
                .ToList();

            foreach (var promptParent in childToPromptParent.Values.Distinct())
                AttachChoiceChildrenToSiblingPromptParents(promptParent);
        }

        private static bool IsPromptParentCandidate(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(node.Label))
                return false;

            return HasPromptOptionLabels(node.CommonLines);
        }

        private static void IntroduceSharedPromptHierarchyFromChoiceChildContent(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                IntroduceSharedPromptHierarchyFromChoiceChildContent(child);

            var groups = BuildSharedPromptContentGroups(node);
            if (groups.Count == 0)
                return;

            var usedChildren = new HashSet<VariantTreeNode>();
            var childToSyntheticParent = new Dictionary<VariantTreeNode, VariantTreeNode>();

            foreach (var group in groups)
            {
                if (group.Children.Any(child => usedChildren.Contains(child)))
                    continue;

                if (group.Children.Any(child => !NodeContentStartsWithLines(child, group.Lines)))
                    continue;

                var syntheticParent = new VariantTreeNode
                {
                    CommonLines = new List<string>(group.Lines)
                };

                foreach (var child in group.Children)
                {
                    if (!TryRemoveLeadingLinesFromNodeContent(child, group.Lines))
                        continue;

                    syntheticParent.Children.Add(child);
                    childToSyntheticParent[child] = syntheticParent;
                    usedChildren.Add(child);
                }
            }

            if (childToSyntheticParent.Count == 0)
                return;

            var emittedParents = new HashSet<VariantTreeNode>();
            var rebuiltChildren = new List<VariantTreeNode>();
            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (child != null && childToSyntheticParent.TryGetValue(child, out var syntheticParent))
                {
                    if (emittedParents.Add(syntheticParent))
                        rebuiltChildren.Add(syntheticParent);

                    continue;
                }

                rebuiltChildren.Add(child);
            }

            node.Children = rebuiltChildren;
        }

        private static List<SharedPromptContentGroup> BuildSharedPromptContentGroups(VariantTreeNode node)
        {
            var result = new Dictionary<string, SharedPromptContentGroup>(StringComparer.Ordinal);
            var children = (node?.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();

            if (children.Count < 2)
                return new List<SharedPromptContentGroup>();

            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                var child = children[childIndex];
                if (string.IsNullOrWhiteSpace(child.Label) || !IsChoiceLikeLabel(child.Label))
                    continue;

                var lines = GetHoistablePromptContentLines(child);
                if (lines.Count == 0)
                    continue;

                for (int length = 1; length <= lines.Count; length++)
                {
                    var prefix = lines.Take(length).ToList();
                    if (!PromptPrefixContainsChoiceLabel(prefix, child.Label))
                        continue;

                    string key = BuildCommonLinesKey(prefix);
                    if (!result.TryGetValue(key, out var group))
                    {
                        group = new SharedPromptContentGroup
                        {
                            Lines = prefix
                        };
                        result[key] = group;
                    }

                    if (!group.Children.Contains(child))
                        group.Children.Add(child);

                    group.FirstChildIndex = Math.Min(group.FirstChildIndex, childIndex);
                }
            }

            return result.Values
                .Where(ShouldCreateSharedPromptContentParent)
                .OrderBy(group => group.FirstChildIndex)
                .ThenBy(group => group.Lines.Count)
                .ToList();
        }

        private static bool ShouldCreateSharedPromptContentParent(SharedPromptContentGroup group)
        {
            if (group == null || group.Children == null || group.Children.Count < 2)
                return false;

            var optionLabels = ExtractPromptOptionLabels(string.Join("\n", group.Lines ?? new List<string>()))
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (optionLabels.Count < 2)
                return false;

            var childLabels = group.Children
                .Select(child => NormalizeChoiceLabel(child?.Label))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (childLabels.Count < 2)
                return false;

            return childLabels.All(label => optionLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
        }

        private static List<string> GetHoistablePromptContentLines(VariantTreeNode node)
        {
            if (node == null)
                return new List<string>();

            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return node.CommonLines.ToList();

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null)
                .ToList();

            if (directVariants.Count == 0)
                return new List<string>();

            return GetCommonPrefix(directVariants.Select(variant => variant.Lines).ToList());
        }

        private static bool PromptPrefixContainsChoiceLabel(IEnumerable<string> promptLines, string choiceLabel)
        {
            string normalizedChoice = NormalizeChoiceLabel(choiceLabel);
            if (string.IsNullOrWhiteSpace(normalizedChoice))
                return false;

            return ExtractPromptOptionLabels(string.Join("\n", promptLines ?? Enumerable.Empty<string>()))
                .Select(NormalizeChoiceLabel)
                .Any(label =>
                    !string.IsNullOrWhiteSpace(label) &&
                    string.Equals(label, normalizedChoice, StringComparison.OrdinalIgnoreCase));
        }

        private static bool PromptContainsChoiceLabel(IEnumerable<string> promptLines, string choiceLabel)
        {
            return PromptPrefixContainsChoiceLabel(promptLines, choiceLabel);
        }

        private static bool HasPromptOptionLabels(IEnumerable<string> lines)
        {
            return ExtractPromptOptionLabels(string.Join("\n", lines ?? Enumerable.Empty<string>()))
                .Any(label => !string.IsNullOrWhiteSpace(label));
        }

        private static bool NodeContentStartsWithLines(VariantTreeNode node, IReadOnlyList<string> prefix)
        {
            if (node == null || prefix == null || prefix.Count == 0)
                return false;

            if (StartsWithLineSequence(node.CommonLines, prefix))
                return true;

            var variants = GetAllVariants(node)
                .Where(variant => variant != null)
                .ToList();

            return variants.Count > 0 &&
                   variants.All(variant => StartsWithLineSequence(variant.Lines, prefix));
        }

        private static bool TryRemoveLeadingLinesFromNodeContent(VariantTreeNode node, IReadOnlyList<string> prefix)
        {
            if (node == null || prefix == null || prefix.Count == 0)
                return false;

            if (StartsWithLineSequence(node.CommonLines, prefix))
            {
                node.CommonLines = node.CommonLines
                    .Skip(prefix.Count)
                    .ToList();
                return true;
            }

            var variants = GetAllVariants(node)
                .Where(variant => variant != null)
                .ToList();

            if (variants.Count == 0 ||
                variants.Any(variant => !StartsWithLineSequence(variant.Lines, prefix)))
            {
                return false;
            }

            foreach (var variant in variants)
            {
                variant.Lines = variant.Lines
                    .Skip(prefix.Count)
                    .ToList();
            }

            return true;
        }

        private static bool StartsWithLineSequence(IReadOnlyList<string> lines, IReadOnlyList<string> prefix)
        {
            if (lines == null || prefix == null || prefix.Count == 0 || lines.Count < prefix.Count)
                return false;

            for (int i = 0; i < prefix.Count; i++)
            {
                if (!string.Equals(lines[i], prefix[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static void IntroduceSharedPromptHierarchyAcrossChoiceChildren(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children)
                IntroduceSharedPromptHierarchyAcrossChoiceChildren(child);

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();

            if (renderableChildren.Count < 2)
                return;

            var candidateGroups = renderableChildren
                .Where(IsEligibleSharedPromptChoiceChild)
                .GroupBy(child => BuildCommonLinesKey(child.CommonLines), StringComparer.Ordinal)
                .Select(group => group.ToList())
                .Where(group => ShouldCreateSharedPromptParent(group))
                .ToList();

            if (candidateGroups.Count == 0)
                return;

            var childToSyntheticParent = new Dictionary<VariantTreeNode, VariantTreeNode>();

            foreach (var group in candidateGroups)
            {
                var sharedLines = group[0].CommonLines?.ToList() ?? new List<string>();
                if (sharedLines.Count == 0)
                    continue;

                var syntheticParent = new VariantTreeNode
                {
                    CommonLines = new List<string>(sharedLines)
                };

                foreach (var child in group)
                {
                    child.CommonLines = child.CommonLines
                        .Skip(sharedLines.Count)
                        .ToList();
                    syntheticParent.Children.Add(child);
                    childToSyntheticParent[child] = syntheticParent;
                }
            }

            if (childToSyntheticParent.Count == 0)
                return;

            var emittedParents = new HashSet<VariantTreeNode>();
            var rebuiltChildren = new List<VariantTreeNode>();

            foreach (var child in node.Children)
            {
                if (child != null && childToSyntheticParent.TryGetValue(child, out var syntheticParent))
                {
                    if (emittedParents.Add(syntheticParent))
                        rebuiltChildren.Add(syntheticParent);

                    continue;
                }

                rebuiltChildren.Add(child);
            }

            node.Children = rebuiltChildren;
        }

        private static bool IsEligibleSharedPromptChoiceChild(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (string.IsNullOrWhiteSpace(node.Label) || !IsChoiceLikeLabel(node.Label))
                return false;

            return node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line));
        }

        private static bool ShouldCreateSharedPromptParent(List<VariantTreeNode> group)
        {
            if (group == null || group.Count < 2)
                return false;

            var sharedLines = group[0]?.CommonLines;
            if (sharedLines == null || !sharedLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return false;

            string promptText = string.Join("\n", sharedLines);
            var optionLabels = ExtractPromptOptionLabels(promptText)
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (optionLabels.Count < 2)
                return false;

            var childLabels = group
                .Select(child => NormalizeChoiceLabel(child?.Label))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (childLabels.Count < 2)
                return false;

            return childLabels.All(label => optionLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
        }

        private static bool IsChoiceLikeLabel(string label)
        {
            string normalized = NormalizeChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(normalized) || !normalized.EndsWith(")", StringComparison.Ordinal))
                return false;

            string token = normalized.Substring(0, normalized.Length - 1).Trim();
            if (string.IsNullOrWhiteSpace(token))
                return false;

            return Regex.IsMatch(token, @"^[A-Za-z0-9]+$");
        }

        private static string BuildCommonLinesKey(IEnumerable<string> lines)
        {
            return string.Join("\n---\n", (lines ?? Enumerable.Empty<string>()).Select(line => line ?? string.Empty));
        }

        private static void RemoveRedundantInheritedLines(VariantTreeNode node)
        {
            RemoveRedundantInheritedLines(node, new List<string>());
        }

        private static void RemoveRedundantInheritedLines(VariantTreeNode node, List<string> inheritedLines)
        {
            if (node == null)
                return;

            node.CommonLines = RemoveLeadingInheritedSuffix(node.CommonLines, inheritedLines);

            var nextInheritedLines = new List<string>(inheritedLines ?? new List<string>());
            nextInheritedLines.AddRange(node.CommonLines ?? new List<string>());

            foreach (var variant in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (variant != null)
                    variant.Lines = RemoveLeadingInheritedSuffix(variant.Lines, nextInheritedLines);
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                RemoveRedundantInheritedLines(child, nextInheritedLines);
        }

        private static List<string> RemoveLeadingInheritedSuffix(List<string> lines, List<string> inheritedLines)
        {
            if (lines == null || lines.Count == 0 || inheritedLines == null || inheritedLines.Count == 0)
                return lines ?? new List<string>();

            int duplicateCount = GetLeadingInheritedSuffixLength(lines, inheritedLines);
            return duplicateCount > 0
                ? lines.Skip(duplicateCount).ToList()
                : lines;
        }

        private static int GetLeadingInheritedSuffixLength(List<string> lines, List<string> inheritedLines)
        {
            int maxLength = Math.Min(lines?.Count ?? 0, inheritedLines?.Count ?? 0);
            for (int length = maxLength; length > 0; length--)
            {
                int inheritedStart = inheritedLines.Count - length;
                bool matches = true;
                for (int i = 0; i < length; i++)
                {
                    if (!string.Equals(lines[i], inheritedLines[inheritedStart + i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return length;
            }

            return 0;
        }

        private static void PromoteConditionalPartyNotesBeforeBattle(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children)
                PromoteConditionalPartyNotesBeforeBattle(child);

            if (node.Children != null && node.Children.Count > 0)
                return;

            if (node.CommonLines == null || node.CommonLines.Count == 0)
                return;

            int battleIndex = FindFirstBattleLineIndex(node.CommonLines);
            if (battleIndex < 0)
                return;

            var directVariants = node.DirectVariants ?? new List<VariantRenderItem>();
            var renderableVariants = directVariants
                .Where(v => v != null && HasMeaningfulLines(v.Lines))
                .ToList();

            if (TryHoistSharedPartyNotesBeforeBattle(node, renderableVariants, battleIndex))
                return;

            if (renderableVariants.Count != 1)
                return;

            var conditionLines = (renderableVariants[0].Lines ?? new List<string>())
                .Where(IsConditionalPartyStatusLine)
                .ToList();

            if (conditionLines.Count == 0 || conditionLines.Count != (renderableVariants[0].Lines?.Count ?? 0))
                return;

            var reorderedCommon = new List<string>();
            reorderedCommon.AddRange(node.CommonLines.Take(battleIndex));
            reorderedCommon.AddRange(conditionLines);
            reorderedCommon.AddRange(node.CommonLines.Skip(battleIndex));
            node.CommonLines = reorderedCommon;

            renderableVariants[0].Lines = new List<string>();
        }

        private static bool TryHoistSharedPartyNotesBeforeBattle(
            VariantTreeNode node,
            List<VariantRenderItem> renderableVariants,
            int battleIndex)
        {
            if (node == null || renderableVariants == null || renderableVariants.Count < 2)
                return false;

            if (renderableVariants.Any(variant => !ContainsOnlyPartyEffectLines(variant)))
                return false;

            var sharedPartyLines = GetSharedPartyEffectLines(renderableVariants);
            if (sharedPartyLines.Count == 0)
                return false;

            string battleLine = node.CommonLines[battleIndex];
            var reorderedCommon = new List<string>();
            reorderedCommon.AddRange(node.CommonLines.Take(battleIndex));
            reorderedCommon.AddRange(sharedPartyLines);
            node.CommonLines = reorderedCommon;

            foreach (var variant in renderableVariants)
            {
                var remainingLines = RemoveLineOccurrences(variant.Lines, sharedPartyLines);
                remainingLines.Add(battleLine);
                variant.Lines = remainingLines;
            }

            return true;
        }

        private static bool ContainsOnlyPartyEffectLines(VariantRenderItem item)
        {
            if (item?.Lines == null || item.Lines.Count == 0)
                return false;

            var partyEffectLines = GetVariantPartyEffectLines(item);
            if (partyEffectLines.Count == 0)
                return false;

            var availableCounts = BuildLineCounts(partyEffectLines);
            foreach (var line in item.Lines)
            {
                string key = line ?? string.Empty;
                if (!availableCounts.TryGetValue(key, out int remaining) || remaining <= 0)
                    return false;

                if (remaining == 1)
                    availableCounts.Remove(key);
                else
                    availableCounts[key] = remaining - 1;
            }

            return true;
        }

        private static IEnumerable<PartyEffect> GetOrderedDisplayablePartyEffects(
            List<PartyEffect> effects,
            Func<PartyEffect, bool> includePredicate = null)
        {
            return (effects ?? new List<PartyEffect>())
                .Where(effect => effect != null)
                .Where(effect => PartyEffectSemantics.ShouldIncludeInNotes(effect, effects))
                .Where(effect => includePredicate == null || includePredicate(effect))
                .OrderBy(effect => GetSpecialLineSortBucket(effect.ExecutionOrder))
                .ThenBy(effect => NormalizeSpecialExecutionOrder(effect.ExecutionOrder))
                .ThenBy(effect => NormalizeSpecialLineAddress(effect.InstructionAddress))
                .ThenBy(effect => PartyEffectSemantics.BuildHumanDescription(effect), StringComparer.Ordinal);
        }

        private static List<string> GetVariantPartyEffectLines(
            VariantRenderItem item,
            Func<PartyEffect, bool> includePredicate = null)
        {
            var effects = (item?.Variant?.PartyEffects ?? new List<PartyEffect>())
                .Where(effect => effect != null)
                .ToList();

            return GetOrderedDisplayablePartyEffects(effects, includePredicate)
                .Select(effect => BuildPartyEffectDisplayDescription(effect, item?.Variant))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList();
        }

        private static string BuildPartyEffectDisplayDescription(
            PartyEffect effect,
            PathVariantInfo variantContext = null)
        {
            string description = PartyEffectSemantics.BuildHumanDescription(effect);
            if (!ShouldRenderStandardStatusLoopAsCurrentMember(variantContext, effect))
                return description;

            return ReplaceWholePartyConditionTargetWithCurrentMember(description);
        }

        private static string ReplaceWholePartyConditionTargetWithCurrentMember(string description)
        {
            if (string.IsNullOrWhiteSpace(description) ||
                (!description.StartsWith(WholePartyConditionChangePrefix, StringComparison.Ordinal) &&
                 !description.StartsWith(LegacyWholePartyConditionChangePrefix, StringComparison.Ordinal)))
            {
                return description;
            }

            string prefix = description.StartsWith(WholePartyConditionChangePrefix, StringComparison.Ordinal)
                ? WholePartyConditionChangePrefix
                : LegacyWholePartyConditionChangePrefix;

            return CurrentPartyMemberConditionChangePrefix + description.Substring(prefix.Length);
        }

        private static bool ShouldRenderStandardStatusLoopAsCurrentMember(
            PathVariantInfo variant,
            PartyEffect effect)
        {
            if (variant?.BranchChoices == null ||
                !PartyEffectSemantics.IsStandardActivePartyStatusGuardedLoop(effect) ||
                !IsConditionalLoopSubsetOutcomeEffect(effect))
            {
                return false;
            }

            var choices = variant.BranchChoices
                .Where(choice => choice != null)
                .ToList();
            int guardIndex = choices.FindIndex(choice => IsMatchingLoopGuardChoice(effect, choice));
            if (guardIndex < 0)
                return false;

            return choices
                .Skip(guardIndex + 1)
                .Any(IsOutcomeBranchChoiceAfterLoopGuard);
        }

        private static bool IsMatchingLoopGuardChoice(PartyEffect effect, BranchChoice choice)
        {
            if (effect == null || choice?.GuardPredicate == null)
                return false;

            string choicePredicateKey = BuildLoopNormalizedPredicateKey(choice.GuardPredicate);
            if (string.IsNullOrWhiteSpace(choicePredicateKey))
                return false;

            return PartyEffectSemantics.GetEffectiveGuardPredicates(effect)
                .Select(PartyEffectSemantics.BuildPredicateKey)
                .Any(key => string.Equals(key, choicePredicateKey, StringComparison.Ordinal));
        }

        private static string BuildLoopNormalizedPredicateKey(PartyPredicate predicate)
        {
            var normalized = PartyEffectSemantics
                .NormalizeGuardPredicatesForLoopAggregation(new[] { predicate })
                .FirstOrDefault();

            return normalized == null
                ? null
                : PartyEffectSemantics.BuildPredicateKey(normalized);
        }

        private static bool IsOutcomeBranchChoiceAfterLoopGuard(BranchChoice choice)
        {
            if (choice == null || choice.HasGuardPredicate || IsPartyLoopTraversalBranchChoice(choice))
                return false;

            return !string.IsNullOrWhiteSpace(ExtractBranchMnemonic(choice));
        }

        private static bool IsPartyLoopTraversalBranchChoice(BranchChoice choice)
        {
            string mnemonic = ExtractBranchMnemonic(choice);
            if (!IsLowByteRegisterName(choice?.CompareRegister) ||
                choice?.CompareMemoryAddress != PartyCountAddress)
            {
                return false;
            }

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

        private static string ExtractBranchMnemonic(BranchChoice choice)
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

        private static List<string> GetSharedPartyEffectLines(
            List<VariantRenderItem> variants,
            Func<PartyEffect, bool> includePredicate = null)
        {
            if (variants == null || variants.Count == 0)
                return new List<string>();

            var orderedSource = variants
                .Select(variant => new
                {
                    Variant = variant,
                    Lines = GetVariantPartyEffectLines(variant, includePredicate)
                })
                .Where(entry => entry.Lines.Count > 0)
                .OrderBy(entry => entry.Lines.Count)
                .Select(entry => entry.Lines)
                .FirstOrDefault();

            if (orderedSource == null || orderedSource.Count == 0)
                return new List<string>();

            var commonCounts = BuildLineCounts(orderedSource);
            foreach (var variant in variants)
            {
                var variantCounts = BuildLineCounts(variant?.Lines);
                foreach (var key in commonCounts.Keys.ToList())
                {
                    int variantCount = variantCounts.TryGetValue(key, out int count) ? count : 0;
                    int mergedCount = Math.Min(commonCounts[key], variantCount);
                    if (mergedCount <= 0)
                        commonCounts.Remove(key);
                    else
                        commonCounts[key] = mergedCount;
                }
            }

            var result = new List<string>();
            foreach (var line in orderedSource)
            {
                string key = line ?? string.Empty;
                if (!commonCounts.TryGetValue(key, out int remaining) || remaining <= 0)
                    continue;

                result.Add(line);
                if (remaining == 1)
                    commonCounts.Remove(key);
                else
                    commonCounts[key] = remaining - 1;
            }

            return result;
        }

        private static void HoistSharedCommonPartyNotes(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children)
                HoistSharedCommonPartyNotes(child);

            var renderableVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null && HasMeaningfulLines(variant.Lines))
                .ToList();

            if (renderableVariants.Count < 2)
            {
                HoistSharedCommonPartyNotesAcrossChildren(node);
                return;
            }

            // Если битва уже находится в общем префиксе узла, то общий подъём
            // party-заметок нужно отложить до этапа PromoteConditionalPartyNotesBeforeBattle,
            // иначе битва остаётся в CommonLines и узел может схлопнуться обратно
            // в плоский вид (как в PORTSMIT (11,8)).
            if (FindFirstBattleLineIndex(node.CommonLines) >= 0)
                return;

            // Поднимаем наверх только те party-заметки, которые действительно
            // общие для всех листьев этого узла и должны жить в общем корне:
            // guard-like проверки и tracked technical-поля персонажей.
            var sharedPartyLines = GetSharedPartyEffectLines(
                    renderableVariants,
                    effect => effect != null && IsHoistableSharedPartyEffect(effect))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !(node.CommonLines ?? new List<string>()).Contains(line, StringComparer.Ordinal))
                .ToList();

            if (sharedPartyLines.Count == 0)
            {
                HoistSharedCommonPartyNotesAcrossChildren(node);
                return;
            }

            InsertCommonLinesBeforeBattle(node, sharedPartyLines);

            foreach (var variant in renderableVariants)
                variant.Lines = RemoveLineOccurrences(variant.Lines, sharedPartyLines);

            HoistSharedCommonPartyNotesAcrossChildren(node);
        }

        private static bool IsHoistableSharedPartyEffect(PartyEffect effect)
        {
            if (effect == null)
                return false;

            if (PartyEffectSemantics.IsGuardLike(effect))
                return true;

            return (PartyFoodSemantics.IsFoodField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                    PartyTechnicalFieldSemantics.IsTrackedField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                    PartyTemporaryStatSemantics.IsTrackedField(PartyEffectSemantics.GetEffectiveField(effect))) &&
                   PartyEffectSemantics.IsStateChanging(effect);
        }

        private static void HoistSharedCommonPartyNotesAcrossChildren(VariantTreeNode node)
        {
            if (node == null)
                return;

            if (FindFirstBattleLineIndex(node.CommonLines) >= 0)
                return;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();

            var renderableDirectVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null && HasMeaningfulLines(variant.Lines))
                .ToList();

            var branches = new List<SharedPartyHoistBranch>();
            branches.AddRange(renderableChildren.Select(child => new SharedPartyHoistBranch
            {
                ChildNode = child,
                Lines = GetImmediateBranchHoistableLines(child)
            }));
            branches.AddRange(renderableDirectVariants.Select(variant => new SharedPartyHoistBranch
            {
                DirectVariant = variant,
                Lines = GetRenderedHoistablePartyEffectLines(variant)
            }));

            if (branches.Count < 2)
                return;

            if (branches.Any(branch => branch.Lines.Count == 0))
                return;

            var orderedSource = branches
                .OrderBy(branch => branch.Lines.Count)
                .Select(branch => branch.Lines)
                .FirstOrDefault();

            if (orderedSource == null || orderedSource.Count == 0)
                return;

            var sharedCounts = BuildLineCounts(orderedSource);
            foreach (var branch in branches)
            {
                var branchCounts = BuildLineCounts(branch.Lines);
                foreach (var key in sharedCounts.Keys.ToList())
                {
                    int branchCount = branchCounts.TryGetValue(key, out int count) ? count : 0;
                    int mergedCount = Math.Min(sharedCounts[key], branchCount);
                    if (mergedCount <= 0)
                        sharedCounts.Remove(key);
                    else
                        sharedCounts[key] = mergedCount;
                }
            }

            var sharedLines = new List<string>();
            foreach (var line in orderedSource)
            {
                string key = line ?? string.Empty;
                if (!sharedCounts.TryGetValue(key, out int remaining) || remaining <= 0)
                    continue;

                sharedLines.Add(line);
                if (remaining == 1)
                    sharedCounts.Remove(key);
                else
                    sharedCounts[key] = remaining - 1;
            }

            sharedLines = sharedLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !(node.CommonLines ?? new List<string>()).Contains(line, StringComparer.Ordinal))
                .ToList();

            if (sharedLines.Count == 0)
                return;

            InsertCommonLinesBeforeBattle(node, sharedLines);

            foreach (var branch in branches)
            {
                if (branch.ChildNode != null)
                    RemoveImmediateBranchLineOccurrences(branch.ChildNode, sharedLines);
                else if (branch.DirectVariant != null)
                    RemoveImmediateBranchLineOccurrences(branch.DirectVariant, sharedLines);
            }
        }

        private static List<string> GetImmediateBranchHoistableLines(VariantTreeNode node)
        {
            if (node == null)
                return new List<string>();

            var result = new List<string>();
            var descendantHoistableLines = BuildDescendantHoistablePartyLineSet(node);

            foreach (var line in node.CommonLines ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(line) && descendantHoistableLines.Contains(line))
                    result.Add(line);
            }

            if ((node.Children?.Count ?? 0) == 0 && (node.DirectVariants?.Count ?? 0) == 1)
                result.AddRange(GetRenderedHoistablePartyEffectLines(node.DirectVariants[0]));

            return result;
        }

        private static HashSet<string> BuildDescendantHoistablePartyLineSet(VariantTreeNode node)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variant in GetAllVariants(node))
            {
                foreach (var line in GetVariantPartyEffectLines(
                    variant,
                    effect => effect != null && IsHoistableSharedPartyEffect(effect)))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        result.Add(line);
                }
            }

            return result;
        }

        private static List<string> GetRenderedHoistablePartyEffectLines(VariantRenderItem item)
        {
            if (item?.Lines == null || item.Lines.Count == 0)
                return new List<string>();

            var candidateCounts = BuildLineCounts(
                GetVariantPartyEffectLines(
                    item,
                    effect => effect != null && IsHoistableSharedPartyEffect(effect)));

            if (candidateCounts.Count == 0)
                return new List<string>();

            var result = new List<string>();
            foreach (var line in item.Lines)
            {
                string key = line ?? string.Empty;
                if (!candidateCounts.TryGetValue(key, out int remaining) || remaining <= 0)
                    continue;

                result.Add(line);
                if (remaining == 1)
                    candidateCounts.Remove(key);
                else
                    candidateCounts[key] = remaining - 1;
            }

            return result;
        }

        private static void RemoveImmediateBranchLineOccurrences(VariantTreeNode node, IEnumerable<string> linesToRemove)
        {
            if (node == null)
                return;

            foreach (var line in linesToRemove ?? Enumerable.Empty<string>())
            {
                if (RemoveFirstLineOccurrence(node.CommonLines, line))
                    continue;

                if ((node.Children?.Count ?? 0) == 0 && (node.DirectVariants?.Count ?? 0) == 1)
                    RemoveFirstLineOccurrence(node.DirectVariants[0]?.Lines, line);
            }
        }

        private static void RemoveImmediateBranchLineOccurrences(VariantRenderItem item, IEnumerable<string> linesToRemove)
        {
            if (item?.Lines == null)
                return;

            foreach (var line in linesToRemove ?? Enumerable.Empty<string>())
                RemoveFirstLineOccurrence(item.Lines, line);
        }

        private static bool RemoveFirstLineOccurrence(List<string> lines, string line)
        {
            if (lines == null || lines.Count == 0)
                return false;

            string key = line ?? string.Empty;
            int index = lines.FindIndex(item => string.Equals(item ?? string.Empty, key, StringComparison.Ordinal));
            if (index < 0)
                return false;

            lines.RemoveAt(index);
            return true;
        }

        private static void InsertCommonLinesBeforeBattle(VariantTreeNode node, IEnumerable<string> linesToInsert)
        {
            if (node == null)
                return;

            var orderedLines = (linesToInsert ?? Enumerable.Empty<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (orderedLines.Count == 0)
                return;

            node.CommonLines ??= new List<string>();

            int battleIndex = FindFirstBattleLineIndex(node.CommonLines);
            if (battleIndex < 0)
            {
                node.CommonLines.AddRange(orderedLines);
                return;
            }

            var reorderedCommon = new List<string>();
            reorderedCommon.AddRange(node.CommonLines.Take(battleIndex));
            reorderedCommon.AddRange(orderedLines);
            reorderedCommon.AddRange(node.CommonLines.Skip(battleIndex));
            node.CommonLines = reorderedCommon;
        }

        private static List<string> RemoveLineOccurrences(IEnumerable<string> source, IEnumerable<string> linesToRemove)
        {
            var result = new List<string>();
            var pendingCounts = BuildLineCounts(linesToRemove);

            foreach (var line in source ?? Enumerable.Empty<string>())
            {
                string key = line ?? string.Empty;
                if (pendingCounts.TryGetValue(key, out int remaining) && remaining > 0)
                {
                    if (remaining == 1)
                        pendingCounts.Remove(key);
                    else
                        pendingCounts[key] = remaining - 1;

                    continue;
                }

                result.Add(line);
            }

            return result;
        }

        private static Dictionary<string, int> BuildLineCounts(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                string key = line ?? string.Empty;
                if (result.TryGetValue(key, out int count))
                    result[key] = count + 1;
                else
                    result[key] = 1;
            }

            return result;
        }

        private static List<string> GetCommonPrefix(List<List<string>> source)
        {
            var result = new List<string>();
            if (source == null || source.Count == 0)
                return result;

            int minCount = source.Min(lines => lines?.Count ?? 0);
            for (int i = 0; i < minCount; i++)
            {
                string first = source[0][i] ?? string.Empty;
                if (source.All(lines => string.Equals(lines[i] ?? string.Empty, first, StringComparison.Ordinal)))
                    result.Add(first);
                else
                    break;
            }

            return result;
        }

        private static int FindFirstBattleLineIndex(List<string> lines)
        {
            if (lines == null)
                return -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (IsBattleLine(lines[i]))
                    return i;
            }

            return -1;
        }

        private static bool IsBattleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.TrimStart();
            return trimmed.StartsWith("Битва ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("Битва с ", StringComparison.Ordinal);
        }

        private static bool IsConditionalPartyStatusLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return line.TrimStart().StartsWith("CONDITION ", StringComparison.Ordinal);
        }

        private static bool IsPromotableFlatConditionLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return line.TrimStart().StartsWith("ПРОВЕРКА УСЛОВИЯ:", StringComparison.Ordinal);
        }

        private static bool HasMeaningfulLines(List<string> lines)
        {
            return lines != null && lines.Any(line => !string.IsNullOrWhiteSpace(line));
        }

        private static VariantTreeNode CompressVariantTree(VariantTreeNode node)
        {
            if (node == null)
                return null;

            for (int i = 0; i < node.Children.Count; i++)
                node.Children[i] = CompressVariantTree(node.Children[i]);

            while (node.Children.Count == 1 &&
                   node.DirectVariants.Count == 0 &&
                   node.CommonLines.Count == 0 &&
                   string.IsNullOrWhiteSpace(node.Label))
            {
                node = node.Children[0];
            }

            while (node.Children.Count == 1 &&
                   node.DirectVariants.Count == 0 &&
                   node.CommonLines.Count == 0 &&
                   !string.IsNullOrWhiteSpace(node.Label) &&
                   string.Equals(node.Children[0]?.Label, node.Label, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       NormalizeHeaderAnnotation(node.Children[0]?.HeaderAnnotation) ?? string.Empty,
                       NormalizeHeaderAnnotation(node.HeaderAnnotation) ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase))
            {
                node = node.Children[0];
            }

            return node;
        }

        private static decimal GetVariantRenderOrderKey(VariantRenderItem variant)
        {
            return GetPathOrderKey(variant?.Variant);
        }

        private static decimal GetPathOrderKey(PathVariantInfo variant)
        {
            if (variant == null)
                return decimal.MaxValue;

            decimal baseOrderKey = variant.PathOrderKey > 0
                ? variant.PathOrderKey
                : variant.PathId;
            int outcomePriority = GetVariantOutcomeDisplayPriority(variant);

            if (outcomePriority >= int.MaxValue / 2)
                return decimal.MaxValue;

            return outcomePriority * VariantOutcomeOrderStride + baseOrderKey;
        }

        private static int GetVariantOutcomeDisplayPriority(PathVariantInfo variant)
        {
            if (variant == null)
                return int.MaxValue;

            if (variant.HasTeleportTarget && (variant.Texts == null || variant.Texts.Count == 0))
                return 0;

            if (IsPureEmptyVariant(variant))
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

            if (VariantTextsContain(variant, "IMPROPER") ||
                VariantTextsContain(variant, "INCORRECT") ||
                VariantTextsContain(variant, "WRONG"))
            {
                return 2;
            }

            return 1;
        }

        private static bool IsPureEmptyVariant(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.Texts != null && variant.Texts.Count > 0)
                return false;

            return !HasAnyVariantOutcome(variant);
        }

        private static bool HasAnyVariantOutcome(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (variant.CallsRandomEncounter ||
                variant.RandomEncounterMonsterPowerCap.HasValue ||
                variant.RandomEncounterMonsterLevelCap.HasValue ||
                variant.RandomEncounterMonsterBatchCountCap.HasValue ||
                variant.DarkeningLevel.HasValue ||
                variant.RandomEncounterChance.HasValue ||
                variant.RandomEncounterRubicon.HasValue ||
                variant.BattleMonsterStrengthAdjustment != 0 ||
                variant.HasTeleportTarget ||
                variant.BattleMonsterCount.HasValue ||
                variant.BattleMonsterCountRange != null ||
                variant.IsBattleMonsterCountIndeterminate ||
                (variant.PersistentCounterProgressions?.Count ?? 0) > 0 ||
                (variant.DynamicRandomBoundDependencies?.Count ?? 0) > 0 ||
                variant.HasAnyTableLoad)
            {
                return true;
            }

            if (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0)
                return true;

            if (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0)
                return true;

            if (variant.LoadedValues != null && variant.LoadedValues.Count > 0)
                return true;

            return variant.PartyEffects != null &&
                   variant.PartyEffects.Any(PartyEffectSemantics.IsSemanticOutcomeEffect);
        }

        private static bool VariantTextsContain(PathVariantInfo variant, string text)
        {
            if (variant?.Texts == null || string.IsNullOrWhiteSpace(text))
                return false;

            return variant.Texts.Any(line =>
                line?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int GetVariantOccurrenceOrderKey(PathVariantInfo variant)
        {
            if (variant == null)
                return int.MaxValue;

            int minOccurrence = int.MaxValue;

            if (variant.OccurrenceIndices != null && variant.OccurrenceIndices.Count > 0)
                minOccurrence = Math.Min(minOccurrence, variant.OccurrenceIndices.Min());

            if (variant.OccurrenceRanges != null && variant.OccurrenceRanges.Count > 0)
            {
                foreach (var range in variant.OccurrenceRanges.Where(range => range != null))
                    minOccurrence = Math.Min(minOccurrence, range.Start);
            }

            // Отсутствие occurrence-ограничений считаем "самым ранним" случаем,
            // чтобы базовые/обычные исходы показывались перед специальными.
            return minOccurrence == int.MaxValue ? 0 : minOccurrence;
        }

        private static int GetVariantOccurrenceOrderKey(VariantRenderItem variant)
        {
            return GetVariantOccurrenceOrderKey(variant?.Variant);
        }

        private static decimal GetNodeRenderOrderKey(VariantTreeNode node)
        {
            return GetAllVariants(node)
                .Where(variant => variant != null)
                .Select(GetVariantRenderOrderKey)
                .DefaultIfEmpty(decimal.MaxValue)
                .Min();
        }

        private static int GetNodeOccurrenceOrderKey(VariantTreeNode node)
        {
            return GetAllVariants(node)
                .Where(variant => variant != null)
                .Select(GetVariantOccurrenceOrderKey)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
        }

        private static List<OrderedRenderEntry> BuildOrderedRenderEntries(
            VariantTreeNode parentNode,
            IEnumerable<VariantTreeNode> children,
            IEnumerable<VariantRenderItem> directVariants,
            IReadOnlyDictionary<VariantRenderItem, string> syntheticDirectLabels = null)
        {
            var result = new List<OrderedRenderEntry>();
            var promptChoiceOrder = BuildPromptChoiceOrder(parentNode?.CommonLines);
            bool parentHasMultiNumericPrompt = HasMultiNumericPromptOptions(parentNode?.CommonLines);
            var childList = (children ?? Enumerable.Empty<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();
            bool canPromoteNoToChoice =
                childList.Count > 0 &&
                PromptContainsChoiceLabel(parentNode?.CommonLines, "N)") &&
                !HasNodeWithLabel(parentNode, "N)");

            foreach (var child in childList)
            {
                bool hasChoiceOrder = TryGetChoiceOrderKey(child.Label, promptChoiceOrder, out int choiceOrderKey);
                bool mixedPartyScanCancelChoice =
                    ContainsMixedPartyScanSubsetSummary(parentNode) &&
                    IsLikelyCancelNode(child);
                int displayPriority = mixedPartyScanCancelChoice
                    ? -1
                    : (parentHasMultiNumericPrompt && HasPromptOptionLabels(child.CommonLines)
                        ? 1
                        : (IsEmptyOrNoOpNode(child) ? 1 : 0));
                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetNodeOccurrenceOrderKey(child),
                    DisplayPriority = displayPriority,
                    PathOrderKey = GetNodeRenderOrderKey(child),
                    ChoiceOrderKey = choiceOrderKey,
                    HasChoiceOrderKey = hasChoiceOrder,
                    ChildNode = child
                });
            }

            foreach (var variant in directVariants ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (variant == null)
                    continue;

                string syntheticLabel = null;
                syntheticDirectLabels?.TryGetValue(variant, out syntheticLabel);
                bool hasChoiceOrder = TryGetChoiceOrderKey(syntheticLabel, promptChoiceOrder, out int choiceOrderKey);
                bool promotedNoChoice = canPromoteNoToChoice && ShouldRenderAsNoChoiceVariant(variant);
                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetVariantOccurrenceOrderKey(variant),
                    DisplayPriority = promotedNoChoice ? -1 : (IsEmptyOrNoOpVariant(variant) ? 1 : 0),
                    PathOrderKey = GetVariantRenderOrderKey(variant),
                    ChoiceOrderKey = choiceOrderKey,
                    HasChoiceOrderKey = hasChoiceOrder,
                    DirectVariant = variant
                });
            }

            bool useChoiceOrdering = ShouldOrderEntriesByChoiceLabel(result);
            if (useChoiceOrdering)
            {
                return result
                    .OrderBy(entry => entry.ChoiceOrderKey)
                    .ThenBy(entry => entry.OccurrenceOrderKey)
                    .ThenBy(entry => entry.DisplayPriority)
                    .ThenBy(entry => entry.PathOrderKey)
                    .ThenBy(entry => entry.ChildNode == null ? 1 : 0)
                    .ToList();
            }

            return result
                .OrderBy(entry => entry.DisplayPriority < 0 ? entry.DisplayPriority : 0)
                .ThenBy(entry => entry.OccurrenceOrderKey)
                .ThenBy(entry => entry.DisplayPriority)
                .ThenBy(entry => entry.PathOrderKey)
                .ThenBy(entry => entry.ChildNode == null ? 1 : 0)
                .ToList();
        }

        private static bool IsEmptyOrNoOpNode(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)))
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node.HeaderAnnotation)))
                return false;

            if ((node.CommonLines?.Any(line => !string.IsNullOrWhiteSpace(line)) ?? false))
                return false;

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null)
                .ToList();
            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();

            if (directVariants.Count == 0 && children.Count == 0)
                return false;

            return directVariants.All(IsEmptyOrNoOpVariant) &&
                   children.All(IsEmptyOrNoOpNode);
        }

        private static Dictionary<string, int> BuildPromptChoiceOrder(IEnumerable<string> commonLines)
        {
            string promptText = string.Join("\n", commonLines ?? Enumerable.Empty<string>());
            var labels = ExtractPromptOptionLabels(promptText)
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < labels.Count; i++)
            {
                if (!result.ContainsKey(labels[i]))
                    result[labels[i]] = i;
            }

            return result;
        }

        private static bool ShouldOrderEntriesByChoiceLabel(List<OrderedRenderEntry> entries)
        {
            if (entries == null || entries.Count < 4)
                return false;

            if (entries.Any(entry => entry == null || !entry.HasChoiceOrderKey))
                return false;

            return entries
                .Select(entry => entry.ChoiceOrderKey)
                .Distinct()
                .Count() > 1;
        }

        private static bool TryGetChoiceOrderKey(
            string label,
            IReadOnlyDictionary<string, int> promptChoiceOrder,
            out int key)
        {
            key = int.MaxValue;
            string normalized = NormalizeChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (promptChoiceOrder != null && promptChoiceOrder.TryGetValue(normalized, out int promptOrder))
            {
                key = promptOrder;
                return true;
            }

            string token = normalized.Trim();
            if (token.EndsWith(")", StringComparison.Ordinal))
                token = token.Substring(0, token.Length - 1).Trim();

            var randomMatch = Regex.Match(
                token,
                @"^RND\((\d+)\)\s*=\s*(\d+)(?:/(\d+))?$",
                RegexOptions.IgnoreCase);
            if (randomMatch.Success &&
                int.TryParse(randomMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int randomUpperBound) &&
                int.TryParse(randomMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int firstRandomValue))
            {
                key = 50000 + randomUpperBound * 1000 + firstRandomValue;
                return true;
            }

            if (string.Equals(token, "ESC", StringComparison.OrdinalIgnoreCase))
            {
                key = 0;
                return true;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericValue))
            {
                key = 100 + numericValue;
                return true;
            }

            if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
            {
                char upper = char.ToUpperInvariant(token[0]);
                key = char.IsDigit(upper)
                    ? 100 + (upper - '0')
                    : 1000 + (upper - 'A');
                return true;
            }

            return false;
        }

        private static void RenderTopLevelGroup(TopLevelVariantGroup group, StringBuilder sb, int groupNumber)
        {
            var renderableChildren = (group.TreeRoot?.Children ?? Enumerable.Empty<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = group.TreeRoot?.DirectVariants?.Count(v => v != null) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(group.TreeRoot, renderableChildren.Count);

            var directVariants = (group.TreeRoot?.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Where(v => ShouldRenderDirectVariant(
                        v,
                        siblingDirectVariantCount,
                        renderableChildren.Count,
                        suppressEmptyPromptTerminalDirectVariants) &&
                    !ShouldSuppressAsRedundantTopLevelNoOpLeaf(group, v))
                .ToList();

            var singleLeafItem = renderableChildren.Count == 0
                ? GetSingleLeafVariantItem(group?.TreeRoot)
                : null;
            var singleLeaf = singleLeafItem?.Variant;
            var singleLeafLines = singleLeafItem?.Lines?.ToList();

            string header = $"Вариант {groupNumber}";
            string sharedProbabilityLine = singleLeaf == null
                ? GetSharedProbabilityLine(group?.TreeRoot)
                : null;
            var sharedGuardPredicates = singleLeaf == null
                ? GetSharedGuardPredicates(group?.TreeRoot)
                : null;
            string sharedGuardKey = BuildGuardConditionKey(sharedGuardPredicates);
            string sharedGuardAnnotation = BuildGuardHeaderAnnotation(sharedGuardPredicates);
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(
                sharedProbabilityLine,
                !string.IsNullOrWhiteSpace(sharedGuardAnnotation));

            var sharedAnnotations = new List<string>();
            if (!string.IsNullOrWhiteSpace(group?.HeaderAnnotation))
                sharedAnnotations.Add(group.HeaderAnnotation);
            if (singleLeaf != null)
            {
                AddDistinctHeaderAnnotations(
                    sharedAnnotations,
                    BuildVariantHeaderAnnotations(singleLeaf));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(sharedGuardAnnotation))
                    sharedAnnotations.Add(sharedGuardAnnotation);
                if (!string.IsNullOrWhiteSpace(sharedProbabilityAnnotation))
                    sharedAnnotations.Add(sharedProbabilityAnnotation);
            }
            string topAnnotationText = BuildVariantHeaderAnnotationText(sharedAnnotations);
            if (!string.IsNullOrWhiteSpace(topAnnotationText))
                header += $" ({topAnnotationText})";

            string groupLabel = NormalizeUserVisibleChoiceLabel(group?.Label);
            if (!string.IsNullOrWhiteSpace(groupLabel))
                header += $": {groupLabel}";
            else
                header += ":";

            sb.AppendLine(header);
            bool headerContainsProbability = VariantHeaderContainsProbability(header);

            AppendIndentedDisplayLines(
                sb,
                "   ",
                group.TreeRoot?.CommonLines,
                headerContainsProbability);

            bool isPureTopLevelNoOp = IsPureTopLevelNoOpGroup(group);
            if (isPureTopLevelNoOp)
                return;

            if (directVariants.Count == 1 && renderableChildren.Count == 0)
            {
                AppendIndentedDisplayLines(
                    sb,
                    "   ",
                    singleLeafLines ?? directVariants[0].Lines,
                    headerContainsProbability);
                return;
            }

            bool needGapAfterCommon = (group.TreeRoot?.CommonLines?.Count ?? 0) > 0 &&
                                      (renderableChildren.Count > 0 || directVariants.Count > 0);
            if (needGapAfterCommon)
                sb.AppendLine();

            bool canPromoteNoToChoice =
                renderableChildren.Count > 0 &&
                PromptContainsChoiceLabel(group.TreeRoot?.CommonLines, "N)") &&
                !HasNodeWithLabel(group.TreeRoot, "N)");
            int noChoiceCount = directVariants.Count(v => ShouldRenderAsNoChoiceVariant(v));
            var syntheticChoiceLabels = BuildSyntheticChoiceLabels(group.TreeRoot, directVariants, renderableChildren);
            var syntheticChildLabels = BuildSyntheticChildLabels(group.TreeRoot, renderableChildren, directVariants, syntheticChoiceLabels);

            foreach (var child in renderableChildren)
            {
                if (syntheticChildLabels.TryGetValue(child, out string syntheticChildLabel) && string.IsNullOrWhiteSpace(child.Label))
                    child.Label = syntheticChildLabel;
            }

            int childIndex = 1;
            bool wroteAnyChild = false;

            foreach (var entry in BuildOrderedRenderEntries(group.TreeRoot, renderableChildren, directVariants, syntheticChoiceLabels))
            {
                if (wroteAnyChild)
                    sb.AppendLine();

                if (entry.ChildNode != null)
                {
                    RenderVariantTreeNode(
                        entry.ChildNode,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        sharedGuardKey);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(
                        syntheticLabel,
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        sharedGuardKey);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf(
                        "N)",
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        sharedGuardKey);
                }
                else
                {
                    RenderLooseVariant(
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        sharedGuardKey);
                }

                wroteAnyChild = true;
            }
        }


        private static bool IsPureTopLevelNoOpGroup(TopLevelVariantGroup group)
        {
            if (group?.TreeRoot == null)
                return false;

            if ((group.TreeRoot.Children?.Any(IsRenderableStructuralNode) ?? false))
                return false;

            if (!IsNoOpOnly(group.TreeRoot.CommonLines))
                return false;

            return !(group.TreeRoot.DirectVariants?.Any(v => v != null &&
                IsRenderableDirectVariant(v) &&
                !ShouldSuppressAsRedundantTopLevelNoOpLeaf(group, v)) ?? false);
        }

        private static bool ShouldSuppressAsRedundantTopLevelNoOpLeaf(TopLevelVariantGroup group, VariantRenderItem item)
        {
            if (group?.TreeRoot == null || item == null)
                return false;

            return IsNoOpOnly(group.TreeRoot.CommonLines) &&
                   !(group.TreeRoot.Children?.Any(IsRenderableStructuralNode) ?? false) &&
                   ShouldRenderAsNoChoiceVariant(item);
        }

        private static bool TryRenderPartyScanLoopBody(
            VariantTreeNode node,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            bool headerContainsProbability)
        {
            var scanNode = FindImmediatePartyScanPromptNode(node);
            if (scanNode == null || !PartyScanSiblingsAreAggregateOnly(node, scanNode))
                return false;

            var branches = CollectPartyScanOutcomeBranches(scanNode);
            if (branches.Count < 2)
                return false;

            var summaries = BuildPartyScanOutcomeSummaries(branches);
            if (summaries.Count < 2)
                return false;

            string indent = new string(' ', depth * 3);
            if ((node.CommonLines?.Count ?? 0) > 0)
                sb.AppendLine();

            AppendIndentedDisplayLines(
                sb,
                indent + "      ",
                scanNode.CommonLines,
                headerContainsProbability);

            var aggregateLines = BuildPartyScanAggregateLines(node, scanNode, summaries);
            if (TryAttachPartyScanAggregateLinesToOutcome(summaries, aggregateLines))
                aggregateLines = new List<string>();

            int nestedIndex = 1;
            bool wroteAnyOutcome = false;
            foreach (var summary in summaries
                .OrderBy(summary => summary.OrderKey)
                .ThenBy(summary => summary.Label ?? string.Empty, StringComparer.Ordinal))
            {
                if (wroteAnyOutcome || (scanNode.CommonLines?.Count ?? 0) > 0)
                    sb.AppendLine();

                RenderPartyScanOutcomeSummary(
                    summary,
                    sb,
                    new List<int>(numbering) { nestedIndex++ },
                    depth + 1,
                    headerContainsProbability);
                wroteAnyOutcome = true;
            }

            if (aggregateLines.Count > 0)
            {
                sb.AppendLine();
                AppendIndentedDisplayLine(
                    sb,
                    indent + "   ",
                    BuildPartyScanAggregateHeader(summaries),
                    headerContainsProbability);
                AppendIndentedDisplayLines(
                    sb,
                    indent + "      ",
                    aggregateLines,
                    headerContainsProbability);
            }

            return true;
        }

        private static VariantTreeNode FindImmediatePartyScanPromptNode(VariantTreeNode node)
        {
            if (node == null)
                return null;

            var candidates = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .Where(child => HasPartyScanPromptLines(child.CommonLines))
                .Where(ContainsConditionalPartyScanOutcome)
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool HasPartyScanPromptLines(IEnumerable<string> lines)
        {
            string promptText = string.Join("\n", lines ?? Enumerable.Empty<string>());
            if (string.IsNullOrWhiteSpace(promptText))
                return false;

            return ExtractPromptOptionLabels(promptText).Count >= 2;
        }

        private static bool ContainsConditionalPartyScanOutcome(VariantTreeNode node)
        {
            return GetAllVariants(node)
                .Any(item => item?.Variant?.PartyEffects?.Any(IsConditionalLoopSubsetOutcomeEffect) == true);
        }

        private static bool HasConditionalPartyScanOutcome(VariantRenderItem item)
        {
            return item?.Variant?.PartyEffects?.Any(IsConditionalLoopSubsetOutcomeEffect) == true;
        }

        private static bool PartyScanSiblingsAreAggregateOnly(VariantTreeNode node, VariantTreeNode scanNode)
        {
            if (node == null || scanNode == null)
                return false;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (ReferenceEquals(child, scanNode) || !IsRenderableStructuralNode(child))
                    continue;

                if (!IsPartyScanAggregateOnlyNode(child))
                    return false;
            }

            foreach (var variant in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (!IsPartyScanAggregateOnlyVariant(variant))
                    return false;
            }

            return true;
        }

        private static bool IsPartyScanAggregateOnlyNode(VariantTreeNode node)
        {
            if (node == null)
                return true;

            if ((node.CommonLines ?? new List<string>())
                .Any(line => !IsIgnorablePartyScanAggregateLine(line)))
            {
                return false;
            }

            if ((node.DirectVariants ?? new List<VariantRenderItem>())
                .Any(variant => !IsPartyScanAggregateOnlyVariant(variant)))
            {
                return false;
            }

            return (node.Children ?? new List<VariantTreeNode>())
                .All(IsPartyScanAggregateOnlyNode);
        }

        private static bool IsPartyScanAggregateOnlyVariant(VariantRenderItem item)
        {
            if (item == null)
                return true;

            if (HasConditionalPartyScanOutcome(item))
                return false;

            var meaningfulLines = (item.Lines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (meaningfulLines.Count == 0 || IsNoOpOnly(meaningfulLines))
                return true;

            return meaningfulLines.All(IsPartyScanAggregateLine);
        }

        private static bool IsIgnorablePartyScanAggregateLine(string line)
        {
            return string.IsNullOrWhiteSpace(line) ||
                   IsPartyScanAggregateLine(line) ||
                   string.Equals(line.Trim(), "Ничего не происходит", StringComparison.Ordinal) ||
                   string.Equals(line.Trim(), "Ничего не происходит (не выполнены условия для наступления ни одного варианта)", StringComparison.Ordinal);
        }

        private static bool IsPartyScanAggregateLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.TrimStart();
            return trimmed.StartsWith("Телепорт на ", StringComparison.Ordinal);
        }

        private static List<PartyScanOutcomeBranch> CollectPartyScanOutcomeBranches(VariantTreeNode scanNode)
        {
            var result = new List<PartyScanOutcomeBranch>();
            CollectPartyScanOutcomeBranches(scanNode, new List<string>(), null, result);
            return result;
        }

        private static void CollectPartyScanOutcomeBranches(
            VariantTreeNode node,
            List<string> inheritedLines,
            string inheritedLabel,
            List<PartyScanOutcomeBranch> result)
        {
            if (node == null || result == null)
                return;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (child == null || !IsRenderableStructuralNode(child))
                    continue;

                var childLines = new List<string>(inheritedLines ?? new List<string>());
                childLines.AddRange(child.CommonLines ?? new List<string>());

                string childLabel = !string.IsNullOrWhiteSpace(child.Label)
                    ? NormalizeChoiceLabel(child.Label)
                    : inheritedLabel;

                CollectPartyScanOutcomeBranches(child, childLines, childLabel, result);
            }

            foreach (var item in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (!HasConditionalPartyScanOutcome(item))
                    continue;

                var lines = new List<string>(inheritedLines ?? new List<string>());
                lines.AddRange(item.Lines ?? new List<string>());

                result.Add(new PartyScanOutcomeBranch
                {
                    Variant = item,
                    Label = NormalizeChoiceLabel(inheritedLabel),
                    Lines = lines,
                    OrderKey = GetVariantRenderOrderKey(item),
                    HasAdjustedMemory = (item?.Variant?.AdjustedMemoryAddresses?.Count ?? 0) > 0
                });
            }
        }

        private static string BuildPartyScanOutcomeLabel(string inheritedLabel, IEnumerable<string> lines)
        {
            var meaningfulLines = (lines ?? Enumerable.Empty<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            if (meaningfulLines.Any(IsCorrectPartyScanAnswerLine))
                return meaningfulLines.First(IsCorrectPartyScanAnswerLine);

            if (meaningfulLines.Any(IsWrongPartyScanAnswerLine))
                return meaningfulLines.First(IsWrongPartyScanAnswerLine);

            return NormalizeChoiceLabel(inheritedLabel);
        }

        private static List<PartyScanOutcomeSummary> BuildPartyScanOutcomeSummaries(
            IEnumerable<PartyScanOutcomeBranch> branches)
        {
            var summaries = new Dictionary<string, PartyScanOutcomeSummary>(StringComparer.Ordinal);

            foreach (var branch in branches ?? Enumerable.Empty<PartyScanOutcomeBranch>())
            {
                if (branch == null)
                    continue;

                var perMemberLines = (branch.Lines ?? new List<string>())
                    .Where(line => !IsPartyScanAggregateLine(line))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                string label = BuildPartyScanOutcomeLabel(branch.Label, perMemberLines);
                perMemberLines = RemovePartyScanOutcomeHeaderLine(perMemberLines, label);

                if (perMemberLines.Count == 0 && string.IsNullOrWhiteSpace(label))
                    continue;

                string key = string.Join("\n", new[] { label ?? string.Empty }
                    .Concat(perMemberLines.Select(line => line ?? string.Empty)));
                if (!summaries.TryGetValue(key, out var summary))
                {
                    summary = new PartyScanOutcomeSummary
                    {
                        Variant = branch.Variant?.Variant,
                        Label = label,
                        Lines = perMemberLines,
                        OrderKey = branch.OrderKey,
                        HasCorrectText = HasCorrectPartyScanOutcome(label, perMemberLines),
                        HasAdjustedMemory = branch.HasAdjustedMemory
                    };
                    summaries[key] = summary;
                }
                else
                {
                    if (summary.Variant == null)
                        summary.Variant = branch.Variant?.Variant;
                    if (string.IsNullOrWhiteSpace(summary.Label))
                        summary.Label = label;
                    summary.OrderKey = Math.Min(summary.OrderKey, branch.OrderKey);
                    summary.HasCorrectText |= HasCorrectPartyScanOutcome(label, perMemberLines);
                    summary.HasAdjustedMemory |= branch.HasAdjustedMemory;
                }

                foreach (var aggregateLine in (branch.Lines ?? new List<string>())
                    .Where(IsPartyScanAggregateLine))
                {
                    if (!summary.AggregateLines.Contains(aggregateLine, StringComparer.Ordinal))
                        summary.AggregateLines.Add(aggregateLine);
                }
            }

            return summaries.Values.ToList();
        }

        private static List<string> RemovePartyScanOutcomeHeaderLine(IEnumerable<string> lines, string label)
        {
            var result = new List<string>();
            bool removed = false;
            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                if (!removed &&
                    !string.IsNullOrWhiteSpace(label) &&
                    string.Equals(line?.Trim(), label.Trim(), StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                result.Add(line);
            }

            return result;
        }

        private static bool HasCorrectPartyScanOutcome(string label, IEnumerable<string> lines)
        {
            return IsCorrectPartyScanAnswerLine(label) ||
                   (lines ?? Enumerable.Empty<string>())
                   .Any(IsCorrectPartyScanAnswerLine);
        }

        private static bool TryAttachPartyScanAggregateLinesToOutcome(
            List<PartyScanOutcomeSummary> summaries,
            List<string> aggregateLines)
        {
            if (summaries == null || aggregateLines == null || aggregateLines.Count == 0)
                return false;

            var targets = summaries
                .Where(summary => summary != null && summary.HasCorrectText && summary.HasAdjustedMemory)
                .ToList();
            if (targets.Count != 1)
                return false;

            foreach (var line in aggregateLines)
            {
                if (!targets[0].Lines.Contains(line, StringComparer.Ordinal))
                    targets[0].Lines.Add(line);
            }

            return true;
        }

        private static List<string> BuildPartyScanAggregateLines(
            VariantTreeNode parentNode,
            VariantTreeNode scanNode,
            IEnumerable<PartyScanOutcomeSummary> summaries)
        {
            var result = new List<string>();

            foreach (var line in (summaries ?? Enumerable.Empty<PartyScanOutcomeSummary>())
                .SelectMany(summary => summary?.AggregateLines ?? new List<string>()))
            {
                AddDistinctPartyScanAggregateLine(result, line);
            }

            foreach (var item in parentNode?.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (!IsPartyScanAggregateOnlyVariant(item))
                    continue;

                foreach (var line in item.Lines ?? new List<string>())
                    AddDistinctPartyScanAggregateLine(result, line);
            }

            foreach (var child in parentNode?.Children ?? new List<VariantTreeNode>())
            {
                if (ReferenceEquals(child, scanNode) || !IsPartyScanAggregateOnlyNode(child))
                    continue;

                foreach (var line in GetPartyScanAggregateLines(child))
                    AddDistinctPartyScanAggregateLine(result, line);
            }

            return result;
        }

        private static List<string> GetPartyScanAggregateLines(VariantTreeNode node)
        {
            var result = new List<string>();
            if (node == null)
                return result;

            foreach (var line in node.CommonLines ?? new List<string>())
                AddDistinctPartyScanAggregateLine(result, line);

            foreach (var item in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                foreach (var line in item?.Lines ?? new List<string>())
                    AddDistinctPartyScanAggregateLine(result, line);
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                foreach (var line in GetPartyScanAggregateLines(child))
                    AddDistinctPartyScanAggregateLine(result, line);
            }

            return result;
        }

        private static void AddDistinctPartyScanAggregateLine(List<string> target, string line)
        {
            if (target == null || !IsPartyScanAggregateLine(line))
                return;

            if (!target.Contains(line, StringComparer.Ordinal))
                target.Add(line);
        }

        private static string BuildPartyScanAggregateHeader(IEnumerable<PartyScanOutcomeSummary> summaries)
        {
            bool hasCorrectAdjustedOutcome = (summaries ?? Enumerable.Empty<PartyScanOutcomeSummary>())
                .Any(summary => summary != null && summary.HasCorrectText && summary.HasAdjustedMemory);

            return hasCorrectAdjustedOutcome
                ? "После обработки партии, если был хотя бы один правильный ответ:"
                : "После обработки партии, если итоговое условие выполнено:";
        }

        private static void RenderPartyScanOutcomeSummary(
            PartyScanOutcomeSummary summary,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            bool headerContainsProbability)
        {
            if (summary == null)
                return;

            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            string summaryLabel = NormalizeUserVisibleChoiceLabel(summary.Label);
            if (!string.IsNullOrWhiteSpace(summaryLabel))
                header += $": {summaryLabel}";
            else
                header += ":";

            sb.AppendLine(header);
            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                summary.Lines,
                headerContainsProbability);
        }

        private static void RenderVariantTreeNode(
            VariantTreeNode node,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null)
        {
            if (!IsRenderableStructuralNode(node))
                return;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(node, renderableChildren.Count);
            var renderableDirectVariants = OrderDirectVariants(node.DirectVariants)
                .Where(v => ShouldRenderDirectVariant(
                    v,
                    siblingDirectVariantCount,
                    renderableChildren.Count,
                    suppressEmptyPromptTerminalDirectVariants))
                .ToList();

            string indent = new string(' ', depth * 3);
            string variantNumber = string.Join(".", numbering);
            var singleLeafItem = renderableChildren.Count == 0
                ? GetSingleLeafVariantItem(node)
                : null;
            var singleLeaf = singleLeafItem?.Variant;
            var singleLeafLines = singleLeafItem?.Lines?.ToList();
            string nodeLabel = NormalizeUserVisibleChoiceLabel(node.Label);
            var nodeCommonLines = node.CommonLines?.ToList() ?? new List<string>();
            string promotedCommonLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(nodeCommonLines)
                : null;
            string promotedSingleLeafLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(singleLeafLines)
                : null;
            string sharedProbabilityLine = singleLeaf == null
                ? GetSharedProbabilityLine(node, inheritedProbabilityLine)
                : null;
            var sharedGuardPredicates = singleLeaf == null
                ? GetSharedGuardPredicates(node, inheritedGuardKey)
                : null;
            string sharedGuardKey = BuildGuardConditionKey(sharedGuardPredicates);
            string sharedGuardAnnotation = BuildGuardHeaderAnnotation(sharedGuardPredicates);
            bool conditionVisibleForSharedProbability =
                !string.IsNullOrWhiteSpace(sharedGuardAnnotation) ||
                HasInheritedGuardForAllVariants(node, inheritedGuardKey);
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(
                sharedProbabilityLine,
                conditionVisibleForSharedProbability);

            string header = $"{indent}Вариант {variantNumber}";
            if (singleLeaf != null)
            {
                var annotations = BuildVariantHeaderAnnotations(singleLeaf, inheritedProbabilityLine, inheritedGuardKey);
                if (!string.IsNullOrWhiteSpace(node.HeaderAnnotation))
                    annotations.Insert(0, node.HeaderAnnotation);
                string annotationText = BuildVariantHeaderAnnotationText(annotations);
                if (!string.IsNullOrWhiteSpace(annotationText))
                    header += $" ({annotationText})";
            }
            else
            {
                var sharedAnnotations = new List<string>();
                if (!string.IsNullOrWhiteSpace(node.HeaderAnnotation))
                    sharedAnnotations.Add(node.HeaderAnnotation);
                if (!string.IsNullOrWhiteSpace(sharedGuardAnnotation))
                    sharedAnnotations.Add(sharedGuardAnnotation);
                if (!string.IsNullOrWhiteSpace(sharedProbabilityAnnotation))
                    sharedAnnotations.Add(sharedProbabilityAnnotation);
                string annotationText = BuildVariantHeaderAnnotationText(sharedAnnotations);
                if (!string.IsNullOrWhiteSpace(annotationText))
                    header += $" ({annotationText})";
            }

            if (!string.IsNullOrWhiteSpace(nodeLabel))
                header += $": {nodeLabel}";
            else if (!string.IsNullOrWhiteSpace(promotedCommonLabel))
                header += $": {promotedCommonLabel}";
            else if (!string.IsNullOrWhiteSpace(promotedSingleLeafLabel))
                header += $": {promotedSingleLeafLabel}";
            else
                header += ":";

            sb.AppendLine(header);
            bool headerContainsProbability = VariantHeaderContainsProbability(header);

            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                nodeCommonLines,
                headerContainsProbability);

            if (TryRenderPartyScanLoopBody(
                    node,
                    sb,
                    numbering,
                    depth,
                    headerContainsProbability))
            {
                return;
            }

            if (renderableDirectVariants.Count == 1 && renderableChildren.Count == 0)
            {
                AppendIndentedDisplayLines(
                    sb,
                    indent + "   ",
                    singleLeafLines ?? renderableDirectVariants[0].Lines,
                    headerContainsProbability);
                return;
            }

            bool needGapAfterCommon =
                nodeCommonLines.Count > 0 &&
                (renderableChildren.Count > 0 || renderableDirectVariants.Count > 0);
            if (needGapAfterCommon)
                sb.AppendLine();

            bool canPromoteNoToChoice =
                renderableChildren.Count > 0 &&
                PromptContainsChoiceLabel(node.CommonLines, "N)") &&
                !HasNodeWithLabel(node, "N)");
            int noChoiceCount = renderableDirectVariants.Count(v => ShouldRenderAsNoChoiceVariant(v));
            var syntheticChoiceLabels = BuildSyntheticChoiceLabels(node, renderableDirectVariants, renderableChildren);
            var syntheticChildLabels = BuildSyntheticChildLabels(node, renderableChildren, renderableDirectVariants, syntheticChoiceLabels);

            foreach (var child in renderableChildren)
            {
                if (syntheticChildLabels.TryGetValue(child, out string syntheticChildLabel) && string.IsNullOrWhiteSpace(child.Label))
                    child.Label = syntheticChildLabel;
            }

            int nestedIndex = 1;
            bool wroteAny = false;
            string descendantSuppressedProbabilityLine = sharedProbabilityLine ?? inheritedProbabilityLine;
            string descendantSuppressedGuardKey = sharedGuardKey ?? inheritedGuardKey;

            foreach (var entry in BuildOrderedRenderEntries(node, renderableChildren, renderableDirectVariants, syntheticChoiceLabels))
            {
                if (wroteAny)
                    sb.AppendLine();

                if (entry.ChildNode != null)
                {
                    RenderVariantTreeNode(
                        entry.ChildNode,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(
                        syntheticLabel,
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf(
                        "N)",
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey);
                }
                else
                {
                    RenderLooseVariant(
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey);
                }

                wroteAny = true;
            }
        }

        private static bool ShouldRenderAsNoChoiceVariant(VariantRenderItem item)
        {
            if (item == null)
                return false;

            if (ShouldDisplayGuardCondition(item.Variant))
                return false;

            if (GetRelevantBranchChoices(item.Variant).Any())
                return false;

            return item.Lines == null || item.Lines.Count == 0 || IsNoOpOnly(item.Lines);
        }

        private static bool HasNodeWithLabel(VariantTreeNode node, string label)
        {
            if (node == null || string.IsNullOrWhiteSpace(label))
                return false;

            foreach (var child in node.Children.Where(IsRenderableStructuralNode))
            {
                if (string.Equals(child?.Label, label, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool ContainsMixedPartyScanSubsetSummary(VariantTreeNode node)
        {
            return GetAllVariants(node)
                .Where(item => item?.Variant != null)
                .Any(item => HasMixedPartyScanSubsetSummary(item.Variant));
        }

        private static bool HasMixedPartyScanSubsetSummary(PathVariantInfo variant)
        {
            int distinctConditionalEffects = variant?.PartyEffects?
                .Where(IsConditionalLoopSubsetOutcomeEffect)
                .Select(PartyEffectSemantics.BuildSemanticKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Count() ?? 0;

            return distinctConditionalEffects >= 2;
        }

        private static bool IsConditionalLoopSubsetOutcomeEffect(PartyEffect effect)
        {
            return effect != null &&
                   PartyEffectSemantics.IsStateChanging(effect) &&
                   PartyEffectSemantics.IsLoopDerived(effect) &&
                   PartyEffectSemantics.GetEffectiveScope(effect) == PartyEffectScope.PartySubset &&
                   (PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None ||
                    PartyEffectSemantics.HasEffectiveGuardPredicates(effect));
        }

        private static void RenderChoiceLeaf(
            string label,
            VariantRenderItem item,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            var annotations = BuildVariantHeaderAnnotations(item?.Variant, inheritedProbabilityLine, inheritedGuardKey);
            string annotationText = BuildVariantHeaderAnnotationText(annotations);
            if (!string.IsNullOrWhiteSpace(annotationText))
                header += $" ({annotationText})";
            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            header += string.IsNullOrWhiteSpace(displayLabel) ? ":" : $": {displayLabel}";
            sb.AppendLine(header);
        }

        private static IEnumerable<VariantRenderItem> OrderDirectVariants(IEnumerable<VariantRenderItem> variants)
        {
            return (variants ?? Enumerable.Empty<VariantRenderItem>())
                .OrderBy(v => v?.Lines?.Count ?? 0)
                .ThenByDescending(v => v?.Variant?.ProbabilityDenominator ?? 1)
                .ThenByDescending(v => v?.Variant?.ProbabilityNumerator ?? 1)
                .ThenBy(v => GetPathOrderKey(v?.Variant));
        }

        private static void RenderLooseVariant(
            VariantRenderItem item,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            var annotations = BuildVariantHeaderAnnotations(item?.Variant, inheritedProbabilityLine, inheritedGuardKey);
            string annotationText = BuildVariantHeaderAnnotationText(annotations);
            if (!string.IsNullOrWhiteSpace(annotationText))
                header += $" ({annotationText})";

            var lines = item?.Lines?.ToList() ?? new List<string>();
            string promotedLabel = TryPromoteLeadingAnswerLine(lines);
            header += string.IsNullOrWhiteSpace(promotedLabel)
                ? ":"
                : $": {promotedLabel}";
            sb.AppendLine(header);
            bool headerContainsProbability = VariantHeaderContainsProbability(header);

            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                lines,
                headerContainsProbability);
        }

        private static string TryPromoteLeadingAnswerLine(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return null;

            int index = lines.FindIndex(line => !string.IsNullOrWhiteSpace(line));
            if (index < 0)
                return null;

            string candidate = lines[index]?.Trim();
            if (!IsPartyScanAnswerLine(candidate))
                return null;

            lines.RemoveAt(index);
            return candidate;
        }

        private static void AppendLineToFirstMeaningfulVariant(
            Dictionary<int, List<string>> variantContents,
            string line)
        {
            if (string.IsNullOrWhiteSpace(line) || variantContents == null || variantContents.Count == 0)
                return;

            int? targetKey = variantContents
                .OrderBy(v => v.Key)
                .Where(v => HasMeaningfulVariantPayload(v.Value))
                .Select(v => (int?)v.Key)
                .FirstOrDefault();

            if (!targetKey.HasValue)
            {
                targetKey = variantContents
                    .OrderBy(v => v.Key)
                    .Select(v => (int?)v.Key)
                    .FirstOrDefault();
            }

            if (!targetKey.HasValue)
                return;

            if (!variantContents.TryGetValue(targetKey.Value, out var lines) || lines == null)
            {
                lines = new List<string>();
                variantContents[targetKey.Value] = lines;
            }

            if (!lines.Contains(line, StringComparer.Ordinal))
                lines.Add(line);
        }

        private static void AppendLineToFirstMeaningfulVariantItem(
            List<VariantRenderItem> items,
            string line)
        {
            if (string.IsNullOrWhiteSpace(line) || items == null || items.Count == 0)
                return;

            var target = items.FirstOrDefault(item => HasMeaningfulVariantPayload(item?.Lines))
                ?? items.FirstOrDefault(item => item != null);

            if (target == null)
                return;

            target.Lines ??= new List<string>();
            if (!target.Lines.Contains(line, StringComparer.Ordinal))
                target.Lines.Add(line);
        }

        private static bool HasMeaningfulVariantPayload(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return false;

            return lines.Any(line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                    return false;

                string trimmed = line.Trim();
                return !string.Equals(trimmed, "Ничего не происходит", StringComparison.Ordinal)
                    && !string.Equals(trimmed, "Ничего не происходит (не выполнены условия для наступления ни одного варианта)", StringComparison.Ordinal);
            });
        }

        private static string TryBuildSpecialSpoilerLine(
            string filename,
            string fileNameOnly,
            OvrFileConfig config,
            OvrObject obj)
        {
            if (!string.Equals(fileNameOnly, "CAVE4.OVR", StringComparison.OrdinalIgnoreCase))
                return null;

            if (obj?.PatchAddress != 0x0092)
                return null;

            string password = TryReadCave4AccessCode(filename, config);
            if (string.IsNullOrWhiteSpace(password))
                return null;

            return BuildSpoilerAnswerLine(password);
        }

        private static string TryBuildInlineSpecialSpoilerLine(
            string filename,
            string fileNameOnly,
            OvrFileConfig config,
            OvrObject obj)
        {
            if (string.Equals(fileNameOnly, "CAVE7.OVR", StringComparison.OrdinalIgnoreCase))
            {
                if (obj?.PatchAddress != 0x005C)
                    return null;

                string cave7Answer = TryReadCave7RiddleAnswer(filename, config);
                if (string.IsNullOrWhiteSpace(cave7Answer))
                    return null;

                return BuildSpoilerAnswerLine(cave7Answer);
            }

            if (string.Equals(fileNameOnly, "AREAB2.OVR", StringComparison.OrdinalIgnoreCase))
            {
                if (obj == null ||
                    obj.X != 4 ||
                    obj.Y != 4 ||
                    obj.PatchAddress != 0x005F)
                {
                    return null;
                }

                string areaB2Answer = TryReadAreaB2IcePrincessAnswer(filename, config);
                if (string.IsNullOrWhiteSpace(areaB2Answer))
                    return null;

                return BuildSpoilerAnswerLine(areaB2Answer);
            }

            if (string.Equals(fileNameOnly, "AREAB4.OVR", StringComparison.OrdinalIgnoreCase))
            {
                string areaB4Answer = TryReadAreaB4TriviaAnswer(filename, config, obj);
                if (string.IsNullOrWhiteSpace(areaB4Answer))
                    return null;

                return BuildSpoilerAnswerLine(areaB4Answer);
            }

            return null;
        }

        private static string TryBuildSpecialPlayerExplanation(string fileNameOnly, OvrObject obj)
        {
            if (!string.Equals(fileNameOnly, "AREAA3.OVR", StringComparison.OrdinalIgnoreCase))
                return null;

            if (obj == null ||
                obj.X != 3 ||
                obj.Y != 6 ||
                obj.PatchAddress != 0x011C)
            {
                return null;
            }

            return InlineNoteStyleCodec.EncodeWheelRewardExplanationText(
                BuildAreaA3MountainWheelExplanation());
        }

        private static string BuildAreaA3MountainWheelExplanation()
        {
            string[] lines =
            {
                "ПОЯСНЕНИЕ К КОЛЕСУ",
                "",
                "Если выбрать N:",
                "ничего не происходит.",
                "Если выбрать Y:",
                "колесо обрабатывает каждого персонажа партии отдельно.",
                "",
                "Для каждого персонажа считается число убитых боссов (0-4).",
                "0: LOSER автоматически.",
                "1-4: случайный результат:",
                "  50% LOSER",
                "  16,67% EXP",
                "  16,67% GEMS",
                "  16,67% GOLD",
                "",
                "Боссы | GEMS  | GOLD  | EXP",
                "0     | LOSER | LOSER | LOSER",
                "1     | 30    | 1000  | 4000",
                "2     | 60    | 2000  | 8000",
                "3     | 120   | 4000  | 16000",
                "4     | 240   | 8000  | 32000"
            };

            return BuildAsciiFrame(lines, 60);
        }

        private static string BuildAsciiFrame(IEnumerable<string> lines, int contentWidth)
        {
            contentWidth = Math.Max(contentWidth, 1);
            var sb = new StringBuilder();
            string border = new string('=', contentWidth + 6);

            sb.AppendLine(border);

            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                foreach (string segment in WrapFrameLine(line ?? string.Empty, contentWidth))
                {
                    sb.Append("|| ");
                    sb.Append(segment.PadRight(contentWidth));
                    sb.AppendLine(" ||");
                }
            }

            sb.Append(border);
            return sb.ToString();
        }

        private static IEnumerable<string> WrapFrameLine(string line, int contentWidth)
        {
            if (string.IsNullOrEmpty(line))
            {
                yield return string.Empty;
                yield break;
            }

            int index = 0;
            while (index < line.Length)
            {
                int take = Math.Min(contentWidth, line.Length - index);
                if (take == contentWidth && index + take < line.Length)
                {
                    int breakAt = line.LastIndexOf(' ', index + take - 1, take);
                    if (breakAt > index)
                        take = breakAt - index;
                }

                string segment = line.Substring(index, take).TrimEnd();
                yield return segment;

                index += take;
                while (index < line.Length && line[index] == ' ')
                    index++;
            }
        }

        private static string TryReadCave4AccessCode(string filename, OvrFileConfig config)
        {
            // В CAVE4 патч 0x0092 проверяет пароль как storedByte + 0x1F
            // (последовательность CLC / ADC AL, 1Fh перед сравнением с вводом).
            return TryReadShiftedOverlayText(filename, config, 0xC9D3, 32, 0x1F);
        }

        private static string TryReadCave7RiddleAnswer(string filename, OvrFileConfig config)
        {
            // В CAVE7 правильный ответ лежит в оверлее в зашифрованном виде:
            // к каждому байту нужно прибавить 0x1E до сравнения с введённым символом.
            return TryReadShiftedOverlayText(filename, config, 0xCBBC, 32, 0x1E);
        }

        private static string TryReadAreaB2IcePrincessAnswer(string filename, OvrFileConfig config)
        {
            // В AREAB2 клетка (4,4) сравнивает первые 4 символа ввода так:
            // (inputChar & 0x7F) + 0x40 == storedByte at 0xC9E7 + index.
            return TryReadShiftedOverlayText(filename, config, 0xC9E7, 4, unchecked((byte)-0x40));
        }

        private static string TryReadAreaB4TriviaAnswer(string filename, OvrFileConfig config, OvrObject obj)
        {
            if (!TryGetAreaB4TriviaIndex(obj, out int triviaIndex))
                return null;

            const ushort answerPointerTable = 0xCABD;
            ushort pointerAddress = unchecked((ushort)(answerPointerTable + triviaIndex * 2));
            ushort answerAddress = TryReadOverlayWord(filename, config, pointerAddress);
            if (answerAddress == 0)
                return null;

            // В AREAB4 общий обработчик клеток декодирует байт ответа через SUB AL, 40h
            // перед сравнением с введённым символом.
            return TryReadShiftedOverlayText(filename, config, answerAddress, 14, unchecked((byte)-0x40));
        }

        private static bool TryGetAreaB4TriviaIndex(OvrObject obj, out int triviaIndex)
        {
            triviaIndex = -1;

            if (obj == null)
                return false;

            if (obj.X == 8 &&
                obj.Y == 1 &&
                obj.PatchAddress == 0x021D)
            {
                triviaIndex = 0;
                return true;
            }

            if (obj.Y == 0 &&
                obj.X >= 6 &&
                obj.X <= 9 &&
                obj.PatchAddress == 0x0222)
            {
                triviaIndex = obj.X - 5;
                return true;
            }

            return false;
        }

        private static ushort TryReadOverlayWord(string filename, OvrFileConfig config, ushort address)
        {
            if (string.IsNullOrWhiteSpace(filename) || config == null || !File.Exists(filename))
                return 0;

            try
            {
                using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                return OvrOverlayAddressReader.TryReadWord(br, config, address, out ushort value)
                    ? value
                    : (ushort)0;
            }
            catch
            {
                return 0;
            }
        }

        private static string TryReadShiftedOverlayText(
            string filename,
            OvrFileConfig config,
            ushort startAddress,
            int maxLength,
            byte addValue)
        {
            if (string.IsNullOrWhiteSpace(filename) || config == null || !File.Exists(filename))
                return null;

            try
            {
                using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                var decodedBytes = new List<byte>();
                for (int i = 0; i < maxLength; i++)
                {
                    ushort currentAddress = unchecked((ushort)(startAddress + i));
                    if (!OvrOverlayAddressReader.TryReadByte(br, config, currentAddress, out byte encodedByte))
                        break;

                    if (encodedByte == 0)
                        break;

                    decodedBytes.Add(unchecked((byte)(encodedByte + addValue)));
                }

                return decodedBytes.Count == 0
                    ? null
                    : OvrOverlayAddressReader.DecodeText(decodedBytes.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static string BuildSpoilerAnswerLine(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
                return null;

            return $"{SpoilerAnswerLinePrefix}{answer}";
        }

        private static List<string> DecodeNoteTexts(IEnumerable<string> rawTexts)
        {
            var result = new List<string>();
            if (rawTexts == null)
                return result;

            foreach (var rawText in rawTexts)
            {
                string decodedText = ExtractNoteText(rawText);
                if (!string.IsNullOrEmpty(decodedText))
                    result.Add(decodedText);
            }

            return result;
        }

        private static List<string> GetMonsterStatLines(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance)
        {
            var lines = new List<string>();

            if (!obj.HasMonsterStatChanges)
                return lines;

            var powerDesc = obj.GetRandomEncounterMonsterPowerCapDescription(defaultRandomEncounterMonsterPowerCap);
            if (powerDesc != null) lines.Add(powerDesc);

            var levelDesc = obj.GetRandomEncounterMonsterLevelCapDescription(defaultRandomEncounterMonsterLevelCap);
            if (levelDesc != null) lines.Add(levelDesc);

            var batchCountDesc = obj.GetRandomEncounterMonsterBatchCountCapDescription(defaultRandomEncounterMonsterBatchCountCap);
            if (batchCountDesc != null) lines.Add(batchCountDesc);

            var lightingDesc = obj.GetDarkeningLevelDescription(defaultDarkeningLevel);
            if (lightingDesc != null) lines.Add(lightingDesc);

            var randomEncounterDesc = obj.GetRandomEncounterChanceDescription(defaultRandomEncounterChance);
            if (randomEncounterDesc != null) lines.Add(randomEncounterDesc);

            var strengthAdjustmentDesc = obj.GetBattleMonsterStrengthAdjustmentDescription();
            if (strengthAdjustmentDesc != null)
            {
                lines.Add(obj.BattleMonsterStrengthAdjustment > 0
                    ? InlineNoteStyleCodec.EncodeBattleMonsterStrengthIncreaseText(strengthAdjustmentDesc)
                    : InlineNoteStyleCodec.EncodeBattleMonsterStrengthDecreaseText(strengthAdjustmentDesc));
            }

            return lines;
        }

        private static List<string> GetBattleLines(OvrObject obj)
        {
            var lines = new List<string>();

            if (!obj.HasBattleLikeInfo)
                return lines;

            string battleDesc = obj.GetBattleDescription();
            if (!string.IsNullOrEmpty(battleDesc))
                lines.Add(battleDesc);

            return lines;
        }

        private static List<string> GetSpecialNoteLines(OvrObject obj)
        {
            var lines = new List<string>();

            string teleportDescription = obj.GetTeleportDescription();
            if (!string.IsNullOrEmpty(teleportDescription))
                lines.Add(teleportDescription);

            lines.AddRange(BuildOrderedSpecialEffectLines(obj));

            return lines;
        }

        private static List<string> BuildOrderedSpecialEffectLines(OvrObject obj)
        {
            var lines = new List<string>();
            if (obj == null)
                return lines;

            var orderedEntries = new List<(uint Address, int KindOrder, string Text, int SortBucket, int ExecutionOrder)>();
            var seenPartyLines = new HashSet<string>(StringComparer.Ordinal);
            var variantContext = GetSinglePathVariantContext(obj);
            var effects = (obj.PartyEffects ?? new List<PartyEffect>())
                .Where(effect => effect != null)
                .ToList();

            foreach (var effect in GetOrderedDisplayablePartyEffects(effects))
            {
                string description = BuildPartyEffectDisplayDescription(effect, variantContext);
                if (string.IsNullOrWhiteSpace(description) || !seenPartyLines.Add(description))
                    continue;

                orderedEntries.Add((
                    NormalizeSpecialLineAddress(effect.InstructionAddress),
                    0,
                    description,
                    GetSpecialLineSortBucket(effect.ExecutionOrder),
                    NormalizeSpecialExecutionOrder(effect.ExecutionOrder)));
            }

            // Заметка про random encounter нужна только для табличных объектов
            // без явного описания битвы. Если битва уже описана как группа монстров,
            // то информация о random count выводится в самой строке битвы.
            if (obj.IsFromTable && obj.CallsRandomEncounter && !obj.HasBattleLikeInfo)
            {
                orderedEntries.Add((
                    NormalizeSpecialLineAddress(obj.RandomEncounterInstructionAddress),
                    1,
                    "⚠Вызывается random encounter ⚠",
                    GetSpecialLineSortBucket(obj.RandomEncounterExecutionOrder),
                    NormalizeSpecialExecutionOrder(obj.RandomEncounterExecutionOrder)));
            }

            lines.AddRange(orderedEntries
                .OrderBy(entry => entry.SortBucket)
                .ThenBy(entry => entry.ExecutionOrder)
                .ThenBy(entry => entry.Address)
                .ThenBy(entry => entry.KindOrder)
                .Select(entry => entry.Text));

            return lines;
        }

        private static PathVariantInfo GetSinglePathVariantContext(OvrObject obj)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count != 1)
                return null;

            return obj.PathVariants.Values.FirstOrDefault();
        }

        private static int GetSpecialLineSortBucket(int executionOrder)
        {
            return executionOrder > 0 ? 0 : 1;
        }

        private static int NormalizeSpecialExecutionOrder(int executionOrder)
        {
            return executionOrder > 0 ? executionOrder : int.MaxValue;
        }

        private static uint NormalizeSpecialLineAddress(uint address)
        {
            return address == 0 ? uint.MaxValue : address;
        }

        private static Dictionary<int, List<string>> DeduplicateVariantContents(
            Dictionary<int, List<string>> variantContents)
        {
            var result = new Dictionary<int, List<string>>();
            var seenKeys = new HashSet<string>();

            if (variantContents == null || variantContents.Count == 0)
                return result;

            foreach (var kvp in variantContents.OrderBy(v => v.Key))
            {
                var lines = (kvp.Value ?? new List<string>())
                    .Select(l => l?.TrimEnd() ?? string.Empty)
                    .ToList();

                string key = string.Join("\n", lines);

                if (seenKeys.Contains(key))
                    continue;

                seenKeys.Add(key);
                result[result.Count] = lines;
            }

            return result;
        }

        private static Dictionary<int, List<string>> DeduplicateDisplayedVariantContents(
            OvrObject obj,
            Dictionary<int, List<string>> variantContents)
        {
            var result = new Dictionary<int, List<string>>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            if (variantContents == null || variantContents.Count == 0)
                return result;

            foreach (var kvp in variantContents.OrderBy(v => v.Key))
            {
                var lines = (kvp.Value ?? new List<string>())
                    .Select(l => l?.TrimEnd() ?? string.Empty)
                    .ToList();

                string displayKey = BuildDisplayedVariantContentKey(obj, kvp.Key, lines);
                if (seenKeys.Contains(displayKey))
                    continue;

                seenKeys.Add(displayKey);
                result[kvp.Key] = lines;
            }

            return result;
        }

        private static List<VariantRenderItem> DeduplicateDisplayedVariantItems(List<VariantRenderItem> items)
        {
            var result = new List<VariantRenderItem>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in items ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (item == null)
                    continue;

                string displayKey = BuildDisplayedVariantItemKey(item);
                if (seenKeys.Contains(displayKey))
                    continue;

                seenKeys.Add(displayKey);
                result.Add(item);
            }

            return result;
        }

        private static string BuildDisplayedVariantContentKey(
            OvrObject obj,
            int variantKey,
            List<string> lines)
        {
            string probabilityKey = string.Empty;
            string occurrenceKey = string.Empty;
            if (TryResolvePathVariantForDisplayKey(obj, variantKey, out var variant))
            {
                probabilityKey = BuildProbabilityLine(variant) ?? string.Empty;
                occurrenceKey = BuildOccurrenceLine(variant) ?? string.Empty;
            }

            string guardKey = TryResolvePathVariantForDisplayKey(obj, variantKey, out var guardedVariant)
                ? BuildGuardConditionKey(guardedVariant) ?? string.Empty
                : string.Empty;
            string inventoryPresenceKey = TryResolvePathVariantForDisplayKey(obj, variantKey, out var inventoryVariant)
                ? string.Join("|", BuildInventoryPresenceHeaderAnnotationsForFlat(obj, inventoryVariant))
                : string.Empty;
            string linesKey = string.Join("\n", (lines ?? new List<string>()).Select(line => line ?? string.Empty));
            return string.Join("\n---\n", occurrenceKey, probabilityKey, guardKey, inventoryPresenceKey, linesKey);
        }

        private static string BuildDisplayedVariantItemKey(VariantRenderItem item)
        {
            string occurrenceKey = BuildOccurrenceLine(item?.Variant) ?? string.Empty;
            string probabilityKey = BuildProbabilityLine(item?.Variant) ?? string.Empty;
            string guardConditionKey = BuildGuardConditionKey(item?.Variant) ?? string.Empty;
            string branchKey = string.Join("|",
                GetRelevantBranchChoices(item?.Variant)
                    .Select(choice => BuildUserVisibleChoiceDisplayKey(choice))
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
            string linesKey = string.Join("\n",
                (item?.Lines ?? new List<string>())
                    .Select(line => line?.TrimEnd() ?? string.Empty));

            return string.Join("\n---\n", occurrenceKey, probabilityKey, guardConditionKey, branchKey, linesKey);
        }

        private static List<string> NumberLootBlockIfNeeded(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return lines;

            // Для неявных контейнеров анализатор может сначала распознать содержимое,
            // а строку контейнера добавить только позже. Перед нумерацией поднимаем
            // такой контейнер к началу contiguous loot-блока.
            lines = NormalizeLootBlockOrder(lines);

            var result = new List<string>();
            int i = 0;

            while (i < lines.Count)
            {
                string current = lines[i] ?? string.Empty;

                if (!IsContainerLootIntroLine(current))
                {
                    result.Add(current);
                    i++;
                    continue;
                }

                result.Add(current);
                i++;

                bool shouldNumberEntries = CountLootEntriesInBlock(lines, i) > 1;
                const string singleEntryIndent = "   ";
                bool hasLootPayloadEntries = false;
                int entryNumber = 1;

                while (i < lines.Count)
                {
                    string line = lines[i] ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        result.Add(line);
                        i++;
                        break;
                    }

                    if (IsContainerLootIntroLine(line))
                        break;

                    if (IsProbabilityLootHeader(line))
                    {
                        string headerLine = RemoveExistingLootNumbering(line);
                        result.Add(shouldNumberEntries
                            ? $"{entryNumber}) {headerLine}"
                            : singleEntryIndent + headerLine);
                        hasLootPayloadEntries = true;
                        if (shouldNumberEntries)
                            entryNumber++;
                        i++;

                        while (i < lines.Count && IsProbabilityLootItemLine(lines[i]))
                        {
                            result.Add($"   • {RemoveProbabilityBullet(lines[i])}");
                            i++;
                        }

                        continue;
                    }

                    if (IsExplicitLootValueLine(line) || IsPlainLootItemLine(line))
                    {
                        string lootLine = RemoveExistingLootNumbering(line);
                        result.Add(shouldNumberEntries
                            ? $"{entryNumber}) {lootLine}"
                            : singleEntryIndent + lootLine);
                        hasLootPayloadEntries = true;
                        if (shouldNumberEntries)
                            entryNumber++;
                        i++;
                        continue;
                    }

                    break;
                }

                AppendBlankLineAfterLootBlockIfNeeded(result, lines, i, hasLootPayloadEntries);

            }

            return result;
        }

        private static void InsertInlineSpoilerAfterAnswerPrompt(List<string> narrativeLines, string spoilerLine)
        {
            if (string.IsNullOrWhiteSpace(spoilerLine) || narrativeLines == null || narrativeLines.Count == 0)
                return;

            if (narrativeLines.Contains(spoilerLine, StringComparer.Ordinal))
                return;

            for (int i = 0; i < narrativeLines.Count; i++)
            {
                string line = narrativeLines[i];
                if (line?.IndexOf(RiddleAnswerPrompt, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                narrativeLines.Insert(i + 1, spoilerLine);
                return;
            }
        }

        private static void AppendBlankLineAfterLootBlockIfNeeded(
            List<string> result,
            List<string> lines,
            int nextIndex,
            bool hasLootPayloadEntries)
        {
            if (!hasLootPayloadEntries || result == null || lines == null)
                return;

            if (nextIndex < 0 || nextIndex >= lines.Count)
                return;

            string nextLine = lines[nextIndex] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nextLine))
                return;

            if (!IsBattleDescriptionLine(nextLine))
                return;

            if (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
                return;

            result.Add(string.Empty);
        }

        private static bool IsBattleDescriptionLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.TrimStart();
            return trimmed.StartsWith("Битва с группой монстров:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Частично определённая битва", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountLootEntriesInBlock(List<string> lines, int startIndex)
        {
            if (lines == null || startIndex < 0 || startIndex >= lines.Count)
                return 0;

            int entryCount = 0;
            int i = startIndex;

            while (i < lines.Count)
            {
                string line = lines[i] ?? string.Empty;

                if (string.IsNullOrWhiteSpace(line) || IsContainerLootIntroLine(line))
                    break;

                if (IsProbabilityLootHeader(line))
                {
                    entryCount++;
                    i++;

                    while (i < lines.Count && IsProbabilityLootItemLine(lines[i]))
                        i++;

                    continue;
                }

                if (IsExplicitLootValueLine(line) || IsPlainLootItemLine(line))
                {
                    entryCount++;
                    i++;
                    continue;
                }

                break;
            }

            return entryCount;
        }

        private static List<string> NormalizeLootBlockOrder(List<string> lines)
        {
            if (lines == null || lines.Count < 2)
                return lines;

            var result = new List<string>(lines);

            for (int i = 1; i < result.Count; i++)
            {
                if (!IsContainerLootIntroLine(result[i]))
                    continue;

                if (IsContainerLootIntroLine(result[i - 1]))
                    continue;

                int blockStart = FindPrecedingLootPayloadStart(result, i);
                if (blockStart < 0 || blockStart >= i)
                    continue;

                string containerLine = result[i];
                result.RemoveAt(i);
                result.Insert(blockStart, containerLine);
            }

            return result;
        }

        private static int FindPrecedingLootPayloadStart(List<string> lines, int containerIndex)
        {
            if (lines == null || containerIndex <= 0 || containerIndex > lines.Count - 1)
                return -1;

            int start = containerIndex;

            for (int i = containerIndex - 1; i >= 0; i--)
            {
                if (!IsLootPayloadLine(lines[i]))
                    break;

                start = i;
            }

            return start == containerIndex ? -1 : start;
        }

        private static bool IsLootPayloadLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return IsProbabilityLootHeader(line)
                || IsProbabilityLootItemLine(line)
                || IsExplicitLootValueLine(line)
                || IsPlainLootItemLine(line);
        }

        private static bool IsExplicitLootValueLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveExistingLootNumbering(line.Trim());
            string upper = trimmed.ToUpperInvariant();

            if (trimmed.StartsWith("предмет", StringComparison.OrdinalIgnoreCase))
                return true;

            if (upper.StartsWith("ITEM ") || upper.StartsWith("ITEM:"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^\d+(?:-\d+)?\s+GEMS?$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^GEMS?[:\s]+\d+(?:-\d+)?$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^\d+(?:-\d+)?\s+GOLD$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^GOLD[:\s]+\d+(?:-\d+)?$"))
                return true;

            return false;
        }

        private static bool IsContainerLootIntroLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();

            return trimmed.StartsWith("На ячейке находится ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith("в котором лежит:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProbabilityLootHeader(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveExistingLootNumbering(line.Trim());

            return trimmed.Equals("Возможный предмет:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Случайный предмет:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Возможные предметы:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Possible item:", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Possible items:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProbabilityLootItemLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveProbabilityBullet(RemoveExistingLootNumbering(line.Trim()));

            return System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                @"^[A-ZА-ЯЁ][A-ZА-ЯЁ0-9 '\-\+\.]{1,60}\s+\(\d+(?:[.,]\d+)?%\)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private static string RemoveProbabilityBullet(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("• "))
                return trimmed.Substring(2).TrimStart();

            if (trimmed.StartsWith("- "))
                return trimmed.Substring(2).TrimStart();

            if (trimmed.StartsWith("* "))
                return trimmed.Substring(2).TrimStart();

            return trimmed;
        }

        private static bool IsPlainLootItemLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveExistingLootNumbering(line.Trim());
            if (trimmed.Length == 0)
                return false;

            if (IsContainerLootIntroLine(trimmed) || IsProbabilityLootHeader(trimmed))
                return false;

            if (IsExplicitLootValueLine(trimmed))
                return true;

            if (IsProbabilityLootItemLine(trimmed))
                return false;

            if (trimmed.Length > 60)
                return false;

            if (trimmed.Contains("\"") || trimmed.Contains("...") || trimmed.Contains("! ") || trimmed.Contains("? "))
                return false;

            if (trimmed.Any(char.IsDigit))
                return false;

            if (trimmed.Contains(":") || trimmed.Contains(";") || trimmed.Contains(",") || trimmed.Contains("(") || trimmed.Contains(")"))
                return false;

            string normalized = NormalizeLootItemIdentity(trimmed);
            return !string.IsNullOrEmpty(normalized)
                && KnownLootItemNames.Value.Contains(normalized);
        }

        private static HashSet<string> BuildKnownLootItemNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in ItemDatabase.Items)
            {
                string normalized = NormalizeLootItemIdentity(item?.Name);
                if (!string.IsNullOrEmpty(normalized))
                    result.Add(normalized);
            }

            return result;
        }

        private static string NormalizeLootItemIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = RemoveProbabilityBullet(RemoveExistingLootNumbering(value.Trim()));
            if (normalized.Length == 0)
                return string.Empty;

            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.ToUpperInvariant();
        }

        private static string RemoveExistingLootNumbering(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            string trimmed = line.TrimStart();
            int pos = 0;
            while (pos < trimmed.Length && char.IsDigit(trimmed[pos]))
                pos++;

            if (pos == 0 || pos >= trimmed.Length)
                return trimmed;

            if (trimmed[pos] == '.' || trimmed[pos] == ')')
            {
                pos++;
                while (pos < trimmed.Length && char.IsWhiteSpace(trimmed[pos]))
                    pos++;
                return trimmed.Substring(pos);
            }

            return trimmed;
        }

        private static string ExtractNoteText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            int colonIndex = rawText.IndexOf(':');
            string candidate;
            bool isRawOverlayText = IsRawOverlayTextEntry(rawText);

            if (isRawOverlayText && colonIndex >= 0)
            {
                string afterColonWithSeparator = rawText.Substring(colonIndex + 1);
                string payload = afterColonWithSeparator.StartsWith(" ", StringComparison.Ordinal)
                    ? afterColonWithSeparator.Substring(1)
                    : afterColonWithSeparator;
                string afterColon = payload.Trim();
                if (!string.IsNullOrEmpty(afterColon))
                {
                    candidate = afterColon;
                }
                else if (isRawOverlayText && payload.Length > 0)
                {
                    return PreserveWhitespaceOnlyRawOverlayText(DecodeTextString(payload));
                }
                else
                {
                    candidate = rawText.Trim();
                }
            }
            else
            {
                candidate = rawText.Trim();
            }

            string decodedText = DecodeTextString(candidate);
            if (string.IsNullOrWhiteSpace(decodedText))
            {
                if (isRawOverlayText && !string.IsNullOrEmpty(decodedText))
                    return PreserveWhitespaceOnlyRawOverlayText(decodedText);

                return null;
            }

            return decodedText.TrimEnd('\r');
        }

        private static string PreserveWhitespaceOnlyRawOverlayText(string decodedText)
        {
            if (string.IsNullOrEmpty(decodedText))
                return null;

            return decodedText.TrimEnd('\r');
        }

        private static bool IsRawOverlayTextEntry(string rawText)
        {
            return rawText != null &&
                rawText.TrimStart().StartsWith("Text at 0x", StringComparison.Ordinal);
        }

        private static string DecodeTextString(string encodedText)
        {
            if (string.IsNullOrEmpty(encodedText))
                return "";

            return encodedText
                .Replace("\\r", "\r")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }
    }
}

