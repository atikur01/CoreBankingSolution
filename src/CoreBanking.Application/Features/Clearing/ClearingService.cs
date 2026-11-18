namespace CoreBanking.Application.Features.Clearing;

public class ClearingService(IApplicationDbContext db, LedgerService ledger, DepositService deposits, ParamService prm)
{
    public Task<List<ClearingItem>> SearchAsync(ClearingDirection? direction, ClearingStatus? status)
    {
        var query = db.ClearingItems.AsNoTracking().Include(c => c.Account).AsQueryable();
        if (direction != null) query = query.Where(c => c.Direction == direction);
        if (status != null) query = query.Where(c => c.Status == status);
        return query.OrderByDescending(c => c.Id).Take(200).ToListAsync();
    }

    public async Task<ClearingItem> RegisterAsync(ClearingItem c, string maker, UserProfile? p)
    {
        var a = await db.Accounts.FindAsync(c.AccountId) ?? throw new BankingException("Account not found.");
        if (c.Amount <= 0) throw new BankingException("Amount must be positive.");
        if (await db.ClearingItems.AnyAsync(x => x.InstrumentNo == c.InstrumentNo && x.RoutingNo == c.RoutingNo && x.Direction == c.Direction && x.Status != ClearingStatus.Returned))
            throw new BankingException("This instrument is already registered in clearing.");
        c.BranchId = p?.BranchId ?? a.BranchId;
        c.RegisteredOn = (await db.Branches.FindAsync(c.BranchId))!.BusinessDate;
        c.Status = ClearingStatus.Registered; c.MadeBy = maker; c.Id = 0;
        db.ClearingItems.Add(c); await db.SaveChangesAsync();
        return c;
    }

    private async Task EnsureNoPendingSettlementAsync(ClearingItem c)
    {
        if (c.VoucherId is int vid && await db.Vouchers.AnyAsync(v => v.Id == vid && v.Status == VoucherStatus.Pending))
            throw new BankingException("Settlement voucher is pending authorization; authorize or reject it first.");
    }

    public async Task<Voucher> SettleAsync(int id, string maker, UserProfile? p)
    {
        var c = await db.ClearingItems.Include(x => x.Account).FirstAsync(x => x.Id == id);
        if (c.Status != ClearingStatus.Registered) throw new BankingException($"Item is {c.Status}.");
        await EnsureNoPendingSettlementAsync(c);
        var gl = await prm.GlIdAsync("ClearingGl");
        var req = new VoucherReq
        {
            Type = VoucherType.Clearing, BranchId = c.BranchId, Maker = maker, MakerProfile = p,
            Description = $"{c.Direction} clearing settlement {c.InstrumentNo} ({c.BankName})", SourceKind = "Clearing", SourceId = c.Id,
            ChequeNo = c.Direction == ClearingDirection.Inward ? c.InstrumentNo : null
        };
        if (c.Direction == ClearingDirection.Outward)
        {
            req.Dr(gl, null, c.Amount, "Clearing settlement", c.BranchId);
            req.Cr(null, c.AccountId, c.Amount, $"Outward clearing cheque {c.InstrumentNo}");
        }
        else
        {
            req.Dr(null, c.AccountId, c.Amount, $"Inward clearing cheque {c.InstrumentNo}");
            req.Cr(gl, null, c.Amount, "Clearing settlement", c.BranchId);
        }
        var v = await ledger.CreateAsync(req);
        c.VoucherId = v.Id; await db.SaveChangesAsync();
        return v;
    }

    public async Task ReturnAsync(int id, string reason, string maker, UserProfile? p)
    {
        var c = await db.ClearingItems.FindAsync(id) ?? throw new BankingException("Item not found.");
        if (c.Status != ClearingStatus.Registered) throw new BankingException($"Item is {c.Status}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("Return reason is required.");
        await EnsureNoPendingSettlementAsync(c);
        c.Status = ClearingStatus.Returned; c.ReturnReason = reason; c.SettledOn = (await db.Branches.FindAsync(c.BranchId))!.BusinessDate;
        await db.SaveChangesAsync();
        if (c.Direction == ClearingDirection.Inward)
            await deposits.ReturnChequeAsync(c.AccountId, c.InstrumentNo, c.Amount, reason, chargeFee: true, maker, p);
    }
}
