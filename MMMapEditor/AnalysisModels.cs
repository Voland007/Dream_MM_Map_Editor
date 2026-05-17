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
using System.Linq;

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

    public enum StateValueBoundaryKind
    {
        Exact,
        LowerInclusive,
        UpperInclusive,
        Excluded
    }

    public class StateValueConstraintInfo
    {
        public HashSet<byte> ExactValues { get; set; } = new HashSet<byte>();
        public HashSet<byte> LowerInclusiveValues { get; set; } = new HashSet<byte>();
        public HashSet<byte> UpperInclusiveValues { get; set; } = new HashSet<byte>();
        public HashSet<byte> ExcludedValues { get; set; } = new HashSet<byte>();

        public bool IsEmpty =>
            ExactValues.Count == 0 &&
            LowerInclusiveValues.Count == 0 &&
            UpperInclusiveValues.Count == 0 &&
            ExcludedValues.Count == 0;

        public StateValueConstraintInfo Clone()
        {
            return new StateValueConstraintInfo
            {
                ExactValues = new HashSet<byte>(ExactValues),
                LowerInclusiveValues = new HashSet<byte>(LowerInclusiveValues),
                UpperInclusiveValues = new HashSet<byte>(UpperInclusiveValues),
                ExcludedValues = new HashSet<byte>(ExcludedValues)
            };
        }

        public void Add(StateValueBoundaryKind kind, byte value)
        {
            switch (kind)
            {
                case StateValueBoundaryKind.Exact:
                    ExactValues.Add(value);
                    break;

                case StateValueBoundaryKind.LowerInclusive:
                    LowerInclusiveValues.Add(value);
                    break;

                case StateValueBoundaryKind.UpperInclusive:
                    UpperInclusiveValues.Add(value);
                    break;

                case StateValueBoundaryKind.Excluded:
                    ExcludedValues.Add(value);
                    break;
            }
        }

        public void MergeFrom(StateValueConstraintInfo other)
        {
            if (other == null)
                return;

            ExactValues.UnionWith(other.ExactValues ?? new HashSet<byte>());
            LowerInclusiveValues.UnionWith(other.LowerInclusiveValues ?? new HashSet<byte>());
            UpperInclusiveValues.UnionWith(other.UpperInclusiveValues ?? new HashSet<byte>());
            ExcludedValues.UnionWith(other.ExcludedValues ?? new HashSet<byte>());
        }
    }

    public enum RegisterValueDistribution
    {
        Unknown,
        UniformDiscreteRange,
        EvenDiscreteRange
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
        public ushort? CompareMemoryAddress { get; set; }
        public PartyFieldReference ComparedPartyField { get; set; }
        public bool IsInputChoiceBranch { get; set; }
        public RegisterTracker RegisterState { get; set; }
        public int ProbabilityNumerator { get; set; } = 1;
        public int ProbabilityDenominator { get; set; } = 1;
        public int CallDepth { get; set; } = 0;
        public List<uint> PendingReturnAddresses { get; set; } = new List<uint>();
        public Dictionary<ushort, byte> EmulatedMemory8 { get; set; } = new Dictionary<ushort, byte>();
        public Dictionary<ushort, ValueRange8> EmulatedMemory8Ranges { get; set; } = new Dictionary<ushort, ValueRange8>();
        public Dictionary<ushort, RegisterValueDistribution> EmulatedMemory8RangeDistributions { get; set; } = new Dictionary<ushort, RegisterValueDistribution>();
        public Dictionary<ushort, PartyMemberReference> EmulatedPartyPointers { get; set; } = new Dictionary<ushort, PartyMemberReference>();
        public Dictionary<ushort, PartyPointerByteReference> EmulatedPartyPointerBytes { get; set; } = new Dictionary<ushort, PartyPointerByteReference>();
        public Dictionary<ushort, PersistentCounterProgressionInfo> PendingPersistentCounterProgressions { get; set; }
            = new Dictionary<ushort, PersistentCounterProgressionInfo>();
        public Dictionary<ushort, StateValueConstraintInfo> BranchStateValueConstraints { get; set; } = new Dictionary<ushort, StateValueConstraintInfo>();
        public HashSet<ushort> BranchLocallyMaterializedStateValueConstraintAddresses { get; set; } = new HashSet<ushort>();
        public PartyConditionKind BranchPartyCondition { get; set; } = PartyConditionKind.None;
        public PartyPredicate BranchPartyPredicate { get; set; }
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
        public byte? RandomEncounterMonsterPowerCap { get; set; }
        public byte? RandomEncounterMonsterLevelCap { get; set; }
        public byte? RandomEncounterMonsterBatchCountCap { get; set; }
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
        public bool HasPrecedingCondition { get; set; }
        public CoordType PrecedingCoordType { get; set; } = CoordType.Unknown;
        public byte PrecedingValue { get; set; }
        public string PrecedingJumpType { get; set; }
        public bool PrecedingViaJumpTarget { get; set; }
    }

    public enum TextSemanticKind
    {
        Unknown = 0,
        LootContainerIntro = 1,
        LootPayload = 2
    }

    public enum NoteInlineStyleKind
    {
        InverseVideo = 0,
        AggregateTemporaryStatHighlight = 1,
        AggregateTemporaryStatGroup = 2,
        AggregateTemporaryStatTemporaryWord = 3,
        AggregateTemporaryStatValue = 4,
        RandomEncounterRubiconWarning = 5,
        RandomEncounterRubiconThreshold = 6,
        PersistentCounterProgressionNote = 7,
        PersistentCounterProgressionValue = 8,
        DynamicRandomBoundDependencyNote = 9,
        DynamicRandomBoundDependencyFormula = 10,
        MutedParentheticalNote = 11,
        WheelRewardExplanation = 12,
        RepeatedBattleWarning = 13,
        RawOverlayText = 14,
        BattleMonsterStrengthIncrease = 15,
        BattleMonsterStrengthDecrease = 16,
        ItemName = 17,
        HpRestoredToMaximum = 18,
        AlignmentRestoreKeyword = 19
    }

    public sealed class NoteInlineStyleSpan
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public NoteInlineStyleKind Kind { get; set; }

        public NoteInlineStyleSpan Clone()
        {
            return new NoteInlineStyleSpan
            {
                Start = Start,
                Length = Length,
                Kind = Kind
            };
        }
    }

    public class TextEntry
    {
        public string Text { get; set; }
        public int Order { get; set; }
        public bool IsContextual { get; set; }
        public uint Address { get; set; }
        public TextSemanticKind SemanticKind { get; set; } = TextSemanticKind.Unknown;
        public bool IsInferred { get; set; } = false;

        public TextEntry Clone()
        {
            return new TextEntry
            {
                Text = Text,
                Order = Order,
                IsContextual = IsContextual,
                Address = Address,
                SemanticKind = SemanticKind,
                IsInferred = IsInferred
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
        public HashSet<ushort> MemoryReadAddresses { get; set; } = new HashSet<ushort>();
        public HashSet<ushort> MemoryWrittenAddresses { get; set; } = new HashSet<ushort>();
        public HashSet<ushort> AdjustedMemoryAddresses { get; set; } = new HashSet<ushort>();
        public HashSet<ushort> MemoryReadBeforeWriteAddresses { get; set; } = new HashSet<ushort>();
        public Dictionary<ushort, PersistentMemoryFirstAccessKind> PersistentMemoryFirstAccessKinds { get; set; }
            = new Dictionary<ushort, PersistentMemoryFirstAccessKind>();
        public Dictionary<ushort, StateValueConstraintInfo> StateValueConstraints { get; set; } = new Dictionary<ushort, StateValueConstraintInfo>();
        public HashSet<ushort> LocallyMaterializedStateValueConstraintAddresses { get; set; } = new HashSet<ushort>();
        public Dictionary<ushort, byte> ExitEmulatedMemory8 { get; set; } = new Dictionary<ushort, byte>();
        public Dictionary<ushort, ValueRange8> ExitEmulatedMemory8Ranges { get; set; } = new Dictionary<ushort, ValueRange8>();
        public Dictionary<ushort, RegisterValueDistribution> ExitEmulatedMemory8RangeDistributions { get; set; } = new Dictionary<ushort, RegisterValueDistribution>();
        public Dictionary<int, PathAnalysisResult> NestedPaths { get; set; } = new Dictionary<int, PathAnalysisResult>();
        public byte? RandomEncounterMonsterPowerCap { get; set; }
        public byte? RandomEncounterMonsterLevelCap { get; set; }
        public byte? RandomEncounterMonsterBatchCountCap { get; set; }
        public byte? DarkeningLevel { get; set; }
        public byte? RandomEncounterChance { get; set; }
        public byte? RandomEncounterRubicon { get; set; }
        public int BattleMonsterStrengthAdjustment { get; set; } = 0;
        public byte? MonsterIndex1 { get; set; }
        public byte? MonsterIndex2 { get; set; }
        public byte? BattleMonsterCount { get; set; }
        public ValueRange8 BattleMonsterCountRange { get; set; }
        public bool IsBattleMonsterCountIndeterminate { get; set; } = false;
        public Dictionary<ushort, PersistentCounterProgressionInfo> PendingPersistentCounterProgressions { get; set; }
            = new Dictionary<ushort, PersistentCounterProgressionInfo>();
        public List<PersistentCounterProgressionInfo> PersistentCounterProgressions { get; set; }
            = new List<PersistentCounterProgressionInfo>();
        public List<DynamicRandomBoundDependencyInfo> DynamicRandomBoundDependencies { get; set; }
            = new List<DynamicRandomBoundDependencyInfo>();
        public Dictionary<int, (byte val1, byte val2, bool isIndeterminate)> BattleMonsterEntries { get; set; }
            = new Dictionary<int, (byte, byte, bool)>();
        public bool HasPartialBattlePattern { get; set; } = false;
        public List<PartialBattleInfo> PartialBattleInfo { get; set; } = new List<PartialBattleInfo>();
        public List<PartiallyDefinedBattle> PartialBattles { get; set; } = new List<PartiallyDefinedBattle>();
        public List<PartyFieldReference> PartyFieldAccesses { get; set; } = new List<PartyFieldReference>();
        public PendingPartyStatOperation PendingPartyHpOperation { get; set; }
        public PendingPartyStatOperation PendingPartySpOperation { get; set; }
        public List<PendingPartyStatOperation> CompletedPartyStatOperations { get; set; } = new List<PendingPartyStatOperation>();
        public PartyConditionKind ActivePartyCondition { get; set; } = PartyConditionKind.None;
        public bool IsInLoop { get; set; } = false;
        public uint LoopStartAddress { get; set; } = 0;
        public uint LoopEndAddress { get; set; } = 0;
        public int LoopIteration { get; set; } = 0;
        public bool IsIndeterminateLoop { get; set; } = false;
        public int LoopIterationCount { get; set; } = 0;
        public LoopSemanticKind LoopSemantic { get; set; } = LoopSemanticKind.None;
        public bool IsTerminated { get; set; } = false;
        public bool HasSignificantCode { get; set; } = false;
        public bool TerminatedByRepeatedBackEdge { get; set; } = false;
        public bool TerminatedByPromptLoopBackEdge { get; set; } = false;
        public bool TerminatedByTerminalRet { get; set; } = false;
        public bool DisablesCurrentMapEvent { get; set; } = false;
        public bool CallsRandomEncounter { get; set; } = false;
        public bool IsOnlyRandomEncounterJump { get; set; } = false;
        public uint RandomEncounterInstructionAddress { get; set; } = 0;
        public int RandomEncounterExecutionOrder { get; set; } = 0;
        public byte? TeleportTargetX { get; set; }
        public byte? TeleportTargetY { get; set; }
        public ValueRange8 TeleportTargetXRange { get; set; }
        public ValueRange8 TeleportTargetYRange { get; set; }
        public bool HasTeleportTarget => TeleportTargetX.HasValue || TeleportTargetY.HasValue || TeleportTargetXRange != null || TeleportTargetYRange != null;
        public List<AlternativePath> AlternativePaths { get; set; } = new List<AlternativePath>();
        public HashSet<uint> VisitedAddresses { get; set; } = new HashSet<uint>();
        public List<uint> ExitPendingReturnAddresses { get; set; } = new List<uint>();
        public int ExitCallDepth { get; set; } = 0;
        public bool UsesInitialCoordinates { get; set; } = false;
        public bool UsesStaticMapData { get; set; } = false;
        public Dictionary<ushort, byte> StaticMapDataReads { get; set; } = new Dictionary<ushort, byte>();
        public int InlineProbabilityNumerator { get; set; } = 1;
        public int InlineProbabilityDenominator { get; set; } = 1;
        public List<BranchChoice> InlineBranchChoices { get; set; } = new List<BranchChoice>();

        // Адрес первой инструкции, которая загрузила локальный текст
        public uint FirstLocalTextAddress { get; set; } = uint.MaxValue;
        public int NextSpecialEventOrder { get; set; } = 0;

        // Для отслеживания последнего сравнения
        public string LastCompareReg { get; set; }
        public byte? LastCompareImm { get; set; }
        public ushort? LastCompareMem { get; set; }
        public List<PartyEffect> PartyEffects { get; set; } = new List<PartyEffect>();
    }

    public enum PersistentCounterProgressionTargetKind
    {
        Unknown = 0,
        BattleMonsterCount = 1
    }

    public class PersistentCounterProgressionInfo
    {
        public ushort CounterAddress { get; set; }
        public byte InitialValue { get; set; }
        public byte CurrentValue { get; set; }
        public int Delta { get; set; }
        public byte? CapValue { get; set; }
        public ushort? TargetAddress { get; set; }
        public PersistentCounterProgressionTargetKind TargetKind { get; set; } = PersistentCounterProgressionTargetKind.Unknown;
        public uint AdjustmentInstructionAddress { get; set; }
        public uint CapInstructionAddress { get; set; }
        public uint TargetWriteInstructionAddress { get; set; }

        public bool IsLinkedToBattleMonsterCount =>
            TargetKind == PersistentCounterProgressionTargetKind.BattleMonsterCount;

        public bool HasFutureProgression =>
            IsLinkedToBattleMonsterCount &&
            Delta > 0 &&
            CapValue.HasValue &&
            CurrentValue < CapValue.Value;

        public PersistentCounterProgressionInfo Clone()
        {
            return new PersistentCounterProgressionInfo
            {
                CounterAddress = CounterAddress,
                InitialValue = InitialValue,
                CurrentValue = CurrentValue,
                Delta = Delta,
                CapValue = CapValue,
                TargetAddress = TargetAddress,
                TargetKind = TargetKind,
                AdjustmentInstructionAddress = AdjustmentInstructionAddress,
                CapInstructionAddress = CapInstructionAddress,
                TargetWriteInstructionAddress = TargetWriteInstructionAddress
            };
        }

        public string GetIdentityKey()
        {
            return string.Join("|",
                CounterAddress.ToString("X4"),
                InitialValue.ToString("X2"),
                CurrentValue.ToString("X2"),
                Delta.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CapValue?.ToString("X2") ?? "-",
                TargetAddress?.ToString("X4") ?? "-",
                TargetKind.ToString(),
                AdjustmentInstructionAddress.ToString("X4"),
                CapInstructionAddress.ToString("X4"),
                TargetWriteInstructionAddress.ToString("X4"));
        }

        public string ToDebugString()
        {
            string capText = CapValue.HasValue ? $"cap=0x{CapValue.Value:X2}" : "cap=?";
            string targetText = TargetAddress.HasValue
                ? $"{TargetKind}@[0x{TargetAddress.Value:X4}]"
                : TargetKind.ToString();
            string opText = Delta >= 0 ? $"+{Delta}" : Delta.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"[0x{CounterAddress:X4}] {InitialValue}->{CurrentValue} ({opText}), {capText}, target={targetText}, at=0x{TargetWriteInstructionAddress:X4}";
        }
    }

    public enum DynamicRandomBoundTargetKind
    {
        Unknown = 0,
        RandomUpperBound = 1,
        PartialBattleVariantCount = 2
    }

    public class DynamicValueFormulaInfo
    {
        public PartyFieldReference SourceField { get; set; }
        public int Multiplier { get; set; } = 1;
        public int AdditiveOffset { get; set; }
        public byte? MinValue { get; set; }
        public byte? MaxValue { get; set; }
        public uint SourceInstructionAddress { get; set; }
        public uint LastTransformInstructionAddress { get; set; }

        public DynamicValueFormulaInfo Clone()
        {
            return new DynamicValueFormulaInfo
            {
                SourceField = SourceField?.Clone(),
                Multiplier = Multiplier,
                AdditiveOffset = AdditiveOffset,
                MinValue = MinValue,
                MaxValue = MaxValue,
                SourceInstructionAddress = SourceInstructionAddress,
                LastTransformInstructionAddress = LastTransformInstructionAddress
            };
        }

        public DynamicValueFormulaInfo WithAdditiveOffset(int delta, uint instructionAddress)
        {
            var clone = Clone();
            clone.AdditiveOffset += delta;
            clone.MinValue = AddClamped(clone.MinValue, delta);
            clone.MaxValue = AddClamped(clone.MaxValue, delta);
            clone.LastTransformInstructionAddress = instructionAddress;
            return clone;
        }

        public DynamicValueFormulaInfo WithValueConstraint(byte min, byte max)
        {
            var clone = Clone();
            clone.MinValue = clone.MinValue.HasValue ? (byte)System.Math.Max(clone.MinValue.Value, min) : min;
            clone.MaxValue = clone.MaxValue.HasValue ? (byte)System.Math.Min(clone.MaxValue.Value, max) : max;

            if (clone.MinValue.HasValue &&
                clone.MaxValue.HasValue &&
                clone.MinValue.Value > clone.MaxValue.Value)
            {
                clone.MinValue = min;
                clone.MaxValue = max;
            }

            return clone;
        }

        public string GetFormulaExpression()
        {
            string baseText = GetFormulaBaseText(SourceField?.Field);
            if (Multiplier != 1)
                baseText = $"{Multiplier}*{baseText}";

            if (AdditiveOffset > 0)
                return $"{baseText} + {AdditiveOffset}";

            if (AdditiveOffset < 0)
                return $"{baseText} - {System.Math.Abs(AdditiveOffset)}";

            return baseText;
        }

        public string GetSourceDescription()
        {
            string fieldText = GetSourceFieldPhrase(SourceField?.Field);
            string memberText = GetMemberPhrase(SourceField?.Member);
            return string.IsNullOrWhiteSpace(memberText)
                ? fieldText
                : $"{fieldText} {memberText}";
        }

        public string GetIdentityKey()
        {
            var member = SourceField?.Member;
            return string.Join("|",
                SourceField?.Field.ToString() ?? "-",
                SourceField?.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                member?.MemberIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                member?.IsPartyLoopMember == true ? "loop" : "-",
                Multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AdditiveOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MinValue?.ToString("X2") ?? "-",
                MaxValue?.ToString("X2") ?? "-",
                SourceInstructionAddress.ToString("X4"),
                LastTransformInstructionAddress.ToString("X4"));
        }

        private static byte? AddClamped(byte? value, int delta)
        {
            if (!value.HasValue)
                return null;

            int adjusted = value.Value + delta;
            if (adjusted < 0)
                adjusted = 0;
            if (adjusted > 0xFF)
                adjusted = 0xFF;

            return (byte)adjusted;
        }

        private static string GetFormulaBaseText(PartyFieldKind? field)
        {
            return field switch
            {
                PartyFieldKind.TempLevel => "временный LEVEL",
                PartyFieldKind.TempIntellect => "временный INTELLECT",
                PartyFieldKind.TempMight => "временный MIGHT",
                PartyFieldKind.TempPersonality => "временный PERSONALITY",
                PartyFieldKind.TempEndurance => "временный ENDURANCE",
                PartyFieldKind.TempSpeed => "временный SPEED",
                PartyFieldKind.TempAccuracy => "временный ACCURANCY",
                PartyFieldKind.TempLuck => "временный LUCK",
                PartyFieldKind.Food => PartyFoodSemantics.FieldLabel,
                _ when field.HasValue && PartyTemporaryStatSemantics.IsTrackedField(field.Value) =>
                    PartyTemporaryStatSemantics.GetFieldLabel(field.Value),
                _ when field.HasValue && PartyTechnicalFieldSemantics.IsTrackedField(field.Value) =>
                    PartyTechnicalFieldSemantics.GetFieldLabel(field.Value),
                _ => field?.ToString() ?? "значение"
            };
        }

        private static string GetSourceFieldPhrase(PartyFieldKind? field)
        {
            return field switch
            {
                PartyFieldKind.TempLevel => "временного уровня",
                PartyFieldKind.Food => PartyFoodSemantics.FieldLabel,
                _ when field.HasValue && PartyTemporaryStatSemantics.IsTrackedField(field.Value) =>
                    PartyTemporaryStatSemantics.GetFieldLabel(field.Value),
                _ when field.HasValue && PartyTechnicalFieldSemantics.IsTrackedField(field.Value) =>
                    PartyTechnicalFieldSemantics.GetFieldLabel(field.Value),
                _ => field?.ToString() ?? "значения"
            };
        }

        private static string GetMemberPhrase(PartyMemberReference member)
        {
            if (member == null)
                return null;

            if (member.IsPartyLoopMember)
                return "каждого персонажа партии";

            if (member.MemberIndex == 0)
                return "первого героя";

            if (member.MemberIndex.HasValue)
                return $"персонажа {PartyMemberReference.FormatDisplayIndex(member.MemberIndex.Value)}";

            return null;
        }
    }

    public class DynamicRandomBoundDependencyInfo
    {
        public DynamicValueFormulaInfo UpperBoundFormula { get; set; }
        public DynamicRandomBoundTargetKind TargetKind { get; set; } = DynamicRandomBoundTargetKind.RandomUpperBound;
        public byte? MaxObservedUpperBound { get; set; }
        public string ResultRegisterName { get; set; }
        public uint RandomCallInstructionAddress { get; set; }
        public uint TargetInstructionAddress { get; set; }
        public ushort? TargetAddress { get; set; }

        public DynamicRandomBoundDependencyInfo Clone()
        {
            return new DynamicRandomBoundDependencyInfo
            {
                UpperBoundFormula = UpperBoundFormula?.Clone(),
                TargetKind = TargetKind,
                MaxObservedUpperBound = MaxObservedUpperBound,
                ResultRegisterName = ResultRegisterName,
                RandomCallInstructionAddress = RandomCallInstructionAddress,
                TargetInstructionAddress = TargetInstructionAddress,
                TargetAddress = TargetAddress
            };
        }

        public string GetIdentityKey()
        {
            return string.Join("|",
                UpperBoundFormula?.GetIdentityKey() ?? "-",
                TargetKind.ToString(),
                MaxObservedUpperBound?.ToString("X2") ?? "-",
                ResultRegisterName ?? "-",
                RandomCallInstructionAddress.ToString("X4"),
                TargetInstructionAddress.ToString("X4"),
                TargetAddress?.ToString("X4") ?? "-");
        }

        public string BuildDescription(bool battleContext)
        {
            if (UpperBoundFormula == null)
                return null;

            string sourceText = UpperBoundFormula.GetSourceDescription();
            string formulaText = UpperBoundFormula.GetFormulaExpression();

            if (battleContext || TargetKind == DynamicRandomBoundTargetKind.PartialBattleVariantCount)
                return $"Количество вариантов зависит от {sourceText} и рассчитывается по формуле: {formulaText}";

            return $"Верхняя граница случайного значения зависит от {sourceText} и рассчитывается по формуле: {formulaText}";
        }

        public string ToDebugString()
        {
            string maxText = MaxObservedUpperBound.HasValue ? $"max=0x{MaxObservedUpperBound.Value:X2}" : "max=?";
            string targetText = TargetAddress.HasValue
                ? $"{TargetKind}@[0x{TargetAddress.Value:X4}]"
                : TargetKind.ToString();
            return $"{targetText}, {maxText}, formula={UpperBoundFormula?.GetFormulaExpression() ?? "?"}, call=0x{RandomCallInstructionAddress:X4}";
        }
    }

    public enum PersistentMemoryFirstAccessKind
    {
        Read = 0,
        Write = 1
    }


    public enum PartyEffectKind
    {
        Unknown = 0,
        HpHalved = 1,
        HpWritten = 2,
        sexWritten = 3,
        sexCompared = 4,
        StatusWritten = 5,
        TechnicalFieldRead = 6,
        TechnicalFieldWritten = 7,
        TechnicalFieldCompared = 8,
        AlignmentWritten = 9,
        AlignmentCompared = 10
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
        BitToggle = 9,
        Read = 10
    }

    public enum PartyConditionKind
    {
        None = 0,
        MaleOnly = 1,
        FemaleOnly = 2
    }

    public class PartyConditionWindow
    {
        public PartyConditionKind Condition { get; set; } = PartyConditionKind.None;
        public uint StartAddress { get; set; }
        public uint EndAddress { get; set; }
        public PartyMemberReference TargetMember { get; set; }

        public bool IsActiveAt(uint address)
        {
            if (Condition == PartyConditionKind.None)
                return false;

            if (address < StartAddress)
                return false;

            return EndAddress == 0 || address < EndAddress;
        }

        public PartyConditionWindow Clone()
        {
            return new PartyConditionWindow
            {
                Condition = Condition,
                StartAddress = StartAddress,
                EndAddress = EndAddress,
                TargetMember = TargetMember?.Clone()
            };
        }
    }

    public class OccurrenceRangeInfo
    {
        public int Start { get; set; }
        public int End { get; set; }
        public bool IsOpenEnded { get; set; }

        public OccurrenceRangeInfo Clone()
        {
            return new OccurrenceRangeInfo
            {
                Start = Start,
                End = End,
                IsOpenEnded = IsOpenEnded
            };
        }
    }

    public enum PartyPredicateComparison
    {
        Unknown = 0,
        Equal = 1,
        NotEqual = 2,
        LessThan = 3,
        LessOrEqual = 4,
        GreaterThan = 5,
        GreaterOrEqual = 6
    }

    public enum PartyPredicateLoopQuantifier
    {
        Unspecified = 0,
        Any = 1,
        None = 2
    }

    public class PartyPredicate
    {
        public PartyFieldKind Field { get; set; } = PartyFieldKind.Unknown;
        public PartyPredicateComparison Comparison { get; set; } = PartyPredicateComparison.Unknown;
        public PartyPredicateLoopQuantifier LoopQuantifier { get; set; } = PartyPredicateLoopQuantifier.Unspecified;
        public PartyValueKnowledge ValueKnowledge { get; set; } = PartyValueKnowledge.Unknown;
        public ushort? ImmediateValue { get; set; }
        public ValueRange8 ImmediateRange { get; set; }
        public byte? FieldOffset { get; set; }
        public ValueRange8 FieldOffsetRange { get; set; }
        public uint InstructionAddress { get; set; }
        public PartyMemberReference TargetMember { get; set; }
        public string Description { get; set; }

        public PartyPredicate Clone()
        {
            return new PartyPredicate
            {
                Field = Field,
                Comparison = Comparison,
                LoopQuantifier = LoopQuantifier,
                ValueKnowledge = ValueKnowledge,
                ImmediateValue = ImmediateValue,
                ImmediateRange = ImmediateRange == null ? null : new ValueRange8(ImmediateRange.Min, ImmediateRange.Max),
                FieldOffset = FieldOffset,
                FieldOffsetRange = FieldOffsetRange == null ? null : new ValueRange8(FieldOffsetRange.Min, FieldOffsetRange.Max),
                InstructionAddress = InstructionAddress,
                TargetMember = TargetMember?.Clone(),
                Description = Description
            };
        }
    }

    public class PartyPredicateWindow
    {
        public PartyPredicate Predicate { get; set; }
        public uint StartAddress { get; set; }
        public uint EndAddress { get; set; }

        public bool IsActiveAt(uint address)
        {
            if (Predicate == null)
                return false;

            if (address < StartAddress)
                return false;

            return EndAddress == 0 || address < EndAddress;
        }

        public PartyPredicateWindow Clone()
        {
            return new PartyPredicateWindow
            {
                Predicate = Predicate?.Clone(),
                StartAddress = StartAddress,
                EndAddress = EndAddress
            };
        }
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
        public byte? TechnicalFieldOffset { get; set; }
        public PartyFieldKind ComparedField { get; set; } = PartyFieldKind.Unknown;
        public PartyEffectOperation Operation { get; set; } = PartyEffectOperation.Unknown;
        public PartyEffectScope Scope { get; set; } = PartyEffectScope.Unknown;
        public PartyConditionKind Condition { get; set; } = PartyConditionKind.None;
        public List<PartyPredicate> GuardPredicates { get; set; } = new List<PartyPredicate>();
        public int? MemberIndex { get; set; }
        public int? ObservedMemberIndex { get; set; }
        public int? ComparedMemberIndex { get; set; }
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
        public int ExecutionOrder { get; set; }
        public int ApplicationCount { get; set; } = 1;
        public string Description { get; set; }
        public bool IsSynchronizedTemporaryMirror { get; set; }

        public PartyEffect Clone()
        {
            return new PartyEffect
            {
                Kind = Kind,
                Field = Field,
                TechnicalFieldOffset = TechnicalFieldOffset,
                ComparedField = ComparedField,
                Operation = Operation,
                Scope = Scope,
                Condition = Condition,
                GuardPredicates = GuardPredicates?.Select(predicate => predicate?.Clone()).Where(predicate => predicate != null).ToList()
                    ?? new List<PartyPredicate>(),
                MemberIndex = MemberIndex,
                ObservedMemberIndex = ObservedMemberIndex,
                ComparedMemberIndex = ComparedMemberIndex,
                IsLoopDerived = IsLoopDerived,
                ValueKnowledge = ValueKnowledge,
                ImmediateValue = ImmediateValue,
                ImmediateRange = ImmediateRange == null ? null : new ValueRange8(ImmediateRange.Min, ImmediateRange.Max),
                InstructionAddress = InstructionAddress,
                ExecutionOrder = ExecutionOrder,
                ApplicationCount = ApplicationCount,
                Description = Description,
                IsSynchronizedTemporaryMirror = IsSynchronizedTemporaryMirror
            };
        }
    }

    public enum BattleSourceIndexBehavior
    {
        Unknown = 0,
        Fixed = 1,
        AdvancesWithLoop = 2,
        ExternalRandom = 3
    }

    public class PartialBattleInfo
    {
        public int BxIndex { get; set; }           // Индекс в массиве сохранения
        public ushort DestAddr { get; set; }       // Адрес назначения (0x3C58 или 0x3C29)
        public string SrcReg { get; set; }         // Исходный регистр (AL, CL, DL, BL)
        public byte SrcRegValue { get; set; }      // Точное или минимальное значение в регистре
        public byte ValueMin { get; set; }         // Нижняя граница сохранённого значения
        public byte ValueMax { get; set; }         // Верхняя граница сохранённого значения
        public bool IsFromTable { get; set; }      // Загружено ли из таблицы
        public ushort? SourceTableAddr { get; set; } // Адрес в таблице-источнике
        public ushort? SourceTableBaseAddr { get; set; } // Базовый адрес семейства таблицы (адрес первой записи)
        public string SourceTable { get; set; }    // Тип таблицы ("CDA9", "CDB1", "CDBD", "CDB5", "CA7F", "CA84")
        public ushort? OriginalSourceIndex { get; set; } // Значение индексного BX в момент чтения из таблицы
        public ushort? SourceIndexProviderAddr { get; set; } // Адрес памяти, из которого был загружен индекс источника (если известен)
        public BattleSourceIndexBehavior SourceIndexBehavior { get; set; } = BattleSourceIndexBehavior.Unknown;
        public bool SourceIndexExternallyDerived { get; set; } // Индекс таблицы зависит от внешнего случайного вызова
        public bool HasExactValue => ValueMin == ValueMax;
        public bool HasRangeValue => ValueMin != ValueMax;
    }
}
