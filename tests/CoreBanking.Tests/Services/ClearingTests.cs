namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class ClearingTests(DatabaseFixture fx)
{
    private static int _instrument = 500_000;

    private static ClearingItem Item(Account acc, ClearingDirection direction, decimal amount, string? no = null) => new()
    {
        Direction = direction, InstrumentNo = no ?? Interlocked.Increment(ref _instrument).ToString(), AccountId = acc.Id,
        Amount = amount, BankName = "Other Bank", RoutingNo = "200271234"
    };

    private static Task<ClearingItem> RegisterAsync(World w, ClearingItem item) =>
        w.Do(s => s.Clearing.RegisterAsync(item, World.Maker, new UserProfile { BranchId = w.Branch.Id }));

    private static Task<ClearingItem> LoadAsync(World w, int id) => w.Read(db => db.ClearingItems.AsNoTracking().FirstAsync(c => c.Id == id));

    [Fact]
    public async Task Registering_validates_the_amount_and_blocks_duplicate_instruments()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();

        await Assert.ThrowsAsync<BankingException>(() => RegisterAsync(w, Item(acc, ClearingDirection.Outward, 0m)));
        var item = Item(acc, ClearingDirection.Outward, 1_000m, "777001");
        var saved = await RegisterAsync(w, item);
        var duplicate = Item(acc, ClearingDirection.Outward, 1_000m, "777001");

        Assert.Equal(ClearingStatus.Registered, saved.Status);
        Assert.Equal(w.Today, saved.RegisteredOn);
        await Assert.ThrowsAsync<BankingException>(() => RegisterAsync(w, duplicate));
    }

    [Fact]
    public async Task Settling_an_outward_cheque_credits_the_customer_after_authorization()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 2_000m);
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 3_000m));

        var v = await w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, new UserProfile { BranchId = w.Branch.Id }));
        Assert.Equal(2_000m, await w.BalanceAsync(acc.Id));
        await w.AuthorizeAsync(v.Id);

        Assert.Equal(5_000m, await w.BalanceAsync(acc.Id));
        var stored = await LoadAsync(w, item.Id);
        Assert.Equal(ClearingStatus.Settled, stored.Status);
        Assert.Equal(w.Today, stored.SettledOn);
        Assert.Equal(3_000m, await w.GlBalanceAsync("1210"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Inward_cheques_debit_the_drawer_and_need_sufficient_funds()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 10_000m);
        await w.Do(s => s.Deposits.IssueChequeBookAsync(acc.Id, 10, World.Maker, null));
        var leaf = (await w.Read(db => db.ChequeBooks.AsNoTracking().FirstAsync(b => b.AccountId == acc.Id))).FromNo.ToString();
        var good = await RegisterAsync(w, Item(acc, ClearingDirection.Inward, 4_000m, leaf));
        var tooBig = await RegisterAsync(w, Item(acc, ClearingDirection.Inward, 40_000m, (long.Parse(leaf) + 1).ToString()));

        var v = await w.Do(s => s.Clearing.SettleAsync(good.Id, World.Maker, profile));
        await w.AuthorizeAsync(v.Id);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.SettleAsync(tooBig.Id, World.Maker, profile)));

        Assert.Equal(6_000m, await w.BalanceAsync(acc.Id));
    }

    [Fact]
    public async Task An_item_cannot_be_settled_again_while_its_voucher_is_pending_or_after_settlement()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync();
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 500m));

        var v = await w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, profile));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, profile)));
        await w.AuthorizeAsync(v.Id);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, profile)));
    }

    [Fact]
    public async Task An_item_cannot_be_returned_while_its_settlement_voucher_is_pending()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync();
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 500m));
        var v = await w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, profile));

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.ReturnAsync(item.Id, "refer to drawer", World.Maker, profile)));
        await w.AuthorizeAsync(v.Id);

        Assert.Equal(ClearingStatus.Settled, (await LoadAsync(w, item.Id)).Status);
    }

    [Fact]
    public async Task A_returned_item_cannot_be_settled_again_even_after_a_rejected_settlement()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 500m));
        var plain = await w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, new UserProfile { BranchId = w.Branch.Id }));
        await w.Do(s => s.Ledger.RejectAsync(plain.Id, World.Checker, "re-do"));

        await w.Do(s => s.Clearing.ReturnAsync(item.Id, "refer to drawer", World.Maker, new UserProfile { BranchId = w.Branch.Id }));

        Assert.Equal(ClearingStatus.Returned, (await LoadAsync(w, item.Id)).Status);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.SettleAsync(item.Id, World.Maker, new UserProfile { BranchId = w.Branch.Id })));
    }

    [Fact]
    public async Task Returning_an_inward_cheque_records_a_return_and_charges_the_drawer()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 10_000m);
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Inward, 4_000m));

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.ReturnAsync(item.Id, " ", World.Maker, profile)));
        await w.Do(s => s.Clearing.ReturnAsync(item.Id, "insufficient funds", World.Maker, profile));

        var stored = await LoadAsync(w, item.Id);
        Assert.Equal(ClearingStatus.Returned, stored.Status);
        Assert.Equal("insufficient funds", stored.ReturnReason);
        Assert.Equal(10_000m - 862.5m, await w.BalanceAsync(acc.Id));
        Assert.Equal(1, await w.Read(db => db.ChequeReturns.CountAsync(r => r.AccountId == acc.Id)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Clearing.ReturnAsync(item.Id, "again", World.Maker, profile)));
    }

    [Fact]
    public async Task A_returned_instrument_can_be_registered_again()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync();
        var item = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 500m, "888001"));
        await w.Do(s => s.Clearing.ReturnAsync(item.Id, "stale", World.Maker, profile));

        var again = await RegisterAsync(w, Item(acc, ClearingDirection.Outward, 500m, "888001"));

        Assert.NotEqual(item.Id, again.Id);
    }
}
