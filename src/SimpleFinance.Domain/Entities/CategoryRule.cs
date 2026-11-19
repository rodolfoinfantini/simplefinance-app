using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A user-defined categorization rule, per PRD CAT-03. Rules are evaluated in ascending
/// <see cref="Priority"/> order; the first active rule that matches a transaction wins and
/// sets its CategoryId + CategorySource=Rule. This table is schema-only in this plan (04-01) —
/// the matching/evaluation engine is 04-04's job, and rule CRUD/test/apply-now endpoints are
/// 04-05's job.
/// </summary>
public class CategoryRule : UserOwnedEntity
{
    public RuleMatchField MatchField { get; set; }

    public RuleMatchOperator MatchOperator { get; set; }

    /// <summary>
    /// The CONTAINS/EQUALS/REGEX operand for Description/Merchant/PluggyCategory fields; unused
    /// for RANGE. For EQUALS on Amount, holds the decimal as a string (parsed at match time).
    /// </summary>
    public string? MatchValue { get; set; }

    /// <summary>Inclusive lower bound for the RANGE operator on the Amount field only.</summary>
    public decimal? AmountMin { get; set; }

    /// <summary>Inclusive upper bound for the RANGE operator on the Amount field only.</summary>
    public decimal? AmountMax { get; set; }

    /// <summary>The target Category this rule assigns on match. No navigation property required.</summary>
    public Guid CategoryId { get; set; }

    /// <summary>Lower value = evaluated first. Rules are evaluated in ascending Priority order.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Inactive rules are skipped during automatic categorization (sync-time) but MAY still be
    /// targeted directly by the "apply now" retroactive action (04-05).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When true, matching transactions are also flagged as internal transfers (<c>IsTransfer = true</c>),
    /// excluding them from income/expense/budget totals (respecting <c>IsTransferManualOverride</c>).
    /// </summary>
    public bool MarkAsTransfer { get; set; }
}
