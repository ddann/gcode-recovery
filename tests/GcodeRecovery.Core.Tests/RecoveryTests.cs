using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.IO;
using GcodeRecovery.Core.Recovery;

namespace GcodeRecovery.Core.Tests;

public class RecoveryPlanTests
{
    [Fact]
    public void Shift_mode_puts_contact_at_zero_and_resume_layer_at_its_thickness()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions());
        Assert.Equal(6.0, plan.SurfaceLayer.Z, 6);
        Assert.Equal(6.2, plan.ResumeLayer.Z, 6);
        Assert.Equal(0, plan.ContactZ, 6);
        Assert.Equal(0.2, plan.ResumeZ, 6);
        Assert.Equal(6.0, plan.ZShift, 6);
    }

    [Fact]
    public void First_layer_gap_mode_uses_first_layer_height_from_the_file()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 60, layerHeight: 0.12, firstLayer: 0.25, style: SyntheticGcode.Style.Orca));
        var plan = RecoveryPlan.Create(model, 40, 110, 110, new RecoveryOptions { Gap = ResumeGapMode.FirstLayerHeight });
        Assert.Equal(0, plan.ContactZ, 6);
        Assert.Equal(0.25, plan.ResumeZ, 6);
    }

    [Fact]
    public void Keep_mode_preserves_original_coordinates()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions { ZFrame = ZFrameMode.KeepOriginal, TriggerOvertravelMm = 0.02 });
        Assert.Equal(0, plan.ZShift);
        Assert.Equal(6.0, plan.ContactZ, 6);
        Assert.Equal(5.98, plan.ContactZCompensated, 6);
        Assert.Equal(6.2, plan.ResumeZ, 6);
    }

    [Fact]
    public void Rejects_the_final_layer_as_last_completed()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryPlan.Create(model, 9, 0, 0, new RecoveryOptions()));
    }
}

