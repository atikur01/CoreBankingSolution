using System.ComponentModel.DataAnnotations;
using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Loans;

public class LoanApplyVm
{
    [Required, Display(Name = "Borrower CIF")] public string Cif { get; set; } = "";
    public int ProductId { get; set; }
    public int BranchId { get; set; }
    [Required, Display(Name = "Settlement account no (borrower's deposit account)")] public string SettlementAccountNo { get; set; } = "";
    [Range(1, 1e12), Display(Name = "Loan amount")] public decimal Principal { get; set; }
    [Range(1, 600), Display(Name = "Term (months)")] public int TermMonths { get; set; } = 12;
    [Display(Name = "Interest rate % (blank/0 = product slab)")] public decimal InterestRate { get; set; }
    [Required] public string Purpose { get; set; } = "";
    public string? Collateral { get; set; }
    [Display(Name = "Collateral value")] public decimal CollateralValue { get; set; }
    public string? Guarantor { get; set; }

    public LoanApplicationRequest ToRequest() => new()
    {
        Cif = Cif, ProductId = ProductId, BranchId = BranchId, SettlementAccountNo = SettlementAccountNo, Principal = Principal,
        TermMonths = TermMonths, InterestRate = InterestRate, Purpose = Purpose, Collateral = Collateral, CollateralValue = CollateralValue, Guarantor = Guarantor
    };
}

[Authorize(Roles = Roles.Makers)]
public class LoansController(
    ICurrentUser me, LookupService lookups, LoanQueries queries, LoanApplicationService applications, LoanService loans) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(string? q, LoanStatus? status, LoanClass? cls)
    {
        ViewBag.Q = q;
        return View(await queries.SearchAsync(q, status, cls));
    }

    private async Task LoadFormListsAsync(int? branch = null)
    {
        ViewBag.Products = await ProductList(loans: true);
        ViewBag.Branches = await BranchList(branch ?? await MyBranchAsync());
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Apply(string? cif)
    {
        await LoadFormListsAsync();
        return View(new LoanApplyVm { Cif = cif ?? "", BranchId = await MyBranchAsync() });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Apply(LoanApplyVm m)
    {
        if (!ModelState.IsValid) { await LoadFormListsAsync(m.BranchId); return View(m); }
        Loan? loan = null;
        var ok = await Run(async () => loan = await applications.ApplyAsync(m.ToRequest(), Me.UserName),
            "Loan application recorded - awaiting approval by a checker.");
        if (!ok) { await LoadFormListsAsync(m.BranchId); return View(m); }
        return RedirectToAction(nameof(Details), new { id = loan!.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var details = await queries.GetDetailsAsync(id);
        if (details == null) return NotFound();
        ViewBag.Preview = details.SchedulePreview;
        ViewBag.Payoff = details.Payoff;
        ViewBag.Pending = details.PendingVouchers;
        return View(details.Loan);
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public Task<IActionResult> Decide(int id, bool approve, string? reason) =>
        Back(id, () => loans.ApproveAsync(id, Me.UserName, approve, reason), approve ? "Loan approved." : "Loan rejected.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> Disburse(int id) =>
        Back(id, async () => await loans.DisburseAsync(id, Me.UserName, await Me.ProfileAsync()), "Disbursement voucher created - awaiting authorization.");

    [HttpPost]
    public Task<IActionResult> Repay(int id, decimal amount, string? sourceAccountNo) =>
        Back(id, async () =>
        {
            var source = await Lookups.OptionalAccountIdAsync(sourceAccountNo, "Source account not found.");
            await loans.RepayAsync(id, amount, source, Me.UserName, await Me.ProfileAsync());
        }, "Repayment voucher created - awaiting authorization.");

    private async Task<IActionResult> Back(int id, Func<Task> work, string success)
    {
        await Run(work, success);
        return RedirectToAction(nameof(Details), new { id });
    }
}
