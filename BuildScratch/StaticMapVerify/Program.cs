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

using System.Drawing;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MMMapEditor;

bool debug = args.Any(arg => string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase));
string filename = args.FirstOrDefault(arg => !string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase))
    ?? @"C:\GOG Games\Might and Magic 1\AREAB3.OVR";

if (debug)
{
    Trace.Listeners.Clear();
    Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
    Trace.AutoFlush = true;
    AnalysisDebug.Configure(enabled: true, targetX: 6, targetY: 0, enableGlobalLogs: true, disableCacheForTargetCell: true);
}

string configKey = Path.GetFileName(filename).ToUpperInvariant();
var config = OvrFileConfigs.Configs[configKey];
var centralOptions = BuildCentralOptions(config);
var analyzedObjects = OvrFileAnalyzer.AnalyzeOvrFile(
    filename,
    config,
    new Dictionary<Point, string>(centralOptions));
var objectDistribution = analyzedObjects
    .Where(obj => !obj.IsFromTable)
    .GroupBy(obj => obj.PathVariants?.Count ?? 0)
    .OrderBy(group => group.Key)
    .Select(group => $"objectVariants={group.Key}: cells={group.Count()}");
Console.WriteLine(string.Join(Environment.NewLine, objectDistribution));

var staticMapObjects = analyzedObjects
    .Where(obj => !obj.IsFromTable)
    .Where(obj => obj.PathVariants?.Values.Any(variant => variant?.UsesStaticMapData == true) == true)
    .OrderBy(obj => obj.Y)
    .ThenBy(obj => obj.X)
    .Select(obj => $"({obj.X},{obj.Y})/{obj.PathVariants.Count}");
Console.WriteLine("staticMapObjects=" +
    (staticMapObjects.Any() ? string.Join("; ", staticMapObjects) : "<none>"));

var result = OvrNotesBuilder.BuildNotes(
    filename,
    centralOptions,
    useHierarchicalView: false,
    preAnalyzedObjects: analyzedObjects);

var distribution = new SortedDictionary<int, int>();
var cellsWithThreeVariants = new List<Point>();

foreach (var entry in result.NotesPerCell.OrderBy(kvp => kvp.Key.Y).ThenBy(kvp => kvp.Key.X))
{
    string note = entry.Value ?? string.Empty;
    int variantCount = Regex.Matches(note, @"(?m)^Вариант\s+\d+").Count;
    if (variantCount <= 0)
        continue;

    distribution.TryGetValue(variantCount, out int current);
    distribution[variantCount] = current + 1;

    if (variantCount == 3)
        cellsWithThreeVariants.Add(entry.Key);
}

foreach (var entry in distribution)
    Console.WriteLine($"variants={entry.Key}: cells={entry.Value}");

Console.WriteLine("cellsWithThreeVariants=" +
    (cellsWithThreeVariants.Count == 0
        ? "<none>"
        : string.Join("; ", cellsWithThreeVariants.Select(point => $"({point.X},{point.Y})"))));

static Dictionary<Point, string> BuildCentralOptions(OvrFileConfig config)
{
    var centralOptions = new Dictionary<Point, string>();

    for (int y = 0; y < 16; y++)
    {
        string[] secondLayer = config.Second16Lines[y]
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        for (int x = 0; x < 16; x++)
        {
            int value = Convert.ToInt32(secondLayer[x], 16);
            centralOptions[new Point(x, y)] = (value & 0x80) != 0
                ? "Случайная встреча"
                : "Пустота";
        }
    }

    return centralOptions;
}
