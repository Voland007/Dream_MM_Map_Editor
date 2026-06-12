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
using System.IO;
using IniParser;
using IniParser.Model;

namespace MMMapEditor
{
    internal static class ObjectProfileSettings
    {
        private const string SettingsFileName = "Settings.ini";
        private const string GeneralSectionName = "General";
        private const string DefaultConfigObjectFileKey = "DefaultConfigObjectFile";
        private const string LocalDefaultProfileFileName = "objects.json";

        public static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);

        public static string LocalDefaultProfilePath =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LocalDefaultProfileFileName));

        public static string ResolveProfilePath(string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                return "";

            return Path.IsPathRooted(profilePath)
                ? Path.GetFullPath(profilePath)
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilePath));
        }

        public static string ResolveDefaultProfilePath(bool initializeFromLocalObjects)
        {
            string configuredProfilePath = GetConfiguredDefaultProfilePath();
            if (!string.IsNullOrWhiteSpace(configuredProfilePath))
                return configuredProfilePath;

            string localProfilePath = LocalDefaultProfilePath;
            if (!File.Exists(localProfilePath))
                return "";

            if (initializeFromLocalObjects)
                TryWriteDefaultProfilePath(localProfilePath, out _);

            return localProfilePath;
        }

        public static string GetConfiguredDefaultProfilePath()
        {
            string configuredValue = GetConfiguredDefaultProfileValue();
            return ResolveProfilePath(configuredValue);
        }

        public static string GetConfiguredDefaultProfileValue()
        {
            string settingsPath = SettingsFilePath;
            if (!File.Exists(settingsPath))
                return "";

            try
            {
                var parser = new FileIniDataParser();
                IniData data = parser.ReadFile(settingsPath);

                if (data.Sections.ContainsSection(GeneralSectionName) &&
                    data[GeneralSectionName].ContainsKey(DefaultConfigObjectFileKey))
                {
                    return data[GeneralSectionName][DefaultConfigObjectFileKey];
                }
            }
            catch
            {
                return "";
            }

            return "";
        }

        public static void WriteDefaultProfilePath(string profilePath)
        {
            string settingsPath = SettingsFilePath;
            var parser = new FileIniDataParser();
            IniData iniData;

            if (File.Exists(settingsPath))
            {
                try
                {
                    iniData = parser.ReadFile(settingsPath);
                }
                catch
                {
                    iniData = new IniData();
                }
            }
            else
            {
                iniData = new IniData();
            }

            if (!iniData.Sections.ContainsSection(GeneralSectionName))
                iniData.Sections.AddSection(GeneralSectionName);

            iniData[GeneralSectionName][DefaultConfigObjectFileKey] = ResolveProfilePath(profilePath);
            parser.WriteFile(settingsPath, iniData);
        }

        public static bool TryWriteDefaultProfilePath(string profilePath, out string errorMessage)
        {
            try
            {
                WriteDefaultProfilePath(profilePath);
                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
