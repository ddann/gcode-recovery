using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.IO;
using GcodeRecovery.Core.Preview;
using GcodeRecovery.Core.Recovery;
using GcodeRecovery.Core.Streaming;
using GcodeRecovery.Printers;

namespace GcodeRecovery.App;

public partial class MainWindow
{
    private IReadOnlyList<string>? _streamLines;
    private string _streamName = "";
    private GcodeStreamer? _streamer;
    private CancellationTokenSource? _streamCts;
    private DispatcherTimer? _streamTimer;
    private Offset3 _pendingOffset;

    private void InitStreamTab()
    {
        StreamRecoveryButton.Click += async (_, _) => await LoadStreamFromRecoveryAsync();
        StreamFileButton.Click += async (_, _) => await LoadStreamFromFileAsync();
        StreamStartButton.Click += (_, _) => StartStream();
        StreamPauseButton.Click += (_, _) => _streamer?.Pause();
        StreamPlayButton.Click += (_, _) => _streamer?.Play();
        StreamStopButton.Click += (_, _) => _streamCts?.Cancel();

        void Nudge(double dx, double dy, double dz)
        {
            if (_streamer is not null) _streamer.Nudge(dx, dy, dz);
            else _pendingOffset = new Offset3(_pendingOffset.X + dx, _pendingOffset.Y + dy, _pendingOffset.Z + dz);
            RefreshStreamUi();
        }
        Xm1.Click += (_, _) => Nudge(-1, 0, 0);
        Xm01.Click += (_, _) => Nudge(-0.1, 0, 0);
        Xp01.Click += (_, _) => Nudge(0.1, 0, 0);
        Xp1.Click += (_, _) => Nudge(1, 0, 0);
        Ym1.Click += (_, _) => Nudge(0, -1, 0);
        Ym01.Click += (_, _) => Nudge(0, -0.1, 0);
        Yp01.Click += (_, _) => Nudge(0, 0.1, 0);
        Yp1.Click += (_, _) => Nudge(0, 1, 0);
        Zm01.Click += (_, _) => Nudge(0, 0, -0.1);
        Zm002.Click += (_, _) => Nudge(0, 0, -0.02);
        Zp002.Click += (_, _) => Nudge(0, 0, 0.02);
        Zp01.Click += (_, _) => Nudge(0, 0, 0.1);
        ResetOffsetButton.Click += (_, _) =>
        {
            if (_streamer is not null) _streamer.Offset = Offset3.Zero;
            _pendingOffset = Offset3.Zero;
            RefreshStreamUi();
        };

        LayerDownButton.Click += (_, _) => StreamJump(s => s.JumpRelative(-1), "previous layer");
        RepeatLayerButton.Click += (_, _) => StreamJump(s => s.RepeatLayer(), "repeat current layer");
        LayerUpButton.Click += (_, _) => StreamJump(s => s.JumpRelative(1), "next layer");
        JumpLayerButton.Click += (_, _) => StreamJump(s => s.JumpToLayer((int)Num(JumpLayerBox, 1) - 1), $"layer {(int)Num(JumpLayerBox, 1)}");

        _streamTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshStreamUi());
        _streamTimer.Start();
    }

    private async Task LoadStreamFromRecoveryAsync()
    {
        if (_model is null || BuildPlan(requireSafe: true) is not { } built)
        {
            Log("Analyze a file and choose a safe touch point on the Recover tab first.");
            return;
        }
        var model = _model;
        var layersShown = (int)Num(PreviewLayersBox, 15);
        var (lines, ghost) = await Task.Run(() =>
            (ResumeGenerator.Generate(model, built.Plan, built.Profile).Lines, BuildScene(model, built.Plan, built.Profile, layersShown).Ghost));
        _lastGhost = ghost;
        await SetStreamProgramAsync(lines, $"recovery program (resume layer {built.Plan.ResumeLayer.Index + 1})");
    }

    private async Task LoadStreamFromFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "G-code to stream",
            FileTypeFilter = [new FilePickerFileType("G-code") { Patterns = ["*.gcode", "*.3mf", "*.gco", "*.g"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var source = await Task.Run(() => GcodeSource.Load(path));
        await SetStreamProgramAsync(source.Lines, Path.GetFileName(path));
    }

    private async Task SetStreamProgramAsync(IReadOnlyList<string> lines, string name)
    {
        if (_streamer is not null && _streamCts is not null)
        {
            Log("Stop the running stream before loading another program.");
            return;
        }
        _streamLines = lines;
        _streamName = name;
        var moves = await Task.Run(() => ToolpathSimulator.Simulate(lines));
        View3D.SetScene(_lastGhost, moves);
        ScrubSlider.Maximum = Math.Max(1, moves.Count);
        View3D.VisibleMoves = 0;
        var layers = await Task.Run(() => LayerParser.Parse(lines).Layers.Count);
        JumpLayerBox.Maximum = Math.Max(1, layers);
        StreamInfo.Text = $"{name}\n{lines.Count:N0} lines · {layers} layers";
        StreamStartButton.IsEnabled = true;
        UpcomingBox.Text = string.Join('\n', lines.Where(l => l.Length > 0 && l[0] != ';').Take(40));
    }

    private void StartStream()
    {
        if (_streamLines is null) return;
        IGcodeSink sink;
        if (DryRunBox.IsChecked == true) sink = new DryRunSink();
        else if (_printer is { IsConnected: true } printer) sink = new PrinterSink(printer);
        else
        {
            Log("Connect to the printer on the Printer tab (or tick Dry run) before streaming.");
            return;
        }

        _streamer = new GcodeStreamer(_streamLines, sink) { Offset = _pendingOffset };
        if (sink is PrinterSink ps) _streamer.WaitForHeating = ps.WaitForHeatingAsync;
        _streamCts = new CancellationTokenSource();
        var streamer = _streamer;
        var ct = _streamCts.Token;
        Log($"Streaming {_streamName} {(sink is DryRunSink ? "(dry run)" : "to the printer")}.");
        StreamStartButton.IsEnabled = false;
        StreamPauseButton.IsEnabled = StreamPlayButton.IsEnabled = StreamStopButton.IsEnabled = true;
        _ = Task.Run(async () =>
        {
            await streamer.RunAsync(ct);
            OnUi(() =>
            {
                var snap = streamer.Snapshot();
                Log($"Streaming ended: {snap.State}{(snap.Error is null ? "" : " — " + snap.Error)}");
                _pendingOffset = snap.Offset;
                _streamCts = null;
                StreamStartButton.IsEnabled = true;
                StreamPauseButton.IsEnabled = StreamPlayButton.IsEnabled = StreamStopButton.IsEnabled = false;
                RefreshStreamUi();
            });
        });
    }

    private void StreamJump(Action<GcodeStreamer> jump, string what)
    {
        if (_streamer is null || _streamCts is null)
        {
            Log("Layer jumps are available while streaming.");
            return;
        }
        jump(_streamer);
        Log($"Stream: {what} (lifts, restores extruder position, continues from the layer start).");
        RefreshStreamUi();
    }

    private void RefreshStreamUi()
    {
        var offset = _streamer?.Offset ?? _pendingOffset;
        OffXText.Text = offset.X.ToString("+0.00;-0.00;0.00");
        OffYText.Text = offset.Y.ToString("+0.00;-0.00;0.00");
        OffZText.Text = offset.Z.ToString("+0.00;-0.00;0.00");
        View3D.NozzleOffset = (offset.X, offset.Y, offset.Z);

        if (_streamer is null)
        {
            View3D.InvalidateVisual();
            return;
        }
        var snap = _streamer.Snapshot();
        StreamProgress.Value = snap.TotalLines == 0 ? 0 : 100.0 * snap.NextLine / snap.TotalLines;
        StreamStatus.Text = $"{snap.State} · line {snap.NextLine:N0} / {snap.TotalLines:N0}";
        StreamLayerText.Text = snap.LayerIndex < 0
            ? $"Preparation (before layer 1) · {snap.LayerCount} layers"
            : $"Sending layer {snap.LayerIndex + 1} of {snap.LayerCount} (Z {_streamer.Model.Layers[snap.LayerIndex].Z:0.00} in program coordinates)";
        View3D.VisibleMoves = View3D.MoveIndexForLine(snap.NextLine);
        if (snap.State is StreamerState.Running or StreamerState.Paused)
            UpcomingBox.Text = string.Join('\n', _streamer.PeekUpcoming(40));
    }

    /// <summary>Sends streamed chunks to the connected printer.</summary>
    private sealed class PrinterSink(IPrinterConnection printer) : IGcodeSink
    {
        public bool WaitsForExecution => printer.SendWaitsForExecution;

        public Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct) => printer.SendGcodeAsync(lines, ct);

        /// <summary>For fire-and-forget printers: hold streaming until the reported temperature reaches the target.</summary>
        public async Task WaitForHeatingAsync(string line, CancellationToken ct)
        {
            var parsed = GcodeLine.Parse(line);
            if (!parsed.TryGet('S', out var target) || target <= 0) return;
            var bed = parsed.Command == "M190";
            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (DateTime.UtcNow < deadline)
            {
                var current = bed ? printer.Status.BedTemp : printer.Status.NozzleTemp;
                if (current is { } c && c >= target - 3) return;
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>Dry run: nothing is sent, the streamer paces itself by estimated motion time.</summary>
    private sealed class DryRunSink : IGcodeSink
    {
        public bool WaitsForExecution => false;
        public Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct) => Task.CompletedTask;
    }
}
