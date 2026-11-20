using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>
/// GET /bills?accountId= response item shape (Pluggy Credit Card Bills API).
/// </summary>
public sealed record PluggyBillDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("dueDate")] DateTime? DueDate,
    [property: JsonPropertyName("billClosingDate")] DateTime? BillClosingDate,
    [property: JsonPropertyName("totalAmount")] decimal TotalAmount,
    [property: JsonPropertyName("totalAmountCurrencyCode")] string? TotalAmountCurrencyCode,
    [property: JsonPropertyName("minimumPaymentAmount")] decimal? MinimumPaymentAmount,
    [property: JsonPropertyName("payments")] IReadOnlyList<PluggyBillPaymentDto>? Payments);

public sealed record PluggyBillPaymentDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("valueType")] string? ValueType,
    [property: JsonPropertyName("paymentDate")] DateTime? PaymentDate,
    [property: JsonPropertyName("amount")] decimal Amount);
