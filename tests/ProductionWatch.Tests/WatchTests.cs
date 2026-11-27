using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProductionWatch;

namespace ProductionWatch.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Schema => Path.Combine(Root, "dial", "schema", "guard-event.v1.schema.json");

    public static string Fixture(string name) => Path.Combine(Root, "tests", "ProductionWatch.Tests", "fixtures", name);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PilotGate.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PilotGate.slnx not found");
    }
}

/// <summary>Builds schema-valid guard events at chosen times.</summary>
internal static class Ev
{
    private static int _n;

    public static readonly DateTimeOffset End = new(2026, 11, 27, 16, 0, 0, TimeSpan.Zero);

    private static string Id() => Interlocked.Increment(ref _n).ToString("x32");

    private static JsonObject Base(DateTimeOffset at, string request, string kind, string deployment, string profile) => new()
    {
        ["schema"] = "dial-guard.event.v1",
        ["time"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["event_id"] = Id(),
        ["request_id"] = request,
        ["conversation_id"] = "conv-1",
        ["deployment"] = deployment,
        ["profile"] = profile,
        ["mode"] = "enforce",
        ["kind"] = kind,
    };

    /// <summary>One request: its findings (kind -> count) and its summary line.</summary>
    public static IEnumerable<string> Request(DateTimeOffset at, string outcome = "ok", Dictionary<string, int>? findings = null,
        double durationMs = 100, int guardErrors = 0, int failOpen = 0, string deployment = "claimsassist", string profile = "claimsassist")
    {
        var request = Id();
        foreach (var (kind, count) in findings ?? [])
        {
            var e = Base(at, request, kind, deployment, profile);
            e["direction"] = "response";
            e["location"] = "choices[0].content";
            e["detector"] = kind switch { "pii" => "pii.email", "link" => "link.host_not_allowed", "canary" => "canary", "injection" => "injection.instruction", _ => "attachment.host_not_allowed" };
            e["action"] = kind switch { "pii" => "redacted", "link" => "removed", "canary" => "blocked", "injection" => "quarantined", _ => "replaced" };
            e["count"] = count;
            yield return e.ToJsonString();
        }

        var done = Base(at, request, "request_completed", deployment, profile);
        done["outcome"] = outcome;
        done["stream"] = true;
        done["duration_ms"] = durationMs;
        done["findings"] = new JsonObject((findings ?? []).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value)));
        done["guard_errors"] = guardErrors;
        done["fail_open_passthroughs"] = failOpen;
        yield return done.ToJsonString();
    }

    public static string WriteLog(IEnumerable<string> lines, bool trailingNewline = true)
    {
        var path = Path.Combine(Path.GetTempPath(), "watch-" + Guid.NewGuid().ToString("n") + ".jsonl");
        var text = string.Join("\n", lines) + (trailingNewline ? "\n" : string.Empty);
        File.WriteAllText(path, text);
        return path;
    }

    public static WatchConfig Config(params string[] eventFiles) => new()
    {
        EventFiles = [.. eventFiles],
        SchemaFile = Repo.Schema,
        WindowMinutes = 60,
        BaselineWindows = 24,
        BaseDirectory = Path.GetTempPath(),
    };
}

public class EventReaderTests
{
    private readonly EventReader _reader = EventReader.FromSchemaFile(Repo.Schema);

    [Fact]
    public void Events_written_by_the_real_guard_validate()
    {
        var load = _reader.Read([Repo.Fixture("guard-events.sample.jsonl")]);
        Assert.Empty(load.Invalid);
        Assert.True(load.Events.Count >= 30);
        Assert.Contains(load.Events, e => e.Kind == "request_completed");
        Assert.Contains(load.Events, e => e.Kind == "canary");
    }

    [Fact]
    public void Invalid_lines_are_counted_with_their_line_number()
    {
        var good = Ev.Request(Ev.End.AddMinutes(-5)).ToList();
        var withValue = JsonNode.Parse(good[0])!.AsObject();
        withValue["value"] = "max@example.com";
        var badKind = JsonNode.Parse(good[0])!.AsObject();
        badKind["kind"] = "secret";
        var path = Ev.WriteLog([good[0], "not json", withValue.ToJsonString(), "", badKind.ToJsonString(), "{}"]);
        var load = _reader.Read([path]);
        Assert.Single(load.Events);
        Assert.Equal([2, 3, 5, 6], load.Invalid.Select(i => i.Line));
        Assert.Equal("not JSON", load.Invalid[0].Reason);
    }

