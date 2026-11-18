using System.ComponentModel.DataAnnotations;
using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Users;

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

    public UserInput ToInput() => new()
    {
        Id = Id, Email = Email.Trim(), FullName = FullName, EmployeeId = EmployeeId, Designation = Designation, PrimeMenu = PrimeMenu, BranchId = BranchId,
        Role = Role, AllowedIp = AllowedIp, WorkFrom = WorkFrom, WorkTo = WorkTo, MaxCredit = MaxCredit, MaxDebit = MaxDebit,
        MaxInterBranchCredit = MaxInterBranchCredit, MaxInterBranchDebit = MaxInterBranchDebit, IsActive = IsActive
    };

    public static UserVm From(string userName, UserInput i) => new()
    {
        Id = i.Id, UserName = userName, Email = i.Email, FullName = i.FullName, EmployeeId = i.EmployeeId, Designation = i.Designation, PrimeMenu = i.PrimeMenu,
        BranchId = i.BranchId, Role = i.Role, AllowedIp = i.AllowedIp, WorkFrom = i.WorkFrom, WorkTo = i.WorkTo, MaxCredit = i.MaxCredit, MaxDebit = i.MaxDebit,
        MaxInterBranchCredit = i.MaxInterBranchCredit, MaxInterBranchDebit = i.MaxInterBranchDebit, IsActive = i.IsActive
    };
}

public class UserRow
{
    public UserListItem User { get; set; } = null!;
    public UserProfile? Profile => User.Profile;
    public string Roles => User.Roles;
    public bool Locked => User.Locked;
}

[Authorize(Roles = Roles.Administrator)]
public class UsersController(ICurrentUser me, LookupService lookups, IUserAdministration users, AuditQueries audit) : BankController(me, lookups)
{
    public async Task<IActionResult> Index() => View((await users.ListAsync()).Select(u => new UserRow { User = u }).ToList());

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewBag.Branches = await BranchList();
        return View("Form", new UserVm { BranchId = await MyBranchAsync() });
    }

    [HttpPost]
    public async Task<IActionResult> Create(UserVm m)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var credential = await users.CreateAsync(m.ToInput());
                TempData["ok"] = $"User '{credential.UserName}' created. Temporary password (shown once, communicate separately): {credential.Password}";
                return RedirectToAction(nameof(Index));
            }
            catch (ValidationFailedException e) { foreach (var error in e.Errors) ModelState.AddModelError("", error); }
            catch (BankingException e) { ModelState.AddModelError("", e.Message); }
        }
        ViewBag.Branches = await BranchList(m.BranchId);
        return View("Form", m);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await users.GetAsync(id);
        if (user == null) return NotFound();
        var vm = UserVm.From(user.Value.UserName, user.Value.Input);
        if (vm.BranchId == 0) vm.BranchId = await MyBranchAsync();
        ViewBag.Branches = await BranchList(vm.BranchId);
        return View("Form", vm);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string id, UserVm m)
    {
        if (await users.GetAsync(id) == null) return NotFound();
        if (ModelState.IsValid)
        {
            try
            {
                await users.UpdateAsync(id, m.ToInput(), Me.UserId ?? "");
                Ok("User updated.");
                return RedirectToAction(nameof(Index));
            }
            catch (ValidationFailedException e) { foreach (var error in e.Errors) ModelState.AddModelError("", error); }
            catch (BankingException e)
            {
                Err(e.Message);
                return RedirectToAction(nameof(Edit), new { id });
            }
        }
        ViewBag.Branches = await BranchList(m.BranchId);
        return View("Form", m);
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(string id)
    {
        try
        {
            var credential = await users.ResetPasswordAsync(id);
            TempData["ok"] = $"Password reset for '{credential.UserName}'. Temporary password (shown once): {credential.Password}";
        }
        catch (BankingException e) { Err(e.Message); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Unlock(string id)
    {
        await Run(() => users.UnlockAsync(id), "User unlocked.");
        return RedirectToAction(nameof(Index));
    }

    [ActionName("Roles")] public IActionResult RoleMatrix() => View("Roles");

    public async Task<IActionResult> Audit(string? user, string? entity, string? q, DateTime? from, DateTime? to, bool csv = false)
    {
        var page = await audit.SearchAsync(new AuditFilter(user, entity, q, from, to));
        if (csv)
            return Csv("audit-trail", ["Time", "User", "IP", "Action", "Entity", "Key", "Details"],
                page.Rows.Select(a => new object?[] { a.At.ToString("yyyy-MM-dd HH:mm:ss"), a.UserName, a.Ip, a.Action, a.Entity, a.EntityKey, a.Details }));
        ViewBag.From = page.From;
        ViewBag.To = page.To;
        ViewBag.User = user;
        ViewBag.Entity = entity;
        ViewBag.Q = q;
        return View(page.Rows);
    }
}
