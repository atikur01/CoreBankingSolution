
namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

internal sealed class DemoLoanSeeder(DemoState s)
{
    private sealed record Plan(int Customer, string Product, decimal Principal, decimal Rate, int Term, int DaysAgo, int Paid, LoanStatus Status,
        string Purpose, string? Collateral, decimal CollateralValue, string? Guarantor, string? RejectReason = null);

    private static readonly Plan[] Plans =
    [
        new(0, "61", 500000, 12.0m, 36, 200, -1, LoanStatus.Disbursed, "Working capital for retail business", "Registered mortgage on shop premises", 1200000, "Md. Anwar Hossain"),
        new(1, "61", 1200000, 11.5m, 24, 100, 2, LoanStatus.Disbursed, "Home renovation", "Lien on FDR and salary assignment", 1500000, null),
        new(2, "61", 300000, 13.0m, 12, 160, 2, LoanStatus.Disbursed, "Purchase of laptop and equipment", "Personal guarantee", 0, "Kamal Uddin Ahmed"),
        new(4, "61", 800000, 12.5m, 36, 300, 3, LoanStatus.Disbursed, "Daughter's higher education abroad", "Registered mortgage on residential flat", 2400000, "Md. Shahidul Alam"),
        new(5, "61", 600000, 14.0m, 24, 400, 2, LoanStatus.Disbursed, "Business expansion", "Hypothecation of stock and machinery", 900000, "Md. Jamal Uddin"),
        new(6, "62", 1000000, 13.0m, 6, 60, 0, LoanStatus.Disbursed, "Bridge finance against export proceeds", "Lien on export LC", 1250000, null),
        new(7, "61", 200000, 12.0m, 12, 420, -2, LoanStatus.Closed, "Medical expenses", "Personal guarantee", 0, "Mizanur Rahman"),
        new(8, "61", 2000000, 12.0m, 60, 5, 0, LoanStatus.Applied, "Purchase of commercial vehicle", "Registered hypothecation of vehicle", 2600000, "Late Nazrul Chowdhury"),
        new(15, "62", 500000, 13.0m, 6, 3, 0, LoanStatus.Applied, "Working capital for clinic", "Lien on savings", 600000, null),
        new(20, "61", 750000, 12.0m, 24, 12, 0, LoanStatus.Approved, "Office fit-out", "Registered mortgage on land", 1800000, null),
        new(23, "61", 3000000, 12.0m, 60, 20, 0, LoanStatus.Rejected, "Construction of residential building", "Plot documents (title unverified)", 3500000, null, "Insufficient collateral coverage and unverified land title"),
        new(26, "61", 400000, 12.5m, 24, 2, 0, LoanStatus.Applied, "Purchase of agricultural machinery", "Hypothecation of machinery", 480000, "Md. Abdul Wahab"),
    ];

    public void Build()
    {
        foreach (var plan in Plans)
        {
            var cust = s.Customers[plan.Customer];
            var settlement = s.Accounts.First(a => a.Customer == cust && a.Product!.Category == ProductCategory.Savings && a.Status == AccountStatus.Active);
            var product = s.Products[plan.Product];
            var branch = s.Branches.First(b => b.Id == cust.BranchId);
            var running = plan.Status is LoanStatus.Disbursed or LoanStatus.Closed;
            var date = s.WorkDay(s.Today.AddDays(-plan.DaysAgo));
            var loan = new Loan
            {
                LoanNo = s.AccountNo(branch, product.Code), Customer = cust, Product = product, BranchId = branch.Id, SettlementAccount = settlement,
                Principal = plan.Principal, InterestRate = plan.Rate, TermMonths = plan.Term, Purpose = plan.Purpose, Collateral = plan.Collateral,
                CollateralValue = plan.CollateralValue, Guarantor = plan.Guarantor, Status = plan.Status, MadeBy = DemoState.OfficerUser,
                ApplicationDate = running ? s.WorkDay(date.AddDays(-7)) : date, ApprovedBy = plan.Status == LoanStatus.Applied ? null : DemoState.ManagerUser, RejectReason = plan.RejectReason
            };
            s.Loans.Add(loan);
            s.Db.Loans.Add(loan);
            if (running) Activate(loan, plan, date);
        }
    }

