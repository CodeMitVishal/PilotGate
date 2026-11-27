using System.Globalization;
using System.Text.Json;

using ClaimsAssist.Core.Ai;
using ClaimsAssist.Core.Guardrails;

namespace ClaimsAssist.Tests;

/// <summary>
/// Figure grounding: the shared corpus (tests/grounding-corpus.v1.json, also run by PilotGate), the abstention policy,
/// and a replay of the real answers saved in the evidence packs, with a mutation test that measures how many invented
/// figures the check catches.
/// </summary>
public sealed class GroundingTests
{
    private static readonly JsonElement Corpus = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestData.RepoRoot, "tests", "grounding-corpus.v1.json"))).RootElement;

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

    private static string[] Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Theory]
    [MemberData(nameof(Cases), "atoms")]
    public void Atoms_match_the_corpus(string id)
    {
        var c = Case("atoms", id);
        var atoms = GroundingVerifier.Atoms(c.GetProperty("text").GetString()!, c.GetProperty("source").GetBoolean());
        Assert.Equal(Strings(c, "expect").Order(StringComparer.Ordinal), atoms.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases), "verify")]
    public void Verification_matches_the_corpus(string id)
    {
        var c = Case("verify", id);
        var result = GroundingVerifier.Verify(c.GetProperty("answer").GetString()!, Strings(c, "sources"),
            c.GetProperty("question").GetString()!, Strings(c, "opened_claims"));
        Assert.Equal(c.GetProperty("expect_withheld").GetInt32(), result.Withheld);
        Assert.Equal(c.GetProperty("expect_text").GetString(), result.Text);
    }

    [Theory]
    [MemberData(nameof(Cases), "assemble")]
    public void Sources_are_assembled_from_what_the_model_saw(string id)
    {
        var c = Case("assemble", id);
        var sources = GroundingVerifier.SourcesFrom(Strings(c, "tool_results"), Strings(c, "cited"));
        var result = GroundingVerifier.Verify(c.GetProperty("answer").GetString()!, sources, c.GetProperty("question").GetString()!, Strings(c, "opened_claims"));
        Assert.Equal(c.GetProperty("expect_unsupported").GetInt32(), result.Withheld);
        Assert.Equal(c.GetProperty("expect_text").GetString(), result.Text);
    }

    [Fact]
    public void An_answer_with_nothing_verifiable_left_becomes_a_refusal()
    {
        var shown = GroundingVerifier.ForHandler("Jewellery is covered up to 20% of the sum insured.", answerFound: true,
            ["Hausrat Comfort covers burglary, fire, storm and escape of tap water."], "What is the jewellery limit?", []);

        Assert.Equal(GroundingVerifier.NotConfirmed, shown.Answer);
        Assert.False(shown.AnswerFound);
        Assert.Equal(["num:20"], shown.Check.Missing);
        Assert.Contains("approved sources do not", shown.Answer, StringComparison.Ordinal); // read as "not found" by PilotGate's DIAL mode
    }

    [Fact]
    public void A_partly_verified_answer_keeps_its_verified_sentences()
    {
        var shown = GroundingVerifier.ForHandler("Burst pipes are covered [KB-HR-AVB#insured-perils]. The limit is 7,000 EUR.", true,
            ["Burst pipes are covered as escape of tap water."], "Is a burst pipe covered?", []);

        Assert.Equal("Burst pipes are covered [KB-HR-AVB#insured-perils].", shown.Answer);
        Assert.True(shown.AnswerFound);
        Assert.Equal(1, shown.Check.Withheld);
    }

    [Fact]
    public void A_fully_verified_answer_is_unchanged()
    {
        const string answer = "You may settle up to 5,000 EUR on your own authority [KB-PROP-GUIDE#settlement-authority].";
        var shown = GroundingVerifier.ForHandler(answer, true, ["Handlers may settle up to 5.000 EUR."], "What is my authority?", []);

        Assert.Same(answer, shown.Answer);
        Assert.Equal((1, 0), (shown.Check.Statements, shown.Check.Withheld));
    }

    /// <summary>
    /// Replays the real answers of gate runs 1, 3, 5 and 6 (evidence packs in docs/evidence) through the check: the
    /// numbers below are the prototype's numbers too, so the port is exact. One sentence is withheld (run 1, "only the
    /// last four digits are shown": the source says "ending 3000", never "four"). Then every supported sentence gets
    /// its first figure changed twice (+1, and doubled): 195 of 210 changes are caught; the 15 missed ones turned into a
    /// figure that also appears in the cited sources (for example wind force 7 into 8, where the source states 8).
    /// </summary>
    [Fact]
    public void Replay_of_real_answers_withholds_one_inference_and_catches_most_invented_figures()
    {
        int answers = 0, statements = 0, withheld = 0, mutated = 0, caught = 0;
        foreach (var pack in new[] { "1-v1-baseline-NO-GO", "3-v2-hardened-GO-WITH-CONDITIONS", "5-v3-scope-fix-NO-GO-citation-bug", "6-v4-final-GO-WITH-CONDITIONS" })
        {
            foreach (var line in File.ReadLines(Path.Combine(TestData.RepoRoot, "docs", "evidence", pack, "traces.jsonl")))
            {
                using var record = JsonDocument.Parse(line);
                var question = record.RootElement.GetProperty("scenario").GetProperty("question").GetString()!;
                foreach (var label in new[] { "first", "afterDecision" })
                {
                    if (!record.RootElement.TryGetProperty(label, out var turn) || turn.ValueKind != JsonValueKind.Object
                        || turn.GetProperty("status").GetString() != "answered")
                    {
                        continue;
                    }

                    answers++;
                    var trace = turn.GetProperty("trace");
                    var sources = GroundingVerifier.SourcesFrom(
                        trace.GetProperty("groundingContext").EnumerateArray().Select(e => e.GetString()!),
                        turn.GetProperty("citations").EnumerateArray().Select(e => e.GetProperty("ref").GetString()!));
                    var opened = trace.GetProperty("openedClaimIds").EnumerateArray().Select(e => e.GetString()!).ToList();
                    var answer = turn.GetProperty("answer").GetString()!;
                    var result = GroundingVerifier.Verify(answer, sources, question, opened);
                    statements += result.Statements;
                    withheld += result.Withheld;

                    var support = new HashSet<string>(GroundingVerifier.Atoms(question, true));
                    foreach (var s in sources)
                    {
                        support.UnionWith(GroundingVerifier.Atoms(s, true));
                    }

                    support.UnionWith(opened.Select(o => "claim:" + o.ToUpperInvariant()));
                    foreach (var sentence in GroundingVerifier.Sentences(answer))
                    {
                        var atoms = GroundingVerifier.Atoms(sentence, false);
                        if (atoms.Count == 0 || !atoms.IsSubsetOf(support) || GroundingVerifier.FirstNumber(sentence) is not { } first)
                        {
                            continue;
                        }

                        foreach (var changed in new[] { first.Value + 1, first.Value > 1 ? first.Value * 2 : first.Value + 7 })
                        {
                            var text = first.Body[..first.Start] + changed.ToString("0.############################", CultureInfo.InvariantCulture)
                                + first.Body[(first.Start + first.Length)..];
                            mutated++;
                            if (!GroundingVerifier.Atoms(text, false).IsSubsetOf(support))
                            {
                                caught++;
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal((103, 121, 1), (answers, statements, withheld));
        Assert.Equal((195, 210), (caught, mutated));
    }

    [Theory]
    [InlineData("https://intranet.contoso.example")]
    [InlineData("Intranet.Contoso.Example")]
    [InlineData("intranet.contoso.example/path")]
    [InlineData("intranet.contoso.example:443")]
    [InlineData("localhost")]
    [InlineData(".contoso.example")]
    public void A_link_allow_list_entry_that_is_not_a_host_name_is_refused(string host)
    {
        var settings = new ClaimsAssistSettings { AllowedLinkHosts = [host] };
        Assert.Contains(settings.StartupProblems(), p => p.Contains("AllowedLinkHosts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(121, true)]
    [InlineData(5, false)]
    [InlineData(20, false)]
    public void The_model_timeout_must_be_sensible(int seconds, bool refused)
    {
        var problems = new ClaimsAssistSettings { ModelTimeoutSeconds = seconds }.StartupProblems();
        Assert.Equal(refused, problems.Any(p => p.Contains("ModelTimeoutSeconds", StringComparison.Ordinal)));
    }

    [Fact]
    public void Policy_lists_default_to_the_strictest_value()
    {
        var settings = new ClaimsAssistSettings();
        Assert.Empty(settings.AllowedLinkHosts); // no links allowed until configured
        Assert.Empty(settings.ApproverGroups);   // only the requesting handler decides
        Assert.Empty(new ClaimsAssistSettings { AuthMode = "DevPersonas", AllowedLinkHosts = ["intranet.contoso.example", "kb.contoso.example"] }.StartupProblems());
    }
}
