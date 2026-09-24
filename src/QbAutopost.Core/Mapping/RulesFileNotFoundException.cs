namespace QbAutopost.Core.Mapping;

/// <summary>
/// T-920: <c>Company:RulesFile</c> names a file that is not there.
/// <para>
/// Running without it is a state the app deliberately allows — startup warns and <c>/health/ready</c> reports
/// <c>rules: false</c> — so this is a misconfiguration to report, not a reason to refuse to start. It is typed so the
/// API can answer the RFC 7807 problem details the conventions promise, naming <b>the path that was actually
/// looked at</b>: the defect that prompted this was a relative path resolving against the content root
/// (<see cref="AppContext.BaseDirectory"/>, T-901) rather than the project folder, and no message said where.
/// </para>
/// </summary>
public sealed class RulesFileNotFoundException(string path)
    : FileNotFoundException($"The rules file was not found: {path}", path);
