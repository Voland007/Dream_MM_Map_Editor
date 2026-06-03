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
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MMMapEditor
{
    internal static class OvrAnalysisPerfStats
    {
#if DEVELOPMENT_TOOLS
        private sealed class Entry
        {
            public int AnalyzeCalls { get; set; }
            public int CacheHits { get; set; }
            public int SingleOccurrencePasses { get; set; }
            public int CacheRejectedByCoordinateUsage { get; set; }
            public int FastForwardSuccesses { get; set; }
            public int FastForwardMultiChangeRejects { get; set; }
            public int FastForwardConstraintRejects { get; set; }
            public int FastForwardPlanRejects { get; set; }
            public int FastForwardOtherRejects { get; set; }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static volatile bool Enabled;

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void Reset()
        {
            lock (Sync)
            {
                Enabled = true;
                Entries.Clear();
            }
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void Disable()
        {
            Enabled = false;
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordAnalyzeCall(string key, bool cacheHit)
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(key))
                key = "<NO_CACHE_KEY>";

            lock (Sync)
            {
                var entry = GetOrCreate(key);
                entry.AnalyzeCalls++;
                if (cacheHit)
                    entry.CacheHits++;
            }
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordSingleOccurrencePass(string key)
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(key))
                key = "<NO_CACHE_KEY>";

            lock (Sync)
                GetOrCreate(key).SingleOccurrencePasses++;
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordCoordinateUsageCacheRejection(string key)
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(key))
                key = "<NO_CACHE_KEY>";

            lock (Sync)
                GetOrCreate(key).CacheRejectedByCoordinateUsage++;
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordFastForwardDecision(string key, string category)
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(key))
                key = "<NO_CACHE_KEY>";

            category ??= "other";

            lock (Sync)
            {
                var entry = GetOrCreate(key);
                switch (category)
                {
                    case "success":
                        entry.FastForwardSuccesses++;
                        break;
                    case "multi":
                        entry.FastForwardMultiChangeRejects++;
                        break;
                    case "constraint":
                        entry.FastForwardConstraintRejects++;
                        break;
                    case "plan":
                        entry.FastForwardPlanRejects++;
                        break;
                    default:
                        entry.FastForwardOtherRejects++;
                        break;
                }
            }
        }

        public static string BuildSummary(int top = 20)
        {
            if (!Enabled)
                return string.Empty;

            lock (Sync)
            {
                var sb = new StringBuilder();
                foreach (var kvp in Entries
                             .OrderByDescending(item => item.Value.SingleOccurrencePasses)
                             .ThenByDescending(item => item.Value.AnalyzeCalls)
                             .Take(Math.Max(1, top)))
                {
                    sb.Append(kvp.Key)
                        .Append(" | calls=").Append(kvp.Value.AnalyzeCalls)
                        .Append(" | hits=").Append(kvp.Value.CacheHits)
                        .Append(" | passes=").Append(kvp.Value.SingleOccurrencePasses)
                        .Append(" | coordRejects=").Append(kvp.Value.CacheRejectedByCoordinateUsage)
                        .Append(" | ff=").Append(kvp.Value.FastForwardSuccesses)
                        .Append('/').Append(kvp.Value.FastForwardMultiChangeRejects)
                        .Append('/').Append(kvp.Value.FastForwardConstraintRejects)
                        .Append('/').Append(kvp.Value.FastForwardPlanRejects)
                        .Append('/').Append(kvp.Value.FastForwardOtherRejects)
                        .AppendLine();
                }

                return sb.ToString();
            }
        }

        private static Entry GetOrCreate(string key)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                Entries[key] = entry;
            }

            return entry;
        }
#else
        [Conditional("DEVELOPMENT_TOOLS")]
        public static void Reset()
        {
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void Disable()
        {
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordAnalyzeCall(string key, bool cacheHit)
        {
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordSingleOccurrencePass(string key)
        {
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordCoordinateUsageCacheRejection(string key)
        {
        }

        [Conditional("DEVELOPMENT_TOOLS")]
        public static void RecordFastForwardDecision(string key, string category)
        {
        }

        public static string BuildSummary(int top = 20)
        {
            return string.Empty;
        }
#endif
    }
}
