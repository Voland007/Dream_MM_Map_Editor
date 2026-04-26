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
                    return $"Cannot locate text at 0x{textAddress:X4}";

                long originalPos = br.BaseStream.Position;
                br.BaseStream.Position = fileOffset;

                var bytes = new List<byte>();
                byte b;
                int maxLength = 250;

                while ((b = br.ReadByte()) != 0 && bytes.Count < maxLength)
                    bytes.Add(b);

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
