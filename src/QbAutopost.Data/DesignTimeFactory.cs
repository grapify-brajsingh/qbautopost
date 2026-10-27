using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QbAutopost.Data;

/// <summary>
/// Builds the context for <c>dotnet ef</c> (build-time only). The connection comes from
/// <c>QBAUTOPOST_DESIGN_CONNECTION</c>, else the local development database of <c>deploy/dev/docker-compose.yml</c>.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<QbAutopostDbContext>
{
    public const string ConnectionVariable = "QBAUTOPOST_DESIGN_CONNECTION";

    // Local development only: the same throwaway password as deploy/dev/docker-compose.yml, never a server's.
    private const string DevelopmentConnection =
        "Host=127.0.0.1;Port=5432;Database=qbautopost;Username=qbautopost;Password=qbautopost-dev";

    public QbAutopostDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var options = new DbContextOptionsBuilder<QbAutopostDbContext>()
            .UseNpgsql(string.IsNullOrWhiteSpace(connection) ? DevelopmentConnection : connection)
            .Options;
        return new QbAutopostDbContext(options);
    }
}
