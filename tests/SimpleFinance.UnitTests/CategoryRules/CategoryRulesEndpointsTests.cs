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

namespace SimpleFinance.UnitTests.CategoryRules;

/// <summary>
/// CAT-03/CAT-04: full CategoryRule CRUD (create/update/reorder/toggle-active/delete), the
/// test-rule preview endpoint, and apply-now's retroactive bulk recategorization -- including the
/// load-bearing regression test proving apply-now never touches a CategorySource.UserOverride
/// row. Mirrors CategoriesEndpointsTests' minimal WebApplicationFactory pattern.
/// </summary>
public class CategoryRulesEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task CreateRule_ValidDescriptionContainsRule_Returns201()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules", accessToken,
            new { matchField = "Description", matchOperator = "Contains", matchValue = "Uber", categoryId = category.Id, isActive = true }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Description", body.GetProperty("matchField").GetString());
        Assert.Equal("Contains", body.GetProperty("matchOperator").GetString());
        Assert.Equal(category.Id, body.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task CreateRule_RangeWithOnlyAmountMin_Returns400()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules", accessToken,
            new { matchField = "Amount", matchOperator = "Range", amountMin = 10m, categoryId = category.Id, isActive = true }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRule_AmountFieldWithContainsOperator_Returns400()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules", accessToken,
            new { matchField = "Amount", matchOperator = "Contains", matchValue = "10", categoryId = category.Id, isActive = true }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRule_NonExistentCategoryId_Returns400()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules", accessToken,
            new { matchField = "Description", matchOperator = "Contains", matchValue = "Uber", categoryId = Guid.NewGuid(), isActive = true }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_MissingOneExistingId_Returns400_ValidReorderUpdatesPriorityToIndex()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");

        var ruleA = await SeedCategoryRuleAsync(factory, userId, category.Id, "A", priority: 0);
        var ruleB = await SeedCategoryRuleAsync(factory, userId, category.Id, "B", priority: 1);
        var ruleC = await SeedCategoryRuleAsync(factory, userId, category.Id, "C", priority: 2);

        var incompleteResponse = await client.SendAsync(AuthedPut(
            "/api/category-rules/reorder", accessToken, new { orderedIds = new[] { ruleA.Id, ruleB.Id } }));
        Assert.Equal(HttpStatusCode.BadRequest, incompleteResponse.StatusCode);

        var validResponse = await client.SendAsync(AuthedPut(
            "/api/category-rules/reorder", accessToken, new { orderedIds = new[] { ruleC.Id, ruleA.Id, ruleB.Id } }));
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.Equal(0, (await db.CategoryRules.FirstAsync(r => r.Id == ruleC.Id)).Priority);
        Assert.Equal(1, (await db.CategoryRules.FirstAsync(r => r.Id == ruleA.Id)).Priority);
        Assert.Equal(2, (await db.CategoryRules.FirstAsync(r => r.Id == ruleB.Id)).Priority);
    }

    [Fact]
    public async Task ToggleActive_MissingIsActiveField_Returns400()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");
        var rule = await SeedCategoryRuleAsync(factory, userId, category.Id, "Uber", priority: 0);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/category-rules/{rule.Id}/active", accessToken, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRule_RemovesRule_SubsequentGetNoLongerIncludesIt()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries");
        var rule = await SeedCategoryRuleAsync(factory, userId, category.Id, "Uber", priority: 0);

        var deleteResponse = await client.SendAsync(AuthedDelete($"/api/category-rules/{rule.Id}", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/category-rules");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var listResponse = await client.SendAsync(listRequest);
        var body = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(body.EnumerateArray(), r => r.GetProperty("id").GetGuid() == rule.Id);
    }

    [Fact]
    public async Task TestRule_DraftMatchesThreeTransactions_ReturnsCountAndSample()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Transport");
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");

        await SeedTransactionAsync(factory, userId, account.Id, "Uber ride 1", null, 20m, CategorySource.SystemMapping, null);
        await SeedTransactionAsync(factory, userId, account.Id, "Uber ride 2", null, 25m, CategorySource.SystemMapping, null);
        await SeedTransactionAsync(factory, userId, account.Id, "Uber ride 3", null, 30m, CategorySource.SystemMapping, null);
        await SeedTransactionAsync(factory, userId, account.Id, "Supermarket run", null, 100m, CategorySource.SystemMapping, null);

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules/test", accessToken,
            new { matchField = "Description", matchOperator = "Contains", matchValue = "Uber" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("count").GetInt32());
        Assert.Equal(3, body.GetProperty("sample").GetArrayLength());
    }

    [Fact]
    public async Task TestRule_DraftMatchesZero_ReturnsCountZeroEmptySample()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        await SeedTransactionAsync(factory, userId, account.Id, "Supermarket run", null, 100m, CategorySource.SystemMapping, null);

        var response = await client.SendAsync(AuthedPost(
            "/api/category-rules/test", accessToken,
            new { matchField = "Description", matchOperator = "Contains", matchValue = "NoSuchMerchant" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(0, body.GetProperty("sample").GetArrayLength());
    }

    [Fact]
    public async Task ApplyNow_RecategorizesMatches_ExceptUserOverride()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var ruleCategory = await SeedCategoryAsync(factory, userId, "Transport");
        var overrideCategory = await SeedCategoryAsync(factory, userId, "Manually Chosen");
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");

        var overrideTransaction = await SeedTransactionAsync(
            factory, userId, account.Id, "Uber ride override", null, 20m, CategorySource.UserOverride, overrideCategory.Id);
        var normalTransaction = await SeedTransactionAsync(
            factory, userId, account.Id, "Uber ride normal", null, 25m, CategorySource.SystemMapping, null);

        var rule = await SeedCategoryRuleAsync(factory, userId, ruleCategory.Id, "Uber", priority: 0);

        var response = await client.SendAsync(AuthedPost($"/api/category-rules/{rule.Id}/apply-now", accessToken, new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("updatedCount").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var unchanged = await db.Transactions.FirstAsync(t => t.Id == overrideTransaction.Id);
        Assert.Equal(overrideCategory.Id, unchanged.CategoryId);
        Assert.Equal(CategorySource.UserOverride, unchanged.CategorySource);

        var changed = await db.Transactions.FirstAsync(t => t.Id == normalTransaction.Id);
        Assert.Equal(ruleCategory.Id, changed.CategoryId);
        Assert.Equal(CategorySource.Rule, changed.CategorySource);
    }

    [Fact]
    public async Task ApplyNow_MatchesZeroTransactions_ReturnsUpdatedCountZero()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Transport");
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        await SeedTransactionAsync(factory, userId, account.Id, "Supermarket run", null, 100m, CategorySource.SystemMapping, null);

        var rule = await SeedCategoryRuleAsync(factory, userId, category.Id, "NoSuchMerchant", priority: 0);

        var response = await client.SendAsync(AuthedPost($"/api/category-rules/{rule.Id}/apply-now", accessToken, new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("updatedCount").GetInt32());
    }

    [Fact]
    public async Task ApplyNow_WorksEvenWhenRuleIsInactive()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Transport");
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(
            factory, userId, account.Id, "Uber ride", null, 20m, CategorySource.SystemMapping, null);

        var rule = await SeedCategoryRuleAsync(factory, userId, category.Id, "Uber", priority: 0, isActive: false);

        var response = await client.SendAsync(AuthedPost($"/api/category-rules/{rule.Id}/apply-now", accessToken, new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("updatedCount").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var updated = await db.Transactions.FirstAsync(t => t.Id == transaction.Id);
        Assert.Equal(category.Id, updated.CategoryId);
        Assert.Equal(CategorySource.Rule, updated.CategorySource);
    }

    [Fact]
    public async Task CreateAndApplyNow_RuleWithMarkAsTransfer_FlagsMatchesAndRespectsManualTransferOverride()
    {
        using var factory = new CategoryRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Transferências");
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Mercado Pago");

        var cofrinho = await SeedTransactionAsync(
            factory, userId, account.Id, "Dinheiro retirado Fatura", null, 500m, CategorySource.SystemMapping, null);
        var manualNonTransfer = await SeedTransactionAsync(
            factory, userId, account.Id, "Dinheiro retirado Manual", null, 500m, CategorySource.SystemMapping, null);
        using (var overrideScope = factory.Services.CreateScope())
        {
            var overrideDb = overrideScope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var row = await overrideDb.Transactions.FirstAsync(t => t.Id == manualNonTransfer.Id);
            row.IsTransfer = false;
            row.IsTransferManualOverride = true;
            await overrideDb.SaveChangesAsync();
        }

        var createResponse = await client.SendAsync(AuthedPost(
            "/api/category-rules", accessToken,
            new { matchField = "Description", matchOperator = "Contains", matchValue = "Dinheiro retirado", categoryId = category.Id, isActive = true, markAsTransfer = true }));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("markAsTransfer").GetBoolean());
        var ruleId = created.GetProperty("id").GetGuid();

        var applyResponse = await client.SendAsync(AuthedPost($"/api/category-rules/{ruleId}/apply-now", accessToken, new { }));
        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var updatedCofrinho = await db.Transactions.FirstAsync(t => t.Id == cofrinho.Id);
        var updatedManual = await db.Transactions.FirstAsync(t => t.Id == manualNonTransfer.Id);

        Assert.Equal(category.Id, updatedCofrinho.CategoryId);
        Assert.True(updatedCofrinho.IsTransfer);
        Assert.Equal(category.Id, updatedManual.CategoryId);
        Assert.False(updatedManual.IsTransfer);
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

    private static HttpRequestMessage AuthedPost(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedPut(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
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

    private static HttpRequestMessage AuthedDelete(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<Category> SeedCategoryAsync(CategoryRulesApiFactory factory, Guid userId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Type = CategoryType.Expense,
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<CategoryRule> SeedCategoryRuleAsync(
        CategoryRulesApiFactory factory, Guid userId, Guid categoryId, string matchValue, int priority, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var rule = new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MatchField = RuleMatchField.Description,
            MatchOperator = RuleMatchOperator.Contains,
            MatchValue = matchValue,
            CategoryId = categoryId,
            Priority = priority,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CategoryRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    private static async Task<(Connection Connection, Account Account)> SeedConnectionAndAccountAsync(
        CategoryRulesApiFactory factory, Guid userId, string institutionName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluggyItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = institutionName,
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
            PluggyAccountId = $"pluggy-account-{Guid.NewGuid():N}",
            Type = AccountType.Bank,
            Subtype = AccountSubtype.CheckingAccount,
            Name = institutionName,
            Balance = 0m,
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);

        await db.SaveChangesAsync();
        return (connection, account);
    }

    private static async Task<Transaction> SeedTransactionAsync(
        CategoryRulesApiFactory factory,
        Guid userId,
        Guid accountId,
        string description,
        string? merchantName,
        decimal amount,
        CategorySource categorySource,
        Guid? categoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            PluggyTransactionId = $"pluggy-txn-{Guid.NewGuid():N}",
            Date = now,
            Description = description,
            MerchantName = merchantName,
            Amount = amount,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategoryId = categoryId,
            CategorySource = categorySource,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// CategoriesApiFactory/TransactionsApiFactory's minimal pattern.
/// </summary>
internal sealed class CategoryRulesApiFactory : WebApplicationFactory<Program>
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
