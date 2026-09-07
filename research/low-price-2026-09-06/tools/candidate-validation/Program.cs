using System.Security.Cryptography;
using System.Text.Json;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

var options = new JsonSerializerOptions { WriteIndented = true };
var results = new List<object>();
bool passed = true;
string root = Path.Combine(AppContext.BaseDirectory, "candidates");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
var held = new StrategyPositionContext(1m, 10m, DateTimeOffset.UnixEpoch);
var warmups = new Dictionary<string, int> { ["C1"] = 85, ["C2"] = 85, ["C3"] = 85, ["B1"] = 84, ["R1"] = 51 };
foreach (string id in warmups.Keys)
{
    string path = Path.Combine(root, $"LowPrice Research {id}.thinkscript");
    string source = File.ReadAllText(path);
    var script = ThinkScriptCompiler.Compile(source);
    string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    string expectedHash = manifest.RootElement.GetProperty("Candidates").EnumerateArray()
        .Single(candidate => candidate.GetProperty("Id").GetString() == id).GetProperty("SourceSha256").GetString()!;
    bool compilePassed = script.IsCompatible && script.RequiredWarmupBars == warmups[id] && sourceSha256 == expectedHash;
    passed &= compilePassed;
    var checks = new List<object>();
    if (script.IsCompatible)
    {
        Check("insufficient warmup", Enumerable.Repeat(10m, script.RequiredWarmupBars - 1).ToArray(), false, "Hold", "WARMING UP");
        Check("flat after warmup", Enumerable.Repeat(10m, script.RequiredWarmupBars).ToArray(), false, "Hold");
        decimal[] entry = id.StartsWith('C')
            ? Enumerable.Range(0, 100).Select(i => 10m + i * 0.01m).Concat([10.93m, 10.89m, 10.98m]).ToArray()
            : id == "B1" ? Enumerable.Repeat(10m, 100).Append(10.1m).ToArray()
            : Enumerable.Repeat(10m, 100).Concat([9.9m, 9.8m, 9.7m, 9.8m, 9.82m]).ToArray();
        Check("entry fixture", entry, false, "Buy");
        Check("entry does not add while long", entry, true, "Hold");
        decimal[] exit = Enumerable.Range(0, 100).Select(i => 11m - i * 0.01m).ToArray();
        Check("weakness exits long", exit, true, "Sell");
        Check("exit does not open a short", exit, false, "Hold");
        decimal[] maximumHistory = Enumerable.Range(0, ThinkScriptCompiler.MaximumBars)
            .Select(i => 10m + (decimal)Math.Sin(i / 9.0) * 0.2m + i * 0.0001m).ToArray();
        Check("maximum 4096 bars flat", maximumHistory, false);
        Check("maximum 4096 bars long", maximumHistory, true);
    }
    results.Add(new { Id = id, File = Path.GetFileName(path), SourceSha256 = sourceSha256,
        script.IsCompatible, script.RequiredWarmupBars, HashMatchesManifest = sourceSha256 == expectedHash,
        Errors = script.Diagnostics.Where(diagnostic => diagnostic.IsError), CompilePassed = compilePassed, Checks = checks });

    void Check(string name, decimal[] values, bool holding, string? expectedAction = null, string? expectedState = null)
    {
        StrategyBar[] bars = values.Select((value, i) => new StrategyBar(DateTimeOffset.UnixEpoch.AddMinutes(i),
            DateTimeOffset.UnixEpoch.AddMinutes(i + 1), value, value, value, value, 0m)).ToArray();
        var proposal = script.Evaluate(bars, holding ? held : StrategyPositionContext.Flat);
        var repeat = script.Evaluate(bars, holding ? held : StrategyPositionContext.Flat);
        bool checkPassed = proposal.State != "SCRIPT ERROR" && (expectedAction is null || proposal.Action.ToString() == expectedAction)
            && (expectedState is null || proposal.State == expectedState) && !proposal.IndicatorsTruncated
            && proposal.Indicators.Count <= proposal.IndicatorLimit && proposal.Action == repeat.Action
            && proposal.State == repeat.State && proposal.Reason == repeat.Reason
            && proposal.Indicators.SequenceEqual(repeat.Indicators);
        passed &= checkPassed;
        checks.Add(new { Name = name, Bars = bars.Length, Holding = holding,
            Action = proposal.Action.ToString(), proposal.State, proposal.IndicatorCount, Passed = checkPassed });
    }
}
string output = JsonSerializer.Serialize(new { Runtime = ThinkScriptCompiler.RuntimeVersion, TestedAtUtc = DateTimeOffset.UtcNow,
    Purpose = "Original candidate syntax, conservative warmup, synthetic decisions, determinism, and maximum-history resource check. No market data or profitability assessment.",
    CaseCount = 40, EachCaseRepeated = true, Passed = passed, Results = results }, options);
Console.WriteLine(output);
if (args.Length > 0) File.WriteAllText(args[0], output);
return passed ? 0 : 1;
