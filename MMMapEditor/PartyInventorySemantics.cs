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

        public static bool IsInventorySlotOffset(byte? fieldOffset)
        {
            return fieldOffset.HasValue &&
                   fieldOffset.Value >= FirstSlotOffset &&
                   fieldOffset.Value <= LastSlotOffset;
        }

        public static bool IsInventorySlotField(PartyFieldReference fieldRef)
        {
            return fieldRef != null &&
                   fieldRef.Field == PartyFieldKind.Unknown &&
                   IsInventorySlotOffset(fieldRef.FieldOffset);
        }

        public static bool IsInventoryItemPresencePredicate(PartyPredicate predicate)
        {
            return predicate != null &&
                   predicate.Field == PartyFieldKind.Unknown &&
                   IsInventorySlotOffset(predicate.FieldOffset) &&
                   predicate.ImmediateValue.HasValue &&
                   (predicate.Comparison == PartyPredicateComparison.Equal ||
                    predicate.Comparison == PartyPredicateComparison.NotEqual);
        }

        public static bool TryBuildItemPresenceChoiceLabel(BranchChoice choice, out string label)
        {
            label = null;
            if (!TryBuildItemPresenceChoiceParts(choice, out string itemName, out string presenceText))
                return false;

            label = $"{itemName} {presenceText}";
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
                !ItemDatabase.TryGetItemNameByGameItemCode(choice.CompareValue.Value, out string resolvedItemName) ||
                string.IsNullOrWhiteSpace(resolvedItemName))
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
