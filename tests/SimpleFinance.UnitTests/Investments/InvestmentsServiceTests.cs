using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Investments;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Investments;

/// <summary>
/// Postgres-backed integration tests for InvestmentsService (06-03) -- InvestmentsService
/// delegates every actual aggregate to InvestmentQueries (Dapper), which requires a REAL
/// Postgres connection (EF Core's InMemory provider has no ADO.NET connection behind it for
/// Dapper to use). Same connection-string-resolution pattern as
/// InvestmentQueriesTests.cs/DashboardServiceTests.cs: ConnectionStrings__Test env var, else the
/// docker-compose default at 127.0.0.1:5433. Requires `docker compose up postgres -d` running
/// locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// InvestmentQueriesTests.cs/DashboardServiceTests.cs precedent).
/// </summary>
public class InvestmentsServiceTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static InvestmentsService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new InvestmentsService(new InvestmentQueries(new NpgsqlDbConnectionFactory(configuration)));
    }

    private static Investment NewInvestment(
        Guid userId,
        InvestmentType type = InvestmentType.FixedIncome,
        string? name = null,
        decimal? value = null,
        decimal? amountInvested = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyInvestmentId = $"inv-{Guid.NewGuid():N}",
        Name = name ?? "Test Investment",
        Type = type,
        Value = value,
        AmountInvested = amountInvested,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- GetPositionsAsync -- Gain/GainPercent ----

    [Fact]
    public async Task GetPositionsAsync_ValueAndAmountInvestedPresent_ComputesGainAndPercent()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, value: 1500m, amountInvested: 1000m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetPositionsAsync(userId, CancellationToken.None);

        var position = Assert.Single(result.Items);
        Assert.Equal(500m, position.Gain);
        Assert.Equal(50m, position.GainPercent);
    }

    [Fact]
    public async Task GetPositionsAsync_AmountInvestedNull_GainAndPercentNull()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, value: 1000m, amountInvested: null));
        await db.SaveChangesAsync();

        var result = await CreateService().GetPositionsAsync(userId, CancellationToken.None);

        var position = Assert.Single(result.Items);
        Assert.Null(position.Gain);
        Assert.Null(position.GainPercent);
    }

    [Fact]
    public async Task GetPositionsAsync_ValueNull_GainAndPercentNull()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, value: null, amountInvested: 1000m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetPositionsAsync(userId, CancellationToken.None);

        var position = Assert.Single(result.Items);
        Assert.Null(position.Gain);
        Assert.Null(position.GainPercent);
    }

    [Fact]
    public async Task GetPositionsAsync_AmountInvestedZero_GainComputed_PercentNull()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, value: 1000m, amountInvested: 0m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetPositionsAsync(userId, CancellationToken.None);

        var position = Assert.Single(result.Items);
        Assert.Equal(1000m, position.Gain);
        Assert.Null(position.GainPercent);
    }

    // ---- GetPositionsAsync -- Type display label (all 7 InvestmentType values) ----

    [Theory]
    [InlineData(InvestmentType.MutualFund, "Mutual Fund")]
    [InlineData(InvestmentType.FixedIncome, "Fixed Income")]
    [InlineData(InvestmentType.Equity, "Equity")]
    [InlineData(InvestmentType.Etf, "ETF")]
    [InlineData(InvestmentType.Security, "Security")]
    [InlineData(InvestmentType.Coe, "COE")]
    [InlineData(InvestmentType.Crypto, "Crypto")]
    public async Task GetPositionsAsync_MapsTypeToExactDisplayLabel(InvestmentType type, string expectedLabel)
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, type: type));
        await db.SaveChangesAsync();

        var result = await CreateService().GetPositionsAsync(userId, CancellationToken.None);

        var position = Assert.Single(result.Items);
        Assert.Equal(expectedLabel, position.Type);
    }

    // ---- GetSummaryAsync ----

    [Fact]
    public async Task GetSummaryAsync_NoInvestments_ReturnsZeroSafeSummary()
    {
        var userId = Guid.NewGuid();

        var result = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(0m, result.TotalInvested);
        Assert.Equal(0m, result.TotalCurrentValue);
        Assert.Equal(0m, result.TotalGain);
        Assert.Null(result.TotalGainPercent);
        Assert.Empty(result.ByType);
        Assert.Empty(result.ByIssuer);
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesTotalGainAndPercent_WhenTotalInvestedNonZero()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, value: 1500m, amountInvested: 1000m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(1000m, result.TotalInvested);
        Assert.Equal(1500m, result.TotalCurrentValue);
        Assert.Equal(500m, result.TotalGain);
        Assert.Equal(50m, result.TotalGainPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_ByTypeDistribution_PercentsProportionalToTotal()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, type: InvestmentType.Equity, value: 600m, amountInvested: 500m),
            NewInvestment(userId, type: InvestmentType.FixedIncome, value: 400m, amountInvested: 400m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(1000m, result.TotalCurrentValue);
        Assert.Equal(2, result.ByType.Count);

        var equity = result.ByType.Single(i => i.Label == "Equity");
        var fixedIncome = result.ByType.Single(i => i.Label == "Fixed Income");
        Assert.Equal(60m, equity.Percent);
        Assert.Equal(40m, fixedIncome.Percent);
        Assert.Equal(100m, equity.Percent + fixedIncome.Percent);
    }

    [Fact]
    public async Task GetSummaryAsync_EquitiesWithBalanceAndNullAmountInvested_SumsBalanceAndDoesNotReportFakeGain()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var petr = NewInvestment(userId, type: InvestmentType.Equity, name: "PETR4", value: 43.17m, amountInvested: null);
        petr.Balance = 86.34m;
        petr.Quantity = 2m;
        var bbas = NewInvestment(userId, type: InvestmentType.Equity, name: "BBAS3", value: 14.12m, amountInvested: null);
        bbas.Balance = 225.92m;
        bbas.Quantity = 16m;
        db.Investments.AddRange(petr, bbas);
        await db.SaveChangesAsync();

        var summary = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(312.26m, summary.TotalCurrentValue);
        Assert.Equal(0m, summary.TotalInvested);
        Assert.Equal(0m, summary.TotalGain);
        Assert.Null(summary.TotalGainPercent);
    }
}
