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

    public class ValueRange8
    {
        public byte Min { get; set; }
        public byte Max { get; set; }

        public ValueRange8() { }

        public ValueRange8(byte min, byte max)
        {
            Min = min;
            Max = max;
        }

        public bool IsExact => Min == Max;

        public override string ToString()
        {
            return IsExact ? Min.ToString() : $"{Min}-{Max}";
        }
    }

    public enum RegisterValueDistribution
    {
        Unknown,
        UniformDiscreteRange
    }

    public enum LoopSemanticKind
    {
        None = 0,
        PartialBattle = 1,
        PartyMemberScan = 2
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
        public bool IsInputChoiceBranch { get; set; }
        public RegisterTracker RegisterState { get; set; }
        public int ProbabilityNumerator { get; set; } = 1;
        public int ProbabilityDenominator { get; set; } = 1;
        public int CallDepth { get; set; } = 0;
        public List<uint> PendingReturnAddresses { get; set; } = new List<uint>();
        public Dictionary<ushort, byte> EmulatedMemory8 { get; set; } = new Dictionary<ushort, byte>();
        public Dictionary<ushort, PartyMemberReference> EmulatedPartyPointers { get; set; } = new Dictionary<ushort, PartyMemberReference>();
        public Dictionary<ushort, PartyPointerByteReference> EmulatedPartyPointerBytes { get; set; } = new Dictionary<ushort, PartyPointerByteReference>();
        public PartyConditionKind BranchPartyCondition { get; set; } = PartyConditionKind.None;
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
        public byte? DarkeningLevel { get; set; }
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

        /// <summary>
        /// Общая точка входа в decision-tree, к которому относится найденное сравнение.
        /// Может быть раньше CompareAddress, если перед координатной проверкой есть
        /// доминирующие guard-условия (например, внешний флаг, который отсекает весь блок).
        /// </summary>
        public uint MacroEntryAddress { get; set; }
    }

    public class TextEntry
    {
        public string Text { get; set; }
        public int Order { get; set; }
        public bool IsContextual { get; set; }
        public uint Address { get; set; }

        public TextEntry Clone()
        {
            return new TextEntry
            {
                Text = Text,
                Order = Order,
                IsContextual = IsContextual,
                Address = Address
            };
        }
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
        public HashSet<string> ContextTexts { get; set; } = new HashSet<string>(); // legacy-коллекции для дедупликации и совместимости
        public List<TextEntry> OrderedTexts { get; set; } = new List<TextEntry>();
        public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
        public byte? MonsterPower { get; set; }
        public byte? MonsterLevel { get; set; }
        public byte? MonsterBatchCount { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public byte? MonsterIndex1 { get; set; }
        public byte? MonsterIndex2 { get; set; }
        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; } = false;
        public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
            = new Dictionary<int, (byte, byte, bool)>();
        public bool HasPartialBattlePattern { get; set; } = false;
        public List<PartialBattleInfo> PartialBattleInfo { get; set; } = new List<PartialBattleInfo>();
        public List<PartiallyDefinedBattle> PartialBattles { get; set; } = new List<PartiallyDefinedBattle>();
        public List<PartyFieldReference> PartyFieldAccesses { get; set; } = new List<PartyFieldReference>();
        public PendingPartyHpOperation PendingPartyHpOperation { get; set; }
        public PartyConditionKind ActivePartyCondition { get; set; } = PartyConditionKind.None;
        public bool IsInLoop { get; set; } = false;
        public uint LoopStartAddress { get; set; } = 0;
        public int LoopIteration { get; set; } = 0;
        public bool IsIndeterminateLoop { get; set; } = false;
        public int LoopIterationCount { get; set; } = 0;
        public LoopSemanticKind LoopSemantic { get; set; } = LoopSemanticKind.None;
        public bool IsTerminated { get; set; } = false;
        public bool HasSignificantCode { get; set; } = false;
        public bool TerminatedByRepeatedBackEdge { get; set; } = false;
        public bool TerminatedByTerminalRet { get; set; } = false;
        public bool CallsRandomEncounter { get; set; } = false;
        public bool IsOnlyRandomEncounterJump { get; set; } = false;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public ValueRange8 TeleportTargetXRange { get; set; }
        public ValueRange8 TeleportTargetYRange { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue || TeleportTargetXRange != null || TeleportTargetYRange != null;
        public List<AlternativePath> AlternativePaths { get; set; } = new List<AlternativePath>();
        public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
        public List<uint> ExitPendingReturnAddresses { get; set; } = new List<uint>();
        public int ExitCallDepth { get; set; } = 0;

        // Адрес первой инструкции, которая загрузила локальный текст
        public uint FirstLocalTextAddress { get; set; } = uint.MaxValue;

        // Для отслеживания последнего сравнения
        public string LastCompareReg { get; set; }
        public byte? LastCompareImm { get; set; }
        public ushort? LastCompareMem { get; set; }
        public List<PartyEffect> PartyEffects { get; set; } = new List<PartyEffect>();
    }


    public enum PartyEffectKind
    {
        Unknown = 0,
        HpHalved = 1,
        HpWritten = 2,
        GenderWritten = 3,
        GenderCompared = 4,
        StatusWritten = 5
    }

    public enum PartyEffectScope
    {
        Unknown = 0,
        SingleMember = 1,
        CurrentLoopMember = 2,
        PartySubset = 3,
        WholeParty = 4,
        SelectedMember = 5,
        RandomMember = 6
    }

    public enum PartyEffectOperation
    {
        Unknown = 0,
        Compare = 1,
        Write = 2,
        Halve = 3,
        Increment = 4,
        Decrement = 5,
        Transform = 6,
        BitSet = 7,
        BitClear = 8,
        BitToggle = 9
    }

    public enum PartyConditionKind
    {
        None = 0,
        MaleOnly = 1,
        FemaleOnly = 2
    }

    public enum PartyValueKnowledge
    {
        Unknown = 0,
        ExactImmediate = 1,
        ExactDerived = 2,
        Range = 3,
        Structural = 4
    }

    public class PartyEffect
    {
        public PartyEffectKind Kind { get; set; }
        public PartyFieldKind Field { get; set; } = PartyFieldKind.Unknown;
        public PartyEffectOperation Operation { get; set; } = PartyEffectOperation.Unknown;
        public PartyEffectScope Scope { get; set; } = PartyEffectScope.Unknown;
        public PartyConditionKind Condition { get; set; } = PartyConditionKind.None;
        public int? MemberIndex { get; set; }
        public bool IsLoopDerived { get; set; }
        public bool AppliesToWholePartyLoop
        {
            get => Scope == PartyEffectScope.PartySubset || Scope == PartyEffectScope.WholeParty;
            set
            {
                if (value)
                {
                    if (Scope == PartyEffectScope.Unknown || Scope == PartyEffectScope.CurrentLoopMember)
                        Scope = PartyEffectScope.WholeParty;
                }
                else if (Scope == PartyEffectScope.WholeParty)
                {
                    Scope = PartyEffectScope.Unknown;
                }
            }
        }

        // Legacy compatibility fields. New code should prefer Condition.
        public bool? MaleOnly
        {
            get => Condition == PartyConditionKind.MaleOnly ? true : (bool?)null;
            set
            {
                if (value == true)
                    Condition = PartyConditionKind.MaleOnly;
                else if (Condition == PartyConditionKind.MaleOnly)
                    Condition = PartyConditionKind.None;
            }
        }

        public bool? FemaleOnly
        {
            get => Condition == PartyConditionKind.FemaleOnly ? true : (bool?)null;
            set
            {
                if (value == true)
                    Condition = PartyConditionKind.FemaleOnly;
                else if (Condition == PartyConditionKind.FemaleOnly)
                    Condition = PartyConditionKind.None;
            }
        }

        public PartyValueKnowledge ValueKnowledge { get; set; } = PartyValueKnowledge.Unknown;
        public ushort? ImmediateValue { get; set; }
        public ValueRange8 ImmediateRange { get; set; }
        public uint InstructionAddress { get; set; }
        public string Description { get; set; }

        public PartyEffect Clone()
        {
            return new PartyEffect
            {
                Kind = Kind,
                Field = Field,
                Operation = Operation,
                Scope = Scope,
                Condition = Condition,
                MemberIndex = MemberIndex,
                IsLoopDerived = IsLoopDerived,
                ValueKnowledge = ValueKnowledge,
                ImmediateValue = ImmediateValue,
                ImmediateRange = ImmediateRange == null ? null : new ValueRange8(ImmediateRange.Min, ImmediateRange.Max),
                InstructionAddress = InstructionAddress,
                Description = Description
            };
        }
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
