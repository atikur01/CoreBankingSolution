namespace CoreBanking.Application.Features.Accounts;

public record AccountDetails(
    Account Account, List<AccountTransaction> Transactions, List<ChequeBook> Books, List<StopPayment> Stops,
    List<ChequeReturn> Returns, List<Voucher> PendingVouchers, List<StandingInstruction> StandingInstructions);

public record AccountStatement(Account Account, DateTime From, DateTime To, decimal Opening, List<AccountTransaction> Rows);

public record AccountLookup(bool Found, string? Title = null, string? Product = null, string? Status = null, string? Balance = null, string? Branch = null);

public class AccountQueries(IApplicationDbContext db)
{
    public async Task<List<Account>> SearchAsync(string? q, AccountStatus? status, int? branchId)
    {
        var query = db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Include(a => a.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a => a.AccountNo.Contains(q) || a.Title.Contains(q) || a.Customer!.CifNo.Contains(q) || a.Customer.Mobile.Contains(q));
        if (status != null) query = query.Where(a => a.Status == status);
        if (branchId != null) query = query.Where(a => a.BranchId == branchId);
        return await query.OrderByDescending(a => a.Id).Take(200).ToListAsync();
    }

    public async Task<AccountLookup> LookupAsync(string number)
    {
        var trimmed = (number ?? "").Trim();
        var a = await db.Accounts.AsNoTracking().Include(x => x.Product).Include(x => x.Branch).FirstOrDefaultAsync(x => x.AccountNo == trimmed);
        return a == null
            ? new AccountLookup(false)
            : new AccountLookup(true, a.Title, a.Product!.Name, a.Status.ToString(), a.Balance.ToString("N2"), a.Branch!.Code);
    }

    public async Task<AccountDetails?> GetDetailsAsync(int id)
    {
        var account = await db.Accounts.Include(x => x.Product).Include(x => x.Customer).Include(x => x.Branch)
            .Include(x => x.Holders).ThenInclude(h => h.Customer).Include(x => x.Nominees).FirstOrDefaultAsync(x => x.Id == id);
        if (account == null) return null;
        return new AccountDetails(
            account,
            await db.AccountTransactions.AsNoTracking().Include(t => t.Voucher).Where(t => t.AccountId == id).OrderByDescending(t => t.Id).Take(15).ToListAsync(),
            await db.ChequeBooks.AsNoTracking().Where(b => b.AccountId == id).ToListAsync(),
            await db.StopPayments.AsNoTracking().Where(s => s.AccountId == id).OrderByDescending(s => s.Id).ToListAsync(),
            await db.ChequeReturns.AsNoTracking().Where(s => s.AccountId == id).OrderByDescending(s => s.Id).ToListAsync(),
            await db.Vouchers.AsNoTracking().Where(v => v.Status == VoucherStatus.Pending && v.Lines.Any(l => l.AccountId == id)).ToListAsync(),
            await db.StandingInstructions.AsNoTracking().Include(s => s.ToAccount).Where(s => s.FromAccountId == id).ToListAsync());
    }

    public async Task<AccountStatement?> GetStatementAsync(int id, DateTime? from, DateTime? to)
    {
        var account = await db.Accounts.Include(x => x.Product).Include(x => x.Customer).Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (account == null) return null;
        var start = (from ?? account.Branch!.BusinessDate.AddMonths(-1)).Date;
        var end = (to ?? account.Branch!.BusinessDate).Date;
        var opening = await db.AccountTransactions.Where(x => x.AccountId == id && x.TxnDate < start)
            .OrderByDescending(x => x.Id).Select(x => (decimal?)x.RunningBalance).FirstOrDefaultAsync() ?? 0;
        var rows = await db.AccountTransactions.AsNoTracking().Include(x => x.Voucher)
            .Where(x => x.AccountId == id && x.TxnDate >= start && x.TxnDate <= end).OrderBy(x => x.Id).ToListAsync();
        return new AccountStatement(account, start, end, opening, rows);
    }
}
