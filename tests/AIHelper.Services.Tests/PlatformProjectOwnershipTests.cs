using System.Xml.Linq;
using System.IO;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class PlatformProjectOwnershipTests
{
    [Fact]
    public void CoreProject_DoesNotEnableWpfOrUseWpfNamespaces()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, "src", "AIHelper.Core", "AIHelper.Core.csproj"));
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "UseWPF" && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        string coreDirectory = Path.Combine(root, "src", "AIHelper.Core");
        foreach (string sourceFile in Directory.EnumerateFiles(coreDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InfrastructureProject_ExplicitlyOwnsProtectedDataDependency()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, "src", "AIHelper.Infrastructure", "AIHelper.Infrastructure.csproj"));
        XElement? protectedData = project.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "PackageReference"
            && string.Equals(element.Attribute("Include")?.Value, "System.Security.Cryptography.ProtectedData", StringComparison.Ordinal));

        Assert.NotNull(protectedData);
        Assert.StartsWith("10.", protectedData!.Attribute("Version")?.Value, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AIHelper.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("AIHelper repository root was not found.");
    }
}
