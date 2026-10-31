using System.Text.Json;

namespace AIHelper.Services.StockData;

public static class T1ResearchSamples
{
    public const string ManifestFile = "manifest.json", SnapshotFile = "original-snapshot.json", FeaturesFile = "features.json";
    internal static Action<string>? BoundaryHook;
    public static string SnapshotPath(string directory) => Path.Combine(directory, SnapshotFile);
    public static string RankingPath(string directory, T1ResearchManifest manifest) => Path.Combine(directory, manifest.Content.RankingDirectoryName);
    private static T1ResearchManifestContent Build(string snapshotPath, string rankingPath, IReadOnlyList<IntradayFeatureResult> features,
        T1EvidenceOrigin origin, T1ResearchTimings timings, IReadOnlyDictionary<string, string> hashes)
    {
        var source = T1EvidenceReadiness.VerifySnapshot(snapshotPath);
        var sample = T1EvidenceReadiness.Evaluate(new(snapshotPath, rankingPath, Origin: origin));
        if (source.Structure != T1ReadinessStatus.PASS || sample.Binding != T1ReadinessStatus.PASS || sample.Coverage is null)
            throw new InvalidDataException("RESEARCH_SOURCE_BINDING_INVALID");
        var s = source.Snapshot!;
        if (features.Count != s.Symbols.Count || features.Count > 20 || features.Select(f => f.Symbol).Distinct().Count() != features.Count
            || features.Any(f => f.SchemaVersion != 1 || f.ComputationVersion != IntradayFeatureAnalysis.Version
                || f.SnapshotFingerprint != source.Fingerprint || f.Scan != s.Scan || f.RequestedAsOf != s.RequestedAsOf)
            || s.Symbols.Any(c => !features.Any(f => f.Symbol == c.Symbol))) throw new InvalidDataException("ORIGINAL_FEATURE_RESULTS_REQUIRED");
        DateTimeOffset?[] observed = { timings.FeatureCompletedAtUtc, timings.RankingCompletedAtUtc, timings.ExportCompletedAtUtc };
        if (observed.Any(t => t < s.CapturedAtUtc) || observed[0] is { } ft && observed[1] < ft || observed[1] is { } rt && observed[2] < rt)
            throw new InvalidDataException("RESEARCH_TIMING_ORDER_INVALID");
        using var document = JsonDocument.Parse(T1ResearchStorage.Read(Path.Combine(rankingPath, T1RankingArtifact.SidecarFileName)));
        var excluded = document.RootElement.GetProperty("Content").GetProperty("Excluded").Deserialize<T1RankingExclusion[]>(T1ResearchStorage.Json)!;
        var candidates = s.Symbols.OrderBy(c => c.Rank).Select(c =>
        {
            var f = features.Single(f => f.Symbol == c.Symbol);
            var quality = MinuteQualityAggregation.Summarize(c.Minutes, s.TradingDate, s.RequestedAsOf, IntradayFreshnessPolicy.Default);
            return new T1ResearchCandidate(new(c.Symbol, c.Name, c.Rank, c.Score), T1EvidenceReadiness.ExchangeOf(c.Symbol)!, c.Minutes.Rows.Count,
                f.EffectiveObservationTime, f.Return5m, f.Return15m, quality,
                quality.Anomalies.Any(a => a.Quality is IntradayQuality.FutureData or IntradayQuality.WrongTradingDate or IntradayQuality.InconsistentEvidence)
                    || c.Minutes.Rows.Any(r => r.ObservedAtUtc > s.RequestedAsOf || r.AvailableAtUtc > s.RequestedAsOf || r.TransportReceivedAtUtc > s.RequestedAsOf)
                    ? T1ReadinessStatus.FAIL : c.Minutes.Rows.Count == 0 || c.Minutes.Rows.Any(r => r.ExchangeEventTime is null || r.AvailableAtUtc is null || r.TransportReceivedAtUtc is null)
                    ? T1ReadinessStatus.NOT_VERIFIED : T1ReadinessStatus.PASS,
                c.Minutes.Rows.Select(r => r.VolumeUnit).Distinct().Order(StringComparer.Ordinal).ToArray(),
                c.Minutes.Rows.Select(r => r.AmountUnit).Distinct().Order(StringComparer.Ordinal).ToArray(),
                c.Minutes.Rows.Count > 0 && c.Minutes.Rows.All(r => r.EventTimeSemanticsVerified), "UNKNOWN_BASIS",
                c.Minutes.Rows.Select(r => r.Provider + "/" + r.Provenance.Source).Distinct().Order(StringComparer.Ordinal).ToArray(),
                excluded.SingleOrDefault(x => x.Symbol == c.Symbol));
        }).ToArray();
        var start = new TimeOnly(14, 30); var end = new TimeOnly(14, 55);
        bool InWindow(DateTimeOffset t) => ChinaExchangeTime.Date(t) == s.TradingDate
            && TimeOnly.FromDateTime(t.ToOffset(ChinaExchangeTime.Offset).DateTime) >= start
            && TimeOnly.FromDateTime(t.ToOffset(ChinaExchangeTime.Offset).DateTime) <= end;
        return new(1, s.Scan.RunId, s.Scan.Strategy, s.Scan.StrategyVersion, s.Scan.ParameterFingerprint, s.TradingDate,
            s.Scan.ScanStartedAtUtc, s.Scan.ScanCompletedAtUtc, s.RequestedAsOf, s.AcquisitionStartedAtUtc, s.CapturedAtUtc,
            checked((long)(s.CapturedAtUtc - s.Scan.ScanCompletedAtUtc).TotalMilliseconds), start, end,
            !InWindow(s.Scan.ScanStartedAtUtc) || !InWindow(s.RequestedAsOf) || !InWindow(s.CapturedAtUtc),
            source.Fingerprint, sample.PackageFingerprint, Path.GetFileName(rankingPath), sample.Coverage, origin, sample.EvidenceClass,
            timings, candidates, hashes, new[] { "Diagnostic local evidence; PIT_AUTHENTICATION_UNVERIFIED", "PRODUCTION_EVALUATION_BLOCKED",
                "Minute adjustment basis unknown; raw daily prices do not establish compatibility", "Missing timestamps are not reconstructed; no execution evidence" },
            new[] { "Original schema-1 snapshot bytes", "Original immutable PriceOnlyV1 CSV/JSON bytes", "Original completed feature results" });
    }
    public static T1ResearchManifest Verify(string directory) => VerifyCore(directory, true);
    internal static T1ResearchManifest VerifyStaged(string directory) => VerifyCore(directory, false);
    private static T1ResearchManifest VerifyCore(string directory, bool requireIdentity)
    {
        string root = T1ResearchStorage.PathChecked(directory);
        var m = T1ResearchStorage.Load<T1ResearchManifest>(Path.Combine(root, ManifestFile));
        string rankName = m.Content.RankingDirectoryName;
        if (m.SchemaVersion != 1 || m.Content.SchemaVersion != 1 || rankName != Path.GetFileName(rankName) || rankName.Contains('\\') || rankName.Contains('/')
            || m.ArchivedAtUtc == default || m.Fingerprint != T1ResearchStorage.Fingerprint(m.Content)) throw new InvalidDataException("ARCHIVE_MANIFEST_INVALID");
        if (requireIdentity && Path.GetFileName(root) != m.Content.RunId.ToString("N") + "-" + m.Fingerprint)
            throw new InvalidDataException("ARCHIVE_DIRECTORY_IDENTITY_INVALID");
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Take(5).Order(StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(new[] { ManifestFile, SnapshotFile, FeaturesFile, rankName }.Order(StringComparer.Ordinal))
            || Directory.EnumerateFileSystemEntries(Path.Combine(root, rankName)).Take(3).Count() != 2)
            throw new InvalidDataException("ARCHIVE_UNEXPECTED_COMPONENT");
        var hashes = HashFiles(root, rankName);
        var features = T1ResearchStorage.Load<IntradayFeatureResult[]>(Path.Combine(root, FeaturesFile));
        var rebuilt = Build(SnapshotPath(root), Path.Combine(root, rankName), features, m.Content.Origin, m.Content.Timings, hashes);
        if (T1ResearchStorage.Fingerprint(rebuilt) != m.Fingerprint) throw new InvalidDataException("ARCHIVE_CONTENT_INVALID");
        return m;
    }
    private static SortedDictionary<string, string> HashFiles(string root, string rankingName)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in new[] { SnapshotFile, FeaturesFile, rankingName + "/" + T1RankingArtifact.CsvFileName, rankingName + "/" + T1RankingArtifact.SidecarFileName })
            hashes.Add(file, T1ResearchStorage.Hash(T1ResearchStorage.Read(Path.Combine(root, file))));
        return hashes;
    }
    public static T1PreparedResearchSample Prepare(string snapshotPath, string rankingPath, IReadOnlyList<IntradayFeatureResult> features,
        string archiveRoot, T1EvidenceOrigin origin = T1EvidenceOrigin.UNVERIFIED, T1ResearchTimings? timings = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); string root = T1ResearchStorage.Root(archiveRoot);
        snapshotPath = T1ResearchStorage.PathChecked(snapshotPath); rankingPath = T1ResearchStorage.PathChecked(rankingPath);
        if (root.Equals(rankingPath, StringComparison.OrdinalIgnoreCase) || root.StartsWith(rankingPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ARCHIVE_OVERLAPS_INPUT");
        string rankName = Path.GetFileName(rankingPath);
        // Validate original results before touching archive storage. Never analyzes or ranks replacement data.
        _ = Build(snapshotPath, rankingPath, features, origin, timings ?? new(), new Dictionary<string, string>());
        Directory.CreateDirectory(root); string stage = Path.Combine(root, ".t1-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage); Directory.CreateDirectory(Path.Combine(stage, rankName));
            T1ResearchStorage.Write(Path.Combine(stage, SnapshotFile), T1ResearchStorage.Read(snapshotPath));
            T1ResearchStorage.Write(Path.Combine(stage, FeaturesFile), T1ResearchStorage.Bytes(features));
            foreach (string file in new[] { T1RankingArtifact.CsvFileName, T1RankingArtifact.SidecarFileName })
                T1ResearchStorage.Write(Path.Combine(stage, rankName, file), T1ResearchStorage.Read(Path.Combine(rankingPath, file)));
            BoundaryHook?.Invoke("copied"); token.ThrowIfCancellationRequested();
            var content = Build(SnapshotPath(stage), Path.Combine(stage, rankName), features, origin, timings ?? new(), HashFiles(stage, rankName));
            var manifest = new T1ResearchManifest(1, T1ResearchStorage.Fingerprint(content), DateTimeOffset.UtcNow, content);
            T1ResearchStorage.Write(Path.Combine(stage, ManifestFile), T1ResearchStorage.Bytes(manifest));
            _ = VerifyStaged(stage); token.ThrowIfCancellationRequested();
            return new(root, stage, manifest);
        }
        catch { T1ResearchStorage.RemoveStaging(root, stage); throw; }
    }
    public static T1ResearchArchive Retain(string snapshotPath, string rankingPath, IReadOnlyList<IntradayFeatureResult> features,
        string archiveRoot, T1EvidenceOrigin origin = T1EvidenceOrigin.UNVERIFIED, T1ResearchTimings? timings = null, CancellationToken token = default)
    { using var prepared = Prepare(snapshotPath, rankingPath, features, archiveRoot, origin, timings, token); return prepared.Publish(token); }
}

