using JobFinder.Core.Domain;

namespace JobFinder.Core.Filtering;

/// <summary>Runtime values the decision depends on (the settings table overrides the <c>Decision</c> config section).</summary>
public sealed record DecisionSettings(int AutoApplyThreshold, int ApprovalThreshold, int DailyAutoApplyLimit, bool AutoApplyPaused);

/// <summary>What the policy knows about the vacancy's applicability.</summary>
/// <param name="CanAutoApply">An hh/rabota.by source without an employer test: the browser can send the form unattended.</param>
/// <param name="AutoAppliedToday">Automatic applications already sent (or queued) today, in the configured time zone.</param>
public readonly record struct DecisionInput(int Score, bool CanAutoApply, int AutoAppliedToday);

public static class DecisionPolicy
{
    public static Decision Decide(DecisionInput input, DecisionSettings settings)
    {
        if (input.Score >= settings.AutoApplyThreshold
            && input.CanAutoApply
            && !settings.AutoApplyPaused
            && input.AutoAppliedToday < settings.DailyAutoApplyLimit)
        {
            return Decision.AutoApply;
        }

        // Above the auto threshold but blocked (limit, pause, test, non-hh source) still deserves a look: approval, not silence.
        return input.Score >= settings.ApprovalThreshold ? Decision.Approval : Decision.Ignore;
    }
}
