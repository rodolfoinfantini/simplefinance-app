using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Reports;

/// <summary>
/// RPT-01: GET /api/reports/* HTTP-layer tests -- auth/response-shape/validation concerns.
/// Mirrors NetWorthEndpointsTests.cs's pattern: ReportsService always reaches
/// DashboardQueries/ReportQueries (Dapper, real-Postgres-only) even on empty-state 200 paths, so
/// this factory points ConnectionStrings:Default at the same real local Postgres
/// (127.0.0.1:5433) used by ReportQueriesTests.cs/ReportsServiceTests.cs. Requires
/// `docker compose up postgres -d` running locally first.
/// </summary>
public class ReportsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Theory]
    [InlineData("/api/reports/spending-by-category?dateFrom=2026-06-01&dateTo=2026-06-30")]
    [InlineData("/api/reports/income-expense?dateFrom=2026-06-01&dateTo=2026-06-30")]
    [InlineData("/api/reports/net-worth-evolution")]
    [InlineData("/api/reports/cash-flow-by-account?dateFrom=2026-06-01&dateTo=2026-06-30")]
    [InlineData("/api/reports/card-statements?dateFrom=2026-06-01&dateTo=2026-06-30")]
    [InlineData("/api/reports/recurring")]
    [InlineData("/api/reports/budget-realized-vs-planned?year=2026&month=6")]
    [InlineData("/api/reports/spending-by-weekday?dateFrom=2026-06-01&dateTo=2026-06-30")]
    public async Task GetReportRoute_NoAuthToken_Returns401(string path)
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSpendingByCategory_ValidToken_Returns200()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/spending-by-category?dateFrom=2026-06-01&dateTo=2026-06-30", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetSpendingByCategory_MissingDateFrom_Returns400()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/spending-by-category?dateTo=2026-06-30", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIncomeExpense_MissingDateTo_Returns400()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/income-expense?dateFrom=2026-06-01", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public async Task GetNetWorthEvolution_AllowedDaysValue_Returns200(int days)
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet($"/api/reports/net-worth-evolution?days={days}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(999999)]
    public async Task GetNetWorthEvolution_DisallowedDaysValue_Returns400(int days)
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet($"/api/reports/net-worth-evolution?days={days}", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCashFlowByAccount_MissingDates_Returns400()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/reports/cash-flow-by-account", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCardStatements_ValidToken_Returns200()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/card-statements?dateFrom=2026-06-01&dateTo=2026-06-30", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRecurring_ValidToken_Returns200EmptyShape()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/reports/recurring", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("monthlyCommittedCost").GetDecimal());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetBudgetRealizedVsPlanned_MissingYearOrMonth_Returns400()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/reports/budget-realized-vs-planned?year=2026", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetBudgetRealizedVsPlanned_ValidToken_Returns200EmptyArray()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/budget-realized-vs-planned?year=2026&month=6", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task GetSpendingByWeekday_MissingDateFrom_Returns400()
    {
        using var factory = new ReportsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet("/api/reports/spending-by-weekday?dateTo=2026-06-30", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
/// auth/user data (mirrors NetWorthApiFactory's pattern), but also sets ConnectionStrings:Default
/// to a real local Postgres so Reporting's IDbConnectionFactory (real-Postgres-only) can serve
/// DashboardQueries/ReportQueries for the 200-path tests above.
/// </summary>
internal sealed class ReportsApiFactory : WebApplicationFactory<Program>
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
