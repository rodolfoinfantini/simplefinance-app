using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A bank or credit account belonging to a Connection, per PRD §11.3. Supports PLG-03/SYNC-04.
/// </summary>
public class Account : UserOwnedEntity
{
    public Guid ConnectionId { get; set; }

    /// <summary>Pluggy's Account UUID. Unique per (ConnectionId, PluggyAccountId).</summary>
    public string PluggyAccountId { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    public AccountSubtype Subtype { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? MarketingName { get; set; }

    public string? NumberMasked { get; set; }

    public string? Owner { get; set; }

    public decimal Balance { get; set; }

    /// <summary>
    /// Funds held in automatic investment / reserve vaults linked to the bank account
    /// (e.g. Pluggy <c>bankData.automaticallyInvestedBalance</c> for Mercado Pago Cofrinhos).
    /// </summary>
    public decimal? AutomaticallyInvestedBalance { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>ACCT-03 groundwork for Phase 3 — hide an account from consolidated views.</summary>
    public bool IsHidden { get; set; }
}
