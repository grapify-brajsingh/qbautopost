using System.ComponentModel;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace QbAutopost.Data.Tests;

/// <summary>
/// Marks a <see cref="SavedRequestStoreContract"/> subclass whose store needs a real PostgreSQL. Its inherited
/// <see cref="ContractFactAttribute"/> and <see cref="ContractTheoryAttribute"/> tests then behave as
/// <see cref="PostgresFactAttribute"/>: skipped with the reason when Docker cannot provide one.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresPostgresAttribute : Attribute;

/// <summary>A contract fact: a plain fact, or a <see cref="PostgresFactAttribute"/> on a <see cref="RequiresPostgresAttribute"/> class.</summary>
[XunitTestCaseDiscoverer("QbAutopost.Data.Tests.ContractFactDiscoverer", "QbAutopost.Data.Tests")]
public sealed class ContractFactAttribute : FactAttribute;

/// <summary>A contract theory, skipped like <see cref="ContractFactAttribute"/>.</summary>
[XunitTestCaseDiscoverer("QbAutopost.Data.Tests.ContractTheoryDiscoverer", "QbAutopost.Data.Tests")]
public sealed class ContractTheoryAttribute : TheoryAttribute;

public sealed class ContractFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    public override IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute) =>
        PostgresSkip.Reason(testMethod) is { } reason
            ? [new PostgresSkippedTestCase(DiagnosticMessageSink, discoveryOptions, testMethod, reason)]
            : base.Discover(discoveryOptions, testMethod, factAttribute);
}

public sealed class ContractTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    public override IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute) =>
        PostgresSkip.Reason(testMethod) is { } reason
            ? [new PostgresSkippedTestCase(DiagnosticMessageSink, discoveryOptions, testMethod, reason)]
            : base.Discover(discoveryOptions, testMethod, theoryAttribute);
}

internal static class PostgresSkip
{
    /// <summary>The skip reason, or null when the class needs no PostgreSQL or PostgreSQL is running.</summary>
    public static string? Reason(ITestMethod testMethod) =>
        testMethod.TestClass.Class is IReflectionTypeInfo { Type: var type }
        && type.IsDefined(typeof(RequiresPostgresAttribute), inherit: false)
            ? PostgresFixture.SharedSkipReason
            : null;
}

/// <summary>A test case that is reported as skipped with the reason PostgreSQL is not available.</summary>
public sealed class PostgresSkippedTestCase : XunitTestCase
{
    private string _reason = "";

    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public PostgresSkippedTestCase()
    {
    }

    public PostgresSkippedTestCase(
        IMessageSink diagnosticMessageSink, ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, string reason)
        : base(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod)
    {
        _reason = reason;
    }

    protected override string GetSkipReason(IAttributeInfo factAttribute) => _reason;

    public override void Serialize(IXunitSerializationInfo data)
    {
        base.Serialize(data);
        data.AddValue("PostgresSkipReason", _reason);
    }

    public override void Deserialize(IXunitSerializationInfo data)
    {
        _reason = data.GetValue<string>("PostgresSkipReason");
        base.Deserialize(data);
    }
}
