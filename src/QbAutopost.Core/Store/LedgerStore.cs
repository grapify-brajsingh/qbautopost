using System.Text.Json;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary>Loads and atomically saves <c>ledger.json</c>. Callers serialise access (single worker, ADR-0003).</summary>
public sealed class LedgerStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public Ledger Load()
    {
        if (!File.Exists(FilePath))
        {
            return Ledger.Empty;
        }

        return JsonSerializer.Deserialize<Ledger>(File.ReadAllText(FilePath), JsonOptions.Default)
            ?? throw new InvalidDataException($"Ledger file '{FilePath}' is empty or null.");
    }

    public void Save(Ledger ledger) => AtomicFile.WriteJson(FilePath, ledger);
}
