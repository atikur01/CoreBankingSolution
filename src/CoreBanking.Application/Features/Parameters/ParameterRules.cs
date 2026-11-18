namespace CoreBanking.Application.Features.Parameters;

public static class ParameterRules
{
    private static readonly HashSet<string> NonNegativeAmounts = ["Level2Amount", "CtrThreshold", "MinRemittanceCommission"];
    private static readonly HashSet<string> NonNegativeCounts =
        ["BackValueMaxDays", "DormantAfterDays", "InoperativeAfterDays", "PasswordExpiryDays", "PasswordHistoryCount"];

    public static bool IsGlParameter(string key) => key.EndsWith("Gl", StringComparison.Ordinal);

    public static string? Validate(string key, string value)
    {
        if (IsGlParameter(key)) return string.IsNullOrWhiteSpace(value) ? "A GL code is required." : null;
        if (key == "BankName") return string.IsNullOrWhiteSpace(value) ? "A bank name is required." : null;
        if (key == "BaseCurrency") return value.Length == 3 && value.All(char.IsLetter) ? null : "Enter a 3-letter currency code.";
        if (key == "WeeklyHolidays")
        {
            var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.All(n => Enum.TryParse<DayOfWeek>(n, true, out _)) ? null : "Use comma separated day names such as Friday,Saturday.";
        }
        if (key == "FinancialYearStartMonth") return IsInteger(value, 1, 12) ? null : "Enter a month number between 1 and 12.";
        if (key == "InterestDayBasis") return IsInteger(value, 1, 366) ? null : "Enter a day-count basis between 1 and 366.";
        if (NonNegativeCounts.Contains(key)) return IsInteger(value, 0, int.MaxValue) ? null : "Enter a whole number of zero or more.";
        if (NonNegativeAmounts.Contains(key)) return IsDecimal(value, out var amount) && amount >= 0 ? null : "Enter an amount of zero or more.";
        if (key.EndsWith("Rate", StringComparison.Ordinal) || key.EndsWith("Pct", StringComparison.Ordinal))
            return IsDecimal(value, out var pct) && pct is >= 0 and <= 100 ? null : "Enter a percentage between 0 and 100.";
        return null;
    }

    public static IEnumerable<string> ValidateCombination(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("DormantAfterDays", out var dormant) && values.TryGetValue("InoperativeAfterDays", out var inoperative)
            && IsInteger(dormant, 0, int.MaxValue) && IsInteger(inoperative, 0, int.MaxValue) && int.Parse(dormant) > int.Parse(inoperative))
            yield return "DormantAfterDays cannot be greater than InoperativeAfterDays.";
    }

    private static bool IsInteger(string value, int min, int max) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max;

    private static bool IsDecimal(string value, out decimal result) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
