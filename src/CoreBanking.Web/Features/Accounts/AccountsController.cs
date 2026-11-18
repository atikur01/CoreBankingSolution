using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Accounts;

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

    public AccountOpeningRequest ToRequest() => new()
    {
        Cif = Cif, JointCifs = JointCifs, ProductId = ProductId, BranchId = BranchId, Title = Title, Mode = Mode, InitialDeposit = InitialDeposit,
        SourceAccountNo = SourceAccountNo, LinkedAccountNo = LinkedAccountNo, TermMonths = TermMonths, InstallmentAmount = InstallmentAmount,
        AutoRenew = AutoRenew, OdLimit = OdLimit, SingleDebitLimit = SingleDebitLimit, DailyDebitLimit = DailyDebitLimit,
        MonthlyDebitLimit = MonthlyDebitLimit, PowerOfAttorney = PowerOfAttorney,
        Nominees = [new NomineeRequest(N1Name, N1Relation, N1Nid, N1Share), new NomineeRequest(N2Name, N2Relation, N2Nid, N2Share)]
    };
}

public class StatementVm
{
    public Account Account { get; set; } = null!;
    public List<AccountTransaction> Rows { get; set; } = [];
}

[Authorize(Roles = Roles.Makers)]
public class AccountsController(
    ICurrentUser me, LookupService lookups, AccountQueries queries, AccountOpeningService opening,
    AccountAdministrationService administration, DepositService deposits) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(string? q, AccountStatus? status, int? branchId)
    {
        ViewBag.Q = q;
        ViewBag.Status = status;
        ViewBag.Branches = await BranchList(branchId);
        return View(await queries.SearchAsync(q, status, branchId));
    }

    [HttpGet]
    public async Task<IActionResult> Lookup(string no)
    {
        var result = await queries.LookupAsync(no);
        return Json(new { found = result.Found, title = result.Title, product = result.Product, status = result.Status, balance = result.Balance, branch = result.Branch });
    }

    private async Task LoadFormListsAsync(int? branch = null)
    {
        var products = (await Lookups.ActiveProductsAsync(loans: false));
        ViewBag.Products = ToSelect(products.Select(p => new LookupItem(p.Id, p.Code + " - " + p.Name)));
        ViewBag.Cats = JsonSerializer.Serialize(products.ToDictionary(p => p.Id.ToString(), p => p.Category.ToString()));
        ViewBag.Branches = await BranchList(branch ?? await MyBranchAsync());
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Open(string? cif)
    {
        await LoadFormListsAsync();
        return View(new OpenAccountVm { Cif = cif ?? "", BranchId = await MyBranchAsync() });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Open(OpenAccountVm m)
    {
        if (!ModelState.IsValid) { await LoadFormListsAsync(m.BranchId); return View(m); }
        Account? created = null;
        var ok = await Run(async () => created = await opening.OpenAsync(m.ToRequest(), Me.UserName, await Me.ProfileAsync()),
            "Account created. It will become active once the opening voucher is authorized by a checker.");
        if (!ok || created == null) { await LoadFormListsAsync(m.BranchId); return View(m); }
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var details = await queries.GetDetailsAsync(id);
        if (details == null) return NotFound();
        ViewBag.Txns = details.Transactions;
        ViewBag.Books = details.Books;
        ViewBag.Stops = details.Stops;
        ViewBag.Returns = details.Returns;
        ViewBag.Pending = details.PendingVouchers;
        ViewBag.Sis = details.StandingInstructions;
        return View(details.Account);
    }

    public async Task<IActionResult> Statement(int id, DateTime? from, DateTime? to, bool csv = false)
    {
        var statement = await queries.GetStatementAsync(id, from, to);
        if (statement == null) return NotFound();
        if (csv)
            return Csv("statement-" + statement.Account.AccountNo, ["Date", "Value date", "Voucher", "Narration", "Cheque", "Debit", "Credit", "Balance"],
                statement.Rows.Select(r => new object?[] { r.TxnDate.ToString("yyyy-MM-dd"), r.ValueDate.ToString("yyyy-MM-dd"), r.Voucher?.VoucherNo, r.Narration, r.ChequeNo, r.Debit, r.Credit, r.RunningBalance }));
        ViewBag.From = statement.From;
        ViewBag.To = statement.To;
        ViewBag.Opening = statement.Opening;
        return View(new StatementVm { Account = statement.Account, Rows = statement.Rows });
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public Task<IActionResult> Approve(int id) =>
        Back(id, () => deposits.ApproveZeroBalanceAsync(id, Me.UserName), "Account approved and activated.");

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> SetStatus(int id, AccountStatus status, string? reason)
    {
        string? message = null;
        if (await Run(async () => message = await administration.SetStatusAsync(id, status, reason))) Ok(message!);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> Limits(int id, decimal odLimit, decimal single, decimal daily, decimal monthly, string? poa, decimal lien) =>
        Back(id, () => administration.UpdateLimitsAsync(id, new AccountLimitsRequest(odLimit, single, daily, monthly, poa, lien), Me.IsChecker), "Limits updated.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> IssueCheque(int id, int leaves) =>
        Back(id, async () => await deposits.IssueChequeBookAsync(id, leaves, Me.UserName, await Me.ProfileAsync()),
            "Cheque book issued (fee voucher, if any, awaits authorization).");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> StopPay(int id, string chequeNo, decimal amount, string reason) =>
        Back(id, async () => await deposits.StopPaymentAsync(id, chequeNo.Trim(), amount, reason, Me.UserName, await Me.ProfileAsync()), "Stop payment recorded.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> ReleaseStop(int id, int stopId) =>
        Back(id, () => administration.ReleaseStopPaymentAsync(id, stopId), "Stop payment released.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> ReturnCheque(int id, string chequeNo, decimal amount, string reason, bool charge) =>
        Back(id, async () => await deposits.ReturnChequeAsync(id, chequeNo.Trim(), amount, reason, charge, Me.UserName, await Me.ProfileAsync()),
            "Cheque return recorded in the Cheque Return Register.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> PayDps(int id, string? sourceAccountNo) =>
        Back(id, async () =>
        {
            var source = await Lookups.OptionalAccountIdAsync(sourceAccountNo, "Source account not found.");
            await deposits.PayDpsInstallmentAsync(id, source, Me.UserName, await Me.ProfileAsync());
        }, "DPS installment voucher created - awaiting authorization.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> Encash(int id, bool premature, bool toCash) =>
        Back(id, async () => await deposits.EncashAsync(id, premature, toCash, Me.UserName, await Me.ProfileAsync()),
            "Encashment voucher created - awaiting authorization.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> CloseAccount(int id) =>
        Back(id, async () => await deposits.CloseAsync(id, Me.UserName, await Me.ProfileAsync()),
            "Closing voucher created - the account closes when it is authorized.");

    private async Task<IActionResult> Back(int id, Func<Task> work, string success)
    {
        await Run(work, success);
        return RedirectToAction(nameof(Details), new { id });
    }
}
