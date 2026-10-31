using System.Text.Json;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    /// <summary>Explicitly selected offline CLI path; returns 2 for expected evidence blockers and 1 for invalid input.</summary>
    public static int RunAssociationCommand(IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error)
    {
        if (options.ContainsKey("help"))
        {
            output.WriteLine("--associate-t1 [--snapshot FILE] [--ranking-package DIR] [--calendar FILE] [--outcomes FILE] [--test-fixture] [--output IGNORED_DIR]");
            output.WriteLine("Offline only. Calendar: T1CalendarEvidence JSON. Outcomes: T1AssociationOutcomeInput JSON. Enum names are strings.");
            output.WriteLine("Optional --outcomes includes per-symbol DecisionBases. Metadata cannot authenticate PIT. Exit 2=BLOCKED, 1=invalid input, 0=help.");
            output.WriteLine("Missing paths report NO_AUTHORITATIVE_SAMPLE/CALENDAR_NOT_VERIFIED/OUTCOME_SOURCE_NOT_VERIFIED. No data is fetched.");
            return 0;
        }
        try
        {
            var allowed = new HashSet<string>(new[] { "associate-t1", "snapshot", "ranking-package", "calendar", "outcomes", "test-fixture", "output" }, StringComparer.OrdinalIgnoreCase);
            if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("UNKNOWN_ASSOCIATION_OPTION");
            string? Value(string key) => options.TryGetValue(key, out var value) ? value : null;
            T? Read<T>(string key) where T : class
            {
                string? path = Value(key);
                if (Missing(path)) return null;
                try
                {
                    using var file = OpenBounded(path!);
                    return JsonSerializer.Deserialize<T>(ReadBounded(file), Json) ?? throw new InvalidDataException("EMPTY_INPUT");
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
            }
            var calendar = Read<T1CalendarEvidence>("calendar");
            var outcomes = Read<T1AssociationOutcomeInput>("outcomes");
            var request = new T1EvidenceRequest(Value("snapshot"), Value("ranking-package"), calendar, outcomes?.Outcomes,
                Origin: options.TryGetValue("test-fixture", out var fixture) && bool.Parse(fixture) ? T1EvidenceOrigin.TEST_FIXTURE : T1EvidenceOrigin.UNVERIFIED);
            var artifact = Associate(new[] { new T1AssociationInput(request, outcomes?.DecisionBases) });
            output.WriteLine("SAMPLE=" + artifact.Content.Coverage.SourceCoverage.SampleState);
            if (artifact.Content.Coverage.SourceCoverage.VerifiedSnapshots == 0) output.WriteLine("NO_AUTHORITATIVE_SAMPLE: supply the original retained schema-1 snapshot.");
            if (artifact.Content.Coverage.SourceCoverage.MissingCalendarCoverage > 0) output.WriteLine("CALENDAR_NOT_VERIFIED: supply a complete source-declared exchange calendar with coverage evidence.");
            if (artifact.Content.Coverage.UsableDeclaredAssociations == 0) output.WriteLine("OUTCOME_SOURCE_NOT_VERIFIED: supply exact target-date independent envelopes and compatible per-symbol basis metadata.");
            output.WriteLine("READINESS=" + artifact.Content.Coverage.SourceCoverage.Overall);
            output.WriteLine("ASSOCIATION_FINGERPRINT=" + artifact.Fingerprint);
            output.WriteLine("CANDIDATES=" + artifact.Content.Coverage.CandidateAssociations);
            output.WriteLine("AUTHENTICATION=NOT_VERIFIED; no execution or performance claims.");
            if (Value("output") is { } directory)
            {
                var protectedPaths = new[] { Value("snapshot"), Value("ranking-package"), Value("calendar"), Value("outcomes") }.Where(p => !Missing(p)).Select(p => p!);
                var publication = PublishAssociation(artifact, directory, protectedInputs: protectedPaths);
                output.WriteLine("PUBLICATION=" + (publication.ReusedExisting ? "REUSED" : "PUBLISHED"));
            }
            return artifact.Content.Coverage.SourceCoverage.Overall == T1ReadinessStatus.FAIL ? 1 : 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { error.WriteLine("ASSOCIATION_INPUT_OR_PUBLICATION_INVALID: check bounded evidence formats and a Git-ignored output directory; no source files were repaired."); return 1; }
    }
}
