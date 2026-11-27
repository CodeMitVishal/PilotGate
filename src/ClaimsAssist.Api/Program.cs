using Azure.Identity;
using ClaimsAssist.Core.Agent;
using ClaimsAssist.Core.Ai;
using ClaimsAssist.Core.Claims;
using ClaimsAssist.Core.Dial;
using ClaimsAssist.Core.Guardrails;
using ClaimsAssist.Core.Knowledge;
using ClaimsAssist.Core.Security;
using ClaimsAssist.Core.Telemetry;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("ClaimsAssist").Get<ClaimsAssistSettings>() ?? new ClaimsAssistSettings();

if (string.IsNullOrWhiteSpace(settings.Endpoint))
{
    throw new InvalidOperationException(
        "Set ClaimsAssist:Endpoint (environment variable ClaimsAssist__Endpoint) to the Azure OpenAI endpoint.");
}

var dialMode = string.Equals(settings.AuthMode, "Dial", StringComparison.OrdinalIgnoreCase);
if (!dialMode && !string.Equals(settings.AuthMode, "DevPersonas", StringComparison.OrdinalIgnoreCase))
{
    throw new NotSupportedException(
        "ClaimsAssist:AuthMode must be DevPersonas (development API) or Dial (EPAM DIAL application). Production path without DIAL: Microsoft.Identity.Web, with groups taken from the Entra ID token (see docs/production-path.md).");
}

if (dialMode && (!Uri.TryCreate(settings.Dial.Url, UriKind.Absolute, out var dialUri) || dialUri.Scheme is not ("http" or "https") || settings.Dial.RolePersonas.Count == 0))
{
    throw new InvalidOperationException(
        "Dial mode needs ClaimsAssist:Dial:Url (the DIAL Core base URL) and at least one ClaimsAssist:Dial:RolePersonas entry.");
}

var problems = settings.StartupProblems();
if (problems.Count > 0)
{
    throw new InvalidOperationException(string.Join(" ", problems));
}

var app = builder.Build();

// Development identities are only safe where nobody else can connect. Until the bound addresses are confirmed to be
// loopback (after start, below), every request is refused, so nothing is served from a reachable address.
var devBindingConfirmed = dialMode;
if (!dialMode)
{
    app.Use(async (context, next) =>
    {
        if (!Volatile.Read(ref devBindingConfirmed))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await next(context);
    });
}

var repoRoot = RepoRoot.Find();
string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path);

var dataPath = Resolve(settings.DataPath);
var credential = AgentCredential.Create();
var openAi = AzureOpenAIFactory.Create(settings.Endpoint, credential, TimeSpan.FromSeconds(settings.ModelTimeoutSeconds));
var chatClient = openAi.GetChatClient(settings.ChatDeployment).AsIChatClient();
var embedder = openAi.GetEmbeddingClient(settings.EmbeddingDeployment).AsIEmbeddingGenerator();

// Ingestion guard: strip hidden markup and quarantine instruction-like text before anything is indexed.
var chunks = UntrustedContentGuard.CleanForIngestion(MarkdownKnowledgeLoader.LoadDirectory(Path.Combine(dataPath, "kb")));
foreach (var cleaned in chunks.Where(c => c.RemovedSegments > 0))
{
    app.Logger.LogWarning("Ingestion removed {Count} hidden or instruction-like segment(s) from {Chunk} (source: {Source})", cleaned.RemovedSegments, cleaned.ChunkId, cleaned.Source);
}

var cache = new EmbeddingCache(Path.Combine(dataPath, ".cache", $"embeddings.{settings.EmbeddingDeployment}.json"));
if (cache.UnreadableFileIgnored is { } reason)
{
    app.Logger.LogWarning("The embedding cache could not be read and is rebuilt ({Reason}).", reason);
}
var retriever = await HybridRetriever.CreateAsync(chunks, embedder, settings.EmbeddingDeployment, cache);
if (cache.SaveError is { } saveError)
{
    app.Logger.LogWarning("The embedding cache could not be saved; the next start embeds again ({Reason}).", saveError);
}
var claims = ClaimStore.Load(Path.Combine(dataPath, "claims.json"));
var personas = PersonaDirectory.Load(Path.Combine(dataPath, "personas.json"));
var redactor = PiiRedactor.ForClaims(claims);
var audit = settings.AuditToStdout
    ? new AuditLog(Console.Out, redactor, settings.PseudonymKey)
    : new AuditLog(Resolve(settings.AuditLogPath), redactor, settings.PseudonymKey);
