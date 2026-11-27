using System.Text.Json;
using System.Text.Json.Serialization;

namespace PilotGate;

public sealed class GateConfig
{
    public PilotInfo Pilot { get; set; } = new();

    public AgentEndpoint Agent { get; set; } = new();

    /// <summary>Settings for gating an application hosted on EPAM DIAL (run with --dial &lt;DIAL Core URL&gt;).</summary>
    public DialTarget Dial { get; set; } = new();

    public JudgeConfig Judge { get; set; } = new();

    public PricingConfig Pricing { get; set; } = new();

    public Thresholds Thresholds { get; set; } = new();

    public BusinessCaseConfig BusinessCase { get; set; } = new();

    public ScreeningAnswers AiActScreening { get; set; } = new();

    public PiiCanaries PiiCanaries { get; set; } = new();

    /// <summary>Strings that only an attack payload produces. Checked in every answer, not only in attack scenarios.</summary>
    public List<string> AttackIndicators { get; set; } = [];

    public List<string> ScenarioFiles { get; set; } = [];

    [JsonIgnore]
    public string BaseDirectory { get; set; } = ".";

    public string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseDirectory, path));

    public static GateConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<GateConfig>(File.ReadAllText(full), Json.Options)
            ?? throw new InvalidOperationException($"Could not read {path}.");
        config.BaseDirectory = Path.GetDirectoryName(full)!;
        return config;
    }

    public List<Scenario> LoadScenarios()
    {
        var scenarios = new List<Scenario>();
        foreach (var file in ScenarioFiles)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ResolvePath(file)));
            scenarios.AddRange(doc.RootElement.GetProperty("scenarios").Deserialize<List<Scenario>>(Json.Options) ?? []);
        }

        var duplicate = scenarios.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? scenarios : throw new InvalidOperationException($"Duplicate scenario id {duplicate.Key}.");
    }
}

public sealed class PilotInfo
{
    public string Name { get; set; } = string.Empty;

    public string Client { get; set; } = string.Empty;

    public string UseCase { get; set; } = string.Empty;

    public string Sponsor { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;
}

public sealed class AgentEndpoint
{
    public string BaseUrl { get; set; } = "http://localhost:5080";

    public string PersonaHeader { get; set; } = "X-Dev-Persona";

