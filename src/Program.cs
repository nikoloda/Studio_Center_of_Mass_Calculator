// =============================================================================
// Studio Center of Mass Calculator
// =============================================================================
// BrickLink Studio saves models as ".io" files. They can be renamed and treated as ZIP archives
// containing model2.ldr (BrickLink part IDs + positions), model.ldr, thumbnail.png, .info, etc.
//
// This program:
//   1. Unpacks the .io file
//   2. Reads each part's position from model2.ldr (Contains .dat files which correspond to BrickLink catalog IDs)
//   3. Fetches weight + size from the BrickLink API
//   4. Calculates the center of mass (balance point)
//   5. Copies the model and adds a red technic ball part at that point (in model.ldr)
//   6. Repacks everything into a new .io file
// =============================================================================

using System.Globalization;       // CultureInfo: parse numbers like "1.35" reliably
using System.IO.Compression;      // ZipFile: treat .io files as ZIP archives
using System.Numerics;            // Vector3: 3D positions (X, Y, Z)
using System.Text;                // Encoding for text/HTTP
using System.Text.Json.Nodes;     // Edit JSON files (.info) easily
using System.Text.RegularExpressions; // Regex: pattern-match LDraw file lines

// BrickLink.cs is in the same namespace and handles the BrickLink API calls.
namespace StudioCenterOfMass;

internal static class Program
{
    // Center of Mass Marker: red technic ball
    const int MarkerColor = 4;              // 4 is red in LDraw
    const string MarkerPartNo = "32474";

    // UTF-8 encoding expected by Studio for .ldr text files.
    static readonly UTF8Encoding Utf8 = new(false);

    // Regeular expression which matches LDraw type-1 placement lines. Each line has, in order:
    //   line type (always 1), color, X/Y/Z position, nine rotation numbers, then a part or submodel name.
    // Examples:
    //   1 15 -60.0 -24.0 0.0 1 0 0 0 1 0 0 0 1 3003.dat          | (color 15, brick 3003)
    //   1 -1 282.0 -69.0 80.96 1 0 0 0 1 0 0 0 1 submodel group 1  | (color -1, nested submodel)
    // The regex function uses named groups x, y, z, m, and "part" to capture these fields after a successful match.

    static readonly Regex Type1Line = new(
        @"^1\s+-?\d+\s+(?<x>[-\d.]+)\s+(?<y>[-\d.]+)\s+(?<z>[-\d.]+)\s+(?<m>[-\d.]+(?:\s+[-\d.]+){8})\s+(?<part>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static bool _pauseOnExit;

    static async Task<int> Main(string[] args)
    {
        _pauseOnExit = args.Length == 1
            && args[0].EndsWith(".io", StringComparison.OrdinalIgnoreCase);

        if (args is not [var input, ..] || args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine("""
                Usage: StudioCenterOfMass <input.io> [output.io]

                Or drag a .io file onto DropHere.bat.

                Requires a .env file in the project root (copy .env.example to .env):
                  BRICKLINK_CONSUMER_KEY, BRICKLINK_CONSUMER_SECRET,
                  BRICKLINK_TOKEN (or BRICKLINK_TOKEN_VALUE), BRICKLINK_TOKEN_SECRET
                """);
            return args.Length == 0 ? 1 : 0;
        }
        // Get input io file
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) return Fail($"Not found: {input}");

        // Duplicate input file to create output io file
        var output = args.Length >= 2 ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetDirectoryName(input)!, $"{Path.GetFileNameWithoutExtension(input)}_com.io");

        // Work in a temp folder so to avoid cluttering the user's directory.
        var work = Path.Combine(Path.GetTempPath(), $"studio_com_{Guid.NewGuid():N}");
        var src = Path.Combine(work, "src");  // extracted original
        var dst = Path.Combine(work, "dst");  // copy we modify before zipping