/// <summary>Preparation may run in the background; WPF publishes synchronously only after dispatcher ownership checks.</summary>
public sealed class T1PreparedResearchSample : IDisposable
{
    private readonly string _root, _stage;
    public T1ResearchManifest Manifest { get; }
    internal T1PreparedResearchSample(string root, string stage, T1ResearchManifest manifest) { _root = root; _stage = stage; Manifest = manifest; }
    public T1ResearchArchive Publish(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); T1ResearchStorage.PathChecked(_root);
        using var runLock = new FileStream(Path.Combine(_root, ".run-" + Manifest.Content.RunId.ToString("N") + ".lock"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        string prefix = Manifest.Content.RunId.ToString("N") + "-";
        var matches = Directory.EnumerateDirectories(_root, prefix + "*").Take(2).ToArray();
        string final = Path.Combine(_root, prefix + Manifest.Fingerprint);
        if (matches.Length > 0)
        {
            if (matches.Length != 1 || !matches[0].Equals(final, StringComparison.OrdinalIgnoreCase)
                || T1ResearchSamples.Verify(final).Fingerprint != Manifest.Fingerprint) throw new InvalidDataException("ARCHIVE_RUN_COLLISION");
            return new(final, T1ResearchSamples.Verify(final), true);
        }
        T1ResearchSamples.BoundaryHook?.Invoke("publish"); token.ThrowIfCancellationRequested();
        if (T1ResearchSamples.VerifyStaged(_stage).Fingerprint != Manifest.Fingerprint) throw new InvalidDataException("STAGING_CHANGED");
        token.ThrowIfCancellationRequested(); Directory.Move(_stage, final);
        return new(final, Manifest, false);
    }
    public void Dispose() => T1ResearchStorage.RemoveStaging(_root, _stage);
}
