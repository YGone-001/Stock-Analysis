using AIHelper.Helpers;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Neutral same-snapshot comparison of two V2-family strategy versions (V2/V3/V4/V5). Both scans run through
/// the shared scanner (and therefore the shared K-line cache), and the diff carries no winner semantics. The
/// frozen Classic-vs-V2 Compare mode stays untouched; this service serves version-pair research only.
/// </summary>
public sealed class SparrowPairComparisonService
{
    private readonly SparrowScannerService _scanner;
    private readonly SparrowRankingEngine _ranking;

    public SparrowPairComparisonService(SparrowScannerService scanner, SparrowRankingEngine? ranking = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _ranking = ranking ?? new SparrowRankingEngine();
    }

    public async Task<SparrowPairComparisonResult> CompareAsync(
        IEnumerable<(string Code, string Name)> targetPool,
        SparrowPairComparisonSide left,
        SparrowScanParameters leftParameters,
        SparrowMomentumGateConfig? leftMomentumGates,
        SparrowVolumeGateConfig? leftVolumeGates,
        SparrowWeeklyGateConfig? leftWeeklyGates,
        SparrowPairComparisonSide right,
        SparrowScanParameters rightParameters,
        SparrowMomentumGateConfig? rightMomentumGates,
        SparrowVolumeGateConfig? rightVolumeGates,
        SparrowWeeklyGateConfig? rightWeeklyGates,
        IProgress<SparrowScanReport>? progress,
        CancellationToken cancellationToken,
        bool exportCsv = true)
    {
        ArgumentNullException.ThrowIfNull(targetPool);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(leftParameters);
        ArgumentNullException.ThrowIfNull(rightParameters);

        List<(string Code, string Name)> universe = targetPool
            .Where(stock => SparrowClassicScanner.IsEligibleStock(stock.Code, stock.Name))
            .GroupBy(stock => stock.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(stock => stock.Code, StringComparer.Ordinal)
            .ToList();

        progress?.Report(new SparrowScanReport { LogMessage = $"🧪 [对照组] {left.Version} vs {right.Version}；候选池 {universe.Count}" });

        List<SparrowV2Candidate> leftCandidates = await _scanner.ScanWithFeaturesAsync(universe, leftParameters, progress, cancellationToken, leftMomentumGates, leftVolumeGates, leftWeeklyGates);
        cancellationToken.ThrowIfCancellationRequested();
        List<SparrowV2Candidate> rightCandidates = await _scanner.ScanWithFeaturesAsync(universe, rightParameters, progress, cancellationToken, rightMomentumGates, rightVolumeGates, rightWeeklyGates);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SparrowRankedCandidate> leftRanking = _ranking.RankV2(
            leftCandidates.Select(candidate => candidate.RankingFeatures), leftParameters.MinRise, leftParameters.MaxRise, leftParameters.CheckAlpha);
        IReadOnlyList<SparrowRankedCandidate> rightRanking = _ranking.RankV2(
            rightCandidates.Select(candidate => candidate.RankingFeatures), rightParameters.MinRise, rightParameters.MaxRise, rightParameters.CheckAlpha);

        Dictionary<string, (SparrowV2Candidate Candidate, SparrowRankedCandidate? Ranked)> leftByCode = Join(leftCandidates, leftRanking);
        Dictionary<string, (SparrowV2Candidate Candidate, SparrowRankedCandidate? Ranked)> rightByCode = Join(rightCandidates, rightRanking);

        List<SparrowPairComparisonRow> rows = new();
        foreach (string code in leftByCode.Keys.Union(rightByCode.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool inLeft = leftByCode.TryGetValue(code, out var leftEntry);
            bool inRight = rightByCode.TryGetValue(code, out var rightEntry);
            rows.Add(new SparrowPairComparisonRow
            {
                Code = code,
                Name = (inLeft ? leftEntry.Candidate.Name : rightEntry.Candidate!.Name) ?? code,
                LeftRank = inLeft ? leftEntry.Ranked?.Rank : null,
                LeftScore = inLeft ? leftEntry.Ranked?.TotalScore : null,
                LeftReason = inLeft ? leftEntry.Candidate.Reason : null,
                RightRank = inRight ? rightEntry.Ranked?.Rank : null,
                RightScore = inRight ? rightEntry.Ranked?.TotalScore : null,
                RightReason = inRight ? rightEntry.Candidate!.Reason : null,
                Category = inLeft && inRight ? SparrowPairComparisonCategory.Both
                    : inLeft ? SparrowPairComparisonCategory.LeftOnly
                    : SparrowPairComparisonCategory.RightOnly
            });
        }

        SparrowPairComparisonRow[] frozenRows = rows.ToArray();
        int both = frozenRows.Count(row => row.Category == SparrowPairComparisonCategory.Both);
        var metrics = new SparrowPairComparisonMetrics(universe.Count, leftCandidates.Count, rightCandidates.Count, both,
            frozenRows.Count(row => row.Category == SparrowPairComparisonCategory.LeftOnly),
            frozenRows.Count(row => row.Category == SparrowPairComparisonCategory.RightOnly));
        var result = new SparrowPairComparisonResult(left, right, DateTimeOffset.Now, frozenRows, leftRanking, rightRanking, metrics);
        if (exportCsv)
        {
            result.CsvPath = await ExportCsvAsync(result, cancellationToken);
        }

        return result;
    }

    private static Dictionary<string, (SparrowV2Candidate Candidate, SparrowRankedCandidate? Ranked)> Join(
        IReadOnlyList<SparrowV2Candidate> candidates, IReadOnlyList<SparrowRankedCandidate> ranking)
    {
        Dictionary<string, SparrowRankedCandidate> rankedByCode = ranking.ToDictionary(item => item.Code, StringComparer.Ordinal);
        return candidates.ToDictionary(candidate => candidate.Code,
            candidate => (candidate, rankedByCode.TryGetValue(candidate.Code, out SparrowRankedCandidate? ranked) ? ranked : null),
            StringComparer.Ordinal);
    }

    private static async Task<string> ExportCsvAsync(SparrowPairComparisonResult result, CancellationToken cancellationToken)
    {
        string directory = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"麻雀对照组_{result.Left.Version}_vs_{result.Right.Version}_{result.CapturedAt:yyyyMMdd_HHmmss}.csv");
        var lines = new List<string> { "Code,Name,Category,LeftRank,LeftScore,LeftReason,RightRank,RightScore,RightReason" };
        foreach (SparrowPairComparisonRow row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(string.Join(',',
                Csv(row.Code), Csv(row.Name), row.Category.ToString(),
                row.LeftRank?.ToString() ?? "", row.LeftScore?.ToString("R") ?? "", Csv(row.LeftReason ?? ""),
                row.RightRank?.ToString() ?? "", row.RightScore?.ToString("R") ?? "", Csv(row.RightReason ?? "")));
        }
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines), cancellationToken);
        return path;
    }

    private static string Csv(string value) => value.Contains(',', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
