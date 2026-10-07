using System.Linq.Expressions;
using JobFinder.Core.Domain;

namespace JobFinder.Core.Filtering;

public static class CommentPublicationRules
{
    public static Expression<Func<CommentDraft, bool>> CountsTowardDailyLimit(DateTimeOffset since, bool autoPublish) =>
        autoPublish
            ? draft => draft.CreatedAt >= since && (draft.Status == CommentStatus.Approved || draft.Status == CommentStatus.Posted
                || draft.Status == CommentStatus.Sending || draft.Status == CommentStatus.OutcomeUnknown)
            : draft => draft.CreatedAt >= since && draft.Status != CommentStatus.Rejected;

    public static void Decide(CommentDraft draft, bool worthCommenting, bool autoPublish, DateTimeOffset now)
    {
        var usable = draft.Variants.FirstOrDefault(variant => variant.Trim().Length is > 0 and <= 500);
        if (!worthCommenting || usable is null)
        {
            draft.Status = CommentStatus.Rejected;
            return;
        }
        if (!autoPublish)
        {
            draft.Status = CommentStatus.Pending;
            return;
        }
        draft.Status = CommentStatus.Approved;
        draft.ChosenText = usable.Trim();
        draft.DecidedAt = now;
    }

    public static AudienceKind Audience(string raw) => raw switch
    {
        "recruiter" => AudienceKind.Recruiter,
        "hiring_manager" => AudienceKind.HiringManager,
        "tech" => AudienceKind.Tech,
        "hiring" => AudienceKind.Hiring,
        _ => AudienceKind.Other,
    };
}
