namespace GcodeRecovery.App;

internal static class HelpContent
{
    public const string Checklist = """
        BEFORE YOU START
        • Do not touch, move or remove the part. Do not home Z and do not run bed leveling: the part occupies the bed.
        • Fix the cause (clear the jam, load new filament) and make sure the correct filament is loaded in the right slot / tool.
        • Clean the nozzle tip: a blob of plastic on the tip gives a wrong contact height.
        • Remove loose strings or blobs from the top of the part where the touch point is.
        • If the printer was switched off, jog the bed so the nozzle is roughly 5–20 mm above the part (use the Printer tab or the printer screen).
        • Keep the bed warm (same temperature as the print) so the part stays attached.

        RECOVER TAB
        1. Open the exact G-code (or Bambu .gcode.3mf) that was printed.
        2. Measure the part height from the bed to the top surface with calipers or a ruler and enter it. ±0.5 mm is fine.
        3. Press Analyze. The program looks at every layer within the search range, finds where each has solid material,
           and picks the largest flat area that is solid in all of them — so the touch point lands on material no matter
           exactly which of those layers was the last one printed.
        4. Check the last fully printed layer. If the print stopped half-way through a layer, tick the box: that layer is printed
           again from its first line.
        5. Optionally click the preview to choose a different touch point (it must be green).
        6. Save the touch test and run it first. It heats the bed, lets the nozzle cool, homes X/Y only, moves above the
           touch point and lowers the cold nozzle slowly until the force sensor detects contact, then backs off.
        7. If the touch test behaves as expected, save and run the recovery file. It repeats the touch-down, uses the contact as
           the Z reference, reuses the stored bed mesh, heats and purges/wipes away from the part, and continues from the resume layer.

        HOW THE HEIGHT IS FOUND
        The caliper value only narrows the search. The real Z reference comes from the load cell touching the part,
        so Z is physically correct even if the chosen layer is off by one. Being one layer off only makes the finished
        part one layer taller or shorter.

        Z OPTIONS
        • "Contact = Z0": the touched surface becomes Z = 0 and every remaining Z is shifted, so the resume layer is printed
          like a first layer (at its own layer height, or at the first-layer height from the G-code if you choose that).
        • "Keep original Z values": the contact is set to the surface's original height. Use this if your firmware fades
          the bed mesh with height.

        PRINTER TAB
        Connect to the printer on your local network to watch the camera, follow temperatures and progress, jog, set
        temperatures, pause/resume/stop, send G-code, and upload + start the touch test or the recovery file.
        Bambu Lab: enable LAN mode (or LAN-only liveview for the camera) and enter the printer IP, serial number and access code.
        Snapmaker U1 / Klipper: enter the printer IP (Moonraker on port 7125). Klipper refuses Z moves before Z is homed.
        The app never homes Z on the part: it either sets Z without homing (needs [force_move] enable_force_move: True,
        which the app can add when the config is writable) or, if you agree, homes Z at the bed corner X10 Y10, but only
        when that corner is at least 8 mm clear of the part.

        SAFETY
        Bambu firmware G-code is undocumented; the Bambu profile is experimental. Watch the first run and keep a hand near the
        power switch. Review the G-code templates for your machine before printing. You use this software at your own risk.
        """;
}
