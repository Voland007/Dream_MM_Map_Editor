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
        private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
        private Dictionary<string, string> registerSources = new Dictionary<string, string>();
        private Dictionary<string, (ushort addr, bool fromTable, ushort originalBx, string sourceTable, bool sourceIndexExternallyDerived, ushort? sourceIndexProviderAddr)> registerSources2 =
            new Dictionary<string, (ushort, bool, ushort, string, bool, ushort?)>();
        private HashSet<string> externallyDerivedRegisters = new HashSet<string>();
        private HashSet<string> pendingExternalCallRegisters = new HashSet<string>();
        private Dictionary<string, ValueRange8> registerRanges = new Dictionary<string, ValueRange8>();
        private Dictionary<string, RegisterValueDistribution> registerRangeDistributions = new Dictionary<string, RegisterValueDistribution>();
        private Dictionary<string, PartyMemberReference> partyMemberBases = new Dictionary<string, PartyMemberReference>();
        private Dictionary<string, PartyFieldReference> partyFieldValues = new Dictionary<string, PartyFieldReference>();
        private Dictionary<string, PartyPointerByteReference> partyPointerBytes = new Dictionary<string, PartyPointerByteReference>();
        private HashSet<string> coordinateSeedRegisters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                partyFieldValues.Remove(regUpper);
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

        public void SetRegisterRange(string reg, byte min, byte max, RegisterValueDistribution distribution = RegisterValueDistribution.Unknown)
        {
            string regUpper = reg.ToUpperInvariant();
            ClearCoordinateSeed(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearPartyFieldValue(regUpper);
            ClearPartyPointerByteValue(regUpper);
            ClearPartyMemberBase(regUpper);
            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out string lowReg, out string highReg))
            {
                registerSources.Remove(fullReg);
                registerSources2.Remove(fullReg);
                ClearPartyFieldValue(fullReg);
                ClearPartyMemberBase(fullReg);

                if (regUpper == fullReg)
                {
                    registerSources.Remove(lowReg);
                    registerSources.Remove(highReg);
                    registerSources2.Remove(lowReg);
                    registerSources2.Remove(highReg);
                    ClearFullRegisterByteSemantics(fullReg);
                }
                else
                {
                    registerSources.Remove(regUpper);
                    registerSources2.Remove(regUpper);
                    ClearFullRegisterByteSemantics(fullReg);
                }
            }

            registerRanges[regUpper] = new ValueRange8(min, max);
            registerRangeDistributions[regUpper] = distribution;

            registers.Remove(regUpper);

            if (regUpper == "AX")
            {
                registerRanges["AX"] = new ValueRange8(min, max);
                registerRanges["AL"] = new ValueRange8(min, max);
                registerRangeDistributions["AX"] = distribution;
                registerRangeDistributions["AL"] = distribution;

                registers.Remove("AX");
                registers.Remove("AL");
                registers.Remove("AH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                registerRanges["AX"] = new ValueRange8(min, max);
                registerRanges["AL"] = new ValueRange8(min, max);
                registerRangeDistributions["AX"] = distribution;
                registerRangeDistributions["AL"] = distribution;

                registers.Remove("AX");
                registers.Remove("AL");
                registers.Remove("AH");
            }
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
            ushort? sourceIndexProviderAddr = null)
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

            if (TryGetByteRegisterFamily(regUpper, out string fullReg, out _, out _))
            {
                registerSources2[fullReg] = srcInfo;
                registerSources[fullReg] = registerSources[regUpper];
            }
        }

        public void ClearRegisterRange(string reg)
        {
            string regUpper = reg.ToUpperInvariant();
            registerRanges.Remove(regUpper);
            registerRangeDistributions.Remove(regUpper);

            if (regUpper == "AX")
            {
                registerRanges.Remove("AL");
                registerRanges.Remove("AH");
                registerRangeDistributions.Remove("AX");
                registerRangeDistributions.Remove("AL");
                registerRangeDistributions.Remove("AH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                registerRanges.Remove("AX");
                registerRanges.Remove("AL");
                registerRanges.Remove("AH");
                registerRangeDistributions.Remove("AX");
                registerRangeDistributions.Remove("AL");
                registerRangeDistributions.Remove("AH");
                registerRangeDistributions.Remove("AX");
                registerRangeDistributions.Remove("AL");
                registerRangeDistributions.Remove("AH");
            }
        }

        public void SetRegisterValue(string reg, ushort value, uint address, string instruction)
        {
            string regUpper = reg.ToUpper();
            ClearCoordinateSeed(regUpper);
            ClearExternalDerivation(regUpper);
            ClearPendingExternalCallResult(regUpper);
            ClearRegisterRange(regUpper);
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

        public void MarkRegisterAsPendingExternalCallResult(string reg)
        {
            string regUpper = reg.ToUpper();
            pendingExternalCallRegisters.Add(regUpper);

            if (regUpper == "AX")
            {
                pendingExternalCallRegisters.Add("AL");
                pendingExternalCallRegisters.Add("AH");
            }
            else if (regUpper == "BX")
            {
                pendingExternalCallRegisters.Add("BL");
                pendingExternalCallRegisters.Add("BH");
            }
            else if (regUpper == "CX")
            {
                pendingExternalCallRegisters.Add("CL");
                pendingExternalCallRegisters.Add("CH");
            }
            else if (regUpper == "DX")
            {
                pendingExternalCallRegisters.Add("DL");
                pendingExternalCallRegisters.Add("DH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                pendingExternalCallRegisters.Add("AX");
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                pendingExternalCallRegisters.Add("BX");
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                pendingExternalCallRegisters.Add("CX");
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                pendingExternalCallRegisters.Add("DX");
            }
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
            pendingExternalCallRegisters.Remove(regUpper);

            if (regUpper == "AX")
            {
                pendingExternalCallRegisters.Remove("AL");
                pendingExternalCallRegisters.Remove("AH");
            }
            else if (regUpper == "BX")
            {
                pendingExternalCallRegisters.Remove("BL");
                pendingExternalCallRegisters.Remove("BH");
            }
            else if (regUpper == "CX")
            {
                pendingExternalCallRegisters.Remove("CL");
                pendingExternalCallRegisters.Remove("CH");
            }
            else if (regUpper == "DX")
            {
                pendingExternalCallRegisters.Remove("DL");
                pendingExternalCallRegisters.Remove("DH");
            }
            else if (regUpper == "AL" || regUpper == "AH")
            {
                pendingExternalCallRegisters.Remove("AL");
                pendingExternalCallRegisters.Remove("AH");
                pendingExternalCallRegisters.Remove("AX");
            }
            else if (regUpper == "BL" || regUpper == "BH")
            {
                pendingExternalCallRegisters.Remove("BL");
                pendingExternalCallRegisters.Remove("BH");
                pendingExternalCallRegisters.Remove("BX");
            }
            else if (regUpper == "CL" || regUpper == "CH")
            {
                pendingExternalCallRegisters.Remove("CL");
                pendingExternalCallRegisters.Remove("CH");
                pendingExternalCallRegisters.Remove("CX");
            }
            else if (regUpper == "DL" || regUpper == "DH")
            {
                pendingExternalCallRegisters.Remove("DL");
                pendingExternalCallRegisters.Remove("DH");
                pendingExternalCallRegisters.Remove("DX");
            }
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
            ClearByteRegisterSemantics(regUpper);

            if (regUpper == "AX")
            {
                registers.Remove("AL");
                registers.Remove("AH");
                registerSources.Remove("AL");
                registerSources.Remove("AH");
                registerSources2.Remove("AL");
                registerSources2.Remove("AH");
                partyFieldValues.Remove("AL");
                partyFieldValues.Remove("AH");
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
                partyFieldValues.Remove("BL");
                partyFieldValues.Remove("BH");
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
                partyFieldValues.Remove("CL");
                partyFieldValues.Remove("CH");
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
                partyFieldValues.Remove("DL");
                partyFieldValues.Remove("DH");
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
            registerRanges.Clear();
            registerRangeDistributions.Clear();
            partyMemberBases.Clear();
            partyFieldValues.Clear();
            partyPointerBytes.Clear();
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

        public void TrackPartialRegisterOperation(string fullReg, string partialReg,
            byte value, uint address, string instruction)
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

            // Сохраняем информацию об источнике для полного регистра
            if (registerSources2.TryGetValue(partialRegUpper, out var srcInfo))
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
            foreach (var kvp in registerRanges)
            {
                clone.registerRanges[kvp.Key] = new ValueRange8(kvp.Value.Min, kvp.Value.Max);
            }
            foreach (var kvp in registerRangeDistributions)
            {
                clone.registerRangeDistributions[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in partyMemberBases)
            {
                clone.partyMemberBases[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in partyFieldValues)
            {
                clone.partyFieldValues[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var kvp in partyPointerBytes)
            {
                clone.partyPointerBytes[kvp.Key] = kvp.Value?.Clone();
            }
            foreach (var reg in coordinateSeedRegisters)
            {
                clone.coordinateSeedRegisters.Add(reg);
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
