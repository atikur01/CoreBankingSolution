namespace CoreBanking.Tests.Unit;

public class LoanCalculatorTests
{
    private static readonly DateTime Start = new(2026, 1, 31);

    [Fact]
    public void Emi_schedule_repays_exact_principal_with_declining_interest()
    {
        var rows = LoanCalculator.Schedule(120_000m, 12m, 12, Start, bullet: false);

        Assert.Equal(12, rows.Count);
        Assert.Equal(120_000m, rows.Sum(r => r.Principal));
        Assert.Equal(1_200m, rows[0].Interest);
        Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.True(pair.Second.Interest < pair.First.Interest));
        Assert.Equal(Enumerable.Range(1, 12), rows.Select(r => r.No));
    }

    [Fact]
    public void Emi_installments_are_level_except_for_the_last_rounding_adjustment()
    {
        var rows = LoanCalculator.Schedule(120_000m, 12m, 12, Start, bullet: false);
        var emis = rows.Select(r => r.Principal + r.Interest).ToList();

        Assert.All(emis.Take(11), e => Assert.Equal(10_661.85m, e));
        Assert.InRange(emis[11], 10_661.85m - 1m, 10_661.85m + 1m);
    }

    [Fact]
    public void Due_dates_advance_one_month_each_from_the_start_date()
    {
        var rows = LoanCalculator.Schedule(12_000m, 10m, 3, Start, bullet: false);

        Assert.Equal(Start.AddMonths(1), rows[0].Due);
        Assert.Equal(Start.AddMonths(2), rows[1].Due);
        Assert.Equal(Start.AddMonths(3), rows[2].Due);
    }

    [Fact]
    public void Zero_rate_loan_splits_principal_and_absorbs_rounding_in_the_last_installment()
    {
        var rows = LoanCalculator.Schedule(10_000m, 0m, 3, Start, bullet: false);

        Assert.Equal([3333.33m, 3333.33m, 3333.34m], rows.Select(r => r.Principal));
        Assert.All(rows, r => Assert.Equal(0m, r.Interest));
    }

    [Fact]
    public void Bullet_loan_has_a_single_installment_with_simple_interest()
    {
        var rows = LoanCalculator.Schedule(100_000m, 13m, 6, Start, bullet: true);

        var row = Assert.Single(rows);
        Assert.Equal(100_000m, row.Principal);
        Assert.Equal(6_500m, row.Interest);
        Assert.Equal(Start.AddMonths(6), row.Due);
    }

    [Theory]
    [InlineData(0, LoanClass.Standard, 1)]
    [InlineData(59, LoanClass.Standard, 1)]
    [InlineData(60, LoanClass.SMA, 1)]
    [InlineData(89, LoanClass.SMA, 1)]
    [InlineData(90, LoanClass.SubStandard, 20)]
    [InlineData(269, LoanClass.SubStandard, 20)]
    [InlineData(270, LoanClass.Doubtful, 50)]
    [InlineData(359, LoanClass.Doubtful, 50)]
    [InlineData(360, LoanClass.BadLoss, 100)]
    [InlineData(2000, LoanClass.BadLoss, 100)]
    public void Classification_follows_overdue_day_bands(int days, LoanClass expectedClass, int expectedPct)
    {
        var (cls, pct) = LoanCalculator.Classify(days);

        Assert.Equal(expectedClass, cls);
        Assert.Equal(expectedPct, pct);
    }

    private static LoanInstallment Inst(int no, decimal principal, decimal interest, decimal principalPaid = 0, decimal interestPaid = 0, InstallmentStatus status = InstallmentStatus.Due) =>
        new() { No = no, PrincipalDue = principal, InterestDue = interest, PrincipalPaid = principalPaid, InterestPaid = interestPaid, Status = status };

    [Fact]
    public void Allocation_pays_interest_before_principal_in_installment_order()
    {
        var plan = new[] { Inst(2, 1000, 100), Inst(1, 1000, 100) };

        var alloc = LoanService.Allocate(plan, 150m);

        var first = Assert.Single(alloc);
        Assert.Equal(1, first.Inst.No);
        Assert.Equal(100m, first.Interest);
        Assert.Equal(50m, first.Principal);
    }

    [Fact]
    public void Allocation_spills_over_to_the_next_installment_and_skips_paid_ones()
    {
        var plan = new[]
        {
            Inst(1, 1000, 100, 1000, 100, InstallmentStatus.Paid),
            Inst(2, 1000, 100),
            Inst(3, 1000, 100)
        };

        var alloc = LoanService.Allocate(plan, 1250m);

        Assert.Equal([2, 3], alloc.Select(a => a.Inst.No));
        Assert.Equal(1100m, alloc[0].Interest + alloc[0].Principal);
        Assert.Equal(150m, alloc[1].Interest + alloc[1].Principal);
        Assert.Equal(1250m, alloc.Sum(a => a.Interest + a.Principal));
    }

    [Fact]
    public void Allocation_respects_amounts_already_paid()
    {
        var plan = new[] { Inst(1, 1000, 100, principalPaid: 400, interestPaid: 100) };

        var alloc = LoanService.Allocate(plan, 5000m);

        var a = Assert.Single(alloc);
        Assert.Equal(0m, a.Interest);
        Assert.Equal(600m, a.Principal);
    }

    [Fact]
    public void Allocation_of_zero_returns_nothing() =>
        Assert.Empty(LoanService.Allocate([Inst(1, 1000, 100)], 0m));
}
