using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Reporting;

/// <summary>
/// Postgres-backed integration tests for DashboardQueries (05-04) — Dapper's raw SQL isn't
/// exercised by EF Core's InMemory provider, so these run against a real local Postgres, exactly
/// like ConcurrentSyncTests.cs (127.0.0.1:5433, ConnectionStrings__Test env var else the
/// docker-compose default). Requires `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// ConcurrentSyncTests.cs's precedent).
/// </summary>
public class DashboardQueriesTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static DashboardQueries CreateQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new DashboardQueries(new NpgsqlDbConnectionFactory(configuration));
    }

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

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type,
        bool isTransfer = false, bool isHidden = false, Guid? categoryId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = "Test Transaction",
        Amount = amount,
        Type = type,
        CurrencyCode = "BRL",
        Status = TransactionStatus.Posted,
        CategorySource = CategorySource.SystemMapping,
        CategoryId = categoryId,
        IsTransfer = isTransfer,
        IsHidden = isHidden,
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

    private static Category NewCategory(Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        Type = CategoryType.Expense,
        Icon = "icon",
        Color = "#000000",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetTotalsAsync_ExcludesTransfer_FromPeriodIncome()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            // Real income -- must be counted.
            NewTransaction(userId, account.Id, midPeriod, amount: 500m, TransactionType.Credit),
            // A transfer disguised as a large Credit -- must be EXCLUDED from PeriodIncome.
            NewTransaction(userId, account.Id, midPeriod, amount: 999_999m, TransactionType.Credit, isTransfer: true));
        await db.SaveChangesAsync();

        var totals = await CreateQueries().GetTotalsAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(500m, totals.PeriodIncome);
    }

    [Fact]
    public async Task GetTotalsAsync_ExcludesHidden_FromPeriodExpense()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            // Real expense -- must be counted.
            NewTransaction(userId, account.Id, midPeriod, amount: 200m, TransactionType.Debit),
            // Hidden expense -- must be EXCLUDED from PeriodExpense.
            NewTransaction(userId, account.Id, midPeriod, amount: 777m, TransactionType.Debit, isHidden: true));
        await db.SaveChangesAsync();

        var totals = await CreateQueries().GetTotalsAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(200m, totals.PeriodExpense);
    }

    [Fact]
    public async Task GetTotalsAsync_ExcludesHiddenAccountBalance_AndNullInvestmentValueContributesZero()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var visibleAccount = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var hiddenAccount = NewAccount(userId, AccountType.Bank, balance: 5000m, isHidden: true);
        db.Accounts.AddRange(visibleAccount, hiddenAccount);
        db.Investments.Add(NewInvestment(userId, value: null));
        await db.SaveChangesAsync();

        // Must not throw despite the null Investment.Value -- it should contribute 0, not
        // null-propagate the whole SUM to NULL.
        var totals = await CreateQueries().GetTotalsAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(1000m, totals.TotalBalance);
        Assert.Equal(1000m, totals.NetWorth);
    }

    [Fact]
    public async Task GetTotalsAsync_TotalCardDebt_SumsLastStatementBalance_ForNonHiddenCreditAccountsOnly()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var visibleCard = NewAccount(userId, AccountType.Credit, balance: 0m);
        var hiddenCard = NewAccount(userId, AccountType.Credit, balance: 0m, isHidden: true);
        db.Accounts.AddRange(visibleCard, hiddenCard);
        db.CreditCardDetails.AddRange(
            NewCreditCardDetail(visibleCard.Id, lastStatementBalance: 300m),
            // Null LastStatementBalance -- must be COALESCEd to 0, not null-propagate.
            NewCreditCardDetail(hiddenCard.Id, lastStatementBalance: null));
        await db.SaveChangesAsync();

        var totals = await CreateQueries().GetTotalsAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(300m, totals.TotalCardDebt);
        Assert.Equal(-300m, totals.NetWorth);
    }

    [Fact]
    public async Task GetTotalsAndComposition_CreditCardWithPositiveBalanceAndNullLastStatementBalance_SubtractedAsDebtNotAddedToAccounts()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var bankAccount = NewAccount(userId, AccountType.Bank, balance: 751.31m);
        var creditCard = NewAccount(userId, AccountType.Credit, balance: 14673.06m);
        db.Accounts.AddRange(bankAccount, creditCard);
        db.CreditCardDetails.Add(NewCreditCardDetail(creditCard.Id, lastStatementBalance: null));
        var investment = NewInvestment(userId, value: 43.17m);
        investment.Balance = 654.01m;
        db.Investments.Add(investment);
        await db.SaveChangesAsync();

        var queries = CreateQueries();
        var totals = await queries.GetTotalsAsync(userId, periodStart, periodEnd, CancellationToken.None);
        var composition = await queries.GetNetWorthCompositionAsync(userId, CancellationToken.None);

        Assert.Equal(751.31m, totals.TotalBalance);
        Assert.Equal(14673.06m, totals.TotalCardDebt);
        Assert.Equal(751.31m + 654.01m - 14673.06m, totals.NetWorth);
        Assert.Equal(751.31m, composition.AccountsBalance);
        Assert.Equal(654.01m, composition.InvestmentsBalance);
        Assert.Equal(14673.06m, composition.CreditCardDebt);
        Assert.Equal(totals.NetWorth, composition.NetWorth);
    }

    [Fact]
    public async Task GetCategorySpendAsync_ExcludesTransfer_EvenWhenTypeIsDebit()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var category = NewCategory(userId, "Groceries");
        db.Accounts.Add(account);
        db.Categories.Add(category);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 50m, TransactionType.Debit, categoryId: category.Id),
            // A Debit-typed transfer -- must not appear in ANY group's Amount.
            NewTransaction(userId, account.Id, midPeriod, amount: 10_000m, TransactionType.Debit, isTransfer: true, categoryId: category.Id));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCategorySpendAsync(userId, periodStart, periodEnd, CancellationToken.None);

        var groceriesRow = Assert.Single(rows);
        Assert.Equal(category.Id, groceriesRow.CategoryId);
        Assert.Equal(50m, groceriesRow.Amount);
    }

    [Fact]
    public async Task GetCategorySpendAsync_SumsSameCategory_AndProducesNullRowForUncategorized()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var category = NewCategory(userId, "Groceries");
        db.Accounts.Add(account);
        db.Categories.Add(category);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 30m, TransactionType.Debit, categoryId: category.Id),
            NewTransaction(userId, account.Id, midPeriod, amount: 20m, TransactionType.Debit, categoryId: category.Id),
            NewTransaction(userId, account.Id, midPeriod, amount: 15m, TransactionType.Debit, categoryId: null));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCategorySpendAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var groceriesRow = Assert.Single(rows, r => r.CategoryId == category.Id);
        Assert.Equal(50m, groceriesRow.Amount);
        var uncategorizedRow = Assert.Single(rows, r => r.CategoryId == null);
        Assert.Equal(15m, uncategorizedRow.Amount);
        // Sorted descending by Amount.
        Assert.Equal(groceriesRow, rows[0]);
    }

    [Fact]
    public async Task GetIncomeExpenseByMonthAsync_GroupsByMonth_ExcludingTransferAndHidden()
    {
        var userId = Guid.NewGuid();
        var rangeStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var rangeEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var mayDate = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var juneDate = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, mayDate, amount: 1000m, TransactionType.Credit),
            NewTransaction(userId, account.Id, mayDate, amount: 400m, TransactionType.Debit),
            NewTransaction(userId, account.Id, juneDate, amount: 300m, TransactionType.Debit),
            // Excluded: transfer and hidden.
            NewTransaction(userId, account.Id, juneDate, amount: 50_000m, TransactionType.Credit, isTransfer: true),
            NewTransaction(userId, account.Id, juneDate, amount: 999m, TransactionType.Debit, isHidden: true));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetIncomeExpenseByMonthAsync(userId, rangeStart, rangeEnd, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var may = Assert.Single(rows, r => r.Year == 2026 && r.Month == 5);
        Assert.Equal(1000m, may.Income);
        Assert.Equal(400m, may.Expense);
        var june = Assert.Single(rows, r => r.Year == 2026 && r.Month == 6);
        Assert.Equal(0m, june.Income);
        Assert.Equal(300m, june.Expense);
    }

    [Fact]
    public async Task GetNetWorthEvolutionAsync_ReturnsEmptyList_NotThrow()
    {
        var userId = Guid.NewGuid();
        var rangeStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // No NetWorthSnapshot rows are seeded for this userId -- the table is schema-only until
        // Phase 6's INV-04 job. This must return an empty list, never throw.
        var rows = await CreateQueries().GetNetWorthEvolutionAsync(userId, rangeStart, CancellationToken.None);

        Assert.Empty(rows);
    }
}
