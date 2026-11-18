namespace CoreBanking.Application.Features.Accounts;

public record AccountLimitsRequest(decimal OdLimit, decimal SingleDebitLimit, decimal DailyDebitLimit, decimal MonthlyDebitLimit, string? PowerOfAttorney, decimal Lien);

public class AccountAdministrationService(IApplicationDbContext db)
{
    public async Task<string> SetStatusAsync(int id, AccountStatus status, string? reason)
    {
        var account = await db.Accounts.Include(x => x.Product).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BankingException("Account not found.");
        if (status is AccountStatus.PendingApproval or AccountStatus.Matured) throw new BankingException("Invalid target status.");
        if (account.Status is AccountStatus.PendingApproval or AccountStatus.Closed) throw new BankingException($"Account is {account.Status}.");
        if (account.Status == status) throw new BankingException($"Account is already {status}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("A reason is required for a status change.");
        if (status == AccountStatus.Closed)
        {
            if (account.Balance != 0 || account.AccruedInterest >= 0.005m || account.OdInterestAccrued >= 0.005m)
                throw new BankingException("Account has a balance/accrued interest; use 'Close account & pay out' instead.");
            if (await db.Loans.AnyAsync(l => l.SettlementAccountId == id && l.Status == LoanStatus.Disbursed))
                throw new BankingException("Account is the settlement account of a running loan.");
        }

        var previous = account.Status;
        var businessDate = (await db.Branches.FindAsync(account.BranchId))!.BusinessDate;
        account.Status = status;
        account.Version++;
        if (status == AccountStatus.Closed) account.ClosedOn = businessDate;
        if (status == AccountStatus.Active && previous is AccountStatus.Dormant or AccountStatus.Inoperative) account.LastTxnDate = businessDate;
        await db.SaveChangesAsync();
        return $"Status changed {previous} -> {status} ({reason}).";
    }

    public async Task UpdateLimitsAsync(int id, AccountLimitsRequest request, bool canChangeLien)
    {
        var account = await db.Accounts.Include(x => x.Product).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BankingException("Account not found.");
        if (new[] { request.OdLimit, request.SingleDebitLimit, request.DailyDebitLimit, request.MonthlyDebitLimit }.Any(v => v < 0))
            throw new BankingException("Limits cannot be negative.");
        if (request.Lien < 0 || request.Lien > account.Balance) throw new BankingException("Lien must be between 0 and the balance.");
        if (request.Lien != account.LienAmount && !canChangeLien) throw new BankingException("Only a checker can change the lien.");
        if (request.OdLimit > 0 && account.Product!.Category != ProductCategory.Current) throw new BankingException("Overdraft is allowed on current accounts only.");

        account.OdLimit = request.OdLimit;
        account.SingleDebitLimit = request.SingleDebitLimit;
        account.DailyDebitLimit = request.DailyDebitLimit;
        account.MonthlyDebitLimit = request.MonthlyDebitLimit;
        account.PowerOfAttorney = request.PowerOfAttorney;
        account.LienAmount = request.Lien;
        account.Version++;
        await db.SaveChangesAsync();
    }

    public async Task ReleaseStopPaymentAsync(int accountId, int stopId)
    {
        var stop = await db.StopPayments.FindAsync(stopId);
        if (stop == null || stop.AccountId != accountId) throw new BankingException("Stop payment not found.");
        if (!stop.IsActive) throw new BankingException("Stop payment is already released.");
        stop.IsActive = false;
        stop.ReleasedOn = DateTime.Now;
        await db.SaveChangesAsync();
    }
}
