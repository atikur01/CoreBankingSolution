namespace CoreBanking.Application.Features.Ledger;

public record VoucherDetails(Voucher Voucher, string? CurrencyCode, Voucher? Reversal, Voucher? ReversedBy);

public record VoucherFilter(DateTime? From, DateTime? To, VoucherStatus? Status, VoucherType? Type, string? Query, int? BranchId);

public record VoucherRegister(DateTime From, DateTime To, List<Voucher> Rows);

public record JournalLineRequest(string? GlCode, int? BranchId, decimal Debit, decimal Credit, string? Narration);

public record JournalRequest(string Description, DateTime? ValueDate, List<JournalLineRequest> Lines);

public class VoucherQueries(IApplicationDbContext db)
{
    public async Task<List<Voucher>> PendingAsync(int? branchId)
    {
        var query = db.Vouchers.AsNoTracking().Include(v => v.Branch).Where(v => v.Status == VoucherStatus.Pending);
        if (branchId != null) query = query.Where(v => v.BranchId == branchId);
        return await query.OrderBy(v => v.Id).ToListAsync();
    }

    public async Task<VoucherDetails?> GetDetailsAsync(int id)
    {
        var voucher = await db.Vouchers.Include(x => x.Branch).Include(x => x.Lines).ThenInclude(l => l.GlAccount)
            .Include(x => x.Lines).ThenInclude(l => l.Account).FirstOrDefaultAsync(x => x.Id == id);
        if (voucher == null) return null;
        return new VoucherDetails(
            voucher,
            (await db.Currencies.FindAsync(voucher.CurrencyId))?.Code,
            voucher.ReversalOfId == null ? null : await db.Vouchers.FindAsync(voucher.ReversalOfId),
            voucher.ReversedById == null ? null : await db.Vouchers.FindAsync(voucher.ReversedById));
    }

    public async Task<VoucherRegister> SearchAsync(VoucherFilter filter)
    {
        var from = (filter.From ?? DateTime.Today.AddDays(-7)).Date;
        var to = (filter.To ?? DateTime.Today.AddYears(1)).Date;
        var query = db.Vouchers.AsNoTracking().Include(v => v.Branch).Where(v => v.TxnDate >= from && v.TxnDate <= to);
        if (filter.Status != null) query = query.Where(v => v.Status == filter.Status);
        if (filter.Type != null) query = query.Where(v => v.Type == filter.Type);
        if (filter.BranchId != null) query = query.Where(v => v.BranchId == filter.BranchId);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query;
            query = query.Where(v => v.VoucherNo.Contains(q) || v.Description.Contains(q) || v.MakerId.Contains(q) || (v.ChequeNo != null && v.ChequeNo.Contains(q)));
        }
        return new VoucherRegister(from, to, await query.OrderByDescending(v => v.Id).Take(500).ToListAsync());
    }
}

public class JournalService(IApplicationDbContext db, LedgerService ledger)
{
    public async Task<Voucher> CreateAsync(JournalRequest request, int branchId, string maker, UserProfile? profile)
    {
        var req = new VoucherReq
        {
            Type = VoucherType.Journal, BranchId = branchId, Description = request.Description, ValueDate = request.ValueDate,
            Maker = maker, MakerProfile = profile
        };
        foreach (var line in request.Lines.Where(x => !string.IsNullOrWhiteSpace(x.GlCode)))
        {
            var code = line.GlCode!.Trim();
            var gl = await db.GlAccounts.FirstOrDefaultAsync(g => g.Code == code) ?? throw new BankingException($"GL {line.GlCode} not found.");
            if (await db.Products.AnyAsync(p => p.GlAccountId == gl.Id))
                throw new BankingException($"GL {gl.Code} is a customer-account control GL; use deposit/withdrawal/transfer functions instead.");
            req.Lines.Add(new LineReq(gl.Id, null, line.Debit, line.Credit, line.Narration ?? request.Description, line.BranchId));
        }
        return await ledger.CreateAsync(req);
    }
}
