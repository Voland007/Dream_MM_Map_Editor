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


namespace MMMapEditor
{
    public class OvrObject
    {
        public byte X { get; set; }
        public byte Y { get; set; }
        public byte DirectionByte { get; set; }

        // Словарь для хранения текстов по путям: путь -> список текстов
        public Dictionary<int, HashSet<string>> PathTexts { get; set; } = new Dictionary<int, HashSet<string>>();

        // Новые свойства для информации о монстрах
        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }

        // Свойство, которое говорит, нужно ли показывать префикс Path0
        public bool ShouldShowPath0
        {
            get
            {
                if (!PathTexts.ContainsKey(0) || PathTexts[0].Count == 0)
                    return false;

                // Проверяем, есть ли уникальные альтернативные пути
                return PathTexts.Any(kvp => kvp.Key != 0 && kvp.Value.Count > 0);
            }
        }

        // Получение всех направлений с сообщениями
        public List<Direction> GetDirectionsWithMessages()
        {
            var directions = new List<Direction>();

            for (int i = 0; i < 4; i++)
            {
                int mask = 0x3 << (i * 2);
                bool hasMessage = (DirectionByte & mask) == mask;

                if (hasMessage)
                {
                    Direction direction = i switch
                    {
                        0 => Direction.Bottom,
                        1 => Direction.Left,
                        2 => Direction.Right,
                        3 => Direction.Top,
                        _ => Direction.Top
                    };
                    directions.Add(direction);
                }
            }

            return directions;
        }
    }
}