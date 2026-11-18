namespace CoreBanking.Application.Features.Deposits;

public class DepositService(IApplicationDbContext db, LedgerService ledger, NumberService numbers, ParamService prm,
    SanctionService sanctions, RateService rates)
{
    /// <summary>Adds fee + VAT lines credited to income. Fee is debited from an account or taken from cash by the caller.</summary>
    public async Task<(decimal fee, decimal vat)> FeeAsync(decimal fee)
    {
        fee = Money.R(fee);
        var vat = Money.R(fee * await prm.DecAsync("VatRate") / 100m);
        return (fee, vat);
    }

    public async Task AddFeeCreditsAsync(VoucherReq req, decimal fee, decimal vat, string narr, int branchId)
    {
        req.Cr(await prm.GlIdAsync("FeeIncomeGl"), null, fee, narr, branchId);
        req.Cr(await prm.GlIdAsync("VatPayableGl"), null, vat, "VAT on " + narr, branchId);
    }

    public async Task<Account> OpenAsync(OpenAccountInput i, string maker, UserProfile? profile)
    {
        var cust = await db.Customers.FindAsync(i.CustomerId) ?? throw new BankingException("Customer not found.");
        var product = await db.Products.FindAsync(i.ProductId) ?? throw new BankingException("Product not found.");
        var branch = await db.Branches.FindAsync(i.BranchId) ?? throw new BankingException("Branch not found.");
        if (!cust.IsActive) throw new BankingException("Customer is inactive.");
        if (cust.Kyc != KycStatus.Verified) throw new BankingException("KYC must be verified by a checker before opening an account.");
        if (!product.IsActive || product.IsLoan) throw new BankingException("Select an active deposit product.");
        if (i.InitialDeposit < 0) throw new BankingException("Invalid initial deposit.");
        if (i.InitialDeposit < product.MinOpeningBalance)
            throw new BankingException($"Minimum opening balance for {product.Name} is {product.MinOpeningBalance:N2}.");

        bool individual = cust.Type is CustomerType.Individual or CustomerType.Joint or CustomerType.Minor or CustomerType.Staff or CustomerType.NonResident;
        if (i.JointCustomerIds.Count > 0 && !individual)
            throw new BankingException("Joint accounts are allowed for individual customers only.");
        if (i.Nominees.Count > 0 && !individual)
            throw new BankingException("Nominees can be added for individual customers only.");
        if (i.Nominees.Count > 0 && Math.Abs(i.Nominees.Sum(n => n.SharePercent) - 100m) > 0.01m)
            throw new BankingException("Nominee shares must total 100%.");
        if (i.Mode != OperationMode.Single && i.JointCustomerIds.Count == 0)
            throw new BankingException("Joint operation mode needs at least one joint holder.");

        var holders = new List<Customer>();
        foreach (var jid in i.JointCustomerIds.Distinct().Where(j => j != cust.Id))
        {
            var jc = await db.Customers.FindAsync(jid) ?? throw new BankingException("Joint holder not found.");
            if (jc.Kyc != KycStatus.Verified) throw new BankingException($"KYC of joint holder {jc.Name} is not verified.");
            holders.Add(jc);
        }

        // UNSCR + local sanction screening: applicant, joint holders, nominees, PoA holder, introducer
        var screen = new List<string?> { cust.Name, cust.Nid, cust.Passport, i.PowerOfAttorney, cust.IntroducerName };
        foreach (var h in holders) { screen.Add(h.Name); screen.Add(h.Nid); }
        foreach (var n in i.Nominees) { screen.Add(n.Name); screen.Add(n.Nid); }
        var hits = await sanctions.ScreenAsync(screen);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit - account opening blocked: " + SanctionService.Format(hits));

        var cur = await db.Currencies.FirstAsync(c => c.IsBase);
        var cat = product.Category;
        decimal rate = product.InterestRate;
        if (cat is ProductCategory.FDR or ProductCategory.DPS)
        {
            if (i.TermMonths <= 0) i.TermMonths = product.DefaultTermMonths;
            if (i.TermMonths <= 0) throw new BankingException("Term (months) is required for FDR/DPS.");
            rate = await rates.GetRateAsync(product.Id, i.InitialDeposit, i.TermMonths, branch.BusinessDate);
        }
        if (cat == ProductCategory.FDR)
        {
            if (i.InitialDeposit <= 0) throw new BankingException("FDR principal must be greater than zero.");
        }
        if (cat == ProductCategory.DPS && i.InstallmentAmount <= 0)
            throw new BankingException("Monthly installment amount is required for DPS.");
        if (cat == ProductCategory.DPS && i.InitialDeposit != i.InstallmentAmount && i.InitialDeposit > 0)
            throw new BankingException("DPS opening deposit must equal the first installment.");
        if (cat != ProductCategory.Current && i.OdLimit > 0) throw new BankingException("Overdraft is allowed on current accounts only.");

        var acc = new Account
        {
            AccountNo = await numbers.NextAccountNoAsync(branch.Code, product.Code),
            Title = string.IsNullOrWhiteSpace(i.Title) ? cust.Name : i.Title!,
            CustomerId = cust.Id, ProductId = product.Id, BranchId = branch.Id, CurrencyId = cur.Id,
            Mode = i.Mode, OpenedOn = branch.BusinessDate, Status = AccountStatus.PendingApproval,
            OdLimit = i.OdLimit, SingleDebitLimit = i.SingleDebitLimit, DailyDebitLimit = i.DailyDebitLimit, MonthlyDebitLimit = i.MonthlyDebitLimit,
            PowerOfAttorney = i.PowerOfAttorney, InterestRate = rate, TermMonths = i.TermMonths,
            InstallmentAmount = i.InstallmentAmount, LinkedAccountId = i.LinkedAccountId, AutoRenew = i.AutoRenew,
            PrincipalAmount = cat == ProductCategory.FDR ? i.InitialDeposit : 0, MadeBy = maker
        };
        if (cat is ProductCategory.FDR or ProductCategory.DPS)
            acc.MaturityDate = branch.BusinessDate.AddMonths(i.TermMonths);
        foreach (var h in holders) acc.Holders.Add(new AccountHolder { CustomerId = h.Id });
        foreach (var n in i.Nominees) { n.Id = 0; acc.Nominees.Add(n); }

        using var tx = await db.Database.BeginTransactionAsync();
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        if (i.InitialDeposit > 0)
        {
            var (fee, vat) = await FeeAsync(product.OpeningFee);
            var type = cat == ProductCategory.FDR ? VoucherType.FdrOpen : VoucherType.AccountOpening;
            var req = new VoucherReq
            {
                Type = type, BranchId = branch.Id, Maker = maker, MakerProfile = profile,
                Description = $"{(cat == ProductCategory.FDR ? "FDR" : "Account")} opening {acc.AccountNo} - {acc.Title}",
                SourceKind = "Account", SourceId = acc.Id
            };
            if (cat == ProductCategory.FDR && i.SourceAccountId is int src)
            {
                var sa = await db.Accounts.FindAsync(src) ?? throw new BankingException("Source account not found.");
                if (sa.CustomerId != cust.Id && !await db.AccountHolders.AnyAsync(h => h.AccountId == sa.Id && h.CustomerId == cust.Id))
                    throw new BankingException("Source account must belong to the same customer.");
                req.Dr(null, src, i.InitialDeposit, "FDR opening debit");
                req.Cr(null, acc.Id, i.InitialDeposit, "FDR principal");
            }
            else
            {
                req.Dr(await prm.GlIdAsync("CashGl"), null, i.InitialDeposit, "Cash received", branch.Id);
                if (fee + vat > i.InitialDeposit) throw new BankingException("Deposit is smaller than the account opening fee.");
                req.Cr(null, acc.Id, i.InitialDeposit - fee - vat, "Opening deposit (net of fees)");
                if (fee > 0) await AddFeeCreditsAsync(req, fee, vat, "account opening fee", branch.Id);
            }
            await ledger.CreateAsync(req);
        }
        await tx.CommitAsync();
        return acc;
    }

    /// <summary>Approve an account that has no opening voucher (zero-balance opening).</summary>
    public async Task ApproveZeroBalanceAsync(int accountId, string checker)
    {
        var a = await db.Accounts.Include(x => x.Product).FirstAsync(x => x.Id == accountId);
        if (a.Status != AccountStatus.PendingApproval) throw new BankingException("Account is not pending approval.");
        if (string.Equals(a.MadeBy, checker, StringComparison.OrdinalIgnoreCase)) throw new BankingException("Maker-checker violation: you cannot approve your own entry.");
        if (a.Product!.MinOpeningBalance > 0 || a.Product.Category is ProductCategory.FDR or ProductCategory.DPS)
            throw new BankingException("This account needs an opening deposit voucher; approve the voucher instead.");
        if (await db.VoucherLines.AnyAsync(l => l.AccountId == a.Id && l.Voucher!.Status == VoucherStatus.Pending))
            throw new BankingException("An opening voucher is pending; authorize that voucher instead.");
        a.Status = AccountStatus.Active; a.CheckedBy = checker; a.Version++;
        await db.SaveChangesAsync();
    }

    public async Task<Voucher> PayDpsInstallmentAsync(int dpsAccountId, int? sourceAccountId, string maker, UserProfile? profile)
    {
        var dps = await db.Accounts.Include(a => a.Product).FirstAsync(a => a.Id == dpsAccountId);
        if (dps.Product!.Category != ProductCategory.DPS || dps.Status != AccountStatus.Active) throw new BankingException("Not an active DPS account.");
        if (dps.InstallmentsPaid >= dps.TermMonths) throw new BankingException("All installments are already paid.");
        var req = new VoucherReq
        {
            Type = VoucherType.DpsInstallment, BranchId = dps.BranchId, Maker = maker, MakerProfile = profile,
            Description = $"DPS installment {dps.InstallmentsPaid + 1}/{dps.TermMonths} - {dps.AccountNo}", SourceKind = "Account", SourceId = dps.Id
        };
        if (sourceAccountId is int s) req.Dr(null, s, dps.InstallmentAmount, "DPS installment");
        else req.Dr(await prm.GlIdAsync("CashGl"), null, dps.InstallmentAmount, "Cash received", dps.BranchId);
        req.Cr(null, dps.Id, dps.InstallmentAmount, "DPS installment");
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> EncashAsync(int accountId, bool premature, bool toCash, string maker, UserProfile? profile)
    {
        var a = await db.Accounts.Include(x => x.Product).Include(x => x.Branch).FirstAsync(x => x.Id == accountId);
        var cat = a.Product!.Category;
        if (cat is not (ProductCategory.FDR or ProductCategory.DPS)) throw new BankingException("Only FDR/DPS accounts can be encashed.");
        if (a.Status is not (AccountStatus.Active or AccountStatus.Matured)) throw new BankingException($"Account is {a.Status}.");
        if (await db.Vouchers.AnyAsync(v => v.SourceKind == "Account" && v.SourceId == a.Id && v.Type == VoucherType.Encash && v.Status == VoucherStatus.Pending))
            throw new BankingException("An encashment is already pending authorization.");
        var today = a.Branch!.BusinessDate;
        bool matured = a.MaturityDate != null && a.MaturityDate <= today;
        if (!matured && !premature) throw new BankingException($"Not matured until {a.MaturityDate:dd-MMM-yyyy}. Use premature encashment.");

        var basis = await prm.DecAsync("InterestDayBasis");
        var accrued = Money.R(a.AccruedInterest);
        var payout = accrued;
        if (!matured)
        {
            var days = Math.Max(0, (today - a.OpenedOn).Days);
            payout = Math.Min(accrued, Money.R(a.Balance * a.Product.PrematureRate / 100m * days / basis));
        }
        var excess = accrued - payout;
        var tax = Money.R(payout * await prm.DecAsync("SourceTaxRate") / 100m);

        var req = new VoucherReq
        {
            Type = VoucherType.Encash, BranchId = a.BranchId, Maker = maker, MakerProfile = profile,
            Description = $"{cat} {(matured ? "maturity" : "premature")} encashment {a.AccountNo}", SourceKind = "Account", SourceId = a.Id
        };
        var net = a.Balance + payout - tax;
        req.Dr(null, a.Id, a.Balance, "Principal repaid");
        var accrualGl = a.Product.AccrualGlId ?? throw new BankingException("Product has no interest payable GL configured.");
        req.Dr(accrualGl, null, accrued, "Accrued interest settled", a.BranchId);
        if (excess > 0) req.Cr(a.Product.InterestGlId, null, excess, "Reversal of excess interest accrued", a.BranchId);
        req.Cr(await prm.GlIdAsync("TaxPayableGl"), null, tax, "Source tax on interest", a.BranchId);
        if (!toCash && a.LinkedAccountId is int linked) req.Cr(null, linked, net, "FDR/DPS proceeds");
        else req.Cr(await prm.GlIdAsync("CashGl"), null, net, "Cash paid", a.BranchId);
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> CloseAsync(int accountId, string maker, UserProfile? profile)
    {
        var a = await db.Accounts.Include(x => x.Product).FirstAsync(x => x.Id == accountId);
        if (a.Product!.Category is ProductCategory.FDR or ProductCategory.DPS) throw new BankingException("Use encashment for FDR/DPS.");
        if (a.Status is AccountStatus.Closed or AccountStatus.PendingApproval) throw new BankingException($"Account is {a.Status}.");
        if (a.Balance < 0) throw new BankingException("Account has a negative balance; clear the overdraft first.");
        if (a.LienAmount > 0) throw new BankingException("Release the lien before closing.");
        if (await db.Loans.AnyAsync(l => l.SettlementAccountId == a.Id && l.Status == LoanStatus.Disbursed))
            throw new BankingException("Account is the settlement account of a running loan.");
        var (fee, vat) = await FeeAsync(a.Product.ClosingFee);
        var accrued = Money.R(a.AccruedInterest);
        var tax = Money.R(accrued * await prm.DecAsync("SourceTaxRate") / 100m);
        var pay = a.Balance + accrued - tax - fee - vat;
        if (pay < 0) throw new BankingException("Balance does not cover closing charges.");
        if (a.Balance + accrued <= 0) throw new BankingException("Nothing to pay out; account has zero balance. Use status change -> Closed.");
        var req = new VoucherReq
        {
            Type = VoucherType.CashWithdrawal, BranchId = a.BranchId, Maker = maker, MakerProfile = profile,
            Description = $"Account closing {a.AccountNo}", SourceKind = "AccountClose", SourceId = a.Id
        };
        if (a.Balance > 0) req.Dr(null, a.Id, a.Balance, "Closing balance paid");
        if (accrued > 0) { req.Dr(a.Product.AccrualGlId, null, accrued, "Accrued interest", a.BranchId); req.Cr(await prm.GlIdAsync("TaxPayableGl"), null, tax, "Source tax", a.BranchId); }
        if (fee > 0) await AddFeeCreditsAsync(req, fee, vat, "account closing fee", a.BranchId);
        req.Cr(await prm.GlIdAsync("CashGl"), null, pay, "Cash paid", a.BranchId);
        return await ledger.CreateAsync(req);
    }

    public const long ChequeNumberBase = 10_000_000;

    public async Task<Voucher?> IssueChequeBookAsync(int accountId, int leaves, string maker, UserProfile? profile)
    {
        var a = await db.Accounts.Include(x => x.Product).Include(x => x.Branch).FirstAsync(x => x.Id == accountId);
        if (a.Product!.Category is not (ProductCategory.Current or ProductCategory.Savings or ProductCategory.SND))
            throw new BankingException("Cheque books are not issued for this product.");
        if (a.Status != AccountStatus.Active) throw new BankingException("Account must be active.");
        if (leaves is not (10 or 25 or 50 or 100)) throw new BankingException("Leaves must be 10, 25, 50 or 100.");
        var (fee, vat) = await FeeAsync(a.Product.ChequeBookFee * leaves / 25m);

        using var tx = await db.Database.BeginTransactionAsync();
        var from = ChequeNumberBase + await numbers.ReserveRangeAsync("CHQ-LEAF", leaves);
        var book = new ChequeBook { AccountId = a.Id, FromNo = from, ToNo = from + leaves - 1, IssuedOn = a.Branch!.BusinessDate, IssuedBy = maker };
        Voucher? v = null;
        if (fee > 0)
        {
            var req = new VoucherReq { Type = VoucherType.Charge, BranchId = a.BranchId, Maker = maker, MakerProfile = profile, Description = $"Cheque book fee {a.AccountNo} ({book.FromNo}-{book.ToNo})" };
            req.Dr(null, a.Id, fee + vat, "Cheque book fee");
            await AddFeeCreditsAsync(req, fee, vat, "cheque book fee", a.BranchId);
            v = await ledger.CreateAsync(req);
        }
        db.ChequeBooks.Add(book);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return v;
    }

    public async Task<Voucher?> StopPaymentAsync(int accountId, string chequeNo, decimal amount, string reason, string maker, UserProfile? profile)
    {
        var a = await db.Accounts.Include(x => x.Product).FirstAsync(x => x.Id == accountId);
        if (!long.TryParse(chequeNo, out var no) || !await db.ChequeBooks.AnyAsync(b => b.AccountId == a.Id && b.FromNo <= no && b.ToNo >= no))
            throw new BankingException("Cheque number was not issued against this account.");
        if (await db.StopPayments.AnyAsync(s => s.AccountId == a.Id && s.ChequeNo == chequeNo && s.IsActive))
            throw new BankingException("Stop payment already active for this cheque.");
        var (fee, vat) = await FeeAsync(a.Product!.StopPaymentFee);

        using var tx = await db.Database.BeginTransactionAsync();
        Voucher? v = null;
        if (fee > 0)
        {
            var req = new VoucherReq { Type = VoucherType.Charge, BranchId = a.BranchId, Maker = maker, MakerProfile = profile, Description = $"Stop payment fee cheque {chequeNo}" };
            req.Dr(null, a.Id, fee + vat, "Stop payment fee");
            await AddFeeCreditsAsync(req, fee, vat, "stop payment fee", a.BranchId);
            v = await ledger.CreateAsync(req);
        }
        db.StopPayments.Add(new StopPayment { AccountId = a.Id, ChequeNo = chequeNo, Amount = amount, Reason = reason, MadeBy = maker });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return v;
    }

    /// <summary>Record a dishonoured cheque and recover the dishonour charge.</summary>
    public async Task<Voucher?> ReturnChequeAsync(int accountId, string chequeNo, decimal amount, string reason, bool chargeFee, string maker, UserProfile? profile)
    {
        var a = await db.Accounts.Include(x => x.Product).Include(x => x.Branch).FirstAsync(x => x.Id == accountId);
        using var tx = await db.Database.BeginTransactionAsync();
        Voucher? v = null; decimal charged = 0;
        if (chargeFee)
        {
            var (fee, vat) = await FeeAsync(a.Product!.ChequeReturnFee);
            if (fee > 0)
            {
                var req = new VoucherReq { Type = VoucherType.Charge, BranchId = a.BranchId, Maker = maker, MakerProfile = profile, IsSystem = true, Description = $"Cheque dishonour charge {chequeNo}" };
                // dishonour fee may take the account into overdraft by design: debit GL against account directly
                req.Dr(null, a.Id, fee + vat, "Cheque return charge");
                await AddFeeCreditsAsync(req, fee, vat, "cheque return charge", a.BranchId);
                v = await ledger.CreateAsync(req); charged = fee + vat;
            }
        }
        db.ChequeReturns.Add(new ChequeReturn { AccountId = a.Id, ChequeNo = chequeNo, Amount = amount, Reason = reason, ReturnDate = a.Branch!.BusinessDate, ChargeApplied = charged });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return v;
    }
}
