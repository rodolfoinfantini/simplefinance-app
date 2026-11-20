using Dapper;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Reporting;

public sealed record DashboardTotals(
    decimal NetWorth, decimal TotalBalance, decimal TotalCardDebt,
    decimal PeriodIncome, decimal PeriodExpense);

public sealed record CategorySpend(
    Guid? CategoryId, string? CategoryName, string? CategoryIcon, string? CategoryColor, decimal Amount);

public sealed record MonthlyIncomeExpense(int Year, int Month, decimal Income, decimal Expense);

public sealed record NetWorthPoint(DateTime Date, decimal NetWorth);

public sealed record NetWorthComposition(
    decimal AccountsBalance, decimal InvestmentsBalance, decimal CreditCardDebt, decimal NetWorth);

/// <summary>
/// The single source of truth for "how do we sum money correctly" in Phase 5 — every hand-written
/// SQL aggregate every Dashboard/Budget endpoint reads from. Every income/expense/spend query
/// excludes IsTransfer=true and IsHidden=true transactions (05-CONTEXT.md's locked correctness
/// requirement), and every query is scoped by the caller's own UserId (T-05-05).
///
/// EF Core's global soft-delete query filter does NOT apply to Dapper's raw SQL — every query
/// here explicitly adds "DeletedAt" IS NULL on every soft-deletable table it touches.
///
/// periodStart/periodEnd and rangeStart/rangeEnd are always inclusive on both ends
/// ("Date" >= start AND "Date" <= end), matching 03-03-SUMMARY.md's TransactionsEndpoints filter
/// convention.
///
/// Every enum comparison is bound via a named parameter from the C#'s (int) cast — never a bare
/// numeric literal inlined into the SQL string — so the query stays correct if an enum's declared
/// order ever changes.
///
/// Amount aggregation always uses ABS(Amount) rather than trusting a signed convention, mirroring
/// 03-03-SUMMARY.md's documented "amountMin/amountMax always compare Math.Abs(Amount)... stays
/// agnostic of Pluggy's unverified sign convention" precedent — income/expense are distinguished
/// by Transaction.Type, never by the sign of Amount.
/// </summary>
public sealed class DashboardQueries(IDbConnectionFactory connectionFactory) : ICategorySpendReader, IPeriodTotalsReader
{
    private const string DebitTypeParam = "DebitType";
    private const string TxnCreditTypeParam = "TxnCreditType";
    private const string BankTypeParam = "BankType";
    private const string AcctCreditTypeParam = "AcctCreditType";

    public async Task<DashboardTotals> GetTotalsAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        const string sql = """
            SELECT
                COALESCE((SELECT SUM(a."Balance" + COALESCE(a."AutomaticallyInvestedBalance", 0)) FROM "Accounts" a
                          WHERE a."UserId" = @UserId AND a."Type" = @BankType
                            AND a."IsHidden" = false AND a."DeletedAt" IS NULL), 0)
                + COALESCE((SELECT SUM(COALESCE(i."Balance", CASE WHEN i."Value" IS NOT NULL AND i."Quantity" IS NOT NULL THEN i."Value" * i."Quantity" ELSE i."Value" END, 0)) FROM "Investments" i
                            WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL), 0)
                    AS "NetWorth",

                COALESCE((SELECT SUM(ba."Balance" + COALESCE(ba."AutomaticallyInvestedBalance", 0)) FROM "Accounts" ba
                          WHERE ba."UserId" = @UserId AND ba."Type" = @BankType
                            AND ba."IsHidden" = false AND ba."DeletedAt" IS NULL), 0)
                    AS "TotalBalance",

                COALESCE((SELECT SUM(COALESCE(dccd."LastStatementBalance", da."Balance", 0)) FROM "Accounts" da
                          LEFT JOIN "CreditCardDetails" dccd ON dccd."AccountId" = da."Id"
                          WHERE da."UserId" = @UserId AND da."Type" = @AcctCreditType
                            AND da."IsHidden" = false AND da."DeletedAt" IS NULL), 0)
                    AS "TotalCardDebt",

                COALESCE((SELECT SUM(ABS(ti."Amount")) FROM "Transactions" ti
                          WHERE ti."UserId" = @UserId AND ti."Date" >= @PeriodStart AND ti."Date" <= @PeriodEnd
                            AND ti."Type" = @TxnCreditType AND ti."IsTransfer" = false AND ti."IsHidden" = false
                            AND ti."DeletedAt" IS NULL), 0)
                    AS "PeriodIncome",

                COALESCE((SELECT SUM(ABS(te."Amount")) FROM "Transactions" te
                          WHERE te."UserId" = @UserId AND te."Date" >= @PeriodStart AND te."Date" <= @PeriodEnd
                            AND te."Type" = @DebitType AND te."IsTransfer" = false AND te."IsHidden" = false
                            AND te."DeletedAt" IS NULL), 0)
                    AS "PeriodExpense";
            """;

