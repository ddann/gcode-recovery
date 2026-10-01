using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.Streaming;

namespace GcodeRecovery.Core.Tests;

public class OffsetTransformerTests
{
    [Fact]
    public void Offsets_absolute_moves_only()
    {
        var t = new OffsetTransformer();
        var o = new Offset3(0.5, -0.25, 0.1);
        Assert.Equal("G1 X10.5 Y4.75 Z0.3 E0.2", t.Apply("G1 X10 Y5 Z0.2 E0.2", o));
        Assert.Equal("G92 Z0", t.Apply("G92 Z0", o)); // G92 untouched, otherwise the correction cancels out
        Assert.Equal("G91", t.Apply("G91", o));
        Assert.Equal("G1 Z1", t.Apply("G1 Z1", o)); // relative
        Assert.Equal("G90", t.Apply("G90", o));
        Assert.Equal("G1 E1", t.Apply("G1 E1", o));
        Assert.Equal("M104 S200", t.Apply("M104 S200", o));
    }
}

public class GcodeStreamerTests
{
    private sealed class RecordingSink(bool waits) : IGcodeSink
    {
        public List<string> Sent { get; } = new();
        public Func<int, Task>? OnChunk { get; set; }
        private int _chunks;
        public bool WaitsForExecution => waits;

        public async Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            Sent.AddRange(lines);
            if (OnChunk is not null) await OnChunk(++_chunks);
        }
    }

    [Fact]
    public async Task Streams_whole_program_without_comments()
    {
        var lines = SyntheticGcode.SolidBlock(layers: 5);
        var sink = new RecordingSink(true);
        var streamer = new GcodeStreamer(lines, sink);
        await streamer.RunAsync(CancellationToken.None);
        Assert.Equal(StreamerState.Finished, streamer.Snapshot().State);
        Assert.Equal(lines.Count(l => !l.TrimStart().StartsWith(';')), sink.Sent.Count);
    }

    [Fact]
    public async Task Offset_change_applies_to_lines_sent_afterwards()
    {
        var lines = SyntheticGcode.SolidBlock(layers: 6);
        var sink = new RecordingSink(true);
        var streamer = new GcodeStreamer(lines, sink, new StreamerOptions { ChunkLines = 10 });
        sink.OnChunk = n => { if (n == 30) streamer.Nudge(1, 0, 0.1); return Task.CompletedTask; };
        await streamer.RunAsync(CancellationToken.None);

        var lastLayerMoves = sink.Sent.Skip(sink.Sent.Count - 40).Select(GcodeLine.Parse).Where(l => l.IsMove && l.Has('X')).ToList();
        Assert.All(lastLayerMoves, l => { l.TryGet('X', out var x); Assert.InRange(x, 101, 121); });
        Assert.Contains(sink.Sent, l => l == "G1 Z1.3"); // layer 6 Z 1.2 + 0.1 offset
        Assert.Contains(sink.Sent, l => l == "G1 Z0.2"); // first layer, before the change
    }

    [Fact]
    public async Task Repeat_and_jump_restart_at_layer_start_with_lift()
    {
        var lines = SyntheticGcode.SolidBlock(layers: 8, absoluteE: true);
        var sink = new RecordingSink(true);
        var streamer = new GcodeStreamer(lines, sink, new StreamerOptions { ChunkLines = 20 });
        var jumped = false;
        sink.OnChunk = _ =>
        {
            if (!jumped && streamer.LayerAt(streamer.Snapshot().NextLine) == 4)
            {
                jumped = true;
                streamer.JumpToLayer(2); // user sees it is actually layer 3: go back
            }
            return Task.CompletedTask;
        };
        await streamer.RunAsync(CancellationToken.None);

        var jump = sink.Sent.FindIndex(l => l.StartsWith("; --- jump to layer 3"));
        Assert.True(jump > 0);
        Assert.Equal("G91", sink.Sent[jump + 1]);
        Assert.Equal("G1 Z1 F600", sink.Sent[jump + 2]);
        var restoreE = sink.Sent.Skip(jump).First(l => l.StartsWith("G92 E"));
        Assert.Equal("G92 E" + streamer.Model.Layers[2].StateAtStart.E.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture), restoreE);
        // Layer 3 (Z 0.6) is printed twice in total.
        Assert.Equal(2, sink.Sent.Count(l => l == "G1 Z0.6"));
        Assert.Equal(StreamerState.Finished, streamer.Snapshot().State);
    }

    [Fact]
    public async Task Pause_holds_streaming_until_play()
    {
        var lines = SyntheticGcode.SolidBlock(layers: 4);
        var sink = new RecordingSink(true);
        var streamer = new GcodeStreamer(lines, sink, new StreamerOptions { ChunkLines = 10 });
        sink.OnChunk = n => { if (n == 3) streamer.Pause(); return Task.CompletedTask; };
        var run = streamer.RunAsync(CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(StreamerState.Paused, streamer.Snapshot().State);
        var sentWhilePaused = sink.Sent.Count;
        await Task.Delay(100);
        Assert.Equal(sentWhilePaused, sink.Sent.Count);
        streamer.Offset = new Offset3(0, 0, -0.05);
        Assert.Contains(streamer.PeekUpcoming(400), l => l == "G1 Z0.35"); // preview shows layer 2 at the corrected height
        streamer.Play();
        await run;
        Assert.Equal(StreamerState.Finished, streamer.Snapshot().State);
    }

    [Fact]
    public async Task Fire_and_forget_sink_is_paced_by_estimated_motion_time()
    {
        var lines = new List<string> { "G90", "G1 X0 Y0 F6000" };
        for (var i = 0; i < 12; i++) lines.Add(i % 2 == 0 ? "G1 X100 F6000" : "G1 X0 F6000"); // 1 s per move
        var sink = new RecordingSink(false);
        var streamer = new GcodeStreamer(lines, sink, new StreamerOptions { ChunkLines = 1, LookaheadSeconds = 9.5 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await streamer.RunAsync(CancellationToken.None);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 5); // 12 s of motion minus 9.5 s lookahead
    }
}
