using System.Reflection;
using System.Text.Json;
using Medical.Clinical;
using Medical.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NRules;
using NRules.Fluent;
using RulesEngine.Models;

namespace Medical.Rules;

/// <summary>
/// Clinical decision support engine. Facts from a <see cref="PatientRecord"/> are evaluated by the NRules rule set in
/// <c>Medical.Rules.Definitions</c> (compiled once into a Rete network and shared); the resulting alerts are de-duplicated
/// and ordered by severity. Each evaluation is audited.
/// </summary>
public sealed class ClinicalDecisionSupport(DrugKnowledge? drugs = null, IAuditLog? audit = null, IMedicalEventBus? bus = null)
{
    private static readonly Lazy<ISessionFactory> Factory = new(() =>
    {
        var repo = new RuleRepository();
        repo.Load(x => x.From(typeof(ClinicalDecisionSupport).Assembly));
        return repo.Compile();
    });

    private readonly DrugKnowledge _drugs = drugs ?? DrugKnowledge.Default;

    /// <summary>Names of the loaded rules.</summary>
    public static IReadOnlyList<string> RuleNames
    {
        get
        {
            var repo = new RuleRepository();
            repo.Load(x => x.From(typeof(ClinicalDecisionSupport).Assembly));
            return repo.GetRules().Select(r => r.Name).ToArray();
        }
    }

    public IReadOnlyList<ClinicalAlert> Evaluate(PatientRecord record, DateOnly? today = null)
    {
        var session = Factory.Value.CreateSession();
        session.InsertAll(FactBuilder.Build(record, _drugs, today));
        session.Fire();
        var alerts = session.Query<ClinicalAlert>().DistinctBy(a => a.Key)
            .OrderByDescending(a => a.Severity).ThenBy(a => a.Category).ThenBy(a => a.Rule, StringComparer.Ordinal).ToArray();
        audit?.Record(new AuditEvent(DateTimeOffset.UtcNow, "cds", AuditAction.Alert, "ClinicalDecisionSupport", record.Patient.Id, $"{alerts.Length} alert(s)"));
        foreach (var a in alerts) bus?.Publish(a);
        return alerts;
    }
}

/// <summary>Flat snapshot passed to JSON rule workflows (Microsoft RulesEngine expressions use <c>input1.Property</c>).</summary>
public sealed record ClinicalSnapshot
{
    public int Age { get; init; }
    public string Gender { get; init; } = "unknown";
    public int News2 { get; init; }
    public int QSofa { get; init; }
    public double HeartRate { get; init; }
    public double RespiratoryRate { get; init; }
    public double SystolicBp { get; init; }
    public double SpO2 { get; init; }
    public double TemperatureC { get; init; }
    public bool Alert { get; init; } = true;
    public double Lactate { get; init; }
    public double Egfr { get; init; }
    public double Glucose { get; init; }
    public double Wbc { get; init; }
    public double Platelets { get; init; }
    public bool Diabetes { get; init; }
    public bool Hypertension { get; init; }
    public bool Ckd { get; init; }
    public bool Pneumonia { get; init; }
    public bool Dengue { get; init; }
    public int Medications { get; init; }

