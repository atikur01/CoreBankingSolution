namespace CoreBanking.Tests.Unit;

public class ParameterRulesTests
{
    [Theory]
    [InlineData("VatRate", "15")]
    [InlineData("VatRate", "0")]
    [InlineData("VatRate", "100")]
    [InlineData("PoCommissionPct", "0.15")]
    [InlineData("InterestDayBasis", "365")]
    [InlineData("FinancialYearStartMonth", "7")]
    [InlineData("Level2Amount", "500000")]
    [InlineData("WeeklyHolidays", "Friday,Saturday")]
    [InlineData("WeeklyHolidays", "")]
    [InlineData("BaseCurrency", "BDT")]
    [InlineData("BankName", "Core Bank PLC")]
    [InlineData("CashGl", "1100")]
    [InlineData("PasswordExpiryDays", "0")]
    [InlineData("SomeCustomKey", "anything")]
    public void Valid_values_are_accepted(string key, string value) => Assert.Null(ParameterRules.Validate(key, value));

    [Theory]
    [InlineData("VatRate", "abc")]
    [InlineData("VatRate", "-1")]
    [InlineData("VatRate", "101")]
    [InlineData("SourceTaxRate", "")]
    [InlineData("InterestDayBasis", "0")]
    [InlineData("InterestDayBasis", "400")]
    [InlineData("FinancialYearStartMonth", "13")]
    [InlineData("FinancialYearStartMonth", "0")]
    [InlineData("Level2Amount", "-5")]
    [InlineData("CtrThreshold", "lots")]
    [InlineData("WeeklyHolidays", "Fri-day")]
    [InlineData("BaseCurrency", "TAKA")]
    [InlineData("BaseCurrency", "B1T")]
    [InlineData("BankName", "  ")]
    [InlineData("CashGl", " ")]
    [InlineData("BackValueMaxDays", "-1")]
    [InlineData("PasswordHistoryCount", "2.5")]
    public void Invalid_values_are_rejected_with_a_message(string key, string value) => Assert.NotNull(ParameterRules.Validate(key, value));

    [Fact]
    public void Every_shipped_default_passes_its_own_validation() =>
        Assert.All(ParamService.Defaults, d => Assert.Null(ParameterRules.Validate(d.Key, d.Value)));

    [Fact]
    public void Dormancy_threshold_cannot_exceed_the_inoperative_threshold()
    {
        var bad = new Dictionary<string, string> { ["DormantAfterDays"] = "2000", ["InoperativeAfterDays"] = "1000" };
        var good = new Dictionary<string, string> { ["DormantAfterDays"] = "365", ["InoperativeAfterDays"] = "1095" };

        Assert.Single(ParameterRules.ValidateCombination(bad));
        Assert.Empty(ParameterRules.ValidateCombination(good));
    }

    [Fact]
    public void Gl_parameters_are_recognised_by_their_suffix()
    {
        Assert.True(ParameterRules.IsGlParameter("InterBranchGl"));
        Assert.False(ParameterRules.IsGlParameter("VatRate"));
    }
}
