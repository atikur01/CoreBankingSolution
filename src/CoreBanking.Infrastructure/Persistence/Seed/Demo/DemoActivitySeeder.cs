
namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

internal sealed class DemoActivitySeeder(DemoState s)
{
    private static readonly ProductCategory[] Operating = [ProductCategory.Savings, ProductCategory.Current, ProductCategory.SND];
    private readonly Dictionary<Account, List<long>> _leaves = [];
    private readonly Dictionary<Account, DateTime> _bookDate = [];

    private static bool IsOperating(Account a) => Operating.Contains(a.Product!.Category);
    private IEnumerable<Account> Active => s.Accounts.Where(a => a.Status == AccountStatus.Active && IsOperating(a));
    private IEnumerable<Account> Seasoned => Active.Where(a => a.OpenedOn < s.Today.AddDays(-200)).OrderBy(a => a.OpenedOn).ThenBy(a => a.Id);

    public void Run()
    {
        Capital();
        Openings();
        ChequeBooks();
        StopPaymentsAndReturns();
        RandomTransactions();
        TodayActivity();
        Closures();
        StandingInstructions();
        Remittances();
        Clearing();
    }

    private void Capital()
    {
        foreach (var br in s.Branches)
        {
            var d = s.WorkDay(s.Today.AddDays(-560));
            var amount = br.Code == "001" ? 500_000_000m : 300_000_000m;
            s.Enqueue(d, () => s.Post(VoucherType.Journal, br, d, "Opening capital & vault funding",
                [new(s.Gl["1100"], null, amount, 0, "Vault funding"), new(s.Gl["3100"], null, 0, amount, "Paid-up capital")], maker: DemoState.OfficerUser));
        }
    }

    private void Openings()
    {
        foreach (var (a, amount) in s.InitialDeposits)
        {
            var type = a.Product!.Category == ProductCategory.FDR ? VoucherType.FdrOpen : VoucherType.AccountOpening;
            var desc = $"Account opening deposit {a.AccountNo}";
            if (a.Status == AccountStatus.PendingApproval)
            {
                s.CashIn(a, s.Today, amount, desc, type, "Account", a.Id, status: VoucherStatus.Pending);
                continue;
            }
            var d = a.OpenedOn;
            s.Enqueue(d, () => s.CashIn(a, d, amount, desc, type, "Account", a.Id));
            if (a.Product.Category == ProductCategory.DPS) DpsInstallments(a);
        }
    }

    private void DpsInstallments(Account a)
    {
        var months = (s.Today - a.OpenedOn).Days / 30;
        for (int k = 1; k <= months; k++)
        {
            var d = s.WorkDay(a.OpenedOn.AddMonths(k));
            if (d >= s.Today) break;
            var no = k + 1;
            s.Enqueue(d, () =>
            {
                s.CashIn(a, d, a.InstallmentAmount, $"DPS installment {no}/{a.TermMonths} - {a.AccountNo}", VoucherType.DpsInstallment, "Account", a.Id);
                a.InstallmentsPaid++;
            });
        }
    }

    private void ChequeBooks()
    {
        foreach (var a in s.Accounts.Where(x => x.Status == AccountStatus.Active && x.OpenedOn < s.Today.AddDays(-60)
                     && (x.Product!.Category == ProductCategory.Current || (x.Product.Category == ProductCategory.Savings && x.Id % 3 == 0))))
        {
            int leaves = a.Product!.Category == ProductCategory.Current ? 50 : 25;
            var issued = s.WorkDay(a.OpenedOn.AddDays(2));
            s.ChequeSeq++;
            long from = (s.ChequeSeq - 1) * 100 + 1000001;
            s.Db.ChequeBooks.Add(new ChequeBook { Account = a, FromNo = from, ToNo = from + leaves - 1, IssuedOn = issued, IssuedBy = DemoState.OfficerUser });
            _leaves[a] = Enumerable.Range(0, leaves).Select(x => from + x).ToList();
            _bookDate[a] = issued;
            s.Enqueue(issued, () => s.Charge(a, issued, $"Cheque book fee {a.AccountNo} ({from}-{from + leaves - 1})", a.Product.ChequeBookFee * leaves / 25m));
        }
    }

