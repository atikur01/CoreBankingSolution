using System.ComponentModel.DataAnnotations;
using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Transactions;

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

    public CashTxnVm With(string title, string action)
    {
        Title = title;
        Action = action;
        return this;
    }
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

public class JournalLine
{
    public string? GlCode { get; set; }
    public int? BranchId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Narration { get; set; }
}

[Authorize(Roles = Roles.Makers)]
public class TransactionsController(
    ICurrentUser me, LookupService lookups, TransactionService transactions, LedgerService ledger,
    VoucherQueries queries, JournalService journals) : BankController(me, lookups)
{
    private const string DepositTitle = "Cash deposit";
    private const string WithdrawTitle = "Cash withdrawal";

    [HttpGet] public IActionResult Deposit(string? no) => View("Cash", new CashTxnVm { AccountNo = no ?? "" }.With(DepositTitle, "Deposit"));
    [HttpGet] public IActionResult Withdraw(string? no) => View("Cash", new CashTxnVm { AccountNo = no ?? "" }.With(WithdrawTitle, "Withdraw"));

    [HttpPost]
    public async Task<IActionResult> Deposit(CashTxnVm m)
    {
        if (!ModelState.IsValid) return View("Cash", m.With(DepositTitle, "Deposit"));
        Voucher? voucher = null;
        var ok = await Run(async () =>
        {
            var accountId = await Lookups.AccountIdByNumberAsync(m.AccountNo);
            voucher = await transactions.DepositAsync(accountId, m.Amount, m.PartyName, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate);
        });
        if (!ok) return View("Cash", m.With(DepositTitle, "Deposit"));
        Ok($"Deposit voucher {voucher!.VoucherNo} created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id = voucher.Id });
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(CashTxnVm m)
    {
        if (!ModelState.IsValid) return View("Cash", m.With(WithdrawTitle, "Withdraw"));
        Voucher? voucher = null;
        var ok = await Run(async () =>
        {
            var accountId = await Lookups.AccountIdByNumberAsync(m.AccountNo);
            voucher = await transactions.WithdrawAsync(accountId, m.Amount, m.ChequeNo, m.PartyName, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate);
        });
        if (!ok) return View("Cash", m.With(WithdrawTitle, "Withdraw"));
        Ok($"Withdrawal voucher {voucher!.VoucherNo} created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id = voucher.Id });
    }

    [HttpGet] public IActionResult Transfer(string? from) => View(new TransferVm { FromNo = from ?? "" });

    [HttpPost]
    public async Task<IActionResult> Transfer(TransferVm m)
    {
        if (!ModelState.IsValid) return View(m);
        Voucher? voucher = null;
        var ok = await Run(async () =>
        {
            var fromId = await Lookups.AccountIdByNumberAsync(m.FromNo);
            var toId = await Lookups.AccountIdByNumberAsync(m.ToNo);
            voucher = await transactions.TransferAsync(fromId, toId, m.Amount, m.ChequeNo, m.Narration, Me.UserName, await Me.ProfileAsync(), m.ValueDate);
        });
        if (!ok) return View(m);
        Ok($"Transfer voucher {voucher!.VoucherNo} created - awaiting authorization.");
        return RedirectToAction(nameof(Details), new { id = voucher.Id });
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Journal()
    {
        ViewBag.Branches = await BranchList();
        return View(new JournalVm());
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Journal(JournalVm m)
    {
        Voucher? voucher = null;
        var ok = await Run(async () =>
        {
            var request = new JournalRequest(m.Description, m.ValueDate,
                m.Lines.Select(l => new JournalLineRequest(l.GlCode, l.BranchId, l.Debit, l.Credit, l.Narration)).ToList());
            voucher = await journals.CreateAsync(request, await MyBranchAsync(), Me.UserName, await Me.ProfileAsync());
        });
        if (ok)
        {
            Ok($"Journal voucher {voucher!.VoucherNo} created - awaiting authorization.");
            return RedirectToAction(nameof(Details), new { id = voucher.Id });
        }
        ViewBag.Branches = await BranchList();
        return View(m);
    }

    [Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Pending(int? branchId)
    {
        ViewBag.Branches = await BranchList(branchId);
        return View(await queries.PendingAsync(branchId));
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Authorize(int id, string? returnTo)
    {
        await Run(() => ledger.AuthorizeAsync(id, Me.UserName, Me.IsManagerLevel), "Voucher authorized and posted.");
        return returnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Pending));
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Reject(int id, string reason, string? returnTo)
    {
        await Run(() => ledger.RejectAsync(id, Me.UserName, reason), "Voucher rejected.");
        return returnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Pending));
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Reverse(int id, string reason)
    {
        Voucher? reversal = null;
        if (await Run(async () => reversal = await ledger.CreateReversalAsync(id, reason, Me.UserName, await Me.ProfileAsync())))
        {
            Ok($"Reversal voucher {reversal!.VoucherNo} created - awaiting authorization.");
            return RedirectToAction(nameof(Details), new { id = reversal.Id });
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var details = await queries.GetDetailsAsync(id);
        if (details == null) return NotFound();
        ViewBag.Currency = details.CurrencyCode;
        ViewBag.Reversal = details.Reversal;
        ViewBag.ReversedBy = details.ReversedBy;
        return View(details.Voucher);
    }

    public async Task<IActionResult> Register(DateTime? from, DateTime? to, VoucherStatus? status, VoucherType? type, string? q, int? branchId, bool csv = false)
    {
        var register = await queries.SearchAsync(new VoucherFilter(from, to, status, type, q, branchId));
        if (csv)
            return Csv("vouchers", ["Voucher", "Date", "Value date", "Branch", "Type", "Description", "Amount", "Status", "Maker", "Checker"],
                register.Rows.Select(v => new object?[] { v.VoucherNo, v.TxnDate.ToString("yyyy-MM-dd"), v.ValueDate.ToString("yyyy-MM-dd"), v.Branch?.Code, v.Type, v.Description, v.TotalAmount, v.Status, v.MakerId, v.CheckerId }));
        ViewBag.From = register.From;
        ViewBag.To = register.To;
        ViewBag.Q = q;
        ViewBag.Branches = await BranchList(branchId);
        return View(register.Rows);
    }
}
