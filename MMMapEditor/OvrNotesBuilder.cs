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


﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace MMMapEditor
{
    public sealed class OvrNotesBuildResult
    {
        public Dictionary<Point, string> NotesPerCell { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, string> CentralOptions { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, MainForm.SideValues<bool>> MessageStates { get; set; }
            = new Dictionary<Point, MainForm.SideValues<bool>>();

        public int TotalObjects { get; set; }
        public int TableObjects { get; set; }
        public int SpecObjects { get; set; }
    }

    public static class OvrNotesBuilder
    {
        public static OvrNotesBuildResult BuildNotes(
            string filename,
            Dictionary<Point, string> existingCentralOptions,
            Dictionary<Point, string> existingNotes = null,
            Dictionary<Point, MainForm.SideValues<bool>> existingMessageStates = null)
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
                    ? existingMessageStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone() ?? new MainForm.SideValues<bool>(false, false, false, false))
                    : new Dictionary<Point, MainForm.SideValues<bool>>()
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

            var allObjects = OvrFileAnalyzer.AnalyzeOvrFile(filename, config, result.CentralOptions);

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

                result.CentralOptions[pos] = obj.IsFromTable ? "AnyObject" : "AnyObjectSpec";

                var directionsWithMessages = obj.GetDirectionsWithMessages();

                var currentMessages = result.MessageStates.TryGetValue(pos, out var prev)
                    ? prev.Clone()
                    : new MainForm.SideValues<bool>(false, false, false, false);

                currentMessages.Top = currentMessages.Top || directionsWithMessages.Contains(Direction.Top);
                currentMessages.Left = currentMessages.Left || directionsWithMessages.Contains(Direction.Left);
                currentMessages.Bottom = currentMessages.Bottom || directionsWithMessages.Contains(Direction.Bottom);
                currentMessages.Right = currentMessages.Right || directionsWithMessages.Contains(Direction.Right);

                result.MessageStates[pos] = currentMessages;

                Dictionary<int, List<string>> variantContents = BuildVariantContents(
                    obj,
                    defaultMonsterPower,
                    defaultMonsterLevel,
                    defaultMonsterBatchCount,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance);

                foreach (var key in variantContents.Keys.ToList())
                    variantContents[key] = NumberLootBlockIfNeeded(variantContents[key]);

                // Для объектов с PathVariants сохраняем все варианты исполнения как есть.
                // Иначе разные ветки с одинаковым текстом (например, найденные внутри CALL)
                // снова схлопнутся уже на этапе построения заметок.
                bool preserveExplicitPathVariants = obj.PathVariants != null && obj.PathVariants.Count > 0;
                if (!preserveExplicitPathVariants)
                    variantContents = DeduplicateVariantContents(variantContents);

                StringBuilder newNotes = new StringBuilder();

                if (variantContents.Count == 0)
                {
                    newNotes.Append(existingCellNotes);
                }
                else if (variantContents.Count == 1)
                {
                    var singleVariant = variantContents.First().Value;
                    foreach (var line in singleVariant)
                        newNotes.Append(line + "\n");
                }
                else
                {
                    newNotes.AppendLine("Эта ячейка содержит различные варианты текста:");

                    var sortedVariants = variantContents.OrderBy(v => v.Key).ToList();
                    for (int i = 0; i < sortedVariants.Count; i++)
                    {
                        var variant = sortedVariants[i];
                        string variantHeader = BuildVariantHeader(obj, variant.Key, i + 1);
                        bool headerContainsProbability = VariantHeaderContainsProbability(variantHeader);
                        newNotes.Append($"{variantHeader}:\n");

                        foreach (var line in variant.Value)
                            newNotes.Append(FormatVariantLine(line, headerContainsProbability) + "\n");

                        if (i < sortedVariants.Count - 1)
                            newNotes.AppendLine();
                    }
                }

                result.NotesPerCell[pos] = newNotes.Length > 0
                    ? newNotes.ToString().TrimEnd('\n')
                    : existingCellNotes;
            }

            return result;
        }

        private static Dictionary<int, List<string>> BuildVariantContents(
            OvrObject obj,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance)
        {
            if (obj.PathVariants != null && obj.PathVariants.Count > 0)
            {
                return BuildVariantContentsFromPathVariants(
                    obj,
                    defaultMonsterPower,
                    defaultMonsterLevel,
                    defaultMonsterBatchCount,
                    defaultDarkeningLevel,
                    defaultRandomEncounterChance);
            }

            return BuildVariantContentsFromObjectTexts(
                obj,
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
                defaultDarkeningLevel,
                defaultRandomEncounterChance);
        }

        private static Dictionary<int, List<string>> BuildVariantContentsFromPathVariants(
            OvrObject obj,
            byte defaultMonsterPower,
            byte defaultMonsterLevel,
            byte defaultMonsterBatchCount,
            byte defaultDarkeningLevel,
            byte defaultRandomEncounterChance)
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
                    defaultRandomEncounterChance);

                if (lines.Count == 0)
                    lines.Add("Ничего не происходит");

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
            byte defaultRandomEncounterChance)
        {
            var variantContents = BuildVariantContentsFromRawTexts(obj);
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
            byte defaultRandomEncounterChance)
        {
            var lines = new List<string>();

            lines.AddRange(DecodeNoteTexts(rawTexts));
            lines.AddRange(GetMonsterStatLines(
                variantObject,
                defaultMonsterPower,
                defaultMonsterLevel,
                defaultMonsterBatchCount,
                defaultDarkeningLevel,
                defaultRandomEncounterChance));
            lines.AddRange(GetBattleLines(variantObject));
            lines.AddRange(GetSpecialNoteLines(variantObject));

            if (lines.Count == 0)
                lines.Add("Ничего не происходит");

            return lines;
        }

        private static string BuildVariantHeader(OvrObject obj, int variantKey, int displayVariantNumber)
        {
            string header = $"Вариант{displayVariantNumber}";

            if (obj?.PathVariants != null && obj.PathVariants.TryGetValue(variantKey, out var pathVariant))
            {
                string probability = BuildProbabilityLine(pathVariant);
                if (!string.IsNullOrEmpty(probability) && probability.StartsWith("Вероятность: "))
                {
                    probability = probability.Substring("Вероятность: ".Length);
                    header += $" (вероятность наступления {probability})";
                }
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

        private static string BuildProbabilityLine(PathVariantInfo variant)
        {
            if (variant == null)
                return null;

            return variant.GetProbabilityDescription();
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
                battleVariantLines.AddRange(battleLines);
                battleVariantLines.AddRange(specialNoteLines);

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

            bool hasBattleInfo =
                obj.HasBattleInfo ||
                obj.HasPartiallyDefinedBattles ||
                (obj.HasAnyTableLoad && obj.LoadedValues.Count > 0);

            if (!hasBattleInfo)
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

            // Заметка про random encounter нужна только для табличных объектов
            // без явного описания битвы. Если битва уже описана как группа монстров,
            // то информация о random count выводится в самой строке битвы.
            // Если одновременно есть телепорт, выводим его раньше: это ближе к
            // реальному порядку исполнения патча, где сначала меняются координаты,
            // а затем вызывается random encounter / событие.
            if (obj.IsFromTable && obj.CallsRandomEncounter && !obj.HasBattleInfo)
                lines.Add("⚠Вызывается random encounter ⚠");

            return lines;
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

        private static List<string> NumberLootBlockIfNeeded(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return lines;

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
                        result.Add($"{entryNumber}) {RemoveExistingLootNumbering(line)}");
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
                        result.Add($"{entryNumber}) {RemoveExistingLootNumbering(line)}");
                        entryNumber++;
                        i++;
                        continue;
                    }

                    break;
                }
            }

            return result;
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

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^\d+\s+GEMS?$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^GEMS?[:\s]+\d+$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^\d+\s+GOLD$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^GOLD[:\s]+\d+$"))
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

            int letterCount = trimmed.Count(char.IsLetter);
            if (letterCount == 0)
                return false;

            int upperCount = trimmed.Count(char.IsUpper);
            return upperCount >= Math.Max(3, letterCount * 3 / 4);
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