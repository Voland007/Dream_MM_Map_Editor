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


﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MMMapEditor
{
    public class OvrObject
    {
        #region Основные свойства

        public byte X { get; set; }
        public byte Y { get; set; }
        public byte DirectionByte { get; set; }
        public Dictionary<int, HashSet<string>> PathTexts { get; set; } = new Dictionary<int, HashSet<string>>();

        // НОВОЕ: упорядоченные версии путей для отображения
        public Dictionary<int, List<string>> PathTextsOrdered { get; set; } = new Dictionary<int, List<string>>();

        // Финальные leaf-варианты пути с привязанными branch-specific данными
        public Dictionary<int, PathVariantInfo> PathVariants { get; set; } = new Dictionary<int, PathVariantInfo>();

        public bool IsFromTable { get; set; } = false;

        #endregion

        #region Характеристики монстров (статистики)

        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }
        public byte? MonsterBatchCount { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public bool CallsRandomEncounter { get; set; } = false;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue;

        #endregion

        #region Информация о битве (полностью определённые)

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; } = false;
        public bool HasBattleInfo => BattleMonsters.Count > 0;
        public bool HasMonsterStatChanges => MonsterPower.HasValue || MonsterLevel.HasValue || MonsterBatchCount.HasValue || DarkeningLevel.HasValue || RandomEncounterChance.HasValue || CallsRandomEncounter;

        #endregion

        #region Информация о частично определённых битвах

        public List<PartiallyDefinedBattle> PartiallyDefinedBattles { get; set; } = new List<PartiallyDefinedBattle>();
        public bool HasPartiallyDefinedBattles => PartiallyDefinedBattles.Count > 0;

        #endregion

        #region Информация о загрузке из таблиц (неполные паттерны)

        /// <summary>
        /// Флаг наличия загрузки из таблиц (даже если не найдена полная пара)
        /// </summary>
        public bool HasAnyTableLoad { get; set; } = false;

        /// <summary>
        /// Информация о загруженных значениях из таблиц
        /// </summary>
        public class LoadedValueInfo
        {
            public int BxIndex { get; set; }
            public string RegName { get; set; }
            public byte Value { get; set; }
            public ushort SourceAddr { get; set; }
            public bool IsFirstTable { get; set; } // true для CDA9+, false для CDB1+
            public bool IsSaved { get; set; } // true если значение было сохранено

            public override string ToString()
            {
                string tableName = IsFirstTable ? "CDA9+" : "CDB1+";
                string savedStatus = IsSaved ? "сохранено" : "загружено";
                return $"{RegName} = 0x{Value:X2} из [{SourceAddr:X4}] ({tableName}, {savedStatus})";
            }
        }

        private List<LoadedValueInfo> _loadedValues = new List<LoadedValueInfo>();
        public IReadOnlyList<LoadedValueInfo> LoadedValues => _loadedValues.AsReadOnly();

        /// <summary>
        /// Добавляет информацию о загруженном значении из таблицы
        /// </summary>
        public void AddLoadedValue(int bxIndex, string regName, byte value, ushort sourceAddr, bool isFirstTable, bool isSaved = false)
        {
            // Проверяем, не добавляли ли уже такое значение
            if (!_loadedValues.Any(v => v.BxIndex == bxIndex &&
                                        v.RegName == regName &&
                                        v.SourceAddr == sourceAddr))
            {
                _loadedValues.Add(new LoadedValueInfo
                {
                    BxIndex = bxIndex,
                    RegName = regName,
                    Value = value,
                    SourceAddr = sourceAddr,
                    IsFirstTable = isFirstTable,
                    IsSaved = isSaved
                });
                HasAnyTableLoad = true;
            }
        }

        /// <summary>
        /// Получает список загруженных значений, сгруппированных по BX индексу
        /// </summary>
        public Dictionary<int, List<LoadedValueInfo>> GetGroupedLoadedValues()
        {
            return _loadedValues
                .GroupBy(v => v.BxIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.IsFirstTable).ToList());
        }

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
                        0 => Direction.Left,
                        1 => Direction.Bottom,
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

        #region Методы для работы с множественными монстрами (полностью определённые)

        public void AddBattleMonster(int index, byte val1, byte val2, bool isIndeterminate = false)
        {
            if (val1 == 0 && val2 == 0)
                return;

            var existing = BattleMonsters.FirstOrDefault(m => m.Index == index);

            if (existing == null)
            {
                BattleMonsters.Add(new BattleMonster
                {
                    Index = index,
                    MonsterIndex1 = val1,
                    MonsterIndex2 = val2,
                    IsIndeterminate = isIndeterminate
                });
                return;
            }

            // Полный дубликат: только улучшаем определённость.
            if (existing.MonsterIndex1 == val1 && existing.MonsterIndex2 == val2)
            {
                if (existing.IsIndeterminate && !isIndeterminate)
                    existing.IsIndeterminate = false;
                return;
            }

            // Определённая запись должна заменять неопределённую.
            if (existing.IsIndeterminate && !isIndeterminate)
            {
                existing.MonsterIndex1 = val1;
                existing.MonsterIndex2 = val2;
                existing.IsIndeterminate = false;
                return;
            }

            // Неопределённая запись не должна портить уже найденную определённую.
            if (!existing.IsIndeterminate && isIndeterminate)
                return;

            // В остальных случаях оставляем последнюю запись для этого BX.
            existing.MonsterIndex1 = val1;
            existing.MonsterIndex2 = val2;
            existing.IsIndeterminate = isIndeterminate;
        }

        public List<MonsterGroupInfo> GetGroupedBattleMonsters()
        {
            var normalized = BattleMonsters
                .Where(m => m.IsValid)
                .GroupBy(m => m.Index)
                .Select(g =>
                    g.OrderBy(m => m.IsIndeterminate ? 1 : 0)
                     .ThenByDescending(m => m.MonsterIndex1)
                     .ThenByDescending(m => m.MonsterIndex2)
                     .First())
                .ToList();

            return normalized
                .GroupBy(m => m.MonsterId)
                .Select(g => new MonsterGroupInfo
                {
                    MonsterId = g.Key,
                    MonsterName = g.First().MonsterName,
                    Count = g.Count(),
                    FixedCount = g.Count(m => !m.IsIndeterminate),
                    HasRandomPart = g.Any(m => m.IsIndeterminate),
                    Indices = g.Select(m => m.Index).OrderBy(i => i).ToList(),
                    IsIndeterminate = g.All(m => m.IsIndeterminate)
                })
                .OrderBy(g => g.Indices.Min())
                .ThenBy(g => g.MonsterId)
                .ToList();
        }

        #endregion

        #region Методы для работы с частично определёнными битвами

        public void AddPartiallyDefinedBattle(int bxIndex, byte rangeStart1, byte rangeEnd1, byte rangeStart2, byte rangeEnd2)
        {
            // Проверяем, не существует ли уже такая запись
            if (!PartiallyDefinedBattles.Any(p => p.BxIndex == bxIndex &&
                                                   p.RangeStart1 == rangeStart1 &&
                                                   p.RangeEnd1 == rangeEnd1 &&
                                                   p.RangeStart2 == rangeStart2 &&
                                                   p.RangeEnd2 == rangeEnd2))
            {
                PartiallyDefinedBattles.Add(new PartiallyDefinedBattle
                {
                    BxIndex = bxIndex,
                    RangeStart1 = rangeStart1,
                    RangeEnd1 = rangeEnd1,
                    RangeStart2 = rangeStart2,
                    RangeEnd2 = rangeEnd2
                });
            }
        }

        public List<PartiallyDefinedBattle> GetPartiallyDefinedBattles()
        {
            return PartiallyDefinedBattles.OrderBy(p => p.BxIndex).ToList();
        }

        #endregion

        #region Методы для получения описаний

        private string CleanMonsterName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return new string(name.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
        }

        // Вспомогательный метод для DistinctBy
        private IEnumerable<T> DistinctBy<T, TKey>(IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            HashSet<TKey> seenKeys = new HashSet<TKey>();
            foreach (T element in source)
            {
                if (seenKeys.Add(keySelector(element)))
                {
                    yield return element;
                }
            }
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

        public string GetMonsterBatchCountDescription(byte defaultBatchCount)
        {
            if (!MonsterBatchCount.HasValue) return null;
            byte newBatchCount = MonsterBatchCount.Value;
            if (newBatchCount > defaultBatchCount) return $"Количество монстров в группе увеличивается с {defaultBatchCount} до {newBatchCount}";
            if (newBatchCount < defaultBatchCount) return $"Количество монстров в группе уменьшается с {defaultBatchCount} до {newBatchCount}";
            return $"Количество монстров в группе остаётся прежним: {newBatchCount}";
        }

        public string GetDarkeningLevelDescription(byte defaultDarkeningLevel)
        {
            if (!DarkeningLevel.HasValue) return null;
            byte newDarkeningLevel = DarkeningLevel.Value;
            if (newDarkeningLevel > defaultDarkeningLevel) return $"Уровень затемнённости увеличивается с {defaultDarkeningLevel} до {newDarkeningLevel}";
            if (newDarkeningLevel < defaultDarkeningLevel) return $"Уровень затемнённости уменьшается с {defaultDarkeningLevel} до {newDarkeningLevel}";
            return $"Уровень затемнённости остаётся прежним: {newDarkeningLevel}";
        }

        public string GetRandomEncounterChanceDescription(byte defaultChance)
        {
            if (!RandomEncounterChance.HasValue) return null;

            byte newChance = RandomEncounterChance.Value;
            double defaultPercent = DecodeRandomEncounterChance(defaultChance);
            double newPercent = DecodeRandomEncounterChance(newChance);

            string defaultPercentText = FormatPercent(defaultPercent);
            string newPercentText = FormatPercent(newPercent);

            if (newPercent > defaultPercent)
                return $"Шанс случайной встречи увеличивается с {defaultPercentText} (0x{defaultChance:X2}) до {newPercentText} (0x{newChance:X2})";
            if (newPercent < defaultPercent)
                return $"Шанс случайной встречи уменьшается с {defaultPercentText} (0x{defaultChance:X2}) до {newPercentText} (0x{newChance:X2})";
            return $"Шанс случайной встречи остаётся прежним: {newPercentText} (0x{newChance:X2})";
        }


        public string GetTeleportDescription()
        {
            if (!HasTeleportTarget)
                return null;

            string xPart = TeleportTargetX.HasValue ? $"X={TeleportTargetX.Value}" : "X=?";
            string yPart = TeleportTargetY.HasValue ? $"Y={TeleportTargetY.Value}" : "Y=?";
            return $"Телепорт перед сражением / событием на клетку ({xPart}, {yPart})";
        }

        private static double DecodeRandomEncounterChance(byte value)
        {
            if (value == 0x00 || value == 0xFF)
                return 0;

            return (256 - value) * 100.0 / 256.0;
        }

        private static string FormatPercent(double value)
        {
            return $"{value:0.##}%";
        }

        /// <summary>
        /// Полное описание битвы (все типы)
        /// </summary>
        public string GetBattleDescription()
        {
            var descriptions = new List<string>();

            // Используем HashSet для отслеживания уже добавленных описаний
            var addedDescriptions = new HashSet<string>();

            // ===== ПОЛНОСТЬЮ ОПРЕДЕЛЁННЫЕ БИТВЫ =====
            if (BattleMonsters.Count > 0)
            {
                var grouped = GetGroupedBattleMonsters();
                grouped = grouped.OrderBy(g => g.Indices.Min()).ToList();

                // Общий BattleMonsterCount безопасно использовать только для одной группы.
                // Для нескольких групп он ломает смешанные случаи вида x1+? / x?.
                bool canUseGlobalBattleCount =
                    grouped.Count == 1 &&
                    BattleMonsterCount.HasValue &&
                    !IsBattleMonsterCountIndeterminate;

                bool canUseGlobalBattleCountRange =
                    grouped.Count == 1 &&
                    BattleMonsterCountRange != null &&
                    !BattleMonsterCountRange.IsExact &&
                    !IsBattleMonsterCountIndeterminate;

                var result = "Битва с группой монстров:";
                int totalFixedAcrossGroups = grouped.Sum(x => x.FixedCount > 0 ? x.FixedCount : x.Count);
                bool canUseTailRangeHeuristic =
                    BattleMonsterCountRange != null &&
                    !IsBattleMonsterCountIndeterminate &&
                    grouped.Count > 1 &&
                    !grouped.Any(x => x.HasRandomPart) &&
                    BattleMonsterCountRange.Max > totalFixedAcrossGroups;

                foreach (var g in grouped)
                {
                    string cleanName = CleanMonsterNameForDisplay(g.MonsterName);
                    string countDisplay;

                    if (canUseGlobalBattleCount)
                    {
                        countDisplay = BattleMonsterCount.Value.ToString();
                    }
                    else if (canUseGlobalBattleCountRange)
                    {
                        countDisplay = $"{BattleMonsterCountRange.Min}-{BattleMonsterCountRange.Max}";
                    }
                    else if (g.HasRandomPart)
                    {
                        bool shouldCollapseToPureRandom =
                            IsBattleMonsterCountIndeterminate ||
                            grouped.Any(x => x.IsIndeterminate);

                        if (BattleMonsterCountRange != null && !IsBattleMonsterCountIndeterminate)
                        {
                            int otherFixed = grouped.Where(x => x != g).Sum(x => x.FixedCount > 0 ? x.FixedCount : x.Count);
                            int min = BattleMonsterCountRange.Min - otherFixed;
                            int max = BattleMonsterCountRange.Max - otherFixed;
                            if (min < g.FixedCount) min = g.FixedCount;
                            if (max < min) max = min;
                            countDisplay = min == max ? min.ToString() : $"{min}-{max}";
                        }
                        else if (shouldCollapseToPureRandom)
                        {
                            countDisplay = "? (Random count)";
                        }
                        else
                        {
                            countDisplay = g.FixedCount > 0
                                ? $"{g.FixedCount}+? (Random count)"
                                : "? (Random count)";
                        }
                    }
                    else if (canUseTailRangeHeuristic && g == grouped.Last())
                    {
                        int otherFixed = grouped.Where(x => x != g).Sum(x => x.FixedCount > 0 ? x.FixedCount : x.Count);
                        int min = BattleMonsterCountRange.Min - otherFixed;
                        int max = BattleMonsterCountRange.Max - otherFixed;
                        int baseCount = g.FixedCount > 0 ? g.FixedCount : g.Count;
                        if (min < baseCount) min = baseCount;
                        if (max < min) max = min;
                        countDisplay = min == max ? min.ToString() : $"{min}-{max}";
                    }
                    else
                    {
                        countDisplay = (g.FixedCount > 0 ? g.FixedCount : g.Count).ToString();
                    }

                    result += $"\n  • {cleanName} x{countDisplay}";
                }

                if (!addedDescriptions.Contains(result))
                {
                    descriptions.Add(result);
                    addedDescriptions.Add(result);
                }
            }

            // ===== ЧАСТИЧНО ОПРЕДЕЛЁННЫЕ БИТВЫ =====
            if (PartiallyDefinedBattles.Count > 0)
            {
                // Группируем частичные битвы по диапазону BX индексов
                var firstBattle = PartiallyDefinedBattles[0];

                // Проверяем, являются ли все битвы частью одного цикла
                bool isSequential = true;
                int minBx = PartiallyDefinedBattles.Min(b => b.BxIndex);
                int maxBx = PartiallyDefinedBattles.Max(b => b.BxIndex);

                for (int i = minBx; i <= maxBx; i++)
                {
                    if (!PartiallyDefinedBattles.Any(b => b.BxIndex == i))
                    {
                        isSequential = false;
                        break;
                    }
                }

                if (isSequential && PartiallyDefinedBattles.Count > 1)
                {
                    // Это последовательность из цикла - объединяем в одну запись
                    var monsters = new List<PossibleMonster>();

                    // Собираем всех монстров из всех частичных битв
                    foreach (var battle in PartiallyDefinedBattles.OrderBy(b => b.BxIndex))
                    {
                        monsters.AddRange(battle.GetPossibleMonsters());
                    }

                    if (monsters.Count > 0)
                    {
                        string result;

                        if (monsters.Count == 1)
                        {
                            string cleanName = CleanMonsterNameForDisplay(monsters[0].MonsterName);
                            result = $"Частично определённая битва: {cleanName}";
                        }
                        else
                        {
                            result = $"Частично определённая битва. {monsters.Count} вариант(ов):";

                            // Показываем все варианты (их обычно 8)
                            for (int i = 0; i < monsters.Count; i++)
                            {
                                string cleanName = CleanMonsterNameForDisplay(monsters[i].MonsterName);
                                result += $"\n  • Вариант {i + 1}: {cleanName}";
                            }
                        }

                        if (!addedDescriptions.Contains(result))
                        {
                            descriptions.Add(result);
                            addedDescriptions.Add(result);
                        }
                    }
                }
                else
                {
                    // Обычная обработка - каждая частичная битва отдельно
                    foreach (var battle in PartiallyDefinedBattles.OrderBy(b => b.BxIndex))
                    {
                        string battleKey = $"Partial_{battle.BxIndex}";
                        if (addedDescriptions.Contains(battleKey))
                            continue;

                        addedDescriptions.Add(battleKey);

                        var monsters = battle.GetPossibleMonsters();

                        if (monsters.Count == 1)
                        {
                            string cleanName = CleanMonsterNameForDisplay(monsters[0].MonsterName);
                            string desc = $"Частично определённая битва: {cleanName}";

                            if (!addedDescriptions.Contains(desc))
                            {
                                descriptions.Add(desc);
                                addedDescriptions.Add(desc);
                            }
                        }
                        else if (monsters.Count > 0)
                        {
                            string result = $"Частично определённая битва (BX={battle.BxIndex}, {monsters.Count} вариантов):";

                            int displayCount = Math.Min(monsters.Count, 10);
                            for (int i = 0; i < displayCount; i++)
                            {
                                string cleanName = CleanMonsterNameForDisplay(monsters[i].MonsterName);
                                result += $"\n  • Вариант {i + 1}: {cleanName}";
                            }

                            if (monsters.Count > displayCount)
                            {
                                result += $"\n  • ... и ещё {monsters.Count - displayCount} вариантов";
                            }

                            if (!addedDescriptions.Contains(result))
                            {
                                descriptions.Add(result);
                                addedDescriptions.Add(result);
                            }
                        }
                        else
                        {
                            string desc = $"Частично определённая битва (BX={battle.BxIndex}, диапазоны: [{battle.RangeStart1:X2}-{battle.RangeEnd1:X2}] + [{battle.RangeStart2:X2}-{battle.RangeEnd2:X2}])";
                            if (!addedDescriptions.Contains(desc))
                            {
                                descriptions.Add(desc);
                                addedDescriptions.Add(desc);
                            }
                        }
                    }
                }
            }

            // ===== ИНФОРМАЦИЯ О ЗАГРУЗКЕ ИЗ ТАБЛИЦ =====
            if (HasAnyTableLoad && _loadedValues.Count > 0 && PartiallyDefinedBattles.Count == 0)
            {
                var grouped = GetGroupedLoadedValues();

                foreach (var group in grouped.OrderBy(g => g.Key))
                {
                    int bxIndex = group.Key;
                    var values = group.Value;

                    string loadKey = $"Load_{bxIndex}";
                    if (addedDescriptions.Contains(loadKey))
                        continue;

                    addedDescriptions.Add(loadKey);

                    bool hasFirstTable = values.Any(v => v.IsFirstTable);
                    bool hasSecondTable = values.Any(v => !v.IsFirstTable);

                    string desc = "";

                    if (hasFirstTable && !hasSecondTable)
                    {
                        desc = $"Неполная загрузка из таблиц (BX={bxIndex}):\n";
                        desc += $"  • Загружено из CDA9+ → сохранено в 3C58+\n";
                        desc += $"  • Загрузка из CDB1+ не найдена\n";

                        foreach (var val in DistinctBy(values.Where(v => v.IsFirstTable), v => v.SourceAddr))
                        {
                            string status = val.IsSaved ? "сохранено" : "загружено (не сохранено)";
                            desc += $"    {val.RegName} = 0x{val.Value:X2} из [{val.SourceAddr:X4}] ({status})\n";
                        }
                    }
                    else if (!hasFirstTable && hasSecondTable)
                    {
                        desc = $"Неполная загрузка из таблиц (BX={bxIndex}):\n";
                        desc += $"  • Загружено из CDB1+ → сохранено в 3C29+\n";
                        desc += $"  • Загрузка из CDA9+ не найдена\n";

                        foreach (var val in DistinctBy(values.Where(v => !v.IsFirstTable), v => v.SourceAddr))
                        {
                            string status = val.IsSaved ? "сохранено" : "загружено (не сохранено)";
                            desc += $"    {val.RegName} = 0x{val.Value:X2} из [{val.SourceAddr:X4}] ({status})\n";
                        }
                    }
                    else if (hasFirstTable && hasSecondTable)
                    {
                        desc = $"Неполная загрузка из обеих таблиц (BX={bxIndex}):\n";

                        var firstVals = DistinctBy(values.Where(v => v.IsFirstTable), v => v.SourceAddr).ToList();
                        var secondVals = DistinctBy(values.Where(v => !v.IsFirstTable), v => v.SourceAddr).ToList();

                        foreach (var val in firstVals)
                        {
                            string status = val.IsSaved ? "сохранено" : "не сохранено";
                            desc += $"  • {val.RegName} = 0x{val.Value:X2} из [{val.SourceAddr:X4}] ({status})\n";
                        }

                        foreach (var val in secondVals)
                        {
                            string status = val.IsSaved ? "сохранено" : "не сохранено";
                            desc += $"  • {val.RegName} = 0x{val.Value:X2} из [{val.SourceAddr:X4}] ({status})\n";
                        }
                    }

                    if (!string.IsNullOrEmpty(desc) && !addedDescriptions.Contains(desc))
                    {
                        descriptions.Add(desc.TrimEnd('\n'));
                        addedDescriptions.Add(desc);
                    }
                }
            }

            return descriptions.Count > 0 ? string.Join("\n", descriptions) : null;
        }

        // Вспомогательный метод для очистки имени монстра при отображении
        private string CleanMonsterNameForDisplay(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Оставляем только английские буквы, пробелы и базовые знаки препинания
            var clean = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    c == ' ' || c == '-' || c == '\'' || c == '.')
                {
                    clean.Append(c);
                }
            }

            // Убираем лишние пробелы
            string result = clean.ToString().Trim();
            result = Regex.Replace(result, @"\s+", " ");

            return result;
        }

        public bool HasAnyInfo =>
            (PathTexts != null && PathTexts.Any(kvp => kvp.Value != null && kvp.Value.Count > 0)) ||
            HasMonsterStatChanges ||
            HasBattleInfo ||
            HasPartiallyDefinedBattles ||
            HasAnyTableLoad ||
            CallsRandomEncounter;

        #endregion

        public override string ToString()
        {
            var parts = new List<string>
            {
                $"OvrObject [X={X}, Y={Y}, Dir=0x{DirectionByte:X2}, Paths={NonEmptyPathsCount}"
            };

            if (MonsterPower.HasValue)
                parts.Add($"Power={MonsterPower.Value}");
            else
                parts.Add("Power=none");

            if (MonsterLevel.HasValue)
                parts.Add($"Level={MonsterLevel.Value}");
            else
                parts.Add("Level=none");

            if (MonsterBatchCount.HasValue)
                parts.Add($"BatchCount={MonsterBatchCount.Value}");
            else
                parts.Add("BatchCount=none");

            if (RandomEncounterChance.HasValue)
                parts.Add($"EncounterChance={RandomEncounterChance.Value}");
            else
                parts.Add("EncounterChance=none");

            parts.Add($"BattleMonsters={BattleMonsters.Count}");
            parts.Add($"PartiallyDefined={PartiallyDefinedBattles.Count}");
            parts.Add($"TableLoad={HasAnyTableLoad}");

            return string.Join(", ", parts) + "]";
        }
    }

    public class PathVariantInfo
    {
        public int PathId { get; set; }
        public bool IsLeaf { get; set; }

        public List<string> Texts { get; set; } = new List<string>();

        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }
        public byte? MonsterBatchCount { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public bool CallsRandomEncounter { get; set; } = false;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue;

        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; }

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public List<PartiallyDefinedBattle> PartiallyDefinedBattles { get; set; } = new List<PartiallyDefinedBattle>();

        public bool HasAnyTableLoad { get; set; }
        public List<OvrObject.LoadedValueInfo> LoadedValues { get; set; } = new List<OvrObject.LoadedValueInfo>();

        public int ProbabilityNumerator { get; set; } = 1;
        public int ProbabilityDenominator { get; set; } = 1;

        public bool HasProbabilityInfo => ProbabilityDenominator > 1;

        public OvrObject ToOvrObject(byte x, byte y, byte directionByte)
        {
            var obj = new OvrObject
            {
                X = x,
                Y = y,
                DirectionByte = directionByte,
                PathTexts = new Dictionary<int, HashSet<string>>(),
                PathTextsOrdered = new Dictionary<int, List<string>>(),
                PathVariants = new Dictionary<int, PathVariantInfo>(),
                MonsterPower = MonsterPower,
                MonsterLevel = MonsterLevel,
                MonsterBatchCount = MonsterBatchCount,
                DarkeningLevel = DarkeningLevel,
                RandomEncounterChance = RandomEncounterChance,
                CallsRandomEncounter = CallsRandomEncounter,
                TeleportTargetX = TeleportTargetX,
                TeleportTargetY = TeleportTargetY,
                BattleMonsterCount = BattleMonsterCount,
                BattleMonsterCountRange = BattleMonsterCountRange == null ? null : new ValueRange8(BattleMonsterCountRange.Min, BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = IsBattleMonsterCountIndeterminate,
                IsFromTable = true,
                HasAnyTableLoad = HasAnyTableLoad
            };

            // Вероятность варианта хранится в PathVariantInfo и используется построителем заметок.

            if (Texts != null && Texts.Count > 0)
            {
                obj.PathTexts[0] = new HashSet<string>(Texts);
                obj.PathTextsOrdered[0] = new List<string>(Texts);
            }

            obj.PathVariants[0] = this;

            if (BattleMonsters != null)
            {
                foreach (var battleMonster in BattleMonsters.OrderBy(m => m.Index))
                {
                    obj.AddBattleMonster(
                        battleMonster.Index,
                        battleMonster.MonsterIndex1,
                        battleMonster.MonsterIndex2,
                        battleMonster.IsIndeterminate);
                }
            }

            if (PartiallyDefinedBattles != null)
            {
                foreach (var partial in PartiallyDefinedBattles.OrderBy(p => p.BxIndex))
                {
                    obj.AddPartiallyDefinedBattle(
                        partial.BxIndex,
                        partial.RangeStart1,
                        partial.RangeEnd1,
                        partial.RangeStart2,
                        partial.RangeEnd2);
                }
            }

            if (LoadedValues != null)
            {
                foreach (var loadedValue in LoadedValues
                    .OrderBy(v => v.BxIndex)
                    .ThenBy(v => v.SourceAddr)
                    .ThenBy(v => v.RegName))
                {
                    obj.AddLoadedValue(
                        loadedValue.BxIndex,
                        loadedValue.RegName,
                        loadedValue.Value,
                        loadedValue.SourceAddr,
                        loadedValue.IsFirstTable,
                        loadedValue.IsSaved);
                }
            }

            return obj;
        }

        public bool HasMonsterStatChanges =>
            MonsterPower.HasValue ||
            MonsterLevel.HasValue ||
            MonsterBatchCount.HasValue ||
            DarkeningLevel.HasValue ||
            RandomEncounterChance.HasValue ||
            CallsRandomEncounter;

        public bool HasBattleInfo => BattleMonsters.Count > 0;
        public bool HasPartiallyDefinedBattles => PartiallyDefinedBattles.Count > 0;
        public bool HasAnyInfo =>
            (Texts != null && Texts.Any(t => !string.IsNullOrWhiteSpace(t))) ||
            HasMonsterStatChanges ||
            HasBattleInfo ||
            HasPartiallyDefinedBattles ||
            HasAnyTableLoad ||
            CallsRandomEncounter ||
            HasProbabilityInfo;

        public string GetProbabilityDescription()
        {
            if (!HasProbabilityInfo || ProbabilityDenominator <= 0)
                return null;

            double percent = 100.0 * ProbabilityNumerator / ProbabilityDenominator;
            string percentText = percent % 1.0 == 0.0
                ? percent.ToString("0")
                : percent.ToString("0.##");

            return $"Вероятность: {percentText}% ({ProbabilityNumerator}/{ProbabilityDenominator})";
        }
    }

    #region Классы для полностью определённых битв

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
        public int FixedCount { get; set; }
        public bool HasRandomPart { get; set; }
        public bool IsIndeterminate { get; set; }
        public List<int> Indices { get; set; } = new List<int>();

        public override string ToString()
        {
            string cleanName = new string(MonsterName.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();

            if (HasRandomPart && FixedCount > 0)
                return $"{cleanName} x{FixedCount}+?";
            if (IsIndeterminate)
                return $"{cleanName} x?";
            return $"{cleanName} x{Count}";
        }
    }

    #endregion

    #region Классы для частично определённых битв

    /// <summary>
    /// Представляет частично определённую битву, где значения берутся из диапазонов
    /// CDA9-CDB0 (первый байт) и CDB1-CDB8 (второй байт)
    /// </summary>
    public class PartiallyDefinedBattle
    {
        /// <summary>
        /// Индекс BX (смещение в массивах)
        /// </summary>
        public int BxIndex { get; set; }

        /// <summary>
        /// Начало диапазона для первого байта (обычно CDA9+)
        /// </summary>
        public byte RangeStart1 { get; set; }

        /// <summary>
        /// Конец диапазона для первого байта (обычно CDB0)
        /// </summary>
        public byte RangeEnd1 { get; set; }

        /// <summary>
        /// Начало диапазона для второго байта (обычно CDB1+)
        /// </summary>
        public byte RangeStart2 { get; set; }

        /// <summary>
        /// Конец диапазона для второго байта (обычно CDB8)
        /// </summary>
        public byte RangeEnd2 { get; set; }

        /// <summary>
        /// Генерирует список возможных монстров из диапазона
        /// </summary>
        public List<PossibleMonster> GetPossibleMonsters()
        {
            var result = new List<PossibleMonster>();

            AnalysisDebug.WriteLine($"      GetPossibleMonsters: диапазоны [{RangeStart1:X2}-{RangeEnd1:X2}] + [{RangeStart2:X2}-{RangeEnd2:X2}]");

            for (byte val1 = RangeStart1; val1 <= RangeEnd1; val1++)
            {
                for (byte val2 = RangeStart2; val2 <= RangeEnd2; val2++)
                {
                    // Для частично определённых битв формула может быть другой
                    // Пробуем разные варианты:

                    // Вариант 1: как в полностью определённых битвах
                    int monsterId1 = val1 + 16 * val2 - 17;

                    // Вариант 2: без вычитания 17
                    int monsterId2 = val1 + 16 * val2;

                    // Вариант 3: с другим смещением
                    int monsterId3 = val1 + 16 * val2 - 1;

                    AnalysisDebug.WriteLine($"        val1={val1:X2}, val2={val2:X2} -> ID1={monsterId1}, ID2={monsterId2}, ID3={monsterId3}");

                    // Проверяем все варианты
                    if (monsterId1 >= 0 && monsterId1 < 256)
                    {
                        string monsterName = MonsterDatabase.GetMonsterName(monsterId1);
                        if (!string.IsNullOrEmpty(monsterName))
                        {
                            result.Add(new PossibleMonster
                            {
                                Val1 = val1,
                                Val2 = val2,
                                MonsterId = monsterId1,
                                MonsterName = monsterName
                            });
                            AnalysisDebug.WriteLine($"          Добавлен (вариант 1): {monsterName} (ID={monsterId1})");
                        }
                    }
                    else if (monsterId2 >= 0 && monsterId2 < 256)
                    {
                        string monsterName = MonsterDatabase.GetMonsterName(monsterId2);
                        if (!string.IsNullOrEmpty(monsterName))
                        {
                            result.Add(new PossibleMonster
                            {
                                Val1 = val1,
                                Val2 = val2,
                                MonsterId = monsterId2,
                                MonsterName = monsterName
                            });
                            AnalysisDebug.WriteLine($"          Добавлен (вариант 2): {monsterName} (ID={monsterId2})");
                        }
                    }
                    else if (monsterId3 >= 0 && monsterId3 < 256)
                    {
                        string monsterName = MonsterDatabase.GetMonsterName(monsterId3);
                        if (!string.IsNullOrEmpty(monsterName))
                        {
                            result.Add(new PossibleMonster
                            {
                                Val1 = val1,
                                Val2 = val2,
                                MonsterId = monsterId3,
                                MonsterName = monsterName
                            });
                            AnalysisDebug.WriteLine($"          Добавлен (вариант 3): {monsterName} (ID={monsterId3})");
                        }
                    }
                }
            }

            AnalysisDebug.WriteLine($"      Найдено монстров: {result.Count}");
            return result;
        }

        /// <summary>
        /// Проверяет, является ли диапазон полным (охватывает все возможные значения)
        /// </summary>
        public bool IsFullRange =>
            RangeStart1 == 0 && RangeEnd1 == 255 &&
            RangeStart2 == 0 && RangeEnd2 == 255;

        /// <summary>
        /// Количество возможных комбинаций
        /// </summary>
        public int PossibleCombinations => (RangeEnd1 - RangeStart1 + 1) * (RangeEnd2 - RangeStart2 + 1);

        public override string ToString()
        {
            var monsters = GetPossibleMonsters();
            if (monsters.Count == 1)
                return $"Частично определён: {monsters[0].MonsterName}";
            else if (monsters.Count > 0)
                return $"Частично определён: {monsters.Count} возможных монстров (диапазоны: [{RangeStart1:X2}-{RangeEnd1:X2}] + [{RangeStart2:X2}-{RangeEnd2:X2}])";
            else
                return $"Частично определён: пустой диапазон";
        }
    }

    /// <summary>
    /// Представляет одного возможного монстра из диапазона
    /// </summary>
    public class PossibleMonster
    {
        public byte Val1 { get; set; }
        public byte Val2 { get; set; }
        public int MonsterId { get; set; }
        public string MonsterName { get; set; }

        public override string ToString()
        {
            string cleanName = new string(MonsterName.Where(c =>
                char.IsLetterOrDigit(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c)).ToArray()).Trim();
            return $"{cleanName} (0x{Val1:X2}, 0x{Val2:X2})";
        }
    }

    #endregion
}