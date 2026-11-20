using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Goals;

/// <summary>
/// The full derived-at-read-time picture for a single Goal row, per 07-04-PLAN.md's interfaces
/// block. Name/TargetAmount/TargetDate/LinkedAccountId/LinkedInvestmentId/Icon/Color/Status are
/// persisted directly on <see cref="Goal"/>; CurrentAmount/PercentComplete/Remaining/
/// RequiredMonthlyPace are always recomputed live by GoalService against the linked Account's
/// Balance or Investment's Value (when linked) so they can never go stale -- mirrors
/// BudgetRowResult's "only the input is persisted, everything derived is live" shape exactly.
/// </summary>
public sealed record GoalResult(
    Guid Id,
    string Name,
    decimal TargetAmount,
    decimal CurrentAmount,
    DateTime? TargetDate,
    Guid? LinkedAccountId,
    Guid? LinkedInvestmentId,
    string? Icon,
    string? Color,
    GoalStatus Status,
    decimal PercentComplete,
    decimal Remaining,
    decimal? RequiredMonthlyPace);

/// <summary>
/// The real, directly-testable Goal business logic (07-04 Task 1) behind GET/POST/PATCH
/// /api/goals -- GoalsEndpoints.cs is a thin HTTP wrapper over this class only, no business logic
/// duplicated there. Deliberately placed in Infrastructure (not Application), mirroring
/// BudgetService's/RecurringService's documented rationale: it depends directly on
/// SimpleFinanceDbContext, and Application cannot reference Infrastructure without a circular
/// project reference.
/// </summary>
public sealed class GoalService(SimpleFinanceDbContext db)
{
    // ~average days per calendar month -- 365.25 / 12, matching the plan's exact tolerance value.
    private const double AverageDaysPerMonth = 30.44;

    /// <summary>
    /// GOAL-01's create path. Trusts its caller's already-validated input (CreateGoalValidator,
    /// Task 2, is the sole enforcer of "not both linked", positive TargetAmount, and FK
    /// ownership). Delegates to the same live-computation helper GetGoalsAsync uses so create and
    /// list never diverge on the derived fields.
    /// </summary>
    public async Task<GoalResult> CreateGoalAsync(
        Guid userId,
        string name,
        decimal targetAmount,
        DateTime? targetDate,
        Guid? linkedAccountId,
        Guid? linkedInvestmentId,
        string? icon,
        string? color,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TargetAmount = targetAmount,
            CurrentAmount = 0m,
            TargetDate = targetDate,
            LinkedAccountId = linkedAccountId,
            LinkedInvestmentId = linkedInvestmentId,
            Icon = icon,
            Color = color,
            Status = GoalStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Goals.Add(goal);
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(goal, ct);
    }

    /// <summary>
    /// GOAL-02's read path + GOAL-03's read-time completion reconciliation (mirrors
    /// BudgetService.GetBudgetsForMonthAsync's copy-forward-on-read precedent). Batches the
    /// needed Account/Investment lookups (one query for all LinkedAccountIds in the set, one for
    /// all LinkedInvestmentIds) to avoid N+1. Any Active row whose live CurrentAmount reaches its
    /// TargetAmount is flipped to Completed and persisted in-place, with a single SaveChangesAsync
    /// call after the loop -- not per-row.
    /// </summary>
    public async Task<IReadOnlyList<GoalResult>> GetGoalsAsync(Guid userId, CancellationToken ct)
    {
        var goals = await db.Goals.Where(g => g.UserId == userId).ToListAsync(ct);
        if (goals.Count == 0)
        {
            return [];
        }

        var accountIds = goals
            .Where(g => g.LinkedAccountId is not null)
            .Select(g => g.LinkedAccountId!.Value)
            .Distinct()
            .ToList();

        var investmentIds = goals
            .Where(g => g.LinkedInvestmentId is not null)
            .Select(g => g.LinkedInvestmentId!.Value)
            .Distinct()
            .ToList();

        var accountsById = accountIds.Count == 0
            ? new Dictionary<Guid, Account>()
            : await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        var investmentsById = investmentIds.Count == 0
            ? new Dictionary<Guid, Investment>()
            : await db.Investments.Where(i => investmentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var results = new List<GoalResult>(goals.Count);
        var dirty = false;
        var now = DateTime.UtcNow;

        foreach (var goal in goals)
        {
            var account = goal.LinkedAccountId is not null && accountsById.TryGetValue(goal.LinkedAccountId.Value, out var a)
                ? a
                : null;
            var investment = goal.LinkedInvestmentId is not null && investmentsById.TryGetValue(goal.LinkedInvestmentId.Value, out var i)
                ? i
                : null;

            var liveCurrentAmount = ComputeLiveCurrentAmount(goal, account, investment);

            if (goal.Status == GoalStatus.Active && liveCurrentAmount >= goal.TargetAmount)
            {
                goal.Status = GoalStatus.Completed;
                goal.UpdatedAt = now;
                dirty = true;
            }

            results.Add(ToResult(goal, liveCurrentAmount));
        }

        if (dirty)
        {
            await db.SaveChangesAsync(ct);
        }

        return results;
    }

    /// <summary>
    /// GOAL-01's edit path. Ownership-checked (T-07-08) -- returns null (never throws) for a
    /// missing/other-user id. Updates every editable field; UpdatedAt bumped explicitly (no global
    /// SaveChanges interceptor exists in this codebase for that yet, matching BudgetService's own
    /// explicit-bump convention).
    /// </summary>
    public async Task<GoalResult?> UpdateGoalAsync(
        Guid userId,
        Guid id,
        string name,
        decimal targetAmount,
        DateTime? targetDate,
        Guid? linkedAccountId,
        Guid? linkedInvestmentId,
        string? icon,
        string? color,
        CancellationToken ct)
    {
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId, ct);
        if (goal is null)
        {
            return null;
        }

        goal.Name = name;
        goal.TargetAmount = targetAmount;
        goal.TargetDate = targetDate;
        goal.LinkedAccountId = linkedAccountId;
        goal.LinkedInvestmentId = linkedInvestmentId;
        goal.Icon = icon;
        goal.Color = color;
        goal.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(goal, ct);
    }

