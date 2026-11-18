using CoreBankingSolution.Data;
using CoreBankingSolution.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Services;

/// <summary>Teller / operations level transactions built on the ledger engine.</summary>
public class TransactionService(ApplicationDbContext db, LedgerService ledger, ParamService prm, SanctionService sanctions)
{
    private async Task<int> TellerBranch(UserProfile? p, Account a) => p?.BranchId ?? a.BranchId;

    public async Task<Voucher> DepositAsync(int accountId, decimal amount, string? depositor, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        var a = await db.Accounts.FindAsync(accountId) ?? throw new BankingException("Account not found.");
        if (!string.IsNullOrWhiteSpace(depositor))
        {
            var hits = await sanctions.ScreenAsync([depositor]);
            if (hits.Count > 0) throw new BankingException("Sanction screening hit on depositor: " + SanctionService.Format(hits));
        }
        var br = await TellerBranch(p, a);
        var req = new VoucherReq
        {
            Type = VoucherType.CashDeposit, BranchId = br, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Cash deposit {a.AccountNo}", CounterParty = depositor
        };
        req.Dr(await prm.GlIdAsync("CashGl"), null, amount, "Cash received", br);
        req.Cr(null, accountId, amount, narration ?? "Cash deposit");
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> WithdrawAsync(int accountId, decimal amount, string? chequeNo, string? withdrawer, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        var a = await db.Accounts.FindAsync(accountId) ?? throw new BankingException("Account not found.");
        if (!string.IsNullOrWhiteSpace(withdrawer))
        {
            var hits = await sanctions.ScreenAsync([withdrawer]);
            if (hits.Count > 0) throw new BankingException("Sanction screening hit on bearer/beneficiary: " + SanctionService.Format(hits));
        }
        var br = await TellerBranch(p, a);
        var req = new VoucherReq
        {
            Type = VoucherType.CashWithdrawal, BranchId = br, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Cash withdrawal {a.AccountNo}", ChequeNo = string.IsNullOrWhiteSpace(chequeNo) ? null : chequeNo.Trim(), CounterParty = withdrawer
        };
        req.Dr(null, accountId, amount, narration ?? "Cash withdrawal");
        req.Cr(await prm.GlIdAsync("CashGl"), null, amount, "Cash paid", br);
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> TransferAsync(int fromId, int toId, decimal amount, string? chequeNo, string? narration, string maker, UserProfile? p, DateTime? valueDate = null)
    {
        if (fromId == toId) throw new BankingException("Source and destination accounts must differ.");
        var from = await db.Accounts.FindAsync(fromId) ?? throw new BankingException("Source account not found.");
        var to = await db.Accounts.FindAsync(toId) ?? throw new BankingException("Destination account not found.");
        var hits = await sanctions.ScreenAsync([to.Title]);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit on beneficiary: " + SanctionService.Format(hits));
        var req = new VoucherReq
        {
            Type = VoucherType.Transfer, BranchId = p?.BranchId ?? from.BranchId, Maker = maker, MakerProfile = p, ValueDate = valueDate,
            Description = narration ?? $"Transfer {from.AccountNo} -> {to.AccountNo}", ChequeNo = string.IsNullOrWhiteSpace(chequeNo) ? null : chequeNo.Trim(), CounterParty = to.Title
        };
        req.Dr(null, fromId, amount, $"Transfer to {to.AccountNo}: {narration}");
        req.Cr(null, toId, amount, $"Transfer from {from.AccountNo}: {narration}");
        return await ledger.CreateAsync(req);
    }
}

public class RemittanceInput
{
    public RemittanceType Type { get; set; }
    public decimal Amount { get; set; }
    public string ApplicantName { get; set; } = "";
    public string BeneficiaryName { get; set; } = "";
    public string? BeneficiaryBank { get; set; }
    public string? BeneficiaryBranch { get; set; }
    public string? BeneficiaryAccountNo { get; set; }
    public int? SourceAccountId { get; set; }
}

public class RemittanceService(ApplicationDbContext db, LedgerService ledger, NumberService numbers, ParamService prm, SanctionService sanctions)
{
    private async Task<int> PayableGl(RemittanceType t) => await prm.GlIdAsync(t switch { RemittanceType.PO => "PoPayableGl", RemittanceType.DD => "DdPayableGl", _ => "TtPayableGl" });

    public async Task<Remittance> IssueAsync(RemittanceInput i, string maker, UserProfile? p)
    {
        if (i.Amount <= 0) throw new BankingException("Amount must be positive.");
        var hits = await sanctions.ScreenAsync([i.ApplicantName, i.BeneficiaryName]);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit: " + SanctionService.Format(hits));
        Account? src = i.SourceAccountId is int s ? await db.Accounts.FindAsync(s) : null;
        var branchId = p?.BranchId ?? src?.BranchId ?? throw new BankingException("Branch unknown.");
        var branch = (await db.Branches.FindAsync(branchId))!;
        var pctKey = i.Type switch { RemittanceType.PO => "PoCommissionPct", RemittanceType.DD => "DdCommissionPct", _ => "TtCommissionPct" };
        var comm = Math.Max(Money.R(i.Amount * await prm.DecAsync(pctKey) / 100m), await prm.DecAsync("MinRemittanceCommission"));
        var vat = Money.R(comm * await prm.DecAsync("VatRate") / 100m);

        var r = new Remittance
        {
            Type = i.Type, InstrumentNo = await numbers.NextInstrumentNoAsync(i.Type, branch.Code), Amount = Money.R(i.Amount), Commission = comm + vat,
            ApplicantName = i.ApplicantName, BeneficiaryName = i.BeneficiaryName, BeneficiaryBank = i.BeneficiaryBank,
            BeneficiaryBranch = i.BeneficiaryBranch, BeneficiaryAccountNo = i.BeneficiaryAccountNo, SourceAccountId = src?.Id,
            BranchId = branchId, IssueDate = branch.BusinessDate, Status = RemittanceStatus.Issued, MadeBy = maker
        };
        using var tx = await db.Database.BeginTransactionAsync();
        db.Remittances.Add(r); await db.SaveChangesAsync();
        var req = new VoucherReq
        {
            Type = VoucherType.RemittanceIssue, BranchId = branchId, Maker = maker, MakerProfile = p,
            Description = $"{i.Type} issue {r.InstrumentNo} to {i.BeneficiaryName}", SourceKind = "Remittance", SourceId = r.Id, CounterParty = i.BeneficiaryName
        };
        var total = r.Amount + comm + vat;
        if (src != null) req.Dr(null, src.Id, total, $"{i.Type} {r.InstrumentNo} + commission");
        else req.Dr(await prm.GlIdAsync("CashGl"), null, total, "Cash received", branchId);
        req.Cr(await PayableGl(i.Type), null, r.Amount, $"{i.Type} payable {r.InstrumentNo}", branchId);
        req.Cr(await prm.GlIdAsync("CommissionIncomeGl"), null, comm, "Remittance commission", branchId);
        req.Cr(await prm.GlIdAsync("VatPayableGl"), null, vat, "VAT on commission", branchId);
        var v = await ledger.CreateAsync(req);
        r.IssueVoucherId = v.Id; await db.SaveChangesAsync();
        await tx.CommitAsync();
        return r;
    }

    public async Task<Voucher> PayAsync(int id, int? creditAccountId, string maker, UserProfile? p)
    {
        var r = await db.Remittances.FindAsync(id) ?? throw new BankingException("Instrument not found.");
        if (r.Status != RemittanceStatus.Issued) throw new BankingException($"Instrument is {r.Status}.");
        if (r.IssueVoucherId is int iv && await db.Vouchers.AnyAsync(v => v.Id == iv && v.Status != VoucherStatus.Authorized))
            throw new BankingException("Issue voucher is not authorized yet.");
        var hits = await sanctions.ScreenAsync([r.BeneficiaryName]);
        if (hits.Count > 0) throw new BankingException("Sanction screening hit on beneficiary: " + SanctionService.Format(hits));
        var br = p?.BranchId ?? r.BranchId;
        var req = new VoucherReq
        {
            Type = VoucherType.RemittancePay, BranchId = br, Maker = maker, MakerProfile = p,
            Description = $"{r.Type} payment {r.InstrumentNo} to {r.BeneficiaryName}", SourceKind = "Remittance", SourceId = r.Id, CounterParty = r.BeneficiaryName
        };
        req.Dr(await PayableGl(r.Type), null, r.Amount, "Instrument paid", r.BranchId);
        if (creditAccountId is int c) req.Cr(null, c, r.Amount, $"{r.Type} {r.InstrumentNo} proceeds");
        else req.Cr(await prm.GlIdAsync("CashGl"), null, r.Amount, "Cash paid", br);
        return await ledger.CreateAsync(req);
    }

    public async Task<Voucher> CancelAsync(int id, string maker, UserProfile? p)
    {
        var r = await db.Remittances.FindAsync(id) ?? throw new BankingException("Instrument not found.");
        if (r.Status != RemittanceStatus.Issued) throw new BankingException($"Instrument is {r.Status}.");
        var req = new VoucherReq
        {
            Type = VoucherType.RemittancePay, BranchId = r.BranchId, Maker = maker, MakerProfile = p,
            Description = $"{r.Type} cancellation {r.InstrumentNo} (commission not refunded)", SourceKind = "RemittanceCancel", SourceId = r.Id
        };
        req.Dr(await PayableGl(r.Type), null, r.Amount, "Instrument cancelled", r.BranchId);
        if (r.SourceAccountId is int s) req.Cr(null, s, r.Amount, "Refund of cancelled " + r.InstrumentNo);
        else req.Cr(await prm.GlIdAsync("CashGl"), null, r.Amount, "Cash refunded", r.BranchId);
        return await ledger.CreateAsync(req);
    }
}

public class RemittanceHook(ApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind is not ("Remittance" or "RemittanceCancel") || v.SourceId is not int id) return;
        var r = await db.Remittances.FindAsync(id);
        if (r == null) return;
        if (v.SourceKind == "RemittanceCancel") r.Status = RemittanceStatus.Cancelled;
        else if (v.Type == VoucherType.RemittancePay) { r.Status = RemittanceStatus.Paid; r.PaidDate = v.TxnDate; r.PayVoucherId = v.Id; }
    }

