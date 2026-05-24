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
using System.Linq;

namespace MMMapEditor
{
    /// <summary>
    /// Отслеживает состояние регистров x86 и их источники
    /// </summary>
    public class RegisterTracker
    {
        public enum ExternalCallResultKind
        {
            Unknown,
            Random,
            UserInput
        }

        private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
        private Dictionary<string, string> registerSources = new Dictionary<string, string>();
        private Dictionary<string, (ushort addr, bool fromTable, ushort originalBx, string sourceTable, bool sourceIndexExternallyDerived, ushort? sourceIndexProviderAddr)> registerSources2 =
            new Dictionary<string, (ushort, bool, ushort, string, bool, ushort?)>();
        private HashSet<string> externallyDerivedRegisters = new HashSet<string>();
        private HashSet<string> pendingExternalCallRegisters = new HashSet<string>();
        private Dictionary<string, ExternalCallResultKind> pendingExternalCallResultKinds =
            new Dictionary<string, ExternalCallResultKind>();
        private Dictionary<string, ValueRange8> registerRanges = new Dictionary<string, ValueRange8>();
        private Dictionary<string, RegisterValueDistribution> registerRangeDistributions = new Dictionary<string, RegisterValueDistribution>();
        private Dictionary<string, SortedSet<byte>> registerDiscreteValues = new Dictionary<string, SortedSet<byte>>();
        private Dictionary<string, ValueRange8> registerSourceIndexRanges = new Dictionary<string, ValueRange8>();
        private Dictionary<string, SortedSet<byte>> registerSourceIndexValues = new Dictionary<string, SortedSet<byte>>();
        private Dictionary<string, RegisterValueDistribution> registerSourceIndexDistributions = new Dictionary<string, RegisterValueDistribution>();
        private Dictionary<string, byte> registerRandomUpperBounds = new Dictionary<string, byte>();
        private Dictionary<string, PartyMemberReference> partyMemberBases = new Dictionary<string, PartyMemberReference>();
        private Dictionary<string, PartyFieldReference> partyFieldValues = new Dictionary<string, PartyFieldReference>();
        private Dictionary<string, DynamicValueFormulaInfo> dynamicValueFormulas = new Dictionary<string, DynamicValueFormulaInfo>();
        private Dictionary<string, PartyPointerByteReference> partyPointerBytes = new Dictionary<string, PartyPointerByteReference>();
        private Dictionary<string, (ushort sourceAddr, int delta)> memoryByteDeltaSources =
            new Dictionary<string, (ushort, int)>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> coordinateSeedRegisters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> splitMaterializedRegisters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool HasObservedCoordinateSeedRead { get; private set; }

        // Для отслеживания флагов
        public bool ZeroFlag { get; set; }
        public bool CarryFlag { get; set; }
        public bool SignFlag { get; set; }
        public bool OverflowFlag { get; set; }
        public bool FlagsKnown { get; set; }

        public enum FlagsOriginKind
        {
            Unknown,
            CompareImmediate,
            CompareMemory,
            Arithmetic,
            Test
        }

        public string LastFlagsRegister { get; set; }
        public FlagsOriginKind LastFlagsOrigin { get; set; }
        public uint? LastFlagsInstructionAddress { get; set; }
        public byte? LastCompareImmediate { get; set; }
        public byte? LastTestMask { get; set; }
        public ushort? LastComparedMemoryAddress { get; set; }
        public PartyFieldReference LastComparedPartyField { get; set; }
        public bool LastFlagsFromCoordinate { get; set; }
        public bool LastFlagsFromBranchConstraint { get; set; }
        public bool BranchConstraintZeroFlagKnown { get; set; }
        public bool BranchConstraintCarryFlagKnown { get; set; }
        public PartyByteWriteTrace LastPartyByteWrite { get; set; }
        public List<PartyConditionWindow> ActivePartyConditionWindows { get; set; } = new List<PartyConditionWindow>();
        public List<PartyPredicateWindow> ActivePartyPredicateWindows { get; set; } = new List<PartyPredicateWindow>();

        public void SetFlagsMetadata(string register, FlagsOriginKind origin, uint? instructionAddress = null, bool? fromCoordinate = null)
        {
            LastFlagsRegister = register?.ToUpperInvariant();
            LastFlagsOrigin = origin;
            LastFlagsInstructionAddress = instructionAddress;
            LastCompareImmediate = null;
            LastTestMask = null;
            LastComparedMemoryAddress = null;
            LastComparedPartyField = null;
            LastFlagsFromCoordinate = fromCoordinate ?? IsCoordinateSourceForRegister(register);
            LastFlagsFromBranchConstraint = false;
            BranchConstraintZeroFlagKnown = false;
            BranchConstraintCarryFlagKnown = false;
        }

        public void ClearFlagsMetadata()
        {
            LastFlagsRegister = null;
            LastFlagsOrigin = FlagsOriginKind.Unknown;
            LastFlagsInstructionAddress = null;
            LastCompareImmediate = null;
            LastTestMask = null;
            LastComparedMemoryAddress = null;
            LastComparedPartyField = null;
            LastFlagsFromCoordinate = false;
            LastFlagsFromBranchConstraint = false;
            BranchConstraintZeroFlagKnown = false;
            BranchConstraintCarryFlagKnown = false;
        }

        public bool IsCoordinateSourceForRegister(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (regUpper == "BL" || regUpper == "BH" || regUpper == "BX")
                return true;

            ushort? sourceAddr = GetSourceAddress(regUpper);
            return sourceAddr == 0x3C38 || sourceAddr == 0x3C39 || sourceAddr == 0x3C3A;
        }



        public void SetPartyMemberBase(string reg, PartyMemberReference value)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (value == null)
                partyMemberBases.Remove(regUpper);
            else
                partyMemberBases[regUpper] = value.Clone();
        }

        public bool TryGetPartyMemberBase(string reg, out PartyMemberReference value)
        {
            value = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (!partyMemberBases.TryGetValue(regUpper, out var existing) || existing == null)
                return false;

            value = existing.Clone();
            return true;
        }

