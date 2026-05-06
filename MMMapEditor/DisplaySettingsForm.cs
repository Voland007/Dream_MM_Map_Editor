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
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using IniParser;
using IniParser.Model;

namespace MMMapEditor
{
    public partial class DisplaySettingsForm : Form
    {
        private readonly CheckBox showSecretPassagesCheckBox;
        private readonly CheckBox showDangerousWaterCellsCheckBox;
        private readonly Button saveAndCloseButton;

        public bool ShowSecretPassages => showSecretPassagesCheckBox.Checked;
        public bool ShowDangerousWaterCells => showDangerousWaterCellsCheckBox.Checked;

        public DisplaySettingsForm()
        {
            Font = new Font("Segoe UI", 9f);
            Text = "Настройки отображения";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 174);

            var displayGroupBox = new GroupBox
            {
                Text = "Параметры отображения",
                Location = new Point(12, 12),
                Size = new Size(336, 96)
            };

            showSecretPassagesCheckBox = new CheckBox
            {
                Text = "Отображать секретные проходы",
                AutoSize = true,
                Location = new Point(16, 30)
            };

            showDangerousWaterCellsCheckBox = new CheckBox
            {
                Text = "Отображать водные клетки опасными",
                AutoSize = true,
                Location = new Point(16, 56)
            };

            displayGroupBox.Controls.Add(showSecretPassagesCheckBox);
            displayGroupBox.Controls.Add(showDangerousWaterCellsCheckBox);

            saveAndCloseButton = new Button
            {
                Text = "Сохранить и закрыть",
                Size = new Size(160, 30),
                Location = new Point(188, 124)
            };
            saveAndCloseButton.Click += SaveAndCloseButton_Click;

            Controls.Add(displayGroupBox);
            Controls.Add(saveAndCloseButton);

            AcceptButton = saveAndCloseButton;
            Load += DisplaySettingsForm_Load;
        }

        private void DisplaySettingsForm_Load(object sender, EventArgs e)
        {
            showSecretPassagesCheckBox.Checked =
                MainForm.GetBooleanSetting("DisplaySettings", "ShowSecretPassages", true);
            showDangerousWaterCellsCheckBox.Checked =
                MainForm.GetBooleanSetting("DisplaySettings", "ShowDangerousWaterCells", true);
        }

        private void SaveAndCloseButton_Click(object sender, EventArgs e)
        {
            try
            {
                string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
                var parser = new FileIniDataParser();
                IniData iniData = File.Exists(settingsPath)
                    ? parser.ReadFile(settingsPath)
                    : new IniData();

                if (!iniData.Sections.ContainsSection("DisplaySettings"))
                    iniData.Sections.AddSection("DisplaySettings");

                iniData["DisplaySettings"]["ShowSecretPassages"] =
                    showSecretPassagesCheckBox.Checked.ToString();
                iniData["DisplaySettings"]["ShowDangerousWaterCells"] =
                    showDangerousWaterCellsCheckBox.Checked.ToString();
                parser.WriteFile(settingsPath, iniData);

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка при сохранении файла Settings.ini: {ex.Message}",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
