namespace SimpleFinance.Application.Sync;

/// <summary>
/// Provider-neutral DTOs consumed by the reconciler (02-04) and produced by whatever
/// Infrastructure-side client implements <see cref="IFinancialDataProvider"/> (02-02/02-03).
/// No Pluggy-specific field names beyond what's needed — these are never raw Pluggy JSON.
/// Status/Type fields are raw strings, not Domain enums: the Infrastructure-side mapper parses
/// Pluggy's exact wire values; Application-side consumers parse these strings into the Domain
/// enums from SimpleFinance.Domain.Enums.
/// </summary>
public sealed record ProviderItemStatus(
    string Status,
    string? ExecutionStatus,
    string? StatusDetail,
    string? ErrorCode,
    string? ErrorMessage,
    int? ConnectorId,
    string? ConnectorName,
    string? InstitutionName,
    string? ImageUrl);

public sealed record ProviderCreditData(
    string? Brand,
    string? Level,
    string? BrandAdditionalInfo,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateTime? BalanceCloseDate,
    DateTime? BalanceDueDate,
    decimal? MinimumPayment,
    string? Status,
    string? HolderType);

public sealed record ProviderCreditCardBill(
    string Id,
    DateTime? DueDate,
    DateTime? BillClosingDate,
    decimal TotalAmount,
    decimal? MinimumPaymentAmount,
    decimal PaidAmount);

public sealed record ProviderAccount(
    string Id,
    string Type,
    string Subtype,
    string Name,
    string? MarketingName,
    string? NumberMasked,
    string? Owner,
    decimal Balance,
    string CurrencyCode,
    ProviderCreditData? CreditData,
    decimal? AutomaticallyInvestedBalance = null);

public sealed record ProviderTransaction(
    string Id,
    string AccountId,
    DateTime Date,
    string Description,
    string? DescriptionRaw,
    decimal Amount,
    decimal? AmountInAccountCurrency,
    decimal? BalanceAfter,
    string CurrencyCode,
    string Status,
    string Type,
    string? PluggyCategory,
    string? PluggyCategoryId,
    string? MerchantName,
    string? MerchantDocument,
    int? InstallmentNumber,
    int? TotalInstallments,
    decimal? TotalInstallmentsAmount,
    string? BillId,
    string? PaymentDataJson);

public sealed record ProviderInvestment(
    string Id,
    string Name,
    string Type,
    string? Subtype,
    string? Institution,
    string? IssuerName,
    decimal? Balance,
    decimal? Value,
    decimal? Quantity,
    decimal? AmountInvested,
    decimal? LastMonthRate,
    decimal? AnnualRate,
    DateTime? Date,
    DateTime? DueDate,
    string? CurrencyCode);
