using Dapper;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Reporting;

public sealed record CashFlowByAccountRow(
    Guid AccountId, string AccountName, decimal Inflow, decimal Outflow, decimal Net);

public sealed record CardStatementRow(
    Guid AccountId, string AccountName, int Year, int Month, decimal StatementTotal,
    decimal? CreditLimit, DateTime? BalanceDueDate);

public sealed record TopMerchantRow(string MerchantName, decimal TotalSpent, int TxnCount);

public sealed record WeekdaySpendRow(int DayOfWeek, decimal TotalAmount, int TransactionCount);

/// <summary>
/// RPT-03: the 2 new Dapper aggregates Phase 7's Reports feature needs that don't already exist
/// in DashboardQueries/InvestmentQueries -- cash-flow-by-account and card-statements-by-month.
/// Sibling class to DashboardQueries/InvestmentQueries in the same namespace, following
/// InvestmentQueries.cs's exact structural precedent: constructor-injects IDbConnectionFactory,
/// each method opens its own connection via CreateConnection() + synchronous connection.Open()
/// (IDbConnection has no async Open), issues one parameterized CommandDefinition.
///
/// Every enum comparison is bound via a named parameter from the C#'s (int) cast -- never a bare
/// numeric literal inlined into the SQL string. Every query explicitly filters
/// "UserId" = @UserId AND "DeletedAt" IS NULL on every table touched (T-07-11) -- no cross-user
/// aggregate leakage. Every income/expense/spend figure excludes IsTransfer=true/IsHidden=true
/// rows and uses SUM(ABS(Amount)), mirroring DashboardQueries.cs's documented
/// exclusion-and-ABS discipline verbatim (the same correctness rule applies here, not a new one)
/// -- income/expense/inflow/outflow are always distinguished by Transaction.Type, never by the
/// sign of Amount. Both methods additionally restrict to Status = Posted (Pending transactions
/// are not yet final and shouldn't appear in a period report).
/// </summary>
public sealed class ReportQueries(IDbConnectionFactory connectionFactory) : ITopMerchantsReader
{
    /// <summary>
    /// Groups non-transfer, non-hidden, POSTED transactions by AccountId within
    /// [periodStart, periodEnd], joined to Accounts for AccountName, scoped to non-hidden
    /// accounts only (matching every other Phase 5/6 "hidden excluded" convention). Uses an
    /// INNER JOIN to Transactions, so an account with zero matching transactions in the period
    /// simply doesn't appear in the result -- the caller only sees accounts with actual activity.
    /// </summary>
    public async Task<IReadOnlyList<CashFlowByAccountRow>> GetCashFlowByAccountAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        const string sql = """
            SELECT
                a."Id" AS "AccountId",
                a."Name" AS "AccountName",
                COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @TxnCreditType), 0) AS "Inflow",
                COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @DebitType), 0) AS "Outflow",
                COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @TxnCreditType), 0)
                    - COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @DebitType), 0) AS "Net"
            FROM "Accounts" a
            INNER JOIN "Transactions" t
                ON t."AccountId" = a."Id"
                AND t."UserId" = @UserId
                AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
                AND t."IsTransfer" = false
                AND t."IsHidden" = false
                AND t."Status" = @PostedStatus
                AND t."DeletedAt" IS NULL
            WHERE a."UserId" = @UserId
              AND a."IsHidden" = false
              AND a."DeletedAt" IS NULL
            GROUP BY a."Id", a."Name"
            ORDER BY a."Name";
            """;

