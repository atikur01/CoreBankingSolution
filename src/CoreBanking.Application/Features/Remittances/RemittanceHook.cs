namespace CoreBanking.Application.Features.Remittances;

public class RemittanceHook(IApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind is not ("Remittance" or "RemittanceCancel") || v.SourceId is not int id) return;
        var r = await db.Remittances.FindAsync(id);
        if (r == null) return;
        if (v.SourceKind == "RemittanceCancel" || v.Type == VoucherType.RemittancePay)
        {
            if (r.Status != RemittanceStatus.Issued)
                throw new BankingException($"Instrument {r.InstrumentNo} is {r.Status}; this voucher can no longer be authorized.");
        }
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
