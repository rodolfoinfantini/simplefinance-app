using Dapper;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Reporting;

public sealed record InvestmentPositionRow(
    Guid Id, string Name, InvestmentType Type, string? Subtype, string? Institution, string? IssuerName,
    decimal? Value, decimal? AmountInvested, decimal? AnnualRate, decimal? LastMonthRate,
    DateTime? Date, DateTime? DueDate, string? CurrencyCode);

public sealed record InvestmentTotals(decimal TotalInvested, decimal TotalCurrentValue, decimal TotalGain);

public sealed record InvestmentTypeDistributionRow(InvestmentType Type, decimal Value);

public sealed record InvestmentIssuerDistributionRow(string IssuerLabel, decimal Value);

/// <summary>
/// The Dapper aggregate queries the Investments screen (06-03) reads from -- read-only over the
/// Investment rows already synced/upserted by Phase 2's reconciler. Mirrors DashboardQueries
/// (05-04)'s structure exactly: each method opens its own connection via
/// IDbConnectionFactory.CreateConnection() + synchronous connection.Open() (IDbConnection has no
/// async Open), then issues a single parameterized CommandDefinition.
///
/// Every query filters "UserId" = @UserId AND "DeletedAt" IS NULL (T-06-03) -- no cross-user
/// aggregate leakage. Investment has no IsHidden column (unlike Account) -- every non-deleted row
/// for the user is included, no hide filter in this phase's scope.
///
/// Null-tolerance is load-bearing throughout: Value/AmountInvested/AnnualRate/LastMonthRate are
/// all nullable on the Investment entity (Pluggy doesn't always return them). GetPositionsAsync
/// passes them through as-is (null stays null); GetTotalsAsync/GetDistributionByTypeAsync/
/// GetDistributionByIssuerAsync all COALESCE null contributions to 0 before summing, and
/// COALESCE the outer SUM() itself since SUM() over zero matching rows is SQL NULL.
/// </summary>
public sealed class InvestmentQueries(IDbConnectionFactory connectionFactory)
{
    // Pluggy's `balance` is the position's current net total value, while `value` is the unit
    // price of a single share/quota (and often null on fixed income). Prefer `Balance`, falling
    // back to `Value * Quantity` when both exist, and finally `Value` for legacy/minimal rows.
    private const string EffectiveValueSql =
        """COALESCE(i."Balance", CASE WHEN i."Value" IS NOT NULL AND i."Quantity" IS NOT NULL THEN i."Value" * i."Quantity" ELSE i."Value" END)""";

    public async Task<IReadOnlyList<InvestmentPositionRow>> GetPositionsAsync(Guid userId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT
                i."Id" AS "Id",
                i."Name" AS "Name",
                i."Type" AS "Type",
                i."Subtype" AS "Subtype",
                i."Institution" AS "Institution",
                i."IssuerName" AS "IssuerName",
                {EffectiveValueSql} AS "Value",
                i."AmountInvested" AS "AmountInvested",
                i."AnnualRate" AS "AnnualRate",
                i."LastMonthRate" AS "LastMonthRate",
                i."Date" AS "Date",
                i."DueDate" AS "DueDate",
                i."CurrencyCode" AS "CurrencyCode"
            FROM "Investments" i
            WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL
            ORDER BY COALESCE({EffectiveValueSql}, 0) DESC;
            """;

        var parameters = new { UserId = userId };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<InvestmentPositionRow>(command);
        return rows.ToList();
    }

    public async Task<InvestmentTotals> GetTotalsAsync(Guid userId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT
                COALESCE(SUM(COALESCE(i."AmountInvested", 0)), 0) AS "TotalInvested",
                COALESCE(SUM(COALESCE({EffectiveValueSql}, 0)), 0) AS "TotalCurrentValue",
                COALESCE(SUM(
                    CASE
                        WHEN i."AmountInvested" IS NOT NULL AND {EffectiveValueSql} IS NOT NULL
                        THEN {EffectiveValueSql} - i."AmountInvested"
                        ELSE 0
                    END
                ), 0) AS "TotalGain"
            FROM "Investments" i
            WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL;
            """;

        var parameters = new { UserId = userId };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        return await connection.QueryFirstAsync<InvestmentTotals>(command);
    }

    public async Task<IReadOnlyList<InvestmentTypeDistributionRow>> GetDistributionByTypeAsync(
        Guid userId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT
                i."Type" AS "Type",
                SUM(COALESCE({EffectiveValueSql}, 0)) AS "Value"
            FROM "Investments" i
            WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL
            GROUP BY i."Type"
            ORDER BY "Value" DESC;
            """;

        var parameters = new { UserId = userId };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<InvestmentTypeDistributionRow>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<InvestmentIssuerDistributionRow>> GetDistributionByIssuerAsync(
        Guid userId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT
                COALESCE(i."IssuerName", i."Institution", 'Unknown') AS "IssuerLabel",
                SUM(COALESCE({EffectiveValueSql}, 0)) AS "Value"
            FROM "Investments" i
            WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL
            GROUP BY COALESCE(i."IssuerName", i."Institution", 'Unknown')
            ORDER BY "Value" DESC;
            """;

        var parameters = new { UserId = userId };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<InvestmentIssuerDistributionRow>(command);
        return rows.ToList();
    }
}
