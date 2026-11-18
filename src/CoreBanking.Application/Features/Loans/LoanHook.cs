namespace CoreBanking.Application.Features.Loans;

public class LoanHook(IApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind != "Loan" || v.SourceId is not int id) return;
        var l = await db.Loans.Include(x => x.Product).Include(x => x.Installments).FirstAsync(x => x.Id == id);
        if (v.Type == VoucherType.LoanDisburse)
        {
            if (l.Status != LoanStatus.Approved) throw new BankingException("Loan is no longer in Approved state.");
            l.Status = LoanStatus.Disbursed; l.DisbursedOn = v.TxnDate; l.OutstandingPrincipal = l.Principal;
            foreach (var r in LoanCalculator.Schedule(l.Principal, l.InterestRate, l.TermMonths, v.TxnDate, l.Product!.Category == ProductCategory.DemandLoan))
                l.Installments.Add(new LoanInstallment { No = r.No, DueDate = r.Due, PrincipalDue = r.Principal, InterestDue = r.Interest });
            l.Version++;
        }
        else if (v.Type == VoucherType.LoanRepay)
        {
            var lines = await db.VoucherLines.Where(x => x.VoucherId == v.Id && x.LoanId == l.Id).ToListAsync();
            var alloc = LoanService.Allocate(l.Installments, v.TotalAmount);
            if (alloc.Sum(a => a.Principal) != lines.Where(x => x.LoanComponent == "Principal").Sum(x => x.Credit) ||
                alloc.Where(a => a.Inst.InterestRecognized).Sum(a => a.Interest) != lines.Where(x => x.LoanComponent == "Interest").Sum(x => x.Credit))
                throw new BankingException("Loan schedule changed since this repayment was entered. Reject it and re-enter.");
            foreach (var a in alloc)
            {
                a.Inst.PrincipalPaid += a.Principal; a.Inst.InterestPaid += a.Interest;
                if (a.Inst.PrincipalPaid >= a.Inst.PrincipalDue && a.Inst.InterestPaid >= a.Inst.InterestDue) a.Inst.Status = InstallmentStatus.Paid;
            }
            l.OutstandingPrincipal -= alloc.Sum(a => a.Principal);
            l.InterestReceivable -= alloc.Where(a => a.Inst.InterestRecognized).Sum(a => a.Interest);
            l.TotalInterestPaid += alloc.Sum(a => a.Interest);
            if (l.Installments.All(i => i.Status == InstallmentStatus.Paid)) { l.Status = LoanStatus.Closed; l.ClosedOn = v.TxnDate; l.OverdueDays = 0; l.OverdueAmount = 0; l.Classification = LoanClass.Standard; }
            l.Version++;
        }
    }

    public Task OnRejectedAsync(Voucher v) => Task.CompletedTask;
}
