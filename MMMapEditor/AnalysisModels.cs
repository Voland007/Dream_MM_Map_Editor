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

using Gee.External.Capstone.X86;
using System.Collections.Generic;

namespace MMMapEditor
{
    /// <summary>
    /// Модели данных, используемые при анализе OVR-файлов
    /// </summary>

    public enum CoordType
    {
        Unknown,
        XCoord,
        YCoord,
        FullCoord
    }

    public enum AccessType
    {
        Read,
        Write,
        Compare,
        Unknown
    }

    public class JumpCondition
    {
        public string Type { get; set; }
        public uint Target { get; set; }
    }

    public class AlternativePath
    {
        public int ObjectIndex { get; set; }
        public uint Address { get; set; }
        public string Condition { get; set; }
        public uint TargetAddress { get; set; }
        public bool Analyzed { get; set; }
        public int PathNumber { get; set; }
        public byte? CompareValue { get; set; }
        public string CompareRegister { get; set; }
        public RegisterTracker RegisterState { get; set; }
    }

    public class MemoryAccess
    {
        public uint Address { get; set; }
        public string Instruction { get; set; }
        public string Operand { get; set; }
        public ushort MemoryAddr { get; set; }
        public byte? ImmediateValue { get; set; }
        public string Register { get; set; }
        public AccessType Type { get; set; }
    }

    public class MacroExecutionResult
    {
        public Dictionary<ConditionRange, CodeEmulationResult> BranchResults { get; set; } = new Dictionary<ConditionRange, CodeEmulationResult>();
        public CodeEmulationResult CommonResult { get; set; } = new CodeEmulationResult();
    }

    public class ConditionRange
    {
        public byte Min { get; set; }
        public byte Max { get; set; }
        public bool IsX { get; set; }

        public bool Matches(byte value)
        {
            return value >= Min && value <= Max;
        }

        public override string ToString()
        {
            return $"{(IsX ? "X" : "Y")} ∈ [{Min}, {Max}]";
        }

        public override bool Equals(object obj)
        {
            if (obj is ConditionRange other)
            {
                return Min == other.Min && Max == other.Max && IsX == other.IsX;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (Min << 16) | (Max << 8) | (IsX ? 1 : 0);
        }
    }

    public class ExecutionPath
    {
        public uint StartAddress { get; set; }
        public X86Instruction Condition { get; set; }
        public uint SourceAddress { get; set; }
    }

    public class ExecutionState
    {
        public Dictionary<string, ushort> Registers { get; set; } = new Dictionary<string, ushort>();
        public Stack<uint> CallStack { get; set; } = new Stack<uint>();
        public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
        public CodeEmulationResult CurrentResult { get; set; } = new CodeEmulationResult();
        public ConditionRange CurrentCondition { get; set; }
    }

    public class CodeEmulationResult
    {
        public HashSet<string> Texts { get; set; } = new HashSet<string>();
        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }
        public byte? MonsterBatchCount { get; set; }
        public byte? LightingLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public List<(int Index, byte Val1, byte Val2, bool IsIndeterminate)> BattleMonsters { get; set; } = new List<(int, byte, byte, bool)>();
        public byte DirectionByte { get; set; }
        public bool HasSignificantCode { get; set; }
    }

    public class CoordinateComparison
    {
        public uint CompareAddress { get; set; }
        public ushort MemAddr { get; set; }
        public byte Value { get; set; }
        public uint JumpTarget { get; set; }
        public string JumpType { get; set; }
        public bool IsLinear { get; set; }
        public byte LinearStart { get; set; }
        public byte LinearEnd { get; set; }
        public CoordType CoordType { get; set; } = CoordType.Unknown;
    }

    public class TextEntry
    {
        public string Text { get; set; }
        public int Order { get; set; }
        public bool IsContextual { get; set; }
        public uint Address { get; set; }
    }

    public class PathResult
    {
        public int PathId { get; set; }
        public List<TextEntry> Texts { get; set; } = new List<TextEntry>();
        public bool IsLeaf { get; set; }
    }

    public class PathAnalysisResult
    {
        public HashSet<string> FoundTexts { get; set; } = new HashSet<string>();
        public HashSet<string> ContextTexts { get; set; } = new HashSet<string>(); // тексты из вызывающего кода
        public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }
        public byte? MonsterBatchCount { get; set; }
        public byte? LightingLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public byte? MonsterIndex1 { get; set; }
        public byte? MonsterIndex2 { get; set; }
        public byte? BattleMonsterCount { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; } = false;
        public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
            = new Dictionary<int, (byte, byte, bool)>();
        public bool HasPartialBattlePattern { get; set; } = false;
        public List<PartialBattleInfo> PartialBattleInfo { get; set; } = new List<PartialBattleInfo>();
        public List<PartiallyDefinedBattle> PartialBattles { get; set; } = new List<PartiallyDefinedBattle>();
        public bool IsInLoop { get; set; } = false;
        public uint LoopStartAddress { get; set; } = 0;
        public int LoopIteration { get; set; } = 0;
        public bool IsIndeterminateLoop { get; set; } = false;
        public int LoopIterationCount { get; set; } = 0;
        public bool IsTerminated { get; set; } = false;
        public bool HasSignificantCode { get; set; } = false;
        public List<AlternativePath> AlternativePaths { get; set; } = new List<AlternativePath>();
        public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();

        // Адрес первой инструкции, которая загрузила локальный текст
        public uint FirstLocalTextAddress { get; set; } = uint.MaxValue;

        // Для отслеживания последнего сравнения
        public string LastCompareReg { get; set; }
        public byte? LastCompareImm { get; set; }
        public ushort? LastCompareMem { get; set; }
    }

    public class PartialBattleInfo
    {
        public int BxIndex { get; set; }           // Индекс в массиве сохранения
        public ushort DestAddr { get; set; }       // Адрес назначения (0x3C58 или 0x3C29)
        public string SrcReg { get; set; }         // Исходный регистр (AL, CL, DL, BL)
        public byte SrcRegValue { get; set; }      // Значение в регистре
        public bool IsFromTable { get; set; }      // Загружено ли из таблицы
        public ushort? SourceTableAddr { get; set; } // Адрес в таблице-источнике
        public string SourceTable { get; set; }    // Тип таблицы ("CDA9", "CDB1", "CDBD", "CDB5")
    }
}