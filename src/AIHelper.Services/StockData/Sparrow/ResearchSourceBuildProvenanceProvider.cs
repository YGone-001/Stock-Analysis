using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Observed local Git source identity. Only authoritative (clean) observations are representable.</summary>
public sealed record GitSourceSnapshot(string CommitSha, string TreeSha);

/// <summary>Reads the authoritative local Git source state. Implementations must not contact any remote.</summary>
public interface IGitSourceSnapshotReader
{
    /// <summary>Returns the HEAD commit/tree for a clean worktree; throws <see cref="ResearchSourceBuildProvenanceException"/> otherwise.</summary>
    GitSourceSnapshot ReadAuthoritative();
}

/// <summary>Observed build identity of the executing research modules.</summary>
public sealed record ResearchBuildIdentity(
    string BuildConfiguration,
    string TargetFramework,
    string HistoricalDataToolModuleVersionId,
    string ServicesModuleVersionId,
    string CoreModuleVersionId);

/// <summary>Reads build identity from the executing assemblies' runtime metadata.</summary>
public interface IResearchBuildIdentityReader
{
    ResearchBuildIdentity Read();
}

/// <summary>Captures authoritative source/build provenance for a research execution. No network and no persistence side effects.</summary>
public interface IResearchSourceBuildProvenanceProvider
{
    ResearchSourceBuildProvenance Capture();
}

/// <summary>
/// Reads Git state with a narrow, read-only command set. It never fetches, pulls, commits, checks out, or resets.
/// </summary>
public sealed class LocalGitSourceSnapshotReader : IGitSourceSnapshotReader
{
    private readonly string? _workingDirectory;

    public LocalGitSourceSnapshotReader(string? workingDirectory = null) => _workingDirectory = workingDirectory;

    public GitSourceSnapshot ReadAuthoritative()
    {
        string? root = Run("rev-parse", "--show-toplevel");
        if (string.IsNullOrWhiteSpace(root))
            throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "Local Git repository root could not be resolved.");

        string? commit = Run("rev-parse", "HEAD");
        string? tree = Run("rev-parse", "HEAD^{tree}");
        if (string.IsNullOrWhiteSpace(commit) || string.IsNullOrWhiteSpace(tree))
            throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "Local Git HEAD or root tree identity could not be resolved.");

        string? status = Run("status", "--porcelain=v1", "--untracked-files=all");
        if (status is null)
            throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "Local Git worktree status could not be read.");
        if (status.Length != 0)
            throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceWorktreeNotClean,
                $"{ResearchSourceBuildProvenanceReasonCodes.SourceWorktreeNotClean}: authoritative source/build provenance requires a clean worktree, but local changes were detected.");

        return new GitSourceSnapshot(commit, tree);
    }

    private string? Run(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(_workingDirectory)) startInfo.WorkingDirectory = _workingDirectory;
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return null;
            string standardOutput = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? standardOutput.Trim() : null;
        }
        catch (Win32Exception)
        {
            // Git executable is not available on this machine.
            return null;
        }
    }
}

/// <summary>Reads build identity from the executing assemblies. No assembly file paths are used or stored.</summary>
public sealed class RuntimeBuildIdentityReader : IResearchBuildIdentityReader
{
    public ResearchBuildIdentity Read()
    {
        Assembly? tool = Assembly.GetEntryAssembly();
        if (tool is null)
            throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "Executing entry assembly is unavailable, so build identity cannot be observed.");

        Assembly services = typeof(SparrowResearchReexecutionValidator).Assembly;
        Assembly core = typeof(ResearchSourceBuildProvenance).Assembly;

        string configuration = tool.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "AssemblyConfigurationAttribute is unavailable on the executing entry assembly.");
        string targetFramework = tool.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
            ?? throw new ResearchSourceBuildProvenanceException(ResearchSourceBuildProvenanceReasonCodes.SourceProvenanceUnavailable,
                "TargetFrameworkAttribute is unavailable on the executing entry assembly.");

        return new ResearchBuildIdentity(
            configuration,
            targetFramework,
            tool.ManifestModule.ModuleVersionId.ToString("D"),
            services.ManifestModule.ModuleVersionId.ToString("D"),
            core.ManifestModule.ModuleVersionId.ToString("D"));
    }
}

/// <summary>Composes observed local Git source state with observed executing build identity into immutable provenance.</summary>
public sealed class ResearchSourceBuildProvenanceProvider : IResearchSourceBuildProvenanceProvider
{
    private readonly IGitSourceSnapshotReader _gitSource;
    private readonly IResearchBuildIdentityReader _buildIdentity;

    public ResearchSourceBuildProvenanceProvider(IGitSourceSnapshotReader? gitSource = null, IResearchBuildIdentityReader? buildIdentity = null)
    {
        _gitSource = gitSource ?? new LocalGitSourceSnapshotReader();
        _buildIdentity = buildIdentity ?? new RuntimeBuildIdentityReader();
    }

    public ResearchSourceBuildProvenance Capture()
    {
        GitSourceSnapshot source = _gitSource.ReadAuthoritative();
        ResearchBuildIdentity build = _buildIdentity.Read();

        return new ResearchSourceBuildProvenance(
            ResearchSourceBuildProvenance.CurrentProvenanceVersion,
            source.CommitSha,
            source.TreeSha,
            ResearchSourceBuildProvenance.CleanSourceState,
            build.BuildConfiguration,
            build.TargetFramework,
            build.HistoricalDataToolModuleVersionId,
            build.ServicesModuleVersionId,
            build.CoreModuleVersionId);
    }
}
