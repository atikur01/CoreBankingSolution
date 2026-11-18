using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace CoreBanking.Infrastructure.Identity;

public class CurrentUser(IHttpContextAccessor http, IApplicationDbContext db) : ICurrentUser
{
    private UserProfile? _profile;
    private bool _loaded;

    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public string UserName => Principal?.Identity?.Name ?? "SYSTEM";
    public string? UserId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    public bool IsManagerLevel => IsInRole(Roles.Manager) || IsInRole(Roles.Administrator);
    public bool IsChecker => IsInRole(Roles.Administrator) || IsInRole(Roles.Manager) || IsInRole(Roles.Supervisor);
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;

    public async Task<UserProfile?> ProfileAsync()
    {
        if (_loaded) return _profile;
        _loaded = true;
        if (UserId != null) _profile = await db.UserProfiles.Include(p => p.Branch).FirstOrDefaultAsync(p => p.UserId == UserId);
        return _profile;
    }

    public async Task<int> BranchIdAsync() =>
        (await ProfileAsync())?.BranchId ?? await db.Branches.OrderBy(b => b.Id).Select(b => b.Id).FirstAsync();
}
