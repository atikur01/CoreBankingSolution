namespace CoreBanking.Application.Features.Deposits;

public class RateService(IApplicationDbContext db)
{
    /// <summary>Latest approved slab effective on the date (supports back-dated rates), else product default.</summary>
    public async Task<decimal> GetRateAsync(int productId, decimal amount, int termMonths, DateTime date)
    {
        var r = await db.ProductRates.AsNoTracking()
            .Where(x => x.ProductId == productId && x.Status == ApprovalStatus.Approved && x.EffectiveFrom <= date
                        && x.MinAmount <= amount && (x.MaxAmount == 0 || x.MaxAmount >= amount)
                        && x.MinTermMonths <= termMonths && (x.MaxTermMonths == 0 || x.MaxTermMonths >= termMonths))
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        if (r != null) return r.Rate;
        return await db.Products.Where(p => p.Id == productId).Select(p => p.InterestRate).FirstAsync();
    }
}
