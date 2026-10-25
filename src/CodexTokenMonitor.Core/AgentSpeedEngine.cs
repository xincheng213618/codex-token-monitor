// Interval weighting, response upserts and estimates follow zoefix/agent-speed (MIT).
namespace CodexTokenMonitor;

/// <summary>Deterministic calculations; a single sampler worker owns this engine.</summary>
internal sealed class AgentSpeedEngine
{
    private readonly Dictionary<string, AgentSpeedResponse> responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSpeedAgent> agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> nativeOwners = new(StringComparer.Ordinal);
    private long revision;
    private (long Revision, DateTimeOffset Day, AgentSpeedMetric Metric, UsageSource? Source, int MeasuredCount)? peakKey;
    private double? cachedPeak;

    public void Add(AgentSpeedResponse response)
    {
        var key = $"{(int)response.Source}:{response.Key}";
        if (response.NativeResponseId is { Length: > 0 } nativeId)
        {
            var nativeKey = $"{(int)response.Source}:{nativeId}";
            if (nativeOwners.TryGetValue(nativeKey, out var owner))
            {
                if (owner != key && responses.TryGetValue(owner, out var canonical) && canonical.AgentId != response.AgentId && responses.Remove(key)) revision++;
                key = owner;
            }
            else nativeOwners[nativeKey] = key;
        }
        if (responses.TryGetValue(key, out var old))
        {
            if (!string.Equals(old.AgentId, response.AgentId, StringComparison.OrdinalIgnoreCase)) return;
            response = response with
            {
                Start = old.Start ?? response.Start, End = old.End > response.End ? old.End : response.End,
                OutputTokens = Math.Max(old.OutputTokens, response.OutputTokens), InputTokens = Math.Max(old.InputTokens, response.InputTokens),
                Model = string.IsNullOrWhiteSpace(response.Model) || response.Model == "未知模型" ? old.Model : response.Model,
                ExecutionMode = response.ExecutionMode ?? old.ExecutionMode, Final = old.Final || response.Final,
                Key = old.Key, NativeResponseId = response.NativeResponseId ?? old.NativeResponseId
            };
            if (old == response) return;
        }
        responses[key] = response; revision++;
    }

    public void Update(AgentSpeedAgent agent) => agents[agent.Id] = agent;
    public void RemoveAgent(string id) => agents.Remove(id);
    internal IReadOnlyList<AgentSpeedResponse> Responses => responses.Values.ToArray();
    internal bool CanAccept(UsageSource source, string key, string agentId)
    {
        var qualified = $"{(int)source}:{key}";
        if (nativeOwners.TryGetValue(qualified, out var canonical)) qualified = canonical;
        return !responses.TryGetValue(qualified, out var old) || string.Equals(old.AgentId, agentId, StringComparison.OrdinalIgnoreCase);
    }

    public AgentSpeedSnapshot Snapshot(DateTimeOffset now, DateTimeOffset dayStart,
        AgentSpeedMetric metric = AgentSpeedMetric.Output, UsageSource? source = null)
    {
        var retainFrom = dayStart < now.AddMinutes(-30) ? dayStart : now.AddMinutes(-30);
        var expired = responses.Where(p => p.Value.End < retainFrom).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in expired) responses.Remove(key);
        if (expired.Count > 0)
            foreach (var alias in nativeOwners.Where(p => expired.Contains(p.Value)).Select(p => p.Key).ToArray()) nativeOwners.Remove(alias);
        var known = responses.Values.Where(r => r.End <= now && (source is null || r.Source == source)).ToArray();
        var today = known.Where(r => r.End >= dayStart).ToArray();
        var valid = known.Where(r => r.Final && r.DurationSeconds is not null && r.End >= now.AddMinutes(-30))
            .OrderByDescending(r => r.End).ToArray();
        var samples = valid.Select(r => (Start: r.EffectiveStart!.Value, End: r.End,
            Measured: r.Speed(metric)!.Value, Estimated: 0d)).ToList();
        var latestEnds = known.GroupBy(r => r.AgentId).ToDictionary(g => g.Key, g => g.Max(r => r.End), StringComparer.OrdinalIgnoreCase);

