using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GcodeRecovery.Core.Analysis;
using GcodeRecovery.Core.Gcode;
using GcodeRecovery.Core.IO;
using GcodeRecovery.Core.Recovery;

namespace GcodeRecovery.App;

public partial class MainWindow : Window
{
    private GcodeSource? _source;
    private GcodeModel? _model;
    private FlatAreaResult? _analysis;
    private (double X, double Y)? _touch;
    private bool _touchSafe;
    private readonly List<int> _layerChoices = new();
    private List<PrinterProfile> _profiles = BuiltInProfiles.All.ToList();
    private PrinterProfile _profile = BuiltInProfiles.BambuP1S();
    private string? _lastRecoveryPath;
    private string? _lastTouchTestPath;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        HelpText.Text = HelpContent.Checklist;

        ProfileBox.ItemsSource = _profiles;
        ProfileBox.SelectionChanged += (_, _) => OnProfileSelected();
        ProfileBox.SelectedIndex = 0;

        OpenButton.Click += async (_, _) => await OpenFileAsync();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        PlateBox.SelectionChanged += async (_, _) =>
        {
            if (_source is not null && PlateBox.SelectedItem is string plate && plate != _source.ArchiveEntry)
                await LoadAsync(_source.FilePath, plate);
        };

        AnalyzeButton.Click += async (_, _) => await AnalyzeAsync();
        SurfaceLayerBox.SelectionChanged += (_, _) => OnLayerSelectionChanged();
        MidLayerBox.IsCheckedChanged += (_, _) => SelectDefaultLayer();
        Preview.TouchPointPicked += (_, p) => SetTouchPoint(p.X, p.Y, userPicked: true);
        GenerateButton.Click += async (_, _) => await SaveAsync(touchTestOnly: false);
        TouchTestButton.Click += async (_, _) => await SaveAsync(touchTestOnly: true);

        ScrubSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) View3D.VisibleMoves = (int)ScrubSlider.Value;
        };
        PreviewLayersBox.ValueChanged += (_, _) => SchedulePreview3D();
        ResetViewButton.Click += (_, _) => View3D.ResetView();
        foreach (var box in new[] { FrameBox, GapBox }) box.SelectionChanged += (_, _) => SchedulePreview3D();

        SaveProfileButton.Click += async (_, _) => await SaveProfileAsync();
        LoadProfileButton.Click += async (_, _) => await LoadProfileAsync();
        ResetProfileButton.Click += (_, _) => OnProfileSelected();

        InitPrinterTab();
        InitStreamTab();
        Closing += async (_, _) =>
        {
            _streamCts?.Cancel();
            await ShutdownPrinterAsync();
        };
        Log("Ready. Open the G-code file that was printed, enter the measured height and press Analyze.");
        KeyDown += (_, e) =>
        {
            // F11 toggles fullscreen; Escape leaves it.
            if (e.Key == Key.F11) WindowState = WindowState == WindowState.FullScreen ? WindowState.Maximized : WindowState.FullScreen;
            else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) WindowState = WindowState.Maximized;
        };
        Opened += async (_, _) => await HandleCommandLineAsync(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    /// <summary>Optional: <c>GcodeRecovery [file] [--height mm] [--range mm] [--tab n] [--stream-dry]</c> opens and analyzes directly.</summary>
    private async Task HandleCommandLineAsync(string[] args)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (file is null) return;
        await LoadAsync(Path.GetFullPath(file));
        var r = Array.IndexOf(args, "--range");
        if (r >= 0 && r + 1 < args.Length && decimal.TryParse(args[r + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
            RangeBox.Value = range;
        var h = Array.IndexOf(args, "--height");
        if (h >= 0 && h + 1 < args.Length && double.TryParse(args[h + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height))
        {
            HeightBox.Value = (decimal)height;
            await AnalyzeAsync();
        }
        if (args.Contains("--stream-dry"))
        {
            await LoadStreamFromRecoveryAsync();
            DryRunBox.IsChecked = true;
            await StartStreamAsync();
            _streamer?.Nudge(0.4, -0.2, 0.06);
            Tabs.SelectedIndex = 0;
        }
        var t = Array.IndexOf(args, "--tab");
        if (t >= 0 && t + 1 < args.Length && int.TryParse(args[t + 1], out var tab)) Tabs.SelectedIndex = tab;
    }

    // ------------------------------------------------------------------ file loading

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open the G-code that was printed",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("G-code / Bambu sliced 3MF") { Patterns = ["*.gcode", "*.3mf", "*.gco", "*.g"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadAsync(path);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is not null) await LoadAsync(path);
    }

    private async Task LoadAsync(string path, string? plate = null)
    {
        if (_busy) return;
        await RunBusy($"Loading {Path.GetFileName(path)}…", async () =>
        {
            var (source, model, plates) = await Task.Run(() =>
            {
                var src = GcodeSource.Load(path, plate);
                var plateList = src.IsArchive ? GcodeSource.ListPlates(path) : [];
                return (src, LayerParser.Parse(src.Lines), plateList);
            });
            _source = source;
            _model = model;
            _analysis = null;
            _touch = null;

            PlateBox.ItemsSource = plates;
            PlateBox.IsVisible = plates.Count > 1;
            PlateBox.SelectedItem = source.ArchiveEntry;

            FileInfo.Text =
                $"{Path.GetFileName(path)}{(source.IsArchive ? $"  [{source.ArchiveEntry}]" : "")}\n" +
                $"{model.Slicer}{(model.PrinterModel is null ? "" : " · " + model.PrinterModel)} · {source.Lines.Count:N0} lines\n" +
                $"{model.Layers.Count} layers · height {model.MaxZ:0.00} mm · first layer {model.FirstLayerHeight:0.###} mm" +
                (model.FirstLayerHeightFromSettings ? "" : " (from first layer Z)") +
                (model.LayerDetection == "Z" ? "\nNo layer markers: layers detected from Z moves." : "");
            AutoSelectProfile(model);
            AnalyzeButton.IsEnabled = model.Layers.Count > 1;
            SurfaceLayerBox.ItemsSource = null;
            Preview.Show(model.Layers.Count > 0 ? model.Layers[^1] : null, null, null, null, true);
            UpdateActionButtons();
            Log($"Loaded {Path.GetFileName(path)}: {model.Layers.Count} layers, {model.LayerDetection.ToLowerInvariant()}-based detection.");
        });
    }

    private void AutoSelectProfile(GcodeModel model)
    {
        var hint = (model.PrinterModel ?? "") + " " + model.Slicer;
        var id = hint.Contains("U1", StringComparison.OrdinalIgnoreCase) || hint.Contains("Snapmaker", StringComparison.OrdinalIgnoreCase) ? "snapmaker-u1"
            : hint.Contains("Bambu", StringComparison.OrdinalIgnoreCase) || hint.Contains("P1S", StringComparison.OrdinalIgnoreCase) ? "bambu-p1s"
            : null;
        var match = id is null ? null : _profiles.FirstOrDefault(p => p.Id == id);
        if (match is not null && !ReferenceEquals(ProfileBox.SelectedItem, match)) ProfileBox.SelectedItem = match;
    }

    // ------------------------------------------------------------------ helpers

    private RecoveryOptions ReadOptions() => new()
    {
        ZFrame = FrameBox.SelectedIndex == 1 ? ZFrameMode.KeepOriginal : ZFrameMode.ShiftToContactZero,
        Gap = GapBox.SelectedIndex == 1 ? ResumeGapMode.FirstLayerHeight : ResumeGapMode.LayerThickness,
        ProbeSpeedMmS = Num(ProbeSpeedBox, 2),
        ProbeNozzleMaxTemp = Num(ProbeTempBox, 80),
        CooldownSeconds = (int)Num(CooldownBox, 180),
        ClearanceMm = Num(ClearanceBox, 5),
        ProbeTravelMm = Num(TravelBox, 40),
        PurgeLengthMm = Num(PurgeBox, 30),
        TriggerOvertravelMm = Num(OvertravelBox, 0),
        HomeZAtClearSpot = ZHomeBox.IsChecked == true,
    };

    private static double Num(NumericUpDown box, double fallback) => box.Value is { } v ? (double)v : fallback;

    private void Log(string message) =>
        LogBox.Text = $"{DateTime.Now:HH:mm:ss}  {message}\n" + (LogBox.Text?.Length > 20000 ? LogBox.Text[..20000] : LogBox.Text);

    private async Task RunBusy(string message, Func<Task> work)
    {
        _busy = true;
        Cursor = new Cursor(StandardCursorType.Wait);
        Log(message);
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            _busy = false;
            Cursor = Cursor.Default;
        }
    }

    private void UpdateActionButtons()
    {
        var ready = _model is not null && _analysis is not null && _touch is not null && SelectedSurfaceLayer() is { } i && i < _model.Layers.Count - 1;
        GenerateButton.IsEnabled = ready;
        TouchTestButton.IsEnabled = ready;
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);
}
