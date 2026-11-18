namespace CoreBanking.Application.Features.Loans;

public class LoanService(IApplicationDbContext db, LedgerService ledger, NumberService numbers, ParamService prm,
    SanctionService sanctions, RateService rates)
{
    public async Task<Loan> ApplyAsync(Loan l, string maker)
    {
        var cust = await db.Customers.FindAsync(l.CustomerId) ?? throw new BankingException("Customer not found.");
        var product = await db.Products.FindAsync(l.ProductId) ?? throw new BankingException("Product not found.");
        var branch = await db.Branches.FindAsync(l.BranchId) ?? throw new BankingException("Branch not found.");
        if (!product.IsLoan || !product.IsActive) throw new BankingException("Select an active loan product.");
        if (cust.Kyc != KycStatus.Verified) throw new BankingException("Customer KYC is not verified.");
        if (l.Principal <= 0 || l.TermMonths <= 0) throw new BankingException("Loan amount and term must be positive.");
        var sa = await db.Accounts.FindAsync(l.SettlementAccountId) ?? throw new BankingException("Settlement account not found.");
        if (sa.CustomerId != cust.Id) throw new BankingException("Settlement account must belong to the borrower.");
        if (sa.Status != AccountStatus.Active) throw new BankingException("Settlement account must be active.");
        var hits = await sanctions.ScreenAsync([cust.Name, cust.Nid, cust.Passport, l.Guarantor]);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit: " + SanctionService.Format(hits));

        l.LoanNo = await numbers.NextAccountNoAsync(branch.Code, product.Code);
        l.InterestRate = l.InterestRate > 0 ? l.InterestRate : await rates.GetRateAsync(product.Id, l.Principal, l.TermMonths, branch.BusinessDate);
        l.ApplicationDate = branch.BusinessDate; l.Status = LoanStatus.Applied; l.MadeBy = maker;
        l.Principal = Money.R(l.Principal);
        db.Loans.Add(l);
        await db.SaveChangesAsync();
        return l;
    }

    public async Task ApproveAsync(int id, string checker, bool approve, string? reason)
    {
        var l = await db.Loans.FindAsync(id) ?? throw new BankingException("Loan not found.");
        if (l.Status != LoanStatus.Applied) throw new BankingException("Loan is not awaiting approval.");
        if (string.Equals(l.MadeBy, checker, StringComparison.OrdinalIgnoreCase)) throw new BankingException("Maker-checker violation: you cannot approve your own application.");
        if (approve) { l.Status = LoanStatus.Approved; l.ApprovedBy = checker; }
        else { if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("Rejection reason required."); l.Status = LoanStatus.Rejected; l.RejectReason = reason; l.ApprovedBy = checker; }
        l.Version++;
        await db.SaveChangesAsync();
    }

    public async Task<Voucher> DisburseAsync(int id, string maker, UserProfile? profile)
    {
        var l = await db.Loans.Include(x => x.Product).FirstAsync(x => x.Id == id);
        if (l.Status != LoanStatus.Approved) throw new BankingException("Only approved loans can be disbursed.");
        if (await db.Vouchers.AnyAsync(v => v.SourceKind == "Loan" && v.SourceId == l.Id && v.Type == VoucherType.LoanDisburse && v.Status == VoucherStatus.Pending))
            throw new BankingException("A disbursement voucher is already pending.");
        var fee = Money.R(l.Principal * l.Product!.ProcessingFeePct / 100m);
        var vat = Money.R(fee * await prm.DecAsync("VatRate") / 100m);
        var req = new VoucherReq
        {
            Type = VoucherType.LoanDisburse, BranchId = l.BranchId, Maker = maker, MakerProfile = profile,
            Description = $"Loan disbursement {l.LoanNo}", SourceKind = "Loan", SourceId = l.Id
        };
        req.Dr(l.Product.GlAccountId, null, l.Principal, "Loan principal", l.BranchId, l.Id, "Principal");
        req.Cr(null, l.SettlementAccountId, l.Principal - fee - vat, "Loan proceeds (net of fees)");
        if (fee > 0)
        {
            req.Cr(await prm.GlIdAsync("FeeIncomeGl"), null, fee, "Loan processing fee", l.BranchId);
            req.Cr(await prm.GlIdAsync("VatPayableGl"), null, vat, "VAT on processing fee", l.BranchId);
        }
        return await ledger.CreateAsync(req);
    }

    public record Alloc(LoanInstallment Inst, decimal Interest, decimal Principal);

    public static List<Alloc> Allocate(IEnumerable<LoanInstallment> installments, decimal amount)
    {
        var res = new List<Alloc>(); var rem = amount;
        foreach (var i in installments.Where(x => x.Status == InstallmentStatus.Due).OrderBy(x => x.No))
        {
            if (rem <= 0) break;
            var pi = Math.Min(rem, i.InterestDue - i.InterestPaid); rem -= pi;
            var pp = Math.Min(rem, i.PrincipalDue - i.PrincipalPaid); rem -= pp;
            if (pi > 0 || pp > 0) res.Add(new Alloc(i, pi, pp));
        }
        return res;
    }

    public async Task<Voucher> RepayAsync(int id, decimal amount, int? sourceAccountId, string maker, UserProfile? profile)
    {
        var l = await db.Loans.Include(x => x.Product).Include(x => x.Installments).FirstAsync(x => x.Id == id);
        if (l.Status != LoanStatus.Disbursed) throw new BankingException("Loan is not running.");
        amount = Money.R(amount);
        var totalLeft = l.Installments.Where(i => i.Status == InstallmentStatus.Due).Sum(i => i.Outstanding);
        if (amount <= 0) throw new BankingException("Amount must be positive.");
        if (amount > totalLeft) throw new BankingException($"Amount exceeds total remaining dues ({totalLeft:N2}).");
        var alloc = Allocate(l.Installments, amount);
        var prin = alloc.Sum(a => a.Principal);
        var intRecv = alloc.Where(a => a.Inst.InterestRecognized).Sum(a => a.Interest);
        var intInc = alloc.Where(a => !a.Inst.InterestRecognized).Sum(a => a.Interest);
        var req = new VoucherReq
        {
            Type = VoucherType.LoanRepay, BranchId = l.BranchId, Maker = maker, MakerProfile = profile,
            Description = $"Loan repayment {l.LoanNo}", SourceKind = "Loan", SourceId = l.Id
        };
        if (sourceAccountId is int s) req.Dr(null, s, amount, "Loan repayment " + l.LoanNo);
        else req.Dr(await prm.GlIdAsync("CashGl"), null, amount, "Cash received", profile?.BranchId ?? l.BranchId);
        req.Cr(l.Product!.GlAccountId, null, prin, "Principal repaid", l.BranchId, l.Id, "Principal");
        req.Cr(await prm.GlIdAsync("LoanInterestReceivableGl"), null, intRecv, "Interest receivable settled", l.BranchId, l.Id, "Interest");
        req.Cr(await prm.GlIdAsync("LoanInterestIncomeGl"), null, intInc, "Interest income (advance payment)", l.BranchId, l.Id, "InterestIncome");
        return await ledger.CreateAsync(req);
    }

    public async Task<decimal> PayoffAmountAsync(int id) =>
        await db.LoanInstallments.Where(i => i.LoanId == id && i.Status == InstallmentStatus.Due).SumAsync(i => i.PrincipalDue - i.PrincipalPaid + i.InterestDue - i.InterestPaid);
}
