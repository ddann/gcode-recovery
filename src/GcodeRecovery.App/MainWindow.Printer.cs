using Avalonia.Media.Imaging;
using GcodeRecovery.Printers;

namespace GcodeRecovery.App;

public partial class MainWindow
{
    private IPrinterConnection? _printer;
    private CancellationTokenSource? _cameraCts;
    private int _framePending;

    private bool IsBambuConnection => ConnTypeBox.SelectedIndex == 0;

    private void InitPrinterTab()
    {
        var saved = AppSettings.Load();
        ConnTypeBox.SelectedIndex = saved.ConnectionType;
        HostBox.Text = saved.Host;
        SerialBox.Text = saved.Serial;
        CameraUrlBox.Text = saved.CameraUrl;

        ConnectButton.Click += async (_, _) => await ConnectAsync();
        DisconnectButton.Click += async (_, _) => await ShutdownPrinterAsync();
        CameraButton.Click += (_, _) => ToggleCamera();

        PauseButton.Click += async (_, _) => await Do("Pause", p => p.PauseAsync());
        ResumeButton.Click += async (_, _) => await Do("Resume", p => p.ResumeAsync());
        StopButton.Click += async (_, _) => await Do("Stop", p => p.StopAsync());
        LightOnButton.Click += async (_, _) => await Do("Light on", p => p.SetLightAsync(true));
        LightOffButton.Click += async (_, _) => await Do("Light off", p => p.SetLightAsync(false));
        HomeXYButton.Click += async (_, _) => await Send("G28 X Y");

        XMinus.Click += async (_, _) => await Jog(Axis.X, -1);
        XPlus.Click += async (_, _) => await Jog(Axis.X, 1);
        YMinus.Click += async (_, _) => await Jog(Axis.Y, -1);
        YPlus.Click += async (_, _) => await Jog(Axis.Y, 1);
        ZMinus.Click += async (_, _) => await Jog(Axis.Z, -1);
        ZPlus.Click += async (_, _) => await Jog(Axis.Z, 1);

        NozzleSetButton.Click += async (_, _) => await Send(PrinterCommands.SetNozzle(Num(NozzleSetBox, 0)));
        BedSetButton.Click += async (_, _) => await Send(PrinterCommands.SetBed(Num(BedSetBox, 0)));
        FanSetButton.Click += async (_, _) => await Send(PrinterCommands.PartFan(Num(FanSetBox, 0), IsBambuConnection));
        ConsoleSendButton.Click += async (_, _) => await SendConsole();
        ConsoleBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SendConsole(); };

