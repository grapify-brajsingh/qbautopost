namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// Which canonical-form rules apply. <see cref="V1"/> is the original behaviour. <see cref="V2"/> (dashboard API v2,
/// spec §7) additionally ignores top-level <c>email</c>, <c>pushId</c> and <c>upload</c> (dashboard-api-m12 7.2), and treats a row's <c>post: true</c> and a
/// missing <c>post</c> as the same thing.
/// </summary>
public enum PayloadRules
{
    V1,
    V2,
}
