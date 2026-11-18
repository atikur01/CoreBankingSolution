using System.ComponentModel.DataAnnotations;
using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

public class OpenAccountVm
{
    [Required, Display(Name = "Customer CIF")] public string Cif { get; set; } = "";
    [Display(Name = "Joint holders' CIFs (comma separated)")] public string? JointCifs { get; set; }
    public int ProductId { get; set; }
    public int BranchId { get; set; }
    [Display(Name = "Account title (blank = customer name)")] public string? Title { get; set; }
    [Display(Name = "Mode of operation")] public OperationMode Mode { get; set; } = OperationMode.Single;
    [Display(Name = "Initial deposit / principal")] public decimal InitialDeposit { get; set; }
    [Display(Name = "FDR: debit from account no (blank = cash)")] public string? SourceAccountNo { get; set; }
    [Display(Name = "FDR/DPS: pay proceeds to account no")] public string? LinkedAccountNo { get; set; }
    [Display(Name = "Term (months)")] public int TermMonths { get; set; }
    [Display(Name = "DPS monthly installment")] public decimal InstallmentAmount { get; set; }
    [Display(Name = "Auto-renew at maturity")] public bool AutoRenew { get; set; }
    [Display(Name = "Overdraft limit (current)")] public decimal OdLimit { get; set; }
    [Display(Name = "Single debit limit")] public decimal SingleDebitLimit { get; set; }
    [Display(Name = "Daily debit limit")] public decimal DailyDebitLimit { get; set; }
    [Display(Name = "Monthly debit limit")] public decimal MonthlyDebitLimit { get; set; }
    [Display(Name = "Power of attorney holder")] public string? PowerOfAttorney { get; set; }
    public string? N1Name { get; set; } public string? N1Relation { get; set; } public string? N1Nid { get; set; } public decimal N1Share { get; set; } = 100;
    public string? N2Name { get; set; } public string? N2Relation { get; set; } public string? N2Nid { get; set; } public decimal N2Share { get; set; }
}

