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
using System.Linq;
using System.Text;

namespace MMMapEditor
{
    public class OvrObject
    {
        #region Основные свойства

        public byte X { get; set; }
        public byte Y { get; set; }
        public byte DirectionByte { get; set; }
        public Dictionary<int, HashSet<string>> PathTexts { get; set; } = new Dictionary<int, HashSet<string>>();

        #endregion

        #region Характеристики монстров (статистики)

        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }

        #endregion

        #region Информация о битве (новое)

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public bool HasBattleInfo => BattleMonsters.Count > 0;
        public bool HasMonsterStatChanges => MonsterPower.HasValue || MonsterLevel.HasValue;

        #endregion

        #region Свойства для текстовых путей

        public bool ShouldShowPath0
        {
            get
            {
                if (!PathTexts.ContainsKey(0) || PathTexts[0] == null || PathTexts[0].Count == 0)
                    return false;
                return PathTexts.Any(kvp => kvp.Key != 0 && kvp.Value != null && kvp.Value.Count > 0);
            }
        }

        public bool HasAlternativePaths =>
            PathTexts.Any(kvp => kvp.Key != 0 && kvp.Value != null && kvp.Value.Count > 0);

        public int NonEmptyPathsCount =>
            PathTexts.Count(kvp => kvp.Value != null && kvp.Value.Count > 0);

        #endregion

        #region Методы для работы с направлениями

        public List<Direction> GetDirectionsWithMessages()
        {
            var directions = new List<Direction>();
            for (int i = 0; i < 4; i++)
            {
                int mask = 0x3 << (i * 2);
                if ((DirectionByte & mask) == mask)
                {
                    directions.Add(i switch
                    {
                        0 => Direction.Bottom,
                        1 => Direction.Left,
                        2 => Direction.Right,
                        3 => Direction.Top,
                        _ => Direction.Top
                    });
                }
            }
            return directions;
        }

        public bool HasMessageInDirection(Direction direction)
        {
            int bitPosition = direction switch
            {
                Direction.Bottom => 0,
                Direction.Left => 2,
                Direction.Right => 4,
                Direction.Top => 6,
                _ => 0
            };
            return (DirectionByte & (0x3 << bitPosition)) == (0x3 << bitPosition);
        }

        #endregion

        #region Методы для работы с множественными монстрами

        public void AddBattleMonster(int index, byte val1, byte val2, bool isIndeterminate = false)
        {
            if (!BattleMonsters.Any(m => m.Index == index &&
                                         m.MonsterIndex1 == val1 &&
                                         m.MonsterIndex2 == val2))
            {
                BattleMonsters.Add(new BattleMonster
                {
                    Index = index,
                    MonsterIndex1 = val1,
                    MonsterIndex2 = val2,
                    IsIndeterminate = isIndeterminate
                });
            }
        }

        public List<MonsterGroupInfo> GetGroupedBattleMonsters()
        {
            return BattleMonsters
                .GroupBy(m => m.MonsterId)
                .Select(g => new MonsterGroupInfo
                {
                    MonsterId = g.Key,
                    MonsterName = g.First().MonsterName,
                    Count = g.Count(),
                    Indices = g.Select(m => m.Index).OrderBy(i => i).ToList(),
                    IsIndeterminate = g.Any(m => m.IsIndeterminate)
                })
                .OrderBy(g => g.MonsterId)
                .ToList();
        }

        private string CleanMonsterName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return new string(name.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
        }

