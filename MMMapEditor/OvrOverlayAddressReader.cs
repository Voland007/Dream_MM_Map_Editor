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
using System.Text;

namespace MMMapEditor
{
    internal static class OvrOverlayAddressReader
    {
        public const int MaxDecodedTextLength = 1000;

        private const ushort KnownResidentDataTextAddress = 0x1296;
        private const ushort KnownResidentDataValidationAddress = 0x12D1;
        private static readonly byte[] KnownResidentDataTextAnchor =
            Encoding.ASCII.GetBytes("ON THIS STONE STATUE OF ");
        private static readonly byte[] KnownResidentDataValidationAnchor =
            Encoding.ASCII.GetBytes("A HUMAN KNIGHT");
        private static readonly object ExecutableImageCacheSync = new object();
        private static readonly Dictionary<string, ResidentExecutableImage> ExecutableImageCache =
            new Dictionary<string, ResidentExecutableImage>(StringComparer.OrdinalIgnoreCase);

        private sealed class ResidentExecutableImage
        {
            public byte[] Data { get; set; }
            public int ResidentDataFileBase { get; set; } = -1;
        }

        public static bool TryMapOverlayAddressToFileOffset(
            BinaryReader br,
            OvrFileConfig config,
            ushort memAddr,
            out long fileOffset)
        {
            fileOffset = -1;

            if (br == null || config == null)
                return false;

            long textBaseOffset = memAddr - config.TextBaseAddr;
            if (textBaseOffset >= 0 && textBaseOffset < br.BaseStream.Length)
            {
                fileOffset = textBaseOffset;
                return true;
            }

            return false;
        }

        public static bool TryReadByte(
            BinaryReader br,
            OvrFileConfig config,
            ushort memAddr,
            out byte value)
        {
            value = 0;

            if (!TryMapOverlayAddressToFileOffset(br, config, memAddr, out long fileOffset))
                return false;

            long originalPos = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset;
                value = br.ReadByte();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
            finally
            {
                br.BaseStream.Position = originalPos;
            }
        }

        public static bool TryReadWord(
            BinaryReader br,
            OvrFileConfig config,
            ushort memAddr,
            out ushort value)
        {
            value = 0;

            if (!TryMapOverlayAddressToFileOffset(br, config, memAddr, out long fileOffset))
                return false;

            if (fileOffset + 1 >= br.BaseStream.Length)
                return false;

            long originalPos = br.BaseStream.Position;
            try
            {
                br.BaseStream.Position = fileOffset;
                byte lowByte = br.ReadByte();
                byte highByte = br.ReadByte();
                value = (ushort)((highByte << 8) | lowByte);
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
            finally
            {
                br.BaseStream.Position = originalPos;
            }
        }

        public static string ExtractText(BinaryReader br, OvrFileConfig config, ushort textAddress)
        {
            try
            {
                if (!TryMapOverlayAddressToFileOffset(br, config, textAddress, out long fileOffset))
                {
                    return $"Cannot locate text at 0x{textAddress:X4}";
                }

                long originalPos = br.BaseStream.Position;
                br.BaseStream.Position = fileOffset;

                var bytes = new List<byte>();
                while (br.BaseStream.Position < br.BaseStream.Length &&
                       bytes.Count < MaxDecodedTextLength)
                {
                    byte b = br.ReadByte();
                    if (b == 0)
                        break;

                    bytes.Add(b);
                }

                br.BaseStream.Position = originalPos;

                if (bytes.Count == 0)
                    return "(empty string)";

                return DecodeText(bytes.ToArray());
            }
            catch (Exception ex)
            {
                return $"Error reading text: {ex.Message}";
            }
        }

        public static bool TryReadExecutableByte(OvrFileConfig config, ushort memAddr, out byte value)
        {
            value = 0;

            if (!TryMapExecutableResidentAddressToFileOffset(config, memAddr, out long fileOffset, out var image))
                return false;

            if (fileOffset < 0 || fileOffset >= image.Data.Length)
                return false;

            value = image.Data[fileOffset];
            return true;
        }

        public static bool TryReadExecutableWord(OvrFileConfig config, ushort memAddr, out ushort value)
        {
            value = 0;

            if (!TryMapExecutableResidentAddressToFileOffset(config, memAddr, out long fileOffset, out var image))
                return false;

            if (fileOffset < 0 || fileOffset + 1 >= image.Data.Length)
                return false;

            value = (ushort)(image.Data[fileOffset] | (image.Data[fileOffset + 1] << 8));
            return true;
        }

        public static bool TryExtractExecutableText(OvrFileConfig config, ushort textAddress, out string text)
        {
            text = null;

            if (!TryMapExecutableResidentAddressToFileOffset(config, textAddress, out long fileOffset, out var image))
                return false;

            var bytes = new List<byte>();
            for (long pos = fileOffset; pos < image.Data.Length && bytes.Count < MaxDecodedTextLength; pos++)
            {
                byte value = image.Data[pos];
                if (value == 0)
                    break;

                bytes.Add(value);
            }

            if (bytes.Count == 0)
                return false;

            text = DecodeText(bytes.ToArray());
            return !string.IsNullOrEmpty(text);
        }

        private static bool TryMapExecutableResidentAddressToFileOffset(
            OvrFileConfig config,
            ushort memAddr,
            out long fileOffset,
            out ResidentExecutableImage image)
        {
            fileOffset = -1;
            image = null;

            if (config == null ||
                string.IsNullOrWhiteSpace(config.GameExecutablePath))
            {
                return false;
            }

            image = GetResidentExecutableImage(config.GameExecutablePath);
            if (image == null || image.ResidentDataFileBase < 0 || image.Data == null)
                return false;

            fileOffset = image.ResidentDataFileBase + memAddr;
            return fileOffset >= 0 && fileOffset < image.Data.Length;
        }

        private static ResidentExecutableImage GetResidentExecutableImage(string executablePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                return null;
            }

            lock (ExecutableImageCacheSync)
            {
                if (ExecutableImageCache.TryGetValue(fullPath, out var cachedImage))
                    return cachedImage;

                ResidentExecutableImage image = LoadResidentExecutableImage(fullPath);
                ExecutableImageCache[fullPath] = image;
                return image;
            }
        }

