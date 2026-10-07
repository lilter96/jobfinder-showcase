namespace JobFinder.Core.Messaging;

public static class DurableQueues
{
    public const string Discovery = "discovery";
    public const string Scoring = "scoring";
    public const string HiringPreparation = "hiring_prepare";
    public const string HiringDelivery = "hiring_deliver";
    public const string LetterDrafting = "hiring_letter_draft";
    public const string LetterReviewing = "hiring_letter_review";
    public const string HiringReplies = "hiring_replies";
    public const string Preparation = "preparation";
    public const string Engagement = "engagement";
    public const string Notifications = "notifications";
    public const string BotControls = "bot_controls";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        Discovery, Scoring, HiringPreparation, HiringDelivery, LetterDrafting, LetterReviewing, HiringReplies, Preparation, Engagement, Notifications, BotControls,
    });
}
