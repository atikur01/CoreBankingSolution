namespace CoreBanking.Application.Features.Remittances;

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

public class RemittanceService(IApplicationDbContext db, LedgerService ledger, NumberService numbers, ParamService prm, SanctionService sanctions)
{
    private async Task EnsureIssuedAndSettledAsync(Remittance r)
    {
        if (r.Status != RemittanceStatus.Issued) throw new BankingException($"Instrument is {r.Status}.");
        if (r.IssueVoucherId is int iv && await db.Vouchers.AnyAsync(v => v.Id == iv && v.Status != VoucherStatus.Authorized))
            throw new BankingException("Issue voucher is not authorized yet.");
        var pendingAction = await db.Vouchers.AnyAsync(v => v.SourceId == r.Id && v.Status == VoucherStatus.Pending
            && ((v.SourceKind == "Remittance" && v.Type == VoucherType.RemittancePay) || v.SourceKind == "RemittanceCancel"));
        if (pendingAction) throw new BankingException("A payment or cancellation of this instrument is already pending authorization.");
    }

    private async Task<int> PayableGl(RemittanceType t) => await prm.GlIdAsync(t switch { RemittanceType.PO => "PoPayableGl", RemittanceType.DD => "DdPayableGl", _ => "TtPayableGl" });

    public async Task StopAsync(int id)
    {
        var instrument = await db.Remittances.FindAsync(id) ?? throw new BankingException("Instrument not found.");
        if (instrument.Status != RemittanceStatus.Issued) throw new BankingException("Only an issued instrument can be stopped.");
        instrument.Status = RemittanceStatus.Stopped;
        await db.SaveChangesAsync();
    }

    public async Task ReleaseAsync(int id)
    {
        var instrument = await db.Remittances.FindAsync(id) ?? throw new BankingException("Instrument not found.");
        if (instrument.Status != RemittanceStatus.Stopped) throw new BankingException("Only a stopped instrument can be released.");
        instrument.Status = RemittanceStatus.Issued;
        await db.SaveChangesAsync();
    }

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
        await EnsureIssuedAndSettledAsync(r);
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
        await EnsureIssuedAndSettledAsync(r);
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
