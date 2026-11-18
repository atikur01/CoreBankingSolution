namespace CoreBanking.Application.Features.Loans;

public record LoanDetails(Loan Loan, List<LoanCalculator.Row>? SchedulePreview, decimal Payoff, List<Voucher> PendingVouchers);

public class LoanApplicationRequest
{
    public string Cif { get; set; } = "";
    public int ProductId { get; set; }
    public int BranchId { get; set; }
    public string SettlementAccountNo { get; set; } = "";
    public decimal Principal { get; set; }
    public int TermMonths { get; set; }
    public decimal InterestRate { get; set; }
    public string Purpose { get; set; } = "";
    public string? Collateral { get; set; }
    public decimal CollateralValue { get; set; }
    public string? Guarantor { get; set; }
}

public class LoanQueries(IApplicationDbContext db, LoanService loans)
{
    public async Task<List<Loan>> SearchAsync(string? q, LoanStatus? status, LoanClass? classification)
    {
        var query = db.Loans.AsNoTracking().Include(l => l.Customer).Include(l => l.Product).Include(l => l.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => l.LoanNo.Contains(q) || l.Customer!.Name.Contains(q) || l.Customer.CifNo.Contains(q));
        if (status != null) query = query.Where(l => l.Status == status);
        if (classification != null) query = query.Where(l => l.Classification == classification);
        return await query.OrderByDescending(l => l.Id).Take(200).ToListAsync();
    }

    public async Task<LoanDetails?> GetDetailsAsync(int id)
    {
        var loan = await db.Loans.Include(x => x.Customer).Include(x => x.Product).Include(x => x.Branch)
            .Include(x => x.SettlementAccount).Include(x => x.Installments).FirstOrDefaultAsync(x => x.Id == id);
        if (loan == null) return null;

        List<LoanCalculator.Row>? preview = null;
        if (loan.Installments.Count == 0 && loan.Status < LoanStatus.Disbursed)
            preview = LoanCalculator.Schedule(loan.Principal, loan.InterestRate, loan.TermMonths, loan.Branch!.BusinessDate,
                loan.Product!.Category == ProductCategory.DemandLoan);
        var payoff = loan.Status == LoanStatus.Disbursed ? await loans.PayoffAmountAsync(id) : 0m;
        var pending = await db.Vouchers.AsNoTracking()
            .Where(v => v.Status == VoucherStatus.Pending && v.SourceKind == "Loan" && v.SourceId == id).ToListAsync();
        return new LoanDetails(loan, preview, payoff, pending);
    }
}

public class LoanApplicationService(LookupService lookups, LoanService loans)
{
    public async Task<Loan> ApplyAsync(LoanApplicationRequest request, string maker)
    {
        var customer = await lookups.CustomerByCifAsync(request.Cif);
        var settlementId = await lookups.AccountIdByNumberAsync(request.SettlementAccountNo, "Settlement account not found.");
        return await loans.ApplyAsync(new Loan
        {
            CustomerId = customer.Id, ProductId = request.ProductId, BranchId = request.BranchId, SettlementAccountId = settlementId,
            Principal = request.Principal, TermMonths = request.TermMonths, InterestRate = request.InterestRate, Purpose = request.Purpose,
            Collateral = request.Collateral, CollateralValue = request.CollateralValue, Guarantor = request.Guarantor
        }, maker);
    }
}