    private long TakeLeaf(Account a, bool last)
    {
        var list = _leaves[a];
        var i = last ? list.Count - 1 : 0;
        var no = list[i];
        list.RemoveAt(i);
        return no;
    }

    private void StopPaymentsAndReturns()
    {
        var holders = Seasoned.Where(a => _leaves.ContainsKey(a)).Take(6).ToList();
        for (int i = 0; i < holders.Count; i++)
        {
            var a = holders[i];
            var no = TakeLeaf(a, true).ToString();
            var d = s.RandomDay(s.Today.AddDays(-50), s.Today.AddDays(-12));
            if (i < 3)
            {
                var released = i == 2;
                s.Db.StopPayments.Add(new StopPayment
                {
                    Account = a, ChequeNo = no, Amount = 10000m * (i + 2), Reason = i == 0 ? "Cheque lost by drawer" : i == 1 ? "Disputed payment to supplier" : "Instruction withdrawn after settlement",
                    InstructionTime = d.AddHours(11), IsActive = !released, ReleasedOn = released ? d.AddDays(9) : null, MadeBy = DemoState.TellerUser
                });
                s.Enqueue(d, () => s.Charge(a, d, $"Stop payment fee cheque {no}", a.Product!.StopPaymentFee));
            }
            else
            {
                var fee = a.Product!.ChequeReturnFee;
                s.Db.ChequeReturns.Add(new ChequeReturn
                {
                    Account = a, ChequeNo = no, Amount = 15000m * (i - 1), Reason = i == 3 ? "Insufficient funds" : "Cheque is stale / out of date",
                    ReturnDate = d, ChargeApplied = Money.R(fee + fee * DemoState.VatRate / 100m)
                });
                s.Enqueue(d, () => s.Charge(a, d, $"Cheque dishonour charge {no}", fee));
            }
        }
    }

    private static decimal Round(decimal v, decimal step) => Math.Round(v / step) * step;

    private void RandomTransactions()
    {
        var eligible = s.Accounts.Where(a => IsOperating(a) && a.Status is AccountStatus.Active or AccountStatus.Frozen or AccountStatus.Closed or AccountStatus.Blocked).ToList();
        var targets = eligible.Where(a => a.Status == AccountStatus.Active).ToList();
        foreach (var a in eligible)
        {
            var corporate = a.Customer!.Type != CustomerType.Individual;
            var scale = corporate ? 10m : a.Product!.Category == ProductCategory.Current ? 3m : 1m;
            var from = a.OpenedOn.AddDays(1);
            if (_bookDate.TryGetValue(a, out var bd) && bd.AddDays(1) > from) from = bd.AddDays(1);
            var to = a.Status == AccountStatus.Closed ? s.Today.AddDays(-25) : s.Today.AddDays(-1);
            if (from >= to) continue;
            int n = a.Product!.Category == ProductCategory.Current ? s.Rng.Next(7, 13) : s.Rng.Next(3, 9);
            for (int k = 0; k < n; k++)
            {
                var d = s.RandomDay(from, to);
                if (d <= a.OpenedOn) continue;
                var roll = s.Rng.Next(100);
                if (roll < 45) ScheduleDeposit(a, d, Round(s.Rng.Next(10, 400) * 500m * scale, 500));
                else if (roll < 75) ScheduleWithdrawal(a, d, Round(s.Rng.Next(4, 160) * 500m * (corporate ? 6m : a.Product.Category == ProductCategory.Current ? 2m : 1m), 500), UseLeaf(a));
                else
                {
                    var peers = targets.Where(t => t != a && t.BranchId == a.BranchId && t.Customer != a.Customer && t.OpenedOn < d).ToList();
                    if (peers.Count == 0) { ScheduleDeposit(a, d, Round(s.Rng.Next(10, 200) * 500m * scale, 500)); continue; }
                    ScheduleTransfer(a, peers[s.Rng.Next(peers.Count)], d, Round(s.Rng.Next(6, 300) * 500m * scale, 500), UseLeaf(a));
                }
            }
        }
    }

