using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MMMapEditor
{
    public partial class MetadataForm : Form
    {
        public string SelectedMapSector { get; private set; }
        public string SelectedSurface { get; private set; }

        public MetadataForm(string mapSector, string surface)
        {
            InitializeComponent();

            // Установка начальных значений радиокнопок, если переданы значения
            SetInitialValues(mapSector, surface);
        }

        private void SetInitialValues(string mapSector, string surface)
        {
            if (string.IsNullOrEmpty(mapSector))
            {
                checkBoxMapSector.Checked = true;
            }
            else
            {
                string sectorPart = mapSector.Split('-')[0];
                string numberPart = mapSector.Split('-')[1];

                SelectRadioButton(sectorPart, groupBoxMapSectorLetters);
                SelectRadioButton(numberPart, groupBoxMapSectorNumbers);
            }

            if (string.IsNullOrEmpty(surface))
            {
                checkBoxSurface.Checked = true;
            }
            else
            {
                Match match = Regex.Match(surface, @"X = (\d+) Y = (\d+)");

                if (match.Success)
                {
                    string xCoord = match.Groups[1].Value;
                    string yCoord = match.Groups[2].Value;

                    SelectRadioButton(xCoord, groupBoxXCoordinate);
                    SelectRadioButton(yCoord, groupBoxYCoordinate);
                }
            }
        }

        private void SelectRadioButton(string value, GroupBox groupBox)
        {
            foreach (Control ctrl in groupBox.Controls)
            {
                if (ctrl is RadioButton radioBtn && radioBtn.Text == value)
                {
                    radioBtn.Checked = true;
                    break;
                }
            }
        }

        private void btnSaveClose_Click(object sender, EventArgs e)
        {
            // Проверка чекбокса для MAP SECTOR
            if (checkBoxMapSector.Checked)
            {
                SelectedMapSector = "";
            }
            else
            {
                // Формирование сектора, учитывая возможные пустоты
                string letter = GetSelectedRadioButton(groupBoxMapSectorLetters);
                string number = GetSelectedRadioButton(groupBoxMapSectorNumbers);

                if (string.IsNullOrEmpty(letter) || string.IsNullOrEmpty(number))
                {
                    SelectedMapSector = "";
                }
                else
                {
                    SelectedMapSector = $"{letter}-{number}";
                }
            }

            // Проверка чекбокса для SURFACE
            if (checkBoxSurface.Checked)
            {
                SelectedSurface = "";
            }
            else
            {
                // Формирование поверхности, учитывая возможные пустоты
                string xCoord = GetSelectedRadioButton(groupBoxXCoordinate);
                string yCoord = GetSelectedRadioButton(groupBoxYCoordinate);

                if (string.IsNullOrEmpty(xCoord) || string.IsNullOrEmpty(yCoord))
                {
                    SelectedSurface = "";
                }
                else
                {
                    SelectedSurface = $"X = {xCoord} Y = {yCoord}";
                }
            }

            // Закрытие окна
            Close();
        }

        private string GetSelectedRadioButton(GroupBox groupBox)
        {
            if (groupBox == null || groupBox.Controls == null)
            {
                return "";
            }

            foreach (Control ctrl in groupBox.Controls)
            {
                if (ctrl is RadioButton radioBtn && radioBtn.Checked)
                {
                    return radioBtn.Text;
                }
            }
            return "";
        }

        private void InitializeComponent()
        {
            // Инициализация контролов
            this.groupBoxMapSector = new GroupBox();
            this.groupBoxMapSectorLetters = new GroupBox();
            this.radioButtonA = new RadioButton();
            this.radioButtonB = new RadioButton();
            this.radioButtonC = new RadioButton();
            this.radioButtonD = new RadioButton();
            this.radioButtonE = new RadioButton();
            this.labelDash = new Label();
            this.groupBoxMapSectorNumbers = new GroupBox();
            this.radioButton1 = new RadioButton();
            this.radioButton2 = new RadioButton();
            this.radioButton3 = new RadioButton();
            this.radioButton4 = new RadioButton();
            this.groupBoxSurface = new GroupBox();
            this.groupBoxXCoordinate = new GroupBox();
            this.groupBoxYCoordinate = new GroupBox();
            this.btnSaveClose = new Button();

            // Настройки компоновки
            this.groupBoxMapSector.SuspendLayout();
            this.groupBoxMapSectorLetters.SuspendLayout();
            this.groupBoxMapSectorNumbers.SuspendLayout();
            this.groupBoxSurface.SuspendLayout();
            this.groupBoxXCoordinate.SuspendLayout();
            this.groupBoxYCoordinate.SuspendLayout();
            this.SuspendLayout();

            this.ClientSize = new Size(550, 210);

            // MAP SECTOR
            this.groupBoxMapSector.Controls.Add(this.groupBoxMapSectorLetters);
            this.groupBoxMapSector.Controls.Add(this.labelDash);
            this.groupBoxMapSector.Controls.Add(this.groupBoxMapSectorNumbers);
            this.groupBoxMapSector.Location = new Point(10, 10);
            this.groupBoxMapSector.Size = new Size(145, 160);
            this.groupBoxMapSector.TabIndex = 0;
            this.groupBoxMapSector.TabStop = false;
            this.groupBoxMapSector.Text = "MAP SECTOR";

            // LEFT GROUP OF LETTERS
            this.groupBoxMapSectorLetters.Controls.Add(this.radioButtonA);
            this.groupBoxMapSectorLetters.Controls.Add(this.radioButtonB);
            this.groupBoxMapSectorLetters.Controls.Add(this.radioButtonC);
            this.groupBoxMapSectorLetters.Controls.Add(this.radioButtonD);
            this.groupBoxMapSectorLetters.Controls.Add(this.radioButtonE);
            this.groupBoxMapSectorLetters.Location = new Point(10, 10);
            this.groupBoxMapSectorLetters.Size = new Size(45, 140);
            this.groupBoxMapSectorLetters.TabIndex = 0;
            this.groupBoxMapSectorLetters.TabStop = false;
            this.groupBoxMapSectorLetters.Text = "";

            // BUTTON A
            this.radioButtonA.AutoSize = true;
            this.radioButtonA.Location = new Point(10, 10);
            this.radioButtonA.Size = new Size(31, 17);
            this.radioButtonA.TabIndex = 0;
            this.radioButtonA.Text = "A";
            this.radioButtonA.UseVisualStyleBackColor = true;

            // BUTTON B
            this.radioButtonB.AutoSize = true;
            this.radioButtonB.Location = new Point(radioButtonA.Left, radioButtonA.Bottom + 10);
            this.radioButtonB.Size = new Size(31, 17);
            this.radioButtonB.TabIndex = 1;
            this.radioButtonB.Text = "B";
            this.radioButtonB.UseVisualStyleBackColor = true;

            // BUTTON C
            this.radioButtonC.AutoSize = true;
            this.radioButtonC.Location = new Point(radioButtonB.Left, radioButtonB.Bottom + 10);
            this.radioButtonC.Size = new Size(31, 17);
            this.radioButtonC.TabIndex = 2;
            this.radioButtonC.Text = "C";
            this.radioButtonC.UseVisualStyleBackColor = true;

            // BUTTON D
            this.radioButtonD.AutoSize = true;
            this.radioButtonD.Location = new Point(radioButtonC.Left, radioButtonC.Bottom + 10);
            this.radioButtonD.Size = new Size(31, 17);
            this.radioButtonD.TabIndex = 3;
            this.radioButtonD.Text = "D";
            this.radioButtonD.UseVisualStyleBackColor = true;

            // BUTTON E
            this.radioButtonE.AutoSize = true;
            this.radioButtonE.Location = new Point(radioButtonD.Left, radioButtonD.Bottom + 10);
            this.radioButtonE.Size = new Size(31, 17);
            this.radioButtonE.TabIndex = 4;
            this.radioButtonE.Text = "E";
            this.radioButtonE.UseVisualStyleBackColor = true;

            // LABEL DASH
            this.labelDash.AutoSize = true;
            this.labelDash.Location = new Point(groupBoxMapSectorLetters.Right + 10, groupBoxMapSectorLetters.Top + 65);
            this.labelDash.Size = new Size(40, 52);
            this.labelDash.TabIndex = 1;
            this.labelDash.Text = "-";

            // RIGHT GROUP OF NUMBERS
            this.groupBoxMapSectorNumbers.Controls.Add(this.radioButton1);
            this.groupBoxMapSectorNumbers.Controls.Add(this.radioButton2);
            this.groupBoxMapSectorNumbers.Controls.Add(this.radioButton3);
            this.groupBoxMapSectorNumbers.Controls.Add(this.radioButton4);
            this.groupBoxMapSectorNumbers.Location = new Point(labelDash.Right + 10, groupBoxMapSectorLetters.Top);
            this.groupBoxMapSectorNumbers.Size = new Size(45, 140);
            this.groupBoxMapSectorNumbers.TabIndex = 2;
            this.groupBoxMapSectorNumbers.TabStop = false;
            this.groupBoxMapSectorNumbers.Text = "";

            // BUTTON 1
            this.radioButton1.AutoSize = true;
            this.radioButton1.Location = new Point(10, 19);
            this.radioButton1.Size = new Size(31, 17);
            this.radioButton1.TabIndex = 0;
            this.radioButton1.Text = "1";
            this.radioButton1.UseVisualStyleBackColor = true;

            // BUTTON 2
            this.radioButton2.AutoSize = true;
            this.radioButton2.Location = new Point(radioButton1.Left, radioButton1.Bottom + 13);
            this.radioButton2.Size = new Size(31, 17);
            this.radioButton2.TabIndex = 1;
            this.radioButton2.Text = "2";
            this.radioButton2.UseVisualStyleBackColor = true;

            // BUTTON 3
            this.radioButton3.AutoSize = true;
            this.radioButton3.Location = new Point(radioButton2.Left, radioButton2.Bottom + 13);
            this.radioButton3.Size = new Size(31, 17);
            this.radioButton3.TabIndex = 2;
            this.radioButton3.Text = "3";
            this.radioButton3.UseVisualStyleBackColor = true;

            // BUTTON 4
            this.radioButton4.AutoSize = true;
            this.radioButton4.Location = new Point(radioButton3.Left, radioButton3.Bottom + 13);
            this.radioButton4.Size = new Size(31, 17);
            this.radioButton4.TabIndex = 3;
            this.radioButton4.Text = "4";
            this.radioButton4.UseVisualStyleBackColor = true;

            // GROUPBOX SURFACE
            this.groupBoxSurface.Controls.Add(this.groupBoxXCoordinate);
            this.groupBoxSurface.Controls.Add(this.groupBoxYCoordinate);
            this.groupBoxSurface.Location = new Point(groupBoxMapSector.Right + 10, groupBoxMapSector.Top);
            this.groupBoxSurface.Size = new Size(370, 160);
            this.groupBoxSurface.TabIndex = 1;
            this.groupBoxSurface.TabStop = false;
            this.groupBoxSurface.Text = "SURFACE";

            // GROUPBOX X COORDINATES
            this.groupBoxXCoordinate.Location = new Point(10, 15); // Внутренние отступы увеличены
            this.groupBoxXCoordinate.Size = new Size(170, 140); // Размеры увеличены вдвое
            this.groupBoxXCoordinate.TabIndex = 0;
            this.groupBoxXCoordinate.TabStop = false;
            this.groupBoxXCoordinate.Text = "X";

            // RADIOBUTTONS FOR X COORDINATES
            int xOffset = 10;
            int yOffset = 15;
            for (int i = 0; i <= 15; i++)
            {
                var rb = new RadioButton()
                {
                    Size = new Size(37, 17),
                    Location = new Point(xOffset + (i % 4) * 40, yOffset + (i / 4) * 30),
                    TabIndex = i,
                    Text = i.ToString(),
                    UseVisualStyleBackColor = true
                };
                this.groupBoxXCoordinate.Controls.Add(rb); // Добавляем радиокнопку в groupBoxXCoordinate
            }

            // GROUPBOX Y COORDINATES
            this.groupBoxYCoordinate.Location = new Point(groupBoxXCoordinate.Right + 10, groupBoxXCoordinate.Top); // Размещаем справа
            this.groupBoxYCoordinate.Size = new Size(170, 140); // Размеры увеличены вдвое
            this.groupBoxYCoordinate.TabIndex = 1;
            this.groupBoxYCoordinate.TabStop = false;
            this.groupBoxYCoordinate.Text = "Y";

            // RADIOBUTTONS FOR Y COORDINATES
            xOffset = 10;
            yOffset = 15;
            for (int i = 0; i <= 15; i++)
            {
                var rb = new RadioButton()
                {
                    Size = new Size(37, 17),
                    Location = new Point(xOffset + (i % 4) * 40, yOffset + (i / 4) * 30),
                    TabIndex = i,
                    Text = i.ToString(),
                    UseVisualStyleBackColor = true
                };
                this.groupBoxYCoordinate.Controls.Add(rb); // Добавляем радиокнопку в groupBoxYCoordinate
            }

            // SAVE AND CLOSE BUTTON
            this.btnSaveClose.DialogResult = DialogResult.OK;
            this.btnSaveClose.Location = new Point(groupBoxSurface.Right - 150, groupBoxSurface.Bottom + 10);
            this.btnSaveClose.Size = new Size(140, 20);
            this.btnSaveClose.TabIndex = 2;
            this.btnSaveClose.Text = "Сохранить и закрыть";
            this.btnSaveClose.UseVisualStyleBackColor = true;
            this.btnSaveClose.Click += new EventHandler(btnSaveClose_Click);

            // Добавляем все компоненты в коллекцию Controls формы
            this.Controls.Add(this.groupBoxMapSector);
            this.Controls.Add(this.groupBoxSurface);
            this.Controls.Add(this.btnSaveClose);

            // Final layout settings
            this.AcceptButton = this.btnSaveClose;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Редактирование метаданных карты";

            this.groupBoxMapSector.ResumeLayout(false);
            this.groupBoxMapSectorLetters.ResumeLayout(false);
            this.groupBoxMapSectorNumbers.ResumeLayout(false);
            this.groupBoxSurface.ResumeLayout(false);
            this.groupBoxXCoordinate.ResumeLayout(false);
            this.groupBoxYCoordinate.ResumeLayout(false);
            this.ResumeLayout(false);

            this.checkBoxMapSector = new CheckBox();
            this.checkBoxMapSector.AutoSize = true;
            this.checkBoxMapSector.Location = new Point(groupBoxMapSector.Left, groupBoxMapSector.Bottom + 15);
            this.checkBoxMapSector.Size = new Size(150, 17);
            this.checkBoxMapSector.TabIndex = 3;
            this.checkBoxMapSector.Text = "Астральный план";
            this.checkBoxMapSector.UseVisualStyleBackColor = true;
            this.checkBoxMapSector.CheckedChanged += new EventHandler(checkBoxMapSector_CheckedChanged);

            this.checkBoxSurface = new CheckBox();
            this.checkBoxSurface.AutoSize = true;
            this.checkBoxSurface.Location = new Point(groupBoxSurface.Left, groupBoxSurface.Bottom + 15);
            this.checkBoxSurface.Size = new Size(150, 17);
            this.checkBoxSurface.TabIndex = 4;
            this.checkBoxSurface.Text = "Сектор глобальной карты";
            this.checkBoxSurface.UseVisualStyleBackColor = true;
            this.checkBoxSurface.CheckedChanged += new EventHandler(checkBoxSurface_CheckedChanged);

            // Добавляем оба чекбокса в список элементов управления формы
            this.Controls.Add(this.checkBoxMapSector);
            this.Controls.Add(this.checkBoxSurface);
        }

        private void checkBoxMapSector_CheckedChanged(object sender, EventArgs e)
        {
            bool checkedState = ((CheckBox)sender).Checked;

            // Блокировка всех радиокнопок группы MAP SECTOR
            foreach (Control control in groupBoxMapSectorLetters.Controls)
            {
                if (control is RadioButton button)
                {
                    button.Enabled = !checkedState;
                }
            }
            foreach (Control control in groupBoxMapSectorNumbers.Controls)
            {
                if (control is RadioButton button)
                {
                    button.Enabled = !checkedState;
                }
            }

            // Очистка значения свойства SelectedMapSector, если чекбокс отмечен
            if (checkedState)
            {
                SelectedMapSector = "";
            }
        }

        private void checkBoxSurface_CheckedChanged(object sender, EventArgs e)
        {
            bool checkedState = ((CheckBox)sender).Checked;

            // Блокировка всех радиокнопок группы SURFACE
            foreach (Control control in groupBoxXCoordinate.Controls)
            {
                if (control is RadioButton button)
                {
                    button.Enabled = !checkedState;
                }
            }
            foreach (Control control in groupBoxYCoordinate.Controls)
            {
                if (control is RadioButton button)
                {
                    button.Enabled = !checkedState;
                }
            }

            // Очистка значения свойства SelectedSurface, если чекбокс отмечен
            if (checkedState)
            {
                SelectedSurface = "";
            }
        }

        // Components declarations
        private GroupBox groupBoxMapSector;
        private GroupBox groupBoxMapSectorLetters;
        private RadioButton radioButtonA;
        private RadioButton radioButtonB;
        private RadioButton radioButtonC;
        private RadioButton radioButtonD;
        private RadioButton radioButtonE;
        private Label labelDash;
        private GroupBox groupBoxMapSectorNumbers;
        private RadioButton radioButton1;
        private RadioButton radioButton2;
        private RadioButton radioButton3;
        private RadioButton radioButton4;
        private GroupBox groupBoxSurface;
        private GroupBox groupBoxXCoordinate;
        private GroupBox groupBoxYCoordinate;
        private Button btnSaveClose;
        private GroupBox groupBoxNumber;
        // Чекбокс для MAP SECTOR
        private CheckBox checkBoxMapSector;

        // Чекбокс для SURFACE
        private CheckBox checkBoxSurface;
    }
}
