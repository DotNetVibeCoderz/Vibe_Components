using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AquaCulture.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

// AquaCulture.Net benchmarks. Made by Gravicode Studios, led by Kang Fadhil.
//
//   dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --filter *              (interactive BenchmarkDotNet)
//   dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --budgets budgets.json --job short --filter *
//
// With --budgets the run fails (exit code 1) when a benchmark's mean time or allocations exceed its budget,
// or when a budgeted benchmark did not run. Budgets are ceilings for a CI runner, not targets.

int bi = Array.IndexOf(args, "--budgets");
string? budgetsPath = bi >= 0 && bi + 1 < args.Length ? args[bi + 1] : null;
var bdnArgs = bi >= 0 ? args.Where((_, i) => i != bi && i != bi + 1).ToArray() : args;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(KernelBenchmarks).Assembly).Run(bdnArgs, DefaultConfig.Instance);
if (budgetsPath is null) return 0;

var results = BudgetCheck.Collect(summaries);
var budgets = BudgetCheck.Load(File.ReadAllText(budgetsPath));
var (report, failures) = BudgetCheck.Evaluate(results, budgets);
Console.WriteLine(report);
if (System.Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
    File.AppendAllText(stepSummary, report + "\n");
if (failures > 0)
{
    Console.Error.WriteLine($"{failures} performance budget(s) exceeded.");
    return 1;
}
Console.WriteLine("All performance budgets met.");
return 0;

namespace AquaCulture.Benchmarks
{
    /// <summary>A measured benchmark: mean time per operation (µs) and bytes allocated per operation.</summary>
    public sealed record BenchResult(string Key, double MeanUs, long? AllocatedBytes);

    /// <summary>A ceiling for one benchmark. Null fields are not checked.</summary>
    public sealed record Budget(string Key, double? MeanUs, long? AllocatedBytes);

    public static class BudgetCheck
    {
        /// <summary>Key of a benchmark case: <c>Type.Method</c> plus <c>[Param=Value]</c> when parameterised.</summary>
        public static string KeyOf(BenchmarkDotNet.Running.BenchmarkCase c) =>
            $"{c.Descriptor.Type.Name}.{c.Descriptor.WorkloadMethod.Name}" + (c.HasParameters ? $"[{c.Parameters.PrintInfo}]" : "");

        public static List<BenchResult> Collect(IEnumerable<Summary> summaries) =>
            summaries.SelectMany(s => s.Reports.Where(r => r.ResultStatistics is not null).Select(r =>
                new BenchResult(KeyOf(r.BenchmarkCase), r.ResultStatistics!.Mean / 1000.0, r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase))))
            .ToList();

        /// <summary>Reads <c>{ "Key": { "meanUs": 120, "allocatedBytes": 0 } }</c>; keys starting with "_" are comments.</summary>
        public static List<Budget> Load(string json)
        {
            var root = JsonNode.Parse(json)!.AsObject();
            return root.Where(kv => !kv.Key.StartsWith('_') && kv.Value is JsonObject)
                .Select(kv => new Budget(kv.Key, (double?)kv.Value!["meanUs"], (long?)kv.Value!["allocatedBytes"]))
                .ToList();
        }

        /// <summary>Compares results with budgets; returns a Markdown table and the number of violations.</summary>
        public static (string Markdown, int Failures) Evaluate(IReadOnlyList<BenchResult> results, IReadOnlyList<Budget> budgets)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("## AquaCulture.Net performance budgets\n\n| Benchmark | Mean (µs) | Budget (µs) | Allocated (B) | Budget (B) | Status |\n|---|---:|---:|---:|---:|---|\n");
            int failures = 0;
            foreach (var b in budgets)
            {
                var r = results.FirstOrDefault(x => x.Key == b.Key);
                if (r is null)
                {
                    failures++;
                    sb.Append(ci, $"| {b.Key} | – | {b.MeanUs?.ToString("0.##", ci) ?? "–"} | – | {b.AllocatedBytes?.ToString(ci) ?? "–"} | ❌ did not run |\n");
                    continue;
                }
                bool timeOk = b.MeanUs is not { } mu || r.MeanUs <= mu;
                bool allocOk = b.AllocatedBytes is not { } ab || r.AllocatedBytes is not { } ra || ra <= ab;
                if (!timeOk || !allocOk) failures++;
                string status = timeOk && allocOk ? "✅" : !timeOk && !allocOk ? "❌ time, allocations" : !timeOk ? "❌ time" : "❌ allocations";
                sb.Append(ci, $"| {b.Key} | {r.MeanUs:0.###} | {b.MeanUs?.ToString("0.##", ci) ?? "–"} | {r.AllocatedBytes?.ToString(ci) ?? "–"} | {b.AllocatedBytes?.ToString(ci) ?? "–"} | {status} |\n");
            }
            foreach (var r in results.Where(r => budgets.All(b => b.Key != r.Key)))
                sb.Append(ci, $"| {r.Key} | {r.MeanUs:0.###} | – | {r.AllocatedBytes?.ToString(ci) ?? "–"} | – | no budget |\n");
            return (sb.ToString(), failures);
        }
    }
}
