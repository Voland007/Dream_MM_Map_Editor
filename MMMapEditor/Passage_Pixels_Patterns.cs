// Copyright (c) Voland007 2025. All rights reserved.
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


﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MMMapEditor
{
    public static class Passage_Pixels_Patterns
    {

        // Исходный шаблон лестницы (14x7)
        public static int[,] stairs_pattern = {
        {0, 0, 5, 0, 0, 0, 0},
        {0, 0, 5, 0, 0, 0, 0},
        {0, 5, 5, 5, 0, 0, 4},
        {0, 5, 5, 5, 0, 0, 1},
        {5, 0, 5, 0, 5, 0, 1},
        {0, 0, 5, 0, 4, 4, 4},
        {0, 0, 5, 0, 1, 1, 1},
        {0, 0, 5, 0, 1, 1, 1},
        {0, 0, 4, 4, 4, 4, 4},
        {0, 0, 1, 1, 1, 1, 1},
        {0, 0, 1, 1, 1, 1, 1},
        {4, 4, 4, 4, 4, 4, 4},
        {1, 1, 1, 1, 1, 1, 1},
        {1, 1, 1, 1, 1, 1, 1}
            };
        // Перевернутый шаблон лестницы (7x14)
        public static int[,] stairs_pattern_rotated = {
        {0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 4, 1, 1},
        {0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 4, 1, 1},
        {0, 5, 5, 5, 0, 0, 0, 0, 4, 1, 1, 4, 1, 1},
        {0, 5, 5, 5, 0, 0, 0, 0, 4, 1, 1, 4, 1, 1},
        {5, 0, 5, 0, 5, 4, 1, 1, 4, 1, 1, 4, 1, 1},
        {0, 0, 5, 0, 0, 4, 1, 1, 4, 1, 1, 4, 1, 1},
        {0, 0, 5, 1, 1, 4, 1, 1, 4, 1, 1, 4, 1, 1}
            };
        // Зеркальное отражение stairsPatternRotated по горизонтали
        public static int[,] stairs_pattern_rotated270 =  {
        {0, 0, 5, 1, 1, 4, 1, 1, 4, 1, 1, 4, 1, 1},
        {0, 0, 5, 0, 0, 4, 1, 1, 4, 1, 1, 4, 1, 1},
        {0, 5, 5, 5, 0, 4, 1, 1, 4, 1, 1, 4, 1, 1},
        {0, 5, 5, 5, 0, 0, 0, 0, 4, 1, 1, 4, 1, 1},
        {5, 0, 5, 0, 5, 0, 0, 0, 4, 1, 1, 4, 1, 1},
        {0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 4, 1, 1},
        {0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 4, 1, 1}
            };
        //Выход сверху
        public static int[,] exit_top = {
        {0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0},
        {0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0},
        {0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0},
        {1, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0}
            };

        //Выход снизу
        public static int[,] exit_bottom = {
        {0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0},
        {1, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1},
        {0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0},
        {0, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0},
        {0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0}
            };

    } 
}