using SimpleFinance.Application.Sync;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// The sole implementation of <see cref="IFinancialDataProvider"/> (02-01's seam) -- wraps
/// <see cref="PluggyClient"/> and maps every Pluggy wire DTO into provider-neutral Provider
/// DTOs here, the last point before Application-layer code (02-04's reconciler) ever touches
/// the data. No Pluggy DTO/type crosses this boundary
/// (02-RESEARCH.md's Architectural Responsibility Map).
/// </summary>
public sealed class PluggyDataProvider : IFinancialDataProvider
{
    private readonly PluggyClient _client;

    public PluggyDataProvider(PluggyClient client)
    {
        _client = client;
    }

    public Task<string> CreateConnectTokenAsync(string? existingPluggyItemId, CancellationToken ct) =>
        _client.CreateConnectTokenAsync(existingPluggyItemId, ct);

    public async Task<ProviderItemStatus> GetItemStatusAsync(string pluggyItemId, CancellationToken ct)
    {
        var item = await _client.GetItemAsync(pluggyItemId, ct);

        return new ProviderItemStatus(
            Status: item.Status,
            ExecutionStatus: item.ExecutionStatus,
            StatusDetail: item.StatusDetail,
            ErrorCode: item.Error?.Code,
            ErrorMessage: item.Error?.Message,
            ConnectorId: item.Connector?.Id,
            ConnectorName: item.Connector?.Name,
            // No separate "institutionName" field exists on PluggyConnectorDto (02-02) --
            // Connector.Name is the closest available field, mapped defensively per this
            // plan's <action> instruction rather than guessing at an unconfirmed wire field.
            InstitutionName: item.Connector?.Name,
            ImageUrl: item.Connector?.ImageUrl);
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string pluggyItemId, CancellationToken ct)
    {
        var accounts = await _client.GetAccountsAsync(pluggyItemId, ct);
        return accounts.Select(MapAccount).ToList();
    }

    public async Task<IReadOnlyList<ProviderTransaction>> GetTransactionsAsync(string pluggyAccountId, CancellationToken ct)
    {
        var transactions = await _client.GetTransactionsAsync(pluggyAccountId, ct);
        return transactions.Select(MapTransaction).ToList();
    }

    public async Task<IReadOnlyList<ProviderInvestment>> GetInvestmentsAsync(string pluggyItemId, CancellationToken ct)
    {
        var investments = await _client.GetInvestmentsAsync(pluggyItemId, ct);
        return investments.Select(MapInvestment).ToList();
    }

    public Task TriggerItemUpdateAsync(string pluggyItemId, CancellationToken ct) =>
        _client.TriggerItemUpdateAsync(pluggyItemId, ct);

    public Task DeleteItemAsync(string pluggyItemId, CancellationToken ct) =>
        _client.DeleteItemAsync(pluggyItemId, ct);

    public Task SubmitMfaAsync(string pluggyItemId, string parameterName, string parameterValue, CancellationToken ct) =>
        _client.SubmitMfaAsync(pluggyItemId, parameterName, parameterValue, ct);

    private static ProviderAccount MapAccount(PluggyAccountDto dto)
    {
        var isCreditAccount = string.Equals(dto.Type, "CREDIT", StringComparison.OrdinalIgnoreCase);
        var creditData = dto.CreditData is null || !isCreditAccount
            ? null
            : new ProviderCreditData(
                Brand: dto.CreditData.Brand,
                Level: dto.CreditData.Level,
                BrandAdditionalInfo: dto.CreditData.BrandAdditionalInfo,
                CreditLimit: dto.CreditData.CreditLimit,
                AvailableCreditLimit: dto.CreditData.AvailableCreditLimit,
                BalanceCloseDate: dto.CreditData.BalanceCloseDate,
                BalanceDueDate: dto.CreditData.BalanceDueDate,
                MinimumPayment: dto.CreditData.MinimumPayment,
                Status: dto.CreditData.Status,
                HolderType: dto.CreditData.HolderType);

        return new ProviderAccount(
            Id: dto.Id,
            Type: dto.Type,
            Subtype: dto.Subtype,
            Name: dto.Name,
            MarketingName: dto.MarketingName,
            NumberMasked: dto.Number,
            Owner: dto.Owner,
            Balance: dto.Balance,
            CurrencyCode: dto.CurrencyCode,
            CreditData: creditData,
            AutomaticallyInvestedBalance: ResolveReservedBalance(dto));
    }

