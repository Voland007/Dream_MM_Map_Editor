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

namespace MMMapEditor
{
    internal static class PartyInventorySemantics
    {
        public const byte FirstSlotOffset = 0x40;
        public const byte LastSlotOffset = 0x45;
        public const byte FirstBackpackSlotOffset = 0x46;
        public const byte LastBackpackSlotOffset = 0x4B;

        public static bool IsInventorySlotOffset(byte? fieldOffset)
        {
            return IsEquipmentSlotOffset(fieldOffset) ||
                   IsBackpackSlotOffset(fieldOffset);
        }

        public static bool IsInventorySlotRange(ValueRange8 fieldOffsetRange)
        {
            return fieldOffsetRange != null &&
                   fieldOffsetRange.Min >= FirstSlotOffset &&
                   fieldOffsetRange.Max <= LastBackpackSlotOffset;
        }

        public static bool IsInventorySlotReference(byte? fieldOffset, ValueRange8 fieldOffsetRange)
        {
            return IsInventorySlotOffset(fieldOffset) ||
                   IsInventorySlotRange(fieldOffsetRange);
        }

        public static bool IsEquipmentSlotOffset(byte? fieldOffset)
        {
            return fieldOffset.HasValue &&
                   fieldOffset.Value >= FirstSlotOffset &&
                   fieldOffset.Value <= LastSlotOffset;
        }

        public static bool IsBackpackSlotOffset(byte? fieldOffset)
        {
            return fieldOffset.HasValue &&
                   fieldOffset.Value >= FirstBackpackSlotOffset &&
                   fieldOffset.Value <= LastBackpackSlotOffset;
        }

        public static string GetSlotFieldLabel(byte? fieldOffset)
        {
            if (!fieldOffset.HasValue)
                return null;

            if (IsBackpackSlotOffset(fieldOffset))
                return $"backpack слот {fieldOffset.Value - FirstBackpackSlotOffset + 1}";

            if (IsEquipmentSlotOffset(fieldOffset))
                return $"слот инвентаря {fieldOffset.Value - FirstSlotOffset + 1}";

            return null;
        }

        public static string GetSlotFieldLabel(PartyFieldReference fieldRef)
        {
            if (fieldRef == null)
                return null;

            if (fieldRef.FieldOffsetRange != null && !fieldRef.FieldOffsetRange.IsExact)
                return null;

            byte? offset = fieldRef.FieldOffsetRange != null && fieldRef.FieldOffsetRange.IsExact
                ? fieldRef.FieldOffsetRange.Min
                : fieldRef.FieldOffset;

            return GetSlotFieldLabel(offset);
        }

        public static string GetSlotFieldLabel(PartyPredicate predicate)
        {
            if (predicate == null)
                return null;

            if (predicate.FieldOffsetRange != null && !predicate.FieldOffsetRange.IsExact)
                return null;

            byte? offset = predicate.FieldOffsetRange != null && predicate.FieldOffsetRange.IsExact
                ? predicate.FieldOffsetRange.Min
                : predicate.FieldOffset;

            return GetSlotFieldLabel(offset);
        }

        public static bool TryFormatItemCode(byte? fieldOffset, ushort value, out string itemText)
        {
            itemText = null;
            if (!IsInventorySlotOffset(fieldOffset))
                return false;

            return TryFormatItemCode(value, allowEmpty: true, out itemText);
        }

        public static bool TryFormatItemCode(PartyPredicate predicate, ushort value, out string itemText)
        {
            itemText = null;
            if (predicate == null ||
                !IsInventorySlotReference(predicate.FieldOffset, predicate.FieldOffsetRange))
            {
                return false;
            }

            return TryFormatItemCode(value, allowEmpty: true, out itemText);
        }

        public static bool IsInventorySlotField(PartyFieldReference fieldRef)
        {
            return fieldRef != null &&
                   fieldRef.Field == PartyFieldKind.Unknown &&
                   IsInventorySlotReference(fieldRef.FieldOffset, fieldRef.FieldOffsetRange);
        }

        public static bool IsInventoryItemPresencePredicate(PartyPredicate predicate)
        {
            return predicate != null &&
                   predicate.Field == PartyFieldKind.Unknown &&
                   IsInventorySlotReference(predicate.FieldOffset, predicate.FieldOffsetRange) &&
                   predicate.ImmediateValue.HasValue &&
                   (predicate.Comparison == PartyPredicateComparison.Equal ||
                    predicate.Comparison == PartyPredicateComparison.NotEqual);
        }

        public static bool TryBuildItemPresenceChoiceLabel(BranchChoice choice, out string label)
        {
            label = null;
            if (!TryBuildItemPresenceChoiceParts(choice, out string itemName, out string presenceText))
                return false;

            string slotLabel = GetSlotFieldLabel(choice?.ComparedPartyField);
            label = string.IsNullOrWhiteSpace(slotLabel)
                ? $"{itemName} {presenceText}"
                : $"{slotLabel}: {itemName} {presenceText}";
            return true;
        }

