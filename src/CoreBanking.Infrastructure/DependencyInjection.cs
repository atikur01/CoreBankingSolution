using CoreBanking.Infrastructure.Identity;
using CoreBanking.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreBanking.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddHttpContextAccessor();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<INumberSequence, SqlNumberSequence>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IFileStore, LocalFileStore>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<IPasswordService, PasswordService>();
        return services;
    }
}
