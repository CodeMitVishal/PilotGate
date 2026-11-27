using System.Text.Json;
using ClaimsAssist.Core.Ai;
using ClaimsAssist.Core.Guardrails;
using ClaimsAssist.Core.Telemetry;

namespace ClaimsAssist.Tests;

/// <summary>What a production-shaped (Dial) process refuses to start with, and the audit sinks.</summary>
public sealed class StartupAndAuditTests
{
    private const string RandomKey = "6f1c0e9a4b7d2f8e3a5c1b9d7e2f4a6c8b0d3e5f7a9c1e3b";

    private static ClaimsAssistSettings Dial(string key, string sink = "File") =>
        new() { AuthMode = "Dial", PseudonymKey = key, AuditSink = sink };

    [Fact]
    public void Dial_mode_refuses_the_public_development_pseudonym_key()
    {
        var problem = Assert.Single(Dial(ClaimsAssistSettings.DevelopmentPseudonymKey).StartupProblems());
        Assert.Contains("PseudonymKey", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    [InlineData("                                        ")]
    [InlineData("0123456789012345678901234567890")] // 31 characters
    public void Dial_mode_refuses_a_missing_or_short_pseudonym_key(string key)
    {
        Assert.Single(Dial(key).StartupProblems());
    }

    [Fact]
    public void Dial_mode_starts_with_a_long_random_key_and_either_sink()
    {
        Assert.Empty(Dial(RandomKey).StartupProblems());
        Assert.Empty(Dial(RandomKey, "stdout").StartupProblems());
        Assert.Empty(Dial(RandomKey[..32]).StartupProblems());
    }

    [Fact]
    public void Development_mode_may_keep_the_development_key()
    {
        Assert.Empty(new ClaimsAssistSettings { AuthMode = "DevPersonas" }.StartupProblems());
    }

    [Fact]
    public void The_default_mode_proves_identity_and_refuses_the_development_key()
    {
        var settings = new ClaimsAssistSettings();
        Assert.Equal("Dial", settings.AuthMode); // a missing setting must never open the development API
        Assert.Contains(settings.StartupProblems(), p => p.Contains("PseudonymKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("https://localhost:7089")]
    [InlineData("http://127.0.0.1:5180")]
    [InlineData("http://[::1]:5000")]
    public void Development_identities_may_listen_on_loopback(string address)
    {
        Assert.Empty(ClaimsAssist.Core.Security.DevIdentityBinding.NonLoopback([address]));
    }

    [Theory]
    [InlineData("http://[::]:8080")]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://+:8080")]
    [InlineData("http://*:8080")]
    [InlineData("http://claimsassist.contoso.example:80")]
    [InlineData("http://192.0.2.10:8080")]
    [InlineData("not a url")]
    public void Development_identities_refuse_any_reachable_address(string address)
    {
        Assert.Equal([address], ClaimsAssist.Core.Security.DevIdentityBinding.NonLoopback(["http://localhost:5000", address]));
    }

    [Theory]
    [InlineData("Console")]
    [InlineData("")]
    [InlineData("file,stdout")]
    public void An_unknown_audit_sink_is_refused_in_any_mode(string sink)
    {
        Assert.Contains(new ClaimsAssistSettings { AuditSink = sink }.StartupProblems(), p => p.Contains("AuditSink"));
        Assert.Contains(Dial(RandomKey, sink).StartupProblems(), p => p.Contains("AuditSink"));
    }

    [Fact]
    public void The_stdout_sink_writes_one_redacted_line_per_record()
    {
        var output = new StringWriter();
        var audit = new AuditLog(output, new PiiRedactor([]), RandomKey);

        audit.Write(Record("Pay the repair to DE89 3704 0044 0532 0130 00, contact max@example.com"));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.DoesNotContain("3704 0044", line);
        Assert.DoesNotContain("max@example.com", line);
        Assert.Equal("ok", JsonDocument.Parse(line).RootElement.GetProperty("status").GetString());
        Assert.EndsWith("\n", output.ToString());
        Assert.Equal("stdout", audit.FilePath);
    }

    [Fact]
    public void Concurrent_records_on_the_stdout_sink_never_interleave()
    {
        var output = new StringWriter();
        var audit = new AuditLog(output, new PiiRedactor([]), RandomKey);

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => audit.Write(Record($"question {i} " + new string('x', 500))));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, l => Assert.Equal("ok", JsonDocument.Parse(l).RootElement.GetProperty("status").GetString()));
    }

    [Fact]
    public void Pseudonyms_depend_on_the_key()
    {
        var a = new AuditLog(new StringWriter(), new PiiRedactor([]), RandomKey);
        var b = new AuditLog(new StringWriter(), new PiiRedactor([]), ClaimsAssistSettings.DevelopmentPseudonymKey);

        Assert.Equal(a.Pseudonymise("anna.property"), a.Pseudonymise("anna.property"));
        Assert.NotEqual(a.Pseudonymise("anna.property"), b.Pseudonymise("anna.property"));
    }

    private static AuditRecord Record(string question) =>
        new(DateTimeOffset.UnixEpoch, "c_test", "u_test", "ok", question, "answer", [], [], 1, 1, 1);
}
