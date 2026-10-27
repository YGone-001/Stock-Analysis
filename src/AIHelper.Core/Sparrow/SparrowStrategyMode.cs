namespace AIHelper.Models;

public enum SparrowStrategyMode
{
    Classic,
    V2,
    Compare,
    V3,
    V4,
    V5,
    V6,
    /// <summary>Orchestration-only mode: same-snapshot comparison of two selectable V2-family versions; never replayable.</summary>
    PairCompare
}
