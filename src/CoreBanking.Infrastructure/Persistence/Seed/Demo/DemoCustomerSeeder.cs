
namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

internal static class DemoCustomerSeeder
{
    private static readonly HashSet<int> PendingKyc = [3, 12, 21];
    private const int RejectedKyc = 25;
    private static readonly HashSet<int> Borrowers = [0, 1, 2, 4, 5, 6, 7, 8];
    private static readonly HashSet<int> CurrentOwners = [0, 2, 6, 8, 15, 20, 23, 26];
    private static readonly HashSet<int> SndOwners = [4, 7, 19, 23];
    private static readonly (int Idx, decimal Amount, int Term, bool Renew)[] Fdrs =
        [(0, 500000, 12, true), (1, 100000, 6, false), (4, 1500000, 12, true), (8, 5000000, 24, true), (10, 2500000, 12, false), (20, 750000, 12, true), (26, 300000, 12, false), (27, 1000000, 36, true)];
    private static readonly (int Idx, decimal Installment)[] Dpss = [(5, 1000), (9, 2000), (11, 5000), (14, 3000), (17, 2000)];
    private static readonly (int A, int B, OperationMode Mode)[] JointPairs = [(0, 9, OperationMode.JointAny), (2, 4, OperationMode.EitherOrSurvivor), (20, 27, OperationMode.JointAll)];

    public static void Run(DemoState s)
    {
        var made = new Dictionary<int, Customer>();
        for (int i = 0; i < DemoCatalog.People.Length; i++)
        {
            var c = Individual(s, i);
            made[i] = c;
            s.Customers.Add(c);
            s.Db.Customers.Add(c);
        }
        for (int i = 0; i < DemoCatalog.Corporates.Length; i++)
        {
            var c = Corporate(s, i);
            s.Customers.Add(c);
            s.Db.Customers.Add(c);
        }
        foreach (var (a, b, _) in JointPairs) made[a].SpouseName = made[b].Name;

        for (int i = 0; i < DemoCatalog.People.Length; i++)
        {
            var c = made[i];
            if (c.Kyc != KycStatus.Verified) continue;
            BuildIndividualAccounts(s, i, c, made);
        }
        for (int i = 0; i < DemoCatalog.Corporates.Length; i++)
        {
            var c = s.Customers[DemoCatalog.People.Length + i];
            var cur = NewAccount(s, c, "02", c.CreatedOn.AddDays(3), 250000m * (i + 1), c.Name);
            cur.OdLimit = i % 2 == 0 ? 500000m : 0;
            if (i == 0) cur.PowerOfAttorney = "Mr. Anisur Rahman (Managing Director)";
            if (i == 3) NewAccount(s, c, "03", c.CreatedOn.AddDays(10), 2000000m, c.Name + " - Reserve");
        }
        AddPendingAccounts(s, made);
    }

    private static Customer Individual(DemoState s, int i)
    {
        var p = DemoCatalog.People[i];
        var br = s.Branches[i % s.Branches.Count];
        var kyc = PendingKyc.Contains(i) ? KycStatus.Pending : i == RejectedKyc ? KycStatus.Rejected : KycStatus.Verified;
        var days = Borrowers.Contains(i) || i == 13 ? 520 : s.Rng.Next(90, 330);
        var area = DemoCatalog.Areas[i % s.Branches.Count][i % 3];
        return new Customer
        {
            CifNo = s.CifNo(br), Type = CustomerType.Individual, Name = p.Name, FatherName = p.Father, MotherName = p.Mother,
            DateOfBirth = new DateTime(1955 + s.Rng.Next(0, 43), s.Rng.Next(1, 13), s.Rng.Next(1, 28)), Gender = p.Gender,
            Nid = "19" + s.Rng.NextInt64(10000000000, 99999999999), Tin = i % 3 == 0 ? s.Rng.NextInt64(100000000000, 999999999999).ToString() : null,
            Mobile = Mobile(s), Email = i % 2 == 0 ? Email(p.Name) : null,
            Address = $"House {s.Rng.Next(1, 90)}, Road {s.Rng.Next(1, 30)}, {area}", PermanentAddress = $"Village Char Kalibari, Upazila Sadar, District {area.Split(',')[1].Trim()}",
            Profession = p.Profession, Sector = p.Profession, SourceOfFunds = p.Income >= 100000 ? "Business / Professional income" : "Salary / Regular income",
            MonthlyIncome = p.Income, IntroducerName = i % 4 == 0 ? "Md. Rafiqul Islam" : null,
            Risk = i == 10 ? RiskRating.High : i % 3 == 0 ? RiskRating.Medium : RiskRating.Low, Kyc = kyc,
            ScreeningResult = kyc switch { KycStatus.Verified => "No match found", KycStatus.Rejected => "Possible name match - rejected after review", _ => null },
            IsPep = i == 10, BranchId = br.Id, IsActive = kyc != KycStatus.Rejected, MadeBy = DemoState.OfficerUser,
            CheckedBy = kyc == KycStatus.Pending ? null : DemoState.SupervisorUser, CreatedOn = s.WorkDay(s.Today.AddDays(-days))
        };
    }

