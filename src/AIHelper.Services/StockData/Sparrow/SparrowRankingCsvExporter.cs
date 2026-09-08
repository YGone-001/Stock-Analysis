using System.Globalization;
using System.IO;
using System.Text;
using AIHelper.Helpers;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

public static class SparrowRankingCsvExporter
{
    public static Task<string> ExportClassicAsync(
        IReadOnlyList<SparrowRankedCandidate> ranking,
        int topN,
        CancellationToken cancellationToken,
        string? directory = null,
        DateTimeOffset? capturedAt = null) =>
        ExportAsync(ranking, topN, cancellationToken, directory, capturedAt,
            "sparrow_classic_rank_",
            "RankingProfile,Rank,Code,Name,TotalScore,RisePercent,RiseQualityScore,Amount,LiquidityScore,BuyPressureRatio,BuyPressureScore,Adhesion,IsDataComplete,MissingFactors,IsTopN",
            candidate => new[]
            {
                Csv(candidate.RankingProfile), Integer(candidate.Rank), Csv(candidate.Code), Csv(candidate.Name),
                Number(candidate.TotalScore), Number(candidate.Features.RisePercent),
                Number(candidate.Factor(SparrowRankingFactorNames.RiseQuality).Percentile),
                Number(candidate.Features.Amount), Number(candidate.Factor(SparrowRankingFactorNames.Liquidity).Percentile),
                Number(candidate.Features.BuyPressureRatio), Number(candidate.Factor(SparrowRankingFactorNames.BuyPressure).Percentile),
                Number(candidate.Features.Adhesion), Boolean(candidate.IsDataComplete), Csv(candidate.MissingFactors ?? ""),
                Boolean(candidate.Rank <= NormalizeTopN(topN))
            });

    public static Task<string> ExportV2Async(
        IReadOnlyList<SparrowRankedCandidate> ranking,
        int topN,
        CancellationToken cancellationToken,
        string? directory = null,
        DateTimeOffset? capturedAt = null) =>
        ExportAsync(ranking, topN, cancellationToken, directory, capturedAt,
            "sparrow_v2_rank_",
            "RankingProfile,Rank,Code,Name,TotalScore,Momentum,MomentumScore,BuyPressureRatio,BuyPressureScore,AlphaMargin,AlphaStrengthScore,Amount,LiquidityScore,RisePercent,RiseQualityScore,Turnover,Adhesion,IsDataComplete,MissingFactors,IsTopN",
            candidate => new[]
            {
                Csv(candidate.RankingProfile), Integer(candidate.Rank), Csv(candidate.Code), Csv(candidate.Name),
                Number(candidate.TotalScore), Number(candidate.Features.Momentum),
                Number(candidate.Factor(SparrowRankingFactorNames.Momentum).Percentile),
                Number(candidate.Features.BuyPressureRatio), Number(candidate.Factor(SparrowRankingFactorNames.BuyPressure).Percentile),
                Number(candidate.Features.AlphaMargin), Number(candidate.Factor(SparrowRankingFactorNames.AlphaStrength).Percentile),
                Number(candidate.Features.Amount), Number(candidate.Factor(SparrowRankingFactorNames.Liquidity).Percentile),
                Number(candidate.Features.RisePercent), Number(candidate.Factor(SparrowRankingFactorNames.RiseQuality).Percentile),
                Number(candidate.Features.Turnover), Number(candidate.Features.Adhesion),
                Boolean(candidate.IsDataComplete), Csv(candidate.MissingFactors ?? ""), Boolean(candidate.Rank <= NormalizeTopN(topN))
            });

    private static async Task<string> ExportAsync(
        IReadOnlyList<SparrowRankedCandidate> ranking,
        int topN,
        CancellationToken cancellationToken,
        string? directory,
        DateTimeOffset? capturedAt,
        string prefix,
        string header,
        Func<SparrowRankedCandidate, string[]> columns)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        directory = ResolveDirectory(directory);
        Directory.CreateDirectory(directory);
        DateTimeOffset timestamp = capturedAt ?? DateTimeOffset.Now;
        string path = Path.Combine(directory, $"{prefix}{timestamp:yyyyMMdd_HHmmss}.csv");
        var lines = new List<string>(ranking.Count + 1) { header };
        foreach (SparrowRankedCandidate candidate in ranking.OrderBy(candidate => candidate.Rank))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(string.Join(',', columns(candidate)));
        }

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllLinesAsync(
                temporaryPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    private static string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }
        string configured = ConfigManager.Load().DataSavePath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ")
            : configured;
    }

    private static string Number(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("G17", CultureInfo.InvariantCulture)
            : "";

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static int NormalizeTopN(int value) =>
        Math.Clamp(value, SparrowRankingSettings.MinimumTopN, SparrowRankingSettings.MaximumTopN);
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
