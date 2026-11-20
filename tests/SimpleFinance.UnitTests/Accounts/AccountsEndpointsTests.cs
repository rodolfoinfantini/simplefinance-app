using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Accounts;

/// <summary>
/// ACCT-01/02/03: GET /api/accounts returns every bank/credit account (joined with
/// CreditCardDetail and Connection.InstitutionName) for the authenticated caller; PATCH
/// /api/accounts/{id}/hide toggles Account.IsHidden. Pure EF reads/mutation -- unlike
/// ConnectionsEndpointsTests, this plan's endpoints never call IFinancialDataProvider, so no fake
/// provider swap is required in the test factory.
/// </summary>
public class AccountsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetAccounts_NoAuth_Returns401()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAccounts_ReturnsBankAndCreditAccounts_WithJoinedInstitutionAndCreditCard()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var bankConnection = await SeedConnectionAsync(factory, userId, "Bank Co", "item-bank");
        var bankAccount = await SeedAccountAsync(
            factory, userId, bankConnection.Id, AccountType.Bank, AccountSubtype.CheckingAccount, "Checking", balance: 1000m);

        var creditConnection = await SeedConnectionAsync(factory, userId, "Card Co", "item-credit");
        var (creditAccount, creditCardDetail) = await SeedCreditAccountAsync(
            factory, userId, creditConnection.Id, "Platinum Card", balance: -250m);

        var response = await client.SendAsync(AuthedGet("/api/accounts", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, rows.GetArrayLength());

        var bankRow = rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == bankAccount.Id.ToString());
        Assert.Equal(bankConnection.Id.ToString(), bankRow.GetProperty("connectionId").GetString());
        Assert.Equal("Bank Co", bankRow.GetProperty("institutionName").GetString());
        Assert.Equal("Bank", bankRow.GetProperty("type").GetString());
        Assert.Equal("CheckingAccount", bankRow.GetProperty("subtype").GetString());
        Assert.Equal("Checking", bankRow.GetProperty("name").GetString());
        Assert.Equal(1000m, bankRow.GetProperty("balance").GetDecimal());
        Assert.Equal("BRL", bankRow.GetProperty("currencyCode").GetString());
        Assert.False(bankRow.GetProperty("isHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bankRow.GetProperty("creditCard").ValueKind);

        var creditRow = rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == creditAccount.Id.ToString());
        Assert.Equal(creditConnection.Id.ToString(), creditRow.GetProperty("connectionId").GetString());
        Assert.Equal("Card Co", creditRow.GetProperty("institutionName").GetString());
        Assert.Equal("Credit", creditRow.GetProperty("type").GetString());
        Assert.Equal("CreditCard", creditRow.GetProperty("subtype").GetString());
        Assert.Equal("Platinum Card", creditRow.GetProperty("name").GetString());
        Assert.Equal(-250m, creditRow.GetProperty("balance").GetDecimal());

        var creditCard = creditRow.GetProperty("creditCard");
        Assert.Equal(JsonValueKind.Object, creditCard.ValueKind);
        Assert.Equal(creditCardDetail.Brand, creditCard.GetProperty("brand").GetString());
        Assert.Equal(creditCardDetail.Level, creditCard.GetProperty("level").GetString());
        Assert.Equal(creditCardDetail.CreditLimit, creditCard.GetProperty("creditLimit").GetDecimal());
        Assert.Equal(creditCardDetail.AvailableCredit, creditCard.GetProperty("availableCredit").GetDecimal());
        Assert.Equal(creditCardDetail.LastStatementBalance, creditCard.GetProperty("lastStatementBalance").GetDecimal());
        Assert.Equal("Active", creditCard.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ToggleHide_SetTrue_ReturnsUpdatedResource_PersistsIsHidden()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, userId, "Bank Co", "item-hide");
        var account = await SeedAccountAsync(
            factory, userId, connection.Id, AccountType.Bank, AccountSubtype.CheckingAccount, "Checking", balance: 500m);

        var response = await client.SendAsync(
            AuthedPatch($"/api/accounts/{account.Id}/hide", accessToken, new { isHidden = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(account.Id.ToString(), body.GetProperty("id").GetString());
        Assert.True(body.GetProperty("isHidden").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var updated = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        Assert.True(updated.IsHidden);
    }

    [Fact]
    public async Task ToggleHide_SetFalse_UnhidesAccount()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, userId, "Bank Co", "item-unhide");
        var account = await SeedAccountAsync(
            factory, userId, connection.Id, AccountType.Bank, AccountSubtype.CheckingAccount, "Checking", balance: 500m, isHidden: true);

        var response = await client.SendAsync(
            AuthedPatch($"/api/accounts/{account.Id}/hide", accessToken, new { isHidden = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isHidden").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var updated = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        Assert.False(updated.IsHidden);
    }

    [Fact]
    public async Task ToggleHide_MissingAccount_Returns404()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPatch($"/api/accounts/{Guid.NewGuid()}/hide", accessToken, new { isHidden = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ToggleHide_MissingIsHiddenField_Returns400()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, userId, "Bank Co", "item-badbody");
        var account = await SeedAccountAsync(
            factory, userId, connection.Id, AccountType.Bank, AccountSubtype.CheckingAccount, "Checking", balance: 500m);

        var response = await client.SendAsync(
            AuthedPatch($"/api/accounts/{account.Id}/hide", accessToken, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var untouched = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        Assert.False(untouched.IsHidden);
    }

    [Fact]
    public async Task GetAccounts_CreditCardWithNullLastStatementBalance_FallsBackToAccountBalance()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, userId, "Mercado Pago", "item-mp");
        var (account, detail) = await SeedCreditAccountAsync(factory, userId, connection.Id, "Mercado Pago", 12373.44m);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var tracked = await db.CreditCardDetails.SingleAsync(c => c.Id == detail.Id);
            tracked.LastStatementBalance = null;
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(AuthedGet("/api/accounts", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var card = body.EnumerateArray().Single(a => a.GetProperty("id").GetString() == account.Id.ToString());
        Assert.Equal(12373.44m, card.GetProperty("creditCard").GetProperty("lastStatementBalance").GetDecimal());
    }

    private static async Task<(string AccessToken, Guid UserId)> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var userId = Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!);
        return (accessToken, userId);
    }

    private static HttpRequestMessage AuthedGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedPatch(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<Connection> SeedConnectionAsync(
        AccountsApiFactory factory, Guid userId, string institutionName, string pluggyItemId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluggyItemId = pluggyItemId,
            InstitutionName = institutionName,
            Status = ConnectionStatus.Updated,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<Account> SeedAccountAsync(
        AccountsApiFactory factory,
        Guid userId,
        Guid connectionId,
        AccountType type,
        AccountSubtype subtype,
        string name,
        decimal balance,
        bool isHidden = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = connectionId,
            PluggyAccountId = $"pluggy-account-{Guid.NewGuid():N}",
            Type = type,
            Subtype = subtype,
            Name = name,
            Balance = balance,
            CurrencyCode = "BRL",
            IsHidden = isHidden,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<(Account Account, CreditCardDetail CreditCardDetail)> SeedCreditAccountAsync(
        AccountsApiFactory factory, Guid userId, Guid connectionId, string name, decimal balance)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = connectionId,
            PluggyAccountId = $"pluggy-account-{Guid.NewGuid():N}",
            Type = AccountType.Credit,
            Subtype = AccountSubtype.CreditCard,
            Name = name,
            Balance = balance,
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);

        var creditCardDetail = new CreditCardDetail
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Brand = "Visa",
            Level = "Platinum",
            CreditLimit = 5000m,
            AvailableCredit = 4750m,
            LastStatementBalance = 250m,
            Status = CreditCardStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CreditCardDetails.Add(creditCardDetail);

        await db.SaveChangesAsync();
        return (account, creditCardDetail);
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// AuthApiFactory's minimal pattern. No IFinancialDataProvider swap is needed -- unlike
/// ConnectionsApiFactory, AccountsEndpoints never calls the provider (pure reads + an EF mutation).
/// </summary>
internal sealed class AccountsApiFactory : WebApplicationFactory<Program>
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
