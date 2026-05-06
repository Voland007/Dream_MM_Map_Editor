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
        private readonly OvrSideLayoutTemplate _template;

        public OvrSideLayout(OvrSideLayoutRecord record, OvrSideLayoutTemplate template)
        {
            Record = record;
            _template = template ?? throw new ArgumentNullException(nameof(template));
        }

        public OvrSideLayoutRecord Record { get; }
        public string FamilyName => _template.FamilyName;

        public OvrSideElementDefinition? GetDefinition(int structureBits)
            => _template.GetDefinition(structureBits);
    }

    internal sealed class OvrSideLayoutTemplate
    {
        private readonly OvrSideElementDefinition[] _definitions;

        public OvrSideLayoutTemplate(string familyName, OvrSideElementDefinition bit01, OvrSideElementDefinition bit10, OvrSideElementDefinition bit11)
        {
            FamilyName = familyName ?? throw new ArgumentNullException(nameof(familyName));
            _definitions = new OvrSideElementDefinition[4];
            _definitions[1] = bit01;
            _definitions[2] = bit10;
            _definitions[3] = bit11;
        }

        public string FamilyName { get; }

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

    internal readonly struct OvrSideLayoutFamilyKey : IEquatable<OvrSideLayoutFamilyKey>
    {
        public OvrSideLayoutFamilyKey(OvrSideKey bit01Key, OvrSideKey bit10Key, OvrSideKey bit11Key, byte tailByte)
        {
            Bit01Key = bit01Key;
            Bit10Key = bit10Key;
            Bit11Key = bit11Key;
            TailByte = tailByte;
        }

        public OvrSideKey Bit01Key { get; }
        public OvrSideKey Bit10Key { get; }
        public OvrSideKey Bit11Key { get; }
        public byte TailByte { get; }

        public bool Equals(OvrSideLayoutFamilyKey other) =>
            Bit01Key.Equals(other.Bit01Key) &&
            Bit10Key.Equals(other.Bit10Key) &&
            Bit11Key.Equals(other.Bit11Key) &&
            TailByte == other.TailByte;

        public override bool Equals(object? obj) =>
            obj is OvrSideLayoutFamilyKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Bit01Key, Bit10Key, Bit11Key, TailByte);

        public override string ToString() =>
            $"{Bit01Key} | {Bit10Key} | {Bit11Key} | tail {TailByte:X2}";
    }

    internal readonly struct OvrSideLayoutRecord
    {
        public OvrSideLayoutRecord(
            byte mapId,
            OvrSideKey bit01Key,
            OvrSideKey bit10Key,
            OvrSideKey bit11Key,
            byte tailByte,
            ReadOnlyMemory<byte> trailingBytes)
        {
            MapId = mapId;
            Bit01Key = bit01Key;
            Bit10Key = bit10Key;
            Bit11Key = bit11Key;
            TailByte = tailByte;
            TrailingBytes = trailingBytes;
        }

        public byte MapId { get; }
        public OvrSideKey Bit01Key { get; }
        public OvrSideKey Bit10Key { get; }
        public OvrSideKey Bit11Key { get; }
        public byte TailByte { get; }
        public ReadOnlyMemory<byte> TrailingBytes { get; }

        public OvrSideLayoutFamilyKey FamilyKey =>
            new OvrSideLayoutFamilyKey(Bit01Key, Bit10Key, Bit11Key, TailByte);
    }

    internal static class OvrSideElementRegistry
    {
        private const int SideKeysOffsetFromStartAddress = 0x31;

        private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<OvrSideKey, OvrSideElementDefinition>> KnownDefinitionsByStructureBits =
            new Dictionary<int, IReadOnlyDictionary<OvrSideKey, OvrSideElementDefinition>>
            {
                [0x01] = new Dictionary<OvrSideKey, OvrSideElementDefinition>
                {
                    [new OvrSideKey(0x01, 0x0D)] = CreateSecretWall("Кирпичная стена"),
                    [new OvrSideKey(0x01, 0x1A)] = CreateSecretWall("Каменная стена"),
                    [new OvrSideKey(0x02, 0x0B)] = CreateBorder("Дубовый лес")
                },
                [0x02] = new Dictionary<OvrSideKey, OvrSideElementDefinition>
                {
                    [new OvrSideKey(0x01, 0x0B)] = CreateDoor("Кирпичная стена"),
                    [new OvrSideKey(0x01, 0x18)] = CreateDoor("Каменная стена"),
                    [new OvrSideKey(0x01, 0x0D)] = CreateSecretWall("Кирпичная стена"),
                    [new OvrSideKey(0x0B, 0x0A)] = CreateBorder("Горы")
                },
                [0x03] = new Dictionary<OvrSideKey, OvrSideElementDefinition>
                {
                    [new OvrSideKey(0x0B, 0x0A)] = CreateSecretWall("Кирпичная стена"),
                    [new OvrSideKey(0x0B, 0x17)] = CreateGrate("Каменная стена"),
                    [new OvrSideKey(0x01, 0x0B)] = CreateDoor("Кирпичная стена"),
                    [new OvrSideKey(0x05, 0x0D)] = CreateBorder("Еловый лес"),
                    [new OvrSideKey(0x05, 0x1A)] = CreateBorder("\u0412\u043E\u0434\u0430")
                }
            };

        private const int SideLayoutRecordOffsetFromStartAddress = 0x32;
        private const int SideLayoutRecordLength = 0x14;
        private const int SideLayoutTrailingBytesOffset = 0x08;
        private const int SideLayoutTrailingBytesLength = SideLayoutRecordLength - SideLayoutTrailingBytesOffset;

        private static readonly IReadOnlyDictionary<OvrSideLayoutFamilyKey, OvrSideLayoutTemplate> KnownTemplatesByFamilyKey =
            new Dictionary<OvrSideLayoutFamilyKey, OvrSideLayoutTemplate>
            {
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x01, 0x0D),
                    new OvrSideKey(0x01, 0x0B),
                    new OvrSideKey(0x0B, 0x0A),
                    0x05)] = CreateLayoutTemplate(
                        "City",
                        CreateSecretWall("\u041A\u0438\u0440\u043F\u0438\u0447\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateDoor("\u041A\u0438\u0440\u043F\u0438\u0447\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateSecretWall("\u041A\u0438\u0440\u043F\u0438\u0447\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430")),
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x01, 0x1A),
                    new OvrSideKey(0x01, 0x18),
                    new OvrSideKey(0x0B, 0x17),
                    0x05)] = CreateLayoutTemplate(
                        "Cave",
                        CreateSecretWall("\u041A\u0430\u043C\u0435\u043D\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateDoor("\u041A\u0430\u043C\u0435\u043D\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateGrate("\u041A\u0430\u043C\u0435\u043D\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430")),
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x01, 0x1A),
                    new OvrSideKey(0x01, 0x0D),
                    new OvrSideKey(0x01, 0x0B),
                    0x0B)] = CreateLayoutTemplate(
                        "Cave9",
                        CreateSecretWall("\u041A\u0430\u043C\u0435\u043D\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateSecretWall("\u041A\u0438\u0440\u043F\u0438\u0447\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430"),
                        CreateDoor("\u041A\u0438\u0440\u043F\u0438\u0447\u043D\u0430\u044F \u0441\u0442\u0435\u043D\u0430")),
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x02, 0x0B),
                    new OvrSideKey(0x0B, 0x0A),
                    new OvrSideKey(0x05, 0x0D),
                    0x01)] = CreateLayoutTemplate(
                        "Outdoor",
                        CreateBorder("\u0414\u0443\u0431\u043E\u0432\u044B\u0439 \u043B\u0435\u0441"),
                        CreateBorder("\u0413\u043E\u0440\u044B"),
                        CreateBorder("\u0415\u043B\u043E\u0432\u044B\u0439 \u043B\u0435\u0441")),
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x02, 0x0B),
                    new OvrSideKey(0x0B, 0x17),
                    new OvrSideKey(0x05, 0x18),
                    0x0B)] = CreateLayoutTemplate(
                        "OutdoorSnow",
                        CreateBorder("\u0414\u0443\u0431\u043E\u0432\u044B\u0439 \u043B\u0435\u0441"),
                        CreateBorder("\u0413\u043E\u0440\u044B (\u0441\u043D\u0435\u0433)"),
                        CreateBorder("\u0414\u0443\u0431\u043E\u0432\u044B\u0439 \u043B\u0435\u0441(\u0441\u043D\u0435\u0433)")),
                [new OvrSideLayoutFamilyKey(
                    new OvrSideKey(0x02, 0x0B),
                    new OvrSideKey(0x0B, 0x17),
                    new OvrSideKey(0x05, 0x1A),
                    0x01)] = CreateLayoutTemplate(
                        "OutdoorWater",
                        CreateBorder("\u0414\u0443\u0431\u043E\u0432\u044B\u0439 \u043B\u0435\u0441"),
                        CreateBorder("\u0413\u043E\u0440\u044B (\u0441\u043D\u0435\u0433)"),
                        CreateBorder("\u0412\u043E\u0434\u0430"))
            };

        public static OvrSideLayout ReadLayout(byte[] fileData, OvrFileConfig config, string fileNameOnly)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (fileData == null)
                throw new ArgumentNullException(nameof(fileData));

            int offset = config.StartAddress - SideLayoutRecordOffsetFromStartAddress;
            if (offset < 0 || offset + SideLayoutRecordLength > fileData.Length)
            {
                throw new InvalidOperationException(
                    $"Файл {fileNameOnly} слишком мал для чтения ключей сторон по адресу 0x{offset:X}.");
            }

            OvrSideLayoutRecord record = ReadRecord(fileData, offset);
            if (KnownTemplatesByFamilyKey.TryGetValue(record.FamilyKey, out OvrSideLayoutTemplate? template))
                return new OvrSideLayout(record, template);

            throw new InvalidOperationException(
                $"\u0414\u043B\u044F \u0444\u0430\u0439\u043B\u0430 {fileNameOnly} \u043D\u0430\u0439\u0434\u0435\u043D\u043E \u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E\u0435 \u0441\u0435\u043C\u0435\u0439\u0441\u0442\u0432\u043E \u043E\u043F\u0438\u0441\u0430\u043D\u0438\u044F \u0441\u0442\u043E\u0440\u043E\u043D {record.FamilyKey} " +
                $"(mapId {record.MapId:X2}, trailing {FormatBytes(record.TrailingBytes)}). " +
                "\u0414\u043E\u0431\u0430\u0432\u044C\u0442\u0435 \u0435\u0433\u043E \u0432 OvrSideElementRegistry.");
        }

        private static OvrSideLayoutRecord ReadRecord(byte[] fileData, int offset) =>
            new OvrSideLayoutRecord(
                fileData[offset],
                new OvrSideKey(fileData[offset + 1], fileData[offset + 2]),
                new OvrSideKey(fileData[offset + 3], fileData[offset + 4]),
                new OvrSideKey(fileData[offset + 5], fileData[offset + 6]),
                fileData[offset + 7],
                fileData.AsSpan(offset + SideLayoutTrailingBytesOffset, SideLayoutTrailingBytesLength).ToArray());

        private static string FormatBytes(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.IsEmpty)
                return "<none>";

            return BitConverter.ToString(bytes.Span.ToArray()).Replace('-', ' ');
        }

        private static OvrSideLayoutTemplate CreateLayoutTemplate(
            string familyName,
            OvrSideElementDefinition bit01,
            OvrSideElementDefinition bit10,
            OvrSideElementDefinition bit11) =>
            new OvrSideLayoutTemplate(familyName, bit01, bit10, bit11);

        private static OvrSideElementDefinition ResolveDefinition(string fileNameOnly, OvrSideKey key, int structureBits)
        {
            if (KnownDefinitionsByStructureBits.TryGetValue(structureBits, out IReadOnlyDictionary<OvrSideKey, OvrSideElementDefinition>? definitionsByStructureBits) &&
                definitionsByStructureBits.TryGetValue(key, out OvrSideElementDefinition? definition))
            {
                return definition;
            }

            throw new InvalidOperationException(
                $"Для файла {fileNameOnly} найден неизвестный ключ сторон {key} для битов {structureBits:X2}. " +
                "Добавьте его в OvrSideElementRegistry.");
        }

        private static OvrSideElementDefinition CreateBorder(string borderType) =>
            new OvrSideElementDefinition
            {
                BorderType = borderType
            };

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