    public static ClinicalSnapshot From(PatientRecord r, DateOnly? today = null)
    {
        var v = r.LatestVitals;
        var day = today ?? DateOnly.FromDateTime(v?.Timestamp.UtcDateTime ?? DateTime.UtcNow);
        int age = r.Patient.AgeAt(day);
        double creat = r.Latest(Loinc.Creatinine)?.Value ?? 0;
        return new ClinicalSnapshot
        {
            Age = age,
            Gender = r.Patient.Gender.ToString().ToLowerInvariant(),
            News2 = v is null ? 0 : ClinicalScores.News2(v).Score,
            QSofa = v is null ? 0 : ClinicalScores.QSofa(v).Score,
            HeartRate = v?.HeartRate ?? 0,
            RespiratoryRate = v?.RespiratoryRate ?? 0,
            SystolicBp = v?.SystolicBp ?? 0,
            SpO2 = v?.SpO2 ?? 0,
            TemperatureC = v?.TemperatureC ?? 0,
            Alert = v?.Consciousness is null or Consciousness.Alert,
            Lactate = r.Latest(Loinc.Lactate)?.Value ?? 0,
            Egfr = creat > 0 ? Calculators.EgfrCkdEpi2021(creat, age, r.Patient.Gender).Egfr : 0,
            Glucose = r.Latest(Loinc.Glucose)?.Value ?? 0,
            Wbc = r.Latest(Loinc.Wbc)?.Value ?? 0,
            Platelets = r.Latest(Loinc.Platelets)?.Value ?? 0,
            Diabetes = r.HasCondition("E10") || r.HasCondition("E11"),
            Hypertension = r.HasCondition("I10"),
            Ckd = r.HasCondition("N18"),
            Pneumonia = r.HasCondition("J18"),
            Dengue = r.HasCondition("A90") || r.HasCondition("A91"),
            Medications = r.Medications.Count,
        };
    }
}

/// <summary>Outcome of one JSON rule.</summary>
public sealed record WorkflowRuleResult(string Rule, bool Matched, string? Event, string? Error);

/// <summary>
/// Configurable rule workflows (Microsoft RulesEngine) that clinical teams can edit as JSON without recompiling —
/// e.g. triage destination, BPJS referral criteria, dengue warning signs. Built-in workflows are embedded resources.
/// </summary>
public sealed class ConfigurableRules
{
    private readonly RulesEngine.RulesEngine _engine;

    public ConfigurableRules(IEnumerable<Workflow>? workflows = null)
    {
        var list = (workflows ?? LoadEmbedded()).ToArray();
        WorkflowNames = list.Select(w => w.WorkflowName).ToArray();
        _engine = new RulesEngine.RulesEngine(list, new ReSettings { CustomTypes = [typeof(Math)] });
    }

    public IReadOnlyList<string> WorkflowNames { get; }

    /// <summary>Loads workflows from RulesEngine JSON (array of <c>{ "WorkflowName", "Rules": [ { "RuleName", "Expression", "SuccessEvent" } ] }</c>).</summary>
    public static Workflow[] Parse(string json) =>
        JsonSerializer.Deserialize<Workflow[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    public static IEnumerable<Workflow> LoadEmbedded()
    {
        var asm = typeof(ConfigurableRules).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Medical.Rules.Workflows.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(s);
            foreach (var w in Parse(reader.ReadToEnd())) yield return w;
        }
    }

    public async Task<IReadOnlyList<WorkflowRuleResult>> EvaluateAsync(string workflow, ClinicalSnapshot snapshot)
    {
        var results = await _engine.ExecuteAllRulesAsync(workflow, snapshot).ConfigureAwait(false);
        return results.Select(r => new WorkflowRuleResult(r.Rule.RuleName, r.IsSuccess, r.IsSuccess ? r.Rule.SuccessEvent : null,
            string.IsNullOrEmpty(r.ExceptionMessage) ? null : r.ExceptionMessage)).ToArray();
    }
}

public static class RulesBuilderExtensions
{
    /// <summary>Registers <see cref="DrugKnowledge"/>, <see cref="ClinicalDecisionSupport"/> and <see cref="ConfigurableRules"/>.</summary>
    public static MedicalBuilder AddRules(this MedicalBuilder builder) =>
        builder.TryAddModule("rules", s =>
        {
            s.TryAddSingleton(DrugKnowledge.Default);
            s.TryAddSingleton(sp => new ClinicalDecisionSupport(sp.GetService<DrugKnowledge>(), sp.GetService<IAuditLog>(), sp.GetService<IMedicalEventBus>()));
            s.TryAddSingleton(_ => new ConfigurableRules());
        });
}
