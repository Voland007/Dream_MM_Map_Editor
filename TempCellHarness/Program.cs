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

using System.Diagnostics;
using System.Drawing;
using MMMapEditor;

string ovrPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MMMapEditor", "bin", "Debug", "net6.0-windows", "CAVE4.OVR"));

byte targetX = args.Length > 1 && byte.TryParse(args[1], out byte parsedX) ? parsedX : (byte)1;
byte targetY = args.Length > 2 && byte.TryParse(args[2], out byte parsedY) ? parsedY : (byte)3;

Console.WriteLine($"TEMP_HARNESS_START file={ovrPath} cell=({targetX},{targetY})");

if (!File.Exists(ovrPath))
{
    Console.WriteLine("TEMP_HARNESS_ERROR file_not_found");
    return;
}

AnalysisDebug.Configure(enabled: false);

var seedCentralOptions = new Dictionary<Point, string>
{
    [new Point(targetX, targetY)] = "Случайная встреча"
};

var sw = Stopwatch.StartNew();
var loadResult = OvrOverlayLoader.Load(
    ovrPath,
    seedCentralOptions,
    useHierarchicalView: false);
sw.Stop();

Console.WriteLine($"TEMP_HARNESS_DONE ms={sw.ElapsedMilliseconds} notes={loadResult.NotesPerCell.Count} objects={loadResult.TotalObjects}");

var targetPoint = new Point(targetX, targetY);
if (loadResult.NotesPerCell.TryGetValue(targetPoint, out string? note))
{
    Console.WriteLine("TEMP_HARNESS_NOTE_BEGIN");
    Console.WriteLine(note);
    Console.WriteLine("TEMP_HARNESS_NOTE_END");
}
else
{
    Console.WriteLine("TEMP_HARNESS_NOTE_MISSING");
}
