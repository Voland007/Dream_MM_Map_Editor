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
using System.Collections.Generic;
using System.Text;

namespace MMMapEditor
{
    public class ContainerInfo
    {
        public string Name { get; set; }
        public byte[] RawData { get; set; }
        public int Index { get; set; }

        public override string ToString()
        {
            return Name ?? $"Container {Index}";
        }
    }

    public static class ContainerDatabase
    {
        private static List<ContainerInfo> _containers = null;
        private static Dictionary<int, ContainerInfo> _containersById = null;

        public static IReadOnlyList<ContainerInfo> Containers
        {
            get
            {
                if (_containers == null)
                    ParseContainerData();
                return _containers.AsReadOnly();
            }
        }

        public static ContainerInfo GetContainerByIndex(int index)
        {
            if (_containersById == null)
                ParseContainerData();

            _containersById.TryGetValue(index, out var container);
            return container;
        }

        public static string GetContainerName(int index)
        {
            var container = GetContainerByIndex(index);
            return container?.Name ?? $"Container {index}";
        }

        public static bool TryGetContainerName(int index, out string containerName)
        {
            var container = GetContainerByIndex(index);
            if (container != null)
            {
                containerName = container.Name;
                return true;
            }

            containerName = null;
            return false;
        }

        private static void ParseContainerData()
        {
            _containers = new List<ContainerInfo>();
            _containersById = new Dictionary<int, ContainerInfo>();

            string hexData = @"00 20 43 4C 4F 54 48 20 53 41 43 4B 20 00 4C 45 41 54 48 45 52 20 53 41 43 4B 00 20 57 4F 4F 44 45 4E 20 42 4F 58 20 00 57 4F 4F 44 45 4E 20 43 48 45 53 54 00 20 20 49 52 4F 4E 20 42 4F 58 20 20 00 20 49 52 4F 4E 20 43 48 45 53 54 20 00 20 53 49 4C 56 45 52 20 42 4F 58 20 00 53 49 4C 56 45 52 20 43 48 45 53 54 00 20 20 47 4F 4C 44 20 42 4F 58 20 20 00 20 47 4F 4C 44 20 43 48 45 53 54 20 00 20 42 4C 41 43 4B 20 42 4F 58 20 20";

            string[] hexBytes = hexData.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] allBytes = new byte[hexBytes.Length];
            for (int i = 0; i < hexBytes.Length; i++)
            {
                allBytes[i] = Convert.ToByte(hexBytes[i], 16);
            }

            const int entrySize = 13; // 00 + 12 байт ASCII имени
            int containerId = 0;

            for (int index = 0; index + entrySize <= allBytes.Length; index += entrySize)
            {
                byte[] entry = new byte[12];
                Array.Copy(allBytes, index + 1, entry, 0, 12);

                var container = new ContainerInfo
                {
                    Index = containerId,
                    RawData = entry,
                    Name = Encoding.ASCII.GetString(entry)
                };

                _containers.Add(container);
                _containersById[containerId] = container;
                containerId++;
            }
        }
    }
}