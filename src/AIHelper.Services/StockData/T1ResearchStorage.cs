using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelper.Services.StockData;

/// <summary>Local immutable research I/O only. Never discovers credentials or enumerates an unspecified root.</summary>
internal static class T1ResearchStorage
{
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    internal static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string Fingerprint<T>(T value) => Hash(Bytes(value));
    internal static string PathChecked(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Split('\\', '/').Contains("..")) throw new InvalidDataException("RESEARCH_PATH_TRAVERSAL");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (string? p = full; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RESEARCH_LINK_UNSUPPORTED");
        return full;
    }
    internal static byte[] Read(string path)
    {
        using var file = new FileStream(PathChecked(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("RESEARCH_FILE_BOUND");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("RESEARCH_FILE_CHANGED");
        return bytes;
    }
    internal static T Load<T>(string path) => JsonSerializer.Deserialize<T>(Read(path), Json) ?? throw new InvalidDataException("RESEARCH_EMPTY_JSON");
    internal static void Write(string path, byte[] bytes)
    {
        if (bytes.Length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("RESEARCH_FILE_BOUND");
        using var file = new FileStream(PathChecked(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
    internal static (int Code, string Output) Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new InvalidDataException("GIT_UNAVAILABLE");
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10000)) { p.Kill(true); throw new InvalidDataException("GIT_TIMEOUT"); }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        return (p.ExitCode, output.Result.Trim());
    }
    internal static string Root(string path, bool requireIgnored = false)
    {
        string full = PathChecked(path), ancestor = full;
        while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor) ?? throw new InvalidDataException("RESEARCH_ROOT_INVALID");
        var git = Git(ancestor, "rev-parse", "--show-toplevel");
        if (git.Code == 0)
        {
            string relative = Path.GetRelativePath(git.Output, full).Replace('\\', '/');
            if (relative == "." || Git(ancestor, "check-ignore", "--no-index", "-q", "--", relative + "/.research-probe").Code != 0)
                throw new InvalidDataException("RESEARCH_ROOT_MUST_BE_IGNORED");
        }
        else if (requireIgnored || git.Code != 128) throw new InvalidDataException("RESEARCH_ROOT_MUST_BE_IGNORED");
        return full;
    }
    internal static void RemoveStaging(string root, string staging)
    {
        string full = PathChecked(staging);
        if (Path.GetDirectoryName(full) != root || !Path.GetFileName(full).StartsWith(".t1-staging-", StringComparison.Ordinal))
            throw new InvalidDataException("STAGING_CLEANUP_BOUNDARY");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
