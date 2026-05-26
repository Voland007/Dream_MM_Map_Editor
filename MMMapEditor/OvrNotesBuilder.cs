﻿
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.CompilerServices;

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
        private static readonly ConditionalWeakTable<OvrObject, VariantRenderPreparationCache> VariantRenderPreparationCaches =
            new ConditionalWeakTable<OvrObject, VariantRenderPreparationCache>();
        private const string SpoilerAnswerLinePrefix = "[ !!! ВНИМАНИЕ СПОЙЛЕР !!! ] ПРАВИЛЬНЫЙ ОТВЕТ: ";
        private const string RiddleAnswerPrompt = "ANSWER:>";
        private const string ResponseAnswerPrompt = "RESPONSE:";
        private const string GuardHeaderAnnotationPrefix = "при условии:";
        private const string LoopInternalEradicatedExclusionAnnotation =
            "за исключением персонажей, у которых CONDITION == ERADICATED";
        private const string NoOpLine = "Ничего не происходит";
        private const string NoOpBecauseNoConditionsLine = "Ничего не происходит (не выполнены условия для наступления ни одного варианта)";
        private const string LostTextLine = "YOU'RE LOST!!!";
        private const string LostDirectionEffectLine =
            "Направление партии случайно поворачивается налево или направо (50%/50%)";
        private const string WholePartyConditionChangePrefix = "CONDITION персонажа(ей) в партии изменяется на ";
        private const string LegacyWholePartyConditionChangePrefix = "CONDITION всех персонажей в партии изменяется на ";
        private const string CurrentPartyMemberConditionChangePrefix = "CONDITION текущего персонажа партии изменяется на ";
        private const ushort PartyCountAddress = 0x3BC0;
        private const ushort AssumedUserVisibleFoodUpperBoundExclusive = 40;
        private const ushort AssumedUserVisibleStatusUpperBoundExclusive = 0x80;
        private const string TechObjectCentralOption = "TechObject";
        private const uint RenderTemplateDefaultByteEntry = 0x5855;
        private const uint RenderTemplateCallerByteEntry = 0x585B;
        private const decimal VariantOutcomeOrderStride = 10000000000000000000000000m;
        private const int FlatSemanticVariantKeyBase = int.MinValue / 2;
        private const int FlatSemanticVariantKeyStride = 10000;
        private static readonly Dictionary<ushort, string[]> KnownBinaryStateConditionLabels =
            new Dictionary<ushort, string[]>
            {
                { 0x3C97, new[] { "PROTECTION FROM FIRE отсутствует", "PROTECTION FROM FIRE активно" } },
                { 0x3C98, new[] { "PROTECTION FROM POISON отсутствует", "PROTECTION FROM POISON активно" } },
                { 0x3C99, new[] { "PROTECTION FROM ACID отсутствует", "PROTECTION FROM ACID активно" } },
                { 0x3C9E, new[] { "LEVITATE отсутствует", "LEVITATE активно" } },
                { 0x3CA1, new[] { "PSYCHIC PROTECTION отсутствует", "PSYCHIC PROTECTION активно" } },
                { 0xC980, new[] { "квест не взят", "квест взят" } }
            };
        private static readonly string[] KnownProtectiveSpellConditionNames =
        {
            "PROTECTION FROM FIRE",
            "PROTECTION FROM ACID",
            "PROTECTION FROM POISON",
            "LEVITATE",
            "PSYCHIC PROTECTION"
        };

        public static OvrNotesBuildResult BuildNotes(
            string filename,
            Dictionary<Point, string> existingCentralOptions,
            Dictionary<Point, string> existingNotes = null,
            Dictionary<Point, Directions<bool>> existingMessageStates = null,
            bool? useHierarchicalView = null,
            IReadOnlyList<OvrObject> preAnalyzedObjects = null,
            ISet<Point> cellsToBuild = null,
            bool buildInlineStyleSpans = true)
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

            if (!OvrFileConfigs.TryGetConfigForFile(filename, out var config, out string configError))
                throw new InvalidOperationException(configError ?? $"Конфигурация для файла {fileNameOnly} не найдена.");

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
                    defaultDarkeningLevel = OvrMapFlags.GetDarknessValue(fileData[config.DarkeningLevel]);
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
            var renderedNoteCache = !buildInlineStyleSpans
                ? new Dictionary<RenderedObjectNoteCacheKey, string>()
                : null;

            result.TotalObjects = allObjects.Count;
            result.TableObjects = allObjects.Count(o => o.IsFromTable);
            result.SpecObjects = allObjects.Count(o => !o.IsFromTable);

            var tableObjectCoords = new HashSet<string>(
                allObjects.Where(obj => obj.IsFromTable).Select(obj => $"{obj.X},{obj.Y}")
            );

            foreach (var obj in allObjects)
            {
                obj.FlatDisplayPathVariants?.Clear();

                Point pos = new Point(obj.X, obj.Y);
                string coordKey = $"{obj.X},{obj.Y}";

                if (cellsToBuild != null && !cellsToBuild.Contains(pos))
                    continue;

                if (!result.CentralOptions.TryGetValue(pos, out string existingOption))
                    continue;

                if (obj.IsFromTable && !(tableObjectCoords.Contains(coordKey) && existingOption == "Случайная встреча"))
                    continue;

                string existingCellNotes = result.NotesPerCell.TryGetValue(pos, out var notes)
                    ? notes
                    : "";
                var existingInlineStyles = buildInlineStyleSpans &&
                    result.NoteStyleSpansPerCell.TryGetValue(pos, out var priorInlineStyles)
                    ? (priorInlineStyles ?? new List<NoteInlineStyleSpan>())
                        .Where(span => span != null && span.Length > 0 && span.Start >= 0)
                        .Select(span => span.Clone())
                        .ToList()
                    : new List<NoteInlineStyleSpan>();
                var rawOverlayTexts = buildInlineStyleSpans
                    ? CollectRawOverlayVisibleTexts(obj)
                    : new List<string>();
                string technicalRenderPatchLine = TryBuildTechnicalRenderPatchGameplayNote(fileNameOnly, config, obj);

                if (obj.IsFromTable)
                {
                    result.CentralOptions[pos] = "AnyObject";
                }
                else if (!string.IsNullOrWhiteSpace(technicalRenderPatchLine))
                {
                    result.CentralOptions[pos] = TechObjectCentralOption;
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
                string specialFullNotes = TryBuildSpecialFullNotes(
                    fileNameOnly,
                    obj,
                    useHierarchical);
                var renderedNoteCacheKey = renderedNoteCache == null
                    ? null
                    : BuildRenderedObjectNoteCacheKey(
                        fileNameOnly,
                        useHierarchical,
                        obj,
                        defaultRandomEncounterMonsterPowerCap,
                        defaultRandomEncounterMonsterLevelCap,
                        defaultRandomEncounterMonsterBatchCountCap,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        existingCellNotes,
                        technicalRenderPatchLine,
                        specialSpoilerLine,
                        inlineSpecialSpoilerLine,
                        specialPlayerExplanation,
                        trailingRepeatedBattleWarningLine,
                        specialFullNotes);

                if (renderedNoteCacheKey != null &&
                    renderedNoteCache.TryGetValue(renderedNoteCacheKey, out string cachedRenderedNote))
                {
                    result.NotesPerCell[pos] = cachedRenderedNote;
                    continue;
                }

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
                var inlineStyles = buildInlineStyleSpans
                    ? new List<NoteInlineStyleSpan>()
                    : null;

                if (!string.IsNullOrWhiteSpace(technicalRenderPatchLine))
                {
                    AppendRenderedText(newNotes, inlineStyles, technicalRenderPatchLine);
                }
                else if (!string.IsNullOrWhiteSpace(specialFullNotes))
                {
                    AppendRenderedText(newNotes, inlineStyles, specialFullNotes);
                }
                else if (!useHierarchical && !string.IsNullOrWhiteSpace(flatSemanticNotes))
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
                            AppendRenderedText(newNotes, inlineStyles, $"{variantHeader}:\n");

                            foreach (var line in variant.Value)
                                AppendRenderedText(newNotes, inlineStyles, line + "\n");

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

                finalNoteText = NormalizeAreaE1JudgementStatueTechnicalNoteText(
                    fileNameOnly,
                    obj,
                    finalNoteText,
                    inlineStyles);

                string normalizedFinalNoteText = NormalizeNoteLineEndings(finalNoteText);
                NormalizeInlineStyleSpansForLineEndings(finalNoteText, normalizedFinalNoteText, inlineStyles);
                finalNoteText = normalizedFinalNoteText;

                AppendRawOverlayTextSpans(finalNoteText, inlineStyles, rawOverlayTexts);

                result.NotesPerCell[pos] = finalNoteText;
                if (buildInlineStyleSpans)
                {
                    result.NoteStyleSpansPerCell[pos] = inlineStyles
                        .Where(span => span != null && span.Length > 0 && span.Start >= 0)
                        .Select(span => span.Clone())
                        .ToList();
                }

                if (renderedNoteCacheKey != null)
                    renderedNoteCache[renderedNoteCacheKey] = finalNoteText;
            }

            ApplyAreaD4TechnicalRenderPatchFallback(
                fileNameOnly,
                config,
                result,
                cellsToBuild,
                buildInlineStyleSpans);

            return result;
        }

        private static RenderedObjectNoteCacheKey BuildRenderedObjectNoteCacheKey(
            string fileNameOnly,
            bool useHierarchical,
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string existingCellNotes,
            string technicalRenderPatchLine,
            string specialSpoilerLine,
            string inlineSpecialSpoilerLine,
            string specialPlayerExplanation,
            string trailingRepeatedBattleWarningLine,
            string specialFullNotes)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count == 0)
                return null;

            var orderedVariants = obj.PathVariants
                .OrderBy(kvp => kvp.Key)
                .ToList();
            return new RenderedObjectNoteCacheKey(
                fileNameOnly,
                useHierarchical,
                obj.IsFromTable,
                obj.PatchAddress,
                obj.DirectionByte,
                RequiresRenderedNoteCoordinateCacheDiscriminator(fileNameOnly, obj)
                    ? $"{obj.X:X2},{obj.Y:X2}"
                    : string.Empty,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                existingCellNotes,
                technicalRenderPatchLine,
                specialSpoilerLine,
                inlineSpecialSpoilerLine,
                specialPlayerExplanation,
                trailingRepeatedBattleWarningLine,
                specialFullNotes,
                orderedVariants.Select(kvp => kvp.Key).ToArray(),
                orderedVariants.Select(kvp => kvp.Value).ToArray());
        }

        private static bool RequiresRenderedNoteCoordinateCacheDiscriminator(string fileNameOnly, OvrObject obj)
        {
            return IsAreaE1JudgementStatueObject(fileNameOnly, obj);
        }

        private sealed class RenderedObjectNoteCacheKey : IEquatable<RenderedObjectNoteCacheKey>
        {
            private readonly string _fileNameOnly;
            private readonly bool _useHierarchical;
            private readonly bool _isFromTable;
            private readonly uint? _patchAddress;
            private readonly byte _directionByte;
            private readonly string _coordinateDiscriminator;
            private readonly byte _defaultRandomEncounterMonsterPowerCap;
            private readonly byte _defaultRandomEncounterMonsterLevelCap;
            private readonly byte _defaultRandomEncounterMonsterBatchCountCap;
            private readonly byte _defaultDarkeningLevel;
            private readonly byte _defaultRandomEncounterChance;
            private readonly string _existingCellNotes;
            private readonly string _technicalRenderPatchLine;
            private readonly string _specialSpoilerLine;
            private readonly string _inlineSpecialSpoilerLine;
            private readonly string _specialPlayerExplanation;
            private readonly string _trailingRepeatedBattleWarningLine;
            private readonly string _specialFullNotes;
            private readonly int[] _variantKeys;
            private readonly PathVariantInfo[] _variantReferences;

            public RenderedObjectNoteCacheKey(
                string fileNameOnly,
                bool useHierarchical,
                bool isFromTable,
                uint? patchAddress,
                byte directionByte,
                string coordinateDiscriminator,
                byte defaultRandomEncounterMonsterPowerCap,
                byte defaultRandomEncounterMonsterLevelCap,
                byte defaultRandomEncounterMonsterBatchCountCap,
                byte defaultDarkeningLevel,
                byte defaultRandomEncounterChance,
                string existingCellNotes,
                string technicalRenderPatchLine,
                string specialSpoilerLine,
                string inlineSpecialSpoilerLine,
                string specialPlayerExplanation,
                string trailingRepeatedBattleWarningLine,
                string specialFullNotes,
                int[] variantKeys,
                PathVariantInfo[] variantReferences)
            {
                _fileNameOnly = fileNameOnly ?? string.Empty;
                _useHierarchical = useHierarchical;
                _isFromTable = isFromTable;
                _patchAddress = patchAddress;
                _directionByte = directionByte;
                _coordinateDiscriminator = coordinateDiscriminator ?? string.Empty;
                _defaultRandomEncounterMonsterPowerCap = defaultRandomEncounterMonsterPowerCap;
                _defaultRandomEncounterMonsterLevelCap = defaultRandomEncounterMonsterLevelCap;
                _defaultRandomEncounterMonsterBatchCountCap = defaultRandomEncounterMonsterBatchCountCap;
                _defaultDarkeningLevel = defaultDarkeningLevel;
                _defaultRandomEncounterChance = defaultRandomEncounterChance;
                _existingCellNotes = existingCellNotes ?? string.Empty;
                _technicalRenderPatchLine = technicalRenderPatchLine ?? string.Empty;
                _specialSpoilerLine = specialSpoilerLine ?? string.Empty;
                _inlineSpecialSpoilerLine = inlineSpecialSpoilerLine ?? string.Empty;
                _specialPlayerExplanation = specialPlayerExplanation ?? string.Empty;
                _trailingRepeatedBattleWarningLine = trailingRepeatedBattleWarningLine ?? string.Empty;
                _specialFullNotes = specialFullNotes ?? string.Empty;
                _variantKeys = variantKeys ?? Array.Empty<int>();
                _variantReferences = variantReferences ?? Array.Empty<PathVariantInfo>();
            }

            public bool Equals(RenderedObjectNoteCacheKey other)
            {
                if (ReferenceEquals(this, other))
                    return true;

                if (other == null ||
                    _useHierarchical != other._useHierarchical ||
                    _isFromTable != other._isFromTable ||
                    _patchAddress != other._patchAddress ||
                    _directionByte != other._directionByte ||
                    _defaultRandomEncounterMonsterPowerCap != other._defaultRandomEncounterMonsterPowerCap ||
                    _defaultRandomEncounterMonsterLevelCap != other._defaultRandomEncounterMonsterLevelCap ||
                    _defaultRandomEncounterMonsterBatchCountCap != other._defaultRandomEncounterMonsterBatchCountCap ||
                    _defaultDarkeningLevel != other._defaultDarkeningLevel ||
                    _defaultRandomEncounterChance != other._defaultRandomEncounterChance ||
                    !string.Equals(_fileNameOnly, other._fileNameOnly, StringComparison.Ordinal) ||
                    !string.Equals(_coordinateDiscriminator, other._coordinateDiscriminator, StringComparison.Ordinal) ||
                    !string.Equals(_existingCellNotes, other._existingCellNotes, StringComparison.Ordinal) ||
                    !string.Equals(_technicalRenderPatchLine, other._technicalRenderPatchLine, StringComparison.Ordinal) ||
                    !string.Equals(_specialSpoilerLine, other._specialSpoilerLine, StringComparison.Ordinal) ||
                    !string.Equals(_inlineSpecialSpoilerLine, other._inlineSpecialSpoilerLine, StringComparison.Ordinal) ||
                    !string.Equals(_specialPlayerExplanation, other._specialPlayerExplanation, StringComparison.Ordinal) ||
                    !string.Equals(_trailingRepeatedBattleWarningLine, other._trailingRepeatedBattleWarningLine, StringComparison.Ordinal) ||
                    !string.Equals(_specialFullNotes, other._specialFullNotes, StringComparison.Ordinal) ||
                    _variantKeys.Length != other._variantKeys.Length ||
                    _variantReferences.Length != other._variantReferences.Length)
                {
                    return false;
                }

                for (int i = 0; i < _variantKeys.Length; i++)
                {
                    if (_variantKeys[i] != other._variantKeys[i])
                        return false;
                }

                for (int i = 0; i < _variantReferences.Length; i++)
                {
                    if (!ReferenceEquals(_variantReferences[i], other._variantReferences[i]))
                        return false;
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as RenderedObjectNoteCacheKey);
            }

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_fileNameOnly, StringComparer.Ordinal);
                hash.Add(_useHierarchical);
                hash.Add(_isFromTable);
                hash.Add(_patchAddress);
                hash.Add(_directionByte);
                hash.Add(_coordinateDiscriminator, StringComparer.Ordinal);
                hash.Add(_defaultRandomEncounterMonsterPowerCap);
                hash.Add(_defaultRandomEncounterMonsterLevelCap);
                hash.Add(_defaultRandomEncounterMonsterBatchCountCap);
                hash.Add(_defaultDarkeningLevel);
                hash.Add(_defaultRandomEncounterChance);
                hash.Add(_existingCellNotes, StringComparer.Ordinal);
                hash.Add(_technicalRenderPatchLine, StringComparer.Ordinal);
                hash.Add(_specialSpoilerLine, StringComparer.Ordinal);
                hash.Add(_inlineSpecialSpoilerLine, StringComparer.Ordinal);
                hash.Add(_specialPlayerExplanation, StringComparer.Ordinal);
                hash.Add(_trailingRepeatedBattleWarningLine, StringComparer.Ordinal);
                hash.Add(_specialFullNotes, StringComparer.Ordinal);

                foreach (int key in _variantKeys)
                    hash.Add(key);

                foreach (var variant in _variantReferences)
                    hash.Add(variant == null ? 0 : RuntimeHelpers.GetHashCode(variant));

                return hash.ToHashCode();
            }
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

            if (inlineStyles == null)
            {
                target.Append(InlineNoteStyleCodec.RenderVisibleText(rawText));
                return;
            }

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

            visibleText = InlineNoteStyleCodec.RenderVisibleText(decodedText);
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
            var protectedRanges = inlineStyles
                .Where(span => span != null &&
                               span.Kind == NoteInlineStyleKind.WheelRewardExplanation &&
                               span.Length > 0)
                .Select(span => (Start: span.Start, End: span.Start + span.Length))
                .ToList();
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

                    int matchEnd = matchIndex + rawOverlayText.Length;
                    if (protectedRanges.Any(range => matchIndex < range.End && matchEnd > range.Start))
                    {
                        searchStart = matchEnd;
                        continue;
                    }

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
            var variantContents = GetRawVariantContentsFromPathVariants(
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

        private static bool TryBuildLoopInternalStatusGuardCollapsedLines(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            out List<string> lines)
        {
            lines = null;

            var variants = obj?.PathVariants?.Values
                .Where(variant => variant != null)
                .OrderBy(GetPathOrderKey)
                .ToList();
            if (variants == null || variants.Count < 2)
                return false;

            string narrativeKey = BuildDecodedTextKey(variants[0].Texts);
            if (string.IsNullOrWhiteSpace(narrativeKey) ||
                variants.Any(variant => BuildDecodedTextKey(variant.Texts) != narrativeKey) ||
                !IsNorthClericsCureNarrative(narrativeKey))
            {
                return false;
            }

            if (variants.Any(HasDisqualifyingOutcomeForLoopInternalStatusGuardCollapse) ||
                variants.Any(HasDisqualifyingBranchChoiceForLoopInternalStatusGuardCollapse))
            {
                return false;
            }

            var effectVariants = variants
                .Where(variant => variant.PartyEffects?.Any(IsLoopInternalEradicatedStatusGuardedSubsetEffect) == true)
                .ToList();
            if (effectVariants.Count != 1)
                return false;

            var renderVariant = effectVariants[0];
            OvrObject variantObject = renderVariant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte);
            lines = BuildVariantLines(
                variantObject,
                renderVariant.Texts,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
            lines = NormalizeLoopInternalStatusGuardCollapsedEffectLines(lines);
            lines = NumberLootBlockIfNeeded(lines) ?? new List<string>();

            return lines.Any(line => !string.IsNullOrWhiteSpace(line));
        }

        private static bool IsNorthClericsCureNarrative(string narrativeKey)
        {
            return !string.IsNullOrWhiteSpace(narrativeKey) &&
                   narrativeKey.Contains("WE ARE THE CLERICS OF THE N.", StringComparison.Ordinal) &&
                   narrativeKey.Contains("YOUR PARTY IS CURED!", StringComparison.Ordinal);
        }

        private static List<string> NormalizeLoopInternalStatusGuardCollapsedEffectLines(IEnumerable<string> sourceLines)
        {
            var result = new List<string>();
            foreach (var line in sourceLines ?? Enumerable.Empty<string>())
            {
                string normalizedLine = line;
                if (string.Equals(
                        normalizedLine,
                        "CONDITION подходящих персонажей партии изменяется на GOOD",
                        StringComparison.Ordinal))
                {
                    normalizedLine = "CONDITION персонажей партии изменяется на GOOD";
                }
                else if (string.Equals(
                             normalizedLine,
                             InlineNoteStyleCodec.EncodeHpRestoredToMaximumText(
                                 "HP части партии восстанавливается до максимального значения"),
                             StringComparison.Ordinal) ||
                         string.Equals(
                             normalizedLine,
                             "HP части партии восстанавливается до максимального значения",
                             StringComparison.Ordinal))
                {
                    normalizedLine = InlineNoteStyleCodec.EncodeHpRestoredToMaximumText(
                        "HP персонажей партии восстанавливается до максимального значения");
                }

                result.Add(normalizedLine);
            }

            return result;
        }

        private static string BuildLoopInternalStatusGuardCollapsedNote(IEnumerable<string> lines)
        {
            var displayLines = (lines ?? Enumerable.Empty<string>())
                .ToList();
            if (displayLines.Count == 0)
                return null;

            var builder = new StringBuilder();
            builder.AppendLine("Эта ячейка содержит различные варианты текста:");
            builder.AppendLine($"Вариант 1 ({LoopInternalEradicatedExclusionAnnotation}):");
            foreach (string line in displayLines)
                builder.AppendLine(line ?? string.Empty);

            return builder.ToString().TrimEnd('\r', '\n');
        }

        private static bool TryBuildPerMemberAgeAdjustmentCollapsedNote(OvrObject obj, out string note)
        {
            note = null;

            if (!TryBuildPerMemberAgeAdjustmentCollapseInfo(obj, out var info))
                return false;

            var builder = new StringBuilder();
            builder.AppendLine("Эта ячейка содержит различные варианты текста:");
            builder.AppendLine($"Вариант 1 (выбор {info.EffectChoiceLabel}):");
            foreach (string line in info.NarrativeLines)
                builder.AppendLine(line ?? string.Empty);

            string ageBlock = BuildPerMemberAgeAdjustmentSummaryBlock(info.Outcomes);
            if (string.IsNullOrWhiteSpace(ageBlock))
                return false;

            builder.AppendLine(ageBlock);

            if (!string.IsNullOrWhiteSpace(info.NoEffectChoiceLabel))
            {
                builder.AppendLine();
                builder.AppendLine($"Вариант 2 (выбор {info.NoEffectChoiceLabel}):");
                foreach (string line in info.NarrativeLines)
                    builder.AppendLine(line ?? string.Empty);
            }

            note = builder.ToString().TrimEnd('\r', '\n');
            return true;
        }

        private static string BuildPerMemberAgeAdjustmentSummaryBlock(
            IEnumerable<PerMemberAgeAdjustmentOutcome> outcomes)
        {
            var orderedOutcomes = (outcomes ?? Enumerable.Empty<PerMemberAgeAdjustmentOutcome>())
                .OrderBy(GetPerMemberAgeAdjustmentOutcomeOrder)
                .ToList();
            if (orderedOutcomes.Count == 0)
                return null;

            var builder = new StringBuilder();
            builder.AppendLine($"У каждого персонажа партии меняется {PartyAgeSemantics.FieldLabel}: если");

            for (int i = 0; i < orderedOutcomes.Count; i++)
            {
                var outcome = orderedOutcomes[i];
                string predicateText = BuildPerMemberAgeConditionText(outcome.Predicate);
                string effectText = BuildPerMemberAgeCompactEffectText(outcome.Effect, outcome.Predicate);
                if (string.IsNullOrWhiteSpace(predicateText) || string.IsNullOrWhiteSpace(effectText))
                    return null;

                builder.AppendLine($"{i + 1}) {predicateText}: {effectText}");
            }

            return InlineNoteStyleCodec.EncodeAgeChangeNoteText(
                builder.ToString().TrimEnd('\r', '\n'));
        }

        private static bool TryBuildPerMemberAgeAdjustmentCollapseInfo(
            OvrObject obj,
            out PerMemberAgeAdjustmentCollapseInfo info)
        {
            info = null;

            var variants = obj?.PathVariants?.Values
                .Where(variant => variant != null)
                .OrderBy(GetPathOrderKey)
                .ToList();
            if (variants == null || variants.Count < 2)
                return false;

            var outcomes = new List<PerMemberAgeAdjustmentOutcome>();
            foreach (var variant in variants)
            {
                var effect = GetSinglePerMemberAgeAdjustmentEffect(variant);
                if (effect == null)
                    continue;

                var predicate = GetSingleAgeBranchPredicate(variant);
                string choiceLabel = GetPrimaryPromptChoiceToken(variant);
                if (predicate == null || string.IsNullOrWhiteSpace(choiceLabel))
                    return false;

                outcomes.Add(new PerMemberAgeAdjustmentOutcome
                {
                    Variant = variant,
                    Predicate = predicate,
                    Effect = effect,
                    ChoiceLabel = choiceLabel
                });
            }

            if (outcomes.Count != 2)
                return false;

            string effectChoiceLabel = outcomes[0].ChoiceLabel;
            if (outcomes.Any(outcome => !string.Equals(outcome.ChoiceLabel, effectChoiceLabel, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (!AreComplementaryAgePredicates(outcomes[0].Predicate, outcomes[1].Predicate))
                return false;

            string narrativeKey = BuildDecodedTextKey(outcomes[0].Variant.Texts);
            if (string.IsNullOrWhiteSpace(narrativeKey) ||
                outcomes.Any(outcome => BuildDecodedTextKey(outcome.Variant.Texts) != narrativeKey))
            {
                return false;
            }

            var outcomeVariants = outcomes
                .Select(outcome => outcome.Variant)
                .ToHashSet();
            var nonOutcomeVariants = variants
                .Where(variant => !outcomeVariants.Contains(variant))
                .ToList();

            if (nonOutcomeVariants.Any(HasNonTextOutcome) ||
                nonOutcomeVariants.Any(variant => BuildDecodedTextKey(variant.Texts) != narrativeKey))
            {
                return false;
            }

            string noEffectChoiceLabel = nonOutcomeVariants
                .Select(GetPrimaryPromptChoiceToken)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SingleOrDefault();

            info = new PerMemberAgeAdjustmentCollapseInfo
            {
                EffectChoiceLabel = effectChoiceLabel.ToUpperInvariant(),
                NoEffectChoiceLabel = noEffectChoiceLabel?.ToUpperInvariant(),
                NarrativeLines = DecodeNoteTexts(outcomes[0].Variant.Texts)
                    .Select(line => line ?? string.Empty)
                    .ToList(),
                Outcomes = outcomes
            };

            return true;
        }

        private static PartyEffect GetSinglePerMemberAgeAdjustmentEffect(PathVariantInfo variant)
        {
            var effects = variant?.PartyEffects?
                .Where(effect => effect != null)
                .ToList() ?? new List<PartyEffect>();
            if (effects.Count != 1)
                return null;

            var effect = effects[0];
            if (!PartyAgeSemantics.IsAgeField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                !PartyEffectSemantics.IsLoopDerived(effect) ||
                PartyEffectSemantics.GetEffectiveScope(effect) != PartyEffectScope.WholeParty ||
                !effect.ImmediateValue.HasValue)
            {
                return null;
            }

            var operation = PartyEffectSemantics.GetEffectiveOperation(effect);
            return operation == PartyEffectOperation.Write ||
                   operation == PartyEffectOperation.Increment ||
                   operation == PartyEffectOperation.Decrement
                ? effect
                : null;
        }

        private static PartyPredicate GetSingleAgeBranchPredicate(PathVariantInfo variant)
        {
            var predicates = variant?.BranchChoices?
                .Select(choice => choice?.GuardPredicate)
                .Where(IsAgeBranchPredicate)
                .ToList() ?? new List<PartyPredicate>();

            return predicates.Count == 1
                ? predicates[0]
                : null;
        }

        private static bool IsAgeBranchPredicate(PartyPredicate predicate)
        {
            return predicate != null &&
                   PartyAgeSemantics.IsAgeField(predicate.Field) &&
                   TryGetExactPredicateValue(predicate, out _);
        }

        private static bool AreComplementaryAgePredicates(PartyPredicate left, PartyPredicate right)
        {
            if (!TryGetExactPredicateValue(left, out ushort leftValue) ||
                !TryGetExactPredicateValue(right, out ushort rightValue) ||
                leftValue != rightValue)
            {
                return false;
            }

            return IsComplementaryComparisonPair(left.Comparison, right.Comparison);
        }

        private static bool IsComplementaryComparisonPair(
            PartyPredicateComparison left,
            PartyPredicateComparison right)
        {
            return (left == PartyPredicateComparison.LessThan && right == PartyPredicateComparison.GreaterOrEqual) ||
                   (left == PartyPredicateComparison.GreaterOrEqual && right == PartyPredicateComparison.LessThan) ||
                   (left == PartyPredicateComparison.LessOrEqual && right == PartyPredicateComparison.GreaterThan) ||
                   (left == PartyPredicateComparison.GreaterThan && right == PartyPredicateComparison.LessOrEqual) ||
                   (left == PartyPredicateComparison.Equal && right == PartyPredicateComparison.NotEqual) ||
                   (left == PartyPredicateComparison.NotEqual && right == PartyPredicateComparison.Equal);
        }

        private static bool TryGetExactPredicateValue(PartyPredicate predicate, out ushort value)
        {
            value = 0;
            if (predicate == null)
                return false;

            if (predicate.ImmediateValue.HasValue)
            {
                value = predicate.ImmediateValue.Value;
                return true;
            }

            if (predicate.ImmediateRange?.IsExact == true)
            {
                value = predicate.ImmediateRange.Min;
                return true;
            }

            return false;
        }

        private static int GetPerMemberAgeAdjustmentOutcomeOrder(PerMemberAgeAdjustmentOutcome outcome)
        {
            return outcome?.Predicate?.Comparison switch
            {
                PartyPredicateComparison.LessThan => 0,
                PartyPredicateComparison.LessOrEqual => 0,
                PartyPredicateComparison.Equal => 1,
                PartyPredicateComparison.NotEqual => 2,
                PartyPredicateComparison.GreaterOrEqual => 3,
                PartyPredicateComparison.GreaterThan => 3,
                _ => 4
            };
        }

        private static string BuildPerMemberAgePredicateText(PartyPredicate predicate)
        {
            if (!TryGetExactPredicateValue(predicate, out ushort value))
                return null;

            string comparison = predicate.Comparison switch
            {
                PartyPredicateComparison.Equal => "=",
                PartyPredicateComparison.NotEqual => "!=",
                PartyPredicateComparison.LessThan => "<",
                PartyPredicateComparison.LessOrEqual => "<=",
                PartyPredicateComparison.GreaterThan => ">",
                PartyPredicateComparison.GreaterOrEqual => ">=",
                _ => null
            };

            return string.IsNullOrWhiteSpace(comparison)
                ? null
                : $"{PartyAgeSemantics.FieldLabel} {comparison} {PartyAgeSemantics.FormatYears(value)}";
        }

        private static string BuildPerMemberAgeConditionText(PartyPredicate predicate)
        {
            if (!TryGetExactPredicateValue(predicate, out ushort value))
                return null;

            string comparison = predicate.Comparison switch
            {
                PartyPredicateComparison.Equal => "=",
                PartyPredicateComparison.NotEqual => "!=",
                PartyPredicateComparison.LessThan => "<",
                PartyPredicateComparison.LessOrEqual => "<=",
                PartyPredicateComparison.GreaterThan => ">",
                PartyPredicateComparison.GreaterOrEqual => ">=",
                _ => null
            };

            return string.IsNullOrWhiteSpace(comparison)
                ? null
                : $"{comparison} {PartyAgeSemantics.FormatYears(value)}";
        }

        private static string BuildPerMemberAgeCompactEffectText(PartyEffect effect, PartyPredicate predicate)
        {
            if (effect == null || !effect.ImmediateValue.HasValue)
                return null;

            ushort value = effect.ImmediateValue.Value;
            var operation = PartyEffectSemantics.GetEffectiveOperation(effect);
            string adjustmentSummary = PartyAgeSemantics.FormatAdjustmentSummary(operation, value);
            if (!string.IsNullOrWhiteSpace(adjustmentSummary))
                return adjustmentSummary;

            if (operation == PartyEffectOperation.Write)
            {
                string directionHint = BuildAgeAssignmentDirectionHint(predicate, value);
                string assignmentText = $"становится {PartyAgeSemantics.FormatAssignmentSummary(value)}";
                return string.IsNullOrWhiteSpace(directionHint)
                    ? assignmentText
                    : $"{assignmentText}  ({directionHint})";
            }

            return null;
        }

        private static string BuildPerMemberAgeEffectText(PartyEffect effect, PartyPredicate predicate)
        {
            if (effect == null || !effect.ImmediateValue.HasValue)
                return null;

            ushort value = effect.ImmediateValue.Value;
            var operation = PartyEffectSemantics.GetEffectiveOperation(effect);
            string adjustmentSummary = PartyAgeSemantics.FormatAdjustmentSummary(operation, value);
            if (!string.IsNullOrWhiteSpace(adjustmentSummary))
                return InlineNoteStyleCodec.EncodeAgeChangeNoteText(
                    $"изменился {PartyAgeSemantics.FieldLabel}: {adjustmentSummary}");

            if (operation == PartyEffectOperation.Write)
            {
                string deltaHint = BuildAgeAssignmentDeltaHint(predicate, value);
                string baseText = $"изменился {PartyAgeSemantics.FieldLabel}: {PartyAgeSemantics.FormatAssignmentSummary(value)}";
                string visibleText = string.IsNullOrWhiteSpace(deltaHint)
                    ? baseText
                    : $"{baseText} ({deltaHint})";
                return InlineNoteStyleCodec.EncodeAgeChangeNoteText(visibleText);
            }

            return null;
        }

        private static string BuildAgeAssignmentDirectionHint(PartyPredicate predicate, ushort targetValue)
        {
            if (!TryBuildAgePredicateValueRange(predicate, out int minValue, out int maxValue))
                return null;

            if (maxValue > targetValue)
                return "молодеют";

            if (minValue < targetValue)
                return "стареют";

            return "без изменения";
        }

        private static string BuildAgeAssignmentDeltaHint(PartyPredicate predicate, ushort targetValue)
        {
            if (!TryBuildAgePredicateValueRange(predicate, out int minValue, out int maxValue))
                return "точная разница зависит от текущего возраста";

            var parts = new List<string>();
            if (minValue < targetValue)
            {
                int olderRangeMaxValue = Math.Min(maxValue, targetValue - 1);
                int olderMinDelta = targetValue - olderRangeMaxValue;
                int olderMaxDelta = targetValue - minValue;
                parts.Add($"{BuildAgeValueRangeText(minValue, olderRangeMaxValue)}: стареют на {PartyAgeSemantics.FormatYearRange(olderMinDelta, olderMaxDelta)}");
            }

            if (minValue <= targetValue && targetValue <= maxValue)
                parts.Add($"{BuildAgeValueRangeText(targetValue, targetValue)}: без изменения");

            if (maxValue > targetValue)
            {
                int youngerRangeMinValue = Math.Max(minValue, targetValue + 1);
                int youngerMinDelta = youngerRangeMinValue - targetValue;
                int youngerMaxDelta = maxValue - targetValue;
                parts.Add($"{BuildAgeValueRangeText(youngerRangeMinValue, maxValue)}: молодеют на {PartyAgeSemantics.FormatYearRange(youngerMinDelta, youngerMaxDelta)}");
            }

            return parts.Count == 0
                ? "без изменения"
                : string.Join("; ", parts);
        }

        private static bool TryBuildAgePredicateValueRange(PartyPredicate predicate, out int minValue, out int maxValue)
        {
            minValue = 0;
            maxValue = 255;
            if (!TryGetExactPredicateValue(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.Equal:
                    minValue = value;
                    maxValue = value;
                    return true;
                case PartyPredicateComparison.LessThan when value > 0:
                    maxValue = value - 1;
                    return true;
                case PartyPredicateComparison.LessOrEqual:
                    maxValue = value;
                    return true;
                case PartyPredicateComparison.GreaterThan when value < 255:
                    minValue = value + 1;
                    return true;
                case PartyPredicateComparison.GreaterOrEqual:
                    minValue = value;
                    return true;
                default:
                    return false;
            }
        }

        private static string BuildAgeValueRangeText(int minValue, int maxValue)
        {
            if (minValue == maxValue)
                return $"при текущем возрасте {PartyAgeSemantics.FormatYears((ushort)minValue)}";

            return $"при текущем возрасте {minValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{PartyAgeSemantics.FormatYears((ushort)maxValue)}";
        }

        private static string GetPrimaryPromptChoiceToken(PathVariantInfo variant)
        {
            foreach (var choice in variant?.BranchChoices ?? new List<BranchChoice>())
            {
                var normalized = NormalizeBranchChoiceForDisplay(choice);
                string label = NormalizeUserVisibleChoiceLabel(normalized?.Label);
                string token = ExtractPromptChoiceToken(label);
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }

            return null;
        }

        private static string ExtractPromptChoiceToken(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            string token = label.Trim();
            if (token.EndsWith(")", StringComparison.Ordinal))
                token = token.Substring(0, token.Length - 1).TrimEnd();

            return string.Equals(token, "ESC", StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(token, @"^[A-Za-z0-9]$", RegexOptions.CultureInvariant)
                ? token.ToUpperInvariant()
                : null;
        }

        private static bool TryBuildPermanentStatRaiseCollapsedNote(OvrObject obj, out string note)
        {
            note = null;

            if (!TryBuildPermanentStatRaiseCollapseInfo(obj, out var info))
                return false;

            string headerAnnotation = BuildPermanentStatRaiseCollapseHeaderAnnotation(info);
            if (string.IsNullOrWhiteSpace(headerAnnotation))
                return false;

            var builder = new StringBuilder();
            builder.AppendLine("Эта ячейка содержит различные варианты текста:");
            builder.AppendLine($"Вариант 1 ({headerAnnotation}):");

            foreach (string line in info.NarrativeLines)
                builder.AppendLine(line ?? string.Empty);

            note = builder.ToString().TrimEnd('\r', '\n');
            return true;
        }

        private static bool TryBuildPermanentStatRaiseCollapseInfo(
            OvrObject obj,
            out PermanentStatRaiseCollapseInfo info)
        {
            info = null;

            var variants = obj?.PathVariants?.Values
                .Where(variant => variant != null)
                .OrderBy(GetPathOrderKey)
                .ToList();
            if (variants == null || variants.Count < 2)
                return false;

            var narrativeLines = DecodeNoteTexts(variants[0].Texts)
                .Select(line => line ?? string.Empty)
                .ToList();
            string narrativeKey = string.Join("\n", narrativeLines);
            if (string.IsNullOrWhiteSpace(narrativeKey) ||
                variants.Any(variant => BuildDecodedTextKey(variant.Texts) != narrativeKey))
            {
                return false;
            }

            var statFields = variants
                .SelectMany(GetPermanentStatRaiseRelatedFields)
                .Where(PartyPermanentStatSemantics.IsPermanentStatField)
                .Distinct()
                .ToList();
            if (statFields.Count != 1)
                return false;

            PartyFieldKind statField = statFields[0];
            if (!PartyPermanentStatSemantics.TryGetRaiseFlagMask(statField, out byte expectedMask) ||
                !PartyPermanentStatSemantics.TryGetRaiseFlagStatName(expectedMask, out string statName))
            {
                return false;
            }

            var masks = variants
                .SelectMany(GetPermanentStatRaiseFlagMasks)
                .Distinct()
                .ToList();
            if (masks.Count == 0 || masks.Any(mask => mask != expectedMask))
                return false;

            if (variants.Any(variant => HasDisqualifyingOutcomeForPermanentStatRaiseCollapse(variant, statField)))
                return false;

            TryGetPermanentStatRaiseLimit(
                variants,
                statField,
                out string limitExceptionOperator,
                out ushort? limitExceptionValue);

            info = new PermanentStatRaiseCollapseInfo
            {
                StatField = statField,
                StatName = statName,
                RaiseFlagMask = expectedMask,
                LimitExceptionOperator = limitExceptionOperator,
                LimitExceptionValue = limitExceptionValue,
                NarrativeLines = narrativeLines
            };
            return true;
        }

        private static string BuildPermanentStatRaiseCollapseHeaderAnnotation(
            PermanentStatRaiseCollapseInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.StatName))
                return null;

            var conditions = new List<string>
            {
                $"текущая клетка ещё не посещена ({PartyPermanentStatSemantics.RaiseFlagResetHint})"
            };

            string limitCondition = BuildPermanentStatRaiseLimitRequirementText(info);
            if (!string.IsNullOrWhiteSpace(limitCondition))
                conditions.Add(limitCondition);

            return "для персонажей, у которых " + string.Join(" и ", conditions);
        }

        private static string BuildPermanentStatRaiseLimitRequirementText(
            PermanentStatRaiseCollapseInfo info)
        {
            if (info == null ||
                string.IsNullOrWhiteSpace(info.LimitExceptionOperator) ||
                !info.LimitExceptionValue.HasValue)
            {
                return null;
            }

            string fieldLabel = PartyPermanentStatSemantics.GetFieldLabel(info.StatField);
            string requirementOperator = InvertPermanentStatRaiseLimitExceptionOperator(info.LimitExceptionOperator);
            return string.IsNullOrWhiteSpace(fieldLabel)
                ? null
                : $"{fieldLabel} {requirementOperator} {info.LimitExceptionValue.Value}";
        }

        private static string InvertPermanentStatRaiseLimitExceptionOperator(string exceptionOperator)
        {
            return exceptionOperator switch
            {
                ">=" => "<",
                ">" => "<=",
                "<=" => ">",
                "<" => ">=",
                "==" => "!=",
                "!=" => "==",
                _ => exceptionOperator
            };
        }

        private static bool TryBuildPermanentStatRaiseVariantInfo(
            PathVariantInfo variant,
            out PermanentStatRaiseCollapseInfo info)
        {
            info = null;
            if (variant == null)
                return false;

            if (!MightContainPermanentStatRaiseVariant(variant))
                return false;

            var cacheEntry = GetPermanentStatRaiseVariantInfoCacheEntry(variant);

            if (cacheEntry?.HasInfo != true)
                return false;

            info = cacheEntry.Info;
            return true;
        }

        private static PermanentStatRaiseVariantInfoCacheEntry GetPermanentStatRaiseVariantInfoCacheEntry(
            PathVariantInfo variant)
        {
            lock (variant)
            {
                if (!variant.OvrNotesPermanentStatRaiseVariantInfoCacheComputed)
                {
                    variant.OvrNotesPermanentStatRaiseVariantInfoCacheEntry =
                        BuildPermanentStatRaiseVariantInfoCacheEntry(variant);
                    variant.OvrNotesPermanentStatRaiseVariantInfoCacheComputed = true;
                }

                return variant.OvrNotesPermanentStatRaiseVariantInfoCacheEntry
                    as PermanentStatRaiseVariantInfoCacheEntry;
            }
        }

        private static bool MightContainPermanentStatRaiseVariant(PathVariantInfo variant)
        {
            var effects = variant?.PartyEffects;
            if (effects == null || effects.Count == 0)
                return false;

            bool hasPermanentStatChange = false;
            bool hasRaiseFlagSet = false;
            foreach (var effect in effects)
            {
                if (effect == null || !PartyEffectSemantics.IsStateChanging(effect))
                    continue;

                var field = PartyEffectSemantics.GetEffectiveField(effect);
                if (PartyPermanentStatSemantics.IsPermanentStatField(field))
                    hasPermanentStatChange = true;
                else if (field == PartyFieldKind.PermanentStatRaiseFlags &&
                         PartyEffectSemantics.GetEffectiveOperation(effect) == PartyEffectOperation.BitSet)
                {
                    hasRaiseFlagSet = true;
                }

                if (hasPermanentStatChange && hasRaiseFlagSet)
                    return true;
            }

            return false;
        }

        private static PermanentStatRaiseVariantInfoCacheEntry BuildPermanentStatRaiseVariantInfoCacheEntry(
            PathVariantInfo variant)
        {
            return TryComputePermanentStatRaiseVariantInfo(variant, out var info)
                ? new PermanentStatRaiseVariantInfoCacheEntry { HasInfo = true, Info = info }
                : new PermanentStatRaiseVariantInfoCacheEntry();
        }

        private static bool TryComputePermanentStatRaiseVariantInfo(
            PathVariantInfo variant,
            out PermanentStatRaiseCollapseInfo info)
        {
            info = null;
            if (variant == null)
                return false;

            var effects = variant.PartyEffects?
                .Where(effect => effect != null)
                .ToList() ?? new List<PartyEffect>();
            if (effects.Count == 0)
                return false;

            var statFields = effects
                .Select(PartyEffectSemantics.GetEffectiveField)
                .Where(PartyPermanentStatSemantics.IsPermanentStatField)
                .Distinct()
                .ToList();
            if (statFields.Count != 1)
                return false;

            PartyFieldKind statField = statFields[0];
            if (!PartyPermanentStatSemantics.TryGetRaiseFlagMask(statField, out byte expectedMask) ||
                !PartyPermanentStatSemantics.TryGetRaiseFlagStatName(expectedMask, out string statName))
            {
                return false;
            }

            bool hasStatRaiseEffect = effects.Any(effect =>
                PartyEffectSemantics.IsStateChanging(effect) &&
                PartyEffectSemantics.GetEffectiveField(effect) == statField);
            bool hasRaiseFlagSetEffect = effects.Any(effect =>
                PartyEffectSemantics.IsStateChanging(effect) &&
                PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.PermanentStatRaiseFlags &&
                PartyEffectSemantics.GetEffectiveOperation(effect) == PartyEffectOperation.BitSet &&
                TryGetSinglePermanentStatRaiseMask(effect.ImmediateValue, out byte effectMask) &&
                effectMask == expectedMask);
            if (!hasStatRaiseEffect || !hasRaiseFlagSetEffect)
                return false;

            var masks = GetPermanentStatRaiseFlagMasks(variant)
                .Distinct()
                .ToList();
            if (masks.Count == 0 || masks.Any(mask => mask != expectedMask))
                return false;

            TryGetPermanentStatRaiseLimit(
                new[] { variant },
                statField,
                out string limitExceptionOperator,
                out ushort? limitExceptionValue);

            info = new PermanentStatRaiseCollapseInfo
            {
                StatField = statField,
                StatName = statName,
                RaiseFlagMask = expectedMask,
                LimitExceptionOperator = limitExceptionOperator,
                LimitExceptionValue = limitExceptionValue,
                NarrativeLines = DecodeNoteTexts(variant.Texts)
                    .Select(line => line ?? string.Empty)
                    .ToList()
            };
            return true;
        }

        private static string BuildPermanentStatRaiseHeaderAnnotation(PathVariantInfo variant)
        {
            return TryBuildPermanentStatRaiseVariantInfo(variant, out var info)
                ? BuildPermanentStatRaiseCollapseHeaderAnnotation(info)
                : null;
        }

        private static string BuildNarrativeCoveredConditionalStatRewardHeaderAnnotation(
            PathVariantInfo variant)
        {
            return TryBuildNarrativeCoveredConditionalStatRewardInfo(variant, out var info) &&
                   ShouldUseNarrativeCoveredConditionalStatRewardHeader(variant, info)
                ? info.HeaderAnnotation
                : null;
        }

        private static bool IsPermanentStatRaiseOutcomeEffect(
            PartyEffect effect,
            PermanentStatRaiseCollapseInfo info)
        {
            if (effect == null ||
                info == null ||
                !PartyEffectSemantics.IsStateChanging(effect))
            {
                return false;
            }

            var field = PartyEffectSemantics.GetEffectiveField(effect);
            if (field == info.StatField)
                return true;

            return field == PartyFieldKind.PermanentStatRaiseFlags &&
                   TryGetSinglePermanentStatRaiseMask(effect.ImmediateValue, out byte mask) &&
                   mask == info.RaiseFlagMask;
        }

        private static bool IsPermanentStatRaiseHeaderPredicate(
            PartyPredicate predicate,
            PathVariantInfo variant)
        {
            if (predicate == null ||
                !TryBuildPermanentStatRaiseVariantInfo(variant, out var info))
            {
                return false;
            }

            if (predicate.Comparison == PartyPredicateComparison.Equal &&
                TryGetSinglePermanentStatRaiseMask(predicate, out byte mask) &&
                mask == info.RaiseFlagMask)
            {
                return true;
            }

            if (TryBuildPermanentStatRaiseLimitException(
                    predicate,
                    info.StatField,
                    out string exceptionOperator,
                    out ushort? exceptionValue))
            {
                return string.Equals(exceptionOperator, info.LimitExceptionOperator, StringComparison.Ordinal) &&
                       exceptionValue == info.LimitExceptionValue;
            }

            return false;
        }

        private static bool TryBuildNarrativeCoveredConditionalStatRewardInfo(
            PathVariantInfo variant,
            out NarrativeCoveredConditionalStatRewardInfo info)
        {
            info = null;
            if (variant == null)
                return false;

            if (!MightContainNarrativeCoveredConditionalStatRewardVariant(variant))
                return false;

            var cacheEntry = GetNarrativeCoveredConditionalStatRewardInfoCacheEntry(variant);

            if (cacheEntry?.HasInfo != true)
                return false;

            info = cacheEntry.Info;
            return true;
        }

        private static NarrativeCoveredConditionalStatRewardInfoCacheEntry GetNarrativeCoveredConditionalStatRewardInfoCacheEntry(
            PathVariantInfo variant)
        {
            lock (variant)
            {
                if (!variant.OvrNotesNarrativeCoveredConditionalStatRewardInfoCacheComputed)
                {
                    variant.OvrNotesNarrativeCoveredConditionalStatRewardInfoCacheEntry =
                        BuildNarrativeCoveredConditionalStatRewardInfoCacheEntry(variant);
                    variant.OvrNotesNarrativeCoveredConditionalStatRewardInfoCacheComputed = true;
                }

                return variant.OvrNotesNarrativeCoveredConditionalStatRewardInfoCacheEntry
                    as NarrativeCoveredConditionalStatRewardInfoCacheEntry;
            }
        }

        private static bool MightContainNarrativeCoveredConditionalStatRewardVariant(
            PathVariantInfo variant)
        {
            if (variant == null ||
                variant.PartyEffects == null ||
                variant.PartyEffects.Count == 0 ||
                variant.Texts == null ||
                !variant.Texts.Any(text => !string.IsNullOrEmpty(text) && text.IndexOf('+') >= 0))
            {
                return false;
            }

            return variant.PartyEffects.Any(IsNarrativeCoveredConditionalStatRewardEffect);
        }

        private static NarrativeCoveredConditionalStatRewardInfoCacheEntry BuildNarrativeCoveredConditionalStatRewardInfoCacheEntry(
            PathVariantInfo variant)
        {
            return TryComputeNarrativeCoveredConditionalStatRewardInfo(variant, out var info)
                ? new NarrativeCoveredConditionalStatRewardInfoCacheEntry { HasInfo = true, Info = info }
                : new NarrativeCoveredConditionalStatRewardInfoCacheEntry();
        }

        private static bool TryComputeNarrativeCoveredConditionalStatRewardInfo(
            PathVariantInfo variant,
            out NarrativeCoveredConditionalStatRewardInfo info)
        {
            info = null;
            if (variant == null || TryBuildPermanentStatRaiseVariantInfo(variant, out _))
                return false;

            var narrativeLines = DecodeNoteTexts(variant.Texts)
                .Select(line => line ?? string.Empty)
                .ToList();
            if (narrativeLines.Count == 0)
                return false;

            var candidates = (variant.PartyEffects ?? new List<PartyEffect>())
                .Where(IsNarrativeCoveredConditionalStatRewardEffect)
                .GroupBy(effect => PartyEffectSemantics.GetEffectiveField(effect))
                .Select(group =>
                {
                    var effects = group.ToList();
                    var field = group.Key;
                    var guardPredicates = PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(
                        effects.SelectMany(PartyEffectSemantics.GetEffectiveGuardPredicates));
                    return new NarrativeCoveredConditionalStatRewardInfo
                    {
                        StatField = field,
                        GuardPredicates = guardPredicates,
                        GuardPredicateKeys = guardPredicates
                            .Select(PartyEffectSemantics.BuildPredicateKey)
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .ToHashSet(StringComparer.Ordinal),
                        EffectKeys = effects
                            .Select(PartyEffectSemantics.BuildSemanticKey)
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .ToHashSet(StringComparer.Ordinal)
                    };
                })
                .Where(candidate =>
                    candidate.GuardPredicateKeys.Count > 0 &&
                    HasNarrativeCoveredStatRewardCue(narrativeLines, candidate.StatField))
                .ToList();

            if (candidates.Count != 1)
                return false;

            string header = BuildNarrativeCoveredConditionalStatRewardHeaderAnnotation(candidates[0]);
            if (string.IsNullOrWhiteSpace(header))
                return false;

            candidates[0].HeaderAnnotation = header;
            info = candidates[0];
            return true;
        }

        private static bool IsNarrativeCoveredConditionalStatRewardEffect(PartyEffect effect)
        {
            if (!IsConditionalLoopSubsetOutcomeEffect(effect))
                return false;

            var field = PartyEffectSemantics.GetEffectiveField(effect);
            return PartyPermanentStatSemantics.IsPermanentStatField(field);
        }

        private static string BuildNarrativeCoveredConditionalStatRewardHeaderAnnotation(
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            var conditions = (info?.GuardPredicates ?? new List<PartyPredicate>())
                .Select(predicate => BuildNarrativeCoveredStatRewardRequirementText(predicate, info.StatField))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return conditions.Count == 0
                ? null
                : "для персонажей, у которых " + string.Join(" и ", conditions);
        }

        private static bool ShouldUseNarrativeCoveredConditionalStatRewardHeader(
            PathVariantInfo variant,
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            if (variant == null || info == null)
                return false;

            return !HasMixedNarrativeRewardCue(
                DecodeNoteTexts(variant.Texts),
                info.StatField);
        }

        private static string BuildNarrativeCoveredConditionalStatRewardSupplementalLine(
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.HeaderAnnotation))
                return null;

            string condition = StripNarrativeCoveredConditionalStatRewardHeaderPrefix(
                info.HeaderAnnotation);
            string statAlias = GetNarrativeCoveredStatRewardAliases(info.StatField)
                .FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias)) ?? "характеристики";

            return string.IsNullOrWhiteSpace(condition)
                ? null
                : InlineNoteStyleCodec.EncodeConditionalRewardMechanicsNoteText(
                    $"*** Примечание: повышение {statAlias} применяется только для персонажей, у которых {condition}. ***");
        }

        private static string StripNarrativeCoveredConditionalStatRewardHeaderPrefix(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            const string prefix = "для персонажей, у которых ";
            return normalized?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                ? normalized.Substring(prefix.Length)
                : normalized;
        }

        private static bool HasMixedNarrativeRewardCue(
            IEnumerable<string> lines,
            PartyFieldKind statField)
        {
            var aliases = GetNarrativeCoveredStatRewardAliases(statField)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (aliases.Count == 0)
                return false;

            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line) ||
                    !LineHasNarrativeCoveredStatRewardCue(line, aliases))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(
                    line,
                    @"(?<![A-Z0-9])\+\s*\d+\s+([A-Z]+)(?![A-Z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    string rewardToken = match.Groups[1].Value;
                    if (!aliases.Contains(rewardToken))
                        return true;
                }
            }

            return false;
        }

        private static bool LineHasNarrativeCoveredStatRewardCue(
            string line,
            IReadOnlyCollection<string> aliases)
        {
            if (string.IsNullOrWhiteSpace(line) || aliases == null || aliases.Count == 0)
                return false;

            foreach (string alias in aliases)
            {
                string pattern = @"(?<![A-Z0-9])\+\s*\d+\s+" +
                                 Regex.Escape(alias) +
                                 @"(?![A-Z0-9])";
                if (Regex.IsMatch(
                        line,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildNarrativeCoveredStatRewardRequirementText(
            PartyPredicate predicate,
            PartyFieldKind statField)
        {
            if (predicate == null || !predicate.ImmediateValue.HasValue)
                return null;

            PartyFieldKind predicateField = ResolveNarrativeCoveredStatRewardPredicateField(predicate);
            if (predicateField != statField ||
                !PartyPermanentStatSemantics.IsPermanentStatField(predicateField))
            {
                return null;
            }

            if (!TryFormatPredicateComparisonSymbol(predicate.Comparison, out string comparison))
                return null;

            int value = predicate.ImmediateValue.Value;
            var formula = predicate.ComparedFormula;
            if (formula?.SourceField?.Field == predicateField && formula.Multiplier == 1)
                value -= formula.AdditiveOffset;

            string fieldLabel = PartyPermanentStatSemantics.GetFieldLabel(predicateField);
            return string.IsNullOrWhiteSpace(fieldLabel)
                ? null
                : $"{fieldLabel} {comparison} {value}";
        }

        private static PartyFieldKind ResolveNarrativeCoveredStatRewardPredicateField(
            PartyPredicate predicate)
        {
            if (predicate == null)
                return PartyFieldKind.Unknown;

            if (PartyPermanentStatSemantics.IsPermanentStatField(predicate.Field))
                return predicate.Field;

            var formulaField = predicate.ComparedFormula?.SourceField?.Field ?? PartyFieldKind.Unknown;
            return PartyPermanentStatSemantics.IsPermanentStatField(formulaField)
                ? formulaField
                : PartyFieldKind.Unknown;
        }

        private static bool TryFormatPredicateComparisonSymbol(
            PartyPredicateComparison comparison,
            out string symbol)
        {
            symbol = comparison switch
            {
                PartyPredicateComparison.Equal => "==",
                PartyPredicateComparison.NotEqual => "!=",
                PartyPredicateComparison.LessThan => "<",
                PartyPredicateComparison.LessOrEqual => "<=",
                PartyPredicateComparison.GreaterThan => ">",
                PartyPredicateComparison.GreaterOrEqual => ">=",
                _ => null
            };

            return symbol != null;
        }

        private static bool IsNarrativeCoveredConditionalStatRewardHeaderPredicate(
            PartyPredicate predicate,
            PathVariantInfo variant)
        {
            if (predicate == null ||
                !TryBuildNarrativeCoveredConditionalStatRewardInfo(variant, out var info))
            {
                return false;
            }

            string key = BuildLoopNormalizedPredicateKey(predicate);
            return !string.IsNullOrWhiteSpace(key) &&
                   info.GuardPredicateKeys.Contains(key);
        }

        private static IEnumerable<PartyFieldKind> GetPermanentStatRaiseRelatedFields(
            PathVariantInfo variant)
        {
            foreach (var effect in variant?.PartyEffects ?? new List<PartyEffect>())
            {
                var field = PartyEffectSemantics.GetEffectiveField(effect);
                if (field != PartyFieldKind.Unknown)
                    yield return field;

                foreach (var predicate in PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                {
                    foreach (var predicateField in GetPermanentStatRaiseRelatedFields(predicate))
                        yield return predicateField;
                }
            }

            foreach (var predicate in GetVariantPredicateCandidates(variant))
            {
                foreach (var predicateField in GetPermanentStatRaiseRelatedFields(predicate))
                    yield return predicateField;
            }
        }

        private static IEnumerable<PartyFieldKind> GetPermanentStatRaiseRelatedFields(
            PartyPredicate predicate)
        {
            if (predicate == null)
                yield break;

            if (predicate.Field != PartyFieldKind.Unknown)
                yield return predicate.Field;

            var sourceField = predicate.ComparedFormula?.SourceField?.Field ?? PartyFieldKind.Unknown;
            if (sourceField != PartyFieldKind.Unknown)
                yield return sourceField;
        }

        private static IEnumerable<byte> GetPermanentStatRaiseFlagMasks(PathVariantInfo variant)
        {
            foreach (var effect in variant?.PartyEffects ?? new List<PartyEffect>())
            {
                if (PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.PermanentStatRaiseFlags &&
                    TryGetSinglePermanentStatRaiseMask(effect.ImmediateValue, out byte effectMask))
                {
                    yield return effectMask;
                }

                foreach (var predicate in PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                {
                    if (TryGetSinglePermanentStatRaiseMask(predicate, out byte predicateMask))
                        yield return predicateMask;
                }
            }

            foreach (var predicate in GetVariantPredicateCandidates(variant))
            {
                if (TryGetSinglePermanentStatRaiseMask(predicate, out byte predicateMask))
                    yield return predicateMask;
            }
        }

        private static bool TryGetSinglePermanentStatRaiseMask(PartyPredicate predicate, out byte mask)
        {
            mask = 0;
            if (predicate?.Field != PartyFieldKind.PermanentStatRaiseFlags)
                return false;

            return TryGetSinglePermanentStatRaiseMask(predicate.ImmediateValue, out mask);
        }

        private static bool TryGetSinglePermanentStatRaiseMask(ushort? value, out byte mask)
        {
            mask = 0;
            if (!value.HasValue)
                return false;

            mask = (byte)(value.Value & 0xFF);
            return mask != 0 &&
                   (mask & (mask - 1)) == 0 &&
                   PartyPermanentStatSemantics.TryGetRaiseFlagStatName(mask, out _);
        }

        private static void TryGetPermanentStatRaiseLimit(
            IEnumerable<PathVariantInfo> variants,
            PartyFieldKind statField,
            out string limitExceptionOperator,
            out ushort? limitExceptionValue)
        {
            limitExceptionOperator = null;
            limitExceptionValue = null;

            var limitPredicates = (variants ?? Enumerable.Empty<PathVariantInfo>())
                .SelectMany(GetPermanentStatRaisePredicateCandidates)
                .Where(predicate =>
                    predicate != null &&
                    predicate.Field == statField &&
                    predicate.ImmediateValue.HasValue &&
                    (predicate.Comparison == PartyPredicateComparison.LessThan ||
                     predicate.Comparison == PartyPredicateComparison.LessOrEqual))
                .Select(predicate => new
                {
                    Predicate = predicate,
                    Exception = TryBuildPermanentStatRaiseLimitException(
                        predicate,
                        statField,
                        out string exceptionOperator,
                        out ushort? exceptionValue)
                        ? new { Operator = exceptionOperator, Value = exceptionValue }
                        : null
                })
                .Where(entry => entry.Exception?.Value.HasValue == true)
                .OrderBy(entry => entry.Exception.Value.Value)
                .ToList();
            if (limitPredicates.Count == 0)
                return;

            var selected = limitPredicates[0];
            limitExceptionOperator = selected.Exception.Operator;
            limitExceptionValue = selected.Exception.Value;
        }

        private static bool TryBuildPermanentStatRaiseLimitException(
            PartyPredicate predicate,
            PartyFieldKind statField,
            out string exceptionOperator,
            out ushort? exceptionValue)
        {
            exceptionOperator = null;
            exceptionValue = null;

            if (predicate == null ||
                !predicate.ImmediateValue.HasValue ||
                (predicate.Comparison != PartyPredicateComparison.LessThan &&
                 predicate.Comparison != PartyPredicateComparison.LessOrEqual))
            {
                return false;
            }

            int additiveOffset = 0;
            var formula = predicate.ComparedFormula;
            if (formula != null)
            {
                if (formula.Multiplier != 1)
                    return false;

                PartyFieldKind formulaSourceField =
                    formula.SourceField?.Field ?? PartyFieldKind.Unknown;
                if (formulaSourceField != PartyFieldKind.Unknown &&
                    formulaSourceField != statField)
                {
                    return false;
                }

                additiveOffset = formula.AdditiveOffset;
            }

            int lowerBound = predicate.ImmediateValue.Value - additiveOffset;
            if (predicate.Comparison == PartyPredicateComparison.LessOrEqual)
                lowerBound++;

            if (lowerBound < 0 || lowerBound > ushort.MaxValue)
                return false;

            exceptionOperator = ">=";
            exceptionValue = (ushort)lowerBound;
            return true;
        }

        private static IEnumerable<PartyPredicate> GetPermanentStatRaisePredicateCandidates(
            PathVariantInfo variant)
        {
            foreach (var predicate in GetVariantPredicateCandidates(variant))
                yield return predicate;

            foreach (var effect in variant?.PartyEffects ?? new List<PartyEffect>())
            {
                foreach (var predicate in PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                    yield return predicate;
            }
        }

        private static IEnumerable<PartyPredicate> GetVariantPredicateCandidates(PathVariantInfo variant)
        {
            return variant?.BranchChoices?
                .Select(choice => choice?.GetGuardPredicateForDisplay())
                .Where(predicate => predicate != null) ?? Enumerable.Empty<PartyPredicate>();
        }

        private static bool HasDisqualifyingOutcomeForPermanentStatRaiseCollapse(
            PathVariantInfo variant,
            PartyFieldKind statField)
        {
            if (variant == null)
                return true;

            if (variant.HasTeleportTarget ||
                variant.HasBattleLikeInfo ||
                variant.CallsRandomEncounter ||
                variant.HasMonsterStatChanges ||
                variant.HasAnyTableLoad)
            {
                return true;
            }

            return (variant.PartyEffects ?? new List<PartyEffect>())
                .Any(effect =>
                {
                    var field = PartyEffectSemantics.GetEffectiveField(effect);
                    return field != statField &&
                           field != PartyFieldKind.PermanentStatRaiseFlags;
                });
        }

        private static string BuildDecodedTextKey(IEnumerable<string> rawTexts)
        {
            var lines = DecodeNoteTexts(rawTexts)
                .Select(line => line ?? string.Empty)
                .ToList();

            return string.Join("\n", lines);
        }

        private static bool HasDisqualifyingOutcomeForLoopInternalStatusGuardCollapse(PathVariantInfo variant)
        {
            if (variant == null)
                return true;

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
                   (variant.PartyEffects != null &&
                    variant.PartyEffects.Any(effect => !IsLoopInternalEradicatedStatusGuardedSubsetEffect(effect)));
        }

        private static bool HasDisqualifyingBranchChoiceForLoopInternalStatusGuardCollapse(PathVariantInfo variant)
        {
            return variant?.BranchChoices?.Any(choice =>
                choice != null &&
                !IsPartyLoopTraversalBranchChoice(choice) &&
                !IsLoopInternalEradicatedStatusGuardBranchChoice(choice)) == true;
        }

        private static bool IsLoopInternalEradicatedStatusGuardBranchChoice(BranchChoice choice)
        {
            return IsEradicatedStatusPredicate(choice?.GuardPredicate);
        }

        private static bool IsLoopInternalEradicatedStatusGuardedSubsetEffect(PartyEffect effect)
        {
            return effect != null &&
                   PartyEffectSemantics.IsLoopDerived(effect) &&
                   PartyEffectSemantics.GetEffectiveScope(effect) == PartyEffectScope.PartySubset &&
                   PartyEffectSemantics.GetEffectiveGuardPredicates(effect)
                       .Any(IsEradicatedStatusPredicate);
        }

        private static bool IsEradicatedStatusPredicate(PartyPredicate predicate)
        {
            return predicate != null &&
                   predicate.Field == PartyFieldKind.Status &&
                   (!predicate.FieldOffset.HasValue ||
                    predicate.FieldOffset.Value == PartyStatusSemantics.FieldOffset) &&
                   predicate.ValueKnowledge == PartyValueKnowledge.ExactImmediate &&
                   predicate.ImmediateValue == 0xFF &&
                   predicate.ImmediateRange == null &&
                   (predicate.Comparison == PartyPredicateComparison.Equal ||
                    predicate.Comparison == PartyPredicateComparison.NotEqual);
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
                    lines.Add(BuildNoOpLine(variant));

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
                    normalizedLines.Add(BuildNoOpLine(flatVariant.Variant?.Variant));
                else
                    NormalizeNoOpOnlyLine(normalizedLines, flatVariant.Variant?.Variant);
                RemoveRedundantNoOpLines(normalizedLines);

                int key = BuildFlatSemanticVariantKey(displayIndex++, flatVariant.SourceVariantKey);
                if (ShouldUseFlatDisplayPathVariant(obj, flatVariant.Variant?.Variant))
                {
                    obj.FlatDisplayPathVariants ??= new Dictionary<int, PathVariantInfo>();
                    obj.FlatDisplayPathVariants[key] = flatVariant.Variant.Variant;
                }

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
                NarrativeLines = item.NarrativeLines?.ToList() ?? new List<string>(),
                HeaderAnnotations = item.HeaderAnnotations?.ToList() ?? new List<string>(),
                SupplementalLines = item.SupplementalLines?.ToList() ?? new List<string>(),
                ConditionalComplementOutcomeEffectKeys = item.ConditionalComplementOutcomeEffectKeys != null
                    ? new HashSet<string>(item.ConditionalComplementOutcomeEffectKeys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal)
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

            if (TryFindPathVariantKeyByRenderIdentity(obj, flatVariant?.Variant, out variantKey))
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

        private static bool TryFindPathVariantKeyByRenderIdentity(
            OvrObject obj,
            PathVariantInfo renderVariant,
            out int variantKey)
        {
            variantKey = 0;
            if (obj?.PathVariants == null || renderVariant == null)
                return false;

            foreach (var kvp in obj.PathVariants.OrderBy(kvp => kvp.Key))
            {
                var candidate = kvp.Value;
                if (candidate == null)
                    continue;

                if (candidate.PathId == renderVariant.PathId &&
                    candidate.PathOrderKey == renderVariant.PathOrderKey &&
                    BuildPathVariantTextKey(candidate) == BuildPathVariantTextKey(renderVariant))
                {
                    variantKey = kvp.Key;
                    return true;
                }
            }

            return false;
        }

        private static string BuildPathVariantTextKey(PathVariantInfo variant)
        {
            return string.Join("\n", (variant?.Texts ?? new List<string>())
                .Select(text => text ?? string.Empty));
        }

        private static bool ShouldUseFlatDisplayPathVariant(OvrObject obj, PathVariantInfo renderVariant)
        {
            if (renderVariant == null)
                return false;

            return !IsOriginalPathVariantReference(obj, renderVariant);
        }

        private static bool IsOriginalPathVariantReference(OvrObject obj, PathVariantInfo variant)
        {
            return obj?.PathVariants?.Values
                .Any(candidate => ReferenceEquals(candidate, variant)) == true;
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

            if (obj.FlatDisplayPathVariants != null &&
                obj.FlatDisplayPathVariants.TryGetValue(displayKey, out variant))
            {
                return true;
            }

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

        private static bool TreeContainsPartyScan(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (FindImmediatePartyScanPromptNode(node) != null)
                return true;

            return (node.Children ?? new List<VariantTreeNode>())
                .Any(TreeContainsPartyScan);
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
            var parts = BuildVariantLineParts(
                variantObject,
                rawTexts,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            return ComposeVariantLines(parts, promoteConditionLinesNearPrompt: true);
        }

        private static VariantLineParts BuildVariantLineParts(
            OvrObject variantObject,
            IEnumerable<string> rawTexts,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            var narrativeLines = RemoveAdjacentDuplicatePromptLines(DecodeNoteTexts(rawTexts));
            InsertInlineSpoilerAfterAnswerPrompt(narrativeLines, inlineSpecialSpoilerLine);

            return new VariantLineParts
            {
                VariantContext = GetSinglePathVariantContext(variantObject),
                NarrativeLines = narrativeLines,
                MonsterStatLines = GetMonsterStatLines(
                    variantObject,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance),
                SpecialNoteLines = GetSpecialNoteLines(variantObject),
                PromotableConditionLines = GetPromotableConditionLines(variantObject),
                BattleLines = GetBattleLines(variantObject)
            };
        }

        private static List<string> ComposeVariantLines(
            VariantLineParts parts,
            bool promoteConditionLinesNearPrompt)
        {
            var lines = new List<string>();
            var narrativeLines = parts?.NarrativeLines?.ToList() ?? new List<string>();
            var specialNoteLines = parts?.SpecialNoteLines?.ToList() ?? new List<string>();
            var promotableConditionLines = parts?.PromotableConditionLines?.ToList() ?? new List<string>();

            if (promoteConditionLinesNearPrompt &&
                narrativeLines.Count > 0 &&
                promotableConditionLines.Count > 0)
            {
                lines.Add(narrativeLines[0]);
                lines.AddRange(promotableConditionLines);
                lines.AddRange(narrativeLines.Skip(1));
                specialNoteLines = RemoveLineOccurrences(specialNoteLines, promotableConditionLines);
            }
            else
            {
                lines.AddRange(narrativeLines);
            }

            lines.AddRange(parts?.MonsterStatLines ?? new List<string>());
            lines.AddRange(specialNoteLines);
                lines.AddRange(parts?.BattleLines ?? new List<string>());

            if (lines.Count == 0)
                lines.Add(BuildNoOpLine(parts?.VariantContext));
            else
                NormalizeNoOpOnlyLine(lines, parts?.VariantContext);

            return lines;
        }

        private static List<string> GetPromotableConditionLines(OvrObject obj)
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
            var parts = BuildVariantLineParts(
                variantObject,
                rawTexts,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            return ComposeVariantLines(parts, promoteConditionLinesNearPrompt: false);
        }

        private static string BuildNoOpLine(PathVariantInfo variant)
        {
            return ShouldExplainNoOpAsUnmetConditions(variant)
                ? NoOpBecauseNoConditionsLine
                : NoOpLine;
        }

        private static void NormalizeNoOpOnlyLine(List<string> lines, PathVariantInfo variant)
        {
            if (lines == null || lines.Count == 0)
                return;

            var meaningfulIndices = lines
                .Select((line, index) => new { Line = line, Index = index })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Line))
                .ToList();
            if (meaningfulIndices.Count != 1)
                return;

            var entry = meaningfulIndices[0];
            if (!string.Equals(entry.Line.Trim(), NoOpLine, StringComparison.Ordinal))
                return;

            lines[entry.Index] = BuildNoOpLine(variant);
        }

        private static void RemoveRedundantNoOpLines(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return;

            if (!HasNonNoOpDisplayLine(lines))
                return;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (IsNoOpDisplayLine(lines[i]))
                    lines.RemoveAt(i);
            }
        }

        private static bool IsNoOpDisplayLine(string line)
        {
            string normalized = line?.Trim();
            return string.Equals(normalized, NoOpLine, StringComparison.Ordinal) ||
                   string.Equals(normalized, NoOpBecauseNoConditionsLine, StringComparison.Ordinal);
        }

        private static bool HasNonNoOpDisplayLine(IEnumerable<string> lines)
        {
            return lines?.Any(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !IsNoOpDisplayLine(line)) == true;
        }

        private static bool ShouldExplainNoOpAsUnmetConditions(PathVariantInfo variant)
        {
            if (variant == null)
                return false;

            if (!string.IsNullOrWhiteSpace(BuildProbabilityLine(variant)))
                return false;

            return !ShouldDisplayGuardCondition(variant);
        }

        private static string BuildVariantHeader(OvrObject obj, int variantKey, int displayVariantNumber)
        {
            string header = $"Вариант {displayVariantNumber}";

            if (TryResolvePathVariantForDisplayKey(obj, variantKey, out var pathVariant))
            {
                var annotations = BuildTerminalVariantHeaderAnnotations(obj, pathVariant);
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
                    HeaderContainsVisibleGuardCondition(variantHeader));
        }

        private static bool HeaderContainsVisibleGuardCondition(string variantHeader)
        {
            if (string.IsNullOrWhiteSpace(variantHeader))
                return false;

            if (variantHeader.IndexOf(GuardHeaderAnnotationPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (variantHeader.IndexOf("главный квест игры выполнен", StringComparison.OrdinalIgnoreCase) >= 0 ||
                variantHeader.IndexOf("главный квест игры не выполнен", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return Regex.IsMatch(
                variantHeader,
                @"\([^)]*(?:>=|<=|!=|=|>|<)\s*(?:0x[0-9A-Fa-f]+|\d+)[^)]*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

            bool hasDynamicVariantCountFormula = TryGetSharedCollapsedPartialBattleDynamicDependency(
                battleVariants,
                out var dynamicDependency);

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

            if (!hasDynamicVariantCountFormula &&
                !CanCollapseProbabilityPartitionedPartialBattleSlices(
                    battleVariants,
                    encounterNumerator,
                    encounterDenominator))
            {
                return false;
            }

            string encounterProbability = BuildProbabilityHeaderAnnotation(
                BuildStandaloneProbabilityLine(encounterNumerator, encounterDenominator));
            if (string.IsNullOrWhiteSpace(encounterProbability))
                return false;

            string dynamicVariantCountLine = null;
            if (hasDynamicVariantCountFormula)
            {
                string formulaText = BuildDiceFormulaExpression(dynamicDependency.UpperBoundFormula);
                string dynamicVariantCountText =
                    $"Количество доступных вариантов рассчитывается по формуле: {formulaText}, но не более {battleOptions.Count}";
                dynamicVariantCountLine = InlineNoteStyleCodec.EncodeMutedParentheticalNoteText(
                    $"({dynamicVariantCountText})");
            }

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            int displayVariantNumber = 1;

            sb.AppendLine($"{BuildVariantHeader(obj, noOpEntry.Key, displayVariantNumber++)}:");
            AppendLines(sb, variantContents.TryGetValue(noOpEntry.Key, out var noOpLines)
                ? noOpLines
                : new List<string> { BuildNoOpLine(noOpEntry.Value) });

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
            if (!string.IsNullOrWhiteSpace(dynamicVariantCountLine))
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

        private static bool TryGetSharedCollapsedPartialBattleDynamicDependency(
            List<PathVariantInfo> battleVariants,
            out DynamicRandomBoundDependencyInfo dynamicDependency)
        {
            dynamicDependency = null;

            var dynamicDependencies = (battleVariants ?? new List<PathVariantInfo>())
                .SelectMany(variant => variant?.DynamicRandomBoundDependencies ?? new List<DynamicRandomBoundDependencyInfo>())
                .Where(dependency => dependency?.UpperBoundFormula != null)
                .ToList();

            if (dynamicDependencies.Count == 0)
                return false;

            dynamicDependency = dynamicDependencies[0];
            string dynamicDependencyKey = dynamicDependency.GetIdentityKey();
            if (dynamicDependencies.Any(dependency =>
                !string.Equals(dependency.GetIdentityKey(), dynamicDependencyKey, StringComparison.Ordinal)))
            {
                dynamicDependency = null;
                return false;
            }

            if (!IsLevelPlusOffsetFormula(dynamicDependency.UpperBoundFormula))
            {
                dynamicDependency = null;
                return false;
            }

            return true;
        }

        private static bool CanCollapseProbabilityPartitionedPartialBattleSlices(
            List<PathVariantInfo> battleVariants,
            int encounterNumerator,
            int encounterDenominator)
        {
            if (!CanCollapseContiguousPartialBattleSlices(battleVariants))
                return false;

            if (!TrySumVariantProbabilities(
                    battleVariants,
                    out int battleNumerator,
                    out int battleDenominator))
            {
                return false;
            }

            return AreEqualFractions(
                battleNumerator,
                battleDenominator,
                encounterNumerator,
                encounterDenominator);
        }

        private static bool CanCollapseContiguousPartialBattleSlices(List<PathVariantInfo> battleVariants)
        {
            var partials = (battleVariants ?? new List<PathVariantInfo>())
                .SelectMany(variant => variant?.PartiallyDefinedBattles ?? new List<PartiallyDefinedBattle>())
                .Where(partial => partial != null)
                .ToList();

            if (partials.Count < 2)
                return false;

            int bxIndex = partials[0].BxIndex;
            if (partials.Any(partial => partial.BxIndex != bxIndex))
                return false;

            var monsterIdsWithDuplicates = partials
                .SelectMany(partial => partial.GetPossibleMonsters())
                .Where(monster => monster != null)
                .Select(monster => monster.MonsterId)
                .ToList();

            var monsterIds = monsterIdsWithDuplicates
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (monsterIds.Count < 2)
                return false;

            if (monsterIdsWithDuplicates.Count != monsterIds.Count)
                return false;

            int expected = monsterIds[0];
            foreach (int monsterId in monsterIds)
            {
                if (monsterId != expected)
                    return false;

                expected++;
            }

            return true;
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

        private static bool TrySumVariantProbabilities(
            IEnumerable<PathVariantInfo> variants,
            out int numerator,
            out int denominator)
        {
            numerator = 0;
            denominator = 1;

            foreach (var variant in variants ?? Enumerable.Empty<PathVariantInfo>())
            {
                if (!TryGetVariantProbabilityFraction(
                        variant,
                        out int variantNumerator,
                        out int variantDenominator))
                {
                    return false;
                }

                if (!TryAddProbability(
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

            ReduceFraction(ref numerator, ref denominator);
            return true;
        }

        private static bool TryAddProbability(
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
                (long)leftNumerator * (lcm / leftDenominator) +
                (long)rightNumerator * (lcm / rightDenominator);

            if (adjustedNumerator < int.MinValue || adjustedNumerator > int.MaxValue)
                return false;

            numerator = (int)adjustedNumerator;
            denominator = (int)lcm;
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

        private static bool AreEqualFractions(
            int leftNumerator,
            int leftDenominator,
            int rightNumerator,
            int rightDenominator)
        {
            if (leftDenominator <= 0 || rightDenominator <= 0)
                return false;

            ReduceFraction(ref leftNumerator, ref leftDenominator);
            ReduceFraction(ref rightNumerator, ref rightDenominator);

            return leftNumerator == rightNumerator &&
                   leftDenominator == rightDenominator;
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
            foreach (var part in SplitDisplayLines(line))
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
            if (variant == null || !variant.HasProbabilityInfo || variant.ProbabilityDenominator <= 0)
                return null;

            int numerator = Math.Max(0, variant.ProbabilityNumerator);
            int denominator = Math.Max(1, variant.ProbabilityDenominator);
            if (numerator == 0)
                return null;

            string percentText = ProbabilityFormatter.FormatPercent(numerator, denominator);
            string label = GetDisplayGuardPredicates(variant).Count > 0
                ? "Вероятность при выполнении условий"
                : "Вероятность";

            return $"{label}: {percentText}% ({numerator}/{denominator})";
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

        private static List<PartyPredicate> GetDisplayGuardPredicates(
            PathVariantInfo variant,
            string suppressedGuardKey = null)
        {
            if (!ShouldDisplayGuardCondition(variant))
                return new List<PartyPredicate>();

            var structurallyRenderedPredicateKeys = BuildStructurallyRenderedPredicateKeySet(variant);

            var predicates = variant?.GetGuardPredicates()?
                .Where(predicate => predicate != null)
                .Where(predicate => !PartyEffectSemantics.IsUnrecognizedTechnicalFieldPredicate(predicate))
                .Where(predicate => !IsRedundantUserVisibleAssumptionPredicateForNotes(predicate, variant))
                .Where(predicate => !IsContradictedByUserVisibleAssumptions(predicate, variant))
                .Where(predicate => !IsRedundantAliveStatusPredicateForNotes(predicate))
                .Where(predicate => !IsPermanentStatRaiseHeaderPredicate(predicate, variant))
                .Where(predicate => !IsNarrativeCoveredConditionalStatRewardHeaderPredicate(predicate, variant))
                .Where(predicate =>
                    !structurallyRenderedPredicateKeys.Contains(PartyEffectSemantics.BuildPredicateKey(predicate)))
                .ToList() ?? new List<PartyPredicate>();

            return FilterSuppressedGuardPredicates(predicates, suppressedGuardKey);
        }

        private static List<PartyPredicate> FilterSuppressedGuardPredicates(
            IEnumerable<PartyPredicate> predicates,
            string suppressedGuardKey)
        {
            var list = (predicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .ToList();
            if (list.Count == 0 || string.IsNullOrWhiteSpace(suppressedGuardKey))
                return list;

            var suppressedKeys = SplitGuardConditionKey(suppressedGuardKey)
                .ToHashSet(StringComparer.Ordinal);
            if (suppressedKeys.Count == 0)
                return list;

            return list
                .Where(predicate => !IsSuppressedGuardPredicate(predicate, suppressedKeys))
                .ToList();
        }

        private static bool HasSuppressedDisplayGuardPredicate(
            PathVariantInfo variant,
            string suppressedGuardKey)
        {
            if (string.IsNullOrWhiteSpace(suppressedGuardKey))
                return false;

            var predicates = GetDisplayGuardPredicates(variant);
            if (predicates.Count == 0)
                return false;

            return predicates.Count != FilterSuppressedGuardPredicates(predicates, suppressedGuardKey).Count;
        }

        private static bool IsGuardConditionFullySuppressedByScope(
            PathVariantInfo variant,
            string suppressedGuardKey)
        {
            if (string.IsNullOrWhiteSpace(suppressedGuardKey))
                return false;

            var predicates = GetDisplayGuardPredicates(variant);
            return predicates.Count > 0 &&
                   FilterSuppressedGuardPredicates(predicates, suppressedGuardKey).Count == 0;
        }

        private static bool IsSuppressedGuardPredicate(
            PartyPredicate predicate,
            ISet<string> suppressedKeys)
        {
            if (predicate == null || suppressedKeys == null || suppressedKeys.Count == 0)
                return false;

            string predicateKey = PartyEffectSemantics.BuildPredicateKey(predicate);
            if (!string.IsNullOrWhiteSpace(predicateKey) && suppressedKeys.Contains(predicateKey))
                return true;

            return IsRanalouSelectedQuestCompletionPredicate(predicate) &&
                   suppressedKeys.Any(IsRanalouPartyQuestCompletionGuardKey);
        }

        private static bool IsRanalouSelectedQuestCompletionPredicate(PartyPredicate predicate)
        {
            if (predicate == null ||
                predicate.Field != PartyFieldKind.Technical71 ||
                predicate.Comparison != PartyPredicateComparison.NotEqual ||
                !predicate.ImmediateValue.HasValue ||
                predicate.ImmediateValue.Value != 127)
            {
                return false;
            }

            return predicate.TargetMember == null ||
                   !predicate.TargetMember.IsPartyLoopMember;
        }

        private static bool IsRanalouPartyQuestCompletionGuardKey(string guardKey)
        {
            if (string.IsNullOrWhiteSpace(guardKey))
                return false;

            var parts = guardKey.Split('|');
            if (parts.Length < 7)
                return false;

            return string.Equals(parts[0], PartyFieldKind.Technical71.ToString(), StringComparison.Ordinal) &&
                   string.Equals(parts[3], PartyPredicateLoopQuantifier.None.ToString(), StringComparison.Ordinal) &&
                   string.Equals(parts[4], PartyPredicateComparison.NotEqual.ToString(), StringComparison.Ordinal) &&
                   string.Equals(parts[6], "127", StringComparison.Ordinal);
        }

        private static bool ShouldSuppressByUserVisibleAssumptions(PathVariantInfo variant)
        {
            return variant?.GetGuardPredicates()?
                .Any(predicate => IsContradictedByUserVisibleAssumptions(predicate, variant)) == true;
        }

        private static bool IsSatisfiedByUserVisibleAssumptions(PartyPredicate predicate, PathVariantInfo variant)
        {
            return IsSatisfiedByAssumedFoodBelowCap(predicate) ||
                   (ShouldApplyAssumedAliveStatusModel(variant) &&
                    IsSatisfiedByAssumedStatusBelowDeathThreshold(predicate));
        }

        private static bool IsRedundantUserVisibleAssumptionPredicateForNotes(
            PartyPredicate predicate,
            PathVariantInfo variant)
        {
            return IsSatisfiedByUserVisibleAssumptions(predicate, variant) ||
                   IsByteSentinelNonOverflowPredicate(predicate) ||
                   IsBackpackEmptySentinelPredicate(predicate);
        }

        private static bool IsByteSentinelNonOverflowPredicate(PartyPredicate predicate)
        {
            if (predicate == null ||
                !predicate.ImmediateValue.HasValue ||
                predicate.ImmediateValue.Value != byte.MaxValue)
            {
                return false;
            }

            if (predicate.Comparison != PartyPredicateComparison.NotEqual &&
                predicate.Comparison != PartyPredicateComparison.LessThan &&
                predicate.Comparison != PartyPredicateComparison.LessOrEqual)
            {
                return false;
            }

            return predicate.Field == PartyFieldKind.Food ||
                   PartyTemporaryStatSemantics.IsTrackedField(predicate.Field);
        }

        private static bool IsBackpackEmptySentinelPredicate(PartyPredicate predicate)
        {
            if (predicate == null ||
                !predicate.ImmediateValue.HasValue ||
                predicate.ImmediateValue.Value != byte.MaxValue)
            {
                return false;
            }

            if (predicate.Comparison != PartyPredicateComparison.Equal &&
                predicate.Comparison != PartyPredicateComparison.NotEqual)
            {
                return false;
            }

            if (PartyInventorySemantics.IsBackpackSlotOffset(predicate.FieldOffset))
                return true;

            var range = predicate.FieldOffsetRange;
            return range != null &&
                   range.Min >= PartyInventorySemantics.FirstBackpackSlotOffset &&
                   range.Max <= PartyInventorySemantics.LastBackpackSlotOffset;
        }

        private static bool IsContradictedByUserVisibleAssumptions(PartyPredicate predicate, PathVariantInfo variant)
        {
            return IsContradictedByAssumedFoodBelowCap(predicate) ||
                   (ShouldApplyAssumedAliveStatusModel(variant) &&
                    IsContradictedByAssumedStatusBelowDeathThreshold(predicate));
        }

        private static bool IsRedundantAliveStatusPredicateForNotes(PartyPredicate predicate)
        {
            if (!TryGetImmediateStatusPredicate(predicate, out ushort value))
                return false;

            if (predicate.Comparison == PartyPredicateComparison.LessThan &&
                value == AssumedUserVisibleStatusUpperBoundExclusive)
            {
                return true;
            }

            if (predicate.Comparison == PartyPredicateComparison.LessOrEqual &&
                value + 1 == AssumedUserVisibleStatusUpperBoundExclusive)
            {
                return true;
            }

            if (predicate.LoopQuantifier != PartyPredicateLoopQuantifier.None)
                return false;

            return (predicate.Comparison == PartyPredicateComparison.GreaterOrEqual &&
                    value == AssumedUserVisibleStatusUpperBoundExclusive) ||
                   (predicate.Comparison == PartyPredicateComparison.GreaterThan &&
                    value + 1 == AssumedUserVisibleStatusUpperBoundExclusive);
        }

        private static bool IsSatisfiedByAssumedFoodBelowCap(PartyPredicate predicate)
        {
            if (!TryGetImmediateFoodPredicate(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.NotEqual:
                    return value >= AssumedUserVisibleFoodUpperBoundExclusive;
                case PartyPredicateComparison.LessThan:
                    return value >= AssumedUserVisibleFoodUpperBoundExclusive;
                case PartyPredicateComparison.LessOrEqual:
                    return value + 1 >= AssumedUserVisibleFoodUpperBoundExclusive;
                default:
                    return false;
            }
        }

        private static bool IsContradictedByAssumedFoodBelowCap(PartyPredicate predicate)
        {
            if (!TryGetImmediateFoodPredicate(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.Equal:
                    return value >= AssumedUserVisibleFoodUpperBoundExclusive;
                case PartyPredicateComparison.GreaterThan:
                    return value + 1 >= AssumedUserVisibleFoodUpperBoundExclusive;
                case PartyPredicateComparison.GreaterOrEqual:
                    return value >= AssumedUserVisibleFoodUpperBoundExclusive;
                default:
                    return false;
            }
        }

        private static bool TryGetImmediateFoodPredicate(PartyPredicate predicate, out ushort value)
        {
            value = 0;
            if (predicate == null ||
                predicate.Field != PartyFieldKind.Food ||
                !predicate.ImmediateValue.HasValue)
            {
                return false;
            }

            value = predicate.ImmediateValue.Value;
            return true;
        }

        private static bool IsSatisfiedByAssumedStatusBelowDeathThreshold(PartyPredicate predicate)
        {
            if (!TryGetImmediateStatusPredicate(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.NotEqual:
                    return value >= AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.LessThan:
                    return value >= AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.LessOrEqual:
                    return value + 1 >= AssumedUserVisibleStatusUpperBoundExclusive;
                default:
                    return false;
            }
        }

        private static bool IsContradictedByAssumedStatusBelowDeathThreshold(PartyPredicate predicate)
        {
            if (!TryGetImmediateStatusPredicate(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.Equal:
                    return value >= AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.GreaterThan:
                    return value + 1 >= AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.GreaterOrEqual:
                    return value >= AssumedUserVisibleStatusUpperBoundExclusive;
                default:
                    return false;
            }
        }

        private static bool TryGetImmediateStatusPredicate(PartyPredicate predicate, out ushort value)
        {
            value = 0;
            if (predicate == null || !predicate.ImmediateValue.HasValue)
                return false;

            bool isStatusField =
                predicate.Field == PartyFieldKind.Status &&
                (!predicate.FieldOffset.HasValue ||
                 predicate.FieldOffset.Value == PartyStatusSemantics.FieldOffset);
            if (!isStatusField)
                return false;

            value = predicate.ImmediateValue.Value;
            return true;
        }

        private static bool ShouldApplyAssumedAliveStatusModel(PathVariantInfo variant)
        {
            return VariantTextsContain(variant, "CLIMB TREE (Y/N)?");
        }

        private static HashSet<string> BuildStructurallyRenderedPredicateKeySet(PathVariantInfo variant)
        {
            return GetRelevantBranchChoices(variant)
                .Where(choice => choice?.GuardPredicate != null)
                .Where(choice => PartyInventorySemantics.TryBuildItemPresenceChoiceLabel(choice, out _))
                .Select(choice => PartyEffectSemantics.BuildPredicateKey(choice.GetGuardPredicateForDisplay()))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool ShouldDisplayGuardCondition(PathVariantInfo variant)
        {
            if (variant?.HasStateGuardInfo != true)
                return false;

            return variant.HasProbabilityInfo ||
                   HasPermanentStatRaiseGuardCondition(variant);
        }

        private static bool HasPermanentStatRaiseGuardCondition(PathVariantInfo variant)
        {
            return variant?.GetGuardPredicates()?
                .Any(IsPermanentStatRaiseGuardPredicate) == true;
        }

        private static bool IsPermanentStatRaiseGuardPredicate(PartyPredicate predicate)
        {
            if (predicate == null)
                return false;

            if (predicate.Field == PartyFieldKind.PermanentStatRaiseFlags &&
                predicate.ImmediateValue.HasValue)
            {
                byte mask = (byte)(predicate.ImmediateValue.Value & 0xFF);
                return mask != 0 &&
                       (mask & (mask - 1)) == 0 &&
                       PartyPermanentStatSemantics.TryGetRaiseFlagStatName(mask, out _);
            }

            PartyFieldKind formulaSourceField =
                predicate.ComparedFormula?.SourceField?.Field ?? PartyFieldKind.Unknown;
            return PartyPermanentStatSemantics.IsPermanentStatField(formulaSourceField) ||
                   PartyPermanentStatSemantics.IsPermanentStatField(predicate.Field);
        }

        private static string BuildGuardConditionKey(PathVariantInfo variant)
        {
            return BuildGuardConditionKey(GetDisplayGuardPredicates(variant));
        }

        private static string BuildGuardConditionKey(IEnumerable<PartyPredicate> predicates)
        {
            var keys = (predicates ?? Enumerable.Empty<PartyPredicate>())
                .Where(predicate => predicate != null)
                .Where(predicate => !PartyEffectSemantics.IsUnrecognizedTechnicalFieldPredicate(predicate))
                .Where(predicate => !IsRedundantUserVisibleAssumptionPredicateForNotes(predicate, null))
                .Where(predicate => !IsContradictedByUserVisibleAssumptions(predicate, null))
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
            var predicates = GetDisplayGuardPredicates(variant, suppressedGuardKey);
            string guardKey = BuildGuardConditionKey(predicates);
            if (string.IsNullOrWhiteSpace(guardKey))
                return null;

            return BuildGuardHeaderAnnotation(predicates);
        }

        private static string BuildGuardHeaderAnnotation(IEnumerable<PartyPredicate> predicates)
        {
            string text = PartyEffectSemantics.BuildPredicateListDisplayText(
                (predicates ?? Enumerable.Empty<PartyPredicate>())
                    .Where(predicate => predicate != null)
                    .Where(predicate => !PartyEffectSemantics.IsUnrecognizedTechnicalFieldPredicate(predicate))
                    .Where(predicate => !IsRedundantUserVisibleAssumptionPredicateForNotes(predicate, null))
                    .Where(predicate => !IsContradictedByUserVisibleAssumptions(predicate, null)));
            return string.IsNullOrWhiteSpace(text)
                ? null
                : GuardHeaderAnnotationPrefix + " " + text;
        }

        private static bool IsGuardConditionVisibleInScope(PathVariantInfo variant, string suppressedGuardKey)
        {
            if (string.IsNullOrWhiteSpace(suppressedGuardKey))
                return false;

            return HasSuppressedDisplayGuardPredicate(variant, suppressedGuardKey);
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

            string permanentStatRaise = BuildPermanentStatRaiseHeaderAnnotation(variant);
            if (!string.IsNullOrWhiteSpace(permanentStatRaise))
                annotations.Add(permanentStatRaise);

            string statReward = string.IsNullOrWhiteSpace(permanentStatRaise)
                ? BuildNarrativeCoveredConditionalStatRewardHeaderAnnotation(variant)
                : null;
            if (!string.IsNullOrWhiteSpace(statReward))
                annotations.Add(statReward);

            string probability = BuildProbabilityLine(variant);
            if (!string.IsNullOrEmpty(suppressedProbabilityLine) &&
                string.Equals(probability, suppressedProbabilityLine, StringComparison.Ordinal))
            {
                probability = null;
            }

            bool conditionVisible = !string.IsNullOrWhiteSpace(guard) ||
                                    !string.IsNullOrWhiteSpace(permanentStatRaise) ||
                                    !string.IsNullOrWhiteSpace(statReward) ||
                                    IsGuardConditionVisibleInScope(variant, suppressedGuardKey);
            string probabilityAnnotation = BuildProbabilityHeaderAnnotation(probability, conditionVisible);
            if (!string.IsNullOrWhiteSpace(probabilityAnnotation))
                annotations.Add(probabilityAnnotation);

            return annotations;
        }

        private static List<string> BuildVariantHeaderAnnotations(
            VariantRenderItem item,
            string suppressedProbabilityLine = null,
            string suppressedGuardKey = null)
        {
            var annotations = new List<string>();
            AddDistinctHeaderAnnotations(annotations, item?.HeaderAnnotations);
            string effectiveSuppressedGuardKey = MergeGuardConditionKeys(
                suppressedGuardKey,
                BuildVisibleHeaderAnnotationsGuardKey(annotations));
            AddDistinctHeaderAnnotations(
                annotations,
                BuildVariantHeaderAnnotations(
                    item?.Variant,
                    suppressedProbabilityLine,
                    effectiveSuppressedGuardKey));
            return SuppressSemanticallyRedundantHeaderAnnotations(annotations);
        }

        private static List<string> BuildTerminalVariantHeaderAnnotations(
            OvrObject obj,
            PathVariantInfo variant,
            IEnumerable<string> visiblePathAnnotations = null)
        {
            var annotations = new List<string>();
            AddDistinctHeaderAnnotations(
                annotations,
                visiblePathAnnotations);

            var inventoryAnnotations = BuildInventoryPresenceHeaderAnnotationsForVariantPath(obj, variant)
                .Where(annotation => !HasVisibleInventoryPresenceAnnotationForSameItem(annotations, annotation))
                .Where(annotation => !ContainsHeaderAnnotation(annotations, annotation))
                .ToList();
            AddDistinctHeaderAnnotations(
                annotations,
                inventoryAnnotations);
            string effectiveSuppressedGuardKey = BuildVisibleHeaderAnnotationsGuardKey(annotations);
            AddDistinctHeaderAnnotations(
                annotations,
                BuildVariantHeaderAnnotations(variant, suppressedGuardKey: effectiveSuppressedGuardKey));
            return SuppressSemanticallyRedundantHeaderAnnotations(annotations);
        }

        private static List<string> BuildHierarchicalTerminalVariantHeaderAnnotations(
            OvrObject obj,
            VariantRenderItem item,
            IEnumerable<string> localHeaderAnnotations = null,
            IEnumerable<string> inheritedVisibleHeaderAnnotations = null,
            string suppressedProbabilityLine = null,
            string suppressedGuardKey = null)
        {
            var annotations = new List<string>();
            AddDistinctHeaderAnnotations(
                annotations,
                localHeaderAnnotations);
            AddDistinctHeaderAnnotations(
                annotations,
                item?.HeaderAnnotations);

            var visibleAnnotations = new List<string>();
            AddDistinctHeaderAnnotations(
                visibleAnnotations,
                inheritedVisibleHeaderAnnotations);
            AddDistinctHeaderAnnotations(
                visibleAnnotations,
                annotations);

            var inventoryAnnotations = BuildInventoryPresenceHeaderAnnotationsForVariantPath(obj, item?.Variant)
                .Where(annotation => !HasVisibleInventoryPresenceAnnotationForSameItem(visibleAnnotations, annotation))
                .Where(annotation => !ContainsHeaderAnnotation(visibleAnnotations, annotation))
                .ToList();
            AddDistinctHeaderAnnotations(
                annotations,
                inventoryAnnotations);
            AddDistinctHeaderAnnotations(
                visibleAnnotations,
                inventoryAnnotations);

            string effectiveSuppressedGuardKey = MergeGuardConditionKeys(
                suppressedGuardKey,
                BuildVisibleHeaderAnnotationsGuardKey(visibleAnnotations));
            AddDistinctHeaderAnnotations(
                annotations,
                BuildVariantHeaderAnnotations(
                    item?.Variant,
                    suppressedProbabilityLine,
                    effectiveSuppressedGuardKey));
            return SuppressSemanticallyRedundantHeaderAnnotations(annotations);
        }

        private static string BuildVisibleHeaderAnnotationsGuardKey(IEnumerable<string> annotations)
        {
            var guardKeys = (annotations ?? Enumerable.Empty<string>())
                .Select(TryBuildVisibleHeaderAnnotationGuardKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToArray();

            return MergeGuardConditionKeys(guardKeys);
        }

        private static string TryBuildVisibleHeaderAnnotationGuardKey(string annotation)
        {
            string normalized = StripGuardHeaderAnnotationPrefix(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (string.Equals(normalized, "Все условия квеста RANALOU выполнены", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRanalouPartyQuestCompletionGuardKey(
                    PartyPredicateLoopQuantifier.None,
                    PartyPredicateComparison.NotEqual);
            }

            if (string.Equals(normalized, "Условия квеста RANALOU не выполнены", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRanalouPartyQuestCompletionGuardKey(
                    PartyPredicateLoopQuantifier.Any,
                    PartyPredicateComparison.NotEqual);
            }

            return null;
        }

        private static string BuildRanalouPartyQuestCompletionGuardKey(
            PartyPredicateLoopQuantifier quantifier,
            PartyPredicateComparison comparison)
        {
            return string.Join("|",
                PartyFieldKind.Technical71,
                "-",
                "-",
                quantifier,
                comparison,
                PartyValueKnowledge.ExactImmediate,
                "127",
                "-",
                "-",
                $"{PartyMemberSelectionKind.Dynamic}:Loop:-:-:-");
        }

        private static bool HasVisibleInventoryPresenceAnnotationForSameItem(
            IEnumerable<string> visibleAnnotations,
            string candidate)
        {
            if (!TrySplitInventoryPresenceHeaderAnnotation(
                    candidate,
                    out string candidateItemName,
                    out _,
                    out string candidateSlotLabel))
            {
                return false;
            }

            return (visibleAnnotations ?? Enumerable.Empty<string>())
                .Any(annotation =>
                    TrySplitInventoryPresenceHeaderAnnotation(
                        annotation,
                        out string itemName,
                        out _,
                        out string slotLabel) &&
                    string.Equals(itemName, candidateItemName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(slotLabel ?? string.Empty, candidateSlotLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        private enum HeaderAnnotationSemanticKind
        {
            None = 0,
            RanalouAllQuestConditionsComplete,
            RanalouSelectedQuestProgressNot127
        }

        private static List<string> SuppressSemanticallyRedundantHeaderAnnotations(
            IEnumerable<string> annotations)
        {
            var result = new List<string>();
            foreach (string annotation in annotations ?? Enumerable.Empty<string>())
            {
                foreach (string part in SplitMergedHeaderAnnotation(annotation))
                {
                    string normalized = NormalizeHeaderAnnotation(part);
                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;

                    if (result.Any(existing => HeaderAnnotationSuppresses(existing, normalized)))
                        continue;

                    result.RemoveAll(existing => HeaderAnnotationSuppresses(normalized, existing));

                    if (!result.Contains(normalized, StringComparer.Ordinal))
                        result.Add(normalized);
                }
            }

            return result;
        }

        private static bool HeaderAnnotationSuppresses(string suppressor, string candidate)
        {
            var suppressorKind = GetHeaderAnnotationSemanticKind(suppressor);
            var candidateKind = GetHeaderAnnotationSemanticKind(candidate);

            return suppressorKind == HeaderAnnotationSemanticKind.RanalouAllQuestConditionsComplete &&
                   candidateKind == HeaderAnnotationSemanticKind.RanalouSelectedQuestProgressNot127;
        }

        private static HeaderAnnotationSemanticKind GetHeaderAnnotationSemanticKind(string annotation)
        {
            string normalized = StripGuardHeaderAnnotationPrefix(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return HeaderAnnotationSemanticKind.None;

            if (string.Equals(normalized, "Все условия квеста RANALOU выполнены", StringComparison.OrdinalIgnoreCase))
                return HeaderAnnotationSemanticKind.RanalouAllQuestConditionsComplete;

            if (string.Equals(
                    normalized,
                    "ни у одного персонажа партии поле прогресса линейки квестов волшебника RANALOU (+0x71) != 127",
                    StringComparison.OrdinalIgnoreCase))
            {
                return HeaderAnnotationSemanticKind.RanalouAllQuestConditionsComplete;
            }

            if (string.Equals(
                    normalized,
                    "у выбранного персонажа поле прогресса линейки квестов волшебника RANALOU (+0x71) != 127",
                    StringComparison.OrdinalIgnoreCase))
            {
                return HeaderAnnotationSemanticKind.RanalouSelectedQuestProgressNot127;
            }

            return HeaderAnnotationSemanticKind.None;
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

        private static bool ContainsHeaderAnnotation(IEnumerable<string> annotations, string candidate)
        {
            string normalizedCandidate = NormalizeHeaderAnnotation(candidate);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
                return false;

            return (annotations ?? Enumerable.Empty<string>())
                .Select(NormalizeHeaderAnnotation)
                .Any(annotation => string.Equals(annotation, normalizedCandidate, StringComparison.Ordinal));
        }

        private static string BuildVariantHeaderAnnotationText(IEnumerable<string> annotations)
        {
            var normalizedAnnotations = SuppressSemanticallyRedundantHeaderAnnotations(annotations)
                .Select(NormalizeUserVisibleHeaderAnnotation)
                .Where(annotation => !string.IsNullOrWhiteSpace(annotation))
                .ToList();
            var displayAnnotations = BuildVariantHeaderAnnotationDisplayParts(normalizedAnnotations);

            return displayAnnotations.Count == 0
                ? null
                : string.Join("; ", displayAnnotations);
        }

        private static List<string> BuildVariantHeaderAnnotationDisplayParts(List<string> annotations)
        {
            var result = new List<string>();
            if (annotations == null || annotations.Count == 0)
                return result;

            int imposterDefeatedIndex = annotations.FindIndex(IsEveryPartyMemberUnmaskedAlamarHeaderAnnotation);
            int eyeOfGorosPresentIndex = annotations.FindIndex(IsUnslottedEyeOfGorosPresentHeaderAnnotation);
            bool hasAlamarEyeShortcut = imposterDefeatedIndex >= 0 && eyeOfGorosPresentIndex >= 0;
            int shortcutIndex = hasAlamarEyeShortcut
                ? Math.Min(imposterDefeatedIndex, eyeOfGorosPresentIndex)
                : -1;

            for (int i = 0; i < annotations.Count; i++)
            {
                if (hasAlamarEyeShortcut && i == shortcutIndex)
                {
                    result.Add(
                        $"{EncodeVariantHeaderAnnotationForDisplay(annotations[imposterDefeatedIndex])} или " +
                        $"{EncodeVariantHeaderAnnotationForDisplay(annotations[eyeOfGorosPresentIndex])}");
                }

                if (hasAlamarEyeShortcut && (i == imposterDefeatedIndex || i == eyeOfGorosPresentIndex))
                    continue;

                result.Add(EncodeVariantHeaderAnnotationForDisplay(annotations[i]));
            }

            return result;
        }

        private static bool IsEveryPartyMemberUnmaskedAlamarHeaderAnnotation(string annotation)
        {
            string normalized = StripGuardHeaderAnnotationPrefix(NormalizeHeaderAnnotation(annotation));
            return string.Equals(
                normalized,
                "Аламар уже разоблачён как самозванец каждым персонажем партии",
                StringComparison.Ordinal);
        }

        private static bool IsUnslottedEyeOfGorosPresentHeaderAnnotation(string annotation)
        {
            return TrySplitInventoryPresenceHeaderAnnotation(
                       annotation,
                       out string itemName,
                       out string presenceLabel,
                       out string slotLabel) &&
                   string.Equals(itemName, "EYE OF GOROS", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(presenceLabel, "есть", StringComparison.Ordinal) &&
                   string.IsNullOrWhiteSpace(slotLabel);
        }

        private static string EncodeVariantHeaderAnnotationForDisplay(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            if (TrySplitProtectiveSpellStateHeaderAnnotation(
                    normalized,
                    out string spellName,
                    out string spellStateLabel))
            {
                return $"{InlineNoteStyleCodec.EncodeItemNameText(spellName)} {spellStateLabel}";
            }

            if (TrySplitInventoryPresenceHeaderAnnotation(
                    normalized,
                    out string itemName,
                    out string presenceLabel,
                    out string slotLabel))
            {
                string itemPresenceText = $"{InlineNoteStyleCodec.EncodeItemNameText(itemName)} {presenceLabel}";
                return string.IsNullOrWhiteSpace(slotLabel)
                    ? itemPresenceText
                    : $"{slotLabel}: {itemPresenceText}";
            }

            return normalized;
        }

        private static bool TrySplitProtectiveSpellStateHeaderAnnotation(
            string annotation,
            out string spellName,
            out string stateLabel)
        {
            spellName = null;
            stateLabel = null;

            string normalized = StripGuardHeaderAnnotationPrefix(NormalizeHeaderAnnotation(annotation));
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            foreach (string label in new[] { "активно", "отсутствует" })
            {
                string suffix = " " + label;
                if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                string candidateSpellName = normalized.Substring(0, normalized.Length - suffix.Length).Trim();
                if (!KnownProtectiveSpellConditionNames.Any(name =>
                        string.Equals(name, candidateSpellName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                spellName = candidateSpellName;
                stateLabel = label;
                return true;
            }

            return false;
        }

        private static List<string> BuildInventoryPresenceHeaderAnnotationsForVariantPath(
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

            string variantOutcomeFamilyKey = BuildInventoryComplementOutcomeFamilyKey(variant);

            var candidates = obj.PathVariants.Values
                .Where(pathVariant => pathVariant != null && !ReferenceEquals(pathVariant, variant))
                .SelectMany(pathVariant => GetInventoryPresenceChoiceInfos(pathVariant)
                    .Select(info => new
                    {
                        Info = info,
                        OutcomeFamilyKey = BuildInventoryComplementOutcomeFamilyKey(pathVariant)
                    }))
                .Where(candidate =>
                    string.IsNullOrWhiteSpace(variantOutcomeFamilyKey) ||
                    string.Equals(candidate.OutcomeFamilyKey, variantOutcomeFamilyKey, StringComparison.Ordinal))
                .GroupBy(candidate => BuildInventoryPresenceChoiceKey(candidate.Info), StringComparer.Ordinal)
                .Select(group =>
                {
                    var presenceLabels = group
                        .Select(candidate => candidate.Info.PresenceLabel)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    return new
                    {
                        ItemName = group.First().Info.ItemName,
                        SlotLabel = group.First().Info.SlotLabel,
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

            return new List<string>
            {
                BuildInventoryPresenceHeaderAnnotation(
                    candidates[0].ItemName,
                    oppositePresence,
                    candidates[0].SlotLabel)
            };
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
            public List<string> HeaderAnnotations { get; set; } = new List<string>();
            public List<string> SupplementalLines { get; set; } = new List<string>();
            public HashSet<string> ConditionalComplementOutcomeEffectKeys { get; set; }
                = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class FlatVariantRenderItem
        {
            public PathVariantInfo Variant { get; set; }
            public decimal OrderKey { get; set; } = decimal.MaxValue;
            public List<string> Lines { get; set; } = new List<string>();
            public List<string> HeaderAnnotations { get; set; } = new List<string>();
        }

        private sealed class FlatVariantDisplayGroup
        {
            public FlatVariantRenderItem Item { get; set; }
            public decimal OrderKey { get; set; } = decimal.MaxValue;
            public int SourceIndex { get; set; } = int.MaxValue;
            public List<string> HeaderAnnotations { get; set; } = new List<string>();
            public List<string> NonChoiceHeaderAnnotations { get; set; } = new List<string>();
            public List<string> SimpleChoiceHeaderAnnotations { get; set; } = new List<string>();
            public int SourceVariantCount { get; set; }
        }

        private sealed class VariantTreeNode
        {
            public string SegmentKey { get; set; }
            public string Label { get; set; }
            public string HeaderAnnotation { get; set; }
            public string HeaderGuardKey { get; set; }
            public bool PreserveTransparentWrapper { get; set; }
            public int? RenderPriorityOverride { get; set; }
            public List<string> CommonLines { get; set; } = new List<string>();
            public List<VariantRenderItem> DirectVariants { get; set; } = new List<VariantRenderItem>();
            public List<VariantTreeNode> Children { get; set; } = new List<VariantTreeNode>();
        }

        private sealed class TopLevelVariantGroup
        {
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public VariantTreeNode TreeRoot { get; set; }
            public string Label { get; set; }
            public string HeaderAnnotation { get; set; }
            public string HeaderGuardKey { get; set; }
            public string ConsumedTopChoiceKey { get; set; }
            public bool GroupedByChoice { get; set; }
            public int SourceOrderKey { get; set; } = int.MaxValue;
        }

        private sealed class SemanticVariantRenderAnalysis
        {
            public List<VariantRenderItem> Items { get; set; } = new List<VariantRenderItem>();
            public List<VariantRenderItem> SourceItems { get; set; } = new List<VariantRenderItem>();
            public Dictionary<int, VariantRenderItem> SourceItemsByKey { get; set; }
                = new Dictionary<int, VariantRenderItem>();
            public List<TopLevelVariantGroup> Groups { get; set; } = new List<TopLevelVariantGroup>();
            public bool ContainsPartyScan { get; set; }
            public bool HasPromptBranching { get; set; }
            public bool HasMeaningfulChoiceHierarchy { get; set; }
            public bool HasCommonPrefixHierarchy { get; set; }
        }

        private sealed class SemanticNoteModel
        {
            public SemanticVariantRenderAnalysis Variants { get; set; }
            public InventoryResourceAttritionNoteModel InventoryResourceAttrition { get; set; }
        }

        private sealed class VariantRenderPreparationCache
        {
            public object Sync { get; } = new object();
            public Dictionary<string, VariantRenderPreparation> Entries { get; } =
                new Dictionary<string, VariantRenderPreparation>(StringComparer.Ordinal);
        }

        private sealed class VariantRenderPreparation
        {
            public bool LoopInternalStatusGuardCollapsedLinesComputed { get; set; }
            public List<string> LoopInternalStatusGuardCollapsedLines { get; set; }
            public bool RawVariantContentsComputed { get; set; }
            public Dictionary<int, List<string>> RawVariantContents { get; set; }
            public bool SemanticNoteModelComputed { get; set; }
            public SemanticNoteModel SemanticNoteModel { get; set; }
        }

        private sealed class OrderedRenderEntry
        {
            public int OccurrenceOrderKey { get; set; }
            public int DisplayPriority { get; set; }
            public decimal PathOrderKey { get; set; }
            public int ChoiceOrderKey { get; set; } = int.MaxValue;
            public bool HasChoiceOrderKey { get; set; }
            public VariantTreeNode ChildNode { get; set; }
            public string ChildLabelOverride { get; set; }
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
            public string SlotLabel { get; set; }
            public string PresenceLabel { get; set; }
            public string HeaderAnnotation { get; set; }
        }

        private sealed class InventoryPresenceChoiceShadowInfo
        {
            public string ItemName { get; set; }
            public byte? FieldOffset { get; set; }
            public ValueRange8 FieldOffsetRange { get; set; }
        }

        private sealed class InventoryItemRangeDisplayInfo
        {
            public string Annotation { get; set; }
            public HashSet<string> PredicateKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public int InsertBeforeChoiceIndex { get; set; } = int.MaxValue;
            public byte LowerInclusive { get; set; }
            public byte UpperExclusive { get; set; }
            public string ScopeKey { get; set; }
        }

        private sealed class BranchChoicePredicateInfo
        {
            public BranchChoice Choice { get; set; }
            public PartyPredicate Predicate { get; set; }
            public string PredicateKey { get; set; }
            public int Index { get; set; }
        }

        private sealed class VariantLineParts
        {
            public PathVariantInfo VariantContext { get; set; }
            public List<string> NarrativeLines { get; set; } = new List<string>();
            public List<string> MonsterStatLines { get; set; } = new List<string>();
            public List<string> SpecialNoteLines { get; set; } = new List<string>();
            public List<string> PromotableConditionLines { get; set; } = new List<string>();
            public List<string> BattleLines { get; set; } = new List<string>();
        }

        private enum StateConditionPolarity
        {
            Zero,
            NonZero
        }

        private sealed class StateConditionBranchInfo
        {
            public VariantTreeNode Node { get; set; }
            public ushort Address { get; set; }
            public StateConditionPolarity Polarity { get; set; }
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

        private sealed class PermanentStatRaiseCollapseInfo
        {
            public PartyFieldKind StatField { get; set; }
            public string StatName { get; set; }
            public byte RaiseFlagMask { get; set; }
            public string LimitExceptionOperator { get; set; }
            public ushort? LimitExceptionValue { get; set; }
            public List<string> NarrativeLines { get; set; } = new List<string>();
        }

        private sealed class PermanentStatRaiseVariantInfoCacheEntry
        {
            public bool HasInfo { get; set; }
            public PermanentStatRaiseCollapseInfo Info { get; set; }
        }

        private sealed class NarrativeCoveredConditionalStatRewardInfo
        {
            public PartyFieldKind StatField { get; set; }
            public List<PartyPredicate> GuardPredicates { get; set; } = new List<PartyPredicate>();
            public HashSet<string> GuardPredicateKeys { get; set; } =
                new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> EffectKeys { get; set; } =
                new HashSet<string>(StringComparer.Ordinal);
            public string HeaderAnnotation { get; set; }
        }

        private sealed class NarrativeCoveredConditionalStatRewardInfoCacheEntry
        {
            public bool HasInfo { get; set; }
            public NarrativeCoveredConditionalStatRewardInfo Info { get; set; }
        }

        private sealed class PerMemberAgeAdjustmentCollapseInfo
        {
            public string EffectChoiceLabel { get; set; }
            public string NoEffectChoiceLabel { get; set; }
            public List<string> NarrativeLines { get; set; } = new List<string>();
            public List<PerMemberAgeAdjustmentOutcome> Outcomes { get; set; } =
                new List<PerMemberAgeAdjustmentOutcome>();
        }

        private sealed class PerMemberAgeAdjustmentOutcome
        {
            public PathVariantInfo Variant { get; set; }
            public PartyPredicate Predicate { get; set; }
            public PartyEffect Effect { get; set; }
            public string ChoiceLabel { get; set; }
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

            if (TryGetLoopInternalStatusGuardCollapsedLines(
                    obj,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine,
                    out var collapsedLines))
            {
                return BuildLoopInternalStatusGuardCollapsedNote(collapsedLines);
            }

            if (TryBuildPerMemberAgeAdjustmentCollapsedNote(obj, out string perMemberAgeAdjustmentCollapsedNote))
                return perMemberAgeAdjustmentCollapsedNote;

            if (TryBuildPermanentStatRaiseCollapsedNote(obj, out string permanentStatRaiseCollapsedNote))
                return permanentStatRaiseCollapsedNote;

            var rawVariantContents = GetRawVariantContentsFromPathVariants(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
            if (CanBuildCollapsedCappedRandomPartialBattleNote(obj, rawVariantContents))
                return null;

            var noteModel = GetSemanticNoteModel(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine,
                specialSpoilerLine);
            if (noteModel?.Variants == null)
                return null;

            if (noteModel.InventoryResourceAttrition != null &&
                TryBuildInventoryResourceAttritionRandomOutcomeHierarchy(
                    noteModel.InventoryResourceAttrition,
                    out string resourceAttritionHierarchy))
            {
                return resourceAttritionHierarchy;
            }

            var analysis = noteModel.Variants;
            if (!analysis.HasMeaningfulChoiceHierarchy && !analysis.HasCommonPrefixHierarchy)
                return null;

            bool hasRealMultiplicity =
                analysis.Groups.Count > 1 ||
                analysis.Groups.Any(group =>
                    (group?.TreeRoot?.Children?.Any(IsRenderableStructuralNode) ?? false) ||
                    (CountRenderableDirectVariants(group?.TreeRoot) > 1));

            if (!hasRealMultiplicity)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            for (int i = 0; i < analysis.Groups.Count; i++)
            {
            RenderTopLevelGroup(obj, analysis.Groups[i], sb, i + 1);
                if (i < analysis.Groups.Count - 1)
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

            if (TryGetLoopInternalStatusGuardCollapsedLines(
                    obj,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine,
                    out var collapsedLines))
            {
                return BuildLoopInternalStatusGuardCollapsedNote(collapsedLines);
            }

            if (TryBuildPerMemberAgeAdjustmentCollapsedNote(obj, out string perMemberAgeAdjustmentCollapsedNote))
                return perMemberAgeAdjustmentCollapsedNote;

            if (TryBuildPermanentStatRaiseCollapsedNote(obj, out string permanentStatRaiseCollapsedNote))
                return permanentStatRaiseCollapsedNote;

            var rawVariantContents = GetRawVariantContentsFromPathVariants(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);
            if (CanBuildCollapsedCappedRandomPartialBattleNote(obj, rawVariantContents))
                return null;

            var noteModel = GetSemanticNoteModel(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine,
                specialSpoilerLine);
            if (noteModel?.Variants == null)
                return null;

            if (noteModel.InventoryResourceAttrition != null)
            {
                return BuildInventoryResourceAttritionFlatNotes(noteModel.InventoryResourceAttrition);
            }

            var analysis = noteModel.Variants;
            if (!analysis.ContainsPartyScan &&
                !analysis.HasPromptBranching &&
                !analysis.HasMeaningfulChoiceHierarchy &&
                !analysis.HasCommonPrefixHierarchy)
            {
                return null;
            }

            return BuildFlatSemanticVariantNotesFromAnalysis(obj, analysis);
        }

        private static SemanticNoteModel BuildSemanticNoteModel(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            string specialSpoilerLine = null)
        {
            var analysis = BuildSemanticVariantRenderAnalysis(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine,
                specialSpoilerLine);
            if (analysis == null)
                return null;

            TryBuildInventoryResourceAttritionRandomOutcomeModel(
                analysis.SourceItems ?? analysis.Items,
                out var resourceAttritionModel);

            return new SemanticNoteModel
            {
                Variants = analysis,
                InventoryResourceAttrition = resourceAttritionModel
            };
        }

        private static SemanticVariantRenderAnalysis BuildSemanticVariantRenderAnalysis(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            string specialSpoilerLine = null)
        {
            if (obj?.PathVariants == null || obj.PathVariants.Count <= 1)
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

            var sourceItems = items
                .Select(CloneVariantRenderItemForSourceMatch)
                .Where(item => item != null)
                .ToList();

            var groups = BuildTopLevelVariantGroups(obj, items);
            if (groups.Count == 0)
                return null;

            return new SemanticVariantRenderAnalysis
            {
                Items = items,
                SourceItems = sourceItems,
                SourceItemsByKey = BuildSourceVariantItemMap(obj, sourceItems),
                Groups = groups,
                ContainsPartyScan = groups.Any(group => TreeContainsPartyScan(group?.TreeRoot)),
                HasPromptBranching = groups.Any(group => TreeContainsPromptBranching(group?.TreeRoot)),
                HasMeaningfulChoiceHierarchy = items.Any(item => GetRelevantBranchChoices(item?.Variant).Any()),
                HasCommonPrefixHierarchy = groups.Any(group =>
                    (group?.Items?.Count ?? 0) > 1 &&
                    (group.TreeRoot?.CommonLines?.Any(line => !string.IsNullOrWhiteSpace(line)) ?? false))
            };
        }

        private static string BuildFlatSemanticVariantNotesFromAnalysis(
            OvrObject obj,
            SemanticVariantRenderAnalysis analysis)
        {
            if (analysis?.Groups == null || analysis.Groups.Count == 0)
                return null;

            var flatVariants = BuildFlatTerminalVariantsFromAnalysis(analysis)
                .Where(variant => variant != null)
                .ToList();
            flatVariants = NormalizeFlatTerminalVariantsForDisplay(obj, flatVariants);

            if (flatVariants.Count <= 1)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");

            for (int i = 0; i < flatVariants.Count; i++)
            {
                var flatVariant = flatVariants[i];
                var annotations = BuildTerminalVariantHeaderAnnotations(
                    obj,
                    flatVariant.Variant,
                    flatVariant.HeaderAnnotations);

                string header = $"Вариант {i + 1}";
                string annotationText = BuildVariantHeaderAnnotationText(annotations);
                if (!string.IsNullOrWhiteSpace(annotationText))
                    header += $" ({annotationText})";
                header += ":";

                sb.AppendLine(header);

                var lines = flatVariant.Lines?
                    .Where(line => line != null)
                    .ToList() ?? new List<string>();
                if (!lines.Any(line => !string.IsNullOrWhiteSpace(line)))
                    lines.Add(BuildNoOpLine(flatVariant.Variant));
                else
                    NormalizeNoOpOnlyLine(lines, flatVariant.Variant);
                RemoveRedundantNoOpLines(lines);

                foreach (var line in lines)
                {
                    foreach (var part in SplitDisplayLines(line))
                        sb.AppendLine(part);
                }

                if (i < flatVariants.Count - 1)
                    sb.AppendLine();
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static bool TryGetLoopInternalStatusGuardCollapsedLines(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            out List<string> lines)
        {
            lines = null;
            if (obj == null)
                return false;

            var cache = VariantRenderPreparationCaches.GetValue(
                obj,
                _ => new VariantRenderPreparationCache());
            string cacheKey = BuildVariantRenderPreparationCacheKey(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            lock (cache.Sync)
            {
                var preparation = GetOrCreateVariantRenderPreparation(cache, cacheKey);
                if (!preparation.LoopInternalStatusGuardCollapsedLinesComputed)
                {
                    preparation.LoopInternalStatusGuardCollapsedLinesComputed = true;
                    preparation.LoopInternalStatusGuardCollapsedLines =
                        TryBuildLoopInternalStatusGuardCollapsedLines(
                            obj,
                            defaultRandomEncounterMonsterPowerCap,
                            defaultRandomEncounterMonsterLevelCap,
                            defaultRandomEncounterMonsterBatchCountCap,
                            defaultDarkeningLevel,
                            defaultRandomEncounterChance,
                            inlineSpecialSpoilerLine,
                            out var computedLines)
                            ? computedLines?.ToList()
                            : null;
                }

                if (preparation.LoopInternalStatusGuardCollapsedLines == null)
                    return false;

                lines = preparation.LoopInternalStatusGuardCollapsedLines.ToList();
                return true;
            }
        }

        private static Dictionary<int, List<string>> GetRawVariantContentsFromPathVariants(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine)
        {
            if (obj == null)
                return new Dictionary<int, List<string>>();

            var cache = VariantRenderPreparationCaches.GetValue(
                obj,
                _ => new VariantRenderPreparationCache());
            string cacheKey = BuildVariantRenderPreparationCacheKey(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine);

            lock (cache.Sync)
            {
                var preparation = GetOrCreateVariantRenderPreparation(cache, cacheKey);
                if (!preparation.RawVariantContentsComputed)
                {
                    preparation.RawVariantContentsComputed = true;
                    preparation.RawVariantContents = CloneVariantContents(
                        BuildRawVariantContentsFromPathVariants(
                            obj,
                            defaultRandomEncounterMonsterPowerCap,
                            defaultRandomEncounterMonsterLevelCap,
                            defaultRandomEncounterMonsterBatchCountCap,
                            defaultDarkeningLevel,
                            defaultRandomEncounterChance,
                            inlineSpecialSpoilerLine));
                }

                return CloneVariantContents(preparation.RawVariantContents);
            }
        }

        private static SemanticNoteModel GetSemanticNoteModel(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            string specialSpoilerLine = null)
        {
            if (obj == null)
                return null;

            var cache = VariantRenderPreparationCaches.GetValue(
                obj,
                _ => new VariantRenderPreparationCache());
            string cacheKey = BuildVariantRenderPreparationCacheKey(
                obj,
                defaultRandomEncounterMonsterPowerCap,
                defaultRandomEncounterMonsterLevelCap,
                defaultRandomEncounterMonsterBatchCountCap,
                defaultDarkeningLevel,
                defaultRandomEncounterChance,
                inlineSpecialSpoilerLine,
                specialSpoilerLine);

            lock (cache.Sync)
            {
                var preparation = GetOrCreateVariantRenderPreparation(cache, cacheKey);
                if (!preparation.SemanticNoteModelComputed)
                {
                    preparation.SemanticNoteModelComputed = true;
                    preparation.SemanticNoteModel = BuildSemanticNoteModel(
                        obj,
                        defaultRandomEncounterMonsterPowerCap,
                        defaultRandomEncounterMonsterLevelCap,
                        defaultRandomEncounterMonsterBatchCountCap,
                        defaultDarkeningLevel,
                        defaultRandomEncounterChance,
                        inlineSpecialSpoilerLine,
                        specialSpoilerLine);
                }

                return preparation.SemanticNoteModel;
            }
        }

        private static VariantRenderPreparation GetOrCreateVariantRenderPreparation(
            VariantRenderPreparationCache cache,
            string cacheKey)
        {
            if (!cache.Entries.TryGetValue(cacheKey, out var preparation))
            {
                preparation = new VariantRenderPreparation();
                cache.Entries[cacheKey] = preparation;
            }

            return preparation;
        }

        private static string BuildVariantRenderPreparationCacheKey(
            OvrObject obj,
            byte defaultRandomEncounterMonsterPowerCap,
            byte defaultRandomEncounterMonsterLevelCap,
            byte defaultRandomEncounterMonsterBatchCountCap,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance,
            string inlineSpecialSpoilerLine,
            string specialSpoilerLine = null)
        {
            return string.Join(
                "\u001F",
                obj?.PathVariants?.Count.ToString(CultureInfo.InvariantCulture) ?? "0",
                defaultRandomEncounterMonsterPowerCap.ToString(CultureInfo.InvariantCulture),
                defaultRandomEncounterMonsterLevelCap.ToString(CultureInfo.InvariantCulture),
                defaultRandomEncounterMonsterBatchCountCap.ToString(CultureInfo.InvariantCulture),
                defaultDarkeningLevel.ToString(CultureInfo.InvariantCulture),
                defaultRandomEncounterChance.ToString(CultureInfo.InvariantCulture),
                inlineSpecialSpoilerLine ?? string.Empty,
                specialSpoilerLine ?? string.Empty);
        }

        private static Dictionary<int, List<string>> CloneVariantContents(
            Dictionary<int, List<string>> source)
        {
            var result = new Dictionary<int, List<string>>();
            foreach (var kvp in source ?? new Dictionary<int, List<string>>())
                result[kvp.Key] = kvp.Value?.ToList() ?? new List<string>();

            return result;
        }

        private static List<FlatVariantRenderItem> BuildFlatTerminalVariantsFromAnalysis(
            SemanticVariantRenderAnalysis analysis)
        {
            var result = new List<FlatVariantRenderItem>();
            foreach (var group in analysis?.Groups ?? new List<TopLevelVariantGroup>())
                result.AddRange(BuildFlatTerminalVariantsFromTopLevelGroup(group));

            return result;
        }

        private static List<FlatVariantRenderItem> NormalizeFlatTerminalVariantsForDisplay(
            OvrObject obj,
            List<FlatVariantRenderItem> variants)
        {
            var groups = new List<FlatVariantDisplayGroup>();
            var groupsByKey = new Dictionary<string, FlatVariantDisplayGroup>(StringComparer.Ordinal);

            int sourceIndex = 0;
            foreach (var variant in variants ?? new List<FlatVariantRenderItem>())
            {
                int currentSourceIndex = sourceIndex++;
                if (variant == null)
                    continue;

                var lines = (variant.Lines ?? new List<string>())
                    .Select(line => line?.TrimEnd() ?? string.Empty)
                    .ToList();
                decimal orderKey = GetFlatVariantRenderOrderKey(variant);

                var annotations = new List<string>();
                AddDistinctHeaderAnnotations(
                    annotations,
                    BuildTerminalVariantHeaderAnnotations(
                        obj,
                        variant.Variant,
                        variant.HeaderAnnotations));

                var nonChoiceAnnotations = annotations
                    .Where(annotation => !IsSimpleFlatChoiceHeaderAnnotation(annotation))
                    .ToList();
                string key = string.Join(
                    "\n---\n",
                    string.Join("|", nonChoiceAnnotations),
                    string.Join("\n", lines));

                if (!groupsByKey.TryGetValue(key, out var group))
                {
                    group = new FlatVariantDisplayGroup
                    {
                        Item = new FlatVariantRenderItem
                        {
                            Variant = variant.Variant,
                            OrderKey = orderKey,
                            Lines = lines,
                            HeaderAnnotations = annotations.ToList()
                        },
                        OrderKey = orderKey,
                        SourceIndex = currentSourceIndex,
                        HeaderAnnotations = annotations.ToList(),
                        SourceVariantCount = 1
                    };
                    AddDistinctHeaderAnnotations(group.NonChoiceHeaderAnnotations, nonChoiceAnnotations);
                    AddDistinctHeaderAnnotations(
                        group.SimpleChoiceHeaderAnnotations,
                        annotations.Where(IsSimpleFlatChoiceHeaderAnnotation));

                    groupsByKey[key] = group;
                    groups.Add(group);
                    continue;
                }

                if (orderKey < group.OrderKey)
                {
                    group.OrderKey = orderKey;
                    group.Item.OrderKey = orderKey;
                    group.Item.Variant = variant.Variant;
                }

                if (currentSourceIndex < group.SourceIndex)
                    group.SourceIndex = currentSourceIndex;

                group.SourceVariantCount++;
                AddDistinctHeaderAnnotations(group.HeaderAnnotations, annotations);
                AddDistinctHeaderAnnotations(group.NonChoiceHeaderAnnotations, nonChoiceAnnotations);
                AddDistinctHeaderAnnotations(
                    group.SimpleChoiceHeaderAnnotations,
                    annotations.Where(IsSimpleFlatChoiceHeaderAnnotation));
            }

            bool suppressSimpleChoiceAnnotations = false;
            bool suppressRepeatedSimpleChoiceAnnotations =
                ShouldSuppressRepeatedSimpleChoiceAnnotations(groups);

            foreach (var group in groups)
            {
                bool suppressGroupSimpleChoiceAnnotations =
                    suppressRepeatedSimpleChoiceAnnotations ||
                    group.SourceVariantCount > 1 &&
                    group.SimpleChoiceHeaderAnnotations.Count > 1;
                suppressSimpleChoiceAnnotations |= suppressGroupSimpleChoiceAnnotations;

                group.Item.HeaderAnnotations = suppressGroupSimpleChoiceAnnotations
                    ? group.HeaderAnnotations
                        .Where(annotation => !IsSimpleFlatChoiceHeaderAnnotation(annotation))
                        .ToList()
                    : group.HeaderAnnotations.ToList();
                group.Item.OrderKey = group.OrderKey;
            }

            bool sortSuppressedSimpleChoiceGroups =
                suppressSimpleChoiceAnnotations &&
                ShouldSortSuppressedSimpleChoiceGroups(groups);

            var orderedGroups = sortSuppressedSimpleChoiceGroups
                ? groups
                    .Select((group, index) => new { group, index })
                    .OrderBy(item => item.group.OrderKey)
                    .ThenBy(item => item.group.SourceIndex)
                    .ThenBy(item => item.index)
                    .Select(item => item.group)
                    .ToList()
                : groups;

            return orderedGroups
                .Select(group => group.Item)
                .Where(item => item != null)
                .ToList();
        }

        private static decimal GetFlatVariantRenderOrderKey(FlatVariantRenderItem variant)
        {
            if (variant == null)
                return decimal.MaxValue;

            return variant.OrderKey < decimal.MaxValue
                ? variant.OrderKey
                : GetPathOrderKey(variant.Variant);
        }

        private static bool ShouldSortSuppressedSimpleChoiceGroups(
            List<FlatVariantDisplayGroup> groups)
        {
            if (groups == null || groups.Count == 0)
                return false;

            if (groups.Any(group =>
                group?.NonChoiceHeaderAnnotations?.Count > 0 ||
                group?.Item?.Lines?.Any(line =>
                    string.Equals(
                        line?.Trim(),
                        "Ничего не происходит (не выполнены условия для наступления ни одного варианта)",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        line?.Trim(),
                        "Ничего не происходит",
                        StringComparison.Ordinal)) == true) == true)
            {
                return true;
            }

            return groups
                .Select(group => group?.Item?.Lines?
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?
                    .Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;
        }

        private static bool ShouldSuppressRepeatedSimpleChoiceAnnotations(
            List<FlatVariantDisplayGroup> groups)
        {
            var source = groups?
                .Where(group => group != null)
                .ToList() ?? new List<FlatVariantDisplayGroup>();
            if (source.Count == 0)
                return false;

            var repeatedSimpleChoiceAnnotations = source
                .SelectMany(group => group.SimpleChoiceHeaderAnnotations ?? new List<string>())
                .GroupBy(annotation => annotation, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            return repeatedSimpleChoiceAnnotations.Count > 0 &&
                   repeatedSimpleChoiceAnnotations.All(IsBinaryFlatChoiceHeaderAnnotation) &&
                   (HasSingleCompactBinaryPromptGroup(source) ||
                    HasSexWriteLoopSubsetOutcomeGroup(source));
        }

        private static bool IsSimpleFlatChoiceHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"^выбор\s+(?:ESC|[A-Za-z0-9])$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsBinaryFlatChoiceHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            return string.Equals(normalized, "выбор Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "выбор N", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasSingleCompactBinaryPromptGroup(List<FlatVariantDisplayGroup> groups)
        {
            var promptGroupCounts = groups?
                .Select(group => BuildBinaryPromptKey(group?.Item?.Lines))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .GroupBy(key => key, StringComparer.Ordinal)
                .Select(group => group.Count())
                .ToList() ?? new List<int>();

            return promptGroupCounts.Count == 1 &&
                   promptGroupCounts[0] <= 2;
        }

        private static bool HasSexWriteLoopSubsetOutcomeGroup(List<FlatVariantDisplayGroup> groups)
        {
            return groups?.Any(group =>
                group?.Item?.Variant?.PartyEffects?.Any(IsSexWriteLoopSubsetOutcomeEffect) == true) == true;
        }

        private static string BuildBinaryPromptKey(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return null;

            var promptLines = new List<string>();
            foreach (string line in lines)
            {
                string normalized = line?.TrimEnd() ?? string.Empty;
                if (promptLines.Count == 0 && string.IsNullOrWhiteSpace(normalized))
                    continue;

                promptLines.Add(normalized);
                if (IsBinaryPromptLine(normalized))
                    return string.Join("\n", promptLines).Trim();
            }

            return null;
        }

        private static bool IsBinaryPromptLine(string line)
        {
            return !string.IsNullOrWhiteSpace(line) &&
                   Regex.IsMatch(
                       line,
                       @"\(\s*Y\s*/\s*N\s*\)\s*\?",
                       RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static List<FlatVariantRenderItem> BuildFlatTerminalVariantsFromTopLevelGroup(
            TopLevelVariantGroup group)
        {
            var result = new List<FlatVariantRenderItem>();
            if (group?.TreeRoot == null)
                return result;

            var renderableChildren = (group.TreeRoot.Children ?? Enumerable.Empty<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            int siblingDirectVariantCount = group.TreeRoot.DirectVariants?.Count(v => v != null) ?? 0;
            bool suppressEmptyPromptTerminalDirectVariants =
                ShouldSuppressEmptyPromptTerminalDirectVariants(group.TreeRoot, renderableChildren.Count);

            var directVariants = (group.TreeRoot.DirectVariants ?? Enumerable.Empty<VariantRenderItem>())
                .Where(v => ShouldRenderDirectVariant(
                        v,
                        siblingDirectVariantCount,
                        renderableChildren.Count,
                        suppressEmptyPromptTerminalDirectVariants) &&
                    !ShouldSuppressAsRedundantTopLevelNoOpLeaf(group, v))
                .ToList();

            var prefix = group.TreeRoot.CommonLines?.ToList() ?? new List<string>();
            var pathAnnotations = new List<string>();
            AddFlatPathLabel(pathAnnotations, prefix, group.Label);
            AddFlatPathHeaderAnnotations(pathAnnotations, null, group.HeaderAnnotation);

            if (IsPureTopLevelNoOpGroup(group))
            {
                result.Add(CreateFlatTerminalVariant(
                    GetSingleLeafVariantItem(group.TreeRoot),
                    prefix,
                    pathAnnotations,
                    includeItemLines: false));
                return result;
            }

            if (renderableChildren.Count == 0 && directVariants.Count == 0)
            {
                if (prefix.Any(line => !string.IsNullOrWhiteSpace(line)) || pathAnnotations.Count > 0)
                    result.Add(CreateFlatTerminalVariant(
                        GetSingleLeafVariantItem(group.TreeRoot),
                        prefix,
                        pathAnnotations));

                return result;
            }

            var singleLeafItem = renderableChildren.Count == 0
                ? GetSingleLeafVariantItem(group.TreeRoot)
                : null;
            if (directVariants.Count == 1 && renderableChildren.Count == 0)
            {
                result.Add(CreateFlatTerminalVariant(
                    PreferItemWithVariant(singleLeafItem, directVariants[0]),
                    prefix,
                    pathAnnotations));
                return result;
            }

            bool canPromoteNoToChoice =
                renderableChildren.Count > 0 &&
                PromptContainsChoiceLabel(group.TreeRoot.CommonLines, "N)") &&
                !HasNodeWithLabel(group.TreeRoot, "N)");
            int noChoiceCount = directVariants.Count(v => ShouldRenderAsNoChoiceVariant(v));
            var syntheticChoiceLabels = BuildSyntheticChoiceLabels(group.TreeRoot, directVariants, renderableChildren);
            var syntheticChildLabels = BuildSyntheticChildLabels(group.TreeRoot, renderableChildren, directVariants, syntheticChoiceLabels);

            foreach (var entry in BuildOrderedRenderEntries(
                group.TreeRoot,
                renderableChildren,
                directVariants,
                syntheticChoiceLabels,
                syntheticChildLabels))
            {
                if (entry.ChildNode != null)
                {
                    result.AddRange(BuildFlatTerminalVariantsFromTreeNode(
                        entry.ChildNode,
                        prefix,
                        pathAnnotations,
                        BuildSiblingNoOpLeafKeysForRenderEntry(
                            entry,
                            renderableChildren,
                            directVariants),
                        entry.ChildLabelOverride));
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    result.Add(CreateFlatChoiceTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations,
                        syntheticLabel));
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    result.Add(CreateFlatChoiceTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations,
                        "N)"));
                }
                else
                {
                    result.Add(CreateFlatLooseTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations));
                }
            }

            return result;
        }

        private static void ApplyAreaD4TechnicalRenderPatchFallback(
            string fileNameOnly,
            OvrFileConfig config,
            OvrNotesBuildResult result,
            ISet<Point> cellsToBuild,
            bool buildInlineStyleSpans)
        {
            if (!string.Equals(fileNameOnly, "AREAD4.OVR", StringComparison.OrdinalIgnoreCase) ||
                config?.First16Lines == null ||
                result == null)
            {
                return;
            }

            string technicalLine = BuildTechnicalRenderPatchLine(new[]
            {
                RenderTemplateDefaultByteEntry,
                RenderTemplateCallerByteEntry
            });

            for (int y = 0; y < Math.Min(16, config.First16Lines.Length); y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    if (!TryReadFirstLayerCell(config, x, y, out byte firstLayerCell) ||
                        firstLayerCell != 0xAA)
                    {
                        continue;
                    }

                    var pos = new Point(x, y);
                    if (cellsToBuild != null && !cellsToBuild.Contains(pos))
                        continue;

                    string existingNote = result.NotesPerCell.TryGetValue(pos, out string note)
                        ? note
                        : string.Empty;

                    if (result.CentralOptions.TryGetValue(pos, out string centralOption))
                    {
                        if (centralOption == "AnyObject" ||
                            (centralOption == "AnyObjectSpec" && !IsEmptyOrNoOpNote(existingNote)))
                        {
                            continue;
                        }
                    }

                    result.CentralOptions[pos] = TechObjectCentralOption;
                    if (!string.IsNullOrWhiteSpace(existingNote) &&
                        existingNote.Contains("Техническая клетка без игрового эффекта", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var newNotes = new StringBuilder();
                    var inlineStyles = buildInlineStyleSpans
                        ? new List<NoteInlineStyleSpan>()
                        : null;

                    if (!IsEmptyOrNoOpNote(existingNote))
                    {
                        var existingStyles = buildInlineStyleSpans &&
                            result.NoteStyleSpansPerCell.TryGetValue(pos, out var styles)
                            ? styles
                            : null;

                        AppendExistingRenderedText(newNotes, inlineStyles, existingNote, existingStyles);
                        AppendRenderedText(newNotes, inlineStyles, "\n\n");
                    }

                    AppendRenderedText(newNotes, inlineStyles, technicalLine);
                    result.NotesPerCell[pos] = NormalizeNoteLineEndings(newNotes.ToString().TrimEnd('\r', '\n'));

                    if (buildInlineStyleSpans)
                    {
                        result.NoteStyleSpansPerCell[pos] = inlineStyles
                            .Where(span => span != null && span.Length > 0 && span.Start >= 0)
                            .Select(span => span.Clone())
                            .ToList();
                    }
                }
            }
        }

        private static bool IsEmptyOrNoOpNote(string note)
        {
            string normalized = NormalizeNoteLineEndings(note ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ||
                   string.Equals(normalized, NoOpLine, StringComparison.Ordinal) ||
                   string.Equals(normalized, NoOpBecauseNoConditionsLine, StringComparison.Ordinal);
        }

        private static List<FlatVariantRenderItem> BuildFlatTerminalVariantsFromTreeNode(
            VariantTreeNode node,
            List<string> inheritedLines,
            List<string> inheritedHeaderAnnotations,
            IReadOnlySet<string> siblingNoOpLeafKeys = null,
            string nodeLabelOverride = null)
        {
            var result = new List<FlatVariantRenderItem>();
            if (!IsRenderableStructuralNode(node))
                return result;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            renderableChildren = SuppressNoOpChildrenInTransparentRenderNode(
                node,
                renderableChildren,
                siblingNoOpLeafKeys,
                nodeLabelOverride);

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
            renderableDirectVariants = SuppressInheritedDuplicateNoOpDirectVariants(
                node,
                renderableChildren,
                renderableDirectVariants,
                siblingNoOpLeafKeys);

            if (renderableChildren.Count == 1 &&
                renderableDirectVariants.Count == 0 &&
                IsTransparentRenderNode(node, nodeLabelOverride))
            {
                return BuildFlatTerminalVariantsFromTreeNode(
                    renderableChildren[0],
                    inheritedLines,
                    inheritedHeaderAnnotations,
                    siblingNoOpLeafKeys);
            }

            var nodeLines = node.CommonLines?.ToList() ?? new List<string>();
            var singleLeafItem = renderableChildren.Count == 0
                ? GetSingleLeafVariantItem(node)
                : null;
            var singleLeafLines = BuildVariantRenderLines(singleLeafItem);
            string nodeLabel = NormalizeUserVisibleChoiceLabel(GetEffectiveNodeLabel(node, nodeLabelOverride));
            string promotedCommonLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(nodeLines)
                : null;
            string promotedSingleLeafLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(singleLeafLines)
                : null;

            var prefix = inheritedLines?.ToList() ?? new List<string>();
            prefix.AddRange(nodeLines);

            var pathAnnotations = inheritedHeaderAnnotations?.ToList() ?? new List<string>();
            AddFlatPathLabel(
                pathAnnotations,
                prefix,
                nodeLabel ?? promotedCommonLabel ?? promotedSingleLeafLabel);
            AddFlatPathHeaderAnnotations(pathAnnotations, null, node.HeaderAnnotation);

            if (TryBuildFlatPartyScanOutcomeLines(node, prefix, out var partyScanLines))
            {
                foreach (var flatVariant in partyScanLines)
                {
                    var annotations = pathAnnotations.ToList();
                    AddDistinctHeaderAnnotations(annotations, flatVariant.HeaderAnnotations);
                    flatVariant.HeaderAnnotations = annotations;
                    result.Add(flatVariant);
                }

                return result;
            }

            if (renderableChildren.Count == 0 && renderableDirectVariants.Count == 0)
            {
                if (prefix.Any(line => !string.IsNullOrWhiteSpace(line)) || pathAnnotations.Count > 0)
                    result.Add(CreateFlatTerminalVariant(singleLeafItem, prefix, pathAnnotations, includeItemLines: false));

                return result;
            }

            if (renderableDirectVariants.Count == 1 && renderableChildren.Count == 0)
            {
                var leafItem = PreferItemWithVariant(singleLeafItem, renderableDirectVariants[0]);
                var leaf = CreateFlatTerminalVariant(
                    leafItem,
                    prefix,
                    pathAnnotations);
                if (singleLeafItem != null && singleLeafLines != null)
                    leaf.Lines = prefix.Concat(singleLeafLines).ToList();

                result.Add(leaf);
                return result;
            }

            bool canPromoteNoToChoice =
                renderableChildren.Count > 0 &&
                PromptContainsChoiceLabel(node.CommonLines, "N)") &&
                !HasNodeWithLabel(node, "N)");
            int noChoiceCount = renderableDirectVariants.Count(v => ShouldRenderAsNoChoiceVariant(v));
            var syntheticChoiceLabels = BuildSyntheticChoiceLabels(node, renderableDirectVariants, renderableChildren);
            var syntheticChildLabels = BuildSyntheticChildLabels(node, renderableChildren, renderableDirectVariants, syntheticChoiceLabels);

            var orderedEntries = BuildOrderedRenderEntries(
                node,
                renderableChildren,
                renderableDirectVariants,
                syntheticChoiceLabels,
                syntheticChildLabels);
            bool parentHeaderIsOnlyVariantNumber =
                pathAnnotations.Count == 0 &&
                !prefix.Any(line => !string.IsNullOrWhiteSpace(line));

            foreach (var entry in orderedEntries)
            {
                if (ShouldSkipNoOpRenderEntryInTransparentParent(parentHeaderIsOnlyVariantNumber, entry, orderedEntries))
                    continue;

                if (entry.ChildNode != null)
                {
                    result.AddRange(BuildFlatTerminalVariantsFromTreeNode(
                        entry.ChildNode,
                        prefix,
                        pathAnnotations,
                        BuildSiblingNoOpLeafKeysForRenderEntry(
                            entry,
                            renderableChildren,
                            renderableDirectVariants),
                        entry.ChildLabelOverride));
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    result.Add(CreateFlatChoiceTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations,
                        syntheticLabel));
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    result.Add(CreateFlatChoiceTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations,
                        "N)"));
                }
                else
                {
                    result.Add(CreateFlatLooseTerminalVariant(
                        entry.DirectVariant,
                        prefix,
                        pathAnnotations));
                }
            }

            return result;
        }

        private static FlatVariantRenderItem CreateFlatChoiceTerminalVariant(
            VariantRenderItem item,
            List<string> inheritedLines,
            List<string> inheritedHeaderAnnotations,
            string label)
        {
            var annotations = inheritedHeaderAnnotations?.ToList() ?? new List<string>();

            var itemLines = item?.Lines?.ToList() ?? new List<string>();
            AddFlatPathLabel(annotations, itemLines, label);
            AddDistinctHeaderAnnotations(annotations, item?.HeaderAnnotations);
            if (itemLines.Any(line => !string.IsNullOrWhiteSpace(line)) && IsNoOpOnly(itemLines))
                itemLines.Clear();
            AddDistinctSupplementalLines(itemLines, item?.SupplementalLines);

            return new FlatVariantRenderItem
            {
                Variant = item?.Variant,
                OrderKey = GetVariantRenderOrderKey(item),
                Lines = (inheritedLines ?? new List<string>()).Concat(itemLines).ToList(),
                HeaderAnnotations = annotations
            };
        }

        private static FlatVariantRenderItem CreateFlatLooseTerminalVariant(
            VariantRenderItem item,
            List<string> inheritedLines,
            List<string> inheritedHeaderAnnotations)
        {
            var annotations = inheritedHeaderAnnotations?.ToList() ?? new List<string>();
            var itemLines = item?.Lines?.ToList() ?? new List<string>();
            string promotedLabel = TryPromoteLeadingAnswerLine(itemLines);
            AddFlatPathLabel(annotations, itemLines, promotedLabel);
            AddDistinctHeaderAnnotations(annotations, item?.HeaderAnnotations);
            AddDistinctSupplementalLines(itemLines, item?.SupplementalLines);

            return new FlatVariantRenderItem
            {
                Variant = item?.Variant,
                OrderKey = GetVariantRenderOrderKey(item),
                Lines = (inheritedLines ?? new List<string>()).Concat(itemLines).ToList(),
                HeaderAnnotations = annotations
            };
        }

        private static FlatVariantRenderItem CreateFlatTerminalVariant(
            VariantRenderItem item,
            List<string> inheritedLines,
            List<string> inheritedHeaderAnnotations,
            bool includeItemLines = true)
        {
            var annotations = inheritedHeaderAnnotations?.ToList() ?? new List<string>();
            AddDistinctHeaderAnnotations(annotations, item?.HeaderAnnotations);
            var itemLines = includeItemLines
                ? BuildVariantRenderLines(item)
                : new List<string>();

            return new FlatVariantRenderItem
            {
                Variant = item?.Variant,
                OrderKey = GetVariantRenderOrderKey(item),
                Lines = (inheritedLines ?? new List<string>())
                    .Concat(itemLines)
                    .ToList(),
                HeaderAnnotations = annotations
            };
        }

        private static List<string> BuildVariantRenderLines(VariantRenderItem item)
        {
            var lines = item?.Lines?.ToList() ?? new List<string>();
            AddDistinctSupplementalLines(lines, item?.SupplementalLines);
            return lines;
        }

        private static void AddFlatPathHeaderAnnotations(
            List<string> target,
            string label,
            string headerAnnotation)
        {
            if (target == null)
                return;

            string displayLabel = BuildFlatChoiceLabelHeaderAnnotation(label);
            if (!string.IsNullOrWhiteSpace(displayLabel))
                AddDistinctHeaderAnnotations(target, new[] { displayLabel });

            string displayHeaderAnnotation = NormalizeUserVisibleHeaderAnnotation(headerAnnotation);
            if (!string.IsNullOrWhiteSpace(displayHeaderAnnotation))
                AddDistinctHeaderAnnotations(target, new[] { displayHeaderAnnotation });
        }

        private static void AddFlatPathLabel(
            List<string> headerAnnotations,
            List<string> bodyLines,
            string label)
        {
            if (TryBuildFlatChoiceLabelHeaderAnnotation(label, out string headerAnnotation))
            {
                AddDistinctHeaderAnnotations(headerAnnotations, new[] { headerAnnotation });
                return;
            }

            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(displayLabel) || bodyLines == null)
                return;

            if (!bodyLines.Any(line => string.Equals(line?.Trim(), displayLabel, StringComparison.Ordinal)))
                bodyLines.Add(displayLabel);
        }

        private static void PrependBodyOnlyLabelLine(List<string> bodyLines, string label)
        {
            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(displayLabel) ||
                bodyLines == null ||
                !IsFlatOutcomeBodyLabel(displayLabel))
            {
                return;
            }

            if (!bodyLines.Any(line => string.Equals(line?.Trim(), displayLabel, StringComparison.Ordinal)))
                bodyLines.Insert(0, displayLabel);
        }

        private static string BuildFlatChoiceLabelHeaderAnnotation(string label)
        {
            return TryBuildFlatChoiceLabelHeaderAnnotation(label, out string annotation)
                ? annotation
                : null;
        }

        private static bool TryBuildFlatChoiceLabelHeaderAnnotation(string label, out string annotation)
        {
            annotation = null;
            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(displayLabel))
                return false;

            if (IsFlatOutcomeBodyLabel(displayLabel))
                return false;

            string token = displayLabel.EndsWith(")", StringComparison.Ordinal)
                ? displayLabel.Substring(0, displayLabel.Length - 1).Trim()
                : null;

            if (!string.IsNullOrWhiteSpace(token) &&
                (string.Equals(token, "ESC", StringComparison.OrdinalIgnoreCase) ||
                 Regex.IsMatch(token, @"^[A-Za-z0-9]$", RegexOptions.CultureInvariant)))
            {
                annotation = "выбор " + token.ToUpperInvariant();
                return true;
            }

            if (IsFlatConditionChoiceLabel(displayLabel))
            {
                annotation = displayLabel;
                return true;
            }

            return false;
        }

        private static bool IsFlatConditionChoiceLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string trimmed = label.Trim();
            if (HasGuardHeaderAnnotationPrefix(trimmed) ||
                IsMainQuestCompletionHeaderAnnotation(trimmed))
            {
                return true;
            }

            return Regex.IsMatch(
                trimmed,
                @"(?:\bесть|\bотсутствует|\bвзят|\bне\s+взят)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsFlatOutcomeBodyLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string trimmed = label.TrimStart();
            return IsPartyScanAnswerLine(trimmed) ||
                   IsConditionalPartyStatusLine(trimmed) ||
                   IsBattleOutcomeLine(trimmed) ||
                   trimmed.StartsWith("!", StringComparison.Ordinal) ||
                   trimmed.StartsWith("+", StringComparison.Ordinal) ||
                   trimmed.StartsWith("У партии", StringComparison.Ordinal) ||
                   trimmed.StartsWith("У всех", StringComparison.Ordinal) ||
                   trimmed.StartsWith("Телепорт ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("Монстры битвы ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("На ячейке ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("Ничего не происходит", StringComparison.Ordinal) ||
                   trimmed.StartsWith("POOF", StringComparison.OrdinalIgnoreCase);
        }

        private static PathVariantInfo NormalizeInventoryItemRangePredicatesForDisplay(
            OvrObject obj,
            PathVariantInfo variant)
        {
            var rangeInfos = BuildInventoryItemRangeDisplayInfosForVariant(obj, variant)
                .Where(info => info != null && !string.IsNullOrWhiteSpace(info.Annotation))
                .ToList();
            if (rangeInfos.Count == 0)
                return variant;

            var clone = ClonePathVariantForRender(variant);
            var consumedPredicateKeys = rangeInfos
                .SelectMany(info => info.PredicateKeys ?? new HashSet<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
            var infosByIndex = rangeInfos
                .GroupBy(info => info.InsertBeforeChoiceIndex)
                .ToDictionary(group => group.Key, group => group.ToList());

            var normalizedChoices = new List<BranchChoice>();
            var choices = clone.BranchChoices ?? new List<BranchChoice>();
            for (int index = 0; index < choices.Count; index++)
            {
                if (infosByIndex.TryGetValue(index, out var insertedInfos))
                {
                    foreach (var info in insertedInfos)
                        normalizedChoices.Add(CreateInventoryItemRangeBranchChoice(info.Annotation));
                }

                string predicateKey = BuildChoiceDisplayPredicateKey(choices[index]);
                if (!string.IsNullOrWhiteSpace(predicateKey) &&
                    consumedPredicateKeys.Contains(predicateKey))
                {
                    continue;
                }

                normalizedChoices.Add(choices[index]);
            }

            if (infosByIndex.TryGetValue(int.MaxValue, out var trailingInfos))
            {
                foreach (var info in trailingInfos)
                    normalizedChoices.Add(CreateInventoryItemRangeBranchChoice(info.Annotation));
            }

            clone.BranchChoices = normalizedChoices;
            return clone;
        }

        private static BranchChoice CreateInventoryItemRangeBranchChoice(string annotation)
        {
            return new BranchChoice
            {
                DisplayHeaderAnnotation = NormalizeHeaderAnnotation(annotation),
                Condition = "InventoryItemRange",
                IsLinear = false
            };
        }

        private static List<InventoryItemRangeDisplayInfo> BuildInventoryItemRangeDisplayInfosForVariant(
            OvrObject obj,
            PathVariantInfo variant)
        {
            var explicitInfos = BuildExplicitInventoryItemRangeDisplayInfos(variant);
            if (explicitInfos.Count > 0)
                return explicitInfos;

            return InferComplementaryInventoryItemRangeDisplayInfos(obj, variant);
        }

        private static List<InventoryItemRangeDisplayInfo> BuildExplicitInventoryItemRangeDisplayInfos(
            PathVariantInfo variant)
        {
            var result = new List<InventoryItemRangeDisplayInfo>();
            var choices = GetInventoryItemThresholdChoiceInfos(variant);
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var lower in choices.OrderBy(info => info.Index))
            {
                if (consumed.Contains(lower.PredicateKey) ||
                    lower.Predicate.LoopQuantifier != PartyPredicateLoopQuantifier.Any ||
                    !TryGetInventoryItemLowerBound(lower.Predicate, out byte lowerInclusive))
                {
                    continue;
                }

                var upper = choices
                    .Where(candidate =>
                        !consumed.Contains(candidate.PredicateKey) &&
                        candidate.Predicate.LoopQuantifier == PartyPredicateLoopQuantifier.None &&
                        SameInventoryItemRangePredicateScope(lower.Predicate, candidate.Predicate) &&
                        TryGetInventoryItemLowerBound(candidate.Predicate, out byte upperExclusive) &&
                        upperExclusive > lowerInclusive)
                    .OrderBy(candidate =>
                        TryGetInventoryItemLowerBound(candidate.Predicate, out byte upperExclusive)
                            ? upperExclusive
                            : byte.MaxValue)
                    .ThenBy(candidate => candidate.Index)
                    .FirstOrDefault();

                if (upper == null ||
                    !TryGetInventoryItemLowerBound(upper.Predicate, out byte selectedUpperExclusive) ||
                    !TryBuildInventoryItemRangeDisplayInfo(
                        lowerInclusive,
                        selectedUpperExclusive,
                        "есть",
                        Math.Min(lower.Index, upper.Index),
                        new[] { lower.PredicateKey, upper.PredicateKey },
                        BuildInventoryItemRangePredicateScopeKey(lower.Predicate),
                        out var info))
                {
                    continue;
                }

                result.Add(info);
                consumed.Add(lower.PredicateKey);
                consumed.Add(upper.PredicateKey);
            }

            return result;
        }

        private static List<InventoryItemRangeDisplayInfo> InferComplementaryInventoryItemRangeDisplayInfos(
            OvrObject obj,
            PathVariantInfo variant)
        {
            if (obj?.PathVariants == null || variant == null)
                return new List<InventoryItemRangeDisplayInfo>();

            var knownRanges = obj.PathVariants.Values
                .Where(pathVariant => pathVariant != null && !ReferenceEquals(pathVariant, variant))
                .SelectMany(BuildExplicitInventoryItemRangeDisplayInfos)
                .ToList();
            if (knownRanges.Count == 0)
                return new List<InventoryItemRangeDisplayInfo>();

            var result = new List<InventoryItemRangeDisplayInfo>();
            foreach (var choiceInfo in GetInventoryItemThresholdChoiceInfos(variant))
            {
                if (choiceInfo.Predicate.LoopQuantifier != PartyPredicateLoopQuantifier.None ||
                    !TryGetInventoryItemLowerBound(choiceInfo.Predicate, out byte lowerInclusive))
                {
                    continue;
                }

                var knownRange = knownRanges
                    .Where(range =>
                        range.LowerInclusive == lowerInclusive &&
                        string.Equals(
                            range.ScopeKey,
                            BuildInventoryItemRangePredicateScopeKey(choiceInfo.Predicate),
                            StringComparison.Ordinal))
                    .OrderBy(range => range.UpperExclusive)
                    .FirstOrDefault();

                if (knownRange == null ||
                    !TryBuildInventoryItemRangeDisplayInfo(
                        knownRange.LowerInclusive,
                        knownRange.UpperExclusive,
                        "отсутствует",
                        choiceInfo.Index,
                        new[] { choiceInfo.PredicateKey },
                        knownRange.ScopeKey,
                        out var info))
                {
                    continue;
                }

                result.Add(info);
            }

            return result;
        }

        private static List<BranchChoicePredicateInfo> GetInventoryItemThresholdChoiceInfos(
            PathVariantInfo variant)
        {
            var result = new List<BranchChoicePredicateInfo>();
            var choices = variant?.BranchChoices ?? new List<BranchChoice>();
            for (int index = 0; index < choices.Count; index++)
            {
                var predicate = choices[index]?.GetGuardPredicateForDisplay();
                string predicateKey = PartyEffectSemantics.BuildPredicateKey(predicate);
                if (!IsInventoryItemThresholdPredicate(predicate) ||
                    string.IsNullOrWhiteSpace(predicateKey))
                {
                    continue;
                }

                result.Add(new BranchChoicePredicateInfo
                {
                    Choice = choices[index],
                    Predicate = predicate,
                    PredicateKey = predicateKey,
                    Index = index
                });
            }

            return result;
        }

        private static bool TryBuildInventoryItemRangeDisplayInfo(
            byte lowerInclusive,
            byte upperExclusive,
            string presenceLabel,
            int insertBeforeChoiceIndex,
            IEnumerable<string> predicateKeys,
            string scopeKey,
            out InventoryItemRangeDisplayInfo info)
        {
            info = null;
            if (upperExclusive <= lowerInclusive ||
                !PartyInventorySemantics.TryFormatItemCodeRange(
                    lowerInclusive,
                    (byte)(upperExclusive - 1),
                    out string itemRangeText))
            {
                return false;
            }

            string annotation = BuildInventoryPresenceHeaderAnnotation(
                itemRangeText,
                presenceLabel,
                slotLabel: null);
            if (string.IsNullOrWhiteSpace(annotation))
                return false;

            info = new InventoryItemRangeDisplayInfo
            {
                Annotation = annotation,
                InsertBeforeChoiceIndex = insertBeforeChoiceIndex,
                LowerInclusive = lowerInclusive,
                UpperExclusive = upperExclusive,
                ScopeKey = scopeKey
            };

            foreach (string key in predicateKeys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(key))
                    info.PredicateKeys.Add(key);
            }

            return true;
        }

        private static bool IsInventoryItemThresholdPredicate(PartyPredicate predicate)
        {
            return predicate != null &&
                   predicate.ValueKnowledge == PartyValueKnowledge.ExactImmediate &&
                   predicate.ImmediateValue.HasValue &&
                   predicate.ImmediateValue.Value <= byte.MaxValue &&
                   predicate.ImmediateRange == null &&
                   PartyInventorySemantics.IsInventorySlotReference(
                       predicate.FieldOffset,
                       predicate.FieldOffsetRange) &&
                   TryGetInventoryItemLowerBound(predicate, out _);
        }

        private static bool TryGetInventoryItemLowerBound(PartyPredicate predicate, out byte lowerBound)
        {
            lowerBound = 0;
            if (predicate == null ||
                !predicate.ImmediateValue.HasValue ||
                predicate.ImmediateValue.Value > byte.MaxValue)
            {
                return false;
            }

            byte value = (byte)predicate.ImmediateValue.Value;
            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.GreaterOrEqual:
                    lowerBound = value;
                    return true;
                case PartyPredicateComparison.GreaterThan when value < byte.MaxValue:
                    lowerBound = (byte)(value + 1);
                    return true;
                default:
                    return false;
            }
        }

        private static bool SameInventoryItemRangePredicateScope(
            PartyPredicate left,
            PartyPredicate right)
        {
            if (left == null || right == null)
                return false;

            return left.Field == right.Field &&
                   string.Equals(
                       BuildPredicateTargetKeyForDisplayMerge(left.TargetMember),
                       BuildPredicateTargetKeyForDisplayMerge(right.TargetMember),
                       StringComparison.Ordinal) &&
                   InventorySlotReferencesOverlap(left, right);
        }

        private static bool InventorySlotReferencesOverlap(
            PartyPredicate left,
            PartyPredicate right)
        {
            if (!TryGetInventorySlotReferenceRange(left, out byte leftMin, out byte leftMax) ||
                !TryGetInventorySlotReferenceRange(right, out byte rightMin, out byte rightMax))
            {
                return false;
            }

            return leftMin <= rightMax && rightMin <= leftMax;
        }

        private static bool TryGetInventorySlotReferenceRange(
            PartyPredicate predicate,
            out byte min,
            out byte max)
        {
            min = 0;
            max = 0;
            if (predicate == null)
                return false;

            if (predicate.FieldOffsetRange != null)
            {
                min = predicate.FieldOffsetRange.Min;
                max = predicate.FieldOffsetRange.Max;
                return PartyInventorySemantics.IsInventorySlotRange(predicate.FieldOffsetRange);
            }

            if (PartyInventorySemantics.IsInventorySlotOffset(predicate.FieldOffset))
            {
                min = predicate.FieldOffset.Value;
                max = predicate.FieldOffset.Value;
                return true;
            }

            return false;
        }

        private static string BuildInventoryItemRangePredicateScopeKey(PartyPredicate predicate)
        {
            if (predicate == null)
                return null;

            if (!TryGetInventorySlotReferenceRange(predicate, out byte min, out byte max))
                return null;

            return string.Join("|",
                predicate.Field,
                $"{min:X2}-{max:X2}",
                BuildPredicateTargetKeyForDisplayMerge(predicate.TargetMember));
        }

        private static string BuildChoiceDisplayPredicateKey(BranchChoice choice)
        {
            var predicate = choice?.GetGuardPredicateForDisplay();
            return predicate == null
                ? null
                : PartyEffectSemantics.BuildPredicateKey(predicate);
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
                var displayVariant = NormalizeInventoryItemRangePredicatesForDisplay(obj, variant);
                displayVariant = NormalizeAreaE1JudgementStatueScoreScratchForDisplay(obj, displayVariant);
                if (ShouldSuppressByUserVisibleAssumptions(displayVariant))
                    continue;

                var variantObject = displayVariant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte);
                var narrativeLines = DecodeNoteTexts(displayVariant.Texts);
                var lines = BuildVariantLinesForHierarchy(
                    variantObject,
                    displayVariant.Texts,
                    defaultRandomEncounterMonsterPowerCap,
                    defaultRandomEncounterMonsterLevelCap,
                    defaultRandomEncounterMonsterBatchCountCap,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance,
                    inlineSpecialSpoilerLine);

                lines = SuppressRedundantFoodEffectLines(lines);
                lines = NumberLootBlockIfNeeded(lines) ?? new List<string>();
                lines = ApplyAreaE1JudgementStatueScoreSpecificRangesToLines(obj, displayVariant, lines);
                narrativeLines = ApplyAreaE1JudgementStatueScoreSpecificRangesToLines(
                    obj,
                    displayVariant,
                    narrativeLines);

                if (ShouldSkipHierarchicalVariant(lines, narrativeLines))
                    continue;

                items.Add(new VariantRenderItem
                {
                    Variant = displayVariant,
                    Lines = lines,
                    NarrativeLines = narrativeLines
                });
            }

            items = SuppressRedundantPermanentStatRaiseNoEffectVariants(items);
            NormalizePermanentStatRaiseOutcomeItems(items);
            items = SuppressRedundantNarrativeCoveredConditionalStatRewardNoEffectVariants(items);
            NormalizeNarrativeCoveredConditionalStatRewardOutcomeItems(items);
            items = CollapseExhaustiveGuardDuplicateItems(items);
            items = CollapseRanalouPrisonerPartyScanItems(items);
            return SuppressStatusComplementVariantsByUserVisibleAssumptions(items);
        }

        private static List<VariantRenderItem> CollapseRanalouPrisonerPartyScanItems(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            var replacements = new Dictionary<VariantRenderItem, VariantRenderItem>();
            var consumed = new HashSet<VariantRenderItem>();

            foreach (var group in source
                .Select(item => new
                {
                    Item = item,
                    Choice = TryGetRanalouPrisonerChoiceValue(item?.Variant, out byte choiceValue)
                        ? choiceValue
                        : (byte?)null
                })
                .Where(entry => entry.Choice.HasValue)
                .GroupBy(entry => entry.Choice.Value))
            {
                var groupItems = group
                    .Select(entry => entry.Item)
                    .Where(item => item != null)
                    .OrderBy(GetVariantRenderOrderKey)
                    .ToList();

                if (groupItems.Count < 2 ||
                    !groupItems.Any(item => HasRanalouPrisonerPartyScanEffect(item?.Variant)))
                {
                    continue;
                }

                var replacement = BuildCollapsedRanalouPrisonerPartyScanItem(groupItems);
                if (replacement == null)
                    continue;

                replacements[groupItems[0]] = replacement;
                foreach (var item in groupItems.Skip(1))
                    consumed.Add(item);
            }

            if (replacements.Count == 0)
                return source;

            var result = new List<VariantRenderItem>();
            foreach (var item in source)
            {
                if (consumed.Contains(item))
                    continue;

                result.Add(replacements.TryGetValue(item, out var replacement) ? replacement : item);
            }

            return result;
        }

        private static bool TryGetRanalouPrisonerChoiceValue(PathVariantInfo variant, out byte choiceValue)
        {
            choiceValue = 0;
            var values = (variant?.BranchChoices ?? new List<BranchChoice>())
                .Where(choice => choice != null &&
                                 !string.IsNullOrWhiteSpace(choice.Label) &&
                                 choice.Label.StartsWith("InputChoice", StringComparison.OrdinalIgnoreCase) &&
                                 choice.CompareValue >= (byte)'1' &&
                                 choice.CompareValue <= (byte)'9')
                .Select(choice => choice.CompareValue.Value)
                .ToList();

            if (values.Count == 0)
                return false;

            choiceValue = values.Max();
            return true;
        }

        private static bool HasRanalouPrisonerPartyScanEffect(PathVariantInfo variant)
        {
            return variant?.PartyEffects?.Any(IsRanalouPrisonerPartyScanEffect) == true;
        }

        private static bool IsRanalouPrisonerPartyScanEffect(PartyEffect effect)
        {
            if (effect == null || !PartyEffectSemantics.IsLoopDerived(effect))
                return false;

            var field = PartyEffectSemantics.GetEffectiveField(effect);
            var operation = PartyEffectSemantics.GetEffectiveOperation(effect);

            return (field == PartyFieldKind.Technical71 &&
                    operation == PartyEffectOperation.BitSet &&
                    effect.ImmediateValue.HasValue &&
                    PartyTechnicalFieldSemantics.IsRanalouPrisonerProgressMask(
                        (byte)effect.ImmediateValue.Value)) ||
                   (field == PartyFieldKind.Technical6E &&
                    operation == PartyEffectOperation.Increment &&
                    effect.ImmediateValue == 0x20);
        }

        private static VariantRenderItem BuildCollapsedRanalouPrisonerPartyScanItem(
            List<VariantRenderItem> groupItems)
        {
            var preferred = (groupItems ?? new List<VariantRenderItem>())
                .Where(item => item?.Variant != null)
                .OrderByDescending(item => item.Variant.PartyEffects?.Count(IsRanalouPrisonerPartyScanEffect) ?? 0)
                .ThenByDescending(item => item.Variant.PartyEffects?.Count ?? 0)
                .ThenBy(GetVariantRenderOrderKey)
                .FirstOrDefault();
            if (preferred?.Variant == null)
                return null;

            var mergedVariant = ClonePathVariantForRender(preferred.Variant);
            mergedVariant.PathId = groupItems
                .Where(item => item?.Variant != null)
                .Min(item => item.Variant.PathId);
            mergedVariant.PathOrderKey = groupItems
                .Where(item => item?.Variant != null)
                .Min(item => item.Variant.PathOrderKey);
            mergedVariant.BranchChoices = preferred.Variant.BranchChoices?
                .Where(IsRanalouPrisonerInputChoiceBranch)
                .Select(choice => choice.Clone())
                .ToList() ?? new List<BranchChoice>();
            mergedVariant.PartyEffects = groupItems
                .SelectMany(item => item?.Variant?.PartyEffects ?? Enumerable.Empty<PartyEffect>())
                .Where(effect => effect != null)
                .GroupBy(PartyEffectSemantics.BuildSemanticKey)
                .Select(group => group.First().Clone())
                .OrderBy(PartyEffectSemantics.BuildSemanticKey)
                .ToList();

            foreach (var item in groupItems)
                MergeVariantOccurrencesForRender(mergedVariant, item?.Variant);

            var mergedItem = new VariantRenderItem
            {
                Variant = mergedVariant,
                NarrativeLines = preferred.NarrativeLines?.ToList() ?? new List<string>(),
                HeaderAnnotations = preferred.HeaderAnnotations?.ToList() ?? new List<string>(),
                SupplementalLines = preferred.SupplementalLines?.ToList() ?? new List<string>(),
                ConditionalComplementOutcomeEffectKeys = preferred.ConditionalComplementOutcomeEffectKeys != null
                    ? new HashSet<string>(preferred.ConditionalComplementOutcomeEffectKeys, StringComparer.Ordinal)
                    : null
            };

            var oldEffectLines = groupItems
                .SelectMany(item => GetVariantPartyEffectLines(item))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var baseLines = RemoveLineOccurrences(
                preferred.Lines?.Where(line => line != null).ToList() ?? new List<string>(),
                oldEffectLines);
            var mergedEffectLines = GetVariantPartyEffectLines(mergedItem)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            mergedItem.Lines = InsertMissingLinesBeforeBattle(baseLines, mergedEffectLines);
            return mergedItem;
        }

        private static bool IsRanalouPrisonerInputChoiceBranch(BranchChoice choice)
        {
            return choice != null &&
                   !string.IsNullOrWhiteSpace(choice.Label) &&
                   choice.Label.StartsWith("InputChoice", StringComparison.OrdinalIgnoreCase);
        }

        private static void MergeVariantOccurrencesForRender(PathVariantInfo target, PathVariantInfo source)
        {
            if (target == null || source == null)
                return;

            target.OccurrenceIndices = (target.OccurrenceIndices ?? new List<int>())
                .Concat(source.OccurrenceIndices ?? new List<int>())
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }

        private static List<string> InsertMissingLinesBeforeBattle(
            List<string> lines,
            IEnumerable<string> linesToInsert)
        {
            var result = lines?.Where(line => line != null).ToList() ?? new List<string>();
            foreach (var line in linesToInsert ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line) ||
                    result.Contains(line, StringComparer.Ordinal))
                {
                    continue;
                }

                result = InsertLineBeforeBattleOutcome(result, line);
            }

            return result;
        }

        private static List<VariantRenderItem> SuppressRedundantPermanentStatRaiseNoEffectVariants(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            var outcomeEntries = source
                .Select(item => TryBuildPermanentStatRaiseVariantInfo(item?.Variant, out var info)
                    ? new
                    {
                        Item = item,
                        Info = info,
                        BaseKey = BuildPermanentStatRaiseBaseDisplayKey(item, info)
                    }
                    : null)
                .Where(entry =>
                    entry != null &&
                    !string.IsNullOrWhiteSpace(entry.BaseKey))
                .ToList();
            if (outcomeEntries.Count == 0)
                return source;

            var result = new List<VariantRenderItem>();
            foreach (var item in source)
            {
                if (outcomeEntries.Any(entry => ReferenceEquals(entry.Item, item)))
                {
                    result.Add(item);
                    continue;
                }

                if (!TryGetSinglePermanentStatRaiseGuardMask(item?.Variant, out byte guardMask))
                {
                    result.Add(item);
                    continue;
                }

                string baseKey = BuildPermanentStatRaiseBaseDisplayKey(item, null);
                bool shadowedByOutcome = outcomeEntries.Any(entry =>
                    entry.Info.RaiseFlagMask == guardMask &&
                    string.Equals(entry.BaseKey, baseKey, StringComparison.Ordinal));
                if (!shadowedByOutcome)
                    result.Add(item);
            }

            return result;
        }

        private static void NormalizePermanentStatRaiseOutcomeItems(List<VariantRenderItem> items)
        {
            foreach (var item in items ?? new List<VariantRenderItem>())
            {
                if (!TryBuildPermanentStatRaiseVariantInfo(item?.Variant, out var info))
                    continue;

                var linesWithoutTechnicalEffects = RemovePermanentStatRaiseOutcomeEffectLines(item, info);
                if (!HasPermanentStatRaiseNarrativeCue(linesWithoutTechnicalEffects, info))
                    continue;

                item.Lines = linesWithoutTechnicalEffects;
            }
        }

        private static string BuildPermanentStatRaiseBaseDisplayKey(
            VariantRenderItem item,
            PermanentStatRaiseCollapseInfo info)
        {
            if (item == null)
                return null;

            var lines = info == null
                ? item.Lines?.Where(line => line != null).ToList() ?? new List<string>()
                : RemovePermanentStatRaiseOutcomeEffectLines(item, info);

            string occurrenceKey = BuildOccurrenceLine(item.Variant) ?? string.Empty;
            string probabilityKey = BuildProbabilityLine(item.Variant) ?? string.Empty;
            byte? raiseFlagMask = info?.RaiseFlagMask;
            if (!raiseFlagMask.HasValue &&
                TryGetSinglePermanentStatRaiseGuardMask(item.Variant, out byte guardMask))
            {
                raiseFlagMask = guardMask;
            }

            string choiceKey = string.Join("|",
                GetRelevantBranchChoices(item.Variant, raiseFlagMask)
                    .Select(BuildUserVisibleChoiceDisplayKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
            string linesKey = BuildCommonLinesKey(lines);

            return string.Join("\n---\n", occurrenceKey, probabilityKey, choiceKey, linesKey);
        }

        private static List<string> RemovePermanentStatRaiseOutcomeEffectLines(
            VariantRenderItem item,
            PermanentStatRaiseCollapseInfo info)
        {
            if (item == null || info == null)
                return item?.Lines?.Where(line => line != null).ToList() ?? new List<string>();

            var linesToRemove = GetVariantPartyEffectLines(
                item,
                effect => IsPermanentStatRaiseOutcomeEffect(effect, info));
            return RemoveLineOccurrences(item.Lines, linesToRemove);
        }

        private static bool HasPermanentStatRaiseNarrativeCue(
            IEnumerable<string> lines,
            PermanentStatRaiseCollapseInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.StatName))
                return false;

            return (lines ?? Enumerable.Empty<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Any(line =>
                    line.IndexOf(info.StatName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf('+') >= 0);
        }

        private static bool TryGetSinglePermanentStatRaiseGuardMask(
            PathVariantInfo variant,
            out byte mask)
        {
            mask = 0;
            var masks = GetVariantPredicateCandidates(variant)
                .Where(predicate => predicate?.Comparison == PartyPredicateComparison.Equal)
                .Select(predicate => TryGetSinglePermanentStatRaiseMask(predicate, out byte predicateMask)
                    ? (byte?)predicateMask
                    : null)
                .Where(predicateMask => predicateMask.HasValue)
                .Select(predicateMask => predicateMask.Value)
                .Distinct()
                .ToList();

            if (masks.Count != 1)
                return false;

            mask = masks[0];
            return true;
        }

        private static List<VariantRenderItem> SuppressRedundantNarrativeCoveredConditionalStatRewardNoEffectVariants(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            var outcomeEntries = source
                .Select(item => TryBuildNarrativeCoveredConditionalStatRewardInfo(item?.Variant, out var info)
                    ? new
                    {
                        Item = item,
                        Info = info,
                        BaseKey = BuildNarrativeCoveredConditionalStatRewardBaseDisplayKey(item, info)
                    }
                    : null)
                .Where(entry =>
                    entry != null &&
                    !string.IsNullOrWhiteSpace(entry.BaseKey))
                .ToList();
            if (outcomeEntries.Count == 0)
                return source;

            var result = new List<VariantRenderItem>();
            foreach (var item in source)
            {
                if (outcomeEntries.Any(entry => ReferenceEquals(entry.Item, item)))
                {
                    result.Add(item);
                    continue;
                }

                bool shadowedByOutcome = outcomeEntries.Any(entry =>
                    HasNarrativeCoveredConditionalStatRewardGuardPredicate(item?.Variant, entry.Info) &&
                    string.Equals(
                        BuildNarrativeCoveredConditionalStatRewardBaseDisplayKey(item, entry.Info),
                        entry.BaseKey,
                        StringComparison.Ordinal));
                if (!shadowedByOutcome)
                    result.Add(item);
            }

            return result;
        }

        private static void NormalizeNarrativeCoveredConditionalStatRewardOutcomeItems(
            List<VariantRenderItem> items)
        {
            foreach (var item in items ?? new List<VariantRenderItem>())
            {
                if (!TryBuildNarrativeCoveredConditionalStatRewardInfo(item?.Variant, out var info))
                    continue;

                if (ShouldUseNarrativeCoveredConditionalStatRewardHeader(item.Variant, info))
                {
                    AddDistinctHeaderAnnotations(item.HeaderAnnotations, new[] { info.HeaderAnnotation });
                }
                else
                {
                    AddDistinctSupplementalLines(
                        item.SupplementalLines,
                        new[] { BuildNarrativeCoveredConditionalStatRewardSupplementalLine(info) });
                }

                item.Lines = RemoveNarrativeCoveredConditionalStatRewardEffectLines(item, info);
            }
        }

        private static string BuildNarrativeCoveredConditionalStatRewardBaseDisplayKey(
            VariantRenderItem item,
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            if (item == null || info == null)
                return null;

            var lines = RemoveNarrativeCoveredConditionalStatRewardEffectLines(item, info);
            string occurrenceKey = BuildOccurrenceLine(item.Variant) ?? string.Empty;
            string probabilityKey = BuildProbabilityLine(item.Variant) ?? string.Empty;
            string choiceKey = string.Join("|",
                GetRelevantBranchChoices(
                        item.Variant,
                        suppressedLoopGuardPredicateKeys: info.GuardPredicateKeys)
                    .Select(BuildUserVisibleChoiceDisplayKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
            string linesKey = BuildCommonLinesKey(lines);

            return string.Join("\n---\n", occurrenceKey, probabilityKey, choiceKey, linesKey);
        }

        private static List<string> RemoveNarrativeCoveredConditionalStatRewardEffectLines(
            VariantRenderItem item,
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            if (item == null || info == null)
                return item?.Lines?.Where(line => line != null).ToList() ?? new List<string>();

            var linesToRemove = GetVariantPartyEffectLines(
                item,
                effect => effect != null &&
                          info.EffectKeys.Contains(PartyEffectSemantics.BuildSemanticKey(effect)));
            return RemoveLineOccurrences(item.Lines, linesToRemove);
        }

        private static bool HasNarrativeCoveredConditionalStatRewardGuardPredicate(
            PathVariantInfo variant,
            NarrativeCoveredConditionalStatRewardInfo info)
        {
            if (variant == null || info?.GuardPredicateKeys == null || info.GuardPredicateKeys.Count == 0)
                return false;

            return GetVariantPredicateCandidates(variant)
                .Any(predicate =>
                {
                    string key = BuildLoopNormalizedPredicateKey(predicate);
                    return !string.IsNullOrWhiteSpace(key) &&
                           info.GuardPredicateKeys.Contains(key);
                });
        }

        private static bool HasNarrativeCoveredStatRewardCue(
            IEnumerable<string> lines,
            PartyFieldKind statField)
        {
            var aliases = GetNarrativeCoveredStatRewardAliases(statField)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToList();
            if (aliases.Count == 0)
                return false;

            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line) || line.IndexOf('+') < 0)
                    continue;

                foreach (string alias in aliases)
                {
                    string pattern = @"(?<![A-Z0-9])\+\s*\d+\s+" +
                                     Regex.Escape(alias) +
                                     @"(?![A-Z0-9])";
                    if (Regex.IsMatch(
                            line,
                            pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> GetNarrativeCoveredStatRewardAliases(PartyFieldKind field)
        {
            switch (field)
            {
                case PartyFieldKind.PermanentIntellect:
                    yield return "INT";
                    yield return "INTELLECT";
                    break;
                case PartyFieldKind.PermanentMight:
                    yield return "MIGHT";
                    break;
                case PartyFieldKind.PermanentPersonality:
                    yield return "PER";
                    yield return "PERSONALITY";
                    break;
                case PartyFieldKind.PermanentEndurance:
                    yield return "END";
                    yield return "ENDURANCE";
                    break;
                case PartyFieldKind.PermanentSpeed:
                    yield return "SPD";
                    yield return "SPEED";
                    break;
                case PartyFieldKind.PermanentAccuracy:
                    yield return "ACC";
                    yield return "ACCURACY";
                    yield return "ACCURANCY";
                    break;
                case PartyFieldKind.PermanentLuck:
                    yield return "LUCK";
                    break;
            }
        }

        private static List<VariantRenderItem> CollapseExhaustiveGuardDuplicateItems(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            var consumed = new HashSet<VariantRenderItem>();
            var replacements = new Dictionary<VariantRenderItem, VariantRenderItem>();

            foreach (var group in source
                .Select(item => new
                {
                    Item = item,
                    Key = BuildDisplayedVariantItemKeyExcludingOwnGuardPredicate(item)
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .GroupBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var groupItems = group
                    .Select(entry => entry.Item)
                    .Where(item => item != null)
                    .ToList();
                if (groupItems.Count < 2)
                    continue;

                if (!TryGetExhaustiveGuardDuplicatePredicateKeys(groupItems, out var predicateKeysToStrip))
                    continue;

                var first = groupItems
                    .OrderBy(GetVariantRenderOrderKey)
                    .First();
                var replacement = CloneVariantRenderItemWithoutGuardPredicates(
                    first,
                    predicateKeysToStrip);
                foreach (var item in groupItems)
                    MergeDisplayedVariantItemMetadata(replacement, item);

                replacements[first] = replacement;

                foreach (var item in groupItems)
                {
                    if (!ReferenceEquals(item, first))
                        consumed.Add(item);
                }
            }

            if (replacements.Count == 0)
                return source;

            var result = new List<VariantRenderItem>();
            foreach (var item in source)
            {
                if (consumed.Contains(item))
                    continue;

                result.Add(replacements.TryGetValue(item, out var replacement)
                    ? replacement
                    : item);
            }

            return result;
        }

        private static string BuildDisplayedVariantItemKeyExcludingOwnGuardPredicate(
            VariantRenderItem item)
        {
            if (!TryGetSingleComplementaryGuardPredicate(item, out var predicate, out string predicateKey))
                return null;

            return BuildDisplayedVariantItemKeyExcludingGuardPredicates(
                item,
                new HashSet<string>(new[] { predicateKey }, StringComparer.Ordinal));
        }

        private static string BuildDisplayedVariantItemKeyExcludingGuardPredicates(
            VariantRenderItem item,
            HashSet<string> predicateKeysToExclude)
        {
            string occurrenceKey = BuildOccurrenceLine(item?.Variant) ?? string.Empty;
            string probabilityKey = BuildProbabilityLine(item?.Variant) ?? string.Empty;
            string guardConditionKey = string.Join("&",
                GetDisplayGuardPredicates(item?.Variant)
                    .Where(predicate => !ContainsPredicateKey(predicateKeysToExclude, predicate))
                    .Select(PartyEffectSemantics.BuildPredicateKey)
                    .OrderBy(key => key));
            string branchKey = string.Join("|",
                GetRelevantBranchChoices(item?.Variant)
                    .Where(choice => !ContainsPredicateKey(predicateKeysToExclude, choice?.GetGuardPredicateForDisplay()))
                    .Select(choice => BuildUserVisibleChoiceDisplayKey(choice))
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
            string linesKey = string.Join("\n",
                (item?.Lines ?? new List<string>())
                    .Select(line => line?.TrimEnd() ?? string.Empty));

            return string.Join("\n---\n", occurrenceKey, probabilityKey, guardConditionKey, branchKey, linesKey);
        }

        private static bool TryGetExhaustiveGuardDuplicatePredicateKeys(
            List<VariantRenderItem> items,
            out HashSet<string> predicateKeys)
        {
            predicateKeys = new HashSet<string>(StringComparer.Ordinal);

            var predicates = new List<PartyPredicate>();
            foreach (var item in items ?? new List<VariantRenderItem>())
            {
                if (!TryGetSingleComplementaryGuardPredicate(item, out var predicate, out string predicateKey))
                    return false;

                if (predicateKeys.Add(predicateKey))
                    predicates.Add(predicate);
            }

            return predicates.Count == 2 &&
                   AreExhaustiveComplementaryPredicates(predicates[0], predicates[1]);
        }

        private static bool TryGetSingleComplementaryGuardPredicate(
            VariantRenderItem item,
            out PartyPredicate predicate,
            out string predicateKey)
        {
            predicate = null;
            predicateKey = null;

            var predicates = (item?.Variant?.BranchChoices ?? new List<BranchChoice>())
                .Where(choice => choice?.GuardPredicate != null)
                .Select(choice => choice.GetGuardPredicateForDisplay())
                .Where(IsPotentialExhaustiveComplementPredicate)
                .GroupBy(BuildExhaustiveGuardPredicateKey)
                .Select(group => group.First())
                .ToList();

            if (predicates.Count != 1)
                return false;

            predicate = predicates[0];
            predicateKey = BuildExhaustiveGuardPredicateKey(predicate);
            return !string.IsNullOrWhiteSpace(predicateKey);
        }

        private static string BuildExhaustiveGuardPredicateKey(PartyPredicate predicate)
        {
            string key = PartyEffectSemantics.BuildPredicateKey(predicate);
            if (predicate == null)
                return key;

            bool hasDisplayLoopQuantifier =
                predicate.LoopQuantifier == PartyPredicateLoopQuantifier.Any ||
                predicate.LoopQuantifier == PartyPredicateLoopQuantifier.None;
            return hasDisplayLoopQuantifier
                ? $"{key}|display-q:{predicate.LoopQuantifier}"
                : key;
        }

        private static bool IsPotentialExhaustiveComplementPredicate(PartyPredicate predicate)
        {
            if (predicate == null ||
                PartyInventorySemantics.IsInventoryItemPresencePredicate(predicate) ||
                predicate.ValueKnowledge != PartyValueKnowledge.ExactImmediate ||
                !predicate.ImmediateValue.HasValue ||
                predicate.ImmediateRange != null)
            {
                return false;
            }

            return predicate.Comparison == PartyPredicateComparison.Equal ||
                   predicate.Comparison == PartyPredicateComparison.NotEqual ||
                   predicate.Comparison == PartyPredicateComparison.LessThan ||
                   predicate.Comparison == PartyPredicateComparison.LessOrEqual ||
                   predicate.Comparison == PartyPredicateComparison.GreaterThan ||
                   predicate.Comparison == PartyPredicateComparison.GreaterOrEqual;
        }

        private static bool AreExhaustiveComplementaryPredicates(
            PartyPredicate first,
            PartyPredicate second)
        {
            if (!HaveSamePredicateBoundary(first, second))
                return false;

            return AreExhaustiveComplementaryComparisons(first.Comparison, second.Comparison) ||
                   AreExhaustiveComplementaryComparisons(second.Comparison, first.Comparison) ||
                   AreExhaustiveComplementaryLoopQuantifiers(first, second);
        }

        private static bool AreExhaustiveComplementaryLoopQuantifiers(
            PartyPredicate first,
            PartyPredicate second)
        {
            if (first == null || second == null ||
                first.Comparison != second.Comparison)
            {
                return false;
            }

            return (first.LoopQuantifier == PartyPredicateLoopQuantifier.Any &&
                    second.LoopQuantifier == PartyPredicateLoopQuantifier.None) ||
                   (first.LoopQuantifier == PartyPredicateLoopQuantifier.None &&
                    second.LoopQuantifier == PartyPredicateLoopQuantifier.Any);
        }

        private static bool HaveSamePredicateBoundary(PartyPredicate first, PartyPredicate second)
        {
            if (first == null || second == null)
                return false;

            return first.Field == second.Field &&
                   first.FieldOffset == second.FieldOffset &&
                   BuildRangeKey(first.FieldOffsetRange) == BuildRangeKey(second.FieldOffsetRange) &&
                   first.ValueKnowledge == second.ValueKnowledge &&
                   first.ImmediateValue == second.ImmediateValue &&
                   BuildRangeKey(first.ImmediateRange) == BuildRangeKey(second.ImmediateRange) &&
                   BuildPredicateTargetKeyForDisplayMerge(first.TargetMember) ==
                   BuildPredicateTargetKeyForDisplayMerge(second.TargetMember);
        }

        private static bool AreExhaustiveComplementaryComparisons(
            PartyPredicateComparison first,
            PartyPredicateComparison second)
        {
            return (first == PartyPredicateComparison.Equal && second == PartyPredicateComparison.NotEqual) ||
                   (first == PartyPredicateComparison.LessThan && second == PartyPredicateComparison.GreaterOrEqual) ||
                   (first == PartyPredicateComparison.LessOrEqual && second == PartyPredicateComparison.GreaterThan);
        }

        private static string BuildRangeKey(ValueRange8 range)
        {
            return range == null
                ? "-"
                : $"{range.Min:X2}-{range.Max:X2}";
        }

        private static string BuildPredicateTargetKeyForDisplayMerge(PartyMemberReference member)
        {
            if (member == null)
                return "-";

            if (member.IsPartyLoopMember)
                return $"{PartyMemberSelectionKind.Dynamic}:Loop:-:-:-";

            return string.Join(":",
                member.SelectionKind,
                member.IsPartyLoopMember ? "Loop" : "Direct",
                member.MemberIndex?.ToString() ?? "-",
                member.PointerTableAddress?.ToString("X4") ?? "-",
                member.StructureAddress?.ToString("X4") ?? "-");
        }

        private static bool ContainsPredicateKey(
            HashSet<string> predicateKeys,
            PartyPredicate predicate)
        {
            if (predicateKeys == null || predicateKeys.Count == 0 || predicate == null)
                return false;

            string key = PartyEffectSemantics.BuildPredicateKey(predicate);
            if (!string.IsNullOrWhiteSpace(key) && predicateKeys.Contains(key))
                return true;

            string exhaustiveKey = BuildExhaustiveGuardPredicateKey(predicate);
            return !string.IsNullOrWhiteSpace(exhaustiveKey) &&
                   predicateKeys.Contains(exhaustiveKey);
        }

        private static VariantRenderItem CloneVariantRenderItemWithoutGuardPredicates(
            VariantRenderItem item,
            HashSet<string> predicateKeysToStrip)
        {
            if (item == null)
                return null;

            var renderVariant = ClonePathVariantForRender(item.Variant);
            if (renderVariant != null)
            {
                renderVariant.BranchChoices = renderVariant.BranchChoices?
                    .Where(choice => !ContainsPredicateKey(predicateKeysToStrip, choice?.GetGuardPredicateForDisplay()))
                    .ToList() ?? new List<BranchChoice>();
            }

            return new VariantRenderItem
            {
                Variant = renderVariant,
                Lines = item.Lines?.ToList() ?? new List<string>(),
                NarrativeLines = item.NarrativeLines?.ToList() ?? new List<string>(),
                HeaderAnnotations = item.HeaderAnnotations?.ToList() ?? new List<string>(),
                SupplementalLines = item.SupplementalLines?.ToList() ?? new List<string>(),
                ConditionalComplementOutcomeEffectKeys = item.ConditionalComplementOutcomeEffectKeys != null
                    ? new HashSet<string>(item.ConditionalComplementOutcomeEffectKeys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal)
            };
        }

        private static PathVariantInfo NormalizeAreaE1JudgementStatueScoreScratchForDisplay(
            OvrObject obj,
            PathVariantInfo variant)
        {
            if (!IsAreaE1JudgementStatueObject(obj) || variant == null)
                return variant;

            bool hasInternalScoreChoice = variant.BranchChoices?.Any(IsAreaE1JudgementStatueScoreScratchChoice) == true;
            bool hasInternalScoreEffect = variant.PartyEffects?.Any(IsAreaE1JudgementStatueScoreScratchEffect) == true;
            if (!hasInternalScoreChoice && !hasInternalScoreEffect)
                return variant;

            var clone = ClonePathVariantForRender(variant);
            clone.BranchChoices = clone.BranchChoices?
                .Select(choice =>
                    TryBuildAreaE1JudgementStatueScoreDisplayChoice(choice, out var displayChoice)
                        ? displayChoice
                        : choice)
                .Where(choice => !IsAreaE1JudgementStatueScoreScratchChoice(choice))
                .ToList() ?? new List<BranchChoice>();
            clone.PartyEffects = clone.PartyEffects?
                .Where(effect => !IsAreaE1JudgementStatueScoreScratchEffect(effect))
                .ToList() ?? new List<PartyEffect>();
            return clone;
        }

        private static bool TryBuildAreaE1JudgementStatueScoreDisplayChoice(
            BranchChoice choice,
            out BranchChoice displayChoice)
        {
            displayChoice = null;

            var predicate = choice?.GuardPredicate;
            if (predicate?.Field != PartyFieldKind.Technical6E ||
                predicate.ImmediateValue != 128)
            {
                return false;
            }

            string annotation;
            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.LessThan:
                    annotation = "0/16/32/48 EXPERIENCE";
                    break;
                case PartyPredicateComparison.GreaterOrEqual:
                    annotation = "64/80/96 EXPERIENCE";
                    break;
                default:
                    return false;
            }

            displayChoice = new BranchChoice
            {
                DisplayHeaderAnnotation = annotation,
                Condition = choice.Condition,
                IsLinear = choice.IsLinear
            };
            return true;
        }

        private const string AreaE1JudgementStatueLowScoreLineMarker = " [[AREA_E1_SCORE_LOW]]";
        private const string AreaE1JudgementStatueHighScoreLineMarker = " [[AREA_E1_SCORE_HIGH]]";

        private static List<string> ApplyAreaE1JudgementStatueScoreSpecificRangesToLines(
            OvrObject obj,
            PathVariantInfo variant,
            List<string> lines)
        {
            if (!IsAreaE1JudgementStatueObject(obj) ||
                lines == null ||
                lines.Count == 0)
            {
                return lines ?? new List<string>();
            }

            var range = GetAreaE1JudgementStatueScoreRange(variant);
            if (range == AreaE1JudgementStatueScoreRange.None)
                return lines;

            var transformed = lines
                .Select(line => ApplyAreaE1JudgementStatueScoreRangeToRawLine(line, range))
                .ToList();
            return MoveAreaE1JudgementStatueRewardResetAfterExperience(transformed);
        }

        private static AreaE1JudgementStatueScoreRange GetAreaE1JudgementStatueScoreRange(
            PathVariantInfo variant)
        {
            foreach (var choice in variant?.BranchChoices ?? new List<BranchChoice>())
            {
                var range = GetAreaE1JudgementStatueScoreRangeFromHeader(choice?.DisplayHeaderAnnotation);
                if (range != AreaE1JudgementStatueScoreRange.None)
                    return range;
            }

            return AreaE1JudgementStatueScoreRange.None;
        }

        private static string ApplyAreaE1JudgementStatueScoreRangeToRawLine(
            string line,
            AreaE1JudgementStatueScoreRange range)
        {
            if (string.IsNullOrEmpty(line))
                return line;

            string scoreRange = range == AreaE1JudgementStatueScoreRange.High
                ? "4/5/6"
                : "0/1/2/3";
            string experienceRange = range == AreaE1JudgementStatueScoreRange.High
                ? "64/80/96"
                : "0/16/32/48";
            string scoreMarker = range == AreaE1JudgementStatueScoreRange.High
                ? AreaE1JudgementStatueHighScoreLineMarker
                : AreaE1JudgementStatueLowScoreLineMarker;

            line = line.Replace(
                "YOUR ACTIONS REFLECT YOUR VIEWS   OF 6",
                "YOUR ACTIONS REFLECT YOUR VIEWS " + scoreRange + " OF 6");
            line = ApplyAreaE1JudgementStatueScoreRangeToLine(line, range);

            if (line.IndexOf(
                    "-=*У выбранного персонажа партии прогресс линейки квестов волшебника RANALOU",
                    StringComparison.Ordinal) >= 0)
            {
                line += scoreMarker;
            }

            line = Regex.Replace(
                line,
                @"^(\s*)\+\s+EXPERIENCE\b(.*)$",
                match => match.Groups[1].Value + "+" + experienceRange + " EXPERIENCE" + match.Groups[2].Value,
                RegexOptions.CultureInvariant);

            return Regex.Replace(
                line,
                @"^(\s*)\+\s*$",
                match => match.Groups[1].Value + "+" + experienceRange,
                RegexOptions.CultureInvariant);
        }

        private static List<string> MoveAreaE1JudgementStatueRewardResetAfterExperience(List<string> lines)
        {
            if (lines == null || lines.Count < 2)
                return lines ?? new List<string>();

            int resetIndex = lines.FindIndex(line =>
                !string.IsNullOrEmpty(line) &&
                line.IndexOf(
                    "-=*У выбранного персонажа партии прогресс линейки квестов волшебника RANALOU",
                    StringComparison.Ordinal) >= 0);
            if (resetIndex < 0)
                return lines;

            int experienceIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsAreaE1JudgementStatueExperienceRewardLine(lines[i]))
                    experienceIndex = i;
            }

            if (experienceIndex < 0 || resetIndex == experienceIndex + 1)
                return lines;

            string resetLine = lines[resetIndex];
            lines.RemoveAt(resetIndex);
            if (resetIndex < experienceIndex)
                experienceIndex--;
            lines.Insert(Math.Min(experienceIndex + 1, lines.Count), resetLine);
            return lines;
        }

        private static bool IsAreaE1JudgementStatueExperienceRewardLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            return string.Equals(trimmed, "EXPERIENCE", StringComparison.Ordinal) ||
                   Regex.IsMatch(
                       trimmed,
                       @"^\+(?:\d+(?:/\d+)*)?\s+EXPERIENCE\b",
                       RegexOptions.CultureInvariant);
        }

        private static bool IsAreaE1JudgementStatueScoreScratchChoice(BranchChoice choice)
        {
            if (choice == null)
                return false;

            var predicate = choice.GuardPredicate ?? choice.GetGuardPredicateForDisplay();
            if (predicate?.Field == PartyFieldKind.Technical6E)
                return true;

            return choice.ComparedPartyField?.Field == PartyFieldKind.Technical6E;
        }

        private static bool IsAreaE1JudgementStatueScoreScratchEffect(PartyEffect effect)
        {
            return effect != null &&
                   PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Technical6E;
        }

        private static List<VariantRenderItem> SuppressStatusComplementVariantsByUserVisibleAssumptions(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            source = SuppressDeadStatusComplementTextOnlyVariants(source);
            if (source.Count <= 1)
                return source;

            var assumedStatusChangeKeys = source
                .Where(item => ShouldApplyAssumedAliveStatusModel(item?.Variant))
                .Where(item => VariantHasAssumedAliveStatusGuard(item?.Variant))
                .Where(item => (item.Lines ?? new List<string>()).Any(IsConditionalPartyStatusLine))
                .Select(BuildStatusComplementVariantKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);

            if (assumedStatusChangeKeys.Count == 0)
                return source;

            return source
                .Where(item =>
                    (item.Lines ?? new List<string>()).Any(IsConditionalPartyStatusLine) ||
                    (ShouldApplyAssumedAliveStatusModel(item?.Variant) &&
                     VariantHasAssumedAliveStatusGuard(item?.Variant)) ||
                    HasNonTextOutcome(item?.Variant) ||
                    !assumedStatusChangeKeys.Contains(BuildStatusComplementVariantKey(item)))
                .ToList();
        }

        private static List<VariantRenderItem> SuppressDeadStatusComplementTextOnlyVariants(
            List<VariantRenderItem> items)
        {
            var source = (items ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (source.Count <= 1)
                return source;

            var guardedOutcomeEntries = source
                .Where(HasAssumedAliveStatusGuardedOutcome)
                .Select(item => new
                {
                    Item = item,
                    ContextKey = BuildStatusComplementRandomContextKey(item?.Variant),
                    Lines = BuildMeaningfulLineList(item?.Lines)
                })
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.ContextKey) &&
                    entry.Lines.Count > 0)
                .ToList();
            if (guardedOutcomeEntries.Count == 0)
                return source;

            return source
                .Where(item =>
                    !ShouldSuppressDeadStatusComplementTextOnlyVariant(
                        item,
                        guardedOutcomeEntries.Select(entry => (entry.Item, entry.ContextKey, entry.Lines))))
                .ToList();
        }

        private static bool ShouldSuppressDeadStatusComplementTextOnlyVariant(
            VariantRenderItem item,
            IEnumerable<(VariantRenderItem Item, string ContextKey, List<string> Lines)> guardedOutcomeEntries)
        {
            if (!IsDeadStatusComplementTextOnlyVariant(item))
                return false;

            string contextKey = BuildStatusComplementRandomContextKey(item?.Variant);
            if (string.IsNullOrWhiteSpace(contextKey))
                return false;

            var lines = BuildMeaningfulLineList(item?.Lines);
            if (lines.Count == 0)
                return false;

            return (guardedOutcomeEntries ?? Enumerable.Empty<(VariantRenderItem Item, string ContextKey, List<string> Lines)>())
                .Any(entry =>
                    !ReferenceEquals(entry.Item, item) &&
                    string.Equals(entry.ContextKey, contextKey, StringComparison.Ordinal) &&
                    IsLineListPrefix(lines, entry.Lines));
        }

        private static bool IsDeadStatusComplementTextOnlyVariant(VariantRenderItem item)
        {
            var variant = item?.Variant;
            if (variant == null || HasNonTextOutcome(variant))
                return false;

            return GetStatusGuardPredicatesForComplementAnalysis(variant)
                .Any(IsDeadStatusComplementPredicate);
        }

        private static bool HasAssumedAliveStatusGuardedOutcome(VariantRenderItem item)
        {
            var variant = item?.Variant;
            if (variant == null || !HasNonTextOutcome(variant))
                return false;

            return GetStatusGuardPredicatesForComplementAnalysis(variant)
                .Any(IsSatisfiedByAssumedStatusBelowDeathThreshold);
        }

        private static IEnumerable<PartyPredicate> GetStatusGuardPredicatesForComplementAnalysis(
            PathVariantInfo variant)
        {
            foreach (var predicate in variant?.GetGuardPredicates() ?? Array.Empty<PartyPredicate>())
            {
                if (predicate != null)
                    yield return predicate;
            }

            foreach (var choice in variant?.BranchChoices ?? new List<BranchChoice>())
            {
                if (choice?.GuardPredicate != null)
                    yield return choice.GuardPredicate;

                var displayPredicate = choice?.GetGuardPredicateForDisplay();
                if (displayPredicate != null)
                    yield return displayPredicate;
            }

            foreach (var predicate in (variant?.PartyEffects ?? new List<PartyEffect>())
                         .SelectMany(PartyEffectSemantics.GetEffectiveGuardPredicates))
            {
                if (predicate != null)
                    yield return predicate;
            }
        }

        private static bool IsDeadStatusComplementPredicate(PartyPredicate predicate)
        {
            if (!TryGetImmediateStatusPredicate(predicate, out ushort value))
                return false;

            switch (predicate.Comparison)
            {
                case PartyPredicateComparison.GreaterOrEqual:
                    return value == AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.GreaterThan:
                    return value + 1 == AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.LessThan:
                    return predicate.LoopQuantifier == PartyPredicateLoopQuantifier.None &&
                           value == AssumedUserVisibleStatusUpperBoundExclusive;
                case PartyPredicateComparison.LessOrEqual:
                    return predicate.LoopQuantifier == PartyPredicateLoopQuantifier.None &&
                           value + 1 == AssumedUserVisibleStatusUpperBoundExclusive;
                default:
                    return false;
            }
        }

        private static string BuildStatusComplementRandomContextKey(PathVariantInfo variant)
        {
            return string.Join("\n",
                GetRelevantBranchChoices(variant)
                    .Select(choice => NormalizeChoiceLabel(choice?.Label))
                    .Where(IsTechnicalChoiceLabel));
        }

        private static List<string> BuildMeaningfulLineList(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .Select(line => line?.TrimEnd() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private static bool IsLineListPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> lines)
        {
            if (prefix == null || lines == null || prefix.Count == 0 || prefix.Count > lines.Count)
                return false;

            for (int i = 0; i < prefix.Count; i++)
            {
                if (!string.Equals(prefix[i], lines[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool VariantHasAssumedAliveStatusGuard(PathVariantInfo variant)
        {
            return ShouldApplyAssumedAliveStatusModel(variant) &&
                   variant?.GetGuardPredicates()?
                .Any(IsSatisfiedByAssumedStatusBelowDeathThreshold) == true;
        }

        private static string BuildStatusComplementVariantKey(VariantRenderItem item)
        {
            if (item == null)
                return null;

            var randomChoiceLabels = GetRelevantBranchChoices(item.Variant)
                .Select(choice => NormalizeChoiceLabel(choice?.Label))
                .Where(IsTechnicalChoiceLabel)
                .ToList();

            var visibleLinesWithoutStatus = (item.Lines ?? new List<string>())
                .Where(line => !IsConditionalPartyStatusLine(line))
                .Select(line => line?.TrimEnd() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return string.Join("\n", randomChoiceLabels) + "\u001F" +
                   string.Join("\n", visibleLinesWithoutStatus);
        }

        private static List<string> SuppressRedundantFoodEffectLines(List<string> lines)
        {
            if (lines == null || !HasFoodRewardNarrativeLine(lines))
                return lines ?? new List<string>();

            return lines
                .Where(line => !IsGenericFoodChangeEffectLine(line))
                .ToList();
        }

        private static bool HasFoodRewardNarrativeLine(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .Any(line => Regex.IsMatch(
                    line?.Trim() ?? string.Empty,
                    @"^\+\d+\s+FOOD!$",
                    RegexOptions.IgnoreCase));
        }

        private static bool IsGenericFoodChangeEffectLine(string line)
        {
            return !string.IsNullOrWhiteSpace(line) &&
                   line.IndexOf("изменяется FOOD", StringComparison.OrdinalIgnoreCase) >= 0;
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
                        HeaderGuardKey = groupedByChoice ? BuildBranchGuardConditionKey(firstChoice) : null,
                        ConsumedTopChoiceKey = groupedByChoice ? firstChoiceKey : null,
                        GroupedByChoice = groupedByChoice,
                        SourceOrderKey = GetTopLevelGroupSourceOrderKey(obj, ordered)
                    };
                })
                .ToList();

            foreach (var group in groups)
            {
                var root = BuildVariantTree(group.Items, group.GroupedByChoice ? group.ConsumedTopChoiceKey : null);
                PromoteBinaryStateConditionBranches(root);
                IntroduceComplementaryInventoryPresenceBranches(root);
                IntroduceGuardedNoOpComplementOutcomes(root);
                ComputeCommonLines(root);
                IntroduceSharedLineHierarchyAcrossBinaryStateConditionChildren(root);
                IntroduceSharedLineHierarchy(root);
                AttachChoiceChildrenToSiblingPromptParents(root);
                IntroduceSharedPromptHierarchyFromChoiceChildContent(root);
                IntroduceSharedPromptHierarchyAcrossChoiceChildren(root);
                HoistSharedLeadingLines(root);
                HoistSharedCommonPartyNotes(root);
                NormalizeConditionalStatusComplementLines(root);
                GroupConditionalLoopSubsetComplementOutcomes(root);
                PromoteSingleLeafNarrativeCoveredStatRewardAnnotations(root);
                PromoteConditionalPartyNotesBeforeBattle(root);
                RemoveRedundantInheritedLines(root);
                CollapseTransparentDirectVariantWrappers(root);
                IntroduceSharedLineHierarchyAcrossHiddenTechnicalChildren(root);
                HoistSharedLeadingLines(root);
                PromoteSingleLeafNarrativeCoveredStatRewardAnnotations(root);
                CollapseTransparentDirectVariantWrappers(root);
                RemoveRedundantInheritedLines(root);
                group.TreeRoot = PruneDecorativeChoiceLeaves(
                    FlattenTransparentStructuralWrappers(
                        SimplifyGenericChoiceTree(
                            CompressVariantTree(root))));
                HoistSharedLeadingLines(group.TreeRoot, allowDisplayLineHoist: true);
                CollapseSingleSubvariantBranches(group);
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
            if (!TryBuildInventoryResourceAttritionRandomOutcomeModel(items, out var model))
            {
                hierarchy = null;
                return false;
            }

            return TryBuildInventoryResourceAttritionRandomOutcomeHierarchy(model, out hierarchy);
        }

        private static bool TryBuildInventoryResourceAttritionRandomOutcomeHierarchy(
            InventoryResourceAttritionNoteModel model,
            out string hierarchy)
        {
            hierarchy = null;
            if (model == null)
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
                        out string slotLabel,
                        out string presenceLabel))
                {
                    continue;
                }

                if (!TryClassifyResourceAttrition(item, out var resourceKind))
                    continue;

                string groupKey = $"{BuildInventoryPresenceChoiceKey(itemName, slotLabel)}\n{presenceLabel}";
                var outcomeKind = ClassifyResourceRandomOutcome(item);
                if (!grouped.TryGetValue(groupKey, out var group))
                {
                    group = new InventoryResourceGroup
                    {
                        Label = null,
                        HeaderAnnotation = BuildInventoryPresenceHeaderAnnotation(itemName, presenceLabel, slotLabel),
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

            foreach (var resourceGroup in grouped.Values
                .SelectMany(group => group.Resources.Values))
            {
                DeduplicateResourceAttritionOutcomeItems(resourceGroup);
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

        private static void DeduplicateResourceAttritionOutcomeItems(ResourceAttritionGroup resourceGroup)
        {
            if (resourceGroup?.Outcomes == null || resourceGroup.Outcomes.Count == 0)
                return;

            var normalizedItems = new List<VariantRenderItem>();
            foreach (var outcomeGroup in resourceGroup.Outcomes.Values)
            {
                if (outcomeGroup == null || outcomeGroup.Items == null || outcomeGroup.Items.Count == 0)
                    continue;

                outcomeGroup.Items = SelectPreferredResourceOutcomeItems(
                    outcomeGroup.Items,
                    resourceGroup.Kind);
                outcomeGroup.Probability = outcomeGroup.Items
                    .Sum(item => GetVariantProbability(item?.Variant));
                normalizedItems.AddRange(outcomeGroup.Items);
            }

            resourceGroup.Items = normalizedItems
                .Where(item => item != null)
                .Distinct()
                .ToList();
        }

        private static List<VariantRenderItem> SelectPreferredResourceOutcomeItems(
            IEnumerable<VariantRenderItem> items,
            ResourceAttritionKind resourceKind)
        {
            var result = new List<VariantRenderItem>();
            foreach (var group in (items ?? Enumerable.Empty<VariantRenderItem>())
                .Where(item => item != null)
                .GroupBy(BuildResourceOutcomeDisplayDedupKey, StringComparer.Ordinal))
            {
                var groupItems = group.ToList();
                int minNoise = groupItems.Min(item => CountIgnoredResourceAttritionDisplayLines(item, resourceKind));
                result.AddRange(groupItems.Where(item =>
                    CountIgnoredResourceAttritionDisplayLines(item, resourceKind) == minNoise));
            }

            return result
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();
        }

        private static string BuildResourceOutcomeDisplayDedupKey(VariantRenderItem item)
        {
            var lines = (item?.Lines ?? new List<string>())
                .Where(line => !IsResourceAttritionDisplayLine(line))
                .Select(line => line?.TrimEnd() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return string.Join("\n", lines);
        }

        private static int CountIgnoredResourceAttritionDisplayLines(
            VariantRenderItem item,
            ResourceAttritionKind resourceKind)
        {
            return (item?.Lines ?? new List<string>())
                .Count(IsResourceAttritionDisplayLine);
        }

        private static bool IsResourceAttritionDisplayLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            return string.Equals(trimmed, "У партии персонажей уменьшается FOOD на 1", StringComparison.Ordinal) ||
                   trimmed.IndexOf("изменяется FOOD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.IndexOf("временная выносливость", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   IsConditionalPartyStatusLine(trimmed);
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
            out string slotLabel,
            out string presenceLabel)
        {
            itemChoice = null;
            itemName = null;
            slotLabel = null;
            presenceLabel = null;
            itemChoice = GetRelevantBranchChoices(variant)
                .FirstOrDefault(choice => TryBuildInventoryPresenceChoiceInfo(choice, out _));

            if (itemChoice == null ||
                !TryBuildInventoryPresenceChoiceInfo(itemChoice, out var info))
            {
                return false;
            }

            itemName = info.ItemName;
            slotLabel = info.SlotLabel;
            presenceLabel = info.PresenceLabel;
            return true;
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
                .Where(predicate => !IsRedundantUserVisibleAssumptionPredicateForNotes(predicate, null))
                .Where(predicate => !IsContradictedByUserVisibleAssumptions(predicate, null))
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

            if (result.Count == 0 && !HasNonNoOpDisplayLine(printedBeforeOutcome))
                result.Add(NoOpLine);
            else
                RemoveRedundantNoOpLines(result);

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
            return IsRenderableStructuralNode(group?.TreeRoot) ||
                   !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(group?.Label)) ||
                   !string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(group?.HeaderAnnotation));
        }

        private static void CollapseSingleSubvariantBranches(TopLevelVariantGroup group)
        {
            if (group?.TreeRoot == null)
                return;

            CollapseSingleSubvariantBranches(group.TreeRoot);
            PromoteCollapsedRootMetadataToTopLevelGroup(group);
        }

        private static void CollapseSingleSubvariantBranches(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                CollapseSingleSubvariantBranches(child);

            node.Children = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();

            while (TryGetSingleSubvariantChild(node, out var child))
                MergeSingleSubvariantChildIntoParent(node, child);
        }

        private static bool TryGetSingleSubvariantChild(
            VariantTreeNode node,
            out VariantTreeNode child)
        {
            child = null;
            if (node == null)
                return false;

            if ((node.DirectVariants ?? new List<VariantRenderItem>()).Any(item => item != null))
                return false;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            if (renderableChildren.Count != 1)
                return false;

            node.Children = renderableChildren;
            child = renderableChildren[0];
            return child != null;
        }

        private static void MergeSingleSubvariantChildIntoParent(
            VariantTreeNode parent,
            VariantTreeNode child)
        {
            if (parent == null || child == null)
                return;

            MergeCollapsedChildLabelIntoParent(parent, child.Label);
            parent.HeaderAnnotation = MergeHeaderAnnotations(parent.HeaderAnnotation, child.HeaderAnnotation);
            parent.HeaderGuardKey = MergeGuardConditionKeys(parent.HeaderGuardKey, child.HeaderGuardKey);

            if (!parent.RenderPriorityOverride.HasValue && child.RenderPriorityOverride.HasValue)
                parent.RenderPriorityOverride = child.RenderPriorityOverride;

            parent.PreserveTransparentWrapper = parent.PreserveTransparentWrapper || child.PreserveTransparentWrapper;
            parent.CommonLines ??= new List<string>();
            parent.CommonLines.AddRange((child.CommonLines ?? new List<string>())
                .Where(line => line != null));
            parent.DirectVariants = (child.DirectVariants ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            parent.Children = (child.Children ?? new List<VariantTreeNode>())
                .Where(grandChild => grandChild != null)
                .ToList();
        }

        private static void PromoteCollapsedRootMetadataToTopLevelGroup(TopLevelVariantGroup group)
        {
            var root = group?.TreeRoot;
            if (root == null)
                return;

            group.HeaderAnnotation = MergeHeaderAnnotations(group.HeaderAnnotation, root.HeaderAnnotation);
            group.HeaderGuardKey = MergeGuardConditionKeys(group.HeaderGuardKey, root.HeaderGuardKey);
            root.HeaderAnnotation = null;
            root.HeaderGuardKey = null;

            string rootLabel = NormalizeChoiceLabel(root.Label);
            if (!string.IsNullOrWhiteSpace(rootLabel))
            {
                string groupLabel = NormalizeChoiceLabel(group.Label);
                if (string.IsNullOrWhiteSpace(groupLabel))
                {
                    group.Label = rootLabel;
                }
                else if (!string.Equals(groupLabel, rootLabel, StringComparison.OrdinalIgnoreCase))
                {
                    MergeCollapsedLabelIntoTopLevelGroup(group, root, rootLabel);
                }
            }

            root.Label = null;
        }

        private static void MergeCollapsedChildLabelIntoParent(VariantTreeNode parent, string childLabel)
        {
            string normalizedChildLabel = NormalizeChoiceLabel(childLabel);
            if (parent == null || string.IsNullOrWhiteSpace(normalizedChildLabel))
                return;

            string normalizedParentLabel = NormalizeChoiceLabel(parent.Label);
            if (string.IsNullOrWhiteSpace(normalizedParentLabel))
            {
                parent.Label = normalizedChildLabel;
                return;
            }

            if (string.Equals(normalizedParentLabel, normalizedChildLabel, StringComparison.OrdinalIgnoreCase))
                return;

            string displayLabel = NormalizeUserVisibleChoiceLabel(normalizedChildLabel);
            if (string.IsNullOrWhiteSpace(displayLabel))
                return;

            if (IsFlatConditionChoiceLabel(displayLabel) || HasGuardHeaderAnnotationPrefix(displayLabel))
            {
                parent.HeaderAnnotation = MergeHeaderAnnotations(parent.HeaderAnnotation, displayLabel);
                return;
            }

            parent.CommonLines ??= new List<string>();
            if (!parent.CommonLines.Any(line => string.Equals(line?.Trim(), displayLabel, StringComparison.Ordinal)))
                parent.CommonLines.Add(displayLabel);
        }

        private static void MergeCollapsedLabelIntoTopLevelGroup(
            TopLevelVariantGroup group,
            VariantTreeNode root,
            string label)
        {
            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            if (string.IsNullOrWhiteSpace(displayLabel))
                return;

            if (IsFlatConditionChoiceLabel(displayLabel) || HasGuardHeaderAnnotationPrefix(displayLabel))
            {
                group.HeaderAnnotation = MergeHeaderAnnotations(group.HeaderAnnotation, displayLabel);
                return;
            }

            root.CommonLines ??= new List<string>();
            if (!root.CommonLines.Any(line => string.Equals(line?.Trim(), displayLabel, StringComparison.Ordinal)))
                root.CommonLines.Insert(0, displayLabel);
        }

        private static string MergeHeaderAnnotations(params string[] annotations)
        {
            var merged = new List<string>();
            foreach (string annotation in annotations ?? new string[0])
            {
                foreach (string part in SplitMergedHeaderAnnotation(annotation))
                {
                    string displayPart = NormalizeUserVisibleHeaderAnnotation(part);
                    if (string.IsNullOrWhiteSpace(displayPart))
                        continue;

                    if (!merged.Contains(displayPart, StringComparer.Ordinal))
                        merged.Add(displayPart);
                }
            }

            return merged.Count == 0
                ? null
                : string.Join("; ", merged);
        }

        private static string MergeGuardConditionKeys(params string[] guardKeys)
        {
            var merged = new List<string>();
            foreach (string guardKey in guardKeys ?? new string[0])
            {
                foreach (string part in SplitGuardConditionKey(guardKey))
                {
                    if (!merged.Contains(part, StringComparer.Ordinal))
                        merged.Add(part);
                }
            }

            return merged.Count == 0
                ? null
                : string.Join("&", merged.OrderBy(key => key, StringComparer.Ordinal));
        }

        private static IEnumerable<string> SplitGuardConditionKey(string guardKey)
        {
            if (string.IsNullOrWhiteSpace(guardKey))
                yield break;

            foreach (string part in guardKey.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = part?.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        private static IEnumerable<string> SplitMergedHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                yield break;

            foreach (string part in normalized.Split(';'))
            {
                string trimmed = part?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    yield return trimmed;
            }
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

        private static VariantRenderItem PreferItemWithVariant(
            VariantRenderItem preferred,
            VariantRenderItem fallback)
        {
            if (preferred == null)
                return fallback;

            if (preferred.Variant != null || fallback?.Variant == null)
                return preferred;

            return new VariantRenderItem
            {
                Variant = fallback.Variant,
                Lines = (preferred.Lines != null && preferred.Lines.Count > 0
                        ? preferred.Lines
                        : fallback.Lines)
                    ?.ToList() ?? new List<string>(),
                NarrativeLines = (preferred.NarrativeLines != null && preferred.NarrativeLines.Count > 0
                        ? preferred.NarrativeLines
                        : fallback.NarrativeLines)
                    ?.ToList() ?? new List<string>(),
                HeaderAnnotations = MergeVariantRenderItemHeaderAnnotations(preferred, fallback),
                SupplementalLines = MergeVariantRenderItemSupplementalLines(preferred, fallback),
                ConditionalComplementOutcomeEffectKeys =
                    new HashSet<string>(
                        preferred.ConditionalComplementOutcomeEffectKeys ??
                        fallback.ConditionalComplementOutcomeEffectKeys ??
                        new HashSet<string>(StringComparer.Ordinal),
                        StringComparer.Ordinal)
            };
        }

        private static List<string> MergeVariantRenderItemHeaderAnnotations(
            params VariantRenderItem[] items)
        {
            var annotations = new List<string>();
            foreach (var item in items ?? new VariantRenderItem[0])
                AddDistinctHeaderAnnotations(annotations, item?.HeaderAnnotations);

            return annotations;
        }

        private static List<string> MergeVariantRenderItemSupplementalLines(
            params VariantRenderItem[] items)
        {
            var lines = new List<string>();
            foreach (var item in items ?? new VariantRenderItem[0])
                AddDistinctSupplementalLines(lines, item?.SupplementalLines);

            return lines;
        }

        private static void AddDistinctSupplementalLines(
            List<string> target,
            IEnumerable<string> lines)
        {
            if (target == null || lines == null)
                return;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!target.Contains(line, StringComparer.Ordinal))
                    target.Add(line);
            }
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

            if (item.SupplementalLines != null &&
                item.SupplementalLines.Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                return true;
            }

            if (item.Variant?.HasProbabilityInfo == true)
                return true;

            if (ShouldDisplayGuardCondition(item.Variant))
                return true;

            if (item.HeaderAnnotations != null &&
                item.HeaderAnnotations.Any(annotation =>
                    !string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(annotation))))
            {
                return true;
            }

            return false;
        }

        private static bool IsEmptyOrNoOpVariant(VariantRenderItem item)
        {
            if (item == null)
                return false;

            if (item.SupplementalLines != null &&
                item.SupplementalLines.Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                return false;
            }

            return item.Lines == null || item.Lines.Count == 0 || IsNoOpOnly(item.Lines);
        }

        private static bool IsStatusOnlyNoBattleVariant(VariantRenderItem item)
        {
            if (item == null || VariantItemHasBattleLaunchOutcome(item))
                return false;

            var lines = (item.Lines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return lines.Count > 0 &&
                   lines.All(IsPossibleConditionalPartyStatusLine);
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

        private static bool IsUserVisibleNoOpOnly(IEnumerable<string> lines)
        {
            return !string.IsNullOrWhiteSpace(BuildUserVisibleNoOpLineKey(lines));
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

            string firstDisplayLine = GetFirstMeaningfulDisplayLine(item.Lines);
            return !string.IsNullOrWhiteSpace(firstDisplayLine)
                ? "LINE|" + firstDisplayLine
                : "LINE|" + string.Join("\n---\n", item.Lines.Take(1));
        }

        private static string GetNarrativeRootLine(VariantRenderItem item)
        {
            if (item?.NarrativeLines == null)
                return null;

            return GetFirstMeaningfulDisplayLine(item.NarrativeLines);
        }

        private static string GetFirstMeaningfulDisplayLine(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .SelectMany(SplitDisplayLines)
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
                            HeaderAnnotation = NormalizeHeaderAnnotation(choice?.DisplayHeaderAnnotation),
                            HeaderGuardKey = BuildBranchGuardConditionKey(choice)
                        };
                        current.Children.Add(child);
                    }
                    else if (string.IsNullOrWhiteSpace(child.Label))
                    {
                        child.Label = NormalizeChoiceLabel(choice?.Label);
                    }

                    if (string.IsNullOrWhiteSpace(child.HeaderAnnotation))
                        child.HeaderAnnotation = NormalizeHeaderAnnotation(choice?.DisplayHeaderAnnotation);
                    if (string.IsNullOrWhiteSpace(child.HeaderGuardKey))
                        child.HeaderGuardKey = BuildBranchGuardConditionKey(choice);

                    current = child;
                }

                current.DirectVariants.Add(item);
            }

            return root;
        }

        private static void IntroduceGuardedNoOpComplementOutcomes(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                IntroduceGuardedNoOpComplementOutcomes(child);

            if (!IsRandomTechnicalChoiceNode(node) ||
                (((node.Children?.Count ?? 0) < 2) &&
                 ((node.DirectVariants?.Count ?? 0) < 2)))
            {
                return;
            }

            bool injectedAny = false;

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();
            if (children.Count >= 2)
            {
                var noOpChildren = children
                    .Where(IsGuardedNoOpComplementSourceNode)
                    .ToList();

                foreach (var outcomeChild in children.Where(child => !noOpChildren.Contains(child)))
                {
                    if (TryInjectGuardedNoOpComplementLeaves(outcomeChild))
                        injectedAny = true;
                }

                if (injectedAny)
                {
                    node.Children = children
                        .Where(child => !noOpChildren.Contains(child))
                        .ToList();
                }
            }

            TryInjectGuardedNoOpComplementDirectVariants(node);
        }

        private static bool IsRandomTechnicalChoiceNode(VariantTreeNode node)
        {
            string label = NormalizeChoiceLabel(node?.Label);
            if (string.IsNullOrWhiteSpace(label) || !IsTechnicalChoiceLabel(label))
                return false;

            return Regex.IsMatch(
                label,
                @"^RND(?:\(\d+\))?\s*=",
                RegexOptions.IgnoreCase);
        }

        private static bool IsGuardedNoOpComplementSourceNode(VariantTreeNode node)
        {
            if (node == null)
                return false;

            return TryBuildNoOpOnlyRenderNodeKey(node, out string key) &&
                   !string.IsNullOrWhiteSpace(key) &&
                   GetAllVariants(node).Any(item => item?.Variant != null);
        }

        private static bool IsGuardedNoOpComplementSourceItem(VariantRenderItem item)
        {
            return item?.Variant != null &&
                   !string.IsNullOrWhiteSpace(BuildUserVisibleNoOpRenderKey(item));
        }

        private static bool IsGuardedNoOpComplementSourceItem(
            VariantRenderItem item,
            IReadOnlyList<string> sharedPrefix)
        {
            if (IsGuardedNoOpComplementSourceItem(item))
                return true;

            if (item?.Variant == null || sharedPrefix == null || sharedPrefix.Count == 0)
                return false;

            var localLines = RemoveLeadingLineSequence(item.Lines, sharedPrefix);
            return !string.IsNullOrWhiteSpace(BuildUserVisibleNoOpLineKey(localLines));
        }

        private static List<string> RemoveLeadingLineSequence(
            IReadOnlyList<string> lines,
            IReadOnlyList<string> prefix)
        {
            var source = lines?.Where(line => line != null).ToList() ?? new List<string>();
            if (prefix == null || prefix.Count == 0 || !StartsWithLineSequence(source, prefix))
                return source;

            return source.Skip(prefix.Count).ToList();
        }

        private static bool TryInjectGuardedNoOpComplementDirectVariants(VariantTreeNode node)
        {
            if (node == null)
                return false;

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (directVariants.Count < 2)
                return false;

            var sharedPrefix = CommonPrefix(directVariants
                .Select(item => item.Lines ?? new List<string>())
                .ToList());
            var noOpSources = directVariants
                .Where(item => IsGuardedNoOpComplementSourceItem(item, sharedPrefix))
                .ToList();
            if (noOpSources.Count == 0)
                return false;

            var outcomeItems = directVariants
                .Where(item => !noOpSources.Contains(item) &&
                               GetGuardedNoOpComplementEffectKeys(item).Count > 0)
                .ToList();
            if (outcomeItems.Count < 2)
                return false;

            var syntheticComplements = new List<VariantRenderItem>();
            foreach (var item in outcomeItems)
            {
                var effectKeys = GetGuardedNoOpComplementEffectKeys(item);
                if (effectKeys.Count == 0)
                    continue;

                var existingItems = directVariants
                    .Concat(syntheticComplements)
                    .ToList();
                if (HasExistingComplementWithoutEffects(existingItems, item, effectKeys))
                    continue;

                var synthetic = BuildGuardedNoOpComplementRenderItem(item, effectKeys);
                if (synthetic == null)
                    continue;

                item.ConditionalComplementOutcomeEffectKeys ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in effectKeys)
                    item.ConditionalComplementOutcomeEffectKeys.Add(key);

                syntheticComplements.Add(synthetic);
            }

            if (syntheticComplements.Count == 0)
                return false;

            node.DirectVariants = directVariants
                .Where(item => !noOpSources.Contains(item))
                .Concat(syntheticComplements)
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();
            return true;
        }

        private static bool TryInjectGuardedNoOpComplementLeaves(VariantTreeNode outcomeNode)
        {
            if (outcomeNode == null)
                return false;

            var candidateItems = GetAllVariants(outcomeNode)
                .Where(item => GetGuardedNoOpComplementEffectKeys(item).Count > 0)
                .ToList();
            if (candidateItems.Count < 2)
                return false;

            return InjectGuardedNoOpComplementLeaves(outcomeNode);
        }

        private static bool InjectGuardedNoOpComplementLeaves(VariantTreeNode node)
        {
            if (node == null)
                return false;

            bool changed = false;
            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (InjectGuardedNoOpComplementLeaves(child))
                    changed = true;
            }

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (directVariants.Count == 0)
                return changed;

            var syntheticComplements = new List<VariantRenderItem>();
            foreach (var item in directVariants)
            {
                var effectKeys = GetGuardedNoOpComplementEffectKeys(item);
                if (effectKeys.Count == 0)
                    continue;

                if (HasExistingComplementWithoutEffects(directVariants, item, effectKeys))
                    continue;

                var synthetic = BuildGuardedNoOpComplementRenderItem(item, effectKeys);
                if (synthetic == null)
                    continue;

                item.ConditionalComplementOutcomeEffectKeys ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in effectKeys)
                    item.ConditionalComplementOutcomeEffectKeys.Add(key);

                syntheticComplements.Add(synthetic);
            }

            if (syntheticComplements.Count == 0)
                return changed;

            node.DirectVariants = directVariants
                .Concat(syntheticComplements)
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();
            return true;
        }

        private static HashSet<string> GetGuardedNoOpComplementEffectKeys(VariantRenderItem item)
        {
            return (item?.Variant?.PartyEffects ?? new List<PartyEffect>())
                .Where(IsGuardedNoOpComplementCandidateEffect)
                .Select(PartyEffectSemantics.BuildSemanticKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsGuardedNoOpComplementCandidateEffect(PartyEffect effect)
        {
            if (effect == null ||
                !PartyEffectSemantics.IsStateChanging(effect) ||
                !PartyEffectSemantics.IsLoopDerived(effect))
            {
                return false;
            }

            var field = PartyEffectSemantics.GetEffectiveField(effect);
            if (field != PartyFieldKind.Hp &&
                field != PartyFieldKind.Sp)
            {
                return false;
            }

            return PartyEffectSemantics.HasEffectiveGuardPredicates(effect) ||
                   PartyEffectSemantics.GetEffectiveCondition(effect) != PartyConditionKind.None;
        }

        private static bool HasExistingComplementWithoutEffects(
            List<VariantRenderItem> directVariants,
            VariantRenderItem source,
            HashSet<string> effectKeys)
        {
            if (directVariants == null || source == null || effectKeys == null || effectKeys.Count == 0)
                return false;

            string sourceBaseKey = BuildGuardedNoOpComplementBaseLineKey(source, effectKeys);
            if (string.IsNullOrWhiteSpace(sourceBaseKey))
                return false;

            return directVariants
                .Where(item => item != null && !ReferenceEquals(item, source))
                .Any(item =>
                    !VariantHasAnyEffectKey(item, effectKeys) &&
                    string.Equals(
                        BuildGuardedNoOpComplementBaseLineKey(item, effectKeys),
                        sourceBaseKey,
                        StringComparison.Ordinal));
        }

        private static bool VariantHasAnyEffectKey(VariantRenderItem item, HashSet<string> effectKeys)
        {
            if (item?.Variant?.PartyEffects == null || effectKeys == null || effectKeys.Count == 0)
                return false;

            return item.Variant.PartyEffects
                .Where(effect => effect != null)
                .Select(PartyEffectSemantics.BuildSemanticKey)
                .Any(effectKeys.Contains);
        }

        private static string BuildGuardedNoOpComplementBaseLineKey(
            VariantRenderItem item,
            HashSet<string> effectKeys)
        {
            var lines = RemoveGuardedNoOpComplementEffectLines(item, effectKeys);
            return BuildCommonLinesKey(lines);
        }

        private static VariantRenderItem BuildGuardedNoOpComplementRenderItem(
            VariantRenderItem source,
            HashSet<string> effectKeys)
        {
            if (source?.Variant == null || effectKeys == null || effectKeys.Count == 0)
                return null;

            var clone = ClonePathVariantForRender(source.Variant);
            if (clone == null)
                return null;

            clone.PartyEffects = clone.PartyEffects?
                .Where(effect => effect != null &&
                                 !effectKeys.Contains(PartyEffectSemantics.BuildSemanticKey(effect)))
                .Select(effect => effect.Clone())
                .ToList() ?? new List<PartyEffect>();

            return new VariantRenderItem
            {
                Variant = clone,
                Lines = RemoveGuardedNoOpComplementEffectLines(source, effectKeys),
                NarrativeLines = source.NarrativeLines?.ToList() ?? new List<string>(),
                HeaderAnnotations = source.HeaderAnnotations?.ToList() ?? new List<string>(),
                SupplementalLines = source.SupplementalLines?.ToList() ?? new List<string>(),
                ConditionalComplementOutcomeEffectKeys = new HashSet<string>(StringComparer.Ordinal)
            };
        }

        private static List<string> RemoveGuardedNoOpComplementEffectLines(
            VariantRenderItem item,
            HashSet<string> effectKeys)
        {
            if (item == null || effectKeys == null || effectKeys.Count == 0)
                return item?.Lines?.Where(line => line != null).ToList() ?? new List<string>();

            var linesToRemove = GetVariantPartyEffectLines(
                item,
                effect => effect != null &&
                          effectKeys.Contains(PartyEffectSemantics.BuildSemanticKey(effect)));
            return RemoveLineOccurrences(item.Lines, linesToRemove);
        }

        private static void PromoteBinaryStateConditionBranches(VariantTreeNode node)
        {
            if (node == null)
                return;

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();

            if (children.Count >= 2)
            {
                var conditionBranches = children
                    .Select(child => TryBuildStateConditionBranchInfo(child, out var info) ? info : null)
                    .Where(info => info != null)
                    .ToList();

                var binaryGroups = conditionBranches
                    .GroupBy(info => info.Address)
                    .Where(group =>
                        group.Any(info => info.Polarity == StateConditionPolarity.Zero) &&
                        group.Any(info => info.Polarity == StateConditionPolarity.NonZero));

                foreach (var group in binaryGroups)
                {
                    foreach (var info in group)
                    {
                        if (TryBuildUserVisibleStateConditionAnnotation(
                                info.Address,
                                info.Polarity,
                                out string annotation))
                        {
                            info.Node.HeaderAnnotation = annotation;
                        }
                    }
                }
            }

            foreach (var child in children)
                PromoteBinaryStateConditionBranches(child);
        }

        private static bool TryBuildStateConditionBranchInfo(
            VariantTreeNode node,
            out StateConditionBranchInfo info)
        {
            info = null;
            if (node == null || HasUserVisibleNodeLabelOrAnnotation(node))
                return false;

            if (!TryParseBinaryStateConditionAnnotation(
                    node.HeaderAnnotation,
                    out ushort address,
                    out StateConditionPolarity polarity))
            {
                return false;
            }

            info = new StateConditionBranchInfo
            {
                Node = node,
                Address = address,
                Polarity = polarity
            };
            return true;
        }

        private static bool TryBuildUserVisibleStateConditionAnnotation(
            ushort address,
            StateConditionPolarity polarity,
            out string annotation)
        {
            annotation = null;
            if (!KnownBinaryStateConditionLabels.TryGetValue(address, out var labels) ||
                labels == null ||
                labels.Length < 2)
            {
                return false;
            }

            annotation = polarity == StateConditionPolarity.Zero
                ? labels[0]
                : labels[1];
            return !string.IsNullOrWhiteSpace(annotation);
        }

        private static bool TryParseBinaryStateConditionAnnotation(
            string annotation,
            out ushort address,
            out StateConditionPolarity polarity)
        {
            address = 0;
            polarity = StateConditionPolarity.Zero;

            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var match = Regex.Match(
                StripGuardHeaderAnnotationPrefix(normalized),
                @"^\[0x([0-9A-Fa-f]{1,4})\]\s*(>=|<=|!=|=|>|<)\s*(0x[0-9A-Fa-f]{1,2}|\d+)\s*$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            if (!ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
                return false;

            if (!TryParseStateConditionValue(match.Groups[3].Value, out byte value))
                return false;

            string op = match.Groups[2].Value;
            if ((op == "=" && value == 0) ||
                (op == "<" && value == 1) ||
                (op == "<=" && value == 0))
            {
                polarity = StateConditionPolarity.Zero;
                return true;
            }

            if ((op == "!=" && value == 0) ||
                (op == ">" && value == 0) ||
                (op == ">=" && value == 1))
            {
                polarity = StateConditionPolarity.NonZero;
                return true;
            }

            return false;
        }

        private static bool TryParseStateConditionValue(string text, out byte value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return byte.TryParse(
                    trimmed.Substring(2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value);
            }

            return byte.TryParse(
                trimmed,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
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
                    out string slotLabel,
                    out string observedPresenceLabel))
            {
                return;
            }

            if (!TryGetOppositeInventoryPresenceLabel(observedPresenceLabel, out string oppositePresenceLabel))
                return;

            var observedOutcomeFamilyKeys = BuildInventoryPresenceChildOutcomeFamilyKeys(
                node,
                itemName,
                slotLabel,
                observedPresenceLabel);

            var directVariantsToMove = node.DirectVariants
                .Where(item => item != null &&
                               !VariantHasInventoryPresenceChoiceForItem(item.Variant, itemName, slotLabel) &&
                               ShouldMoveToInventoryComplement(item, observedOutcomeFamilyKeys))
                .ToList();

            if (directVariantsToMove.Count == 0)
                return;

            string inferredHeaderAnnotation = BuildInventoryPresenceHeaderAnnotation(itemName, oppositePresenceLabel, slotLabel);
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
                    SegmentKey = $"INVENTORY_COMPLEMENT|{slotLabel}|{itemName}|{oppositePresenceLabel}",
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

        private static bool ShouldMoveToInventoryComplement(
            VariantRenderItem item,
            HashSet<string> observedOutcomeFamilyKeys)
        {
            if (observedOutcomeFamilyKeys == null || observedOutcomeFamilyKeys.Count == 0)
                return true;

            string itemOutcomeFamilyKey = BuildInventoryComplementOutcomeFamilyKey(item);
            return string.IsNullOrWhiteSpace(itemOutcomeFamilyKey) ||
                   observedOutcomeFamilyKeys.Contains(itemOutcomeFamilyKey);
        }

        private static HashSet<string> BuildInventoryPresenceChildOutcomeFamilyKeys(
            VariantTreeNode node,
            string itemName,
            string slotLabel,
            string presenceLabel)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (node?.Children == null || string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(presenceLabel))
                return result;

            foreach (var child in node.Children)
            {
                if (!TrySplitInventoryPresenceHeaderAnnotation(
                        child?.HeaderAnnotation,
                        out string childItemName,
                        out string childPresenceLabel,
                        out string childSlotLabel))
                {
                    continue;
                }

                if (!string.Equals(childItemName, itemName, StringComparison.Ordinal) ||
                    !string.Equals(childSlotLabel ?? string.Empty, slotLabel ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(childPresenceLabel, presenceLabel, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var item in GetAllVariants(child))
                {
                    string key = BuildInventoryComplementOutcomeFamilyKey(item);
                    if (!string.IsNullOrWhiteSpace(key))
                        result.Add(key);
                }
            }

            return result;
        }

        private static bool TryFindSingleInventoryPresenceChildSide(
            VariantTreeNode node,
            out string itemName,
            out string slotLabel,
            out string presenceLabel)
        {
            itemName = null;
            slotLabel = null;
            presenceLabel = null;

            var candidates = (node?.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .Select(child =>
                    TrySplitInventoryPresenceHeaderAnnotation(
                        child?.HeaderAnnotation,
                        out string childItemName,
                        out string childPresenceLabel,
                        out string childSlotLabel)
                        ? new { ItemName = childItemName, SlotLabel = childSlotLabel, PresenceLabel = childPresenceLabel }
                        : null)
                .Where(candidate => candidate != null)
                .GroupBy(candidate => BuildInventoryPresenceChoiceKey(candidate.ItemName, candidate.SlotLabel), StringComparer.Ordinal)
                .Select(group =>
                {
                    var presenceLabels = group
                        .Select(candidate => candidate.PresenceLabel)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    return new
                    {
                        ItemName = group.First().ItemName,
                        SlotLabel = group.First().SlotLabel,
                        PresenceLabels = presenceLabels
                    };
                })
                .Where(candidate => candidate.PresenceLabels.Count == 1)
                .ToList();

            if (candidates.Count != 1)
                return false;

            itemName = candidates[0].ItemName;
            slotLabel = candidates[0].SlotLabel;
            presenceLabel = candidates[0].PresenceLabels[0];
            return !string.IsNullOrWhiteSpace(itemName) &&
                   !string.IsNullOrWhiteSpace(presenceLabel);
        }

        private static bool VariantHasInventoryPresenceChoiceForItem(
            PathVariantInfo variant,
            string itemName,
            string slotLabel)
        {
            if (variant == null || string.IsNullOrWhiteSpace(itemName))
                return false;

            return GetInventoryPresenceChoiceInfos(variant)
                .Any(info =>
                    string.Equals(info.ItemName, itemName, StringComparison.Ordinal) &&
                    string.Equals(info.SlotLabel ?? string.Empty, slotLabel ?? string.Empty, StringComparison.Ordinal));
        }

        private static string BuildInventoryComplementOutcomeFamilyKey(PathVariantInfo variant)
        {
            return BuildInventoryComplementOutcomeFamilyKey(DecodeNoteTexts(variant?.Texts));
        }

        private static string BuildInventoryComplementOutcomeFamilyKey(VariantRenderItem item)
        {
            return BuildInventoryComplementOutcomeFamilyKey(item?.Lines);
        }

        private static string BuildInventoryComplementOutcomeFamilyKey(IEnumerable<string> lines)
        {
            foreach (var line in (lines ?? Enumerable.Empty<string>()).SelectMany(SplitDisplayLines))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (IsCorrectPartyScanAnswerLine(line))
                    return "answer:correct";

                if (IsWrongPartyScanAnswerLine(line))
                    return "answer:wrong";
            }

            return null;
        }

        private static IEnumerable<BranchChoice> GetRelevantBranchChoices(
            PathVariantInfo variant,
            byte? suppressedPermanentStatRaiseGuardMask = null,
            IReadOnlySet<string> suppressedLoopGuardPredicateKeys = null)
        {
            if (variant?.BranchChoices == null)
                yield break;

            if (!suppressedPermanentStatRaiseGuardMask.HasValue &&
                (suppressedLoopGuardPredicateKeys == null || suppressedLoopGuardPredicateKeys.Count == 0))
            {
                foreach (var choice in GetCachedRelevantBranchChoices(variant))
                    yield return choice;

                yield break;
            }

            foreach (var choice in BuildRelevantBranchChoices(
                variant,
                suppressedPermanentStatRaiseGuardMask,
                suppressedLoopGuardPredicateKeys))
            {
                yield return choice;
            }
        }

        private static List<BranchChoice> GetCachedRelevantBranchChoices(PathVariantInfo variant)
        {
            lock (variant)
            {
                if (!variant.OvrNotesRelevantBranchChoicesComputed)
                {
                    variant.OvrNotesRelevantBranchChoices = BuildRelevantBranchChoices(
                        variant,
                        null,
                        null);
                    variant.OvrNotesRelevantBranchChoicesComputed = true;
                }

                return variant.OvrNotesRelevantBranchChoices?.ToList() ?? new List<BranchChoice>();
            }
        }

        private static List<BranchChoice> BuildRelevantBranchChoices(
            PathVariantInfo variant,
            byte? suppressedPermanentStatRaiseGuardMask,
            IReadOnlySet<string> suppressedLoopGuardPredicateKeys)
        {
            if (variant?.BranchChoices == null)
                return new List<BranchChoice>();

            var candidates = new List<BranchChoiceDisplayCandidate>();
            foreach (var choice in variant.BranchChoices)
            {
                if (IsPartyLoopGuardBranchChoice(variant, choice))
                    continue;

                if (suppressedPermanentStatRaiseGuardMask.HasValue &&
                    IsPermanentStatRaiseGuardBranchChoice(
                        choice,
                        suppressedPermanentStatRaiseGuardMask.Value))
                {
                    continue;
                }

                if (suppressedLoopGuardPredicateKeys?.Count > 0 &&
                    IsSuppressedLoopGuardBranchChoice(choice, suppressedLoopGuardPredicateKeys))
                {
                    continue;
                }

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
            var collapsedCandidates = CollapseFlagPropagatedInputChoiceDuplicates(candidates);
            var displayCandidates = SuppressMainQuestCompletionThresholdChoicesShadowedByTransferReadyFlag(
                collapsedCandidates);
            var result = new List<BranchChoice>();
            foreach (var candidate in SuppressInventorySlotChoicesShadowedByRange(displayCandidates))
            {
                if (candidate?.Normalized == null)
                    continue;

                if (TryBuildInventoryPresenceChoiceInfo(candidate.Normalized, out var itemPresenceInfo) &&
                    !seenInventoryPresenceItems.Add(BuildInventoryPresenceChoiceKey(itemPresenceInfo)))
                {
                    continue;
                }

                result.Add(candidate.Normalized);
            }

            return result;
        }

        private static List<BranchChoiceDisplayCandidate> SuppressMainQuestCompletionThresholdChoicesShadowedByTransferReadyFlag(
            List<BranchChoiceDisplayCandidate> choices)
        {
            var source = choices ?? new List<BranchChoiceDisplayCandidate>();
            bool hasTransferReadyFlagChoice = source.Any(candidate =>
                IsMainQuestPredicate(candidate?.Normalized?.GuardPredicate, 0x40));
            if (!hasTransferReadyFlagChoice)
                return source;

            return source
                .Where(candidate => !IsMainQuestPredicate(
                    candidate?.Normalized?.GuardPredicate,
                    PartyTechnicalFieldSemantics.MainQuestCompletedThreshold))
                .ToList();
        }

        private static bool IsMainQuestPredicate(PartyPredicate predicate, byte immediateValue)
        {
            return predicate?.Field == PartyFieldKind.Technical7D &&
                   predicate.ImmediateValue.HasValue &&
                   predicate.ImmediateValue.Value == immediateValue;
        }

        private static bool IsPermanentStatRaiseGuardBranchChoice(
            BranchChoice choice,
            byte raiseFlagMask)
        {
            if (choice?.GuardPredicate == null || raiseFlagMask == 0)
                return false;

            var predicate = choice.GetGuardPredicateForDisplay() ?? choice.GuardPredicate;
            return predicate?.Comparison == PartyPredicateComparison.Equal &&
                   TryGetSinglePermanentStatRaiseMask(predicate, out byte mask) &&
                   mask == raiseFlagMask;
        }

        private static bool IsSuppressedLoopGuardBranchChoice(
            BranchChoice choice,
            IReadOnlySet<string> suppressedLoopGuardPredicateKeys)
        {
            if (choice?.GuardPredicate == null ||
                suppressedLoopGuardPredicateKeys == null ||
                suppressedLoopGuardPredicateKeys.Count == 0)
            {
                return false;
            }

            string displayKey = BuildLoopNormalizedPredicateKey(choice.GetGuardPredicateForDisplay());
            string rawKey = BuildLoopNormalizedPredicateKey(choice.GuardPredicate);
            return (!string.IsNullOrWhiteSpace(displayKey) &&
                    suppressedLoopGuardPredicateKeys.Contains(displayKey)) ||
                   (!string.IsNullOrWhiteSpace(rawKey) &&
                    suppressedLoopGuardPredicateKeys.Contains(rawKey));
        }

        private static bool IsPartyLoopGuardBranchChoice(PathVariantInfo variant, BranchChoice choice)
        {
            if (choice?.GuardPredicate == null ||
                variant?.PartyEffects == null)
            {
                return false;
            }

            var loopGuardPredicateKeys = GetPartyLoopGuardPredicateKeys(variant);
            if (loopGuardPredicateKeys.Count == 0)
                return false;

            string choicePredicateKey = BuildLoopNormalizedPredicateKey(choice.GetGuardPredicateForDisplay());
            string rawChoicePredicateKey = BuildLoopNormalizedPredicateKey(choice.GuardPredicate);
            return (!string.IsNullOrWhiteSpace(choicePredicateKey) &&
                    loopGuardPredicateKeys.Contains(choicePredicateKey)) ||
                   (!string.IsNullOrWhiteSpace(rawChoicePredicateKey) &&
                    loopGuardPredicateKeys.Contains(rawChoicePredicateKey));
        }

        private static HashSet<string> GetPartyLoopGuardPredicateKeys(PathVariantInfo variant)
        {
            if (variant == null)
                return new HashSet<string>(StringComparer.Ordinal);

            lock (variant)
            {
                if (variant.OvrNotesPartyLoopGuardPredicateKeysComputed)
                {
                    return variant.OvrNotesPartyLoopGuardPredicateKeys
                        ?? new HashSet<string>(StringComparer.Ordinal);
                }

                var keys = BuildPartyLoopGuardPredicateKeys(variant);
                variant.OvrNotesPartyLoopGuardPredicateKeys = keys;
                variant.OvrNotesPartyLoopGuardPredicateKeysComputed = true;
                return keys;
            }
        }

        private static HashSet<string> BuildPartyLoopGuardPredicateKeys(PathVariantInfo variant)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (variant?.PartyEffects == null)
                return result;

            var loopGuardEffects = variant.PartyEffects
                .Where(IsSexWriteLoopSubsetOutcomeEffect)
                .ToList();
            if (TryBuildPermanentStatRaiseVariantInfo(variant, out var permanentStatRaiseInfo))
            {
                loopGuardEffects.AddRange(variant.PartyEffects
                    .Where(effect => IsPermanentStatRaiseLoopSubsetOutcomeEffect(
                        effect,
                        permanentStatRaiseInfo)));
            }
            if (TryBuildNarrativeCoveredConditionalStatRewardInfo(variant, out var statRewardInfo))
            {
                loopGuardEffects.AddRange(variant.PartyEffects
                    .Where(effect =>
                        effect != null &&
                        statRewardInfo.EffectKeys.Contains(PartyEffectSemantics.BuildSemanticKey(effect))));
            }

            if (loopGuardEffects.Count == 0)
                return result;

            foreach (string key in loopGuardEffects
                .SelectMany(effect => PartyEffectSemantics.GetEffectiveGuardPredicates(effect))
                .Select(BuildLoopNormalizedPredicateKey)
                .Where(key => !string.IsNullOrWhiteSpace(key)))
            {
                result.Add(key);
            }

            return result;
        }

        private static bool IsPermanentStatRaiseLoopSubsetOutcomeEffect(
            PartyEffect effect,
            PermanentStatRaiseCollapseInfo info)
        {
            return IsConditionalLoopSubsetOutcomeEffect(effect) &&
                   IsPermanentStatRaiseOutcomeEffect(effect, info);
        }

        private static bool IsSexWriteLoopSubsetOutcomeEffect(PartyEffect effect)
        {
            return IsConditionalLoopSubsetOutcomeEffect(effect) &&
                   PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.sex &&
                   PartyEffectSemantics.GetEffectiveOperation(effect) == PartyEffectOperation.Write;
        }

        private static List<BranchChoiceDisplayCandidate> SuppressInventorySlotChoicesShadowedByRange(
            List<BranchChoiceDisplayCandidate> choices)
        {
            var source = choices ?? new List<BranchChoiceDisplayCandidate>();
            var rangeInfos = source
                .Select(candidate => TryBuildInventoryPresenceChoiceShadowInfo(candidate?.Normalized, out var info) ? info : null)
                .Where(info => info != null &&
                               info.FieldOffsetRange != null &&
                               !info.FieldOffsetRange.IsExact &&
                               PartyInventorySemantics.IsInventorySlotRange(info.FieldOffsetRange))
                .ToList();

            if (rangeInfos.Count == 0)
                return source;

            var result = new List<BranchChoiceDisplayCandidate>();
            foreach (var candidate in source)
            {
                if (TryBuildInventoryPresenceChoiceShadowInfo(candidate?.Normalized, out var slotInfo) &&
                    slotInfo.FieldOffset.HasValue &&
                    rangeInfos.Any(rangeInfo =>
                        string.Equals(rangeInfo.ItemName, slotInfo.ItemName, StringComparison.OrdinalIgnoreCase) &&
                        SlotOffsetIsInsideRange(slotInfo.FieldOffset.Value, rangeInfo.FieldOffsetRange)))
                {
                    continue;
                }

                result.Add(candidate);
            }

            return result;
        }

        private static bool TryBuildInventoryPresenceChoiceShadowInfo(
            BranchChoice choice,
            out InventoryPresenceChoiceShadowInfo info)
        {
            info = null;
            if (!TryBuildInventoryPresenceChoiceInfo(choice, out var presenceInfo) ||
                string.IsNullOrWhiteSpace(presenceInfo?.ItemName))
            {
                return false;
            }

            var fieldRef = choice?.ComparedPartyField;
            if (fieldRef == null)
                return false;

            var range = fieldRef.FieldOffsetRange;
            byte? exactOffset = null;
            if (range == null)
            {
                exactOffset = fieldRef.FieldOffset;
            }
            else if (range.IsExact)
            {
                exactOffset = range.Min;
            }

            info = new InventoryPresenceChoiceShadowInfo
            {
                ItemName = presenceInfo.ItemName.Trim(),
                FieldOffset = exactOffset,
                FieldOffsetRange = range
            };
            return true;
        }

        private static bool SlotOffsetIsInsideRange(byte offset, ValueRange8 range)
        {
            return range != null &&
                   offset >= range.Min &&
                   offset <= range.Max;
        }

        private static List<InventoryPresenceChoiceInfo> GetInventoryPresenceChoiceInfos(PathVariantInfo variant)
        {
            return GetRelevantBranchChoices(variant)
                .Select(choice => TryBuildInventoryPresenceChoiceInfo(choice, out var info) ? info : null)
                .Where(info => info != null)
                .GroupBy(info => info.HeaderAnnotation, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(info => info.SlotLabel ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(info => info.ItemName, StringComparer.Ordinal)
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
            string slotLabel = PartyInventorySemantics.GetSlotFieldLabel(choice?.ComparedPartyField)?.Trim();
            presenceLabel = presenceLabel?.Trim();
            if (TrySplitInventoryPresenceHeaderAnnotation(
                    choice?.DisplayHeaderAnnotation,
                    out string headerItemName,
                    out string headerPresenceLabel,
                    out string headerSlotLabel) &&
                string.Equals(
                    headerItemName?.Trim(),
                    itemName,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(headerSlotLabel) ||
                 string.Equals(
                     headerSlotLabel.Trim(),
                     slotLabel,
                     StringComparison.OrdinalIgnoreCase)))
            {
                presenceLabel = headerPresenceLabel?.Trim();
                if (!string.IsNullOrWhiteSpace(headerSlotLabel))
                    slotLabel = headerSlotLabel.Trim();
            }

            if (string.IsNullOrWhiteSpace(itemName) ||
                string.IsNullOrWhiteSpace(presenceLabel))
            {
                return false;
            }

            info = new InventoryPresenceChoiceInfo
            {
                ItemName = itemName,
                SlotLabel = slotLabel,
                PresenceLabel = presenceLabel,
                HeaderAnnotation = BuildInventoryPresenceHeaderAnnotation(itemName, presenceLabel, slotLabel)
            };
            return true;
        }

        private static string BuildInventoryPresenceHeaderAnnotation(
            string itemName,
            string presenceLabel,
            string slotLabel)
        {
            string normalizedItemName = itemName?.Trim();
            string normalizedPresenceLabel = presenceLabel?.Trim();
            string normalizedSlotLabel = slotLabel?.Trim();

            if (string.IsNullOrWhiteSpace(normalizedItemName) ||
                string.IsNullOrWhiteSpace(normalizedPresenceLabel))
            {
                return null;
            }

            string annotation = string.IsNullOrWhiteSpace(normalizedSlotLabel)
                ? $"{normalizedItemName} {normalizedPresenceLabel}"
                : $"{normalizedSlotLabel}: {normalizedItemName} {normalizedPresenceLabel}";

            return NormalizeHeaderAnnotation(annotation);
        }

        private static string BuildInventoryPresenceChoiceKey(InventoryPresenceChoiceInfo info)
        {
            return info == null
                ? "<NULL_INVENTORY_PRESENCE>"
                : BuildInventoryPresenceChoiceKey(info.ItemName, info.SlotLabel);
        }

        private static string BuildInventoryPresenceChoiceKey(string itemName, string slotLabel)
        {
            return string.Join("|",
                itemName?.Trim() ?? string.Empty,
                slotLabel?.Trim() ?? string.Empty);
        }

        private static bool TrySplitInventoryPresenceHeaderAnnotation(
            string annotation,
            out string itemName,
            out string presenceLabel)
        {
            return TrySplitInventoryPresenceHeaderAnnotation(
                annotation,
                out itemName,
                out presenceLabel,
                out _);
        }

        private static bool TrySplitInventoryPresenceHeaderAnnotation(
            string annotation,
            out string itemName,
            out string presenceLabel,
            out string slotLabel)
        {
            itemName = null;
            presenceLabel = null;
            slotLabel = null;

            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            foreach (string label in new[] { "есть", "отсутствует" })
            {
                string suffix = " " + label;
                if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                string subject = normalized.Substring(0, normalized.Length - suffix.Length).Trim();
                int slotSeparatorIndex = subject.IndexOf(": ", StringComparison.Ordinal);
                if (slotSeparatorIndex > 0)
                {
                    string candidateSlotLabel = subject.Substring(0, slotSeparatorIndex).Trim();
                    string candidateItemName = subject.Substring(slotSeparatorIndex + 2).Trim();
                    if (IsInventoryPresenceSlotLabel(candidateSlotLabel) &&
                        !string.IsNullOrWhiteSpace(candidateItemName))
                    {
                        slotLabel = candidateSlotLabel;
                        itemName = candidateItemName;
                    }
                    else
                    {
                        itemName = subject;
                    }
                }
                else
                {
                    itemName = subject;
                }

                presenceLabel = label;
                return !string.IsNullOrWhiteSpace(itemName);
            }

            return false;
        }

        private static bool IsInventoryPresenceSlotLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string normalized = label.Trim();
            return normalized.StartsWith("backpack слот ", StringComparison.Ordinal) ||
                   normalized.StartsWith("слот инвентаря ", StringComparison.Ordinal);
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
                string itemPresenceSlotLabel =
                    PartyInventorySemantics.GetSlotFieldLabel(choice?.ComparedPartyField);
                displayHeaderAnnotation = BuildInventoryPresenceHeaderAnnotation(
                    itemPresenceName,
                    itemPresenceLabel,
                    itemPresenceSlotLabel);
            }
            else
            {
                inferredLabel = InferChoiceLabel(choice);
                if (string.IsNullOrWhiteSpace(displayHeaderAnnotation))
                    displayHeaderAnnotation = BuildBranchGuardHeaderAnnotation(choice);
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
                GuardPredicate = choice.GetGuardPredicateForDisplay()
            };
        }

        private static string BuildBranchGuardHeaderAnnotation(BranchChoice choice)
        {
            return BuildGuardHeaderAnnotation(GetBranchDisplayGuardPredicates(choice));
        }

        private static string BuildBranchGuardConditionKey(BranchChoice choice)
        {
            return BuildGuardConditionKey(GetBranchDisplayGuardPredicates(choice));
        }

        private static List<PartyPredicate> GetBranchDisplayGuardPredicates(BranchChoice choice)
        {
            if (choice?.GuardPredicate == null || IsPartyLoopTraversalBranchChoice(choice))
                return new List<PartyPredicate>();

            var predicate = choice.GetGuardPredicateForDisplay();
            return PartyEffectSemantics.NormalizeGuardPredicatesForLoopAggregation(
                new[] { predicate })
                .Where(p => !IsRedundantUserVisibleAssumptionPredicateForNotes(p, null))
                .Where(p => !IsContradictedByUserVisibleAssumptions(p, null))
                .Where(p => !IsRedundantAliveStatusPredicateForNotes(p))
                .ToList();
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
                   !HasUserVisibleNodeLabelOrAnnotation(node))
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

            if (node.PreserveTransparentWrapper)
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)))
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node.HeaderAnnotation)))
                return false;

            if (!string.IsNullOrWhiteSpace(node.HeaderGuardKey))
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

            if (!string.IsNullOrWhiteSpace(node.HeaderGuardKey))
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

                node.HeaderGuardKey = MergeGuardConditionKeys(node.HeaderGuardKey, child.HeaderGuardKey);

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
                return BuildSyntheticBattleOutcomeLabels(directVariants);

            var optionLabels = ExtractPromptOptionLabels(promptText);
            var normalizedOptionLabels = optionLabels
                .Select(NormalizeChoiceLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedOptionLabels.Count == 0)
                return BuildSyntheticBattleOutcomeLabels(directVariants);

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

        private static Dictionary<VariantRenderItem, string> BuildSyntheticBattleOutcomeLabels(
            List<VariantRenderItem> directVariants)
        {
            var result = new Dictionary<VariantRenderItem, string>();
            var variants = (directVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null)
                .ToList();

            if (variants.Count <= 1 || !variants.Any(VariantItemHasBattleLaunchOutcome))
                return result;

            foreach (var variant in variants)
            {
                if (VariantItemHasBattleLaunchOutcome(variant))
                    continue;

                if (IsEmptyOrNoOpVariant(variant) || IsStatusOnlyNoBattleVariant(variant))
                    result[variant] = "без боя";
            }

            return result;
        }

        private static bool VariantItemHasBattleLaunchOutcome(VariantRenderItem item)
        {
            if (item == null)
                return false;

            if (HasBattleLaunchOutcome(item.Variant))
                return true;

            return (item.Lines ?? new List<string>())
                .Any(line => line?.IndexOf("random encounter", StringComparison.OrdinalIgnoreCase) >= 0);
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
            return IsTechnicalChoiceLabel(normalized) || IsSyntheticNoBattleLabel(normalized)
                ? null
                : normalized;
        }

        private static string NormalizeUserVisibleHeaderAnnotation(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            return IsTechnicalHeaderAnnotation(normalized)
                ? null
                : StripGuardHeaderAnnotationPrefix(normalized);
        }

        private static bool HasGuardHeaderAnnotationPrefix(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            return normalized?.StartsWith(GuardHeaderAnnotationPrefix, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static string StripGuardHeaderAnnotationPrefix(string annotation)
        {
            string normalized = NormalizeHeaderAnnotation(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            return HasGuardHeaderAnnotationPrefix(normalized)
                ? normalized.Substring(GuardHeaderAnnotationPrefix.Length).TrimStart()
                : normalized;
        }

        private static bool IsMainQuestCompletionHeaderAnnotation(string annotation)
        {
            string normalized = StripGuardHeaderAnnotationPrefix(annotation);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return normalized.IndexOf("главный квест игры выполнен", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("главный квест игры не выполнен", StringComparison.OrdinalIgnoreCase) >= 0;
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
                StripGuardHeaderAnnotationPrefix(normalized),
                @"^\[0x[0-9A-Fa-f]+\]\s*(?:>=|<=|!=|=|>|<)",
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

            var firstPredicates = GetDisplayGuardPredicates(variants[0].Variant, inheritedGuardKey);
            string firstGuardKey = BuildGuardConditionKey(firstPredicates);
            if (string.IsNullOrWhiteSpace(firstGuardKey))
                return null;

            if (!string.IsNullOrWhiteSpace(inheritedGuardKey) &&
                string.Equals(firstGuardKey, inheritedGuardKey, StringComparison.Ordinal))
            {
                return null;
            }

            return variants
                .Select(variant => BuildGuardConditionKey(GetDisplayGuardPredicates(variant.Variant, inheritedGuardKey)))
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
                       IsGuardConditionFullySuppressedByScope(variant.Variant, inheritedGuardKey));
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
                   HasAnyVariantItem(node);
        }

        private static void IntroduceSharedLineHierarchyAcrossBinaryStateConditionChildren(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                IntroduceSharedLineHierarchyAcrossBinaryStateConditionChildren(child);

            var candidates = (node.Children ?? new List<VariantTreeNode>())
                .Select((child, index) => TryBuildVisibleStateConditionBranchInfo(child, out var info)
                    ? new { Child = child, Info = info, Index = index }
                    : null)
                .Where(entry =>
                    entry != null &&
                    entry.Child.CommonLines != null &&
                    entry.Child.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)) &&
                    HasAnyVariantItem(entry.Child))
                .ToList();
            if (candidates.Count < 2)
                return;

            var childToSyntheticParent = new Dictionary<VariantTreeNode, VariantTreeNode>();
            foreach (var group in candidates
                .GroupBy(entry => new
                {
                    entry.Info.Address,
                    FirstLine = GetFirstMeaningfulLine(entry.Child.CommonLines) ?? string.Empty
                })
                .Where(group =>
                    !string.IsNullOrWhiteSpace(group.Key.FirstLine) &&
                    group.Any(entry => entry.Info.Polarity == StateConditionPolarity.Zero) &&
                    group.Any(entry => entry.Info.Polarity == StateConditionPolarity.NonZero))
                .OrderBy(group => group.Min(entry => entry.Index)))
            {
                var entries = group.OrderBy(entry => entry.Index).ToList();
                if (entries.Any(entry => childToSyntheticParent.ContainsKey(entry.Child)))
                    continue;

                var sharedLines = GetCommonPrefix(entries.Select(entry => entry.Child.CommonLines).ToList());
                if (!sharedLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                    continue;

                var syntheticParent = new VariantTreeNode
                {
                    CommonLines = sharedLines.ToList()
                };

                foreach (var entry in entries)
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

        private static bool TryBuildVisibleStateConditionBranchInfo(
            VariantTreeNode node,
            out StateConditionBranchInfo info)
        {
            info = null;
            if (node == null ||
                !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)))
            {
                return false;
            }

            string annotation = StripGuardHeaderAnnotationPrefix(node.HeaderAnnotation);
            if (string.IsNullOrWhiteSpace(annotation))
                return false;

            foreach (var kvp in KnownBinaryStateConditionLabels)
            {
                var labels = kvp.Value;
                if (labels == null || labels.Length < 2)
                    continue;

                if (string.Equals(annotation, labels[0], StringComparison.Ordinal))
                {
                    info = new StateConditionBranchInfo
                    {
                        Node = node,
                        Address = kvp.Key,
                        Polarity = StateConditionPolarity.Zero
                    };
                    return true;
                }

                if (string.Equals(annotation, labels[1], StringComparison.Ordinal))
                {
                    info = new StateConditionBranchInfo
                    {
                        Node = node,
                        Address = kvp.Key,
                        Polarity = StateConditionPolarity.NonZero
                    };
                    return true;
                }
            }

            return false;
        }

        private static string GetFirstMeaningfulLine(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static void CollapseTransparentDirectVariantWrappers(VariantTreeNode node)
        {
            CollapseTransparentDirectVariantWrappers(
                node,
                new Dictionary<VariantTreeNode, bool>());
        }

        private static string BuildUserVisibleNoOpRenderKey(VariantTreeNode node)
        {
            if (node == null)
                return null;

            return BuildUserVisibleNoOpLineKey(node.CommonLines);
        }

        private static string BuildUserVisibleNoOpRenderKey(VariantRenderItem item)
        {
            if (item?.Lines == null)
                return null;

            return BuildUserVisibleNoOpLineKey(item.Lines);
        }

        private static string BuildUserVisibleNoOpLineKey(IEnumerable<string> lines)
        {
            var meaningfulLines = (lines ?? Enumerable.Empty<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
            if (meaningfulLines.Count != 1)
                return null;

            return NormalizeUserVisibleNoOpLine(meaningfulLines[0]);
        }

        private static string NormalizeUserVisibleNoOpLine(string line)
        {
            string normalized = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (string.Equals(normalized, "NOTHING HERE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "NOTHING HERE.", StringComparison.OrdinalIgnoreCase))
            {
                return "NOTHING HERE";
            }

            if (string.Equals(normalized, "Ничего не происходит", StringComparison.Ordinal) ||
                string.Equals(normalized, "Ничего не происходит (не выполнены условия для наступления ни одного варианта)", StringComparison.Ordinal))
            {
                return "Ничего не происходит";
            }

            return null;
        }

        private static void CollapseTransparentDirectVariantWrappers(
            VariantTreeNode node,
            Dictionary<VariantTreeNode, bool> renderabilityCache)
        {
            if (node == null)
                return;

            foreach (var child in (node.Children ?? new List<VariantTreeNode>()).ToList())
                CollapseTransparentDirectVariantWrappers(child, renderabilityCache);

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(child => child != null)
                .ToList();
            if (children.Count == 0)
                return;

            bool hasRenderableDirectSibling = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Any(IsRenderableDirectVariant);
            var renderableChildFlags = children
                .Select(child => IsRenderableStructuralNode(child, renderabilityCache))
                .ToList();
            int renderableChildCount = renderableChildFlags.Count(isRenderable => isRenderable);

            var promotedDirectVariants = new List<VariantRenderItem>();
            var rebuiltChildren = new List<VariantTreeNode>();
            bool changed = false;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                bool hasRenderableSibling = hasRenderableDirectSibling ||
                                            renderableChildCount - (renderableChildFlags[i] ? 1 : 0) > 0;

                if (IsTransparentDirectVariantWrapper(child, hasRenderableSibling, renderabilityCache))
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

        private static bool ShouldPreserveTransparentMixedSiblingGroup(
            VariantTreeNode node,
            bool hasRenderableSibling,
            Dictionary<VariantTreeNode, bool> renderabilityCache)
        {
            if (node == null || !hasRenderableSibling)
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(node))
                return false;

            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return false;

            bool hasDirectOutcome = node.DirectVariants != null &&
                                    node.DirectVariants.Any(IsRenderableDirectVariant);
            bool hasNestedOutcome = node.Children != null &&
                                    node.Children.Any(child => IsRenderableStructuralNode(child, renderabilityCache));

            return hasDirectOutcome && hasNestedOutcome;
        }

        private static bool IsTransparentDirectVariantWrapper(
            VariantTreeNode node,
            bool hasRenderableSibling,
            Dictionary<VariantTreeNode, bool> renderabilityCache)
        {
            if (node == null)
                return false;

            if (node.PreserveTransparentWrapper)
                return false;

            if (ShouldPreserveTransparentMixedSiblingGroup(node, hasRenderableSibling, renderabilityCache))
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(node))
                return false;

            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return false;

            bool hasDirectVariant = node.DirectVariants != null &&
                                    node.DirectVariants.Any(item => item != null);
            bool hasRenderableChild = node.Children != null &&
                                      node.Children.Any(child => IsRenderableStructuralNode(child, renderabilityCache));

            return hasDirectVariant || hasRenderableChild;
        }

        private static bool IsRenderableStructuralNode(
            VariantTreeNode node,
            Dictionary<VariantTreeNode, bool> renderabilityCache)
        {
            if (node == null)
                return false;

            if (renderabilityCache != null && renderabilityCache.TryGetValue(node, out bool cached))
                return cached;

            bool result =
                !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(node.Label)) ||
                !string.IsNullOrWhiteSpace(NormalizeUserVisibleHeaderAnnotation(node.HeaderAnnotation)) ||
                ((node.CommonLines?.Count ?? 0) > 0) ||
                (node.Children != null && node.Children.Any(child => IsRenderableStructuralNode(child, renderabilityCache))) ||
                (node.DirectVariants != null && node.DirectVariants.Any(IsRenderableDirectVariant));

            if (renderabilityCache != null)
                renderabilityCache[node] = result;
            return result;
        }

        private static bool HasAnyVariantItem(VariantTreeNode node)
        {
            if (node == null)
                return false;

            if (node.DirectVariants != null && node.DirectVariants.Any(item => item != null))
                return true;

            return node.Children != null && node.Children.Any(HasAnyVariantItem);
        }

        private static bool HasUserVisibleNodeLabelOrAnnotation(VariantTreeNode node, string labelOverride = null)
        {
            return !string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(GetEffectiveNodeLabel(node, labelOverride))) ||
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

        private static List<string> GetHoistablePromptDisplayLines(VariantTreeNode node)
        {
            if (node == null)
                return new List<string>();

            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return FlattenDisplayLines(node.CommonLines);

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null)
                .ToList();

            if (directVariants.Count == 0)
                return new List<string>();

            return GetCommonDisplayLinePrefix(directVariants.Select(variant => variant.Lines).ToList());
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

        private static bool NodeDisplayContentStartsWithLines(VariantTreeNode node, IReadOnlyList<string> prefix)
        {
            if (node == null || prefix == null || prefix.Count == 0)
                return false;

            if (StartsWithLineSequence(FlattenDisplayLines(node.CommonLines), prefix))
                return true;

            var variants = GetAllVariants(node)
                .Where(variant => variant != null)
                .ToList();

            return variants.Count > 0 &&
                   variants.All(variant => StartsWithLineSequence(FlattenDisplayLines(variant.Lines), prefix));
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

        private static bool TryRemoveLeadingDisplayLinesFromNodeContent(VariantTreeNode node, IReadOnlyList<string> prefix)
        {
            if (node == null || prefix == null || prefix.Count == 0)
                return false;

            if (StartsWithLineSequence(FlattenDisplayLines(node.CommonLines), prefix))
            {
                node.CommonLines = FlattenDisplayLines(node.CommonLines)
                    .Skip(prefix.Count)
                    .ToList();
                return true;
            }

            var variants = GetAllVariants(node)
                .Where(variant => variant != null)
                .ToList();

            if (variants.Count == 0 ||
                variants.Any(variant => !StartsWithLineSequence(FlattenDisplayLines(variant.Lines), prefix)))
            {
                return false;
            }

            foreach (var variant in variants)
            {
                variant.Lines = FlattenDisplayLines(variant.Lines)
                    .Skip(prefix.Count)
                    .ToList();
            }

            return true;
        }

        private static void HoistSharedLeadingLines(
            VariantTreeNode node,
            bool allowDisplayLineHoist = false)
        {
            if (node == null)
                return;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                HoistSharedLeadingLines(child, allowDisplayLineHoist);

            HoistSharedLeadingLinesAcrossDirectVariants(node, allowDisplayLineHoist);

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            if (children.Count < 2)
                return;

            var childLeadingLines = children
                .Select(GetHoistablePromptContentLines)
                .ToList();
            if (childLeadingLines.Any(lines => lines == null || lines.Count == 0))
                return;

            var commonLines = GetCommonPrefix(childLeadingLines);
            bool useDisplayLinePrefix = false;
            if (!commonLines.Any(line => !string.IsNullOrWhiteSpace(line)) &&
                allowDisplayLineHoist)
            {
                childLeadingLines = children
                    .Select(GetHoistablePromptDisplayLines)
                    .ToList();
                if (childLeadingLines.Any(lines => lines == null || lines.Count == 0))
                    return;

                commonLines = GetCommonPrefix(childLeadingLines);
                useDisplayLinePrefix = commonLines.Any(line => !string.IsNullOrWhiteSpace(line));
            }

            if (!commonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return;

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null && HasMeaningfulLines(variant.Lines))
                .ToList();
            if (useDisplayLinePrefix
                ? directVariants.Any(variant => !StartsWithLineSequence(FlattenDisplayLines(variant.Lines), commonLines))
                : directVariants.Any(variant => !StartsWithLineSequence(variant.Lines, commonLines)))
            {
                return;
            }

            var descendants = children
                .SelectMany(GetAllVariants)
                .Where(variant => variant != null)
                .ToList();
            if (ShouldKeepSharedPartyEffectPrefixInline(descendants, commonLines))
                return;

            if (children.Any(child => useDisplayLinePrefix
                    ? !NodeDisplayContentStartsWithLines(child, commonLines)
                    : !NodeContentStartsWithLines(child, commonLines)))
            {
                return;
            }

            foreach (var child in children)
            {
                if (useDisplayLinePrefix)
                    TryRemoveLeadingDisplayLinesFromNodeContent(child, commonLines);
                else
                    TryRemoveLeadingLinesFromNodeContent(child, commonLines);
            }

            foreach (var variant in directVariants)
            {
                variant.Lines = (useDisplayLinePrefix ? FlattenDisplayLines(variant.Lines) : variant.Lines)
                    .Skip(commonLines.Count)
                    .ToList();
            }

            node.CommonLines ??= new List<string>();
            node.CommonLines.AddRange(commonLines);
        }

        private static void HoistSharedLeadingLinesAcrossDirectVariants(
            VariantTreeNode node,
            bool allowDisplayLineHoist)
        {
            if (node == null)
                return;

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(variant => variant != null && HasMeaningfulLines(variant.Lines))
                .ToList();
            if (directVariants.Count < 2)
                return;

            var commonLines = GetCommonPrefix(directVariants.Select(variant => variant.Lines).ToList());
            bool useDisplayLinePrefix = false;
            if (!commonLines.Any(line => !string.IsNullOrWhiteSpace(line)) &&
                allowDisplayLineHoist)
            {
                commonLines = GetCommonDisplayLinePrefix(directVariants.Select(variant => variant.Lines).ToList());
                useDisplayLinePrefix = commonLines.Any(line => !string.IsNullOrWhiteSpace(line));
            }

            if (!commonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                return;

            var children = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            if (useDisplayLinePrefix && children.Count > 0)
                return;

            if (children.Any(child => !NodeContentStartsWithLines(child, commonLines)))
                return;

            var descendants = directVariants
                .Concat(children.SelectMany(GetAllVariants))
                .Where(variant => variant != null)
                .ToList();
            if (ShouldKeepSharedPartyEffectPrefixInline(descendants, commonLines))
                return;

            foreach (var variant in directVariants)
            {
                variant.Lines = useDisplayLinePrefix
                    ? FlattenDisplayLines(variant.Lines).Skip(commonLines.Count).ToList()
                    : variant.Lines.Skip(commonLines.Count).ToList();
            }

            foreach (var child in children)
                TryRemoveLeadingLinesFromNodeContent(child, commonLines);

            node.CommonLines ??= new List<string>();
            node.CommonLines.AddRange(commonLines);
        }

        private static List<string> GetCommonDisplayLinePrefix(List<List<string>> source)
        {
            if (source == null || source.Count == 0)
                return new List<string>();

            return GetCommonPrefix(source.Select(FlattenDisplayLines).ToList());
        }

        private static List<string> FlattenDisplayLines(IEnumerable<string> lines)
        {
            var result = new List<string>();

            foreach (var line in lines ?? Enumerable.Empty<string>())
                result.AddRange(SplitDisplayLines(line));

            return result;
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

        private static void PromoteSingleLeafNarrativeCoveredStatRewardAnnotations(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                PromoteSingleLeafNarrativeCoveredStatRewardAnnotations(child);

            if (node.CommonLines == null ||
                !node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                return;
            }

            var singleLeafItem = GetSingleLeafVariantItem(node);
            if (singleLeafItem?.Variant == null)
                return;

            var singleLeafLines = singleLeafItem.Lines ?? new List<string>();
            if (singleLeafLines.Any(line => !string.IsNullOrWhiteSpace(line)) &&
                !IsNoOpOnly(singleLeafLines))
            {
                return;
            }

            if (!TryBuildNarrativeCoveredConditionalStatRewardInfo(singleLeafItem.Variant, out var info) ||
                !ShouldUseNarrativeCoveredConditionalStatRewardHeader(singleLeafItem.Variant, info))
            {
                return;
            }

            string annotation = info.HeaderAnnotation;
            if (string.IsNullOrWhiteSpace(annotation))
                return;

            node.HeaderAnnotation = MergeHeaderAnnotations(node.HeaderAnnotation, annotation);
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
            if (TryBuildMainQuestMilestoneWriteDescription(effect, variantContext, out string mainQuestMilestoneDescription))
                return mainQuestMilestoneDescription;

            if (TryBuildMainQuestTransferReadyCompletionDescription(effect, variantContext, out string mainQuestCompletionDescription))
                return mainQuestCompletionDescription;

            if (TryBuildRanalouPrisonerChoiceJudgementDescription(effect, variantContext, out string ranalouJudgementDescription))
                return ranalouJudgementDescription;

            string description = PartyEffectSemantics.BuildHumanDescription(effect);
            if (!ShouldRenderStandardStatusLoopAsCurrentMember(variantContext, effect))
                return description;

            return ReplaceWholePartyConditionTargetWithCurrentMember(description);
        }

        private static bool TryBuildMainQuestMilestoneWriteDescription(
            PartyEffect effect,
            PathVariantInfo variantContext,
            out string description)
        {
            description = null;

            if (effect == null ||
                PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Technical7D ||
                PartyEffectSemantics.GetEffectiveOperation(effect) != PartyEffectOperation.Write ||
                PartyEffectSemantics.GetEffectiveScope(effect) != PartyEffectScope.WholeParty)
            {
                return false;
            }

            if (effect.ImmediateValue == PartyTechnicalFieldSemantics.ImposterDefeatedTransferReadyValue ||
                VariantTextLooksLikeImposterDefeatedMilestone(variantContext))
            {
                description =
                    "-=*Каждый персонаж партии отмечается победившим самозванца (главгада) " +
                    "по главному квесту*=-";
                return true;
            }

            if (!effect.ImmediateValue.HasValue && VariantTextLooksLikeAstralProjectorMilestone(variantContext))
            {
                description =
                    "-=*Текущий астральный проектор засчитан каждому персонажу партии " +
                    "для главного квеста*=-";
                return true;
            }

            return false;
        }

        private static bool VariantTextLooksLikeAstralProjectorMilestone(PathVariantInfo variantContext)
        {
            return VariantTextsContain(variantContext, "ASTRAL PROJECTOR");
        }

        private static bool VariantTextLooksLikeImposterDefeatedMilestone(PathVariantInfo variantContext)
        {
            return VariantTextsContain(variantContext, "IMPOSTER") &&
                   (VariantTextsContain(variantContext, "VOIDED") ||
                    VariantTextsContain(variantContext, "ELIGIBLE FOR") ||
                    VariantTextsContain(variantContext, "TRANSFER"));
        }

        private static bool TryBuildMainQuestTransferReadyCompletionDescription(
            PartyEffect effect,
            PathVariantInfo variantContext,
            out string description)
        {
            description = null;

            if (effect == null ||
                variantContext == null ||
                PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Technical7D ||
                PartyEffectSemantics.GetEffectiveOperation(effect) != PartyEffectOperation.Write ||
                PartyEffectSemantics.GetEffectiveScope(effect) != PartyEffectScope.PartySubset ||
                !effect.ImmediateValue.HasValue ||
                effect.ImmediateValue.Value < PartyTechnicalFieldSemantics.MainQuestCompletedThreshold)
            {
                return false;
            }

            bool hasTransferReadyVariantGuard = variantContext.GetGuardPredicates()
                .Any(IsMainQuestTransferReadyPartyLoopGuard);

            if (!hasTransferReadyVariantGuard)
                return false;

            description =
                "-=*У персонажей партии, победивших самозванца (главгада) по главному квесту, " +
                "главный квест игры отмечается выполненным*=-";
            return true;
        }

        private static bool IsMainQuestTransferReadyPartyLoopGuard(PartyPredicate predicate)
        {
            return predicate != null &&
                   predicate.Field == PartyFieldKind.Technical7D &&
                   predicate.Comparison == PartyPredicateComparison.Equal &&
                   predicate.ImmediateValue == PartyTechnicalFieldSemantics.ImposterDefeatedTransferReadyValue &&
                   predicate.LoopQuantifier != PartyPredicateLoopQuantifier.None &&
                   (predicate.TargetMember?.IsPartyLoopMember == true ||
                    predicate.LoopQuantifier == PartyPredicateLoopQuantifier.Any ||
                    predicate.LoopQuantifier == PartyPredicateLoopQuantifier.Unspecified);
        }

        private static bool TryBuildRanalouPrisonerChoiceJudgementDescription(
            PartyEffect effect,
            PathVariantInfo variantContext,
            out string description)
        {
            description = null;
            if (effect == null ||
                PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Technical6E ||
                PartyEffectSemantics.GetEffectiveOperation(effect) != PartyEffectOperation.Increment ||
                effect.ImmediateValue != 0x20 ||
                !PartyEffectSemantics.IsLoopDerived(effect) ||
                !TryGetRanalouPrisonerChoiceValue(variantContext, out byte choiceValue) ||
                !TryGetRanalouPrisonerChoiceAlignmentName(choiceValue, out string alignmentName))
            {
                return false;
            }

            description =
                "-=*Узник засчитывается по квесту RANALOU каждому персонажу текущей партии, " +
                $"у которого этот узник ещё не был отмечен и текущий ALIGNMENT = {alignmentName}*=-";
            return true;
        }

        private static bool TryGetRanalouPrisonerChoiceAlignmentName(byte choiceValue, out string alignmentName)
        {
            byte? alignmentValue = choiceValue switch
            {
                (byte)'1' => PartyAlignmentSemantics.GoodValue,
                (byte)'2' => PartyAlignmentSemantics.EvilValue,
                (byte)'3' => PartyAlignmentSemantics.NeutralValue,
                _ => (byte?)null
            };

            alignmentName = alignmentValue.HasValue
                ? PartyAlignmentSemantics.FormatAlignmentValue(alignmentValue.Value)
                : null;

            return alignmentName != null;
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

            string choicePredicateKey = BuildLoopNormalizedPredicateKey(choice.GetGuardPredicateForDisplay());
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

            if (IsScalarStatSharedPartyEffect(effect))
                return true;

            return (PartyFoodSemantics.IsFoodField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                    PartyAgeSemantics.IsAgeField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                    PartyTechnicalFieldSemantics.IsTrackedField(PartyEffectSemantics.GetEffectiveField(effect)) ||
                    PartyTemporaryStatSemantics.IsTrackedField(PartyEffectSemantics.GetEffectiveField(effect))) &&
                   PartyEffectSemantics.IsStateChanging(effect);
        }

        private static bool IsScalarStatSharedPartyEffect(PartyEffect effect)
        {
            if (effect == null || !PartyEffectSemantics.IsStateChanging(effect))
                return false;

            var field = PartyEffectSemantics.GetEffectiveField(effect);
            return field == PartyFieldKind.Hp || field == PartyFieldKind.Sp;
        }

        private static void NormalizeConditionalStatusComplementLines(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var item in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (ShouldNormalizeConditionalStatusDisplayLines(item?.Variant))
                    item.Lines = NormalizeConditionalStatusDisplayLines(item.Lines);
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                NormalizeConditionalStatusComplementLines(child);
        }

        private static void GroupConditionalLoopSubsetComplementOutcomes(VariantTreeNode node)
        {
            if (node == null)
                return;

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
                GroupConditionalLoopSubsetComplementOutcomes(child);

            var directVariants = (node.DirectVariants ?? new List<VariantRenderItem>())
                .Where(item => item != null)
                .ToList();
            if (directVariants.Count < 2)
                return;

            var consumed = new HashSet<VariantRenderItem>();
            var outcomeNodes = new List<VariantTreeNode>();

            foreach (var group in directVariants.GroupBy(BuildConditionalLoopSubsetComplementDisplayKey))
            {
                var groupItems = group.ToList();
                if (TryBuildConditionalLoopSubsetComplementOutcomeNode(groupItems, out var outcomeNode))
                {
                    foreach (var item in groupItems)
                        consumed.Add(item);

                    outcomeNodes.Add(outcomeNode);
                }
            }

            if (outcomeNodes.Count < 2)
                return;

            node.DirectVariants = directVariants
                .Where(item => !consumed.Contains(item))
                .ToList();
            node.Children ??= new List<VariantTreeNode>();
            node.Children.AddRange(outcomeNodes);
        }

        private static bool TryBuildConditionalLoopSubsetComplementOutcomeNode(
            List<VariantRenderItem> items,
            out VariantTreeNode outcomeNode)
        {
            outcomeNode = null;

            var groupItems = (items ?? new List<VariantRenderItem>())
                .Where(item => item?.Variant != null)
                .OrderBy(GetVariantRenderOrderKey)
                .ToList();
            if (groupItems.Count < 2)
                return false;

            if (groupItems.Any(item => item.Variant?.HasProbabilityInfo != true))
                return false;

            var itemsWithOutcome = groupItems
                .Where(item => GetConditionalLoopSubsetOutcomeLines(item).Count > 0)
                .ToList();
            if (itemsWithOutcome.Count == 0 || itemsWithOutcome.Count == groupItems.Count)
                return false;

            var distinctOutcomeLineSets = itemsWithOutcome
                .Select(item => BuildCommonLinesKey(GetConditionalLoopSubsetOutcomeLines(item)))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (distinctOutcomeLineSets.Count != 1)
                return false;

            var outcomeLines = GetConditionalLoopSubsetOutcomeLines(itemsWithOutcome.First());
            if (outcomeLines.Count == 0)
                return false;

            var baseItem = groupItems
                .FirstOrDefault(item => GetConditionalLoopSubsetOutcomeLines(item).Count == 0)
                ?? groupItems.First();
            var baseLines = RemoveConditionalLoopSubsetOutcomeLines(baseItem);

            var noStatusSource = groupItems
                .FirstOrDefault(item => GetConditionalLoopSubsetOutcomeLines(item).Count == 0)
                ?? groupItems.First();
            var statusSource = itemsWithOutcome.First();

            var label = BuildConditionalLoopSubsetOutcomeLabel(baseLines, out var childBaseLines);
            outcomeNode = new VariantTreeNode
            {
                SegmentKey = "conditional-status:" + BuildCommonLinesKey(baseLines),
                Label = label,
                PreserveTransparentWrapper = true,
                CommonLines = new List<string>(),
                Children = new List<VariantTreeNode>
                {
                    BuildConditionalLoopSubsetOutcomeChildNode(
                        noStatusSource,
                        childBaseLines,
                        null,
                        "no-status"),
                    BuildConditionalLoopSubsetOutcomeChildNode(
                        statusSource,
                        childBaseLines,
                        outcomeLines,
                        "status")
                }
            };
            return true;
        }

        private static VariantTreeNode BuildConditionalLoopSubsetOutcomeChildNode(
            VariantRenderItem source,
            List<string> baseLines,
            List<string> outcomeLines,
            string segmentSuffix)
        {
            var lines = (baseLines ?? new List<string>())
                .Where(line => line != null)
                .ToList();

            string label = null;
            var visibleOutcomeLines = (outcomeLines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (visibleOutcomeLines.Count > 0)
            {
                if (lines.Any(line => !string.IsNullOrWhiteSpace(line)))
                {
                    foreach (var line in visibleOutcomeLines)
                        lines = InsertLineBeforeBattleOutcome(lines, line);
                }
                else if (visibleOutcomeLines.Count == 1)
                {
                    label = visibleOutcomeLines[0];
                }
                else
                {
                    lines = visibleOutcomeLines;
                }
            }

            var renderVariant = ClonePathVariantForRender(source?.Variant);
            if (renderVariant != null)
            {
                int numerator = Math.Max(0, renderVariant.ProbabilityNumerator);
                int denominator = Math.Max(1, renderVariant.ProbabilityDenominator);
                ReduceFraction(ref numerator, ref denominator);
                renderVariant.ProbabilityNumerator = numerator;
                renderVariant.ProbabilityDenominator = denominator;
            }

            return new VariantTreeNode
            {
                SegmentKey = "conditional-status-" + segmentSuffix,
                Label = label,
                PreserveTransparentWrapper = true,
                RenderPriorityOverride = visibleOutcomeLines.Count == 0 ? -1 : 0,
                DirectVariants = new List<VariantRenderItem>
                {
                    new VariantRenderItem
                    {
                        Variant = renderVariant,
                        Lines = lines,
                        NarrativeLines = source?.NarrativeLines?.ToList() ?? new List<string>(),
                        HeaderAnnotations = source?.HeaderAnnotations?.ToList() ?? new List<string>(),
                        SupplementalLines = source?.SupplementalLines?.ToList() ?? new List<string>()
                    }
                }
            };
        }

        private static string BuildConditionalLoopSubsetOutcomeLabel(
            List<string> baseLines,
            out List<string> childBaseLines)
        {
            childBaseLines = (baseLines ?? new List<string>())
                .Where(line => line != null)
                .ToList();

            var meaningfulLines = childBaseLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            if (meaningfulLines.Count == 0 || IsNoOpOnly(childBaseLines))
            {
                childBaseLines = new List<string>();
                return "без боя";
            }

            return null;
        }

        private static string BuildConditionalLoopSubsetComplementDisplayKey(VariantRenderItem item)
        {
            var baseLines = RemoveConditionalLoopSubsetOutcomeLines(item);
            return BuildCommonLinesKey(baseLines);
        }

        private static List<string> GetConditionalLoopSubsetOutcomeLines(VariantRenderItem item)
        {
            var candidateLines = GetVariantPartyEffectLines(item, effect => IsConditionalLoopComplementOutcomeEffect(item, effect))
                .Select(NormalizeConditionalStatusDisplayLine)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidateLines.Count == 0)
                return new List<string>();

            var itemLineCounts = BuildLineCounts(item?.Lines ?? new List<string>());
            return candidateLines
                .Where(line => itemLineCounts.ContainsKey(line ?? string.Empty))
                .ToList();
        }

        private static List<string> RemoveConditionalLoopSubsetOutcomeLines(VariantRenderItem item)
        {
            var linesToRemove = BuildLineCounts(GetConditionalLoopSubsetOutcomeLines(item));
            if (linesToRemove.Count == 0)
                return item?.Lines?.Where(line => line != null).ToList() ?? new List<string>();

            var result = new List<string>();
            foreach (var line in item?.Lines ?? new List<string>())
            {
                string key = line ?? string.Empty;
                if (linesToRemove.TryGetValue(key, out int remaining) && remaining > 0)
                {
                    if (remaining == 1)
                        linesToRemove.Remove(key);
                    else
                        linesToRemove[key] = remaining - 1;
                    continue;
                }

                if (line != null)
                    result.Add(line);
            }

            return result;
        }

        private static bool IsConditionalLoopComplementOutcomeEffect(PartyEffect effect)
        {
            return IsConditionalLoopSubsetOutcomeEffect(effect) ||
                   (effect != null &&
                    PartyEffectSemantics.IsStateChanging(effect) &&
                    PartyEffectSemantics.IsLoopDerived(effect) &&
                    PartyEffectSemantics.GetEffectiveField(effect) != PartyFieldKind.Hp);
        }

        private static bool IsConditionalLoopComplementOutcomeEffect(
            VariantRenderItem item,
            PartyEffect effect)
        {
            if (IsConditionalLoopComplementOutcomeEffect(effect))
                return true;

            if (item?.ConditionalComplementOutcomeEffectKeys == null ||
                item.ConditionalComplementOutcomeEffectKeys.Count == 0 ||
                effect == null)
            {
                return false;
            }

            string key = PartyEffectSemantics.BuildSemanticKey(effect);
            return !string.IsNullOrWhiteSpace(key) &&
                   item.ConditionalComplementOutcomeEffectKeys.Contains(key);
        }

        private static bool ShouldNormalizeConditionalStatusDisplayLines(PathVariantInfo variant)
        {
            return variant?.PartyEffects?
                .Any(IsConditionalLoopSubsetStatusOutcomeEffect) == true;
        }

        private static List<string> NormalizeConditionalStatusDisplayLines(IEnumerable<string> lines)
        {
            var result = new List<string>();
            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                result.Add(NormalizeConditionalStatusDisplayLine(line));
            }

            return result;
        }

        private static string NormalizeConditionalStatusDisplayLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            string trimmed = line.TrimStart();
            string normalized = BuildPossibleConditionalStatusLine(trimmed);
            if (string.IsNullOrWhiteSpace(normalized))
                return line;

            int leadingLength = line.Length - trimmed.Length;
            return leadingLength > 0
                ? line.Substring(0, leadingLength) + normalized
                : normalized;
        }

        private static List<string> InsertLineBeforeBattleOutcome(IEnumerable<string> lines, string lineToInsert)
        {
            var result = (lines ?? Enumerable.Empty<string>())
                .Where(line => line != null)
                .ToList();

            if (string.IsNullOrWhiteSpace(lineToInsert) ||
                result.Contains(lineToInsert, StringComparer.Ordinal))
            {
                return result;
            }

            int battleIndex = FindFirstBattleLineIndex(result);
            if (battleIndex < 0)
                result.Add(lineToInsert);
            else
                result.Insert(battleIndex, lineToInsert);

            return result;
        }

        private static string BuildPossibleConditionalStatusLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            if (line.StartsWith(CurrentPartyMemberConditionChangePrefix, StringComparison.Ordinal))
                return LegacyWholePartyConditionChangePrefix +
                       line.Substring(CurrentPartyMemberConditionChangePrefix.Length);

            if (line.StartsWith(WholePartyConditionChangePrefix, StringComparison.Ordinal))
                return line;

            if (line.StartsWith(LegacyWholePartyConditionChangePrefix, StringComparison.Ordinal))
                return line;

            return null;
        }

        private static bool IsPossibleConditionalPartyStatusLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.TrimStart();
            return trimmed.StartsWith(CurrentPartyMemberConditionChangePrefix, StringComparison.Ordinal) ||
                   trimmed.StartsWith(WholePartyConditionChangePrefix, StringComparison.Ordinal) ||
                   trimmed.StartsWith(LegacyWholePartyConditionChangePrefix, StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION текущего персонажа партии может измениться на ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION всех персонажей в партии может измениться на ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION персонажа(ей) в партии может измениться на ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION текущего персонажа партии не изменяется", StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION всех персонажей в партии не изменяется", StringComparison.Ordinal) ||
                   trimmed.StartsWith("CONDITION персонажа(ей) в партии не изменяется", StringComparison.Ordinal);
        }

        private static bool IsConditionalLoopSubsetStatusOutcomeEffect(PartyEffect effect)
        {
            return IsConditionalLoopSubsetOutcomeEffect(effect) &&
                   PartyEffectSemantics.GetEffectiveField(effect) == PartyFieldKind.Status;
        }

        private static bool TrySumVariantProbabilities(
            IEnumerable<VariantRenderItem> items,
            out int numerator,
            out int denominator)
        {
            numerator = 0;
            denominator = 1;

            long sumNumerator = 0;
            long commonDenominator = 1;
            bool hasAny = false;

            foreach (var item in items ?? Enumerable.Empty<VariantRenderItem>())
            {
                var variant = item?.Variant;
                if (variant == null)
                    continue;

                int currentNumerator = Math.Max(0, variant.ProbabilityNumerator);
                int currentDenominator = Math.Max(1, variant.ProbabilityDenominator);
                ReduceFraction(ref currentNumerator, ref currentDenominator);

                long gcd = GreatestCommonDivisor(
                    (int)Math.Min(int.MaxValue, Math.Abs(commonDenominator)),
                    currentDenominator);
                long lcm = commonDenominator / gcd * currentDenominator;
                if (lcm <= 0 || lcm > int.MaxValue)
                    return false;

                sumNumerator =
                    sumNumerator * (lcm / commonDenominator) +
                    (long)currentNumerator * (lcm / currentDenominator);
                commonDenominator = lcm;
                hasAny = true;
            }

            if (!hasAny || sumNumerator <= 0 || sumNumerator > int.MaxValue)
                return false;

            numerator = (int)sumNumerator;
            denominator = (int)commonDenominator;
            ReduceFraction(ref numerator, ref denominator);
            if (numerator >= denominator && numerator > 0)
            {
                numerator = 1;
                denominator = 1;
            }

            return true;
        }

        private static PathVariantInfo ClonePathVariantForRender(PathVariantInfo source)
        {
            if (source == null)
                return null;

            var clone = new PathVariantInfo();
            foreach (var property in typeof(PathVariantInfo).GetProperties())
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;

                property.SetValue(clone, property.GetValue(source));
            }

            clone.Texts = source.Texts?.ToList() ?? new List<string>();
            clone.TextEntries = source.TextEntries?
                .Where(entry => entry != null)
                .Select(entry => entry.Clone())
                .ToList() ?? new List<TextEntry>();
            clone.BranchChoices = source.BranchChoices?
                .Where(choice => choice != null)
                .Select(choice => choice.Clone())
                .ToList() ?? new List<BranchChoice>();
            clone.PartyEffects = source.PartyEffects?
                .Where(effect => effect != null)
                .Select(effect => effect.Clone())
                .ToList() ?? new List<PartyEffect>();
            clone.OccurrenceIndices = source.OccurrenceIndices?.ToList() ?? new List<int>();
            clone.OccurrenceRanges = source.OccurrenceRanges?
                .Where(range => range != null)
                .Select(range => range.Clone())
                .ToList() ?? new List<OccurrenceRangeInfo>();
            clone.StaticMapDataReads = source.StaticMapDataReads != null
                ? new Dictionary<ushort, byte>(source.StaticMapDataReads)
                : new Dictionary<ushort, byte>();

            return clone;
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
                if (IsBattleOutcomeLine(lines[i]))
                    return i;
            }

            return -1;
        }

        private static bool IsBattleOutcomeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            return IsBattleLine(line) ||
                   line.IndexOf("random encounter", StringComparison.OrdinalIgnoreCase) >= 0;
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
                   !HasUserVisibleNodeLabelOrAnnotation(node))
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
            IReadOnlyDictionary<VariantRenderItem, string> syntheticDirectLabels = null,
            IReadOnlyDictionary<VariantTreeNode, string> syntheticChildLabels = null)
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
                string syntheticChildLabel = null;
                syntheticChildLabels?.TryGetValue(child, out syntheticChildLabel);
                string effectiveChildLabel = GetEffectiveNodeLabel(child, syntheticChildLabel);
                bool hasChoiceOrder = TryGetChoiceOrderKey(effectiveChildLabel, promptChoiceOrder, out int choiceOrderKey);
                bool mixedPartyScanCancelChoice =
                    !hasChoiceOrder &&
                    ContainsMixedPartyScanSubsetSummary(parentNode) &&
                    IsLikelyCancelNode(child);
                bool promotedNoBattleChild = IsSyntheticNoBattleLabel(effectiveChildLabel);
                int displayPriority = child.RenderPriorityOverride ?? (mixedPartyScanCancelChoice
                    ? -1
                    : (promotedNoBattleChild
                        ? -1
                        : (parentHasMultiNumericPrompt && HasPromptOptionLabels(child.CommonLines)
                        ? 1
                        : (IsEmptyOrNoOpNode(child, syntheticChildLabel) ? 1 : 0))));
                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetNodeOccurrenceOrderKey(child),
                    DisplayPriority = displayPriority,
                    PathOrderKey = GetNodeRenderOrderKey(child),
                    ChoiceOrderKey = choiceOrderKey,
                    HasChoiceOrderKey = hasChoiceOrder,
                    ChildNode = child,
                    ChildLabelOverride = string.IsNullOrWhiteSpace(child.Label)
                        ? syntheticChildLabel
                        : null
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
                bool promotedNoBattleChoice = IsSyntheticNoBattleLabel(syntheticLabel);
                result.Add(new OrderedRenderEntry
                {
                    OccurrenceOrderKey = GetVariantOccurrenceOrderKey(variant),
                    DisplayPriority = promotedNoChoice || promotedNoBattleChoice
                        ? -1
                        : (IsEmptyOrNoOpVariant(variant) ? 1 : 0),
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

        private static bool IsSyntheticNoBattleLabel(string label)
        {
            return string.Equals(
                NormalizeChoiceLabel(label),
                "без боя",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetEffectiveNodeLabel(VariantTreeNode node, string labelOverride)
        {
            return !string.IsNullOrWhiteSpace(node?.Label)
                ? node.Label
                : labelOverride;
        }

        private static bool IsEmptyOrNoOpNode(VariantTreeNode node, string labelOverride = null)
        {
            if (node == null)
                return false;

            if (!string.IsNullOrWhiteSpace(NormalizeUserVisibleChoiceLabel(GetEffectiveNodeLabel(node, labelOverride))))
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
                   children.All(child => IsEmptyOrNoOpNode(child));
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

        private static void RenderTopLevelGroup(OvrObject obj, TopLevelVariantGroup group, StringBuilder sb, int groupNumber)
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
            var singleLeafLines = BuildVariantRenderLines(singleLeafItem);

            string header = $"Вариант {groupNumber}";
            string sharedProbabilityLine = singleLeaf == null
                ? GetSharedProbabilityLine(group?.TreeRoot)
                : null;
            string groupGuardKey = group?.HeaderGuardKey;
            var sharedGuardPredicates = singleLeaf == null
                ? GetSharedGuardPredicates(group?.TreeRoot, groupGuardKey)
                : null;
            string sharedGuardKey = BuildGuardConditionKey(sharedGuardPredicates);
            string topSuppressedGuardKey = MergeGuardConditionKeys(groupGuardKey, sharedGuardKey);
            string sharedGuardAnnotation = BuildGuardHeaderAnnotation(sharedGuardPredicates);
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(
                sharedProbabilityLine,
                !string.IsNullOrWhiteSpace(sharedGuardAnnotation) ||
                HasInheritedGuardForAllVariants(group?.TreeRoot, topSuppressedGuardKey));

            var sharedAnnotations = new List<string>();
            if (!string.IsNullOrWhiteSpace(group?.HeaderAnnotation))
                sharedAnnotations.Add(group.HeaderAnnotation);
            if (singleLeaf != null)
            {
                AddDistinctHeaderAnnotations(
                    sharedAnnotations,
                    BuildHierarchicalTerminalVariantHeaderAnnotations(
                        obj,
                        singleLeafItem,
                        localHeaderAnnotations: sharedAnnotations,
                        inheritedVisibleHeaderAnnotations: null,
                        suppressedProbabilityLine: null,
                        suppressedGuardKey: groupGuardKey));
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
            var topVisibleHeaderAnnotations = !string.IsNullOrWhiteSpace(topAnnotationText)
                ? sharedAnnotations.ToList()
                : new List<string>();

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

            int childIndex = 1;
            bool wroteAnyChild = false;

            foreach (var entry in BuildOrderedRenderEntries(
                group.TreeRoot,
                renderableChildren,
                directVariants,
                syntheticChoiceLabels,
                syntheticChildLabels))
            {
                if (wroteAnyChild)
                    sb.AppendLine();

                if (entry.ChildNode != null)
                {
                    RenderVariantTreeNode(
                        obj,
                        entry.ChildNode,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        topSuppressedGuardKey,
                        topVisibleHeaderAnnotations,
                        BuildSiblingNoOpLeafKeysForRenderEntry(
                            entry,
                            renderableChildren,
                            directVariants),
                        entry.ChildLabelOverride);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(
                        obj,
                        syntheticLabel,
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        topSuppressedGuardKey,
                        topVisibleHeaderAnnotations);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf(
                        obj,
                        "N)",
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        topSuppressedGuardKey,
                        topVisibleHeaderAnnotations);
                }
                else
                {
                    RenderLooseVariant(
                        obj,
                        entry.DirectVariant,
                        sb,
                        new List<int> { groupNumber, childIndex++ },
                        1,
                        sharedProbabilityLine,
                        topSuppressedGuardKey,
                        topVisibleHeaderAnnotations);
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

            if (!IsUserVisibleNoOpOnly(group.TreeRoot.CommonLines))
                return false;

            return !(group.TreeRoot.DirectVariants?.Any(v => v != null &&
                IsRenderableDirectVariant(v) &&
                !ShouldSuppressAsRedundantTopLevelNoOpLeaf(group, v)) ?? false);
        }

        private static bool ShouldSuppressAsRedundantTopLevelNoOpLeaf(TopLevelVariantGroup group, VariantRenderItem item)
        {
            if (group?.TreeRoot == null || item == null)
                return false;

            return IsUserVisibleNoOpOnly(group.TreeRoot.CommonLines) &&
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
            var lines = summary.Lines?.ToList() ?? new List<string>();
            if (IsFlatOutcomeBodyLabel(summaryLabel))
            {
                PrependBodyOnlyLabelLine(lines, summaryLabel);
                summaryLabel = null;
            }

            if (!string.IsNullOrWhiteSpace(summaryLabel))
                header += $": {summaryLabel}";
            else
                header += ":";

            sb.AppendLine(header);
            AppendIndentedDisplayLines(
                sb,
                indent + "   ",
                lines,
                headerContainsProbability);
        }

        private static void RenderVariantTreeNode(
            OvrObject obj,
            VariantTreeNode node,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null,
            IEnumerable<string> inheritedVisibleHeaderAnnotations = null,
            IReadOnlySet<string> siblingNoOpLeafKeys = null,
            string nodeLabelOverride = null)
        {
            if (!IsRenderableStructuralNode(node))
                return;

            var renderableChildren = (node.Children ?? new List<VariantTreeNode>())
                .Where(IsRenderableStructuralNode)
                .ToList();
            renderableChildren = SuppressNoOpChildrenInTransparentRenderNode(
                node,
                renderableChildren,
                siblingNoOpLeafKeys,
                nodeLabelOverride);
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
            renderableDirectVariants = SuppressInheritedDuplicateNoOpDirectVariants(
                node,
                renderableChildren,
                renderableDirectVariants,
                siblingNoOpLeafKeys);

            if (renderableChildren.Count == 1 &&
                renderableDirectVariants.Count == 0 &&
                IsTransparentRenderNode(node, nodeLabelOverride))
            {
                RenderVariantTreeNode(
                    obj,
                    renderableChildren[0],
                    sb,
                    numbering,
                    depth,
                    inheritedProbabilityLine,
                    inheritedGuardKey,
                    inheritedVisibleHeaderAnnotations,
                    siblingNoOpLeafKeys);
                return;
            }

            string indent = new string(' ', depth * 3);
            string variantNumber = string.Join(".", numbering);
            var singleLeafItem = renderableChildren.Count == 0
                ? GetSingleLeafVariantItem(node)
                : null;
            var singleLeaf = singleLeafItem?.Variant;
            var singleLeafLines = BuildVariantRenderLines(singleLeafItem);
            string nodeLabel = NormalizeUserVisibleChoiceLabel(GetEffectiveNodeLabel(node, nodeLabelOverride));
            var nodeCommonLines = node.CommonLines?.ToList() ?? new List<string>();
            if (IsFlatOutcomeBodyLabel(nodeLabel))
            {
                PrependBodyOnlyLabelLine(nodeCommonLines, nodeLabel);
                nodeLabel = null;
            }

            string promotedCommonLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(nodeCommonLines)
                : null;
            string promotedSingleLeafLabel = string.IsNullOrWhiteSpace(nodeLabel)
                ? TryPromoteLeadingAnswerLine(singleLeafLines)
                : null;
            string nodeSuppressedGuardKey = MergeGuardConditionKeys(inheritedGuardKey, node.HeaderGuardKey);
            string sharedProbabilityLine = singleLeaf == null
                ? GetSharedProbabilityLine(node, inheritedProbabilityLine)
                : null;
            var sharedGuardPredicates = singleLeaf == null
                ? GetSharedGuardPredicates(node, nodeSuppressedGuardKey)
                : null;
            string sharedGuardKey = BuildGuardConditionKey(sharedGuardPredicates);
            string sharedGuardAnnotation = BuildGuardHeaderAnnotation(sharedGuardPredicates);
            string currentSuppressedGuardKey = MergeGuardConditionKeys(nodeSuppressedGuardKey, sharedGuardKey);
            bool conditionVisibleForSharedProbability =
                !string.IsNullOrWhiteSpace(sharedGuardAnnotation) ||
                HasInheritedGuardForAllVariants(node, currentSuppressedGuardKey);
            string sharedProbabilityAnnotation = BuildProbabilityHeaderAnnotation(
                sharedProbabilityLine,
                conditionVisibleForSharedProbability);
            var inheritedVisibleAnnotations = new List<string>();
            AddDistinctHeaderAnnotations(
                inheritedVisibleAnnotations,
                inheritedVisibleHeaderAnnotations);
            var descendantVisibleHeaderAnnotations = inheritedVisibleAnnotations.ToList();

            string header = $"{indent}Вариант {variantNumber}";
            if (singleLeaf != null)
            {
                var annotations = new List<string>();
                if (!string.IsNullOrWhiteSpace(node.HeaderAnnotation))
                    AddDistinctHeaderAnnotations(annotations, new[] { node.HeaderAnnotation });
                annotations = BuildHierarchicalTerminalVariantHeaderAnnotations(
                    obj,
                    singleLeafItem,
                    localHeaderAnnotations: annotations,
                    inheritedVisibleHeaderAnnotations: inheritedVisibleAnnotations,
                    suppressedProbabilityLine: inheritedProbabilityLine,
                    suppressedGuardKey: nodeSuppressedGuardKey);
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
                {
                    header += $" ({annotationText})";
                    AddDistinctHeaderAnnotations(
                        descendantVisibleHeaderAnnotations,
                        sharedAnnotations);
                }
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
                var leafLines = singleLeafLines ?? renderableDirectVariants[0].Lines;
                AppendIndentedDisplayLines(
                    sb,
                    indent + "   ",
                    leafLines,
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

            int nestedIndex = 1;
            bool wroteAny = false;
            string descendantSuppressedProbabilityLine = sharedProbabilityLine ?? inheritedProbabilityLine;
            string descendantSuppressedGuardKey = currentSuppressedGuardKey;
            var orderedEntries = BuildOrderedRenderEntries(
                node,
                renderableChildren,
                renderableDirectVariants,
                syntheticChoiceLabels,
                syntheticChildLabels);
            bool parentHeaderIsOnlyVariantNumber = Regex.IsMatch(
                header.Trim(),
                @"^Вариант\s+\d+(?:\.\d+)*:$",
                RegexOptions.CultureInvariant);

            foreach (var entry in orderedEntries)
            {
                if (ShouldSkipNoOpRenderEntryInTransparentParent(parentHeaderIsOnlyVariantNumber, entry, orderedEntries))
                    continue;

                if (wroteAny)
                    sb.AppendLine();

                if (entry.ChildNode != null)
                {
                    RenderVariantTreeNode(
                        obj,
                        entry.ChildNode,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey,
                        descendantVisibleHeaderAnnotations,
                        BuildSiblingNoOpLeafKeysForRenderEntry(
                            entry,
                            renderableChildren,
                            renderableDirectVariants),
                        entry.ChildLabelOverride);
                }
                else if (syntheticChoiceLabels.TryGetValue(entry.DirectVariant, out string syntheticLabel))
                {
                    RenderChoiceLeaf(
                        obj,
                        syntheticLabel,
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey,
                        descendantVisibleHeaderAnnotations);
                }
                else if (canPromoteNoToChoice && noChoiceCount == 1 && ShouldRenderAsNoChoiceVariant(entry.DirectVariant))
                {
                    RenderChoiceLeaf(
                        obj,
                        "N)",
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey,
                        descendantVisibleHeaderAnnotations);
                }
                else
                {
                    RenderLooseVariant(
                        obj,
                        entry.DirectVariant,
                        sb,
                        new List<int>(numbering) { nestedIndex++ },
                        depth + 1,
                        descendantSuppressedProbabilityLine,
                        descendantSuppressedGuardKey,
                        descendantVisibleHeaderAnnotations);
                }

                wroteAny = true;
            }
        }

        private static bool ShouldSkipNoOpRenderEntryInTransparentParent(
            bool parentHeaderIsOnlyVariantNumber,
            OrderedRenderEntry entry,
            List<OrderedRenderEntry> siblingEntries)
        {
            if (!parentHeaderIsOnlyVariantNumber ||
                entry == null ||
                siblingEntries == null ||
                siblingEntries.Count <= 1)
                return false;

            if (entry.DirectVariant != null)
                return !string.IsNullOrWhiteSpace(BuildUserVisibleNoOpRenderKey(entry.DirectVariant));

            if (entry.ChildNode == null)
                return false;

            if (TryBuildNoOpOnlyRenderNodeKey(entry.ChildNode, entry.ChildLabelOverride, out _))
                return true;

            return !string.IsNullOrWhiteSpace(
                BuildUserVisibleNoOpRenderKey(GetSingleLeafVariantItem(entry.ChildNode)));
        }

        private static List<VariantRenderItem> SuppressInheritedDuplicateNoOpDirectVariants(
            VariantTreeNode node,
            List<VariantTreeNode> renderableChildren,
            List<VariantRenderItem> renderableDirectVariants,
            IReadOnlySet<string> siblingNoOpLeafKeys)
        {
            if (node == null ||
                renderableChildren == null ||
                renderableChildren.Count == 0 ||
                !IsTransparentRenderNode(node))
            {
                return renderableDirectVariants ?? new List<VariantRenderItem>();
            }

            return (renderableDirectVariants ?? new List<VariantRenderItem>())
                .Where(item =>
                {
                    string key = BuildUserVisibleNoOpRenderKey(item);
                    if (string.IsNullOrWhiteSpace(key))
                        return true;

                    return siblingNoOpLeafKeys != null &&
                           siblingNoOpLeafKeys.Count > 0 &&
                           !siblingNoOpLeafKeys.Contains(key);
                })
                .ToList();
        }

        private static List<VariantTreeNode> SuppressNoOpChildrenInTransparentRenderNode(
            VariantTreeNode node,
            List<VariantTreeNode> renderableChildren,
            IReadOnlySet<string> siblingNoOpLeafKeys,
            string nodeLabelOverride = null)
        {
            if (node == null ||
                renderableChildren == null ||
                renderableChildren.Count <= 1 ||
                !IsTransparentRenderNode(node, nodeLabelOverride))
            {
                return renderableChildren ?? new List<VariantTreeNode>();
            }

            var result = new List<VariantTreeNode>();
            foreach (var child in renderableChildren)
            {
                if (TryBuildNoOpOnlyRenderNodeKey(child, out string noOpKey))
                {
                    bool hasSiblingMatch = siblingNoOpLeafKeys != null &&
                                           siblingNoOpLeafKeys.Count > 0 &&
                                           siblingNoOpLeafKeys.Contains(noOpKey);
                    if (hasSiblingMatch || siblingNoOpLeafKeys == null || siblingNoOpLeafKeys.Count == 0)
                        continue;
                }

                result.Add(child);
            }

            return result.Count == 0 ? renderableChildren : result;
        }

        private static bool TryBuildNoOpOnlyRenderNodeKey(
            VariantTreeNode node,
            out string key)
        {
            return TryBuildNoOpOnlyRenderNodeKey(node, null, out key);
        }

        private static bool TryBuildNoOpOnlyRenderNodeKey(
            VariantTreeNode node,
            string labelOverride,
            out string key)
        {
            key = null;
            if (node == null || HasUserVisibleNodeLabelOrAnnotation(node, labelOverride))
                return false;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (node.CommonLines != null && node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                string commonKey = BuildUserVisibleNoOpLineKey(node.CommonLines);
                if (string.IsNullOrWhiteSpace(commonKey))
                    return false;

                keys.Add(commonKey);
            }

            foreach (var item in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                if (item == null)
                    continue;

                string itemKey = BuildUserVisibleNoOpRenderKey(item);
                if (string.IsNullOrWhiteSpace(itemKey) && IsRenderableDirectVariant(item))
                    return false;

                if (!string.IsNullOrWhiteSpace(itemKey))
                    keys.Add(itemKey);
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                if (child == null)
                    continue;

                if (!TryBuildNoOpOnlyRenderNodeKey(child, out string childKey))
                    return false;

                if (!string.IsNullOrWhiteSpace(childKey))
                    keys.Add(childKey);
            }

            if (keys.Count != 1)
                return false;

            key = keys.First();
            return true;
        }

        private static bool IsTransparentRenderNode(VariantTreeNode node, string labelOverride = null)
        {
            if (node == null)
                return false;

            if (node.PreserveTransparentWrapper)
                return false;

            if (HasUserVisibleNodeLabelOrAnnotation(node, labelOverride))
                return false;

            return node.CommonLines == null ||
                   !node.CommonLines.Any(line => !string.IsNullOrWhiteSpace(line));
        }

        private static HashSet<string> BuildSiblingNoOpLeafKeysForRenderEntry(
            OrderedRenderEntry currentEntry,
            IEnumerable<VariantTreeNode> siblingChildren,
            IEnumerable<VariantRenderItem> siblingDirectVariants)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in siblingDirectVariants ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (currentEntry?.DirectVariant != null && ReferenceEquals(item, currentEntry.DirectVariant))
                    continue;

                string key = BuildUserVisibleNoOpRenderKey(item);
                if (!string.IsNullOrWhiteSpace(key))
                    result.Add(key);
            }

            foreach (var child in siblingChildren ?? Enumerable.Empty<VariantTreeNode>())
            {
                if (currentEntry?.ChildNode != null && ReferenceEquals(child, currentEntry.ChildNode))
                    continue;

                foreach (var key in CollectNoOpLeafKeys(child))
                    result.Add(key);
            }

            return result;
        }

        private static IEnumerable<string> CollectNoOpLeafKeys(VariantTreeNode node)
        {
            if (node == null)
                yield break;

            string nodeNoOpKey = BuildUserVisibleNoOpRenderKey(node);
            if (!string.IsNullOrWhiteSpace(nodeNoOpKey))
                yield return nodeNoOpKey;

            foreach (var item in node.DirectVariants ?? new List<VariantRenderItem>())
            {
                string key = BuildUserVisibleNoOpRenderKey(item);
                if (!string.IsNullOrWhiteSpace(key))
                    yield return key;
            }

            foreach (var child in node.Children ?? new List<VariantTreeNode>())
            {
                foreach (var key in CollectNoOpLeafKeys(child))
                    yield return key;
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
            OvrObject obj,
            string label,
            VariantRenderItem item,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null,
            IEnumerable<string> inheritedVisibleHeaderAnnotations = null)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            var annotations = BuildHierarchicalTerminalVariantHeaderAnnotations(
                obj,
                item,
                localHeaderAnnotations: null,
                inheritedVisibleHeaderAnnotations: inheritedVisibleHeaderAnnotations,
                suppressedProbabilityLine: inheritedProbabilityLine,
                suppressedGuardKey: inheritedGuardKey);
            string annotationText = BuildVariantHeaderAnnotationText(annotations);
            if (!string.IsNullOrWhiteSpace(annotationText))
                header += $" ({annotationText})";
            var lines = item?.Lines?.ToList() ?? new List<string>();
            string displayLabel = NormalizeUserVisibleChoiceLabel(label);
            if (IsFlatOutcomeBodyLabel(displayLabel))
            {
                PrependBodyOnlyLabelLine(lines, displayLabel);
                displayLabel = null;
            }
            AddDistinctSupplementalLines(lines, item?.SupplementalLines);

            header += string.IsNullOrWhiteSpace(displayLabel) ? ":" : $": {displayLabel}";
            sb.AppendLine(header);

            if (lines.Any(line => !string.IsNullOrWhiteSpace(line)) && !IsNoOpOnly(lines))
            {
                bool headerContainsProbability = VariantHeaderContainsProbability(header);
                AppendIndentedDisplayLines(
                    sb,
                    indent + "   ",
                    lines,
                    headerContainsProbability);
            }
        }

        private static IEnumerable<VariantRenderItem> OrderDirectVariants(IEnumerable<VariantRenderItem> variants)
        {
            return (variants ?? Enumerable.Empty<VariantRenderItem>())
                .OrderBy(v => TryGetRanalouPrisonerChoiceValue(v?.Variant, out byte choiceValue)
                    ? choiceValue
                    : byte.MaxValue)
                .ThenBy(v => v?.Lines?.Count ?? 0)
                .ThenByDescending(v => v?.Variant?.ProbabilityDenominator ?? 1)
                .ThenByDescending(v => v?.Variant?.ProbabilityNumerator ?? 1)
                .ThenBy(v => GetPathOrderKey(v?.Variant));
        }

        private static void RenderLooseVariant(
            OvrObject obj,
            VariantRenderItem item,
            StringBuilder sb,
            List<int> numbering,
            int depth,
            string inheritedProbabilityLine = null,
            string inheritedGuardKey = null,
            IEnumerable<string> inheritedVisibleHeaderAnnotations = null)
        {
            string indent = new string(' ', depth * 3);
            string header = $"{indent}Вариант {string.Join(".", numbering)}";
            var annotations = BuildHierarchicalTerminalVariantHeaderAnnotations(
                obj,
                item,
                localHeaderAnnotations: null,
                inheritedVisibleHeaderAnnotations: inheritedVisibleHeaderAnnotations,
                suppressedProbabilityLine: inheritedProbabilityLine,
                suppressedGuardKey: inheritedGuardKey);
            string annotationText = BuildVariantHeaderAnnotationText(annotations);
            if (!string.IsNullOrWhiteSpace(annotationText))
                header += $" ({annotationText})";

            var lines = item?.Lines?.ToList() ?? new List<string>();
            string promotedLabel = TryPromoteLeadingAnswerLine(lines);
            AddDistinctSupplementalLines(lines, item?.SupplementalLines);
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
            if (!IsPartyScanAnswerLine(candidate) || IsFlatOutcomeBodyLabel(candidate))
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

            if (string.Equals(fileNameOnly, "AREAD4.OVR", StringComparison.OrdinalIgnoreCase))
            {
                if (obj == null ||
                    obj.X != 7 ||
                    obj.Y != 1 ||
                    obj.PatchAddress != 0x0217)
                {
                    return null;
                }

                string areaD4Answer = TryReadFiniteInputValidationAnswer(filename, config, obj.PatchAddress.Value);
                if (string.IsNullOrWhiteSpace(areaD4Answer))
                    return null;

                return BuildSpoilerAnswerLine(areaD4Answer);
            }

            if (string.Equals(fileNameOnly, "DEMON.OVR", StringComparison.OrdinalIgnoreCase))
            {
                if (obj == null ||
                    obj.X != 6 ||
                    obj.Y != 0)
                {
                    return null;
                }

                string demonAnswer = TryReadSentinelInputValidationAnswer(filename, config, 0x0020);
                if (string.IsNullOrWhiteSpace(demonAnswer))
                    return null;

                return BuildSpoilerAnswerLine(demonAnswer);
            }

            return null;
        }

        private static string TryBuildSpecialPlayerExplanation(string fileNameOnly, OvrObject obj)
        {
            if (string.Equals(fileNameOnly, "AREAA3.OVR", StringComparison.OrdinalIgnoreCase))
            {
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

            if (string.Equals(fileNameOnly, "AREAE1.OVR", StringComparison.OrdinalIgnoreCase))
            {
                if (obj == null ||
                    obj.X != 9 ||
                    obj.Y != 13 ||
                    obj.PatchAddress != 0x0322)
                {
                    return null;
                }

                return InlineNoteStyleCodec.EncodeWheelRewardExplanationText(
                    BuildAreaE1JudgementStatueExplanation());
            }

            return null;
        }

        private static string TryBuildSpecialFullNotes(string fileNameOnly, OvrObject obj, bool useHierarchical)
        {
            string sorpigalLeprechaunNote = TryBuildSorpigalLeprechaunGuideNote(
                fileNameOnly,
                obj,
                useHierarchical);
            if (!string.IsNullOrWhiteSpace(sorpigalLeprechaunNote))
                return sorpigalLeprechaunNote;

            return null;
        }

        private static string TryBuildSorpigalLeprechaunGuideNote(
            string fileNameOnly,
            OvrObject obj,
            bool useHierarchical)
        {
            if (!string.Equals(fileNameOnly, "SORPIGAL.OVR", StringComparison.OrdinalIgnoreCase) ||
                obj == null ||
                obj.X != 11 ||
                obj.Y != 3 ||
                obj.PatchAddress != 0x0160 ||
                obj.PathVariants == null)
            {
                return null;
            }

            string promptText = obj.PathVariants.Values
                .Where(variant => variant != null)
                .SelectMany(variant => DecodeNoteTexts(variant.Texts))
                .FirstOrDefault(text =>
                    !string.IsNullOrWhiteSpace(text) &&
                    text.IndexOf("TENACIOUS LEPRECHAUN", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrWhiteSpace(promptText))
                return null;

            var paidChoiceTargets = obj.PathVariants.Values
                .Where(variant =>
                    variant != null &&
                    variant.HasTeleportTarget &&
                    variant.PartyEffects != null &&
                    variant.PartyEffects.Count > 0)
                .Select(variant => new
                {
                    Choice = TryGetSorpigalStoredTownChoice(variant),
                    Teleport = variant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte).GetTeleportDescription()
                })
                .Where(entry =>
                    entry.Choice.HasValue &&
                    entry.Choice.Value >= 1 &&
                    entry.Choice.Value <= 5 &&
                    !string.IsNullOrWhiteSpace(entry.Teleport))
                .GroupBy(entry => entry.Choice.Value)
                .ToDictionary(group => group.Key, group => group.First().Teleport);

            if (paidChoiceTargets.Count != 5)
                return null;

            string noGemTeleport = obj.PathVariants.Values
                .Where(variant =>
                    variant != null &&
                    variant.HasTeleportTarget &&
                    (variant.PartyEffects == null || variant.PartyEffects.Count == 0))
                .Select(variant => variant.ToOvrObject(obj.X, obj.Y, obj.DirectionByte).GetTeleportDescription())
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (string.IsNullOrWhiteSpace(noGemTeleport))
                return null;

            return useHierarchical
                ? BuildSorpigalLeprechaunGuideHierarchicalNote(promptText, paidChoiceTargets, noGemTeleport)
                : BuildSorpigalLeprechaunGuideFlatNote(promptText, paidChoiceTargets, noGemTeleport);
        }

        private static int? TryGetSorpigalStoredTownChoice(PathVariantInfo variant)
        {
            foreach (var choice in variant?.BranchChoices ?? new List<BranchChoice>())
            {
                string combined = $"{choice?.Condition} {choice?.DisplayHeaderAnnotation}";
                var match = Regex.Match(
                    combined,
                    @"\[0xCC3E\]\s*=\s*0x(?<hex>[0-9A-Fa-f]{2})",
                    RegexOptions.CultureInvariant);
                if (!match.Success)
                    continue;

                int asciiValue = int.Parse(
                    match.Groups["hex"].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                int choiceNumber = asciiValue - '0';
                if (choiceNumber >= 1 && choiceNumber <= 5)
                    return choiceNumber;
            }

            return null;
        }

        private static string BuildSorpigalLeprechaunGuideHierarchicalNote(
            string promptText,
            IReadOnlyDictionary<int, string> paidChoiceTargets,
            string noGemTeleport)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            sb.AppendLine("Вариант 1:");
            AppendIndentedDisplayLines(sb, "   ", SplitDisplayLines(promptText), headerContainsProbability: false);
            sb.AppendLine();
            sb.AppendLine("   Вариант 1.1: ESC)");

            for (int choice = 1; choice <= 5; choice++)
            {
                sb.AppendLine();
                sb.AppendLine($"   Вариант 1.{choice + 1}: {choice})");
                sb.AppendLine("      Вариант 1." + (choice + 1) + ".1 (GEM есть хотя бы у одного персонажа партии):");
                sb.AppendLine("         " + BuildSorpigalGemChargeNote());
                sb.AppendLine($"         {paidChoiceTargets[choice]}");
                sb.AppendLine();
                sb.AppendLine("      Вариант 1." + (choice + 1) + ".2 (GEM нет ни у одного персонажа партии):");
                sb.AppendLine($"         {noGemTeleport}");
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildSorpigalLeprechaunGuideFlatNote(
            string promptText,
            IReadOnlyDictionary<int, string> paidChoiceTargets,
            string noGemTeleport)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Эта ячейка содержит различные варианты текста:");
            int variantNumber = 1;

            sb.AppendLine($"Вариант {variantNumber++} (выбор ESC):");
            AppendLines(sb, SplitDisplayLines(promptText));

            for (int choice = 1; choice <= 5; choice++)
            {
                sb.AppendLine();
                sb.AppendLine($"Вариант {variantNumber++} (выбор {choice}; GEM есть хотя бы у одного персонажа партии):");
                AppendLines(sb, SplitDisplayLines(promptText));
                sb.AppendLine(BuildSorpigalGemChargeNote());
                sb.AppendLine(paidChoiceTargets[choice]);

                sb.AppendLine();
                sb.AppendLine($"Вариант {variantNumber++} (выбор {choice}; GEM нет ни у одного персонажа партии):");
                AppendLines(sb, SplitDisplayLines(promptText));
                sb.AppendLine(noGemTeleport);
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildSorpigalGemChargeNote()
        {
            return InlineNoteStyleCodec.EncodeSubtleMechanicsNoteText(
                "*** У первого персонажа партии, у которого есть GEM, уменьшается GEMS на 1 ***");
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

        private static string BuildAreaE1JudgementStatueExplanation()
        {
            return string.Join("\n", new[]
            {
                "===================================================================",
                "|| ПОЯСНЕНИЕ К СТАТУЕ ПРАВОСУДИЯ                                  ||",
                "||                                                                ||",
                "|| Статуя просит выбрать персонажа 1-6.                           ||",
                "|| Если выбранный персонаж ещё не достоин, ответ будет:           ||",
                "|| NOT WORTHY!                                                    ||",
                "||                                                                ||",
                "|| Персонаж становится достойным после того как  были  найдены    ||",
                "|| все 6 узников. В этом случае он получает награду за то,        ||",
                "|| сколько узников было осуждено в соответствии с мирровозрением  ||",
                "|| (ALIGNMENT) персонажа (в этом случае узник считается зачтённым)||",
                "||                                                                ||",
                "|| Зачтено | EXP | Дополнительная награда                         ||",
                "|| 0/6     | 0   | нет                                            ||",
                "|| 1/6     | 16  | нет                                            ||",
                "|| 2/6     | 32  | нет                                            ||",
                "|| 3/6     | 48  | нет                                            ||",
                "|| 4/6     | 64  | случайная характеристика +4                    ||",
                "|| 5/6     | 80  | случайная характеристика +4                    ||",
                "|| 6/6     | 96  | случайная характеристика +4                    ||",
                "||                                                                ||",
                "|| При 4/6 и выше дополнительно выбирается одна характеристика:   ||",
                "|| INTELLECT, MIGHT, PERSONALITY, ENDURANCE, SPEED,               ||",
                "|| ACCURACY или LUCK, и повышается на +4 (Игровой баг - написано  ||",
                "|| +3, а по факту на +4)                                          ||",
                "||                                                                ||",
                "|| Бонус даётся только если выбранная характеристика ниже 43.     ||",
                "|| Если уже 43 или выше, он не растёт.                            ||",
                "==================================================================="
            });
        }

        private static string NormalizeAreaE1JudgementStatueTechnicalNoteText(
            string fileNameOnly,
            OvrObject obj,
            string noteText,
            List<NoteInlineStyleSpan> inlineStyles)
        {
            if (!IsAreaE1JudgementStatueObject(fileNameOnly, obj) ||
                string.IsNullOrEmpty(noteText))
            {
                return noteText;
            }

            const string oldNotWorthyCondition =
                "хотя бы у одного персонажа партии поле прогресса линейки квестов волшебника RANALOU (+0x71) != 127";
            const string oldWorthyCondition =
                "ни у одного персонажа партии поле прогресса линейки квестов волшебника RANALOU (+0x71) != 127";
            const string notWorthyCondition =
                "Условия квеста RANALOU не выполнены";
            const string worthyCondition =
                "Все условия квеста RANALOU выполнены";
            const string sourceRanalouRewardEffect =
                "-=*У выбранного персонажа партии прогресс линейки квестов волшебника RANALOU (+0x71) становится равным 0x80*=-";
            const string displayRanalouRewardEffect =
                "-=*У выбранного персонажа партии прогресс линейки квестов волшебника RANALOU становится равным 0x80 (сбрасывается)*=-";
            const string questProgressRange = "0/1/2/3/4/5/6";
            const string experienceRange = "0/16/32/48/64/80/96";
            const string statOptions =
                ",+3 INTELLECT\n,+3 MIGHT\n,+3 PERSONALITY\n,+3 ENDURANCE\n,+3 SPEED\n,+3 ACCURACY\n,+3 LUCK";

            noteText = noteText
                .Replace(oldNotWorthyCondition, notWorthyCondition)
                .Replace(oldWorthyCondition, worthyCondition);
            noteText = RemoveAreaE1JudgementStatueScoreRangeLineMarkers(noteText);

            noteText = noteText.Replace(
                "YOUR ACTIONS REFLECT YOUR VIEWS   OF 6",
                "YOUR ACTIONS REFLECT YOUR VIEWS " + questProgressRange + " OF 6");

            noteText = Regex.Replace(
                noteText,
                @"(?m)^(\s*)\+\s*$\r?\n(\s*)EXPERIENCE\s*$",
                match => $"{match.Groups[1].Value}+{experienceRange} EXPERIENCE",
                RegexOptions.CultureInvariant);

            noteText = Regex.Replace(
                noteText,
                @"(?m)^(\s*)\+\s+EXPERIENCE\b(.*)$",
                match => $"{match.Groups[1].Value}+{experienceRange} EXPERIENCE{match.Groups[2].Value}",
                RegexOptions.CultureInvariant);

            noteText = ApplyAreaE1JudgementStatueScoreSpecificRanges(noteText);

            noteText = Regex.Replace(
                noteText,
                Regex.Escape(sourceRanalouRewardEffect) + @"\r?\n(?:,\+3)?EXPERIENCE(?=\r?\n(?:\r?\n|Вариант|=)|$)",
                displayRanalouRewardEffect + "\n" + statOptions,
                RegexOptions.CultureInvariant);

            noteText = noteText.Replace(sourceRanalouRewardEffect, displayRanalouRewardEffect);

            noteText = Regex.Replace(
                noteText,
                @"(?m)^(\s*Вариант\s+\d+(?:\.\d+)*:\r?\n)(\s*)(?:,\+3)?EXPERIENCE\s*$",
                match =>
                {
                    string indent = match.Groups[2].Value;
                    string indentedStatOptions = string.Join(
                        "\n",
                        statOptions.Split('\n').Select(line => indent + line));
                    return match.Groups[1].Value + indentedStatOptions + "\n";
                },
                RegexOptions.CultureInvariant);

            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, questProgressRange);
            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, experienceRange);
            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, "0/1/2/3");
            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, "4/5/6");
            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, "0/16/32/48");
            AddGeneratedOverlaySubstitutionSpans(noteText, inlineStyles, "64/80/96");
            RefreshAreaE1JudgementExplanationStyleSpan(noteText, inlineStyles);

            return noteText;
        }

        private static string RemoveAreaE1JudgementStatueScoreRangeLineMarkers(string noteText)
        {
            return string.IsNullOrEmpty(noteText)
                ? noteText
                : noteText
                    .Replace(AreaE1JudgementStatueLowScoreLineMarker, string.Empty)
                    .Replace(AreaE1JudgementStatueHighScoreLineMarker, string.Empty);
        }

        private enum AreaE1JudgementStatueScoreRange
        {
            None = 0,
            Low = 1,
            High = 2
        }

        private static string ApplyAreaE1JudgementStatueScoreSpecificRanges(string noteText)
        {
            if (string.IsNullOrEmpty(noteText) ||
                (noteText.IndexOf("0/16/32/48 EXPERIENCE", StringComparison.Ordinal) < 0 &&
                 noteText.IndexOf("64/80/96 EXPERIENCE", StringComparison.Ordinal) < 0))
            {
                return noteText;
            }

            string normalized = noteText.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var activeRanges = new List<(int Indent, AreaE1JudgementStatueScoreRange Range)>();
            var sb = new StringBuilder(normalized.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var headerMatch = Regex.Match(
                    line,
                    @"^(\s*)Вариант\s+\d+(?:\.\d+)*(?:\s+\((.*?)\))?:\s*$",
                    RegexOptions.CultureInvariant);

                if (headerMatch.Success)
                {
                    int indent = headerMatch.Groups[1].Value.Length;
                    activeRanges.RemoveAll(entry => entry.Indent >= indent);

                    var headerRange = GetAreaE1JudgementStatueScoreRangeFromHeader(headerMatch.Groups[2].Value);
                    if (headerRange != AreaE1JudgementStatueScoreRange.None)
                        activeRanges.Add((indent, headerRange));
                }

                var activeRange = activeRanges.Count == 0
                    ? AreaE1JudgementStatueScoreRange.None
                    : activeRanges[activeRanges.Count - 1].Range;
                if (activeRange != AreaE1JudgementStatueScoreRange.None)
                    line = ApplyAreaE1JudgementStatueScoreRangeToLine(line, activeRange);

                if (i > 0)
                    sb.Append('\n');
                sb.Append(line);
            }

            return sb.ToString();
        }

        private static AreaE1JudgementStatueScoreRange GetAreaE1JudgementStatueScoreRangeFromHeader(
            string headerAnnotation)
        {
            if (string.IsNullOrWhiteSpace(headerAnnotation))
                return AreaE1JudgementStatueScoreRange.None;

            if (headerAnnotation.IndexOf("0/16/32/48 EXPERIENCE", StringComparison.Ordinal) >= 0)
                return AreaE1JudgementStatueScoreRange.Low;

            if (headerAnnotation.IndexOf("64/80/96 EXPERIENCE", StringComparison.Ordinal) >= 0)
                return AreaE1JudgementStatueScoreRange.High;

            return AreaE1JudgementStatueScoreRange.None;
        }

        private static string ApplyAreaE1JudgementStatueScoreRangeToLine(
            string line,
            AreaE1JudgementStatueScoreRange range)
        {
            string scoreRange = range == AreaE1JudgementStatueScoreRange.High
                ? "4/5/6"
                : "0/1/2/3";
            string experienceRange = range == AreaE1JudgementStatueScoreRange.High
                ? "64/80/96"
                : "0/16/32/48";

            line = line.Replace(
                "YOUR ACTIONS REFLECT YOUR VIEWS 0/1/2/3/4/5/6 OF 6",
                "YOUR ACTIONS REFLECT YOUR VIEWS " + scoreRange + " OF 6");

            return Regex.Replace(
                line,
                @"^(\s*)\+0/16/32/48/64/80/96(\s+EXPERIENCE\b.*)?$",
                match => match.Groups[1].Value + "+" + experienceRange + match.Groups[2].Value,
                RegexOptions.CultureInvariant);
        }

        private static void AddGeneratedOverlaySubstitutionSpans(
            string noteText,
            List<NoteInlineStyleSpan> inlineStyles,
            string visibleText)
        {
            if (string.IsNullOrEmpty(noteText) ||
                string.IsNullOrEmpty(visibleText) ||
                inlineStyles == null)
            {
                return;
            }

            int searchStart = 0;
            while (searchStart < noteText.Length)
            {
                int index = noteText.IndexOf(visibleText, searchStart, StringComparison.Ordinal);
                if (index < 0)
                    break;

                inlineStyles.Add(new NoteInlineStyleSpan
                {
                    Start = index,
                    Length = visibleText.Length,
                    Kind = NoteInlineStyleKind.GeneratedOverlaySubstitution
                });
                searchStart = index + visibleText.Length;
            }
        }

        private static void RefreshAreaE1JudgementExplanationStyleSpan(
            string noteText,
            List<NoteInlineStyleSpan> inlineStyles)
        {
            if (string.IsNullOrEmpty(noteText) || inlineStyles == null)
                return;

            const string title = "ПОЯСНЕНИЕ К СТАТУЕ ПРАВОСУДИЯ";
            int titleIndex = noteText.IndexOf(title, StringComparison.Ordinal);
            if (titleIndex < 0)
                return;

            int titleLineStart = noteText.LastIndexOf('\n', titleIndex);
            titleLineStart = titleLineStart < 0 ? 0 : titleLineStart + 1;
            int previousLineStart = titleLineStart > 1
                ? noteText.LastIndexOf('\n', titleLineStart - 2)
                : -1;
            int start = previousLineStart < 0 ? titleLineStart : previousLineStart + 1;

            int end = noteText.Length;
            Match closingBorder = Regex.Match(
                noteText.Substring(titleIndex),
                @"(?m)^={10,}\s*$",
                RegexOptions.CultureInvariant);
            if (closingBorder.Success)
                end = titleIndex + closingBorder.Index + closingBorder.Length;

            if (end <= start)
                return;

            inlineStyles.RemoveAll(span =>
                span != null &&
                (span.Kind == NoteInlineStyleKind.WheelRewardExplanation ||
                 (span.Start < end && span.Start + span.Length > start)));

            inlineStyles.Add(new NoteInlineStyleSpan
            {
                Start = start,
                Length = end - start,
                Kind = NoteInlineStyleKind.WheelRewardExplanation
            });
        }

        private static bool IsAreaE1JudgementStatueObject(string fileNameOnly, OvrObject obj)
        {
            return string.Equals(fileNameOnly, "AREAE1.OVR", StringComparison.OrdinalIgnoreCase) &&
                   IsAreaE1JudgementStatueObject(obj);
        }

        private static bool IsAreaE1JudgementStatueObject(OvrObject obj)
        {
            return obj != null &&
                   obj.X == 9 &&
                   obj.Y == 13 &&
                   obj.PatchAddress == 0x0322;
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

        private static string TryReadFiniteInputValidationAnswer(string filename, OvrFileConfig config, uint startAddress)
        {
            if (string.IsNullOrWhiteSpace(filename) || config == null || !File.Exists(filename))
                return null;

            try
            {
                using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                uint scanEnd = (uint)Math.Min(br.BaseStream.Length, startAddress + 0x200);
                for (uint address = startAddress; address + 16 < scanEnd; address++)
                {
                    if (!TryReadFiniteInputValidationAnswerPattern(
                            br,
                            address,
                            scanEnd,
                            out ushort answerAddress,
                            out int answerLength,
                            out byte storedToAnswerAddValue))
                    {
                        continue;
                    }

                    return TryReadShiftedOverlayText(
                        filename,
                        config,
                        answerAddress,
                        answerLength,
                        storedToAnswerAddValue);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string TryReadSentinelInputValidationAnswer(string filename, OvrFileConfig config, uint startAddress)
        {
            if (string.IsNullOrWhiteSpace(filename) || config == null || !File.Exists(filename))
                return null;

            try
            {
                using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                uint scanEnd = (uint)Math.Min(br.BaseStream.Length, startAddress + 0x200);
                for (uint address = startAddress; address + 16 < scanEnd; address++)
                {
                    if (!TryReadSentinelInputValidationAnswerPattern(
                            br,
                            address,
                            scanEnd,
                            out ushort answerAddress,
                            out byte storedToAnswerAddValue))
                    {
                        continue;
                    }

                    return TryReadShiftedOverlayText(
                        filename,
                        config,
                        answerAddress,
                        32,
                        storedToAnswerAddValue);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool TryReadSentinelInputValidationAnswerPattern(
            BinaryReader br,
            uint loopStartAddress,
            uint scanEnd,
            out ushort answerAddress,
            out byte storedToAnswerAddValue)
        {
            const ushort inputBufferAddress = 0x3CB8;

            answerAddress = 0;
            storedToAnswerAddValue = 0;

            if (!TryReadFileByte(br, loopStartAddress, out byte movOpcode) ||
                movOpcode != 0x8A ||
                !TryReadFileByte(br, loopStartAddress + 1, out byte movModRm) ||
                movModRm != 0x87 ||
                !TryReadFileWord(br, loopStartAddress + 2, out answerAddress) ||
                answerAddress < OvrFileConfig.OverlayTextStartAddress)
            {
                return false;
            }

            uint cursor = loopStartAddress + 4;
            if (!TryReadFileByte(br, cursor, out byte orOpcode) ||
                orOpcode != 0x0A ||
                !TryReadFileByte(br, cursor + 1, out byte orModRm) ||
                orModRm != 0xC0 ||
                !TryReadFileByte(br, cursor + 2, out byte zeroTerminatorJumpOpcode) ||
                zeroTerminatorJumpOpcode != 0x74)
            {
                return false;
            }

            cursor += 4;
            if (!TryReadFileByte(br, cursor, out byte transformOpcode) ||
                !TryReadFileByte(br, cursor + 1, out byte transformValue))
            {
                return false;
            }

            if (transformOpcode == 0x04)
            {
                storedToAnswerAddValue = transformValue;
            }
            else if (transformOpcode == 0x2C)
            {
                storedToAnswerAddValue = unchecked((byte)-transformValue);
            }
            else
            {
                return false;
            }

            cursor += 2;
            if (!TryReadFileByte(br, cursor, out byte cmpOpcode) ||
                cmpOpcode != 0x3A ||
                !TryReadFileByte(br, cursor + 1, out byte cmpModRm) ||
                cmpModRm != 0x87 ||
                !TryReadFileWord(br, cursor + 2, out ushort comparedAddress) ||
                comparedAddress != inputBufferAddress)
            {
                return false;
            }

            cursor += 4;
            if (!TryReadFileByte(br, cursor, out byte failureJumpOpcode) ||
                failureJumpOpcode != 0x75)
            {
                return false;
            }

            cursor += 2;
            if (!TryReadFileByte(br, cursor, out byte incOpcode) ||
                incOpcode != 0xFE ||
                !TryReadFileByte(br, cursor + 1, out byte incModRm) ||
                incModRm != 0xC3)
            {
                return false;
            }

            cursor += 2;
            if (!TryReadFileByte(br, cursor, out byte loopJumpOpcode) ||
                loopJumpOpcode != 0xEB ||
                !TryReadFileByte(br, cursor + 1, out byte relativeTarget))
            {
                return false;
            }

            uint nextAddress = cursor + 2;
            uint branchTarget = unchecked((uint)((int)nextAddress + (sbyte)relativeTarget));
            return branchTarget == loopStartAddress && nextAddress <= scanEnd;
        }

        private static bool TryReadFiniteInputValidationAnswerPattern(
            BinaryReader br,
            uint loopStartAddress,
            uint scanEnd,
            out ushort answerAddress,
            out int answerLength,
            out byte storedToAnswerAddValue)
        {
            const ushort inputBufferAddress = 0x3CB8;

            answerAddress = 0;
            answerLength = 0;
            storedToAnswerAddValue = 0;

            if (!TryReadFileByte(br, loopStartAddress, out byte movOpcode) ||
                movOpcode != 0x8A ||
                !TryReadFileByte(br, loopStartAddress + 1, out byte movModRm) ||
                movModRm != 0x87 ||
                !TryReadFileWord(br, loopStartAddress + 2, out ushort inputAddress) ||
                inputAddress != inputBufferAddress)
            {
                return false;
            }

            uint cursor = loopStartAddress + 4;
            if (!TryReadFileByte(br, cursor, out byte transformOpcode) ||
                !TryReadFileByte(br, cursor + 1, out byte transformValue))
            {
                return false;
            }

            if (transformOpcode == 0x04)
            {
                storedToAnswerAddValue = unchecked((byte)-transformValue);
            }
            else if (transformOpcode == 0x2C)
            {
                storedToAnswerAddValue = transformValue;
            }
            else
            {
                return false;
            }

            cursor += 2;
            if (TryReadFileByte(br, cursor, out byte maybeClc) && maybeClc == 0xF8)
                cursor++;

            if (!TryReadFileByte(br, cursor, out byte cmpOpcode) ||
                cmpOpcode != 0x3A ||
                !TryReadFileByte(br, cursor + 1, out byte cmpModRm) ||
                cmpModRm != 0x87 ||
                !TryReadFileWord(br, cursor + 2, out answerAddress) ||
                answerAddress < OvrFileConfig.OverlayTextStartAddress)
            {
                return false;
            }

            cursor += 4;
            if (!TryReadFileByte(br, cursor, out byte failureJumpOpcode) ||
                failureJumpOpcode != 0x75)
            {
                return false;
            }

            cursor += 2;
            if (!TryReadFileByte(br, cursor, out byte incOpcode) ||
                incOpcode != 0xFE ||
                !TryReadFileByte(br, cursor + 1, out byte incModRm) ||
                incModRm != 0xC3 ||
                !TryReadFileByte(br, cursor + 2, out byte cmpBlOpcode) ||
                cmpBlOpcode != 0x80 ||
                !TryReadFileByte(br, cursor + 3, out byte cmpBlModRm) ||
                cmpBlModRm != 0xFB ||
                !TryReadFileByte(br, cursor + 4, out byte loopLength) ||
                loopLength == 0 ||
                loopLength > 0x40)
            {
                return false;
            }

            answerLength = loopLength;
            cursor += 5;

            if (!TryReadFileByte(br, cursor, out byte loopJumpOpcode) ||
                loopJumpOpcode != 0x72 ||
                !TryReadFileByte(br, cursor + 1, out byte relativeTarget))
            {
                return false;
            }

            uint nextAddress = cursor + 2;
            uint branchTarget = unchecked((uint)((int)nextAddress + (sbyte)relativeTarget));
            return branchTarget == loopStartAddress && nextAddress <= scanEnd;
        }

        private static bool TryReadFileByte(BinaryReader br, uint address, out byte value)
        {
            value = 0;
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                address >= br.BaseStream.Length)
            {
                return false;
            }

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(address, SeekOrigin.Begin);
                value = br.ReadByte();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
        }

        private static bool TryReadFileWord(BinaryReader br, uint address, out ushort value)
        {
            value = 0;
            if (br?.BaseStream == null ||
                !br.BaseStream.CanSeek ||
                address + 1 >= br.BaseStream.Length)
            {
                return false;
            }

            long originalPosition = br.BaseStream.Position;
            try
            {
                br.BaseStream.Seek(address, SeekOrigin.Begin);
                value = br.ReadUInt16();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                br.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            }
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

            InsertLostDirectionEffectNoteAfterLostText(result);
            return result;
        }

        private static void InsertLostDirectionEffectNoteAfterLostText(List<string> narrativeLines)
        {
            if (narrativeLines == null || narrativeLines.Count == 0)
                return;

            string encodedEffectLine = InlineNoteStyleCodec.EncodeLostDirectionEffectNoteText(LostDirectionEffectLine);
            for (int i = 0; i < narrativeLines.Count; i++)
            {
                string line = narrativeLines[i];
                if (string.IsNullOrEmpty(line) ||
                    line.IndexOf(LostTextLine, StringComparison.Ordinal) < 0 ||
                    line.IndexOf(LostDirectionEffectLine, StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                var parts = SplitDisplayLines(line).ToList();
                if (parts.Count == 0)
                    continue;

                var rebuilt = new List<string>();
                bool inserted = false;
                foreach (string part in parts)
                {
                    rebuilt.Add(part);
                    if (!inserted &&
                        string.Equals(part.Trim(), LostTextLine, StringComparison.Ordinal))
                    {
                        rebuilt.Add(encodedEffectLine);
                        inserted = true;
                    }
                }

                if (inserted)
                    narrativeLines[i] = string.Join("\n", rebuilt);
            }
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

            if (ShouldDisplayExternalJumpLines(obj))
                lines.AddRange(BuildExternalJumpLines(obj));

            return lines;
        }

        private static bool ShouldDisplayExternalJumpLines(OvrObject obj)
        {
            var targets = GetObjectExternalJumpTargets(obj).ToList();
            if (obj == null || targets.Count == 0)
                return false;

            if (targets.All(IsTechnicalRenderPatchTarget))
                return false;

            if (HasVisibleTextInfo(obj))
                return false;

            if (HasGameplayInfo(obj))
                return false;

            return true;
        }

        private static bool HasVisibleTextInfo(OvrObject obj)
        {
            return
                (obj.PathTexts != null && obj.PathTexts.Any(kvp => kvp.Value != null && kvp.Value.Any(text => !string.IsNullOrWhiteSpace(text)))) ||
                (obj.PathTextsOrdered != null && obj.PathTextsOrdered.Any(kvp => kvp.Value != null && kvp.Value.Any(text => !string.IsNullOrWhiteSpace(text)))) ||
                (obj.PathVariants != null && obj.PathVariants.Values.Any(variant =>
                    variant?.Texts != null && variant.Texts.Any(text => !string.IsNullOrWhiteSpace(text))));
        }

        private static bool HasGameplayInfo(OvrObject obj)
        {
            if (obj.HasTeleportTarget ||
                obj.CallsRandomEncounter ||
                obj.RandomEncounterMonsterPowerCap.HasValue ||
                obj.RandomEncounterMonsterLevelCap.HasValue ||
                obj.RandomEncounterMonsterBatchCountCap.HasValue ||
                obj.DarkeningLevel.HasValue ||
                obj.RandomEncounterChance.HasValue ||
                obj.RandomEncounterRubicon.HasValue ||
                obj.BattleMonsterStrengthAdjustment != 0 ||
                obj.BattleMonsterCount.HasValue ||
                obj.BattleMonsterCountRange != null ||
                obj.IsBattleMonsterCountIndeterminate ||
                obj.HasBattleLikeInfo ||
                (obj.PersistentCounterProgressions != null && obj.PersistentCounterProgressions.Count > 0) ||
                (obj.DynamicRandomBoundDependencies != null && obj.DynamicRandomBoundDependencies.Count > 0) ||
                (obj.PartyEffects != null && obj.PartyEffects.Count > 0))
            {
                return true;
            }

            return obj.PathVariants != null &&
                obj.PathVariants.Values.Any(HasGameplayInfo);
        }

        private static bool HasGameplayInfo(PathVariantInfo variant)
        {
            return variant != null &&
                (variant.HasTeleportTarget ||
                 variant.CallsRandomEncounter ||
                 variant.RandomEncounterMonsterPowerCap.HasValue ||
                 variant.RandomEncounterMonsterLevelCap.HasValue ||
                 variant.RandomEncounterMonsterBatchCountCap.HasValue ||
                 variant.DarkeningLevel.HasValue ||
                 variant.RandomEncounterChance.HasValue ||
                 variant.RandomEncounterRubicon.HasValue ||
                 variant.BattleMonsterStrengthAdjustment != 0 ||
                 variant.BattleMonsterCount.HasValue ||
                 variant.BattleMonsterCountRange != null ||
                 variant.IsBattleMonsterCountIndeterminate ||
                 variant.HasAnyTableLoad ||
                 (variant.LoadedValues != null && variant.LoadedValues.Count > 0) ||
                 (variant.PersistentCounterProgressions != null && variant.PersistentCounterProgressions.Count > 0) ||
                 (variant.DynamicRandomBoundDependencies != null && variant.DynamicRandomBoundDependencies.Count > 0) ||
                 (variant.BattleMonsters != null && variant.BattleMonsters.Count > 0) ||
                 (variant.PartiallyDefinedBattles != null && variant.PartiallyDefinedBattles.Count > 0) ||
                 (variant.PartyEffects != null && variant.PartyEffects.Count > 0));
        }

        private static List<string> BuildExternalJumpLines(OvrObject obj)
        {
            var targetValues = GetObjectExternalJumpTargets(obj)
                .Where(target => !IsTechnicalRenderPatchTarget(target))
                .ToList();
            var targets = targetValues
                .Select(target => $"0x{target:X4}")
                .ToList();

            if (targets.Count == 0)
                return new List<string>();

            return new List<string>
            {
                $"Передача управления внешней игровой процедуре: {string.Join(", ", targets.Select(DescribeExternalJumpTarget))}"
            };
        }

        private static IEnumerable<uint> GetObjectExternalJumpTargets(OvrObject obj)
        {
            if (obj == null)
                return Enumerable.Empty<uint>();

            return (obj.ExternalJumpTargets ?? new List<uint>())
                .Where(target => target <= ushort.MaxValue)
                .Distinct()
                .OrderBy(target => target);
        }

        private static IEnumerable<uint> GetAllExternalJumpTargets(OvrObject obj)
        {
            if (obj == null)
                return Enumerable.Empty<uint>();

            var targets = GetObjectExternalJumpTargets(obj).ToList();

            if (obj.PathVariants != null)
            {
                foreach (var variant in obj.PathVariants.Values)
                {
                    if (variant?.ExternalJumpTargets != null)
                        targets.AddRange(variant.ExternalJumpTargets);
                }
            }

            return targets
                .Where(target => target <= ushort.MaxValue)
                .Distinct()
                .OrderBy(target => target);
        }

        private static string TryBuildTechnicalRenderPatchGameplayNote(string fileNameOnly, OvrFileConfig config, OvrObject obj)
        {
            if (!IsTechnicalRenderPatchObject(fileNameOnly, config, obj))
                return null;

            return BuildTechnicalRenderPatchLine(GetAllExternalJumpTargets(obj).ToList());
        }

        private static bool IsTechnicalRenderPatchObject(string fileNameOnly, OvrFileConfig config, OvrObject obj)
        {
            if (obj == null ||
                !string.Equals(fileNameOnly, "AREAD4.OVR", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!TryReadFirstLayerCell(config, obj.X, obj.Y, out byte firstLayerCell) ||
                firstLayerCell != 0xAA)
            {
                return false;
            }

            var targets = GetAllExternalJumpTargets(obj).ToList();
            return targets.Count > 0 &&
                targets.All(IsTechnicalRenderPatchTarget) &&
                targets.Contains(RenderTemplateDefaultByteEntry) &&
                targets.Contains(RenderTemplateCallerByteEntry) &&
                !HasVisibleTextInfo(obj) &&
                !HasGameplayInfo(obj);
        }

        private static bool TryReadFirstLayerCell(OvrFileConfig config, int x, int y, out byte value)
        {
            value = 0;

            if (config?.First16Lines == null ||
                y < 0 || y >= config.First16Lines.Length ||
                x < 0 || x >= 16)
            {
                return false;
            }

            string line = config.First16Lines[y];
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= x)
                return false;

            return byte.TryParse(tokens[x], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsTechnicalRenderPatchTarget(uint target)
        {
            return target == RenderTemplateDefaultByteEntry ||
                   target == RenderTemplateCallerByteEntry;
        }

        private static string BuildTechnicalRenderPatchLine(IReadOnlyCollection<uint> targets)
        {
            if (targets.Contains(RenderTemplateDefaultByteEntry) && targets.Contains(RenderTemplateCallerByteEntry))
            {
                return InlineNoteStyleCodec.EncodeTechnicalRenderPatchNoteText(
                    "=*** Техническая клетка без игрового эффекта: для west используется 0x5855/[0x239F]=0x80, для остальных направлений 0x585B/AL=0x95 ***=");
            }

            if (targets.Contains(RenderTemplateCallerByteEntry))
                return InlineNoteStyleCodec.EncodeTechnicalRenderPatchNoteText(
                    "=*** Техническая клетка без игрового эффекта: внешний вход 0x585B использует AL=0x95 ***=");

            return InlineNoteStyleCodec.EncodeTechnicalRenderPatchNoteText(
                "=*** Техническая клетка без игрового эффекта: внешний вход 0x5855 использует [0x239F]=0x80 ***=");
        }

        private static string DescribeExternalJumpTarget(string formattedTarget)
        {
            return formattedTarget switch
            {
                "0x5855" => "0x5855 (шаблон рендера: AL берётся из [0x239F])",
                "0x585B" => "0x585B (шаблон рендера: AL задан вызывающим кодом)",
                _ => formattedTarget
            };
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
            var seenItemsByKey = new Dictionary<string, VariantRenderItem>(StringComparer.Ordinal);

            foreach (var item in items ?? Enumerable.Empty<VariantRenderItem>())
            {
                if (item == null)
                    continue;

                string displayKey = BuildDisplayedVariantItemKey(item);
                if (seenItemsByKey.TryGetValue(displayKey, out var existing))
                {
                    MergeDisplayedVariantItemMetadata(existing, item);
                    continue;
                }

                seenItemsByKey[displayKey] = item;
                result.Add(item);
            }

            return result;
        }

        private static void MergeDisplayedVariantItemMetadata(
            VariantRenderItem target,
            VariantRenderItem source)
        {
            if (target == null || source == null)
                return;

            AddDistinctHeaderAnnotations(target.HeaderAnnotations, source.HeaderAnnotations);
            AddDistinctSupplementalLines(target.SupplementalLines, source.SupplementalLines);

            if (source.ConditionalComplementOutcomeEffectKeys != null &&
                source.ConditionalComplementOutcomeEffectKeys.Count > 0)
            {
                target.ConditionalComplementOutcomeEffectKeys ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in source.ConditionalComplementOutcomeEffectKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        target.ConditionalComplementOutcomeEffectKeys.Add(key);
                }
            }
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
                ? string.Join("|", BuildInventoryPresenceHeaderAnnotationsForVariantPath(obj, inventoryVariant))
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
                if (!IsAnswerPromptLine(line))
                    continue;

                narrativeLines.Insert(i + 1, spoilerLine);
                return;
            }
        }

        private static bool IsAnswerPromptLine(string line)
        {
            return line?.IndexOf(RiddleAnswerPrompt, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line?.IndexOf(ResponseAnswerPrompt, StringComparison.OrdinalIgnoreCase) >= 0;
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

