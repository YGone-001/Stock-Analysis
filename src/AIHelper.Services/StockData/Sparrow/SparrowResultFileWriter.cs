using System.IO;
using System.Text;
using AIHelper.Helpers;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Writes Sparrow scan results to the data directory. Shared by the V2 and Classic
/// scanners to keep result-file formatting in one place.
/// </summary>
public static class SparrowResultFileWriter
{
    public static string ResolveDataDirectory()
    {
        string path = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        return path;
    }

    public static async Task<string> WriteV2Async(IReadOnlyCollection<SparrowV2Candidate> results)
    {
        string directory = ResolveDataDirectory();
        Directory.CreateDirectory(directory);

        string fileName = $"麻雀池高速筛选结果_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt";
        string fullPath = Path.Combine(directory, fileName);

        using StreamWriter writer = new StreamWriter(fullPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【麻雀战法高速版】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (var item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
        }
        return fullPath;
    }

    public static async Task<string> WriteClassicAsync(IReadOnlyCollection<SparrowClassicCandidate> results)
    {
        string directory = ResolveDataDirectory();
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"麻雀池_Classic_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
        await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【Sparrow Classic】选股结果\nStrategy: {SparrowClassicCandidate.StrategyName}\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (SparrowClassicCandidate item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t策略: {item.Strategy} \t说明: {item.Reason}");
        }
        return filePath;
    }
}
