using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Pluggy;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.UnitTests.Pluggy;

/// <summary>
/// Covers PluggyClient's data-fetching surface (accounts, v2 cursor-paginated transactions,
/// investments) against synthetic sandbox-shaped fixtures -- no live Pluggy credentials needed
/// (02-CONTEXT.md's "Claude's Discretion" mocking note). Asserts all 3 &lt;behavior&gt; items from
/// 02-03-PLAN.md Task 1: cursor-following via response.next verbatim, empty-list-not-exception
/// on zero results, and null category/merchant tolerance (Pro-gating per 02-RESEARCH.md).
/// </summary>
public class PluggyClientDataTests
{
    private const string ApiKey = "test-api-key";

    private sealed class FixtureHandler(IReadOnlyDictionary<string, string> fixturesByPathAndQuery) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            RequestedPaths.Add(pathAndQuery);

            if (request.Method == HttpMethod.Post && pathAndQuery == "/auth")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PluggyAuthResponse(ApiKey)),
                });
            }

            if (fixturesByPathAndQuery.TryGetValue(pathAndQuery, out var json))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            throw new InvalidOperationException($"No fixture registered for {request.Method} {pathAndQuery}");
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pluggy", "Fixtures", fileName));

    private static PluggyClient CreateSut(IReadOnlyDictionary<string, string> fixturesByPathAndQuery, out FixtureHandler handler)
    {
        handler = new FixtureHandler(fixturesByPathAndQuery);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api-sandbox.pluggy.ai") };
        var options = Options.Create(new PluggyOptions { ClientId = "client-id", ClientSecret = "client-secret" });
        var apiKeyProvider = new PluggyApiKeyProvider(new FakeHttpClientFactory(httpClient), options);
        return new PluggyClient(httpClient, apiKeyProvider, options);
    }

    [Fact]
    public async Task GetAccountsAsync_ReturnsAllAccountsFromFixture()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/accounts?itemId=item-001"] = ReadFixture("pluggy-account.json"),
        };
        var sut = CreateSut(fixtures, out _);

        var accounts = await sut.GetAccountsAsync("item-001", CancellationToken.None);

        Assert.Equal(2, accounts.Count);
        var bankAccount = Assert.Single(accounts, a => a.Type == "BANK");
        Assert.Null(bankAccount.CreditData);
        var creditAccount = Assert.Single(accounts, a => a.Type == "CREDIT");
        Assert.NotNull(creditAccount.CreditData);
        Assert.Equal("VISA", creditAccount.CreditData!.Brand);
    }

    [Fact]
    public async Task GetAccountsAsync_ZeroResults_ReturnsEmptyListNotException()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/accounts?itemId=item-empty"] = "{\"results\": []}",
        };
        var sut = CreateSut(fixtures, out _);

        var accounts = await sut.GetAccountsAsync("item-empty", CancellationToken.None);

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task GetInvestmentsAsync_ReturnsAllInvestmentsFromFixture()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/investments?itemId=item-001"] = ReadFixture("pluggy-investment.json"),
        };
        var sut = CreateSut(fixtures, out _);

        var investments = await sut.GetInvestmentsAsync("item-001", CancellationToken.None);

        var investment = Assert.Single(investments);
        Assert.Equal("Tesouro Selic 2029", investment.Name);
        Assert.Equal("FIXED_INCOME", investment.Type);
    }

    [Fact]
    public async Task GetInvestmentsAsync_ZeroResults_ReturnsEmptyListNotException()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/investments?itemId=item-empty"] = "{\"results\": []}",
        };
        var sut = CreateSut(fixtures, out _);

        var investments = await sut.GetInvestmentsAsync("item-empty", CancellationToken.None);

        Assert.Empty(investments);
    }

    [Fact]
    public async Task GetInvestmentsAsync_MultiplePages_FollowsPageQueryUntilTotalPages()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/investments?itemId=item-paged"] = """{"page": 1, "totalPages": 2, "results": [{"id": "inv-1", "name": "PETR4", "balance": 86.34, "type": "EQUITY", "value": 43.17, "quantity": 2, "itemId": "item-paged"}]}""",
            ["/investments?itemId=item-paged&page=2"] = """{"page": 2, "totalPages": 2, "results": [{"id": "inv-2", "name": "BBAS3", "balance": 225.92, "type": "EQUITY", "value": 14.12, "quantity": 16, "itemId": "item-paged"}]}""",
        };
        var sut = CreateSut(fixtures, out var handler);

        var investments = await sut.GetInvestmentsAsync("item-paged", CancellationToken.None);

        Assert.Equal(2, investments.Count);
        Assert.Contains(investments, i => i.Name == "PETR4" && i.Balance == 86.34m);
        Assert.Contains(investments, i => i.Name == "BBAS3" && i.Balance == 225.92m);
        Assert.Contains(handler.RequestedPaths, p => p == "/investments?itemId=item-paged");
        Assert.Contains(handler.RequestedPaths, p => p == "/investments?itemId=item-paged&page=2");
    }

    [Fact]
    public async Task GetTransactionsAsync_FollowsCursorAcrossTwoPages_AndStopsOnNullNext()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/v2/transactions?accountId=acc-bank-001"] = ReadFixture("pluggy-transaction-page-1.json"),
            ["/v2/transactions?accountId=acc-bank-001&after=cursor123"] = ReadFixture("pluggy-transaction-page-2.json"),
        };
        var sut = CreateSut(fixtures, out var handler);

        var transactions = await sut.GetTransactionsAsync("acc-bank-001", CancellationToken.None);

        Assert.Equal(4, transactions.Count);
        Assert.Contains(handler.RequestedPaths, p => p == "/v2/transactions?accountId=acc-bank-001");
        Assert.Contains(handler.RequestedPaths, p => p == "/v2/transactions?accountId=acc-bank-001&after=cursor123");
    }

    [Fact]
    public async Task GetTransactionsAsync_TransactionWithNullCategoryAndMerchant_DeserializesWithoutThrowing()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["/v2/transactions?accountId=acc-bank-001"] = ReadFixture("pluggy-transaction-page-1.json"),
            ["/v2/transactions?accountId=acc-bank-001&after=cursor123"] = ReadFixture("pluggy-transaction-page-2.json"),
        };
        var sut = CreateSut(fixtures, out _);

        var transactions = await sut.GetTransactionsAsync("acc-bank-001", CancellationToken.None);

        var installmentTxn = Assert.Single(transactions, t => t.Id == "txn-003");
        Assert.Null(installmentTxn.Category);
        Assert.Null(installmentTxn.CategoryId);
        Assert.Null(installmentTxn.MerchantName);
        Assert.Null(installmentTxn.MerchantDocument);
        Assert.NotNull(installmentTxn.CreditCardMetadata);
        Assert.Equal(2, installmentTxn.CreditCardMetadata!.InstallmentNumber);
        Assert.Equal(6, installmentTxn.CreditCardMetadata.TotalInstallments);
    }
}
