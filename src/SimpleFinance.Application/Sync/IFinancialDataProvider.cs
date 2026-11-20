namespace SimpleFinance.Application.Sync;

/// <summary>
/// The ONLY seam any non-Infrastructure code may depend on for external financial-provider data
/// (per ARCHITECTURE.md Pattern 1 — never let PluggyClient be referenced directly from
/// Application or Api). Implemented by Infrastructure's PluggyClient/PluggyDataProvider (02-02).
/// Isolates the provider so a future swap (Belvo/Tecnospeed) never touches the Domain.
/// </summary>
public interface IFinancialDataProvider
{
    /// <summary>
    /// Requests a Connect Token from the provider. Pass <paramref name="existingPluggyItemId"/>
    /// to open the widget in update/reconnect mode for an existing Item; null for a brand-new
    /// connection.
    /// </summary>
    Task<string> CreateConnectTokenAsync(string? existingPluggyItemId, CancellationToken ct);

    Task<ProviderItemStatus> GetItemStatusAsync(string pluggyItemId, CancellationToken ct);

    Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string pluggyItemId, CancellationToken ct);

    /// <summary>
    /// Fetches the full available transaction window for one account. No <c>from</c>/DateOnly
    /// param — 02-03's cursor-based /v2/transactions always fetches the full available window
    /// per page, which suits the full-fetch-then-diff reconciler (cursor pagination has no
    /// arbitrary-page concept per 02-RESEARCH.md's Pattern 1 trade-off note).
    /// </summary>
    Task<IReadOnlyList<ProviderTransaction>> GetTransactionsAsync(string pluggyAccountId, CancellationToken ct);

    Task<IReadOnlyList<ProviderInvestment>> GetInvestmentsAsync(string pluggyItemId, CancellationToken ct);

    /// <summary>
    /// Fetches credit card bills (invoices) for a CREDIT account. Default empty implementation keeps
    /// test fakes source-compatible when they do not exercise bill reconciliation.
    /// </summary>
    Task<IReadOnlyList<ProviderCreditCardBill>> GetCreditCardBillsAsync(string pluggyAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderCreditCardBill>>([]);

    /// <summary>Maps to PATCH /items/{id} — triggers a new provider-side sync for the Item.</summary>
    Task TriggerItemUpdateAsync(string pluggyItemId, CancellationToken ct);

    /// <summary>Maps to DELETE /items/{id}.</summary>
    Task DeleteItemAsync(string pluggyItemId, CancellationToken ct);

    /// <summary>Submits an MFA challenge response. Maps to POST /items/{id}/mfa.</summary>
    Task SubmitMfaAsync(string pluggyItemId, string parameterName, string parameterValue, CancellationToken ct);
}
