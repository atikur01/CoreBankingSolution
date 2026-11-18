namespace CoreBanking.Application.Features.Transactions;

/// <summary>Teller / operations level transactions built on the ledger engine.</summary>
public class TransactionService(IApplicationDbContext db, LedgerService ledger, ParamService prm, SanctionService sanctions)
{
    private async Task<int> TellerBranch(UserProfile? p, Account a) => p?.BranchId ?? a.BranchId;

    private static void RequirePositive(decimal amount)
    {
        if (Money.R(amount) <= 0) throw new BankingException("Amount must be greater than zero.");
    }

    public async Task<Voucher> DepositAsync(int accountId, decimal amount, string? depositor, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        RequirePositive(amount);
        var a = await db.Accounts.FindAsync(accountId) ?? throw new BankingException("Account not found.");
        if (!string.IsNullOrWhiteSpace(depositor))
        {
            var hits = await sanctions.ScreenAsync([depositor]);
            if (hits.Count > 0) throw new BankingException("Sanction screening hit on depositor: " + SanctionService.Format(hits));
        }
        var br = await TellerBranch(p, a);
        var req = new VoucherReq
        {
            Type = VoucherType.CashDeposit, BranchId = br, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Cash deposit {a.AccountNo}", CounterParty = depositor
        };
        req.Dr(await prm.GlIdAsync("CashGl"), null, amount, "Cash received", br);
        req.Cr(null, accountId, amount, narration ?? "Cash deposit");
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> WithdrawAsync(int accountId, decimal amount, string? chequeNo, string? withdrawer, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        RequirePositive(amount);
        var a = await db.Accounts.FindAsync(accountId) ?? throw new BankingException("Account not found.");
        if (!string.IsNullOrWhiteSpace(withdrawer))
        {
            var hits = await sanctions.ScreenAsync([withdrawer]);
            if (hits.Count > 0) throw new BankingException("Sanction screening hit on bearer/beneficiary: " + SanctionService.Format(hits));
        }
        var br = await TellerBranch(p, a);
        var req = new VoucherReq
        {
            Type = VoucherType.CashWithdrawal, BranchId = br, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Cash withdrawal {a.AccountNo}", ChequeNo = string.IsNullOrWhiteSpace(chequeNo) ? null : chequeNo.Trim(), CounterParty = withdrawer
        };
        req.Dr(null, accountId, amount, narration ?? "Cash withdrawal");
        req.Cr(await prm.GlIdAsync("CashGl"), null, amount, "Cash paid", br);
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> TransferAsync(int fromId, int toId, decimal amount, string? chequeNo, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        RequirePositive(amount);
        if (fromId == toId) throw new BankingException("Source and destination accounts must differ.");
        var from = await db.Accounts.FindAsync(fromId) ?? throw new BankingException("Source account not found.");
        var to = await db.Accounts.FindAsync(toId) ?? throw new BankingException("Destination account not found.");
        var hits = await sanctions.ScreenAsync([to.Title]);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit on beneficiary: " + SanctionService.Format(hits));
        var req = new VoucherReq
        {
            Type = VoucherType.Transfer, BranchId = p?.BranchId ?? from.BranchId, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Transfer {from.AccountNo} -> {to.AccountNo}", ChequeNo = string.IsNullOrWhiteSpace(chequeNo) ? null : chequeNo.Trim(), CounterParty = to.Title
        };
        req.Dr(null, fromId, amount, $"Transfer to {to.AccountNo}: {narration}");
        req.Cr(null, toId, amount, $"Transfer from {from.AccountNo}: {narration}");
        return await ledger.CreateAsync(req);
    }
}
