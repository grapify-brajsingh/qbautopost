using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests;

/// <summary>The model against spec §5.2, read from EF metadata; no database is opened.</summary>
public sealed class ModelMappingTests
{
    [Theory]
    [InlineData(typeof(SavedRequestEntity), "saved_requests")]
    [InlineData(typeof(SavedRequestVersionEntity), "saved_request_versions")]
    [InlineData(typeof(PostAttemptEntity), "post_attempts")]
    [InlineData(typeof(SavedRequestEventEntity), "saved_request_events")]
    [InlineData(typeof(RefreshTokenEntity), "refresh_tokens")]
    [InlineData(typeof(UserAuditEntity), "user_audit")]
    [InlineData(typeof(AppUser), "users")]
    public void Should_MapEntityToSpecTable_When_ModelIsBuilt(Type entity, string table)
    {
        using var db = CreateContext();

        Assert.Equal(table, db.Model.FindEntityType(entity)!.GetTableName());
    }

    [Fact]
    public void Should_NameEveryColumnInSnakeCase_When_ModelIsBuilt()
    {
        using var db = CreateContext();

        var columns = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).Select(p => p.GetColumnName());

        Assert.All(columns, c => Assert.Matches("^[a-z][a-z0-9_]*$", c));
    }

    [Theory]
    [InlineData(SavedRequestStatus.Pending, "pending")]
    [InlineData(AttemptOutcome.InProgress, "in-progress")]
    [InlineData(ResolveOutcome.NotPosted, "not-posted")]
    public void Should_StoreEnumAsKebabCaseText_When_Converted(Enum value, string text)
    {
        var converter = (ValueConverter)Activator.CreateInstance(
            typeof(KebabCaseEnumConverter<>).MakeGenericType(value.GetType()))!;

        Assert.Equal(text, converter.ConvertToProvider(value));
        Assert.Equal(value, converter.ConvertFromProvider(text));
    }

    [Fact]
    public void Should_UseRowVersionAsConcurrencyToken_When_SavedRequestIsUpdated()
    {
        using var db = CreateContext();

        var rowVersion = db.Model.FindEntityType(typeof(SavedRequestEntity))!.FindProperty(nameof(SavedRequestEntity.RowVersion))!;

        Assert.True(rowVersion.IsConcurrencyToken);
    }

    private static QbAutopostDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<QbAutopostDbContext>().UseNpgsql("Host=127.0.0.1;Database=qbautopost_model_only").Options);
}