public class ResumeGeneratorTests
{
    private static (GcodeModel Model, RecoveryPlan Plan, GenerationResult Result) Run(PrinterProfile profile, bool absoluteE = false, bool touchTest = false)
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50, absoluteE: absoluteE));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions());
        return (model, plan, ResumeGenerator.Generate(model, plan, profile, touchTest));
    }

    private static List<GcodeLine> Code(IEnumerable<string> lines) => lines.Select(GcodeLine.Parse).Where(l => l.Command.Length > 0).ToList();

    [Fact]
    public void Bambu_output_discards_everything_before_the_resume_layer()
    {
        var (model, plan, result) = Run(BuiltInProfiles.BambuP1S());
        var marker = result.Lines.FindIndex(l => l.StartsWith("; --- Original G-code"));
        Assert.True(marker > 0);
        Assert.Equal("; CHANGE_LAYER", result.Lines[marker + 1]);
        Assert.Equal(model.Lines.Count - plan.ResumeLayer.StartLine, result.KeptLines);
        // The resume layer is reprinted from its very first line: every line of it is present in order.
        var resumeLines = model.Lines.Skip(plan.ResumeLayer.StartLine).Take(plan.ResumeLayer.EndLine - plan.ResumeLayer.StartLine).Count();
        Assert.True(result.Lines.Count - (marker + 1) >= resumeLines);
        Assert.DoesNotContain(result.Lines, l => l == "G1 X60 E5 F1500"); // start purge line of the original
    }

    [Fact]
    public void No_new_bed_leveling_and_no_z_homing()
    {
        foreach (var profile in BuiltInProfiles.All)
        {
            var (_, _, result) = Run(profile);
            var code = Code(result.Lines);
            Assert.DoesNotContain(code, l => l.Command is "G29" or "BED_MESH_CALIBRATE");
            var homing = code.Where(l => l.Command == "G28").Select(l => l.Raw.Split(';')[0]).ToList();
            Assert.NotEmpty(homing);
            Assert.All(homing, h => Assert.Equal("G28 X Y", h.Trim()));
        }
    }

    [Fact]
    public void Probe_happens_cold_before_heating_and_purge()
    {
        foreach (var profile in BuiltInProfiles.All)
        {
            var (_, _, result) = Run(profile);
            var probe = result.Lines.FindIndex(l => l.StartsWith("G380") || l.StartsWith("PROBE "));
            var coldCmd = result.Lines.FindIndex(l => l.StartsWith("M104 S0"));
            var heat = result.Lines.FindIndex(l => l.StartsWith("M109 S220") || l.StartsWith("INNER_PREEXTRUDE_FILAMENT TEMP=220"));
            var purge = result.Lines.FindIndex(l => l.StartsWith("G1 E30") || l.StartsWith("INNER_PREEXTRUDE_FILAMENT"));
            Assert.True(coldCmd >= 0 && coldCmd < probe, profile.Name);
            Assert.True(probe < heat && heat <= purge, profile.Name);
            var setZ = result.Lines.Skip(probe).First(l => l.StartsWith("G92 Z"));
            Assert.Equal("G92 Z0", setZ.Split(';')[0].Trim());
        }
    }

    [Fact]
    public void Remaining_z_values_are_shifted_by_the_surface_height()
    {
        var (_, plan, result) = Run(BuiltInProfiles.SnapmakerU1());
        var marker = result.Lines.FindIndex(l => l.StartsWith("; --- Original G-code"));
        var zs = Code(result.Lines.Skip(marker)).Where(l => l.IsMove && l.Has('Z')).Select(l => { l.TryGet('Z', out var z); return z; }).ToList();
        Assert.Equal(0.2, zs.Where(z => z < 0.5).Min(), 6); // resume layer at its thickness
        Assert.Equal(4.4, zs.Max(), 6); // last layer hop: 10.0 + 0.4 - 6.0
        Assert.Equal(6.0, plan.ZShift, 6);
    }

    [Fact]
    public void Absolute_extrusion_position_is_restored()
    {
        var (_, plan, result) = Run(BuiltInProfiles.SnapmakerU1(), absoluteE: true);
        var expected = "G92 E" + plan.ResumeLayer.StateAtStart.E.ToString("0.#####", CultureInfo.InvariantCulture);
        var restore = result.Lines.FindIndex(l => l.StartsWith("; --- Straight from"));
        Assert.Contains(expected, result.Lines.Skip(restore));
        Assert.Contains("M82", result.Lines.Skip(restore));
    }

    [Fact]
    public void Fans_temperatures_and_tool_are_restored_for_klipper()
    {
        var (_, _, result) = Run(BuiltInProfiles.SnapmakerU1());
        Assert.Contains("M106 P1 S255", result.Lines);
        Assert.Contains("M190 S60", result.Lines.Select(l => l.Split(';')[0].Trim()));
        Assert.Contains("BED_MESH_PROFILE LOAD=default", result.Lines);
        Assert.DoesNotContain(result.Lines, l => l.Contains('{') && !l.TrimStart().StartsWith(';'));
    }

    [Fact]
    public void Touch_test_contains_no_print_moves()
    {
        var (_, _, result) = Run(BuiltInProfiles.BambuP1S(), touchTest: true);
        Assert.Equal(0, result.KeptLines);
        Assert.DoesNotContain(Code(result.Lines), l => l.IsMove && l.Has('E'));
        Assert.Contains(result.Lines, l => l.StartsWith("G380"));
    }

    [Fact]
    public void Archive_output_replaces_plate_gcode_and_md5()
    {
        var dir = Directory.CreateTempSubdirectory("gcr-test");
        try
        {
            var src = Path.Combine(dir.FullName, "print.gcode.3mf");
            using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
            {
                Write(zip, "Metadata/plate_1.gcode", string.Join('\n', SyntheticGcode.SolidBlock(layers: 20)));
                Write(zip, "Metadata/plate_1.gcode.md5", "OLD");
                Write(zip, "Metadata/plate_1.png", "png");
            }
            var source = GcodeSource.Load(src);
            Assert.Equal("Metadata/plate_1.gcode", source.ArchiveEntry);
            var model = LayerParser.Parse(source.Lines);
            var plan = RecoveryPlan.Create(model, 10, 110, 110, new RecoveryOptions());
            var result = ResumeGenerator.Generate(model, plan, BuiltInProfiles.BambuP1S());

            var outPath = Path.Combine(dir.FullName, "print-recovery.gcode.3mf");
            GcodeSource.WriteArchive(src, source.ArchiveEntry!, outPath, result.Lines);

            using var check = ZipFile.OpenRead(outPath);
            var gcode = Read(check, "Metadata/plate_1.gcode");
            Assert.Contains("Gcode Recovery: resume program", gcode);
            var md5 = Read(check, "Metadata/plate_1.gcode.md5");
            Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(gcode))), md5);
            Assert.NotNull(check.GetEntry("Metadata/plate_1.png"));
            Assert.Throws<IOException>(() => GcodeSource.WriteArchive(src, source.ArchiveEntry!, src, result.Lines));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }
}

