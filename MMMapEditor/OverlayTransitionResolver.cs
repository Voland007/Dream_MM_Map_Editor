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
using System.IO;
using System.Linq;

namespace MMMapEditor
{
    internal static class OverlayTransitionResolver
    {
        private const string GameExecutableFileName = "MM.EXE";
        private const int ExecutableOverlayEntryCount = 55;
        private const int ExecutableOverlayCoordinateTableOffset = 0x10B2B;
        private const int ExecutableOverlayPointerTableOffset = 0x10B99;
        private const int ExecutableOverlayNameBaseOffset = 0x109B0;

        private sealed class LoadedOverlayMetadata
        {
            public string MapSector { get; set; }
            public bool IsOutdoorOverlay { get; set; }
            public byte? SurfaceX { get; set; }
            public byte? SurfaceY { get; set; }
        }

        private sealed class OverlayTransitionContext
        {
            public OvrObject Object { get; set; }
            public PathVariantInfo Variant { get; set; }
            public OverlayTransitionInfo Transition { get; set; }
        }

        private sealed class ReverseOutdoorTarget
        {
            public byte X { get; set; }
            public byte Y { get; set; }
            public ushort MapSelector { get; set; }
            public string LoadedOverlayName { get; set; }
        }

        private sealed class ReverseOutdoorTargetIndex
        {
            public Dictionary<string, List<ReverseOutdoorTarget>> ByInteriorCell { get; }
                = new Dictionary<string, List<ReverseOutdoorTarget>>(StringComparer.Ordinal);
        }

        private sealed class ExecutableOverlayTable
        {
            public Dictionary<string, string> Entries { get; }
                = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static readonly object ReverseOutdoorTargetIndexLock = new object();
        private static readonly Dictionary<string, ReverseOutdoorTargetIndex> ReverseOutdoorTargetIndexes =
            new Dictionary<string, ReverseOutdoorTargetIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ReverseOutdoorTargetIndexesInProgress =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ExecutableOverlayTableLock = new object();
        private static readonly Dictionary<string, ExecutableOverlayTable> ExecutableOverlayTables =
            new Dictionary<string, ExecutableOverlayTable>(StringComparer.OrdinalIgnoreCase);

        public static string ResolveLoadedOverlayName(byte globalX, byte globalY, ushort mapSelector)
        {
            if (mapSelector == 2)
                return ResolveOutdoorSectorOverlayName(globalX, globalY);

            return null;
        }

        public static void EnrichLoadedOverlayMetadata(IEnumerable<OvrObject> objects, string sourceFilename)
        {
            if (objects == null || string.IsNullOrWhiteSpace(sourceFilename))
                return;

            string sourceDirectory = Path.GetDirectoryName(sourceFilename);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                return;

            var metadataCache = new Dictionary<string, LoadedOverlayMetadata>(StringComparer.OrdinalIgnoreCase);
            string sourceOverlayName = Path.GetFileName(sourceFilename);
            LoadedOverlayMetadata sourceMetadata = TryReadOverlayMetadata(sourceFilename);

            foreach (var context in EnumerateTransitionContexts(objects))
            {
                var transition = context.Transition;
                ApplyExecutableOverlayTableResolution(sourceDirectory, context);
                ApplySourceOutdoorSectorCorrection(sourceDirectory, sourceOverlayName, sourceMetadata, context);
                TryApplyReverseOutdoorTargetCorrection(sourceDirectory, sourceOverlayName, context);

                if (transition == null || string.IsNullOrWhiteSpace(transition.LoadedOverlayName))
                    continue;

                if (!metadataCache.TryGetValue(transition.LoadedOverlayName, out LoadedOverlayMetadata metadata))
                {
                    metadata = TryReadLoadedOverlayMetadata(sourceDirectory, transition.LoadedOverlayName);
                    metadataCache[transition.LoadedOverlayName] = metadata;
                }

                if (metadata == null)
                    continue;

                if (string.IsNullOrWhiteSpace(transition.LoadedMapSector))
                    transition.LoadedMapSector = metadata.MapSector;
                if (!transition.LoadedSurfaceX.HasValue)
                    transition.LoadedSurfaceX = metadata.SurfaceX;
                if (!transition.LoadedSurfaceY.HasValue)
                    transition.LoadedSurfaceY = metadata.SurfaceY;
            }
        }

