namespace CoreBanking.Application.Features.Setup;

public record ChartRow(GlAccount Account, int Depth);

public record CurrencyPage(List<Currency> Currencies, List<ExchangeRate> Rates, List<Currency> ForeignCurrencies);

public class SetupService(IApplicationDbContext db)
{
    public Task<List<Branch>> BranchesAsync() => db.Branches.Include(b => b.Parent).AsNoTracking().OrderBy(b => b.Code).ToListAsync();

    public Task<Branch?> FindBranchAsync(int id) => db.Branches.FirstOrDefaultAsync(b => b.Id == id);

    public Task<List<LookupItem>> BranchParentOptionsAsync(int? excludeId) =>
        db.Branches.Where(b => b.Id != excludeId).OrderBy(b => b.Code).Select(b => new LookupItem(b.Id, b.Code + " - " + b.Name)).ToListAsync();

    public async Task<List<ValidationIssue>> ValidateBranchAsync(Branch branch)
    {
        var issues = new List<ValidationIssue>();
        if (await db.Branches.AnyAsync(b => b.Code == branch.Code && b.Id != branch.Id)) issues.Add(new("Code", "Branch code already exists."));
        if (branch.ParentId != null && branch.ParentId == branch.Id) issues.Add(new("ParentId", "A branch cannot be its own parent."));
        return issues;
    }

    public async Task SaveBranchAsync(Branch m)
    {
        if (m.Id == 0)
        {
            m.BusinessDate = await db.Branches.MaxAsync(b => (DateTime?)b.BusinessDate) ?? DateTime.Today;
            db.Branches.Add(m);
        }
        else
        {
            var e = await db.Branches.FindAsync(m.Id) ?? throw new BankingException("Branch not found.");
            e.Name = m.Name; e.Address = m.Address; e.Phone = m.Phone; e.RoutingNumber = m.RoutingNumber; e.ParentId = m.ParentId; e.Layer = m.Layer;
            e.CostCenter = m.CostCenter; e.ProfitCenter = m.ProfitCenter; e.IsActive = m.IsActive; e.DayOpen = m.DayOpen;
        }
        await db.SaveChangesAsync();
    }

    public async Task<List<ChartRow>> ChartOfAccountsAsync()
    {
        var all = await db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();
        var byParent = all.ToLookup(g => g.ParentId);
        var rows = new List<ChartRow>();
        void Walk(int? parent, int depth)
        {
            foreach (var g in byParent[parent])
            {
                rows.Add(new ChartRow(g, depth));
                Walk(g.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return rows;
    }

    public Task<GlAccount?> FindGlAsync(int id) => db.GlAccounts.FirstOrDefaultAsync(g => g.Id == id);

    public Task<List<LookupItem>> GroupGlOptionsAsync(int? excludeId) =>
        db.GlAccounts.Where(g => g.Id != excludeId && !g.IsPosting).OrderBy(g => g.Code).Select(g => new LookupItem(g.Id, g.Code + " - " + g.Name)).ToListAsync();

    public async Task<List<ValidationIssue>> ValidateGlAsync(GlAccount gl)
    {
        var issues = new List<ValidationIssue>();
        if (await db.GlAccounts.AnyAsync(g => g.Code == gl.Code && g.Id != gl.Id)) issues.Add(new("Code", "GL code already exists."));
        if (gl.ParentId != null && gl.ParentId == gl.Id) issues.Add(new("ParentId", "A GL cannot be its own parent."));
        if (gl.Id != 0 && gl.IsPosting && await db.GlAccounts.AnyAsync(g => g.ParentId == gl.Id))
            issues.Add(new("IsPosting", "A GL with children cannot be a posting account."));
        if (gl.Id != 0 && !gl.IsPosting && await db.VoucherLines.AnyAsync(l => l.GlAccountId == gl.Id))
            issues.Add(new("IsPosting", "GL has postings; it cannot become a group."));
        return issues;
    }

    public async Task SaveGlAsync(GlAccount m)
    {
        if (m.Id == 0) db.GlAccounts.Add(m);
        else
        {
            var e = await db.GlAccounts.FindAsync(m.Id) ?? throw new BankingException("GL account not found.");
            e.Name = m.Name; e.Type = m.Type; e.ParentId = m.ParentId; e.IsPosting = m.IsPosting; e.IsActive = m.IsActive; e.Remarks = m.Remarks;
        }
        await db.SaveChangesAsync();
    }

    public Task<List<Product>> ProductsAsync() => db.Products.Include(p => p.GlAccount).AsNoTracking().OrderBy(p => p.Code).ToListAsync();

    public Task<Product?> FindProductAsync(int id) => db.Products.FirstOrDefaultAsync(p => p.Id == id);

    public Task<List<LookupItem>> PostingGlOptionsAsync() =>
        db.GlAccounts.Where(g => g.IsPosting && g.IsActive).OrderBy(g => g.Code).Select(g => new LookupItem(g.Id, g.Code + " - " + g.Name)).ToListAsync();

    public async Task<List<ValidationIssue>> ValidateProductAsync(Product product)
    {
        var issues = new List<ValidationIssue>();
        if (await db.Products.AnyAsync(p => p.Code == product.Code && p.Id != product.Id)) issues.Add(new("Code", "Product code already exists."));
        if (product.Id != 0)
        {
            var existing = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == product.Id);
            if (existing != null && existing.Category != product.Category && await db.Accounts.AnyAsync(a => a.ProductId == product.Id))
                issues.Add(new("Category", "Category cannot change once accounts exist."));
        }
        return issues;
    }

    public async Task SaveProductAsync(Product m)
    {
        if (m.Id == 0) db.Products.Add(m);
        else
        {
            var e = await db.Products.FindAsync(m.Id) ?? throw new BankingException("Product not found.");
            db.Entry(e).CurrentValues.SetValues(m);
        }
        await db.SaveChangesAsync();
    }

    public Task<List<ProductRate>> RatesAsync(int productId) =>
        db.ProductRates.AsNoTracking().Where(r => r.ProductId == productId).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).ToListAsync();

    public async Task AddRateAsync(ProductRate m, string maker)
    {
        if (m.Rate < 0 || m.Rate > 100) throw new BankingException("Rate must be between 0 and 100.");
        if (m.MaxAmount != 0 && m.MaxAmount < m.MinAmount) throw new BankingException("Amount-to must be at least amount-from.");
        if (m.MaxTermMonths != 0 && m.MaxTermMonths < m.MinTermMonths) throw new BankingException("Term-to must be at least term-from.");
        if (!await db.Products.AnyAsync(p => p.Id == m.ProductId)) throw new BankingException("Product not found.");
        m.Id = 0;
        m.Status = ApprovalStatus.Pending;
        m.MadeBy = maker;
        db.ProductRates.Add(m);
        await db.SaveChangesAsync();
    }

    public Task<ProductRate?> FindRateAsync(int id) => db.ProductRates.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);

