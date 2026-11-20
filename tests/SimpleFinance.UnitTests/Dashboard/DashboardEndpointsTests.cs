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

namespace SimpleFinance.UnitTests.Dashboard;

/// <summary>
/// DASH-01..05: GET /api/dashboard/* HTTP-layer tests -- covers ONLY auth/validation concerns
/// that don't need real spend/aggregate data (DashboardService's own aggregation/ranking/
/// gap-fill/join logic is covered end-to-end against real Postgres by DashboardServiceTests).
/// Mirrors BudgetsEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class DashboardEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Theory]
    [InlineData("/api/dashboard/summary")]
    [InlineData("/api/dashboard/spending-by-category")]
    [InlineData("/api/dashboard/spending-by-parent-category")]
    [InlineData("/api/dashboard/income-expense")]
    [InlineData("/api/dashboard/net-worth-evolution")]
    [InlineData("/api/dashboard/at-risk-budgets")]
    [InlineData("/api/dashboard/upcoming-due-dates")]
    public async Task GetDashboardRoute_NoAuthToken_Returns401(string path)
    {
        using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetNetWorthEvolution_InvalidMonthsValue_Returns400()
    {
        using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/dashboard/net-worth-evolution?months=3", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_MissingDateFrom_Returns400()
    {
        using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet($"/api/dashboard/summary?dateTo={Uri.EscapeDataString("2026-07-31")}", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSpendingByCategory_MissingDateFrom_Returns400()
    {
        using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet($"/api/dashboard/spending-by-category?dateTo={Uri.EscapeDataString("2026-07-31")}", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSpendingByParentCategory_MissingDateFrom_Returns400()
    {
        using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedGet($"/api/dashboard/spending-by-parent-category?dateTo={Uri.EscapeDataString("2026-07-31")}", accessToken));

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
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// BudgetsApiFactory's minimal pattern. Reporting's IDbConnectionFactory (real-Postgres-only) is
/// never exercised by these tests -- every scenario here fails auth/validation before
/// DashboardService reaches DashboardQueries.
/// </summary>
internal sealed class DashboardApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

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
