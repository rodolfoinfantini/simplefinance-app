using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Sync;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// Runs <see cref="ITransferDetectionService"/> once at app boot so newly added transfer heuristics
/// (such as same-account cofrinho/reserve pairs) immediately clean up historical transactions without
/// waiting for the next Pluggy sync. Idempotent and respects <c>IsTransferManualOverride</c>.
/// </summary>
public sealed class TransferDetectionStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TransferDetectionStartupTask> _logger;

    public TransferDetectionStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<TransferDetectionStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var detector = scope.ServiceProvider.GetRequiredService<ITransferDetectionService>();

            var userIds = await db.Users.Select(u => u.Id).ToListAsync(cancellationToken);
            foreach (var userId in userIds)
            {
                var flagged = await detector.DetectTransfersForUserAsync(userId, cancellationToken);
                if (flagged > 0)
                {
                    _logger.LogInformation(
                        "Startup transfer detection flagged {Count} transactions as internal transfers for user {UserId}.",
                        flagged,
                        userId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Startup transfer detection backstop failed -- app continues to boot; will retry on next sync.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
