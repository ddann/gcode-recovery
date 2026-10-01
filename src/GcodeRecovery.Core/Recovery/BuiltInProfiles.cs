namespace GcodeRecovery.Core.Recovery;

/// <summary>
/// Default profiles for the two supported load-cell printers. The templates are deliberately verbose and
/// commented: they end up in the generated file, where the user should be able to read what will happen.
/// </summary>
public static class BuiltInProfiles
{
    public static IReadOnlyList<PrinterProfile> All => [BambuP1S(), SnapmakerU1()];

    public static PrinterProfile BambuP1S() => new()
    {
        Id = "bambu-p1s",
        Name = "Bambu Lab P1S",
        Flavor = FirmwareFlavor.Bambu,
        BedWidth = 256,
        BedDepth = 256,
        PreferArchiveOutput = true,
        Notes =
            "EXPERIMENTAL. Bambu Lab does not document its G-code dialect. The touch-down uses G380 S2 " +
            "(move until the heatbed force sensors detect contact), as seen in Bambu's own P1S start G-code. " +
            "The bed mesh is not re-measured: the firmware keeps the mesh saved (M500) by the original print. " +
            "Run the touch-test file first and keep a hand near the power switch.",
        PrepareTemplate = """
            ; --- Preparation (Bambu Lab P1S) ---
            M140 S{bed_temp} ; keep the part at its printing temperature so it stays attached and true to size
            M104 S{probe_nozzle_temp} ; nozzle off/cold for the touch-down
            M106 P1 S255 ; part fan speeds up nozzle cooling
            M190 S{bed_temp}
            G4 S{cooldown_s} ; wait for the nozzle to cool (target below {probe_nozzle_max_temp} C)
            M106 P1 S0
            G90
            M83
            G92 Z0 ; provisional Z: the real height is unknown until the nozzle touches the part
            G91
            G1 Z{lift} F600 ; lower the bed a little before any X/Y motion
            G90
            G28 X Y ; home X and Y only. Never home Z: the part occupies the bed
            """,
        LevelingTemplate = """
            ; --- Bed mesh: reuse the original one (no G29, the bed is occupied) ---
            G29.2 S1 ; keep bed-mesh compensation enabled with the mesh stored by the original print
            """,
        ProbeTemplate = """
            ; --- Load-cell touch-down on the part ---
            G1 X{touch_x} Y{touch_y} F{travel_feed}
            M400
            G380 S2 Z-{probe_travel} F{probe_feed} ; lower slowly until the force sensor detects contact
            M400
            G92 Z{contact_z} ; the contact point becomes the new Z reference
            G1 Z{safe_z} F600 ; back off
            """,
        PurgeTemplate = """
            ; --- Heat, purge and wipe over the purge chute (positions from Bambu's P1S start G-code) ---
            G1 Z{safe_z} F600
            G1 X67 F12000
            G1 Y265 F3000
            M109 S{nozzle_temp}
            M106 P1 S0
            G92 E0
            G1 E{purge_length} F300 ; purge
            M400
            M106 P1 S178
            G4 S3
            G1 X100 F5000 ; shake and wipe against the chute
            G1 X70 F15000
            G1 X100 F5000
            G1 X70 F15000
            G1 X100 F5000
            M106 P1 S0
            G1 Y250 F3000
            """,
        ToolSelectTemplate = "",
        TouchTestEndTemplate = """
            ; --- Touch test finished: the nozzle has touched the part once and backed off ---
            G1 Z{safe_z} F600
            M104 S0
            ; The bed is left at {bed_temp} C so the part stays attached for the real recovery run.
            """,
    };

