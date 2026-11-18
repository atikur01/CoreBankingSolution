namespace CoreBanking.Tests.Infrastructure;

public sealed class Svc(IServiceScope scope) : IDisposable
{
    private T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public IApplicationDbContext Db => Get<IApplicationDbContext>();
    public LedgerService Ledger => Get<LedgerService>();
    public DepositService Deposits => Get<DepositService>();
    public TransactionService Transactions => Get<TransactionService>();
    public RemittanceService Remittances => Get<RemittanceService>();
    public ClearingService Clearing => Get<ClearingService>();
    public LoanService Loans => Get<LoanService>();
    public EodService Eod => Get<EodService>();
    public ParamService Params => Get<ParamService>();
    public NumberService Numbers => Get<NumberService>();
    public SanctionService Sanctions => Get<SanctionService>();
    public RateService Rates => Get<RateService>();
    public T Resolve<T>() where T : notnull => Get<T>();

    public void Dispose() => scope.Dispose();
}
