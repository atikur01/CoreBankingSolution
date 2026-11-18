using System.Text;

namespace CoreBanking.Domain.Common;

public static class Money
{
    public static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Luhn mod-10 check digit for a numeric payload.</summary>
    public static int CheckDigit(string payload)
    {
        int sum = 0; bool dbl = true;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            int d = payload[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return (10 - sum % 10) % 10;
    }

    public static bool ValidNumber(string number) =>
        number.Length > 1 && number.All(char.IsDigit) && CheckDigit(number[..^1]) == number[^1] - '0';

    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