    [Fact]
    public void A_line_still_being_written_is_skipped_and_missing_files_are_reported()
    {
        var lines = Ev.Request(Ev.End.AddMinutes(-5)).ToList();
        var path = Ev.WriteLog([.. lines, "{\"schema\":\"dial-guard.ev"], trailingNewline: false);
        var load = _reader.Read([path, path + ".missing"]);
        Assert.Equal(lines.Count, load.Events.Count);
        Assert.Empty(load.Invalid);
        Assert.Single(load.MissingFiles);
    }
}

public class MetricsAndRulesTests
{
    private static (List<GuardStats> Now, List<GuardStats> Before, List<Alert> Alerts) Evaluate(IEnumerable<string> lines, WatchConfig? config = null)
    {
        var path = Ev.WriteLog(lines);
        config ??= Ev.Config(path);
        var load = EventReader.FromSchemaFile(Repo.Schema).Read([path]);
        Assert.Empty(load.Invalid);
        var (window, baseline) = Metrics.Windows(Ev.End, config);
        var now = Metrics.Guard(load.Events, window);
        var before = Metrics.Guard(load.Events, baseline);
        return (now, before, Rules.Evaluate(now, before, null, 0, config));
    }

    private static IEnumerable<string> Many(int count, Func<int, IEnumerable<string>> one) => Enumerable.Range(0, count).SelectMany(one);

    [Fact]
    public void Windows_counts_rates_and_percentiles_are_exact()
    {
        var lines = Many(40, i => Ev.Request(Ev.End.AddMinutes(-30), findings: i < 10 ? new() { ["pii"] = 2 } : null, durationMs: i + 1))
            .Concat(Ev.Request(Ev.End.AddMinutes(-90), outcome: "blocked")) // baseline only
            .Concat(Ev.Request(Ev.End.AddMinutes(1))); // after the window: ignored
        var (now, before, _) = Evaluate(lines);
        var s = Assert.Single(now);
        Assert.Equal((40, 10, 20), (s.Requests, s.RequestsWithFinding["pii"], s.Findings["pii"]));
        Assert.Equal(0.25, s.FindingRate("pii"));
        Assert.Equal((20.0, 38.0), (s.P50DurationMs, s.P95DurationMs));
        Assert.Equal(1, Assert.Single(before).Outcomes["blocked"]);
    }

    [Fact]
    public void Every_fail_open_passthrough_is_critical_even_with_little_traffic()
    {
        var (_, _, alerts) = Evaluate(Ev.Request(Ev.End.AddMinutes(-5), failOpen: 1));
        Assert.Contains(alerts, a => a is { Rule: "GUARD_FAIL_OPEN", Severity: Severity.Critical });
    }

    [Fact]
    public void Guard_error_rate_respects_threshold_and_minimum_traffic()
    {
        var few = Evaluate(Ev.Request(Ev.End.AddMinutes(-5), guardErrors: 1)).Alerts;
        Assert.Contains(few, a => a is { Rule: "GUARD_ERRORS", Severity: Severity.Warning });
        var rateOk = Evaluate(Many(200, i => Ev.Request(Ev.End.AddMinutes(-5), guardErrors: i < 2 ? 1 : 0))).Alerts;
        Assert.DoesNotContain(rateOk, a => a.Rule == "GUARD_ERRORS"); // 1.0% is not above 1.0%
        var rateHigh = Evaluate(Many(200, i => Ev.Request(Ev.End.AddMinutes(-5), guardErrors: i < 3 ? 1 : 0))).Alerts;
        Assert.Contains(rateHigh, a => a is { Rule: "GUARD_ERRORS", Severity: Severity.Critical });
    }

