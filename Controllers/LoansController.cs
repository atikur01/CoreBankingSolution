using System.ComponentModel.DataAnnotations;
using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

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
}

[Authorize(Roles = Roles.Makers)]
public class LoansController : BankController
{
    public async Task<IActionResult> Index(string? q, LoanStatus? status, LoanClass? cls)
    {
        var qry = Db.Loans.AsNoTracking().Include(l => l.Customer).Include(l => l.Product).Include(l => l.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(l => l.LoanNo.Contains(q) || l.Customer!.Name.Contains(q) || l.Customer.CifNo.Contains(q));
        if (status != null) qry = qry.Where(l => l.Status == status);
        if (cls != null) qry = qry.Where(l => l.Classification == cls);
        ViewBag.Q = q;
        return View(await qry.OrderByDescending(l => l.Id).Take(200).ToListAsync());
    }

    private async Task Lists(int? branch = null)
    {
        ViewBag.Products = await ProductList(p => p.IsLoan);
        ViewBag.Branches = await BranchList(branch ?? await MyBranchAsync());
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Apply(string? cif) { await Lists(); return View(new LoanApplyVm { Cif = cif ?? "", BranchId = await MyBranchAsync() }); }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Apply(LoanApplyVm m)
    {
        if (!ModelState.IsValid) { await Lists(m.BranchId); return View(m); }
        Loan? loan = null;
        var ok = await Run(async () =>
        {
            var cust = await Db.Customers.FirstOrDefaultAsync(c => c.CifNo == m.Cif.Trim()) ?? throw new BankingException("Customer CIF not found.");
            var acc = await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == m.SettlementAccountNo.Trim()) ?? throw new BankingException("Settlement account not found.");
            loan = await Svc<LoanService>().ApplyAsync(new Loan
            {
                CustomerId = cust.Id, ProductId = m.ProductId, BranchId = m.BranchId, SettlementAccountId = acc.Id, Principal = m.Principal, TermMonths = m.TermMonths,
                InterestRate = m.InterestRate, Purpose = m.Purpose, Collateral = m.Collateral, CollateralValue = m.CollateralValue, Guarantor = m.Guarantor
            }, Me.UserName);
        }, "Loan application recorded - awaiting approval by a checker.");
        if (!ok) { await Lists(m.BranchId); return View(m); }
        return RedirectToAction(nameof(Details), new { id = loan!.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var l = await Db.Loans.Include(x => x.Customer).Include(x => x.Product).Include(x => x.Branch).Include(x => x.SettlementAccount).Include(x => x.Installments).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        var sched = l.Installments.OrderBy(i => i.No).ToList();
        if (sched.Count == 0 && l.Status < LoanStatus.Disbursed)
            ViewBag.Preview = LoanCalculator.Schedule(l.Principal, l.InterestRate, l.TermMonths, l.Branch!.BusinessDate, l.Product!.Category == ProductCategory.DemandLoan);
        ViewBag.Payoff = l.Status == LoanStatus.Disbursed ? await Svc<LoanService>().PayoffAmountAsync(id) : 0m;
        ViewBag.Pending = await Db.Vouchers.AsNoTracking().Where(v => v.Status == VoucherStatus.Pending && v.SourceKind == "Loan" && v.SourceId == id).ToListAsync();
        return View(l);
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Decide(int id, bool approve, string? reason)
    {
        await Run(() => Svc<LoanService>().ApproveAsync(id, Me.UserName, approve, reason), approve ? "Loan approved." : "Loan rejected.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Disburse(int id)
    {
        await Run(async () => await Svc<LoanService>().DisburseAsync(id, Me.UserName, await Me.ProfileAsync()), "Disbursement voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Repay(int id, decimal amount, string? sourceAccountNo)
    {
        await Run(async () =>
        {
            int? src = null;
            if (!string.IsNullOrWhiteSpace(sourceAccountNo)) src = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == sourceAccountNo.Trim()) ?? throw new BankingException("Source account not found.")).Id;
            await Svc<LoanService>().RepayAsync(id, amount, src, Me.UserName, await Me.ProfileAsync());
        }, "Repayment voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id });
    }
}
