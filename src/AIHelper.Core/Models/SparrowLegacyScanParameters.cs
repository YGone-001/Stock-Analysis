using System;

#pragma warning disable CS8618
#pragma warning disable CS8618
namespace AIHelper.Models;

public class SparrowLegacyScanParameters
{
    public bool MacroDef { get; set; } = true;
    public double MinRise { get; set; } = 0;
    public double MaxRise { get; set; } = 9.9;
    public double VolRatio { get; set; } = 1.0;
    public double MinAmount { get; set; } = 5000.0; // Internally * 10000
    public bool CheckMA60 { get; set; } = true;
    public double MinAdhesion { get; set; } = 0.0;
    public double MaxAdhesion { get; set; } = 15.0;
    public int MaxConcurrency { get; set; } = 8;
    public bool UseCache { get; set; } = true;
}

public class SparrowLegacyScanReport
{
    public string LogMessage { get; set; } = null!;
    public bool IsHighlight { get; set; }
    public double? ProgressValue { get; set; }
    public double? ProgressMax { get; set; }
    public int? P2Survivors { get; set; }
    public int? P3Winners { get; set; }
}
