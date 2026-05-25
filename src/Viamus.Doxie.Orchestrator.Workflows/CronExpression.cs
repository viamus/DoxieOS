using NCrontab;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Thin wrapper around <c>NCrontab</c> for the prototype's cron needs:
/// validate an expression at save time, and ask "given this expression
/// and a starting point, when's the next firing?" at daemon-tick time.
///
/// <para>Standard 5-field cron: <c>minute hour day-of-month month day-of-week</c>.
/// We deliberately don't enable the optional 6-field (with-seconds)
/// variant — sub-minute precision isn't a workflow trigger we need
/// for this prototype, and the simpler grammar matches what users
/// already know from /etc/crontab and GitHub Actions.</para>
/// </summary>
public static class CronExpression
{
    /// <summary>
    /// Returns true if <paramref name="expression"/> is a syntactically
    /// valid 5-field cron expression. Used by the workflow form +
    /// REST endpoints to reject invalid trigger config at save time.
    /// </summary>
    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try
        {
            CrontabSchedule.Parse(expression);
            return true;
        }
        catch (CrontabException) { return false; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Returns the first occurrence STRICTLY AFTER <paramref name="after"/>,
    /// or <c>null</c> if the expression is invalid. The caller is the
    /// cron daemon's per-tick "did this workflow's next firing pass?"
    /// check.
    /// </summary>
    public static DateTime? NextOccurrenceAfter(string? expression, DateTime after)
    {
        if (!IsValid(expression)) return null;
        // IsValid already verified parse succeeds; this Parse can't throw.
        var schedule = CrontabSchedule.Parse(expression);
        return schedule.GetNextOccurrence(after);
    }
}