    public static PrinterProfile SnapmakerU1() => new()
    {
        Id = "snapmaker-u1",
        Name = "Snapmaker U1",
        Flavor = FirmwareFlavor.Klipper,
        BedWidth = 270,
        BedDepth = 270,
        PreferArchiveOutput = false,
        Notes =
            "Klipper-based. The touch-down uses the nozzle-contact probe through PROBE (the same call the U1 " +
            "firmware uses for bed contact). Z is never homed on the part: by default a provisional Z is set with " +
            "SET_KINEMATIC_POSITION, which needs '[force_move] enable_force_move: True' (the app can add it when the " +
            "config is writable). Alternatively Z can be homed at the bed corner X10 Y10 when that spot is clear of the " +
            "part. The original bed mesh is re-loaded from the 'default' profile. Purging and wiping use the " +
            "U1's own nozzle cleaner (purge, cut-off, brush, discard) and printing starts right after it.",
        PrepareTemplate = """
            ; --- Preparation (Snapmaker U1 / Klipper) ---
            ; Requires [force_move] enable_force_move: True  (for SET_KINEMATIC_POSITION)
            M140 S{bed_temp} ; keep the part at its printing temperature so it stays attached and true to size
            M104 S{probe_nozzle_temp} ; nozzle off/cold for the touch-down
            M106 S255 ; part fan speeds up nozzle cooling
            M190 S{bed_temp}
            G90
            M83
            SET_KINEMATIC_POSITION Z={probe_travel} ; provisional Z: real height unknown until contact
            G91
            G1 Z{lift} F600 ; lower the bed a little before any X/Y motion
            G90
            G28 X Y ; home X and Y only. Never home Z: the part occupies the bed
            TEMPERATURE_WAIT SENSOR=extruder MAXIMUM={probe_nozzle_max_temp}
            M106 S0
            """,
        ZHomeX = 10,
        ZHomeY = 10,
        ZHomePrepareTemplate = """
            ; --- Preparation (Snapmaker U1 / Klipper), Z homed at the bed corner ---
            ; Used only when X{zhome_x} Y{zhome_y} was verified to be clear of the part. The U1 first drops the bed to its
            ; bottom endstop, then touches the bed with the nozzle at that corner, so the part is never touched.
            M140 S{bed_temp} ; keep the part at its printing temperature so it stays attached and true to size
            M104 S{probe_nozzle_temp} ; nozzle off/cold for the touch-down
            M106 S255 ; part fan speeds up nozzle cooling
            M190 S{bed_temp}
            G90
            M83
            G28 ; X/Y, then Z at the bed corner (clear of the part)
            G1 Z{part_clear_z} F600 ; rise above the part before any X/Y travel
            TEMPERATURE_WAIT SENSOR=extruder MAXIMUM={probe_nozzle_max_temp}
            M106 S0
            """,
        LevelingTemplate = """
            ; --- Bed mesh: reuse the original one (no BED_MESH_CALIBRATE, the bed is occupied) ---
            BED_MESH_PROFILE LOAD=default
            """,
        ProbeTemplate = """
            ; --- Load-cell touch-down on the part ---
            G1 X{touch_x} Y{touch_y} F{travel_feed}
            M400
            PROBE SAMPLE_TRIG_FREQ=450 SAMPLES=1 PROBE_SPEED={probe_speed} ; nozzle-contact probe, slow
            G92 Z{contact_z} ; the contact point becomes the new Z reference
            G1 Z{safe_z} F600 ; back off
            """,
        PrimeAfterPurgeMm = 0.5,
        PurgeTemplate = """
            ; --- Heat, purge and clean the nozzle with the U1's own cleaner ---
            ; INNER_PREEXTRUDE_FILAMENT is the firmware's purge routine: it moves (X/Y only) to the discard station,
            ; heats, purges, retracts {prime_length} mm, cools the strand, cuts it off, brushes the nozzle and discards
            ; the strand. Z is never moved, so it runs safely at the height above the part.
            G90
            G1 Z{safe_z} F600 ; stay above the part
            INNER_PREEXTRUDE_FILAMENT TEMP={nozzle_temp} LENGTH={purge_length} RETRACT_LENGTH={prime_length}
            M109 S{nozzle_temp} ; make sure the nozzle is at printing temperature
            """,
        ToolSelectTemplate = "T{tool}",
        TouchTestEndTemplate = """
            ; --- Touch test finished: the nozzle has touched the part once and backed off ---
            G1 Z{safe_z} F600
            M104 S0
            RESPOND MSG="Gcode Recovery: touch test complete"
            ; The bed is left at {bed_temp} C so the part stays attached for the real recovery run.
            """,
    };
}