        var parameters = new
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DebitType = (int)TransactionType.Debit,
            TxnCreditType = (int)TransactionType.Credit,
            PostedStatus = (int)TransactionStatus.Posted,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<CashFlowByAccountRow>(command);
        return rows.ToList();
    }

    /// <summary>
    /// For every Credit-type non-hidden Account belonging to the user, groups that account's own
    /// non-transfer/non-hidden/POSTED transactions by (year, month) within
    /// [periodStart, periodEnd], left-joined to the account's CreditCardDetail row for
    /// CreditLimit/BalanceDueDate context. StatementTotal is a transaction-derived approximation
    /// of the monthly statement total, not Pluggy's own historical invoice figure -- this
    /// codebase has no per-month invoice-history table, only the latest CreditCardDetail
    /// snapshot, so this is the correct and only buildable interpretation given the actual
    /// schema. Like GetCashFlowByAccountAsync, a card with zero matching transactions in the
    /// period simply doesn't appear (INNER JOIN to Transactions).
    /// </summary>
    public async Task<IReadOnlyList<CardStatementRow>> GetCardStatementsByMonthAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        const string sql = """
            SELECT
                a."Id" AS "AccountId",
                a."Name" AS "AccountName",
                EXTRACT(YEAR FROM t."Date")::int AS "Year",
                EXTRACT(MONTH FROM t."Date")::int AS "Month",
                SUM(ABS(t."Amount")) AS "StatementTotal",
                ccd."CreditLimit" AS "CreditLimit",
                ccd."BalanceDueDate" AS "BalanceDueDate"
            FROM "Accounts" a
            INNER JOIN "Transactions" t
                ON t."AccountId" = a."Id"
                AND t."UserId" = @UserId
                AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
                AND t."IsTransfer" = false
                AND t."IsHidden" = false
                AND t."Status" = @PostedStatus
                AND t."DeletedAt" IS NULL
            LEFT JOIN "CreditCardDetails" ccd ON ccd."AccountId" = a."Id"
            WHERE a."UserId" = @UserId
              AND a."Type" = @AcctCreditType
              AND a."IsHidden" = false
              AND a."DeletedAt" IS NULL
            GROUP BY a."Id", a."Name", EXTRACT(YEAR FROM t."Date"), EXTRACT(MONTH FROM t."Date"),
                     ccd."CreditLimit", ccd."BalanceDueDate"
            ORDER BY a."Name", "Year", "Month";
            """;

        var parameters = new
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PostedStatus = (int)TransactionStatus.Posted,
            AcctCreditType = (int)AccountType.Credit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<CardStatementRow>(command);
        return rows.ToList();
    }

    /// <summary>
    /// 19-03 (AI-FCT-03): the top-N merchants by spend for the prior-month summary fact package. Groups the
    /// user's non-transfer, non-hidden, POSTED, Debit transactions within [periodStart, periodEnd] by
    /// "MerchantName", summing SUM(ABS("Amount")) desc and returning the top <paramref name="limit"/>. Rows
    /// with a null/empty MerchantName are excluded (they carry no useful merchant label for the summary).
    /// Follows GetCashFlowByAccountAsync's exact discipline: every enum bound via a named parameter from an
    /// (int) cast (never a bare literal), UserId-scoped with "DeletedAt" IS NULL on the transaction table,
    /// transfers/hidden excluded, spend distinguished by Transaction.Type = Debit (never the sign of Amount).
    ///
    /// Merchant PII redaction is deliberately NOT done in SQL — the raw MerchantName is returned here and the
    /// fact-builder layer (MonthlySummaryFactBuilder) passes every merchant string through
    /// <c>IPiiRedactor.Redact</c> before it can leave the VPS (RESEARCH Pitfall 6 / T-19-07).
    /// </summary>
    public async Task<IReadOnlyList<TopMerchantRow>> GetTopMerchantsAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT
                t."MerchantName" AS "MerchantName",
                SUM(ABS(t."Amount")) AS "TotalSpent",
                COUNT(*)::int AS "TxnCount"
            FROM "Transactions" t
            WHERE t."UserId" = @UserId
              AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
              AND t."IsTransfer" = false
              AND t."IsHidden" = false
              AND t."Type" = @DebitType
              AND t."Status" = @PostedStatus
              AND t."DeletedAt" IS NULL
              AND t."MerchantName" IS NOT NULL
              AND t."MerchantName" <> ''
            GROUP BY t."MerchantName"
            ORDER BY SUM(ABS(t."Amount")) DESC
            LIMIT @Limit;
            """;

        var parameters = new
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DebitType = (int)TransactionType.Debit,
            PostedStatus = (int)TransactionStatus.Posted,
            Limit = limit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<TopMerchantRow>(command);
        return rows.ToList();
    }

    /// <summary>Groups non-transfer, non-hidden Debit expenses by day of week (0 = Sunday .. 6 = Saturday)
    /// in America/Sao_Paulo timezone, optionally excluding multi-installment charges to focus on variable
    /// day-to-day spending habits.</summary>
    public async Task<IReadOnlyList<WeekdaySpendRow>> GetSpendingByWeekdayAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, bool excludeInstallments, CancellationToken ct)
    {
        var sql = excludeInstallments
            ? """
              SELECT
                  EXTRACT(DOW FROM (t."Date" AT TIME ZONE 'America/Sao_Paulo'))::int AS "DayOfWeek",
                  SUM(ABS(t."Amount")) AS "TotalAmount",
                  COUNT(*)::int AS "TransactionCount"
              FROM "Transactions" t
              WHERE t."UserId" = @UserId
                AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
                AND t."Type" = @DebitType
                AND t."IsTransfer" = false
                AND t."IsHidden" = false
                AND t."DeletedAt" IS NULL
                AND (t."TotalInstallments" IS NULL OR t."TotalInstallments" <= 1)
              GROUP BY 1
              ORDER BY 1;
              """
            : """
              SELECT
                  EXTRACT(DOW FROM (t."Date" AT TIME ZONE 'America/Sao_Paulo'))::int AS "DayOfWeek",
                  SUM(ABS(t."Amount")) AS "TotalAmount",
                  COUNT(*)::int AS "TransactionCount"
              FROM "Transactions" t
              WHERE t."UserId" = @UserId
                AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
                AND t."Type" = @DebitType
                AND t."IsTransfer" = false
                AND t."IsHidden" = false
                AND t."DeletedAt" IS NULL
              GROUP BY 1
              ORDER BY 1;
              """;

        var parameters = new
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DebitType = (int)TransactionType.Debit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<WeekdaySpendRow>(command);
        return rows.ToList();
    }
}
