using System.Diagnostics;
using System.Text.Json;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    internal static Action<string>? AssociationBoundaryHook;
    public const string AssociationFileName = "association.json";

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidDataException("GIT_UNAVAILABLE");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); throw new InvalidDataException("GIT_CHECK_TIMEOUT"); }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidDataException("OUTPUT_MUST_BE_GIT_IGNORED");
        return stdout.Result.Trim();
    }

    private static string IgnoredOutput(string directory, IEnumerable<string>? protectedInputs)
    {
        string output = Path.GetFullPath(directory);
        foreach (string path in protectedInputs ?? Array.Empty<string>())
        {
            string input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (output.Equals(input, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OUTPUT_OVERLAPS_INPUT");
        }
        string? ancestor = output;
        while (ancestor is not null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
        if (ancestor is null) throw new InvalidDataException("OUTPUT_DIRECTORY_INVALID");
        for (string? check = ancestor; check is not null; check = Path.GetDirectoryName(check))
            if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("OUTPUT_LINK_UNSUPPORTED");
        string root = Git(ancestor, "rev-parse", "--show-toplevel");
        string relative = Path.GetRelativePath(root, output).Replace('\\', '/');
        if (relative == "." || relative.StartsWith("../", StringComparison.Ordinal)) throw new InvalidDataException("OUTPUT_DIRECTORY_INVALID");
        Git(root, "check-ignore", "--no-index", "-q", "--", relative + "/.association-probe");
        return output;
    }

    public static bool VerifyAssociationPackage(string directory)
    {
        try
        {
            using var file = OpenBounded(Path.Combine(directory, AssociationFileName));
            var artifact = JsonSerializer.Deserialize<T1AssociationArtifact>(ReadBounded(file), Json);
            return artifact is not null && VerifyAssociation(artifact)
                && Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) == "t1-association-" + artifact.Fingerprint;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    public static T1AssociationPublication PublishAssociation(T1AssociationArtifact artifact, string outputDirectory,
        CancellationToken token = default, IEnumerable<string>? protectedInputs = null)
    {
        if (!VerifyAssociation(artifact)) throw new InvalidDataException("ASSOCIATION_INTEGRITY_INVALID");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, Json);
        if (bytes.Length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("ASSOCIATION_SIZE_BOUND");
        token.ThrowIfCancellationRequested();
        string root = IgnoredOutput(outputDirectory, protectedInputs);
        Directory.CreateDirectory(root);
        string final = Path.Combine(root, "t1-association-" + artifact.Fingerprint);
        T1AssociationPublication Reuse()
        {
            if (!VerifyAssociationPackage(final) || (File.GetAttributes(final) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("EXISTING_ASSOCIATION_MISMATCH");
            using var existing = OpenBounded(Path.Combine(final, AssociationFileName));
            if (!ReadBounded(existing).SequenceEqual(bytes)) throw new InvalidDataException("EXISTING_ASSOCIATION_MISMATCH");
            return new(artifact.Fingerprint, final, true);
        }
        if (Directory.Exists(final)) return Reuse();
        string staging = Path.Combine(root, ".association-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using (var stream = new FileStream(Path.Combine(staging, AssociationFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            AssociationBoundaryHook?.Invoke("written");
            token.ThrowIfCancellationRequested();
            using (var file = OpenBounded(Path.Combine(staging, AssociationFileName)))
            {
                var staged = JsonSerializer.Deserialize<T1AssociationArtifact>(ReadBounded(file), Json);
                if (staged is null || !VerifyAssociation(staged) || staged.Fingerprint != artifact.Fingerprint)
                    throw new InvalidDataException("STAGED_ASSOCIATION_INVALID");
            }
            AssociationBoundaryHook?.Invoke("publish");
            token.ThrowIfCancellationRequested();
            try { Directory.Move(staging, final); }
            catch (IOException) when (Directory.Exists(final)) { return Reuse(); }
            return new(artifact.Fingerprint, final, false);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
