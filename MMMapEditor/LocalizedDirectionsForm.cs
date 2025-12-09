using IniParser;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MMMapEditor
{
    // Отдельный класс для формы настройки направлений
    // Локализованная форма для настройки направлений
    public partial class LocalizedDirectionsForm : Form
    {
        // Элементы интерфейса формы
        private ComboBox cbTop;
        private ComboBox cbBottom;
        private ComboBox cbLeft;
        private ComboBox cbRight;
        private Button btnSaveClose;

        public event EventHandler LocalizationChanged;

        public LocalizedDirectionsForm()
        {
            InitializeComponent();
           // InitializeComponents(); // Если используете конструктор форм дизайнера, иначе этот метод надо убрать
            SetupComboboxes();
        }

        private void SetupComboboxes()
        {
            // Создаем элементы интерфейса вручную, если не используется дизайнер
            cbTop = new ComboBox();
            cbBottom = new ComboBox();
            cbLeft = new ComboBox();
            cbRight = new ComboBox();
            btnSaveClose = new Button();

            // Добавляем стандартные варианты выбора
            string[] choices = { "Верх", "Север", "North" };
            cbTop.Items.AddRange(choices);
            cbTop.SelectedIndex = 0;

            choices = new string[] { "Низ", "Юг", "South" };
            cbBottom.Items.AddRange(choices);
            cbBottom.SelectedIndex = 0;

            choices = new string[] { "Лево", "Запад", "West" };
            cbLeft.Items.AddRange(choices);
            cbLeft.SelectedIndex = 0;

            choices = new string[] { "Право", "Восток", "East" };
            cbRight.Items.AddRange(choices);
            cbRight.SelectedIndex = 0;

            // Добавляем элементы на форму
            this.Controls.Add(cbTop);
            this.Controls.Add(cbBottom);
            this.Controls.Add(cbLeft);
            this.Controls.Add(cbRight);
            this.Controls.Add(btnSaveClose);

            // Конфигурация элементов
            cbTop.Location = new Point(15, 15);
            cbBottom.Location = new Point(cbTop.Right + 15, cbTop.Top);
            cbLeft.Location = new Point(cbBottom.Right + 15, cbBottom.Top);
            cbRight.Location = new Point(cbLeft.Right + 15, cbLeft.Top);
            btnSaveClose.Location = new Point(cbBottom.Left + 15, cbBottom.Bottom + 15);
            btnSaveClose.Size = new Size(200, btnSaveClose.Height);
            btnSaveClose.Text = "Сохранить и закрыть";
            btnSaveClose.Click += BtnSaveClose_Click;
        }

        private void BtnSaveClose_Click(object sender, EventArgs e)
        {
            // Сохраняем настройки в INI-файл
            SetLocalizedStrings(
                cbTop.Text,
                cbBottom.Text,
                cbLeft.Text,
                cbRight.Text
            );

            Close();
        }

        private void SetLocalizedStrings(string top, string bottom, string left, string right)
        {
            // Обновляем настройки в секции CustomDirections
            var parser = new FileIniDataParser();
            var iniFile = parser.ReadFile("Settings.ini");

            iniFile.Sections.AddSection("CustomDirections");
            iniFile["CustomDirections"]["Top"] = top;
            iniFile["CustomDirections"]["Bottom"] = bottom;
            iniFile["CustomDirections"]["Left"] = left;
            iniFile["CustomDirections"]["Right"] = right;

            parser.WriteFile("Settings.ini", iniFile);

            // Отправляем событие о смене локализации
            LocalizationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
