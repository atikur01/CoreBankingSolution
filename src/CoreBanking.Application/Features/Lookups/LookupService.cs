namespace CoreBanking.Application.Features.Lookups;

public record LookupItem(int Id, string Text);

public class LookupService(IApplicationDbContext db)
{
    public async Task<List<LookupItem>> BranchesAsync() =>
        await db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code)
            .Select(b => new LookupItem(b.Id, b.Code + " - " + b.Name)).ToListAsync();

    public async Task<List<Product>> ActiveProductsAsync(bool? loans = null)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync();
        return loans == null ? products : products.Where(p => p.IsLoan == loans).ToList();
    }

    public async Task<Account> AccountByNumberAsync(string number, string? notFoundMessage = null)
    {
        var trimmed = (number ?? "").Trim();
        return await db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == trimmed)
               ?? throw new BankingException(notFoundMessage ?? $"Account {trimmed} not found.");
    }

    public async Task<int> AccountIdByNumberAsync(string number, string? notFoundMessage = null) =>
        (await AccountByNumberAsync(number, notFoundMessage)).Id;

    public async Task<int?> OptionalAccountIdAsync(string? number, string notFoundMessage) =>
        string.IsNullOrWhiteSpace(number) ? null : await AccountIdByNumberAsync(number, notFoundMessage);

    public async Task<Customer> CustomerByCifAsync(string cif, string? notFoundMessage = null)
    {
        var trimmed = (cif ?? "").Trim();
        return await db.Customers.FirstOrDefaultAsync(c => c.CifNo == trimmed)
               ?? throw new BankingException(notFoundMessage ?? "Customer CIF not found.");
    }
}
