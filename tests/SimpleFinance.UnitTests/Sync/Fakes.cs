using SimpleFinance.Application.Common;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// Hand-built IFinancialDataProvider test double (per 02-CONTEXT.md's Claude's Discretion note —
/// not a mocking framework) shared by SyncConnectionUseCaseTests (EF InMemory) and
/// ConcurrentSyncTests (real Postgres, Task 3) so both exercise the identical fake-provider
/// contract. Kept internal (not per-file private) specifically so it's reusable across those two
/// test classes without duplicating a second copy.
/// </summary>
internal sealed class FakeFinancialDataProvider : IFinancialDataProvider
{
    public List<ProviderAccount> Accounts { get; set; } = [];

    public List<ProviderInvestment> Investments { get; set; } = [];

    public Dictionary<string, List<ProviderTransaction>> TransactionsByAccountId { get; set; } = [];

    public Dictionary<string, List<ProviderCreditCardBill>> BillsByAccountId { get; set; } = [];

    public ProviderItemStatus ItemStatus { get; set; } =
        new("UPDATED", "SUCCESS", null, null, null, 1, "TestConnector", "Test Bank", null);

    /// <summary>Optional hook invoked at the start of every GetAccountsAsync call — Task 3's
    /// concurrency test uses this to inject an artificial delay that widens the race window.</summary>
    public Func<CancellationToken, Task>? OnGetAccounts { get; set; }

    /// <summary>When set, GetAccountsAsync throws this instead of returning — exercises the
    /// failure/SyncLog path (Task 3).</summary>
    public Exception? ThrowOnGetAccounts { get; set; }

    public Task<string> CreateConnectTokenAsync(string? existingPluggyItemId, CancellationToken ct) =>
        Task.FromResult("fake-connect-token");

    public Task<ProviderItemStatus> GetItemStatusAsync(string pluggyItemId, CancellationToken ct) =>
        Task.FromResult(ItemStatus);

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string pluggyItemId, CancellationToken ct)
    {
        if (OnGetAccounts is not null)
        {
            await OnGetAccounts(ct);
        }

        if (ThrowOnGetAccounts is not null)
        {
            throw ThrowOnGetAccounts;
        }

        return Accounts;
    }

    public Task<IReadOnlyList<ProviderTransaction>> GetTransactionsAsync(string pluggyAccountId, CancellationToken ct)
    {
        IReadOnlyList<ProviderTransaction> result = TransactionsByAccountId.TryGetValue(pluggyAccountId, out var list)
            ? list
            : [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProviderInvestment>> GetInvestmentsAsync(string pluggyItemId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderInvestment>>(Investments);

    public Task<IReadOnlyList<ProviderCreditCardBill>> GetCreditCardBillsAsync(string pluggyAccountId, CancellationToken ct)
    {
        IReadOnlyList<ProviderCreditCardBill> result = BillsByAccountId.TryGetValue(pluggyAccountId, out var list)
            ? list
            : [];
        return Task.FromResult(result);
    }

    public Task TriggerItemUpdateAsync(string pluggyItemId, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteItemAsync(string pluggyItemId, CancellationToken ct) => Task.CompletedTask;

    public Task SubmitMfaAsync(string pluggyItemId, string parameterName, string parameterValue, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>No-op distributed lock for InMemory-backed tests — the real Postgres-backed
/// PostgresAdvisoryLockProvider is exercised separately in Task 3's ConcurrentSyncTests, which
/// requires a real Postgres connection.</summary>
internal sealed class FakeDistributedLockProvider : IDistributedLockProvider
{
    public Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(new NoopHandle());

    private sealed class NoopHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Always-null-category fake — used only where the categorization outcome itself isn't
/// under test (Task 1's account/investment-only tests). Task 2's transaction-categorization tests
/// use the real CategoryRuleEngine against the same InMemory DbContext instead.</summary>
internal sealed class FakeCategorizationService : ICategorizationService
{
    public Task<CategorizationResult> CategorizeAsync(Guid userId, CategorizationInput input, CancellationToken ct) =>
        Task.FromResult(new CategorizationResult(null, CategorySource.SystemMapping));
}

/// <summary>No-op spy for ITransferDetectionService (03-02) — records every UserId it was called
/// with (matching FakeCategorizationService's existing spy style) so SyncConnectionUseCaseTests
/// can assert the post-sync detection hook fires exactly once per successful run, without
/// exercising TransferDetectionService's own matching logic (covered separately by
/// TransferDetectionServiceTests).</summary>
internal sealed class FakeTransferDetectionService : ITransferDetectionService
{
    public List<Guid> CalledWithUserIds { get; } = [];

    public Task<int> DetectTransfersForUserAsync(Guid userId, CancellationToken ct)
    {
        CalledWithUserIds.Add(userId);
        return Task.FromResult(0);
    }
}
