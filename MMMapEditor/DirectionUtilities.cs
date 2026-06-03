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
    internal enum DirectionByteLayout
    {
        LegacyTextImport,
        OvrObject
    }

    internal static class DirectionUtilities
    {
        private static readonly Direction[] LegacyTextImportDirectionsByPairIndex =
        {
            Direction.Bottom,
            Direction.Left,
            Direction.Right,
            Direction.Top
        };

        private static readonly Direction[] OvrObjectDirectionsByPairIndex =
        {
            Direction.Left,
            Direction.Bottom,
            Direction.Right,
            Direction.Top
        };

        public static Directions<T> Filled<T>(T value)
        {
            return new Directions<T>(value, value, value, value);
        }

        public static Direction Opposite(Direction direction)
        {
            return direction switch
            {
                Direction.Top => Direction.Bottom,
                Direction.Bottom => Direction.Top,
                Direction.Left => Direction.Right,
                Direction.Right => Direction.Left,
                _ => throw new ArgumentOutOfRangeException(nameof(direction))
            };
        }

        public static IReadOnlyList<Direction> GetMessageDirections(byte directionByte, DirectionByteLayout layout)
        {
            Direction[] mapping = GetDirectionMapping(layout);
            var directions = new List<Direction>(mapping.Length);

            for (int i = 0; i < mapping.Length; i++)
            {
                if (IsMessageBitPairSet(directionByte, i))
                    directions.Add(mapping[i]);
            }

            return directions;
        }

        public static bool HasMessageInDirection(byte directionByte, Direction direction, DirectionByteLayout layout)
        {
            Direction[] mapping = GetDirectionMapping(layout);

            for (int i = 0; i < mapping.Length; i++)
            {
                if (mapping[i] == direction)
                    return IsMessageBitPairSet(directionByte, i);
            }

            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        public static void MergeMessages(Directions<bool> target, IEnumerable<Direction> directions)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (directions == null)
                return;

            foreach (Direction direction in directions)
                target.Set(direction, true);
        }

        private static bool IsMessageBitPairSet(byte directionByte, int pairIndex)
        {
            int mask = 0x3 << (pairIndex * 2);
            return (directionByte & mask) == mask;
        }

        private static Direction[] GetDirectionMapping(DirectionByteLayout layout)
        {
            return layout switch
            {
                DirectionByteLayout.LegacyTextImport => LegacyTextImportDirectionsByPairIndex,
                DirectionByteLayout.OvrObject => OvrObjectDirectionsByPairIndex,
                _ => throw new ArgumentOutOfRangeException(nameof(layout))
            };
        }
    }
}
