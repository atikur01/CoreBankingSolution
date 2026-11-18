namespace CoreBanking.Application.Features.Remittances;

public class RemittanceQueries(IApplicationDbContext db)
{
    public async Task<List<Remittance>> SearchAsync(RemittanceType? type, RemittanceStatus? status, string? q)
    {
        var query = db.Remittances.AsNoTracking().Include(r => r.Branch).Include(r => r.SourceAccount).AsQueryable();
        if (type != null) query = query.Where(r => r.Type == type);
        if (status != null) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(r => r.InstrumentNo.Contains(q) || r.BeneficiaryName.Contains(q) || r.ApplicantName.Contains(q));
        return await query.OrderByDescending(r => r.Id).Take(200).ToListAsync();
    }
}
