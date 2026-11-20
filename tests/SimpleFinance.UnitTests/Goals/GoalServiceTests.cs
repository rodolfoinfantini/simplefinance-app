using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Goals;

/// <summary>
/// GOAL-01/02/03 (07-04-PLAN.md Task 1): GoalService is pure EF Core (no Dapper), so these tests
/// use EF Core's InMemory provider -- mirrors RecurringDetectionJobTests.cs/RecurringServiceTests
/// precedent (07-03), not BudgetServiceTests.cs's real-Postgres pattern (that one needs Dapper via
/// DashboardQueries; GoalService never does).
/// </summary>
public class GoalServiceTests
{
    private static SimpleFinanceDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static Account NewAccount(Guid userId, decimal balance) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = AccountType.Bank,
        Subtype = AccountSubtype.CheckingAccount,
        Name = "Test Account",
        Balance = balance,
        CurrencyCode = "BRL",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Investment NewInvestment(Guid userId, decimal? value) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyInvestmentId = $"inv-{Guid.NewGuid():N}",
        Name = "Test Investment",
        Type = InvestmentType.FixedIncome,
        Value = value,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Goal NewGoal(
        Guid userId, string name, decimal targetAmount, decimal currentAmount,
        DateTime? targetDate = null, Guid? linkedAccountId = null, Guid? linkedInvestmentId = null,
        GoalStatus status = GoalStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        TargetAmount = targetAmount,
        CurrentAmount = currentAmount,
        TargetDate = targetDate,
        LinkedAccountId = linkedAccountId,
        LinkedInvestmentId = linkedInvestmentId,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task CreateGoalAsync_WithLinkedAccountAndNoInvestment_Succeeds()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = NewAccount(userId, 250m);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = await service.CreateGoalAsync(
            userId, "Emergency Fund", 1000m, null, account.Id, null, "icon", "#FFAA00", CancellationToken.None);

        Assert.Equal("Emergency Fund", result.Name);
        Assert.Equal(account.Id, result.LinkedAccountId);
        Assert.Null(result.LinkedInvestmentId);
        Assert.Equal(250m, result.CurrentAmount);
        Assert.Equal(GoalStatus.Active, result.Status);

        var persisted = await db.Goals.SingleAsync(g => g.Id == result.Id);
        Assert.Equal(account.Id, persisted.LinkedAccountId);
    }

    [Fact]
    public async Task GetGoalsAsync_LinkedToAccount_ReturnsLiveBalance_NotStalePersisted()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = NewAccount(userId, 100m);
        db.Accounts.Add(account);
        db.Goals.Add(NewGoal(userId, "Vacation", 1000m, currentAmount: 999m, linkedAccountId: account.Id));
        await db.SaveChangesAsync();

        var service = new GoalService(db);

        var first = await service.GetGoalsAsync(userId, CancellationToken.None);
        Assert.Equal(100m, Assert.Single(first).CurrentAmount);

        account.Balance = 350m;
        await db.SaveChangesAsync();

