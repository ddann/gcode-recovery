using System.Globalization;

namespace GcodeRecovery.Core.Gcode;

/// <summary>
/// One parsed G-code line. The raw text is kept verbatim so untouched lines are copied byte-for-byte.
/// Classic commands (G/M/T) are tokenised into letter/number parameters, which also records where each
/// number sits in the raw text so a single parameter can be rewritten without disturbing the rest.
/// Extended (Klipper style) commands such as <c>SET_KINEMATIC_POSITION Z=1</c> keep only their command name.
/// </summary>
public sealed class GcodeLine
{
    private readonly Param[] _params;

    private GcodeLine(string raw, string command, string? comment, Param[] parameters)
    {
        Raw = raw;
        Command = command;
        Comment = comment;
        _params = parameters;
    }

    public string Raw { get; }

    /// <summary>Upper-case command such as "G1", "G29.1", "M104", "T0" or "SET_FAN_SPEED". Empty if none.</summary>
    public string Command { get; }

    /// <summary>Text after the first ';', trimmed. Null if the line has no comment.</summary>
    public string? Comment { get; }

    public bool IsMove => Command is "G0" or "G1" or "G2" or "G3";

    public bool TryGet(char letter, out double value)
    {
        letter = char.ToUpperInvariant(letter);
        foreach (var p in _params)
        {
            if (p.Letter == letter)
            {
                value = p.Value;
                return true;
            }
        }
        value = 0;
        return false;
    }

    public bool Has(char letter) => TryGet(letter, out _);

    /// <summary>Returns the raw line with the numeric value of <paramref name="letter"/> replaced.</summary>
    public string WithParam(char letter, double newValue, string format = "0.###")
    {
        letter = char.ToUpperInvariant(letter);
        foreach (var p in _params)
        {
            if (p.Letter != letter) continue;
            var text = newValue.ToString(format, CultureInfo.InvariantCulture);
            if (text == "-0") text = "0";
            return string.Concat(Raw.AsSpan(0, p.Start), text, Raw.AsSpan(p.Start + p.Length));
        }
        return Raw;
    }

    public static GcodeLine Parse(string raw)
    {
        var semicolon = raw.IndexOf(';');
        var codeEnd = semicolon >= 0 ? semicolon : raw.Length;
        string? comment = semicolon >= 0 ? raw[(semicolon + 1)..].Trim() : null;

        var i = 0;
        while (i < codeEnd && char.IsWhiteSpace(raw[i])) i++;
        if (i >= codeEnd) return new GcodeLine(raw, string.Empty, comment, []);

        var first = char.ToUpperInvariant(raw[i]);
        var classic = (first is 'G' or 'M' or 'T') && i + 1 < codeEnd && (char.IsDigit(raw[i + 1]) || raw[i + 1] == '-');
        if (!classic)
        {
            var start = i;
            while (i < codeEnd && !char.IsWhiteSpace(raw[i])) i++;
            return new GcodeLine(raw, raw[start..i].ToUpperInvariant(), comment, []);
        }

        // Command word: letter + number (e.g. G1, G29.1, M620).
        i++;
        var numStart = i;
        i = ScanNumber(raw, i, codeEnd);
        var command = first + NormaliseNumber(raw.AsSpan(numStart, i - numStart));

        var list = new List<Param>(6);
        while (i < codeEnd)
        {
            var c = raw[i];
            if (!char.IsLetter(c))
            {
                i++;
                continue;
            }
            var letter = char.ToUpperInvariant(c);
            i++;
            var start = i;
            i = ScanNumber(raw, i, codeEnd);
            if (i > start && double.TryParse(raw.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(new Param(letter, v, start, i - start));
        }
        return new GcodeLine(raw, command, comment, list.ToArray());
    }

    private static int ScanNumber(string s, int i, int end)
    {
        if (i < end && (s[i] == '-' || s[i] == '+')) i++;
        while (i < end && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        return i;
    }

    private static string NormaliseNumber(ReadOnlySpan<char> number)
    {
        // "G01" -> "G1", "G29.1" stays "G29.1".
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        return number.ToString();
    }

    private readonly record struct Param(char Letter, double Value, int Start, int Length);
}