    public async Task<ProductRate> DecideRateAsync(int id, bool approve, string checker)
    {
        var rate = await db.ProductRates.FindAsync(id) ?? throw new BankingException("Rate not found.");
        if (rate.MadeBy == checker) throw new BankingException("Maker-checker: you cannot approve a rate you entered.");
        if (rate.Status != ApprovalStatus.Pending) throw new BankingException("Rate is not pending.");
        rate.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        rate.CheckedBy = checker;
        await db.SaveChangesAsync();
        return rate;
    }

    public async Task<CurrencyPage> CurrencyPageAsync() => new(
        await db.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync(),
        await db.ExchangeRates.Include(r => r.Currency).AsNoTracking().OrderByDescending(r => r.RateDate).ThenBy(r => r.Currency!.Code).Take(60).ToListAsync(),
        await db.Currencies.AsNoTracking().Where(c => !c.IsBase).ToListAsync());

    public async Task AddCurrencyAsync(Currency m)
    {
        m.Code = (m.Code ?? "").ToUpperInvariant();
        if (m.Code.Length != 3 || string.IsNullOrWhiteSpace(m.Name)) throw new BankingException("Enter a 3-letter code and a name.");
        if (await db.Currencies.AnyAsync(c => c.Code == m.Code)) throw new BankingException("Currency exists.");
        m.Id = 0;
        m.IsBase = false;
        db.Currencies.Add(m);
        await db.SaveChangesAsync();
    }

    public async Task AddExchangeRateAsync(ExchangeRate m)
    {
        if (m.BuyRate <= 0 || m.SellRate <= 0) throw new BankingException("Rates must be positive.");
        if (!await db.Currencies.AnyAsync(c => c.Id == m.CurrencyId)) throw new BankingException("Currency not found.");
        m.Id = 0;
        db.ExchangeRates.Add(m);
        await db.SaveChangesAsync();
    }

    public Task<List<Holiday>> HolidaysAsync() => db.Holidays.AsNoTracking().OrderBy(h => h.Date).ToListAsync();

    public async Task AddHolidayAsync(Holiday m)
    {
        if (string.IsNullOrWhiteSpace(m.Description)) throw new BankingException("Description required.");
        var date = m.Date.Date;
        if (await db.Holidays.AnyAsync(h => h.Date == date)) throw new BankingException("Holiday already defined.");
        m.Id = 0;
        m.Date = date;
        db.Holidays.Add(m);
        await db.SaveChangesAsync();
    }

    public async Task DeleteHolidayAsync(int id)
    {
        var holiday = await db.Holidays.FindAsync(id);
        if (holiday == null) return;
        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync();
    }

    public async Task<List<ExciseSlab>> ExciseSlabsAsync() => (await db.ExciseSlabs.AsNoTracking().ToListAsync()).OrderBy(s => s.MinBalance).ToList();

    public async Task AddExciseSlabAsync(ExciseSlab m)
    {
        if (m.Duty <= 0) throw new BankingException("Duty must be positive.");
        if (m.MaxBalance != 0 && m.MaxBalance < m.MinBalance) throw new BankingException("Balance-to must be at least balance-from.");
        m.Id = 0;
        db.ExciseSlabs.Add(m);
        await db.SaveChangesAsync();
    }

    public async Task DeleteExciseSlabAsync(int id)
    {
        var slab = await db.ExciseSlabs.FindAsync(id);
        if (slab == null) return;
        db.ExciseSlabs.Remove(slab);
        await db.SaveChangesAsync();
    }

    public Task<List<BankParameter>> ParametersAsync() => db.BankParameters.AsNoTracking().OrderBy(p => p.Key).ToListAsync();

    public async Task SaveParametersAsync(IReadOnlyDictionary<string, string> values)
    {
        var rows = await db.BankParameters.ToListAsync();
        var proposed = rows.ToDictionary(r => r.Key, r => values.TryGetValue(r.Key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : r.Value);
        var errors = new List<string>();
        foreach (var (key, value) in proposed)
        {
            var error = ParameterRules.Validate(key, value);
            if (error == null && ParameterRules.IsGlParameter(key)
                && !await db.GlAccounts.AnyAsync(g => g.Code == value && g.IsPosting && g.IsActive))
                error = $"GL code '{value}' is not an active posting account.";
            if (error != null) errors.Add($"{key}: {error}");
        }
        errors.AddRange(ParameterRules.ValidateCombination(proposed));
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        foreach (var row in rows) row.Value = proposed[row.Key];
        await db.SaveChangesAsync();
    }
}
