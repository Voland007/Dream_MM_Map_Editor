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

namespace MMMapEditor
{
    internal sealed class OvrSideElementDefinition
    {
        public string BorderType { get; init; } = "Пустота";
        public int PassageType { get; init; }
        public bool SuppressPassageWhenSecondLowBitSet { get; init; }
        public bool MarkClosedWhenSecondLowBitSet { get; init; }
    }

    internal sealed class OvrSideLayout
    {
        private readonly OvrSideElementDefinition[] _definitions;

        public OvrSideLayout(OvrSideElementDefinition bit01, OvrSideElementDefinition bit10, OvrSideElementDefinition bit11)
        {
            _definitions = new OvrSideElementDefinition[4];
            _definitions[1] = bit01;
            _definitions[2] = bit10;
            _definitions[3] = bit11;
        }

        public OvrSideElementDefinition? GetDefinition(int structureBits)
        {
            if (structureBits < 0 || structureBits >= _definitions.Length)
                return null;

            return _definitions[structureBits];
        }
    }

    internal readonly struct OvrSideKey : IEquatable<OvrSideKey>
    {
        public OvrSideKey(byte firstByte, byte secondByte)
        {
            FirstByte = firstByte;
            SecondByte = secondByte;
        }

        public byte FirstByte { get; }
        public byte SecondByte { get; }

        public bool Equals(OvrSideKey other) =>
            FirstByte == other.FirstByte && SecondByte == other.SecondByte;

        public override bool Equals(object? obj) =>
            obj is OvrSideKey other && Equals(other);

        public override int GetHashCode() =>
            (FirstByte << 8) | SecondByte;

        public override string ToString() =>
            $"{FirstByte:X2} {SecondByte:X2}";
    }

    internal static class OvrSideElementRegistry
    {
        private const int SideKeysOffsetFromStartAddress = 0x31;

        private static readonly IReadOnlyDictionary<OvrSideKey, OvrSideElementDefinition> KnownDefinitions =
            new Dictionary<OvrSideKey, OvrSideElementDefinition>
            {
                [new OvrSideKey(0x01, 0x0D)] = CreateSecretWall("Кирпичная стена"),
                [new OvrSideKey(0x01, 0x0B)] = CreateDoor("Кирпичная стена"),
                [new OvrSideKey(0x0B, 0x0A)] = CreateSecretWall("Кирпичная стена"),
                [new OvrSideKey(0x01, 0x1A)] = CreateSecretWall("Каменная стена"),
                [new OvrSideKey(0x01, 0x18)] = CreateDoor("Каменная стена"),
                [new OvrSideKey(0x0B, 0x17)] = CreateGrate("Каменная стена")
            };

        public static OvrSideLayout ReadLayout(byte[] fileData, OvrFileConfig config, string fileNameOnly)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (fileData == null)
                throw new ArgumentNullException(nameof(fileData));

            int offset = config.StartAddress - SideKeysOffsetFromStartAddress;
            if (offset < 0 || offset + 5 >= fileData.Length)
            {
                throw new InvalidOperationException(
                    $"Файл {fileNameOnly} слишком мал для чтения ключей сторон по адресу 0x{offset:X}.");
            }

            OvrSideKey bit01Key = new OvrSideKey(fileData[offset], fileData[offset + 1]);
            OvrSideKey bit10Key = new OvrSideKey(fileData[offset + 2], fileData[offset + 3]);
            OvrSideKey bit11Key = new OvrSideKey(fileData[offset + 4], fileData[offset + 5]);

            return new OvrSideLayout(
                ResolveDefinition(fileNameOnly, bit01Key, 0x01),
                ResolveDefinition(fileNameOnly, bit10Key, 0x02),
                ResolveDefinition(fileNameOnly, bit11Key, 0x03));
        }

        private static OvrSideElementDefinition ResolveDefinition(string fileNameOnly, OvrSideKey key, int structureBits)
        {
            if (KnownDefinitions.TryGetValue(key, out OvrSideElementDefinition definition))
                return definition;

            throw new InvalidOperationException(
                $"Для файла {fileNameOnly} найден неизвестный ключ сторон {key} для битов {structureBits:X2}. " +
                "Добавьте его в OvrSideElementRegistry.");
        }

        private static OvrSideElementDefinition CreateSecretWall(string borderType) =>
            new OvrSideElementDefinition
            {
                BorderType = borderType,
                PassageType = 3,
                SuppressPassageWhenSecondLowBitSet = true
            };

        private static OvrSideElementDefinition CreateDoor(string borderType) =>
            new OvrSideElementDefinition
            {
                BorderType = borderType,
                PassageType = 1,
                MarkClosedWhenSecondLowBitSet = true
            };

        private static OvrSideElementDefinition CreateGrate(string borderType) =>
            new OvrSideElementDefinition
            {
                BorderType = borderType,
                PassageType = 2,
                MarkClosedWhenSecondLowBitSet = true
            };
    }
}