    [Fact]
    public void Finding_rates_and_leak_blocks()
    {
        var alerts = Evaluate(Many(50, i => Ev.Request(Ev.End.AddMinutes(-5), outcome: i < 2 ? "blocked" : "ok",
            findings: i < 2 ? new() { ["canary"] = 1 } : i < 15 ? new() { ["pii"] = 1 } : null))).Alerts;
        Assert.Contains(alerts, a => a is { Rule: "LEAK_BLOCKS", Severity: Severity.Critical });
        Assert.Contains(alerts, a => a is { Rule: "FINDING_RATE", Severity: Severity.Critical } && a.Message.Contains("canary", StringComparison.Ordinal));
        Assert.Contains(alerts, a => a.Rule == "FINDING_RATE" && a.Message.Contains("pii", StringComparison.Ordinal)); // 26% > 20%
        var quiet = Evaluate(Many(50, i => Ev.Request(Ev.End.AddMinutes(-5), findings: i < 10 ? new() { ["pii"] = 1 } : null))).Alerts;
        Assert.Empty(quiet); // 20% is not above 20%
    }

    [Fact]
    public void Drift_needs_factor_minimum_rate_and_enough_baseline()
    {
        IEnumerable<string> Baseline(int requests, int withLinks) => Many(requests, i => Ev.Request(Ev.End.AddHours(-5), findings: i < withLinks ? new() { ["link"] = 1 } : null));
        IEnumerable<string> Current(int requests, int withLinks) => Many(requests, i => Ev.Request(Ev.End.AddMinutes(-10), findings: i < withLinks ? new() { ["link"] = 1 } : null));

        Assert.Contains(Evaluate(Baseline(100, 2).Concat(Current(40, 4))).Alerts, a => a.Rule == "DRIFT"); // 10% vs 2%
        Assert.DoesNotContain(Evaluate(Baseline(100, 5).Concat(Current(40, 4))).Alerts, a => a.Rule == "DRIFT"); // 10% vs 5%: 2x
        Assert.DoesNotContain(Evaluate(Baseline(100, 0).Concat(Current(40, 1))).Alerts, a => a.Rule == "DRIFT"); // 2.5% < min rate
        Assert.Contains(Evaluate(Baseline(100, 0).Concat(Current(40, 3))).Alerts, a => a.Rule == "DRIFT" && a.Message.Contains("appeared", StringComparison.Ordinal));
        Assert.DoesNotContain(Evaluate(Baseline(30, 0).Concat(Current(40, 4))).Alerts, a => a.Rule == "DRIFT"); // baseline too small
    }

    [Fact]
    public void Silence_after_traffic_is_reported()
    {
        var alerts = Evaluate(Many(60, _ => Ev.Request(Ev.End.AddHours(-3)))).Alerts;
        Assert.Contains(alerts, a => a.Rule == "NO_TRAFFIC");
    }

    [Fact]
    public void Deployments_and_profiles_are_kept_apart()
    {
        var (now, _, _) = Evaluate(Ev.Request(Ev.End.AddMinutes(-5), deployment: "mock-echo", profile: "strict")
            .Concat(Ev.Request(Ev.End.AddMinutes(-5), deployment: "mock-echo", profile: "monitor"))
            .Concat(Ev.Request(Ev.End.AddMinutes(-5))));
        Assert.Equal(3, now.Count);
    }
}

