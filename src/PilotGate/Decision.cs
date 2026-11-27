using System.Globalization;

namespace PilotGate;

public sealed class ScreeningAnswers
{
    public string DeploymentCountry { get; set; } = "DE";

    public bool ProhibitedPractice { get; set; }

    public bool LifeOrHealthInsuranceRiskAssessmentOrPricing { get; set; }

    public bool CreditworthinessEvaluation { get; set; }

    public bool EmploymentDecisions { get; set; }

    public bool BiometricIdentification { get; set; }

    public bool InteractsWithNaturalPersons { get; set; }

    public bool SolelyAutomatedDecisionsWithLegalEffect { get; set; }

    public bool ProcessesHealthData { get; set; }

    public bool LogsIdentifyEmployees { get; set; }
}

public sealed record ScreeningResult(string Tier, string Basis, List<string> Obligations, List<string> Conditions, List<string> Notes);

/// <summary>EU AI Act and GDPR screening of the use case. A screening aid for the client's legal team, not legal advice.</summary>
public static class AiActScreening
{
    public const string HighRisk = "High-risk";

    public static ScreeningResult Screen(ScreeningAnswers a)
    {
        var obligations = new List<string>();
        var conditions = new List<string>();
        var notes = new List<string>();
        string tier;
        string basis;

        var annexIii = new[]
        {
            a.LifeOrHealthInsuranceRiskAssessmentOrPricing ? "Annex III point 5(c): risk assessment and pricing in life and health insurance" : null,
            a.CreditworthinessEvaluation ? "Annex III point 5(b): creditworthiness evaluation" : null,
            a.EmploymentDecisions ? "Annex III point 4: employment and worker management" : null,
            a.BiometricIdentification ? "Annex III point 1: biometrics" : null,
        }.OfType<string>().ToList();

        if (a.ProhibitedPractice)
        {
            tier = "Prohibited";
            basis = "Art. 5: prohibited AI practice";
            conditions.Add("Stop: a prohibited practice cannot go to production in the EU.");
        }
        else if (annexIii.Count > 0)
        {
            tier = HighRisk;
            basis = string.Join("; ", annexIii);
            obligations.Add("Arts. 9 to 15: risk management, data governance, technical documentation, record-keeping, transparency to deployers, human oversight, accuracy and robustness.");
            obligations.Add("Conformity assessment, EU database registration and post-market monitoring before use.");
            obligations.Add("Art. 26: deployer duties, including human oversight by trained staff and keeping the logs.");
        }
        else if (a.InteractsWithNaturalPersons)
        {
            tier = "Limited risk (transparency obligations)";
            basis = "Art. 50(1): the system interacts directly with natural persons; no Annex III use";
            obligations.Add("Art. 50(1): users must be told they are interacting with an AI system. Verified at runtime by the TRANSPARENCY check.");
        }
        else
        {
            tier = "Minimal risk";
            basis = "No Annex III use and no direct interaction with natural persons";
        }

        if (tier != HighRisk && tier != "Prohibited")
        {
            notes.Add("Scope trigger: using the assistant for risk assessment or pricing in life or health insurance would make it high-risk (Annex III point 5(c)). Re-run this screening on every scope change.");
        }

        if (a.ProcessesHealthData)
        {
            conditions.Add("GDPR Art. 9 special-category (health) data is processed: complete a data protection impact assessment (GDPR Art. 35) before production.");
        }

        if (a.LogsIdentifyEmployees && string.Equals(a.DeploymentCountry, "DE", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("Works council: the audit trail could be used to monitor individual handlers, so co-determination applies (BetrVG §87(1) no. 6). Agree a works agreement before production; logs are pseudonymised already.");
        }

        if (a.SolelyAutomatedDecisionsWithLegalEffect)
        {
            conditions.Add("Solely automated decisions with legal effect: GDPR Art. 22 safeguards (human intervention, right to contest) are required.");
        }
        else
        {
            notes.Add("Settlements need a human approval step, so no decision is solely automated (GDPR Art. 22).");
        }

        notes.Add("Screening aid, not legal advice. Several AI Act application dates changed in 2026 (Digital Omnibus); confirm the current dates on EUR-Lex with the client's legal team.");
        return new ScreeningResult(tier, basis, obligations, conditions, notes);
    }
}

public sealed record BusinessCaseResult(
    string Metric,
    double MinutesSavedPerSuccess,
    double ExpectedNetMinutesPerLookup,
    double ConservativeNetMinutesPerLookup,
    double ExpectedHoursPerMonth,
    double ConservativeHoursPerMonth,
    double ExpectedValueEurPerMonth,
    double ConservativeValueEurPerMonth,
    double ModelCostPerMonth,
    string ModelCostCurrency,
    string Source);

public static class BusinessCase
{
    /// <summary>
    /// Net minutes per lookup = success rate x minutes saved - failure rate x minutes wasted on the assistant
    /// before falling back to the manual search. Conservative case: Wilson lower bound and half the saving.
    /// </summary>
    public static BusinessCaseResult Compute(BusinessCaseConfig cfg, TaskStats tasks, CostStats cost, string currency)
    {
        var saved = cfg.BaselineMinutesPerLookup - cfg.AssistedMinutesPerLookup;
        double Net(double rate, double minutesSaved) => (rate * minutesSaved) - ((1 - rate) * cfg.AssistedMinutesPerLookup);

        var expected = Net(tasks.Rate, saved);
        var conservative = Net(tasks.WilsonLow, saved * 0.5);
        double Hours(double minutes) => cfg.LookupsPerMonth * minutes / 60.0;

        return new BusinessCaseResult(
            cfg.Metric,
            saved,
            expected,
            conservative,
            Hours(expected),
            Hours(conservative),
            Hours(expected) * cfg.LoadedCostPerHourEur,
            Hours(conservative) * cfg.LoadedCostPerHourEur,
            cost.MeanUsd * cfg.LookupsPerMonth,
            currency,
            cfg.Source);
    }
}

public sealed record Verdict(string Decision, List<string> Reasons, List<string> Conditions);

public static class GateVerdict
{
    public const string NoGo = "NO-GO";
    public const string GoWithConditions = "GO WITH CONDITIONS";
    public const string Go = "GO";

