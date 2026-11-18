using System.Security.Claims;
using CoreBankingSolution.Data;
using CoreBankingSolution.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Services;

/// <summary>Information about the signed-in banker (profile, branch, role level).</summary>
public class UserContext(IHttpContextAccessor http, ApplicationDbContext db)
{
    private UserProfile? _profile; private bool _loaded;
    public ClaimsPrincipal? Principal => http.HttpContext?.User;
    public string UserName => Principal?.Identity?.Name ?? "SYSTEM";
    public string? UserId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    public bool IsManagerLevel => Principal?.IsInRole(Roles.Manager) == true || Principal?.IsInRole(Roles.Administrator) == true;
    public string? Ip => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public async Task<UserProfile?> ProfileAsync()
    {
        if (_loaded) return _profile;
        _loaded = true;
        if (UserId != null) _profile = await db.UserProfiles.Include(p => p.Branch).FirstOrDefaultAsync(p => p.UserId == UserId);
        return _profile;
    }

    public async Task<int> BranchIdAsync() => (await ProfileAsync())?.BranchId ?? (await db.Branches.OrderBy(b => b.Id).Select(b => b.Id).FirstAsync());
}

/// <summary>Forces password change at first login / after expiry and enforces operating hours & terminal restrictions.</summary>
public class PasswordPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ApplicationDbContext db, ParamService prm)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var path = ctx.Request.Path.Value ?? "";
            bool exempt = path.StartsWith("/Password", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/Identity/Account/Logout", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/lib") || path.StartsWith("/css") || path.StartsWith("/js") || path.StartsWith("/favicon");
            var uid = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var p = uid == null ? null : await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
            if (p != null && !exempt)
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString();
                var blocked = !p.IsActive
                    || (!string.IsNullOrWhiteSpace(p.AllowedIp) && p.AllowedIp != ip && ip != "::1" && ip != "127.0.0.1")
                    || (p.WorkFrom != null && p.WorkTo != null && (DateTime.Now.TimeOfDay < p.WorkFrom || DateTime.Now.TimeOfDay > p.WorkTo));
                if (blocked)
                {
                    await ctx.SignOutAsync(IdentityConstants.ApplicationScheme);
                    ctx.Response.Redirect("/Identity/Account/Login?denied=1");
                    return;
                }
                var days = await prm.IntAsync("PasswordExpiryDays");
                if (p.MustChangePassword || (days > 0 && (DateTime.Now - p.PasswordChangedOn).TotalDays > days))
                {
                    ctx.Response.Redirect("/Password/Change?forced=1");
                    return;
                }
            }
        }
        await next(ctx);
    }
}
