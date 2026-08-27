using FluentAssertions;
using KartPaymentService.Domain.Payments;
using Xunit;

namespace KartPaymentService.UnitTests.Domain;

public sealed class PaymentIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CurrencyCode Usd = new("USD");
    private static readonly GatewayTransactionId Txn1 = new("txn_1");
    private static readonly ChargebackId Cb1 = new("cb_1");

    private static Money Amount(decimal amount) => new(amount, Usd);

    private static PaymentIntent CreatePendingIntent(decimal amount = 100m) =>
        PaymentIntent.Create(Guid.NewGuid(), new OrderId("order-1"), new GatewayToken("tok_good"), Amount(amount), "system:test", Now);

    [Fact]
    public void Create_SetsInitialStatusToPending()
    {
        var intent = CreatePendingIntent();

        intent.Status.Should().Be(PaymentIntentStatus.Pending);
        intent.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void MarkCompleted_FromPending_TransitionsAndRaisesPaymentCompletedEvent()
    {
        var intent = CreatePendingIntent();

        var result = intent.MarkCompleted(Txn1, "system:test", Now);

        result.IsSuccess.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.Completed);
        intent.TxnId.Should().Be(Txn1);
        intent.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PaymentCompletedDomainEvent>();
    }

    [Fact]
    public void MarkCompleted_WhenAlreadyCompleted_IsIdempotentNoOp()
    {
        var intent = CreatePendingIntent();
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.ClearDomainEvents();

        var result = intent.MarkCompleted(Txn1, "system:test", Now);

        result.IsSuccess.Should().BeTrue();
        intent.DomainEvents.Should().BeEmpty("a duplicate webhook delivery must not re-publish PaymentCompleted");
    }

    [Fact]
    public void MarkCompleted_FromFailed_IsRejected()
    {
        var intent = CreatePendingIntent();
        intent.MarkFailed("declined", "system:test", Now);

        var result = intent.MarkCompleted(Txn1, "system:test", Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public void MarkFailed_FromPending_TransitionsAndRaisesPaymentFailedEvent()
    {
        var intent = CreatePendingIntent();

        var result = intent.MarkFailed("card_declined", "system:test", Now);

        result.IsSuccess.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.Failed);
        intent.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PaymentFailedDomainEvent>();
    }

    [Fact]
    public void RequestRefund_WithinCapturedAmount_Succeeds()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);

        var result = intent.RequestRefund(Amount(40m), "agent-1", Now);

        result.IsSuccess.Should().BeTrue();
        intent.Refunds.Should().ContainSingle();
    }

    [Fact]
    public void RequestRefund_ExceedingCapturedAmount_ReturnsConflict()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.RequestRefund(Amount(60m), "agent-1", Now);

        var result = intent.RequestRefund(Amount(50m), "agent-1", Now); // 60 + 50 > 100

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        intent.Refunds.Should().ContainSingle("the over-ceiling attempt must not be recorded");
    }

    [Fact]
    public void RequestRefund_ExactlyAtCapturedAmount_Succeeds()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.RequestRefund(Amount(60m), "agent-1", Now);

        var result = intent.RequestRefund(Amount(40m), "agent-1", Now); // exactly 100

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RequestRefund_AgainstPendingIntent_IsRejected()
    {
        var intent = CreatePendingIntent(100m); // never completed

        var result = intent.RequestRefund(Amount(10m), "agent-1", Now);

        result.IsFailure.Should().BeTrue("Saga Compensation Gating: a refund against a non-terminal intent must never fire unconditionally");
    }

    [Fact]
    public void RequestRefund_AgainstDisputedIntent_IsRejected()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.MarkDisputed(Cb1, Amount(100m), "fraud", Now, "system:test", Now);

        var result = intent.RequestRefund(Amount(10m), "agent-1", Now);

        result.IsFailure.Should().BeTrue("ADR-0012: a disputed intent rejects any new refund attempt");
    }

    [Fact]
    public void RequestRefund_WithMismatchedCurrency_ReturnsConflict()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);

        var result = intent.RequestRefund(new Money(10m, new CurrencyCode("EUR")), "agent-1", Now);

        result.IsFailure.Should().BeTrue("a refund must be denominated in the same currency as the captured amount");
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public void MarkRefundSucceeded_RaisesRefundIssuedEvent_ExactlyOnce()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        var refund = intent.RequestRefund(Amount(30m), "agent-1", Now).Value;
        intent.ClearDomainEvents();

        intent.MarkRefundSucceeded(refund.Id, Now);
        intent.MarkRefundSucceeded(refund.Id, Now); // duplicate webhook delivery

        intent.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<RefundIssuedDomainEvent>();
        intent.TotalRefunded.Should().Be(Amount(30m));
    }

    [Fact]
    public void MarkDisputed_FromCompleted_TransitionsAndRaisesChargebackReceivedEvent()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.ClearDomainEvents();

        var result = intent.MarkDisputed(Cb1, Amount(100m), "fraud", Now, "system:webhook", Now);

        result.IsSuccess.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.Disputed);
        intent.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<ChargebackReceivedDomainEvent>();
    }

    [Fact]
    public void MarkDisputed_FromPending_IsRejected()
    {
        var intent = CreatePendingIntent(100m); // never completed

        var result = intent.MarkDisputed(Cb1, Amount(100m), "fraud", Now, "system:webhook", Now);

        result.IsFailure.Should().BeTrue("a charge that never completed cannot be charged back");
    }

    [Fact]
    public void MarkDisputed_WhenAlreadyDisputed_IsIdempotentNoOp()
    {
        var intent = CreatePendingIntent(100m);
        intent.MarkCompleted(Txn1, "system:test", Now);
        intent.MarkDisputed(Cb1, Amount(100m), "fraud", Now, "system:webhook", Now);
        intent.ClearDomainEvents();

        var result = intent.MarkDisputed(Cb1, Amount(100m), "fraud", Now, "system:webhook", Now);

        result.IsSuccess.Should().BeTrue();
        intent.DomainEvents.Should().BeEmpty("a duplicate chargeback notification must not re-publish ChargebackReceived");
    }
}
