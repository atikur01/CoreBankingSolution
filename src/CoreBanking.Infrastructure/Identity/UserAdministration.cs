using System.Security.Cryptography;
using CoreBanking.Application.Features.Users;
using Microsoft.AspNetCore.Identity;

namespace CoreBanking.Infrastructure.Identity;

public class UserAdministration(UserManager<IdentityUser> users, IApplicationDbContext db) : IUserAdministration
{
    public async Task<List<UserListItem>> ListAsync()
    {
        var list = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        var profiles = await db.UserProfiles.AsNoTracking().Include(p => p.Branch).ToListAsync();
        var rows = new List<UserListItem>();
        foreach (var u in list)
            rows.Add(new UserListItem(u.Id, u.UserName ?? "", u.Email, profiles.FirstOrDefault(p => p.UserId == u.Id),
                string.Join(", ", await users.GetRolesAsync(u)), await users.IsLockedOutAsync(u)));
        return rows;
    }

    public async Task<(string UserName, UserInput Input)?> GetAsync(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user == null) return null;
        var p = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id);
        var input = new UserInput
        {
            Id = id, Email = user.Email ?? "", FullName = p?.FullName ?? user.UserName!, EmployeeId = p?.EmployeeId, Designation = p?.Designation,
            PrimeMenu = p?.PrimeMenu, BranchId = p?.BranchId ?? 0, Role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? Roles.Teller,
            AllowedIp = p?.AllowedIp, WorkFrom = p?.WorkFrom, WorkTo = p?.WorkTo, MaxCredit = p?.MaxCredit ?? 0, MaxDebit = p?.MaxDebit ?? 0,
            MaxInterBranchCredit = p?.MaxInterBranchCredit ?? 0, MaxInterBranchDebit = p?.MaxInterBranchDebit ?? 0, IsActive = p?.IsActive ?? true
        };
        return (user.UserName!, input);
    }

    public async Task<TemporaryCredential> CreateAsync(UserInput input)
    {
        if (!Roles.All.Contains(input.Role)) throw new ValidationFailedException(["Invalid role."]);
        var password = TemporaryPassword();
        var user = new IdentityUser { UserName = input.Email.Trim(), Email = input.Email.Trim(), EmailConfirmed = true, LockoutEnabled = true };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) throw new ValidationFailedException(created.Errors.Select(e => e.Description).ToList());

        try
        {
            var role = await users.AddToRoleAsync(user, input.Role);
            if (!role.Succeeded) throw new ValidationFailedException(role.Errors.Select(e => e.Description).ToList());
            var profile = new UserProfile { UserId = user.Id, IsActive = true, MustChangePassword = true, PasswordChangedOn = DateTime.Now };
            Apply(profile, input);
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
        }
        catch
        {
            await users.DeleteAsync(user);
            throw;
        }
        return new TemporaryCredential(user.UserName!, password);
    }

    public async Task UpdateAsync(string id, UserInput input, string actingUserId)
    {
        var user = await users.FindByIdAsync(id) ?? throw new BankingException("User not found.");
        if (!Roles.All.Contains(input.Role)) throw new ValidationFailedException(["Invalid role."]);
        if (user.Id == actingUserId && (!input.IsActive || input.Role != Roles.Administrator))
            throw new BankingException("You cannot deactivate or demote your own account.");

        user.Email = input.Email;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) throw new ValidationFailedException(updated.Errors.Select(e => e.Description).ToList());

        var current = await users.GetRolesAsync(user);
        if (!current.Contains(input.Role) || current.Count > 1)
        {
            await users.RemoveFromRolesAsync(user, current);
            await users.AddToRoleAsync(user, input.Role);
        }

        var profile = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id);
        if (profile == null)
        {
            profile = new UserProfile { UserId = id };
            db.UserProfiles.Add(profile);
        }
        Apply(profile, input);
        await users.SetLockoutEndDateAsync(user, input.IsActive ? null : DateTimeOffset.MaxValue);
        await db.SaveChangesAsync();
    }

    public async Task<TemporaryCredential> ResetPasswordAsync(string id)
    {
        var user = await users.FindByIdAsync(id) ?? throw new BankingException("User not found.");
        var password = TemporaryPassword();
        var oldHash = user.PasswordHash;
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) throw new ValidationFailedException(result.Errors.Select(e => e.Description).ToList());

        if (oldHash != null) db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, Hash = oldHash });
        var profile = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id);
        if (profile != null)
        {
            profile.MustChangePassword = true;
            profile.PasswordChangedOn = DateTime.Now;
        }
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await db.SaveChangesAsync();
        return new TemporaryCredential(user.UserName!, password);
    }

    public async Task UnlockAsync(string id)
    {
        var user = await users.FindByIdAsync(id) ?? throw new BankingException("User not found.");
        var profile = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id);
        if (profile is { IsActive: false }) throw new BankingException("User is deactivated. Re-activate through Edit.");
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
    }

    private static void Apply(UserProfile p, UserInput i)
    {
        p.FullName = i.FullName; p.EmployeeId = i.EmployeeId; p.Designation = i.Designation; p.PrimeMenu = i.PrimeMenu; p.BranchId = i.BranchId;
        p.AllowedIp = i.AllowedIp; p.WorkFrom = i.WorkFrom; p.WorkTo = i.WorkTo; p.MaxCredit = i.MaxCredit; p.MaxDebit = i.MaxDebit;
        p.MaxInterBranchCredit = i.MaxInterBranchCredit; p.MaxInterBranchDebit = i.MaxInterBranchDebit; p.IsActive = i.IsActive;
    }

    private static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!@#$%*?";
        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) };
        chars.AddRange(Enumerable.Range(0, 8).Select(_ => Pick(upper + lower + digits)));
        var shuffled = chars.ToArray();
        RandomNumberGenerator.Shuffle<char>(shuffled);
        return new string(shuffled);
    }
}

public class PasswordService(UserManager<IdentityUser> users, IApplicationDbContext db, ParamService prm) : IPasswordService
{
    public async Task ChangeAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await users.FindByIdAsync(userId) ?? throw new BankingException("User not found.");
        var keep = await prm.IntAsync("PasswordHistoryCount");
        var history = await db.PasswordHistories.Where(h => h.UserId == user.Id).OrderByDescending(h => h.SetOn).Take(keep).ToListAsync();
        var hasher = new PasswordHasher<IdentityUser>();
        var reused = history.Any(h => hasher.VerifyHashedPassword(user, h.Hash, newPassword) != PasswordVerificationResult.Failed)
                     || (user.PasswordHash != null && hasher.VerifyHashedPassword(user, user.PasswordHash, newPassword) != PasswordVerificationResult.Failed);
        if (reused) throw new ValidationFailedException([$"You cannot re-use any of your last {keep} passwords."]);

        var oldHash = user.PasswordHash;
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded) throw new ValidationFailedException(result.Errors.Select(e => e.Description).ToList());

        if (oldHash != null) db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, Hash = oldHash });
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (profile != null)
        {
            profile.MustChangePassword = false;
            profile.PasswordChangedOn = DateTime.Now;
        }
        await db.SaveChangesAsync();
    }
}