// Pending approvals: in this process (one replica), or in Redis so every replica can decide them and they survive a
// restart (more than one replica). Either way they expire after ApprovalTtlMinutes.
var approvals = await ApprovalStoreFactory.CreateAsync(settings, credential);
app.Logger.LogInformation("Pending approvals are kept in {Store} and expire after {Minutes} minutes.", settings.ApprovalStore, settings.ApprovalTtlMinutes);
var agent = new ClaimsAgentService(
    chatClient,
    retriever,
    claims,
    new SettlementService(claims),
    audit,
    redactor,
    settings.ChatDeployment,
    app.Services.GetRequiredService<ILoggerFactory>(),
    settings.AllowedLinkHosts,
    settings.ApproverGroups,
    approvals,
    TimeSpan.FromMinutes(settings.ApprovalTtlMinutes));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    agent = "ClaimsAssist",
    model = settings.ChatDeployment,
    embedding = settings.EmbeddingDeployment,
    chunks = chunks.Count,
    auth = settings.AuthMode,
    approvals = settings.ApprovalStore,
    auditLog = audit.FilePath,
}));

// Probes for the platform. Both answer only once startup is complete (the index is loaded and the approval store
// connected before the port opens), and neither checks a shared dependency: if Redis or the model is down, pulling
// every replica out of rotation would also stop the questions that do not need them.
// Live: the process answers. Ready: it takes new requests; it reports 503 if probed while the app is stopping.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (IHostApplicationLifetime lifetime) => lifetime.ApplicationStopping.IsCancellationRequested
    ? Results.Json(new { status = "stopping" }, statusCode: StatusCodes.Status503ServiceUnavailable)
    : Results.Ok(new { status = "ready" }));

if (dialMode)
{
    // EPAM DIAL application: identity only from DIAL Core (GET /v1/user/info with the per-request key). The
    // development endpoints and the X-Dev-Persona header do not exist in this mode.
    var dialHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var dialHandler = new DialChatHandler(agent, new DialIdentityResolver(dialHttp, settings.Dial, personas), settings.Dial,
        logger: app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<DialChatHandler>());
    app.MapPost("/openai/deployments/{deploymentId}/chat/completions", async (string deploymentId, HttpContext http, CancellationToken ct) =>
    {
        if (!string.Equals(deploymentId, settings.Dial.DeploymentId, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        string body;
        using (var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        var result = await dialHandler.HandleAsync(
            http.Request.Headers["api-key"].FirstOrDefault(), http.Request.Headers["x-conversation-id"].FirstOrDefault(), body, ct);
        if (result.Events is null)
        {
            return Results.Content(result.Json, "application/json", System.Text.Encoding.UTF8, result.StatusCode);
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        foreach (var e in result.Events)
        {
            await http.Response.WriteAsync(e, ct);
            await http.Response.Body.FlushAsync(ct);
        }

        return Results.Empty;
    });
}
else
{
    // Development identity: a persona header. Production: the Entra ID bearer token (oid + groups claims).
    UserContext? CurrentUser(HttpContext http) => personas.Find(http.Request.Headers["X-Dev-Persona"].FirstOrDefault());


    app.MapPost("/chat", async (ChatRequest request, HttpContext http, CancellationToken ct) =>
    {
        var user = CurrentUser(http);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new { error = "message is required" });
        }

        return Results.Ok(await agent.AskAsync(request.Message, user, request.ConversationId, ct));
    });

    app.MapPost("/approvals/{approvalId}", async (string approvalId, ApprovalDecision decision, HttpContext http, CancellationToken ct) =>
    {
        var user = CurrentUser(http);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await agent.ResolveApprovalAsync(approvalId, decision.Approve, decision.Reason, user, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    });
}

await app.StartAsync();
if (!dialMode)
{
    var reachable = DevIdentityBinding.NonLoopback(app.Urls);
    if (reachable.Count > 0 || app.Urls.Count == 0)
    {
        app.Logger.LogCritical(
            "AuthMode DevPersonas trusts the X-Dev-Persona header, so it may only listen on loopback addresses; refusing to serve on {Addresses}. Use AuthMode Dial outside a developer machine.",
            string.Join(", ", reachable.Count > 0 ? reachable : ["(no address reported)"]));
        await app.StopAsync();
        Environment.Exit(1);
    }

    Volatile.Write(ref devBindingConfirmed, true);
}

await app.WaitForShutdownAsync();

internal static class RepoRoot
{
    /// <summary>Walks up from the build output to the folder that holds PilotGate.slnx.</summary>
    public static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PilotGate.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
