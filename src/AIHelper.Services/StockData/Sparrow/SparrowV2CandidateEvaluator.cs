using AIHelper.Models;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Stable, request-free V2 rule evidence. Stage and reason-code values are stable IDs.</summary>
public sealed record SparrowV2CandidateEvaluation(
    string Code,
    SparrowRuleComparison P2,
    SparrowRuleComparison P3,
    string StrategyVersion = SparrowStrategyVersions.V2)
{
    public bool Passed => P2.Passed && P3.Passed;
}

/// <summary>Pure candidate evaluator; acquisition, cache provenance, and transport parsing stay outside.</summary>
public static class SparrowV2CandidateEvaluator
{
    public static SparrowRuleComparison EvaluateQuote(SparrowQuoteData quote, SparrowScanParameters parameters) =>
        SparrowComparisonRuleEvaluators.EvaluateV2Quote(quote, parameters);

    public static SparrowV2CandidateEvaluation Evaluate(
        string code,
        SparrowQuoteData quote,
        SparrowKlineSnapshot? snapshot,
        double shIndexPctChg,
        SparrowScanParameters parameters)
    {
        SparrowRuleComparison p2 = EvaluateQuote(quote, parameters);
        SparrowTechnicalEvaluation technical = p2.Passed
            ? SparrowV2RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters)
            : new SparrowTechnicalEvaluation(SparrowRuleComparison.NotRun("P3", "P2 did not pass"));
        return new SparrowV2CandidateEvaluation(code, p2, technical.Rule);
    }
}
