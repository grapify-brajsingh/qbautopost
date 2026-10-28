using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QbAutopost.Core.Security;
using QbAutopost.Data;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

public enum ProvisionOutcome
{
    Created,

    /// <summary>The email is already held (compared without regard to case).</summary>
    EmailExists,

    /// <summary>Identity refused the user or the password; <see cref="ProvisionResult.Errors"/> says which rule.</summary>
    Refused,
}

public sealed record ProvisionResult(ProvisionOutcome Outcome, AppUser? User, IReadOnlyList<string> Errors);

/// <summary>
/// Saved-requests spec 11.4, 5.3: how a dashboard user comes to exist. The user and their one role are saved in a
/// single transaction, so a user never exists without a role (Q-87). Shared by <c>POST /users</c> and the
/// <see cref="BootstrapAdmin"/>.
/// </summary>
public static class UserProvisioning
{
    public const int MaxEmailLength = 254;

    public const int MaxDisplayNameLength = 100;

    /// <summary>The canonical spelling of <paramref name="role"/> (<see cref="DashboardRoles.All"/>), or null when it is not one.</summary>
    public static string? CanonicalRole(string? role) =>
        DashboardRoles.All.FirstOrDefault(r => r.Equals(role?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True for <c>name@host.tld</c> with no spaces: the shape Identity and the sign-in form both rely on.</summary>
    public static bool IsEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength || email.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return MailAddress.TryCreate(email, out var parsed)
               && parsed.Address.Equals(email, StringComparison.Ordinal)
               && parsed.Host.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes sure the Identity role named <paramref name="role"/> exists. Outside any transaction on purpose: a
    /// unique-index failure inside one would abort it.
    /// </summary>
    public static async Task EnsureRoleAsync(RoleManager<IdentityRole> roles, string role)
    {
        if (await roles.RoleExistsAsync(role))
        {
            return;
        }

        var created = await roles.CreateAsync(new IdentityRole(role));
        if (!created.Succeeded && !await roles.RoleExistsAsync(role))
        {
            throw new InvalidOperationException($"Identity role '{role}' could not be created: {string.Join("; ", created.Errors.Select(e => e.Code))}");
        }
    }

    /// <summary>
    /// Creates the user with <paramref name="temporaryPassword"/> (which Identity hashes; it is stored nowhere else),
    /// <c>mustChangePassword: true</c>, and exactly <paramref name="role"/>.
    /// </summary>
    public static async Task<ProvisionResult> CreateAsync(
        UserManager<AppUser> users,
        RoleManager<IdentityRole> roles,
        QbAutopostDbContext db,
        string email,
        string displayName,
        string role,
        string temporaryPassword,
        string createdBy,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await EnsureRoleAsync(roles, role);
        if (await users.FindByEmailAsync(email) is not null)
        {
            return new ProvisionResult(ProvisionOutcome.EmailExists, null, []);
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            MustChangePassword = true,
            CreatedUtc = nowUtc,
            CreatedBy = createdBy,
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = await users.CreateAsync(user, temporaryPassword);
            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return Failed(created);
            }

            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return Failed(added);
            }

            await transaction.CommitAsync(ct);
            return new ProvisionResult(ProvisionOutcome.Created, user, []);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two creations of one email at once: the unique index decides, and the loser is a plain duplicate.
            return new ProvisionResult(ProvisionOutcome.EmailExists, null, []);
        }
    }

    private static ProvisionResult Failed(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return new ProvisionResult(ProvisionOutcome.EmailExists, null, []);
        }

        return new ProvisionResult(ProvisionOutcome.Refused, null, result.Errors.Select(e => e.Description).ToList());
    }
}
