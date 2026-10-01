using Avalonia.Platform.Storage;
using GcodeRecovery.Core.Recovery;

namespace GcodeRecovery.App;

public partial class MainWindow
{
    private void OnProfileSelected()
    {
        if (ProfileBox.SelectedItem is not PrinterProfile selected) return;
        _profile = selected.Clone();
        ProfileNotes.Text = _profile.Notes;
        PrepareTpl.Text = _profile.PrepareTemplate;
        LevelingTpl.Text = _profile.LevelingTemplate;
        ProbeTpl.Text = _profile.ProbeTemplate;
        PurgeTpl.Text = _profile.PurgeTemplate;
        ToolTpl.Text = _profile.ToolSelectTemplate;
        TestEndTpl.Text = _profile.TouchTestEndTemplate;
        ConnTypeBox.SelectedIndex = _profile.Flavor == FirmwareFlavor.Bambu ? 0 : 1;
        // Bambu's force-probe and homing commands are undocumented: zero Z by hand there unless the user opts in.
        ZZeroBox.SelectedIndex = _profile.Flavor == FirmwareFlavor.Bambu ? 1 : 0;
        SchedulePreview3D();
    }

    /// <summary>Copies the (possibly edited) template text boxes into the working profile.</summary>
    private void SyncTemplatesFromUi()
    {
        _profile.PrepareTemplate = PrepareTpl.Text ?? "";
        _profile.LevelingTemplate = LevelingTpl.Text ?? "";
        _profile.ProbeTemplate = ProbeTpl.Text ?? "";
        _profile.PurgeTemplate = PurgeTpl.Text ?? "";
        _profile.ToolSelectTemplate = ToolTpl.Text ?? "";
        _profile.TouchTestEndTemplate = TestEndTpl.Text ?? "";
    }

    private async Task SaveProfileAsync()
    {
        SyncTemplatesFromUi();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save printer profile",
            SuggestedFileName = _profile.Id + ".json",
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, _profile.ToJson());
        Log($"Profile saved: {path}");
    }

    private async Task LoadProfileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load printer profile",
            FileTypeFilter = [new FilePickerFileType("Profile JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var loaded = PrinterProfile.FromJson(await File.ReadAllTextAsync(path));
            _profiles = _profiles.Where(p => p.Id != loaded.Id).Append(loaded).ToList();
            ProfileBox.ItemsSource = _profiles;
            ProfileBox.SelectedItem = loaded;
            Log($"Profile loaded: {loaded.Name}");
        }
        catch (Exception ex)
        {
            Log("Could not load profile: " + ex.Message);
        }
    }
}
