using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Export;

/// <summary>
/// RPT-02: GET /api/export/* HTTP-layer tests -- auth/content-type/filename/round-trip concerns.
/// Mirrors ReportsEndpointsTests.cs's pattern: report exports always reach
/// DashboardQueries/ReportQueries (Dapper, real-Postgres-only) even on empty-state 200 paths, so
/// this factory points ConnectionStrings:Default at the same real local Postgres
/// (127.0.0.1:5433) used by ReportsEndpointsTests.cs. Requires `docker compose up postgres -d`
/// running locally first.
/// </summary>
public class ExportEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Theory]
    [InlineData("/api/export/transactions?format=csv")]
    [InlineData("/api/export/reports/spending-by-category?format=csv")]
    public async Task GetExportRoute_NoAuthToken_Returns401(string path)
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExportTransactions_InvalidFormat_Returns400()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/export/transactions?format=pdf", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExportReport_UnknownReportType_Returns400()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/export/reports/not-a-real-report?format=csv", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExportTransactions_Csv_ReturnsCsvContentTypeAndFixedFilename()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/export/transactions?format=csv", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        Assert.StartsWith("transactions-", fileName?.Trim('"'));
        Assert.EndsWith(".csv", fileName?.Trim('"'));

        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("Date,Description,Merchant,Account,Category,Type,Amount,Status,Transfer,Hidden", body);
    }

    [Fact]
    public async Task ExportTransactions_Xlsx_RoundTripsHeaderAndRowCount_ExcludingHidden()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);
        await SeedTransactionsAsync(factory, accessToken);

        var response = await client.SendAsync(AuthedGet("/api/export/transactions?format=xlsx", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Export");

        Assert.Equal("Date", worksheet.Cell(1, 1).GetString());
        Assert.Equal("Description", worksheet.Cell(1, 2).GetString());

        // Seeded 2 visible transactions + 1 hidden (excluded) + 1 transfer (excluded by default).
        var lastRow = worksheet.LastRowUsed()!.RowNumber();
        Assert.Equal(3, lastRow); // header + 2 data rows
    }

    [Fact]
    public async Task ExportTransactions_Csv_AppliesAllQueryFilters_IncludingParentCategoryAndEndDate()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);
        var userId = DecodeUserIdFromToken(accessToken);

        Guid accountAId;
        Guid parentCategoryId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var now = DateTime.UtcNow;

            var accountA = new Account
            {
                Id = Guid.NewGuid(), UserId = userId, ConnectionId = Guid.NewGuid(),
                PluggyAccountId = "acct-a", Type = AccountType.Bank, Subtype = AccountSubtype.CheckingAccount,
                Name = "Bank A", Balance = 1000m, CurrencyCode = "BRL", CreatedAt = now, UpdatedAt = now,
            };
            var accountB = new Account
            {
                Id = Guid.NewGuid(), UserId = userId, ConnectionId = Guid.NewGuid(),
                PluggyAccountId = "acct-b", Type = AccountType.Bank, Subtype = AccountSubtype.CheckingAccount,
                Name = "Bank B", Balance = 500m, CurrencyCode = "BRL", CreatedAt = now, UpdatedAt = now,
            };
            accountAId = accountA.Id;

            var parentCat = new Category
            {
                Id = Guid.NewGuid(), UserId = userId, Name = "Transporte", Type = CategoryType.Expense,
                CreatedAt = now, UpdatedAt = now,
            };
            var childCat = new Category
            {
                Id = Guid.NewGuid(), UserId = userId, Name = "Uber", ParentCategoryId = parentCat.Id,
                Type = CategoryType.Expense, CreatedAt = now, UpdatedAt = now,
            };
            var otherCat = new Category
            {
                Id = Guid.NewGuid(), UserId = userId, Name = "Alimentação", Type = CategoryType.Expense,
                CreatedAt = now, UpdatedAt = now,
            };
            parentCategoryId = parentCat.Id;

            db.Accounts.AddRange(accountA, accountB);
            db.Categories.AddRange(parentCat, childCat, otherCat);

            var matching = NewTransaction(userId, accountA.Id, new DateTime(2026, 11, 10, 18, 30, 0, DateTimeKind.Utc), 45m, TransactionType.Debit, "Uber Trip");
            matching.CategoryId = childCat.Id;

            var wrongAccount = NewTransaction(userId, accountB.Id, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), 45m, TransactionType.Debit, "Uber Trip");
            wrongAccount.CategoryId = childCat.Id;

            var wrongCategory = NewTransaction(userId, accountA.Id, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), 45m, TransactionType.Debit, "Uber Eats");
            wrongCategory.CategoryId = otherCat.Id;

            var outsideDate = NewTransaction(userId, accountA.Id, new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), 45m, TransactionType.Debit, "Uber Trip");
            outsideDate.CategoryId = childCat.Id;

            db.Transactions.AddRange(matching, wrongAccount, wrongCategory, outsideDate);
            await db.SaveChangesAsync();
        }

        var url = $"/api/export/transactions?format=csv&dateFrom=2026-11-01&dateTo=2026-11-10&accountId={accountAId}&categoryId={parentCategoryId}&type=expense&status=posted&amountMin=40&amountMax=50&search=uber&excludeTransfers=true";
        var response = await client.SendAsync(AuthedGet(url, accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + 1 matching row
        Assert.Contains("Uber Trip", lines[1]);
        Assert.DoesNotContain("Uber Eats", body);
    }

    [Theory]
    [InlineData("spending-by-category")]
    [InlineData("income-expense")]
    [InlineData("net-worth-evolution")]
    [InlineData("cash-flow-by-account")]
    [InlineData("card-statements")]
    [InlineData("recurring")]
    [InlineData("budget-realized-vs-planned")]
    public async Task ExportReport_AllSevenTypes_Xlsx_ReturnsNonEmptyWorkbook(string reportType)
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet($"/api/export/reports/{reportType}?format=xlsx", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Export");
        Assert.False(string.IsNullOrEmpty(worksheet.Cell(1, 1).GetString()));
    }

    [Fact]
    public async Task ExportReport_SpendingByCategory_Csv_ReturnsCsvContentType()
    {
        using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/export/reports/spending-by-category?format=csv", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task SeedTransactionsAsync(ExportApiFactory factory, string accessToken)
    {
        var userId = DecodeUserIdFromToken(accessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = Guid.NewGuid(),
            PluggyAccountId = $"acct-{Guid.NewGuid():N}",
            Type = AccountType.Bank,
            Subtype = AccountSubtype.CheckingAccount,
            Name = "Checking",
            Balance = 1000m,
            CurrencyCode = "BRL",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);

        var now = DateTime.UtcNow;
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, now, 100m, TransactionType.Debit, "Groceries"),
            NewTransaction(userId, account.Id, now, 5000m, TransactionType.Credit, "Salary"),
            NewTransaction(userId, account.Id, now, 50m, TransactionType.Debit, "Hidden Purchase", isHidden: true),
            NewTransaction(userId, account.Id, now, 999m, TransactionType.Credit, "Transfer In", isTransfer: true));

        await db.SaveChangesAsync();
    }

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type, string description,
        bool isHidden = false, bool isTransfer = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = description,
        Amount = amount,
        Type = type,
        CurrencyCode = "BRL",
        Status = TransactionStatus.Posted,
        CategorySource = CategorySource.SystemMapping,
        IsHidden = isHidden,
        IsTransfer = isTransfer,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // MapInboundClaims=false (Program.cs) means the JWT's own "sub" claim is readable as-is --
    // decoding it directly here avoids needing a dedicated GET /api/auth/me route just for tests.
    private static Guid DecodeUserIdFromToken(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(accessToken);
        var sub = token.Claims.First(c => c.Type == "sub").Value;
        return Guid.Parse(sub);
    }

    private static async Task<string> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage AuthedGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database for
/// auth/user/transaction data (mirrors ReportsApiFactory's pattern), but also sets
/// ConnectionStrings:Default to a real local Postgres so Reporting's IDbConnectionFactory
/// (real-Postgres-only) can serve DashboardQueries/ReportQueries for report-export 200 paths.
/// </summary>
internal sealed class ExportApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    private static string GetReportingConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long-for-hmac256",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["Cors:AllowedOrigin"] = "http://localhost:5173",
                ["Totp:EncryptionKey"] = "IZS7xtbC5Uj4lmJzMPDBA7PWgzb5irgqIKSB9jnZnns=",
                ["ConnectionStrings:Default"] = GetReportingConnectionString(),
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(SimpleFinanceDbContext)
                    || d.ServiceType == typeof(DbContextOptions<SimpleFinanceDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(SimpleFinanceDbContext))))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SimpleFinanceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }
}