        var parameters = new
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DebitType = (int)TransactionType.Debit,
            TxnCreditType = (int)TransactionType.Credit,
            BankType = (int)AccountType.Bank,
            AcctCreditType = (int)AccountType.Credit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        return await connection.QueryFirstAsync<DashboardTotals>(command);
    }

    public async Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        const string sql = """
            SELECT
                t."CategoryId" AS "CategoryId",
                c."Name" AS "CategoryName",
                c."Icon" AS "CategoryIcon",
                c."Color" AS "CategoryColor",
                SUM(ABS(t."Amount")) AS "Amount"
            FROM "Transactions" t
            LEFT JOIN "Categories" c ON c."Id" = t."CategoryId"
            WHERE t."UserId" = @UserId
              AND t."Date" >= @PeriodStart AND t."Date" <= @PeriodEnd
              AND t."Type" = @DebitType
              AND t."IsTransfer" = false
              AND t."IsHidden" = false
              AND t."DeletedAt" IS NULL
            GROUP BY t."CategoryId", c."Name", c."Icon", c."Color"
            ORDER BY "Amount" DESC;
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
        var rows = await connection.QueryAsync<CategorySpend>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MonthlyIncomeExpense>> GetIncomeExpenseByMonthAsync(
        Guid userId, DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
    {
        // A month with zero matching transactions is simply absent from the result — gap-filling
        // (e.g. producing a zero row for every month in the requested range) is the caller's
        // responsibility, not this method's.
        const string sql = """
            SELECT
                EXTRACT(YEAR FROM t."Date")::int AS "Year",
                EXTRACT(MONTH FROM t."Date")::int AS "Month",
                COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @TxnCreditType), 0) AS "Income",
                COALESCE(SUM(ABS(t."Amount")) FILTER (WHERE t."Type" = @DebitType), 0) AS "Expense"
            FROM "Transactions" t
            WHERE t."UserId" = @UserId
              AND t."Date" >= @RangeStart AND t."Date" <= @RangeEnd
              AND t."IsTransfer" = false
              AND t."IsHidden" = false
              AND t."DeletedAt" IS NULL
            GROUP BY EXTRACT(YEAR FROM t."Date"), EXTRACT(MONTH FROM t."Date")
            ORDER BY "Year", "Month";
            """;

        var parameters = new
        {
            UserId = userId,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            DebitType = (int)TransactionType.Debit,
            TxnCreditType = (int)TransactionType.Credit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<MonthlyIncomeExpense>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<NetWorthPoint>> GetNetWorthEvolutionAsync(
        Guid userId, DateTime rangeStart, CancellationToken ct)
    {
        // Plain SELECT from the (currently empty) NetWorthSnapshot table — safe to call before
        // Phase 6's INV-04 job ever runs; returns an empty list rather than throwing.
        const string sql = """
            SELECT n."Date" AS "Date", n."NetWorth" AS "NetWorth"
            FROM "NetWorthSnapshots" n
            WHERE n."UserId" = @UserId
              AND n."Date" >= @RangeStart
              AND n."Date" <= @Now
              AND n."DeletedAt" IS NULL
            ORDER BY n."Date" ASC;
            """;

        var parameters = new
        {
            UserId = userId,
            RangeStart = rangeStart,
            Now = DateTime.UtcNow,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<NetWorthPoint>(command);
        return rows.ToList();
    }

    /// <summary>
    /// INV-04: the exact same net-worth figure GetTotalsAsync's "NetWorth" column already computes
    /// (non-hidden Bank accounts' Balance + investments effective Balance; credit card balances are
    /// reported separately in CreditCardDebt and NOT subtracted from NetWorth because many Brazilian
    /// banks report total future installments in Pluggy's credit account balance).
    /// </summary>
    public async Task<NetWorthComposition> GetNetWorthCompositionAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                COALESCE((SELECT SUM(a."Balance" + COALESCE(a."AutomaticallyInvestedBalance", 0)) FROM "Accounts" a
                          WHERE a."UserId" = @UserId AND a."Type" = @BankType
                            AND a."IsHidden" = false AND a."DeletedAt" IS NULL), 0)
                    AS "AccountsBalance",

                COALESCE((SELECT SUM(COALESCE(i."Balance", CASE WHEN i."Value" IS NOT NULL AND i."Quantity" IS NOT NULL THEN i."Value" * i."Quantity" ELSE i."Value" END, 0)) FROM "Investments" i
                          WHERE i."UserId" = @UserId AND i."DeletedAt" IS NULL), 0)
                    AS "InvestmentsBalance",

                COALESCE((SELECT SUM(COALESCE(ccd."LastStatementBalance", ca."Balance", 0)) FROM "Accounts" ca
                          LEFT JOIN "CreditCardDetails" ccd ON ccd."AccountId" = ca."Id"
                          WHERE ca."UserId" = @UserId AND ca."Type" = @AcctCreditType
                            AND ca."IsHidden" = false AND ca."DeletedAt" IS NULL), 0)
                    AS "CreditCardDebt",

                COALESCE((SELECT SUM(a2."Balance" + COALESCE(a2."AutomaticallyInvestedBalance", 0)) FROM "Accounts" a2
                          WHERE a2."UserId" = @UserId AND a2."Type" = @BankType
                            AND a2."IsHidden" = false AND a2."DeletedAt" IS NULL), 0)
                + COALESCE((SELECT SUM(COALESCE(i2."Balance", CASE WHEN i2."Value" IS NOT NULL AND i2."Quantity" IS NOT NULL THEN i2."Value" * i2."Quantity" ELSE i2."Value" END, 0)) FROM "Investments" i2
                            WHERE i2."UserId" = @UserId AND i2."DeletedAt" IS NULL), 0)
                    AS "NetWorth";
            """;

        var parameters = new
        {
            UserId = userId,
            BankType = (int)AccountType.Bank,
            AcctCreditType = (int)AccountType.Credit,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        return await connection.QueryFirstAsync<NetWorthComposition>(command);
    }
}
