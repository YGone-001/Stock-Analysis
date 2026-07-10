using System;

#pragma warning disable CS8618
#pragma warning disable CS8618
namespace AIHelper.Models;

public class TurtleScanParameters
{
    public bool MacroDef { get; set; } = true;
    public int N1 { get; set; } = 20;
    public int N2 { get; set; } = 60;
    public double MinTurnover { get; set; } = 0.0;
    public double MaxTurnover { get; set; } = 100.0;
    public double MinAmount { get; set; } = 50000000.0; // Internally represented as value * 10000
    public int MaxConcurrency { get; set; } = 8;
    public bool UseCache { get; set; } = true;
}

public class TurtleScanReport
{
    public string LogMessage { get; set; } = null!;
    public bool IsHighlight { get; set; }
    public double? ProgressValue { get; set; }
    public double? ProgressMax { get; set; }
}
