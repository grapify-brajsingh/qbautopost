using System.Text.Json;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary><c>qb-lists.json</c> (spec FR-15). Missing file → empty lists (sync has not run yet).</summary>
public sealed class QbListsStore(string filePath)
{
    public QbLists Load() =>
        File.Exists(filePath)
            ? JsonSerializer.Deserialize<QbLists>(AtomicFile.ReadAllText(filePath), JsonOptions.Default) ?? QbLists.Empty
            : QbLists.Empty;

    public void Save(QbLists lists) => AtomicFile.WriteJson(filePath, lists);
}
