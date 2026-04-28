
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

            byte defaultMonsterPower = 0;
            byte defaultMonsterLevel = 0;
            byte defaultMonsterBatchCount = 0;
            byte defaultDarkeningLevel = 0;
            byte defaultRandomEncounterChance = 0;

            try
            {
                byte[] fileData = File.ReadAllBytes(filename);
                if (config.MonsterPower < fileData.Length)
                    defaultMonsterPower = fileData[config.MonsterPower];
                if (config.MonsterLevel < fileData.Length)
                    defaultMonsterLevel = fileData[config.MonsterLevel];
                if (config.MonsterBatchCount < fileData.Length)
                    defaultMonsterBatchCount = fileData[config.MonsterBatchCount];
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

                string hierarchicalNotes = useHierarchical
                    ? BuildHierarchicalVariantNotes(
                        obj,
                        defaultMonsterPower,
                        defaultMonsterLevel,
                        defaultMonsterBatchCount,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        specialSpoilerLine,
                        inlineSpecialSpoilerLine)
                    : string.Empty;

                StringBuilder newNotes = new StringBuilder();
                var inlineStyles = new List<NoteInlineStyleSpan>();

                if (useHierarchical && !string.IsNullOrWhiteSpace(hierarchicalNotes))
                {
                    AppendRenderedText(newNotes, inlineStyles, hierarchicalNotes);
                }
                else
                {
                    Dictionary<int, List<string>> variantContents = BuildVariantContents(
                        obj,
                        defaultMonsterPower,
                        defaultMonsterLevel,
                        defaultMonsterBatchCount,
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

                result.NotesPerCell[pos] = newNotes.Length > 0
                    ? newNotes.ToString().TrimEnd('\n')
                    : existingCellNotes;
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

        private static Dictionary<int, List<string>> BuildVariantContents(
            OvrObject obj,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            if (obj.PathVariants != null && obj.PathVariants.Count > 0)
            {
                return BuildVariantContentsFromPathVariants(
                    obj,
                    defaultMonsterPower,
                    defaultMonsterLevel,
                    defaultMonsterBatchCount,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);
            }

            return BuildVariantContentsFromObjectTexts(
                obj,
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromPathVariants(
            OvrObject obj,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
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
                    defaultMonsterPower,
                    defaultMonsterLevel,
                    defaultMonsterBatchCount,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);

                if (lines.Count == 0)
                    lines.Add("Ничего не происходит (не выполнены условия для наступления ни одного варианта)");

                variantContents[variantNumber] = lines;
            }

            return variantContents;
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromObjectTexts(
            OvrObject obj,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
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
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
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
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var lines = new List<string>();
            var narrativeLines = DecodeNoteTexts(rawTexts);
            InsertInlineSpoilerAfterAnswerPrompt(narrativeLines, inlineSpecialSpoilerLine);
            var monsterStatLines = GetMonsterStatLines(
                variantObject,
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
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

        private static List<string> BuildVariantLinesForHierarchy(
            OvrObject variantObject,
            IEnumerable<string> rawTexts,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var lines = new List<string>();
            var narrativeLines = DecodeNoteTexts(rawTexts);
            InsertInlineSpoilerAfterAnswerPrompt(narrativeLines, inlineSpecialSpoilerLine);
            var specialNoteLines = GetSpecialNoteLines(variantObject);

            lines.AddRange(narrativeLines);
            lines.AddRange(GetMonsterStatLines(
                variantObject,
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
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

            if (obj?.PathVariants != null && obj.PathVariants.TryGetValue(variantKey, out var pathVariant))
            {
                var annotations = BuildVariantHeaderAnnotations(pathVariant);
                if (annotations.Count > 0)
                    header += $" ({string.Join("; ", annotations)})";
            }

            return header;
        }

        private static bool VariantHeaderContainsProbability(string variantHeader)
        {
            return !string.IsNullOrEmpty(variantHeader)
                && variantHeader.IndexOf("вероятность наступления", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatVariantLine(string line, bool headerContainsProbability)
        {
            if (string.Equals(line, "Ничего не происходит", StringComparison.Ordinal) && !headerContainsProbability)
                return "Ничего не происходит (не выполнены условия для наступления ни одного варианта)";

            return line;
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

        private static string BuildProbabilityHeaderAnnotation(string probabilityLine)
        {
            if (string.IsNullOrWhiteSpace(probabilityLine))
                return null;

            if (probabilityLine.StartsWith("Вероятность: ", StringComparison.Ordinal))
                probabilityLine = probabilityLine.Substring("Вероятность: ".Length);

            return PrefixProbabilityWordInParentheses(probabilityLine);
        }

        private static List<string> BuildVariantHeaderAnnotations(
            PathVariantInfo variant,
            string suppressedProbabilityLine = null)
        {
            var annotations = new List<string>();

            string occurrence = BuildOccurrenceLine(variant);
            if (!string.IsNullOrWhiteSpace(occurrence))
                annotations.Add(occurrence);

            string probability = BuildProbabilityLine(variant);
            if (!string.IsNullOrEmpty(suppressedProbabilityLine) &&
                string.Equals(probability, suppressedProbabilityLine, StringComparison.Ordinal))
            {
                probability = null;
            }

            string probabilityAnnotation = BuildProbabilityHeaderAnnotation(probability);
            if (!string.IsNullOrWhiteSpace(probabilityAnnotation))
                annotations.Add(probabilityAnnotation);

            return annotations;
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

        private sealed class VariantTreeNode
        {
            public string SegmentKey { get; set; }
            public string Label { get; set; }
            public List<string> CommonLines { get; set; } = new List<string>();
            public List<VariantRenderItem> DirectVariants { get; set; } = new List<VariantRenderItem>();
            public List<VariantTreeNode> Children { get; set; } = new List<VariantTreeNode>();
        }

        private sealed class TopLevelVariantGroup
        {
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public VariantTreeNode TreeRoot { get; set; }
            public string Label { get; set; }
            public bool GroupedByChoice { get; set; }
        }

        private sealed class OrderedRenderEntry
        {
            public int OccurrenceOrderKey { get; set; }
            public int DisplayPriority { get; set; }
            public int PathOrderKey { get; set; }
            public VariantTreeNode ChildNode { get; set; }
            public VariantRenderItem DirectVariant { get; set; }
        }

        private sealed class SharedPartyHoistBranch
        {
            public VariantTreeNode ChildNode { get; set; }
            public VariantRenderItem DirectVariant { get; set; }
            public List<string> Lines { get; set; } = new List<string>();
        }

private static string BuildHierarchicalVariantNotes(
    OvrObject obj,
    byte defaultMonsterPower,
    byte defaultMonsterLevel,
    byte defaultMonsterBatchCount,
    byte defaultDarkeningLevel,
    byte defaultRandomEncounterChance,
    string specialSpoilerLine,
    string inlineSpecialSpoilerLine)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count <= 1)
                return null;

            var items = new List<VariantRenderItem>();
            foreach (var variant in obj.PathVariants.Values
                .Where(v => v != null)
                .OrderBy(v => v.PathId))
            {
                var variantObject = variant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte);
                var narrativeLines = DecodeNoteTexts(variant.Texts);
                var lines = BuildVariantLinesForHierarchy(
                    variantObject,
                    variant.Texts,
                    defaultMonsterPower,
                    defaultMonsterLevel,
                    defaultMonsterBatchCount,
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

            if (items.Count <= 1)
                return null;

            items = DeduplicateDisplayedVariantItems(items);

            if (items.Count <= 1)
                return null;

            AppendLineToFirstMeaningfulVariantItem(items, specialSpoilerLine);

            var groups = BuildTopLevelVariantGroups(items);
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

        private static List<TopLevelVariantGroup> BuildTopLevelVariantGroups(List<VariantRenderItem> items)
        {
            var groups = items
                .GroupBy(item => BuildTopLevelGroupKey(item))
                .Select(g =>
                {
                    var ordered = g.OrderBy(v => v.Variant?.PathId ?? int.MaxValue).ToList();
                    var first = ordered.FirstOrDefault();
                    var firstChoice = GetRelevantBranchChoices(first?.Variant).FirstOrDefault();
                    string firstNarrativeLine = GetNarrativeRootLine(first);
                    bool groupedByChoice = string.IsNullOrWhiteSpace(firstNarrativeLine) &&
                        !string.IsNullOrWhiteSpace(firstChoice?.Label);

                    return new TopLevelVariantGroup
                    {
                        Items = ordered,
                        Label = groupedByChoice ? NormalizeChoiceLabel(firstChoice?.Label) : null,
                        GroupedByChoice = groupedByChoice
                    };
                })
                .ToList();

            foreach (var group in groups)
            {
                var root = BuildVariantTree(group.Items, group.GroupedByChoice ? group.Label : null);
                ComputeCommonLines(root);
                IntroduceSharedLineHierarchy(root);
                IntroduceSharedPromptHierarchyAcrossChoiceChildren(root);
                HoistSharedCommonPartyNotes(root);
                PromoteConditionalPartyNotesBeforeBattle(root);
                group.TreeRoot = PruneDecorativeSingleChoiceLeaves(
                    SimplifyGenericChoiceTree(
                        CompressVariantTree(root)));
            }

            groups = groups
                .Where(HasRenderableTopLevelContent)
                .OrderByDescending(g => g.GroupedByChoice)
                .ThenBy(g => g.Items.Min(v => v.Variant?.PathId ?? int.MaxValue))
                .ToList();

            return groups;
        }


        private static bool HasRenderableTopLevelContent(TopLevelVariantGroup group)
        {
            return IsRenderableStructuralNode(group?.TreeRoot);
        }

        private static bool IsRenderableStructuralNode(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(node.Label))
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
            int renderableChildCount)
        {
            if (item == null)
                return false;

            if (IsRenderableDirectVariant(item))
                return true;

            if (!IsEmptyOrNoOpVariant(item))
                return false;

            return renderableChildCount > 0 || siblingDirectVariantCount > 1;
        }

        private static int CountRenderableDirectVariants(VariantTreeNode node)
        {
            if (node == null)
                return 0;

            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            int renderableChildCount = node.Children?.Count(IsRenderableStructuralNode) ?? 0;

            return (node.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Count(v => ShouldRenderDirectVariant(v, siblingDirectVariantCount, renderableChildCount));
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
            if (!string.IsNullOrWhiteSpace(firstChoice?.Label))
                return "CHOICE|" + NormalizeChoiceLabel(firstChoice.Label);

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

        private static VariantTreeNode BuildVariantTree(List<VariantRenderItem> items, string consumedTopChoiceLabel = null)
        {
            var root = new VariantTreeNode();

            foreach (var item in items)
            {
                var current = root;
                var choices = GetRelevantBranchChoices(item?.Variant).ToList();
                if (!string.IsNullOrWhiteSpace(consumedTopChoiceLabel) &&
                    choices.Count > 0 &&
                    string.Equals(NormalizeChoiceLabel(choices[0].Label), consumedTopChoiceLabel, StringComparison.Ordinal))
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
                            Label = NormalizeChoiceLabel(choice?.Label)
                        };
                        current.Children.Add(child);
                    }
                    else if (string.IsNullOrWhiteSpace(child.Label))
                    {
                        child.Label = NormalizeChoiceLabel(choice?.Label);
                    }

                    current = child;
                }

                current.DirectVariants.Add(item);
            }

            return root;
        }

        private static IEnumerable<BranchChoice> GetRelevantBranchChoices(PathVariantInfo variant)
        {
            if (variant?.BranchChoices == null)
                yield break;

            foreach (var choice in variant.BranchChoices)
            {
                var normalized = NormalizeBranchChoiceForDisplay(choice);
                if (normalized != null)
                    yield return normalized;
            }
        }

        private static BranchChoice NormalizeBranchChoiceForDisplay(BranchChoice choice)
        {
            if (choice == null)
                return null;

            string rawLabel = string.IsNullOrWhiteSpace(choice.Label)
                ? null
                : choice.Label.Trim();

            bool rawLabelIsTechnical = IsGenericTechnicalChoiceLabel(rawLabel);

            string inferredLabel = InferChoiceLabel(choice);
            string label = rawLabelIsTechnical
                ? inferredLabel
                : (string.IsNullOrWhiteSpace(rawLabel) ? inferredLabel : rawLabel);

            if (string.IsNullOrWhiteSpace(label))
                return null;

            return new BranchChoice
            {
                Label = label,
                Condition = choice.Condition,
                CompareValue = choice.CompareValue,
                CompareRegister = choice.CompareRegister,
                IsLinear = choice.IsLinear
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
                choice.Label ?? string.Empty,
                choice.CompareRegister ?? string.Empty,
                choice.CompareValue?.ToString() ?? string.Empty);
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
                   string.Equals(node.Children[0]?.Label, node.Label, StringComparison.OrdinalIgnoreCase))
            {
                node = node.Children[0];
            }

            return node;
        }

        private static VariantTreeNode PruneDecorativeSingleChoiceLeaves(VariantTreeNode node)
        {
            if (node == null)
                return null;

            for (int i = 0; i < node.Children.Count; i++)
                node.Children[i] = PruneDecorativeSingleChoiceLeaves(node.Children[i]);

            node.Children = node.Children
                .Where(IsRenderableStructuralNode)
                .ToList();

            while (node.Children.Count == 1 &&
                   CountRenderableDirectVariants(node) == 0 &&
                   IsDecorativeChoicePlaceholderLeaf(node.Children[0]))
            {
                node.Children.Clear();
            }

            return node;
        }

        private static bool IsDecorativeChoicePlaceholderLeaf(VariantTreeNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Label))
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
                .OrderBy(v => v.Variant?.PathId ?? int.MaxValue)
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
            if (node == null || string.IsNullOrWhiteSpace(node.Label))
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

                var rangeMatch = Regex.Match(token, @"^\s*(\d+)\s*[-–—]\s*(\d+)\s*$");
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
            return label.EndsWith(")", StringComparison.Ordinal) ? label : label + ")";
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
                .Select(PartyEffectSemantics.BuildHumanDescription)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList();
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

            return (PartyTechnicalFieldSemantics.IsTrackedField(PartyEffectSemantics.GetEffectiveField(effect)) ||
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
                   string.Equals(node.Children[0]?.Label, node.Label, StringComparison.OrdinalIgnoreCase))
            {
                node = node.Children[0];
            }

            return node;
        }

        private static int GetVariantRenderOrderKey(VariantRenderItem variant)
        {
            return variant?.Variant?.PathId ?? int.MaxValue;
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

        private static int GetNodeRenderOrderKey(VariantTreeNode node)
        {
            return GetAllVariants(node)
                .Where(variant => variant != null)
                .Select(GetVariantRenderOrderKey)
                .DefaultIfEmpty(int.MaxValue)
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
            IEnumerable<VariantTreeNode> children,
            IEnumerable<VariantRenderItem> directVariants)
        {
            var result = new List<OrderedRenderEntry>();

            foreach (var child in children ?? Enumerable.Empty<VariantTreeNode>())
            {
                if (child == null)
                    continue;

                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetNodeOccurrenceOrderKey(child),
                    DisplayPriority = 0,
                    PathOrderKey = GetNodeRenderOrderKey(child),
                    ChildNode = child
                });
            }

            foreach (var variant in directVariants ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (variant == null)
                    continue;

                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetVariantOccurrenceOrderKey(variant),
                    DisplayPriority = IsEmptyOrNoOpVariant(variant) ? 1 : 0,
                    PathOrderKey = GetVariantRenderOrderKey(variant),
                    DirectVariant = variant
                });
            }

            return result
                .OrderBy(entry => entry.OccurrenceOrderKey)
                .ThenBy(entry => entry.DisplayPriority)
                .ThenBy(entry => entry.PathOrderKey)
                .ThenBy(entry => entry.ChildNode == null ? 1 : 0)
                .ToList();
        }

        private static void RenderTopLevelGroup(TopLevelVariantGroup group, StringBuilder sb, int groupNumber)
        {
            string header = $"Вариант {groupNumber}";
            string sharedProbabilityLine = GetSharedProbabilityLine(group?.TreeRoot);
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(sharedProbabilityLine);
            if (!string.IsNullOrWhiteSpace(sharedProbabilityAnnotation))
                header += $" ({sharedProbabilityAnnotation})";

            if (!string.IsNullOrWhiteSpace(group?.Label))
                header += $": {group.Label}";
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

            var renderableChildren = (group.TreeRoot?.Children ?? Enumerable.Empty<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = group.TreeRoot?.DirectVariants?.Count(v => v != null) ?? 0;

            var directVariants = (group.TreeRoot?.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Where(v => ShouldRenderDirectVariant(v, siblingDirectVariantCount, renderableChildren.Count) &&
                    !ShouldSuppressAsRedundantTopLevelNoOpLeaf(group, v))
                .ToList();

            bool needGapAfterCommon = (group.TreeRoot?.CommonLines?.Count ?? 0) > 0 &&
                                      (renderableChildren.Count > 0 || directVariants.Count > 0);
            if (needGapAfterCommon)
                sb.AppendLine();

            bool canPromoteNoToChoice = renderableChildren.Count > 0 &&
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

            foreach (var entry in BuildOrderedRenderEntries(renderableChildren, directVariants))
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
                        sharedProbabilityLine);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(syntheticLabel, entry.DirectVariant, sb, new List<int> { groupNumber, childIndex++ }, 1);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf("N)", entry.DirectVariant, sb, new List<int> { groupNumber, childIndex++ }, 1);
                }
                else
                {
                    RenderLooseVariant(
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine);
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

        private static void RenderVariantTreeNode(
            VariantTreeNode node,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null)
        {
            if (!IsRenderableStructuralNode(node))
                return;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = node.DirectVariants?.Count(v => v != null) ?? 0;
            var renderableDirectVariants = OrderDirectVariants(node.DirectVariants)
                .Where(v => ShouldRenderDirectVariant(v, siblingDirectVariantCount, renderableChildren.Count))
                .ToList();

            string indent = new string(' ', depth * 3);
            string variantNumber = string.Join(".", numbering);
            var singleLeaf = renderableDirectVariants.Count == 1 && renderableChildren.Count == 0
                ? renderableDirectVariants[0].Variant
                : null;
            string sharedProbabilityLine = singleLeaf == null
                ? GetSharedProbabilityLine(node, inheritedProbabilityLine)
                : null;
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(sharedProbabilityLine);

            string header = $"{indent}Вариант {variantNumber}";
            if (singleLeaf != null)
            {
                var annotations = BuildVariantHeaderAnnotations(singleLeaf, inheritedProbabilityLine);
                if (annotations.Count > 0)
                    header += $" ({string.Join("; ", annotations)})";
            }
            else if (!string.IsNullOrWhiteSpace(sharedProbabilityAnnotation))
            {
                header += $" ({sharedProbabilityAnnotation})";
            }

            if (!string.IsNullOrWhiteSpace(node.Label))
                header += $": {node.Label}";
            else
                header += ":";

            sb.AppendLine(header);
            bool headerContainsProbability = VariantHeaderContainsProbability(header);

            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                node.CommonLines,
                headerContainsProbability);

            if (renderableDirectVariants.Count == 1 && renderableChildren.Count == 0)
            {
                AppendIndentedDisplayLines(
                    sb,
                    indent + "   ",
                    renderableDirectVariants[0].Lines,
                    headerContainsProbability);
                return;
            }

            bool needGapAfterCommon =
                node.CommonLines.Count > 0 &&
                (renderableChildren.Count > 0 || renderableDirectVariants.Count > 0);
            if (needGapAfterCommon)
                sb.AppendLine();

            bool canPromoteNoToChoice = renderableChildren.Count > 0 && !HasNodeWithLabel(node, "N)");
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

            foreach (var entry in BuildOrderedRenderEntries(renderableChildren, renderableDirectVariants))
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
                        descendantSuppressedProbabilityLine);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(syntheticLabel, entry.DirectVariant, sb, new List<int>(numbering) { nestedIndex++ }, depth + 1);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf("N)", entry.DirectVariant, sb, new List<int>(numbering) { nestedIndex++ }, depth + 1);
                }
                else
                {
                    RenderLooseVariant(
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine);
                }

                wroteAny = true;
            }
        }

        private static bool ShouldRenderAsNoChoiceVariant(VariantRenderItem item)
        {
            if (item == null)
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

        private static void RenderChoiceLeaf(string label, VariantRenderItem item, StringBuilder sb, List<int> numbering, int depth)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}: {label}";
            sb.AppendLine(header);
        }

        private static IEnumerable<VariantRenderItem> OrderDirectVariants(IEnumerable<VariantRenderItem> variants)
        {
            return (variants ?? Enumerable.Empty<VariantRenderItem>())
                .OrderBy(v => v?.Lines?.Count ?? 0)
                .ThenByDescending(v => v?.Variant?.ProbabilityDenominator ?? 1)
                .ThenByDescending(v => v?.Variant?.ProbabilityNumerator ?? 1)
                .ThenBy(v => v?.Variant?.PathId ?? int.MaxValue);
        }

        private static void RenderLooseVariant(
            VariantRenderItem item,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            var annotations = BuildVariantHeaderAnnotations(item?.Variant, inheritedProbabilityLine);
            if (annotations.Count > 0)
                header += $" ({string.Join("; ", annotations)})";
            header += ":";
            sb.AppendLine(header);
            bool headerContainsProbability = VariantHeaderContainsProbability(header);

            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                item?.Lines,
                headerContainsProbability);
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
            if (!string.Equals(fileNameOnly, "CAVE7.OVR", StringComparison.OrdinalIgnoreCase))
                return null;

            if (obj?.PatchAddress != 0x005C)
                return null;

            string answer = TryReadCave7RiddleAnswer(filename, config);
            if (string.IsNullOrWhiteSpace(answer))
                return null;

            return BuildSpoilerAnswerLine(answer);
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
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance)
        {
            var lines = new List<string>();

            if (!obj.HasMonsterStatChanges)
                return lines;

            var powerDesc = obj.GetMonsterPowerDescription(defaultMonsterPower);
            if (powerDesc != null) lines.Add(powerDesc);

            var levelDesc = obj.GetMonsterLevelDescription(defaultMonsterLevel);
            if (levelDesc != null) lines.Add(levelDesc);

            var batchCountDesc = obj.GetMonsterBatchCountDescription(defaultMonsterBatchCount);
            if (batchCountDesc != null) lines.Add(batchCountDesc);

            var lightingDesc = obj.GetDarkeningLevelDescription(defaultDarkeningLevel);
            if (lightingDesc != null) lines.Add(lightingDesc);

            var randomEncounterDesc = obj.GetRandomEncounterChanceDescription(defaultRandomEncounterChance);
            if (randomEncounterDesc != null) lines.Add(randomEncounterDesc);

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
            var effects = (obj.PartyEffects ?? new List<PartyEffect>())
                .Where(effect => effect != null)
                .ToList();

            foreach (var effect in GetOrderedDisplayablePartyEffects(effects))
            {
                string description = PartyEffectSemantics.BuildHumanDescription(effect);
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
            if (obj?.PathVariants != null && obj.PathVariants.TryGetValue(variantKey, out var variant))
            {
                probabilityKey = BuildProbabilityLine(variant) ?? string.Empty;
                occurrenceKey = BuildOccurrenceLine(variant) ?? string.Empty;
            }

            string linesKey = string.Join("\n", (lines ?? new List<string>()).Select(line => line ?? string.Empty));
            return occurrenceKey + "\n---\n" + probabilityKey + "\n---\n" + linesKey;
        }

        private static string BuildDisplayedVariantItemKey(VariantRenderItem item)
        {
            string occurrenceKey = BuildOccurrenceLine(item?.Variant) ?? string.Empty;
            string probabilityKey = BuildProbabilityLine(item?.Variant) ?? string.Empty;
            string branchKey = string.Join("|",
                GetRelevantBranchChoices(item?.Variant)
                    .Select(choice => NormalizeChoiceLabel(choice?.Label) ?? string.Empty));
            string linesKey = string.Join("\n",
                (item?.Lines ?? new List<string>())
                    .Select(line => line?.TrimEnd() ?? string.Empty));

            return string.Join("\n---\n", occurrenceKey, probabilityKey, branchKey, linesKey);
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

            if (colonIndex >= 0)
            {
                string afterColon = rawText.Substring(colonIndex + 1).Trim();
                candidate = !string.IsNullOrEmpty(afterColon)
                    ? afterColon
                    : rawText.Trim();
            }
            else
            {
                candidate = rawText.Trim();
            }

            string decodedText = DecodeTextString(candidate);
            if (string.IsNullOrWhiteSpace(decodedText))
                return null;

            return decodedText.TrimEnd('\r');
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

