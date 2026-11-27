using System.Globalization;
using System.Text;
using System.Text.Json;
using PilotGate;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

try
{
    var options = CliOptions.Parse(args);
    if (options.Command is not ("run" or "rescore" or "screen"))
    {
        Console.WriteLine(CliOptions.Usage);
        return 2;
    }

    var config = GateConfig.Load(options.ConfigPath);
    return options.Command switch
    {
        "screen" => Gate.Screen(config),
        "rescore" => Gate.Rescore(config, options),
        _ => await Gate.RunAsync(config, options),
    };
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or AgentUnavailableException)
{
    Console.Error.WriteLine("PilotGate error: " + ex.Message);
    return 2;
}

internal static class Gate
{
    public static int Screen(GateConfig config)
    {
        var s = AiActScreening.Screen(config.AiActScreening);
        Console.WriteLine($"Tier: {s.Tier}\nBasis: {s.Basis}");
        foreach (var line in s.Obligations.Select(o => "Obligation: " + o).Concat(s.Conditions.Select(c => "Condition: " + c)).Concat(s.Notes.Select(n => "Note: " + n)))
        {
            Console.WriteLine(" - " + line);
        }

        return 0;
    }

    public static async Task<int> RunAsync(GateConfig config, CliOptions options)
    {
        var started = DateTimeOffset.UtcNow;
        var runId = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var scenarios = config.LoadScenarios();
        if (options.Only.Count > 0)
        {
            var unknown = options.Only.Where(id => scenarios.All(s => !s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine($"Unknown scenario id(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", scenarios.Select(s => s.Id))}");
                return 2;
            }

            scenarios = scenarios.Where(s => options.Only.Contains(s.Id)).ToList();
        }

        var dial = options.DialUrl is not null;
        using var http = new HttpClient
        {
            BaseAddress = new Uri(options.DialUrl ?? options.AgentUrl ?? config.Agent.BaseUrl),
            Timeout = TimeSpan.FromSeconds(dial ? config.Dial.RequestTimeoutSeconds : config.Agent.RequestTimeoutSeconds),
        };
        IAgentClient agent = dial
            ? new DialAgentClient(http, config.Dial, DialAgentClient.LoadKeys(config.ResolvePath(config.Dial.KeysFile)))
            : new AgentClient(http, config.Agent.PersonaHeader);
        var health = await agent.HealthAsync(CancellationToken.None);
        if (health is null)
        {
            Console.Error.WriteLine(dial ? $"DIAL Core not reachable at {http.BaseAddress}health" : $"Agent not reachable at {http.BaseAddress}health");
            return 2;
        }

        var auditPath = dial
            ? (string.IsNullOrWhiteSpace(config.Dial.AuditLogPath) ? null : config.ResolvePath(config.Dial.AuditLogPath))
            : AuditPath(config);
        var auditOffset = auditPath is not null && File.Exists(auditPath) ? new FileInfo(auditPath).Length : 0;

        Console.WriteLine(dial
            ? $"PilotGate run {runId}: {scenarios.Count} scenarios against DIAL deployment '{config.Dial.Deployment}' at {http.BaseAddress} (black box)"
            : $"PilotGate run {runId}: {scenarios.Count} scenarios against {http.BaseAddress}");
        var runs = await ScenarioRunner.RunAsync(agent, scenarios, run => Console.WriteLine(ProgressLine(run)), CancellationToken.None);
        var auditDelta = ReadAppended(auditPath, auditOffset);

        LlmJudge? judge = null;
        var judgeEndpoint = Environment.GetEnvironmentVariable(config.Judge.EndpointEnvironmentVariable);
        if (config.Judge.Enabled && !options.NoJudge)
        {
            if (string.IsNullOrWhiteSpace(judgeEndpoint))
            {
                Console.WriteLine($"Judge skipped: set {config.Judge.EndpointEnvironmentVariable} to the Azure OpenAI endpoint.");
            }
            else
            {
                // PILOTGATE_JUDGE_CACHE lets a CI agent keep the judge cache outside its cleaned workspace,
                // so unchanged answers are not billed again on the next run.
                var cacheRoot = Environment.GetEnvironmentVariable("PILOTGATE_JUDGE_CACHE") is { Length: > 0 } cacheOverride
                    ? Path.GetFullPath(cacheOverride)
                    : config.ResolvePath(config.Judge.CachePath);
                judge = LlmJudge.Create(judgeEndpoint, config.Judge.Deployment, cacheRoot, runId);
                foreach (var run in runs.Where(r => r.Scenario.Suite == "business" && r.Scenario.Expect.AnswerFound != false && r.Final is { Status: "answered" }))
                {
                    var grounding = string.Join("\n\n", run.Final!.Trace.GroundingContext);
                    run.Judge = await judge.ScoreAsync(
                        run.Scenario.Id,
                        run.Scenario.Question,
                        run.Final.Answer,
                        grounding.Length > 0 ? grounding
                            : run.Final.TraceAvailable ? "(no sources were retrieved)" : "(not visible: black-box target; groundedness is not used)",
                        run.Scenario.Expect.GroundTruth ?? string.Empty,
                        CancellationToken.None);
                    Console.WriteLine($"  judged {run.Scenario.Id}: groundedness {run.Judge.Groundedness:0.#}/5, completeness {run.Judge.Completeness:0.#}/5");
                }
            }
        }

        var agentModel = runs.Select(r => r.Final?.Trace.Model).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "unknown";
        var agentPrice = config.Pricing.Models.GetValueOrDefault(agentModel);
        var agentInput = runs.Sum(r => r.Final?.Trace.InputTokens ?? 0);
        var agentOutput = runs.Sum(r => r.Final?.Trace.OutputTokens ?? 0);
        var agentUsd = agentPrice?.Cost(agentInput, agentOutput) ?? 0;
        var judgeUsd = judge is not null && config.Pricing.Models.TryGetValue(judge.Model, out var judgePrice) ? judgePrice.Cost(judge.InputTokens, judge.OutputTokens) : 0;
        var runCost = new RunCost(agentInput, agentOutput, agentUsd, judge?.InputTokens ?? 0, judge?.OutputTokens ?? 0, judge?.Calls ?? 0, judgeUsd, agentUsd + judgeUsd);

        var report = BuildReport(config, runs, health, judge?.Model, runId, options.Label, started, DateTimeOffset.UtcNow, runCost, auditDelta);
        var dir = EvidencePack.Write(report, runs, auditDelta, OutputRoot(config, options), updateLatest: true);
        Print(report, dir);
        return report.Verdict.Decision == GateVerdict.NoGo ? 1 : 0;
    }