        UploadTestButton.Click += async (_, _) => await UploadAndStart(_lastTouchTestPath, "touch test");
        UploadRecoveryButton.Click += async (_, _) => await UploadAndStart(_lastRecoveryPath, "recovery program");
    }

    private async Task ConnectAsync()
    {
        await ShutdownPrinterAsync();
        var settings = new ConnectionSettings
        {
            Host = HostBox.Text?.Trim() ?? "",
            Serial = SerialBox.Text?.Trim() ?? "",
            Secret = SecretBox.Text ?? "",
            CameraUrl = string.IsNullOrWhiteSpace(CameraUrlBox.Text) ? null : CameraUrlBox.Text.Trim(),
        };
        // The access code is deliberately not persisted.
        new AppSettings { ConnectionType = ConnTypeBox.SelectedIndex, Host = settings.Host, Serial = settings.Serial, CameraUrl = settings.CameraUrl ?? "" }.Save();

        IPrinterConnection printer = IsBambuConnection ? new BambuConnection(settings) : new MoonrakerConnection(settings);
        printer.Log += (_, m) => OnUi(() => PrinterLog(m));
        printer.StatusChanged += (_, s) => OnUi(() => ShowStatus(s));
        ConnectButton.IsEnabled = false;
        PrinterLog($"Connecting to {settings.Host}…");
        try
        {
            await printer.ConnectAsync();
            _printer = printer;
            ControlsPanel.IsEnabled = true;
            CameraButton.IsEnabled = true;
            DisconnectButton.IsEnabled = true;
            StatusText.Text = $"Connected: {printer.Name}";
        }
        catch (Exception ex)
        {
            PrinterLog("Connection failed: " + ex.Message);
            await printer.DisposeAsync();
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task ShutdownPrinterAsync()
    {
        StopCamera();
        if (_printer is not null)
        {
            var p = _printer;
            _printer = null;
            await p.DisposeAsync();
            PrinterLog("Disconnected.");
        }
        ControlsPanel.IsEnabled = false;
        CameraButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        ConnectButton.IsEnabled = true;
        StatusText.Text = "Not connected. Connection details are in Settings.";
    }

    private void ShowStatus(PrinterStatus s)
    {
        static string T(double? v, double? target) => v is null ? "–" : target is > 0 ? $"{v:0}/{target:0} °C" : $"{v:0} °C";
        StatusText.Text =
            $"{_printer?.Name}\nState: {s.State}   Progress: {(s.ProgressPercent is { } p ? $"{p:0}%" : "–")}" +
            $"   Layer: {s.Layer?.ToString() ?? "–"}/{s.TotalLayers?.ToString() ?? "–"}" +
            (s.RemainingMinutes is { } m ? $"   Remaining: {m} min" : "") +
            $"\nNozzle: {T(s.NozzleTemp, s.NozzleTarget)}   Bed: {T(s.BedTemp, s.BedTarget)}" +
            (s.ChamberTemp is { } c ? $"   Chamber: {c:0} °C" : "") +
            (s.FileName is { Length: > 0 } f ? $"\nFile: {f}" : "");
    }

    private async Task Do(string what, Func<IPrinterConnection, Task> action)
    {
        if (_printer is null) return;
        try
        {
            await action(_printer);
            PrinterLog(what + " sent.");
        }
        catch (Exception ex)
        {
            PrinterLog($"{what} failed: {ex.Message}");
        }
    }

    private Task Send(params string[] lines) => Do(string.Join(" | ", lines), p => p.SendGcodeAsync(lines));

    private Task Jog(Axis axis, int direction)
    {
        var step = StepBox.SelectedIndex switch { 0 => 0.1, 2 => 10.0, _ => 1.0 };
        var feed = axis == Axis.Z ? 600 : 3000;
        return Do($"Jog {axis}{direction * step:+0.###;-0.###}", p => p.SendGcodeAsync(PrinterCommands.Jog(axis, direction * step, feed)));
    }

    private async Task SendConsole()
    {
        var text = ConsoleBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ConsoleBox.Text = "";
        await Send(text);
    }

    private async Task UploadAndStart(string? path, string what)
    {
        if (_printer is null)
        {
            PrinterLog("Connect to the printer first (details in Settings).");
            return;
        }
        if (path is null || !File.Exists(path))
        {
            PrinterLog($"Save the {what} on the Recover tab first.");
            return;
        }
        UploadProgress.IsVisible = true;
        UploadProgress.Value = 0;
        await Do($"Upload and start {Path.GetFileName(path)}", p =>
            p.UploadAndStartAsync(path, new Progress<double>(v => UploadProgress.Value = v)));
        UploadProgress.IsVisible = false;
    }

    private void ToggleCamera()
    {
        if (_cameraCts is not null)
        {
            StopCamera();
            return;
        }
        if (_printer is null) return;
        var printer = _printer;
        _cameraCts = new CancellationTokenSource();
        var ct = _cameraCts.Token;
        CameraButton.Content = "Stop camera";
        CameraInfo.Text = "Connecting…";
        var frames = 0;
        var started = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                await printer.StreamCameraAsync(jpeg =>
                {
                    frames++;
                    // Drop frames while the UI is still decoding the previous one.
                    if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
                    OnUi(() =>
                    {
                        try
                        {
                            var old = CameraImage.Source as IDisposable;
                            CameraImage.Source = new Bitmap(new MemoryStream(jpeg));
                            old?.Dispose();
                            CameraInfo.Text = $"{frames / Math.Max(1, (DateTime.UtcNow - started).TotalSeconds):0.0} fps";
                        }
                        catch (Exception ex)
                        {
                            CameraInfo.Text = "Bad frame: " + ex.Message;
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _framePending, 0);
                        }
                    });
                }, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnUi(() =>
                {
                    PrinterLog("Camera: " + ex.Message);
                    StopCamera();
                });
            }
        }, ct);
    }

    private void StopCamera()
    {
        _cameraCts?.Cancel();
        _cameraCts = null;
        CameraButton.Content = "Camera";
        CameraInfo.Text = "";
    }

    private void PrinterLog(string message) => Log("[printer] " + message);
}
