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

namespace MMMapEditor
{
    internal static class SpecialNoteTexts
    {
        public const ushort SecretPassageToDoomCastleAddress = 0xC9D4;
        public const string SecretPassageToDoomCastle =
            "***Открывается SECRET PASSAGE TO DOOM CASTLE (7,14)***";

        public const ushort RedDragonResurrectionAddress = 0xC9B6;
        public const string RedDragonResurrection =
            "***Воскрешение RED DRAGON на клетке (11,3)***";

        public const ushort WaterMonsterResurrectionAddress = 0xC993;
        public const string WaterMonsterResurrection =
            "***Вода стихает. Но в её глубине всё ещё что-то ждёт (Водяное чудище (7,9) воскресает при выполнении ряда условий)***";

        public const ushort GiantScorpionResurrectionAddress = 0xC983;
        public const string GiantScorpionResurrection =
            "***Пустыня затихла, но её молчание обманчиво: где-то в её недрах ждёт древний хищник (Гигантский скорпион (10,5) воскресает при выполнении ряда условий)***";
    }
}
