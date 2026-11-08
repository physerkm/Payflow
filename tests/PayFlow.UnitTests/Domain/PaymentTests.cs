using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Refunds;

namespace PayFlow.UnitTests.Domain;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Payment NewPayment(long amount = 150075) =>
        Payment.Initiate(TenantId, Guid.NewGuid(), new Money(amount, "TRY"), "pm_test_success", "mock", Now);

    [Fact]
    public void Initiate_creates_pending_payment_with_queued_attempt_and_requested_event()
    {
        var payment = NewPayment();

        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.Attempts.Count.ShouldBe(1);
        payment.CurrentAttempt!.Status.ShouldBe(PaymentAttemptStatus.Queued);
        payment.CurrentAttempt.Trigger.ShouldBe(AttemptTrigger.Initial);
        var requested = payment.DomainEvents.OfType<PaymentRequested>().ShouldHaveSingleItem();
        requested.PaymentAttemptId.ShouldBe(payment.CurrentAttempt.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Initiate_rejects_non_positive_amounts(long amount)
    {
        var ex = Should.Throw<DomainException>(() => NewPayment(amount));
        ex.Code.ShouldBe("payment_amount_invalid");
    }

    [Fact]
    public void Successful_attempt_moves_payment_to_succeeded_and_raises_event()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;

        payment.BeginAttempt(attempt.Id, Now).ShouldBeTrue();
        payment.RecordSucceeded(attempt.Id, "mock_ch_1", Now);

        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Succeeded);
        attempt.ProviderReference.ShouldBe("mock_ch_1");
        payment.DomainEvents.OfType<PaymentSucceeded>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Duplicate_success_confirmation_is_idempotent()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now);
        payment.RecordSucceeded(attempt.Id, "mock_ch_1", Now);

        Should.NotThrow(() => payment.RecordSucceeded(attempt.Id, "mock_ch_1", Now));
        payment.DomainEvents.OfType<PaymentSucceeded>().Count().ShouldBe(1);
    }

    [Fact]
    public void Succeeded_payment_can_never_become_failed()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now);
        payment.RecordSucceeded(attempt.Id, "mock_ch_1", Now);

        var ex = Should.Throw<DomainException>(() => payment.RecordDeclined(attempt.Id, null, "card_declined", Now));
        ex.Code.ShouldBe("payment_already_succeeded");
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public void Decline_fails_payment_without_scheduling_a_retry()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now);

        payment.RecordDeclined(attempt.Id, "mock_ch_2", "card_declined", Now);

        payment.Status.ShouldBe(PaymentStatus.Failed);
        payment.FailureCode.ShouldBe("card_declined");
        payment.Attempts.Count.ShouldBe(1);
        payment.DomainEvents.OfType<PaymentRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public void Timeout_is_unknown_not_failure_and_can_be_reconciled_to_success()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now);

        payment.RecordUnknown(attempt.Id, null, Now);
        payment.Status.ShouldBe(PaymentStatus.Unknown);
        payment.Attempts.Count.ShouldBe(1); // no blind second charge

        payment.RecordSucceeded(attempt.Id, "mock_ch_3", Now.AddSeconds(30));
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public void Transient_errors_retry_with_new_attempts_until_the_run_limit_then_fail()
    {
        var payment = NewPayment();

        for (var i = 1; i < Payment.MaxAutomaticAttemptsPerRun; i++)
        {
            var attempt = payment.CurrentAttempt!;
            payment.BeginAttempt(attempt.Id, Now).ShouldBeTrue();
            var next = payment.RecordTransientFailure(attempt.Id, "provider_unavailable", Now.AddSeconds(i), Now);
            next.ShouldNotBeNull();
            next.Trigger.ShouldBe(AttemptTrigger.AutomaticRetry);
            next.ScheduledAt.ShouldBe(Now.AddSeconds(i));
        }

        var last = payment.CurrentAttempt!;
        payment.BeginAttempt(last.Id, Now);
        payment.RecordTransientFailure(last.Id, "provider_unavailable", Now, Now).ShouldBeNull();

        payment.Status.ShouldBe(PaymentStatus.Failed);
        payment.FailureCode.ShouldBe("provider_unavailable");
        payment.Attempts.Count.ShouldBe(Payment.MaxAutomaticAttemptsPerRun);
        payment.Attempts.ShouldAllBe(a => a.Status == PaymentAttemptStatus.Failed);
    }

    [Fact]
    public void Manual_retry_opens_a_new_attempt_and_a_new_automatic_retry_budget()
    {
        var payment = NewPayment();
        var first = payment.CurrentAttempt!;
        payment.BeginAttempt(first.Id, Now);
        payment.RecordDeclined(first.Id, null, "card_declined", Now);

        var retry = payment.Retry("pm_test_success", Now);

        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.FailureCode.ShouldBeNull();
        retry.AttemptNumber.ShouldBe(2);
        retry.Trigger.ShouldBe(AttemptTrigger.ManualRetry);
        payment.BeginAttempt(retry.Id, Now);
        payment.RecordTransientFailure(retry.Id, "provider_unavailable", Now, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Retry_is_only_allowed_for_failed_payments()
    {
        var payment = NewPayment();
        Should.Throw<DomainException>(() => payment.Retry(null, Now)).Code.ShouldBe("payment_not_retryable");
    }

    [Fact]
    public void Cancel_is_allowed_only_before_the_attempt_was_sent_to_the_provider()
    {
        var cancellable = NewPayment();
        cancellable.Cancel(Now);
        cancellable.Status.ShouldBe(PaymentStatus.Cancelled);
        cancellable.CurrentAttempt!.Status.ShouldBe(PaymentAttemptStatus.Cancelled);
        cancellable.BeginAttempt(cancellable.CurrentAttempt.Id, Now).ShouldBeFalse();

        var processing = NewPayment();
        processing.BeginAttempt(processing.CurrentAttempt!.Id, Now);
        Should.Throw<DomainException>(() => processing.Cancel(Now)).Code.ShouldBe("payment_not_cancellable");
    }

    [Fact]
    public void Redelivered_message_for_processing_attempt_is_resent_with_the_same_attempt()
    {
        var payment = NewPayment();
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now).ShouldBeTrue();

        payment.BeginAttempt(attempt.Id, Now).ShouldBeTrue();
        payment.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public void Refund_reservations_never_exceed_the_captured_amount()
    {
        var payment = SucceededPayment(10_000);

        payment.ReserveRefund(6_000, Now);
        payment.RefundableAmountMinor.ShouldBe(4_000);

        var ex = Should.Throw<DomainException>(() => payment.ReserveRefund(4_001, Now));
        ex.Code.ShouldBe("refund_amount_exceeds_refundable");
        payment.PendingRefundAmountMinor.ShouldBe(6_000);
    }

    [Fact]
    public void Completed_refunds_move_payment_to_partially_refunded_then_refunded()
    {
        var payment = SucceededPayment(10_000);
        var first = Refund.Request(payment, 4_000, "partial", Guid.NewGuid(), Now);
        first.MarkSucceeded(payment, "mock_re_1", Now);
        payment.Status.ShouldBe(PaymentStatus.PartiallyRefunded);

        var second = Refund.Request(payment, 6_000, null, Guid.NewGuid(), Now);
        second.MarkSucceeded(payment, "mock_re_2", Now);
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.RefundedAmountMinor.ShouldBe(10_000);
        payment.RefundableAmountMinor.ShouldBe(0);
    }

    [Fact]
    public void Failed_refund_releases_its_reservation()
    {
        var payment = SucceededPayment(10_000);
        var refund = Refund.Request(payment, 10_000, null, Guid.NewGuid(), Now);
        payment.RefundableAmountMinor.ShouldBe(0);

        refund.MarkFailed(payment, "refund_declined", Now);

        refund.Status.ShouldBe(RefundStatus.Failed);
        payment.RefundableAmountMinor.ShouldBe(10_000);
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public void Refunds_require_a_succeeded_payment()
    {
        var pending = NewPayment(10_000);
        Should.Throw<DomainException>(() => Refund.Request(pending, 100, null, Guid.NewGuid(), Now)).Code.ShouldBe("payment_not_refundable");
    }

    private static Payment SucceededPayment(long amount)
    {
        var payment = NewPayment(amount);
        var attempt = payment.CurrentAttempt!;
        payment.BeginAttempt(attempt.Id, Now);
        payment.RecordSucceeded(attempt.Id, "mock_ch_x", Now);
        return payment;
    }
}
