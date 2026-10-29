using QbAutopost.Core.Dashboard;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

public class AttemptResultRowsTests
{
    [Fact]
    public void Should_SumPostedArrayAmounts_When_ResultHasRows()
    {
        const string result = """
            {"postId":"P-1","status":"partial","totals":{"submitted":999.99,"posted":1.00},
             "posted":[{"index":0,"txnId":"T1","amount":0.10},{"index":1,"txnId":"T2","amount":0.20},{"index":2,"txnId":"T3","amount":1234567.89}],
             "held":[{"index":3,"amount":50.00}]}
            """;

        var (rows, amount) = AttemptResultRows.Read(result);

        Assert.Equal(3, rows);
        Assert.Equal(1234568.19m, amount);
    }

    [Fact]
    public void Should_ReturnZero_When_ResultNull()
    {
        Assert.Equal((0, 0m), AttemptResultRows.Read(null));
        Assert.Equal((0, 0m), AttemptResultRows.Read("""{"postId":"P-1","status":"failed"}"""));
        Assert.Equal((0, 0m), AttemptResultRows.Read("not json"));
    }
}