    private string? UseLeaf(Account a) => _leaves.TryGetValue(a, out var l) && l.Count > 5 && s.Rng.Next(2) == 0 ? TakeLeaf(a, false).ToString() : null;

    private void ScheduleDeposit(Account a, DateTime d, decimal amount) =>
        s.Enqueue(d, () => s.CashIn(a, d, amount, $"Cash deposit {a.AccountNo}", depositor: a.Customer!.Name));

    private void ScheduleWithdrawal(Account a, DateTime d, decimal amount, string? chq) =>
        s.Enqueue(d, () =>
        {
            s.Fund(a, d, amount);
            s.CashOut(a, d, amount, $"Cash withdrawal {a.AccountNo}", chq, withdrawer: a.Customer!.Name);
        });

    private void ScheduleTransfer(Account from, Account to, DateTime d, decimal amount, string? chq) =>
        s.Enqueue(d, () =>
        {
            s.Fund(from, d, amount);
            s.Post(VoucherType.Transfer, s.BranchOf(from), d, $"Fund transfer {from.AccountNo} to {to.AccountNo}",
                [new(null, from, amount, 0, $"Transfer to {to.AccountNo}"), new(null, to, 0, amount, $"Transfer from {from.AccountNo}")], chequeNo: chq);
        });

    private void TodayActivity()
    {
        var t = s.Today;
        var corporates = Active.Where(a => a.Customer!.Type != CustomerType.Individual && a.Product!.Category == ProductCategory.Current).ToList();
        var savings = Seasoned.Where(a => a.Product!.Category == ProductCategory.Savings).ToList();
        var bigDeposits = new[] { 1500000m, 2250000m, 1000000m };
        for (int i = 0; i < Math.Min(corporates.Count, bigDeposits.Length); i++)
        {
            var a = corporates[i]; var amt = bigDeposits[i];
            s.Enqueue(t, () => s.CashIn(a, t, amt, $"Cash deposit {a.AccountNo}", depositor: a.Customer!.ContactPerson));
        }
        if (corporates.Count > 0)
        {
            var a = corporates[0];
            s.Enqueue(t, () => { s.Fund(a, t, 1200000m); s.CashOut(a, t, 1200000m, $"Cash withdrawal {a.AccountNo}", withdrawer: a.Customer!.ContactPerson); });
        }
        for (int i = 0; i < 5 && i < savings.Count; i++)
        {
            var a = savings[i];
            if (i % 2 == 0) ScheduleDeposit(a, t, Round(s.Rng.Next(20, 300) * 500m, 500));
            else ScheduleWithdrawal(a, t, Round(s.Rng.Next(10, 80) * 500m, 500), null);
        }
        if (savings.Count < 8) return;
        var p = savings.Skip(5).Take(3).ToList();
        var peer = savings.FirstOrDefault(x => x.BranchId == p[2].BranchId && x != p[2]) ?? p[0];
        s.CashIn(p[0], t, 120000m, $"Cash deposit {p[0].AccountNo}", status: VoucherStatus.Pending, depositor: p[0].Customer!.Name);
        s.CashIn(p[1], t, 750000m, $"Cash deposit {p[1].AccountNo}", status: VoucherStatus.Pending, depositor: p[1].Customer!.Name);
        s.CashOut(p[2], t, 40000m, $"Cash withdrawal {p[2].AccountNo}", status: VoucherStatus.Pending, withdrawer: p[2].Customer!.Name);
        s.Post(VoucherType.Transfer, s.BranchOf(p[2]), t, $"Fund transfer {p[2].AccountNo} to {peer.AccountNo}",
            [new(null, p[2], 25000m, 0, "Transfer out"), new(null, peer, 0, 25000m, "Transfer in")], VoucherStatus.Pending);
        var y = s.WorkDay(t.AddDays(-1));
        var r = savings[0];
        s.Enqueue(y, () => s.CashOut(r, y, 85000m, $"Cash withdrawal {r.AccountNo}", status: VoucherStatus.Rejected, rejectReason: "Signature does not match the specimen on file"));
    }