        try
        {
            PrintHeader(Path.GetFileName(input), Path.GetFileName(output));

            // Step 1: Unpack the .io (ZIP) archive
            PrintStep("Unpacking model...");
            Directory.CreateDirectory(src);
            ZipFile.ExtractToDirectory(input, src);

            // model2.ldr has BrickLink IDs + positions; weight and size are not stored in the .io file
            var catalogPath = Path.Combine(src, "model2.ldr");
            if (!File.Exists(catalogPath)) return Fail("Missing model2.ldr. Not a Studio .io file?");

            // Step 2: Look up weight + dimensions from BrickLink

            // Connect to BrickLink API
            var api = new BrickLinkApi(BrickLinkApi.LoadCredentials());

            // Parse the model2.ldr file to get the list of parts in the model
            var parts = ParsePlacements(catalogPath);
            if (parts.Count == 0) return Fail("No parts in model2.ldr.");

            var partTypes = parts.Select(p => p.PartNo).Append(MarkerPartNo).Distinct().Count();
            PrintStep($"Looking up {partTypes} part types on BrickLink...");

            // Load weight and size for each unique part number from BrickLink to fill our internal cache with everything needed to compute the CoM.
            await api.LoadPartsAsync(parts.Select(p => p.PartNo).Append(MarkerPartNo));

            // api.Parts is a dictionary keyed by part number, example:
            //   "3003" → PartProperties { WeightGrams = 1.35, LocalCenterOfMass = (0, 12, 0) }
            //   "3020" → PartProperties { WeightGrams = 1.20, LocalCenterOfMass = (0, 4, 0) }
            var partProperties = api.Parts;
            var com = ComputeCenterOfMass(parts, partProperties);
            var mass = parts.Sum(p => partProperties[p.PartNo].WeightGrams);

            PrintStep("Calculating center of mass...");
            PrintResults(com, mass, parts.Count);

            // Step 3: Copy original model, add marker to copy, update metadata
            PrintStep("Writing output...");
            CopyTree(src, dst);

            // Place the marker so its geometric CENTER sits on the CoM, not its origin point.
            // LDraw part origins are at the top of studs, so we subtract the local offset.
            var markerOrigin = com - partProperties[MarkerPartNo].LocalCenterOfMass;
            InsertMarker(Path.Combine(dst, "model.ldr"), false, markerOrigin);
            if (File.Exists(Path.Combine(dst, "modelv2.ldr")))
                InsertMarker(Path.Combine(dst, "modelv2.ldr"), true, markerOrigin);
            PatchInfo(Path.Combine(dst, ".info"), parts.Count + 1);

            // Step 4: Repack into a new .io output file 
            if (File.Exists(output)) File.Delete(output);
            ZipFile.CreateFromDirectory(dst, output, CompressionLevel.Optimal, includeBaseDirectory: false);
            PrintDone(output);
            MaybePause();
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            // finally ALWAYS runs to clean up the temp folder even after return or catch.
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    static int Fail(string msg)
    {
        Console.WriteLine();
        WriteError(msg);
        MaybePause();
        return 1;
    }

    static void PrintHeader(string inputName, string outputName)
    {
        Console.WriteLine();
        Console.WriteLine("Studio Center of Mass Calculator");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"  Input:  {inputName}");
        Console.WriteLine($"  Output: {outputName}");
        Console.WriteLine();
    }

    static void PrintStep(string msg) => Console.WriteLine($"  {msg}");

    static void PrintResults(Vector3 com, double mass, int partCount)
    {
        Console.WriteLine();
        Console.WriteLine("  Result");
        Console.WriteLine($"    Center of mass: ({com.X:F3}, {com.Y:F3}, {com.Z:F3}) LDU");
        Console.WriteLine($"    Total mass:     {mass:F2} g ({partCount} parts)");
        Console.WriteLine();
    }

    static void PrintDone(string outputPath)
    {
        Console.WriteLine("  Done.");
        Console.WriteLine($"    {outputPath}");
    }

