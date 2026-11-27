using System.Globalization;
using System.Text;
using System.Text.Json;
using ProductionWatch;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

try
{
    var options = WatchOptions.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(WatchOptions.Usage);
        return 0;
    }

    var config = WatchConfig.Load(options.ConfigPath);
    if (options.EventFiles.Count > 0)
    {
        config.EventFiles = [.. options.EventFiles];
    }

    if (options.AuditFiles.Count > 0)
    {
        config.AuditFiles = [.. options.AuditFiles];
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(config.Alerts.TimeoutSeconds, 60)) };
    var report = await WatchRun.RunAsync(config, options, http, CancellationToken.None);

    var outDir = options.OutputDirectory is not null ? Path.GetFullPath(options.OutputDirectory) : config.ResolvePath(config.OutputDirectory);
    Directory.CreateDirectory(outDir);
    var stamp = report.Window.End.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    File.WriteAllText(Path.Combine(outDir, $"watch-{stamp}.json"), report.Json());
    File.WriteAllText(Path.Combine(outDir, $"watch-{stamp}.md"), report.Markdown());
    File.WriteAllText(Path.Combine(outDir, "latest.md"), report.Markdown());

    Console.WriteLine(report.Summary());
    foreach (var alert in report.Alerts)
    {
        Console.WriteLine($"  {alert.Severity.ToString().ToUpperInvariant(),-8} {alert.Rule,-16} {alert.Scope}: {alert.Message}");
    }

    if (report.Delivery is { Attempted: true })
    {
        Console.WriteLine($"Alert delivery: {report.Delivery.Detail}");
    }

    Console.WriteLine($"Report: {Path.Combine(outDir, $"watch-{stamp}.md")}");
    return WatchRun.ExitCode(report);
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or FormatException)
{
    Console.Error.WriteLine("Production Watch error: " + ex.Message);
    return 2;
}

namespace ProductionWatch
{
    public static class WatchRun
    {
        /// <summary>0 all clear, 1 alerts (delivered or no webhook configured), 3 alerts whose delivery failed.</summary>
        public static int ExitCode(WatchReport report) =>
            report.Alerts.Count == 0 ? 0 : report.Delivery is { Attempted: true, Delivered: false } ? 3 : 1;

        public static async Task<WatchReport> RunAsync(WatchConfig config, WatchOptions options, HttpClient http, CancellationToken cancellationToken, IAnswerJudge? judge = null)
        {
            var end = options.At ?? DateTimeOffset.UtcNow;
            var (window, baseline) = Metrics.Windows(end, config);

            var reader = EventReader.FromSchemaFile(config.ResolvePath(config.SchemaFile));
            var events = reader.Read(config.EventFiles.Select(config.ResolvePath));
            var audit = AuditReader.Read(config.AuditFiles.Select(config.ResolvePath));

            var guard = Metrics.Guard(events.Events, window);
            var guardBaseline = Metrics.Guard(events.Events, baseline);
            var agent = config.AuditFiles.Count > 0 ? Metrics.Agent(audit.Records, window) : null;
            var alerts = Rules.Evaluate(guard, guardBaseline, agent, events.Invalid.Count, config);

            RescoreResult? rescoring = null;
            if (config.Rescoring.Enabled || options.Rescore)
            {
                judge ??= CreateJudge(config, http);
                rescoring = judge is null
                    ? new RescoreResult(0, [], 0, false, $"judge not configured: set {config.Rescoring.EndpointEnvironmentVariable}")
                    : await new Rescorer(judge, config.Rescoring).RunAsync(audit.Records, window, cancellationToken);
                if (rescoring.MeanScore is { } mean && mean < config.Rescoring.MinMeanScore)
                {
                    alerts.Add(new(Severity.Warning, "RESCORE_LOW", "application",
                        string.Create(CultureInfo.InvariantCulture, $"re-scored answers average {mean:0.0}/5 (< {config.Rescoring.MinMeanScore:0.0}).")));
                }
            }

            var draft = new WatchReport(DateTimeOffset.UtcNow, window, baseline, events.Events.Count, events.Invalid, events.MissingFiles.Concat(audit.MissingFiles).ToList(),
                guard, guardBaseline, agent, rescoring, alerts, null);

            Delivery? delivery = null;
            var webhook = Notifier.WebhookFromEnvironment(config.Alerts);
            if (alerts.Count > 0 && webhook is not null && !options.NoAlerts)
            {
                delivery = await new Notifier(http, config.Alerts).SendAsync(webhook, alerts, draft.Summary(), window, cancellationToken);
            }
            else if (alerts.Count > 0)
            {
                delivery = new Delivery(false, false, 0, options.NoAlerts ? "not sent (--no-alerts)" : $"not sent: {config.Alerts.WebhookUrlEnvironmentVariable} is not set");
            }

            return draft with { Delivery = delivery };
        }

        private static IAnswerJudge? CreateJudge(WatchConfig config, HttpClient http)
        {
            var endpoint = Environment.GetEnvironmentVariable(config.Rescoring.EndpointEnvironmentVariable);
            return string.IsNullOrWhiteSpace(endpoint) ? null : new AzureOpenAIJudge(http, endpoint, config.Rescoring.Deployment, config.Rescoring.MaxOutputTokens);
        }
    }

    public sealed class WatchOptions
    {
        public const string Usage = """
            Production Watch: metrics, drift and alerts from DIAL Guard events and the application's audit log.

            Usage:
              productionwatch [--config watch/watch.json] [--at 2026-11-27T16:00:00Z] [--out <dir>] [--no-alerts] [--rescore]
                              [--events <file>]... [--audit <file>]...

            --events and --audit replace the files listed in the config (for a CI agent whose checkout has no live logs).

            Exit codes: 0 all clear, 1 alerts, 2 error, 3 alerts that could not be delivered.
            The webhook URL is read from the environment variable named in alerts.webhookUrlEnvironmentVariable.
            """;

        public string ConfigPath { get; private set; } = "watch/watch.json";

        public DateTimeOffset? At { get; private set; }

        public string? OutputDirectory { get; private set; }

        public bool NoAlerts { get; private set; }

        public bool Rescore { get; private set; }

        public bool Help { get; private set; }

        public List<string> EventFiles { get; } = [];

        public List<string> AuditFiles { get; } = [];

        public static WatchOptions Parse(string[] args)
        {
            var options = new WatchOptions();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--config": options.ConfigPath = Next(); break;
                    case "--at": options.At = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal); break;
                    case "--out": options.OutputDirectory = Next(); break;
                    case "--no-alerts": options.NoAlerts = true; break;
                    case "--rescore": options.Rescore = true; break;
                    case "--events": options.EventFiles.Add(Path.GetFullPath(Next())); break;
                    case "--audit": options.AuditFiles.Add(Path.GetFullPath(Next())); break;
                    case "--help" or "-h": options.Help = true; break;
                    default: throw new InvalidOperationException($"Unknown option {args[i]}.");
                }
            }

            return options;
        }
    }
}