public class NotifierTests
{
    private sealed class Scripted(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return answers[Math.Min(Calls++, answers.Length - 1)]();
        }
    }

    private static readonly Alert[] Alerts = [new(Severity.Critical, "GUARD_FAIL_OPEN", "claimsassist (claimsassist)", "1 field passed unchecked.")];
    private static readonly Window Window = new(Ev.End.AddHours(-1), Ev.End);
    private static readonly Uri Hook = new("https://hooks.example.test/x");

    private static (Notifier Notifier, List<TimeSpan> Waits) Make(HttpMessageHandler handler, int attempts = 4)
    {
        var waits = new List<TimeSpan>();
        var notifier = new Notifier(new HttpClient(handler), new AlertConfig { MaxAttempts = attempts, InitialBackoffMilliseconds = 100, TimeoutSeconds = 5 },
            (d, _) => { waits.Add(d); return Task.CompletedTask; });
        return (notifier, waits);
    }

    [Fact]
    public async Task Server_errors_are_retried_with_backoff_until_delivered()
    {
        var handler = new Scripted(() => new(HttpStatusCode.InternalServerError), () => new(HttpStatusCode.BadGateway), () => new(HttpStatusCode.OK));
        var (notifier, waits) = Make(handler);
        var result = await notifier.SendAsync(Hook, Alerts, "summary", Window, default);
        Assert.True(result.Delivered);
        Assert.Equal(3, result.Attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], waits);
        var payload = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("summary", payload["text"]!.GetValue<string>());
        Assert.Equal("critical", payload["alerts"]![0]!["severity"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_errors_are_permanent_and_failures_are_reported()
    {
        var (permanent, _) = Make(new Scripted(() => new(HttpStatusCode.NotFound)));
        var r = await permanent.SendAsync(Hook, Alerts, "s", Window, default);
        Assert.Equal((false, 1), (r.Delivered, r.Attempts));
        Assert.Contains("permanent", r.Detail, StringComparison.Ordinal);

        var (down, waits) = Make(new Scripted(() => throw new HttpRequestException("refused")), attempts: 3);
        var gaveUp = await down.SendAsync(Hook, Alerts, "s", Window, default);
        Assert.Equal((false, 3, 2), (gaveUp.Delivered, gaveUp.Attempts, waits.Count));
        Assert.Contains("gave up", gaveUp.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limits_honour_retry_after()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        var (notifier, waits) = Make(new Scripted(() => limited, () => new(HttpStatusCode.Accepted)));
        Assert.True((await notifier.SendAsync(Hook, Alerts, "s", Window, default)).Delivered);
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Theory]
    [InlineData("https://hooks.example.test/x", true)]
    [InlineData("http://127.0.0.1:9999/hook", true)]
    [InlineData("http://localhost:9999/hook", true)]
    [InlineData("http://hooks.example.test/x", false)]
    [InlineData("ftp://hooks.example.test/x", false)]
    [InlineData("not a url", false)]
    public void Webhook_urls_must_be_https_except_on_localhost(string url, bool ok)
    {
        var env = new Dictionary<string, string?> { ["HOOK"] = url };
        var config = new AlertConfig { WebhookUrlEnvironmentVariable = "HOOK" };
        if (ok)
        {
            Assert.NotNull(Notifier.WebhookFromEnvironment(config, env));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Notifier.WebhookFromEnvironment(config, env));
        }

        Assert.Null(Notifier.WebhookFromEnvironment(config, new Dictionary<string, string?>()));
    }

    [Fact]
    public void An_unexpanded_pipeline_variable_means_no_webhook()
    {
        var config = new AlertConfig { WebhookUrlEnvironmentVariable = "HOOK" };
        Assert.Null(Notifier.WebhookFromEnvironment(config, new Dictionary<string, string?> { ["HOOK"] = "$(PILOTGATE_WATCH_WEBHOOK)" }));
    }

    [Fact]
    public async Task Alerts_reach_a_real_http_receiver()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            while (true)
            {
                var n = await stream.ReadAsync(buffer);
                text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                var s = text.ToString();
                var split = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (split >= 0)
                {
                    var length = int.Parse(System.Text.RegularExpressions.Regex.Match(s, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
                    if (Encoding.UTF8.GetByteCount(s[(split + 4)..]) >= length)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                        return s[(split + 4)..];
                    }
                }
            }
        });

        var notifier = new Notifier(new HttpClient(), new AlertConfig { TimeoutSeconds = 5 });
        var result = await notifier.SendAsync(new Uri($"http://127.0.0.1:{port}/hook"), Alerts, "real receiver", Window, default);
        Assert.True(result.Delivered, result.Detail);
        var body = JsonNode.Parse(await received)!;
        Assert.Equal("GUARD_FAIL_OPEN", body["alerts"]![0]!["rule"]!.GetValue<string>());
    }
}

public class RescorerTests
{
    private sealed class FakeJudge(int score = 5, long tokensIn = 400, long tokensOut = 40) : IAnswerJudge
    {
        public List<string> Seen { get; } = [];

        public Task<JudgeVerdict> ScoreAsync(string question, string answer, CancellationToken cancellationToken)
        {
            Seen.Add(question);
            return Task.FromResult(new JudgeVerdict(score, "ok", tokensIn, tokensOut));
        }
    }

    private static List<AuditRecord> Records(int count, string status = "answered", int minutesAgo = 10) =>
        Enumerable.Range(0, count).Select(i => new AuditRecord
        {
            Timestamp = Ev.End.AddMinutes(-minutesAgo).AddSeconds(i), ConversationId = $"c{i}", Status = status,
            Question = $"question {i}", Answer = "The deductible is 500 EUR [KB-1].",
        }).ToList();

