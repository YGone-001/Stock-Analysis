using AIHelper.Services;
using Microsoft.Win32;

namespace AIHelper;

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? OpenHistoricalDataset()
    {
        OpenFileDialog dialog = new() { Title = "加载历史数据集", Filter = "历史数据集 (*.json)|*.json|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? SaveReplayResult(string defaultFileName) => Save(defaultFileName, "保存回放结果");
    public string? SaveBacktestResult(string defaultFileName) => Save(defaultFileName, "保存回测结果");
    private static string? Save(string defaultFileName, string title)
    {
        SaveFileDialog dialog = new() { Title = title, Filter = "JSON (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = defaultFileName, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