    public async Task OnRejectedAsync(Voucher v)
    {
        if (v.SourceKind == "Remittance" && v.Type == VoucherType.RemittanceIssue && v.SourceId is int id)
        {
            var r = await db.Remittances.FindAsync(id);
            if (r != null) r.Status = RemittanceStatus.Cancelled;
        }
    }
}

public class ClearingService(ApplicationDbContext db, LedgerService ledger, DepositService deposits, ParamService prm)
{
    public async Task<ClearingItem> RegisterAsync(ClearingItem c, string maker, UserProfile? p)
    {
        var a = await db.Accounts.FindAsync(c.AccountId) ?? throw new BankingException("Account not found.");
        if (c.Amount <= 0) throw new BankingException("Amount must be positive.");
        if (await db.ClearingItems.AnyAsync(x => x.InstrumentNo == c.InstrumentNo && x.RoutingNo == c.RoutingNo && x.Direction == c.Direction && x.Status != ClearingStatus.Returned))
            throw new BankingException("This instrument is already registered in clearing.");
        c.BranchId = p?.BranchId ?? a.BranchId;
        c.RegisteredOn = (await db.Branches.FindAsync(c.BranchId))!.BusinessDate;
        c.Status = ClearingStatus.Registered; c.MadeBy = maker; c.Id = 0;
        db.ClearingItems.Add(c); await db.SaveChangesAsync();
        return c;
    }