    /// <summary>
    /// GOAL-02's manual-tracking write path. Succeeds only when the goal has neither
    /// LinkedAccountId nor LinkedInvestmentId set (T-07-09) -- returns null (never throws), same
    /// as the missing/other-user case, so the caller maps both to the same 400/404 without needing
    /// to distinguish. A manual update that reaches the target also flips Status=Completed
    /// immediately, rather than waiting for the next GetGoalsAsync call.
    /// </summary>
    public async Task<GoalResult?> UpdateProgressAsync(Guid userId, Guid id, decimal currentAmount, CancellationToken ct)
    {
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId, ct);
        if (goal is null || goal.LinkedAccountId is not null || goal.LinkedInvestmentId is not null)
        {
            return null;
        }

        goal.CurrentAmount = currentAmount;
        goal.UpdatedAt = DateTime.UtcNow;

        if (goal.Status == GoalStatus.Active && currentAmount >= goal.TargetAmount)
        {
            goal.Status = GoalStatus.Completed;
        }

        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(goal, ct);
    }

    /// <summary>GOAL-03's archive path. Sets Status=Archived regardless of prior state (Active or
    /// Completed). Scoped by (Id, UserId) -- returns null rather than throwing when missing/
    /// other-user.</summary>
    public async Task<GoalResult?> ArchiveGoalAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId, ct);
        if (goal is null)
        {
            return null;
        }

        goal.Status = GoalStatus.Archived;
        goal.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(goal, ct);
    }

    // Single-row variant used by Create/Update/UpdateProgress/Archive -- fetches just the one
    // linked Account/Investment (if any) for the row just written, then delegates to the exact
    // same ComputeLiveCurrentAmount/ToResult helpers GetGoalsAsync uses, so every write path and
    // the list path can never diverge on the derived fields.
    private async Task<GoalResult> ComputeSingleRowAsync(Goal goal, CancellationToken ct)
    {
        Account? account = null;
        Investment? investment = null;

        if (goal.LinkedAccountId is not null)
        {
            account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == goal.LinkedAccountId.Value, ct);
        }
        else if (goal.LinkedInvestmentId is not null)
        {
            investment = await db.Investments.FirstOrDefaultAsync(i => i.Id == goal.LinkedInvestmentId.Value, ct);
        }

        var liveCurrentAmount = ComputeLiveCurrentAmount(goal, account, investment);
        return ToResult(goal, liveCurrentAmount);
    }

    // GOAL-02: a linked goal ALWAYS shows its current live balance, never the persisted
    // CurrentAmount column (that column only holds meaning for unlinked/manually-tracked goals).
    // A linked-but-missing Account/Investment (should never happen given ownership validation at
    // create/update time) defaults to 0, never a stale figure.
    private static decimal ComputeLiveCurrentAmount(Goal goal, Account? account, Investment? investment)
    {
        if (goal.LinkedAccountId is not null)
        {
            return account is null ? 0m : account.Balance + (account.AutomaticallyInvestedBalance ?? 0m);
        }

        if (goal.LinkedInvestmentId is not null)
        {
            return investment?.Balance
                ?? (investment?.Value is not null && investment.Quantity is not null
                    ? investment.Value.Value * investment.Quantity.Value
                    : investment?.Value)
                ?? 0m;
        }

        return goal.CurrentAmount;
    }

    // GOAL-02: PercentComplete/Remaining/RequiredMonthlyPace math -- goals always have
    // TargetAmount > 0 (enforced by CreateGoalValidator/UpdateGoalValidator, Task 2), so no
    // zero-guard is needed here.
    private static GoalResult ToResult(Goal goal, decimal currentAmount)
    {
        var percentComplete = currentAmount / goal.TargetAmount * 100m;
        var remaining = Math.Max(goal.TargetAmount - currentAmount, 0m);
        var requiredMonthlyPace = ComputeRequiredMonthlyPace(goal.TargetDate, remaining);

        return new GoalResult(
            goal.Id,
            goal.Name,
            goal.TargetAmount,
            currentAmount,
            goal.TargetDate,
            goal.LinkedAccountId,
            goal.LinkedInvestmentId,
            goal.Icon,
            goal.Color,
            goal.Status,
            percentComplete,
            remaining,
            requiredMonthlyPace);
    }

    // Null when TargetDate is null, in the past, or Remaining is 0; otherwise
    // Remaining / monthsRemaining, where monthsRemaining is the ceiling of days-until-TargetDate
    // divided by ~30.44, floored at 1 month (per the plan's exact spec).
    private static decimal? ComputeRequiredMonthlyPace(DateTime? targetDate, decimal remaining)
    {
        if (targetDate is null || remaining <= 0m)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (targetDate.Value <= now)
        {
            return null;
        }

        var daysRemaining = (targetDate.Value - now).TotalDays;
        var monthsRemaining = Math.Max(1, (int)Math.Ceiling(daysRemaining / AverageDaysPerMonth));

        return remaining / monthsRemaining;
    }
}
