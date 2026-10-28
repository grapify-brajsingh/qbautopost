using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace QbAutopost.Data;

/// <summary>
/// Snake_case for every table, column, key, foreign key and index name (spec §5.1). A small loop instead of a
/// naming-convention package, which is not approved (Q-80).
/// </summary>
public static class SnakeCaseNames
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        var entities = modelBuilder.Model.GetEntityTypes().ToList();

        // Columns first: the default key and index names are built from the column names.
        foreach (var entity in entities)
        {
            if (entity.GetTableName() is { } table)
            {
                entity.SetTableName(ToSnake(table));
            }

            var tableId = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.GetColumnName(tableId) ?? property.Name));
            }
        }

        foreach (var entity in entities)
        {
            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnake(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnake(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnake(index.GetDatabaseName()!));
            }
        }
    }

    /// <summary><c>NormalizedUserName</c> → <c>normalized_user_name</c>; <c>OriginalSha256</c> → <c>original_sha256</c>.</summary>
    public static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '_';
                var next = i + 1 < name.Length ? name[i + 1] : '_';
                var startsWord = char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next));
                if (i > 0 && previous != '_' && startsWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
