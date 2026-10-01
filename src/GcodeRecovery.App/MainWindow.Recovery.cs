using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GcodeRecovery.Core.Analysis;
using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.IO;
using GcodeRecovery.Core.Preview;
using GcodeRecovery.Core.Recovery;

namespace GcodeRecovery.App;

public partial class MainWindow
{
    private DispatcherTimer? _previewTimer;
    private int _previewVersion;

    // ------------------------------------------------------------------ analysis

    private async Task AnalyzeAsync()
    {
        if (_model is null || _busy) return;
        var model = _model;
        var height = Num(HeightBox, 10);
        var settings = new AnalysisSettings { SearchRangeMm = Num(RangeBox, 0.6), MinTouchSizeMm = Num(MinTouchBox, 3) };
        await RunBusy($"Analyzing layers around {height:0.00} mm…", async () =>
        {
            var result = await Task.Run(() => FlatAreaFinder.Analyze(model, height, settings));
            _analysis = result;

            _layerChoices.Clear();
            var items = new List<string>();
            var first = Math.Max(0, result.NearestLayerIndex - 10);
            var last = Math.Min(model.Layers.Count - 2, result.NearestLayerIndex + 10);
            for (var i = first; i <= last; i++)
            {
                var layer = model.Layers[i];
                var cand = result.Candidates.FirstOrDefault(c => c.Layer.Index == i);
                var flat = cand is null ? "" : cand.LargestRect is { } r ? $" · flat {r.Width:0.0}×{r.Height:0.0} mm" : " · no flat area";
                items.Add($"Layer {i + 1}  ·  Z {layer.Z:0.00} mm{flat}{(i == result.NearestLayerIndex ? "  ← closest to measurement" : "")}");
                _layerChoices.Add(i);
            }
            SurfaceLayerBox.ItemsSource = items;

            foreach (var c in result.Candidates)
                Log($"  candidate layer {c.Layer.Index + 1} (Z {c.Layer.Z:0.00}): largest flat area {(c.LargestRect is { } r ? r.ToString() : "none")}");
            if (result.LargestSingleLayer is { } best)
                Log($"Layer with the largest flat area: {best.Layer.Index + 1} ({best.LargestRect})");
            foreach (var w in result.Warnings) Log("WARNING: " + w);

            if (result.TouchRect is { } t)
            {
                Log($"Touch-down area solid in all {result.Candidates.Count} candidate layers: {t}");
                SetTouchPoint(t.CenterX, t.CenterY, userPicked: false);
            }
            else
            {
                _touch = null;
            }
            SelectDefaultLayer();
        });
    }

    private void SelectDefaultLayer()
    {
        if (_analysis is null || _model is null) return;
        var target = _analysis.NearestLayerIndex - (MidLayerBox.IsChecked == true ? 1 : 0);
        target = Math.Clamp(target, 0, _model.Layers.Count - 2);
        var idx = _layerChoices.IndexOf(target);
        if (idx >= 0) SurfaceLayerBox.SelectedIndex = idx;
        OnLayerSelectionChanged();
    }

    private int? SelectedSurfaceLayer() =>
        SurfaceLayerBox.SelectedIndex >= 0 && SurfaceLayerBox.SelectedIndex < _layerChoices.Count ? _layerChoices[SurfaceLayerBox.SelectedIndex] : null;

    private void OnLayerSelectionChanged()
    {
        RefreshPreview();
        UpdateTouchInfo();
        UpdateActionButtons();
        SchedulePreview3D();
    }

    private void SetTouchPoint(double x, double y, bool userPicked)
    {
        if (_analysis is null) return;
        _touch = (x, y);
        _touchSafe = FlatAreaFinder.IsTouchPointSafe(_analysis, x, y);
        if (userPicked)
            Log(_touchSafe
                ? $"Touch point moved to X {x:0.0} Y {y:0.0} (solid in every candidate layer)."
                : $"X {x:0.0} Y {y:0.0} is NOT solid in every candidate layer — pick a point inside the green area.");
        RefreshPreview();
        UpdateTouchInfo();
        UpdateActionButtons();
        SchedulePreview3D();
    }

