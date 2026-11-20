using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Sync;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// Exercises TransferDetectionService (TXN-05) against EF Core InMemory. Task 1 covers the
/// Pluggy paymentData primary signal, malformed-JSON safety, idempotency, and manual-override
/// protection. Task 2 extends this file with the amount/date-window fallback signal and the
/// false-positive/ambiguous-match guard scenarios.
/// </summary>
public class TransferDetectionServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Account SeedAccount(SimpleFinanceDbContext db, Guid userId)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = Guid.NewGuid(),
            PluggyAccountId = Guid.NewGuid().ToString(),
            Type = AccountType.Bank,
            Subtype = AccountSubtype.CheckingAccount,
            Name = "Test Account",
            Balance = 0m,
            CurrencyCode = "BRL",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        db.SaveChanges();
        return account;
    }

    private static Transaction SeedTransaction(
        SimpleFinanceDbContext db,
        Guid userId,
        Guid accountId,
        TransactionType type,
        decimal amount,
        DateTime date,
        string? paymentDataJson = null,
        bool isTransfer = false,
        bool isTransferManualOverride = false,
        string description = "Test Transaction")
    {
        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            PluggyTransactionId = Guid.NewGuid().ToString(),
            Date = date,
            Description = description,
            Amount = amount,
            Type = type,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            PaymentDataJson = paymentDataJson,
            IsTransfer = isTransfer,
            IsTransferManualOverride = isTransferManualOverride,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        db.SaveChanges();
        return transaction;
    }

    private static string PaymentDataJson(string? payerDoc, string? receiverDoc) =>
        $$"""
        {
          "payer": {{(payerDoc is null ? "null" : $$"""{ "documentNumber": "{{payerDoc}}" }""")}},
          "receiver": {{(receiverDoc is null ? "null" : $$"""{ "documentNumber": "{{receiverDoc}}" }""")}}
        }
        """;

    [Fact]
    public async Task DetectTransfersForUserAsync_PaymentDataMatch_FlagsBothLegs()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var debit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 500m, baseDate,
            paymentDataJson: PaymentDataJson(payerDoc: "11122233344", receiverDoc: "99988877766"));
        var credit = SeedTransaction(
            db, userId, accountB.Id, TransactionType.Credit, 500m, baseDate.AddDays(1),
            // Casing intentionally different -- must match case-insensitively, mirroring
            // Pluggy's real-world inconsistent property casing.
            paymentDataJson: """{ "Payer": { "DocumentNumber": "99988877766" }, "Receiver": { "DocumentNumber": "11122233344" } }""");

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(2, flaggedCount);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedCredit = await db.Transactions.SingleAsync(t => t.Id == credit.Id);
        Assert.True(updatedDebit.IsTransfer);
        Assert.True(updatedCredit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_MalformedPaymentDataJson_NeverThrowsAndFallsThroughToFallbackSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var debit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 500m, baseDate,
            paymentDataJson: "{ this is not valid json");
        var credit = SeedTransaction(
            db, userId, accountB.Id, TransactionType.Credit, 500m, baseDate.AddDays(1),
            paymentDataJson: "also not valid json at all");

        var sut = new TransferDetectionService(db);

        var exception = await Record.ExceptionAsync(() =>
            sut.DetectTransfersForUserAsync(userId, CancellationToken.None));

        Assert.Null(exception);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedCredit = await db.Transactions.SingleAsync(t => t.Id == credit.Id);
        // Malformed paymentData on both legs never throws AND doesn't block the fallback
        // signal (Task 2) -- this pair is opposite-Type/same-|Amount|/different-accounts/
        // within-window, so it's a legitimate fallback match despite the unparseable JSON.
        Assert.True(updatedDebit.IsTransfer);
        Assert.True(updatedCredit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_UnparseablePaymentDataAndNoFallbackMatch_NeverThrowsAndStaysUnflagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // A lone Debit with malformed paymentData and no counterpart transaction at all -- proves
        // the parse failure alone never throws, independent of whether a fallback match exists.
        var debit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 500m, baseDate,
            paymentDataJson: "{ not json");

        var sut = new TransferDetectionService(db);

        var exception = await Record.ExceptionAsync(() =>
            sut.DetectTransfersForUserAsync(userId, CancellationToken.None));

        Assert.Null(exception);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        Assert.False(updatedDebit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_NoPaymentDataOppositeSignSameAmountDateWindow_FlagsBothLegsViaFallback()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var debit = SeedTransaction(db, userId, accountA.Id, TransactionType.Debit, 750m, baseDate);
        var credit = SeedTransaction(db, userId, accountB.Id, TransactionType.Credit, 750m, baseDate.AddDays(2));

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(2, flaggedCount);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedCredit = await db.Transactions.SingleAsync(t => t.Id == credit.Id);
        Assert.True(updatedDebit.IsTransfer);
        Assert.True(updatedCredit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_SingleDebitNoMatchingCredit_NeverFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // A normal external expense -- no counterpart transaction anywhere in the user's own
        // accounts.
        var debit = SeedTransaction(db, userId, accountA.Id, TransactionType.Debit, 120m, baseDate);

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(0, flaggedCount);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        Assert.False(updatedDebit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_ExternalPixShape_ReceiverDocumentMatchesNoOwnCreditAndNoFallbackMatch_NeverFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // Debit is a PIX sent to someone else -- its receiver document doesn't correspond to any
        // Credit transaction the user owns, and the one unrelated Credit present doesn't match on
        // amount or document either, so neither signal fires.
        var debit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 300m, baseDate,
            paymentDataJson: PaymentDataJson(payerDoc: "11122233344", receiverDoc: "00000000000"));
        var unrelatedCredit = SeedTransaction(
            db, userId, accountB.Id, TransactionType.Credit, 899.90m, baseDate.AddDays(1),
            paymentDataJson: PaymentDataJson(payerDoc: "55566677788", receiverDoc: "11122233344"));

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(0, flaggedCount);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedCredit = await db.Transactions.SingleAsync(t => t.Id == unrelatedCredit.Id);
        Assert.False(updatedDebit.IsTransfer);
        Assert.False(updatedCredit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_TwoDebitsSameAmountDifferentAccounts_NeverFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // Same sign (both Debit) -- never qualifies as a transfer pair regardless of matching
        // amount/date.
        var debitOne = SeedTransaction(db, userId, accountA.Id, TransactionType.Debit, 200m, baseDate);
        var debitTwo = SeedTransaction(db, userId, accountB.Id, TransactionType.Debit, 200m, baseDate.AddDays(1));

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(0, flaggedCount);
        var updatedOne = await db.Transactions.SingleAsync(t => t.Id == debitOne.Id);
        var updatedTwo = await db.Transactions.SingleAsync(t => t.Id == debitTwo.Id);
        Assert.False(updatedOne.IsTransfer);
        Assert.False(updatedTwo.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_AmbiguousTwoEquallyValidCreditCandidates_NeitherFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var accountC = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var debit = SeedTransaction(db, userId, accountA.Id, TransactionType.Debit, 400m, baseDate);
        // Two equally-valid Credit candidates -- same amount, both within the date window, on two
        // different accounts. Never guess: neither is flagged.
        var creditOne = SeedTransaction(db, userId, accountB.Id, TransactionType.Credit, 400m, baseDate.AddDays(1));
        var creditTwo = SeedTransaction(db, userId, accountC.Id, TransactionType.Credit, 400m, baseDate.AddDays(-1));

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(0, flaggedCount);
        var updatedDebit = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedCreditOne = await db.Transactions.SingleAsync(t => t.Id == creditOne.Id);
        var updatedCreditTwo = await db.Transactions.SingleAsync(t => t.Id == creditTwo.Id);
        Assert.False(updatedDebit.IsTransfer);
        Assert.False(updatedCreditOne.IsTransfer);
        Assert.False(updatedCreditTwo.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_RunTwice_SecondRunIsANoOp()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var debit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 500m, baseDate,
            paymentDataJson: PaymentDataJson(payerDoc: "11122233344", receiverDoc: "99988877766"));
        SeedTransaction(
            db, userId, accountB.Id, TransactionType.Credit, 500m, baseDate.AddDays(1),
            paymentDataJson: PaymentDataJson(payerDoc: "99988877766", receiverDoc: "11122233344"));

        var sut = new TransferDetectionService(db);

        var first = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);
        var debitAfterFirst = await db.Transactions.SingleAsync(t => t.Id == debit.Id);
        var updatedAtAfterFirst = debitAfterFirst.UpdatedAt;

        var second = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);
        var debitAfterSecond = await db.Transactions.SingleAsync(t => t.Id == debit.Id);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal(updatedAtAfterFirst, debitAfterSecond.UpdatedAt);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_ManualOverride_NeverTouchedEvenWhenPaymentDataWouldMatch()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var accountA = SeedAccount(db, userId);
        var accountB = SeedAccount(db, userId);
        var baseDate = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var overriddenDebit = SeedTransaction(
            db, userId, accountA.Id, TransactionType.Debit, 500m, baseDate,
            paymentDataJson: PaymentDataJson(payerDoc: "11122233344", receiverDoc: "99988877766"),
            isTransferManualOverride: true);
        var credit = SeedTransaction(
            db, userId, accountB.Id, TransactionType.Credit, 500m, baseDate.AddDays(1),
            paymentDataJson: PaymentDataJson(payerDoc: "99988877766", receiverDoc: "11122233344"));

        var sut = new TransferDetectionService(db);

        await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        var updatedOverriddenDebit = await db.Transactions.SingleAsync(t => t.Id == overriddenDebit.Id);
        var updatedCredit = await db.Transactions.SingleAsync(t => t.Id == credit.Id);
        Assert.False(updatedOverriddenDebit.IsTransfer);
        // The would-be counterpart also stays unflagged -- the overridden leg is excluded from
        // the candidate set entirely, so it's never offered as a match to begin with.
        Assert.False(updatedCredit.IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_SameAccountCofrinhoPairsSameDaySameAmount_FlagsReservePairsAndIgnoresRefunds()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mercadoPago = SeedAccount(db, userId);
        var day = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

        // Two distinct R$ 500 cofrinho operations on the exact same account and day:
        var retiradoCredit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 500m, day, description: "Dinheiro retirado Fatura");
        var retiradoDebit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -500m, day, description: "Dinheiro retirado");
        var reservadoCredit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 500m, day, description: "Dinheiro reservado");
        var reservadoDebit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -500m, day, description: "Dinheiro reservado Emergência");

        // Unrelated same-day same-account purchase + refund — must NOT be flagged as internal transfer.
        var compra = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -100m, day, description: "Compra Loja X");
        var estorno = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 100m, day, description: "Estorno Loja X");

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(4, flaggedCount);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == retiradoCredit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == retiradoDebit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == reservadoCredit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == reservadoDebit.Id)).IsTransfer);
        Assert.False((await db.Transactions.SingleAsync(t => t.Id == compra.Id)).IsTransfer);
        Assert.False((await db.Transactions.SingleAsync(t => t.Id == estorno.Id)).IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_SameAccountCofrinho_NotStolenByUnrelatedCrossAccountSameAmount()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mercadoPago = SeedAccount(db, userId);
        var otherBank = SeedAccount(db, userId);
        var day = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

        var retiradoCredit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 500m, day, description: "Dinheiro retirado Fatura");
        var retiradoDebit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -500m, day, description: "Dinheiro retirado");
        // Unrelated +500 credit on another bank the same day must NOT steal the Mercado Pago cofrinho debit.
        var unrelatedCredit = SeedTransaction(db, userId, otherBank.Id, TransactionType.Credit, 500m, day, description: "Pix recebido Joao");

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(2, flaggedCount);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == retiradoCredit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == retiradoDebit.Id)).IsTransfer);
        Assert.False((await db.Transactions.SingleAsync(t => t.Id == unrelatedCredit.Id)).IsTransfer);
    }

    [Fact]
    public async Task DetectTransfersForUserAsync_ReservaPorGastoPairs_FlagsBothSamePrefixAndCrossLabeledWithDinheiroReservado()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mercadoPago = SeedAccount(db, userId);
        var day = new DateTime(2026, 11, 18, 15, 0, 0, DateTimeKind.Utc);

        // Pair 1: both legs labeled "Reserva por gasto"
        var pair1Debit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -12.50m, day, description: "Reserva por gasto");
        var pair1Credit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 12.50m, day, description: "Reserva por gasto Emergência");

        // Pair 2: debit labeled "Reserva por gasto" paired with credit labeled "Dinheiro reservado"
        var pair2Debit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Debit, -7.25m, day, description: "Reserva por gasto");
        var pair2Credit = SeedTransaction(db, userId, mercadoPago.Id, TransactionType.Credit, 7.25m, day, description: "Dinheiro reservado");

        var sut = new TransferDetectionService(db);

        var flaggedCount = await sut.DetectTransfersForUserAsync(userId, CancellationToken.None);

        Assert.Equal(4, flaggedCount);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == pair1Debit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == pair1Credit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == pair2Debit.Id)).IsTransfer);
        Assert.True((await db.Transactions.SingleAsync(t => t.Id == pair2Credit.Id)).IsTransfer);
    }
}