    /// <summary>
    /// Re-scores a saved run with the current gate (checks, scenario expectations and indicators) without
    /// calling the agent or a model again. Used to prove a gate improvement on the exact same evidence.
    /// </summary>
    public static int Rescore(GateConfig config, CliOptions options)
    {
        var runDir = Path.GetFullPath(options.RunDirectory ?? throw new InvalidOperationException("rescore needs --run <evidence folder>."));
        using var oldReport = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "report.json")));
        var root = oldReport.RootElement;
        var started = root.GetProperty("startedUtc").GetDateTimeOffset();
        var finished = root.GetProperty("finishedUtc").GetDateTimeOffset();
        var current = config.LoadScenarios().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        var runs = File.ReadLines(Path.Combine(runDir, "traces.jsonl"))
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<TraceLine>(l, Json.Options)!)
            .Select(t => new ScenarioRun
            {
                Scenario = current.GetValueOrDefault(t.Scenario.Id) ?? t.Scenario,
                First = t.First,
                AfterDecision = t.AfterDecision,
                Judge = t.Judge,
                LatencyMs = t.LatencyMs,
                Error = t.Error,
            })
            .ToList();

        var auditFile = Path.Combine(runDir, "audit-delta.jsonl");
        var auditDelta = File.Exists(auditFile) ? File.ReadAllText(auditFile) : AuditWindow(AuditPath(config), started, finished);
        var runCost = root.GetProperty("runCost").Deserialize<RunCost>(Json.Options)!;
        var health = root.TryGetProperty("agentHealth", out var h) && h.ValueKind == JsonValueKind.Object ? h.Clone() : (JsonElement?)null;
        var judgeModel = root.TryGetProperty("judgeModel", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
        var runId = root.GetProperty("runId").GetString() + "-rescored";
        var label = options.Label.Length > 0 ? options.Label : "re-scored with the current gate";

        var report = BuildReport(config, runs, health, judgeModel, runId, label, started, finished, runCost, auditDelta);
        var dir = EvidencePack.Write(report, runs, auditDelta, OutputRoot(config, options), updateLatest: false);
        Print(report, dir);
        return report.Verdict.Decision == GateVerdict.NoGo ? 1 : 0;
    }

    private static GateReport BuildReport(
        GateConfig config,
        List<ScenarioRun> runs,
        JsonElement? health,
        string? judgeModel,
        string runId,
        string label,
        DateTimeOffset started,
        DateTimeOffset finished,
        RunCost runCost,
        string? auditDelta)
    {
        var checks = new List<CheckResult>
        {
            GateChecks.PermissionTrimming(runs),
            GateChecks.PromptInjection(runs, config.AttackIndicators),
            GateChecks.HumanOversight(runs),
            GateChecks.PiiProtection(runs, config.PiiCanaries, auditDelta),
            GateChecks.GroundedAnswers(runs, config.Thresholds, judgeModel is not null && runs.Any(r => r.Judge is not null), out var tasks),
            GateChecks.CostAndLatency(runs, config, out var cost),
            GateChecks.Transparency(runs),
        };

        var screening = AiActScreening.Screen(config.AiActScreening);
        return new GateReport
        {
            RunId = runId,
            Label = label,
            StartedUtc = started,
            FinishedUtc = finished,
            Pilot = config.Pilot,
            AgentHealth = health,
            JudgeModel = judgeModel,
            Verdict = GateVerdict.Decide(checks, screening),
            Checks = checks,
            Screening = screening,
            BusinessCase = BusinessCase.Compute(config.BusinessCase, tasks, cost, config.Pricing.Currency),
            Tasks = tasks,
            Cost = cost,
            RunCost = runCost,
            Scenarios = EvidencePack.Summarise(runs, checks, config),
            ProductionPath = EvidencePack.ProductionPath,
        };
    }

    private static void Print(GateReport report, string dir)
    {
        Console.WriteLine();
        foreach (var c in report.Checks)
        {
            Console.WriteLine($"{c.Status.ToString().ToUpperInvariant(),-5} {c.Id,-20} {c.Result}");
            foreach (var f in c.Findings)
            {
                Console.WriteLine($"        - {f.ScenarioId}: {GateChecks.Short(f.Detail, 220)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"VERDICT: {report.Verdict.Decision}");
        foreach (var reason in report.Verdict.Reasons.Concat(report.Verdict.Conditions.Select(c => "Condition: " + c)))
        {
            Console.WriteLine("  " + reason);
        }

        var cost = report.RunCost;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Run cost: ${cost.TotalUsd:0.0000} (agent {cost.AgentInputTokens:N0}/{cost.AgentOutputTokens:N0} tokens, judge {cost.JudgeInputTokens:N0}/{cost.JudgeOutputTokens:N0} tokens in {cost.JudgeCalls} billed calls)"));
        Console.WriteLine($"Evidence pack: {Path.Combine(dir, "report.html")}");
    }

    private static string ProgressLine(ScenarioRun run)
    {
        var f = run.Final;
        if (f is null)
        {
            return $"  [{run.Scenario.Id}] ERROR {GateChecks.Short(run.Error, 200)}";
        }

        var tools = string.Join(",", f.Trace.ToolCalls.Select(t => t.Name + (t.Executed ? string.Empty : "*")));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"  [{run.Scenario.Id}] {run.Scenario.Suite,-10} {run.Scenario.Persona,-14} {f.Status,-17} found={f.AnswerFound,-5} cites={f.Citations.Count} tools={(tools.Length == 0 ? "-" : tools)} tokens={f.Trace.InputTokens}/{f.Trace.OutputTokens} {run.LatencyMs / 1000.0:0.0}s");
    }

    private static string OutputRoot(GateConfig config, CliOptions options) =>
        options.OutputDirectory is not null ? Path.GetFullPath(options.OutputDirectory) : config.ResolvePath("../evidence");

    private static string? AuditPath(GateConfig config) =>
        string.IsNullOrWhiteSpace(config.Agent.AuditLogPath) ? null : config.ResolvePath(config.Agent.AuditLogPath);

    private static string? ReadAppended(string? path, long offset)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Audit records written between two instants, for runs saved before the delta was stored.</summary>
    private static string? AuditWindow(string? path, DateTimeOffset from, DateTimeOffset to)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadLines(path).Where(line =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var ts = doc.RootElement.GetProperty("timestamp").GetDateTimeOffset();
                return ts >= from && ts <= to;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                return false;
            }
        }).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines) + "\n";
    }

    private sealed class TraceLine
    {
        public Scenario Scenario { get; set; } = new();

        public AgentTurn? First { get; set; }

        public AgentTurn? AfterDecision { get; set; }

        public JudgeScores? Judge { get; set; }

        public long LatencyMs { get; set; }

        public string? Error { get; set; }
    }
}

