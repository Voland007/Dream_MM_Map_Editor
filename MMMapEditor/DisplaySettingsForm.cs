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
        private readonly CheckBox showDangerousDesertCellsCheckBox;
        private readonly Button saveAndCloseButton;

        public bool ShowSecretPassages => showSecretPassagesCheckBox.Checked;
        public bool ShowDangerousWaterCells => showDangerousWaterCellsCheckBox.Checked;
        public bool ShowDangerousDesertCells => showDangerousDesertCellsCheckBox.Checked;

        public DisplaySettingsForm()
        {
            Font = new Font("Segoe UI", 9f);
            Text = "Настройки отображения";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 200);

            var displayGroupBox = new GroupBox
            {
                Text = "Параметры отображения",
                Location = new Point(12, 12),
                Size = new Size(336, 122)
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

            showDangerousDesertCellsCheckBox = new CheckBox
            {
                Text = "\u041e\u0442\u043e\u0431\u0440\u0430\u0436\u0430\u0442\u044c \u043f\u0443\u0441\u0442\u044b\u043d\u043d\u044b\u0435 \u043a\u043b\u0435\u0442\u043a\u0438 \u043e\u043f\u0430\u0441\u043d\u044b\u043c\u0438",
                AutoSize = true,
                Location = new Point(16, 82)
            };

            displayGroupBox.Controls.Add(showSecretPassagesCheckBox);
            displayGroupBox.Controls.Add(showDangerousWaterCellsCheckBox);
            displayGroupBox.Controls.Add(showDangerousDesertCellsCheckBox);

            saveAndCloseButton = new Button
            {
                Text = "Сохранить и закрыть",
                Size = new Size(160, 30),
                Location = new Point(188, 150)
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
            showDangerousDesertCellsCheckBox.Checked =
                MainForm.GetBooleanSetting("DisplaySettings", "ShowDangerousDesertCells", true);
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
                iniData["DisplaySettings"]["ShowDangerousDesertCells"] =
                    showDangerousDesertCellsCheckBox.Checked.ToString();
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