    private static Customer Corporate(DemoState s, int i)
    {
        var d = DemoCatalog.Corporates[i];
        var br = s.Branches[(i * 2) % s.Branches.Count];
        return new Customer
        {
            CifNo = s.CifNo(br), Type = d.Type, Name = d.Name, DateOfBirth = new DateTime(2005 + i * 3, 3, 12), TradeLicense = d.License,
            Tin = s.Rng.NextInt64(100000000000, 999999999999).ToString(), Mobile = Mobile(s), Email = $"accounts@{d.Name.Split(' ')[0].ToLowerInvariant()}.com.bd",
            Address = $"Plot {s.Rng.Next(1, 60)}, Commercial Area, {DemoCatalog.Areas[(i * 2) % s.Branches.Count][0]}", ContactPerson = d.Contact, Sector = d.Sector,
            SourceOfFunds = "Business turnover", MonthlyIncome = 3000000m * (i + 1), Risk = i == 1 ? RiskRating.Medium : RiskRating.Low,
            Kyc = KycStatus.Verified, ScreeningResult = "No match found", BranchId = br.Id, MadeBy = DemoState.OfficerUser, CheckedBy = DemoState.ManagerUser,
            CreatedOn = s.WorkDay(s.Today.AddDays(-s.Rng.Next(200, 400)))
        };
    }

    private static void BuildIndividualAccounts(DemoState s, int i, Customer c, Dictionary<int, Customer> made)
    {
        if (i == 24) { NewAccount(s, c, "01", c.CreatedOn.AddDays(2), 25000m, c.Name); return; }
        if (i == 22) return;

        var opened = s.WorkDay(c.CreatedOn.AddDays(2));
        var savings = NewAccount(s, c, "01", opened, 5000m + s.Rng.Next(1, 40) * 1000m, c.Name);
        if (i % 4 == 0) savings.DailyDebitLimit = 500000m;
        if (i % 6 == 0) savings.MonthlyDebitLimit = 5000000m;
        AddNominees(s, i, savings);

        var pair = JointPairs.FirstOrDefault(j => j.A == i);
        if (pair != default)
        {
            savings.Mode = pair.Mode;
            savings.Title = $"{c.Name} & {made[pair.B].Name}";
            savings.Holders.Add(new AccountHolder { Customer = made[pair.B] });
        }

        if (i == 13) savings.Status = AccountStatus.Dormant;
        if (i == 16) savings.Status = AccountStatus.Frozen;
        if (i == 19) savings.Status = AccountStatus.Blocked;
        if (i == 18) savings.Status = AccountStatus.Closed;

        if (CurrentOwners.Contains(i)) NewAccount(s, c, "02", opened.AddDays(5), 25000m + s.Rng.Next(1, 30) * 1000m, c.Name + " (Current)");
        if (SndOwners.Contains(i)) NewAccount(s, c, "03", opened.AddDays(9), 50000m + s.Rng.Next(1, 30) * 5000m, c.Name + " (SND)");

        foreach (var f in Fdrs.Where(x => x.Idx == i)) AddFdr(s, c, f.Amount, f.Term, f.Renew);
        if (i == 6) AddFdr(s, c, 200000m, 3, false, s.Today.AddDays(-130));
        foreach (var d in Dpss.Where(x => x.Idx == i)) AddDps(s, c, d.Installment, savings);
    }

