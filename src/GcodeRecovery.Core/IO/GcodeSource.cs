using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GcodeRecovery.Core.IO;

/// <summary>
/// Where the G-code came from: a plain .gcode file or a plate inside a Bambu-style .gcode.3mf archive.
/// Knowing the origin lets the recovery file be written back in the same container.
/// </summary>
public sealed partial class GcodeSource
{
    [GeneratedRegex(@"^Metadata/plate_(\d+)\.gcode$", RegexOptions.IgnoreCase)]
    private static partial Regex PlateEntry();

    public required string FilePath { get; init; }
    public required List<string> Lines { get; init; }

    /// <summary>Zip entry name of the plate G-code when loaded from a 3MF archive; otherwise null.</summary>
    public string? ArchiveEntry { get; init; }

    public bool IsArchive => ArchiveEntry is not null;

    public static IReadOnlyList<string> ListPlates(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).Where(n => PlateEntry().IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static bool LooksLikeArchive(string path)
    {
        if (path.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) return true;
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return fs.Read(magic) == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;
    }

    /// <summary>Loads a .gcode file, or the given (default: first) plate of a .gcode.3mf archive.</summary>
    public static GcodeSource Load(string path, string? plateEntry = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("G-code file not found.", path);
        if (!LooksLikeArchive(path))
            return new GcodeSource { FilePath = path, Lines = ReadLines(File.OpenRead(path)) };

        using var zip = ZipFile.OpenRead(path);
        var plates = zip.Entries.Where(e => PlateEntry().IsMatch(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        if (plates.Count == 0)
            throw new InvalidDataException("This 3MF has no sliced plate G-code (Metadata/plate_N.gcode). Export it from the slicer with \"Export plate sliced file\".");
        var entry = plateEntry is null ? plates[0] : plates.FirstOrDefault(e => e.FullName == plateEntry)
            ?? throw new InvalidDataException($"Plate '{plateEntry}' not found in archive.");
        return new GcodeSource { FilePath = path, ArchiveEntry = entry.FullName, Lines = ReadLines(entry.Open()) };
    }

    private static List<string> ReadLines(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>(1 << 16);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>Writes plain G-code with LF line endings.</summary>
    public static void WriteGcode(string outputPath, IEnumerable<string> lines)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (var l in lines) writer.WriteLine(l);
    }

    /// <summary>
    /// Copies the original 3MF archive and replaces the plate G-code (and its .md5 companion, which
    /// Bambu firmware checks) with the recovery program.
    /// </summary>
    public static void WriteArchive(string sourceArchive, string entryName, string outputPath, IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var l in lines) text.Append(l).Append('\n');
        var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
        var md5 = Convert.ToHexString(MD5.HashData(bytes));

        var full = Path.GetFullPath(outputPath);
        if (string.Equals(full, Path.GetFullPath(sourceArchive), StringComparison.Ordinal))
            throw new IOException("Refusing to overwrite the original file. Choose a different output name.");

        File.Copy(sourceArchive, full, overwrite: true);
        using var zip = ZipFile.Open(full, ZipArchiveMode.Update);
        Replace(zip, entryName, bytes);
        if (zip.GetEntry(entryName + ".md5") is not null)
            Replace(zip, entryName + ".md5", Encoding.ASCII.GetBytes(md5));
    }

    private static void Replace(ZipArchive zip, string name, byte[] content)
    {
        zip.GetEntry(name)?.Delete();
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(content);
    }
}
