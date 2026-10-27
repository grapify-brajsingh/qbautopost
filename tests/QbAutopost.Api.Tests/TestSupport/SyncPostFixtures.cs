using System.Text;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1014: the validate-and-post payloads in <c>tests/fixtures/sync-post</c>. Both <c>SyncPostApiTests</c> and the
/// saved-request equivalence test (spec §14.4) send these same files, so the two routes are compared on the same bytes.
/// </summary>
public static class SyncPostFixtures
{
    public const string Posted = "posted.json";
    public const string PostedOneRow = "posted-one-row.json";
    public const string Held = "held.json";
    public const string HeldSeveral = "held-several.json";
    public const string HeldUnknownPayee = "held-unknown-payee.json";
    public const string DryRunRefused = "dry-run-refused.json";
    public const string DryRunFalseRefused = "dry-run-false-refused.json";
    public const string ModelAccountsRefused = "model-accounts-refused.json";
    public const string ControlTotalRefused = "control-total-refused.json";
    public const string Partial = "partial.json";
    public const string Failed = "failed.json";
    public const string NoResponse = "no-response.json";

    public static string Read(string name) => File.ReadAllText(Fixtures.PathOf("sync-post", name), Encoding.UTF8);

    public static HttpContent Content(string name) => new StringContent(Read(name), Encoding.UTF8, "application/json");
}
