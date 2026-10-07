using System.Reflection;
using System.Reflection.Emit;
using JobFinder.App.Cli;
using JobFinder.Bot;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Workers;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Jobs;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Tests.Pipeline;

public sealed class ModuleOwnershipTests
{
    [Fact]
    public void Telegram_membership_effects_have_one_gateway_and_source_marker_has_one_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramUserClient)
            && call.Target.Name is "JoinChannelAsync" or "MuteChannelAsync" or "ArchiveChannelAsync" or "AddChannelToFolderAsync", typeof(ChannelMembershipGateway));
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramChannel) && call.Target.Name == "set_MembershipRequestId", typeof(ChannelMembershipRequests));
        AssertOwners(call => call.Target.DeclaringType == typeof(IChannelMembershipGateway) && call.Target.Name == "ApplyAsync", typeof(ChannelMembershipWorkflow));
        Assert.DoesNotContain(typeof(TelegramUserClient).GetMethods(), method => method.Name == "JoinAndTidyAsync");
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ChannelMembershipLedger)), call =>
            call.Target.DeclaringType == typeof(TelegramUserClient) || call.Target.DeclaringType == typeof(IChannelMembershipGateway));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(JobFinder.Pipeline.Telegram.TelegramService)), call =>
            call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync"
            || call.Target.DeclaringType == typeof(TelegramChannel) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void Channel_decisions_have_one_revision_writer_and_ui_bot_admission_uses_captured_controls()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramChannel) && call.Target.Name == "set_DecisionRevision", typeof(ChannelDecisions));
        var page = typeof(JobFinder.App.Components.Pages.Channels);
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == page), call =>
            call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(ChannelDecisions) && call.Target.Name is "SetAsync" or "ApproveAsync" or "ApproveBatchAsync", typeof(ApprovalService));
        Assert.Contains(ProductionCalls.Value, call => call.CallerOwner == typeof(BotService)
            && call.Target.DeclaringType == typeof(ApprovalService) && call.Target.Name == "ApproveChannelBatchAsync");
        Assert.DoesNotContain(typeof(ApprovalService).GetMethods(), method => method.Name == "ApproveChannelsAsync" && method.GetParameters()[0].ParameterType == typeof(IEnumerable<long>));
    }

    [Fact]
    public void Scrape_transitions_have_one_request_adapter_and_never_use_generic_settings_writes()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinder.Integrations.Http.IScrapeRequestLedger)
            && call.Target.Name is "ReserveAsync" or "BlockAsync" or "RecoverAsync", typeof(JobFinder.Integrations.Http.PoliteHttp));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ScrapeStateStore)), call =>
            call.Target.DeclaringType == typeof(RuntimeSettings));
    }

    [Fact]
    public void Automation_controls_publish_captured_versions_and_only_the_receipt_owner_records_application_blocks()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(ApplicationBlocks) && call.Target.Name == "RecordJoinedAsync", typeof(DeliveryLedger));
        var pages = new[] { typeof(JobFinder.App.Components.Pages.Overview), typeof(JobFinder.App.Components.Pages.Facts), typeof(JobFinder.App.Components.Pages.SettingsPage) };
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => pages.Contains(call.CallerOwner)), call =>
            call.Target.DeclaringType == typeof(RuntimeSettings) && call.Target.Name is "SetAsync" or "RemoveAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(AutomationPolicyStore) && call.Target.Name == "PublishJoinedAsync", typeof(ApplicationAutomationControls));
    }

    [Fact]
    public void Weekly_report_has_one_paid_adapter_and_the_scheduler_and_dashboard_only_admit_source_owned_work()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.HhAdviceAsync), typeof(WeeklyReportSteps));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(WeeklyReportJob)), call =>
            call.Target.Name is "SaveChangesAsync" or "SetAsync" or "SendAsync" or "HhAdviceAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(WeeklyReportSources) && call.Target.Name == nameof(WeeklyReportSources.PublishJoinedAsync), typeof(WeeklyReportWorkflow));
        var page = typeof(JobFinder.App.Components.Pages.Analytics);
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == page), call => call.Target.DeclaringType == typeof(RuntimeSettings) && call.Target.Name == "SetAsync");
        Assert.Contains(ProductionCalls.Value, call => call.CallerOwner == page && call.Target.DeclaringType == typeof(WeeklyReportJob) && call.Target.Name == "RequestAsync");
    }

    [Fact]
    public void Rejection_review_has_one_paid_owner_and_no_inline_publication_in_its_facade()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.RejectionReviewAsync), typeof(RejectionReviewSteps));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(RejectionReviewService)), call =>
            call.Target.Name is "SaveChangesAsync" or "SetAsync" or "SendAsync" or "RejectionReviewAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(RejectionReviewSources) && call.Target.Name == nameof(RejectionReviewSources.PublishJoinedAsync), typeof(RejectionReviewWorkflow));
    }

    [Fact]
    public void Hr_playbook_has_one_paid_executor_and_facades_only_admit_native_work()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.HrAnswerAsync), typeof(HrPlaybookPreparationSteps));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(HrPlaybookService) || call.CallerOwner == typeof(HrPlaybookJob)), call =>
            call.Target.Name is "SaveChangesAsync" or "SetAsync" or "SendAsync" or "HrAnswerAsync" || call.Target.DeclaringType == typeof(GeneratedAnswers));
        AssertOwners(call => call.Target.DeclaringType == typeof(HrPlaybookSources) && call.Target.Name == nameof(HrPlaybookSources.PublishJoinedAsync), typeof(HrPlaybookWorkflow));
        Assert.DoesNotContain(typeof(HrPlaybookService).GetMethods(), method => method.Name == "SavePositionAsync" && method.GetParameters()[0].ParameterType == typeof(HrQuestion));
    }

    [Fact]
    public void Career_dialogue_router_cannot_overwrite_or_clear_source_state_and_completion_is_joined()
    {
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CareerChat)), call =>
            call.Target.DeclaringType == typeof(ConversationStore) && call.Target.Name is "SetAsync" or "ClearAsync" or "TryStartAsync" or "TrySetAsync" or "TryClearAsync"
            || call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync");
        Assert.DoesNotContain(ProductionCalls.Value, call => call.Target.DeclaringType == typeof(ConversationStore)
            && call.Target.Name is "SetAsync" or "ClearAsync" or "TryStartAsync" or "TrySetAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(CareerPromptSources)
            && call.Target.Name == nameof(CareerPromptSources.CompleteJoinedAsync), typeof(InterviewSources), typeof(InterviewTransitions));
    }

    [Fact]
    public void Mock_training_has_one_paid_executor_and_source_writer_and_versioned_stop_controls()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target.Name is nameof(JobFinderLlm.MockQuestionsAsync) or nameof(JobFinderLlm.MockReviewAsync) or nameof(JobFinderLlm.MockSummaryAsync), typeof(MockPreparationSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(MockSession) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal), typeof(MockSources), typeof(MockPreparationRequests));
        AssertOwners(call => call.Target.DeclaringType == typeof(MockSessionRevision) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal), typeof(MockSources));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(MockInterviews)), call =>
            call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync" or "ClearAsync" or "SetAsync"
                || call.Target.DeclaringType == typeof(JobFinderLlm));
        Assert.DoesNotContain(typeof(MockInterviews).GetMethods(), method => method.Name == "FinishAsync" && method.GetParameters()[0].ParameterType == typeof(long));
    }

    [Fact]
    public void Learning_progress_has_one_writer_and_controls_require_a_captured_witness()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(LearningProgressRevision) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal), typeof(LearningProgressSources));
        AssertOwners(call => call.Target.DeclaringType == typeof(LearningProgressSources)
            && call.Target.Name is nameof(LearningProgressSources.MarkAsync) or nameof(LearningProgressSources.ApproveAsync) or nameof(LearningProgressSources.ResetAsync), typeof(InterviewProgress));
        Assert.DoesNotContain(typeof(InterviewProgress).GetMethods(), method => method.Name is "SetAsync" or "SetApprovedAsync"
            || method.Name == "ResetAsync" && method.GetParameters()[0].ParameterType == typeof(string));
        var controls = new[] { typeof(JobFinder.App.Components.Shared.LearningProgressEditor), typeof(MockInterviews) };
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => controls.Contains(call.CallerOwner)), call => call.Target.DeclaringType == typeof(RuntimeSettings));
    }

    [Fact]
    public void Score_preview_only_requests_native_assessment_and_never_calls_a_paid_engine_or_saves_business_rows()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CliHost)
            && ((call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("ScoreAsync", StringComparison.Ordinal)
                || (call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("ScorePreviewResultAsync", StringComparison.Ordinal))).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(VacancyAssessments) && call.Target.Name == nameof(VacancyAssessments.RequestPreviewAsync));
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(VacancyAssessments) && call.Target.Name == nameof(VacancyAssessments.WaitPreviewAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name != nameof(JobFinderLlm.RestoreOperation)
            || call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync");
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.ScoreAsync), typeof(VacancyAssessmentSteps));
    }
    [Fact]
    public void Offer_source_publication_has_one_owner_and_advisor_chat_cannot_insert_or_clear_the_dialogue()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(Offer) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal), typeof(OfferSources));
        var facade = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OfferAdvisor)).ToArray();
        Assert.Contains(facade, call => call.Target.DeclaringType == typeof(OfferPreparationRequests) && call.Target.Name == nameof(OfferPreparationRequests.RequestAsync));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.OfferAsync), typeof(OfferPreparationSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(OfferSources) && call.Target.Name == nameof(OfferSources.PublishJoinedAsync), typeof(OfferPreparationWorkflow), typeof(OfferSources));
        Assert.DoesNotContain(facade, call => call.Target.Name is "SaveChangesAsync" or "Advance" or "ClearAsync"
            || call.Target.Name is "Add" or "AddAsync" && call.Target.DeclaringType?.IsGenericType == true
                && call.Target.DeclaringType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>));
    }

    [Fact]
    public void Evaluation_annotation_backfill_only_requests_native_work_and_never_scores_or_mutates_sources()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CliHost)
            && (call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("BackfillEvaluationAsync", StringComparison.Ordinal)).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(VacancyAssessments) && call.Target.Name == nameof(VacancyAssessments.RequestAnnotationsAsync));
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(VacancyAssessments) && call.Target.Name == nameof(VacancyAssessments.WaitAnnotationsAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(JobFinderLlm)
            || call.Target.DeclaringType == typeof(Vacancy) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            || call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync");
    }

    [Fact]
    public void Owner_bot_content_has_one_native_SDK_sender_and_one_captured_publication_boundary()
    {
        AssertOwners(call => call.Target.DeclaringType?.Assembly == typeof(Telegram.Bot.ITelegramBotClient).Assembly
            && call.Target.Name is "SendMessage" or "SendDocument" or "EditMessageReplyMarkup", typeof(NotificationDeliveryTransport));
        AssertOwners(call => call.Target.DeclaringType == typeof(NotificationDeliveryRequests)
            && call.Target.Name == nameof(NotificationDeliveryRequests.PublishCapturedAsync), typeof(OwnerBotMessages));
        AssertOwners(call => call.Target.DeclaringType == typeof(OwnerBotMessages)
            && call.Target.Name is nameof(OwnerBotMessages.PublishAsync) or nameof(OwnerBotMessages.EditKeyboardAsync), typeof(BotService), typeof(CareerBot));
        AssertOwners(call => call.Target.DeclaringType == typeof(NotificationDeliveryRequests)
            && call.Target.Name == nameof(NotificationDeliveryRequests.PublishKeyboardAsync), typeof(OwnerBotMessages));
    }

    [Fact]
    public void Contact_mutations_have_one_source_owner_and_registry_cannot_delete_outreach_history()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(ContactSources)
            && call.Target.Name is nameof(ContactSources.UpsertAsync) or nameof(ContactSources.RetireAsync) or nameof(ContactSources.PublishPostAsync), typeof(ContactRegistry));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ContactRegistry)), call =>
            call.Target.DeclaringType == typeof(CompanyContact) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            || call.Target.Name.StartsWith("ExecuteDelete", StringComparison.Ordinal)
            || call.Target.Name.StartsWith("ExecuteSql", StringComparison.Ordinal));
        AssertOwners(call => call.Target.DeclaringType == typeof(CompanyContact)
            && call.Target.Name is "set_RetiredAt" or "set_Revision", typeof(ContactSources));
    }

    [Fact]
    public void Outreach_scheduler_owns_no_process_local_skip_state_or_operational_writes()
    {
        Assert.DoesNotContain(typeof(OutreachJob).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => field.FieldType == typeof(HashSet<long>));
        AssertOwners(call => call.Target.DeclaringType == typeof(OutreachScans)
            && call.Target.Name is nameof(OutreachScans.ReserveAsync) or nameof(OutreachScans.FinishAsync), typeof(OutreachJob));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OutreachJob)), call =>
            call.Target.DeclaringType == typeof(AppSetting) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_notification_importer_cannot_call_the_SDK_or_create_delivery_attempts()
    {
        var owners = new[] { typeof(NotificationDispatcher), typeof(LegacyNotificationImports) };
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => owners.Contains(call.CallerOwner)), call =>
            call.Target.DeclaringType?.Assembly == typeof(Telegram.Bot.ITelegramBotClient).Assembly
                && (call.Target.Name.StartsWith("Send", StringComparison.Ordinal) || call.Target.Name.StartsWith("EditMessage", StringComparison.Ordinal))
            || call.Target.DeclaringType == typeof(NotificationDeliveryAttempt) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void LinkedIn_people_paid_reading_and_browser_search_have_one_owner_each()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.ReadLinkedInPeopleAsync),
            typeof(LinkedInPeopleSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinder.Integrations.LinkedIn.LinkedInClient) && call.Target.Name == "SearchAsync",
            typeof(LinkedInPeopleReader));
        AssertOwners(call => call.Target.DeclaringType == typeof(LinkedInSearchQuota) && call.Target.Name == nameof(LinkedInSearchQuota.TakeAsync),
            typeof(LinkedInPeopleReader));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(LinkedInPeopleReader)),
            call => call.Target.DeclaringType == typeof(JobFinderLlm));
    }

    private static readonly Lazy<IReadOnlyList<Invocation>> ProductionCalls = new(() => ReadCalls(
        new[] { typeof(EmployerMessages).Assembly, typeof(CliHost).Assembly, typeof(BotService).Assembly, typeof(TelegramUserClient).Assembly }
            .Distinct().SelectMany(assembly => assembly.GetTypes())));

    [Fact]
    public void Company_user_preferences_have_one_owner_and_the_page_cannot_write_source_rows()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(Company)
            && call.Target.Name is "set_Watch" or "set_Blacklisted" or "set_PreferencesRevision"
            && !(call.CallerOwner == typeof(PostProcessingJsonContext)
                && call.Caller.Name.StartsWith("<CompanyPropInit>", StringComparison.Ordinal)), typeof(CompanyPreferences));
        var page = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(JobFinder.App.Components.Pages.Companies)).ToArray();
        Assert.Contains(page, call => call.Target.DeclaringType == typeof(CompanyPreferences) && call.Target.Name == nameof(CompanyPreferences.SetAsync));
        Assert.DoesNotContain(page, call => call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync");
    }

    [Fact]
    public void Candidate_story_writes_have_one_owner_and_ui_bot_facades_cannot_mutate_source_rows()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(Story) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            && !(typeof(System.Text.Json.Serialization.JsonSerializerContext).IsAssignableFrom(call.CallerOwner)
                && (call.Caller.Name.StartsWith("<StoryPropInit>", StringComparison.Ordinal)
                    || call.Caller.Name.StartsWith("<Create_Story>", StringComparison.Ordinal))), typeof(StorySources));
        AssertOwners(call => call.Target.DeclaringType == typeof(StoryRevision) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal), typeof(StorySources));
        var facades = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(StoryBank) || call.CallerOwner == typeof(JobFinder.App.Components.Pages.Stories)).ToArray();
        Assert.Contains(facades, call => call.Target.DeclaringType == typeof(StorySources) && call.Target.Name == nameof(StorySources.CreateFromConversationAsync));
        Assert.Contains(facades, call => call.Target.DeclaringType == typeof(StorySources) && call.Target.Name == nameof(StorySources.RetireAsync));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.ComposeStoryAsync), typeof(StoryPreparationSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(StorySources) && call.Target.Name == nameof(StorySources.PublishDraftJoinedAsync), typeof(StoryPreparationWorkflow));
        Assert.DoesNotContain(facades, call => call.Target.Name is "SaveChangesAsync" or "SaveChanges" or "ExecuteDeleteAsync" or "ExecuteUpdateAsync"
            || call.Target.Name is "Update" or "Add" or "Remove" && call.Target.DeclaringType?.IsGenericType == true
                && call.Target.DeclaringType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>));
    }
    private static readonly IReadOnlyDictionary<ushort, OpCode> Instructions = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(instruction => unchecked((ushort)instruction.Value)).ToDictionary(group => group.Key, group => group.First());

    [Fact]
    public void Reply_analysis_has_one_native_paid_adapter_and_producers_do_not_analyze()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.AnalyzeReplyAsync),
            typeof(ReplyProcessingSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(ReplyDraftSources) && call.Target.Name == "PublishJoinedAsync",
            typeof(ReplyDraftSources), typeof(ReplyProcessingSources));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ReplyService)),
            call => call.Target.DeclaringType == typeof(ApplicationEvent) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        var reprocess = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CliHost)
            && (call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("EmailReprocessAsync", StringComparison.Ordinal)).ToArray();
        Assert.Contains(reprocess, call => call.Target.DeclaringType == typeof(ReplyProcessingRequests)
            && call.Target.Name == nameof(ReplyProcessingRequests.ReprocessAsync));
        Assert.DoesNotContain(reprocess, call => call.Target.Name is "Remove" or "SaveChangesAsync" or "ExecuteDeleteAsync"
            || call.Target.DeclaringType == typeof(ReplyService) || call.Target.DeclaringType == typeof(JobFinderLlm));
    }

    [Fact]
    public void Bot_edit_prompts_have_one_persisted_owner_and_do_not_execute_paid_or_recipient_work()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(BotEdits) && call.Target.Name == nameof(BotEdits.BeginQuestionAsync),
            typeof(BotService), typeof(CareerBot));
        Assert.DoesNotContain(ProductionCalls.Value, call => call.Target.DeclaringType == typeof(BotEdits) && call.Target.Name == nameof(BotEdits.BeginAsync));
        AssertOwners(call => call.Target.DeclaringType == typeof(BotEdits) && call.Target.Name == nameof(BotEdits.BindAsync),
            typeof(BotEdits));
        AssertOwners(call => call.Target.DeclaringType == typeof(BotEdits)
            && call.Target.Name is nameof(BotEdits.ConfirmQuestionAsync) or nameof(BotEdits.MayDeliverQuestionAsync), typeof(NotificationDeliveryWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(Telegram.Bot.Types.ReplyMarkups.ForceReplyMarkup)
            && call.Target.IsConstructor, typeof(NotificationDeliveryTransport));
        AssertOwners(call => call.Target.DeclaringType == typeof(BotEdits)
            && call.Target.Name is nameof(BotEdits.RecordReplyAsync) or nameof(BotEdits.CompleteAsync), typeof(BotService), typeof(CareerBot));
        var owner = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(BotEdits));
        Assert.DoesNotContain(owner, call => call.Target.DeclaringType == typeof(JobFinderLlm) || call.Target.DeclaringType == typeof(EmployerMessages)
            || call.Target.DeclaringType == typeof(ApprovalService) || call.Target.DeclaringType == typeof(IDeliveryTransport));
    }

    [Fact]
    public void Reply_draft_publication_is_owned_by_Hiring_and_cli_and_redraft_do_not_mutate_drafts()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(ReplyDraftSources)
            && call.Target.Name == nameof(ReplyDraftSources.PublishAsync), typeof(ReplyService), typeof(CliHost));
        AssertOwners(call => call.Target.DeclaringType == typeof(ReplyDraftSources)
            && call.Target.Name == nameof(ReplyDraftSources.DismissAsync), typeof(ReplyService));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ReplyService)),
            call => call.Target.DeclaringType == typeof(ApplicationEvent) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
                && (call.Caller.DeclaringType?.Name.Contains("DismissDraftAsync", StringComparison.Ordinal) == true));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ReplyService) || call.CallerOwner == typeof(CliHost));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(ApplicationEvent)
            && call.Target.Name is "set_Draft" or "set_DraftStatus" or "set_Revision"
            && ((call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("ReplyCardAsync", StringComparison.Ordinal)
                || (call.Caller.DeclaringType?.Name ?? call.Caller.Name).Contains("RedraftAsync", StringComparison.Ordinal)));
        var owner = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ReplyDraftSources));
        Assert.DoesNotContain(owner, call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target != typeof(JobFinderLlm).GetMethod(nameof(JobFinderLlm.VacancyText), new[] { typeof(Vacancy), typeof(bool) }));
    }

    [Fact]
    public void Outreach_source_preparation_publishes_only_through_Hiring_without_direct_source_or_card_writes()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(OutreachSources)
            && call.Target.Name == nameof(OutreachSources.PublishAsync), typeof(OutreachService));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OutreachService)
            && (call.Caller.DeclaringType?.Name.Contains("PrepareAsync", StringComparison.Ordinal) == true
                || call.Caller.DeclaringType?.Name.Contains("DraftForContactAsync", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Outreach) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OutreachService)),
            call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.OutreachAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Notifier)
            && call.Target.Name is nameof(Notifier.Add) or nameof(Notifier.SaveAndFlushAsync));
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OutreachService)),
            call => call.Target.DeclaringType == typeof(Outreach) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        AssertOwners(call => call.Target.DeclaringType == typeof(OutreachSources)
            && call.Target.Name == nameof(OutreachSources.DecideAsync), typeof(OutreachService));
        var owner = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(OutreachSources));
        Assert.DoesNotContain(owner, call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target != typeof(JobFinderLlm).GetMethod(nameof(JobFinderLlm.VacancyText), new[] { typeof(Vacancy), typeof(bool) })
            || call.Target.DeclaringType == typeof(LetterWriter) && call.Target.Name != nameof(LetterWriter.Matches));
    }

    [Fact]
    public void Letter_content_is_executed_only_by_its_native_step_adapter()
    {
        Assert.False(typeof(LetterContent).IsPublic);
        AssertOwners(call => Owner(call.Target.DeclaringType!) == typeof(LetterContent), typeof(LetterContent), typeof(LetterPreparationSteps));
    }

    [Fact]
    public void Bot_control_polling_admits_only_and_execution_has_one_native_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinder.Pipeline.Controls.IBotControlExecutor)
            && call.Target.Name == nameof(JobFinder.Pipeline.Controls.IBotControlExecutor.ExecuteAsync), typeof(JobFinder.Pipeline.Controls.BotControlWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramBotClientHolder)
            && call.Target.Name == "get_ControlClient", typeof(BotService));
        AssertOwners(call => call.Target.DeclaringType == typeof(BotService)
            && call.Target.Name is "OnCallbackAsync" or "OnMessageAsync", typeof(BotService));
        var polling = typeof(BotService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(CancellationToken)], null)!;
        var stateMachine = polling.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
        var calls = ProductionCalls.Value.Where(call => call.Caller.DeclaringType == stateMachine).ToArray();
        Assert.NotEmpty(calls);
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(BotService) && call.Target.Name is "OnCallbackAsync" or "OnMessageAsync"
            || call.Target.DeclaringType == typeof(AppSetting) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_notification_effects_and_the_no_retry_client_have_one_durable_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(INotificationDeliveryTransport)
            && call.Target.Name == nameof(INotificationDeliveryTransport.SendAsync), typeof(NotificationDeliveryWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(NotificationDeliveryWorkflow)
            && call.Target.Name is nameof(NotificationDeliveryWorkflow.RunAsync) or nameof(NotificationDeliveryWorkflow.BindAsync), typeof(NotificationDeliveryHandler));
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramBotClientHolder)
            && call.Target.Name == "get_DeliveryClient", typeof(NotificationDeliveryTransport));
    }

    [Fact]
    public void Engagement_bootstrap_preferences_are_read_only_by_their_import_owner() => AssertOwners(
        call => call.Target.DeclaringType == typeof(ThreadsOptions)
            && call.Target.Name is "get_AutoPublish" or "get_MaxDraftsPerDay" or "get_AuthorCooldownDays", typeof(EngagementPolicyStore));

    [Fact]
    public void Policy_publications_are_owned_by_the_validating_feature_stores() => AssertOwners(
        call => call.Target.DeclaringType == typeof(PgPolicyStore)
            && call.Target.Name is nameof(PgPolicyStore.PublishAsync) or nameof(PgPolicyStore.EnsureImportedAsync),
        typeof(AutomationPolicyStore), typeof(LlmPolicyStore), typeof(SearchPolicyStore), typeof(EngagementPolicyStore));

    [Fact]
    public void Joined_candidate_fact_publication_is_owned_by_candidate_and_interview_source_commands() => AssertOwners(
        call => call.Target.DeclaringType == typeof(CandidateDocuments) && call.Target.Name == nameof(CandidateDocuments.PublishFactsAsync)
            && call.Target.GetParameters().FirstOrDefault()?.ParameterType == typeof(JobFinder.Data.JobFinderDbContext), typeof(InterviewSources), typeof(HrPlaybookSources));

    [Fact]
    public void Interview_root_handoff_is_owned_by_request_admission_and_source_commands() => AssertOwners(
        call => call.Target.DeclaringType == typeof(InterviewPreparationRequests) && call.Target.Name == nameof(InterviewPreparationRequests.QueueAsync),
        typeof(InterviewPreparationRequests), typeof(InterviewSources));

    [Fact]
    public void Interview_scheduling_and_debrief_join_the_shared_source_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(InterviewSources)
            && call.Target.Name is nameof(InterviewSources.EnsureAsync) or nameof(InterviewSources.CompleteDebriefAsync), typeof(InterviewTransitions));
        AssertOwners(call => call.Target.DeclaringType == typeof(FunnelService) && call.Target.Name == nameof(FunnelService.ApplyStage),
            typeof(FunnelService), typeof(InterviewTransitions));
    }

    [Fact]
    public void Native_paid_steps_are_invoked_only_by_their_workflow_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(ILetterPreparationSteps), typeof(LetterPreparationWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(IResumePreparationSteps), typeof(ResumePreparationWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(ICommentPreparationSteps), typeof(CommentPreparationWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(IPostProcessingSteps), typeof(PostProcessingWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(IInterviewPreparationSteps), typeof(InterviewPreparationWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(IInterviewPreparationCapture), typeof(InterviewPreparationWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(InterviewPreparationWorkflow) && call.Target.Name == nameof(InterviewPreparationWorkflow.RunAsync), typeof(InterviewPreparationHandler));
    }

    [Fact]
    public void Interview_pack_facade_and_importer_cannot_execute_paid_stages()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(InterviewPrep) || call.CallerOwner == typeof(InterviewWorker));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(IInterviewPreparationSteps)
            || call.Target.DeclaringType == typeof(IInterviewPreparationCapture)
            || call.Target.DeclaringType == typeof(InterviewPreparationSteps)
            || call.Target.DeclaringType == typeof(GeneratedAnswers)
            || call.Target.DeclaringType == typeof(InterviewAnswerContent));
        Assert.DoesNotContain(calls.Where(call => call.CallerOwner == typeof(InterviewWorker)),
            call => call.Target.DeclaringType == typeof(JobFinderLlm) || call.Target.DeclaringType == typeof(InterviewPrep));
        AssertOwners(call => call.Target.DeclaringType == typeof(InterviewTransitions) && call.Target.Name == nameof(InterviewTransitions.AdvanceAsync),
            typeof(InterviewTransitions), typeof(ReplyProcessingSources), typeof(HhNegotiationSources), typeof(FollowUpSources), typeof(OfferSources));
    }

    [Fact]
    public void Follow_up_timer_publishes_source_changes_only_through_the_Hiring_owner()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(FollowUpSources)
            && call.Target.Name is nameof(FollowUpSources.DraftAsync) or nameof(FollowUpSources.CloseAsync),
            typeof(FollowUpSources), typeof(FollowUpJob));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(FollowUpJob));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Microsoft.EntityFrameworkCore.DbContext)
            && call.Target.Name is "SaveChanges" or "SaveChangesAsync");
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Notifier)
            && call.Target.Name is nameof(Notifier.Add) or nameof(Notifier.SaveAndFlushAsync) or nameof(Notifier.SaveJoinedAsync));
    }

    [Fact]
    public void Email_polling_adapter_cannot_commit_an_incoming_or_cursor_independently()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(EmailInboxSources)
            && call.Target.Name is nameof(EmailInboxSources.CaptureAsync) or nameof(EmailInboxSources.PublishAsync), typeof(EmailInboxJob));
        AssertOwners(call => call.Target.DeclaringType == typeof(ReplyProcessingRequests)
            && call.Target.Name == nameof(ReplyProcessingRequests.IncomingJoinedAsync), typeof(ReplyProcessingRequests), typeof(EmailInboxSources));
        AssertOwners(call => call.Target.DeclaringType == typeof(EmailInboxSources)
            && call.Target.Name == nameof(EmailInboxSources.AcknowledgeHhRefreshJoinedAsync), typeof(HhNegotiationSources));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(EmailInboxJob));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Microsoft.EntityFrameworkCore.DbContext)
            && call.Target.Name is "SaveChanges" or "SaveChangesAsync");
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(RuntimeSettings)
            && call.Target.Name is nameof(RuntimeSettings.SetAsync) or nameof(RuntimeSettings.RemoveAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(ReplyService) && call.Target.Name == nameof(ReplyService.HandleInboundAsync));
    }

    [Fact]
    public void Telegram_history_network_adapter_cannot_own_business_writes()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(TelegramChatHistorySources)
            && call.Target.Name is nameof(TelegramChatHistorySources.CaptureAsync) or nameof(TelegramChatHistorySources.PublishAsync), typeof(RecruiterChatService));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(RecruiterChatService));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Microsoft.EntityFrameworkCore.DbContext)
            && call.Target.Name is "SaveChanges" or "SaveChangesAsync");
        Assert.DoesNotContain(calls, call => call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            && call.Target.DeclaringType is { } target && (target == typeof(Application) || target == typeof(ApplicationEvent) || target == typeof(Vacancy)));
    }

    [Fact]
    public void Hh_negotiation_publication_is_owned_by_Hiring_instead_of_the_network_adapter()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(HhNegotiationSources)
            && call.Target.Name == nameof(HhNegotiationSources.PublishAsync), typeof(HhNegotiationsJob));
        AssertOwners(call => call.Target.DeclaringType == typeof(HhHistory) && call.Target.Name == nameof(HhHistory.Link),
            typeof(HhNegotiationSources), typeof(VacancyAssessmentWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(HhResponse) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal),
            typeof(HhNegotiationSources));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(HhNegotiationsJob));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Notifier)
            && call.Target.Name is nameof(Notifier.Add) or nameof(Notifier.SaveAndFlushAsync) or nameof(Notifier.SaveJoinedAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(HhResponse) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(ApplicationEvent) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(AppSetting) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        AssertOwners(call => call.Target.DeclaringType == typeof(HhChatHistorySources)
            && call.Target.Name is nameof(HhChatHistorySources.CaptureAsync) or nameof(HhChatHistorySources.PublishAsync) or nameof(HhChatHistorySources.RecordFailureAsync), typeof(HhNegotiationsJob));
        AssertOwners(call => call.Target.DeclaringType == typeof(HhResponseBackfillSources)
            && call.Target.Name is nameof(HhResponseBackfillSources.MissingAsync) or nameof(HhResponseBackfillSources.MarkUnavailableAsync), typeof(HhNegotiationsJob));
    }

    [Fact]
    public void Digest_source_publication_is_owned_outside_the_scheduler_adapter()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(DigestPublications)
            && call.Target.Name is nameof(DigestPublications.PublishAsync) or nameof(DigestPublications.LastSlotAsync), typeof(DigestJob));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(DigestJob));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Microsoft.EntityFrameworkCore.DbContext)
            && call.Target.Name is "SaveChanges" or "SaveChangesAsync");
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(Notifier)
            && call.Target.Name is nameof(Notifier.Add) or nameof(Notifier.SaveAndFlushAsync) or nameof(Notifier.SaveJoinedAsync));
    }

    [Fact]
    public void Raw_post_importer_and_reparse_facade_do_not_own_paid_work_or_ingest_mutation()
    {
        var callers = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(RawPostWorker) || call.CallerOwner == typeof(JobFinder.Pipeline.Ingest.PostReparser));
        Assert.DoesNotContain(callers, call => call.Target.DeclaringType == typeof(JobFinderLlm)
            || call.Target.DeclaringType == typeof(JobFinder.Pipeline.Ingest.VacancyIngestor)
            || call.Target.DeclaringType == typeof(JobFinder.Pipeline.Ingest.PostEnricher)
            || call.Target.DeclaringType == typeof(JobFinder.Pipeline.Ingest.TelegramContactVerifier));
        AssertOwners(call => call.Target.DeclaringType == typeof(PostProcessingWorkflow) && call.Target.Name == nameof(PostProcessingWorkflow.RunAsync), typeof(PostProcessingHandler));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm) && call.Target.Name == nameof(JobFinderLlm.ExtractPostAsync), typeof(PostProcessingSteps), typeof(Doctor));
    }

    [Fact]
    public void Comment_source_work_is_executed_only_by_its_native_handler_and_child_requests_join_its_transaction()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentSourceWorkflow)
            && call.Target.Name == nameof(CommentSourceWorkflow.RunAsync), typeof(CommentSourceHandler));
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentPreparationRequests)
            && call.Target.Name == nameof(CommentPreparationRequests.QueueAsync), typeof(CommentPreparationRequests), typeof(CommentSourceWorkflow));
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentPreparationRequests)
            && call.Target.Name == nameof(CommentPreparationRequests.PublishDependentsAsync), typeof(CommentPreparationRequests), typeof(CommentPreparationWorkflow));
    }

    [Fact]
    public void Threads_collector_only_captures_comment_sources_and_never_waits_for_paid_preparation()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ThreadsJob)).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(CommentSources)
            && call.Target.Name == nameof(CommentSources.CollectAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(JobFinderLlm)
            || call.Target.DeclaringType == typeof(CommentPreparationRequests)
            || call.Target.DeclaringType == typeof(CommentPlanner) && call.Target.Name != nameof(CommentPlanner.IsCandidate)
            || call.Target.DeclaringType == typeof(CommentDraft) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void Comment_approval_and_replacement_are_owned_by_feature_commands_instead_of_UI_adapters()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentDecisions)
            && call.Target.Name is nameof(CommentDecisions.SetAsync) or nameof(CommentDecisions.PickAsync) or nameof(CommentDecisions.RejectAsync), typeof(ApprovalService));
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentDraft)
            && call.Target.Name is "set_Variants" or "set_PostText" or "set_PostUrl" or "set_SourceRequestId",
            typeof(CommentSourceWorkflow), typeof(CommentSourceJsonContext));
    }

    [Fact]
    public void Retention_has_one_owner_and_cannot_mutate_business_history_or_mark_a_live_run_interrupted()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinder.Pipeline.Maintenance.OperationalRetention)
            && call.Target.Name == nameof(JobFinder.Pipeline.Maintenance.OperationalRetention.SweepAsync), typeof(HousekeepingJob));
        var housekeeping = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(HousekeepingJob)).ToArray();
        Assert.Contains(housekeeping, call => call.Target.DeclaringType == typeof(JobFinder.Pipeline.Maintenance.OperationalRetention));
        Assert.DoesNotContain(housekeeping, call => call.Target.DeclaringType == typeof(JobFinder.Data.JobFinderDbContext)
            || call.Target.DeclaringType == typeof(JobRun) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            || call.Target is MethodInfo method && method.IsGenericMethod && method.Name is "ExecuteDeleteAsync" or "ExecuteUpdateAsync");
        var retention = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(JobFinder.Pipeline.Maintenance.OperationalRetention)
            && call.Target.DeclaringType == typeof(JobFinder.Data.JobFinderDbContext)).ToArray();
        Assert.NotEmpty(retention);
        Assert.All(retention, call => Assert.Contains(call.Target.Name, new[] { "get_RawPosts", "get_JobRuns" }));
    }

    [Fact]
    public void Housekeeping_delegates_comment_expiry_without_mutating_drafts_or_sending_inline()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(CommentExpiry)
            && call.Target.Name == nameof(CommentExpiry.ExpireBatchAsync), typeof(HousekeepingJob));
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(HousekeepingJob)).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(CommentExpiry));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(CommentDraft)
            && call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
            || call.Target is MethodInfo method && method.IsGenericMethod && method.GetGenericArguments().Contains(typeof(CommentDraft))
                && method.Name is "ExecuteUpdateAsync" or "ExecuteDeleteAsync"
            || call.Target.DeclaringType == typeof(ICommentDeliveryTransport)
            || call.Target.DeclaringType == typeof(INotificationDeliveryTransport));
    }

    [Fact]
    public void Assessment_and_interview_schedulers_delegate_source_writes_to_module_owners()
    {
        foreach (var facade in new[] { typeof(VacancyWorker), typeof(InterviewReminderJob) })
        {
            var calls = ProductionCalls.Value.Where(call => call.CallerOwner == facade).ToArray();
            Assert.DoesNotContain(calls, call => call.Target.Name == nameof(Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync)
                || call.Target.DeclaringType == typeof(Notifier) && call.Target.Name == nameof(Notifier.SaveAndFlushAsync)
                || call.Target.Name.StartsWith("set_", StringComparison.Ordinal)
                    && (call.Target.DeclaringType == typeof(Vacancy) || call.Target.DeclaringType == typeof(Interview)));
        }
    }

    [Fact]
    public void Legacy_import_workers_join_notices_to_native_admission_instead_of_saving_unowned_cards()
    {
        foreach (var owner in new[] { typeof(ApplicationImportSources), typeof(VacancyAssessmentCapture) })
        {
            var calls = ProductionCalls.Value.Where(call => call.CallerOwner == owner).ToArray();
            Assert.Contains(calls, call => call.Target.DeclaringType == typeof(Notifier)
                && call.Target.Name == nameof(Notifier.SaveAndFlushAsync));
            var publicationCalls = owner == typeof(VacancyAssessmentCapture)
                ? calls.Where(call => call.Caller.Name == nameof(VacancyAssessmentCapture.FailPublishAsync)
                    || call.Caller.DeclaringType?.Name.StartsWith("<FailPublishAsync>", StringComparison.Ordinal) == true)
                : calls;
            Assert.DoesNotContain(publicationCalls, call => call.Target.Name == nameof(Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync)
                || call.Target.DeclaringType == typeof(INotificationDeliveryTransport));
        }
    }

    [Fact]
    public void Letter_and_resume_facade_tasks_are_owned_by_the_native_content_adapters()
    {
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target.Name is nameof(JobFinderLlm.RecruiterMessageAsync) or nameof(JobFinderLlm.CheckRecruiterMessageAsync), typeof(LetterContent));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target.Name is nameof(JobFinderLlm.TailorResumeAsync) or nameof(JobFinderLlm.ReviewResumeAsync), typeof(ResumePreparationSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target.Name == nameof(JobFinderLlm.PlanCommentAsync), typeof(CommentPreparationSteps));
        AssertOwners(call => call.Target.DeclaringType == typeof(JobFinderLlm)
            && call.Target.Name == nameof(JobFinderLlm.InterviewAnswerAsync), typeof(InterviewAnswerContent));
    }

    [Fact]
    public void Employer_transport_is_invoked_only_by_the_delivery_gateway() => AssertOwners(
        call => call.Target.DeclaringType == typeof(IDeliveryTransport) && call.Target.Name == nameof(IDeliveryTransport.SendAsync), typeof(DeliveryGateway));

    [Fact]
    public void Initial_application_gate_is_invoked_only_by_its_native_workflow() => AssertOwners(
        call => call.Target.DeclaringType == typeof(IApplicationDeliveryGate), typeof(ApplicationDeliveryWorkflow));

    [Fact]
    public void Comment_publication_adapters_are_invoked_only_by_the_native_transport() => AssertOwners(
        call => call.Target.DeclaringType is { } declaring && (declaring == typeof(ThreadsApiClient) || declaring == typeof(ThreadsBrowserClient))
            && call.Target.Name is nameof(ThreadsApiClient.PublishPreparedAsync) or nameof(ThreadsApiClient.PreparePublicationAsync), typeof(CommentDeliveryTransport));

    [Fact]
    public void Comment_transport_effects_are_invoked_only_by_its_native_workflow() => AssertOwners(
        call => call.Target.DeclaringType == typeof(ICommentDeliveryTransport)
            && call.Target.Name is nameof(ICommentDeliveryTransport.PrepareAsync) or nameof(ICommentDeliveryTransport.PublishAsync), typeof(CommentDeliveryWorkflow));

    [Fact]
    public void Comment_reconciler_cannot_publish_inline()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CommentPublishWorker)).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(CommentDeliveryRequests) && call.Target.Name == nameof(CommentDeliveryRequests.RequestAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(ICommentDeliveryTransport)
            || call.Target.DeclaringType == typeof(ThreadsApiClient) || call.Target.DeclaringType == typeof(ThreadsBrowserClient) || call.Target.DeclaringType == typeof(ThreadsGateway));
        Assert.Null(typeof(IThreadsClient).GetMethod("ReplyAsync"));
        Assert.Null(typeof(ThreadsGateway).GetMethod("ReplyAsync"));
    }

    [Fact]
    public void Initial_application_reconciler_cannot_prepare_or_send_inline()
    {
        var calls = ProductionCalls.Value.Where(call => call.CallerOwner == typeof(ApplyWorker)).ToArray();
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(ApplicationDeliveryRequests)
            && call.Target.Name == nameof(ApplicationDeliveryRequests.RequestAsync));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType == typeof(DeliveryGateway)
            || call.Target.DeclaringType == typeof(EmployerContentGate) || call.Target.DeclaringType == typeof(IDeliveryTransport)
            || call.Target.DeclaringType == typeof(LetterWriter) || call.Target.DeclaringType == typeof(ResumeTailor)
            || call.Target.DeclaringType == typeof(JobFinderLlm));
        Assert.DoesNotContain(calls, call => call.Target.Name == nameof(Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync)
            || call.Target.Name == "ExecuteUpdateAsync" || call.Target.Name == "ExecuteDeleteAsync"
            || call.Target.DeclaringType == typeof(Application) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void Company_collector_is_an_observer_and_metadata_has_one_source_owner()
    {
        Assert.DoesNotContain(ProductionCalls.Value.Where(call => call.CallerOwner == typeof(CompanyCollectorJob)), call =>
            call.Target.Name is "SaveChangesAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync"
            || call.Target.DeclaringType == typeof(Company) && call.Target.Name.StartsWith("set_", StringComparison.Ordinal));
        AssertOwners(call => call.Target.DeclaringType == typeof(Company) && call.Target.Name is "set_DotnetVacancies"
            or "set_DetailsFetchedAt" or "set_ItAccredited" or "set_OpenVacancies"
            && !(call.CallerOwner == typeof(PostProcessingJsonContext) && call.Caller.Name.StartsWith("<CompanyPropInit>", StringComparison.Ordinal)), typeof(CompanyMetadataSources));
    }

    [Fact]
    public void Recipient_capable_adapters_are_called_only_by_the_gateway_transport_or_self_only_APIs()
    {
        var endpoints = new Dictionary<Type, string>
        {
            [typeof(TelegramUserClient)] = nameof(TelegramUserClient.SendDirectMessageAsync),
            [typeof(EmailSender)] = nameof(EmailSender.SendAsync),
            [typeof(HhApplier)] = nameof(HhApplier.ApplyAsync),
            [typeof(HhChat)] = nameof(HhChat.SendAsync),
        };
        var calls = ProductionCalls.Value.Where(call => call.Target.DeclaringType is { } declaring
            && endpoints.TryGetValue(declaring, out var endpoint) && call.Target.Name == endpoint).ToArray();
        Assert.Equal(endpoints.Count, calls.Select(call => call.Target.DeclaringType).Distinct().Count());
        Assert.DoesNotContain(calls, call => call.CallerOwner != typeof(EmployerDeliveryTransport)
            && !(call.Caller.DeclaringType == typeof(TelegramUserClient) && call.Caller.Name == nameof(TelegramUserClient.SendToSavedMessagesAsync))
            && !(call.Caller.DeclaringType == typeof(EmailSender) && call.Caller.Name == nameof(EmailSender.SendToSelfAsync)));
    }

    [Fact]
    public void Ownership_guard_detects_async_and_method_group_bypasses()
    {
        var calls = ReadCalls(typeof(ArchitectureBoundaryProbe).Assembly.GetTypes().Where(type => Owner(type) == typeof(ArchitectureBoundaryProbe)));
        var violations = Violations(calls.Where(call => call.Target.DeclaringType == typeof(IDeliveryTransport)), [typeof(DeliveryGateway)]);
        Assert.Equal(2, violations.Count);
        Assert.All(violations, call => Assert.Equal(typeof(ArchitectureBoundaryProbe), call.CallerOwner));
        Assert.Contains(violations, call => call.Caller.Name == "MoveNext");
        Assert.Contains(violations, call => call.Caller.Name == nameof(ArchitectureBoundaryProbe.Bind));
        Assert.Empty(Violations(violations, [typeof(ArchitectureBoundaryProbe)]));
    }

    private static void AssertOwners(Func<Invocation, bool> matches, params Type[] owners)
    {
        var calls = ProductionCalls.Value.Where(matches).ToArray();
        Assert.NotEmpty(calls);
        Assert.Empty(Violations(calls, owners));
    }

    private static IReadOnlyList<Invocation> Violations(IEnumerable<Invocation> calls, IReadOnlyCollection<Type> owners) =>
        calls.Where(call => !owners.Contains(call.CallerOwner)).ToArray();

    private static Type Owner(Type type)
    {
        while (type.DeclaringType is { } parent) type = parent;
        return type;
    }

    private static IReadOnlyList<Invocation> ReadCalls(IEnumerable<Type> types)
    {
        var calls = new List<Invocation>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in types)
        foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } bytes) continue;
            var offset = 0;
            while (offset < bytes.Length)
            {
                var value = bytes[offset++];
                var instruction = Instructions[value == 0xfe ? (ushort)(0xfe00 | bytes[offset++]) : value];
                if (instruction.OperandType == OperandType.InlineMethod)
                {
                    var target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), type.GetGenericArguments(),
                        method is MethodInfo information ? information.GetGenericArguments() : Type.EmptyTypes)
                        ?? throw new InvalidOperationException($"unresolved method operand in {method}");
                    calls.Add(new(method, Owner(type), target));
                }
                offset += instruction.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                        or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                    _ => throw new InvalidOperationException($"unsupported operand {instruction.OperandType} in {method}"),
                };
            }
        }
        return calls;
    }

    private sealed record Invocation(MethodBase Caller, Type CallerOwner, MethodBase Target);
}

internal static class ArchitectureBoundaryProbe
{
    public static async Task<DeliveryReceipt> SendAsync(IDeliveryTransport transport, ApprovedDispatch packet, CancellationToken ct) =>
        await transport.SendAsync(packet, ct);

    public static Func<ApprovedDispatch, CancellationToken, Task<DeliveryReceipt>> Bind(IDeliveryTransport transport) => transport.SendAsync;
}