        public void ClearPartyMemberBase(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(regUpper))
                partyMemberBases.Remove(regUpper);
        }

        public void SetPartyFieldValue(string reg, PartyFieldReference value)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (value == null)
                partyFieldValues.Remove(regUpper);
            else
                partyFieldValues[regUpper] = value.Clone();
        }

        public bool TryGetPartyFieldValue(string reg, out PartyFieldReference value)
        {
            value = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (!partyFieldValues.TryGetValue(regUpper, out var existing) || existing == null)
                return false;

            value = existing.Clone();
            return true;
        }

        public void ClearPartyFieldValue(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(regUpper))
            {
                partyFieldValues.Remove(regUpper);
                ClearDynamicValueFormula(regUpper);
            }
        }

        public void SetDynamicValueFormula(string reg, DynamicValueFormulaInfo value)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (value == null)
            {
                dynamicValueFormulas.Remove(regUpper);
            }
            else
            {
                dynamicValueFormulas[regUpper] = value.Clone();
            }
        }

        public bool TryGetDynamicValueFormula(string reg, out DynamicValueFormulaInfo value)
        {
            value = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (!dynamicValueFormulas.TryGetValue(regUpper, out var existing) || existing == null)
                return false;

            value = existing.Clone();
            return true;
        }

        public void ClearDynamicValueFormula(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            dynamicValueFormulas.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            dynamicValueFormulas.Remove(fullReg);

            if (regUpper == fullReg)
            {
                dynamicValueFormulas.Remove(lowReg);
                dynamicValueFormulas.Remove(highReg);
            }
        }

        public void SetPartyPointerByteValue(string reg, PartyPointerByteReference value)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (value == null)
                partyPointerBytes.Remove(regUpper);
            else
                partyPointerBytes[regUpper] = value.Clone();

            RefreshPartyMemberBaseFromPointerBytes(regUpper);
        }

        public bool TryGetPartyPointerByteValue(string reg, out PartyPointerByteReference value)
        {
            value = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (!partyPointerBytes.TryGetValue(regUpper, out var existing) || existing == null)
                return false;

            value = existing.Clone();
            return true;
        }

        public void ClearPartyPointerByteValue(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            partyPointerBytes.Remove(regUpper);
            RefreshPartyMemberBaseFromPointerBytes(regUpper);
        }

        private static bool TryGetByteRegisterFamily(string regUpper, out string fullReg, out string lowReg, out string highReg)
        {
            fullReg = null;
            lowReg = null;
            highReg = null;

            switch (regUpper)
            {
                case "AL":
                case "AH":
                case "AX":
                    fullReg = "AX";
                    lowReg = "AL";
                    highReg = "AH";
                    return true;
                case "BL":
                case "BH":
                case "BX":
                    fullReg = "BX";
                    lowReg = "BL";
                    highReg = "BH";
                    return true;
                case "CL":
                case "CH":
                case "CX":
                    fullReg = "CX";
                    lowReg = "CL";
                    highReg = "CH";
                    return true;
                case "DL":
                case "DH":
                case "DX":
                    fullReg = "DX";
                    lowReg = "DL";
                    highReg = "DH";
                    return true;
                default:
                    return false;
            }
        }

        private static SortedSet<byte> NormalizeDiscreteValues(IEnumerable<byte> values)
        {
            return values == null
                ? new SortedSet<byte>()
                : new SortedSet<byte>(values);
        }

        private static SortedSet<byte> BuildDiscreteValues(byte min, byte max, RegisterValueDistribution distribution)
        {
            if (max < min)
                return new SortedSet<byte>();

            if (distribution != RegisterValueDistribution.UniformDiscreteRange &&
                distribution != RegisterValueDistribution.EvenDiscreteRange)
            {
                return new SortedSet<byte>();
            }

            var values = new SortedSet<byte>();
            for (int value = min; value <= max; value++)
            {
                if (distribution == RegisterValueDistribution.EvenDiscreteRange &&
                    (value & 1) != 0)
                {
                    continue;
                }

                values.Add((byte)value);
            }

            return values;
        }

        private static SortedSet<byte> CloneDiscreteValues(IEnumerable<byte> values)
        {
            return values == null
                ? null
                : new SortedSet<byte>(values);
        }

        private void SetDiscreteValuesForName(string regUpper, IEnumerable<byte> values)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            var normalized = NormalizeDiscreteValues(values);
            if (normalized.Count == 0)
                registerDiscreteValues.Remove(regUpper);
            else
                registerDiscreteValues[regUpper] = normalized;
        }

        private void SetDiscreteValuesForRangeTargets(string regUpper, IEnumerable<byte> values)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            var normalized = NormalizeDiscreteValues(values);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                SetDiscreteValuesForName(regUpper, normalized);
                return;
            }

            if (regUpper == fullReg || regUpper == lowReg)
            {
                SetDiscreteValuesForName(fullReg, normalized);
                SetDiscreteValuesForName(lowReg, normalized);
                registerDiscreteValues.Remove(highReg);
            }
            else
            {
                SetDiscreteValuesForName(highReg, normalized);
                registerDiscreteValues.Remove(fullReg);
            }
        }

        private void ClearRegisterDiscreteValues(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            registerDiscreteValues.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            registerDiscreteValues.Remove(fullReg);

            if (regUpper == fullReg)
            {
                registerDiscreteValues.Remove(lowReg);
                registerDiscreteValues.Remove(highReg);
            }
            else
            {
                registerDiscreteValues.Remove(regUpper);
                if (regUpper == lowReg)
                    registerDiscreteValues.Remove(fullReg);
            }
        }

        private void ClearRegisterRandomUpperBound(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            registerRandomUpperBounds.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            registerRandomUpperBounds.Remove(fullReg);

            if (regUpper == fullReg)
            {
                registerRandomUpperBounds.Remove(lowReg);
                registerRandomUpperBounds.Remove(highReg);
            }
            else
            {
                registerRandomUpperBounds.Remove(regUpper);
                if (regUpper == lowReg)
                    registerRandomUpperBounds.Remove(fullReg);
            }
        }

        public void SetRegisterRandomUpperBound(string reg, byte upperBound)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (upperBound == 0)
            {
                ClearRegisterRandomUpperBound(regUpper);
                return;
            }

            registerRandomUpperBounds[regUpper] = upperBound;

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out _) &&
                (regUpper == fullReg || regUpper == lowReg))
            {
                registerRandomUpperBounds[fullReg] = upperBound;
                registerRandomUpperBounds[lowReg] = upperBound;
            }
        }

        public bool TryGetRegisterRandomUpperBound(string reg, out byte upperBound)
        {
            upperBound = 0;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (registerRandomUpperBounds.TryGetValue(regUpper, out upperBound))
                return true;

            if ((regUpper == "AL" || regUpper == "AH") && registerRandomUpperBounds.TryGetValue("AX", out upperBound))
                return true;
            if ((regUpper == "BL" || regUpper == "BH") && registerRandomUpperBounds.TryGetValue("BX", out upperBound))
                return true;
            if ((regUpper == "CL" || regUpper == "CH") && registerRandomUpperBounds.TryGetValue("CX", out upperBound))
                return true;
            if ((regUpper == "DL" || regUpper == "DH") && registerRandomUpperBounds.TryGetValue("DX", out upperBound))
                return true;

            return false;
        }

        public bool TryGetRegisterDiscreteValues(string reg, out List<byte> values)
        {
            values = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (registerDiscreteValues.TryGetValue(regUpper, out var existing) &&
                existing != null &&
                existing.Count > 0)
            {
                MarkCoordinateSeedRead(regUpper);
                values = existing.ToList();
                return true;
            }

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                regUpper != fullReg &&
                registerDiscreteValues.TryGetValue(fullReg, out existing) &&
                existing != null &&
                existing.Count > 0)
            {
                MarkCoordinateSeedRead(regUpper);
                values = existing.ToList();
                return true;
            }

            return false;
        }

        private void ClearExactRegisterValuesForRangeTarget(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            registers.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            if (regUpper == fullReg)
            {
                registers.Remove(lowReg);
                registers.Remove(highReg);
            }
            else
            {
                registers.Remove(fullReg);
            }
        }

        private void SetSourceIndexMetadataForName(
            string regUpper,
            ValueRange8 range,
            IEnumerable<byte> values,
            RegisterValueDistribution distribution)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (range == null)
                registerSourceIndexRanges.Remove(regUpper);
            else
                registerSourceIndexRanges[regUpper] = new ValueRange8(range.Min, range.Max);

            var normalized = NormalizeDiscreteValues(values);
            if (normalized.Count == 0)
                registerSourceIndexValues.Remove(regUpper);
            else
                registerSourceIndexValues[regUpper] = normalized;

            if (distribution == RegisterValueDistribution.Unknown)
                registerSourceIndexDistributions.Remove(regUpper);
            else
                registerSourceIndexDistributions[regUpper] = distribution;
        }

        private void SetSourceIndexMetadataForRangeTargets(
            string regUpper,
            ValueRange8 range,
            IEnumerable<byte> values,
            RegisterValueDistribution distribution)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                SetSourceIndexMetadataForName(regUpper, range, values, distribution);
                return;
            }

            if (regUpper == fullReg || regUpper == lowReg)
            {
                SetSourceIndexMetadataForName(fullReg, range, values, distribution);
                SetSourceIndexMetadataForName(lowReg, range, values, distribution);
                registerSourceIndexRanges.Remove(highReg);
                registerSourceIndexValues.Remove(highReg);
                registerSourceIndexDistributions.Remove(highReg);
            }
            else
            {
                SetSourceIndexMetadataForName(highReg, range, values, distribution);
                registerSourceIndexRanges.Remove(fullReg);
                registerSourceIndexValues.Remove(fullReg);
                registerSourceIndexDistributions.Remove(fullReg);
            }
        }

        private void ClearSourceIndexMetadata(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            registerSourceIndexRanges.Remove(regUpper);
            registerSourceIndexValues.Remove(regUpper);
            registerSourceIndexDistributions.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            registerSourceIndexRanges.Remove(fullReg);
            registerSourceIndexValues.Remove(fullReg);
            registerSourceIndexDistributions.Remove(fullReg);

            if (regUpper == fullReg)
            {
                registerSourceIndexRanges.Remove(lowReg);
                registerSourceIndexRanges.Remove(highReg);
                registerSourceIndexValues.Remove(lowReg);
                registerSourceIndexValues.Remove(highReg);
                registerSourceIndexDistributions.Remove(lowReg);
                registerSourceIndexDistributions.Remove(highReg);
            }
            else if (regUpper == lowReg)
            {
                registerSourceIndexRanges.Remove(lowReg);
                registerSourceIndexValues.Remove(lowReg);
                registerSourceIndexDistributions.Remove(lowReg);
            }
            else
            {
                registerSourceIndexRanges.Remove(highReg);
                registerSourceIndexValues.Remove(highReg);
                registerSourceIndexDistributions.Remove(highReg);
            }
        }

        private void SetRegisterSourceIndexMetadata(
            string regUpper,
            ValueRange8 sourceIndexRange,
            IEnumerable<byte> sourceIndexValues,
            RegisterValueDistribution sourceIndexDistribution = RegisterValueDistribution.Unknown)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            ValueRange8 range = sourceIndexRange == null
                ? null
                : new ValueRange8(sourceIndexRange.Min, sourceIndexRange.Max);
            var normalized = NormalizeDiscreteValues(sourceIndexValues);

            if (range == null && normalized.Count == 0)
            {
                ClearSourceIndexMetadata(regUpper);
                return;
            }

            SetSourceIndexMetadataForRangeTargets(regUpper, range, normalized, sourceIndexDistribution);
        }

        public bool TryGetSourceIndexRange(string reg, out ValueRange8 range)
        {
            range = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (registerSourceIndexRanges.TryGetValue(regUpper, out var existing) && existing != null)
            {
                range = new ValueRange8(existing.Min, existing.Max);
                return true;
            }

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                regUpper != fullReg &&
                registerSourceIndexRanges.TryGetValue(fullReg, out existing) &&
                existing != null)
            {
                range = new ValueRange8(existing.Min, existing.Max);
                return true;
            }

            return false;
        }

        public bool TryGetSourceIndexValues(string reg, out List<byte> values)
        {
            values = null;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (registerSourceIndexValues.TryGetValue(regUpper, out var existing) &&
                existing != null &&
                existing.Count > 0)
            {
                values = existing.ToList();
                return true;
            }

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                regUpper != fullReg &&
                registerSourceIndexValues.TryGetValue(fullReg, out existing) &&
                existing != null &&
                existing.Count > 0)
            {
                values = existing.ToList();
                return true;
            }

            return false;
        }

        public bool TryGetSourceIndexDistribution(string reg, out RegisterValueDistribution distribution)
        {
            distribution = RegisterValueDistribution.Unknown;
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (registerSourceIndexDistributions.TryGetValue(regUpper, out distribution))
                return true;

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                regUpper != fullReg &&
                registerSourceIndexDistributions.TryGetValue(fullReg, out distribution))
            {
                return true;
            }

            distribution = RegisterValueDistribution.Unknown;
            return false;
        }

        public void SetRegisterDiscreteValues(
            string reg,
            IEnumerable<byte> values,
            RegisterValueDistribution distribution = RegisterValueDistribution.Unknown)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            var normalized = NormalizeDiscreteValues(values);
            if (normalized.Count == 0)
            {
                ClearRegisterRange(regUpper);
                return;
            }

            SetRegisterRange(regUpper, normalized.Min, normalized.Max, distribution, preserveRandomUpperBound: true);
            SetDiscreteValuesForRangeTargets(regUpper, normalized);
        }

        public void SetRegisterDiscreteValuesWithSource(
            string reg,
            IEnumerable<byte> values,
            RegisterValueDistribution distribution,
            ushort sourceAddr,
            uint address,
            string instruction,
            bool fromTable = false,
            ushort originalBx = 0,
            string sourceTable = null,
            bool sourceIndexExternallyDerived = false,
            ushort? sourceIndexProviderAddr = null,
            ValueRange8 sourceIndexRange = null,
            IEnumerable<byte> sourceIndexValues = null,
            RegisterValueDistribution sourceIndexDistribution = RegisterValueDistribution.Unknown)
        {
            SetRegisterDiscreteValues(reg, values, distribution);

            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            var normalized = NormalizeDiscreteValues(values);
            if (normalized.Count == 0)
                return;

            string tableType = sourceTable;
            if (tableType == null && fromTable)
                tableType = ResolveKnownTableType(sourceAddr);

            var srcInfo = (addr: sourceAddr, fromTable, originalBx, tableType, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            registerSources2[regUpper] = srcInfo;
            registerSources[regUpper] = $"0x{normalized.Min:X2}..0x{normalized.Max:X2} loaded at 0x{address:X4} via {instruction}";
            SetRegisterSourceIndexMetadata(regUpper, sourceIndexRange, sourceIndexValues, sourceIndexDistribution);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _))
            {
                registerSources2[fullReg] = srcInfo;
                registerSources[fullReg] = registerSources[regUpper];
            }
        }

        private static bool CanCombinePartyPointerBytes(PartyPointerByteReference low, PartyPointerByteReference high)
        {
            if (low == null || high == null || low.IsHighByte || !high.IsHighByte)
                return false;

            var lowMember = low.Member;
            var highMember = high.Member;
            if (lowMember == null || highMember == null)
                return false;

            if (lowMember.IsPartyLoopMember && highMember.IsPartyLoopMember)
                return true;

            if (lowMember.MemberIndex.HasValue && highMember.MemberIndex.HasValue)
                return lowMember.MemberIndex.Value == highMember.MemberIndex.Value;

            if (lowMember.PointerTableAddress.HasValue && highMember.PointerTableAddress.HasValue)
                return lowMember.PointerTableAddress.Value == highMember.PointerTableAddress.Value;

            if (lowMember.PointerAddress.HasValue && highMember.PointerAddress.HasValue)
                return lowMember.PointerAddress.Value == highMember.PointerAddress.Value;

            return false;
        }

        private void RefreshPartyMemberBaseFromPointerBytes(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            if (partyPointerBytes.TryGetValue(lowReg, out var lowByte) &&
                partyPointerBytes.TryGetValue(highReg, out var highByte) &&
                CanCombinePartyPointerBytes(lowByte, highByte))
            {
                partyMemberBases[fullReg] = lowByte.Member?.Clone();
            }
            else
            {
                partyMemberBases.Remove(fullReg);
            }
        }

        private void ClearByteRegisterSemantics(string regUpper)
        {
            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _))
                return;

            if (regUpper != fullReg)
            {
                partyPointerBytes.Remove(regUpper);
                dynamicValueFormulas.Remove(regUpper);
                RefreshPartyMemberBaseFromPointerBytes(fullReg);
            }
        }

        private void ClearFullRegisterByteSemantics(string regUpper)
        {
            if (!TryGetByteRegisterFamily(regUpper, out _, out string lowReg, out string highReg))
                return;

            partyPointerBytes.Remove(lowReg);
            partyPointerBytes.Remove(highReg);
            partyFieldValues.Remove(lowReg);
            partyFieldValues.Remove(highReg);
            dynamicValueFormulas.Remove(regUpper);
            dynamicValueFormulas.Remove(lowReg);
            dynamicValueFormulas.Remove(highReg);
        }

        private void ClearCoordinateSeed(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                coordinateSeedRegisters.Remove(regUpper);
                return;
            }

            coordinateSeedRegisters.Remove(fullReg);
            coordinateSeedRegisters.Remove(lowReg);
            coordinateSeedRegisters.Remove(highReg);
        }

        private void ClearRegisterSourceMetadata(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);
            ClearMemoryByteDeltaSource(regUpper);
            ClearSourceIndexMetadata(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            registerSources.Remove(fullReg);
            registerSources2.Remove(fullReg);
            ClearSourceIndexMetadata(fullReg);

            if (regUpper == fullReg)
            {
                registerSources.Remove(lowReg);
                registerSources.Remove(highReg);
                registerSources2.Remove(lowReg);
                registerSources2.Remove(highReg);
                ClearSourceIndexMetadata(lowReg);
                ClearSourceIndexMetadata(highReg);
            }
        }

        private void ClearMemoryByteDeltaSource(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            memoryByteDeltaSources.Remove(regUpper);

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
                return;

            memoryByteDeltaSources.Remove(fullReg);

            if (regUpper == fullReg)
            {
                memoryByteDeltaSources.Remove(lowReg);
                memoryByteDeltaSources.Remove(highReg);
            }
        }

        public void ClearMemoryByteDeltaSourceForRegister(string reg)
        {
            ClearMemoryByteDeltaSource(reg?.ToUpperInvariant());
        }

        public void SetMemoryByteDeltaSource(string reg, ushort sourceAddr, int delta)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (delta == 0)
            {
                ClearMemoryByteDeltaSource(regUpper);
                return;
            }

            memoryByteDeltaSources[regUpper] = (sourceAddr, delta);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                regUpper != fullReg)
            {
                memoryByteDeltaSources[fullReg] = (sourceAddr, delta);
            }
        }

        public bool TryGetMemoryByteDeltaSource(string reg, out ushort sourceAddr, out int delta)
        {
            sourceAddr = 0;
            delta = 0;

            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (memoryByteDeltaSources.TryGetValue(regUpper, out var source))
            {
                sourceAddr = source.sourceAddr;
                delta = source.delta;
                return true;
            }

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _) &&
                memoryByteDeltaSources.TryGetValue(fullReg, out source))
            {
                sourceAddr = source.sourceAddr;
                delta = source.delta;
                return true;
            }

            return false;
        }

        public void MarkRegisterAsCoordinateSeed(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (!TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                coordinateSeedRegisters.Add(regUpper);
                return;
            }

            coordinateSeedRegisters.Add(fullReg);
            coordinateSeedRegisters.Add(lowReg);
            coordinateSeedRegisters.Add(highReg);
        }

        private void MarkCoordinateSeedRead(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            if (coordinateSeedRegisters.Contains(regUpper))
            {
                HasObservedCoordinateSeedRead = true;
                return;
            }

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg) &&
                (coordinateSeedRegisters.Contains(fullReg) ||
                 coordinateSeedRegisters.Contains(lowReg) ||
                 coordinateSeedRegisters.Contains(highReg)))
            {
                HasObservedCoordinateSeedRead = true;
            }
        }

        public void SetRegisterRange(
            string reg,
            byte min,
            byte max,
            RegisterValueDistribution distribution = RegisterValueDistribution.Unknown,
            bool preserveRandomUpperBound = false)
        {
            string regUpper = reg.ToUpperInvariant();
            ClearRegisterDiscreteValues(regUpper);
            if (!preserveRandomUpperBound)
                ClearRegisterRandomUpperBound(regUpper);
            ClearCoordinateSeed(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearPartyFieldValue(regUpper);
            ClearPartyPointerByteValue(regUpper);
            ClearPartyMemberBase(regUpper);
            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);
            ClearSourceIndexMetadata(regUpper);
            ClearMemoryByteDeltaSource(regUpper);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                registerSources.Remove(fullReg);
                registerSources2.Remove(fullReg);
                ClearSourceIndexMetadata(fullReg);
                ClearMemoryByteDeltaSource(fullReg);
                ClearPartyFieldValue(fullReg);
                ClearPartyMemberBase(fullReg);

                if (regUpper == fullReg)
                {
                    registerSources.Remove(lowReg);
                    registerSources.Remove(highReg);
                    registerSources2.Remove(lowReg);
                    registerSources2.Remove(highReg);
                    ClearSourceIndexMetadata(lowReg);
                    ClearSourceIndexMetadata(highReg);
                    ClearMemoryByteDeltaSource(lowReg);
                    ClearMemoryByteDeltaSource(highReg);
                    ClearFullRegisterByteSemantics(fullReg);
                }
                else
                {
                    registerSources.Remove(regUpper);
                    registerSources2.Remove(regUpper);
                    ClearSourceIndexMetadata(regUpper);
                    ClearFullRegisterByteSemantics(fullReg);
                }
            }

            registerRanges[regUpper] = new ValueRange8(min, max);
            registerRangeDistributions[regUpper] = distribution;

            ClearExactRegisterValuesForRangeTarget(regUpper);

            if (TryGetByteRegisterFamily(regUpper, out string rangeFullReg, out string rangeLowReg, out string rangeHighReg))
            {
                if (regUpper == rangeFullReg)
                {
                    registerRanges[rangeLowReg] = new ValueRange8(min, max);
                    registerRangeDistributions[rangeLowReg] = distribution;
                    registers.Remove(rangeFullReg);
                    registers.Remove(rangeLowReg);
                    registers.Remove(rangeHighReg);
                }
                else if (regUpper == rangeLowReg &&
                         registers.TryGetValue(rangeHighReg, out ushort highValue) &&
                         (byte)highValue == 0)
                {
                    registerRanges[rangeFullReg] = new ValueRange8(min, max);
                    registerRangeDistributions[rangeFullReg] = distribution;
                    registers.Remove(rangeFullReg);
                    registers.Remove(rangeLowReg);
                }
            }

            SetDiscreteValuesForRangeTargets(regUpper, BuildDiscreteValues(min, max, distribution));
        }

        public bool TryGetRegisterDistribution(string reg, out RegisterValueDistribution distribution)
        {
            string regUpper = reg.ToUpperInvariant();
            if (registerRangeDistributions.TryGetValue(regUpper, out distribution))
                return true;

            if ((regUpper == "AL" || regUpper == "AH") && registerRangeDistributions.TryGetValue("AX", out distribution))
                return true;

            distribution = RegisterValueDistribution.Unknown;
            return false;
        }

        public bool TryGetRegisterRange(string reg, out ValueRange8 range)
        {
            string regUpper = reg.ToUpperInvariant();
            if (registerRanges.TryGetValue(regUpper, out range))
            {
                MarkCoordinateSeedRead(regUpper);
                return true;
            }

            if ((regUpper == "AL" || regUpper == "AH") && registerRanges.TryGetValue("AX", out range))
            {
                MarkCoordinateSeedRead(regUpper);
                return true;
            }

            range = null;
            return false;
        }

        public void SetRegisterRangeWithSource(
            string reg,
            byte min,
            byte max,
            RegisterValueDistribution distribution,
            ushort sourceAddr,
            uint address,
            string instruction,
            bool fromTable = false,
            ushort originalBx = 0,
            string sourceTable = null,
            bool sourceIndexExternallyDerived = false,
            ushort? sourceIndexProviderAddr = null,
            ValueRange8 sourceIndexRange = null,
            IEnumerable<byte> sourceIndexValues = null,
            RegisterValueDistribution sourceIndexDistribution = RegisterValueDistribution.Unknown)
        {
            SetRegisterRange(reg, min, max, distribution);

            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            string tableType = sourceTable;
            if (tableType == null && fromTable)
                tableType = ResolveKnownTableType(sourceAddr);

            var srcInfo = (addr: sourceAddr, fromTable, originalBx, tableType, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            registerSources2[regUpper] = srcInfo;
            registerSources[regUpper] = $"0x{min:X2}..0x{max:X2} loaded at 0x{address:X4} via {instruction}";
            SetRegisterSourceIndexMetadata(regUpper, sourceIndexRange, sourceIndexValues, sourceIndexDistribution);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _))
            {
                registerSources2[fullReg] = srcInfo;
                registerSources[fullReg] = registerSources[regUpper];
            }
        }

        public void SetRegisterSourceMetadataForExistingValue(
            string reg,
            ushort sourceAddr,
            uint address,
            string instruction,
            bool fromTable = false,
            ushort originalBx = 0,
            string sourceTable = null,
            bool sourceIndexExternallyDerived = false,
            ushort? sourceIndexProviderAddr = null,
            ValueRange8 sourceIndexRange = null,
            IEnumerable<byte> sourceIndexValues = null,
            RegisterValueDistribution sourceIndexDistribution = RegisterValueDistribution.Unknown)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            string tableType = sourceTable;
            if (tableType == null && fromTable)
                tableType = ResolveKnownTableType(sourceAddr);

            var srcInfo = (addr: sourceAddr, fromTable, originalBx, tableType, sourceIndexExternallyDerived, sourceIndexProviderAddr);
            registerSources2[regUpper] = srcInfo;
            registerSources[regUpper] = $"source 0x{sourceAddr:X4} associated at 0x{address:X4} via {instruction}";
            SetRegisterSourceIndexMetadata(regUpper, sourceIndexRange, sourceIndexValues, sourceIndexDistribution);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _))
            {
                registerSources2[fullReg] = srcInfo;
                registerSources[fullReg] = registerSources[regUpper];
            }
        }

        public void ClearRegisterRange(string reg)
        {
            string regUpper = reg.ToUpperInvariant();
            ClearRegisterDiscreteValues(regUpper);
            ClearRegisterRandomUpperBound(regUpper);
            ClearSplitMaterializedRegister(regUpper);
            ClearSourceIndexMetadata(regUpper);
            registerRanges.Remove(regUpper);
            registerRangeDistributions.Remove(regUpper);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                registerRanges.Remove(fullReg);
                registerRanges.Remove(lowReg);
                registerRanges.Remove(highReg);
                registerRangeDistributions.Remove(fullReg);
                registerRangeDistributions.Remove(lowReg);
                registerRangeDistributions.Remove(highReg);
            }
        }

        private void ClearSplitMaterializedRegister(string regUpper)
        {
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            splitMaterializedRegisters.Remove(regUpper);
            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                splitMaterializedRegisters.Remove(fullReg);
                splitMaterializedRegisters.Remove(lowReg);
                splitMaterializedRegisters.Remove(highReg);
            }
        }

        public void MarkRegisterAsSplitMaterialized(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            splitMaterializedRegisters.Add(regUpper);
            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                if (regUpper == fullReg || regUpper == lowReg)
                {
                    splitMaterializedRegisters.Add(fullReg);
                    splitMaterializedRegisters.Add(lowReg);
                }
                else if (regUpper == highReg)
                {
                    splitMaterializedRegisters.Add(highReg);
                }
            }
        }

        public bool IsRegisterSplitMaterialized(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return false;

            if (splitMaterializedRegisters.Contains(regUpper))
                return true;

            return TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg) &&
                   (splitMaterializedRegisters.Contains(fullReg) ||
                    splitMaterializedRegisters.Contains(lowReg) ||
                    splitMaterializedRegisters.Contains(highReg));
        }

        public void SetRegisterValue(string reg, ushort value, uint address, string instruction)
        {
            string regUpper = reg.ToUpper();
            ClearCoordinateSeed(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearRegisterRange(regUpper);
            ClearRegisterSourceMetadata(regUpper);
            ClearPartyMemberBase(regUpper);
            ClearPartyFieldValue(regUpper);
            ClearFullRegisterByteSemantics(regUpper);
            registers[regUpper] = value;
            registerSources[regUpper] = $"0x{value:X4} loaded at 0x{address:X4} via {instruction}";

            if (regUpper == "AX")
            {
                registers["AL"] = (byte)(value & 0xFF);
                registers["AH"] = (byte)(value >> 8);
            }
            else if (regUpper == "CX")
            {
                registers["CL"] = (byte)(value & 0xFF);
                registers["CH"] = (byte)(value >> 8);
            }
            else if (regUpper == "DX")
            {
                registers["DL"] = (byte)(value & 0xFF);
                registers["DH"] = (byte)(value >> 8);
            }
            else if (regUpper == "BX")
            {
                registers["BL"] = (byte)(value & 0xFF);
                registers["BH"] = (byte)(value >> 8);
            }
            else if (regUpper == "CH")
            {
                if (registers.TryGetValue("CX", out ushort cxValue))
                {
                    cxValue = (ushort)((cxValue & 0x00FF) | (value << 8));
                    registers["CX"] = cxValue;
                }
            }
            else if (regUpper == "CL")
            {
                if (registers.TryGetValue("CX", out ushort cxValue))
                {
                    cxValue = (ushort)((cxValue & 0xFF00) | value);
                    registers["CX"] = cxValue;
                }
            }
        }

        private static string ResolveKnownTableType(ushort sourceAddr)
        {
            if (sourceAddr >= 0xCDBD && sourceAddr <= 0xCDC4)
                return "CDBD";
            if (sourceAddr >= 0xCDB5 && sourceAddr <= 0xCDBC)
                return "CDB5";
            if (sourceAddr >= 0xCDA9 && sourceAddr <= 0xCDB0)
                return "CDA9";
            if (sourceAddr >= 0xCDB1 && sourceAddr <= 0xCDB8)
                return "CDB1";
            if (sourceAddr >= 0xCA7F && sourceAddr <= 0xCA83)
                return "CA7F";
            if (sourceAddr >= 0xCA84 && sourceAddr <= 0xCA88)
                return "CA84";

            return "UNKNOWN";
        }

        public void SetRegisterValueWithSource(string reg, ushort value, ushort sourceAddr,
            ushort originalBx, bool fromTable, uint address, string instruction, string sourceTable = null,
            bool sourceIndexExternallyDerived = false, ushort? sourceIndexProviderAddr = null)
        {
            SetRegisterValue(reg, value, address, instruction);

            string tableType = sourceTable;
            if (tableType == null && fromTable)
            {
                tableType = ResolveKnownTableType(sourceAddr);
            }

            registerSources2[reg.ToUpper()] = (sourceAddr, fromTable, originalBx, tableType, sourceIndexExternallyDerived, sourceIndexProviderAddr);
        }

        public bool IsFromTable(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src) && src.fromTable)
                return true;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc) && cxSrc.fromTable)
                return true;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc) && axSrc.fromTable)
                return true;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc) && bxSrc.fromTable)
                return true;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc) && dxSrc.fromTable)
                return true;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2) && bxSrc2.fromTable)
                return true;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2) && cxSrc2.fromTable)
                return true;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2) && dxSrc2.fromTable)
                return true;

            return false;
        }

        public string GetSourceTable(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src))
                return src.sourceTable;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                return cxSrc.sourceTable;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                return axSrc.sourceTable;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc))
                return bxSrc.sourceTable;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc))
                return dxSrc.sourceTable;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2))
                return bxSrc2.sourceTable;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2))
                return cxSrc2.sourceTable;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2))
                return dxSrc2.sourceTable;

            return null;
        }

        public ushort? GetSourceAddress(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src))
                return src.addr;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                return cxSrc.addr;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                return axSrc.addr;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc))
                return bxSrc.addr;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc))
                return dxSrc.addr;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2))
                return bxSrc2.addr;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2))
                return cxSrc2.addr;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2))
                return dxSrc2.addr;

            return null;
        }

        public ushort? GetOriginalBx(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src))
                return src.originalBx;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                return cxSrc.originalBx;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                return axSrc.originalBx;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc))
                return bxSrc.originalBx;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc))
                return dxSrc.originalBx;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2))
                return bxSrc2.originalBx;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2))
                return cxSrc2.originalBx;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2))
                return dxSrc2.originalBx;

            return null;
        }

        public ushort? GetSourceIndexProviderAddress(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src))
                return src.sourceIndexProviderAddr;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                return cxSrc.sourceIndexProviderAddr;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                return axSrc.sourceIndexProviderAddr;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc))
                return bxSrc.sourceIndexProviderAddr;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc))
                return dxSrc.sourceIndexProviderAddr;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2))
                return bxSrc2.sourceIndexProviderAddr;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2))
                return cxSrc2.sourceIndexProviderAddr;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2))
                return dxSrc2.sourceIndexProviderAddr;

            return null;
        }

        public bool GetSourceIndexExternallyDerived(string reg)
        {
            string regUpper = reg.ToUpper();

            if (registerSources2.TryGetValue(regUpper, out var src))
                return src.sourceIndexExternallyDerived;

            if (regUpper == "CL" && registerSources2.TryGetValue("CX", out var cxSrc))
                return cxSrc.sourceIndexExternallyDerived;

            if (regUpper == "AL" && registerSources2.TryGetValue("AX", out var axSrc))
                return axSrc.sourceIndexExternallyDerived;

            if (regUpper == "BL" && registerSources2.TryGetValue("BX", out var bxSrc))
                return bxSrc.sourceIndexExternallyDerived;

            if (regUpper == "DL" && registerSources2.TryGetValue("DX", out var dxSrc))
                return dxSrc.sourceIndexExternallyDerived;

            if (regUpper == "BH" && registerSources2.TryGetValue("BX", out var bxSrc2))
                return bxSrc2.sourceIndexExternallyDerived;

            if (regUpper == "CH" && registerSources2.TryGetValue("CX", out var cxSrc2))
                return cxSrc2.sourceIndexExternallyDerived;

            if (regUpper == "DH" && registerSources2.TryGetValue("DX", out var dxSrc2))
                return dxSrc2.sourceIndexExternallyDerived;

            return false;
        }

        public void MarkRegisterAsPendingExternalCallResult(string reg, ExternalCallResultKind kind = ExternalCallResultKind.Unknown)
        {
            string regUpper = reg.ToUpper();
            AddPendingExternalCallRegister(regUpper, kind);

            if (regUpper == "AX")
            {
                AddPendingExternalCallRegister("AL", kind);
                AddPendingExternalCallRegister("AH", kind);
            }
            else if (regUpper == "BX")
            {
                AddPendingExternalCallRegister("BL", kind);
                AddPendingExternalCallRegister("BH", kind);
            }
            else if (regUpper == "CX")
            {
                AddPendingExternalCallRegister("CL", kind);
                AddPendingExternalCallRegister("CH", kind);
            }
            else if (regUpper == "DX")
            {
                AddPendingExternalCallRegister("DL", kind);
                AddPendingExternalCallRegister("DH", kind);
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                AddPendingExternalCallRegister("AX", kind);
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                AddPendingExternalCallRegister("BX", kind);
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                AddPendingExternalCallRegister("CX", kind);
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                AddPendingExternalCallRegister("DX", kind);
            }
        }

        private void AddPendingExternalCallRegister(string regUpper, ExternalCallResultKind kind)
        {
            pendingExternalCallRegisters.Add(regUpper);
            pendingExternalCallResultKinds[regUpper] = kind;
        }

        public bool HasPendingExternalCallResult(string reg)
        {
            string regUpper = reg.ToUpper();
            if (pendingExternalCallRegisters.Contains(regUpper))
                return true;

            if (regUpper == "AL" || regUpper == "AH")
                return pendingExternalCallRegisters.Contains("AX");
            if (regUpper == "BL" || regUpper == "BH")
                return pendingExternalCallRegisters.Contains("BX");
            if (regUpper == "CL" || regUpper == "CH")
                return pendingExternalCallRegisters.Contains("CX");
            if (regUpper == "DL" || regUpper == "DH")
                return pendingExternalCallRegisters.Contains("DX");

            return false;
        }

        public bool TryGetPendingExternalCallResultKind(string reg, out ExternalCallResultKind kind)
        {
            string regUpper = reg.ToUpper();
            if (pendingExternalCallResultKinds.TryGetValue(regUpper, out kind))
                return true;

            if (regUpper == "AL" || regUpper == "AH")
                return pendingExternalCallResultKinds.TryGetValue("AX", out kind);
            if (regUpper == "BL" || regUpper == "BH")
                return pendingExternalCallResultKinds.TryGetValue("BX", out kind);
            if (regUpper == "CL" || regUpper == "CH")
                return pendingExternalCallResultKinds.TryGetValue("CX", out kind);
            if (regUpper == "DL" || regUpper == "DH")
                return pendingExternalCallResultKinds.TryGetValue("DX", out kind);

            kind = ExternalCallResultKind.Unknown;
            return false;
        }

        public void MaterializePendingExternalCallResult(string reg)
        {
            if (HasPendingExternalCallResult(reg))
            {
                ClearPendingExternalCallResult(reg);
                MarkRegisterAsExternallyDerived(reg);
            }
        }

        public void ClearPendingExternalCallResult(string reg)
        {
            string regUpper = reg.ToUpper();
            RemovePendingExternalCallRegister(regUpper);

            if (regUpper == "AX")
            {
                RemovePendingExternalCallRegister("AL");
                RemovePendingExternalCallRegister("AH");
            }
            else if (regUpper == "BX")
            {
                RemovePendingExternalCallRegister("BL");
                RemovePendingExternalCallRegister("BH");
            }
            else if (regUpper == "CX")
            {
                RemovePendingExternalCallRegister("CL");
                RemovePendingExternalCallRegister("CH");
            }
            else if (regUpper == "DX")
            {
                RemovePendingExternalCallRegister("DL");
                RemovePendingExternalCallRegister("DH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                RemovePendingExternalCallRegister("AL");
                RemovePendingExternalCallRegister("AH");
                RemovePendingExternalCallRegister("AX");
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                RemovePendingExternalCallRegister("BL");
                RemovePendingExternalCallRegister("BH");
                RemovePendingExternalCallRegister("BX");
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                RemovePendingExternalCallRegister("CL");
                RemovePendingExternalCallRegister("CH");
                RemovePendingExternalCallRegister("CX");
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                RemovePendingExternalCallRegister("DL");
                RemovePendingExternalCallRegister("DH");
                RemovePendingExternalCallRegister("DX");
            }
        }

        private void RemovePendingExternalCallRegister(string regUpper)
        {
            pendingExternalCallRegisters.Remove(regUpper);
            pendingExternalCallResultKinds.Remove(regUpper);
        }

        public void MarkRegisterAsExternallyDerived(string reg)
        {
            string regUpper = reg.ToUpper();
            externallyDerivedRegisters.Add(regUpper);

            if (regUpper == "AX")
            {
                externallyDerivedRegisters.Add("AL");
                externallyDerivedRegisters.Add("AH");
            }
            else if (regUpper == "BX")
            {
                externallyDerivedRegisters.Add("BL");
                externallyDerivedRegisters.Add("BH");
            }
            else if (regUpper == "CX")
            {
                externallyDerivedRegisters.Add("CL");
                externallyDerivedRegisters.Add("CH");
            }
            else if (regUpper == "DX")
            {
                externallyDerivedRegisters.Add("DL");
                externallyDerivedRegisters.Add("DH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                externallyDerivedRegisters.Add("AX");
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                externallyDerivedRegisters.Add("BX");
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                externallyDerivedRegisters.Add("CX");
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                externallyDerivedRegisters.Add("DX");
            }
        }

        public bool IsRegisterExternallyDerived(string reg)
        {
            string regUpper = reg.ToUpper();
            if (externallyDerivedRegisters.Contains(regUpper))
                return true;

            if (regUpper == "AL" || regUpper == "AH")
                return externallyDerivedRegisters.Contains("AX");
            if (regUpper == "BL" || regUpper == "BH")
                return externallyDerivedRegisters.Contains("BX");
            if (regUpper == "CL" || regUpper == "CH")
                return externallyDerivedRegisters.Contains("CX");
            if (regUpper == "DL" || regUpper == "DH")
                return externallyDerivedRegisters.Contains("DX");

            return false;
        }

        public void ClearExternalDerivation(string reg)
        {
            string regUpper = reg.ToUpper();
            externallyDerivedRegisters.Remove(regUpper);

            if (regUpper == "AX")
            {
                externallyDerivedRegisters.Remove("AL");
                externallyDerivedRegisters.Remove("AH");
            }
            else if (regUpper == "BX")
            {
                externallyDerivedRegisters.Remove("BL");
                externallyDerivedRegisters.Remove("BH");
            }
            else if (regUpper == "CX")
            {
                externallyDerivedRegisters.Remove("CL");
                externallyDerivedRegisters.Remove("CH");
            }
            else if (regUpper == "DX")
            {
                externallyDerivedRegisters.Remove("DL");
                externallyDerivedRegisters.Remove("DH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                externallyDerivedRegisters.Remove("AL");
                externallyDerivedRegisters.Remove("AH");
                externallyDerivedRegisters.Remove("AX");
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                externallyDerivedRegisters.Remove("BL");
                externallyDerivedRegisters.Remove("BH");
                externallyDerivedRegisters.Remove("BX");
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                externallyDerivedRegisters.Remove("CL");
                externallyDerivedRegisters.Remove("CH");
                externallyDerivedRegisters.Remove("CX");
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                externallyDerivedRegisters.Remove("DL");
                externallyDerivedRegisters.Remove("DH");
                externallyDerivedRegisters.Remove("DX");
            }
        }

        public bool TryGetRegisterValue(string reg, out ushort value)
        {
            string regUpper = reg.ToUpperInvariant();
            bool found = registers.TryGetValue(regUpper, out value);
            if (found)
                MarkCoordinateSeedRead(regUpper);

            return found;
        }

        public bool TryGetByteRegisterValue(string reg, out byte value)
        {
            value = 0;
            string regUpper = reg.ToUpperInvariant();
            if (registers.TryGetValue(regUpper, out ushort fullValue))
            {
                MarkCoordinateSeedRead(regUpper);
                value = (byte)fullValue;
                return true;
            }
            return false;
        }


        public void ClearConcreteByteRegisterValueKeepSemantic(string reg)
        {
            string regUpper = reg?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(regUpper))
                return;

            ClearCoordinateSeed(regUpper);
            registers.Remove(regUpper);
            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);
            ClearMemoryByteDeltaSource(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearRegisterRange(regUpper);

            string fullReg = null;
            string siblingReg = null;

            switch (regUpper)
            {
                case "AL":
                    fullReg = "AX";
                    siblingReg = "AH";
                    break;
                case "AH":
                    fullReg = "AX";
                    siblingReg = "AL";
                    break;
                case "BL":
                    fullReg = "BX";
                    siblingReg = "BH";
                    break;
                case "BH":
                    fullReg = "BX";
                    siblingReg = "BL";
                    break;
                case "CL":
                    fullReg = "CX";
                    siblingReg = "CH";
                    break;
                case "CH":
                    fullReg = "CX";
                    siblingReg = "CL";
                    break;
                case "DL":
                    fullReg = "DX";
                    siblingReg = "DH";
                    break;
                case "DH":
                    fullReg = "DX";
                    siblingReg = "DL";
                    break;
            }

            if (!string.IsNullOrEmpty(fullReg))
            {
                registers.Remove(fullReg);
                registerSources.Remove(fullReg);
                registerSources2.Remove(fullReg);
                ClearMemoryByteDeltaSource(fullReg);
                ClearExternalDerivation(fullReg);
                ClearPendingExternalCallResult(fullReg);
                ClearRegisterRange(fullReg);
            }

            if (!string.IsNullOrEmpty(siblingReg) && registers.ContainsKey(siblingReg))
            {
                // sibling byte may remain known, but full 16-bit register is no longer exact
                registers.Remove(fullReg);
                registerSources.Remove(fullReg);
                registerSources2.Remove(fullReg);
                ClearMemoryByteDeltaSource(fullReg);
            }
        }

        public void InvalidateRegister(string reg)
        {
            string regUpper = reg.ToUpper();

            ClearCoordinateSeed(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearRegisterRange(regUpper);
            ClearPartyFieldValue(regUpper);
            ClearPartyMemberBase(regUpper);
            registers.Remove(regUpper);
            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);
            ClearSourceIndexMetadata(regUpper);
            ClearMemoryByteDeltaSource(regUpper);
            ClearByteRegisterSemantics(regUpper);

            if (regUpper == "AX")
            {
                registers.Remove("AL");
                registers.Remove("AH");
                registerSources.Remove("AL");
                registerSources.Remove("AH");
                registerSources2.Remove("AL");
                registerSources2.Remove("AH");
                ClearSourceIndexMetadata("AX");
                ClearMemoryByteDeltaSource("AX");
                partyFieldValues.Remove("AL");
                partyFieldValues.Remove("AH");
                dynamicValueFormulas.Remove("AL");
                dynamicValueFormulas.Remove("AH");
                partyPointerBytes.Remove("AL");
                partyPointerBytes.Remove("AH");
            }
            else if (regUpper == "BX")
            {
                registers.Remove("BL");
                registers.Remove("BH");
                registerSources.Remove("BL");
                registerSources.Remove("BH");
                registerSources2.Remove("BL");
                registerSources2.Remove("BH");
                ClearSourceIndexMetadata("BX");
                ClearMemoryByteDeltaSource("BX");
                partyFieldValues.Remove("BL");
                partyFieldValues.Remove("BH");
                dynamicValueFormulas.Remove("BL");
                dynamicValueFormulas.Remove("BH");
                partyPointerBytes.Remove("BL");
                partyPointerBytes.Remove("BH");
            }
            else if (regUpper == "CX")
            {
                registers.Remove("CL");
                registers.Remove("CH");
                registerSources.Remove("CL");
                registerSources.Remove("CH");
                registerSources2.Remove("CL");
                registerSources2.Remove("CH");
                ClearSourceIndexMetadata("CX");
                ClearMemoryByteDeltaSource("CX");
                partyFieldValues.Remove("CL");
                partyFieldValues.Remove("CH");
                dynamicValueFormulas.Remove("CL");
                dynamicValueFormulas.Remove("CH");
                partyPointerBytes.Remove("CL");
                partyPointerBytes.Remove("CH");
            }
            else if (regUpper == "DX")
            {
                registers.Remove("DL");
                registers.Remove("DH");
                registerSources.Remove("DL");
                registerSources.Remove("DH");
                registerSources2.Remove("DL");
                registerSources2.Remove("DH");
                ClearSourceIndexMetadata("DX");
                ClearMemoryByteDeltaSource("DX");
                partyFieldValues.Remove("DL");
                partyFieldValues.Remove("DH");
                dynamicValueFormulas.Remove("DL");
                dynamicValueFormulas.Remove("DH");
                partyPointerBytes.Remove("DL");
                partyPointerBytes.Remove("DH");
            }
        }

        public void Clear()
        {
            registers.Clear();
            registerSources.Clear();
            registerSources2.Clear();
            externallyDerivedRegisters.Clear();
            pendingExternalCallRegisters.Clear();
            pendingExternalCallResultKinds.Clear();
            registerRanges.Clear();
            registerRangeDistributions.Clear();
            registerDiscreteValues.Clear();
            registerSourceIndexRanges.Clear();
            registerSourceIndexValues.Clear();
            registerSourceIndexDistributions.Clear();
            registerRandomUpperBounds.Clear();
            partyMemberBases.Clear();
            partyFieldValues.Clear();
            dynamicValueFormulas.Clear();
            partyPointerBytes.Clear();
            memoryByteDeltaSources.Clear();
            ZeroFlag = false;
            CarryFlag = false;
            SignFlag = false;
            OverflowFlag = false;
            FlagsKnown = false;
            LastFlagsRegister = null;
            LastFlagsOrigin = FlagsOriginKind.Unknown;
            LastFlagsInstructionAddress = null;
            LastCompareImmediate = null;
            LastTestMask = null;
            LastComparedMemoryAddress = null;
            LastComparedPartyField = null;
            LastFlagsFromCoordinate = false;
            LastFlagsFromBranchConstraint = false;
            BranchConstraintZeroFlagKnown = false;
            BranchConstraintCarryFlagKnown = false;
            LastPartyByteWrite = null;
            ActivePartyConditionWindows.Clear();
            ActivePartyPredicateWindows.Clear();
        }

        private sealed class ZeroExtendedLowByteSnapshot
        {
            public string FullReg { get; set; }
            public string LowReg { get; set; }
            public string HighReg { get; set; }
            public ValueRange8 Range { get; set; }
            public RegisterValueDistribution Distribution { get; set; }
            public List<byte> DiscreteValues { get; set; }
            public bool HasSourceInfo { get; set; }
            public (ushort addr, bool fromTable, ushort originalBx, string sourceTable, bool sourceIndexExternallyDerived, ushort? sourceIndexProviderAddr) SourceInfo { get; set; }
            public string SourceDescription { get; set; }
            public ValueRange8 SourceIndexRange { get; set; }
            public List<byte> SourceIndexValues { get; set; }
            public RegisterValueDistribution SourceIndexDistribution { get; set; }
            public bool IsExternallyDerived { get; set; }
            public bool HasPendingExternalCallResult { get; set; }
            public ExternalCallResultKind PendingExternalCallKind { get; set; }
            public bool HasRandomUpperBound { get; set; }
            public byte RandomUpperBound { get; set; }
        }

        private ZeroExtendedLowByteSnapshot CaptureZeroExtendedLowByteSnapshot(
            string fullRegUpper,
            string partialRegUpper,
            byte value)
        {
            if (value != 0 ||
                !TryGetByteRegisterFamily(partialRegUpper, out string familyFullReg, out string lowReg, out string highReg) ||
                !string.Equals(fullRegUpper, familyFullReg, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(partialRegUpper, highReg, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            TryGetRegisterRange(lowReg, out var lowRange);
            TryGetRegisterDistribution(lowReg, out var distribution);
            TryGetRegisterDiscreteValues(lowReg, out var discreteValues);
            TryGetSourceIndexRange(lowReg, out var sourceIndexRange);
            TryGetSourceIndexValues(lowReg, out var sourceIndexValues);
            TryGetSourceIndexDistribution(lowReg, out var sourceIndexDistribution);
            bool hasSourceInfo = registerSources2.TryGetValue(lowReg, out var sourceInfo) ||
                                 registerSources2.TryGetValue(fullRegUpper, out sourceInfo);
            registerSources.TryGetValue(lowReg, out string sourceDescription);
            if (sourceDescription == null)
                registerSources.TryGetValue(fullRegUpper, out sourceDescription);

            bool hasRange = lowRange != null;
            bool hasDiscreteValues = discreteValues != null && discreteValues.Count > 0;
            if (!hasRange && !hasDiscreteValues)
                return null;

            bool hasPendingExternalCallResult = TryGetPendingExternalCallResultKind(lowReg, out var pendingKind) ||
                                                TryGetPendingExternalCallResultKind(fullRegUpper, out pendingKind);
            bool hasRandomUpperBound = TryGetRegisterRandomUpperBound(lowReg, out byte randomUpperBound) ||
                                       TryGetRegisterRandomUpperBound(fullRegUpper, out randomUpperBound);

            return new ZeroExtendedLowByteSnapshot
            {
                FullReg = fullRegUpper,
                LowReg = lowReg,
                HighReg = highReg,
                Range = lowRange == null ? null : new ValueRange8(lowRange.Min, lowRange.Max),
                Distribution = distribution,
                DiscreteValues = discreteValues == null ? new List<byte>() : new List<byte>(discreteValues),
                HasSourceInfo = hasSourceInfo,
                SourceInfo = sourceInfo,
                SourceDescription = sourceDescription,
                SourceIndexRange = sourceIndexRange == null ? null : new ValueRange8(sourceIndexRange.Min, sourceIndexRange.Max),
                SourceIndexValues = sourceIndexValues == null ? new List<byte>() : new List<byte>(sourceIndexValues),
                SourceIndexDistribution = sourceIndexDistribution,
                IsExternallyDerived = IsRegisterExternallyDerived(lowReg) || IsRegisterExternallyDerived(fullRegUpper),
                HasPendingExternalCallResult = hasPendingExternalCallResult,
                PendingExternalCallKind = pendingKind,
                HasRandomUpperBound = hasRandomUpperBound,
                RandomUpperBound = randomUpperBound
            };
        }

        private void RestoreZeroExtendedLowByteSnapshot(ZeroExtendedLowByteSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            registers.Remove(snapshot.FullReg);
            registers[snapshot.HighReg] = 0;

            if (snapshot.Range != null)
            {
                registerRanges[snapshot.FullReg] = new ValueRange8(snapshot.Range.Min, snapshot.Range.Max);
                registerRangeDistributions[snapshot.FullReg] = snapshot.Distribution;
                registerRanges[snapshot.LowReg] = new ValueRange8(snapshot.Range.Min, snapshot.Range.Max);
                registerRangeDistributions[snapshot.LowReg] = snapshot.Distribution;
            }

            if (snapshot.DiscreteValues != null && snapshot.DiscreteValues.Count > 0)
                SetDiscreteValuesForRangeTargets(snapshot.LowReg, snapshot.DiscreteValues);
            else if (snapshot.Range != null)
                SetDiscreteValuesForRangeTargets(snapshot.LowReg, BuildDiscreteValues(snapshot.Range.Min, snapshot.Range.Max, snapshot.Distribution));

            if (snapshot.HasSourceInfo)
            {
                registerSources2[snapshot.LowReg] = snapshot.SourceInfo;
                registerSources2[snapshot.FullReg] = snapshot.SourceInfo;

                if (snapshot.SourceDescription != null)
                {
                    registerSources[snapshot.LowReg] = snapshot.SourceDescription;
                    registerSources[snapshot.FullReg] = snapshot.SourceDescription;
                }
            }

            SetRegisterSourceIndexMetadata(
                snapshot.LowReg,
                snapshot.SourceIndexRange,
                snapshot.SourceIndexValues,
                snapshot.SourceIndexDistribution);

            if (snapshot.IsExternallyDerived)
                MarkRegisterAsExternallyDerived(snapshot.LowReg);

            if (snapshot.HasPendingExternalCallResult)
                MarkRegisterAsPendingExternalCallResult(snapshot.LowReg, snapshot.PendingExternalCallKind);

            if (snapshot.HasRandomUpperBound)
                SetRegisterRandomUpperBound(snapshot.LowReg, snapshot.RandomUpperBound);
        }

        public void TrackPartialRegisterOperation(string fullReg, string partialReg,
            byte value, uint address, string instruction, bool preserveSourceMetadata = false)
        {
            string fullRegUpper = fullReg.ToUpper();
            string partialRegUpper = partialReg.ToUpper();

            string mnemonicUpper = string.Empty;
            if (!string.IsNullOrWhiteSpace(instruction))
            {
                int spaceIndex = instruction.IndexOf(' ');
                mnemonicUpper = (spaceIndex >= 0 ? instruction.Substring(0, spaceIndex) : instruction)
                    .Trim()
                    .ToUpperInvariant();
            }

            bool preserveExternalDerivation = IsRegisterExternallyDerived(fullRegUpper) &&
                mnemonicUpper != "MOV";

            ushort currentValue = 0;
            if (registers.TryGetValue(fullRegUpper, out ushort existingValue))
            {
                currentValue = existingValue;
            }

            ZeroExtendedLowByteSnapshot zeroExtendedLowByteSnapshot =
                CaptureZeroExtendedLowByteSnapshot(fullRegUpper, partialRegUpper, value);

            bool preserveZeroExtendedLowByteSource = false;
            (ushort addr, bool fromTable, ushort originalBx, string sourceTable, bool sourceIndexExternallyDerived, ushort? sourceIndexProviderAddr) lowByteSource = default;
            string lowByteSourceDescription = null;
            if (!preserveSourceMetadata &&
                value == 0 &&
                TryGetByteRegisterFamily(partialRegUpper, out string familyFullReg, out string lowReg, out string highReg) &&
                string.Equals(fullRegUpper, familyFullReg, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(partialRegUpper, highReg, StringComparison.OrdinalIgnoreCase) &&
                registerSources2.TryGetValue(lowReg, out lowByteSource))
            {
                preserveZeroExtendedLowByteSource = true;
                registerSources.TryGetValue(lowReg, out lowByteSourceDescription);
            }

            if (!preserveSourceMetadata)
            {
                ClearRegisterSourceMetadata(partialRegUpper);
                if (preserveZeroExtendedLowByteSource)
                {
                    registerSources2[fullRegUpper] = lowByteSource;
                    if (lowByteSourceDescription != null)
                        registerSources[fullRegUpper] = lowByteSourceDescription;
                }
            }
            // Сохраняем информацию об источнике для полного регистра
            else if (registerSources2.TryGetValue(partialRegUpper, out var srcInfo))
            {
                // Если у нас есть информация о частичном регистре, сохраняем её для полного
                if (!registerSources2.ContainsKey(fullRegUpper))
                {
                    registerSources2[fullRegUpper] = srcInfo;
                }
            }

            void ClearSemanticsAfterByteWrite()
            {
                ClearPartyFieldValue(partialRegUpper);
                ClearPartyFieldValue(fullRegUpper);
                ClearPartyMemberBase(fullRegUpper);
                ClearPartyPointerByteValue(partialRegUpper);
            }

            if (partialRegUpper == "AL" || partialRegUpper == "AH")
            {
                if (fullRegUpper == "AX")
                {
                    if (partialRegUpper == "AL")
                    {
                        currentValue = (ushort)((currentValue & 0xFF00) | value);
                    }
                    else if (partialRegUpper == "AH")
                    {
                        currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                    }
                    if (!preserveExternalDerivation)
                    {
                        ClearCoordinateSeed(partialRegUpper);
                        ClearExternalDerivation(fullRegUpper);
                        ClearPendingExternalCallResult(fullRegUpper);
                        ClearRegisterRange(fullRegUpper);
                    }
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
                    ClearSemanticsAfterByteWrite();
                    RestoreZeroExtendedLowByteSnapshot(zeroExtendedLowByteSnapshot);
                }
            }
            else if (partialRegUpper == "CL" || partialRegUpper == "CH")
            {
                if (fullRegUpper == "CX")
                {
                    if (partialRegUpper == "CL")
                    {
                        currentValue = (ushort)((currentValue & 0xFF00) | value);
                    }
                    else if (partialRegUpper == "CH")
                    {
                        currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                    }
                    if (!preserveExternalDerivation)
                    {
                        ClearCoordinateSeed(partialRegUpper);
                        ClearExternalDerivation(fullRegUpper);
                        ClearPendingExternalCallResult(fullRegUpper);
                        ClearRegisterRange(fullRegUpper);
                    }
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
                    ClearSemanticsAfterByteWrite();
                    RestoreZeroExtendedLowByteSnapshot(zeroExtendedLowByteSnapshot);
                }
            }
            else if (partialRegUpper == "DL" || partialRegUpper == "DH")
            {
                if (fullRegUpper == "DX")
                {
                    if (partialRegUpper == "DL")
                    {
                        currentValue = (ushort)((currentValue & 0xFF00) | value);
                    }
                    else if (partialRegUpper == "DH")
                    {
                        currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                    }
                    if (!preserveExternalDerivation)
                    {
                        ClearCoordinateSeed(partialRegUpper);
                        ClearExternalDerivation(fullRegUpper);
                        ClearPendingExternalCallResult(fullRegUpper);
                        ClearRegisterRange(fullRegUpper);
                    }
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
                    ClearSemanticsAfterByteWrite();
                    RestoreZeroExtendedLowByteSnapshot(zeroExtendedLowByteSnapshot);
                }
            }
            else if (partialRegUpper == "BL" || partialRegUpper == "BH")
            {
                if (fullRegUpper == "BX")
                {
                    if (partialRegUpper == "BL")
                    {
                        currentValue = (ushort)((currentValue & 0xFF00) | value);
                    }
                    else if (partialRegUpper == "BH")
                    {
                        currentValue = (ushort)((currentValue & 0x00FF) | (value << 8));
                    }
                    if (!preserveExternalDerivation)
                    {
                        ClearCoordinateSeed(partialRegUpper);
                        ClearExternalDerivation(fullRegUpper);
                        ClearPendingExternalCallResult(fullRegUpper);
                        ClearRegisterRange(fullRegUpper);
                    }
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
                    ClearSemanticsAfterByteWrite();
                    RestoreZeroExtendedLowByteSnapshot(zeroExtendedLowByteSnapshot);
                }
            }
        }

        public void SetByteRegisterValueWithSource(
            string fullReg,
            string partialReg,
            byte value,
            ushort sourceAddr,
            uint address,
            string instruction,
            bool fromTable = false,
            ushort originalBx = 0,
            string sourceTable = null,
            bool sourceIndexExternallyDerived = false,
            ushort? sourceIndexProviderAddr = null)
        {
            TrackPartialRegisterOperation(fullReg, partialReg, value, address, instruction);

            string fullRegUpper = fullReg?.ToUpperInvariant();
            string partialRegUpper = partialReg?.ToUpperInvariant();

            string tableType = sourceTable;
            if (tableType == null && fromTable)
                tableType = ResolveKnownTableType(sourceAddr);

            var srcInfo = (addr: sourceAddr, fromTable, originalBx, tableType, sourceIndexExternallyDerived, sourceIndexProviderAddr);

            if (!string.IsNullOrWhiteSpace(partialRegUpper))
                registerSources2[partialRegUpper] = srcInfo;

            if (!string.IsNullOrWhiteSpace(fullRegUpper))
                registerSources2[fullRegUpper] = srcInfo;
        }

        public RegisterTracker Clone()
        {
            var clone = new RegisterTracker();
            foreach (var kvp in registers)
            {
                clone.registers[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in registerSources)
            {
                clone.registerSources[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in registerSources2)
            {
                clone.registerSources2[kvp.Key] = kvp.Value;
            }
            foreach (var reg in externallyDerivedRegisters)
            {
                clone.externallyDerivedRegisters.Add(reg);
            }
            foreach (var reg in pendingExternalCallRegisters)
            {
                clone.pendingExternalCallRegisters.Add(reg);
            }
            foreach (var kvp in pendingExternalCallResultKinds)
            {
                clone.pendingExternalCallResultKinds[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in registerRanges)
            {
                clone.registerRanges[kvp.Key] = new ValueRange8(kvp.Value.Min, kvp.Value.Max);
            }
            foreach (var kvp in registerRangeDistributions)
            {
                clone.registerRangeDistributions[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in registerDiscreteValues)
            {
                clone.registerDiscreteValues[kvp.Key] = CloneDiscreteValues(kvp.Value);
            }
            foreach (var kvp in registerSourceIndexRanges)
            {
                clone.registerSourceIndexRanges[kvp.Key] = kvp.Value == null ? null : new ValueRange8(kvp.Value.Min, kvp.Value.Max);
            }
            foreach (var kvp in registerSourceIndexValues)
            {
                clone.registerSourceIndexValues[kvp.Key] = CloneDiscreteValues(kvp.Value);
            }
            foreach (var kvp in registerSourceIndexDistributions)
            {
                clone.registerSourceIndexDistributions[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in registerRandomUpperBounds)
            {
                clone.registerRandomUpperBounds[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in partyMemberBases)
            {
                clone.partyMemberBases[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in partyFieldValues)
            {
                clone.partyFieldValues[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in dynamicValueFormulas)
            {
                clone.dynamicValueFormulas[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in partyPointerBytes)
            {
                clone.partyPointerBytes[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in memoryByteDeltaSources)
            {
                clone.memoryByteDeltaSources[kvp.Key] = kvp.Value;
            }
            foreach (var reg in coordinateSeedRegisters)
            {
                clone.coordinateSeedRegisters.Add(reg);
            }
            foreach (var reg in splitMaterializedRegisters)
            {
                clone.splitMaterializedRegisters.Add(reg);
            }
            clone.ZeroFlag = this.ZeroFlag;
            clone.CarryFlag = this.CarryFlag;
            clone.SignFlag = this.SignFlag;
            clone.OverflowFlag = this.OverflowFlag;
            clone.FlagsKnown = this.FlagsKnown;
            clone.LastFlagsRegister = this.LastFlagsRegister;
            clone.LastFlagsOrigin = this.LastFlagsOrigin;
            clone.LastFlagsInstructionAddress = this.LastFlagsInstructionAddress;
            clone.LastCompareImmediate = this.LastCompareImmediate;
            clone.LastTestMask = this.LastTestMask;
            clone.LastComparedMemoryAddress = this.LastComparedMemoryAddress;
            clone.LastComparedPartyField = this.LastComparedPartyField?.Clone();
            clone.LastFlagsFromCoordinate = this.LastFlagsFromCoordinate;
            clone.LastFlagsFromBranchConstraint = this.LastFlagsFromBranchConstraint;
            clone.BranchConstraintZeroFlagKnown = this.BranchConstraintZeroFlagKnown;
            clone.BranchConstraintCarryFlagKnown = this.BranchConstraintCarryFlagKnown;
            clone.LastPartyByteWrite = this.LastPartyByteWrite?.Clone();
            clone.HasObservedCoordinateSeedRead = this.HasObservedCoordinateSeedRead;
            clone.ActivePartyConditionWindows = this.ActivePartyConditionWindows?
                .Select(window => window?.Clone())
                .Where(window => window != null)
                .ToList() ?? new List<PartyConditionWindow>();
            clone.ActivePartyPredicateWindows = this.ActivePartyPredicateWindows?
                .Select(window => window?.Clone())
                .Where(window => window != null)
                .ToList() ?? new List<PartyPredicateWindow>();
            return clone;
        }
    }
}
