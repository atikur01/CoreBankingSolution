namespace CoreBanking.Application.Features.Loans;

public static class LoanCalculator
{
    public record Row(int No, DateTime Due, decimal Principal, decimal Interest);

    /// <summary>EMI (reducing balance) for term loans; single bullet installment for demand loans.</summary>
    public static List<Row> Schedule(decimal principal, decimal annualRate, int months, DateTime start, bool bullet)
    {
        var rows = new List<Row>();
        var r = annualRate / 1200m;
        if (bullet)
        {
            rows.Add(new Row(1, start.AddMonths(months), principal, Money.R(principal * annualRate / 1200m * months)));
            return rows;
        }
        decimal emi = r == 0 ? Money.R(principal / months)
            : Money.R(principal * r * (decimal)Math.Pow((double)(1 + r), months) / ((decimal)Math.Pow((double)(1 + r), months) - 1));
        var bal = principal;
        for (int i = 1; i <= months; i++)
        {
            var interest = Money.R(bal * r);
            var prin = i == months ? bal : Math.Min(bal, emi - interest);
            rows.Add(new Row(i, start.AddMonths(i), prin, interest));
            bal -= prin;
        }
        return rows;
    }

    public static (LoanClass cls, decimal pct) Classify(int overdueDays) => overdueDays switch
    {
        >= 360 => (LoanClass.BadLoss, 100m),
        >= 270 => (LoanClass.Doubtful, 50m),
        >= 90 => (LoanClass.SubStandard, 20m),
        >= 60 => (LoanClass.SMA, 1m),
        _ => (LoanClass.Standard, 1m)
    };
}
