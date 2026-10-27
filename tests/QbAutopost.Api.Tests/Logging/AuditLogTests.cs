using System.Text.Json;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>
/// T-912 / FR-A-16: one JSON line per audited request in <c>Paths:Logs/audit-yyyyMMdd.jsonl</c>, kept for
/// <c>Api:AuditRetentionDays</c> (400) because this is money evidence, not operational noise.
/// <para>
/// The file is deliberately not a Serilog sink: it must survive a <c>Serilog:MinimumLevel</c> of Warning, and a
/// levelled logger is one configuration mistake away from silently losing the record of a posting (api-v1 §8).
/// </para>
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 9, 23, 12, 30, 15, DateTimeKind.Utc);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Folder => _dir.Combine("logs");

    private AuditLog Log(int retentionDays = 400) => new(Folder, retentionDays);

    private static AuditEntry Entry(DateTime utc, string outcome = "ok", int status = 200) => new()
    {
        Utc = utc,
        RequestId = "01K5ZC4Q7N9V8T2R6M3H1X0B4C",
        ClientId = "acme-erp",
        RemoteIp = "127.0.0.1",
        Method = "POST",
        Path = "/api/v1/quickbooks/transactions",
        IdempotencyKey = "acme-0042",
        Outcome = outcome,
        Status = status,
        BatchId = "api-payroll-2026-09#1",
        JobId = null,
        Counts = new AuditCounts(3, 2, 1, 0),
        TotalAmount = 184.32m,
    };

    private string[] Lines(DateTime utc)
    {
        var file = Path.Combine(Folder, AuditLog.FileNameFor(utc));
        return File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
    }

    [Fact]
    public void Should_NameTheFileAfterTheDay_When_ALineIsAppended()
    {
        Log().Append(Entry(Noon));

        Assert.Equal("audit-20260923.jsonl", Path.GetFileName(Assert.Single(Directory.GetFiles(Folder))));
    }

    [Fact]
    public void Should_AppendOneLinePerEntry_When_SeveralAreWritten()
    {
        var log = Log();

        log.Append(Entry(Noon));
        log.Append(Entry(Noon.AddSeconds(1)));
        log.Append(Entry(Noon.AddSeconds(2)));

        Assert.Equal(3, Lines(Noon).Length);
    }

    [Fact]
    public void Should_StartANewFile_When_TheDayChanges()
    {
        var log = Log();

        log.Append(Entry(Noon));
        log.Append(Entry(Noon.AddDays(1)));

        Assert.Single(Lines(Noon));
        Assert.Single(Lines(Noon.AddDays(1)));
    }

    [Fact]
    public void Should_WriteEveryFieldOfFrA16_When_ALineIsWritten()
    {
        Log().Append(Entry(Noon));

        var line = JsonDocument.Parse(Assert.Single(Lines(Noon))).RootElement;
        Assert.Equal("01K5ZC4Q7N9V8T2R6M3H1X0B4C", line.GetProperty("requestId").GetString());
        Assert.Equal("acme-erp", line.GetProperty("clientId").GetString());
        Assert.Equal("127.0.0.1", line.GetProperty("remoteIp").GetString());
        Assert.Equal("POST", line.GetProperty("method").GetString());
        Assert.Equal("/api/v1/quickbooks/transactions", line.GetProperty("path").GetString());
        Assert.Equal("acme-0042", line.GetProperty("idempotencyKey").GetString());
        Assert.Equal("ok", line.GetProperty("outcome").GetString());
        Assert.Equal(200, line.GetProperty("status").GetInt32());
        Assert.Equal("api-payroll-2026-09#1", line.GetProperty("batchId").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("jobId").ValueKind);
        Assert.Equal(2, line.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(184.32m, line.GetProperty("totalAmount").GetDecimal());
        Assert.StartsWith("2026-09-23T12:30:15", line.GetProperty("utc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_KeepTheWholeEntryOnOneLine_When_ItIsWritten()
    {
        // A reader tails this file line by line; an indented object would break every such reader.
        Log().Append(Entry(Noon));

        Assert.Single(Lines(Noon));
        Assert.DoesNotContain("\n", Lines(Noon)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Should_DeleteFilesOlderThanRetention_When_ItSweeps()
    {
        var log = Log(retentionDays: 400);
        log.Append(Entry(Noon.AddDays(-401)));
        log.Append(Entry(Noon.AddDays(-399)));

        var deleted = log.Sweep(Noon);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(Folder, AuditLog.FileNameFor(Noon.AddDays(-401)))));
        Assert.True(File.Exists(Path.Combine(Folder, AuditLog.FileNameFor(Noon.AddDays(-399)))));
    }

    [Fact]
    public void Should_LeaveTheOperationalLogAlone_When_ItSweeps()
    {
        var log = Log(retentionDays: 1);
        Directory.CreateDirectory(Folder);
        var operationalLog = Path.Combine(Folder, "qbautopost-20240101.log");
        File.WriteAllText(operationalLog, "not mine to delete");

        log.Sweep(Noon);

        Assert.True(File.Exists(operationalLog));
    }

    [Fact]
    public void Should_SweepOldFiles_When_TheFirstLineOfADayIsWritten()
    {
        var log = Log(retentionDays: 30);
        log.Append(Entry(Noon.AddDays(-40)));

        log.Append(Entry(Noon));

        Assert.False(File.Exists(Path.Combine(Folder, AuditLog.FileNameFor(Noon.AddDays(-40)))));
    }

    [Fact]
    public void Should_KeepEveryFile_When_RetentionIsZeroOrNegative()
    {
        // A misconfigured retention must never be read as "delete everything": this is the money evidence.
        var log = Log(retentionDays: 0);
        log.Append(Entry(Noon.AddDays(-4000)));

        var deleted = log.Sweep(Noon);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(Folder, AuditLog.FileNameFor(Noon.AddDays(-4000)))));
    }
}