    static void WriteError(string msg)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"  Error: {msg}");
        Console.ForegroundColor = prev;
    }

    // If _pauseOnExit is true, pause the program after completion to allow user to see results when dragging a .io file onto DropHere.bat
    static void MaybePause()
    {
        if (!_pauseOnExit) return;
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    // =========================================================================
    // Center of mass math
    // =========================================================================
    // CoM = Σ (mass × position) / totalMass. Heavier parts pull the balance point toward them.
    // model2.ldr gives each part's origin + rotation; BrickLink gives weight + height (for local CoM).
    // =========================================================================

    static Vector3 ComputeCenterOfMass(List<Placement> parts, IReadOnlyDictionary<string, PartProperties> partProperties)
    {
        double totalMass = 0;
        var weightedSum = Vector3.Zero;

        foreach (var p in parts)
        {
            var props = partProperties[p.PartNo];
            var position = LdrawTransform(p.Matrix, p.Pos, props.LocalCenterOfMass);
            weightedSum += position * (float)props.WeightGrams;
            totalMass += props.WeightGrams;
        }

        if (totalMass <= 0) throw new InvalidOperationException("Total mass is zero.");
        return weightedSum / (float)totalMass;
    }

    // "4073.dat" from model2.ldr → "4073" (already a BrickLink item number).
    static string PartNumber(string datFilename) => Path.GetFileNameWithoutExtension(datFilename);

    static string ToLdrawFile(string partNo) => $"{partNo}.dat";

    static bool IsDatFile(string name) => name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);

    // LDraw stores the 3×3 rotation matrix in COLUMN order. LDU: 1 stud = 20, 1 brick = 24.
    static Vector3 LdrawTransform(float[] m, Vector3 t, Vector3 p) => new(
        m[0] * p.X + m[3] * p.Y + m[6] * p.Z + t.X,
        m[1] * p.X + m[4] * p.Y + m[7] * p.Z + t.Y,
        m[2] * p.X + m[5] * p.Y + m[8] * p.Z + t.Z);

    // Read model2.ldr: expand submodels recursively, skip embedded part-geometry sections.
    static List<Placement> ParsePlacements(string path)
    {
        var (rootName, sections) = ParseAssemblySections(path);
        if (rootName is null || !sections.ContainsKey(rootName))
            throw new InvalidOperationException("model2.ldr has no root assembly section.");

        var parts = new List<Placement>();
        ExpandSubmodel(sections, rootName, Matrix.Identity, Vector3.Zero, parts, []);
        return parts;
    }

    // Split model2.ldr into MPD sections ("0 FILE name" … "0 NOFILE").
    // Sections named "*.dat" hold mesh data for rendering, not build steps, so we skip them.
    static (string? RootName, Dictionary<string, List<string>> Sections) ParseAssemblySections(string path)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? root = null, current = null;
        bool skip = false;

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("0 FILE ", StringComparison.Ordinal))
            {
                current = line["0 FILE ".Length..].Trim();
                root ??= current;
                skip = IsDatFile(current);
                if (!skip) sections.TryAdd(current, []);
                continue;
            }
            if (line.TrimStart().StartsWith("0 NOFILE", StringComparison.Ordinal))
            {
                current = null;
                skip = false;
                continue;
            }
            if (current is not null && !skip)
                sections[current].Add(line);
        }

        return (root, sections);
    }

    // Walk the submodel tree: .dat lines become parts; other names recurse into nested submodels.
    static void ExpandSubmodel(
        Dictionary<string, List<string>> sections,
        string name,
        float[] parentMatrix,
        Vector3 parentPos,
        List<Placement> results,
        HashSet<string> visiting)
    {
        if (!sections.TryGetValue(name, out var lines))
            throw new InvalidOperationException($"Unknown submodel '{name}' in model2.ldr.");

        if (!visiting.Add(name.ToUpperInvariant()))
            throw new InvalidOperationException($"Circular submodel reference: {name}");

        foreach (string line in lines)
        {
            var local = TryParsePlacement(line);
            if (local is null) continue;

            var (worldMatrix, worldPos) = ComposeTransform(parentMatrix, parentPos, local.Value.Matrix, local.Value.Pos);

            if (IsDatFile(local.Value.PartRef))
            {
                string partNo = PartNumber(local.Value.PartRef);
                if (!IsMarkerPart(partNo))
                    results.Add(new Placement(worldPos, worldMatrix, partNo));
            }
            else
                ExpandSubmodel(sections, local.Value.PartRef, worldMatrix, worldPos, results, visiting);
        }

        visiting.Remove(name.ToUpperInvariant());
    }

    static bool IsMarkerPart(string partNo) =>
        partNo.Equals(MarkerPartNo, StringComparison.OrdinalIgnoreCase);

    static (float[] Matrix, Vector3 Pos) ComposeTransform(float[] parentM, Vector3 parentT, float[] localM, Vector3 localT)
    {
        var combined = new float[9];
        MultiplyMatrix3x3(parentM, localM, combined);
        return (combined, LdrawTransform(parentM, parentT, localT));
    }

    static void MultiplyMatrix3x3(float[] a, float[] b, float[] result)
    {
        for (int col = 0; col < 3; col++)
            for (int row = 0; row < 3; row++)
                result[row + col * 3] =
                    a[row] * b[col * 3] + a[row + 3] * b[1 + col * 3] + a[row + 6] * b[2 + col * 3];
    }

    static ParsedLine? TryParsePlacement(string line)
    {
        // Run the regex; if the line is not type-1 format, m.Success is false.
        var m = Type1Line.Match(line.Trim());
        if (!m.Success) return null;

        // m.Groups["m"] is the nine rotation numbers as one string, e.g. "1 0 0 0 1 0 0 0 1".
        var matrix = m.Groups["m"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        // m.Groups["x"], ["y"], ["z"] are the position; ["part"] is "3003.dat" or a submodel name.
        // InvariantCulture: LDraw always uses "." as the decimal separator.
        return new ParsedLine(
            new Vector3(
                float.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture)),
            matrix,
            m.Groups["part"].Value.Trim());
    }

    // Editing the model files inside the .io archive

    // Insert the marker line just before the trailing "0 NOFILE" line.
    static void InsertMarker(string path, bool v2, Vector3 pos)
    {
        var lines = File.ReadAllLines(path).ToList();

        int end = lines.FindIndex(l => l.TrimStart().StartsWith("0 NOFILE", StringComparison.Ordinal));
        if (end <= 0) throw new InvalidOperationException($"Bad model file: {path}");

        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Contains("NumOfBricks", StringComparison.Ordinal))
                lines[i] = Regex.Replace(lines[i], @"(\d+)\s*$", m =>
                    (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));

        var id = Matrix.Identity;

        // modelv2.ldr uses line type "11" with extra Studio fields (instance ID, etc.)
        lines.Insert(end, v2
            ? $"11 {MarkerColor} {Random.Shared.NextInt64()} False 0 {Fmt(pos, id, ToLdrawFile(MarkerPartNo))}"
            : $"1 {MarkerColor} {Fmt(pos, id, ToLdrawFile(MarkerPartNo))}");

        File.WriteAllLines(path, lines, Utf8);
    }

    static string Fmt(Vector3 p, float[] m, string part) => string.Format(CultureInfo.InvariantCulture,
        "{0:F6} {1:F6} {2:F6} {3:F6} {4:F6} {5:F6} {6:F6} {7:F6} {8:F6} {9:F6} {10:F6} {11:F6} {12}",
        p.X, p.Y, p.Z, m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], part);

    // .info is a small JSON file Studio uses for metadata (part count, version, etc.)
    static void PatchInfo(string path, int totalParts)
    {
        if (!File.Exists(path)) return;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["total_parts"] = totalParts;
            File.WriteAllText(path, json.ToJsonString());
        }
        catch { /* non-critical */ }
    }

    static void CopyTree(string src, string dst)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    // One placed part in world space: position, 3×3 rotation, BrickLink part number.
    readonly record struct Placement(Vector3 Pos, float[] Matrix, string PartNo);

    // Raw type-1 line before submodel expansion (PartRef is "3003.dat" or a submodel name).
    readonly record struct ParsedLine(Vector3 Pos, float[] Matrix, string PartRef);

    static class Matrix
    {
        public static float[] Identity => [1, 0, 0, 0, 1, 0, 0, 0, 1];
    }
}

