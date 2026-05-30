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
        private sealed class LoadedOverlayMetadata
        {
            public string MapSector { get; set; }
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

        private static readonly Dictionary<string, string> KnownInteriorTargets =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { BuildKey(4, 6, 1), "SORPIGAL.OVR" },
                { BuildKey(17, 10, 1), "CAVE1.OVR" },
                { BuildKey(1, 0, 1), "CAVE2.OVR" },
                { BuildKey(1, 12, 1), "CAVE3.OVR" },
                { BuildKey(2, 2, 1), "CAVE4.OVR" },
                { BuildKey(5, 0, 1), "CAVE5.OVR" },
                { BuildKey(27, 5, 1), "CAVE6.OVR" },
                { BuildKey(18, 2, 1), "CAVE7.OVR" },
                { BuildKey(1, 6, 1), "CAVE8.OVR" },
                { BuildKey(0, 10, 1), "CAVE9.OVR" },
                { BuildKey(3, 12, 1), "PORTSMIT.OVR" },
                { BuildKey(3, 2, 1), "ALGARY.OVR" },
                { BuildKey(2, 8, 1), "DUSK.OVR" },
                { BuildKey(26, 11, 1), "ERLIQUIN.OVR" },
                { BuildKey(8, 5, 3), "BLACKRS.OVR" },
                { BuildKey(8, 15, 3), "BLACKRN.OVR" },
                { BuildKey(6, 7, 3), "DOOM.OVR" },
                { BuildKey(1, 15, 3), "PP1.OVR" },
                { BuildKey(1, 7, 3), "PP2.OVR" },
                { BuildKey(0, 14, 3), "PP3.OVR" },
                { BuildKey(1, 2, 3), "PP4.OVR" },
                { BuildKey(3, 15, 3), "QVL1.OVR" },
                { BuildKey(3, 7, 3), "QVL2.OVR" },
                { BuildKey(2, 15, 3), "RWL1.OVR" },
                { BuildKey(2, 7, 3), "RWL2.OVR" },
                { BuildKey(4, 15, 3), "ENF1.OVR" },
                { BuildKey(4, 7, 3), "ENF2.OVR" },
                { BuildKey(18, 4, 3), "DEMON.OVR" },
                { BuildKey(26, 11, 3), "DEMON.OVR" },
                { BuildKey(7, 11, 3), "ALAMAR.OVR" },
                { BuildKey(17, 10, 3), "WHITEW.OVR" },
                { BuildKey(7, 1, 3), "DRAGAD.OVR" },
                { BuildKey(5, 15, 3), "UDRAG1.OVR" },
                { BuildKey(0, 10, 3), "UDRAG2.OVR" },
                { BuildKey(5, 7, 3), "UDRAG3.OVR" }
            };

        private static readonly object ReverseOutdoorTargetIndexLock = new object();
        private static readonly Dictionary<string, ReverseOutdoorTargetIndex> ReverseOutdoorTargetIndexes =
            new Dictionary<string, ReverseOutdoorTargetIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ReverseOutdoorTargetIndexesInProgress =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string ResolveLoadedOverlayName(byte globalX, byte globalY, ushort mapSelector)
        {
            if (mapSelector == 2)
                return ResolveOutdoorSectorOverlayName(globalX, globalY);

            if (KnownInteriorTargets.TryGetValue(BuildKey(globalX, globalY, mapSelector), out string knownName))
                return knownName;

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

            return new LoadedOverlayMetadata
            {
                MapSector = ReadSectorMap(fileData, config.SectorMapLetter, config.SectorMapDigit),
                SurfaceX = ReadMetadataByte(fileData, config.SurfaceX),
                SurfaceY = ReadMetadataByte(fileData, config.SurfaceY)
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