        double? Reference(AgentSpeedAgent agent)
        {
            var matching = valid.Where(r => r.Source == agent.Source && r.Model == agent.Model && r.ExecutionMode == agent.ExecutionMode);
            var own = matching.Where(r => r.AgentId == agent.Id && r.End >= now.AddMinutes(-10)).Take(3).ToArray();
            return Average(own.Length > 0 ? own : agent.Model is "未知模型" or "auto" ? [] : matching.Take(10), metric);
        }
        foreach (var pending in known.Where(r => !r.Final && r.DurationSeconds is not null && r.End >= now.AddMinutes(-5)))
        {
            var owner = agents.GetValueOrDefault(pending.AgentId) ?? new(pending.AgentId, pending.AgentId, pending.Model, false,
                AgentSpeedPhase.Idle, pending.End, null, pending.Source, pending.ExecutionMode);
            if (Reference(owner) is { } speed) samples.Add((pending.EffectiveStart!.Value, pending.End, 0, speed));
        }
        var summaries = new List<AgentSpeedAgentSummary>();
        foreach (var agent in agents.Values.Where(a => source is null || a.Source == source))
        {
            var fresh = agent.LastActivity is { } activity && activity <= now.AddSeconds(5) && now - activity <= TimeSpan.FromMinutes(5);
            var phase = fresh ? agent.Phase : AgentSpeedPhase.Idle;
            if (phase == AgentSpeedPhase.Idle && (agent.LastActivity is null || agent.LastActivity < now.AddMinutes(-10))) continue;
            var measured = valid.Where(r => r.AgentId == agent.Id).Sum(r => r.Speed(metric)!.Value * Overlap(r.EffectiveStart!.Value, r.End, now.AddSeconds(-3), now)) / 3;
            var estimated = 0d;
            if (phase == AgentSpeedPhase.Generating && agent.RequestStart is { } requestStart && requestStart <= now && Reference(agent) is { } speed)
            {
                var start = latestEnds.TryGetValue(agent.Id, out var ownEnd) && ownEnd > requestStart ? ownEnd : requestStart;
                samples.Add((start, now, 0, speed));
                estimated = speed * Overlap(start, now, now.AddSeconds(-3), now) / 3;
            }
            foreach (var pending in known.Where(r => r.AgentId == agent.Id && !r.Final && r.DurationSeconds is not null))
                if (Reference(agent) is { } partialSpeed) estimated += partialSpeed * Overlap(pending.EffectiveStart!.Value, pending.End, now.AddSeconds(-3), now) / 3;
            if (agent.LastActivity >= now.AddMinutes(-10) || phase != AgentSpeedPhase.Idle)
                summaries.Add(new(agent, phase, measured + estimated, measured + estimated > 0 ? estimated / (measured + estimated) : 0));
        }
        var liveMeasured = samples.Sum(s => s.Measured * Overlap(s.Start, s.End, now.AddSeconds(-3), now)) / 3;
        var liveEstimated = samples.Sum(s => s.Estimated * Overlap(s.Start, s.End, now.AddSeconds(-3), now)) / 3;
        var history = new AgentSpeedPoint[300];
        var lastSecond = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        for (var index = 0; index < history.Length; index++)
        {
            var start = lastSecond.AddSeconds(index - history.Length + 1);
            var end = start.AddSeconds(1) < now ? start.AddSeconds(1) : now;
            history[index] = new(start, samples.Sum(s => s.Measured * Overlap(s.Start, s.End, start, end)),
                samples.Sum(s => s.Estimated * Overlap(s.Start, s.End, start, end)));
        }
        var peakIdentity = (revision, dayStart, metric, source, today.Count(r => r.Final && r.DurationSeconds is not null));
        if (peakKey != peakIdentity) { cachedPeak = Peak(today, dayStart, metric); peakKey = peakIdentity; }
        return new(now, dayStart, liveMeasured + liveEstimated, liveMeasured + liveEstimated > 0 ? liveEstimated / (liveMeasured + liveEstimated) : 0,
            today.OrderByDescending(r => r.End).FirstOrDefault(), Average(today, metric), today.Length, today.Count(r => r.Final && r.DurationSeconds is not null),
            today.GroupBy(r => (r.Source, r.Model, r.ExecutionMode)).Select(group => new AgentSpeedModelSummary(group.Key.Model, group.Count(),
                group.Count(r => r.Final && r.DurationSeconds is not null), group.Sum(r => Math.Max(0, r.OutputTokens)), Average(group, metric),
                group.Key.Source, group.Key.ExecutionMode, group.Sum(r => Math.Max(0, r.InputTokens)))).OrderByDescending(r => r.OutputTokens).ToArray(),
            BuildTree(summaries), history, Metric: metric, MeasuredPeak: cachedPeak, UnfinalizedRequests: today.Count(r => !r.Final));
    }

    private IReadOnlyList<AgentSpeedAgentSummary> BuildTree(List<AgentSpeedAgentSummary> active)
    {
        var bySession = agents.Values.GroupBy(a => (a.Source, a.SessionId)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id, StringComparer.Ordinal).First());
        var rows = active.ToDictionary(r => r.Agent.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var original in active)
        {
            var current = original.Agent; var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Id };
            while (current.ParentSessionId is { } parentSession && bySession.TryGetValue((current.Source, parentSession), out var parent) && visited.Add(parent.Id))
            { rows.TryAdd(parent.Id, new(parent, AgentSpeedPhase.Idle, 0, 0)); current = parent; }
        }
        foreach (var (id, row) in rows.ToArray())
        {
            var current = row.Agent; var depth = 0; string? parentId = null;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            while (current.ParentSessionId is { } parentSession && bySession.TryGetValue((current.Source, parentSession), out var parent) && rows.ContainsKey(parent.Id))
            {
                if (!visited.Add(parent.Id)) { depth = 0; parentId = null; break; }
                parentId ??= parent.Id; depth++; current = parent;
            }
            rows[id] = row with { Depth = depth, ParentId = parentId };
        }
        var output = new List<AgentSpeedAgentSummary>();
        void Append(AgentSpeedAgentSummary row)
        {
            output.Add(row);
            foreach (var child in rows.Values.Where(r => r.ParentId == row.Agent.Id).OrderByDescending(r => r.Phase == AgentSpeedPhase.Generating).ThenByDescending(r => r.Live)) Append(child);
        }
        string RootOf(AgentSpeedAgentSummary row)
        {
            while (row.ParentId is { } parentId && rows.TryGetValue(parentId, out var parent)) row = parent;
            return row.Agent.Id;
        }
        var groups = rows.Values.GroupBy(RootOf).ToDictionary(group => group.Key, group => new
        {
            Generating = group.Any(row => row.Phase == AgentSpeedPhase.Generating), Live = group.Sum(row => row.Live),
            Activity = group.Max(row => row.Agent.LastActivity)
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var root in rows.Values.Where(r => r.ParentId is null).OrderByDescending(r => groups[r.Agent.Id].Generating)
            .ThenByDescending(r => groups[r.Agent.Id].Live).ThenByDescending(r => groups[r.Agent.Id].Activity).ThenBy(r => r.Agent.Id, StringComparer.Ordinal)) Append(root);
        return output;
    }

    private static double? Peak(IEnumerable<AgentSpeedResponse> today, DateTimeOffset dayStart, AgentSpeedMetric metric)
    {
        double[]? buckets = null;
        foreach (var response in today.Where(r => r.Final && r.DurationSeconds is not null))
        {
            buckets ??= new double[86_400];
            var start = response.EffectiveStart!.Value; var end = response.End;
            var first = Math.Max(0, (int)Math.Floor((start - dayStart).TotalSeconds));
            var last = Math.Min(buckets.Length - 1, (int)Math.Ceiling((end - dayStart).TotalSeconds) - 1);
            var speed = response.Speed(metric)!.Value;
            for (var second = first; second <= last; second++) buckets[second] += speed * Overlap(start, end, dayStart.AddSeconds(second), dayStart.AddSeconds(second + 1));
        }
        if (buckets is null) return null;
        var peak = 0d; var sum = 0d;
        for (var index = 0; index < buckets.Length; index++) { sum += buckets[index]; if (index >= 3) sum -= buckets[index - 3]; peak = Math.Max(peak, sum / 3); }
        return peak;
    }

    private static double? Average(IEnumerable<AgentSpeedResponse> source, AgentSpeedMetric metric)
    {
        var tokens = 0d; var seconds = 0d;
        foreach (var response in source) if (response.Final && response.DurationSeconds is { } duration) { tokens += response.Tokens(metric); seconds += duration; }
        return seconds > 0 ? tokens / seconds : null;
    }

    private static double Overlap(DateTimeOffset a0, DateTimeOffset a1, DateTimeOffset b0, DateTimeOffset b1) => Math.Max(0, ((a1 < b1 ? a1 : b1) - (a0 > b0 ? a0 : b0)).TotalSeconds);
}
