using KanAuth.Application.Interfaces;
using KanAuth.Application.Services;
using KanAuth.Infrastructure.Data;
using KanAuth.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KanAuth.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "sqlite";
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        services.AddDbContext<AppDbContext>(options =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlserver":
                    options.UseSqlServer(connectionString,
                        sql => sql.MigrationsAssembly("KanAuth.Infrastructure"));
                    break;

                case "postgresql":
                    options.UseNpgsql(connectionString,
                        npgsql => npgsql.MigrationsAssembly("KanAuth.Infrastructure"));
                    break;

                case "mysql":
                    options.UseMySql(connectionString,
                        ServerVersion.AutoDetect(connectionString),
                        mysql => mysql.MigrationsAssembly("KanAuth.Infrastructure"));
                    break;

                case "sqlite":
                    options.UseSqlite(connectionString,
                        sqlite => sqlite.MigrationsAssembly("KanAuth.Infrastructure"));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported database provider '{provider}'. " +
                        "Valid values: sqlserver, postgresql, mysql, sqlite.");
            }
        });

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITokenService, TokenService>();

        return services;
    }
}
