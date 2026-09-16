// Host skeleton (T-001). Endpoints, JobQueue/JobWorker, config and API-key auth arrive in M1 (T-102..T-104).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Run();

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program
{
}
