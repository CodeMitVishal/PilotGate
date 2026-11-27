using System.Text.Json;
using PilotGate;

namespace PilotGate.Tests;

/// <summary>
/// The gate's own figure check. It runs the same shared corpus as the agent's verifier (tests/grounding-corpus.v1.json),
/// so the two independent implementations stay on one rule, and it decides GROUNDED_ANSWERS when a figure slips through.
/// </summary>
public sealed class FigureGroundingTests
{
    private static readonly JsonElement Corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "grounding-corpus.v1.json"))).RootElement;

    private static readonly Thresholds Thresholds = new() { MinEvidenceQuestions = 1 };

    public static TheoryData<string> Cases(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.GetProperty(section).EnumerateArray())
        {
            data.Add(c.GetProperty("id").GetString()!);
        }

        return data;
    }

    private static JsonElement Case(string section, string id) =>
        Corpus.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Theory]
    [MemberData(nameof(Cases), "atoms")]
    public void Atoms_match_the_corpus(string id)
    {
        var c = Case("atoms", id);
        var atoms = FigureGrounding.Atoms(c.GetProperty("text").GetString()!, c.GetProperty("source").GetBoolean());
        Assert.Equal(Strings(c, "expect").Order(StringComparer.Ordinal), atoms.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases), "verify")]
    public void Unsupported_sentences_match_the_corpus(string id)
    {
        var c = Case("verify", id);
        var outcome = FigureGrounding.Check(c.GetProperty("answer").GetString()!, Strings(c, "sources"),
            c.GetProperty("question").GetString()!, Strings(c, "opened_claims"));
        Assert.Equal(c.GetProperty("expect_withheld").GetInt32(), outcome.Unsupported.Count);
    }

    [Theory]
    [MemberData(nameof(Cases), "assemble")]
    public void Sources_are_assembled_from_what_the_model_saw(string id)
    {
        var c = Case("assemble", id);
        var sources = FigureGrounding.SourcesFrom(Strings(c, "tool_results"), Strings(c, "cited"));
        var outcome = FigureGrounding.Check(c.GetProperty("answer").GetString()!, sources, c.GetProperty("question").GetString()!, Strings(c, "opened_claims"));
        Assert.Equal(c.GetProperty("expect_unsupported").GetInt32(), outcome.Unsupported.Count);
    }

    [Fact]
    public void An_invented_figure_in_a_shown_answer_fails_grounded_answers()
    {
        var run = Grounded("Handlers may settle up to 6,000 EUR [KB-PROP-GUIDE#settlement-authority].");

        var check = GateChecks.GroundedAnswers([run], Thresholds, judged: false, out _);

        Assert.Equal(CheckStatus.Fail, check.Status);
        var finding = Assert.Single(check.Findings);
        Assert.Contains("num:6000", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("0/1 sentence(s)", check.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void Figures_found_in_the_cited_source_pass()
    {
        var run = Grounded("Handlers may settle up to 5,000 EUR [KB-PROP-GUIDE#settlement-authority].");

        var check = GateChecks.GroundedAnswers([run], Thresholds, judged: false, out _);

        Assert.NotEqual(CheckStatus.Fail, check.Status);
        Assert.Empty(check.Findings);
        Assert.Contains("1/1 sentence(s)", check.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_figure_from_an_uncited_chunk_does_not_count_as_grounded()
    {
        // The chunk with 6.000 was retrieved but not cited: the handler checks the cited source and would not find it.
        var run = Grounded("Handlers may settle up to 6,000 EUR [KB-PROP-GUIDE#settlement-authority].",
            extraDocument: "<document chunk_id=\"KB-PROP-GUIDE#team-lead\" title=\"t\" section=\"s\" applies_to=\"a\" source=\"x\">\nTeam leads settle up to 6.000 EUR.\n</document>");

        Assert.Equal(CheckStatus.Fail, GateChecks.GroundedAnswers([run], Thresholds, judged: false, out _).Status);
    }

    [Fact]
    public void For_a_black_box_target_figure_grounding_is_not_measurable_and_never_decides()
    {
        var run = Grounded("Handlers may settle up to 6,000 EUR [KB-PROP-GUIDE#settlement-authority].");
        run.First!.TraceAvailable = false;

        var check = GateChecks.GroundedAnswers([run], Thresholds, judged: false, out _);

        Assert.Contains("figure grounding not measurable (black-box)", check.Result, StringComparison.Ordinal);
        Assert.DoesNotContain(check.Findings, f => f.Detail.Contains("num:6000", StringComparison.Ordinal));
    }

    [Fact]
    public void What_the_agent_withheld_is_reported()
    {
        var run = Grounded("Handlers may settle up to 5,000 EUR [KB-PROP-GUIDE#settlement-authority].");
        run.First!.Trace.GroundingWithheld = 1;
        run.First.Trace.GroundingMissing.Add("num:20");

        var check = GateChecks.GroundedAnswers([run], Thresholds, judged: false, out _);

        Assert.Contains("the agent withheld 1 sentence(s)", check.Result, StringComparison.Ordinal);
        Assert.Contains("num:20", check.Result, StringComparison.Ordinal);
        Assert.DoesNotContain(check.Notes, n => n.Contains("withheld", StringComparison.Ordinal)); // notes of a warning become conditions
    }

    [Fact]
    public void Figure_grounding_adds_no_condition_when_everything_is_found()
    {
        var check = GateChecks.GroundedAnswers([Grounded("Handlers may settle up to 5,000 EUR [KB-PROP-GUIDE#settlement-authority].")], Thresholds, judged: false, out _);
        Assert.DoesNotContain(check.Notes, n => n.Contains("Figure grounding", StringComparison.Ordinal));
    }

    private static ScenarioRun Grounded(string answer, string? extraDocument = null)
    {
        var run = Build.Run("B01", "business", answer, expect: e => e.AnswerFound = true);
        run.First!.Citations.Add(new AgentCitation { Ref = "KB-PROP-GUIDE#settlement-authority", DocId = "KB-PROP-GUIDE" });
        run.First.Trace.GroundingContext.Add(
            "<document chunk_id=\"KB-PROP-GUIDE#settlement-authority\" title=\"Property guideline\" section=\"Settlement authority\" applies_to=\"property\" source=\"approved\">\nClaims handlers may settle property claims up to 5.000 EUR on their own authority.\n</document>"
            + (extraDocument is null ? string.Empty : "\n" + extraDocument));
        return run;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PilotGate.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (PilotGate.slnx) not found.");
    }
}
