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

﻿using System;
using System.Drawing;
using System.Windows.Forms;

namespace MMMapEditor
{
    public partial class NotesEditorForm : Form
    {
        private readonly RichTextBox editorTextBox;
        private readonly Button saveAndCloseButton;
        private readonly Button closeButton;

        public string EditedText => editorTextBox.Text;

        public NotesEditorForm(Form owner, RichTextBox sourceTextBox)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (sourceTextBox == null) throw new ArgumentNullException(nameof(sourceTextBox));

            Owner = owner;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);

            Width = (int)(owner.Width * 0.75);
            Height = (int)(owner.Height * 0.75);

            Location = new Point(
                owner.Left + (owner.Width - Width) / 2,
                owner.Top + (owner.Height - Height) / 2
            );

            editorTextBox = new RichTextBox
            {
                Multiline = sourceTextBox.Multiline,
                ScrollBars = sourceTextBox.ScrollBars,
                WordWrap = sourceTextBox.WordWrap,
                DetectUrls = sourceTextBox.DetectUrls,
                AcceptsTab = sourceTextBox.AcceptsTab,
                BorderStyle = sourceTextBox.BorderStyle,
                BackColor = sourceTextBox.BackColor,
                ForeColor = sourceTextBox.ForeColor,
                Font = sourceTextBox.Font,
                Location = new Point(10, 10),
                Size = new Size(Width - 20, Height - 70),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            editorTextBox.Rtf = sourceTextBox.Rtf;

            saveAndCloseButton = new Button
            {
                Text = "Сохранить и закрыть",
                Width = 160,
                Height = 32,
                Left = 10,
                Top = Height - 45,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                ForeColor = Color.White
            };
            saveAndCloseButton.Click += SaveAndCloseButton_Click;

            closeButton = new Button
            {
                Text = "Закрыть",
                Width = 100,
                Height = 32,
                Left = Width - 110,
                Top = Height - 45,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                ForeColor = Color.White
            };
            closeButton.Click += CloseButton_Click;

            Controls.Add(editorTextBox);
            Controls.Add(saveAndCloseButton);
            Controls.Add(closeButton);
        }

        private void SaveAndCloseButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
