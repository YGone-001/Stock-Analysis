using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using AIHelper.Core.StockData;
using AIHelper.Helpers;

namespace AIHelper.Services.StockData;

public sealed record T1RankingArtifactPaths(string PackageDirectory, string CsvPath, string SidecarPath,
	string ContentFingerprint, bool ReusedExisting);

/// <summary>
/// Immutable content-addressed publication: directory {RunId}-priceonlyv1-{full content fingerprint} with
/// ranking.csv + provenance.json. Fingerprint = hash of compact canonical content (no volatile
/// timestamps); all-or-nothing staging + atomic rename; published dirs never modified.
/// </summary>
public static class T1RankingArtifact
{
	public const int SchemaVersion = 1;
	public const string CsvFileName = "ranking.csv";
	public const string SidecarFileName = "provenance.json";
	private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
	// Relaxed encoder keeps '+' (offsets) and non-ASCII unescaped so canonical bytes survive a JSON round-trip.
	private static readonly JsonSerializerOptions CanonicalOptions = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
		Disclaimer = "Diagnostic research artifact; never evaluated against next-day outcomes; not a trading signal; captured TopN coverage only.",
		result.Shortlist,
		result.Excluded
	};

	/// <summary>Canonical compact bytes hashed into the content fingerprint; depth-independent on re-parse.</summary>
	public static byte[] ContentBytes(T1RankingResult result) => JsonSerializer.SerializeToUtf8Bytes(Content(result), CanonicalOptions);

	public static string PackageName(Guid runId, string rankingVersion, string contentFingerprint) =>
		$"{runId:N}-{rankingVersion.ToLowerInvariant()}-{contentFingerprint}";

	/// <summary>Deterministic fault/cancellation seam at each publication boundary; tests only.</summary>
	internal static Action<string>? BoundaryHook;

	public static T1RankingArtifactPaths Write(T1RankingResult result, string? directory, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(result);
		string fingerprint = Hash(ContentBytes(result));
		byte[] csv = Csv(result);
		byte[] sidecar = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion, Fingerprint = fingerprint, CsvSha256 = Hash(csv), Content = Content(result) }, IndentedOptions);
		if (sidecar.Length > IntradaySnapshotStore.MaximumBytes || csv.Length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("T1 artifact exceeds size bound");
		string root = ResolveDirectory(directory);
		Directory.CreateDirectory(root);
		string final = Path.Combine(root, PackageName(result.Request.RunId, result.Request.RankingVersion, fingerprint));
		string csvPath = Path.Combine(final, CsvFileName);
		string sidecarPath = Path.Combine(final, SidecarFileName);
		if (Directory.Exists(final)) return Reuse(final, result, fingerprint, csvPath, sidecarPath);
		string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
		bool published = false;
		try
		{
			Directory.CreateDirectory(staging);
			BoundaryHook?.Invoke("sidecar");
			WriteStaged(Path.Combine(staging, SidecarFileName), sidecar);
			token.ThrowIfCancellationRequested();
			BoundaryHook?.Invoke("csv");
			WriteStaged(Path.Combine(staging, CsvFileName), csv);
			token.ThrowIfCancellationRequested();
			var verdict = VerifyPackage(staging, result, false);
			if (!verdict.IsValid) throw new InvalidDataException(string.Join("; ", verdict.Problems));
			BoundaryHook?.Invoke("publish");
			token.ThrowIfCancellationRequested();
			try { Directory.Move(staging, final); published = true; }
			catch (IOException) when (Directory.Exists(final)) { return Reuse(final, result, fingerprint, csvPath, sidecarPath); }
			return new(final, csvPath, sidecarPath, fingerprint, false);
		}
		finally { if (!published && Directory.Exists(staging)) Directory.Delete(staging, true); }
	}

	private static T1RankingArtifactPaths Reuse(string final, T1RankingResult result, string fingerprint, string csvPath, string sidecarPath)
	{
		var verdict = Verify(final, result);
		if (!verdict.IsValid) throw new InvalidDataException("Existing package mismatch: " + string.Join("; ", verdict.Problems));
		return new(final, csvPath, sidecarPath, fingerprint, true);
	}

	private static void WriteStaged(string path, byte[] bytes)
	{
		using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		stream.Write(bytes);
		stream.Flush(true);
	}

	public sealed record T1ArtifactVerdict(bool IsValid, string ContentFingerprint, Guid RunId,
		int ShortlistSize, int ExcludedCount, IReadOnlyList<string> Problems);

	/// <summary>Independent integrity verification; filenames are never trusted instead of content fingerprints.</summary>
	public static T1ArtifactVerdict Verify(string packageDirectory, T1RankingResult? expected = null)
		=> VerifyPackage(packageDirectory, expected, true);

	private static T1ArtifactVerdict VerifyPackage(string packageDirectory, T1RankingResult? expected, bool checkDirectory)
	{
		var problems = new List<string>();
		string fingerprint = "";
		var runId = Guid.Empty;
		int shortlist = 0, excluded = 0;
		try
		{
			byte[] sidecar = ReadComponent(Path.Combine(packageDirectory, SidecarFileName));
			byte[] csv = ReadComponent(Path.Combine(packageDirectory, CsvFileName));
			using var document = JsonDocument.Parse(sidecar);
			var root = document.RootElement;
			if (root.GetProperty("SchemaVersion").GetInt32() != SchemaVersion) problems.Add("unsupported envelope schema");
			fingerprint = root.GetProperty("Fingerprint").GetString() ?? "";
			var content = root.GetProperty("Content");
			if (Hash(JsonSerializer.SerializeToUtf8Bytes(content, CanonicalOptions)) != fingerprint) problems.Add("content fingerprint mismatch");
			string checksum = root.GetProperty("CsvSha256").GetString() ?? "";
			if (checksum.Length != 64 || checksum != Hash(csv)) problems.Add("CSV checksum mismatch");
			var request = content.GetProperty("Request");
			runId = Guid.Parse(request.GetProperty("RunId").GetString() ?? "");
			if (request.GetProperty("RankingModel").GetString() != T1PriceOnlyRanking.Model) problems.Add("model mismatch");
			if (request.GetProperty("RankingVersion").GetString() != T1PriceOnlyRanking.Version) problems.Add("version mismatch");
			if (request.GetProperty("ParameterFingerprint").GetString() != T1PriceOnlyRanking.ParameterFingerprint) problems.Add("parameter fingerprint mismatch");
			if (request.GetProperty("FeatureComputationVersion").GetString() != IntradayFeatureAnalysis.Version) problems.Add("feature version mismatch");
			if (content.GetProperty("RankingProfile").GetString() != T1PriceOnlyRanking.Version) problems.Add("ranking profile mismatch");
			if (content.GetProperty("EvidenceClass").GetString() != T1RankingResult.DiagnosticClass) problems.Add("non-diagnostic evidence class");
			var coverage = content.GetProperty("Coverage");
			int captured = coverage.GetProperty("CapturedCandidates").GetInt32();
			int eligible = coverage.GetProperty("EligibleCandidates").GetInt32();
			int requested = coverage.GetProperty("RequestedShortlistSize").GetInt32();
			int actual = coverage.GetProperty("ActualShortlistSize").GetInt32();
			excluded = coverage.GetProperty("ExcludedCandidates").GetInt32();
			if (captured < 0 || eligible < 0 || excluded < 0 || captured != (long)eligible + excluded) problems.Add("coverage counts inconsistent");
			shortlist = content.GetProperty("Shortlist").GetArrayLength();
			// TopN-omitted eligible candidates are not exclusions.
			if (actual != shortlist || actual != Math.Min(eligible, requested)) problems.Add("shortlist/coverage inconsistent");
			if (excluded != content.GetProperty("Excluded").GetArrayLength()) problems.Add("exclusion count inconsistent");
			if (captured != request.GetProperty("CapturedCandidateCount").GetInt32()) problems.Add("captured request inconsistent");
			if (requested != request.GetProperty("ShortlistTopN").GetInt32()) problems.Add("TopN request inconsistent");
			if (shortlist > requested || requested is < 1 or > 20) problems.Add("shortlist request out of bounds");
			var ranked = new HashSet<string>(StringComparer.Ordinal);
			int index = 0;
			foreach (var candidate in content.GetProperty("Shortlist").EnumerateArray())
			{
				index++;
				if (candidate.GetProperty("T1Rank").GetInt32() != index) problems.Add($"rank sequence broken at position {index}");
				if (!ranked.Add(candidate.GetProperty("Symbol").GetString() ?? "")) problems.Add($"duplicate ranked symbol at position {index}");
			}
			var excludedSymbols = new HashSet<string>(StringComparer.Ordinal);
			foreach (var row in content.GetProperty("Excluded").EnumerateArray())
			{
				string symbol = row.GetProperty("Symbol").GetString() ?? "";
				if (!Enum.TryParse<T1RankingExclusionCode>(row.GetProperty("Code").GetString(), out var code)
					|| !Enum.IsDefined(code) || code == T1RankingExclusionCode.None) problems.Add($"invalid exclusion code for {symbol}");
				if (string.IsNullOrWhiteSpace(row.GetProperty("Reason").GetString())) problems.Add($"missing exclusion reason for {symbol}");
				if (!excludedSymbols.Add(symbol)) problems.Add($"duplicate excluded symbol {symbol}");
				if (ranked.Contains(symbol)) problems.Add($"symbol {symbol} is both ranked and excluded");
			}
			if (checkDirectory && Path.GetFileName(Path.TrimEndingDirectorySeparator(packageDirectory)) != PackageName(runId, request.GetProperty("RankingVersion").GetString() ?? "", fingerprint))
				problems.Add("directory identity mismatch");
			if (expected != null && (Hash(ContentBytes(expected)) != fingerprint || Hash(Csv(expected)) != checksum))
				problems.Add("expected result does not match published package");
		}
		catch (Exception ex) { problems.Add("unreadable package: " + ex.GetType().Name + ": " + ex.Message); }
		return new(problems.Count == 0, fingerprint, runId, shortlist, excluded, problems);
	}

	private static byte[] ReadComponent(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		long length = stream.Length;
		if (length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("component exceeds size bound: " + Path.GetFileName(path));
		byte[] bytes = new byte[(int)length];
		stream.ReadExactly(bytes);
		if (stream.ReadByte() != -1) throw new InvalidDataException("component changed during read");
		return bytes;
	}

	/// <summary>Legacy flat exports can never establish a CSV-to-JSON pairing contract; always unsupported/unverified.</summary>
	public static T1ArtifactVerdict VerifyLegacyFlat(string jsonPath, string csvPath)
	{
		var problems = new List<string>();
		string fingerprint = "";
		var runId = Guid.Empty;
		try
		{
			var root = JsonDocument.Parse(File.ReadAllBytes(jsonPath)).RootElement;
			if (root.GetProperty("SchemaVersion").GetInt32() != SchemaVersion) problems.Add("unsupported envelope schema");
			fingerprint = root.GetProperty("Fingerprint").GetString() ?? "";
			if (Hash(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("Content"), CanonicalOptions)) != fingerprint) problems.Add("content fingerprint mismatch");
			runId = Guid.Parse(root.GetProperty("Content").GetProperty("Request").GetProperty("RunId").GetString() ?? "");
		}
		catch (Exception ex) { problems.Add("unreadable legacy: " + ex.GetType().Name); }
		if (!File.Exists(csvPath)) problems.Add("legacy CSV missing");
		problems.Add("legacy flat export: no pairing contract; unsupported/unverified");
		return new(false, fingerprint, runId, 0, 0, problems);
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
				x.OriginalRank, "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", Time(x.DataTime), "",
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