    public async Task<Voucher> SettleAsync(int id, string maker, UserProfile? p)
    {
        var c = await db.ClearingItems.Include(x => x.Account).FirstAsync(x => x.Id == id);
        if (c.Status != ClearingStatus.Registered) throw new BankingException($"Item is {c.Status}.");
        if (c.VoucherId is int vid && await db.Vouchers.AnyAsync(v => v.Id == vid && v.Status == VoucherStatus.Pending))
            throw new BankingException("Settlement voucher is pending authorization.");
        var gl = await prm.GlIdAsync("ClearingGl");
        var req = new VoucherReq
        {
            Type = VoucherType.Clearing, BranchId = c.BranchId, Maker = maker, MakerProfile = p,
            Description = $"{c.Direction} clearing settlement {c.InstrumentNo} ({c.BankName})", SourceKind = "Clearing", SourceId = c.Id,
            ChequeNo = c.Direction == ClearingDirection.Inward ? c.InstrumentNo : null
        };
        if (c.Direction == ClearingDirection.Outward)
        {
            req.Dr(gl, null, c.Amount, "Clearing settlement", c.BranchId);
            req.Cr(null, c.AccountId, c.Amount, $"Outward clearing cheque {c.InstrumentNo}");
        }
        else
        {
            req.Dr(null, c.AccountId, c.Amount, $"Inward clearing cheque {c.InstrumentNo}");
            req.Cr(gl, null, c.Amount, "Clearing settlement", c.BranchId);
        }
        var v = await ledger.CreateAsync(req);
        c.VoucherId = v.Id; await db.SaveChangesAsync();
        return v;
    }

    public async Task ReturnAsync(int id, string reason, string maker, UserProfile? p)
    {
        var c = await db.ClearingItems.FindAsync(id) ?? throw new BankingException("Item not found.");
        if (c.Status != ClearingStatus.Registered) throw new BankingException($"Item is {c.Status}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("Return reason is required.");
        c.Status = ClearingStatus.Returned; c.ReturnReason = reason; c.SettledOn = (await db.Branches.FindAsync(c.BranchId))!.BusinessDate;
        await db.SaveChangesAsync();
        if (c.Direction == ClearingDirection.Inward)
            await deposits.ReturnChequeAsync(c.AccountId, c.InstrumentNo, c.Amount, reason, chargeFee: true, maker, p);
    }
}

public class ClearingHook(ApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind != "Clearing" || v.SourceId is not int id) return;
        var c = await db.ClearingItems.FindAsync(id);
        if (c != null) { c.Status = ClearingStatus.Settled; c.SettledOn = v.TxnDate; }
    }
    public Task OnRejectedAsync(Voucher v) => Task.CompletedTask;
}
