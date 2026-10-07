using JasperFx;
using JobFinder.Core.Messaging;
using JobFinder.Pipeline.Controls;
using JobFinder.Data.Storage;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Persistence;
using Wolverine.Runtime.Agents;

namespace JobFinder.Pipeline.Messaging;

public static class JobFinderMessaging
{
    public static void AddJobFinderMessaging(this IHostApplicationBuilder builder, bool consumer)
    {
        var connection = builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()?.ConnectionString
            ?? throw new InvalidOperationException("Database:ConnectionString is required for durable messaging");
        if (!consumer)
            builder.Services.AddSingleton<IAgentFamily>(provider => provider.GetRequiredService<MessageStoreCollection>());
        builder.UseWolverine(options =>
        {
            options.UsePostgresqlPersistenceAndTransport(connection, WolverineStorage.MessageSchema, WolverineStorage.QueueSchema);
            options.UseEntityFrameworkCoreTransactions();
            options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            options.Discovery.DisableConventionalDiscovery();
            options.RegisterMessageType(typeof(ReadHhLink));
            options.RegisterMessageType(typeof(ProcessPost));
            options.RegisterMessageType(typeof(ProcessChannelMembership));
            options.RegisterMessageType(typeof(PrepareInterview));
            options.RegisterMessageType(typeof(DeliverNotification));
            options.RegisterMessageType(typeof(BindNotificationTarget));
            options.RegisterMessageType(typeof(PrepareApplicationDelivery));
            options.RegisterMessageType(typeof(DispatchApplicationDelivery));
            options.RegisterMessageType(typeof(RememberApplicationContact));
            options.RegisterMessageType(typeof(RetryApplicationDelivery));
            options.RegisterMessageType(typeof(LinkHhContacts));
            options.RegisterMessageType(typeof(DraftResume));
            options.RegisterMessageType(typeof(PrintResume));
            options.RegisterMessageType(typeof(ReviewResume));
            options.RegisterMessageType(typeof(FinishResume));
            options.RegisterMessageType(typeof(PrepareEmployerMessage));
            options.RegisterMessageType(typeof(DispatchEmployerMessage));
            options.RegisterMessageType(typeof(ScoreVacancy));
            options.RegisterMessageType(typeof(CaptureVacancyAssessment));
            options.RegisterMessageType(typeof(PrepareVacancyLetter));
            options.RegisterMessageType(typeof(FinishVacancyAssessment));
            options.RegisterMessageType(typeof(DraftLetterLane));
            options.RegisterMessageType(typeof(ReviewLetterLane));
            options.RegisterMessageType(typeof(ChooseLetterRound));
            options.RegisterMessageType(typeof(ProcessReply));
            options.RegisterMessageType(typeof(ReadLinkedInPeople));
            options.RegisterMessageType(typeof(AnalyzeOffer));
            options.RegisterMessageType(typeof(PublishOffer));
            options.RegisterMessageType(typeof(DraftStory));
            options.RegisterMessageType(typeof(PublishStory));
            options.RegisterMessageType(typeof(ProcessBotControl));
            options.RegisterMessageType(typeof(QuarantineLegacyNotification));
            options.RegisterMessageType(typeof(DraftComment));
            options.RegisterMessageType(typeof(CheckComment));
            options.RegisterMessageType(typeof(PrepareCommentSource));
            options.RegisterMessageType(typeof(PrepareCommentDelivery));
            options.RegisterMessageType(typeof(DispatchCommentDelivery));
            options.UseSystemTextJsonForSerialization(serializer =>
            {
                serializer.TypeInfoResolverChain.Insert(0, DiscoveryMessageJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, PostProcessingJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, ChannelMembershipJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, InterviewPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, NotificationDeliveryJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, ResumePreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, EmployerMessageJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, VacancyAssessmentJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, LetterPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, ReplyProcessingJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, LinkedInPeopleJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, OfferPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, StoryPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, MockPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, HrPlaybookJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, RejectionReviewJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, WeeklyReportJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, BotControlJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, ApplicationDeliveryJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, CommentPreparationJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, CommentSourceJsonContext.Default);
                serializer.TypeInfoResolverChain.Insert(0, CommentDeliveryJsonContext.Default);
            });
            options.PublishMessage<ReadHhLink>().ToPostgresqlQueue(DurableQueues.Discovery);
            options.PublishMessage<ProcessChannelMembership>().ToPostgresqlQueue(DurableQueues.Discovery);
            options.PublishMessage<ProcessPost>().ToPostgresqlQueue(DurableQueues.Discovery);
            options.PublishMessage<PrepareInterview>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<DeliverNotification>().ToPostgresqlQueue(DurableQueues.Notifications);
            options.PublishMessage<BindNotificationTarget>().ToPostgresqlQueue(DurableQueues.Notifications);
            options.PublishMessage<QuarantineLegacyNotification>().ToPostgresqlQueue(DurableQueues.Notifications);
            options.PublishMessage<ProcessBotControl>().ToPostgresqlQueue(DurableQueues.BotControls);
            options.PublishMessage<LinkHhContacts>().ToPostgresqlQueue(DurableQueues.Discovery);
            options.PublishMessage<DraftResume>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<PrintResume>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<ReviewResume>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<FinishResume>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<PrepareEmployerMessage>().ToPostgresqlQueue(DurableQueues.HiringDelivery);
            options.PublishMessage<PrepareApplicationDelivery>().ToPostgresqlQueue(DurableQueues.HiringDelivery);
            options.PublishMessage<DispatchApplicationDelivery>().ToPostgresqlQueue(DurableQueues.HiringDelivery);
            options.PublishMessage<RememberApplicationContact>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<RetryApplicationDelivery>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<DispatchEmployerMessage>().ToPostgresqlQueue(DurableQueues.HiringDelivery);
            options.PublishMessage<ScoreVacancy>().ToPostgresqlQueue(DurableQueues.Scoring);
            options.PublishMessage<CaptureVacancyAssessment>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PrepareVacancyLetter>().ToPostgresqlQueue(DurableQueues.Scoring);
            options.PublishMessage<FinishVacancyAssessment>().ToPostgresqlQueue(DurableQueues.Scoring);
            options.PublishMessage<ScoreVacancyPreview>().ToPostgresqlQueue(DurableQueues.Scoring);
            options.PublishMessage<FinishVacancyPreview>().ToPostgresqlQueue(DurableQueues.Scoring);
            options.PublishMessage<DraftLetterLane>().ToPostgresqlQueue(DurableQueues.LetterDrafting);
            options.PublishMessage<ReviewLetterLane>().ToPostgresqlQueue(DurableQueues.LetterReviewing);
            options.PublishMessage<ChooseLetterRound>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<ProcessReply>().ToPostgresqlQueue(DurableQueues.HiringReplies);
            options.PublishMessage<ReadLinkedInPeople>().ToPostgresqlQueue(DurableQueues.HiringReplies);
            options.PublishMessage<AnalyzeOffer>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<PublishOffer>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<DraftStory>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PublishStory>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PrepareMock>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<BuildHrPlaybook>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PublishHrPlaybook>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<AnalyzeRejectionReview>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PublishRejectionReview>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PrepareWeeklyReport>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PublishWeeklyReport>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PublishMock>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<DraftComment>().ToPostgresqlQueue(DurableQueues.Engagement);
            options.PublishMessage<CheckComment>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PrepareCommentSource>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<PrepareCommentDelivery>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<DispatchCommentDelivery>().ToPostgresqlQueue(DurableQueues.Engagement);
            options.Durability.DurabilityAgentEnabled = consumer;
            options.Policies.OnException<UnsupportedHhLinkCommandException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedResumeCommandException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedEmployerMessageException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedApplicationDeliveryException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedVacancyAssessmentException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedLetterPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedReplyProcessingException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedLinkedInPeopleException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedOfferPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<OfferPreparationCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedStoryPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<StoryPreparationCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedMockPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<MockPreparationCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedHrPlaybookException>().MoveToErrorQueue();
            options.Policies.OnException<HrPlaybookCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedRejectionReviewException>().MoveToErrorQueue();
            options.Policies.OnException<RejectionReviewCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedWeeklyReportException>().MoveToErrorQueue();
            options.Policies.OnException<WeeklyReportCheckpointException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedCommentPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedCommentSourceException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedCommentDeliveryException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedPostProcessingException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedChannelMembershipException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedInterviewPreparationException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedNotificationDeliveryException>().MoveToErrorQueue();
            options.Policies.OnException<UnsupportedBotControlException>().MoveToErrorQueue();
options.Policies.OnException<Exception>(error => error is not (UnsupportedHhLinkCommandException or UnsupportedResumeCommandException or UnsupportedEmployerMessageException or UnsupportedVacancyAssessmentException or UnsupportedLetterPreparationException or UnsupportedReplyProcessingException or UnsupportedLinkedInPeopleException or UnsupportedOfferPreparationException or OfferPreparationCheckpointException or UnsupportedStoryPreparationException or StoryPreparationCheckpointException or UnsupportedMockPreparationException or MockPreparationCheckpointException or UnsupportedHrPlaybookException or HrPlaybookCheckpointException or UnsupportedRejectionReviewException or RejectionReviewCheckpointException or UnsupportedWeeklyReportException or WeeklyReportCheckpointException or UnsupportedBotControlException or UnsupportedApplicationDeliveryException or UnsupportedCommentPreparationException or UnsupportedCommentSourceException or UnsupportedCommentDeliveryException or UnsupportedPostProcessingException or UnsupportedChannelMembershipException or UnsupportedInterviewPreparationException or UnsupportedNotificationDeliveryException))
                .ScheduleRetry(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
            if (consumer)
            {
                options.Discovery.IncludeType(typeof(HhLinkReadHandler));
                options.Discovery.IncludeType(typeof(PostProcessingHandler));
                options.Discovery.IncludeType(typeof(ChannelMembershipHandler));
                options.Discovery.IncludeType(typeof(InterviewPreparationHandler));
                options.Discovery.IncludeType(typeof(NotificationDeliveryHandler));
                options.Discovery.IncludeType(typeof(LegacyNotificationHandler));
                options.ListenToPostgresqlQueue(DurableQueues.Notifications).Sequential();
                options.Discovery.IncludeType(typeof(HhLinkContactsHandler));
                options.Discovery.IncludeType(typeof(ResumePreparationHandler));
                options.Discovery.IncludeType(typeof(EmployerMessageHandler));
                options.Discovery.IncludeType(typeof(ApplicationDeliveryHandler));
                options.Discovery.IncludeType(typeof(VacancyAssessmentHandler));
                options.Discovery.IncludeType(typeof(VacancyCaptureHandler));
                options.Discovery.IncludeType(typeof(LetterPreparationHandler));
                options.Discovery.IncludeType(typeof(ReplyProcessingHandler));
                options.Discovery.IncludeType(typeof(LinkedInPeopleHandler));
                options.Discovery.IncludeType(typeof(OfferPreparationHandler));
                options.Discovery.IncludeType(typeof(StoryPreparationHandler));
                options.Discovery.IncludeType(typeof(MockPreparationHandler));
                options.Discovery.IncludeType(typeof(HrPlaybookHandler));
                options.Discovery.IncludeType(typeof(RejectionReviewHandler));
                options.Discovery.IncludeType(typeof(WeeklyReportHandler));
                options.Discovery.IncludeType(typeof(BotControlHandler));
                options.Discovery.IncludeType(typeof(CommentPreparationHandler));
                options.Discovery.IncludeType(typeof(CommentSourceHandler));
                options.Discovery.IncludeType(typeof(CommentDeliveryHandler));
                options.ListenToPostgresqlQueue(DurableQueues.Engagement).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(2).MaximumParallelMessages(2);
                options.ListenToPostgresqlQueue(DurableQueues.Discovery).Sequential();
                options.ListenToPostgresqlQueue(DurableQueues.BotControls).Sequential();
                options.ListenToPostgresqlQueue(DurableQueues.HiringPreparation).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(2).MaximumParallelMessages(2);
                options.ListenToPostgresqlQueue(DurableQueues.HiringDelivery).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(2).MaximumParallelMessages(2);
                options.ListenToPostgresqlQueue(DurableQueues.Scoring).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(2).MaximumParallelMessages(2);
                options.ListenToPostgresqlQueue(DurableQueues.LetterDrafting).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(4).MaximumParallelMessages(4);
                options.ListenToPostgresqlQueue(DurableQueues.LetterReviewing).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(4).MaximumParallelMessages(4);
                options.ListenToPostgresqlQueue(DurableQueues.HiringReplies).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(2).MaximumParallelMessages(2);
                options.ListenToPostgresqlQueue(DurableQueues.Preparation).PollingInterval(TimeSpan.FromSeconds(1))
                    .MaximumMessagesToReceive(4).MaximumParallelMessages(4);
            }
        });
    }
}
