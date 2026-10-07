using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Tests.Pipeline;

public sealed class EditQuestionContractTests
{
    private static NotificationDeliveryRequest Request(NotificationKind kind = NotificationKind.EditPrompt, int version = 2)
    {
        var input = NotificationDeliveryInput.Capture(new Notification { Id = 42, Kind = kind, Text = "Question <not HTML>" },
            new NotificationTarget { BotId = 123456, ChatId = 123456 }, version: version);
        var json = JsonSerializer.Serialize(input, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
        var hash = CanonicalJson.Hash(json);
        return new NotificationDeliveryRequest
        {
            Id = Guid.NewGuid(), NotificationId = 42, InputJson = json, InputHash = hash,
            AdmissionJson = input.Version == 1 ? null : json, AdmissionHash = input.Version == 1 ? null : hash,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Edit_question_never_uses_html_fallback_or_strips_literal_text(bool plainText)
    {
        var request = Request();
        var plan = new NotificationPlan { Version = 2, Effects = [new NotificationEffect
            { Kind = NotificationEffectKind.EditPrompt, Html = false, Text = "Question <not HTML>" }] };
        request.PlanJson = JsonSerializer.Serialize(plan, NotificationDeliveryJsonContext.Default.NotificationPlan);
        request.PlanHash = CanonicalJson.Hash(request.PlanJson);
        request.PlainText = plainText;
        if (plainText) Assert.Throws<UnsupportedNotificationDeliveryException>(() => NotificationDeliveryInputs.Effect(request));
        else Assert.Equal("Question <not HTML>", NotificationDeliveryInputs.Effect(request).Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Ordinary_notification_protocols_keep_their_existing_plan_version(int version)
    {
        var request = Request(NotificationKind.Info, version);
        var plan = new NotificationPlan { Effects = [new NotificationEffect { Kind = NotificationEffectKind.Text, Text = "Question <not HTML>" }] };
        request.PlanJson = JsonSerializer.Serialize(plan, NotificationDeliveryJsonContext.Default.NotificationPlan);
        request.PlanHash = CanonicalJson.Hash(request.PlanJson);
        Assert.Equal(version, NotificationDeliveryInputs.Input(request).Version);
        Assert.Equal(1, NotificationDeliveryInputs.Plan(request).Version);
        plan.Version = 2;
        request.PlanJson = JsonSerializer.Serialize(plan, NotificationDeliveryJsonContext.Default.NotificationPlan);
        request.PlanHash = CanonicalJson.Hash(request.PlanJson);
        Assert.Throws<UnsupportedNotificationDeliveryException>(() => NotificationDeliveryInputs.Plan(request));
    }
}