        var second = await service.GetGoalsAsync(userId, CancellationToken.None);
        Assert.Equal(350m, Assert.Single(second).CurrentAmount);
    }

    [Fact]
    public async Task GetGoalsAsync_LinkedToInvestment_ReturnsLiveValue_ZeroWhenNull()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var investment = NewInvestment(userId, value: null);
        db.Investments.Add(investment);
        db.Goals.Add(NewGoal(userId, "Retirement", 5000m, currentAmount: 0m, linkedInvestmentId: investment.Id));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = await service.GetGoalsAsync(userId, CancellationToken.None);

        Assert.Equal(0m, Assert.Single(result).CurrentAmount);

        investment.Value = 420m;
        await db.SaveChangesAsync();

        var second = await service.GetGoalsAsync(userId, CancellationToken.None);
        Assert.Equal(420m, Assert.Single(second).CurrentAmount);
    }

    [Fact]
    public async Task GetGoalsAsync_Unlinked_ReturnsPersistedCurrentAmountUnchanged()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Goals.Add(NewGoal(userId, "New Car", 20000m, currentAmount: 2500m));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = await service.GetGoalsAsync(userId, CancellationToken.None);

        Assert.Equal(2500m, Assert.Single(result).CurrentAmount);
    }

    [Fact]
    public async Task GetGoalsAsync_ComputesPercentCompleteAndRemaining()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Goals.Add(NewGoal(userId, "Laptop", 2000m, currentAmount: 500m));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Equal(25m, result.PercentComplete);
        Assert.Equal(1500m, result.Remaining);
    }

    [Fact]
    public async Task GetGoalsAsync_RequiredMonthlyPace_NullWhenNoTargetDate()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Goals.Add(NewGoal(userId, "No Date", 1000m, currentAmount: 100m, targetDate: null));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Null(result.RequiredMonthlyPace);
    }

    [Fact]
    public async Task GetGoalsAsync_RequiredMonthlyPace_NullWhenTargetDateInPast()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Goals.Add(NewGoal(
            userId, "Past Date", 1000m, currentAmount: 100m, targetDate: DateTime.UtcNow.AddDays(-10)));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Null(result.RequiredMonthlyPace);
    }

    [Fact]
    public async Task GetGoalsAsync_RequiredMonthlyPace_NullWhenRemainingZero()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Goals.Add(NewGoal(
            userId, "Already There", 1000m, currentAmount: 1000m, targetDate: DateTime.UtcNow.AddDays(60)));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Null(result.RequiredMonthlyPace);
    }

    [Fact]
    public async Task GetGoalsAsync_RequiredMonthlyPace_ComputesCeilingMonthsFromTargetDate()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        // ~100 days out -> ceiling(100/30.44) = 4 months -> 500/4 = 125/month.
        db.Goals.Add(NewGoal(
            userId, "House Down Payment", 1000m, currentAmount: 500m, targetDate: DateTime.UtcNow.AddDays(100)));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Equal(125m, result.RequiredMonthlyPace);
    }

    [Fact]
    public async Task GetGoalsAsync_ActiveGoalReachesTarget_PersistsCompletedStatus()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = NewAccount(userId, 1000m);
        db.Accounts.Add(account);
        var goal = NewGoal(userId, "Bike", 1000m, currentAmount: 200m, linkedAccountId: account.Id);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = Assert.Single(await service.GetGoalsAsync(userId, CancellationToken.None));

        Assert.Equal(GoalStatus.Completed, result.Status);

        var persisted = await db.Goals.SingleAsync(g => g.Id == goal.Id);
        Assert.Equal(GoalStatus.Completed, persisted.Status);
    }

    [Fact]
    public async Task UpdateProgressAsync_UnlinkedGoal_UpdatesCurrentAmount()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var goal = NewGoal(userId, "Manual Goal", 1000m, currentAmount: 100m);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = await service.UpdateProgressAsync(userId, goal.Id, 400m, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(400m, result!.CurrentAmount);

        var persisted = await db.Goals.SingleAsync(g => g.Id == goal.Id);
        Assert.Equal(400m, persisted.CurrentAmount);
    }

    [Fact]
    public async Task UpdateProgressAsync_LinkedGoal_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = NewAccount(userId, 100m);
        db.Accounts.Add(account);
        var goal = NewGoal(userId, "Linked Goal", 1000m, currentAmount: 100m, linkedAccountId: account.Id);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var result = await service.UpdateProgressAsync(userId, goal.Id, 999m, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ArchiveGoalAsync_FromActiveOrCompleted_SetsArchived()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var activeGoal = NewGoal(userId, "Active Goal", 1000m, currentAmount: 100m);
        var completedGoal = NewGoal(userId, "Completed Goal", 1000m, currentAmount: 1000m, status: GoalStatus.Completed);
        db.Goals.AddRange(activeGoal, completedGoal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var archivedActive = await service.ArchiveGoalAsync(userId, activeGoal.Id, CancellationToken.None);
        var archivedCompleted = await service.ArchiveGoalAsync(userId, completedGoal.Id, CancellationToken.None);

        Assert.Equal(GoalStatus.Archived, archivedActive!.Status);
        Assert.Equal(GoalStatus.Archived, archivedCompleted!.Status);
    }

    [Fact]
    public async Task Methods_ScopedByUserId_ForeignOrMissingId_NeverThrows_ReturnsNullOrFalse()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await using var db = CreateDb();
        var foreignGoal = NewGoal(otherUserId, "Not Yours", 1000m, currentAmount: 0m);
        db.Goals.Add(foreignGoal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);

        var updateResult = await service.UpdateGoalAsync(
            userId, foreignGoal.Id, "Hacked", 500m, null, null, null, null, null, CancellationToken.None);
        var progressResult = await service.UpdateProgressAsync(userId, foreignGoal.Id, 500m, CancellationToken.None);
        var archiveResult = await service.ArchiveGoalAsync(userId, foreignGoal.Id, CancellationToken.None);
        var missingResult = await service.ArchiveGoalAsync(userId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(updateResult);
        Assert.Null(progressResult);
        Assert.Null(archiveResult);
        Assert.Null(missingResult);
    }

    [Fact]
    public async Task UpdateGoalAsync_OwnedGoal_UpdatesAllEditableFields()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = NewAccount(userId, 50m);
        db.Accounts.Add(account);
        var goal = NewGoal(userId, "Old Name", 1000m, currentAmount: 0m);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var targetDate = DateTime.UtcNow.AddMonths(6);
        var result = await service.UpdateGoalAsync(
            userId, goal.Id, "New Name", 2000m, targetDate, account.Id, null, "new-icon", "#123456", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("New Name", result!.Name);
        Assert.Equal(2000m, result.TargetAmount);
        Assert.Equal(account.Id, result.LinkedAccountId);
        Assert.Equal("new-icon", result.Icon);
        Assert.Equal("#123456", result.Color);
    }

    [Fact]
    public async Task GetGoalsAsync_LinkedToEquityInvestment_PrefersBalanceOverUnitValueAndFallsBackToValueTimesQuantity()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();

        var withBalance = NewInvestment(userId, value: 14.12m);
        withBalance.Balance = 225.92m;
        withBalance.Quantity = 16m;

        var withoutBalance = NewInvestment(userId, value: 40.19m);
        withoutBalance.Balance = null;
        withoutBalance.Quantity = 5m;

        db.Investments.AddRange(withBalance, withoutBalance);
        db.Goals.AddRange(
            NewGoal(userId, "Goal Balance", 1000m, currentAmount: 0m, linkedInvestmentId: withBalance.Id),
            NewGoal(userId, "Goal Qty", 1000m, currentAmount: 0m, linkedInvestmentId: withoutBalance.Id));
        await db.SaveChangesAsync();

        var service = new GoalService(db);
        var goals = await service.GetGoalsAsync(userId, CancellationToken.None);

        Assert.Equal(225.92m, goals.Single(g => g.Name == "Goal Balance").CurrentAmount);
        Assert.Equal(200.95m, goals.Single(g => g.Name == "Goal Qty").CurrentAmount);
    }
}