        public static bool TryBuildItemPresenceChoiceParts(
            BranchChoice choice,
            out string itemName,
            out string presenceText)
        {
            itemName = null;
            presenceText = null;
            if (choice == null ||
                !IsInventorySlotField(choice.ComparedPartyField) ||
                !choice.CompareValue.HasValue ||
                !TryFormatItemCode(choice.CompareValue.Value, allowEmpty: false, out string resolvedItemName))
            {
                return false;
            }

            var comparison = choice.GuardPredicate?.Comparison ?? PartyPredicateComparison.Unknown;
            if (comparison == PartyPredicateComparison.Unknown)
                comparison = InferComparisonFromCondition(choice);

            switch (comparison)
            {
                case PartyPredicateComparison.Equal:
                    itemName = resolvedItemName.Trim();
                    presenceText = "есть";
                    return true;
                case PartyPredicateComparison.NotEqual:
                    itemName = resolvedItemName.Trim();
                    presenceText = "отсутствует";
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryFormatItemCode(ushort value, bool allowEmpty, out string itemText)
        {
            itemText = null;
            if (value == 0)
            {
                if (!allowEmpty)
                    return false;

                itemText = "пусто";
                return true;
            }

            if (value > byte.MaxValue)
                return false;

            byte itemCode = (byte)value;
            if (ItemDatabase.TryGetItemNameByGameItemCode(itemCode, out string itemName) &&
                !string.IsNullOrWhiteSpace(itemName))
            {
                itemText = itemName.Trim();
                return true;
            }

            itemText = $"ID предмета 0x{itemCode:X2}";
            return true;
        }

        public static bool TryFormatItemCodeRange(
            ushort minValue,
            ushort maxValueInclusive,
            out string itemText)
        {
            itemText = null;
            if (minValue > maxValueInclusive ||
                minValue > byte.MaxValue ||
                maxValueInclusive > byte.MaxValue)
            {
                return false;
            }

            var itemNames = new System.Collections.Generic.List<string>();
            for (ushort value = minValue; value <= maxValueInclusive; value++)
            {
                if (!TryFormatItemCode(value, allowEmpty: false, out string singleItemText) ||
                    string.IsNullOrWhiteSpace(singleItemText))
                {
                    return false;
                }

                itemNames.Add(singleItemText.Trim());
            }

            itemText = FormatItemNameRange(itemNames);
            return !string.IsNullOrWhiteSpace(itemText);
        }

        private static string FormatItemNameRange(System.Collections.Generic.List<string> itemNames)
        {
            if (itemNames == null || itemNames.Count == 0)
                return null;

            if (itemNames.Count == 1)
                return itemNames[0];

            string sharedSuffix = TryGetSharedTrailingWordSuffix(itemNames);
            if (!string.IsNullOrWhiteSpace(sharedSuffix))
            {
                var prefixes = itemNames
                    .Select(name => name.Substring(0, name.Length - sharedSuffix.Length).TrimEnd())
                    .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                    .ToList();

                if (prefixes.Count == itemNames.Count)
                    return $"{string.Join("/", prefixes)}{sharedSuffix}";
            }

            return string.Join("/", itemNames);
        }

        private static string TryGetSharedTrailingWordSuffix(System.Collections.Generic.List<string> itemNames)
        {
            string first = itemNames?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first))
                return null;

            int separatorIndex = first.LastIndexOf(' ');
            if (separatorIndex <= 0 || separatorIndex >= first.Length - 1)
                return null;

            string suffix = first.Substring(separatorIndex);
            return itemNames.All(name =>
                !string.IsNullOrWhiteSpace(name) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                ? suffix
                : null;
        }

        private static PartyPredicateComparison InferComparisonFromCondition(BranchChoice choice)
        {
            string mnemonic = ExtractMnemonic(choice?.Condition);
            if (string.IsNullOrWhiteSpace(mnemonic))
                return PartyPredicateComparison.Unknown;

            return mnemonic.ToUpperInvariant() switch
            {
                "JE" or "JZ" => choice.IsLinear ? PartyPredicateComparison.NotEqual : PartyPredicateComparison.Equal,
                "JNE" or "JNZ" => choice.IsLinear ? PartyPredicateComparison.Equal : PartyPredicateComparison.NotEqual,
                _ => PartyPredicateComparison.Unknown
            };
        }

        private static string ExtractMnemonic(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                return null;

            const string linearPrefix = "LINEAR after ";
            string trimmed = condition.Trim();
            if (trimmed.StartsWith(linearPrefix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(linearPrefix.Length).TrimStart();

            int separatorIndex = trimmed.IndexOf(' ');
            return separatorIndex >= 0
                ? trimmed.Substring(0, separatorIndex).Trim()
                : trimmed;
        }
    }
}