    private void Activate(Loan loan, Plan plan, DateTime date)
    {
        loan.DisbursedOn = date;
        loan.OutstandingPrincipal = loan.Principal;
        foreach (var r in LoanCalculator.Schedule(loan.Principal, loan.InterestRate, loan.TermMonths, date, loan.Product!.Category == ProductCategory.DemandLoan))
            loan.Installments.Add(new LoanInstallment { No = r.No, DueDate = r.Due, PrincipalDue = r.Principal, InterestDue = r.Interest });

        var due = loan.Installments.Where(i => i.DueDate <= s.Today).OrderBy(i => i.No).ToList();
        var paid = plan.Status == LoanStatus.Closed ? loan.Installments.Count : plan.Paid < 0 ? due.Count : plan.Paid;
        foreach (var i in due)
        {
            i.InterestRecognized = true;
            loan.InterestReceivable += i.InterestDue;
        }
        foreach (var i in loan.Installments.OrderBy(i => i.No).Take(paid))
        {
            i.PrincipalPaid = i.PrincipalDue;
            i.InterestPaid = i.InterestDue;
            i.Status = InstallmentStatus.Paid;
            loan.OutstandingPrincipal -= i.PrincipalDue;
            loan.InterestReceivable -= i.InterestDue;
            loan.TotalInterestPaid += i.InterestDue;
        }

        if (plan.Status == LoanStatus.Closed)
        {
            loan.ClosedOn = loan.Installments.Max(i => i.DueDate);
            loan.Classification = LoanClass.Standard;
        }
        else
        {
            var overdue = due.Where(i => i.Status == InstallmentStatus.Due).ToList();
            loan.OverdueDays = overdue.Count == 0 ? 0 : (s.Today - overdue.Min(i => i.DueDate)).Days;
            loan.OverdueAmount = overdue.Sum(i => i.Outstanding);
            var (cls, pct) = LoanCalculator.Classify(loan.OverdueDays);
            loan.Classification = cls;
            loan.ProvisionHeld = Money.R(loan.OutstandingPrincipal * pct / 100m);
        }
        Schedule(loan, plan, date, due, paid);
    }

    private void Schedule(Loan loan, Plan plan, DateTime date, List<LoanInstallment> due, int paid)
    {
        var branch = s.Branches.First(b => b.Id == loan.BranchId);
        var settlement = loan.SettlementAccount!;
        var fee = Money.R(loan.Principal * loan.Product!.ProcessingFeePct / 100m);
        var vat = Money.R(fee * DemoState.VatRate / 100m);
        s.Enqueue(date, () => s.Post(VoucherType.LoanDisburse, branch, date, $"Loan disbursement {loan.LoanNo}",
        [
            new(loan.Product.GlAccountId, null, loan.Principal, 0, "Loan principal", loan, "Principal"),
            new(null, settlement, 0, loan.Principal - fee - vat, "Loan proceeds (net of fees)"),
            new(s.Gl["4400"], null, 0, fee, "Loan processing fee"),
            new(s.Gl["2610"], null, 0, vat, "VAT on processing fee")
        ], sourceKind: "Loan", sourceId: loan.Id, maker: DemoState.OfficerUser));

        foreach (var i in due)
        {
            var inst = i;
            s.Enqueue(inst.DueDate, () => s.Post(VoucherType.Eod, branch, inst.DueDate, $"Loan interest due recognition {loan.LoanNo} #{inst.No}",
                [new(s.Gl["1600"], null, inst.InterestDue, 0, "Interest receivable"), new(s.Gl["4100"], null, 0, inst.InterestDue, "Interest income")], system: true));
        }
        foreach (var i in loan.Installments.OrderBy(x => x.No).Take(paid))
        {
            var inst = i;
            var total = inst.PrincipalDue + inst.InterestDue;
            s.Enqueue(inst.DueDate, () => s.Post(VoucherType.LoanRepay, branch, inst.DueDate, $"Loan repayment {loan.LoanNo} #{inst.No}",
            [
                new(s.Gl["1100"], null, total, 0, "Cash received"),
                new(loan.Product.GlAccountId, null, 0, inst.PrincipalDue, "Principal repaid", loan, "Principal"),
                new(s.Gl["1600"], null, 0, inst.InterestDue, "Interest receivable settled", loan, "Interest")
            ], sourceKind: "Loan", sourceId: loan.Id));
        }
    }

    public void PostProvisions()
    {
        foreach (var g in s.Loans.Where(l => l.ProvisionHeld > 0).GroupBy(l => l.BranchId))
        {
            var branch = s.Branches.First(b => b.Id == g.Key);
            var amount = g.Sum(l => l.ProvisionHeld);
            s.Post(VoucherType.Eod, branch, s.LastMonthEnd, "Month-end loan loss provision",
                [new(s.Gl["5200"], null, amount, 0, "Provision charge"), new(s.Gl["2700"], null, 0, amount, "Provision held")], system: true);
        }
    }
}