    /// <summary>
    /// NO-GO when any check fails: blocker checks are invariants, quality checks carry the business case.
    /// GO WITH CONDITIONS when checks pass but warnings or compliance prerequisites remain.
    /// </summary>
    public static Verdict Decide(IReadOnlyList<CheckResult> checks, ScreeningResult screening)
    {
        if (screening.Tier == "Prohibited")
        {
            return new Verdict(NoGo, ["The use case is a prohibited AI practice (EU AI Act Art. 5)."], []);
        }

        var failed = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
        if (failed.Count > 0)
        {
            return new Verdict(NoGo, failed.Select(c => $"{c.Id} failed: {c.Result}").ToList(), []);
        }

        var conditions = checks.Where(c => c.Status == CheckStatus.Warn)
            .SelectMany(c => c.Notes.Count > 0 ? c.Notes.Select(n => $"{c.Id}: {n}") : [$"{c.Id}: {c.Result}"])
            .Concat(screening.Conditions)
            .ToList();

        if (screening.Tier == AiActScreening.HighRisk)
        {
            conditions.Insert(0, "High-risk under the EU AI Act: the Arts. 9 to 15 obligations and a conformity assessment are prerequisites.");
        }

        return conditions.Count > 0
            ? new Verdict(GoWithConditions, [string.Create(CultureInfo.InvariantCulture, $"All {checks.Count} checks passed or warned; {conditions.Count} condition(s) remain before production.")], conditions)
            : new Verdict(Go, [$"All {checks.Count} checks passed with no open conditions."], []);
    }
}
