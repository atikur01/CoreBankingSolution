namespace CoreBanking.Application.Features.Eod;

public record EodOverview(List<Branch> Branches, Dictionary<int, List<string>> Checks, List<EodLog> Logs);

public class EodQueries(IApplicationDbContext db, EodService eod)
{
    public async Task<EodOverview> GetAsync(int? branchId)
    {
        var branches = await db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).ToListAsync();
        var checks = new Dictionary<int, List<string>>();
        foreach (var branch in branches) checks[branch.Id] = await eod.PreChecksAsync(branch.Id);

        var logs = db.EodLogs.AsNoTracking().Include(l => l.Branch).AsQueryable();
        if (branchId != null) logs = logs.Where(l => l.BranchId == branchId);
        return new EodOverview(branches, checks, await logs.OrderByDescending(l => l.Id).Take(80).ToListAsync());
    }
}
