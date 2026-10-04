using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Observed runtime environment facts of the executing process.</summary>
public sealed record RuntimeEnvironmentSnapshot(
    string FrameworkDescription,
    string RuntimeVersion,
    string RuntimeIdentifier,
    string OSPlatform,
    string OSArchitecture,
    string ProcessArchitecture);

/// <summary>Reads the executing process's runtime environment facts.</summary>
public interface IRuntimeEnvironmentSnapshotReader
{
    RuntimeEnvironmentSnapshot Read();
}

/// <summary>Reads runtime facts from established .NET runtime APIs only. No network, no filesystem discovery.</summary>
public sealed class RuntimeEnvironmentSnapshotReader : IRuntimeEnvironmentSnapshotReader
{
    public RuntimeEnvironmentSnapshot Read() => new(
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString(),
        RuntimeInformation.RuntimeIdentifier,
        ResolvePlatform(),
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString());

    /// <summary>Observed operating system platform family; unknown platforms are reported as Unknown rather than guessed.</summary>
    private static string ResolvePlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "OSX";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) return "FreeBSD";
        return "Unknown";
    }
}

/// <summary>Normalized semantic content of the executing tool's authoritative dependency manifest.</summary>
public sealed record ResearchDependencyManifest(string RuntimeTarget, IReadOnlyList<ResearchResolvedDependency> Dependencies);

/// <summary>Reads the dependency manifest that belongs to the executing research tool.</summary>
public interface IResearchDependencyManifestReader
{
    ResearchDependencyManifest Read();
}

/// <summary>
/// Reads the entry application's own <c>.deps.json</c> and normalizes its semantic dependency graph.
/// <para>
/// The manifest is never discovered by scanning directories. It is resolved from the runtime-provided
/// <c>APP_CONTEXT_DEPS_FILES</c> entry whose file name matches the entry assembly, with the standard sidecar
/// next to the entry assembly as the documented fallback.
/// </para>
/// </summary>
public sealed class DotNetDependencyManifestReader : IResearchDependencyManifestReader
{
    private readonly Func<string> _resolveManifestPath;

    public DotNetDependencyManifestReader(Func<string>? resolveManifestPath = null) =>
        _resolveManifestPath = resolveManifestPath ?? ResolveAuthoritativeManifestPath;

    public ResearchDependencyManifest Read() => Parse(_resolveManifestPath());

    /// <summary>Resolves the authoritative manifest path for the executing entry assembly.</summary>
    public static string ResolveAuthoritativeManifestPath()
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly();
        string entryName = entryAssembly?.GetName().Name
            ?? throw new ResearchExecutionEnvironmentProvenanceException(
                ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestUnavailable,
                "Executing entry assembly is unavailable, so no authoritative dependency manifest can be associated.");

