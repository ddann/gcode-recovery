using System.Diagnostics;
using System.Globalization;
using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Streaming;

/// <summary>Destination of streamed G-code (a connected printer).</summary>
public interface IGcodeSink
{
    /// <summary>
    /// True when <see cref="SendAsync"/> only returns once the printer has processed the lines (Moonraker).
    /// False when sending is fire-and-forget (Bambu MQTT); the streamer then paces itself by estimated motion time.
    /// </summary>
    bool WaitsForExecution { get; }

    Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct);
}

public enum StreamerState { Idle, Running, Paused, Finished, Stopped, Faulted }

public sealed record StreamerSnapshot(StreamerState State, int NextLine, int TotalLines, int LayerIndex, int LayerCount, Offset3 Offset, string? Error);

public sealed record StreamerOptions
{
    /// <summary>Maximum lines per message.</summary>
    public int ChunkLines { get; init; } = 24;

    /// <summary>Fire-and-forget sinks: how many seconds of estimated motion may be queued ahead of the printer.</summary>
    public double LookaheadSeconds { get; init; } = 2.0;

    /// <summary>Relative Z lift performed before jumping to another layer.</summary>
    public double JumpLiftMm { get; init; } = 1.0;

    public double JumpTravelFeed { get; init; } = 6000;
}

/// <summary>
/// Streams a program to the printer line by line from the computer, so it can be corrected while it runs:
/// live X/Y/Z offsets (applied to every line sent after the change), pause/play, repeating the current layer
/// and jumping to any other layer. Offsets take effect within the sink's buffering latency
/// (≈ <see cref="StreamerOptions.LookaheadSeconds"/> for Bambu, one chunk for Moonraker).
/// </summary>
public sealed class GcodeStreamer
{
    private readonly IReadOnlyList<string> _lines;
    private readonly IGcodeSink _sink;
    private readonly StreamerOptions _options;
    private readonly OffsetTransformer _transformer = new();
    private readonly object _gate = new();
    private readonly MachineState _sim = new();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Offset3 _offset;
    private int _next;
    private int? _pendingJump;
    private bool _paused;
    private StreamerState _state = StreamerState.Idle;
    private string? _error;

    public GcodeStreamer(IReadOnlyList<string> lines, IGcodeSink sink, StreamerOptions? options = null)
    {
        _lines = lines;
        _sink = sink;
        _options = options ?? new StreamerOptions();
        Model = LayerParser.Parse(lines);
    }

    /// <summary>Layer index of the streamed program itself (used for jumps).</summary>
    public GcodeModel Model { get; }

    /// <summary>Raised after every chunk and on every control change (from the streaming thread).</summary>
    public event EventHandler<StreamerSnapshot>? Changed;

    /// <summary>Optional: called after a heat-and-wait command (M109/M190) for fire-and-forget sinks.</summary>
    public Func<string, CancellationToken, Task>? WaitForHeating { get; set; }

    public Offset3 Offset
    {
        get { lock (_gate) return _offset; }
        set { lock (_gate) _offset = value; Notify(); }
    }

    public void Nudge(double dx, double dy, double dz)
    {
        lock (_gate) _offset = new Offset3(_offset.X + dx, _offset.Y + dy, _offset.Z + dz);
        Notify();
    }

