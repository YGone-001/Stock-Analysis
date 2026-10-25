namespace AIHelper.Core.Sparrow;

/// <summary>Stable strategy identities for evidence and future replay; not assembly versions.</summary>
/// <remarks>
/// Classic is a frozen control group. To evolve Classic rules, publish a new version constant
/// (e.g. <c>ClassicV2 = "classic-v2"</c>) and add a matching evaluator/snapshot, instead of changing
/// the classic-v1 rules in place. The same version-discipline applies to V2.
/// </remarks>
public static class SparrowStrategyVersions
{
    public const string Classic = "classic-v1";
    public const string V2 = "v2";
    public const string V3 = "v3";
    public const string Legacy = "legacy";
}
