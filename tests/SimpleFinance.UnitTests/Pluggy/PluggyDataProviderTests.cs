using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Sync;
using SimpleFinance.Infrastructure.Pluggy;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.UnitTests.Pluggy;

/// <summary>
/// Exercises PluggyDataProvider's IFinancialDataProvider mapping end-to-end through
/// PluggyClient against Task 1's synthetic fixtures (no live Pluggy credentials) -- covers
/// CREDIT-vs-BANK CreditData branching, null category/merchant tolerance, and installment
/// field mapping per 02-03-PLAN.md Task 2's &lt;action&gt;.
/// </summary>
public class PluggyDataProviderTests
{
    private const string ApiKey = "test-api-key";

    private sealed class FixtureHandler(IReadOnlyDictionary<string, string> fixturesByPathAndQuery) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;

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

    private static IFinancialDataProvider CreateSut(IReadOnlyDictionary<string, string> fixturesByPathAndQuery)
    {
        var handler = new FixtureHandler(fixturesByPathAndQuery);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api-sandbox.pluggy.ai") };
        var options = Options.Create(new PluggyOptions { ClientId = "client-id", ClientSecret = "client-secret" });
        var apiKeyProvider = new PluggyApiKeyProvider(new FakeHttpClientFactory(httpClient), options);
        var pluggyClient = new PluggyClient(httpClient, apiKeyProvider, options);
        return new PluggyDataProvider(pluggyClient);
    }

    [Fact]
    public async Task GetAccountsAsync_CreditAccount_MapsCreditDataAndBankAccountLeavesItNull()
    {
        var sut = CreateSut(new Dictionary<string, string>
        {
            ["/accounts?itemId=item-001"] = ReadFixture("pluggy-account.json"),
        });

        var accounts = await sut.GetAccountsAsync("item-001", CancellationToken.None);

        var bankAccount = Assert.Single(accounts, a => a.Type == "BANK");
        Assert.Null(bankAccount.CreditData);

        var creditAccount = Assert.Single(accounts, a => a.Type == "CREDIT");
        Assert.NotNull(creditAccount.CreditData);
        Assert.Equal("VISA", creditAccount.CreditData!.Brand);
        Assert.Equal("PLATINUM", creditAccount.CreditData.Level);
        Assert.Equal("ACTIVE", creditAccount.CreditData.Status);
    }

    [Fact]
    public async Task GetTransactionsAsync_NullCategoryAndMerchant_MapsToNullProviderFieldsWithoutThrowing()
    {
        var sut = CreateSut(new Dictionary<string, string>
        {
            ["/v2/transactions?accountId=acc-bank-001"] = ReadFixture("pluggy-transaction-page-1.json"),
            ["/v2/transactions?accountId=acc-bank-001&after=cursor123"] = ReadFixture("pluggy-transaction-page-2.json"),
        });

        var transactions = await sut.GetTransactionsAsync("acc-bank-001", CancellationToken.None);

        var installmentTxn = Assert.Single(transactions, t => t.Id == "txn-003");
        Assert.Null(installmentTxn.PluggyCategory);
        Assert.Null(installmentTxn.PluggyCategoryId);
        Assert.Null(installmentTxn.MerchantName);
        Assert.Null(installmentTxn.MerchantDocument);
    }

    [Fact]
    public async Task GetTransactionsAsync_InstallmentTransaction_MapsCreditCardMetadataFields()
    {
        var sut = CreateSut(new Dictionary<string, string>
        {
            ["/v2/transactions?accountId=acc-bank-001"] = ReadFixture("pluggy-transaction-page-1.json"),
            ["/v2/transactions?accountId=acc-bank-001&after=cursor123"] = ReadFixture("pluggy-transaction-page-2.json"),
        });

        var transactions = await sut.GetTransactionsAsync("acc-bank-001", CancellationToken.None);

        var installmentTxn = Assert.Single(transactions, t => t.Id == "txn-003");
        Assert.Equal(2, installmentTxn.InstallmentNumber);
        Assert.Equal(6, installmentTxn.TotalInstallments);
        Assert.Equal(539.40m, installmentTxn.TotalInstallmentsAmount);
        Assert.Null(installmentTxn.BillId);
    }

