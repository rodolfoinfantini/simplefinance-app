using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>
/// GET /accounts?itemId= response item shape, per 02-RESEARCH.md's Data Endpoints section
/// (account schema reconstructed via WebSearch summary, MEDIUM confidence -- recommend a live
/// sandbox call to confirm before finalizing EF entity mapping). BankData/CreditData are
/// mutually exclusive depending on Type (BANK vs CREDIT).
/// </summary>
public sealed record PluggyAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string Subtype,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("marketingName")] string? MarketingName,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("taxNumber")] string? TaxNumber,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("bankData")] PluggyBankDataDto? BankData,
    [property: JsonPropertyName("creditData")] PluggyCreditDataDto? CreditData)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record PluggyBankDataDto(
    [property: JsonPropertyName("transferNumber")] string? TransferNumber,
    [property: JsonPropertyName("closingBalance")] decimal? ClosingBalance,
    [property: JsonPropertyName("automaticallyInvestedBalance")] decimal? AutomaticallyInvestedBalance,
    [property: JsonPropertyName("overdraftContractedLimit")] decimal? OverdraftContractedLimit,
    [property: JsonPropertyName("overdraftUsedLimit")] decimal? OverdraftUsedLimit,
    [property: JsonPropertyName("unarrangedOverdraftAmount")] decimal? UnarrangedOverdraftAmount,
    [property: JsonPropertyName("blockedBalance")] decimal? BlockedBalance = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record PluggyCreditDataDto(
    [property: JsonPropertyName("level")] string? Level,
    [property: JsonPropertyName("brand")] string? Brand,
    [property: JsonPropertyName("brandAdditionalInfo")] string? BrandAdditionalInfo,
    [property: JsonPropertyName("balanceCloseDate")] DateTime? BalanceCloseDate,
    [property: JsonPropertyName("balanceDueDate")] DateTime? BalanceDueDate,
    [property: JsonPropertyName("availableCreditLimit")] decimal? AvailableCreditLimit,
    [property: JsonPropertyName("creditLimit")] decimal? CreditLimit,
    [property: JsonPropertyName("isLimitFlexible")] bool? IsLimitFlexible,
    [property: JsonPropertyName("balanceForeignCurrency")] decimal? BalanceForeignCurrency,
    [property: JsonPropertyName("minimumPayment")] decimal? MinimumPayment,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("holderType")] string? HolderType);
