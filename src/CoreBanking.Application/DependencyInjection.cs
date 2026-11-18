using Microsoft.Extensions.DependencyInjection;

namespace CoreBanking.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ParamService>();
        services.AddScoped<NumberService>();
        services.AddScoped<SanctionService>();
        services.AddScoped<RateService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<DepositService>();
        services.AddScoped<TransactionService>();
        services.AddScoped<LoanService>();
        services.AddScoped<RemittanceService>();
        services.AddScoped<ClearingService>();
        services.AddScoped<EodService>();
        services.AddScoped<LookupService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<SanctionListService>();
        services.AddScoped<StandingInstructionService>();
        services.AddScoped<EodQueries>();
        services.AddScoped<RemittanceQueries>();
        services.AddScoped<AccountQueries>();
        services.AddScoped<AccountAdministrationService>();
        services.AddScoped<AccountOpeningService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<LoanQueries>();
        services.AddScoped<LoanApplicationService>();
        services.AddScoped<VoucherQueries>();
        services.AddScoped<JournalService>();
        services.AddScoped<ReportService>();
        services.AddScoped<SetupService>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<UserAccessPolicy>();
        services.AddScoped<IVoucherHook, DepositHook>();
        services.AddScoped<IVoucherHook, LoanHook>();
        services.AddScoped<IVoucherHook, RemittanceHook>();
        services.AddScoped<IVoucherHook, ClearingHook>();
        return services;
    }
}