    public StreamerSnapshot Snapshot()
    {
        lock (_gate) return new StreamerSnapshot(_state, _next, _lines.Count, LayerAt(_next), Model.Layers.Count, _offset, _error);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != StreamerState.Running) return;
            _paused = true;
            _state = StreamerState.Paused;
        }
        Notify();
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_state != StreamerState.Paused) return;
            _paused = false;
            _state = StreamerState.Running;
            _resume.TrySetResult();
        }
        Notify();
    }

    /// <summary>Continue from the first line of <paramref name="layerIndex"/> (0-based) after the current chunk.</summary>
    public void JumpToLayer(int layerIndex)
    {
        lock (_gate) _pendingJump = Math.Clamp(layerIndex, 0, Math.Max(0, Model.Layers.Count - 1));
        Notify();
    }

    public void RepeatLayer() => JumpToLayer(Math.Max(0, LayerAt(Snapshot().NextLine)));

    public void JumpRelative(int layers) => JumpToLayer(Math.Max(0, LayerAt(Snapshot().NextLine)) + layers);

    /// <summary>The next <paramref name="count"/> lines exactly as they will be sent with the current offset.</summary>
    public IReadOnlyList<string> PeekUpcoming(int count)
    {
        int start;
        Offset3 offset;
        bool absolute;
        lock (_gate)
        {
            start = _pendingJump is { } j ? Model.Layers[j].StartLine : _next;
            offset = _offset;
            absolute = _transformer.Absolute;
        }
        var t = new OffsetTransformer { Absolute = absolute };
        var list = new List<string>(count);
        for (var i = start; i < _lines.Count && list.Count < count; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            list.Add(t.Apply(_lines[i], offset));
        }
        return list;
    }

    /// <summary>Index (0-based) of the layer containing <paramref name="line"/>, or -1 before the first layer.</summary>
    public int LayerAt(int line)
    {
        var layers = Model.Layers;
        int lo = 0, hi = layers.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (layers[mid].StartLine <= line) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        lock (_gate) _state = StreamerState.Running;
        Notify();
        var clock = Stopwatch.StartNew();
        var queuedSeconds = 0.0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task? waitForPlay = null;
                lock (_gate)
                {
                    if (_paused)
                    {
                        if (_resume.Task.IsCompleted) _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        waitForPlay = _resume.Task;
                    }
                }
                if (waitForPlay is not null)
                {
                    await waitForPlay.WaitAsync(ct);
                    queuedSeconds = clock.Elapsed.TotalSeconds; // the printer drained its queue while paused
                    continue;
                }

                var chunk = new List<string>(_options.ChunkLines + 8);
                var heatWait = false;
                Offset3 offset;
                lock (_gate)
                {
                    offset = _offset;
                    if (_pendingJump is { } jump)
                    {
                        _pendingJump = null;
                        chunk.AddRange(JumpPreamble(Model.Layers[jump]).Select(l => _transformer.Apply(l, offset)));
                        _next = Model.Layers[jump].StartLine;
                        queuedSeconds += 2; // lift + travel
                    }
                    if (_next >= _lines.Count && chunk.Count == 0) break;
                    while (_next < _lines.Count && chunk.Count < _options.ChunkLines)
                    {
                        var raw = _lines[_next++];
                        if (IsSkippable(raw)) continue;
                        chunk.Add(_transformer.Apply(raw, offset));
                        queuedSeconds += EstimateSeconds(raw);
                        if (IsHeatWait(raw)) { heatWait = true; break; }
                    }
                }

                if (chunk.Count > 0) await _sink.SendAsync(chunk, ct);
                Notify();

                if (!_sink.WaitsForExecution)
                {
                    if (heatWait && WaitForHeating is not null)
                    {
                        await WaitForHeating(chunk[^1], ct);
                        queuedSeconds = clock.Elapsed.TotalSeconds;
                    }
                    var ahead = queuedSeconds - clock.Elapsed.TotalSeconds;
                    if (ahead > _options.LookaheadSeconds)
                        await Task.Delay(TimeSpan.FromSeconds(ahead - _options.LookaheadSeconds), ct);
                    if (queuedSeconds < clock.Elapsed.TotalSeconds) queuedSeconds = clock.Elapsed.TotalSeconds;
                }
            }
            lock (_gate) _state = StreamerState.Finished;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _state = StreamerState.Stopped;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _state = StreamerState.Faulted;
                _error = ex.Message;
            }
        }
        Notify();
    }

    private IEnumerable<string> JumpPreamble(LayerInfo layer)
    {
        var s = layer.StateAtStart;
        var inv = CultureInfo.InvariantCulture;
        yield return $"; --- jump to layer {layer.Index + 1} ---";
        yield return "G91";
        yield return $"G1 Z{_options.JumpLiftMm.ToString("0.###", inv)} F600";
        yield return "G90";
        yield return s.AbsoluteE ? "M82" : "M83";
        if (s.AbsoluteE) yield return $"G92 E{s.E.ToString("0.#####", inv)}";
        var safeZ = Math.Max(layer.Z, _sim.Z) + _options.JumpLiftMm;
        yield return $"G1 Z{safeZ.ToString("0.###", inv)} F600";
        yield return $"G1 X{s.X.ToString("0.###", inv)} Y{s.Y.ToString("0.###", inv)} F{_options.JumpTravelFeed.ToString("0", inv)}";
        if (!s.AbsoluteXyz) yield return "G91";
    }

    /// <summary>Rough execution time of a line, used to pace fire-and-forget sinks.</summary>
    private double EstimateSeconds(string raw)
    {
        var line = GcodeLine.Parse(raw);
        _sim.ApplyModal(line);
        if (line.Command == "G4")
            return line.TryGet('S', out var s) ? s : line.TryGet('P', out var p) ? p / 1000 : 0;
        if (!line.IsMove) return 0.005;
        double x0 = _sim.X, y0 = _sim.Y, z0 = _sim.Z;
        if (line.TryGet('F', out var f) && f > 0) _sim.Feedrate = f;
        if (line.TryGet('X', out var x)) _sim.X = _sim.AbsoluteXyz ? x : _sim.X + x;
        if (line.TryGet('Y', out var y)) _sim.Y = _sim.AbsoluteXyz ? y : _sim.Y + y;
        if (line.TryGet('Z', out var z)) _sim.Z = _sim.AbsoluteXyz ? z : _sim.Z + z;
        var d = Math.Sqrt(Math.Pow(_sim.X - x0, 2) + Math.Pow(_sim.Y - y0, 2) + Math.Pow(_sim.Z - z0, 2));
        if (d == 0 && line.TryGet('E', out var e)) d = Math.Abs(e);
        return d / Math.Max(1, _sim.Feedrate / 60.0);
    }

    private static bool IsSkippable(string raw)
    {
        var t = raw.AsSpan().TrimStart();
        return t.Length == 0 || t[0] == ';';
    }

    private static bool IsHeatWait(string raw)
    {
        var c = GcodeLine.Parse(raw).Command;
        return c is "M109" or "M190";
    }

    private void Notify() => Changed?.Invoke(this, Snapshot());
}
