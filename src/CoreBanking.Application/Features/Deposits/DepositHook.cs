namespace CoreBanking.Application.Features.Deposits;

/// <summary>Activates / closes accounts when their opening or closing vouchers are authorised or rejected.</summary>
public class DepositHook(IApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind is not ("Account" or "AccountClose") || v.SourceId is not int id) return;
        var a = await db.Accounts.Include(x => x.Product).FirstAsync(x => x.Id == id);
        switch (v.Type)
        {
            case VoucherType.AccountOpening:
            case VoucherType.FdrOpen:
                a.Status = AccountStatus.Active; a.OpenedOn = v.TxnDate; a.CheckedBy = v.CheckerId; a.Version++;
                if (a.Product!.Category == ProductCategory.FDR) { a.PrincipalAmount = a.Balance; a.MaturityDate = v.TxnDate.AddMonths(a.TermMonths); }
                if (a.Product.Category == ProductCategory.DPS) { a.InstallmentsPaid = 1; a.MaturityDate = v.TxnDate.AddMonths(a.TermMonths); }
                break;
            case VoucherType.DpsInstallment:
                a.InstallmentsPaid++; a.Version++;
                break;
            case VoucherType.Encash:
            case VoucherType.CashWithdrawal when v.SourceKind == "AccountClose":
                if (a.Status == AccountStatus.Closed) throw new BankingException($"Account {a.AccountNo} is already closed.");
                a.Status = AccountStatus.Closed; a.ClosedOn = v.TxnDate; a.AccruedInterest = 0; a.Version++;
                break;
        }
    }

    public async Task OnRejectedAsync(Voucher v)
    {
        if (v.SourceKind != "Account" || v.SourceId is not int id) return;
        if (v.Type is VoucherType.AccountOpening or VoucherType.FdrOpen)
        {
            var a = await db.Accounts.FindAsync(id);
            if (a is { Status: AccountStatus.PendingApproval }) { a.Status = AccountStatus.Closed; a.ClosedOn = v.TxnDate; a.Title += " (REJECTED)"; }
        }
    }
}
