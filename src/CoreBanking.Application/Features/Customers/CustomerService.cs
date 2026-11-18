namespace CoreBanking.Application.Features.Customers;

public record ValidationIssue(string Field, string Message);

public record CustomerDetails(Customer Customer, List<Account> Accounts, List<Loan> Loans);

public record CustomerFile(string Path, string ContentType);

public class CustomerService(IApplicationDbContext db, NumberService numbers, SanctionService sanctions, IFileStore files)
{
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".pdf"];
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const string ScreeningClear = "Clear (UNSCR + local list)";

    public async Task<List<Customer>> SearchAsync(string? q, KycStatus? kyc)
    {
        var query = db.Customers.AsNoTracking().Include(c => c.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(c => c.Name.Contains(q) || c.CifNo.Contains(q) || (c.Nid != null && c.Nid.Contains(q)) || c.Mobile.Contains(q));
        if (kyc != null) query = query.Where(c => c.Kyc == kyc);
        return await query.OrderByDescending(c => c.Id).Take(200).ToListAsync();
    }

    public Task<Customer?> FindAsync(int id) => db.Customers.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<CustomerDetails?> GetDetailsAsync(int id)
    {
        var customer = await db.Customers.Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (customer == null) return null;
        var accounts = await db.Accounts.AsNoTracking().Include(a => a.Product)
            .Where(a => a.CustomerId == id || a.Holders.Any(h => h.CustomerId == id)).OrderBy(a => a.AccountNo).ToListAsync();
        var loans = await db.Loans.AsNoTracking().Include(l => l.Product).Where(l => l.CustomerId == id).ToListAsync();
        return new CustomerDetails(customer, accounts, loans);
    }

    public async Task<List<ValidationIssue>> ValidateAsync(Customer customer, int? excludeId = null)
    {
        var issues = new List<ValidationIssue>();
        var personal = customer.Type is CustomerType.Individual or CustomerType.Minor or CustomerType.Joint or CustomerType.Staff or CustomerType.NonResident;
        if (!personal && string.IsNullOrWhiteSpace(customer.TradeLicense))
            issues.Add(new("TradeLicense", "Registration / trade licence number is required for organisations."));
        if (customer.Type is CustomerType.Individual or CustomerType.Staff && string.IsNullOrWhiteSpace(customer.Nid) && string.IsNullOrWhiteSpace(customer.Passport))
            issues.Add(new("Nid", "NID or passport number is required."));
        if (!string.IsNullOrWhiteSpace(customer.Nid) && await db.Customers.AnyAsync(c => c.Nid == customer.Nid && c.Id != excludeId))
            issues.Add(new("Nid", "A customer with this NID already exists (duplicate CIF check)."));
        return issues;
    }

    public async Task<(Customer Customer, bool SanctionHit)> CreateAsync(
        Customer customer, UploadedFile? photo, UploadedFile? signature, UploadedFile? document, string maker)
    {
        var branch = await db.Branches.FindAsync(customer.BranchId) ?? throw new BankingException("Branch not found.");
        customer.PhotoPath = await StoreAsync(photo);
        customer.SignaturePath = await StoreAsync(signature);
        customer.DocumentPath = await StoreAsync(document);

        customer.Id = 0;
        customer.CifNo = await numbers.NextCifAsync(branch.Code);
        customer.MadeBy = maker;
        customer.Kyc = KycStatus.Pending;
        customer.CreatedOn = DateTime.Now;
        var hits = await sanctions.ScreenAsync([customer.Name, customer.Nid, customer.Passport, customer.FatherName, customer.IntroducerName]);
        customer.ScreeningResult = hits.Count == 0 ? ScreeningClear : "HIT: " + SanctionService.Format(hits);
        if (hits.Count > 0) customer.Risk = RiskRating.High;
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return (customer, hits.Count > 0);
    }

    public async Task<bool> UpdateAsync(int id, Customer m, UploadedFile? photo, UploadedFile? signature, UploadedFile? document, string maker)
    {
        var c = await db.Customers.FindAsync(id) ?? throw new BankingException("Customer not found.");
        var keyChange = c.Name != m.Name || c.Nid != m.Nid || c.Passport != m.Passport || c.DateOfBirth != m.DateOfBirth;
        if (photo?.Length > 0) c.PhotoPath = await StoreAsync(photo);
        if (signature?.Length > 0) c.SignaturePath = await StoreAsync(signature);
        if (document?.Length > 0) c.DocumentPath = await StoreAsync(document);

        c.Type = m.Type; c.Name = m.Name; c.FatherName = m.FatherName; c.MotherName = m.MotherName; c.SpouseName = m.SpouseName;
        c.DateOfBirth = m.DateOfBirth; c.Gender = m.Gender; c.Nid = m.Nid; c.Passport = m.Passport; c.Tin = m.Tin;
        c.TradeLicense = m.TradeLicense; c.Mobile = m.Mobile; c.Email = m.Email; c.Address = m.Address;
        c.PermanentAddress = m.PermanentAddress; c.Profession = m.Profession; c.Sector = m.Sector; c.SourceOfFunds = m.SourceOfFunds;
        c.ContactPerson = m.ContactPerson; c.MonthlyIncome = m.MonthlyIncome; c.IntroducerName = m.IntroducerName;
        c.IntroducerAccount = m.IntroducerAccount; c.Risk = m.Risk; c.IsPep = m.IsPep; c.IsActive = m.IsActive;
        if (keyChange) { c.Kyc = KycStatus.Pending; c.CheckedBy = null; }
        c.MadeBy = maker;
        await db.SaveChangesAsync();
        return keyChange;
    }

    public async Task<string> VerifyKycAsync(int id, bool approve, string? remarks, string checker)
    {
        var c = await db.Customers.FindAsync(id) ?? throw new BankingException("Customer not found.");
        if (string.Equals(c.MadeBy, checker, StringComparison.OrdinalIgnoreCase))
            throw new BankingException("Maker-checker: you cannot verify the KYC you entered.");
        if (!approve)
        {
            c.Kyc = KycStatus.Rejected;
            c.CheckedBy = checker;
            await db.SaveChangesAsync();
            return "KYC rejected: " + remarks;
        }

        var hits = await sanctions.ScreenAsync([c.Name, c.Nid, c.Passport]);
        if (hits.Count > 0)
        {
            c.ScreeningResult = "HIT: " + SanctionService.Format(hits);
            await db.SaveChangesAsync();
            throw new BankingException("KYC cannot be approved - sanction screening hit: " + SanctionService.Format(hits));
        }
        if (string.IsNullOrWhiteSpace(c.PhotoPath) && c.Type is CustomerType.Individual or CustomerType.Minor)
            throw new BankingException("Customer photograph is required for KYC.");

        c.Kyc = KycStatus.Verified;
        c.CheckedBy = checker;
        c.ScreeningResult = ScreeningClear;
        await db.SaveChangesAsync();
        return "KYC verified.";
    }

    public async Task<List<SanctionService.Hit>> RescreenAsync(int id)
    {
        var c = await db.Customers.FindAsync(id) ?? throw new BankingException("Customer not found.");
        var hits = await sanctions.ScreenAsync([c.Name, c.Nid, c.Passport, c.FatherName]);
        c.ScreeningResult = hits.Count == 0 ? ScreeningClear : "HIT: " + SanctionService.Format(hits);
        if (hits.Count > 0) c.Risk = RiskRating.High;
        await db.SaveChangesAsync();
        return hits;
    }

    public async Task<CustomerFile?> GetFileAsync(int id, string kind)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        var name = kind switch { "photo" => c?.PhotoPath, "signature" => c?.SignaturePath, "doc" => c?.DocumentPath, _ => null };
        var path = string.IsNullOrEmpty(name) ? null : files.ResolvePath(name);
        if (path == null) return null;
        var contentType = Path.GetExtension(path).ToLowerInvariant() switch { ".pdf" => "application/pdf", ".png" => "image/png", _ => "image/jpeg" };
        return new CustomerFile(path, contentType);
    }

    private async Task<string?> StoreAsync(UploadedFile? file)
    {
        if (file == null || file.Length == 0) return null;
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) throw new BankingException("Only JPG, PNG or PDF files are allowed.");
        if (file.Length > MaxFileBytes) throw new BankingException("File too large (max 2 MB).");
        return await files.SaveAsync(file, extension);
    }
}
