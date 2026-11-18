namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class ApplicationFeatureTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Account_status_changes_need_a_reason_and_respect_the_account_state()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 5_000m);

        await w.Do(async s =>
        {
            var admin = s.Resolve<AccountAdministrationService>();
            await Assert.ThrowsAsync<BankingException>(() => admin.SetStatusAsync(acc.Id, AccountStatus.Frozen, " "));
            await Assert.ThrowsAsync<BankingException>(() => admin.SetStatusAsync(acc.Id, AccountStatus.Matured, "x"));
            await Assert.ThrowsAsync<BankingException>(() => admin.SetStatusAsync(acc.Id, AccountStatus.PendingApproval, "x"));
            await Assert.ThrowsAsync<BankingException>(() => admin.SetStatusAsync(acc.Id, AccountStatus.Active, "no-op"));
            await Assert.ThrowsAsync<BankingException>(() => admin.SetStatusAsync(acc.Id, AccountStatus.Closed, "has money"));
            var message = await admin.SetStatusAsync(acc.Id, AccountStatus.Frozen, "court order");
            Assert.Contains("Active -> Frozen", message);
        });

        Assert.Equal(AccountStatus.Frozen, (await w.AccountAsync(acc.Id)).Status);
    }

    [Fact]
    public async Task Reactivating_a_dormant_account_restarts_its_idle_clock()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        await w.UpdateAccountAsync(acc.Id, a => { a.Status = AccountStatus.Dormant; a.LastTxnDate = w.Today.AddDays(-500); });

        await w.Do(s => s.Resolve<AccountAdministrationService>().SetStatusAsync(acc.Id, AccountStatus.Active, "customer visited"));

        Assert.Equal(w.Today, (await w.AccountAsync(acc.Id)).LastTxnDate);
    }

    [Fact]
    public async Task A_zero_balance_account_can_be_closed_unless_it_settles_a_running_loan()
    {
        var w = await World.CreateAsync(fx);
        var empty = await w.OpenAccountAsync(deposit: 600m);
        await w.WithdrawAsync(empty, 600m);

        await w.Do(s => s.Resolve<AccountAdministrationService>().SetStatusAsync(empty.Id, AccountStatus.Closed, "customer request"));

        var stored = await w.AccountAsync(empty.Id);
        Assert.Equal(AccountStatus.Closed, stored.Status);
        Assert.Equal(w.Today, stored.ClosedOn);
    }

    [Fact]
    public async Task Limit_updates_validate_amounts_lien_and_the_overdraft_product_rule()
    {
        var w = await World.CreateAsync(fx);
        var savings = await w.OpenAccountAsync(deposit: 5_000m);
        var current = await w.OpenAccountAsync(ProductCategory.Current, 10_000m);

        await w.Do(async s =>
        {
            var admin = s.Resolve<AccountAdministrationService>();
            await Assert.ThrowsAsync<BankingException>(() => admin.UpdateLimitsAsync(savings.Id, new AccountLimitsRequest(0, -1, 0, 0, null, 0), true));
            await Assert.ThrowsAsync<BankingException>(() => admin.UpdateLimitsAsync(savings.Id, new AccountLimitsRequest(0, 0, 0, 0, null, 9_999m), true));
            await Assert.ThrowsAsync<BankingException>(() => admin.UpdateLimitsAsync(savings.Id, new AccountLimitsRequest(0, 0, 0, 0, null, 100m), false));
            await Assert.ThrowsAsync<BankingException>(() => admin.UpdateLimitsAsync(savings.Id, new AccountLimitsRequest(1_000m, 0, 0, 0, null, 0), true));
            await admin.UpdateLimitsAsync(current.Id, new AccountLimitsRequest(5_000m, 1_000m, 2_000m, 8_000m, "Attorney", 500m), true);
        });

        var stored = await w.AccountAsync(current.Id);
        Assert.Equal((5_000m, 1_000m, 2_000m, 8_000m, 500m), (stored.OdLimit, stored.SingleDebitLimit, stored.DailyDebitLimit, stored.MonthlyDebitLimit, stored.LienAmount));
        Assert.Equal("Attorney", stored.PowerOfAttorney);
    }

    [Fact]
    public async Task Releasing_a_stop_payment_works_once_and_only_for_the_owning_account()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);
        var other = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);
        await w.Do(s => s.Deposits.IssueChequeBookAsync(acc.Id, 10, World.Maker, null));
        var leaf = (await w.Read(db => db.ChequeBooks.AsNoTracking().FirstAsync(b => b.AccountId == acc.Id))).FromNo.ToString();
        await w.Do(s => s.Deposits.StopPaymentAsync(acc.Id, leaf, 100m, "lost", World.Maker, null));
        var stop = await w.Read(db => db.StopPayments.AsNoTracking().FirstAsync(x => x.AccountId == acc.Id));

        await w.Do(async s =>
        {
            var admin = s.Resolve<AccountAdministrationService>();
            await Assert.ThrowsAsync<BankingException>(() => admin.ReleaseStopPaymentAsync(other.Id, stop.Id));
            await admin.ReleaseStopPaymentAsync(acc.Id, stop.Id);
            await Assert.ThrowsAsync<BankingException>(() => admin.ReleaseStopPaymentAsync(acc.Id, stop.Id));
        });

        Assert.False((await w.Read(db => db.StopPayments.AsNoTracking().FirstAsync(x => x.Id == stop.Id))).IsActive);
        var v = await w.Do(s => s.Transactions.WithdrawAsync(acc.Id, 100m, leaf, null, null, World.Maker, null));
        Assert.Equal(VoucherStatus.Pending, v.Status);
    }

    [Fact]
    public async Task Account_opening_resolves_references_by_number_and_reports_unknown_ones()
    {
        var w = await World.CreateAsync(fx);
        var customer = await w.NewCustomerAsync();
        var product = await w.ProductAsync(ProductCategory.Savings);

        await w.Do(async s =>
        {
            var opening = s.Resolve<AccountOpeningService>();
            await Assert.ThrowsAsync<BankingException>(() => opening.OpenAsync(new AccountOpeningRequest { Cif = "0000000000", ProductId = product.Id, BranchId = w.Branch.Id, InitialDeposit = 5_000m }, World.Maker, null));
            await Assert.ThrowsAsync<BankingException>(() => opening.OpenAsync(new AccountOpeningRequest { Cif = customer.CifNo, ProductId = product.Id, BranchId = w.Branch.Id, InitialDeposit = 5_000m, JointCifs = "9999999999" }, World.Maker, null));
            var account = await opening.OpenAsync(new AccountOpeningRequest
            {
                Cif = " " + customer.CifNo + " ", ProductId = product.Id, BranchId = w.Branch.Id, InitialDeposit = 5_000m,
                Nominees = [new NomineeRequest("Heir", "Son", null, 100m), new NomineeRequest("", null, null, 0m)]
            }, World.Maker, null);
            Assert.Equal(customer.Id, account.CustomerId);
        });
    }

    [Fact]
    public async Task Duplicate_nids_are_detected_on_create_and_on_edit_but_not_against_the_same_customer()
    {
        var w = await World.CreateAsync(fx);
        var first = await w.NewCustomerAsync();
        var second = await w.NewCustomerAsync();

        await w.Do(async s =>
        {
            var customers = s.Resolve<CustomerService>();
            var clash = new Customer { Type = CustomerType.Individual, Nid = first.Nid };
            Assert.Contains(await customers.ValidateAsync(clash), i => i.Field == "Nid");
            Assert.Contains(await customers.ValidateAsync(clash, second.Id), i => i.Field == "Nid");
            Assert.DoesNotContain(await customers.ValidateAsync(clash, first.Id), i => i.Field == "Nid");
        });
    }

    [Fact]
    public async Task Customer_validation_requires_identity_documents_and_licences_by_type()
    {
        var w = await World.CreateAsync(fx);

        await w.Do(async s =>
        {
            var customers = s.Resolve<CustomerService>();
            Assert.Contains(await customers.ValidateAsync(new Customer { Type = CustomerType.Individual }), i => i.Field == "Nid");
            Assert.Contains(await customers.ValidateAsync(new Customer { Type = CustomerType.Staff }), i => i.Field == "Nid");
            Assert.Empty(await customers.ValidateAsync(new Customer { Type = CustomerType.Minor }));
            Assert.Empty(await customers.ValidateAsync(new Customer { Type = CustomerType.Individual, Passport = "AB1234567" }));
            Assert.Contains(await customers.ValidateAsync(new Customer { Type = CustomerType.PrivateLtd }), i => i.Field == "TradeLicense");
            Assert.Empty(await customers.ValidateAsync(new Customer { Type = CustomerType.PrivateLtd, TradeLicense = "TL-1" }));
        });
    }

    [Fact]
    public async Task Kyc_verification_enforces_maker_checker_photo_and_screening()
    {
        var w = await World.CreateAsync(fx);
        var pending = await w.NewCustomerAsync(verified: false);
        var corporate = await w.NewCustomerAsync(verified: false, type: CustomerType.PrivateLtd);

        await w.Do(async s =>
        {
            var customers = s.Resolve<CustomerService>();
            await Assert.ThrowsAsync<BankingException>(() => customers.VerifyKycAsync(pending.Id, true, null, World.Maker));
            var noPhoto = await Assert.ThrowsAsync<BankingException>(() => customers.VerifyKycAsync(pending.Id, true, null, World.Checker));
            Assert.Contains("photograph", noPhoto.Message);
            Assert.Equal("KYC verified.", await customers.VerifyKycAsync(corporate.Id, true, null, World.Checker));
            Assert.StartsWith("KYC rejected", await customers.VerifyKycAsync(pending.Id, false, "blurred", World.Checker));
        });

        Assert.Equal(KycStatus.Verified, (await w.Read(db => db.Customers.AsNoTracking().FirstAsync(c => c.Id == corporate.Id))).Kyc);
        Assert.Equal(KycStatus.Rejected, (await w.Read(db => db.Customers.AsNoTracking().FirstAsync(c => c.Id == pending.Id))).Kyc);
    }

    [Fact]
    public async Task Changing_key_customer_data_sends_the_kyc_back_for_reverification()
    {
        var w = await World.CreateAsync(fx);
        var customer = await w.NewCustomerAsync();

        var keyChange = await w.Do(s => s.Resolve<CustomerService>().UpdateAsync(customer.Id,
            new Customer { Type = CustomerType.Individual, Name = "Renamed Person", Nid = customer.Nid, Mobile = customer.Mobile, Address = "Dhaka", IsActive = true },
            null, null, null, World.Other));

        Assert.True(keyChange);
        var stored = await w.Read(db => db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id));
        Assert.Equal(KycStatus.Pending, stored.Kyc);
        Assert.Equal(World.Other, stored.MadeBy);
        Assert.Equal("Renamed Person", stored.Name);
    }

    [Fact]
    public async Task Uploads_are_limited_to_safe_types_and_sizes()
    {
        var w = await World.CreateAsync(fx);

        await w.Do(async s =>
        {
            var customers = s.Resolve<CustomerService>();
            var template = () => new Customer { Type = CustomerType.Individual, Name = "Upload Test", Mobile = "01711111111", Address = "Dhaka", BranchId = w.Branch.Id, Nid = Guid.NewGuid().ToString("N")[..12] };
            var exe = new UploadedFile(new MemoryStream([1, 2, 3]), "virus.exe", 3);
            var huge = new UploadedFile(new MemoryStream([1]), "scan.jpg", 3 * 1024 * 1024);
            await Assert.ThrowsAsync<BankingException>(() => customers.CreateAsync(template(), exe, null, null, World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => customers.CreateAsync(template(), null, huge, null, World.Maker));
            var ok = new UploadedFile(new MemoryStream([1, 2, 3]), "photo.JPG", 3);
            var (created, hit) = await customers.CreateAsync(template(), ok, null, null, World.Maker);
            Assert.False(hit);
            Assert.EndsWith(".jpg", created.PhotoPath);
            var file = await customers.GetFileAsync(created.Id, "photo");
            Assert.NotNull(file);
            Assert.Equal("image/jpeg", file!.ContentType);
            Assert.Null(await customers.GetFileAsync(created.Id, "signature"));
            Assert.Null(await customers.GetFileAsync(created.Id, "../../etc/passwd"));
        });
    }

    [Fact]
    public async Task Standing_instruction_creation_validates_accounts_amounts_and_dates()
    {
        var w = await World.CreateAsync(fx);
        var from = await w.OpenAccountAsync();
        var to = await w.OpenAccountAsync();
        StandingInstructionRequest Req(string f, string t, decimal amount, DateTime start, DateTime? end = null) =>
            new(f, t, amount, SiFrequency.Monthly, start, end, "rent");

        await w.Do(async s =>
        {
            var service = s.Resolve<StandingInstructionService>();
            await Assert.ThrowsAsync<BankingException>(() => service.CreateAsync(Req("nope", to.AccountNo, 100m, w.Today), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => service.CreateAsync(Req(from.AccountNo, from.AccountNo, 100m, w.Today), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => service.CreateAsync(Req(from.AccountNo, to.AccountNo, 0m, w.Today), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => service.CreateAsync(Req(from.AccountNo, to.AccountNo, 100m, w.Today.AddDays(-1)), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => service.CreateAsync(Req(from.AccountNo, to.AccountNo, 100m, w.Today.AddDays(5), w.Today.AddDays(2)), World.Maker));
            var ok = await service.CreateAsync(Req(from.AccountNo, to.AccountNo, 100m, w.Today.AddDays(1)), World.Maker);
            Assert.Equal(w.Today.AddDays(1), ok.NextRunDate);
            await service.SetStatusAsync(ok.Id, SiStatus.Paused);
            await service.SetStatusAsync(ok.Id, SiStatus.Completed);
        });

        var stored = await w.Read(db => db.StandingInstructions.AsNoTracking().Where(x => x.FromAccountId == from.Id).ToListAsync());
        Assert.Equal(SiStatus.Paused, Assert.Single(stored).Status);
    }

    [Fact]
    public async Task Trial_balance_stays_balanced_and_inter_branch_nets_to_zero_after_cross_branch_activity()
    {
        var home = await World.CreateAsync(fx);
        var other = await World.CreateAsync(fx);
        var acc = await home.OpenAccountAsync(deposit: 10_000m);
        var v = await home.Do(s => s.Transactions.DepositAsync(acc.Id, 3_000m, null, null, World.Maker, new UserProfile { BranchId = other.Branch.Id }));
        await home.AuthorizeAsync(v.Id);

        await home.Do(async s =>
        {
            var reports = s.Resolve<ReportService>();
            var trial = await reports.TrialBalanceAsync(new ReportQuery(null, null, null, null));
            Assert.Contains("books are balanced", trial.Description);
            var branchTrial = await reports.TrialBalanceAsync(new ReportQuery(null, null, home.Branch.Id, null));
            Assert.Contains("books are balanced", branchTrial.Description);
            var interBranch = await reports.InterBranchAsync(new ReportQuery(null, null, null, null));
            Assert.Contains("reconciled", interBranch.Totals![3]);
            Assert.DoesNotContain("UNRECONCILED", interBranch.Totals[3]);
            var balanceSheet = await reports.BalanceSheetAsync(new ReportQuery(null, null, null, null));
            Assert.Contains("balanced", balanceSheet.Description);
        });
    }

    [Fact]
    public async Task Gl_statement_walks_a_group_and_requires_a_known_code()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 2_000m);

        await w.Do(async s =>
        {
            var reports = s.Resolve<ReportService>();
            var none = await reports.GlStatementAsync(new ReportQuery(null, null, w.Branch.Id, null));
            Assert.False(none.AllowExport);
            var unknown = await reports.GlStatementAsync(new ReportQuery(null, null, w.Branch.Id, "7777"));
            Assert.Equal("GL not found.", unknown.Description);
            var cash = await reports.GlStatementAsync(new ReportQuery(w.Today, w.Today, w.Branch.Id, "1100"));
            Assert.Equal("2,000.00", cash.Rows.Last()[6]);
            var assets = await reports.GlStatementAsync(new ReportQuery(w.Today, w.Today, w.Branch.Id, "1000"));
            Assert.Equal("2,000.00", assets.Rows.Last()[6]);
        });
        Assert.NotNull(acc);
    }

    [Fact]
    public async Task Day_book_lists_the_days_postings_and_totals_them()
    {
        var w = await World.CreateAsync(fx);
        await w.OpenAccountAsync(deposit: 1_500m);

        var book = await w.Do(s => s.Resolve<ReportService>().DayBookAsync(new ReportQuery(w.Today, null, w.Branch.Id, null)));

        Assert.NotEmpty(book.Rows);
        Assert.Equal(book.Totals![6], book.Totals[7]);
        Assert.Equal("1,500.00", book.Totals[6]);
    }

    [Fact]
    public async Task Rate_changes_follow_maker_checker_and_validate_slabs()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.FDR);
        ProductRate Rate(decimal rate, decimal min = 0, decimal max = 0) => new() { ProductId = product.Id, Rate = rate, MinAmount = min, MaxAmount = max, EffectiveFrom = w.Today };

        var added = await w.Do(async s =>
        {
            var setup = s.Resolve<SetupService>();
            await Assert.ThrowsAsync<BankingException>(() => setup.AddRateAsync(Rate(101m), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddRateAsync(Rate(-1m), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddRateAsync(Rate(9m, 500, 100), World.Maker));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddRateAsync(new ProductRate { ProductId = 999_999, Rate = 5m }, World.Maker));
            var rate = Rate(9m);
            await setup.AddRateAsync(rate, World.Maker);
            return rate;
        });

        await w.Do(async s =>
        {
            var setup = s.Resolve<SetupService>();
            await Assert.ThrowsAsync<BankingException>(() => setup.DecideRateAsync(added.Id, true, World.Maker));
            var decided = await setup.DecideRateAsync(added.Id, true, World.Checker);
            Assert.Equal(ApprovalStatus.Approved, decided.Status);
            await Assert.ThrowsAsync<BankingException>(() => setup.DecideRateAsync(added.Id, false, World.Checker));
        });
    }

    [Fact]
    public async Task Setup_enforces_unique_codes_and_tree_rules()
    {
        var w = await World.CreateAsync(fx);

        await w.Do(async s =>
        {
            var setup = s.Resolve<SetupService>();
            Assert.Contains(await setup.ValidateBranchAsync(new Branch { Code = w.Branch.Code }), i => i.Field == "Code");
            Assert.Empty(await setup.ValidateBranchAsync(new Branch { Id = w.Branch.Id, Code = w.Branch.Code }));
            Assert.Contains(await setup.ValidateBranchAsync(new Branch { Id = w.Branch.Id, Code = w.Branch.Code, ParentId = w.Branch.Id }), i => i.Field == "ParentId");
            Assert.Contains(await setup.ValidateGlAsync(new GlAccount { Code = "1100" }), i => i.Field == "Code");
            var group = await s.Db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "1000");
            Assert.Contains(await setup.ValidateGlAsync(new GlAccount { Id = group.Id, Code = "1000", IsPosting = true }), i => i.Field == "IsPosting");
            var posted = await s.Db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "1100");
            Assert.Contains(await setup.ValidateGlAsync(new GlAccount { Id = posted.Id, Code = "1100", IsPosting = false }), i => i.Field == "IsPosting");
            var product = await s.Db.Products.AsNoTracking().FirstAsync(p => p.Category == ProductCategory.Savings);
            Assert.Contains(await setup.ValidateProductAsync(new Product { Id = 0, Code = product.Code }), i => i.Field == "Code");
            Assert.Contains(await setup.ValidateProductAsync(new Product { Id = product.Id, Code = product.Code, Category = ProductCategory.Current }), i => i.Field == "Category");
        });
    }

    [Fact]
    public async Task Currency_holiday_and_excise_setup_reject_bad_input_and_duplicates()
    {
        var w = await World.CreateAsync(fx);
        var code = new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(3).ToArray()).PadRight(3, 'X').ToUpperInvariant();
        var date = new DateTime(2030, 1, 1).AddDays(Random.Shared.Next(1, 3000));

        await w.Do(async s =>
        {
            var setup = s.Resolve<SetupService>();
            await Assert.ThrowsAsync<BankingException>(() => setup.AddCurrencyAsync(new Currency { Code = "AB", Name = "Bad" }));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddCurrencyAsync(new Currency { Code = "BDT", Name = "Duplicate" }));
            await setup.AddCurrencyAsync(new Currency { Code = code.ToLowerInvariant(), Name = "Test money" });
            await Assert.ThrowsAsync<BankingException>(() => setup.AddExchangeRateAsync(new ExchangeRate { CurrencyId = 1, BuyRate = 0, SellRate = 1 }));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddHolidayAsync(new Holiday { Date = date, Description = " " }));
            await setup.AddHolidayAsync(new Holiday { Date = date.AddHours(5), Description = "Test day" });
            await Assert.ThrowsAsync<BankingException>(() => setup.AddHolidayAsync(new Holiday { Date = date, Description = "Again" }));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddExciseSlabAsync(new ExciseSlab { Duty = 0 }));
            await Assert.ThrowsAsync<BankingException>(() => setup.AddExciseSlabAsync(new ExciseSlab { Duty = 10, MinBalance = 500, MaxBalance = 100 }));
        });

        var holiday = await w.Read(db => db.Holidays.AsNoTracking().SingleAsync(h => h.Date == date));
        await w.Do(s => s.Resolve<SetupService>().DeleteHolidayAsync(holiday.Id));
        Assert.False(await w.Read(db => db.Holidays.AnyAsync(h => h.Date == date)));
        Assert.True(await w.Read(db => db.Currencies.AnyAsync(c => c.Code == code)));
    }

    [Fact]
    public async Task Saving_bank_parameters_is_all_or_nothing()
    {
        var w = await World.CreateAsync(fx);
        var original = await w.Read(db => db.BankParameters.AsNoTracking().ToDictionaryAsync(p => p.Key, p => p.Value));

        await w.Do(async s =>
        {
            var setup = s.Resolve<SetupService>();
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => setup.SaveParametersAsync(new Dictionary<string, string>
            {
                ["VatRate"] = "20", ["CashGl"] = "9999", ["FinancialYearStartMonth"] = "13"
            }));
            Assert.Equal(2, ex.Errors.Count);
        });

        Assert.Equal(original, await w.Read(db => db.BankParameters.AsNoTracking().ToDictionaryAsync(p => p.Key, p => p.Value)));
        await w.Do(s => s.Resolve<SetupService>().SaveParametersAsync(new Dictionary<string, string> { ["VatRate"] = " 15 " }));
        Assert.Equal("15", (await w.Read(db => db.BankParameters.AsNoTracking().FirstAsync(p => p.Key == "VatRate"))).Value);
    }

    [Fact]
    public async Task Lookups_trim_input_and_raise_friendly_errors()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();

        await w.Do(async s =>
        {
            var lookups = s.Resolve<LookupService>();
            Assert.Equal(acc.Id, await lookups.AccountIdByNumberAsync("  " + acc.AccountNo + " "));
            Assert.Null(await lookups.OptionalAccountIdAsync("   ", "x"));
            var missing = await Assert.ThrowsAsync<BankingException>(() => lookups.AccountIdByNumberAsync("123"));
            Assert.Equal("Account 123 not found.", missing.Message);
            Assert.Equal("Custom", (await Assert.ThrowsAsync<BankingException>(() => lookups.AccountIdByNumberAsync("123", "Custom"))).Message);
            Assert.Contains(await lookups.BranchesAsync(), b => b.Id == w.Branch.Id && b.Text.StartsWith(w.Branch.Code));
            Assert.All(await lookups.ActiveProductsAsync(loans: false), p => Assert.False(p.IsLoan));
            Assert.All(await lookups.ActiveProductsAsync(loans: true), p => Assert.True(p.IsLoan));
        });
    }

    [Fact]
    public async Task Voucher_queries_filter_the_register_and_resolve_reversal_links()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var original = await w.DepositAsync(acc, 700m);
        var reversal = await w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "oops", World.Maker, null));
        await w.AuthorizeAsync(reversal.Id);

        await w.Do(async s =>
        {
            var queries = s.Resolve<VoucherQueries>();
            var register = await queries.SearchAsync(new VoucherFilter(w.Today, w.Today, VoucherStatus.Reversed, VoucherType.CashDeposit, original.VoucherNo, w.Branch.Id));
            Assert.Equal(original.Id, Assert.Single(register.Rows).Id);
            var details = await queries.GetDetailsAsync(original.Id);
            Assert.Equal(reversal.Id, details!.ReversedBy!.Id);
            Assert.Equal("BDT", details.CurrencyCode);
            Assert.Equal(original.Id, (await queries.GetDetailsAsync(reversal.Id))!.Reversal!.Id);
            Assert.Null(await queries.GetDetailsAsync(-1));
            Assert.Empty(await queries.PendingAsync(w.Branch.Id));
        });
    }

    [Fact]
    public async Task Audit_queries_filter_by_user_entity_and_key()
    {
        var w = await World.CreateAsync(fx);
        var customer = await w.NewCustomerAsync();

        var page = await w.Do(s => s.Resolve<AuditQueries>().SearchAsync(new AuditFilter(null, "Customer", customer.Id.ToString(), DateTime.Today.AddDays(-1), DateTime.Today)));

        Assert.NotEmpty(page.Rows);
        Assert.All(page.Rows, r => Assert.Equal("Customer", r.Entity));
    }

    [Fact]
    public async Task Access_policy_blocks_inactive_users_forces_password_changes_and_expires_old_passwords()
    {
        var w = await World.CreateAsync(fx);
        var noon = new DateTime(2026, 3, 9, 12, 0, 0);
        UserProfile Profile(Action<UserProfile> tweak)
        {
            var p = new UserProfile { UserId = Guid.NewGuid().ToString("N"), FullName = "Policy", BranchId = w.Branch.Id, MustChangePassword = false, PasswordChangedOn = noon.AddDays(-1) };
            tweak(p);
            return p;
        }
        var profiles = new[]
        {
            Profile(_ => { }), Profile(p => p.IsActive = false), Profile(p => p.MustChangePassword = true),
            Profile(p => p.PasswordChangedOn = noon.AddDays(-60)), Profile(p => { p.WorkFrom = TimeSpan.FromHours(13); p.WorkTo = TimeSpan.FromHours(17); })
        };
        await w.Do(async s => { s.Db.UserProfiles.AddRange(profiles); await s.Db.SaveChangesAsync(); });

        await w.Do(async s =>
        {
            var policy = s.Resolve<UserAccessPolicy>();
            Assert.Equal(AccessDecision.Allowed, await policy.EvaluateAsync(profiles[0].UserId, "10.0.0.1", noon));
            Assert.Equal(AccessDecision.SignOut, await policy.EvaluateAsync(profiles[1].UserId, "10.0.0.1", noon));
            Assert.Equal(AccessDecision.ChangePassword, await policy.EvaluateAsync(profiles[2].UserId, "10.0.0.1", noon));
            Assert.Equal(AccessDecision.ChangePassword, await policy.EvaluateAsync(profiles[3].UserId, "10.0.0.1", noon));
            Assert.Equal(AccessDecision.SignOut, await policy.EvaluateAsync(profiles[4].UserId, "10.0.0.1", noon));
            Assert.Equal(AccessDecision.Allowed, await policy.EvaluateAsync("unknown-user", "10.0.0.1", noon));
            Assert.Equal(AccessDecision.Allowed, await policy.EvaluateAsync(null, null, noon));
        });
    }
}
