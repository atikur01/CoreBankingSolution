using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

public class UserVm
{
    public string? Id { get; set; }
    [Display(Name = "User ID (login)")] public string UserName { get; set; } = "";
    [Required, EmailAddress, Display(Name = "Email (used as login ID)")] public string Email { get; set; } = "";
    [Required, Display(Name = "Full name")] public string FullName { get; set; } = "";
    [Display(Name = "Employee ID")] public string? EmployeeId { get; set; }
    public string? Designation { get; set; }
    [Display(Name = "Prime menu")] public string? PrimeMenu { get; set; }
    public int BranchId { get; set; }
    [Required] public string Role { get; set; } = Roles.Teller;
    [Display(Name = "Allowed terminal IP (blank = any)")] public string? AllowedIp { get; set; }
    [Display(Name = "Operating hours from")] public TimeSpan? WorkFrom { get; set; }
    [Display(Name = "Operating hours to")] public TimeSpan? WorkTo { get; set; }
    [Display(Name = "Max single credit (0 = no limit)")] public decimal MaxCredit { get; set; }
    [Display(Name = "Max single debit (0 = no limit)")] public decimal MaxDebit { get; set; }
    [Display(Name = "Max inter-branch credit")] public decimal MaxInterBranchCredit { get; set; }
    [Display(Name = "Max inter-branch debit")] public decimal MaxInterBranchDebit { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserRow { public IdentityUser User { get; set; } = null!; public UserProfile? Profile { get; set; } public string Roles { get; set; } = ""; public bool Locked { get; set; } }

[Authorize(Roles = Roles.Administrator)]
public class UsersController(UserManager<IdentityUser> users) : BankController
{
    public async Task<IActionResult> Index()
    {
        var list = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        var profs = await Db.UserProfiles.AsNoTracking().Include(p => p.Branch).ToListAsync();
        var rows = new List<UserRow>();
        foreach (var u in list)
            rows.Add(new UserRow { User = u, Profile = profs.FirstOrDefault(p => p.UserId == u.Id), Roles = string.Join(", ", await users.GetRolesAsync(u)), Locked = await users.IsLockedOutAsync(u) });
        return View(rows);
    }

    private static string TempPassword()
    {
        const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghijkmnpqrstuvwxyz", dg = "23456789", sy = "!@#$%*?";
        string Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)].ToString();
        var chars = (Pick(up) + Pick(lo) + Pick(dg) + Pick(sy) + string.Concat(Enumerable.Range(0, 8).Select(_ => Pick(up + lo + dg)))).ToCharArray();
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).ToArray());
    }

    [HttpGet] public async Task<IActionResult> Create() { ViewBag.Branches = await BranchList(); return View("Form", new UserVm { BranchId = await MyBranchAsync() }); }

    [HttpPost]
    public async Task<IActionResult> Create(UserVm m)
    {
        if (!Roles.All.Contains(m.Role)) ModelState.AddModelError("Role", "Invalid role.");
        if (!ModelState.IsValid) { ViewBag.Branches = await BranchList(m.BranchId); return View("Form", m); }
        var pwd = TempPassword();
        var u = new IdentityUser { UserName = m.Email.Trim(), Email = m.Email.Trim(), EmailConfirmed = true, LockoutEnabled = true };
        var res = await users.CreateAsync(u, pwd);
        if (!res.Succeeded) { foreach (var e in res.Errors) ModelState.AddModelError("", e.Description); ViewBag.Branches = await BranchList(m.BranchId); return View("Form", m); }
        await users.AddToRoleAsync(u, m.Role);
        Db.UserProfiles.Add(new UserProfile
        {
            UserId = u.Id, FullName = m.FullName, EmployeeId = m.EmployeeId, Designation = m.Designation, PrimeMenu = m.PrimeMenu, BranchId = m.BranchId, AllowedIp = m.AllowedIp,
            WorkFrom = m.WorkFrom, WorkTo = m.WorkTo, MaxCredit = m.MaxCredit, MaxDebit = m.MaxDebit, MaxInterBranchCredit = m.MaxInterBranchCredit, MaxInterBranchDebit = m.MaxInterBranchDebit,
            IsActive = true, MustChangePassword = true, PasswordChangedOn = DateTime.Now
        });
        await Db.SaveChangesAsync();
        TempData["ok"] = $"User '{u.UserName}' created. Temporary password (shown once, communicate separately): {pwd}";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var u = await users.FindByIdAsync(id); if (u == null) return NotFound();
        var p = await Db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id);
        var vm = new UserVm
        {
            Id = id, UserName = u.UserName!, Email = u.Email ?? "", FullName = p?.FullName ?? u.UserName!, EmployeeId = p?.EmployeeId, Designation = p?.Designation, PrimeMenu = p?.PrimeMenu,
            BranchId = p?.BranchId ?? await MyBranchAsync(), Role = (await users.GetRolesAsync(u)).FirstOrDefault() ?? Roles.Teller, AllowedIp = p?.AllowedIp, WorkFrom = p?.WorkFrom, WorkTo = p?.WorkTo,
            MaxCredit = p?.MaxCredit ?? 0, MaxDebit = p?.MaxDebit ?? 0, MaxInterBranchCredit = p?.MaxInterBranchCredit ?? 0, MaxInterBranchDebit = p?.MaxInterBranchDebit ?? 0, IsActive = p?.IsActive ?? true
        };
        ViewBag.Branches = await BranchList(vm.BranchId); return View("Form", vm);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string id, UserVm m)
    {
        var u = await users.FindByIdAsync(id); if (u == null) return NotFound();
        if (!Roles.All.Contains(m.Role)) ModelState.AddModelError("Role", "Invalid role.");
        if (!ModelState.IsValid) { ViewBag.Branches = await BranchList(m.BranchId); return View("Form", m); }
        bool self = u.Id == Me.UserId;
        if (self && (!m.IsActive || m.Role != Roles.Administrator)) { Err("You cannot deactivate or demote your own account."); return RedirectToAction(nameof(Edit), new { id }); }
        u.Email = m.Email; await users.UpdateAsync(u);
        var cur = await users.GetRolesAsync(u);
        if (!cur.Contains(m.Role) || cur.Count > 1) { await users.RemoveFromRolesAsync(u, cur); await users.AddToRoleAsync(u, m.Role); }
        var p = await Db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id) ?? Db.UserProfiles.Add(new UserProfile { UserId = id }).Entity;
        p.FullName = m.FullName; p.EmployeeId = m.EmployeeId; p.Designation = m.Designation; p.PrimeMenu = m.PrimeMenu; p.BranchId = m.BranchId; p.AllowedIp = m.AllowedIp;
        p.WorkFrom = m.WorkFrom; p.WorkTo = m.WorkTo; p.MaxCredit = m.MaxCredit; p.MaxDebit = m.MaxDebit; p.MaxInterBranchCredit = m.MaxInterBranchCredit; p.MaxInterBranchDebit = m.MaxInterBranchDebit; p.IsActive = m.IsActive;
        await users.SetLockoutEndDateAsync(u, m.IsActive ? null : DateTimeOffset.MaxValue);
        await Db.SaveChangesAsync(); Ok("User updated."); return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(string id)
    {
        var u = await users.FindByIdAsync(id); if (u == null) return NotFound();
        var pwd = TempPassword();
        var token = await users.GeneratePasswordResetTokenAsync(u);
        var old = u.PasswordHash;
        var res = await users.ResetPasswordAsync(u, token, pwd);
        if (!res.Succeeded) { Err(string.Join("; ", res.Errors.Select(e => e.Description))); return RedirectToAction(nameof(Index)); }
        if (old != null) Db.PasswordHistories.Add(new PasswordHistory { UserId = u.Id, Hash = old });
        var p = await Db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id);
        if (p != null) { p.MustChangePassword = true; p.PasswordChangedOn = DateTime.Now; }
        await users.ResetAccessFailedCountAsync(u); await users.SetLockoutEndDateAsync(u, null);
        await Db.SaveChangesAsync();
        TempData["ok"] = $"Password reset for '{u.UserName}'. Temporary password (shown once): {pwd}";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Unlock(string id)
    {
        var u = await users.FindByIdAsync(id); if (u == null) return NotFound();
        var p = await Db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == id);
        if (p is { IsActive: false }) Err("User is deactivated. Re-activate through Edit.");
        else { await users.SetLockoutEndDateAsync(u, null); await users.ResetAccessFailedCountAsync(u); Ok("User unlocked."); }
        return RedirectToAction(nameof(Index));
    }

    [ActionName("Roles")] public IActionResult RoleMatrix() => View("Roles");

    public async Task<IActionResult> Audit(string? user, string? entity, string? q, DateTime? from, DateTime? to, bool csv = false)
    {
        var f = (from ?? DateTime.Today.AddDays(-3)).Date; var t = (to ?? DateTime.Today).Date.AddDays(1);
        var qry = Db.AuditLogs.AsNoTracking().Where(a => a.At >= f && a.At < t);
        if (!string.IsNullOrWhiteSpace(user)) qry = qry.Where(a => a.UserName!.Contains(user));
        if (!string.IsNullOrWhiteSpace(entity)) qry = qry.Where(a => a.Entity == entity);
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(a => a.EntityKey == q || a.Details!.Contains(q));
        var rows = await qry.OrderByDescending(a => a.Id).Take(500).ToListAsync();
        if (csv) return Csv("audit-trail", ["Time", "User", "IP", "Action", "Entity", "Key", "Details"], rows.Select(a => new object?[] { a.At.ToString("yyyy-MM-dd HH:mm:ss"), a.UserName, a.Ip, a.Action, a.Entity, a.EntityKey, a.Details }));
        ViewBag.From = f; ViewBag.To = t.AddDays(-1); ViewBag.User = user; ViewBag.Entity = entity; ViewBag.Q = q;
        return View(rows);
    }
}
