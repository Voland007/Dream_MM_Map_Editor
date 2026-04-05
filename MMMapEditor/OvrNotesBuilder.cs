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


using System;
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
            byte defaultLightingLevel = 0;
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
                if (config.LightingLevel < fileData.Length)
                    defaultLightingLevel = fileData[config.LightingLevel];
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

                Dictionary<int, List<string>> variantContents = new Dictionary<int, List<string>>();

                List<string> battleInfoLines = new List<string>();
                if (obj.HasBattleInfo)
                {
                    string battleDesc = obj.GetBattleDescription();
                    if (!string.IsNullOrEmpty(battleDesc))
                        battleInfoLines.Add(battleDesc);
                }

                List<string> monsterStatLines = new List<string>();
                if (obj.HasMonsterStatChanges)
                {
                    var powerDesc = obj.GetMonsterPowerDescription(defaultMonsterPower);
                    if (powerDesc != null) monsterStatLines.Add(powerDesc);

                    var levelDesc = obj.GetMonsterLevelDescription(defaultMonsterLevel);
                    if (levelDesc != null) monsterStatLines.Add(levelDesc);

                    var batchCountDesc = obj.GetMonsterBatchCountDescription(defaultMonsterBatchCount);
                    if (batchCountDesc != null) monsterStatLines.Add(batchCountDesc);

                    var lightingDesc = obj.GetLightingLevelDescription(defaultLightingLevel);
                    if (lightingDesc != null) monsterStatLines.Add(lightingDesc);

                    var randomEncounterDesc = obj.GetRandomEncounterChanceDescription(defaultRandomEncounterChance);
                    if (randomEncounterDesc != null) monsterStatLines.Add(randomEncounterDesc);
                }

                List<string> partialBattleLines = new List<string>();
                if (obj.HasPartiallyDefinedBattles)
                {
                    string battleDesc = obj.GetBattleDescription();
                    if (!string.IsNullOrEmpty(battleDesc))
                        partialBattleLines.Add(battleDesc);
                }

                if (obj.PathTextsOrdered != null && obj.PathTextsOrdered.Count > 0)
                {
                    foreach (var kvp in obj.PathTextsOrdered.OrderBy(p => p.Key))
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0)
                            continue;

                        int variantNumber = kvp.Key;
                        if (!variantContents.ContainsKey(variantNumber))
                            variantContents[variantNumber] = new List<string>();

                        foreach (var text in kvp.Value)
                        {
                            string decodedText = ExtractNoteText(text);
                            if (!string.IsNullOrEmpty(decodedText))
                                variantContents[variantNumber].Add(decodedText);
                        }
                    }
                }
                else if (obj.PathTexts != null && obj.PathTexts.Count > 0)
                {
                    foreach (var kvp in obj.PathTexts.OrderBy(p => p.Key))
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0)
                            continue;

                        int variantNumber = kvp.Key;
                        if (!variantContents.ContainsKey(variantNumber))
                            variantContents[variantNumber] = new List<string>();

                        foreach (var text in kvp.Value)
                        {
                            string decodedText = ExtractNoteText(text);
                            if (!string.IsNullOrEmpty(decodedText))
                                variantContents[variantNumber].Add(decodedText);
                        }
                    }
                }

                bool hasBattle = battleInfoLines.Count > 0;
                bool hasPartialBattle = partialBattleLines.Count > 0;

                if (hasBattle || hasPartialBattle)
                {
                    List<string> battleVariantLines = new List<string>();

                    if (hasBattle)
                        battleVariantLines.AddRange(battleInfoLines);

                    if (hasPartialBattle)
                        battleVariantLines.AddRange(partialBattleLines);

                    if (monsterStatLines.Count > 0)
                        battleVariantLines.InsertRange(0, monsterStatLines);

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
                }
                else if (monsterStatLines.Count > 0 && variantContents.Count > 0)
                {
                    int firstVariant = variantContents.Keys.Min();
                    variantContents[firstVariant].InsertRange(0, monsterStatLines);
                }
                else if (monsterStatLines.Count > 0)
                {
                    variantContents[0] = monsterStatLines;
                }

                foreach (var key in variantContents.Keys.ToList())
                    variantContents[key] = NumberLootBlockIfNeeded(variantContents[key]);

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
                        newNotes.Append($"Вариант{i + 1}:\n");

                        foreach (var line in variant.Value)
                            newNotes.Append(line + "\n");

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


        private static List<string> NumberLootBlockIfNeeded(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return lines;

            var result = new List<string>(lines);
            var numberedIndexes = new HashSet<int>();

            for (int i = 0; i < result.Count; i++)
            {
                if (!IsExplicitLootValueLine(result[i]))
                    continue;

                var entries = CollectLootEntries(result, i);
                if (entries.Count <= 1)
                    continue;

                for (int entryNumber = 0; entryNumber < entries.Count; entryNumber++)
                {
                    int entryIndex = entries[entryNumber];
                    if (numberedIndexes.Contains(entryIndex))
                        continue;

                    result[entryIndex] = $"{entryNumber + 1}) {RemoveExistingLootNumbering(result[entryIndex])}";
                    numberedIndexes.Add(entryIndex);
                }
            }

            return result;
        }

        private static List<int> CollectLootEntries(List<string> lines, int anchorIndex)
        {
            var entries = new List<int> { anchorIndex };

            for (int i = anchorIndex - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]) || IsLootIntroLine(lines[i]))
                    break;

                if (IsPossibleLootItemLine(lines[i]))
                {
                    entries.Insert(0, i);
                    continue;
                }

                break;
            }

            for (int i = anchorIndex + 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]) || IsLootIntroLine(lines[i]))
                    break;

                if (IsExplicitLootValueLine(lines[i]) || IsPossibleLootItemLine(lines[i]))
                {
                    entries.Add(i);
                    continue;
                }

                break;
            }

            return entries.Distinct().ToList();
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

        private static bool IsPossibleLootItemLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = RemoveExistingLootNumbering(line.Trim());
            if (trimmed.Length == 0 || IsLootIntroLine(trimmed))
                return false;

            if (IsExplicitLootValueLine(trimmed))
                return true;

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

        private static bool IsLootIntroLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            return trimmed.EndsWith(":");
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