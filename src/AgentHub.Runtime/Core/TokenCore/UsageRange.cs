namespace AgentHub.Core.TokenCore;

/// <summary>用量三档日期窗。上一档与当前档等长、紧挨、不含重叠日。</summary>
public static class UsageRange
{
    public static string Normalize(string? range) =>
        range is "7d" or "month" ? range : "today";

    public static (DateTime From, DateTime To) Current(string range, DateTime today)
    {
        today = today.Date;
        return range switch
        {
            "7d" => (today.AddDays(-6), today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today, today),
        };
    }

    public static (DateTime From, DateTime To) Previous(string range, DateTime today)
    {
        today = today.Date;
        return range switch
        {
            "7d" => (today.AddDays(-13), today.AddDays(-7)),
            "month" => PrevMonth(today),
            _ => (today.AddDays(-1), today.AddDays(-1)),
        };
    }

    private static (DateTime From, DateTime To) PrevMonth(DateTime today)
    {
        var firstThis = new DateTime(today.Year, today.Month, 1);
        var lastPrev = firstThis.AddDays(-1);
        return (new DateTime(lastPrev.Year, lastPrev.Month, 1), lastPrev);
    }
}
