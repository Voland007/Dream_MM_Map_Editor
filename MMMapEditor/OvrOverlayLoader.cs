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

namespace MMMapEditor
{
    public sealed class OvrOverlayLoadResult
    {
        public Dictionary<Point, string> NotesPerCell { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, string> CentralOptions { get; set; } = new Dictionary<Point, string>();
        public Dictionary<Point, MainForm.SideValues<bool>> MessageStates { get; set; }
            = new Dictionary<Point, MainForm.SideValues<bool>>();

        public int TotalObjects { get; set; }
        public int TableObjects { get; set; }
        public int SpecObjects { get; set; }

        public Point? MostDangerousCell { get; set; }
        public Point? MostPeacefulCell { get; set; }

        public byte MonsterPower { get; set; }
        public byte MonsterLevel { get; set; }
        public byte DarkeningLevel { get; set; }
        public byte MonsterBatchCount { get; set; }
        public byte RandomEncounterChanceRaw { get; set; } //исходное шестнадцатеричное число из оверлейного файла
        public double RandomEncounterChancePercent { get; set; } //рассчитанный % на основании RandomEncounterChanceRaw

        public Tuple<byte, byte> SurfaceCoords { get; set; }
        public string SectorMap { get; set; }
    }

    public static class OvrOverlayLoader
    {
        public static OvrOverlayLoadResult Load(
            string filename,
            Dictionary<Point, string> seedCentralOptions = null,
            Dictionary<Point, string> seedNotes = null,
            Dictionary<Point, MainForm.SideValues<bool>> seedMessageStates = null,
            bool? useHierarchicalView = null)
        {
            string fileNameOnly = Path.GetFileName(filename).ToUpper();

            if (!OvrFileConfigs.Configs.ContainsKey(fileNameOnly))
                throw new InvalidOperationException($"Конфигурация для файла {fileNameOnly} не найдена.");

            var config = OvrFileConfigs.Configs[fileNameOnly];
            byte[] fileData = File.ReadAllBytes(filename);

            var buildResult = OvrNotesBuilder.BuildNotes(
                filename,
                seedCentralOptions,
                seedNotes,
                seedMessageStates,
                useHierarchicalView);

            var result = new OvrOverlayLoadResult
            {
                NotesPerCell = buildResult.NotesPerCell,
                CentralOptions = buildResult.CentralOptions,
                MessageStates = buildResult.MessageStates,
                TotalObjects = buildResult.TotalObjects,
                TableObjects = buildResult.TableObjects,
                SpecObjects = buildResult.SpecObjects
            };

            result.MostDangerousCell = ReadCell(fileData, config.MostDangerousCell);
            result.MostPeacefulCell = ReadCell(fileData, config.MostPeacefulCell);

            PrependNote(result.NotesPerCell, result.MostDangerousCell,
                "ВНИМАНИЕ! ЭТО САМАЯ ОПАСНАЯ КЛЕТКА НА КАРТЕ!");
            PrependNote(result.NotesPerCell, result.MostPeacefulCell,
                "ЭТО САМАЯ БЕЗОПАСНАЯ КЛЕТКА НА КАРТЕ!");

            result.RandomEncounterChanceRaw = ReadByte(fileData, config.RandomEncounterChance);
            result.RandomEncounterChancePercent = DecodeRandomEncounterChance(ReadByte(fileData, config.RandomEncounterChance));
            result.MonsterPower = ReadByte(fileData, config.MonsterPower);
            result.MonsterLevel = ReadByte(fileData, config.MonsterLevel);
            result.DarkeningLevel = ReadByte(fileData, config.DarkeningLevel);
            result.MonsterBatchCount = ReadByte(fileData, config.MonsterBatchCount);
            result.SurfaceCoords = ReadSurface(fileData, config.SurfaceX, config.SurfaceY);
            result.SectorMap = ReadSectorMap(fileData, config.SectorMapLetter, config.SectorMapDigit);

            return result;
        }

        private static void PrependNote(Dictionary<Point, string> notesPerCell, Point? cell, string text)
        {
            if (!cell.HasValue)
                return;

            if (notesPerCell.TryGetValue(cell.Value, out var currentNotes) &&
                !string.IsNullOrWhiteSpace(currentNotes))
            {
                if (!currentNotes.StartsWith(text))
                    notesPerCell[cell.Value] = text + "\n" + currentNotes;
            }
            else
            {
                notesPerCell[cell.Value] = text;
            }
        }

        private static Point? ReadCell(byte[] fileData, int address)
        {
            if (address < 0 || address + 1 >= fileData.Length)
                return null;

            byte x = fileData[address];
            byte y = fileData[address + 1];

            return new Point(x & 0xF, y & 0xF);
        }

        private static byte ReadByte(byte[] fileData, int address)
        {
            if (address < 0 || address >= fileData.Length)
                return 0;

            return fileData[address];
        }

        private static double DecodeRandomEncounterChance(byte value)
        {
            if (value == 0x00 || value == 0xFF)
                return 0;

            return (256 - value) * 100.0 / 256.0;
        }

        private static Tuple<byte, byte> ReadSurface(byte[] fileData, int xAddress, int yAddress)
        {
            if (xAddress < 0 || yAddress < 0 || xAddress >= fileData.Length || yAddress >= fileData.Length)
                return null;

            return Tuple.Create(fileData[xAddress], fileData[yAddress]);
        }

        private static string ReadSectorMap(byte[] fileData, int highAddress, int lowAddress)
        {
            if (highAddress < 0 || lowAddress < 0 || highAddress >= fileData.Length || lowAddress >= fileData.Length)
                return null;

            byte highByte = fileData[highAddress];
            byte lowByte = fileData[lowAddress];

            char highChar = (char)(highByte - 0xC1 + 65);
            char lowChar = (char)(lowByte - 0xB1 + 49);

            return $"{highChar}-{lowChar}";
        }
    }
}