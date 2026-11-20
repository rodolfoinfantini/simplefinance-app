using System.Text.Json;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.NetWorth;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>net_worth</c>: the caller's current net worth broken into its accounts /
/// investments / credit-card-debt composition. Wraps <see cref="INetWorthReader.GetSummaryAsync"/> (the
/// same live-computed figure the dashboard shows). The <c>asOf</c> arg is accepted for forward
/// compatibility but the figure is always the current live composition — echoed back so the model can
/// be explicit about the "as of now" nature.
/// </summary>
public sealed class NetWorthTool(INetWorthReader reader) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "net_worth",
        "Patrimônio atual (contas bancárias + investimentos; saldo comprometido em cartões de crédito é retornado separadamente em creditCardDebt e não é subtraído de netWorth).",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "asOf": { "type": "string", "description": "YYYY-MM-DD (opcional; o cálculo é sempre o valor atual)" }
          },
          "additionalProperties": false
        }
        """));

    public string Name => "net_worth";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var summary = await reader.GetSummaryAsync(userId, ct);

        return JsonSerializer.Serialize(new
        {
            currency = "BRL",
            netWorth = Math.Round(summary.NetWorth, 2),
            accountsBalance = Math.Round(summary.AccountsBalance, 2),
            investmentsBalance = Math.Round(summary.InvestmentsBalance, 2),
            creditCardDebt = Math.Round(summary.CreditCardDebt, 2),
            asOf = "now",
        });
    }
}
