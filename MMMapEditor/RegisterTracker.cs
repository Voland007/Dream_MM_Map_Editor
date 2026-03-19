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

using System.Collections.Generic;

namespace MMMapEditor
{
    /// <summary>
    /// Отслеживает состояние регистров x86 и их источники
    /// </summary>
    public class RegisterTracker
    {
        private Dictionary<string, ushort> registers = new Dictionary<string, ushort>();
        private Dictionary<string, string> registerSources = new Dictionary<string, string>();
        private Dictionary<string, (ushort addr, bool fromTable, ushort originalBx, string sourceTable)> registerSources2 =
            new Dictionary<string, (ushort, bool, ushort, string)>();

        // Для отслеживания флагов
        public bool ZeroFlag { get; set; }
        public bool CarryFlag { get; set; }
        public bool SignFlag { get; set; }
        public bool OverflowFlag { get; set; }
        public bool FlagsKnown { get; set; }

        public void SetRegisterValue(string reg, ushort value, uint address, string instruction)
        {
            string regUpper = reg.ToUpper();
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

        public void SetRegisterValueWithSource(string reg, ushort value, ushort sourceAddr,
            ushort originalBx, bool fromTable, uint address, string instruction, string sourceTable = null)
        {
            SetRegisterValue(reg, value, address, instruction);

            string tableType = sourceTable;
            if (tableType == null && fromTable)
            {
                // Определяем тип таблицы по адресу
                if (sourceAddr >= 0xCDBD && sourceAddr <= 0xCDC4)
                    tableType = "CDBD";
                else if (sourceAddr >= 0xCDB5 && sourceAddr <= 0xCDBC)
                    tableType = "CDB5";
                else if (sourceAddr >= 0xCDA9 && sourceAddr <= 0xCDB0)
                    tableType = "CDA9";
                else if (sourceAddr >= 0xCDB1 && sourceAddr <= 0xCDB8)
                    tableType = "CDB1";
                else
                    tableType = "UNKNOWN";
            }

            registerSources2[reg.ToUpper()] = (sourceAddr, fromTable, originalBx, tableType);
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

        public bool TryGetRegisterValue(string reg, out ushort value)
        {
            return registers.TryGetValue(reg.ToUpper(), out value);
        }

        public bool TryGetByteRegisterValue(string reg, out byte value)
        {
            value = 0;
            if (registers.TryGetValue(reg.ToUpper(), out ushort fullValue))
            {
                value = (byte)fullValue;
                return true;
            }
            return false;
        }

        public void InvalidateRegister(string reg)
        {
            string regUpper = reg.ToUpper();

            registers.Remove(regUpper);
            registerSources.Remove(regUpper);
            registerSources2.Remove(regUpper);

            if (regUpper == "AX")
            {
                registers.Remove("AL");
                registers.Remove("AH");
                registerSources.Remove("AL");
                registerSources.Remove("AH");
                registerSources2.Remove("AL");
                registerSources2.Remove("AH");
            }
            else if (regUpper == "BX")
            {
                registers.Remove("BL");
                registers.Remove("BH");
                registerSources.Remove("BL");
                registerSources.Remove("BH");
                registerSources2.Remove("BL");
                registerSources2.Remove("BH");
            }
            else if (regUpper == "CX")
            {
                registers.Remove("CL");
                registers.Remove("CH");
                registerSources.Remove("CL");
                registerSources.Remove("CH");
                registerSources2.Remove("CL");
                registerSources2.Remove("CH");
            }
            else if (regUpper == "DX")
            {
                registers.Remove("DL");
                registers.Remove("DH");
                registerSources.Remove("DL");
                registerSources.Remove("DH");
                registerSources2.Remove("DL");
                registerSources2.Remove("DH");
            }
        }

        public void Clear()
        {
            registers.Clear();
            registerSources.Clear();
            registerSources2.Clear();
            ZeroFlag = false;
            CarryFlag = false;
            SignFlag = false;
            OverflowFlag = false;
            FlagsKnown = false;
        }

        public void TrackPartialRegisterOperation(string fullReg, string partialReg,
            byte value, uint address, string instruction)
        {
            string fullRegUpper = fullReg.ToUpper();
            string partialRegUpper = partialReg.ToUpper();

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
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
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
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
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
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
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
                    registers[fullRegUpper] = currentValue;
                    registers[partialRegUpper] = value;
                }
            }
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
            clone.ZeroFlag = this.ZeroFlag;
            clone.CarryFlag = this.CarryFlag;
            clone.SignFlag = this.SignFlag;
            clone.OverflowFlag = this.OverflowFlag;
            clone.FlagsKnown = this.FlagsKnown;
            return clone;
        }
    }
}