public class ToolpathSimulatorTests
{
    [Fact]
    public void Recovery_program_preview_shows_probe_ending_at_contact_and_resume_layer_above_it()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions());
        var result = ResumeGenerator.Generate(model, plan, BuiltInProfiles.BambuP1S());
        var moves = GcodeRecovery.Core.Preview.ToolpathSimulator.Simulate(result.Lines);

        var probe = Assert.Single(moves, m => m.Kind == GcodeRecovery.Core.Preview.MoveKind.Probe);
        Assert.Equal(0f, probe.Z2, 3);
        Assert.Equal(110f, probe.X1, 3);
        var extrusions = moves.Where(m => m.Kind == GcodeRecovery.Core.Preview.MoveKind.Extrude && m.Line > result.Lines.FindIndex(l => l.StartsWith("; --- Original"))).ToList();
        Assert.Equal(0.2f, extrusions.Min(m => m.Z2), 3);
        Assert.Equal(4.0f, extrusions.Max(m => m.Z2), 3);
    }
}

public class RestoreOrderTests
{
    [Fact]
    public void G90_is_emitted_before_the_extrusion_mode()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 20));
        var plan = RecoveryPlan.Create(model, 10, 110, 110, new RecoveryOptions());
        var lines = ResumeGenerator.Generate(model, plan, BuiltInProfiles.BambuP1S()).Lines;
        var restore = lines.FindIndex(l => l.StartsWith("; --- Straight from"));
        var g90 = lines.FindIndex(restore, l => l == "G90");
        var m83 = lines.FindIndex(restore, l => l == "M83");
        Assert.True(g90 > restore && g90 < m83);
    }
}

public class FilamentSavingsTests
{
    [Fact]
    public void Saved_filament_is_the_material_already_on_the_bed()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 9, 110, 110, new RecoveryOptions());
        // 10 layers × 51 raster lines × 0.8 mm; the purge line of the start G-code is not counted.
        Assert.Equal(408.0, FilamentSavings.FilamentMm(model, plan), 3);
        Assert.Equal(1.75, model.FilamentDiameter);
        Assert.Equal(408.0 * Math.PI * 0.875 * 0.875 / 1000 * 1.24, FilamentSavings.Grams(model, plan), 6);
    }

    [Fact]
    public void Material_settings_are_read_from_the_slicer_block()
    {
        var lines = SyntheticGcode.SolidBlock(layers: 5);
        lines.Add("; filament_diameter = 2.85,2.85");
        lines.Add("; filament_density = 1.27");
        var model = LayerParser.Parse(lines);
        Assert.Equal(2.85, model.FilamentDiameter);
        Assert.Equal(1.27, model.FilamentDensity);
    }
}


public class NozzleCleanerTests
{
    [Fact]
    public void U1_uses_its_own_cleaner_and_prints_right_after_the_wipe()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions());
        var lines = ResumeGenerator.Generate(model, plan, BuiltInProfiles.SnapmakerU1()).Lines;
        var code = lines.Select(l => l.Split(';')[0].Trim()).ToList();

        var cleaner = code.FindIndex(l => l == "INNER_PREEXTRUDE_FILAMENT TEMP=220 LENGTH=30 RETRACT_LENGTH=0.5");
        Assert.True(cleaner > 0);
        Assert.DoesNotContain(code, l => l.StartsWith("G1 E30")); // no free purge above the plate
        Assert.True(code.FindLastIndex(cleaner, l => l.StartsWith("G1 Z")) > code.FindIndex(l => l.StartsWith("PROBE")));

        var original = lines.FindIndex(l => l.StartsWith("; --- Original G-code"));
        var between = code.Skip(cleaner + 1).Take(original - cleaner - 1).Where(l => l.Length > 0).ToList();
        Assert.DoesNotContain(between, l => l.StartsWith("G4") || l.StartsWith("M400")); // no delay after the wipe
        var lower = between.FindIndex(l => l == "G1 Z0.2 F600");
        var prime = between.FindIndex(l => l == "G1 E0.5 F1800");
        Assert.True(lower >= 0 && prime == lower + 2 && between[lower + 1] == "M83"); // prime at the resume point
        Assert.True(between.FindIndex(l => l.StartsWith("G1 X")) < lower); // travel before lowering
    }

    [Fact]
    public void Bambu_keeps_its_chute_purge_without_extra_prime()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var plan = RecoveryPlan.Create(model, 29, 110, 110, new RecoveryOptions());
        var code = ResumeGenerator.Generate(model, plan, BuiltInProfiles.BambuP1S()).Lines.Select(l => l.Split(';')[0].Trim()).ToList();
        Assert.Contains("G1 Y265 F3000", code);
        Assert.DoesNotContain(code, l => l.StartsWith("G1 E0.5 F1800"));
    }
}
