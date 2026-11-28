using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class GeminiModuleTests : IDisposable
{
    private readonly string root;

    public GeminiModuleTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"GeminiModuleTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("GeminiModuleTests-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete outside isolated test root.");
        }

        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    [Fact]
    public void UsageReader_ReadsAntigravityAndCliWithoutDoubleCountingFullTranscript()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));

        var geminiLogs = UsageLogPaths.GetOverrideRoot(UsageSource.Gemini)!;
        var agyDir = Path.Combine(geminiLogs, "antigravity", "brain", "conv-main-1111", ".system_generated", "logs");
        Directory.CreateDirectory(agyDir);

        var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(8));
        var agyLines = new[]
        {
            JsonSerializer.Serialize(new
            {
                step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", status = "DONE",
                created_at = day.AddHours(9).ToUniversalTime(), content = "<USER_REQUEST>Hello</USER_REQUEST>"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", status = "DONE",
                created_at = day.AddHours(9).AddSeconds(2).ToUniversalTime(), model = "gemini-2.5-pro",
                input_tokens = 12_000, cache_read_tokens = 8_000, output_tokens = 500
            }),
            // GENERIC output mentioning PLANNER_RESPONSE inside escaped text must not be parsed as usage.
            JsonSerializer.Serialize(new
            {
                step_index = 2, source = "MODEL", type = "GENERIC", status = "DONE",
                created_at = day.AddHours(9).AddSeconds(10).ToUniversalTime(),
                content = "{\"type\":\"PLANNER_RESPONSE\",\"input_tokens\":999999,\"output_tokens\":999999}"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 3, source = "SYSTEM", type = "CHECKPOINT", status = "DONE",
                created_at = day.AddHours(10).ToUniversalTime(), content = "Conversation summary",
                input_tokens = 1_000, cache_read_tokens = 44_000, output_tokens = 600
            })
        };
        File.WriteAllLines(Path.Combine(agyDir, "transcript.jsonl"), agyLines);
        File.WriteAllLines(Path.Combine(agyDir, "transcript_full.jsonl"), agyLines);

        var cliDir = Path.Combine(geminiLogs, "tmp", "proj-hash", "chats");
        Directory.CreateDirectory(cliDir);
        var cliJson = JsonSerializer.Serialize(new
        {
            sessionId = "cli-sess-1",
            startTime = day.AddHours(11).ToUniversalTime(),
            lastUpdated = day.AddHours(11).AddMinutes(2).ToUniversalTime(),
            messages = new object[]
            {
                new { id = "u1", type = "user", timestamp = day.AddHours(11).ToUniversalTime(), content = "fix bug" },
                new
                {
                    id = "m1", type = "gemini", timestamp = day.AddHours(11).AddSeconds(6).ToUniversalTime(),
                    model = "gemini-2.5-flash",
                    tokens = new { input = 5_000, output = 300, cached = 3_000, thoughts = 100, tool = 200, total = 5_600 }
                }
            }
        });
        File.WriteAllText(Path.Combine(cliDir, "session-2026-11-20.json"), cliJson);

        var reader = UsageSourceReaders.For(UsageSource.Gemini);
        var summary = reader.ReadRange(day, day.AddDays(1), includeLiveToday: true);

        Assert.Equal(3, summary.Events);
        Assert.Equal(70_200, summary.InputTokens);
        Assert.Equal(55_000, summary.CachedInputTokens);
        Assert.Equal(15_200, summary.UncachedInputTokens);
        Assert.Equal(1_500, summary.OutputTokens);
        Assert.Equal(100, summary.ReasoningOutputTokens);
        Assert.Equal(71_700, summary.TotalTokens);
        Assert.Contains("gemini-2.5-pro", summary.ModelUsage.Keys);
        Assert.Contains("gemini-2.5-flash", summary.ModelUsage.Keys);
        // CHECKPOINT inherits gemini-2.5-pro from the preceding PLANNER_RESPONSE so no tokens remain unattributed.
        Assert.Equal(2, summary.ModelUsage["gemini-2.5-pro"].Events);
        var costEstimate = CodexModelCost.Estimate(summary, PricePresetGroups.Gemini);
        Assert.True(costEstimate.IsComplete);
        Assert.Equal(0, costEstimate.UnpricedTokens);

        Assert.Empty(reader.GetIncompleteHistoricalDays(day, day));
        Assert.Equal(71_700, reader.ReadCachedRange(day, day.AddDays(1)).TotalTokens);
    }

    [Fact]
    public void T3AntigravityAndSystemMessageSubagents_AreDiscoveredClassifiedAndUpdatedOnAppend()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "t3-cache"));
        var isolatedHome = Path.Combine(root, "t3-env");
        var t3Userdata = Path.Combine(isolatedHome, ".t3", "userdata");
        var t3Brain = Path.Combine(t3Userdata, "providers", "antigravity", "inst-hash-01", "antigravity-acp", "brain");
        var t3MainLogs = Path.Combine(t3Brain, "t3-main-conv", ".system_generated", "logs");
        var agySubLogs = Path.Combine(isolatedHome, "antigravity", "brain", "agy-sys-sub", ".system_generated", "logs");
        Directory.CreateDirectory(t3MainLogs);
        Directory.CreateDirectory(agySubLogs);

        var discoveredRoots = new List<string>();
        GeminiUsageReader.AddT3AntigravityRoots(t3Userdata, discoveredRoots);
        Assert.Contains(t3Brain, discoveredRoots, StringComparer.OrdinalIgnoreCase);

        var day = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(8));
        var t3Transcript = Path.Combine(t3MainLogs, "transcript.jsonl");
        File.WriteAllLines(t3Transcript,
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", created_at = day.AddHours(12).ToUniversalTime(), content = "<USER_REQUEST>t3 hello</USER_REQUEST>" }),
            JsonSerializer.Serialize(new { step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = day.AddHours(12).AddSeconds(2).ToUniversalTime(), model = "gemini-4-carbon-high", input_tokens = 20_000, cache_read_tokens = 0, output_tokens = 1_000, tool_calls = new[] { new { name = "read_file" } } }),
            JsonSerializer.Serialize(new { step_index = 2, source = "MODEL", type = "GENERIC", created_at = day.AddHours(12).AddSeconds(12).ToUniversalTime(), content = "Completed At: 2026-11-24T04:00:13Z" })
        ]);

        // Real Antigravity invoke_subagent transcript starts with step_index=0 SYSTEM_MESSAGE (no USER_INPUT).
        File.WriteAllLines(Path.Combine(agySubLogs, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "SYSTEM", type = "SYSTEM_MESSAGE", created_at = day.AddHours(14).ToUniversalTime(), content = "The following is a <SYSTEM_MESSAGE> not actually sent by the user." }),
            JsonSerializer.Serialize(new { step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = day.AddHours(14).AddSeconds(1).ToUniversalTime(), model = "gemini-4-carbon-high", input_tokens = 10_000, cache_read_tokens = 0, output_tokens = 500, tool_calls = new[] { new { name = "read_file" } } }),
            JsonSerializer.Serialize(new { step_index = 2, source = "MODEL", type = "GENERIC", created_at = day.AddHours(14).AddSeconds(6).ToUniversalTime(), content = "Completed At: 2026-11-24T06:00:07Z" }),
            JsonSerializer.Serialize(new { step_index = 3, source = "SYSTEM", type = "CHECKPOINT", created_at = day.AddHours(14).AddSeconds(10).ToUniversalTime(), input_tokens = 2_000, cache_read_tokens = 8_000, output_tokens = 300 })
        ]);

        var firstScan = GeminiUsageReader.ReadTransientDetailRowsWithStatus(day, day.AddDays(1), homeOverride: isolatedHome);
        Assert.True(firstScan.IsComplete);
        Assert.Equal(3, firstScan.Rows.Count);

        // Appending a new step to T3 transcript invalidates its cached file signature and picks up the new usage.
        File.AppendAllLines(t3Transcript,
        [
            JsonSerializer.Serialize(new { step_index = 3, source = "MODEL", type = "PLANNER_RESPONSE", created_at = day.AddHours(12).AddSeconds(20).ToUniversalTime(), model = "gemini-4-carbon-high", input_tokens = 5_000, cache_read_tokens = 20_000, output_tokens = 400 })
        ]);
        var secondScan = GeminiUsageReader.ReadTransientDetailRowsWithStatus(day, day.AddDays(1), homeOverride: isolatedHome);
        Assert.Equal(4, secondScan.Rows.Count);

        var stats = GeminiActivityReader.ReadStats(day, day.AddDays(1), day.AddHours(18), homeOverride: isolatedHome);
        Assert.Equal(1, stats.MainSessions);
        Assert.Equal(1, stats.SubagentSessions);
        Assert.Equal(2, stats.AntigravitySessions);
        Assert.Equal(1, stats.T3Sessions);
        Assert.Equal(2, stats.T3Events);
        Assert.Equal(2, stats.ToolCalls);

        var rhythm = GeminiRhythmReader.Read(day.AddDays(1), homeOverride: isolatedHome);
        Assert.Equal(1, rhythm.CountInRange(day, day.AddDays(1)));

        using var monitor = new AgentSpeedMonitor(isolatedHome, UsageSource.Gemini, watchFileSystem: false);
        var speedSnapshot = monitor.Read(day.AddHours(14).AddSeconds(15), day);
        var subAgent = Assert.Single(speedSnapshot.Agents, a => a.Agent.SessionId == "agy-sys-sub");
        Assert.True(subAgent.Agent.IsSubagent);
    }

    [Fact]
    public void Pricing_MatchesGemini4EffortVariantsUpgradesPlaceholdersAndDiscoversCustomModels()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "price-cache"));
        var settings = PriceSettingsStore.Defaults();
        Assert.Equal(("Google", "Gemini 4 Carbon"), (settings.GeminiPresets[0].Provider, settings.GeminiPresets[0].Model));

        // Verify untouched 0x placeholder for gemini-4-carbon-medium upgrades automatically to $2 / $0.10 / $10.
        settings.GeminiPresets.Add(new PricePreset
        {
            Group = PricePresetGroups.Gemini, Provider = "Google",
            Model = "gemini-4-carbon-medium", ModelId = "gemini-4-carbon-medium",
            CurrencySymbol = "$", UnitLabel = "USD / 1M tokens", Divisor = 1_000_000m,
            UncachedInput = 0, CachedInput = 0, CacheWriteInput = 0, Output = 0,
            Source = CodexModelCost.PlaceholderPriceSource
        });
        var normalized = PriceSettingsStore.Normalize(settings);
        var upgraded = Assert.Single(normalized.GeminiPresets, p => p.ModelId == "gemini-4-carbon-medium");
        Assert.Equal(2.00m, upgraded.UncachedInput);
        Assert.Equal(0.10m, upgraded.CachedInput);
        Assert.Equal(10.00m, upgraded.Output);
        PriceSettingsStore.Save(normalized);

        var bucket = new TokenUsageBucket { StartLocal = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(8)) };
        // gemini-4-carbon-high hits gemini-4-carbon via FamilyKey: 0.5M uncached * $2 + 1.0M cached * $0.10 + 0.1M output * $10 = 1.00 + 0.10 + 1.00 = $2.10
        bucket.Add(new TokenUsageEvent(bucket.StartLocal.AddHours(1), 1_500_000, 1_000_000, 100_000, 0, 1_600_000, "e1", ModelId: "gemini-4-carbon-high"));
        bucket.Add(new TokenUsageEvent(bucket.StartLocal.AddHours(2), 500_000, 0, 50_000, 0, 550_000, "e2", ModelId: "gemini-experimental-next"));

        var initialEstimate = CodexModelCost.Estimate(bucket, PricePresetGroups.Gemini);
        Assert.False(initialEstimate.IsComplete);
        Assert.Equal(2.10m, initialEstimate.KnownCost);
        Assert.Equal(1, initialEstimate.UnpricedEvents);

        var added = CodexModelCost.AddMissingGeminiPresets(normalized, bucket.ModelUsage.Keys);
        Assert.Equal(1, added);
        var placeholder = Assert.Single(normalized.GeminiPresets, p => p.ModelId == "gemini-experimental-next");
        Assert.True(CodexModelCost.IsPending(placeholder));
    }

    [Fact]
    public void QuotaAndCycleHistory_Estimate5hAnd7dCapacityFromStatuslineAndReadings()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "quota-cache"));
        var geminiHome = Path.Combine(root, "quota-home");
        var agyDir = Path.Combine(geminiHome, "antigravity", "brain", "conv-quota", ".system_generated", "logs");
        Directory.CreateDirectory(agyDir);

        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(8));
        var reset5h = now.AddHours(3);
        var reset7d = now.AddDays(4);

        // 1M uncached * $2 + 0.2M output * $10 = $4.00 Gemini 4 Carbon spend at 9:30.
        File.WriteAllLines(Path.Combine(agyDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", status = "DONE",
                created_at = now.AddMinutes(-30).ToUniversalTime(), model = "gemini-4-carbon-medium",
                input_tokens = 1_000_000, cache_read_tokens = 0, output_tokens = 200_000
            })
        ]);

        // Simulate Antigravity CLI statusLine payload: 5h remaining 80% (20% used), 7d remaining 90% (10% used).
        var statusLineRecord = JsonSerializer.Serialize(new
        {
            recorded_at = now.AddMinutes(-5).ToUnixTimeSeconds(),
            quota = new
            {
                gemini_5h = new { remaining_fraction = 0.80, reset_time = reset5h.ToUniversalTime().ToString("O") },
                gemini_7d = new { remaining_fraction = 0.90, reset_time = reset7d.ToUniversalTime().ToString("O") }
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(GeminiRateLimitLog.FilePath)!);
        File.WriteAllText(GeminiRateLimitLog.FilePath, statusLineRecord + "\n");

        var snapshot = GeminiActivityReader.ReadCurrent(now.Date, now, now, readings: [], homeOverride: geminiHome);
        Assert.NotNull(snapshot.FiveHourWindow);
        Assert.NotNull(snapshot.WeekWindow);
        Assert.Equal(20.0, snapshot.FiveHourWindow!.ReportedUsedPercent!.Value, precision: 3);
        Assert.Equal(10.0, snapshot.WeekWindow!.ReportedUsedPercent!.Value, precision: 3);

        GeminiQuotaEstimator.RecordSnapshot(snapshot, []);
        var readings = GeminiQuotaReadingStore.Load();
        Assert.Equal(2, readings.Count);

        var fiveRef = GeminiQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.FiveHour, snapshot.FiveHourWindow, readings);
        var weekRef = GeminiQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, snapshot.WeekWindow, readings);
        Assert.NotNull(fiveRef);
        Assert.NotNull(weekRef);
        // $4.00 / 20% = $20.00 (5h full capacity); $4.00 / 10% = $40.00 (7d full capacity)
        Assert.Equal(20.00m, fiveRef!.TotalCostUsd);
        Assert.Equal(40.00m, weekRef!.TotalCostUsd);

        var cycles = GeminiQuotaCycleHistory.Read(now, homeOverride: geminiHome);
        var currentCycle = Assert.Single(cycles);
        Assert.True(currentCycle.IsCurrent);
        Assert.Equal(40.00m, currentCycle.FullReferenceUsd);
    }

    [Fact]
    public void DesktopQuota_ParsesTheGeminiPanelAndKeepsTheWeeklyResetWhenSettingsHideIt()
    {
        var now = new DateTimeOffset(2026, 11, 24, 10, 8, 28, TimeSpan.FromHours(8));
        var menu = GeminiDesktopQuotaParser.Parse(
        [
            "Usage", "Gemini Models", "Weekly limit", "73", "% left", "Resets in 6d 22h",
            "5h limit", "0", "% left", "Resets in 3h 35m",
            "Claude and GPT models", "Weekly limit", "100", "% left", "5h limit", "100", "% left"
        ], now, null);

        Assert.NotNull(menu);
        var five = Assert.IsType<GeminiRateLimitWindow>(menu.FiveHour);
        var week = Assert.IsType<GeminiRateLimitWindow>(menu.SevenDay);
        Assert.Equal(100, five.UsedPercent);
        Assert.Equal(27, week.UsedPercent);
        Assert.Equal(60, five.ResetPrecisionSeconds);
        Assert.Equal(3600, week.ResetPrecisionSeconds);

        string[] settings =
        [
            "Gemini Models", "Weekly Limit Remaining",
            "You have hit your 5-hour limit, so the weekly limit does not currently apply. Your 5-hour limit will refresh in 3 hours, 35 minutes.",
            "73%", "Five Hour Limit Remaining", "You have hit your 5-hour limit, it will refresh in 3 hours, 35 minutes.", "0%",
            "Claude and GPT models", "Weekly Limit Remaining", "100%"
        ];
        var fromSettings = GeminiDesktopQuotaParser.Parse(settings, now.AddMinutes(10), menu);
        Assert.Equal(week, fromSettings?.SevenDay);
        Assert.Equal(five, fromSettings?.FiveHour);
        Assert.Null(GeminiDesktopQuotaParser.Parse(settings, now.AddMinutes(10), previous: null));
    }

    [Fact]
    public void StatuslineQuota_RetainsWeeklyIdentityAsResetApproachesAndIgnoresSeparateBuckets()
    {
        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(8));
        var data = JsonSerializer.Serialize(new
        {
            recorded_at = now.ToUnixTimeSeconds(),
            quota = new Dictionary<string, object>
            {
                ["gemini-argon-weekly"] = new { remaining_fraction = 0.01, reset_in_seconds = 60 },
                ["claude-5h"] = new { remaining_fraction = 0.12, reset_in_seconds = 120 },
                ["gemini-weekly"] = new { remaining_fraction = 0.73, reset_in_seconds = 180 },
                ["gemini-5h"] = new { remaining_fraction = 0.00, reset_in_seconds = 300 }
            }
        });
        var snapshot = Assert.Single(GeminiRateLimitLog.Parse([data]));
        Assert.Equal(100, snapshot.FiveHour?.UsedPercent);
        Assert.Equal(27, snapshot.SevenDay?.UsedPercent);
        Assert.Equal(now.AddMinutes(3), snapshot.SevenDay?.ResetLocal);
    }

    [Fact]
    public void SpeedMonitor_MeasuresAntigravityToolTurnsCliSessionsAndSubagentHierarchy()
    {
        var geminiHome = Path.Combine(root, "speed-home");
        var parentUuid = "11111111-2222-3333-4444-555555555555";
        var childUuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var parentDir = Path.Combine(geminiHome, "antigravity", "brain", parentUuid, ".system_generated", "logs");
        var childDir = Path.Combine(geminiHome, "antigravity", "brain", childUuid, ".system_generated", "logs");
        var cliDir = Path.Combine(geminiHome, "tmp", "p1", "chats");
        Directory.CreateDirectory(parentDir);
        Directory.CreateDirectory(childDir);
        Directory.CreateDirectory(cliDir);

        var day = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(8));
        var t0 = day.AddHours(10);

        File.WriteAllLines(Path.Combine(parentDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", created_at = t0.ToUniversalTime(), content = "<USER_REQUEST>run subagent</USER_REQUEST>" }),
            JsonSerializer.Serialize(new
            {
                step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = t0.ToUniversalTime(),
                model = "gemini-4-carbon-medium", input_tokens = 10_000, cache_read_tokens = 0, output_tokens = 400,
                tool_calls = new[] { new { name = "invoke_subagent", args = new { Role = "Researcher" } } }
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2, source = "MODEL", type = "GENERIC", created_at = t0.AddSeconds(8).ToUniversalTime(),
                content = $"conversationId: {childUuid}\nCompleted At: {t0.AddSeconds(12):O}"
            })
        ]);

        File.WriteAllLines(Path.Combine(childDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "SYSTEM", type = "USER_INPUT", created_at = t0.AddSeconds(9).ToUniversalTime(), content = "Subagent task prompt" }),
            JsonSerializer.Serialize(new
            {
                step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = t0.AddSeconds(9).ToUniversalTime(),
                model = "gemini-2.5-flash", input_tokens = 4_000, cache_read_tokens = 0, output_tokens = 200,
                tool_calls = new[] { new { name = "read_file" } }
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2, source = "MODEL", type = "GENERIC", created_at = t0.AddSeconds(11).ToUniversalTime(),
                content = $"Completed At: {t0.AddSeconds(11).AddMilliseconds(500):O}"
            })
        ]);

        File.WriteAllText(Path.Combine(cliDir, "session-cli.json"), JsonSerializer.Serialize(new
        {
            sessionId = "cli-speed-1",
            messages = new object[]
            {
                new { id = "u1", type = "user", timestamp = t0.AddSeconds(1).ToUniversalTime() },
                new { id = "g1", type = "gemini", timestamp = t0.AddSeconds(5).ToUniversalTime(), model = "gemini-2.5-pro", tokens = new { input = 2000, output = 200, cached = 0, total = 2200 } }
            }
        }));

        using var monitor = new AgentSpeedMonitor(geminiHome, UsageSource.Gemini, watchFileSystem: false);
        var snapshot = monitor.Read(t0.AddSeconds(15), day);

        Assert.Equal(3, snapshot.Requests);
        Assert.Equal(3, snapshot.MeasuredRequests);
        Assert.NotNull(snapshot.Average);
        Assert.Equal(800d / 14d, snapshot.Average!.Value, precision: 3);

        var childAgentRow = Assert.Single(snapshot.Agents, a => a.Agent.SessionId == childUuid);
        Assert.True(childAgentRow.Agent.IsSubagent);
        Assert.Equal(1, childAgentRow.Depth);
    }

    [Fact]
    public void RhythmAndActivity_ExcludeSubagentPromptsAndSummarizeSurfaces()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "rhythm-cache"));
        var geminiHome = Path.Combine(root, "rhythm-home");
        var mainDir = Path.Combine(geminiHome, "antigravity", "brain", "main-conv", ".system_generated", "logs");
        var subDir = Path.Combine(geminiHome, "antigravity", "brain", "sub-conv", ".system_generated", "logs");
        var cliDir = Path.Combine(geminiHome, "tmp", "hash1", "chats");
        Directory.CreateDirectory(mainDir);
        Directory.CreateDirectory(subDir);
        Directory.CreateDirectory(cliDir);

        var day = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.FromHours(8));
        File.WriteAllLines(Path.Combine(mainDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", created_at = day.AddHours(9).ToUniversalTime(), content = "<USER_REQUEST>p1</USER_REQUEST>" }),
            JsonSerializer.Serialize(new { step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = day.AddHours(9).AddSeconds(5).ToUniversalTime(), model = "gemini-2.5-pro", input_tokens = 2000, cache_read_tokens = 8000, output_tokens = 300, tool_calls = new[] { new { name = "read_file" }, new { name = "edit_file" } } }),
            JsonSerializer.Serialize(new { step_index = 2, source = "USER_EXPLICIT", type = "USER_INPUT", created_at = day.AddHours(10).ToUniversalTime(), content = "<USER_REQUEST>p2</USER_REQUEST>" })
        ]);

        File.WriteAllLines(Path.Combine(subDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new { step_index = 0, source = "SYSTEM", type = "USER_INPUT", created_at = day.AddHours(9).AddMinutes(1).ToUniversalTime(), content = "delegated task" }),
            JsonSerializer.Serialize(new { step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", created_at = day.AddHours(9).AddMinutes(2).ToUniversalTime(), model = "gemini-2.5-flash", input_tokens = 1000, cache_read_tokens = 0, output_tokens = 150 })
        ]);

        File.WriteAllText(Path.Combine(cliDir, "session-1.json"), JsonSerializer.Serialize(new
        {
            sessionId = "cli-1",
            messages = new object[]
            {
                new { id = "u1", type = "user", timestamp = day.AddHours(14).ToUniversalTime(), content = "cli prompt" },
                new { id = "g1", type = "gemini", timestamp = day.AddHours(14).AddSeconds(4).ToUniversalTime(), model = "gemini-2.5-pro", tokens = new { input = 1000, output = 100, cached = 500, total = 1100 } }
            }
        }));

        var rhythm = GeminiRhythmReader.Read(day.AddDays(1), homeOverride: geminiHome);
        Assert.Equal(3, rhythm.CountInRange(day, day.AddDays(1)));
        Assert.Equal(3, GeminiRhythmReader.ReadSaved(geminiHome)!.CountInRange(day, day.AddDays(1)));

        var stats = GeminiActivityReader.ReadStats(day, day.AddDays(1), day.AddHours(20), homeOverride: geminiHome);
        Assert.Equal(2, stats.MainSessions);
        Assert.Equal(1, stats.SubagentSessions);
        Assert.Equal(2, stats.AntigravitySessions);
        Assert.Equal(1, stats.CliSessions);
        Assert.Equal(2, stats.AntigravityEvents);
        Assert.Equal(1, stats.CliEvents);
        Assert.Equal(2, stats.ToolCalls);
    }

    [Fact]
    public void CycleHistoryAndAnalysis_PreserveEarlyResetsBothShiftedAndSameAnchor()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "early-reset-cache"));
        var geminiHome = Path.Combine(root, "early-reset-home");
        var agyDir = Path.Combine(geminiHome, "antigravity", "brain", "conv-reset", ".system_generated", "logs");
        Directory.CreateDirectory(agyDir);

        var t1 = new DateTimeOffset(2026, 11, 24, 10, 8, 0, TimeSpan.FromHours(8));
        var reset1 = new DateTimeOffset(2026, 12, 1, 8, 38, 0, TimeSpan.FromHours(8));
        var t2 = new DateTimeOffset(2026, 11, 26, 18, 10, 0, TimeSpan.FromHours(8));
        var reset2 = new DateTimeOffset(2026, 12, 3, 16, 0, 0, TimeSpan.FromHours(8));

        // Usage in cycle 1 ($4.00) and usage in cycle 2 ($2.00).
        File.WriteAllLines(Path.Combine(agyDir, "transcript.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", status = "DONE",
                created_at = t1.AddMinutes(-30).ToUniversalTime(), model = "gemini-4-carbon-high",
                input_tokens = 1_000_000, cache_read_tokens = 0, output_tokens = 200_000
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2, source = "MODEL", type = "PLANNER_RESPONSE", status = "DONE",
                created_at = t2.AddMinutes(-30).ToUniversalTime(), model = "gemini-4-carbon-high",
                input_tokens = 500_000, cache_read_tokens = 0, output_tokens = 100_000
            })
        ]);

        // 1) Save pre-reset observation (27% used, reset 12-01 08:38).
        var snap1 = new GeminiRateLimitSnapshot(
            t1,
            new GeminiRateLimitWindow(100, t1.AddHours(3), t1, ClaudeQuotaReadingSource.Desktop, 60),
            new GeminiRateLimitWindow(27, reset1, t1, ClaudeQuotaReadingSource.Desktop, 3600));
        GeminiRateLimitLog.SaveCurrent(snap1);

        // Verify Settings -> Models (without countdown) detects the upward quota reset cleanly.
        string[] settingsAfterReset = ["Gemini Models", "Weekly Limit Remaining", "95%", "Five Hour Limit Remaining", "100%"];
        Assert.True(GeminiDesktopQuotaParser.TryDetectUnanchoredWeekReset(settingsAfterReset, snap1, out var detectedRem));
        Assert.Equal(95, detectedRem);

        // 2) Save post-early-reset observation with shifted reset (5% used, reset 12-03 16:00 -> new cycle started 11-26 16:00).
        var snap2 = new GeminiRateLimitSnapshot(
            t2,
            new GeminiRateLimitWindow(10, t2.AddHours(4), t2, ClaudeQuotaReadingSource.Desktop, 60),
            new GeminiRateLimitWindow(5, reset2, t2, ClaudeQuotaReadingSource.Desktop, 3600));
        GeminiRateLimitLog.SaveCurrent(snap2);

        var cycles = GeminiQuotaCycleHistory.Read(t2.AddMinutes(5), homeOverride: geminiHome);
        Assert.Equal(2, cycles.Count);

        var current = cycles[0];
        var previous = cycles[1];
        Assert.True(current.IsCurrent);
        Assert.False(previous.IsCurrent);

        // Previous cycle was cut cleanly at new cycle's start (11-26 16:00) instead of being discarded!
        Assert.Equal(reset1.AddDays(-7), previous.Start);
        Assert.Equal(reset2.AddDays(-7), previous.End);
        Assert.Equal(27, previous.MaxUsedPercent);
        Assert.Equal(4.00m, previous.ApiCostUsd);

        Assert.Equal(reset2.AddDays(-7), current.Start);
        Assert.Equal(reset2, current.End);
        Assert.Equal(5, current.MaxUsedPercent);
        Assert.Equal(2.00m, current.ApiCostUsd);
        // $2.00 / 5% = $40.00 full reference
        Assert.Equal(40.00m, current.FullReferenceUsd);

        // 3) Verify cycle analysis, cost curve, and manual interval estimator all work on Gemini cycles.
        var analysisSource = new GeminiQuotaAnalysisSource(geminiHome);
        var analysis = analysisSource.BuildAnalysis(current.ToCodexCycle(), null, 5m, CancellationToken.None);
        Assert.True(analysis.HasData);
        Assert.Single(analysis.Bands);

        var curves = GeminiQuotaCostCurveBuilder.Build(cycles, t2.AddMinutes(5), CancellationToken.None, geminiHome);
        Assert.Equal(2, curves.Curves.Count);

        var liveActivity = GeminiActivityReader.ReadCurrent(t2.Date, t2.AddMinutes(5), t2.AddMinutes(5), homeOverride: geminiHome);
        var manualText = GeminiManualWeekEstimator.Build(liveActivity.WeekWindow, cycles, 100m, 95m, homeOverride: geminiHome);
        Assert.Contains("$40", manualText);

        // 4) Also verify mid-week reset where ResetAt stays identical (e.g., server clears used% to 0% keeping reset2).
        var t3 = t2.AddHours(3);
        GeminiRateLimitLog.SaveCurrent(new GeminiRateLimitSnapshot(
            t3,
            null,
            new GeminiRateLimitWindow(0, reset2, t3, ClaudeQuotaReadingSource.Official)));
        var cyclesAfterSameAnchorClear = GeminiQuotaCycleHistory.Read(t3.AddMinutes(5), homeOverride: geminiHome);
        Assert.Equal(3, cyclesAfterSameAnchorClear.Count);
        Assert.Equal(t3, cyclesAfterSameAnchorClear[0].Start);
        Assert.Equal(0, cyclesAfterSameAnchorClear[0].MaxUsedPercent);
        Assert.Equal(5, cyclesAfterSameAnchorClear[1].MaxUsedPercent);
        Assert.Equal(27, cyclesAfterSameAnchorClear[2].MaxUsedPercent);
    }
}
