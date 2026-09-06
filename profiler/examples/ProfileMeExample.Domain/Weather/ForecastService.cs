namespace ProfileMeExample.Domain.Weather;

public sealed record DayForecast(DateOnly Day, int HighCelsius, int LowCelsius, string Outlook);

/// <summary>
/// Downloads a week of forecasts, one day per request.
/// </summary>
/// <remarks>
/// Defect on purpose: the seven requests are awaited one after the other, so the screen
/// waits seven round trips when one would do. Sampling shows almost nothing - no thread is
/// busy - and that is the clue: the time is spent awaiting. An instrumenting session
/// reports <c>GetWeekAsync (async body)</c> with eight resumptions and a self time that is
/// a fraction of the wall clock the screen shows; the gap is the waiting. Hoisting the
/// requests into <c>Task.WhenAll</c> would cut the wall clock to one round trip.
/// </remarks>
public sealed class ForecastService
{
    private static readonly TimeSpan RoundTrip = TimeSpan.FromMilliseconds(300);

    public async Task<IReadOnlyList<DayForecast>> GetWeekAsync(DateOnly from)
    {
        var week = new List<DayForecast>(7);
        for (var i = 0; i < 7; i++)
            week.Add(await FetchDayAsync(from.AddDays(i)));
        return week;
    }

    private static async Task<DayForecast> FetchDayAsync(DateOnly day)
    {
        var payload = await DownloadAsync(day);
        return Decode(day, payload);
    }

    private static async Task<string> DownloadAsync(DateOnly day)
    {
        await Task.Delay(RoundTrip);
        var seed = day.DayNumber;
        return $"{day:yyyyMMdd}|{15 + seed % 12}|{5 + seed % 7}|{(seed % 3 == 0 ? "Rain" : "Sunny")}|{new string('x', 20000)}";
    }

    /// <summary>The only CPU work of the feature: a checksum over the padding, then the split.</summary>
    private static DayForecast Decode(DateOnly day, string payload)
    {
        var checksum = 0;
        for (var round = 0; round < 40; round++)
            foreach (var c in payload)
                checksum = unchecked(checksum * 31 + c);

        var fields = payload.Split('|');
        return new DayForecast(day, int.Parse(fields[1]), int.Parse(fields[2]), checksum == int.MinValue ? "Unknown" : fields[3]);
    }
}
