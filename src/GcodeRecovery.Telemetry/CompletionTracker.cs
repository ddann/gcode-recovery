namespace GcodeRecovery.Telemetry;

/// <summary>
/// Decides when an uploaded recovery job has really finished, from the printer's status reports.
/// The job must first be seen running (so a stale "finished" state from the previous print doesn't count),
/// then reach a finished state. Cancelled or failed jobs are dropped. The file name is only compared locally.
/// </summary>
public sealed class CompletionTracker(string uploadedFileName, Action onCompleted)
{
    private static readonly string[] Running = ["RUNNING", "PREPARE", "SLICING", "PRINTING", "PAUSE", "PAUSED"];
    private static readonly string[] Finished = ["FINISH", "COMPLETE"];
    private static readonly string[] Failed = ["FAILED", "CANCELLED", "ERROR"];

    private readonly string _name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(uploadedFileName));
    private bool _started;

    public bool IsDone { get; private set; }

    /// <summary>Feed every status update. <paramref name="currentFile"/> may be null when the printer doesn't report it.</summary>
    public void Observe(string? state, string? currentFile)
    {
        if (IsDone || string.IsNullOrEmpty(state)) return;
        var s = state.Trim().ToUpperInvariant();
        var sameFile = currentFile is null || currentFile.Contains(_name, StringComparison.OrdinalIgnoreCase);
        if (!sameFile) return;

        if (Running.Contains(s))
        {
            _started = true;
        }
        else if (_started && Finished.Contains(s))
        {
            IsDone = true;
            onCompleted();
        }
        else if (_started && Failed.Contains(s))
        {
            IsDone = true; // not counted
        }
    }
}
