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
        public uint? PatchAddress { get; set; }
        public Dictionary<int, HashSet<string>> PathTexts { get; set; } = new Dictionary<int, HashSet<string>>();

        // НОВОЕ: упорядоченные версии путей для отображения
        public Dictionary<int, List<string>> PathTextsOrdered { get; set; } = new Dictionary<int, List<string>>();

        // Финальные leaf-варианты пути с привязанными branch-specific данными
        public Dictionary<int, PathVariantInfo> PathVariants { get; set; } = new Dictionary<int, PathVariantInfo>();

        public bool IsFromTable { get; set; } = false;
        public List<PartyEffect> PartyEffects { get; set; } = new List<PartyEffect>();
        public bool HasPartyEffects => PartyEffects != null && PartyEffects.Count > 0;

        #endregion

        #region Характеристики монстров (статистики)

        public byte? RandomEncounterMonsterPowerCap { get; set; }
        public byte? RandomEncounterMonsterLevelCap { get; set; }
        public byte? RandomEncounterMonsterBatchCountCap { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public byte? RandomEncounterRubicon { get; set; }
        public int BattleMonsterStrengthAdjustment { get; set; } = 0;
        public bool CallsRandomEncounter { get; set; } = false;
        public uint RandomEncounterInstructionAddress { get; set; } = 0;
        public int RandomEncounterExecutionOrder { get; set; } = 0;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public ValueRange8 TeleportTargetXRange { get; set; }
        public ValueRange8 TeleportTargetYRange { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue || TeleportTargetXRange != null || TeleportTargetYRange != null;

        #endregion

        #region Информация о битве (полностью определённые)

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; } = false;
        public List<PersistentCounterProgressionInfo> PersistentCounterProgressions { get; set; }
            = new List<PersistentCounterProgressionInfo>();
        public List<DynamicRandomBoundDependencyInfo> DynamicRandomBoundDependencies { get; set; }
            = new List<DynamicRandomBoundDependencyInfo>();
        public bool HasBattleInfo => BattleMonsters.Count > 0;
        public bool HasMonsterStatChanges => RandomEncounterMonsterPowerCap.HasValue || RandomEncounterMonsterLevelCap.HasValue || RandomEncounterMonsterBatchCountCap.HasValue || DarkeningLevel.HasValue || RandomEncounterChance.HasValue || RandomEncounterRubicon.HasValue || BattleMonsterStrengthAdjustment != 0 || CallsRandomEncounter;

        #endregion

        #region Информация о частично определённых битвах

        public List<PartiallyDefinedBattle> PartiallyDefinedBattles { get; set; } = new List<PartiallyDefinedBattle>();
        public bool HasPartiallyDefinedBattles => PartiallyDefinedBattles.Count > 0;
        public bool HasBattleLikeInfo =>
            HasBattleInfo ||
            HasPartiallyDefinedBattles ||
            (HasAnyTableLoad && LoadedValues.Count > 0);

        public List<string> GetPartyEffectDescriptions()
        {
            var effects = (PartyEffects ?? new List<PartyEffect>())
                .Where(effect => effect != null)
                .ToList();

            return effects
                .Where(effect => PartyEffectSemantics.ShouldIncludeInNotes(effect, effects))
                .Select(PartyEffectSemantics.BuildHumanDescription)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList();
        }

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
            return new List<Direction>(
                DirectionUtilities.GetMessageDirections(DirectionByte, DirectionByteLayout.OvrObject));
        }

        public bool HasMessageInDirection(Direction direction)
        {
            return DirectionUtilities.HasMessageInDirection(
                DirectionByte,
                direction,
                DirectionByteLayout.OvrObject);
        }

        #endregion

        #region Методы для работы с множественными монстрами (полностью определённые)

        public void AddBattleMonster(int index, byte val1, byte val2, bool isIndeterminate = false)
        {
            if (val1 == 0 || val2 == 0)
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
            AddPartiallyDefinedBattle(new PartiallyDefinedBattle
            {
                BxIndex = bxIndex,
                RangeStart1 = rangeStart1,
                RangeEnd1 = rangeEnd1,
                RangeStart2 = rangeStart2,
                RangeEnd2 = rangeEnd2
            });
        }

        public void AddPartiallyDefinedBattle(PartiallyDefinedBattle battle)
        {
            if (battle == null)
                return;

            string battleKey = battle.GetIdentityKey();
            if (PartiallyDefinedBattles.Any(p => p.GetIdentityKey() == battleKey))
                return;

            PartiallyDefinedBattles.Add(battle.Clone());
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

        public string GetRandomEncounterMonsterPowerCapDescription(byte defaultPower)
        {
            if (!RandomEncounterMonsterPowerCap.HasValue) return null;
            byte newPower = RandomEncounterMonsterPowerCap.Value;
            if (newPower > defaultPower) return $"Максимальная сила случайных монстров увеличивается с {defaultPower} до {newPower}";
            if (newPower < defaultPower) return $"Максимальная сила случайных монстров уменьшается с {defaultPower} до {newPower}";
            return $"Максимальная сила случайных монстров остаётся прежней: {newPower}";
        }

        public string GetRandomEncounterMonsterLevelCapDescription(byte defaultLevel)
        {
            if (!RandomEncounterMonsterLevelCap.HasValue) return null;
            byte newLevel = RandomEncounterMonsterLevelCap.Value;
            if (newLevel > defaultLevel) return $"Максимальный уровень случайных монстров увеличивается с {defaultLevel} до {newLevel}";
            if (newLevel < defaultLevel) return $"Максимальный уровень случайных монстров уменьшается с {defaultLevel} до {newLevel}";
            return $"Максимальный уровень случайных монстров остаётся прежним: {newLevel}";
        }

        public string GetRandomEncounterMonsterBatchCountCapDescription(byte defaultBatchCount)
        {
            if (!RandomEncounterMonsterBatchCountCap.HasValue) return null;
            byte newBatchCount = RandomEncounterMonsterBatchCountCap.Value;
            if (newBatchCount > defaultBatchCount) return $"Максимальное количество случайных монстров в группе увеличивается с {defaultBatchCount} до {newBatchCount}";
            if (newBatchCount < defaultBatchCount) return $"Максимальное количество случайных монстров в группе уменьшается с {defaultBatchCount} до {newBatchCount}";
            return $"Максимальное количество случайных монстров в группе остаётся прежним: {newBatchCount}";
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

        public string GetBattleMonsterStrengthAdjustmentDescription()
        {
            if (BattleMonsterStrengthAdjustment > 0)
                return $"Монстры битвы усиливаются на +{BattleMonsterStrengthAdjustment}";

            if (BattleMonsterStrengthAdjustment < 0)
                return $"Монстры битвы слабеют на -{Math.Abs(BattleMonsterStrengthAdjustment)}";

            return null;
        }


        public string GetTeleportDescription()
        {
            if (!HasTeleportTarget)
                return null;

            bool hasRandomX = TeleportTargetXRange != null && !TeleportTargetXRange.IsExact;
            bool hasRandomY = TeleportTargetYRange != null && !TeleportTargetYRange.IsExact;

            string xPart = TeleportTargetX.HasValue
                ? $"X={TeleportTargetX.Value}"
                : TeleportTargetXRange != null
                    ? $"X={TeleportTargetXRange.Min}..{TeleportTargetXRange.Max}"
                    : "X=?";

            string yPart = TeleportTargetY.HasValue
                ? $"Y={TeleportTargetY.Value}"
                : TeleportTargetYRange != null
                    ? $"Y={TeleportTargetYRange.Min}..{TeleportTargetYRange.Max}"
                    : "Y=?";

            if (hasRandomX || hasRandomY)
                return $"Телепорт на случайную клетку ({xPart}, {yPart})";

            return $"Телепорт на клетку ({xPart}, {yPart})";
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

        private string GetPartialBattleCountDisplay(int? fallbackCount = null)
        {
            if (BattleMonsterCount.HasValue && !IsBattleMonsterCountIndeterminate)
                return BattleMonsterCount.Value.ToString();

            if (BattleMonsterCountRange != null && !IsBattleMonsterCountIndeterminate)
            {
                return BattleMonsterCountRange.IsExact
                    ? BattleMonsterCountRange.Min.ToString()
                    : $"{BattleMonsterCountRange.Min}-{BattleMonsterCountRange.Max}";
            }

            if (fallbackCount.HasValue && fallbackCount.Value > 1)
                return fallbackCount.Value.ToString();

            return null;
        }

        private static string GetSharedPartialBattleRepeatCountDisplay(IEnumerable<PartiallyDefinedBattle> battles)
        {
            if (battles == null)
                return null;

            var repeatDisplays = battles
                .Where(battle => battle != null)
                .Select(battle => battle.GetRepeatCountDisplay())
                .Where(display => !string.IsNullOrWhiteSpace(display))
                .Distinct()
                .ToList();

            return repeatDisplays.Count == 1 ? repeatDisplays[0] : null;
        }

        private string GetRandomEncounterRubiconWarningDescription()
        {
            if (!CallsRandomEncounter || !RandomEncounterRubicon.HasValue)
                return null;

            int partyLevelSumThreshold = 2 * (RandomEncounterRubicon.Value + 1);
            return InlineNoteStyleCodec.EncodeRandomEncounterRubiconWarning(partyLevelSumThreshold);
        }

        private List<string> GetPersistentBattleCounterProgressionDescriptions()
        {
            return (PersistentCounterProgressions ?? new List<PersistentCounterProgressionInfo>())
                .Where(info => info != null && info.HasFutureProgression)
                .Select(info =>
                {
                    string incrementText = info.Delta == 1 ? "1" : info.Delta.ToString();
                    string description = $"Количество увеличивается на {incrementText} при каждом следующем наступлении этой битвы, максимум x{info.CapValue.Value}";
                    return InlineNoteStyleCodec.EncodePersistentCounterProgressionText(description);
                })
                .Distinct()
                .ToList();
        }

        private List<string> GetDynamicRandomBoundDependencyDescriptions()
        {
            bool battleContext = HasBattleLikeInfo || HasPartiallyDefinedBattles || HasBattleInfo;
            return (DynamicRandomBoundDependencies ?? new List<DynamicRandomBoundDependencyInfo>())
                .Where(info => info != null)
                .Select(info => info.BuildDescription(battleContext))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(InlineNoteStyleCodec.EncodeDynamicRandomBoundDependencyText)
                .Distinct()
                .ToList();
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

                foreach (var progressionDescription in GetPersistentBattleCounterProgressionDescriptions())
                {
                    if (!addedDescriptions.Contains(progressionDescription))
                    {
                        descriptions.Add(progressionDescription);
                        addedDescriptions.Add(progressionDescription);
                    }
                }

                foreach (var dynamicBoundDescription in GetDynamicRandomBoundDependencyDescriptions())
                {
                    if (!addedDescriptions.Contains(dynamicBoundDescription))
                    {
                        descriptions.Add(dynamicBoundDescription);
                        addedDescriptions.Add(dynamicBoundDescription);
                    }
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
                        string sharedRepeatCount = GetSharedPartialBattleRepeatCountDisplay(PartiallyDefinedBattles);
                        string countDisplay = !string.IsNullOrWhiteSpace(sharedRepeatCount)
                            ? sharedRepeatCount
                            : GetPartialBattleCountDisplay(PartiallyDefinedBattles.Count);
                        string result;

                        if (monsters.Count == 1)
                        {
                            string cleanName = CleanMonsterNameForDisplay(monsters[0].MonsterName);
                            result = !string.IsNullOrEmpty(countDisplay)
                                ? $"Частично определённая битва: {cleanName} x{countDisplay}"
                                : $"Частично определённая битва: {cleanName}";
                        }
                        else
                        {
                            result = !string.IsNullOrEmpty(countDisplay)
                                ? $"Частично определённая битва. {monsters.Count} вариант(ов), группы по x{countDisplay}:"
                                : $"Частично определённая битва. {monsters.Count} вариант(ов):";

                            // Показываем все варианты (их обычно 8)
                            for (int i = 0; i < monsters.Count; i++)
                            {
                                string cleanName = CleanMonsterNameForDisplay(monsters[i].MonsterName);
                                string variantText = !string.IsNullOrEmpty(countDisplay)
                                    ? $"{cleanName} x{countDisplay}"
                                    : cleanName;
                                result += $"\n  • Вариант {i + 1}: {variantText}";
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
                        string battleRepeatDisplay = battle.GetRepeatCountDisplay();
                        string countDisplay = !string.IsNullOrWhiteSpace(battleRepeatDisplay)
                            ? battleRepeatDisplay
                            : (PartiallyDefinedBattles.Count == 1 ? GetPartialBattleCountDisplay() : null);

                        if (monsters.Count == 1)
                        {
                            string cleanName = CleanMonsterNameForDisplay(monsters[0].MonsterName);
                            string desc = !string.IsNullOrEmpty(countDisplay)
                                ? $"Частично определённая битва: {cleanName} x{countDisplay}"
                                : $"Частично определённая битва: {cleanName}";

                            if (!addedDescriptions.Contains(desc))
                            {
                                descriptions.Add(desc);
                                addedDescriptions.Add(desc);
                            }
                        }
                        else if (monsters.Count > 0)
                        {
                            bool showBxInHeader = PartiallyDefinedBattles.Count > 1;
                            string result = showBxInHeader
                                ? (!string.IsNullOrEmpty(countDisplay)
                                    ? $"Частично определённая битва (BX={battle.BxIndex}, {monsters.Count} вариантов, группы по x{countDisplay}):"
                                    : $"Частично определённая битва (BX={battle.BxIndex}, {monsters.Count} вариантов):")
                                : (!string.IsNullOrEmpty(countDisplay)
                                    ? $"Частично определённая битва. {monsters.Count} вариант(ов), группы по x{countDisplay}:"
                                    : $"Частично определённая битва. {monsters.Count} вариант(ов):");

                            for (int i = 0; i < monsters.Count; i++)
                            {
                                string cleanName = CleanMonsterNameForDisplay(monsters[i].MonsterName);
                                string variantText = !string.IsNullOrEmpty(countDisplay)
                                    ? $"{cleanName} x{countDisplay}"
                                    : cleanName;
                                result += $"\n  • Вариант {i + 1}: {variantText}";
                            }

                            if (!addedDescriptions.Contains(result))
                            {
                                descriptions.Add(result);
                                addedDescriptions.Add(result);
                            }
                        }
                        else
                        {
                            string repeatSuffix = !string.IsNullOrEmpty(countDisplay)
                                ? $", группа x{countDisplay}"
                                : string.Empty;
                            string desc = $"Частично определённая битва (BX={battle.BxIndex}, диапазоны: [{battle.RangeStart1:X2}-{battle.RangeEnd1:X2}] + [{battle.RangeStart2:X2}-{battle.RangeEnd2:X2}]{repeatSuffix})";
                            if (!addedDescriptions.Contains(desc))
                            {
                                descriptions.Add(desc);
                                addedDescriptions.Add(desc);
                            }
                        }
                    }
                }
            }

            if (descriptions.Count > 0)
            {
                foreach (var progressionDescription in GetPersistentBattleCounterProgressionDescriptions())
                {
                    if (!addedDescriptions.Contains(progressionDescription))
                    {
                        descriptions.Add(progressionDescription);
                        addedDescriptions.Add(progressionDescription);
                    }
                }

                foreach (var dynamicBoundDescription in GetDynamicRandomBoundDependencyDescriptions())
                {
                    if (!addedDescriptions.Contains(dynamicBoundDescription))
                    {
                        descriptions.Add(dynamicBoundDescription);
                        addedDescriptions.Add(dynamicBoundDescription);
                    }
                }
            }

            string rubiconWarning = GetRandomEncounterRubiconWarningDescription();
            if (descriptions.Count > 0 &&
                !string.IsNullOrEmpty(rubiconWarning) &&
                !addedDescriptions.Contains(rubiconWarning))
            {
                descriptions.Add(rubiconWarning);
                addedDescriptions.Add(rubiconWarning);
            }

            return descriptions.Count > 0 ? string.Join("\n", descriptions) : null;
        }

        // Вспомогательный метод для очистки имени монстра при отображении
        private string CleanMonsterNameForDisplay(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Оставляем только английские буквы, цифры, пробелы и базовые знаки препинания
            var clean = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
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
            HasBattleLikeInfo ||
            (DynamicRandomBoundDependencies != null && DynamicRandomBoundDependencies.Count > 0) ||
            HasAnyTableLoad ||
            CallsRandomEncounter;

        public bool ShouldKeepOriginalCentralOption
        {
            get
            {
                if (IsFromTable || PathVariants == null || PathVariants.Count == 0)
                    return false;

                return PathVariants.Values.All(variant => variant != null && variant.IsNoOpSpecVariant);
            }
        }

        #endregion

        public override string ToString()
        {
            var parts = new List<string>
            {
                $"OvrObject [X={X}, Y={Y}, Dir=0x{DirectionByte:X2}, Paths={NonEmptyPathsCount}"
            };

            if (RandomEncounterMonsterPowerCap.HasValue)
                parts.Add($"REPowerCap={RandomEncounterMonsterPowerCap.Value}");
            else
                parts.Add("REPowerCap=none");

            if (RandomEncounterMonsterLevelCap.HasValue)
                parts.Add($"RELevelCap={RandomEncounterMonsterLevelCap.Value}");
            else
                parts.Add("RELevelCap=none");

            if (RandomEncounterMonsterBatchCountCap.HasValue)
                parts.Add($"REBatchCountCap={RandomEncounterMonsterBatchCountCap.Value}");
            else
                parts.Add("REBatchCountCap=none");

            if (RandomEncounterChance.HasValue)
                parts.Add($"EncounterChance={RandomEncounterChance.Value}");
            else
                parts.Add("EncounterChance=none");

            if (RandomEncounterRubicon.HasValue)
                parts.Add($"EncounterRubicon={RandomEncounterRubicon.Value}");
            else
                parts.Add("EncounterRubicon=none");

            if (BattleMonsterStrengthAdjustment != 0)
                parts.Add($"BattleMonsterStrengthAdjustment={BattleMonsterStrengthAdjustment}");
            else
                parts.Add("BattleMonsterStrengthAdjustment=none");

            parts.Add($"BattleMonsters={BattleMonsters.Count}");
            parts.Add($"PartiallyDefined={PartiallyDefinedBattles.Count}");
            parts.Add($"TableLoad={HasAnyTableLoad}");

            return string.Join(", ", parts) + "]";
        }
    }

    public class BranchChoice
    {
        public string Label { get; set; }
        public string Condition { get; set; }
        public byte? CompareValue { get; set; }
        public string CompareRegister { get; set; }
        public ushort? CompareMemoryAddress { get; set; }
        public bool IsLinear { get; set; }
        public PartyPredicate GuardPredicate { get; set; }

        public bool HasGuardPredicate => GuardPredicate != null;

        public BranchChoice Clone()
        {
            return new BranchChoice
            {
                Label = Label,
                Condition = Condition,
                CompareValue = CompareValue,
                CompareRegister = CompareRegister,
                CompareMemoryAddress = CompareMemoryAddress,
                IsLinear = IsLinear,
                GuardPredicate = GuardPredicate?.Clone()
            };
        }

        public string GetIdentityKey()
        {
            string guardKey = HasGuardPredicate
                ? PartyEffectSemantics.BuildPredicateKey(GuardPredicate)
                : "<NO_GUARD>";

            return string.Join("|",
                Label ?? string.Empty,
                Condition ?? string.Empty,
                CompareRegister ?? string.Empty,
                CompareValue?.ToString() ?? string.Empty,
                IsLinear.ToString(),
                guardKey);
        }
    }

    public class PathVariantInfo
    {
        public int PathId { get; set; }
        public decimal PathOrderKey { get; set; }
        public bool IsLeaf { get; set; }

        public List<string> Texts { get; set; } = new List<string>();
        public List<BranchChoice> BranchChoices { get; set; } = new List<BranchChoice>();

        public byte? RandomEncounterMonsterPowerCap { get; set; }
        public byte? RandomEncounterMonsterLevelCap { get; set; }
        public byte? RandomEncounterMonsterBatchCountCap { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public byte? RandomEncounterRubicon { get; set; }
        public int BattleMonsterStrengthAdjustment { get; set; } = 0;
        public bool CallsRandomEncounter { get; set; } = false;
        public bool IsOnlyRandomEncounterJump { get; set; } = false;
        public uint RandomEncounterInstructionAddress { get; set; } = 0;
        public int RandomEncounterExecutionOrder { get; set; } = 0;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public ValueRange8 TeleportTargetXRange { get; set; }
        public ValueRange8 TeleportTargetYRange { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue || TeleportTargetXRange != null || TeleportTargetYRange != null;

        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; }
        public Dictionary<ushort, PersistentCounterProgressionInfo> PendingPersistentCounterProgressions { get; set; }
            = new Dictionary<ushort, PersistentCounterProgressionInfo>();
        public List<PersistentCounterProgressionInfo> PersistentCounterProgressions { get; set; }
            = new List<PersistentCounterProgressionInfo>();
        public List<DynamicRandomBoundDependencyInfo> DynamicRandomBoundDependencies { get; set; }
            = new List<DynamicRandomBoundDependencyInfo>();

        public List<BattleMonster> BattleMonsters { get; set; } = new List<BattleMonster>();
        public List<PartiallyDefinedBattle> PartiallyDefinedBattles { get; set; } = new List<PartiallyDefinedBattle>();

        public bool HasAnyTableLoad { get; set; }
        public List<OvrObject.LoadedValueInfo> LoadedValues { get; set; } = new List<OvrObject.LoadedValueInfo>();
        public List<PartyEffect> PartyEffects { get; set; } = new List<PartyEffect>();
        public Dictionary<ushort, byte> ExitEmulatedMemory8 { get; set; } = new Dictionary<ushort, byte>();
        public HashSet<ushort> AdjustedMemoryAddresses { get; set; } = new HashSet<ushort>();
        public HashSet<ushort> MemoryReadBeforeWriteAddresses { get; set; } = new HashSet<ushort>();
        public Dictionary<ushort, PersistentMemoryFirstAccessKind> PersistentMemoryFirstAccessKinds { get; set; }
            = new Dictionary<ushort, PersistentMemoryFirstAccessKind>();
        public Dictionary<ushort, StateValueConstraintInfo> StateValueConstraints { get; set; } = new Dictionary<ushort, StateValueConstraintInfo>();
        public HashSet<ushort> LocallyMaterializedStateValueConstraintAddresses { get; set; } = new HashSet<ushort>();
        public bool DisablesCurrentMapEvent { get; set; } = false;
        public bool HasRepeatedEventOccurrenceSensitivity { get; set; } = false;
        public bool SuppressRepeatedEventOccurrenceDescription { get; set; } = false;
        public bool UsesInitialCoordinates { get; set; } = false;
        public bool UsesStaticMapData { get; set; } = false;
        public Dictionary<ushort, byte> StaticMapDataReads { get; set; } = new Dictionary<ushort, byte>();
        public List<int> OccurrenceIndices { get; set; } = new List<int>();
        public List<OccurrenceRangeInfo> OccurrenceRanges { get; set; } = new List<OccurrenceRangeInfo>();
        public string OccurrenceDescription { get; set; }

        public int ProbabilityNumerator { get; set; } = 1;
        public int ProbabilityDenominator { get; set; } = 1;
        public bool TerminatedByRepeatedBackEdge { get; set; } = false;
        public bool TerminatedByPromptLoopBackEdge { get; set; } = false;
        public bool TerminatedByTerminalRet { get; set; } = false;
        public bool HasBranchSpecificContribution { get; set; } = false;

        public bool IsNoOpSpecVariant =>
            IsOnlyRandomEncounterJump &&
            (Texts == null || Texts.Count == 0) &&
            !RandomEncounterMonsterPowerCap.HasValue &&
            !RandomEncounterMonsterLevelCap.HasValue &&
            !RandomEncounterMonsterBatchCountCap.HasValue &&
            !DarkeningLevel.HasValue &&
            !RandomEncounterChance.HasValue &&
            !RandomEncounterRubicon.HasValue &&
            BattleMonsterStrengthAdjustment == 0 &&
            !HasTeleportTarget &&
            !BattleMonsterCount.HasValue &&
            BattleMonsterCountRange == null &&
            !IsBattleMonsterCountIndeterminate &&
            (PersistentCounterProgressions == null || PersistentCounterProgressions.Count == 0) &&
            (DynamicRandomBoundDependencies == null || DynamicRandomBoundDependencies.Count == 0) &&
            (BattleMonsters == null || BattleMonsters.Count == 0) &&
            (PartiallyDefinedBattles == null || PartiallyDefinedBattles.Count == 0) &&
            !HasAnyTableLoad &&
            (LoadedValues == null || LoadedValues.Count == 0) &&
            !HasStateGuardInfo &&
            (PartyEffects == null || PartyEffects.Count == 0);

        public bool HasProbabilityInfo => ProbabilityDenominator > 1;
        public bool HasOccurrenceInfo => !string.IsNullOrWhiteSpace(OccurrenceDescription);
        public bool HasStateGuardInfo => GetGuardPredicates().Count > 0;

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
                RandomEncounterMonsterPowerCap = RandomEncounterMonsterPowerCap,
                RandomEncounterMonsterLevelCap = RandomEncounterMonsterLevelCap,
                RandomEncounterMonsterBatchCountCap = RandomEncounterMonsterBatchCountCap,
                DarkeningLevel = DarkeningLevel,
                RandomEncounterChance = RandomEncounterChance,
                RandomEncounterRubicon = RandomEncounterRubicon,
                BattleMonsterStrengthAdjustment = BattleMonsterStrengthAdjustment,
                CallsRandomEncounter = CallsRandomEncounter,
                RandomEncounterInstructionAddress = RandomEncounterInstructionAddress,
                RandomEncounterExecutionOrder = RandomEncounterExecutionOrder,
                TeleportTargetX = TeleportTargetX,
                TeleportTargetY = TeleportTargetY,
                TeleportTargetXRange = TeleportTargetXRange == null ? null : new ValueRange8(TeleportTargetXRange.Min, TeleportTargetXRange.Max),
                TeleportTargetYRange = TeleportTargetYRange == null ? null : new ValueRange8(TeleportTargetYRange.Min, TeleportTargetYRange.Max),
                BattleMonsterCount = BattleMonsterCount,
                BattleMonsterCountRange = BattleMonsterCountRange == null ? null : new ValueRange8(BattleMonsterCountRange.Min, BattleMonsterCountRange.Max),
                IsBattleMonsterCountIndeterminate = IsBattleMonsterCountIndeterminate,
                PersistentCounterProgressions = PersistentCounterProgressions?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<PersistentCounterProgressionInfo>(),
                DynamicRandomBoundDependencies = DynamicRandomBoundDependencies?
                    .Where(info => info != null)
                    .Select(info => info.Clone())
                    .ToList() ?? new List<DynamicRandomBoundDependencyInfo>(),
                IsFromTable = true,
                HasAnyTableLoad = HasAnyTableLoad,
                PartyEffects = PartyEffects?.Select(e => e?.Clone()).Where(e => e != null).ToList() ?? new List<PartyEffect>()
            };

            // Вероятность варианта хранится в PathVariantInfo и используется построителем заметок.

            if (Texts != null && Texts.Count > 0)
            {
                obj.PathTexts[0] = new HashSet<string>(Texts);
                obj.PathTextsOrdered[0] = new List<string>(Texts);
            }

            obj.PathVariants[0] = this;
            obj.PathVariants[0].BranchChoices = BranchChoices?
                .Select(choice => choice?.Clone())
                .Where(choice => choice != null)
                .ToList() ?? new List<BranchChoice>();

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
                    obj.AddPartiallyDefinedBattle(partial);
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
            RandomEncounterMonsterPowerCap.HasValue ||
            RandomEncounterMonsterLevelCap.HasValue ||
            RandomEncounterMonsterBatchCountCap.HasValue ||
            DarkeningLevel.HasValue ||
            RandomEncounterChance.HasValue ||
            RandomEncounterRubicon.HasValue ||
            BattleMonsterStrengthAdjustment != 0 ||
            CallsRandomEncounter;

        public bool HasBattleInfo => BattleMonsters.Count > 0;
        public bool HasPartiallyDefinedBattles => PartiallyDefinedBattles.Count > 0;
        public bool HasBattleLikeInfo =>
            HasBattleInfo ||
            HasPartiallyDefinedBattles ||
            (HasAnyTableLoad && LoadedValues.Count > 0);
        public bool HasAnyInfo =>
            (Texts != null && Texts.Any(t => !string.IsNullOrWhiteSpace(t))) ||
            HasMonsterStatChanges ||
            HasBattleLikeInfo ||
            HasAnyTableLoad ||
            CallsRandomEncounter ||
            HasProbabilityInfo ||
            HasOccurrenceInfo ||
            HasStateGuardInfo ||
            (PartyEffects != null && PartyEffects.Count > 0);

        public string GetProbabilityDescription()
        {
            if (!HasProbabilityInfo || ProbabilityDenominator <= 0)
                return null;

            int numerator = Math.Max(0, ProbabilityNumerator);
            int denominator = Math.Max(1, ProbabilityDenominator);
            if (numerator >= denominator && numerator > 0)
            {
                numerator = 1;
                denominator = 1;
            }

            string percentText = ProbabilityFormatter.FormatPercent(numerator, denominator);

            string label = HasStateGuardInfo
                ? "Вероятность при выполнении условий"
                : "Вероятность";

            return $"{label}: {percentText}% ({numerator}/{denominator})";
        }

        public string GetOccurrenceDescription()
        {
            return string.IsNullOrWhiteSpace(OccurrenceDescription)
                ? null
                : OccurrenceDescription;
        }

        public IReadOnlyList<PartyPredicate> GetGuardPredicates()
        {
            if (BranchChoices == null || BranchChoices.Count == 0)
                return Array.Empty<PartyPredicate>();

            return BranchChoices
                .Where(choice => choice?.GuardPredicate != null)
                .Select(choice => choice.GuardPredicate)
                .GroupBy(PartyEffectSemantics.BuildPredicateKey)
                .Select(group => group.First().Clone())
                .OrderBy(PartyEffectSemantics.BuildPredicateKey)
                .ToList();
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
        public bool IsValid => MonsterIndex1 != 0 && MonsterIndex2 != 0;

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

    public class DiscreteBattleOption
    {
        public byte Val1 { get; set; }
        public byte Val2 { get; set; }

        public int MonsterId => Val1 + 16 * Val2 - 17;
        public string MonsterName => MonsterDatabase.GetMonsterName(MonsterId);

        public DiscreteBattleOption Clone()
        {
            return new DiscreteBattleOption
            {
                Val1 = Val1,
                Val2 = Val2
            };
        }
    }

    /// <summary>
    /// Представляет частично определённую битву:
    /// либо диапазонную (CDA9/CDB1),
    /// либо дискретный набор точных пар (например, CA7F/CA84).
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
        /// Точное количество монстров в итоговой группе, если оно известно.
        /// Для дискретных шаблонов это число повторений выбранного варианта,
        /// для диапазонных шаблонов используется как отображаемый размер группы.
        /// </summary>
        public int RepeatCount { get; set; } = 1;

        /// <summary>
        /// Диапазон количества повторений шаблона, если цикл ограничен случайным
        /// или символическим счётчиком.
        /// </summary>
        public ValueRange8 RepeatCountRange { get; set; }

        /// <summary>
        /// Дискретный набор точных пар val1/val2, если варианты задаются таблицей,
        /// а не декартовым произведением диапазонов.
        /// </summary>
        public List<DiscreteBattleOption> ExactOptions { get; set; } = new List<DiscreteBattleOption>();

        public bool HasExactOptions => ExactOptions != null && ExactOptions.Count > 0;

        /// <summary>
        /// Генерирует список возможных монстров из диапазона
        /// </summary>
        public List<PossibleMonster> GetPossibleMonsters()
        {
            var result = new List<PossibleMonster>();

            if (HasExactOptions)
            {
                AnalysisDebug.WriteLine($"      GetPossibleMonsters: дискретные варианты ({ExactOptions.Count})");

                foreach (var option in ExactOptions
                    .Where(option => option != null)
                    .GroupBy(option => $"{option.Val1:X2}:{option.Val2:X2}")
                    .Select(group => group.First())
                    .OrderBy(option => option.Val1)
                    .ThenBy(option => option.Val2))
                {
                    int monsterId = option.MonsterId;
                    if (monsterId < 0 || monsterId >= 256)
                        continue;

                    string monsterName = option.MonsterName;
                    if (string.IsNullOrEmpty(monsterName))
                        continue;

                    result.Add(new PossibleMonster
                    {
                        Val1 = option.Val1,
                        Val2 = option.Val2,
                        MonsterId = monsterId,
                        MonsterName = monsterName
                    });

                    AnalysisDebug.WriteLine($"        точная пара {option.Val1:X2}/{option.Val2:X2} -> {monsterName} (ID={monsterId})");
                }

                AnalysisDebug.WriteLine($"      Найдено монстров: {result.Count}");
                return result;
            }

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
        public int PossibleCombinations => HasExactOptions
            ? ExactOptions
                .Where(option => option != null)
                .GroupBy(option => $"{option.Val1:X2}:{option.Val2:X2}")
                .Count()
            : (RangeEnd1 - RangeStart1 + 1) * (RangeEnd2 - RangeStart2 + 1);

        public string GetRepeatCountDisplay()
        {
            if (RepeatCountRange != null)
            {
                return RepeatCountRange.IsExact
                    ? RepeatCountRange.Min.ToString()
                    : $"{RepeatCountRange.Min}-{RepeatCountRange.Max}";
            }

            return RepeatCount > 1 ? RepeatCount.ToString() : null;
        }

        public string GetIdentityKey()
        {
            string exactOptionsKey = HasExactOptions
                ? string.Join(",",
                    ExactOptions
                        .Where(option => option != null)
                        .OrderBy(option => option.Val1)
                        .ThenBy(option => option.Val2)
                        .Select(option => $"{option.Val1:X2}:{option.Val2:X2}"))
                : "<NO_EXACT_OPTIONS>";

            string repeatKey = RepeatCountRange != null
                ? $"{RepeatCountRange.Min}-{RepeatCountRange.Max}"
                : Math.Max(1, RepeatCount).ToString();

            return $"{BxIndex}:{RangeStart1:X2}-{RangeEnd1:X2}:{RangeStart2:X2}-{RangeEnd2:X2}:R{repeatKey}:{exactOptionsKey}";
        }

        public PartiallyDefinedBattle Clone()
        {
            return new PartiallyDefinedBattle
            {
                BxIndex = BxIndex,
                RangeStart1 = RangeStart1,
                RangeEnd1 = RangeEnd1,
                RangeStart2 = RangeStart2,
                RangeEnd2 = RangeEnd2,
                RepeatCount = RepeatCount,
                RepeatCountRange = RepeatCountRange == null ? null : new ValueRange8(RepeatCountRange.Min, RepeatCountRange.Max),
                ExactOptions = ExactOptions?
                    .Where(option => option != null)
                    .Select(option => option.Clone())
                    .ToList() ?? new List<DiscreteBattleOption>()
            };
        }

        public override string ToString()
        {
            var monsters = GetPossibleMonsters();
            string repeatDisplay = GetRepeatCountDisplay();
            if (monsters.Count == 1)
                return !string.IsNullOrEmpty(repeatDisplay)
                    ? $"Частично определён: {monsters[0].MonsterName} x{repeatDisplay}"
                    : $"Частично определён: {monsters[0].MonsterName}";
            else if (monsters.Count > 0)
                return HasExactOptions
                    ? $"Частично определён: {monsters.Count} точных вариант(ов){(!string.IsNullOrEmpty(repeatDisplay) ? $" группы x{repeatDisplay}" : string.Empty)}"
                    : $"Частично определён: {monsters.Count} возможных монстров (диапазоны: [{RangeStart1:X2}-{RangeEnd1:X2}] + [{RangeStart2:X2}-{RangeEnd2:X2}]){(!string.IsNullOrEmpty(repeatDisplay) ? $" группы x{repeatDisplay}" : string.Empty)}";
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
