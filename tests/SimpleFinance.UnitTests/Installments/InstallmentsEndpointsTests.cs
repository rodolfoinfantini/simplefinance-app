using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Installments;

public class InstallmentsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetInstallments_NoAuth_Returns401()
    {
        using var factory = new InstallmentsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/installments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetInstallments_GroupsMercadoPagoTruncatedFutureInstallments_AndExcludesCompletedPlans()
    {
        using var factory = new InstallmentsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var now = DateTime.UtcNow;

            var connection = new Connection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PluggyItemId = "item-mp",
                InstitutionName = "MeuPluggy",
                CustomName = "Mercado Pago",
                Status = ConnectionStatus.Updated,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Connections.Add(connection);

            var account = new Account
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ConnectionId = connection.Id,
                PluggyAccountId = "acc-mp",
                Type = AccountType.Credit,
                Subtype = AccountSubtype.CreditCard,
                Name = "Mercado Pago",
                Balance = 1094.59m,
                CurrencyCode = "BRL",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Accounts.Add(account);

            db.CreditCardDetails.Add(new CreditCardDetail
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                BalanceCloseDate = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
                BalanceDueDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
                Status = CreditCardStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });

            // BRUDOVAN PNEUS 10x: 1..3 Posted (paid), 4..10 Pending (unpaid, 5..10 truncated to "BRUDOVAN PNEUS LT")
            var brudovanDates = new[]
            {
                new DateTime(2026, 7, 30, 20, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 12, 10, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 1, 10, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 2, 10, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 3, 10, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 4, 10, 3, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 5, 10, 3, 0, 0, DateTimeKind.Utc),
            };

            for (var i = 0; i < 10; i++)
            {
                var num = i + 1;
                var isPosted = num <= 3;
                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    AccountId = account.Id,
                    PluggyTransactionId = $"brudovan-{num}",
                    Date = brudovanDates[i],
                    Description = num <= 4 ? "BRUDOVAN PNEUS LTDA" : "BRUDOVAN PNEUS LT",
                    Amount = num == 1 ? 156.43m : 156.37m,
                    CurrencyCode = "BRL",
                    Status = isPosted ? TransactionStatus.Posted : TransactionStatus.Pending,
                    BillId = isPosted ? $"bill-{num}" : null,
                    Type = TransactionType.Debit,
                    InstallmentNumber = num,
                    TotalInstallments = 10,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // Already-paid 2x purchase (1/2 and 2/2 both Posted) -> must NOT appear in active unpaid list
            for (var num = 1; num <= 2; num++)
            {
                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    AccountId = account.Id,
                    PluggyTransactionId = $"paid-{num}",
                    Date = new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc).AddMonths(num - 1),
                    Description = "AMAZON BR",
                    Amount = 94.50m,
                    CurrencyCode = "BRL",
                    Status = TransactionStatus.Posted,
                    BillId = $"bill-paid-{num}",
                    Type = TransactionType.Debit,
                    InstallmentNumber = num,
                    TotalInstallments = 2,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/installments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("activeCount").GetInt32());
        Assert.Equal(1094.59m, body.GetProperty("totalRemainingAmount").GetDecimal());
        Assert.Equal(156.37m, body.GetProperty("monthlyTotal").GetDecimal());

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("BRUDOVAN PNEUS LTDA", item.GetProperty("description").GetString());
        Assert.Equal("Mercado Pago", item.GetProperty("institutionName").GetString());
        Assert.Equal(4, item.GetProperty("currentInstallment").GetInt32());
        Assert.Equal(10, item.GetProperty("totalInstallments").GetInt32());
        Assert.Equal(7, item.GetProperty("remainingInstallments").GetInt32());
        Assert.Equal(156.37m, item.GetProperty("installmentAmount").GetDecimal());
        Assert.Equal(1094.59m, item.GetProperty("remainingAmount").GetDecimal());
        Assert.Equal(new DateTime(2026, 7, 30, 20, 0, 0, DateTimeKind.Utc), item.GetProperty("purchaseDate").GetDateTime());
        Assert.Equal(new DateTime(2027, 5, 10, 3, 0, 0, DateTimeKind.Utc), item.GetProperty("lastInstallmentDate").GetDateTime());
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
}

internal sealed class InstallmentsApiFactory : WebApplicationFactory<Program>
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
