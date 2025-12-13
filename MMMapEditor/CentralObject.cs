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
using System.Text;
using System.Threading.Tasks;

namespace MMMapEditor
{
    // Класс для объектов (предметов, существ и т.п.)
    public class CentralObject
    {
        public string Name { get; set; }
        public int LeftMargin { get; set; }
        public int RightMargin { get; set; }
        public int FilterLevel { get; set; }

        // Поле для хранения изображения в виде Base64 строки
        [JsonProperty]
        public string IconBase64 { get; set; }

        // Физически присутствующее поле Icon, но оно не участвует в сериализации
        [JsonIgnore]
        public Image Icon
        {
            get
            {
                if (!string.IsNullOrEmpty(IconBase64))
                {
                    byte[] bytes = Convert.FromBase64String(IconBase64);
                    using (MemoryStream ms = new MemoryStream(bytes))
                    {
                        return Image.FromStream(ms);
                    }
                }
                return null;
            }
            set
            {
                if (value != null)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        value.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        byte[] imageBytes = ms.ToArray();
                        IconBase64 = Convert.ToBase64String(imageBytes);
                    }
                }
                else
                {
                    IconBase64 = null;
                }
            }
        }
    }
}