        public string GetBattleDescription()
        {
            if (BattleMonsters.Count > 0)
            {
                var grouped = GetGroupedBattleMonsters();
                grouped = grouped.OrderBy(g => g.Indices.Min()).ToList();

                if (grouped.Count == 1)
                {
                    var g = grouped[0];
                    string cleanName = CleanMonsterName(g.MonsterName);

                    if (g.IsIndeterminate)
                        return $"Битва: {cleanName} x? (Random count)";
                    else if (g.Count == 1)
                        return $"Битва: {cleanName}";
                    else
                        return $"Битва: {cleanName} x{g.Count}";
                }
                else
                {
                    var result = "Битва с группой монстров:\n";
                    foreach (var g in grouped)
                    {
                        string cleanName = CleanMonsterName(g.MonsterName);
                        result += g.IsIndeterminate
                            ? $"  • {cleanName} x? (Random count)\n"
                            : $"  • {cleanName} x{g.Count}\n";
                    }
                    return result.TrimEnd('\n');
                }
            }
            return null;
        }

        public string GetMonsterPowerDescription(byte defaultPower)
        {
            if (!MonsterPower.HasValue) return null;
            byte newPower = MonsterPower.Value;
            if (newPower > defaultPower) return $"Сила монстров увеличивается с {defaultPower} до {newPower}";
            if (newPower < defaultPower) return $"Сила монстров уменьшается с {defaultPower} до {newPower}";
            return $"Сила монстров остаётся прежней: {newPower}";
        }

        public string GetMonsterLevelDescription(byte defaultLevel)
        {
            if (!MonsterLevel.HasValue) return null;
            byte newLevel = MonsterLevel.Value;
            if (newLevel > defaultLevel) return $"Уровень монстров увеличивается с {defaultLevel} до {newLevel}";
            if (newLevel < defaultLevel) return $"Уровень монстров уменьшается с {defaultLevel} до {newLevel}";
            return $"Уровень монстров остаётся прежним: {newLevel}";
        }

        public string GetFullMonsterDescription(byte defaultPower, byte defaultLevel)
        {
            var descriptions = new List<string>();
            var powerDesc = GetMonsterPowerDescription(defaultPower);
            if (powerDesc != null) descriptions.Add(powerDesc);
            var levelDesc = GetMonsterLevelDescription(defaultLevel);
            if (levelDesc != null) descriptions.Add(levelDesc);
            var battleDesc = GetBattleDescription();
            if (battleDesc != null) descriptions.Add(battleDesc);
            return descriptions.Count > 0 ? string.Join("\n", descriptions) : null;
        }

        public bool HasAnyInfo =>
            (PathTexts != null && PathTexts.Any(kvp => kvp.Value != null && kvp.Value.Count > 0)) ||
            HasMonsterStatChanges ||
            HasBattleInfo;

        public override string ToString() =>
            $"OvrObject [X={X}, Y={Y}, Dir=0x{DirectionByte:X2}, Paths={NonEmptyPathsCount}, " +
            $"Power={(MonsterPower.HasValue ? MonsterPower.Value.ToString() : "none")}, " +
            $"Level={(MonsterLevel.HasValue ? MonsterLevel.Value.ToString() : "none")}, " +
            $"BattleMonsters={BattleMonsters.Count}]";

        #endregion
    }

    public class BattleMonster
    {
        public int Index { get; set; }
        public byte MonsterIndex1 { get; set; }
        public byte MonsterIndex2 { get; set; }
        public int MonsterId => MonsterIndex1 + 16 * MonsterIndex2 - 17;
        public string MonsterName => MonsterDatabase.GetMonsterName(MonsterId);
        public bool IsIndeterminate { get; set; }
        public bool IsValid => MonsterIndex1 != 0 || MonsterIndex2 != 0;

        public override string ToString()
        {
            string cleanName = new string(MonsterName.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
            return $"{cleanName} (BX: {Index})";
        }
    }

    public class MonsterGroupInfo
    {
        public int MonsterId { get; set; }
        public string MonsterName { get; set; }
        public int Count { get; set; }
        public bool IsIndeterminate { get; set; }
        public List<int> Indices { get; set; } = new List<int>();

        public override string ToString()
        {
            string cleanName = new string(MonsterName.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
            return IsIndeterminate ? $"{cleanName} x?" : $"{cleanName} x{Count}";
        }
    }
}