    private static void AddFdr(DemoState s, Customer c, decimal amount, int term, bool renew, DateTime? openOn = null)
    {
        var earliest = c.CreatedOn.AddDays(10);
        if (renew && s.Today.AddMonths(-term).AddDays(20) > earliest) earliest = s.Today.AddMonths(-term).AddDays(20);
        var latest = s.Today.AddDays(-30);
        var open = openOn ?? s.RandomDay(earliest, latest > earliest ? latest : earliest);
        var a = NewAccount(s, c, "04", open, amount, c.Name + " (FDR)");
        a.PrincipalAmount = amount;
        a.TermMonths = term;
        a.AutoRenew = renew;
        a.MaturityDate = open.AddMonths(term);
        a.InterestRate = amount >= 1000000m ? 8.5m : 8.0m;
        if (a.MaturityDate <= s.Today) a.Status = renew ? AccountStatus.Active : AccountStatus.Matured;
    }

    private static void AddDps(DemoState s, Customer c, decimal installment, Account linked)
    {
        var open = s.RandomDay(s.Today.AddDays(-330), s.Today.AddDays(-80));
        var a = NewAccount(s, c, "05", open, installment, c.Name + " (DPS)");
        a.TermMonths = 60;
        a.InstallmentAmount = installment;
        a.MaturityDate = open.AddMonths(60);
        a.InterestRate = 7.5m;
        a.InstallmentsPaid = 1;
        s.Defer(() => a.LinkedAccountId = linked.Id);
    }

    private static void AddPendingAccounts(DemoState s, Dictionary<int, Customer> made)
    {
        var a1 = NewAccount(s, made[22], "01", s.Today, 10000m, made[22].Name);
        a1.Status = AccountStatus.PendingApproval;
        var a2 = NewAccount(s, made[24], "03", s.Today, 100000m, made[24].Name + " (SND)");
        a2.Status = AccountStatus.PendingApproval;
    }

    private static Account NewAccount(DemoState s, Customer c, string productCode, DateTime openedOn, decimal initial, string title)
    {
        var product = s.Products[productCode];
        var branch = s.Branches.First(b => b.Id == c.BranchId);
        var a = new Account
        {
            AccountNo = s.AccountNo(branch, productCode), Title = title, Customer = c, Product = product, BranchId = branch.Id, CurrencyId = s.Bdt.Id,
            Status = AccountStatus.Active, Mode = OperationMode.Single, OpenedOn = s.WorkDay(openedOn), InterestRate = product.InterestRate,
            MadeBy = DemoState.TellerUser, CheckedBy = DemoState.SupervisorUser
        };
        s.Accounts.Add(a);
        s.Db.Accounts.Add(a);
        s.InitialDeposits[a] = initial;
        return a;
    }

    private static void AddNominees(DemoState s, int i, Account a)
    {
        if (i % 2 == 0)
            a.Nominees.Add(new Nominee { Name = DemoCatalog.People[(i + 5) % DemoCatalog.People.Length].Name, Relation = "Spouse", Nid = "19" + s.Rng.NextInt64(10000000000, 99999999999), DateOfBirth = new DateTime(1965 + s.Rng.Next(0, 30), 5, 9), SharePercent = 100, Address = "Same as account holder" });
        else if (i % 5 == 1)
        {
            a.Nominees.Add(new Nominee { Name = DemoCatalog.People[(i + 3) % DemoCatalog.People.Length].Name, Relation = "Son", SharePercent = 60, DateOfBirth = new DateTime(1995, 8, 2) });
            a.Nominees.Add(new Nominee { Name = DemoCatalog.People[(i + 4) % DemoCatalog.People.Length].Name, Relation = "Daughter", SharePercent = 40, DateOfBirth = new DateTime(1998, 1, 17) });
        }
    }

    private static string Mobile(DemoState s) => "01" + s.Rng.Next(3, 10) + s.Rng.Next(10000000, 99999999);

    private static string Email(string name) =>
        string.Concat(name.ToLowerInvariant().Replace("md.", "").Replace("mst.", "").Replace("late ", "").Trim().Replace(' ', '.')) + "@example.com";
}
