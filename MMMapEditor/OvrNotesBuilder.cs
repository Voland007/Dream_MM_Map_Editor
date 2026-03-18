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
        public Dictionary<Point, Tuple<bool, bool, bool, bool>> MessageStates { get; set; }
            = new Dictionary<Point, Tuple<bool, bool, bool, bool>>();

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
            Dictionary<Point, Tuple<bool, bool, bool, bool>> existingMessageStates = null)
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
                    ? new Dictionary<Point, Tuple<bool, bool, bool, bool>>(existingMessageStates)
                    : new Dictionary<Point, Tuple<bool, bool, bool, bool>>()
            };

            string fileNameOnly = Path.GetFileName(filename).ToUpper();

            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
                throw new InvalidOperationException($"Конфигурация для файла {fileNameOnly} не найдена.");

            var config = OvrFileConfigs.Configs[fileNameOnly];

            byte defaultMonsterPower = 0;
            byte defaultMonsterLevel = 0;

            try
            {
                byte[] fileData = File.ReadAllBytes(filename);
                if (config.MonsterPower < fileData.Length)
                    defaultMonsterPower = fileData[config.MonsterPower];
                if (config.MonsterLevel < fileData.Length)
                    defaultMonsterLevel = fileData[config.MonsterLevel];
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

                if (!tableObjectCoords.Contains(coordKey) && existingOption != "Случайная встреча")
                    continue;

                string existingCellNotes = result.NotesPerCell.TryGetValue(pos, out var notes)
                    ? notes
                    : "";

                result.CentralOptions[pos] = obj.IsFromTable ? "AnyObject" : "AnyObjectSpec";

                var directionsWithMessages = obj.GetDirectionsWithMessages();

                var currentMessages = result.MessageStates.TryGetValue(pos, out var prev)
                    ? prev
                    : new Tuple<bool, bool, bool, bool>(false, false, false, false);

                bool top = currentMessages.Item1 || directionsWithMessages.Contains(Direction.Top);
                bool left = currentMessages.Item2 || directionsWithMessages.Contains(Direction.Left);
                bool bottom = currentMessages.Item3 || directionsWithMessages.Contains(Direction.Bottom);
                bool right = currentMessages.Item4 || directionsWithMessages.Contains(Direction.Right);

                result.MessageStates[pos] = new Tuple<bool, bool, bool, bool>(top, left, bottom, right);

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
                            int colonIndex = text.IndexOf(':');
                            if (colonIndex >= 0 && colonIndex + 1 < text.Length)
                            {
                                string textPart = text.Substring(colonIndex + 1).Trim();
                                string decodedText = DecodeTextString(textPart);
                                if (!string.IsNullOrEmpty(decodedText))
                                {
                                    decodedText = decodedText.TrimEnd('\r');
                                    variantContents[variantNumber].Add(decodedText);
                                }
                            }
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
                            int colonIndex = text.IndexOf(':');
                            if (colonIndex >= 0 && colonIndex + 1 < text.Length)
                            {
                                string textPart = text.Substring(colonIndex + 1).Trim();
                                string decodedText = DecodeTextString(textPart);
                                if (!string.IsNullOrEmpty(decodedText))
                                {
                                    decodedText = decodedText.TrimEnd('\r');
                                    variantContents[variantNumber].Add(decodedText);
                                }
                            }
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