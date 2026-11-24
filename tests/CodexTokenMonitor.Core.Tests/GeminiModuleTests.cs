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

        Assert.Empty(reader.GetIncompleteHistoricalDays(day, day));
        Assert.Equal(71_700, reader.ReadCachedRange(day, day.AddDays(1)).TotalTokens);
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
}
