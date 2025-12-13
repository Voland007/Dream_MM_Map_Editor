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
    // Класс для работы с хранилищем объектов
    public static class DataManager
    {
        public static List<CentralObject> LoadObjects(string fileName = "objects.json")
        {
            if (File.Exists(fileName))
            {
                string content = File.ReadAllText(fileName);
                return DeserializeFromJson(content);
            }
            return new List<CentralObject>();
        }

        public static void SaveObjects(List<CentralObject> objects, string fileName = "objects.json")
        {
            string jsonContent = SerializeToJson(objects);
            File.WriteAllText(fileName, jsonContent);
        }

        // Новый метод сериализации
        private static string SerializeToJson(List<CentralObject> objects)
        {
            foreach (var obj in objects.Where(x => x.IconBase64 == null && x.Icon != null))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    obj.Icon.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] imageBytes = ms.ToArray();
                    obj.IconBase64 = Convert.ToBase64String(imageBytes);
                }
            }

            return JsonConvert.SerializeObject(objects, Formatting.Indented);
        }

        // Десериализация (при чтении)
        private static List<CentralObject> DeserializeFromJson(string jsonContent)
        {
            var deserializedObjs = JsonConvert.DeserializeObject<List<CentralObject>>(jsonContent);
            foreach (var obj in deserializedObjs.Where(x => !string.IsNullOrEmpty(x.IconBase64)))
            {
                byte[] bytes = Convert.FromBase64String(obj.IconBase64);
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    obj.Icon = Image.FromStream(ms);
                }
            }
            return deserializedObjs;
        }
    }
}