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
using System.Globalization;

namespace MMMapEditor
{
    internal static class ProbabilityFormatter
    {
        private const string PercentFormat = "0.###";

        public static string FormatPercent(int numerator, int denominator)
        {
            numerator = Math.Max(0, numerator);
            denominator = Math.Max(1, denominator);

            decimal percent = 100m * numerator / denominator;
            return FormatPercent(percent);
        }

        public static string FormatPercent(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent))
                return "0";

            return FormatPercent((decimal)percent);
        }

        private static string FormatPercent(decimal percent)
        {
            return percent
                .ToString(PercentFormat, CultureInfo.InvariantCulture)
                .Replace('.', ',');
        }
    }
}
