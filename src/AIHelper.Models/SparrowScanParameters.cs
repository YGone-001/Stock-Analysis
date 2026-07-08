namespace AIHelper.Models;

public class SparrowScanParameters
{
    public bool MacroDef { get; set; }
    public double MinRise { get; set; }
    public double MaxRise { get; set; }
    public double VolRatio { get; set; }
    public double MinAmount { get; set; }
    public bool CheckMA60 { get; set; }
    public double MinAdhesion { get; set; }
    public double MaxAdhesion { get; set; }
    public double MinTurnover { get; set; }
    public double MaxTurnover { get; set; }
    public double MomentumThreshold { get; set; }
    public bool CheckAlpha { get; set; }
    public int MaxConcurrency { get; set; }
    public bool UseCache { get; set; }
}
