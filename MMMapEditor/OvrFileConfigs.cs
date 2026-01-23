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


﻿using System.Collections.Generic;

namespace MMMapEditor
{
    public static class OvrFileConfigs
    {
        // Словарь, хранящий настройки для каждого файла
        public static Dictionary<string, OvrFileConfig> Configs = new Dictionary<string, OvrFileConfig>
        {
            ["SORPIGAL.OVR"] = new OvrFileConfig
            {
                StartAddress = 0x386,
                First16Lines = new[]
                    {
                "65 06 4C 44 8C 44 4C 04 0C 44 34 15 15 15 45 34",
                "55 11 05 54 19 45 14 43 70 15 11 71 33 41 54 11",
                "CD 30 11 47 00 74 01 44 44 50 C1 44 00 44 C4 70",
                "55 11 41 14 D1 55 11 47 04 74 15 55 33 05 44 54",
                "CD 00 DC 01 04 44 40 54 91 45 40 14 11 11 0D 54",
                "55 11 05 40 10 07 24 46 08 64 D6 11 33 11 03 54",
                "CD 30 51 95 11 43 50 15 13 45 44 50 11 51 01 54",
                "15 41 C4 18 01 44 44 10 C1 44 4C 44 C0 44 10 15",
                "41 04 14 11 11 0D 1C 01 44 44 44 44 44 14 31 11",
                "37 41 50 33 51 41 90 51 65 26 56 65 16 51 11 51",
                "41 0C 4C 40 C4 44 88 44 44 00 C4 C4 40 84 40 3C",
                "55 21 56 15 05 54 19 45 14 11 45 14 07 18 15 11",
                "65 12 45 10 11 87 00 B4 11 01 DC 11 C3 D0 11 11",
                "55 21 56 11 11 59 D1 59 11 13 45 40 44 04 50 33",
                "65 12 45 10 41 44 44 44 50 01 64 06 1C 11 55 11",
                "55 61 56 51 45 64 46 44 54 D1 55 41 70 41 54 51"
            },
                Second16Lines = new[]
                   {
                "C5 05 44 44 84 44 44 04 04 44 14 15 95 15 C5 14",
                "15 11 05 54 91 45 14 C1 50 15 11 11 11 01 54 11",
                "81 10 11 45 00 14 01 44 44 10 41 44 00 44 44 50",
                "51 11 41 14 51 DB 11 45 84 54 14 C5 10 01 44 54",
                "85 00 D4 01 04 44 40 54 11 45 40 14 11 11 05 54",
                "51 11 05 40 10 05 84 44 80 44 D4 11 11 11 01 54",
                "C5 10 51 95 11 41 50 15 11 45 44 50 11 51 01 54",
                "11 41 44 14 01 44 44 10 41 44 44 44 40 44 10 14",
                "41 04 14 11 11 85 14 01 44 44 44 44 44 14 11 11",
                "95 41 50 11 51 41 90 51 45 15 54 45 14 51 11 51",
                "41 04 44 40 44 44 C4 44 44 00 44 44 40 84 40 14",
                "95 11 54 11 29 FC 11 ED 3C 11 45 14 05 90 11 11",
                "11 10 45 10 39 05 80 14 39 01 D4 11 41 50 11 11",
                "45 11 54 11 39 5D 51 4D B9 11 45 40 44 04 50 11",
                "11 10 45 10 69 6C 6C 2C 78 81 44 84 14 11 C5 10",
                "D1 41 D4 51 C1 C4 44 44 54 51 54 41 50 41 54 51"
            },
                TextBaseAddr = 0xC5EC,
                PatchBase = 0x0B7F
            },

            ["PORTSMIT.OVR"] = new OvrFileConfig
            {
                StartAddress = 0x412,
                First16Lines = new[]
                   {
"55 15 55 15 55 15 55 15 55 15 55 15 55 15 55 15",
"55 11 55 11 55 11 55 11 55 11 55 11 55 11 55 11",
"55 33 55 33 55 33 55 33 55 33 55 33 55 33 55 33",
"4D 00 4C 40 4C 00 4C 40 4C 00 4C 40 4C 00 4C 50",
"15 11 15 07 14 21 06 34 15 21 56 15 65 12 15 35",
"13 11 11 43 60 12 41 70 11 21 56 11 65 12 51 31",
"21 12 11 07 24 12 05 34 11 21 56 11 65 22 46 30",
"13 11 11 43 50 21 42 70 11 21 56 91 65 12 15 31",
"51 11 81 44 54 11 45 44 90 21 56 59 65 12 51 71",
"87 00 48 44 44 00 84 44 48 00 44 44 84 00 84 74",
"19 13 45 44 14 11 09 04 14 11 05 04 18 11 09 34",
"11 21 5E 1D 11 11 C1 C0 D0 11 C1 C0 D0 11 C1 50",
"11 21 46 30 11 11 0D 0C 1C 11 0D 0C 1C 11 0D 14",
"11 13 55 D1 51 11 81 40 50 11 41 40 90 11 81 70",
"11 01 44 84 4C 00 48 84 44 00 44 84 48 00 88 74",
"51 73 45 C8 54 D1 45 C8 54 D1 45 C8 54 D1 49 74"
                },
                Second16Lines = new[]
                    {
"55 95 55 95 55 95 55 95 55 95 55 85 15 95 55 95",
"55 11 55 11 55 11 55 11 55 11 55 11 15 11 55 11",
"55 11 55 11 55 11 55 11 55 11 55 11 D4 11 55 11",
"45 80 44 40 44 80 44 40 44 80 44 40 44 80 44 50",
"15 11 15 05 14 11 84 14 15 11 C4 15 C5 11 15 95",
"11 11 11 41 C0 11 41 50 11 11 D4 11 C5 11 51 11",
"81 90 11 05 84 11 05 14 11 11 D4 11 C5 81 C4 10",
"11 11 11 41 50 11 C0 50 11 11 D4 11 C5 11 15 11",
"51 11 81 44 54 11 45 44 90 11 D4 91 C5 01 51 D1",
"05 80 44 44 44 80 04 44 44 80 44 44 04 80 04 54",
"11 11 45 44 14 11 01 04 14 11 05 04 10 11 01 14",
"11 81 D4 15 11 11 41 40 50 11 41 40 50 11 41 50",
"11 81 44 90 11 11 05 04 14 11 05 04 14 11 05 14",
"11 11 55 51 51 11 81 40 50 11 41 40 90 11 01 50",
"11 01 44 04 44 80 C0 04 44 80 44 04 C0 80 00 54",
"D1 D1 45 40 54 51 45 40 54 51 45 40 54 51 C1 54"
            },
                TextBaseAddr = 0xC560,
                PatchBase = 0x0B7F
            },

            // Другие файлы
        };
    }

        public class OvrFileConfig
        {
            public int StartAddress { get; set; }
            public string[] First16Lines { get; set; }
            public string[] Second16Lines { get; set; }
            public int TextBaseAddr { get; set; } // добавляем новое свойство
            public int PatchBase { get; set; } // добавляем новое свойство
        }

}