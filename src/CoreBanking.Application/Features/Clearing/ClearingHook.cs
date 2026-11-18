namespace CoreBanking.Application.Features.Clearing;

public class ClearingHook(IApplicationDbContext db) : IVoucherHook
{
    public async Task OnAuthorizedAsync(Voucher v)
    {
        if (v.SourceKind != "Clearing" || v.SourceId is not int id) return;
        var c = await db.ClearingItems.FindAsync(id);
        if (c == null) return;
        if (c.Status != ClearingStatus.Registered)
            throw new BankingException($"Clearing item {c.InstrumentNo} is {c.Status}; its settlement voucher can no longer be authorized.");
        c.Status = ClearingStatus.Settled;
        c.SettledOn = v.TxnDate;
    }
    public Task OnRejectedAsync(Voucher v) => Task.CompletedTask;
}