    private void Closures()
    {
        foreach (var a in s.Accounts.Where(x => x.Status == AccountStatus.Closed))
        {
            var d = s.WorkDay(s.Today.AddDays(-20));
            a.ClosedOn = d;
            s.Enqueue(d, () =>
            {
                s.Charge(a, d, $"Account closing fee {a.AccountNo}", a.Product!.ClosingFee);
                if (a.Balance > 0) s.CashOut(a, d, a.Balance, $"Account closure payout {a.AccountNo}", sourceKind: "AccountClose", sourceId: a.Id, withdrawer: a.Customer!.Name);
            });
        }
    }

    private void StandingInstructions()
    {
        var pool = Seasoned.Where(a => a.Product!.Category == ProductCategory.Savings).ToList();
        var pairs = new List<(Account From, Account To)>();
        foreach (var a in pool)
        {
            var peer = pool.FirstOrDefault(b => b != a && b.BranchId == a.BranchId);
            if (peer != null && pairs.All(p => p.From != a)) pairs.Add((a, peer));
        }
        var plans = new (SiFrequency Freq, int Runs, SiStatus Status, decimal Amount)[]
        {
            (SiFrequency.Monthly, 4, SiStatus.Active, 5000), (SiFrequency.Monthly, 5, SiStatus.Active, 12000), (SiFrequency.Weekly, 6, SiStatus.Active, 2000),
            (SiFrequency.Quarterly, 2, SiStatus.Active, 25000), (SiFrequency.Monthly, 2, SiStatus.Paused, 8000), (SiFrequency.Monthly, 3, SiStatus.Completed, 3000), (SiFrequency.Monthly, 1, SiStatus.Cancelled, 6000)
        };
        for (int i = 0; i < Math.Min(plans.Length, pairs.Count); i++)
        {
            var (from, to) = pairs[i]; var pl = plans[i];
            var step = (int)pl.Freq;
            var start = s.Today.AddDays(-step * pl.Runs + s.Rng.Next(1, 20));
            var last = start.AddDays(step * (pl.Runs - 1));
            var si = new StandingInstruction
            {
                FromAccount = from, ToAccount = to, Amount = pl.Amount, Frequency = pl.Freq, StartDate = start, Status = pl.Status,
                Narration = i % 2 == 0 ? "Monthly savings transfer" : "Family support", ExecutedCount = pl.Runs, MadeBy = DemoState.OfficerUser,
                NextRunDate = pl.Status == SiStatus.Completed ? last : start.AddDays(step * pl.Runs), EndDate = pl.Status == SiStatus.Completed ? last : null,
                LastResult = "Executed successfully"
            };
            if (i == 3) { si.FailedCount = 1; si.LastResult = "Failed once: insufficient funds; retried and executed"; }
            s.Db.StandingInstructions.Add(si);
            for (int j = 0; j < pl.Runs; j++)
            {
                var d = s.WorkDay(start.AddDays(step * j));
                if (d >= s.Today) continue;
                var amount = pl.Amount;
                s.Enqueue(d, () =>
                {
                    s.Fund(from, d, amount);
                    s.Post(VoucherType.StandingInstruction, s.BranchOf(from), d, $"Standing instruction {from.AccountNo} to {to.AccountNo}",
                        [new(null, from, amount, 0, "Standing instruction"), new(null, to, 0, amount, "Standing instruction")], system: true);
                });
            }
        }
    }

