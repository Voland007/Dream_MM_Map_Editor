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
using Newtonsoft.Json;

namespace MMMapEditor
{
    public enum Direction
    {
        Top,
        Bottom,
        Left,
        Right
    }

    public enum Lighting
    {
        Light,
        Dark,
        Darkness
    }

    public sealed class Directions<T> : IEquatable<Directions<T>>
    {
        [JsonProperty("Item1")]
        public T Top { get; set; }

        [JsonProperty("Item2")]
        public T Bottom { get; set; }

        [JsonProperty("Item3")]
        public T Left { get; set; }

        [JsonProperty("Item4")]
        public T Right { get; set; }

        public Directions()
        {
        }

        public Directions(T top, T bottom, T left, T right)
        {
            Top = top;
            Bottom = bottom;
            Left = left;
            Right = right;
        }

        public T Get(Direction side)
        {
            return side switch
            {
                Direction.Top => Top,
                Direction.Bottom => Bottom,
                Direction.Left => Left,
                Direction.Right => Right,
                _ => throw new ArgumentOutOfRangeException(nameof(side))
            };
        }

        public void Set(Direction side, T value)
        {
            switch (side)
            {
                case Direction.Top:
                    Top = value;
                    break;
                case Direction.Bottom:
                    Bottom = value;
                    break;
                case Direction.Left:
                    Left = value;
                    break;
                case Direction.Right:
                    Right = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(side));
            }
        }

        public Directions<T> Clone()
        {
            return new Directions<T>(Top, Bottom, Left, Right);
        }

        public bool Equals(Directions<T>? other)
        {
            if (ReferenceEquals(other, null))
                return false;

            return EqualityComparer<T>.Default.Equals(Top, other.Top)
                && EqualityComparer<T>.Default.Equals(Bottom, other.Bottom)
                && EqualityComparer<T>.Default.Equals(Left, other.Left)
                && EqualityComparer<T>.Default.Equals(Right, other.Right);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Directions<T>);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + (Top == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Top));
            hash = hash * 31 + (Bottom == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Bottom));
            hash = hash * 31 + (Left == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Left));
            hash = hash * 31 + (Right == null ? 0 : EqualityComparer<T>.Default.GetHashCode(Right));
            return hash;
        }
    }
}
