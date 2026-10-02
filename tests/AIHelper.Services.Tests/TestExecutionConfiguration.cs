using System.IO;

namespace AIHelper.Services.Tests;

/// <summary>Resolves the configuration already used to build the current test assembly.</summary>
internal static class TestExecutionConfiguration
{
    public static string Current()
    {
        string[] segments = AppContext.BaseDirectory.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "Release", StringComparison.OrdinalIgnoreCase))) return "Release";
        if (segments.Any(segment => string.Equals(segment, "Debug", StringComparison.OrdinalIgnoreCase))) return "Debug";
        throw new InvalidOperationException($"Unable to determine test configuration from '{AppContext.BaseDirectory}'.");
    }
}
