using AIHelper.Models;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowUiParametersTests
{
    [Fact]
    public void RecommendedDefaults_AreCentralizedAndDefaultModeIsClassic()
    {
        var state = new SparrowParameterUiState();

        Assert.Equal(SparrowStrategyMode.Classic, state.StrategyMode);
        Assert.Equal(1.0, state.ClassicParameters.MinRise);
        Assert.Equal(5.0, state.ClassicParameters.MaxRise);
        Assert.Equal(5000.0, state.ClassicParameters.MinAmount);
        Assert.Equal(1.10, state.ClassicParameters.VolRatio);
        Assert.Equal(4.0, state.ClassicParameters.MaxAdhesion);
        Assert.Equal(3.0, state.V2Parameters.MinTurnover);
        Assert.Equal(30.0, state.V2Parameters.MaxTurnover);
        Assert.Equal(0.0, state.V2Parameters.MomentumThreshold);
        Assert.True(state.V2Parameters.CheckAlpha);
        Assert.Equal(8, state.SystemSettings.MaxConcurrency);
        Assert.True(state.SystemSettings.UseCache);
    }

    [Fact]
    public void ClassicMapping_ConvertsUiUnitsAndCannotCarryV2Enhancements()
    {
        SparrowClassicUiParameters ui = SparrowParameterDefaults.CreateClassic();
        SparrowSystemSettings system = SparrowParameterDefaults.CreateSystemSettings();

        SparrowClassicScanParameters mapped = SparrowParameterMapper.ToClassic(ui, system);

        Assert.Equal(1.0, mapped.MinRise);
        Assert.Equal(5.0, mapped.MaxRise);
        Assert.Equal(50_000_000.0, mapped.MinAmount);
        Assert.Equal(1.10, mapped.VolRatio);
        Assert.Equal(0.04, mapped.MaxAdhesion, 10);
        Assert.Null(typeof(SparrowClassicScanParameters).GetProperty("MinTurnover"));
        Assert.Null(typeof(SparrowClassicScanParameters).GetProperty("MomentumThreshold"));
        Assert.Null(typeof(SparrowClassicScanParameters).GetProperty("CheckAlpha"));
    }

    [Fact]
    public void V2Mapping_PreservesEnhancementParameters()
    {
        SparrowV2UiParameters ui = SparrowParameterDefaults.CreateV2();
        ui.MinTurnover = 4.5;
        ui.MaxTurnover = 22.0;
        ui.MomentumThreshold = 0.012;
        ui.CheckAlpha = false;

        SparrowScanParameters mapped = SparrowParameterMapper.ToV2(
            ui, SparrowParameterDefaults.CreateSystemSettings());

        Assert.Equal(4.5, mapped.MinTurnover);
        Assert.Equal(22.0, mapped.MaxTurnover);
        Assert.Equal(0.012, mapped.MomentumThreshold);
        Assert.False(mapped.CheckAlpha);
    }

    [Theory]
    [InlineData(SparrowStrategyMode.Classic, false)]
    [InlineData(SparrowStrategyMode.V2, true)]
    [InlineData(SparrowStrategyMode.Compare, true)]
    public void Visibility_IsStrategyAware(SparrowStrategyMode mode, bool showV2)
    {
        var state = new SparrowParameterUiState { StrategyMode = mode };

        Assert.True(state.ShowBaseParameters);
        Assert.Equal(showV2, state.ShowV2Parameters);
        Assert.True(state.ShowSystemParameters);
    }

    [Fact]
    public void ClassicAndV2ParameterState_IsIsolatedAcrossModeSwitches()
    {
        var state = new SparrowParameterUiState();
        state.ClassicParameters.MinRise = 1.5;
        state.ClassicParameters.VolRatio = 1.2;

        state.StrategyMode = SparrowStrategyMode.V2;
        state.V2Parameters.MinRise = 0.8;
        state.StrategyMode = SparrowStrategyMode.Classic;

        Assert.Same(state.ClassicParameters, state.ActiveParameters);
        Assert.Equal(1.5, state.ClassicParameters.MinRise);
        Assert.Equal(1.2, state.ClassicParameters.VolRatio);
        Assert.Equal(0.8, state.V2Parameters.MinRise);
    }

    [Fact]
    public void ResetRecommendedDefaults_ResetsStrategyButNotSystemSettings()
    {
        var state = new SparrowParameterUiState();
        state.ClassicParameters.MinRise = 3;
        state.ClassicParameters.VolRatio = 2;
        state.ClassicParameters.MaxAdhesion = 10;
        state.SystemSettings.MaxConcurrency = 3;
        state.SystemSettings.UseCache = false;

        state.ResetRecommendedDefaults();

        Assert.Equal(1.0, state.ClassicParameters.MinRise);
        Assert.Equal(1.10, state.ClassicParameters.VolRatio);
        Assert.Equal(4.0, state.ClassicParameters.MaxAdhesion);
        Assert.Equal(3, state.SystemSettings.MaxConcurrency);
        Assert.False(state.SystemSettings.UseCache);
        Assert.True(state.ClassicParameters.IsRecommended);
    }

    [Fact]
    public void CompareMapping_UsesOneCommonBaseAndOneSystemConfiguration()
    {
        var state = new SparrowParameterUiState { StrategyMode = SparrowStrategyMode.Compare };
        state.ClassicParameters.MinRise = 1.7;
        state.V2Parameters.MinRise = 0.8;
        state.V2Parameters.MinTurnover = 4;
        state.SystemSettings.MaxConcurrency = 5;
        state.SystemSettings.UseCache = false;

        (SparrowClassicScanParameters classic, SparrowScanParameters v2) =
            SparrowParameterMapper.ToComparison(
                state.ClassicParameters, state.V2Parameters, state.SystemSettings);

        Assert.Equal(1.7, classic.MinRise);
        Assert.Equal(1.7, v2.MinRise);
        Assert.Equal(4, v2.MinTurnover);
        Assert.Equal(5, classic.MaxConcurrency);
        Assert.Equal(5, v2.MaxConcurrency);
        Assert.False(classic.UseCache);
        Assert.False(v2.UseCache);
        Assert.Equal(0.8, state.V2Parameters.MinRise);
    }

    [Fact]
    public void CompareReset_PreservesV2BaseStateButResetsV2Enhancements()
    {
        var state = new SparrowParameterUiState { StrategyMode = SparrowStrategyMode.Compare };
        state.ClassicParameters.MinRise = 2.5;
        state.V2Parameters.MinRise = 0.8;
        state.V2Parameters.MinTurnover = 8;
        state.V2Parameters.CheckAlpha = false;

        state.ResetRecommendedDefaults();

        Assert.Equal(1.0, state.ClassicParameters.MinRise);
        Assert.Equal(0.8, state.V2Parameters.MinRise);
        Assert.Equal(3.0, state.V2Parameters.MinTurnover);
        Assert.True(state.V2Parameters.CheckAlpha);
        Assert.Contains("推荐默认", state.CurrentPresetName);
    }

    [Fact]
    public void PresetName_ChangesToCustomAndBackToRecommended()
    {
        var state = new SparrowParameterUiState();
        Assert.Contains("推荐默认", state.CurrentPresetName);

        state.ClassicParameters.MinRise = 2;
        Assert.Contains("自定义", state.CurrentPresetName);

        state.ResetRecommendedDefaults();
        Assert.Contains("推荐默认", state.CurrentPresetName);
    }

    [Theory]
    [InlineData("rise")]
    [InlineData("adhesion")]
    [InlineData("turnover")]
    [InlineData("ratio")]
    [InlineData("amount")]
    [InlineData("concurrency")]
    public void Validation_RejectsInvalidRangesAndSystemValues(string invalidField)
    {
        var state = new SparrowParameterUiState { StrategyMode = SparrowStrategyMode.Compare };
        switch (invalidField)
        {
            case "rise": state.ClassicParameters.MinRise = state.ClassicParameters.MaxRise + 1; break;
            case "adhesion": state.ClassicParameters.MinAdhesion = state.ClassicParameters.MaxAdhesion + 1; break;
            case "turnover": state.V2Parameters.MinTurnover = state.V2Parameters.MaxTurnover + 1; break;
            case "ratio": state.ClassicParameters.VolRatio = 0; break;
            case "amount": state.ClassicParameters.MinAmount = -1; break;
            case "concurrency": state.SystemSettings.MaxConcurrency = 0; break;
        }

        SparrowParameterValidationResult result = SparrowParameterValidator.Validate(
            state.StrategyMode, state.ClassicParameters, state.V2Parameters, state.SystemSettings);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void ClassicValidation_IgnoresHiddenV2Range()
    {
        var state = new SparrowParameterUiState { StrategyMode = SparrowStrategyMode.Classic };
        state.V2Parameters.MinTurnover = 40;
        state.V2Parameters.MaxTurnover = 10;

        SparrowParameterValidationResult result = SparrowParameterValidator.Validate(
            state.StrategyMode, state.ClassicParameters, state.V2Parameters, state.SystemSettings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(SparrowStrategyMode.Classic, "麻雀 Classic", "V2 增强条件", "")]
    [InlineData(SparrowStrategyMode.V2, "基础麻雀条件", "V2 增强条件", "")]
    [InlineData(SparrowStrategyMode.Compare, "Classic / V2 共同条件", "仅 V2 生效", "Compare 使用同一行情快照；V2 增强条件不会应用到 Classic。")]
    public void HeadersAndHints_MatchStrategyMode(
        SparrowStrategyMode mode,
        string expectedBaseHeader,
        string expectedV2Header,
        string expectedComparisonHint)
    {
        var state = new SparrowParameterUiState { StrategyMode = mode };

        Assert.Equal(expectedBaseHeader, state.BaseParametersHeader);
        Assert.Equal(expectedV2Header, state.V2ParametersHeader);
        Assert.Equal(expectedComparisonHint, state.ComparisonHint);
    }
}
