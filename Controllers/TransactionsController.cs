using System.ComponentModel.DataAnnotations;
using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

public class CashTxnVm
{
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    [Required, Display(Name = "Account number")] public string AccountNo { get; set; } = "";
    [Range(0.01, 1e12), Display(Name = "Amount")] public decimal Amount { get; set; }
    [Display(Name = "Depositor / withdrawer / bearer name")] public string? PartyName { get; set; }
    [Display(Name = "Cheque number (for withdrawal by cheque)")] public string? ChequeNo { get; set; }
    public string? Narration { get; set; }
    [Display(Name = "Value date (back-value needs approval)")] public DateTime? ValueDate { get; set; }
}

public class TransferVm
{
    [Required, Display(Name = "Debit account")] public string FromNo { get; set; } = "";
    [Required, Display(Name = "Credit account")] public string ToNo { get; set; } = "";
    [Range(0.01, 1e12)] public decimal Amount { get; set; }
    [Display(Name = "Cheque number")] public string? ChequeNo { get; set; }
    public string? Narration { get; set; }
    [Display(Name = "Value date")] public DateTime? ValueDate { get; set; }
}

public class JournalVm
{
    [Required] public string Description { get; set; } = "";
    public DateTime? ValueDate { get; set; }
    public List<JournalLine> Lines { get; set; } = Enumerable.Range(0, 4).Select(_ => new JournalLine()).ToList();
}
public class JournalLine { public string? GlCode { get; set; } public int? BranchId { get; set; } public decimal Debit { get; set; } public decimal Credit { get; set; } public string? Narration { get; set; } }

