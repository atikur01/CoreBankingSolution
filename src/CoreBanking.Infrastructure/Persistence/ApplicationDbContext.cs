using System.Text.Json;
using CoreBankingSolution.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        private readonly IHttpContextAccessor? _http;
        private bool _auditing;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IHttpContextAccessor? http = null)
            : base(options)
        {
            _http = http;
        }

        public DbSet<Branch> Branches => Set<Branch>();
        public DbSet<Currency> Currencies => Set<Currency>();
        public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
        public DbSet<Holiday> Holidays => Set<Holiday>();
        public DbSet<BankParameter> BankParameters => Set<BankParameter>();
        public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProductRate> ProductRates => Set<ProductRate>();
        public DbSet<ExciseSlab> ExciseSlabs => Set<ExciseSlab>();
        public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Nominee> Nominees => Set<Nominee>();
        public DbSet<SanctionEntry> SanctionEntries => Set<SanctionEntry>();
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<AccountHolder> AccountHolders => Set<AccountHolder>();
        public DbSet<AccountTransaction> AccountTransactions => Set<AccountTransaction>();
        public DbSet<ChequeBook> ChequeBooks => Set<ChequeBook>();
        public DbSet<StopPayment> StopPayments => Set<StopPayment>();
        public DbSet<ChequeReturn> ChequeReturns => Set<ChequeReturn>();
        public DbSet<StandingInstruction> StandingInstructions => Set<StandingInstruction>();
        public DbSet<ClearingItem> ClearingItems => Set<ClearingItem>();
        public DbSet<Remittance> Remittances => Set<Remittance>();
        public DbSet<Voucher> Vouchers => Set<Voucher>();
        public DbSet<VoucherLine> VoucherLines => Set<VoucherLine>();
        public DbSet<Loan> Loans => Set<Loan>();
        public DbSet<LoanInstallment> LoanInstallments => Set<LoanInstallment>();
        public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
        public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<EodLog> EodLogs => Set<EodLog>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            // Banking data must never be cascade-deleted.
            foreach (var fk in b.Model.GetEntityTypes()
                         .Where(e => e.ClrType.Namespace == typeof(Branch).Namespace)
                         .SelectMany(e => e.GetForeignKeys()))
                fk.DeleteBehavior = DeleteBehavior.Restrict;

            b.Entity<Branch>().HasIndex(x => x.Code).IsUnique();
            b.Entity<Branch>().HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            b.Entity<Currency>().HasIndex(x => x.Code).IsUnique();
            b.Entity<GlAccount>().HasIndex(x => x.Code).IsUnique();
            b.Entity<GlAccount>().HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId);
            b.Entity<Product>().HasIndex(x => x.Code).IsUnique();
            b.Entity<Product>().HasOne(x => x.GlAccount).WithMany().HasForeignKey(x => x.GlAccountId);
            b.Entity<Product>().HasOne(x => x.InterestGl).WithMany().HasForeignKey(x => x.InterestGlId);
            b.Entity<Product>().HasOne(x => x.AccrualGl).WithMany().HasForeignKey(x => x.AccrualGlId);
            b.Entity<Customer>().HasIndex(x => x.CifNo).IsUnique();
            b.Entity<Customer>().HasIndex(x => x.Nid);
            b.Entity<Customer>().HasIndex(x => x.Mobile);
            b.Entity<Account>().HasIndex(x => x.AccountNo).IsUnique();
            b.Entity<Account>().HasIndex(x => x.CustomerId);
            b.Entity<AccountTransaction>().HasIndex(x => new { x.AccountId, x.TxnDate });
            b.Entity<Voucher>().HasIndex(x => x.VoucherNo).IsUnique();
            b.Entity<Voucher>().HasIndex(x => new { x.Status, x.BranchId });
            b.Entity<Voucher>().HasIndex(x => x.TxnDate);
            b.Entity<VoucherLine>().HasIndex(x => x.GlAccountId);
            b.Entity<VoucherLine>().HasOne(x => x.Voucher).WithMany(x => x.Lines).HasForeignKey(x => x.VoucherId);
            b.Entity<Loan>().HasIndex(x => x.LoanNo).IsUnique();
            b.Entity<LoanInstallment>().HasIndex(x => new { x.LoanId, x.No }).IsUnique();
            b.Entity<UserProfile>().HasIndex(x => x.UserId).IsUnique();
            b.Entity<AuditLog>().HasIndex(x => x.At);
            b.Entity<Remittance>().HasIndex(x => x.InstrumentNo).IsUnique();
            b.Entity<StandingInstruction>().HasOne(x => x.FromAccount).WithMany().HasForeignKey(x => x.FromAccountId);
            b.Entity<StandingInstruction>().HasOne(x => x.ToAccount).WithMany().HasForeignKey(x => x.ToAccountId);
            b.Entity<Nominee>().HasOne(x => x.Account).WithMany(x => x.Nominees).HasForeignKey(x => x.AccountId);
            b.Entity<AccountHolder>().HasOne(x => x.Account).WithMany(x => x.Holders).HasForeignKey(x => x.AccountId);
        }

        // ---- Audit trail: every insert / update / delete is logged with user, IP and changed values ----
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_auditing) return await base.SaveChangesAsync(cancellationToken);

            ChangeTracker.DetectChanges();
            var pending = ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                            && e.Entity is not AuditLog && e.Entity is not NumberSequence && e.Entity is not PasswordHistory
                            && e.Metadata.ClrType.Namespace == typeof(Branch).Namespace)
                .Select(e => new
                {
                    Entry = e,
                    State = e.State,
                    Changes = e.State == EntityState.Modified
                        ? e.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name != "Version")
                            .ToDictionary(p => p.Metadata.Name, p => $"{p.OriginalValue} -> {p.CurrentValue}")
                        : e.State == EntityState.Added
                            ? e.Properties.Where(p => p.CurrentValue != null && !p.Metadata.IsPrimaryKey())
                                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue?.ToString() ?? "")
                            : new Dictionary<string, string>()
                }).ToList();

            var result = await base.SaveChangesAsync(cancellationToken);

            if (pending.Count > 0)
            {
                var ctx = _http?.HttpContext;
                var user = ctx?.User?.Identity?.Name ?? "SYSTEM";
                var ip = ctx?.Connection?.RemoteIpAddress?.ToString();
                foreach (var p in pending)
                {
                    var key = p.Entry.Metadata.FindPrimaryKey()?.Properties
                        .Select(k => p.Entry.Property(k.Name).CurrentValue?.ToString()).FirstOrDefault();
                    var details = p.Changes.Count == 0 ? null : JsonSerializer.Serialize(p.Changes);
                    if (details != null && details.Length > 3900) details = details[..3900] + "…";
                    AuditLogs.Add(new AuditLog
                    {
                        UserName = user, Ip = ip, Entity = p.Entry.Metadata.ClrType.Name, EntityKey = key,
                        Action = p.State == EntityState.Added ? "Create" : p.State == EntityState.Modified ? "Update" : "Delete",
                        Details = details
                    });
                }
                _auditing = true;
                try { await base.SaveChangesAsync(cancellationToken); }
                finally { _auditing = false; }
            }
            return result;
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
            SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