    [Fact]
    public async Task GetInvestmentsAsync_MapsFieldsFromFixture()
    {
        var sut = CreateSut(new Dictionary<string, string>
        {
            ["/investments?itemId=item-001"] = ReadFixture("pluggy-investment.json"),
        });

        var investments = await sut.GetInvestmentsAsync("item-001", CancellationToken.None);

        var investment = Assert.Single(investments);
        Assert.Equal("Tesouro Selic 2029", investment.Name);
        Assert.Equal("Tesouro Nacional", investment.IssuerName);
        Assert.Equal(5230.45m, investment.Balance);
    }

    [Fact]
    public async Task GetAccountsAsync_BankAccountWithAutomaticallyInvestedOrBlockedOrClosingBalance_MapsReservedBalance()
    {
        var sut = CreateSut(new Dictionary<string, string>
        {
            ["/accounts?itemId=item-mp"] = """
            {
              "results": [
                {
                  "id": "acc-auto",
                  "type": "BANK",
                  "subtype": "CHECKING_ACCOUNT",
                  "name": "Mercado Pago Auto",
                  "balance": 738.07,
                  "itemId": "item-mp",
                  "currencyCode": "BRL",
                  "bankData": { "closingBalance": 738.07, "automaticallyInvestedBalance": 10093.41 }
                },
                {
                  "id": "acc-blocked",
                  "type": "BANK",
                  "subtype": "CHECKING_ACCOUNT",
                  "name": "Mercado Pago Blocked",
                  "balance": 738.07,
                  "itemId": "item-mp",
                  "currencyCode": "BRL",
                  "bankData": { "closingBalance": 10831.48, "automaticallyInvestedBalance": 0, "blockedBalance": 10093.41 }
                },
                {
                  "id": "acc-closing",
                  "type": "BANK",
                  "subtype": "CHECKING_ACCOUNT",
                  "name": "Mercado Pago Closing",
                  "balance": 738.07,
                  "itemId": "item-mp",
                  "currencyCode": "BRL",
                  "bankData": { "closingBalance": 10831.48, "automaticallyInvestedBalance": 738.07 }
                },
                {
                  "id": "acc-same-as-balance",
                  "type": "BANK",
                  "subtype": "CHECKING_ACCOUNT",
                  "name": "Sicoob Auto Equals Balance",
                  "balance": 4.24,
                  "itemId": "item-mp",
                  "currencyCode": "BRL",
                  "bankData": { "closingBalance": 4.24, "automaticallyInvestedBalance": 4.24 }
                },
                {
                  "id": "acc-cofrinhos",
                  "type": "BANK",
                  "subtype": "CHECKING_ACCOUNT",
                  "name": "Mercado Pago Cofrinhos",
                  "balance": 738.07,
                  "itemId": "item-mp",
                  "currencyCode": "BRL",
                  "bankData": {
                    "closingBalance": 738.07,
                    "automaticallyInvestedBalance": 738.07,
                    "hasReservedBalance": true,
                    "reservedBalances": [
                      { "name": "Emergência", "availableAmounts": [{ "amount": 2563.09, "currencyCode": "BRL" }] },
                      { "name": "Contas", "availableAmounts": [{ "amount": 252.14, "currencyCode": "BRL" }] },
                      { "name": "Financiamento e Seguro", "availableAmounts": [{ "amount": 2086.96, "currencyCode": "BRL" }] },
                      { "name": "Fatura", "availableAmounts": [{ "amount": 5191.22, "currencyCode": "BRL" }] },
                      { "name": "Formatura", "availableAmounts": [{ "amount": 0, "currencyCode": "BRL" }] }
                    ]
                  }
                }
              ]
            }
            """,
        });

        var accounts = await sut.GetAccountsAsync("item-mp", CancellationToken.None);

        Assert.Equal(10093.41m, accounts.Single(a => a.Id == "acc-auto").AutomaticallyInvestedBalance);
        Assert.Equal(10093.41m, accounts.Single(a => a.Id == "acc-blocked").AutomaticallyInvestedBalance);
        Assert.Equal(10093.41m, accounts.Single(a => a.Id == "acc-closing").AutomaticallyInvestedBalance);
        Assert.Null(accounts.Single(a => a.Id == "acc-same-as-balance").AutomaticallyInvestedBalance);
        Assert.Equal(10093.41m, accounts.Single(a => a.Id == "acc-cofrinhos").AutomaticallyInvestedBalance);
    }
}