    private void Remittances()
    {
        var sources = Seasoned.Where(a => a.Product!.Category == ProductCategory.Savings).ToList();
        if (sources.Count == 0) return;
        var plan = new (RemittanceType Type, RemittanceStatus Status, decimal Amount)[]
        {
            (RemittanceType.PO, RemittanceStatus.Paid, 50000), (RemittanceType.PO, RemittanceStatus.Issued, 120000), (RemittanceType.DD, RemittanceStatus.Paid, 300000),
            (RemittanceType.DD, RemittanceStatus.Issued, 75000), (RemittanceType.DD, RemittanceStatus.Cancelled, 200000), (RemittanceType.TT, RemittanceStatus.Paid, 450000),
            (RemittanceType.TT, RemittanceStatus.Issued, 1000000), (RemittanceType.TT, RemittanceStatus.Stopped, 90000), (RemittanceType.PO, RemittanceStatus.Paid, 35000)
        };
        var rates = new Dictionary<RemittanceType, (decimal Pct, string Gl)>
        {
            [RemittanceType.PO] = (0.15m, "2200"), [RemittanceType.DD] = (0.15m, "2210"), [RemittanceType.TT] = (0.25m, "2220")
        };
        for (int i = 0; i < plan.Length; i++)
        {
            var (type, status, amount) = plan[i];
            var src = sources[i % sources.Count];
            var br = s.BranchOf(src);
            var issueDate = status == RemittanceStatus.Issued ? s.RandomDay(s.Today.AddDays(-12), s.Today.AddDays(-2)) : s.RandomDay(s.Today.AddDays(-70), s.Today.AddDays(-20));
            var comm = Math.Max(Money.R(amount * rates[type].Pct / 100m), 50m);
            var vat = Money.R(comm * DemoState.VatRate / 100m);
            var bank = DemoCatalog.Banks[i % DemoCatalog.Banks.Length];
            var r = new Remittance
            {
                Type = type, InstrumentNo = s.InstrumentNo(type, br), Amount = amount, Commission = comm + vat, ApplicantName = src.Customer!.Name,
                BeneficiaryName = DemoCatalog.Beneficiaries[i % DemoCatalog.Beneficiaries.Length], BeneficiaryBank = type == RemittanceType.TT ? "Standard Chartered Bank, Singapore" : bank,
                BeneficiaryBranch = type == RemittanceType.TT ? "Raffles Place" : "Main Branch", BeneficiaryAccountNo = type == RemittanceType.PO ? null : $"{s.Rng.NextInt64(1000000000, 9999999999)}",
                SourceAccount = src, BranchId = br.Id, IssueDate = issueDate, Status = status, MadeBy = DemoState.OfficerUser
            };
            s.Db.Remittances.Add(r);
            var payableGl = s.Gl[rates[type].Gl];
            s.Enqueue(issueDate, () =>
            {
                s.Fund(src, issueDate, amount + comm + vat);
                var v = s.Post(VoucherType.RemittanceIssue, br, issueDate, $"{type} issue {r.InstrumentNo} to {r.BeneficiaryName}",
                [
                    new(null, src, amount + comm + vat, 0, $"{type} {r.InstrumentNo} + commission"), new(payableGl, null, 0, amount, $"{type} payable {r.InstrumentNo}"),
                    new(s.Gl["4500"], null, 0, comm, "Remittance commission"), new(s.Gl["2610"], null, 0, vat, "VAT on commission")
                ], sourceKind: "Remittance", counterParty: r.BeneficiaryName);
                s.Defer(() => { r.IssueVoucherId = v.Id; v.SourceId = r.Id; });
            });
            if (status == RemittanceStatus.Paid)
            {
                var payDate = s.WorkDay(issueDate.AddDays(s.Rng.Next(2, 9)));
                r.PaidDate = payDate;
                s.Enqueue(payDate, () =>
                {
                    var v = s.Post(VoucherType.RemittancePay, br, payDate, $"{type} payment {r.InstrumentNo} to {r.BeneficiaryName}",
                        [new(payableGl, null, amount, 0, "Instrument paid"), new(s.Gl["1100"], null, 0, amount, "Cash paid")], sourceKind: "Remittance", counterParty: r.BeneficiaryName);
                    s.Defer(() => { r.PayVoucherId = v.Id; v.SourceId = r.Id; });
                });
            }
            else if (status == RemittanceStatus.Cancelled)
            {
                var d = s.WorkDay(issueDate.AddDays(3));
                s.Enqueue(d, () =>
                {
                    var v = s.Post(VoucherType.RemittancePay, br, d, $"{type} cancellation {r.InstrumentNo} (commission not refunded)",
                        [new(payableGl, null, amount, 0, "Instrument cancelled"), new(null, src, 0, amount, $"Refund of cancelled {r.InstrumentNo}")], sourceKind: "RemittanceCancel");
                    s.Defer(() => v.SourceId = r.Id);
                });
            }
        }
    }

