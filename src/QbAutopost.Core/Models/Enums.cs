namespace QbAutopost.Core.Models;

/// <summary>Where a statement line came from (spec §7).</summary>
public enum SourceKind
{
    Bank,
    Card,
}

/// <summary>Money direction as seen by the statement's account: debit = money out / charge, credit = money in / refund.</summary>
public enum Direction
{
    Debit,
    Credit,
}

/// <summary>QuickBooks transaction a line maps to (spec FR-6).</summary>
public enum TxnKind
{
    Check,
    CcCharge,
    CcCredit,
    Deposit,
    Skip,
}

/// <summary>How the line account was decided (spec FR-6/FR-7).</summary>
public enum Confidence
{
    Rule,
    History,
    Invoice,
    Model,
    Holding,
    Hold,
}

/// <summary>What happens to a mapped line (spec §10 <c>decision</c>).</summary>
public enum Decision
{
    Post,
    Hold,
    Skip,
}
