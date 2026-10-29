using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using AIHelper.Core.StockData;
using AIHelper.Helpers;

namespace AIHelper.Services.StockData;

public sealed record T1RankingArtifactPaths(string CsvPath, string SidecarPath, string ContentFingerprint);

/// <summary>
/// Compact deterministic research artifact for one T1-Rank result. The CSV is for user inspection;
/// the JSON sidecar is the authoritative provenance. Filenames derive from the scan RunId, content
/// carries no volatile export timestamp, so re-exports of the same snapshot and frozen profile are
/// byte-identical and share one content fingerprint. Crash-safe bounded writes; never touches the
/// source snapshot or other files.
/// </summary>
public static class T1RankingArtifact
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    public static object Content(T1RankingResult result) => new
    {
        SchemaVersion,
        result.Request,
        result.RankingProfile,
        result.EvidenceClass,
        result.Coverage,
        NotIncluded = result.NotIncluded.Select(n => new { n.Dimension, n.Reason }),
        Disclaimer = "Diagnostic research artifact; never evaluated against next-day outcomes; not a trading signal; "
            + "coverage limited to the originally captured TopN snapshot candidates.",
        result.Shortlist,
        result.Excluded
    };

    public static byte[] ContentBytes(T1RankingResult result) => JsonSerializer.SerializeToUtf8Bytes(Content(result), Options);

    public static T1RankingArtifactPaths Write(T1RankingResult result, string? directory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        byte[] content = ContentBytes(result);
        string fingerprint = Hash(content);
        // Authoritative sidecar first: a cancelled export can only leave the self-contained provenance
        // artifact, never a bare CSV masquerading as a complete result. Content is nested JSON, not base64.
        byte[] sidecar = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion, Fingerprint = fingerprint, Content = Content(result) }, Options);
        if (sidecar.Length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("T1 artifact exceeds size bound");
        directory = ResolveDirectory(directory);
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, $"{result.Request.RunId:N}-{result.Request.RankingVersion.ToLowerInvariant()}");
        WriteFile(stem + ".json", sidecar, token);
        WriteFile(stem + ".csv", Csv(result), token);
        return new(stem + ".csv", stem + ".json", fingerprint);
    }

    private static void WriteFile(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Csv(T1RankingResult result)
    {
        static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        static string Time(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "";
        var lines = new List<string>
        {
            "T1Rank,Status,Code,Name,OriginalStrategy,OriginalStrategyVersion,OriginalRank,OriginalScore,PriceOnlyV1Score,"
            + "M5,M15,PersistenceScore,HighProximityScore,ChaseModerationScore,ContributionM5,ContributionM15,ContributionPersistence,"
            + "ContributionHighProximity,ContributionChaseModeration,Return5mPct,Return15mPct,Persistence,PullbackPct,DataTime,"
            + "EvidenceStatus,ExclusionCode,ExclusionReason"
        };
        foreach (var c in result.Shortlist)
        {
            var e = c.Explanation;
            lines.Add(string.Join(',', c.T1Rank, "ranked", Csv(c.Symbol), Csv(c.Name), Csv(e.Strategy), Csv(e.StrategyVersion),
                e.OriginalRank, e.OriginalScore.ToString("G17", CultureInfo.InvariantCulture), Dec(e.FinalScore),
                Dec(e.Components.M5), Dec(e.Components.M15), Dec(e.Components.Persistence), Dec(e.Components.HighProximity),
                Dec(e.Components.ChaseModeration), Dec(e.Contributions.M5), Dec(e.Contributions.M15), Dec(e.Contributions.Persistence),
                Dec(e.Contributions.HighProximity), Dec(e.Contributions.ChaseModeration), Dec(e.Return5mPct), Dec(e.Return15mPct),
                Dec(e.Persistence), Dec(e.PullbackPct), Time(e.DataTime), Csv(e.EvidenceStatus), "", ""));
        }
        foreach (var x in result.Excluded)
        {
            lines.Add(string.Join(',', "", "excluded", Csv(x.Symbol), Csv(x.Name), Csv(x.Strategy), Csv(x.StrategyVersion),
                x.OriginalRank, "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", Time(x.DataTime), "",
                x.Code.ToString(), Csv(x.Reason)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).ToArray();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) return directory;
        string configured = ConfigManager.Load().DataSavePath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ")
            : Path.Combine(configured, "t1-ranking");
    }
}
