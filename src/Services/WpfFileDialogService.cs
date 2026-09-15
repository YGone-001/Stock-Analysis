using AIHelper.Services;
using Microsoft.Win32;

namespace AIHelper;

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? OpenHistoricalDataset()
    {
        OpenFileDialog dialog = new() { Title = "Load Historical Dataset", Filter = "Historical dataset (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? SaveReplayResult(string defaultFileName) => Save(defaultFileName, "Save Replay Result");
    public string? SaveBacktestResult(string defaultFileName) => Save(defaultFileName, "Save Backtest Result");
    private static string? Save(string defaultFileName, string title)
    {
        SaveFileDialog dialog = new() { Title = title, Filter = "JSON (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = defaultFileName, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
