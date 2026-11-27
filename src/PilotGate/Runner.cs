using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PilotGate;

/// <summary>What the scenario runner needs from a target, whatever transport it uses.</summary>
public interface IAgentClient
{
    Task<JsonElement?> HealthAsync(CancellationToken cancellationToken);

    Task<AgentTurn> AskAsync(string persona, string message, CancellationToken cancellationToken);

    Task<AgentTurn> DecideAsync(string persona, string approvalId, bool approve, CancellationToken cancellationToken);
}

/// <summary>Talks to any agent that implements the ClaimsAssist HTTP contract.</summary>
public sealed class AgentClient(HttpClient http, string personaHeader) : IAgentClient
{
    public async Task<JsonElement?> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await http.GetStringAsync("/health", cancellationToken);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public Task<AgentTurn> AskAsync(string persona, string message, CancellationToken cancellationToken) =>
        PostAsync("/chat", persona, new { message }, cancellationToken);

    public Task<AgentTurn> DecideAsync(string persona, string approvalId, bool approve, CancellationToken cancellationToken) =>
        PostAsync($"/approvals/{Uri.EscapeDataString(approvalId)}", persona, new { approve, reason = approve ? null : "Rejected by PilotGate" }, cancellationToken);

    private async Task<AgentTurn> PostAsync(string path, string persona, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json.Options) };
        request.Headers.Add(personaHeader, persona);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {(text.Length > 300 ? text[..300] : text)}");
        }

        return JsonSerializer.Deserialize<AgentTurn>(text, Json.Options) ?? throw new InvalidOperationException("Empty agent response.");
    }
}

public sealed class ScenarioRun
{
    public required Scenario Scenario { get; init; }

    public AgentTurn? First { get; set; }

    public AgentTurn? AfterDecision { get; set; }

    public string? Error { get; set; }

    /// <summary>Agent processing time measured by the gate, excluding the time a human would take to decide.</summary>
    public long LatencyMs { get; set; }

    public JudgeScores? Judge { get; set; }

    [JsonIgnore]
    public AgentTurn? Final => AfterDecision ?? First;

    [JsonIgnore]
    public IEnumerable<AgentTurn> Turns => new[] { First, AfterDecision }.OfType<AgentTurn>();

    [JsonIgnore]
    public string AllAnswers => string.Join("\n", Turns.Select(t => t.Answer));
}

public static class ScenarioRunner
{
    public static async Task<List<ScenarioRun>> RunAsync(
        IAgentClient agent,
        IReadOnlyList<Scenario> scenarios,
        Action<ScenarioRun> progress,
        CancellationToken cancellationToken)
    {
        var runs = new List<ScenarioRun>();
        foreach (var scenario in scenarios)
        {
            var run = new ScenarioRun { Scenario = scenario };
            try
            {
                var started = Stopwatch.GetTimestamp();
                run.First = await agent.AskAsync(scenario.Persona, scenario.Question, cancellationToken);
                run.LatencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (run.First.Status == "approval_required" && run.First.PendingApprovals.Count > 0)
                {
                    // Scenarios that expect an approval decide as configured. Every other scenario rejects,
                    // so an unexpected write request can never execute during a gate run; the request itself
                    // is still recorded and judged.
                    var approve = scenario.Expect.Approval?.Approve ?? false;
                    var decisionStarted = Stopwatch.GetTimestamp();
                    run.AfterDecision = await agent.DecideAsync(scenario.Persona, run.First.PendingApprovals[0].ApprovalId, approve, cancellationToken);
                    run.LatencyMs += (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                run.Error = ex.Message;

                // If the agent itself is gone, the remaining scenarios cannot produce evidence: stop, instead of
                // filling the pack with failures that say nothing about the agent's behaviour.
                if (ex is HttpRequestException or TaskCanceledException && await agent.HealthAsync(cancellationToken) is null)
                {
                    runs.Add(run);
                    progress(run);
                    throw new AgentUnavailableException(scenario.Id, scenarios.Count - runs.Count);
                }
            }

            runs.Add(run);
            progress(run);
        }

        return runs;
    }
}

public sealed class AgentUnavailableException(string scenarioId, int notRun)
    : Exception($"The agent stopped responding at scenario {scenarioId}; {notRun} scenario(s) were not run. The evidence is incomplete, so there is no verdict.");
