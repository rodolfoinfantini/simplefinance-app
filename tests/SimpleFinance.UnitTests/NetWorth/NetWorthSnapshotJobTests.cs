using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.NetWorth;

/// <summary>
/// Postgres-backed integration tests for NetWorthSnapshotJob (06-04, INV-03/INV-04) --
/// NetWorthSnapshotJob delegates every composition figure to DashboardQueries (Dapper), which
/// requires a REAL Postgres connection (EF Core's InMemory provider has no ADO.NET connection
/// behind it for Dapper to use). Same connection-string-resolution pattern as
/// DashboardQueriesTests.cs/InvestmentsServiceTests.cs: ConnectionStrings__Test env var, else the
/// docker-compose default at 127.0.0.1:5433. Requires `docker compose up postgres -d` running
/// locally first.
///
/// Every test scopes its rows to fresh Guid User ids, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// DashboardQueriesTests.cs/InvestmentsServiceTests.cs precedent).
/// </summary>
public class NetWorthSnapshotJobTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static DashboardQueries CreateDashboardQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new DashboardQueries(new NpgsqlDbConnectionFactory(configuration));
    }

    private static User NewUser(string email) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test User",
        Email = email,
        PasswordHash = "hash",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Account NewAccount(
        Guid userId, AccountType type, decimal balance, bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = "Test Account",
        Balance = balance,
        CurrencyCode = "BRL",
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreditCardDetail NewCreditCardDetail(Guid accountId, decimal? lastStatementBalance) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        LastStatementBalance = lastStatementBalance,
        Status = CreditCardStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Investment NewInvestment(Guid userId, decimal? value) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyInvestmentId = $"inv-{Guid.NewGuid():N}",
        Name = "Test Investment",
        Type = InvestmentType.FixedIncome,
        Value = value,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- GetNetWorthCompositionAsync formula (Dapper) ----

    [Fact]
    public async Task GetNetWorthCompositionAsync_MatchesGetTotalsAsyncNetWorthFormula_SplitIntoColumns()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Users.Add(NewUser($"{Guid.NewGuid():N}@example.com"));
        var bankAccount = NewAccount(userId, AccountType.Bank, balance: 5000m);
        var creditAccount = NewAccount(userId, AccountType.Credit, balance: 0m);
        db.Accounts.AddRange(bankAccount, creditAccount);
        db.CreditCardDetails.Add(NewCreditCardDetail(creditAccount.Id, lastStatementBalance: 800m));
        db.Investments.Add(NewInvestment(userId, value: 2000m));
        await db.SaveChangesAsync();

        var composition = await CreateDashboardQueries().GetNetWorthCompositionAsync(userId, CancellationToken.None);

        // AccountsBalance sums Bank accounts only; CreditCardDebt is tracked separately without
        // subtracting future credit-card installments from NetWorth.
        Assert.Equal(5000m, composition.AccountsBalance);
        Assert.Equal(2000m, composition.InvestmentsBalance);
        Assert.Equal(800m, composition.CreditCardDebt);
        Assert.Equal(5000m + 2000m, composition.NetWorth);
    }

    [Fact]
    public async Task GetNetWorthCompositionAsync_ExcludesHiddenAccount_AndHiddenCreditAccount()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var visibleAccount = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var hiddenAccount = NewAccount(userId, AccountType.Bank, balance: 9000m, isHidden: true);
        var hiddenCreditAccount = NewAccount(userId, AccountType.Credit, balance: 0m, isHidden: true);
        db.Accounts.AddRange(visibleAccount, hiddenAccount, hiddenCreditAccount);
        db.CreditCardDetails.Add(NewCreditCardDetail(hiddenCreditAccount.Id, lastStatementBalance: 500m));
        await db.SaveChangesAsync();

        var composition = await CreateDashboardQueries().GetNetWorthCompositionAsync(userId, CancellationToken.None);

        Assert.Equal(1000m, composition.AccountsBalance);
        Assert.Equal(0m, composition.CreditCardDebt);
        Assert.Equal(1000m, composition.NetWorth);
    }

    // ---- NetWorthSnapshotJob.ExecuteAsync -- idempotent upsert ----

    [Fact]
    public async Task ExecuteAsync_CalledTwiceSameDay_ExactlyOneRow_WithSecondCallsValues()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var job = new NetWorthSnapshotJob(db, CreateDashboardQueries(), NullLogger<NetWorthSnapshotJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        // Change the underlying data between the two runs -- the second run's upsert must reflect
        // this new value, proving update-not-duplicate rather than "doesn't crash twice".
        account.Balance = 4500m;
        await db.SaveChangesAsync();

        await job.ExecuteAsync(CancellationToken.None);

        var today = DateTime.UtcNow.Date;
        var snapshots = await db.NetWorthSnapshots
            .Where(s => s.UserId == userId && s.Date == today)
            .ToListAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(4500m, snapshot.AccountsBalance);
        Assert.Equal(4500m, snapshot.NetWorth);
    }

    [Fact]
    public async Task ExecuteAsync_TwoDifferentUsers_EachGetsOwnSnapshotRow_NoCrossUserCollision()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user1 = NewUser($"{Guid.NewGuid():N}@example.com");
        user1.Id = user1Id;
        var user2 = NewUser($"{Guid.NewGuid():N}@example.com");
        user2.Id = user2Id;
        db.Users.AddRange(user1, user2);
        db.Accounts.AddRange(
            NewAccount(user1Id, AccountType.Bank, balance: 1000m),
            NewAccount(user2Id, AccountType.Bank, balance: 7000m));
        await db.SaveChangesAsync();

        var job = new NetWorthSnapshotJob(db, CreateDashboardQueries(), NullLogger<NetWorthSnapshotJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        var today = DateTime.UtcNow.Date;
        var snapshot1 = await db.NetWorthSnapshots.SingleAsync(s => s.UserId == user1Id && s.Date == today);
        var snapshot2 = await db.NetWorthSnapshots.SingleAsync(s => s.UserId == user2Id && s.Date == today);

        Assert.Equal(1000m, snapshot1.NetWorth);
        Assert.Equal(7000m, snapshot2.NetWorth);
    }

    [Fact]
    public async Task ExecuteAsync_UserWithZeroPortfolio_StillGetsAllZeroSnapshotRow_NeverThrows()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var job = new NetWorthSnapshotJob(db, CreateDashboardQueries(), NullLogger<NetWorthSnapshotJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        var today = DateTime.UtcNow.Date;
        var snapshot = await db.NetWorthSnapshots.SingleAsync(s => s.UserId == userId && s.Date == today);

        Assert.Equal(0m, snapshot.AccountsBalance);
        Assert.Equal(0m, snapshot.InvestmentsBalance);
        Assert.Equal(0m, snapshot.CreditCardDebt);
        Assert.Equal(0m, snapshot.NetWorth);
    }

    [Fact]
    public async Task ExecuteAsync_OneUsersSaveThrows_OtherUsersSnapshotStillWritten_BatchNeverAborts()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        await using var db = new ThrowOnSaveForUserDbContext(
            new DbContextOptionsBuilder<SimpleFinanceDbContext>().UseNpgsql(GetConnectionString(), vec => vec.UseVector()).Options,
            throwForUserId: user1Id);

        var user1 = NewUser($"{Guid.NewGuid():N}@example.com");
        user1.Id = user1Id;
        var user2 = NewUser($"{Guid.NewGuid():N}@example.com");
        user2.Id = user2Id;
        db.Users.AddRange(user1, user2);
        db.Accounts.AddRange(
            NewAccount(user1Id, AccountType.Bank, balance: 1000m),
            NewAccount(user2Id, AccountType.Bank, balance: 2000m));
        db.DisableThrow = true;
        await db.SaveChangesAsync();
        db.DisableThrow = false;

        var job = new NetWorthSnapshotJob(db, CreateDashboardQueries(), NullLogger<NetWorthSnapshotJob>.Instance);

        // Must not throw -- user1's simulated save failure is caught and logged, never allowed to
        // abort the batch (T-06-06). User2's snapshot must still be written.
        await job.ExecuteAsync(CancellationToken.None);

        var today = DateTime.UtcNow.Date;
        var user1Snapshot = await db.NetWorthSnapshots
            .SingleOrDefaultAsync(s => s.UserId == user1Id && s.Date == today);
        var user2Snapshot = await db.NetWorthSnapshots
            .SingleOrDefaultAsync(s => s.UserId == user2Id && s.Date == today);

        Assert.Null(user1Snapshot);
        Assert.NotNull(user2Snapshot);
        Assert.Equal(2000m, user2Snapshot!.NetWorth);
    }

    /// <summary>
    /// Test-only DbContext subclass that throws on SaveChangesAsync whenever a tracked
    /// NetWorthSnapshot for a specific UserId is about to be persisted -- simulates a transient
    /// per-user DB failure (per 06-04-PLAN.md's "malformed/unreachable scenario") without touching
    /// production code, exercising NetWorthSnapshotJob's per-user try/catch isolation (T-06-06)
    /// against a real database for every other user in the same batch.
    /// </summary>
    private sealed class ThrowOnSaveForUserDbContext(
        DbContextOptions<SimpleFinanceDbContext> options, Guid throwForUserId)
        : SimpleFinanceDbContext(options)
    {
        private readonly Guid _throwForUserId = throwForUserId;

        public bool DisableThrow { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!DisableThrow && ChangeTracker.Entries<NetWorthSnapshot>()
                    .Any(e => e.Entity.UserId == _throwForUserId
                        && (e.State == EntityState.Added || e.State == EntityState.Modified)))
            {
                throw new InvalidOperationException("simulated per-user snapshot save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
