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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MMMapEditor
{
    public static class OvrFileConfigs
    {
        private const string MazeDataFileName = "MAZEDATA.DTA";
        private const string GameExecutableFileName = "MM.EXE";
        private const int StaticMapLayerWidth = 16;
        private const int StaticMapLayerHeight = 16;
        private const int StaticMapLayerByteCount = StaticMapLayerWidth * StaticMapLayerHeight;
        private const int StaticMapBlockSize = StaticMapLayerByteCount * 2;

        private const int OverlayHeaderSize = 0x0E;
        private const int ObjectCountInstructionOffset = 0x2E;
        private const int ObjectCountInstructionLength = 4;

        public static bool TryGetConfigForFile(string filename, out OvrFileConfig config)
        {
            return TryGetConfigForFile(filename, out config, out _);
        }

        public static bool TryGetConfigForFile(string filename, out OvrFileConfig config, out string errorMessage)
        {
            config = null;
            errorMessage = null;

            try
            {
                config = GetConfigForFile(filename);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is InvalidOperationException ||
                ex is ArgumentException ||
                ex is NotSupportedException)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static OvrFileConfig GetConfigForFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new InvalidOperationException("Имя OVR-файла не задано.");

            string fullOverlayPath = Path.GetFullPath(filename);
            string overlayFileName = Path.GetFileName(fullOverlayPath);
            string overlayStem = Path.GetFileNameWithoutExtension(fullOverlayPath).ToLowerInvariant();
            string overlayDirectory = Path.GetDirectoryName(fullOverlayPath) ?? AppDomain.CurrentDomain.BaseDirectory;

            string mazeDataPath = FindCompanionFile(overlayDirectory, MazeDataFileName);
            if (mazeDataPath == null)
            {
                throw new FileNotFoundException(
                    $"Файл {MazeDataFileName} не найден рядом с {overlayFileName}. " +
                    "Невозможно загрузить данные стен и событий карты.");
            }

            string gameExecutablePath = FindCompanionFile(overlayDirectory, GameExecutableFileName);
            if (gameExecutablePath == null)
            {
                throw new FileNotFoundException(
                    $"Файл {GameExecutableFileName} не найден рядом с {overlayFileName}. " +
                    $"Он нужен для сопоставления блоков {MazeDataFileName} с оверлеями.");
            }

            byte[] mazeData = File.ReadAllBytes(mazeDataPath);
            if (mazeData.Length == 0 || mazeData.Length % StaticMapBlockSize != 0)
            {
                throw new InvalidOperationException(
                    $"Файл {MazeDataFileName} имеет неверный размер {mazeData.Length} байт. " +
                    $"Ожидался размер, кратный {StaticMapBlockSize}.");
            }

            int blockCount = mazeData.Length / StaticMapBlockSize;
            IReadOnlyList<string> overlayNames = ReadOverlayNamesFromExecutable(
                gameExecutablePath,
                blockCount,
                overlayStem);

            int blockIndex = IndexOfOverlayName(overlayNames, overlayStem);
            if (blockIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Для файла {overlayFileName} не найден блок карты в {MazeDataFileName}. " +
                    $"Проверьте таблицу имён оверлеев в {GameExecutableFileName}.");
            }

            int blockOffset = blockIndex * StaticMapBlockSize;
            if (blockOffset + StaticMapBlockSize > mazeData.Length)
            {
                throw new InvalidOperationException(
                    $"Блок карты #{blockIndex} для {overlayFileName} выходит за пределы {MazeDataFileName}.");
            }

            var resolvedLayout = ResolveOverlayDataLayout(fullOverlayPath, overlayFileName);

            return new OvrFileConfig
            {
                First16Lines = FormatMapLayerLines(mazeData, blockOffset),
                Second16Lines = FormatMapLayerLines(mazeData, blockOffset + StaticMapLayerByteCount)
            }.WithStartAddress(resolvedLayout.StartAddress, resolvedLayout.HasObjectTable);
        }

        public static OvrFileConfig ResolveConfig(string filename, OvrFileConfig baseConfig)
        {
            if (baseConfig == null)
                throw new ArgumentNullException(nameof(baseConfig));

            var resolvedLayout = ResolveOverlayDataLayout(filename, Path.GetFileName(filename));
            return baseConfig.WithStartAddress(resolvedLayout.StartAddress, resolvedLayout.HasObjectTable);
        }

        public static int ResolveStartAddress(string filename)
        {
            byte[] fileData = File.ReadAllBytes(filename);
            return ResolveStartAddress(fileData, Path.GetFileName(filename));
        }

        public static int ResolveStartAddress(byte[] fileData, string? displayName = null)
        {
            return ResolveOverlayDataLayout(fileData, displayName).StartAddress;
        }

        private sealed class ResolvedOverlayDataLayout
        {
            public int StartAddress { get; set; }
            public bool HasObjectTable { get; set; }
        }

        private static ResolvedOverlayDataLayout ResolveOverlayDataLayout(string filename, string? displayName = null)
        {
            byte[] fileData = File.ReadAllBytes(filename);
            return ResolveOverlayDataLayout(fileData, displayName);
        }

        private static ResolvedOverlayDataLayout ResolveOverlayDataLayout(byte[] fileData, string? displayName = null)
        {
            if (fileData == null)
                throw new ArgumentNullException(nameof(fileData));

            string label = string.IsNullOrWhiteSpace(displayName)
                ? "overlay"
                : displayName;

            if (fileData.Length < OverlayHeaderSize)
                throw new InvalidOperationException($"{label}: file is too small to read overlay header.");

            if (fileData.Length < ObjectCountInstructionOffset + ObjectCountInstructionLength)
                throw new InvalidOperationException(
                    $"{label}: file is too small to read object-count instruction at 0x{ObjectCountInstructionOffset:X2}.");

            ushort firstBlockLength = ReadUInt16(fileData, 0x04);
            ushort secondBlockLoadAddress = ReadUInt16(fileData, 0x06);
            int secondBlockFileOffset = OverlayHeaderSize + firstBlockLength;

            if (secondBlockFileOffset < OverlayHeaderSize || secondBlockFileOffset >= fileData.Length)
            {
                throw new InvalidOperationException(
                    $"{label}: invalid second block file offset 0x{secondBlockFileOffset:X}.");
            }

            if (TryResolveObjectTableStartAddress(
                    fileData,
                    ObjectCountInstructionOffset,
                    secondBlockFileOffset,
                    secondBlockLoadAddress,
                    out int objectTableStartAddress))
            {
                return new ResolvedOverlayDataLayout
                {
                    StartAddress = objectTableStartAddress,
                    HasObjectTable = true
                };
            }

            int dataStartAddress = secondBlockFileOffset +
                (OvrFileConfig.OverlayTextStartAddress - secondBlockLoadAddress);

            if (dataStartAddress >= 0 &&
                dataStartAddress < fileData.Length &&
                LooksLikeOverlayTextStart(fileData, dataStartAddress))
            {
                return new ResolvedOverlayDataLayout
                {
                    StartAddress = dataStartAddress,
                    HasObjectTable = false
                };
            }

            byte opcode = fileData[ObjectCountInstructionOffset];
            byte modRm = fileData[ObjectCountInstructionOffset + 1];
            throw new InvalidOperationException(
                $"{label}: expected 'cmp bl, byte ptr [imm16]' at 0x{ObjectCountInstructionOffset:X2}, " +
                $"found {opcode:X2} {modRm:X2}.");
        }

        private static bool TryResolveObjectTableStartAddress(
            byte[] fileData,
            int instructionOffset,
            int secondBlockFileOffset,
            ushort secondBlockLoadAddress,
            out int startAddress)
        {
            startAddress = 0;

            if (instructionOffset < 0 ||
                instructionOffset + ObjectCountInstructionLength > fileData.Length ||
                fileData[instructionOffset] != 0x3A ||
                fileData[instructionOffset + 1] != 0x1E)
            {
                return false;
            }

            ushort countMemoryAddress = ReadUInt16(fileData, instructionOffset + 2);
            long mappedOffset = secondBlockFileOffset + (countMemoryAddress - secondBlockLoadAddress);
            if (mappedOffset < 0 || mappedOffset >= fileData.Length)
                return false;

            startAddress = (int)mappedOffset;
            return true;
        }

        private static bool LooksLikeOverlayTextStart(byte[] fileData, int offset)
        {
            if (offset < 0 || offset >= fileData.Length)
                return false;

            byte value = fileData[offset];
            return value == 0 ||
                   value == 0x0D ||
                   value == 0x0A ||
                   (value >= 0x20 && value <= 0x7E);
        }

        private static string FindCompanionFile(string directory, string fileName)
        {
            string exactPath = Path.Combine(directory, fileName);
            if (File.Exists(exactPath))
                return exactPath;

            try
            {
                return Directory.EnumerateFiles(directory)
                    .FirstOrDefault(path =>
                        string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is NotSupportedException)
            {
                return null;
            }
        }

        private static IReadOnlyList<string> ReadOverlayNamesFromExecutable(
            string executablePath,
            int expectedCount,
            string selectedOverlayStem)
        {
            byte[] data = File.ReadAllBytes(executablePath);
            List<string> selectedRun = null;

            foreach (List<string> run in FindZeroTerminatedOverlayNameRuns(data))
            {
                if (run.Count < expectedCount)
                    continue;

                if (!run.Contains(selectedOverlayStem, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (run.Count == expectedCount)
                    return run;

                int selectedIndex = IndexOfOverlayName(run, selectedOverlayStem);
                int start = Math.Max(0, Math.Min(selectedIndex, run.Count - expectedCount));
                selectedRun = run.Skip(start).Take(expectedCount).ToList();
                break;
            }

            if (selectedRun != null)
                return selectedRun;

            throw new InvalidOperationException(
                $"Не удалось найти в {GameExecutableFileName} таблицу из {expectedCount} имён оверлеев.");
        }

        private static IEnumerable<List<string>> FindZeroTerminatedOverlayNameRuns(byte[] data)
        {
            for (int i = 0; i < data.Length;)
            {
                if (!TryReadOverlayNameAt(data, i, out string name, out int nextOffset))
                {
                    i++;
                    continue;
                }

                var names = new List<string>();
                int runOffset = i;
                while (TryReadOverlayNameAt(data, runOffset, out name, out nextOffset))
                {
                    names.Add(name);
                    runOffset = nextOffset;
                }

                if (names.Count > 1)
                    yield return names;

                i = Math.Max(runOffset, i + 1);
            }
        }

        private static bool TryReadOverlayNameAt(byte[] data, int offset, out string name, out int nextOffset)
        {
            name = null;
            nextOffset = offset;

            int pos = offset;
            while (pos < data.Length && data[pos] != 0)
            {
                if (!IsOverlayNameChar(data[pos]))
                    return false;

                pos++;
            }

            int length = pos - offset;
            if (length < 3 || length > 8 || pos >= data.Length)
                return false;

            if (data[offset] < (byte)'a' || data[offset] > (byte)'z')
                return false;

            name = Encoding.ASCII.GetString(data, offset, length);
            nextOffset = pos + 1;
            return true;
        }

        private static bool IsOverlayNameChar(byte value)
        {
            return (value >= (byte)'a' && value <= (byte)'z') ||
                   (value >= (byte)'0' && value <= (byte)'9');
        }

        private static int IndexOfOverlayName(IReadOnlyList<string> overlayNames, string overlayStem)
        {
            if (overlayNames == null || string.IsNullOrWhiteSpace(overlayStem))
                return -1;

            for (int i = 0; i < overlayNames.Count; i++)
            {
                if (string.Equals(overlayNames[i], overlayStem, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static string[] FormatMapLayerLines(byte[] mapData, int layerOffset)
        {
            var lines = new string[StaticMapLayerHeight];
            for (int y = 0; y < StaticMapLayerHeight; y++)
            {
                var sb = new StringBuilder(StaticMapLayerWidth * 3);
                int rowOffset = layerOffset + y * StaticMapLayerWidth;
                for (int x = 0; x < StaticMapLayerWidth; x++)
                {
                    if (x > 0)
                        sb.Append(' ');

                    sb.Append(mapData[rowOffset + x].ToString("X2", CultureInfo.InvariantCulture));
                }

                lines[y] = sb.ToString();
            }

            return lines;
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }
    }

    public class OvrFileConfig
    {
        public const int OverlayTextStartAddress = 0xC972;
        public const int PatchBase = 0x0B7F;
        public const byte OutdoorOverlayIdFlag = 0x80;
        internal const int SideLayoutRecordOffsetFromStartAddress = 0x32;

        private int? _startAddress;

        public int StartAddress
        {
            get
            {
                if (!_startAddress.HasValue)
                    throw new InvalidOperationException("StartAddress must be resolved from the overlay before use.");

                return _startAddress.Value;
            }
            private set => _startAddress = value;
        }

        public bool HasResolvedStartAddress => _startAddress.HasValue;
        public bool HasObjectTable { get; private set; } = true;
        public string[] First16Lines { get; set; } = Array.Empty<string>();
        public string[] Second16Lines { get; set; } = Array.Empty<string>();
        public int TextBaseAddr => OverlayTextStartAddress - StartAddress;
        public int MostDangerousCell => StartAddress - 24;
        public int MostPeacefulCell => StartAddress - 27;
        public int RandomEncounterChance => StartAddress - 21;
        public int RandomEncounterMonsterPowerCap => StartAddress - 17;
        public int RandomEncounterMonsterLevelCap => StartAddress - 3;
        public int DarkeningLevel => StartAddress - 4;
        public int RandomEncounterMonsterBatchCountCap => StartAddress - 16;
        public int OverlayId => StartAddress - SideLayoutRecordOffsetFromStartAddress;
        public int SurfaceX => StartAddress - 8;
        public int SurfaceY => StartAddress - 7;
        public int SectorMapLetter => StartAddress - 15;
        public int SectorMapDigit => StartAddress - 14;

        public bool TryReadOverlayId(byte[] fileData, out byte overlayId)
        {
            overlayId = 0;

            int address = OverlayId;
            if (fileData == null || address < 0 || address >= fileData.Length)
                return false;

            overlayId = fileData[address];
            return true;
        }

        public bool TryIsOutdoorOverlay(byte[] fileData, out bool isOutdoorOverlay)
        {
            isOutdoorOverlay = false;

            if (!TryReadOverlayId(fileData, out byte overlayId))
                return false;

            isOutdoorOverlay = (overlayId & OutdoorOverlayIdFlag) != 0;
            return true;
        }

        public OvrFileConfig WithStartAddress(int startAddress)
        {
            return WithStartAddress(startAddress, HasObjectTable);
        }

        public OvrFileConfig WithStartAddress(int startAddress, bool hasObjectTable)
        {
            return new OvrFileConfig
            {
                StartAddress = startAddress,
                HasObjectTable = hasObjectTable,
                First16Lines = First16Lines,
                Second16Lines = Second16Lines
            };
        }
    }
}
