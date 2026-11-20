using System.Linq;
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

namespace SimpleFinance.UnitTests.Transactions;

/// <summary>
/// TXN-01/02: GET /api/transactions (7 filter dimensions + offset pagination), GET
/// /api/transactions/{id} detail, and GET /api/categories. Pure EF reads -- no
/// IFinancialDataProvider swap needed, mirroring AccountsEndpointsTests' minimal factory pattern.
/// </summary>
public class TransactionsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetTransactions_NoAuth_Returns401()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactions_NoFilters_ReturnsUpTo50_SortedDateDescThenIdDesc_WithEnvelope()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var older = await SeedTransactionAsync(factory, userId, account.Id, date: new DateTime(2026, 1, 1), amount: 10m, description: "Older");
        var newer = await SeedTransactionAsync(factory, userId, account.Id, date: new DateTime(2026, 1, 5), amount: 20m, description: "Newer");

        var response = await client.SendAsync(AuthedGet("/api/transactions", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());

        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(newer.Id.ToString(), items[0].GetProperty("id").GetString());
        Assert.Equal(older.Id.ToString(), items[1].GetProperty("id").GetString());
        Assert.Equal(account.Id.ToString(), items[0].GetProperty("accountId").GetString());
        Assert.Equal(account.Name, items[0].GetProperty("accountName").GetString());
    }

    [Fact]
    public async Task GetTransactions_DateRangeFilter_NarrowsToInclusiveWindow()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var jan1 = await SeedTransactionAsync(factory, userId, account.Id, date: new DateTime(2026, 1, 1), amount: 10m, description: "Jan1");
        var jan10 = await SeedTransactionAsync(factory, userId, account.Id, date: new DateTime(2026, 1, 10), amount: 10m, description: "Jan10");
        await SeedTransactionAsync(factory, userId, account.Id, date: new DateTime(2026, 1, 20), amount: 10m, description: "Jan20");

        var response = await client.SendAsync(AuthedGet(
            "/api/transactions?dateFrom=2026-01-01&dateTo=2026-01-10", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains(jan1.Id.ToString(), ids);
        Assert.Contains(jan10.Id.ToString(), ids);
    }

    [Fact]
    public async Task GetTransactions_AccountFilter_ReturnsOnlyThatAccount()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, accountA) = await SeedConnectionAndAccountAsync(factory, userId, "Bank A");
        var (_, accountB) = await SeedConnectionAndAccountAsync(factory, userId, "Bank B");
        var txA = await SeedTransactionAsync(factory, userId, accountA.Id, date: DateTime.UtcNow, amount: 10m, description: "A");
        await SeedTransactionAsync(factory, userId, accountB.Id, date: DateTime.UtcNow, amount: 10m, description: "B");

        var response = await client.SendAsync(AuthedGet($"/api/transactions?accountId={accountA.Id}", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(txA.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactions_CategoryFilter_ReturnsOnlyThatCategory()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Groceries", CategoryType.Expense);
        var categorized = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Categorized", categoryId: category.Id);
        await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Uncategorized");

        var response = await client.SendAsync(AuthedGet($"/api/transactions?categoryId={category.Id}", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(categorized.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("Groceries", body.GetProperty("items")[0].GetProperty("categoryName").GetString());
    }

    [Fact]
    public async Task GetTransactions_ExcludeTransfersTrue_FiltersOutTransferRows()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var income = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 100m, description: "Salary", type: TransactionType.Credit);
        var expense = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -40m, description: "Coffee", type: TransactionType.Debit);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -500m, description: "Dinheiro reservado", type: TransactionType.Debit, isTransfer: true);

        var response = await client.SendAsync(AuthedGet("/api/transactions?excludeTransfers=true", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains(income.Id.ToString(), ids);
        Assert.Contains(expense.Id.ToString(), ids);
    }

    [Fact]
    public async Task GetTransactions_TypeIncome_ExcludesTransfersEvenWhenCredit()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var income = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 100m, description: "Salary", type: TransactionType.Credit);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 100m, description: "Transfer In", type: TransactionType.Credit, isTransfer: true);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -50m, description: "Expense", type: TransactionType.Debit);

        var response = await client.SendAsync(AuthedGet("/api/transactions?type=income", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(income.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactions_TypeExpense_ExcludesTransfersEvenWhenDebit()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var expense = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -50m, description: "Groceries", type: TransactionType.Debit);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -50m, description: "Transfer Out", type: TransactionType.Debit, isTransfer: true);

        var response = await client.SendAsync(AuthedGet("/api/transactions?type=expense", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(expense.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactions_TypeTransfer_ReturnsOnlyTransferFlaggedRegardlessOfType()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transferIn = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 100m, description: "Transfer In", type: TransactionType.Credit, isTransfer: true);
        var transferOut = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -100m, description: "Transfer Out", type: TransactionType.Debit, isTransfer: true);
        await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -50m, description: "Regular Expense");

        var response = await client.SendAsync(AuthedGet("/api/transactions?type=transfer", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains(transferIn.Id.ToString(), ids);
        Assert.Contains(transferOut.Id.ToString(), ids);
    }

    [Fact]
    public async Task GetTransactions_StatusFilter_MatchesPendingOrPosted()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var pending = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Pending", status: TransactionStatus.Pending);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Posted", status: TransactionStatus.Posted);

        var response = await client.SendAsync(AuthedGet("/api/transactions?status=pending", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(pending.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactions_AmountRangeFilter_MatchesAbsoluteValue()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var inRange = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -75m, description: "InRange");
        await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "TooSmall");
        await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: 500m, description: "TooBig");

        var response = await client.SendAsync(AuthedGet("/api/transactions?amountMin=50&amountMax=100", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(inRange.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactions_SearchFilter_MatchesDescriptionOrMerchantCaseInsensitive()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var byDescription = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Uber Trip");
        var byMerchant = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Ride", merchantName: "UBER B.V.");
        await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: 10m, description: "Grocery Store");

        var response = await client.SendAsync(AuthedGet("/api/transactions?search=uber", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains(byDescription.Id.ToString(), ids);
        Assert.Contains(byMerchant.Id.ToString(), ids);
    }

    [Fact]
    public async Task GetTransactions_Page2_ReturnsNextNonOverlappingPage_WithFullTotalCount()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var baseDate = new DateTime(2026, 1, 1);
        for (var i = 0; i < 60; i++)
        {
            await SeedTransactionAsync(
                factory, userId, account.Id, date: baseDate.AddDays(i), amount: 10m, description: $"Txn {i}");
        }

        var page1Response = await client.SendAsync(AuthedGet("/api/transactions?page=1", accessToken));
        var page1Body = await page1Response.Content.ReadFromJsonAsync<JsonElement>();
        var page1Ids = page1Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToHashSet();

        var page2Response = await client.SendAsync(AuthedGet("/api/transactions?page=2", accessToken));
        var page2Body = await page2Response.Content.ReadFromJsonAsync<JsonElement>();
        var page2Ids = page2Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToHashSet();

        Assert.Equal(50, page1Body.GetProperty("items").GetArrayLength());
        Assert.Equal(10, page2Body.GetProperty("items").GetArrayLength());
        Assert.Equal(60, page1Body.GetProperty("totalCount").GetInt32());
        Assert.Equal(60, page2Body.GetProperty("totalCount").GetInt32());
        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    [Fact]
    public async Task GetTransactions_CombinedFilters_NarrowsWithAndSemantics()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, accountA) = await SeedConnectionAndAccountAsync(factory, userId, "Bank A");
        var (_, accountB) = await SeedConnectionAndAccountAsync(factory, userId, "Bank B");

        var match = await SeedTransactionAsync(
            factory, userId, accountA.Id, date: DateTime.UtcNow, amount: -30m, description: "Uber Eats", type: TransactionType.Debit);
        // Wrong account
        await SeedTransactionAsync(
            factory, userId, accountB.Id, date: DateTime.UtcNow, amount: -30m, description: "Uber Eats", type: TransactionType.Debit);
        // Wrong type (income, not expense)
        await SeedTransactionAsync(
            factory, userId, accountA.Id, date: DateTime.UtcNow, amount: 30m, description: "Uber Eats", type: TransactionType.Credit);
        // Wrong search term
        await SeedTransactionAsync(
            factory, userId, accountA.Id, date: DateTime.UtcNow, amount: -30m, description: "Grocery Store", type: TransactionType.Debit);

        var response = await client.SendAsync(AuthedGet(
            $"/api/transactions?accountId={accountA.Id}&type=expense&search=uber", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(match.Id.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetTransactionDetail_ExistingOwnedTransaction_ReturnsFullShape()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Groceries", CategoryType.Expense);
        var transaction = await SeedTransactionAsync(
            factory,
            userId,
            account.Id,
            date: new DateTime(2026, 1, 15),
            amount: -42.50m,
            description: "Grocery Store",
            categoryId: category.Id,
            merchantName: "Market Co",
            descriptionRaw: "MARKET CO*RAW",
            notes: "Weekly shopping",
            installmentNumber: 1,
            totalInstallments: 3,
            billId: "bill-123");

        var response = await client.SendAsync(AuthedGet($"/api/transactions/{transaction.Id}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(transaction.Id.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("Grocery Store", body.GetProperty("description").GetString());
        Assert.Equal("MARKET CO*RAW", body.GetProperty("descriptionRaw").GetString());
        Assert.Equal("Market Co", body.GetProperty("merchantName").GetString());
        Assert.Equal(account.Id.ToString(), body.GetProperty("accountId").GetString());
        Assert.Equal(account.Name, body.GetProperty("accountName").GetString());
        Assert.Equal(category.Id.ToString(), body.GetProperty("categoryId").GetString());
        Assert.Equal("Groceries", body.GetProperty("categoryName").GetString());
        Assert.Equal(-42.50m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("Debit", body.GetProperty("type").GetString());
        Assert.Equal("Posted", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("isTransfer").GetBoolean());
        Assert.False(body.GetProperty("isHidden").GetBoolean());
        Assert.Equal("Weekly shopping", body.GetProperty("notes").GetString());
        Assert.Equal(1, body.GetProperty("installmentNumber").GetInt32());
        Assert.Equal(3, body.GetProperty("totalInstallments").GetInt32());
        Assert.Equal("bill-123", body.GetProperty("billId").GetString());
    }

    [Fact]
    public async Task GetTransactionDetail_MissingId_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet($"/api/transactions/{Guid.NewGuid()}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactionDetail_OtherUsersTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var (_, otherAccount) = await SeedConnectionAndAccountAsync(factory, otherUserId, "Other Bank");
        var otherTransaction = await SeedTransactionAsync(
            factory, otherUserId, otherAccount.Id, date: DateTime.UtcNow, amount: -10m, description: "Not Mine");

        var response = await client.SendAsync(AuthedGet($"/api/transactions/{otherTransaction.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_ValidCategory_SetsCategoryAndUserOverride_ReturnsDetail()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Groceries", CategoryType.Expense);
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/category", accessToken, new { categoryId = category.Id }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(category.Id.ToString(), body.GetProperty("categoryId").GetString());
        Assert.Equal("Groceries", body.GetProperty("categoryName").GetString());

        var stored = await GetTransactionAsync(factory, transaction.Id);
        Assert.Equal(category.Id, stored.CategoryId);
        Assert.Equal(CategorySource.UserOverride, stored.CategorySource);
    }

    [Fact]
    public async Task PatchCategory_NullCategoryId_UnassignsButStillSetsUserOverride()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Groceries", CategoryType.Expense);
        var transaction = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store", categoryId: category.Id);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/category", accessToken, new { categoryId = (Guid?)null }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetTransactionAsync(factory, transaction.Id);
        Assert.Null(stored.CategoryId);
        Assert.Equal(CategorySource.UserOverride, stored.CategorySource);
    }

    [Fact]
    public async Task PatchCategory_NonExistentCategoryId_Returns400()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/category", accessToken, new { categoryId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_OtherUsersCategoryId_Returns400()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");
        var otherUsersCategory = await SeedCategoryAsync(factory, Guid.NewGuid(), "Not Mine", CategoryType.Expense);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/category", accessToken, new { categoryId = otherUsersCategory.Id }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Groceries", CategoryType.Expense);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{Guid.NewGuid()}/category", accessToken, new { categoryId = category.Id }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_NoAuth_Returns401()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/transactions/{Guid.NewGuid()}/category")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { categoryId = Guid.NewGuid() }), Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchNote_SetsNote_ReturnsDetail()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/note", accessToken, new { note = "Split with roommate" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Split with roommate", body.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task PatchNote_NullNote_ClearsExistingNote()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store", notes: "Old note");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/note", accessToken, new { note = (string?)null }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetTransactionAsync(factory, transaction.Id);
        Assert.Null(stored.Notes);
    }

    [Fact]
    public async Task PatchNote_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{Guid.NewGuid()}/note", accessToken, new { note = "x" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchHide_SetsIsHidden_ReturnsDetail()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/hide", accessToken, new { isHidden = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isHidden").GetBoolean());
    }

    [Fact]
    public async Task PatchHide_MissingIsHidden_Returns400()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/hide", accessToken, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchHide_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{Guid.NewGuid()}/hide", accessToken, new { isHidden = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchTransfer_SetsIsTransferAndManualOverride_ReturnsDetail()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/transfer", accessToken, new { isTransfer = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isTransfer").GetBoolean());

        var stored = await GetTransactionAsync(factory, transaction.Id);
        Assert.True(stored.IsTransfer);
        Assert.True(stored.IsTransferManualOverride);
    }

    [Fact]
    public async Task PatchTransfer_MissingIsTransfer_Returns400()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var transaction = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{transaction.Id}/transfer", accessToken, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchTransfer_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPatch($"/api/transactions/{Guid.NewGuid()}/transfer", accessToken, new { isTransfer = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSimilar_MatchesByMerchantCaseInsensitive_ExcludesAlreadyCategorized_ReturnsCountAndMerchant()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Transport", CategoryType.Expense);
        var source = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Uber Trip", merchantName: "UBER B.V.");
        var matchOne = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -12m, description: "Uber Trip 2", merchantName: "uber b.v.");
        var alreadyCategorized = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -8m, description: "Uber Trip 3", merchantName: "Uber B.V.", categoryId: category.Id);
        await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -20m, description: "Grocery Store", merchantName: "Market Co");

        var response = await client.SendAsync(
            AuthedGet($"/api/transactions/{source.Id}/similar?categoryId={category.Id}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal("UBER B.V.", body.GetProperty("merchant").GetString());
        _ = matchOne;
        _ = alreadyCategorized;
    }

    [Fact]
    public async Task GetSimilar_NoMerchantOnSource_FallsBackToExactDescriptionMatch_NoErrorOnZeroMatches()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Misc", CategoryType.Expense);
        var source = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Cash Withdrawal ATM 001");

        var response = await client.SendAsync(
            AuthedGet($"/api/transactions/{source.Id}/similar?categoryId={category.Id}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal("Cash Withdrawal ATM 001", body.GetProperty("merchant").GetString());
    }

    [Fact]
    public async Task GetSimilar_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Misc", CategoryType.Expense);

        var response = await client.SendAsync(
            AuthedGet($"/api/transactions/{Guid.NewGuid()}/similar?categoryId={category.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSimilar_NoAuth_Returns401()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/transactions/{Guid.NewGuid()}/similar?categoryId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApplySimilar_BulkSetsCategoryAndUserOverride_ExcludesSourceAndAlreadyCategorized()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var category = await SeedCategoryAsync(factory, userId, "Transport", CategoryType.Expense);
        var source = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Uber Trip", merchantName: "UBER B.V.");
        var matchOne = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -12m, description: "Uber Trip 2", merchantName: "uber b.v.");
        var matchTwo = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -14m, description: "Uber Trip 3", merchantName: "Uber B.V.");
        var unrelated = await SeedTransactionAsync(
            factory, userId, account.Id, date: DateTime.UtcNow, amount: -20m, description: "Grocery Store", merchantName: "Market Co");

        var response = await client.SendAsync(
            AuthedPost($"/api/transactions/{source.Id}/apply-similar", accessToken, new { categoryId = category.Id }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("updatedCount").GetInt32());

        var storedMatchOne = await GetTransactionAsync(factory, matchOne.Id);
        var storedMatchTwo = await GetTransactionAsync(factory, matchTwo.Id);
        var storedSource = await GetTransactionAsync(factory, source.Id);
        var storedUnrelated = await GetTransactionAsync(factory, unrelated.Id);

        Assert.Equal(category.Id, storedMatchOne.CategoryId);
        Assert.Equal(CategorySource.UserOverride, storedMatchOne.CategorySource);
        Assert.Equal(category.Id, storedMatchTwo.CategoryId);
        Assert.Equal(CategorySource.UserOverride, storedMatchTwo.CategorySource);
        Assert.Null(storedSource.CategoryId); // apply-similar never touches the source row itself
        Assert.Null(storedUnrelated.CategoryId);
    }

    [Fact]
    public async Task ApplySimilar_NonExistentCategoryId_Returns400()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        var source = await SeedTransactionAsync(factory, userId, account.Id, date: DateTime.UtcNow, amount: -10m, description: "Store");

        var response = await client.SendAsync(
            AuthedPost($"/api/transactions/{source.Id}/apply-similar", accessToken, new { categoryId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApplySimilar_MissingTransaction_Returns404()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);
        var category = await SeedCategoryAsync(factory, userId, "Misc", CategoryType.Expense);

        var response = await client.SendAsync(
            AuthedPost($"/api/transactions/{Guid.NewGuid()}/apply-similar", accessToken, new { categoryId = category.Id }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApplySimilar_NoAuth_Returns401()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/transactions/{Guid.NewGuid()}/apply-similar")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { categoryId = Guid.NewGuid() }), Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCategories_NoAuth_Returns401()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCategories_AfterSetup_ReturnsNonEmptySeededCategoryTree_WithoutError()
    {
        // 04-02/CAT-01: AuthEndpoints.SetupAsync now seeds the default BR category tree
        // synchronously, so the table is no longer empty right after setup (Phase 2/3's original
        // "starts EMPTY until Phase 4" assumption this test previously encoded).
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/categories", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetCategories_WithSeededCategory_ReturnsShapedRow()
    {
        using var factory = new TransactionsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var category = await SeedCategoryAsync(factory, userId, "Transport", CategoryType.Expense);

        var response = await client.SendAsync(AuthedGet("/api/categories", accessToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 04-02/CAT-01: setup now also seeds the default category tree, so the response includes
        // more than just this test's manually-seeded row -- find it by id instead of assuming
        // it's the only row.
        var row = body.EnumerateArray().Single(r => r.GetProperty("id").GetString() == category.Id.ToString());
        Assert.Equal("Transport", row.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("parentCategoryId").ValueKind);
        Assert.Equal("Expense", row.GetProperty("type").GetString());
        Assert.False(row.GetProperty("isHidden").GetBoolean());
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

    private static HttpRequestMessage AuthedPost(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<SimpleFinance.Domain.Entities.Transaction> GetTransactionAsync(
        TransactionsApiFactory factory, Guid transactionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        return await db.Transactions.SingleAsync(t => t.Id == transactionId);
    }

    private static async Task<(Connection Connection, Account Account)> SeedConnectionAndAccountAsync(
        TransactionsApiFactory factory, Guid userId, string institutionName)
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

    private static async Task<Category> SeedCategoryAsync(
        TransactionsApiFactory factory, Guid userId, string name, CategoryType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Type = type,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<SimpleFinance.Domain.Entities.Transaction> SeedTransactionAsync(
        TransactionsApiFactory factory,
        Guid userId,
        Guid accountId,
        DateTime date,
        decimal amount,
        string description,
        TransactionType type = TransactionType.Debit,
        TransactionStatus status = TransactionStatus.Posted,
        bool isTransfer = false,
        Guid? categoryId = null,
        string? merchantName = null,
        string? descriptionRaw = null,
        string? notes = null,
        int? installmentNumber = null,
        int? totalInstallments = null,
        string? billId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var transaction = new SimpleFinance.Domain.Entities.Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            PluggyTransactionId = $"pluggy-txn-{Guid.NewGuid():N}",
            Date = date,
            Description = description,
            DescriptionRaw = descriptionRaw,
            Amount = amount,
            Type = type,
            CurrencyCode = "BRL",
            Status = status,
            MerchantName = merchantName,
            CategoryId = categoryId,
            CategorySource = CategorySource.SystemMapping,
            IsTransfer = isTransfer,
            Notes = notes,
            InstallmentNumber = installmentNumber,
            TotalInstallments = totalInstallments,
            BillId = billId,
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
/// AccountsApiFactory's minimal pattern. No IFinancialDataProvider swap needed -- pure EF reads.
/// </summary>
internal sealed class TransactionsApiFactory : WebApplicationFactory<Program>
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
