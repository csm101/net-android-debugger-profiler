namespace NetAndroidProfiler.Cli;

/// <summary>
/// Background work the control service runs on behalf of a frontend and that is not a
/// profiling session: building and installing an app, installing a missing tool. A build
/// takes minutes and its output is the whole point, so a job is started, then polled -
/// the GUI stays responsive and shows the log as it arrives, exactly as it does for a
/// running session.
/// </summary>
internal sealed class JobRegistry : IDisposable
{
    /// <summary>Enough of a build log to diagnose it; older lines are dropped, not kept forever.</summary>
    private const int MaxLines = 4000;

    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private int _counter;

    public Job Start(string kind, Func<Action<string>, CancellationToken, Task<int>> work)
    {
        Job job;
        lock (_gate)
        {
            job = new Job($"{kind}-{++_counter}", kind);
            _jobs[job.Id] = job;
        }
        job.Run(work);
        return job;
    }

    public Job? Find(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public void Dispose()
    {
        Job[] all;
        lock (_gate) { all = [.. _jobs.Values]; _jobs.Clear(); }
        foreach (var job in all) job.Dispose();
    }

    internal sealed class Job(string id, string kind) : IDisposable
    {
        private readonly List<string> _log = [];
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private int _dropped;

        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string State { get; private set; } = "Running";
        public int? ExitCode { get; private set; }
        public string? Error { get; private set; }
        public Task? Task { get; private set; }

        public void Run(Func<Action<string>, CancellationToken, Task<int>> work)
        {
            Task = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    int code = await work(Append, _cts.Token).ConfigureAwait(false);
                    Finish(code == 0 ? "Succeeded" : "Failed", code, null);
                }
                catch (OperationCanceledException)
                {
                    Append("Canceled.");
                    Finish("Canceled", null, null);
                }
                catch (Exception e)
                {
                    Append(e.Message);
                    Finish("Failed", null, e.Message);
                }
            });
        }

        public void Cancel() => _cts.Cancel();

        /// <summary>The log from <paramref name="from"/> on, with the index the first returned line has.</summary>
        public (int from, int total, IReadOnlyList<string> lines) LogFrom(int from)
        {
            lock (_gate)
            {
                int first = Math.Clamp(from - _dropped, 0, _log.Count);
                return (first + _dropped, _log.Count + _dropped, _log.GetRange(first, _log.Count - first));
            }
        }

        private void Append(string line)
        {
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > MaxLines)
                {
                    int drop = _log.Count - MaxLines;
                    _log.RemoveRange(0, drop);
                    _dropped += drop;
                }
            }
        }

        private void Finish(string state, int? exitCode, string? error)
        {
            lock (_gate)
            {
                State = state;
                ExitCode = exitCode;
                Error = error;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
    }
}