    private static readonly Window Window = new(Ev.End.AddHours(-1), Ev.End);

    [Fact]
    public async Task Sample_size_is_respected_and_the_sample_is_deterministic()
    {
        var records = Records(30).Concat(Records(5, "blocked")).Concat(Records(5, minutesAgo: 120)).ToList();
        var a = new FakeJudge();
        var b = new FakeJudge();
        var first = await new Rescorer(a, new RescoreConfig { SampleSize = 4, MaxUsd = 1 }).RunAsync(records, Window, default);
        await new Rescorer(b, new RescoreConfig { SampleSize = 4, MaxUsd = 1 }).RunAsync(records.AsEnumerable().Reverse(), Window, default);
        Assert.Equal((30, 4), (first.Eligible, first.Scored.Count)); // only answered turns inside the window
        Assert.Equal(a.Seen, b.Seen); // the same answers, whatever the input order
        Assert.Equal(5.0, first.MeanScore);
    }

    [Fact]
    public async Task The_cost_cap_is_never_crossed()
    {
        var config = new RescoreConfig { SampleSize = 50, MaxUsd = 0.001, InputPer1M = 0.4, OutputPer1M = 1.6, MaxOutputTokens = 200 };
        var judge = new FakeJudge(tokensIn: 1000, tokensOut: 200);
        var result = await new Rescorer(judge, config).RunAsync(Records(50), Window, default);
        Assert.True(result.StoppedAtCap);
        Assert.InRange(result.SpentUsd, 0.0001, 0.001);
        Assert.True(result.Scored.Count < 50);
        Assert.Equal(judge.Seen.Count, result.Scored.Count);
    }

    [Fact]
    public async Task A_failing_judge_is_reported_not_thrown()
    {
        var result = await new Rescorer(new ThrowingJudge(), new RescoreConfig { SampleSize = 3, MaxUsd = 1 }).RunAsync(Records(3), Window, default);
        Assert.Equal("judge failed: HttpRequestException", result.Error);
        Assert.Empty(result.Scored);
    }

    private sealed class ThrowingJudge : IAnswerJudge
    {
        public Task<JudgeVerdict> ScoreAsync(string question, string answer, CancellationToken cancellationToken) => throw new HttpRequestException("down");
    }
}

public class WatchRunTests
{
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task Exit_codes_reflect_alerts_and_delivery()
    {
        var clean = Ev.Config(Ev.WriteLog(Enumerable.Range(0, 30).SelectMany(_ => Ev.Request(Ev.End.AddMinutes(-5)))));
        var options = WatchOptions.Parse(["--at", Ev.End.ToString("o")]);
        var report = await WatchRun.RunAsync(clean, options, new HttpClient(new NoNetwork()), default);
        Assert.Equal(0, WatchRun.ExitCode(report));
        Assert.Contains("all clear", report.Summary(), StringComparison.Ordinal);

        var failOpen = Ev.Config(Ev.WriteLog(Ev.Request(Ev.End.AddMinutes(-5), failOpen: 1)));
        var alerting = await WatchRun.RunAsync(failOpen, options, new HttpClient(new NoNetwork()), default);
        Assert.Equal(1, WatchRun.ExitCode(alerting)); // no webhook configured: alerts, nothing attempted
        Assert.Contains("not set", alerting.Delivery!.Detail, StringComparison.Ordinal);
        Assert.Contains("GUARD_FAIL_OPEN", alerting.Markdown(), StringComparison.Ordinal);

        var undelivered = alerting with { Delivery = new Delivery(true, false, 4, "gave up") };
        Assert.Equal(3, WatchRun.ExitCode(undelivered));
        Assert.DoesNotContain("max@example.com", alerting.Json(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_errors_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new WatchConfig { WindowMinutes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WatchConfig { Drift = new DriftConfig { Factor = 1 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WatchConfig { Alerts = new AlertConfig { MaxAttempts = 0 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => WatchOptions.Parse(["--bogus"]));
        var overrides = WatchOptions.Parse(["--events", "a.jsonl", "--events", "b.jsonl", "--audit", "c.jsonl"]);
        Assert.Equal(2, overrides.EventFiles.Count);
        Assert.All(overrides.EventFiles.Concat(overrides.AuditFiles), f => Assert.True(Path.IsPathRooted(f)));
    }
}