    private static decimal? ResolveReservedBalance(PluggyAccountDto dto)
    {
        if (!string.Equals(dto.Type, "BANK", StringComparison.OrdinalIgnoreCase) || dto.BankData is null)
        {
            return null;
        }

        // Pluggy Open Finance / MeuPluggy exposes per-vault savings pockets (e.g. Mercado Pago Cofrinhos)
        // inside bankData.reservedBalances[].availableAmounts[].amount.
        if (dto.BankData.ReservedBalances is { Count: > 0 } reserves)
        {
            var sum = reserves
                .Where(r => r.AvailableAmounts is not null)
                .SelectMany(r => r.AvailableAmounts!)
                .Sum(a => a.Amount ?? 0m);
            if (sum > 0m)
            {
                return sum;
            }
        }

        // Explicit blocked/reserved balance separate from available balance.
        if (dto.BankData.BlockedBalance is > 0m)
        {
            return dto.BankData.BlockedBalance;
        }

        // Closing balance exceeds available balance (e.g. closingBalance = balance + blocked/reserved).
        if (dto.BankData.ClosingBalance is decimal closing && closing > dto.Balance)
        {
            return closing - dto.Balance;
        }

        // Per Pluggy docs, several Brazilian connectors report automaticallyInvestedBalance == balance
        // when the checking balance is already auto-invested; never double-count in that case.
        if (dto.BankData.AutomaticallyInvestedBalance is > 0m
            && dto.BankData.AutomaticallyInvestedBalance != dto.Balance)
        {
            return dto.BankData.AutomaticallyInvestedBalance;
        }

        return null;
    }

    private static ProviderTransaction MapTransaction(PluggyTransactionDto dto)
    {
        // System.Text.Json passthrough: PaymentData is stored as opaque raw JSON text for
        // Phase 3's transfer-detection consumption (threat T-02-08, accepted disposition) --
        // never parsed/typed here.
        var paymentDataJson = dto.PaymentData?.GetRawText();

        return new ProviderTransaction(
            Id: dto.Id,
            AccountId: dto.AccountId,
            Date: dto.Date,
            Description: dto.Description,
            DescriptionRaw: dto.DescriptionRaw,
            Amount: dto.Amount,
            AmountInAccountCurrency: dto.AmountInAccountCurrency,
            BalanceAfter: dto.Balance,
            CurrencyCode: dto.CurrencyCode,
            Status: dto.Status,
            Type: dto.Type,
            PluggyCategory: dto.Category,
            PluggyCategoryId: dto.CategoryId,
            MerchantName: dto.MerchantName,
            MerchantDocument: dto.MerchantDocument,
            InstallmentNumber: dto.CreditCardMetadata?.InstallmentNumber,
            TotalInstallments: dto.CreditCardMetadata?.TotalInstallments,
            TotalInstallmentsAmount: dto.CreditCardMetadata?.TotalAmount,
            BillId: dto.CreditCardMetadata?.BillId,
            PaymentDataJson: paymentDataJson);
    }

    private static ProviderInvestment MapInvestment(PluggyInvestmentDto dto) => new(
        Id: dto.Id,
        Name: dto.Name,
        Type: dto.Type,
        Subtype: dto.Subtype,
        // Institution isn't present in this plan's PluggyInvestmentDto field list
        // (02-RESEARCH.md mentions a separate "institution" wire field, but Task 1's <action>
        // DTO omits it) -- Issuer is the closest mapped field, left null here rather than
        // guessing at an unconfirmed shape.
        Institution: null,
        IssuerName: dto.Issuer,
        Balance: dto.Balance ?? dto.Amount,
        Value: dto.Value,
        Quantity: dto.Quantity,
        AmountInvested: dto.AmountOriginal,
        LastMonthRate: dto.LastMonthRate,
        AnnualRate: dto.AnnualRate,
        Date: dto.Date,
        DueDate: dto.DueDate,
        CurrencyCode: dto.CurrencyCode);
}
