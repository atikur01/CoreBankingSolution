namespace CoreBanking.Application.Features.Users;

public record UserListItem(string Id, string UserName, string? Email, UserProfile? Profile, string Roles, bool Locked);

public class UserInput
{
    public string? Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? EmployeeId { get; set; }
    public string? Designation { get; set; }
    public string? PrimeMenu { get; set; }
    public int BranchId { get; set; }
    public string Role { get; set; } = Roles.Teller;
    public string? AllowedIp { get; set; }
    public TimeSpan? WorkFrom { get; set; }
    public TimeSpan? WorkTo { get; set; }
    public decimal MaxCredit { get; set; }
    public decimal MaxDebit { get; set; }
    public decimal MaxInterBranchCredit { get; set; }
    public decimal MaxInterBranchDebit { get; set; }
    public bool IsActive { get; set; } = true;
}

public record TemporaryCredential(string UserName, string Password);

public interface IUserAdministration
{
    Task<List<UserListItem>> ListAsync();
    Task<(string UserName, UserInput Input)?> GetAsync(string id);
    Task<TemporaryCredential> CreateAsync(UserInput input);
    Task UpdateAsync(string id, UserInput input, string actingUserId);
    Task<TemporaryCredential> ResetPasswordAsync(string id);
    Task UnlockAsync(string id);
}

public interface IPasswordService
{
    Task ChangeAsync(string userId, string currentPassword, string newPassword);
}

public record AuditFilter(string? User, string? Entity, string? Query, DateTime? From, DateTime? To);

public record AuditPage(DateTime From, DateTime To, List<AuditLog> Rows);

public class AuditQueries(IApplicationDbContext db)
{
    public async Task<AuditPage> SearchAsync(AuditFilter filter)
    {
        var from = (filter.From ?? DateTime.Today.AddDays(-3)).Date;
        var to = (filter.To ?? DateTime.Today).Date;
        var end = to.AddDays(1);
        var query = db.AuditLogs.AsNoTracking().Where(a => a.At >= from && a.At < end);
        if (!string.IsNullOrWhiteSpace(filter.User)) query = query.Where(a => a.UserName!.Contains(filter.User));
        if (!string.IsNullOrWhiteSpace(filter.Entity)) query = query.Where(a => a.Entity == filter.Entity);
        if (!string.IsNullOrWhiteSpace(filter.Query)) query = query.Where(a => a.EntityKey == filter.Query || a.Details!.Contains(filter.Query));
        return new AuditPage(from, to, await query.OrderByDescending(a => a.Id).Take(500).ToListAsync());
    }
}
