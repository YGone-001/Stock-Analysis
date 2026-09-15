namespace AIHelper.Services;

/// <summary>Small presentation boundary for research file selection. View-models never construct WPF dialogs.</summary>
public interface IFileDialogService
{
    string? OpenHistoricalDataset();
    string? SaveReplayResult(string defaultFileName);
    string? SaveBacktestResult(string defaultFileName);
}