        private static IEnumerable<OverlayTransitionContext> EnumerateTransitionContexts(IEnumerable<OvrObject> objects)
        {
            foreach (var obj in objects ?? Enumerable.Empty<OvrObject>())
            {
                if (obj?.OverlayTransition != null)
                {
                    yield return new OverlayTransitionContext
                    {
                        Object = obj,
                        Transition = obj.OverlayTransition
                    };
                }

                foreach (var variant in obj?.PathVariants?.Values ?? Enumerable.Empty<PathVariantInfo>())
                {
                    if (variant?.OverlayTransition != null)
                    {
                        yield return new OverlayTransitionContext
                        {
                            Object = obj,
                            Variant = variant,
                            Transition = variant.OverlayTransition
                        };
                    }
                }
            }
        }

        private static void ApplyExecutableOverlayTableResolution(
            string sourceDirectory,
            OverlayTransitionContext context)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) ||
                context?.Transition == null)
            {
                return;
            }

            string overlayName = ResolveLoadedOverlayNameFromExecutable(
                sourceDirectory,
                context.Transition.GlobalX,
                context.Transition.GlobalY,
                context.Transition.MapSelector);

            if (string.IsNullOrWhiteSpace(overlayName) ||
                string.Equals(context.Transition.LoadedOverlayName, overlayName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            context.Transition.LoadedOverlayName = overlayName;
            context.Transition.LoadedMapSector = null;
            context.Transition.LoadedSurfaceX = null;
            context.Transition.LoadedSurfaceY = null;
        }

        private static string ResolveLoadedOverlayNameFromExecutable(
            string sourceDirectory,
            byte globalX,
            byte globalY,
            ushort mapSelector)
        {
            var table = GetExecutableOverlayTable(sourceDirectory);
            if (table == null)
                return null;

            return table.Entries.TryGetValue(BuildKey(globalX, globalY, mapSelector), out string overlayName)
                ? overlayName
                : null;
        }

        private static ExecutableOverlayTable GetExecutableOverlayTable(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                return null;

            string exePath = Path.Combine(sourceDirectory, GameExecutableFileName);
            if (!File.Exists(exePath))
                return null;

            string cacheKey;
            try
            {
                cacheKey = Path.GetFullPath(exePath);
            }
            catch
            {
                cacheKey = exePath;
            }

            lock (ExecutableOverlayTableLock)
            {
                if (ExecutableOverlayTables.TryGetValue(cacheKey, out var cachedTable))
                    return cachedTable;
            }

            ExecutableOverlayTable table = TryReadExecutableOverlayTable(exePath);

            lock (ExecutableOverlayTableLock)
            {
                ExecutableOverlayTables[cacheKey] = table;
                return table;
            }
        }

        private static ExecutableOverlayTable TryReadExecutableOverlayTable(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return null;

            byte[] fileData;
            try
            {
                fileData = File.ReadAllBytes(exePath);
            }
            catch
            {
                return null;
            }

            if (!CanReadExecutableOverlayTables(fileData))
                return null;

            var table = new ExecutableOverlayTable();
            AddExecutableOverlayTableRange(fileData, table, 1, 0, 14);
            AddExecutableOverlayTableRange(fileData, table, 2, 14, 20);
            AddExecutableOverlayTableRange(fileData, table, 3, 34, 21);
            return table.Entries.Count == 0 ? null : table;
        }

        private static bool CanReadExecutableOverlayTables(byte[] fileData)
        {
            if (fileData == null)
                return false;

            int coordinateTableEnd = ExecutableOverlayCoordinateTableOffset + ExecutableOverlayEntryCount * 2;
            int pointerTableEnd = ExecutableOverlayPointerTableOffset + ExecutableOverlayEntryCount * 2;
            return coordinateTableEnd <= fileData.Length &&
                   pointerTableEnd <= fileData.Length &&
                   ExecutableOverlayNameBaseOffset >= 0 &&
                   ExecutableOverlayNameBaseOffset < fileData.Length;
        }

        private static void AddExecutableOverlayTableRange(
            byte[] fileData,
            ExecutableOverlayTable table,
            ushort mapSelector,
            int startIndex,
            int count)
        {
            if (fileData == null || table == null || startIndex < 0 || count <= 0)
                return;

            int endIndex = startIndex + count;
            if (endIndex > ExecutableOverlayEntryCount)
                return;

            for (int index = startIndex; index < endIndex; index++)
            {
                int coordinateOffset = ExecutableOverlayCoordinateTableOffset + index * 2;
                int pointerOffset = ExecutableOverlayPointerTableOffset + index * 2;

                byte globalX = fileData[coordinateOffset];
                byte globalY = fileData[coordinateOffset + 1];
                ushort namePointer = BitConverter.ToUInt16(fileData, pointerOffset);
                string overlayName = ReadExecutableOverlayName(fileData, namePointer);

                if (string.IsNullOrWhiteSpace(overlayName))
                    continue;

                table.Entries[BuildKey(globalX, globalY, mapSelector)] = overlayName;
            }
        }

        private static string ReadExecutableOverlayName(byte[] fileData, ushort namePointer)
        {
            if (fileData == null)
                return null;

            int offset = ExecutableOverlayNameBaseOffset + namePointer;
            if (offset < 0 || offset >= fileData.Length)
                return null;

            int end = offset;
            while (end < fileData.Length && fileData[end] != 0)
                end++;

            if (end == offset || end >= fileData.Length)
                return null;

            var chars = new char[end - offset];
            for (int i = 0; i < chars.Length; i++)
            {
                byte value = fileData[offset + i];
                if (!IsExecutableOverlayNameByte(value))
                    return null;

                chars[i] = (char)value;
            }

            return new string(chars).ToUpperInvariant() + ".OVR";
        }

        private static bool IsExecutableOverlayNameByte(byte value)
        {
            return (value >= (byte)'a' && value <= (byte)'z') ||
                   (value >= (byte)'0' && value <= (byte)'9');
        }

        private static void ApplySourceOutdoorSectorCorrection(
            string sourceDirectory,
            string sourceOverlayName,
            LoadedOverlayMetadata sourceMetadata,
            OverlayTransitionContext context)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) ||
                string.IsNullOrWhiteSpace(sourceOverlayName) ||
                IsOutdoorSectorOverlayName(sourceOverlayName) ||
                string.IsNullOrWhiteSpace(sourceMetadata?.MapSector) ||
                context?.Object == null ||
                context.Transition == null ||
                context.Transition.MapSelector != 2)
            {
                return;
            }

            string outdoorOverlayName = TryBuildOutdoorOverlayNameFromMapSector(sourceMetadata.MapSector);
            if (string.IsNullOrWhiteSpace(outdoorOverlayName))
                return;

            string outdoorOverlayPath = Path.Combine(sourceDirectory, outdoorOverlayName);
            if (!File.Exists(outdoorOverlayPath))
                return;

            if (!string.Equals(context.Transition.LoadedOverlayName, outdoorOverlayName, StringComparison.OrdinalIgnoreCase))
            {
                context.Transition.LoadedOverlayName = outdoorOverlayName;
                context.Transition.LoadedMapSector = null;
                context.Transition.LoadedSurfaceX = null;
                context.Transition.LoadedSurfaceY = null;
            }
        }

        private static void TryApplyReverseOutdoorTargetCorrection(
            string sourceDirectory,
            string sourceOverlayName,
            OverlayTransitionContext context)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) ||
                string.IsNullOrWhiteSpace(sourceOverlayName) ||
                IsOutdoorSectorOverlayName(sourceOverlayName) ||
                context?.Object == null ||
                context.Transition == null ||
                context.Transition.MapSelector != 2 ||
                !IsOutdoorSectorOverlayName(context.Transition.LoadedOverlayName))
            {
                return;
            }

            if (TryGetExactTeleportTarget(context, out _, out _))
                return;

            var index = GetReverseOutdoorTargetIndex(sourceDirectory, context.Transition.LoadedOverlayName);
            if (index == null)
                return;

            string interiorCellKey = BuildCellKey(context.Object.X, context.Object.Y);
            if (!index.ByInteriorCell.TryGetValue(interiorCellKey, out var candidates) ||
                candidates == null ||
                candidates.Count == 0)
            {
                return;
            }

            var sourceOverlayCandidates = candidates
                .Where(candidate => string.Equals(candidate.LoadedOverlayName, sourceOverlayName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sourceOverlayCandidates.Count > 0)
                candidates = sourceOverlayCandidates;

            var distinctCandidates = candidates
                .GroupBy(candidate => BuildCellKey(candidate.X, candidate.Y), StringComparer.Ordinal)
                .Select(group => group.FirstOrDefault(candidate => candidate.MapSelector != 1) ?? group.First())
                .ToList();

            if (distinctCandidates.Count != 1)
            {
                var nonPrimaryCandidates = distinctCandidates
                    .Where(candidate => candidate.MapSelector != 1)
                    .ToList();

                if (nonPrimaryCandidates.Count != 1)
                    return;

                distinctCandidates = nonPrimaryCandidates;
            }

            var target = distinctCandidates[0];
            ApplyTeleportTargetCorrection(context, target.X, target.Y);
        }

        private static void ApplyTeleportTargetCorrection(
            OverlayTransitionContext context,
            byte? teleportTargetX,
            byte? teleportTargetY)
        {
            if (context.Variant != null)
            {
                ApplyTeleportTargetCorrection(context.Variant, teleportTargetX, teleportTargetY);
                return;
            }

            ApplyTeleportTargetCorrection(context.Object, teleportTargetX, teleportTargetY);
        }

        private static void ApplyTeleportTargetCorrection(
            OvrObject obj,
            byte? teleportTargetX,
            byte? teleportTargetY)
        {
            if (obj == null)
                return;

            if (teleportTargetX.HasValue)
            {
                obj.TeleportTargetX = teleportTargetX.Value;
                obj.TeleportTargetXRange = new ValueRange8(teleportTargetX.Value, teleportTargetX.Value);
            }

            if (teleportTargetY.HasValue)
            {
                obj.TeleportTargetY = teleportTargetY.Value;
                obj.TeleportTargetYRange = new ValueRange8(teleportTargetY.Value, teleportTargetY.Value);
            }
        }

        private static void ApplyTeleportTargetCorrection(
            PathVariantInfo variant,
            byte? teleportTargetX,
            byte? teleportTargetY)
        {
            if (variant == null)
                return;

            if (teleportTargetX.HasValue)
            {
                variant.TeleportTargetX = teleportTargetX.Value;
                variant.TeleportTargetXRange = new ValueRange8(teleportTargetX.Value, teleportTargetX.Value);
            }

            if (teleportTargetY.HasValue)
            {
                variant.TeleportTargetY = teleportTargetY.Value;
                variant.TeleportTargetYRange = new ValueRange8(teleportTargetY.Value, teleportTargetY.Value);
            }
        }

        private static ReverseOutdoorTargetIndex GetReverseOutdoorTargetIndex(string sourceDirectory, string outdoorOverlayName)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outdoorOverlayName))
                return null;

            string outdoorOverlayPath = Path.Combine(sourceDirectory, outdoorOverlayName);
            if (!File.Exists(outdoorOverlayPath))
                return null;

            string cacheKey;
            try
            {
                cacheKey = Path.GetFullPath(outdoorOverlayPath);
            }
            catch
            {
                cacheKey = outdoorOverlayPath;
            }

            lock (ReverseOutdoorTargetIndexLock)
            {
                if (ReverseOutdoorTargetIndexes.TryGetValue(cacheKey, out var cachedIndex))
                    return cachedIndex;

                if (ReverseOutdoorTargetIndexesInProgress.Contains(cacheKey))
                    return null;

                ReverseOutdoorTargetIndexesInProgress.Add(cacheKey);
            }

            ReverseOutdoorTargetIndex index;
            try
            {
                index = BuildReverseOutdoorTargetIndex(outdoorOverlayPath);
            }
            catch
            {
                index = new ReverseOutdoorTargetIndex();
            }
            finally
            {
                lock (ReverseOutdoorTargetIndexLock)
                    ReverseOutdoorTargetIndexesInProgress.Remove(cacheKey);
            }

            lock (ReverseOutdoorTargetIndexLock)
            {
                ReverseOutdoorTargetIndexes[cacheKey] = index;
                return index;
            }
        }

        private static ReverseOutdoorTargetIndex BuildReverseOutdoorTargetIndex(string outdoorOverlayPath)
        {
            var index = new ReverseOutdoorTargetIndex();

            if (string.IsNullOrWhiteSpace(outdoorOverlayPath) ||
                !OvrFileConfigs.TryGetConfigForFile(outdoorOverlayPath, out var config, out _))
            {
                return index;
            }

            var objects = OvrFileAnalyzer.AnalyzeOvrFile(
                outdoorOverlayPath,
                config,
                new Dictionary<System.Drawing.Point, string>(),
                null);

            foreach (var context in EnumerateTransitionContexts(objects))
            {
                if (context?.Object == null ||
                    context.Transition == null ||
                    context.Transition.MapSelector == 2 ||
                    !TryGetExactTeleportTarget(context, out byte interiorX, out byte interiorY))
                {
                    continue;
                }

                string interiorCellKey = BuildCellKey(interiorX, interiorY);
                if (!index.ByInteriorCell.TryGetValue(interiorCellKey, out var targets))
                {
                    targets = new List<ReverseOutdoorTarget>();
                    index.ByInteriorCell[interiorCellKey] = targets;
                }

                if (targets.Any(target =>
                        target.X == context.Object.X &&
                        target.Y == context.Object.Y &&
                        string.Equals(target.LoadedOverlayName, context.Transition.LoadedOverlayName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                targets.Add(new ReverseOutdoorTarget
                {
                    X = context.Object.X,
                    Y = context.Object.Y,
                    MapSelector = context.Transition.MapSelector,
                    LoadedOverlayName = context.Transition.LoadedOverlayName
                });
            }

            return index;
        }

        private static bool TryGetExactTeleportTarget(OverlayTransitionContext context, out byte x, out byte y)
        {
            if (context?.Variant != null)
            {
                return TryGetExactTeleportTarget(
                    context.Variant.TeleportTargetX,
                    context.Variant.TeleportTargetY,
                    context.Variant.TeleportTargetXRange,
                    context.Variant.TeleportTargetYRange,
                    out x,
                    out y);
            }

            return TryGetExactTeleportTarget(
                context?.Object?.TeleportTargetX,
                context?.Object?.TeleportTargetY,
                context?.Object?.TeleportTargetXRange,
                context?.Object?.TeleportTargetYRange,
                out x,
                out y);
        }

        private static bool TryGetExactTeleportTarget(
            byte? teleportTargetX,
            byte? teleportTargetY,
            ValueRange8 teleportTargetXRange,
            ValueRange8 teleportTargetYRange,
            out byte x,
            out byte y)
        {
            x = 0;
            y = 0;

            byte? exactX = teleportTargetX;
            byte? exactY = teleportTargetY;

            if (!exactX.HasValue && teleportTargetXRange != null && teleportTargetXRange.IsExact)
                exactX = teleportTargetXRange.Min;
            if (!exactY.HasValue && teleportTargetYRange != null && teleportTargetYRange.IsExact)
                exactY = teleportTargetYRange.Min;

            if (!exactX.HasValue || !exactY.HasValue)
                return false;

            x = exactX.Value;
            y = exactY.Value;
            return true;
        }

        private static LoadedOverlayMetadata TryReadLoadedOverlayMetadata(string sourceDirectory, string loadedOverlayName)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(loadedOverlayName))
                return null;

            string targetPath = Path.Combine(sourceDirectory, loadedOverlayName);
            return TryReadOverlayMetadata(targetPath);
        }

        private static LoadedOverlayMetadata TryReadOverlayMetadata(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return null;

            if (!File.Exists(targetPath))
                return null;

            if (!OvrFileConfigs.TryGetConfigForFile(targetPath, out var config, out _))
                return null;

            byte[] fileData;
            try
            {
                fileData = File.ReadAllBytes(targetPath);
            }
            catch
            {
                return null;
            }

            bool isOutdoorOverlay = config.TryIsOutdoorOverlay(fileData, out bool detectedOutdoorOverlay) &&
                detectedOutdoorOverlay;

            return new LoadedOverlayMetadata
            {
                MapSector = ReadSectorMap(fileData, config.SectorMapLetter, config.SectorMapDigit),
                IsOutdoorOverlay = isOutdoorOverlay,
                SurfaceX = isOutdoorOverlay ? null : ReadMetadataByte(fileData, config.SurfaceX),
                SurfaceY = isOutdoorOverlay ? null : ReadMetadataByte(fileData, config.SurfaceY)
            };
        }

        private static byte? ReadMetadataByte(byte[] fileData, int address)
        {
            if (fileData == null || address < 0 || address >= fileData.Length)
                return null;

            return fileData[address];
        }

        private static string ReadSectorMap(byte[] fileData, int highAddress, int lowAddress)
        {
            if (fileData == null ||
                highAddress < 0 ||
                lowAddress < 0 ||
                highAddress >= fileData.Length ||
                lowAddress >= fileData.Length)
            {
                return null;
            }

            byte highByte = fileData[highAddress];
            byte lowByte = fileData[lowAddress];

            char highChar = (char)(highByte - 0xC1 + 'A');
            char lowChar = (char)(lowByte - 0xB1 + '1');

            return $"{highChar}-{lowChar}";
        }

        private static string ResolveOutdoorSectorOverlayName(byte globalX, byte globalY)
        {
            int letterIndex = globalX / 8;
            int digit = (globalY / 8) + 1;

            if (letterIndex < 0 || letterIndex > 4 || digit < 1 || digit > 4)
                return null;

            char letter = (char)('A' + letterIndex);
            return $"AREA{letter}{digit}.OVR";
        }

        private static bool IsOutdoorSectorOverlayName(string overlayName)
        {
            if (string.IsNullOrWhiteSpace(overlayName))
                return false;

            string name = Path.GetFileName(overlayName).ToUpperInvariant();
            return name.Length == "AREAA1.OVR".Length &&
                   name.StartsWith("AREA", StringComparison.Ordinal) &&
                   name[4] >= 'A' &&
                   name[4] <= 'E' &&
                   name[5] >= '1' &&
                   name[5] <= '4' &&
                   string.Equals(name.Substring(6), ".OVR", StringComparison.Ordinal);
        }

        private static string TryBuildOutdoorOverlayNameFromMapSector(string mapSector)
        {
            if (string.IsNullOrWhiteSpace(mapSector))
                return null;

            string normalized = mapSector.Trim().ToUpperInvariant();
            if (normalized.Length != 3 || normalized[1] != '-')
                return null;

            char letter = normalized[0];
            char digit = normalized[2];
            if (letter < 'A' || letter > 'E' || digit < '1' || digit > '4')
                return null;

            return $"AREA{letter}{digit}.OVR";
        }

        private static string BuildCellKey(byte x, byte y)
        {
            return $"{x}:{y}";
        }

        private static string BuildKey(int globalX, int globalY, int mapSelector)
        {
            return $"{globalX}:{globalY}:{mapSelector}";
        }
    }
}