    private void UpdateTouchInfo()
    {
        if (_model is null || _touch is not { } t || SelectedSurfaceLayer() is not { } s)
        {
            TouchInfo.Text = _analysis is null ? "" : "No touch point yet.";
            return;
        }
        var resume = _model.Layers[s + 1];
        TouchInfo.Text =
            $"Touch at X {t.X:0.0}  Y {t.Y:0.0} {(_touchSafe ? "✓ safe" : "✗ not on solid material")}\n" +
            $"Touch surface: layer {s + 1} (Z {_model.Layers[s].Z:0.00}).  Resume: layer {resume.Index + 1} of {_model.Layers.Count} (Z {resume.Z:0.00}), reprinted from its start." +
            ZHomeInfo(s);
    }

    private string ZHomeInfo(int surfaceLayer)
    {
        if (_model is null || string.IsNullOrWhiteSpace(_profile.ZHomePrepareTemplate)) return "";
        var d = PartClearance.DistanceToPart(_model, surfaceLayer, _profile.ZHomeX, _profile.ZHomeY);
        return d >= ResumeGenerator.MinZHomeClearanceMm
            ? $"\nBed corner X{_profile.ZHomeX:0} Y{_profile.ZHomeY:0} is {d:0} mm clear of the part: Z may be homed there."
            : $"\nBed corner X{_profile.ZHomeX:0} Y{_profile.ZHomeY:0} is only {Math.Max(0, d):0.#} mm from the part: Z must not be homed.";
    }