[Authorize(Roles = Roles.Makers)]
public class TransactionsController : BankController
{
    private async Task<Account> Acc(string no) =>
        await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == no.Trim()) ?? throw new BankingException($"Account {no} not found.");

    // ---------------- teller transactions
    [HttpGet] public IActionResult Deposit(string? no) => View("Cash", new CashTxnVm { AccountNo = no ?? "" }.With("Cash deposit", "Deposit"));
    [HttpGet] public IActionResult Withdraw(string? no) => View("Cash", new CashTxnVm { AccountNo = no ?? "" }.With("Cash withdrawal", "Withdraw"));

    [HttpPost] public async Task<IActionResult> Deposit(CashTxnVm m)
    {
        if (!ModelState.IsValid) return View("Cash", m.With("Cash deposit", "Deposit"));
        Voucher? v = null;
        if (await Run(async () => v = await Svc<TransactionService>().DepositAsync((await Acc(m.AccountNo)).Id, m.Amount, m.PartyName, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate)))
        { Ok($"Deposit voucher {v!.VoucherNo} created - awaiting authorization."); return RedirectToAction(nameof(Details), new { id = v.Id }); }
        return View("Cash", m.With("Cash deposit", "Deposit"));
    }

    [HttpPost] public async Task<IActionResult> Withdraw(CashTxnVm m)
    {
        if (!ModelState.IsValid) return View("Cash", m.With("Cash withdrawal", "Withdraw"));
        Voucher? v = null;
        if (await Run(async () => v = await Svc<TransactionService>().WithdrawAsync((await Acc(m.AccountNo)).Id, m.Amount, m.ChequeNo, m.PartyName, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate)))
        { Ok($"Withdrawal voucher {v!.VoucherNo} created - awaiting authorization."); return RedirectToAction(nameof(Details), new { id = v.Id }); }
        return View("Cash", m.With("Cash withdrawal", "Withdraw"));
    }

    [HttpGet] public IActionResult Transfer(string? from) => View(new TransferVm { FromNo = from ?? "" });

    [HttpPost] public async Task<IActionResult> Transfer(TransferVm m)
    {
        if (!ModelState.IsValid) return View(m);
        Voucher? v = null;
        if (await Run(async () => v = await Svc<TransactionService>().TransferAsync((await Acc(m.FromNo)).Id, (await Acc(m.ToNo)).Id, m.Amount, m.ChequeNo, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate)))
        { Ok($"Transfer voucher {v!.VoucherNo} created - awaiting authorization."); return RedirectToAction(nameof(Details), new { id = v.Id }); }
        return View(m);
    }

    // ---------------- manual GL journal
    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Journal() { ViewBag.Branches = await BranchList(); return View(new JournalVm()); }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Journal(JournalVm m)
    {
        Voucher? v = null;
        var ok = await Run(async () =>
        {
            var p = await Me.ProfileAsync();
            var req = new VoucherReq { Type = VoucherType.Journal, BranchId = p?.BranchId ?? await MyBranchAsync(), Description = m.Description, ValueDate = m.ValueDate, Maker = Me.UserName, MakerProfile = p };
            foreach (var l in m.Lines.Where(x => !string.IsNullOrWhiteSpace(x.GlCode)))
            {
                var gl = await Db.GlAccounts.FirstOrDefaultAsync(g => g.Code == l.GlCode!.Trim()) ?? throw new BankingException($"GL {l.GlCode} not found.");
                if (await Db.Products.AnyAsync(pr => pr.GlAccountId == gl.Id))
                    throw new BankingException($"GL {gl.Code} is a customer-account control GL; use deposit/withdrawal/transfer functions instead.");
                req.Lines.Add(new LineReq(gl.Id, null, l.Debit, l.Credit, l.Narration ?? m.Description, l.BranchId));
            }
            v = await Svc<LedgerService>().CreateAsync(req);
        });
        if (ok) { Ok($"Journal voucher {v!.VoucherNo} created - awaiting authorization."); return RedirectToAction(nameof(Details), new { id = v.Id }); }
        ViewBag.Branches = await BranchList(); return View(m);
    }

    // ---------------- authorization queue
    [Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Pending(int? branchId)
    {
        var qry = Db.Vouchers.AsNoTracking().Include(v => v.Branch).Where(v => v.Status == VoucherStatus.Pending);
        if (branchId != null) qry = qry.Where(v => v.BranchId == branchId);
        ViewBag.Branches = await BranchList(branchId);
        return View(await qry.OrderBy(v => v.Id).ToListAsync());
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Authorize(int id, string? returnTo)
    {
        await Run(() => Svc<LedgerService>().AuthorizeAsync(id, Me.UserName, Me.IsManagerLevel), "Voucher authorized and posted.");
        return returnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Pending));
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Reject(int id, string reason, string? returnTo)
    {
        await Run(() => Svc<LedgerService>().RejectAsync(id, Me.UserName, reason), "Voucher rejected.");
        return returnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Pending));
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Reverse(int id, string reason)
    {
        Voucher? r = null;
        if (await Run(async () => r = await Svc<LedgerService>().CreateReversalAsync(id, reason, Me.UserName, await Me.ProfileAsync())))
        { Ok($"Reversal voucher {r!.VoucherNo} created - awaiting authorization."); return RedirectToAction(nameof(Details), new { id = r.Id }); }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var v = await Db.Vouchers.Include(x => x.Branch).Include(x => x.Lines).ThenInclude(l => l.GlAccount).Include(x => x.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();
        ViewBag.Currency = (await Db.Currencies.FindAsync(v.CurrencyId))?.Code;
        ViewBag.Reversal = v.ReversalOfId == null ? null : await Db.Vouchers.FindAsync(v.ReversalOfId);
        ViewBag.ReversedBy = v.ReversedById == null ? null : await Db.Vouchers.FindAsync(v.ReversedById);
        return View(v);
    }

    public async Task<IActionResult> Register(DateTime? from, DateTime? to, VoucherStatus? status, VoucherType? type, string? q, int? branchId, bool csv = false)
    {
        var f = (from ?? DateTime.Today.AddDays(-7)).Date; var t = (to ?? DateTime.Today.AddYears(1)).Date;
        var qry = Db.Vouchers.AsNoTracking().Include(v => v.Branch).Where(v => v.TxnDate >= f && v.TxnDate <= t);
        if (status != null) qry = qry.Where(v => v.Status == status);
        if (type != null) qry = qry.Where(v => v.Type == type);
        if (branchId != null) qry = qry.Where(v => v.BranchId == branchId);
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(v => v.VoucherNo.Contains(q) || v.Description.Contains(q) || v.MakerId.Contains(q) || (v.ChequeNo != null && v.ChequeNo.Contains(q)));
        var rows = await qry.OrderByDescending(v => v.Id).Take(500).ToListAsync();
        if (csv) return Csv("vouchers", ["Voucher", "Date", "Value date", "Branch", "Type", "Description", "Amount", "Status", "Maker", "Checker"],
            rows.Select(v => new object?[] { v.VoucherNo, v.TxnDate.ToString("yyyy-MM-dd"), v.ValueDate.ToString("yyyy-MM-dd"), v.Branch?.Code, v.Type, v.Description, v.TotalAmount, v.Status, v.MakerId, v.CheckerId }));
        ViewBag.From = f; ViewBag.To = t; ViewBag.Q = q; ViewBag.Branches = await BranchList(branchId);
        return View(rows);
    }
}

public static class CashVmExt
{
    public static CashTxnVm With(this CashTxnVm m, string title, string action) { m.Title = title; m.Action = action; return m; }
}