internal sealed class CliOptions
{
    public const string Usage = """
        PilotGate: turns an AI pilot into a production decision, with evidence.

        Usage:
          pilotgate run     --config gate/gate.json [--agent http://localhost:5080 | --dial http://localhost:8080] [--out evidence] [--no-judge] [--only B01,I01] [--label "v2"]
          pilotgate rescore --config gate/gate.json --run evidence/<run-id> [--label "text"]
          pilotgate screen  --config gate/gate.json

        --dial gates an application hosted on EPAM DIAL through DIAL Core (black box): one DIAL key per persona from
        dial.keysFile; checks that need the agent's trace report "not measurable" and cannot pass.

        Exit codes: 0 = GO or GO WITH CONDITIONS, 1 = NO-GO, 2 = error.
        """;

    public string? Command { get; private set; }

    public string ConfigPath { get; private set; } = "gate/gate.json";

    public string? AgentUrl { get; private set; }

    public string? DialUrl { get; private set; }

    public string? OutputDirectory { get; private set; }

    public string? RunDirectory { get; private set; }

    public bool NoJudge { get; private set; }

    public string Label { get; private set; } = string.Empty;

    public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions { Command = args.FirstOrDefault()?.ToLowerInvariant() };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--config": options.ConfigPath = Next(); break;
                case "--agent": options.AgentUrl = Next(); break;
                case "--dial": options.DialUrl = Next(); break;
                case "--out": options.OutputDirectory = Next(); break;
                case "--run": options.RunDirectory = Next(); break;
                case "--label": options.Label = Next(); break;
                case "--no-judge": options.NoJudge = true; break;
                case "--only":
                    foreach (var id in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        options.Only.Add(id);
                    }

                    break;
                default: throw new InvalidOperationException($"Unknown option {args[i]}.");
            }
        }

        if (options.AgentUrl is not null && options.DialUrl is not null)
        {
            throw new InvalidOperationException("Use either --agent or --dial, not both.");
        }

        return options;
    }
}
