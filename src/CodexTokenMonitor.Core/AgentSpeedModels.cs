namespace CodexTokenMonitor;

internal enum AgentSpeedPhase { Idle, Generating, Tools }
internal enum AgentSpeedMetric { Output, TotalThroughput }

internal sealed record AgentSpeedResponse(
    string Key, string AgentId, string Model, DateTimeOffset? Start,
    DateTimeOffset End, long OutputTokens, long InputTokens,
    UsageSource Source = UsageSource.Codex, bool Final = true, string? ExecutionMode = null, string? NativeResponseId = null)
{
    public double? DurationSeconds => Start is { } start && End >= start && End - start <= TimeSpan.FromMinutes(15)
        ? Math.Max(.5, (End - start).TotalSeconds) : null;
    public double? OutputPerSecond => Speed(AgentSpeedMetric.Output);
    public DateTimeOffset? EffectiveStart => DurationSeconds is { } seconds ? End.AddSeconds(-seconds) : null;
    public double? Speed(AgentSpeedMetric metric) => Final && DurationSeconds is { } seconds ? Tokens(metric) / seconds : null;
    public double Tokens(AgentSpeedMetric metric) => Math.Max(0, OutputTokens) + (metric == AgentSpeedMetric.TotalThroughput ? (double)Math.Max(0, InputTokens) : 0);
}

internal sealed record AgentSpeedAgent(
    string Id, string SessionId, string Model, bool IsSubagent, AgentSpeedPhase Phase,
    DateTimeOffset? LastActivity, DateTimeOffset? RequestStart, UsageSource Source = UsageSource.Codex,
    string? ExecutionMode = null, string? ParentSessionId = null);

internal sealed record AgentSpeedPoint(DateTimeOffset Time, double Measured, double Estimated);
internal sealed record AgentSpeedModelSummary(string Model, int Requests, int MeasuredRequests, long OutputTokens, double? Average,
    UsageSource Source = UsageSource.Codex, string? ExecutionMode = null, long InputTokens = 0);
internal sealed record AgentSpeedAgentSummary(AgentSpeedAgent Agent, AgentSpeedPhase Phase, double Live, double EstimatedShare,
    int Depth = 0, string? ParentId = null);
internal sealed record AgentSpeedSnapshot(
    DateTimeOffset Now, DateTimeOffset DayStart, double Live, double EstimatedShare,
    AgentSpeedResponse? Last, double? Average, int Requests, int MeasuredRequests,
    IReadOnlyList<AgentSpeedModelSummary> Models, IReadOnlyList<AgentSpeedAgentSummary> Agents,
    IReadOnlyList<AgentSpeedPoint> History, int UnreadableFiles = 0,
    AgentSpeedMetric Metric = AgentSpeedMetric.Output, double? MeasuredPeak = null, int UnfinalizedRequests = 0);

internal interface IAgentSpeedParser
{
    AgentSpeedAgent Agent { get; }
    IReadOnlyList<AgentSpeedResponse> ReadResponses(string line);
    void Reset();
}

internal interface IAgentSpeedSampler : IDisposable
{
    UsageSource? Source { get; }
    event EventHandler? Changed;
    AgentSpeedSnapshot Read(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token = default,
        AgentSpeedMetric metric = AgentSpeedMetric.Output);
}
