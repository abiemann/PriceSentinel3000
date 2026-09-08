using System.Security.Cryptography;
using System.Text.Json;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

// Optional first argument: the locally retained, unchanged third-party source directory.
// No downloads, market-data requests, broker access, or execution of source as CLR code.
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
string sampleRoot = AppContext.BaseDirectory;
Fixture[] fixtures = JsonSerializer.Deserialize<Fixture[]>(
    File.ReadAllText(Path.Combine(sampleRoot, "validation-fixtures.json")), json)!;
Source[] references = JsonSerializer.Deserialize<Source[]>(
    File.ReadAllText(Path.Combine(sampleRoot, "research-sources.json")), json)!;
string? referenceRoot = args.Length == 0 ? null : Path.GetFullPath(args[0]);
var results = new List<object>();
bool passed = true;

foreach (string file in new[] { "OriginalConfirmation.thinkscript" }.Concat(references.Select(source => source.File)))
{
    Source? reference = references.SingleOrDefault(source => source.File == file);
    if (reference is not null && referenceRoot is null)
    {
        results.Add(new { File = file, Status = "SKIPPED", Reason = "Third-party research copies are not distributed; supply their local directory to check them." });
        continue;
    }

    string path = Path.Combine(reference is null ? sampleRoot : referenceRoot!, file);
    byte[] bytes = File.ReadAllBytes(path);
    string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (reference is not null && hash != reference.SourceSha256)
        throw new InvalidOperationException($"{file}: source hash changed; obtain the recorded unchanged version before comparing results.");

    CompiledThinkScript script = ThinkScriptCompiler.Compile(File.ReadAllText(path));
    bool compatibleExpected = reference?.ExpectedCompatible ?? true;
    bool compilationPassed = script.IsCompatible == compatibleExpected;
    passed &= compilationPassed;
    results.Add(new
    {
        File = file,
        SourceSha256 = hash,
        ExpectedCompatible = compatibleExpected,
        script.IsCompatible,
        script.RequiredWarmupBars,
        script.Diagnostics,
        Passed = compilationPassed,
    });
    if (!script.IsCompatible)
        continue;

    foreach (Fixture fixture in fixtures.Where(item => item.Source == file))
    {
        DateTimeOffset start = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);
        StrategyBar[] bars = fixture.Closes.Select((close, index) => new StrategyBar(
            start.AddMinutes(index), start.AddMinutes(index + 1), close, close, close, close, 0m)).ToArray();
        StrategyPositionContext position = fixture.Holding
            ? new(1m, fixture.Closes[0], start)
            : StrategyPositionContext.Flat;
        ScriptProposal proposal = script.Evaluate(bars, position);
        bool casePassed = proposal.Action.ToString() == fixture.Expected && proposal.State != "SCRIPT ERROR";
        passed &= casePassed;
        results.Add(new
        {
            Fixture = fixture.Name,
            File = file,
            Bars = bars.Length,
            fixture.Holding,
            fixture.Expected,
            Actual = proposal.Action.ToString(),
            proposal.State,
            proposal.Reason,
            Passed = casePassed,
        });
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = ThinkScriptCompiler.RuntimeVersion,
    TestedAtUtc = DateTimeOffset.UtcNow,
    Purpose = "Synthetic language and decision checks; not historical market backtests or evidence of profitability.",
    Passed = passed,
    Results = results,
}, json));
return passed ? 0 : 1;

internal sealed record Fixture(string Name, string Source, decimal[] Closes, bool Holding, string Expected);
internal sealed record Source(string File, string SourceSha256, bool ExpectedCompatible);