    public string AuditLogPath { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// A DIAL deployment as a black-box target: one DIAL key per persona, the OpenAI chat completions API, no trace.
/// Checks that need the trace (retrievals, tool calls, write outcomes, invalid citations, groundedness context) report
/// "not measurable" and cap at WARN; everything visible in the answer is checked as usual.
/// </summary>
public sealed class DialTarget
{
    public string Deployment { get; set; } = "claimsassist";

    /// <summary>JSON object persona id -> DIAL API key (dial/stack/setup.ps1 writes it; git-ignored).</summary>
    public string KeysFile { get; set; } = "../dial/stack/keys.json";

    /// <summary>The application's audit log, when it is readable from where the gate runs (PII-in-log check).</summary>
    public string AuditLogPath { get; set; } = string.Empty;

    /// <summary>Model behind the application, for the cost check (DIAL returns token usage, not the model).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Text that marks the AI disclosure line in an answer.</summary>
    public string DisclosureMarker { get; set; } = "AI-generated";

    /// <summary>Phrases with which the application says its sources do not cover a question.</summary>
    public List<string> NotFoundPhrases { get; set; } = [];

    public int RequestTimeoutSeconds { get; set; } = 180;
}

public sealed class JudgeConfig
{
    public bool Enabled { get; set; } = true;

    public string Deployment { get; set; } = "gpt-4.1";

    public string EndpointEnvironmentVariable { get; set; } = "PILOTGATE_JUDGE_ENDPOINT";

    public string CachePath { get; set; } = ".pilotgate/judge";
}

public sealed class PricingConfig
{
    public string Currency { get; set; } = "USD";

    public string Source { get; set; } = string.Empty;

    public Dictionary<string, ModelPrice> Models { get; set; } = [];
}

public sealed class ModelPrice
{
    public double InputPer1M { get; set; }

    public double OutputPer1M { get; set; }

    public double Cost(long inputTokens, long outputTokens) =>
        ((inputTokens * InputPer1M) + (outputTokens * OutputPer1M)) / 1_000_000.0;
}

public sealed class Thresholds
{
    public double TaskSuccessRate { get; set; } = 0.85;

    public double MinGroundedness { get; set; } = 4;

    public double MaxInvalidCitationRate { get; set; } = 0.05;

    /// <summary>Share of shown sentences with figures whose every figure is found in the cited sources, the tool results
    /// or the question (the gate's own check, FigureGrounding). 1.0: no unverifiable figure may reach a user.</summary>
    public double MinFigureFidelity { get; set; } = 1.0;

    public double MaxCostPerConversationUsd { get; set; } = 0.01;

    public long MaxP95LatencyMs { get; set; } = 20_000;

    public int MinEvidenceQuestions { get; set; } = 100;
}

public sealed class BusinessCaseConfig
{
    public string Metric { get; set; } = string.Empty;

    public double BaselineMinutesPerLookup { get; set; }

    public double AssistedMinutesPerLookup { get; set; }

    public double LookupsPerMonth { get; set; }

    public double LoadedCostPerHourEur { get; set; }

    public string Source { get; set; } = string.Empty;
}

public sealed class PiiCanaries
{
    public List<string> Answers { get; set; } = [];

    public List<string> Logs { get; set; } = [];
}

public sealed class Scenario
{
    public string Id { get; set; } = string.Empty;

    public string Suite { get; set; } = string.Empty;

    public string Persona { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public bool PositiveControl { get; set; }

    public Expectation Expect { get; set; } = new();
}

public sealed class Expectation
{
    public bool? AnswerFound { get; set; }

    public List<string> Sources { get; set; } = [];

    public List<List<string>> Facts { get; set; } = [];

    public string? GroundTruth { get; set; }

    public List<string> ForbiddenDocs { get; set; } = [];

    public List<string> ForbiddenStrings { get; set; } = [];

    public List<string> ForbiddenTools { get; set; } = [];

    public bool ExpectSuspiciousFlag { get; set; }

    public ApprovalExpectation? Approval { get; set; }
}

public sealed class ApprovalExpectation
{
    public string Tool { get; set; } = string.Empty;

    public bool Approve { get; set; } = true;

    public string ExpectOutcome { get; set; } = "QUEUED";

    public bool AllowNoRequest { get; set; }
}

// Mirror of the agent's HTTP contract. Deliberately not shared with ClaimsAssist.Core:
// the gate must judge the agent from the outside, through the contract only.

public sealed class AgentTurn
{
    public string ConversationId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    public bool AnswerFound { get; set; }

    public List<AgentCitation> Citations { get; set; } = [];

    public List<AgentPendingApproval> PendingApprovals { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public string? AiDisclosure { get; set; }

    public AgentTrace Trace { get; set; } = new();

    /// <summary>False for a black-box target (DIAL): the trace is empty because it is not visible, not because
    /// nothing happened. Checks that depend on it report "not measurable" instead of passing.</summary>
    public bool TraceAvailable { get; set; } = true;
}

public sealed class AgentCitation
{
    public string Ref { get; set; } = string.Empty;

    public string? DocId { get; set; }

    public string? Title { get; set; }

    public string? Section { get; set; }
}

public sealed class AgentPendingApproval
{
    public string ApprovalId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
}

public sealed class AgentTrace
{
    public string Model { get; set; } = string.Empty;

    public List<AgentRetrieval> Retrievals { get; set; } = [];

    public List<AgentToolCall> ToolCalls { get; set; } = [];

    public List<AgentWriteAction> WriteActions { get; set; } = [];

    public List<string> OpenedClaimIds { get; set; } = [];

    public List<string> DeniedClaimIds { get; set; } = [];

    public List<string> GroundingContext { get; set; } = [];

    /// <summary>The agent's own figure check, when it reports one: sentences with figures it examined, sentences it
    /// withheld because a figure was not in its sources, and those figures (normalised).</summary>
    public int GroundingStatements { get; set; }

    public int GroundingWithheld { get; set; }

    public List<string> GroundingMissing { get; set; } = [];

    public List<string> InvalidCitations { get; set; } = [];

    public List<string> QuarantinedSources { get; set; } = [];

    public int RemovedLinks { get; set; }

    public bool ContentFilterTriggered { get; set; }

    public string? ContentFilterCategories { get; set; }

    public int ApprovalsGranted { get; set; }

    public int OutputRedactions { get; set; }

    public bool SuspiciousContentDetected { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long LatencyMs { get; set; }

    public int ModelRuns { get; set; }
}

public sealed class AgentRetrieval
{
    public string Query { get; set; } = string.Empty;

    public List<AgentRetrievedRef> Results { get; set; } = [];
}

public sealed class AgentRetrievedRef
{
    public string ChunkId { get; set; } = string.Empty;

    public string DocId { get; set; } = string.Empty;

    public double Score { get; set; }
}

public sealed class AgentToolCall
{
    public string CallId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";

    public bool RequiresApproval { get; set; }

    public bool? Approved { get; set; }

    public bool Executed { get; set; }

    public string? Outcome { get; set; }
}

public sealed class AgentWriteAction
{
    public string Tool { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public decimal AmountEur { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

/// <summary>Invariant formatting with "93%" instead of the invariant culture's "93 %".</summary>
internal static class Fmt
{
    public static readonly System.Globalization.CultureInfo Culture = Create();

    private static System.Globalization.CultureInfo Create()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PercentPositivePattern = 1;
        culture.NumberFormat.PercentNegativePattern = 1;
        return culture;
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
