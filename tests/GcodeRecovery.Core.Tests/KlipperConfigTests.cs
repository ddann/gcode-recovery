using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.Recovery;
using GcodeRecovery.Printers;

namespace GcodeRecovery.Core.Tests;

public class KlipperConfigEditorTests
{
    private const string Cfg = """
        [include printer_base.cfg]

        [force_move]
        enable_force_move: False

        #*# <---------------------- SAVE_CONFIG ---------------------->
        #*# DO NOT EDIT THIS BLOCK OR BELOW. The contents are auto-generated.
        #*#
        #*# [bed_mesh default]
        """;

    [Fact]
    public void Include_goes_after_user_sections_and_before_save_config_block()
    {
        var edited = KlipperConfigEditor.AddInclude(Cfg);
        var lines = edited.Split('\n').ToList();
        var include = lines.IndexOf(KlipperConfigEditor.IncludeLine);
        Assert.True(include > lines.FindIndex(l => l.StartsWith("enable_force_move: False"))); // later wins in Klipper
        Assert.True(include < lines.FindIndex(l => l.StartsWith("#*#")));
        Assert.EndsWith("#*# [bed_mesh default]", edited);
    }

    [Fact]
    public void Adding_twice_is_a_no_op()
    {
        var once = KlipperConfigEditor.AddInclude(Cfg);
        Assert.Equal(once, KlipperConfigEditor.AddInclude(once));
        Assert.True(KlipperConfigEditor.HasInclude(once));
        Assert.False(KlipperConfigEditor.HasInclude(Cfg));
    }

    [Fact]
    public void Works_without_save_config_block_and_keeps_crlf()
    {
        var edited = KlipperConfigEditor.AddInclude("[printer]\r\nkinematics: corexy\r\n");
        Assert.Contains("\r\n" + KlipperConfigEditor.IncludeLine + "\r\n", edited);
        Assert.Contains("[force_move]\nenable_force_move: True", KlipperConfigEditor.IncludeContent.Replace("\r\n", "\n"));
    }

    [Fact]
    public void U1_program_sets_z_without_homing_and_homes_only_xy()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 20));
        var plan = RecoveryPlan.Create(model, 10, 110, 110, new RecoveryOptions());
        var lines = ResumeGenerator.Generate(model, plan, BuiltInProfiles.SnapmakerU1()).Lines.Select(l => l.Split(';')[0].Trim()).ToList();
        var setZ = lines.FindIndex(l => l.StartsWith("SET_KINEMATIC_POSITION Z="));
        var firstZMove = lines.FindIndex(l => l.StartsWith("G1 Z"));
        var home = lines.FindIndex(l => l.StartsWith("G28"));
        Assert.True(setZ >= 0 && setZ < firstZMove && firstZMove < home);
        Assert.Equal("G28 X Y", lines[home]);
        Assert.Single(lines, l => l.StartsWith("G28"));
    }
}

public class ZHomeAtClearSpotTests
{
    [Fact]
    public void Corner_home_program_homes_z_then_rises_above_part_before_moving()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 40)); // part at X/Y 100..120
        Assert.True(PartClearance.DistanceToPart(model, 20, 10, 10) > 100);
        var plan = RecoveryPlan.Create(model, 20, 110, 110, new RecoveryOptions { HomeZAtClearSpot = true });
        var lines = ResumeGenerator.Generate(model, plan, BuiltInProfiles.SnapmakerU1()).Lines.Select(l => l.Split(';')[0].Trim()).ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith("SET_KINEMATIC_POSITION"));
        var home = lines.IndexOf("G28");
        var rise = lines.FindIndex(home, l => l.StartsWith("G1 Z"));
        var travel = lines.FindIndex(home, l => l.StartsWith("G1 X110 Y110"));
        Assert.True(home >= 0 && rise == home + 1 && rise < travel);
        Assert.Equal("G1 Z10.2 F600", lines[rise]); // surface Z 4.2 + 1 + 5 clearance
        Assert.True(lines.FindIndex(l => l.StartsWith("PROBE")) > travel);
    }

    [Fact]
    public void Corner_home_is_refused_when_the_part_covers_the_corner()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 40, x0: 5, y0: 5, size: 30));
        Assert.True(PartClearance.DistanceToPart(model, 20, 10, 10) < 1);
        var plan = RecoveryPlan.Create(model, 20, 20, 20, new RecoveryOptions { HomeZAtClearSpot = true });
        Assert.Throws<InvalidOperationException>(() => ResumeGenerator.Generate(model, plan, BuiltInProfiles.SnapmakerU1()));
    }
}
