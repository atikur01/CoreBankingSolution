namespace CoreBanking.Tests.Unit;

public class MoneyTests
{
    [Theory]
    [InlineData(2.345, 2.35)]
    [InlineData(2.344, 2.34)]
    [InlineData(-2.345, -2.35)]
    [InlineData(0.005, 0.01)]
    [InlineData(100, 100)]
    public void Round_uses_two_decimals_away_from_zero(double input, double expected) =>
        Assert.Equal((decimal)expected, Money.R((decimal)input));

    [Theory]
    [InlineData("7992739871", 3)]
    [InlineData("0", 0)]
    [InlineData("1", 8)]
    public void CheckDigit_follows_luhn(string payload, int expected) =>
        Assert.Equal(expected, Money.CheckDigit(payload));

    [Fact]
    public void ValidNumber_accepts_generated_numbers_and_rejects_tampered_ones()
    {
        var number = "0010000123" + Money.CheckDigit("0010000123");
        Assert.True(Money.ValidNumber(number));
        var tampered = "0010000124" + number[^1];
        Assert.False(Money.ValidNumber(tampered));
        Assert.False(Money.ValidNumber("12a45"));
        Assert.False(Money.ValidNumber("7"));
    }

    [Theory]
    [InlineData("Md. Abdul-Karim", "md abdul karim")]
    [InlineData("  ALI   AHMED ", "ali ahmed")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Norm_collapses_punctuation_case_and_spaces(string? input, string expected) =>
        Assert.Equal(expected, Money.Norm(input));
}