        string expectedFileName = entryName + ".deps.json";
        List<string> matches = new();
        if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string depsFiles)
        {
            foreach (string candidate in depsFiles.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(Path.GetFileName(candidate), expectedFileName, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
            }
        }

        if (matches.Count > 1)
            throw new ResearchExecutionEnvironmentProvenanceException(
                ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestAmbiguous,
                $"Multiple dependency manifests named '{expectedFileName}' were reported for the executing tool.");

        if (matches.Count == 1)
        {
            if (!File.Exists(matches[0]))
                throw new ResearchExecutionEnvironmentProvenanceException(
                    ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestUnavailable,
                    $"Authoritative dependency manifest '{expectedFileName}' was reported but does not exist.");
            return matches[0];
        }

        string sidecar = Path.Combine(AppContext.BaseDirectory, expectedFileName);
        if (File.Exists(sidecar)) return sidecar;

        throw new ResearchExecutionEnvironmentProvenanceException(
            ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestUnavailable,
            $"No authoritative dependency manifest '{expectedFileName}' could be associated with the executing tool.");
    }

    private static ResearchDependencyManifest Parse(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            throw new ResearchExecutionEnvironmentProvenanceException(
                ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestUnavailable, $"Dependency manifest could not be read: {exception.Message}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("Dependency manifest root is not an object.");

            string runtimeTarget = root.TryGetProperty("runtimeTarget", out JsonElement runtimeTargetElement)
                && runtimeTargetElement.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()!
                    : throw Invalid("Dependency manifest has no runtimeTarget name.");

            if (!root.TryGetProperty("libraries", out JsonElement libraries) || libraries.ValueKind != JsonValueKind.Object)
                throw Invalid("Dependency manifest has no libraries section.");

            Dictionary<string, string> libraryTypes = new(StringComparer.Ordinal);
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
                    throw Invalid($"Dependency manifest library '{library.Name}' declares no factual dependency type.");
                libraryTypes[library.Name] = typeElement.GetString()!;
            }

            if (!root.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object
                || !targets.TryGetProperty(runtimeTarget, out JsonElement target) || target.ValueKind != JsonValueKind.Object)
                throw Invalid($"Dependency manifest has no target entry for runtime target '{runtimeTarget}'.");

            List<ResearchResolvedDependency> dependencies = new();
            foreach (JsonProperty entry in target.EnumerateObject())
            {
                (string name, string version) = SplitKey(entry.Name);
                if (!libraryTypes.TryGetValue(entry.Name, out string? type))
                    throw Invalid($"Dependency manifest target entry '{entry.Name}' has no matching library type.");

                List<ResearchResolvedDependencyReference> edges = new();
                if (entry.Value.TryGetProperty("dependencies", out JsonElement declared) && declared.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty edge in declared.EnumerateObject())
                    {
                        if (edge.Value.ValueKind != JsonValueKind.String)
                            throw Invalid($"Dependency edge '{edge.Name}' has no declared version.");
                        edges.Add(new ResearchResolvedDependencyReference(edge.Name, edge.Value.GetString()!));
                    }
                }

                dependencies.Add(new ResearchResolvedDependency(name, version, type, edges));
            }

            return new ResearchDependencyManifest(runtimeTarget, dependencies);
        }
        catch (JsonException exception)
        {
            throw new ResearchExecutionEnvironmentProvenanceException(
                ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestInvalid, $"Dependency manifest JSON is invalid: {exception.Message}");
        }
    }

    private static (string Name, string Version) SplitKey(string key)
    {
        int separator = key.LastIndexOf('/');
        if (separator <= 0 || separator == key.Length - 1)
            throw Invalid($"Dependency manifest key '{key}' is not in 'name/version' form.");
        return (key[..separator], key[(separator + 1)..]);
    }

    private static ResearchExecutionEnvironmentProvenanceException Invalid(string message) =>
        new(ResearchExecutionEnvironmentProvenanceReasonCodes.DependencyManifestInvalid, message);
}

/// <summary>Captures authoritative execution-environment provenance. Local only, read only, offline and deterministic.</summary>
public interface IResearchExecutionEnvironmentProvenanceProvider
{
    ResearchExecutionEnvironmentProvenance Capture();
}

/// <summary>Composes the observed runtime environment with the normalized dependency graph identity.</summary>
public sealed class ResearchExecutionEnvironmentProvenanceProvider : IResearchExecutionEnvironmentProvenanceProvider
{
    private readonly IRuntimeEnvironmentSnapshotReader _environmentReader;
    private readonly IResearchDependencyManifestReader _dependencyManifestReader;

    public ResearchExecutionEnvironmentProvenanceProvider(
        IRuntimeEnvironmentSnapshotReader? environmentReader = null,
        IResearchDependencyManifestReader? dependencyManifestReader = null)
    {
        _environmentReader = environmentReader ?? new RuntimeEnvironmentSnapshotReader();
        _dependencyManifestReader = dependencyManifestReader ?? new DotNetDependencyManifestReader();
    }

    public ResearchExecutionEnvironmentProvenance Capture()
    {
        RuntimeEnvironmentSnapshot environment = _environmentReader.Read();
        ResearchDependencyManifest manifest = _dependencyManifestReader.Read();

        return ResearchExecutionEnvironmentProvenance.Create(
            ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion,
            environment.FrameworkDescription,
            environment.RuntimeVersion,
            environment.RuntimeIdentifier,
            environment.OSPlatform,
            environment.OSArchitecture,
            environment.ProcessArchitecture,
            manifest.RuntimeTarget,
            manifest.Dependencies);
    }
}
