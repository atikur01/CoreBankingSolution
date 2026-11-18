using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CoreBanking.Application.Common.Abstractions;

public interface IApplicationDbContext
{
    DbSet<Branch> Branches { get; }
    DbSet<Currency> Currencies { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<Holiday> Holidays { get; }
    DbSet<BankParameter> BankParameters { get; }
    DbSet<GlAccount> GlAccounts { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductRate> ProductRates { get; }
    DbSet<ExciseSlab> ExciseSlabs { get; }
    DbSet<NumberSequence> NumberSequences { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Nominee> Nominees { get; }
    DbSet<SanctionEntry> SanctionEntries { get; }
    DbSet<Account> Accounts { get; }
    DbSet<AccountHolder> AccountHolders { get; }
    DbSet<AccountTransaction> AccountTransactions { get; }
    DbSet<ChequeBook> ChequeBooks { get; }
    DbSet<StopPayment> StopPayments { get; }
    DbSet<ChequeReturn> ChequeReturns { get; }
    DbSet<StandingInstruction> StandingInstructions { get; }
    DbSet<ClearingItem> ClearingItems { get; }
    DbSet<Remittance> Remittances { get; }
    DbSet<Voucher> Vouchers { get; }
    DbSet<VoucherLine> VoucherLines { get; }
    DbSet<Loan> Loans { get; }
    DbSet<LoanInstallment> LoanInstallments { get; }
    DbSet<UserProfile> UserProfiles { get; }
    DbSet<PasswordHistory> PasswordHistories { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<EodLog> EodLogs { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
