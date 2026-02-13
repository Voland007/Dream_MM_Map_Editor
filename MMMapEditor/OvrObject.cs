using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MMMapEditor
{
    /// <summary>
    /// Представляет объект из OVR файла, содержащий информацию о координатах,
    /// направлениях сообщений, текстах и характеристиках монстров
    /// </summary>
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

        #region Информация о битве (устаревшее)

        [Obsolete("Используйте BattleMonsters")]
        public byte? MonsterIndex1 { get; set; }

        [Obsolete("Используйте BattleMonsters")]
        public byte? MonsterIndex2 { get; set; }

        #endregion

        #region Информация о битве (новое)

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public bool HasBattleInfo => BattleMonsters.Count > 0 || MonsterIndex1.HasValue || MonsterIndex2.HasValue;
        public bool HasMultipleMonsters => BattleMonsters.Count > 1;
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

        public bool HasAlternativePaths
        {
            get
            {
                return PathTexts.Any(kvp => kvp.Key != 0 && kvp.Value != null && kvp.Value.Count > 0);
            }
        }

        public int NonEmptyPathsCount
        {
            get
            {
                return PathTexts.Count(kvp => kvp.Value != null && kvp.Value.Count > 0);
            }
        }

        #endregion

        #region Методы для работы с направлениями

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

            int mask = 0x3 << bitPosition;
            return (DirectionByte & mask) == mask;
        }

        #endregion

        #region Методы для работы с индексами монстров (устаревшие)

        [Obsolete("Используйте BattleMonsters")]
        public int? GetMonsterIndex()
        {
            if (MonsterIndex1.HasValue && MonsterIndex2.HasValue)
            {
                long result = MonsterIndex1.Value + 16L * MonsterIndex2.Value - 17;
                if (result >= 0 && result <= int.MaxValue)
                    return (int)result;
            }
            return null;
        }

        [Obsolete("Используйте BattleMonsters")]
        public MonsterInfo GetBattleMonsterInfo()
        {
            int? index = GetMonsterIndex();
            if (index.HasValue)
                return MonsterDatabase.GetMonsterByIndex(index.Value);
            return null;
        }

        #endregion

        #region Методы для работы с множественными монстрами

        /// <summary>
        /// Добавляет монстра в список битвы
        /// </summary>
        /// <param name="index">Позиция BX</param>
        /// <param name="val1">Значение из [0x3C58+BX]</param>
        /// <param name="val2">Значение из [0x3C29+BX]</param>
        /// <param name="isIndeterminate">True, если точное количество неизвестно (цикл с рантайм-границей)</param>
        public void AddBattleMonster(int index, byte val1, byte val2, bool isIndeterminate = false)
        {
            // Проверяем, не существует ли уже такой записи
            bool exists = BattleMonsters.Any(m =>
                m.Index == index &&
                m.MonsterIndex1 == val1 &&
                m.MonsterIndex2 == val2);

            if (!exists)
            {
                BattleMonsters.Add(new BattleMonster
                {
                    Index = index,
                    MonsterIndex1 = val1,
                    MonsterIndex2 = val2,
                    IsIndeterminate = isIndeterminate
                });
            }
            else
            {
                // Если запись уже существует, но флаг indeterminate не установлен,
                // а мы пытаемся добавить с флагом true - обновляем флаг
                if (isIndeterminate)
                {
                    var existing = BattleMonsters.First(m =>
                        m.Index == index &&
                        m.MonsterIndex1 == val1 &&
                        m.MonsterIndex2 == val2);
                    existing.IsIndeterminate = true;
                }
            }
        }

        /// <summary>
        /// Помечает ВСЕХ монстров в этой клетке как indeterminate
        /// (используется, когда обнаружен цикл с неизвестной границей)
        /// </summary>
        public void SetAllMonstersIndeterminate()
        {
            foreach (var monster in BattleMonsters)
            {
                monster.IsIndeterminate = true;
            }
        }

        /// <summary>
        /// Помечает монстров с указанным ID как indeterminate
        /// </summary>
        public void SetMonstersIndeterminate(int monsterId)
        {
            foreach (var monster in BattleMonsters.Where(m => m.MonsterId == monsterId))
            {
                monster.IsIndeterminate = true;
            }
        }

        /// <summary>
        /// Помечает монстров с указанными индексами BX как indeterminate
        /// </summary>
        public void SetMonstersIndeterminateByIndices(List<int> indices)
        {
            foreach (var monster in BattleMonsters.Where(m => indices.Contains(m.Index)))
            {
                monster.IsIndeterminate = true;
            }
        }

        // ============ МЕТОД GetGroupedBattleMonsters ============
        /// <summary>
        /// Получает сгруппированный список монстров для отображения
        /// </summary>
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
                    // ВАЖНО: Группа помечается как indeterminate, если ХОТЯ БЫ ОДИН монстр в группе
                    // был записан в цикле с НЕИЗВЕСТНОЙ границей (IsIndeterminate = true)
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

        /// <summary>
        /// Форматированное описание битвы
        /// </summary>
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
                    {
                        return $"Битва: {cleanName} x? (Random count)";
                    }
                    else if (g.Count == 1)
                    {
                        return $"Битва: {cleanName}";
                    }
                    else
                    {
                        return $"Битва: {cleanName} x{g.Count}";
                    }
                }
                else
                {
                    var result = "Битва с группой монстров:\n";
                    foreach (var g in grouped)
                    {
                        string cleanName = CleanMonsterName(g.MonsterName);

                        if (g.IsIndeterminate)
                        {
                            result += $"  • {cleanName} x? (Random count)\n";
                        }
                        else
                        {
                            result += $"  • {cleanName} x{g.Count}\n";
                        }
                    }
                    return result.TrimEnd('\n');
                }
            }

#pragma warning disable CS0618
            int? oldIndex = GetMonsterIndex();
            if (oldIndex.HasValue)
            {
                var monster = MonsterDatabase.GetMonsterByIndex(oldIndex.Value);
                if (monster != null)
                {
                    string cleanName = CleanMonsterName(monster.Name);
                    return $"Битва: {cleanName}";
                }
                return $"Битва с монстром ID: {oldIndex.Value}";
            }
#pragma warning restore CS0618

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

        public bool HasAnyInfo
        {
            get
            {
                return (PathTexts != null && PathTexts.Any(kvp => kvp.Value != null && kvp.Value.Count > 0)) ||
                       HasMonsterStatChanges ||
                       HasBattleInfo;
            }
        }

        public override string ToString()
        {
            return $"OvrObject [X={X}, Y={Y}, Dir=0x{DirectionByte:X2}, " +
                   $"Paths={NonEmptyPathsCount}, " +
                   $"Power={(MonsterPower.HasValue ? MonsterPower.Value.ToString() : "none")}, " +
                   $"Level={(MonsterLevel.HasValue ? MonsterLevel.Value.ToString() : "none")}, " +
                   $"BattleMonsters={BattleMonsters.Count}]";
        }

        #endregion
    }

    public class BattleMonster
    {
        public int Index { get; set; }
        public byte MonsterIndex1 { get; set; }
        public byte MonsterIndex2 { get; set; }
        public int MonsterId => MonsterIndex1 + 16 * MonsterIndex2 - 17;
        public string MonsterName => MonsterDatabase.GetMonsterName(MonsterId);

        /// <summary>
        /// True, если точное количество монстров этого типа в клетке неизвестно
        /// (определяется во время выполнения игры)
        /// </summary>
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