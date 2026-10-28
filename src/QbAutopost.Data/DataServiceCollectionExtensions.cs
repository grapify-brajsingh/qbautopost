using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// A pooled <see cref="IDbContextFactory{TContext}"/> for <see cref="QbAutopostDbContext"/> on PostgreSQL, and
    /// <see cref="ISavedRequestStore"/> as a singleton <see cref="EfSavedRequestStore"/> (each call makes its own context).
    /// </summary>
    public static IServiceCollection AddQbAutopostData(this IServiceCollection services, string connectionString, int commandTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);

        services.AddPooledDbContextFactory<QbAutopostDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(commandTimeoutSeconds)));
        services.AddSingleton<ISavedRequestStore, EfSavedRequestStore>();
        return services;
    }
}
