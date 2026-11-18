namespace CoreBanking.Application.Features.Users;

public enum AccessDecision { Allowed, SignOut, ChangePassword }

public class UserAccessPolicy(IApplicationDbContext db, ParamService prm)
{
    public async Task<AccessDecision> EvaluateAsync(string? userId, string? ip, DateTime now)
    {
        if (userId == null) return AccessDecision.Allowed;
        var profile = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId);
        if (profile == null) return AccessDecision.Allowed;
        if (IsBlocked(profile, ip, now.TimeOfDay)) return AccessDecision.SignOut;

        var expiryDays = await prm.IntAsync("PasswordExpiryDays");
        var expired = expiryDays > 0 && (now - profile.PasswordChangedOn).TotalDays > expiryDays;
        return profile.MustChangePassword || expired ? AccessDecision.ChangePassword : AccessDecision.Allowed;
    }

    public static bool IsBlocked(UserProfile profile, string? ip, TimeSpan timeOfDay)
    {
        if (!profile.IsActive) return true;
        var isLoopback = ip is "::1" or "127.0.0.1";
        if (!string.IsNullOrWhiteSpace(profile.AllowedIp) && profile.AllowedIp != ip && !isLoopback) return true;
        return profile.WorkFrom != null && profile.WorkTo != null && (timeOfDay < profile.WorkFrom || timeOfDay > profile.WorkTo);
    }
}
