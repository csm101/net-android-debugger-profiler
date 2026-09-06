using ProfileMeExample.Pages;

namespace ProfileMeExample.Scenarios;

/// <summary>
/// Every screen of the app and its guide. The README's "Example app" chapter is the
/// long form of the same list; keep the two in step.
/// </summary>
public static class ScenarioCatalog
{
    public const string SlowSearch = "slow-search";
    public const string FrozenUi = "frozen-ui";
    public const string ChattyPricing = "chatty-pricing";
    public const string AllocationStorm = "allocation-storm";
    public const string LeakyDashboard = "leaky-dashboard";
    public const string AsyncWaterfall = "async-waterfall";
    public const string ReenumeratedQuery = "reenumerated-query";

    public static IReadOnlyList<ScenarioInfo> All { get; } =
    [
        new(SlowSearch,
            "Slow search",
            "Typing in the search box takes seconds to answer.",
            "CPU sampling: hotspots, callers, source annotation",
            typeof(SlowSearchPage),
            """
            Run a sampling session (mode: sampling, 20 seconds) and search two or three times while it runs.

            Hotspots, sorted by exclusive CPU: NaiveCatalogSearch.EditDistance and NaiveCatalogSearch.Score are at the top, with Search above them by inclusive time. That is the whole diagnosis: the time is in comparing words, not in the UI.

            Callers of EditDistance: one caller, Score, once per word per product. Annotate NaiveCatalogSearch.cs with the session's symbols to see the figures next to the methods.

            Remember that a method's exclusive samples include its very short callees (ToLowerInvariant, Split), so read "EditDistance" as "EditDistance and its helpers".

            The fix is not in the profiler's job description, but it is obvious once the picture is there: tokenize and lower-case the catalog once, and skip words whose length rules out a match.
            """),

        new(FrozenUi,
            "Frozen button",
            "The screen stops responding for a few seconds after the tap; nothing is computing.",
            "CPU sampling: blocked time against CPU time, threads",
            typeof(FrozenUiPage),
            """
            Run a sampling session and tap Fetch balance a few times.

            Hotspots: LegacyGateway.FetchBalance has many inclusive samples but almost none in the CPU columns. The plain columns count every sample, the *_cpu columns drop the samples taken while the thread was blocked. A large gap between the two means the thread was waiting, not working.

            Threads: the waiting happens on the main thread - the UI cannot paint while it waits.

            LegacyGateway.Parse is the contrast: its samples are CPU samples, and it is the only genuine CPU cost of the feature.

            The blocking call is sync-over-async (GetAwaiter().GetResult() on a Task). Awaiting it, or moving the work off the UI thread, is the fix.
            """),

        new(ChattyPricing,
            "Chatty pricing",
            "Totalling an order takes seconds, although every single method is fast.",
            "Instrumenting: call counts, call tree, callers and callees, kept results",
            typeof(ChattyPricingPage),
            """
            Sampling would point at PriceList.GetUnitPrice, and it would be right - but it cannot say why a fast method costs so much. Run an instrumenting session (mode: instrumenting, callspec N:ProfileMeExample.Domain, assemblies ProfileMeExample.Domain) and total the order once.

            Timings: GetUnitPrice and TaxRules.RateFor are called tens of thousands of times for an order of 2,000 lines. The Calls column is the diagnosis: one lookup per unit instead of one per line.

            Call tree: Build -> GetUnitPrice, with the count and the inclusive time on the edge. Callers of GetUnitPrice: a single caller, Build.

            Snapshot and Archive keep the results of one run so the next can be compared with it - AQTime's Get Results. Clear resets the counters while the app keeps running.

            The fix: look prices and rates up once per line and multiply by the quantity.
            """),

        new(AllocationStorm,
            "Allocation storm",
            "Exporting a report stutters and the app is briefly unresponsive; memory never grows for good.",
            "Allocations by type and by allocating method",
            typeof(AllocationStormPage),
            """
            Run an instrumenting session with allocation tracking (callspec N:ProfileMeExample.Domain) and export once.

            Allocations by type: System.String dominates by a wide margin, followed by boxed Int32 and Decimal, List<Object> and the enumerators of Select and Join.

            Allocations by site: the strings are attributed to SalesReportBuilder.BuildCsv - one new copy of the whole report per row, the += on a string - and the boxes and lists to FormatRow.

            The runtime provider engine reports sizes as well; the weaver reports counts. Both show the same culprits.

            The fix: a StringBuilder for the report, and formatting numbers without boxing them.
            """),

        new(LeakyDashboard,
            "Leaky dashboard",
            "Every visit to the dashboard leaves a little more memory behind; notifications reach widgets that are no longer on screen.",
            "Memory: heap snapshots and the growth between them",
            typeof(LeakyDashboardPage),
            """
            Run a memory session with two snapshots about a minute apart (mode: heap, snapshots 2, interval 60 seconds, launch attach). Between the snapshots, open and close the dashboard five or six times.

            Heap diff: DashboardWidget grows by exactly one instance per cycle, and Byte[] grows by one array of 256 KB per cycle. Types that grow in lockstep with something the user did are the leak.

            The screen shows the same thing from the inside: Publish reports how many subscribers received the notification - one per widget ever created, not one.

            The static event NotificationHub.Published holds every widget through the handler it never removed. Unsubscribe when the widget is dropped, or hold the subscription weakly.
            """),

        new(AsyncWaterfall,
            "Async waterfall",
            "Loading the week's forecast takes over two seconds; the device is idle the whole time.",
            "Instrumenting: async methods, self time against wall clock",
            typeof(AsyncWaterfallPage),
            """
            Sampling first: nothing is hot. No thread has meaningful CPU samples while the forecast loads - the time is spent awaiting, not computing. That alone is worth knowing.

            Then an instrumenting session (callspec N:ProfileMeExample.Domain). An async method appears twice: the stub, whose time is the synchronous part up to the first await, and "<method> (async body)", the state machine, whose calls are its resumptions and whose time excludes the awaits.

            Timings: GetWeekAsync (async body) resumes eight times - once to start, once after each of seven awaited days - and its self time is a small fraction of the wall clock the screen shows. FetchDayAsync (async body) has seven calls; Decode is the only measurable CPU.

            Seven resumptions, one after the other, each waiting a full round trip: a waterfall. Starting the seven downloads together and awaiting Task.WhenAll costs one round trip.
            """),

        new(ReenumeratedQuery,
            "Re-enumerated query",
            "The reorder report takes three times longer than the query it is made of.",
            "Instrumenting: iterators, call counts of lazy sequences",
            typeof(ReenumeratedQueryPage),
            """
            Run an instrumenting session (callspec N:ProfileMeExample.Domain) and build the report once.

            Timings: StockQuery.LowStock (iterator body) is called about three times the number of low-stock lines - one call per item produced, plus the call that ends each pass - and NeedsReorder about three times the number of stock lines.

            The caller is ReorderReport.Build, which enumerates the lazy sequence three times: Any, Count and Sum each start the query from the beginning.

            When an iterator's call count is a multiple of the data it should walk once, look for repeated enumeration. One ToList() at the call site does the work once.
            """),
    ];

    public static ScenarioInfo Get(string id) =>
        All.FirstOrDefault(scenario => scenario.Id == id)
        ?? throw new KeyNotFoundException($"Unknown scenario '{id}'.");
}