        private static ResidentExecutableImage LoadResidentExecutableImage(string executablePath)
        {
            try
            {
                if (!File.Exists(executablePath))
                    return null;

                byte[] data = File.ReadAllBytes(executablePath);
                int residentDataFileBase = ResolveResidentDataFileBase(data);
                return new ResidentExecutableImage
                {
                    Data = data,
                    ResidentDataFileBase = residentDataFileBase
                };
            }
            catch
            {
                return null;
            }
        }

        private static int ResolveResidentDataFileBase(byte[] data)
        {
            int anchorOffset = IndexOf(data, KnownResidentDataTextAnchor, 0);
            if (anchorOffset < 0)
                return -1;

            int baseOffset = anchorOffset - KnownResidentDataTextAddress;
            if (baseOffset < 0)
                return -1;

            int validationOffset = baseOffset + KnownResidentDataValidationAddress;
            if (!MatchesAt(data, KnownResidentDataValidationAnchor, validationOffset))
                return -1;

            return baseOffset;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int startOffset)
        {
            if (data == null ||
                pattern == null ||
                pattern.Length == 0 ||
                startOffset < 0 ||
                startOffset > data.Length - pattern.Length)
            {
                return -1;
            }

            for (int i = startOffset; i <= data.Length - pattern.Length; i++)
            {
                if (MatchesAt(data, pattern, i))
                    return i;
            }

            return -1;
        }

        private static bool MatchesAt(byte[] data, byte[] pattern, int offset)
        {
            if (data == null ||
                pattern == null ||
                offset < 0 ||
                offset + pattern.Length > data.Length)
            {
                return false;
            }

            for (int i = 0; i < pattern.Length; i++)
            {
                if (data[offset + i] != pattern[i])
                    return false;
            }

            return true;
        }

        public static string DecodeText(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes ?? Array.Empty<byte>())
            {
                if (b == 0x0D) sb.Append("\\r");
                else if (b == 0x0A) sb.Append("\\n");
                else if (b == 0x09) sb.Append("\\t");
                else if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
                else if (b == 0x22) sb.Append("\\\"");
                else if (b == 0x5C) sb.Append("\\\\");
                else sb.Append($"\\x{b:X2}");
            }

            return sb.ToString();
        }
    }
}
