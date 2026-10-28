using QbAutopost.Core.Extraction;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>T-1212b: what the extraction tests look at — every record the fake holds, as stored.</summary>
public sealed partial class FakeSavedRequestStore
{
    public IReadOnlyList<StatementExtraction> Extractions
    {
        get
        {
            lock (_gate)
            {
                return _extractions.Values.ToList();
            }
        }
    }
}
