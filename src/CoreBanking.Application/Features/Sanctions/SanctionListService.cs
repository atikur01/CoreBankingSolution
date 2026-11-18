namespace CoreBanking.Application.Features.Sanctions;

public class SanctionListService(IApplicationDbContext db, SanctionService screening)
{
    public Task<List<SanctionEntry>> ListAsync() =>
        db.SanctionEntries.AsNoTracking().OrderByDescending(s => s.Id).Take(500).ToListAsync();

    public async Task AddAsync(SanctionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) throw new BankingException("Name is required.");
        entry.Id = 0;
        entry.AddedOn = DateTime.Now;
        db.SanctionEntries.Add(entry);
        await db.SaveChangesAsync();
    }

    public async Task ToggleAsync(int id)
    {
        var entry = await db.SanctionEntries.FindAsync(id);
        if (entry == null) return;
        entry.IsActive = !entry.IsActive;
        await db.SaveChangesAsync();
    }

    public async Task<List<SanctionService.Hit>> CheckAsync(string? names)
    {
        var inputs = (names ?? "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await screening.ScreenAsync(inputs);
    }
}
