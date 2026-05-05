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
using System.Threading;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;

namespace MMMapEditor
{
    internal static class CapstoneX86Disassembly
    {
        private static readonly object CreateSync = new object();
        private static readonly object SerialDisassembleSync = new object();
        private static readonly ThreadLocal<CapstoneX86Disassembler> ThreadDisassembler =
            new ThreadLocal<CapstoneX86Disassembler>(CreateDisassembler, trackAllValues: false);
        private static readonly Lazy<bool> ForceSerialDisassembly =
            new Lazy<bool>(DetermineForceSerialDisassembly, LazyThreadSafetyMode.ExecutionAndPublication);

        public static X86Instruction[] Disassemble(byte[] code, long address)
        {
            if (code == null || code.Length == 0)
                return Array.Empty<X86Instruction>();

            if (ForceSerialDisassembly.Value)
            {
                lock (SerialDisassembleSync)
                    return ThreadDisassembler.Value.Disassemble(code, address);
            }

            return ThreadDisassembler.Value.Disassemble(code, address);
        }

        private static CapstoneX86Disassembler CreateDisassembler()
        {
            lock (CreateSync)
            {
                var disassembler = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit16);
                disassembler.DisassembleSyntax = DisassembleSyntax.Intel;
                return disassembler;
            }
        }

        private static bool DetermineForceSerialDisassembly()
        {
            string configuredValue = Environment.GetEnvironmentVariable("MMMAPEDITOR_CAPSTONE_SERIAL");
            return string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configuredValue, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
