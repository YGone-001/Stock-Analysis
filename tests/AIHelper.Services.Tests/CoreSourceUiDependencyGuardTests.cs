using Xunit;
using System.IO;
using System.Linq;

namespace AIHelper.Services.Tests;

public sealed class CoreSourceUiDependencyGuardTests
{
    [Fact]
    public void CoreSource_DoesNotReintroduceWpfUiHelpers()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AIHelper.Core"));
        string source = string.Join("\n", Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualTreeHelper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LogicalTreeHelper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DependencyObject", source, StringComparison.Ordinal);
    }
}
