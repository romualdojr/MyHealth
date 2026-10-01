using MyHealth.Domain.Notifications;
using MyHealth.Domain.Privacy;

namespace MyHealth.Domain.UnitTests.Privacy;

public sealed class PrivacyRequestTests
{
    [Fact]
    public void An_export_can_be_downloaded_by_its_owner_until_it_expires()
    {
        var userId = Guid.CreateVersion7();
        var export = PrivacyRequest.RequestExport(userId, TestData.Now);

        export.RecordDelivery(Guid.CreateVersion7(), TestData.Now).Error.ShouldBe(PrivacyErrors.NotFound);
        export.RecordDelivery(userId, TestData.Now.AddDays(6)).IsSuccess.ShouldBeTrue();
        export.RecordDelivery(userId, TestData.Now.AddDays(7)).Error.ShouldBe(PrivacyErrors.ExportExpired);
        export.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public void Operators_move_requests_along_once_and_must_explain_a_rejection()
    {
        var request = PrivacyRequest.Submit(Guid.CreateVersion7(), PrivacyRequestType.Correction, "Meu peso está errado", TestData.Now).Value;
        var operatorId = Guid.CreateVersion7();

        request.Reject(operatorId, "", TestData.Now).Error.ShouldBe(PrivacyErrors.ReasonRequired);
        request.Start(operatorId, TestData.Now).IsSuccess.ShouldBeTrue();
        request.Complete(operatorId, "Corrigido", TestData.Now).IsSuccess.ShouldBeTrue();
        request.Complete(operatorId, "De novo", TestData.Now).Error.ShouldBe(PrivacyErrors.NotOpen);

        request.Status.ShouldBe(PrivacyRequestStatus.Completed);
        request.DomainEvents.OfType<PrivacyRequestUpdated>().Count().ShouldBe(2);
    }

    [Fact]
    public void Exports_are_not_submitted_as_operator_requests_and_details_are_required()
    {
        PrivacyRequest.Submit(Guid.CreateVersion7(), PrivacyRequestType.DataExport, "x", TestData.Now).Error.ShouldBe(PrivacyErrors.UseExportEndpoint);
        PrivacyRequest.Submit(Guid.CreateVersion7(), PrivacyRequestType.Deletion, " ", TestData.Now).Error.ShouldBe(PrivacyErrors.DetailsRequired);
        PrivacyRequest.RequestExport(Guid.CreateVersion7(), TestData.Now).Complete(Guid.CreateVersion7(), null, TestData.Now).Error.ShouldBe(PrivacyErrors.NotOpen);
    }

    [Fact]
    public void Retention_policies_need_a_known_record_type_a_period_and_a_legal_basis()
    {
        RetentionPolicy.Define("Invoices", 12, "x", null, Guid.CreateVersion7(), TestData.Now).Error.Code.ShouldBe("Retention.UnknownRecordType");
        RetentionPolicy.Define("FoodDiaryEntry", 0, "x", null, Guid.CreateVersion7(), TestData.Now).Error.ShouldBe(RetentionErrors.InvalidPeriod);
        RetentionPolicy.Define("FoodDiaryEntry", 24, " ", null, Guid.CreateVersion7(), TestData.Now).Error.ShouldBe(RetentionErrors.LegalBasisRequired);
        RetentionPolicy.Define("FoodDiaryEntry", 24, "Consentimento", null, Guid.CreateVersion7(), TestData.Now).Value.RetentionMonths.ShouldBe(24);
    }
}

public sealed class NotificationDeliveryTests
{
    private static Notification New() =>
        Notification.ForClient(Guid.CreateVersion7(), TestData.TenantId, NotificationType.MealPlanPublished, "MealPlan", Guid.CreateVersion7(), TestData.Now);

    [Fact]
    public void A_new_notification_is_due_for_delivery_immediately()
    {
        var notification = New();

        (notification.DeliveryStatus, notification.NextDeliveryAttemptAt).ShouldBe((NotificationDeliveryStatus.Pending, TestData.Now));
    }

    [Fact]
    public void Failures_back_off_exponentially_and_give_up_after_the_last_attempt()
    {
        var notification = New();

        notification.RecordDeliveryFailure("timeout", TestData.Now);
        notification.NextDeliveryAttemptAt.ShouldBe(TestData.Now.AddMinutes(1));
        notification.RecordDeliveryFailure("timeout", TestData.Now);
        notification.NextDeliveryAttemptAt.ShouldBe(TestData.Now.AddMinutes(4));

        for (var i = 2; i < Notification.MaxDeliveryAttempts; i++)
        {
            notification.RecordDeliveryFailure("timeout", TestData.Now);
        }

        (notification.DeliveryStatus, notification.NextDeliveryAttemptAt, notification.LastDeliveryError).ShouldBe((NotificationDeliveryStatus.Failed, null, "timeout"));
    }

    [Fact]
    public void Delivery_settles_the_outbox_entry()
    {
        var notification = New();
        notification.RecordDeliveryFailure("timeout", TestData.Now);

        notification.MarkDelivered(TestData.Now.AddMinutes(2));

        (notification.DeliveryStatus, notification.DeliveryAttempts, notification.NextDeliveryAttemptAt, notification.LastDeliveryError)
            .ShouldBe((NotificationDeliveryStatus.Delivered, 2, null, null));
    }
}
