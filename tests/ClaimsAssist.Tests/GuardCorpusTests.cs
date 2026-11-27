using System.Text.Json;
using ClaimsAssist.Core.Guardrails;

namespace ClaimsAssist.Tests;

/// <summary>
/// The shared corpus dial/schema/guard-corpus.v1.json is the contract between the .NET guards and DIAL Guard (Python).
/// Both test suites run it, so the two implementations cannot drift apart.
/// </summary>
public sealed class GuardCorpusTests
{
    private static readonly JsonElement Corpus = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestData.RepoRoot, "dial", "schema", "guard-corpus.v1.json"))).RootElement;

    public static TheoryData<string> Cases(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.GetProperty(section).EnumerateArray())
        {
            data.Add(c.GetProperty("id").GetString()!);
        }

        return data;
    }

    // The allow-list is configuration: the corpus brings its own, as the deployment brings its own.
    private static readonly string[] CorpusHosts = Corpus.GetProperty("allowed_link_hosts").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static JsonElement Case(string section, string id) =>
        Corpus.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    [Fact]
    public void Tables_and_policy_match_the_corpus()
    {
        Assert.Equal(UntrustedContentGuard.Placeholder, Corpus.GetProperty("placeholder").GetString());
        Assert.Equal(Corpus.GetProperty("tool_names").EnumerateArray().Select(e => e.GetString()).Order(), UntrustedContentGuard.ToolNames.Order());

        var invisible = Corpus.GetProperty("invisible").EnumerateArray().Select(e => Convert.ToInt32(e.GetString()![2..], 16)).Order();
        Assert.Equal(invisible, TextNormalizer.Invisible.Order());

        var confusables = Corpus.GetProperty("confusables").EnumerateObject()
            .ToDictionary(p => Convert.ToInt32(p.Name[2..], 16), p => p.Value.GetString()!.Single());
        Assert.Equal(confusables.OrderBy(p => p.Key), TextNormalizer.Confusables.OrderBy(p => p.Key));
    }

    [Theory]
    [MemberData(nameof(Cases), "quarantine")]
    public void Quarantine(string id)
    {
        var c = Case("quarantine", id);
        var cleaned = UntrustedContentGuard.CleanUntrusted(c.GetProperty("text").GetString()!);
        Assert.Equal(c.GetProperty("expect_changed").GetBoolean(), cleaned.Removed > 0);
        Assert.Equal(c.GetProperty("expect_text").GetString(), cleaned.Text);
    }

    [Theory]
    [MemberData(nameof(Cases), "links")]
    public void Links(string id)
    {
        var c = Case("links", id);
        var removed = UntrustedContentGuard.RemoveUnapprovedLinks(c.GetProperty("text").GetString()!, CorpusHosts).Removed > 0;
        Assert.Equal(c.GetProperty("expect_disallowed").GetBoolean(), removed);
    }

    [Theory]
    [MemberData(nameof(Cases), "pii")]
    public void Pii(string id)
    {
        var c = Case("pii", id);
        var (text, _) = new PiiRedactor([]).Redact(c.GetProperty("text").GetString()!, includeNames: false);
        Assert.Equal(c.GetProperty("expect_text").GetString(), text);
        var kinds = new List<string>();
        if (text.Contains("[EMAIL]", StringComparison.Ordinal)) kinds.Add("email");
        if (text.Contains("[IBAN ending", StringComparison.Ordinal)) kinds.Add("iban");
        if (text.Contains("[PHONE]", StringComparison.Ordinal)) kinds.Add("phone_de");
        if (text.Contains("[DATE]", StringComparison.Ordinal)) kinds.Add("date_de");
        Assert.Equal(c.GetProperty("expect_kinds").EnumerateArray().Select(e => e.GetString()), kinds.Order());
    }

    [Fact]
    public void Surrogate_pairs_and_expansions_fold_and_map_back()
    {
        // raw: x(0) 𝐢(1-2) 𝐠(3-4) y(5) ﬁ(6) z(7); view: x i g y f i z
        var view = TextNormalizer.Folded("x\U0001D422\U0001D420yﬁz");
        Assert.Equal("xigyfiz", view.Text);
        Assert.Equal(1, view.RawStart(1));
        Assert.Equal(3, view.RawEnd(2)); // the view span [1, 2) covers the whole surrogate pair
        Assert.Equal(6, view.RawStart(4));
        Assert.Equal(7, view.RawEnd(6)); // "fi" came from one ligature character
    }
}