    /// <summary>Rewrites a previously saved program with the current options (e.g. after switching the Z method).</summary>
    private async Task<bool> RegenerateAsync(string outPath, bool touchTestOnly)
    {
        if (_model is null || _source is null || BuildPlan(requireSafe: true) is not { } built)
        {
            Log("Cannot regenerate: analyze the file and choose a touch point first.");
            return false;
        }
        var (plan, profile) = built;
        var model = _model;
        var source = _source;
        var asArchive = outPath.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) && source.IsArchive;
        try
        {
            await Task.Run(() =>
            {
                var r = ResumeGenerator.Generate(model, plan, profile, touchTestOnly);
                if (asArchive) GcodeSource.WriteArchive(source.FilePath, source.ArchiveEntry!, outPath, r.Lines);
                else GcodeSource.WriteGcode(outPath, r.Lines);
            });
            if (!touchTestOnly) _savedJobs[outPath] = (profile.Id, FilamentSavings.Grams(model, plan));
            Log($"Regenerated {Path.GetFileName(outPath)} with the current options.");
            return true;
        }
        catch (Exception ex)
        {
            Log("Regeneration failed: " + ex.Message);
            return false;
        }
    }

    private void RefreshPreview()
    {
        if (_model is null) return;
        var layer = SelectedSurfaceLayer() is { } s ? _model.Layers[s] : _model.Layers[^1];
        Preview.Show(layer, _analysis?.Intersection, _analysis?.TouchRect, _touch, _touchSafe);
    }

    // ------------------------------------------------------------------ generation

    private (RecoveryPlan Plan, PrinterProfile Profile)? BuildPlan(bool requireSafe)
    {
        if (_model is null || _touch is not { } t || SelectedSurfaceLayer() is not { } s) return null;
        if (requireSafe && !_touchSafe)
        {
            Log("Refusing to generate: the touch point is not on material that is solid in every candidate layer.");
            return null;
        }
        SyncTemplatesFromUi();
        return (RecoveryPlan.Create(_model, s, t.X, t.Y, ReadOptions()), _profile.Clone());
    }

    private async Task SaveAsync(bool touchTestOnly)
    {
        if (_model is null || _source is null || BuildPlan(requireSafe: true) is not { } built) return;
        var (plan, profile) = built;
        var asArchive = _source.IsArchive && profile.PreferArchiveOutput;
        var baseName = Path.GetFileName(_source.FilePath);
        foreach (var ext in new[] { ".gcode.3mf", ".3mf", ".gcode" })
            if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { baseName = baseName[..^ext.Length]; break; }
        var suffix = touchTestOnly ? "touch-test" : $"resume-L{plan.ResumeLayer.Index + 1}";
        var extension = asArchive ? ".gcode.3mf" : ".gcode";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = touchTestOnly ? "Save touch test" : "Save recovery program",
            SuggestedFileName = $"{baseName}-{suffix}{extension}",
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } outPath) return;

        var model = _model;
        var source = _source;
        await RunBusy("Generating…", async () =>
        {
            var result = await Task.Run(() =>
            {
                var r = ResumeGenerator.Generate(model, plan, profile, touchTestOnly);
                if (asArchive) GcodeSource.WriteArchive(source.FilePath, source.ArchiveEntry!, outPath, r.Lines);
                else GcodeSource.WriteGcode(outPath, r.Lines);
                return r;
            });
            foreach (var w in result.Warnings) Log("WARNING: " + w);
            if (touchTestOnly)
            {
                _lastTouchTestPath = outPath;
                Log($"Touch test saved: {outPath}");
            }
            else
            {
                _lastRecoveryPath = outPath;
                _savedJobs[outPath] = (profile.Id, FilamentSavings.Grams(model, plan));
                Log($"Recovery program saved: {outPath}\n  kept {result.KeptLines:N0} original lines, dropped {result.DiscardedLines:N0}; " +
                    $"resume layer {plan.ResumeLayer.Index + 1} printed at Z {plan.ResumeZ:0.###} (gap {plan.ResumeGap:0.###} mm).");
            }
        });
    }

    // ------------------------------------------------------------------ 3D preview

    private void SchedulePreview3D()
    {
        _previewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, async (_, _) =>
        {
            _previewTimer!.Stop();
            await UpdatePreview3DAsync();
        });
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private IReadOnlyList<Move3D> _lastGhost = [];

    /// <summary>Saved recovery files and the filament they save (kept in memory only, for completion reports).</summary>
    private readonly Dictionary<string, (string Printer, double Grams)> _savedJobs = new();

    private async Task UpdatePreview3DAsync()
    {
        // While a program is being streamed the 3D view follows the stream instead.
        if (_streamCts is not null) return;
        if (_model is null || BuildPlan(requireSafe: false) is not { } built) return;
        var (plan, profile) = built;
        var model = _model;
        var layersAfter = (int)Num(PreviewLayersBox, 15);
        var version = ++_previewVersion;
        try
        {
            var (ghost, program) = await Task.Run(() => BuildScene(model, plan, profile, layersAfter));
            if (version != _previewVersion || _streamCts is not null) return;
            _lastGhost = ghost;
            View3D.SetScene(ghost, program);
            ScrubSlider.Maximum = Math.Max(1, program.Count);
            ScrubSlider.Value = program.Count;
        }
        catch (Exception ex)
        {
            Log("3D preview failed: " + ex.Message);
        }
    }

    private static (List<Move3D> Ghost, List<Move3D> Program) BuildScene(GcodeModel model, RecoveryPlan plan, PrinterProfile profile, int layersAfter)
    {
        var result = ResumeGenerator.Generate(model, plan, profile);
        var marker = result.Lines.FindIndex(l => l.StartsWith("; --- Original G-code", StringComparison.Ordinal));
        var lastLayer = Math.Min(model.Layers.Count - 1, plan.ResumeLayer.Index + layersAfter - 1);
        var cut = marker < 0 ? result.Lines.Count : Math.Min(result.Lines.Count, marker + 1 + model.Layers[lastLayer].EndLine - plan.ResumeLayer.StartLine);
        var program = ToolpathSimulator.Simulate(result.Lines.GetRange(0, cut), plan.Options.ProbeTravelMm);

        // Ghost of the part already on the bed, in the same (possibly shifted) Z frame. Dense near the top.
        var ghost = new List<Move3D>();
        var shift = (float)plan.ZShift;
        for (var i = plan.SurfaceLayer.Index; i >= 0; i--)
        {
            var depth = plan.SurfaceLayer.Index - i;
            if (depth > 30 && depth % 5 != 0) continue;
            var layer = model.Layers[i];
            var z = (float)layer.Z - shift;
            var step = depth > 30 ? 3 : 1;
            for (var k = 0; k < layer.Segments.Count; k += step)
            {
                var s = layer.Segments[k];
                ghost.Add(new Move3D(s.X1, s.Y1, z, s.X2, s.Y2, z, MoveKind.Extrude, -1));
            }
            if (ghost.Count > 400_000) break;
        }
        return (ghost, program);
    }
}