[Authorize(Roles = Roles.Makers)]
public class AccountsController : BankController
{
    public async Task<IActionResult> Index(string? q, AccountStatus? status, int? branchId)
    {
        var qry = Db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Include(a => a.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(a => a.AccountNo.Contains(q) || a.Title.Contains(q) || a.Customer!.CifNo.Contains(q) || a.Customer.Mobile.Contains(q));
        if (status != null) qry = qry.Where(a => a.Status == status);
        if (branchId != null) qry = qry.Where(a => a.BranchId == branchId);
        ViewBag.Q = q; ViewBag.Status = status; ViewBag.Branches = await BranchList(branchId);
        return View(await qry.OrderByDescending(a => a.Id).Take(200).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> Lookup(string no)
    {
        var a = await Db.Accounts.AsNoTracking().Include(x => x.Product).Include(x => x.Branch).FirstOrDefaultAsync(x => x.AccountNo == no.Trim());
        if (a == null) return Json(new { found = false });
        return Json(new { found = true, title = a.Title, product = a.Product!.Name, status = a.Status.ToString(), balance = a.Balance.ToString("N2"), branch = a.Branch!.Code });
    }

    private async Task Lists(int? branch = null)
    {
        var prods = await Db.Products.Where(p => p.IsActive && p.Category < ProductCategory.TermLoan).OrderBy(p => p.Code).ToListAsync();
        ViewBag.Products = new SelectList(prods.Select(p => new { p.Id, T = p.Code + " - " + p.Name }), "Id", "T");
        ViewBag.Cats = System.Text.Json.JsonSerializer.Serialize(prods.ToDictionary(p => p.Id.ToString(), p => p.Category.ToString()));
        ViewBag.Branches = await BranchList(branch ?? await MyBranchAsync());
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Open(string? cif) { await Lists(); return View(new OpenAccountVm { Cif = cif ?? "", BranchId = await MyBranchAsync() }); }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Open(OpenAccountVm m)
    {
        if (!ModelState.IsValid) { await Lists(m.BranchId); return View(m); }
        Account? created = null;
        var ok = await Run(async () =>
        {
            var cust = await Db.Customers.FirstOrDefaultAsync(c => c.CifNo == m.Cif.Trim()) ?? throw new BankingException("Customer CIF not found.");
            var input = new OpenAccountInput
            {
                CustomerId = cust.Id, ProductId = m.ProductId, BranchId = m.BranchId, Title = m.Title, Mode = m.Mode, InitialDeposit = m.InitialDeposit,
                PowerOfAttorney = m.PowerOfAttorney, OdLimit = m.OdLimit, SingleDebitLimit = m.SingleDebitLimit, DailyDebitLimit = m.DailyDebitLimit,
                MonthlyDebitLimit = m.MonthlyDebitLimit, TermMonths = m.TermMonths, InstallmentAmount = m.InstallmentAmount, AutoRenew = m.AutoRenew
            };
            foreach (var j in (m.JointCifs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var jc = await Db.Customers.FirstOrDefaultAsync(c => c.CifNo == j) ?? throw new BankingException($"Joint holder CIF {j} not found.");
                input.JointCustomerIds.Add(jc.Id);
            }
            if (!string.IsNullOrWhiteSpace(m.SourceAccountNo))
                input.SourceAccountId = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == m.SourceAccountNo.Trim()) ?? throw new BankingException("Source account not found.")).Id;
            if (!string.IsNullOrWhiteSpace(m.LinkedAccountNo))
                input.LinkedAccountId = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == m.LinkedAccountNo.Trim()) ?? throw new BankingException("Linked account not found.")).Id;
            if (!string.IsNullOrWhiteSpace(m.N1Name)) input.Nominees.Add(new Nominee { Name = m.N1Name, Relation = m.N1Relation, Nid = m.N1Nid, SharePercent = m.N1Share });
            if (!string.IsNullOrWhiteSpace(m.N2Name)) input.Nominees.Add(new Nominee { Name = m.N2Name, Relation = m.N2Relation, Nid = m.N2Nid, SharePercent = m.N2Share });
            created = await Svc<DepositService>().OpenAsync(input, Me.UserName, await Me.ProfileAsync());
        }, "Account created. It will become active once the opening voucher is authorized by a checker.");
        if (!ok || created == null) { await Lists(m.BranchId); return View(m); }
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await Db.Accounts.Include(x => x.Product).Include(x => x.Customer).Include(x => x.Branch).Include(x => x.Holders).ThenInclude(h => h.Customer)
            .Include(x => x.Nominees).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        ViewBag.Txns = await Db.AccountTransactions.AsNoTracking().Include(t => t.Voucher).Where(t => t.AccountId == id).OrderByDescending(t => t.Id).Take(15).ToListAsync();
        ViewBag.Books = await Db.ChequeBooks.AsNoTracking().Where(b => b.AccountId == id).ToListAsync();
        ViewBag.Stops = await Db.StopPayments.AsNoTracking().Where(s => s.AccountId == id).OrderByDescending(s => s.Id).ToListAsync();
        ViewBag.Returns = await Db.ChequeReturns.AsNoTracking().Where(s => s.AccountId == id).OrderByDescending(s => s.Id).ToListAsync();
        ViewBag.Pending = await Db.Vouchers.AsNoTracking().Where(v => v.Status == VoucherStatus.Pending && v.Lines.Any(l => l.AccountId == id)).ToListAsync();
        ViewBag.Sis = await Db.StandingInstructions.AsNoTracking().Include(s => s.ToAccount).Where(s => s.FromAccountId == id).ToListAsync();
        return View(a);
    }

    // ---- statement
    public async Task<IActionResult> Statement(int id, DateTime? from, DateTime? to, bool csv = false)
    {
        var a = await Db.Accounts.Include(x => x.Product).Include(x => x.Customer).Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        var f = (from ?? a.Branch!.BusinessDate.AddMonths(-1)).Date; var t = (to ?? a.Branch!.BusinessDate).Date;
        var opening = await Db.AccountTransactions.Where(x => x.AccountId == id && x.TxnDate < f).OrderByDescending(x => x.Id).Select(x => (decimal?)x.RunningBalance).FirstOrDefaultAsync() ?? 0;
        var rows = await Db.AccountTransactions.AsNoTracking().Include(x => x.Voucher).Where(x => x.AccountId == id && x.TxnDate >= f && x.TxnDate <= t).OrderBy(x => x.Id).ToListAsync();
        if (csv) return Csv("statement-" + a.AccountNo, ["Date", "Value date", "Voucher", "Narration", "Cheque", "Debit", "Credit", "Balance"],
            rows.Select(r => new object?[] { r.TxnDate.ToString("yyyy-MM-dd"), r.ValueDate.ToString("yyyy-MM-dd"), r.Voucher?.VoucherNo, r.Narration, r.ChequeNo, r.Debit, r.Credit, r.RunningBalance }));
        ViewBag.From = f; ViewBag.To = t; ViewBag.Opening = opening;
        return View(new StatementVm { Account = a, Rows = rows });
    }

    // ---- maker actions
    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Approve(int id) { await Run(() => Svc<DepositService>().ApproveZeroBalanceAsync(id, Me.UserName), "Account approved and activated."); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> SetStatus(int id, AccountStatus status, string? reason)
    {
        var a = await Db.Accounts.Include(x => x.Product).FirstOrDefaultAsync(x => x.Id == id); if (a == null) return NotFound();
        if (status is AccountStatus.PendingApproval or AccountStatus.Matured) Err("Invalid target status.");
        else if (a.Status is AccountStatus.PendingApproval or AccountStatus.Closed) Err($"Account is {a.Status}.");
        else if (status == AccountStatus.Closed && (a.Balance != 0 || a.AccruedInterest >= 0.005m || a.OdInterestAccrued >= 0.005m)) Err("Account has a balance/accrued interest; use 'Close account & pay out' instead.");
        else if (string.IsNullOrWhiteSpace(reason)) Err("A reason is required for a status change.");
        else
        {
            var old = a.Status; a.Status = status; a.Version++;
            if (status == AccountStatus.Closed) a.ClosedOn = (await Db.Branches.FindAsync(a.BranchId))!.BusinessDate;
            if (status == AccountStatus.Active && old is AccountStatus.Dormant or AccountStatus.Inoperative) a.LastTxnDate = (await Db.Branches.FindAsync(a.BranchId))!.BusinessDate;
            await Db.SaveChangesAsync(); Ok($"Status changed {old} -> {status} ({reason}).");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Limits(int id, decimal odLimit, decimal single, decimal daily, decimal monthly, string? poa, decimal lien)
    {
        var a = await Db.Accounts.FindAsync(id); if (a == null) return NotFound();
        if (lien < 0 || lien > a.Balance) { Err("Lien must be between 0 and the balance."); return RedirectToAction(nameof(Details), new { id }); }
        if (lien != a.LienAmount && !User.IsInRole(Roles.Administrator) && !User.IsInRole(Roles.Manager) && !User.IsInRole(Roles.Supervisor)) { Err("Only a checker can change the lien."); return RedirectToAction(nameof(Details), new { id }); }
        a.OdLimit = odLimit; a.SingleDebitLimit = single; a.DailyDebitLimit = daily; a.MonthlyDebitLimit = monthly; a.PowerOfAttorney = poa; a.LienAmount = lien; a.Version++;
        await Db.SaveChangesAsync(); Ok("Limits updated."); return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> IssueCheque(int id, int leaves)
    {
        await Run(async () => { var v = await Svc<DepositService>().IssueChequeBookAsync(id, leaves, Me.UserName, await Me.ProfileAsync()); }, "Cheque book issued (fee voucher, if any, awaits authorization).");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> StopPay(int id, string chequeNo, decimal amount, string reason)
    {
        await Run(async () => await Svc<DepositService>().StopPaymentAsync(id, chequeNo.Trim(), amount, reason, Me.UserName, await Me.ProfileAsync()), "Stop payment recorded.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> ReleaseStop(int id, int stopId)
    {
        var s = await Db.StopPayments.FindAsync(stopId); if (s != null && s.AccountId == id) { s.IsActive = false; s.ReleasedOn = DateTime.Now; await Db.SaveChangesAsync(); Ok("Stop payment released."); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> ReturnCheque(int id, string chequeNo, decimal amount, string reason, bool charge)
    {
        await Run(async () => await Svc<DepositService>().ReturnChequeAsync(id, chequeNo.Trim(), amount, reason, charge, Me.UserName, await Me.ProfileAsync()), "Cheque return recorded in the Cheque Return Register.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> PayDps(int id, string? sourceAccountNo)
    {
        await Run(async () =>
        {
            int? src = null;
            if (!string.IsNullOrWhiteSpace(sourceAccountNo)) src = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == sourceAccountNo.Trim()) ?? throw new BankingException("Source account not found.")).Id;
            await Svc<DepositService>().PayDpsInstallmentAsync(id, src, Me.UserName, await Me.ProfileAsync());
        }, "DPS installment voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Encash(int id, bool premature, bool toCash)
    {
        await Run(async () => await Svc<DepositService>().EncashAsync(id, premature, toCash, Me.UserName, await Me.ProfileAsync()), "Encashment voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> CloseAccount(int id)
    {
        await Run(async () => await Svc<DepositService>().CloseAsync(id, Me.UserName, await Me.ProfileAsync()), "Closing voucher created - the account closes when it is authorized.");
        return RedirectToAction(nameof(Details), new { id });
    }
}

public class StatementVm { public Account Account { get; set; } = null!; public List<AccountTransaction> Rows { get; set; } = []; }