    private void Clearing()
    {
        var pool = Seasoned.Where(a => a.Product!.Category is ProductCategory.Savings or ProductCategory.Current).Skip(2).ToList();
        if (pool.Count == 0) return;
        var items = new (ClearingDirection Dir, ClearingStatus Status, decimal Amount)[]
        {
            (ClearingDirection.Outward, ClearingStatus.Settled, 85000), (ClearingDirection.Outward, ClearingStatus.Settled, 240000), (ClearingDirection.Outward, ClearingStatus.Registered, 150000),
            (ClearingDirection.Outward, ClearingStatus.Returned, 60000), (ClearingDirection.Inward, ClearingStatus.Settled, 45000), (ClearingDirection.Inward, ClearingStatus.Settled, 130000),
            (ClearingDirection.Inward, ClearingStatus.Registered, 95000), (ClearingDirection.Inward, ClearingStatus.Returned, 500000)
        };
        var clearingGl = s.Gl["1210"];
        for (int i = 0; i < items.Length; i++)
        {
            var (dir, status, amount) = items[i];
            var a = pool[i % pool.Count];
            var br = s.BranchOf(a);
            var reg = status == ClearingStatus.Registered ? s.WorkDay(s.Today.AddDays(-1)) : s.RandomDay(s.Today.AddDays(-30), s.Today.AddDays(-4));
            var settled = s.WorkDay(reg.AddDays(2));
            var no = $"{s.Rng.Next(1000000, 9999999)}";
            var ci = new ClearingItem
            {
                Direction = dir, InstrumentNo = no, BankName = DemoCatalog.Banks[(i + 1) % DemoCatalog.Banks.Length], BranchName = "Principal Branch",
                RoutingNo = $"2002{s.Rng.Next(10000, 99999)}", Amount = amount, Account = a, BranchId = br.Id, RegisteredOn = reg, Status = status, MadeBy = DemoState.TellerUser
            };
            s.Db.ClearingItems.Add(ci);
            if (status == ClearingStatus.Registered) continue;
            ci.SettledOn = settled;
            if (status == ClearingStatus.Returned)
            {
                ci.ReturnReason = dir == ClearingDirection.Inward ? "Insufficient funds" : "Drawer's signature differs";
                if (dir == ClearingDirection.Inward) ScheduleInwardReturn(a, no, amount, settled, ci.ReturnReason);
                continue;
            }
            s.Enqueue(settled, () =>
            {
                if (dir == ClearingDirection.Inward) s.Fund(a, settled, amount);
                var lines = dir == ClearingDirection.Outward
                    ? new Ln[] { new(clearingGl, null, amount, 0, "Clearing settlement"), new(null, a, 0, amount, $"Outward clearing cheque {no}") }
                    : [new(null, a, amount, 0, $"Inward clearing cheque {no}"), new(clearingGl, null, 0, amount, "Clearing settlement")];
                var v = s.Post(VoucherType.Clearing, br, settled, $"{dir} clearing settlement {no} ({ci.BankName})", lines, sourceKind: "Clearing", chequeNo: dir == ClearingDirection.Inward ? no : null);
                s.Defer(() => { ci.VoucherId = v.Id; v.SourceId = ci.Id; });
            });
        }
    }

    private void ScheduleInwardReturn(Account a, string no, decimal amount, DateTime d, string reason)
    {
        var fee = a.Product!.ChequeReturnFee;
        s.Db.ChequeReturns.Add(new ChequeReturn { Account = a, ChequeNo = no, Amount = amount, Reason = reason, ReturnDate = d, ChargeApplied = Money.R(fee + fee * DemoState.VatRate / 100m) });
        s.Enqueue(d, () => s.Charge(a, d, $"Cheque dishonour charge {no}", fee));
    }
}
