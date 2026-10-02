using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Serializes only tests that invoke the Classic scanner's shared timestamp-named output writer.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClassicScannerOutputCollection
{
    public const string Name = "ClassicScannerOutput";
}
