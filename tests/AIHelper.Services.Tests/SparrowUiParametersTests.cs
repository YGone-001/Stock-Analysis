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

    [Fact]
    public void StrategyModeOptions_DisplayChineseNamesAndKeepUnderlyingEnum()
    {
        Assert.Equal("Classic（原始麻雀）", SparrowStrategyMode.Classic.ToDisplayName());
        Assert.Equal("V2（东财增强）", SparrowStrategyMode.V2.ToDisplayName());
        Assert.Equal("Classic + V2 双选", SparrowStrategyMode.Compare.ToDisplayName());

        var optClassic = new SparrowStrategyModeOption(SparrowStrategyMode.Classic);
        var optV2 = new SparrowStrategyModeOption(SparrowStrategyMode.V2);
        var optCompare = new SparrowStrategyModeOption(SparrowStrategyMode.Compare);

        Assert.Equal("Classic（原始麻雀）", optClassic.DisplayName);
        Assert.Equal("Classic（原始麻雀）", optClassic.ToString());
        Assert.Equal(SparrowStrategyMode.Classic, optClassic.Mode);

        Assert.Equal("V2（东财增强）", optV2.DisplayName);
        Assert.Equal("V2（东财增强）", optV2.ToString());
        Assert.Equal(SparrowStrategyMode.V2, optV2.Mode);

        Assert.Equal("Classic + V2 双选", optCompare.DisplayName);
        Assert.Equal("Classic + V2 双选", optCompare.ToString());
        Assert.Equal(SparrowStrategyMode.Compare, optCompare.Mode);
    }

    [Fact]
    public void FindAncestor_LocatesGroupBoxAcrossArbitraryHierarchies()
    {
        RunOnStaThread(() =>
        {
            // Hierarchy 1: GroupBox -> Grid -> WrapPanel -> ChkMacroDef
            var groupBox1 = new System.Windows.Controls.GroupBox();
            var grid1 = new System.Windows.Controls.Grid();
            var wrapPanel1 = new System.Windows.Controls.WrapPanel();
            var chk1 = new System.Windows.Controls.CheckBox();
            wrapPanel1.Children.Add(chk1);
            grid1.Children.Add(wrapPanel1);
            groupBox1.Content = grid1;

            var found1 = SparrowUiTreeHelper.FindAncestor<System.Windows.Controls.GroupBox>(chk1);
            Assert.Same(groupBox1, found1);

            // Hierarchy 2: GroupBox -> StackPanel -> Grid -> ChkMacroDef
            var groupBox2 = new System.Windows.Controls.GroupBox();
            var stackPanel2 = new System.Windows.Controls.StackPanel();
            var grid2 = new System.Windows.Controls.Grid();
            var chk2 = new System.Windows.Controls.CheckBox();
            grid2.Children.Add(chk2);
            stackPanel2.Children.Add(grid2);
            groupBox2.Content = stackPanel2;

            var found2 = SparrowUiTreeHelper.FindAncestor<System.Windows.Controls.GroupBox>(chk2);
            Assert.Same(groupBox2, found2);
        });
    }

    [Fact]
    public void RejectReasons_AreClassifiedWithoutChangingRuleEvaluationMath()
    {
        var parameters = new SparrowClassicScanParameters
        {
            CheckMA60 = true,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.04
        };

        // 1. KlineMissing (too few closes)
        var shortCloses = new List<double> { 10.0, 10.1, 10.2 };
        var res1 = StockData.Sparrow.SparrowClassicRuleEvaluator.Evaluate(shortCloses, 10.2, parameters);
        Assert.False(res1.Passed);
        Assert.Equal(SparrowClassicP3RejectReason.KlineMissing, res1.RejectReason);

        // 2. MaOrder: MA5 < MA10
        // Generate descending sequence
        var descCloses = Enumerable.Range(0, 65).Select(i => 100.0 - i).ToList(); // newest first, 100, 99, 98... wait, 100 is newest so MA5 > MA10
        // If we want MA5 < MA10: newest is low, older is high
        var maOrderFailCloses = Enumerable.Range(0, 65).Select(i => 10.0 + i).ToList(); // 10, 11, 12, ... newest is 10, MA5 ~ 12, MA10 ~ 14.5, so MA5 < MA10
        var res2 = StockData.Sparrow.SparrowClassicRuleEvaluator.Evaluate(maOrderFailCloses, 10.0, parameters);
        Assert.False(res2.Passed);
        Assert.Equal(SparrowClassicP3RejectReason.MaOrder, res2.RejectReason);

        // 3. Ma60: latestPrice <= ma60
        // Create bull order where MA5 > MA10 > MA20, but latestPrice is below MA60
        var bullCloses = Enumerable.Range(0, 65).Select(i => 100.0 - i * 0.1).ToList(); // 100, 99.9, ... MA5 > MA10 > MA20 > MA60
        var res3 = StockData.Sparrow.SparrowClassicRuleEvaluator.Evaluate(bullCloses, 80.0, parameters); // latestPrice = 80 <= ma60 (~96.8)
        Assert.False(res3.Passed);
        Assert.Equal(SparrowClassicP3RejectReason.Ma60, res3.RejectReason);

        // 4. AdhesionHigh: adhesion > 4%
        // Bull order with high spread between MA5 and MA20
        var wideBullCloses = new List<double>();
        for (int i = 0; i < 5; i++) wideBullCloses.Add(20.0); // MA5 = 20
        for (int i = 5; i < 10; i++) wideBullCloses.Add(15.0); // MA10 = 17.5
        for (int i = 10; i < 20; i++) wideBullCloses.Add(12.0); // MA20 = 14.75 -> adhesion = (20-14.75)/14.75 ~ 35% > 4%
        for (int i = 20; i < 65; i++) wideBullCloses.Add(5.0);
        var res4 = StockData.Sparrow.SparrowClassicRuleEvaluator.Evaluate(wideBullCloses, 20.0, parameters);
        Assert.False(res4.Passed);
        Assert.Equal(SparrowClassicP3RejectReason.AdhesionHigh, res4.RejectReason);

        // 5. Passed: tight bull order
        var tightBullCloses = new List<double>();
        for (int i = 0; i < 5; i++) tightBullCloses.Add(10.2); // MA5 = 10.2
        for (int i = 5; i < 10; i++) tightBullCloses.Add(10.1); // MA10 = 10.15
        for (int i = 10; i < 20; i++) tightBullCloses.Add(10.0); // MA20 = 10.075
        for (int i = 20; i < 65; i++) tightBullCloses.Add(9.0); // MA60 ~ 9.35
        var res5 = StockData.Sparrow.SparrowClassicRuleEvaluator.Evaluate(tightBullCloses, 10.2, parameters);
        Assert.True(res5.Passed);
        Assert.Equal(SparrowClassicP3RejectReason.None, res5.RejectReason);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
