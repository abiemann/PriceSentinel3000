using System.Text.Json;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Scripting;

public sealed class BundledStrategyTests
{
    [Fact]
    public void OriginalConfirmation_RepeatsExpectedPositiveAndNegativeDecisions()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Strategies");
        CompiledThinkScript program = ThinkScriptCompiler.Compile(File.ReadAllText(Path.Combine(directory, "OriginalConfirmation.thinkscript")));
        Assert.True(program.IsCompatible, string.Join("; ", program.Diagnostics.Where(d => d.IsError).Select(d => d.Message)));
        Assert.Equal(85, program.RequiredWarmupBars);
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(directory, "validation-fixtures.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Where(fixture => fixture.Source == "OriginalConfirmation.thinkscript").ToArray();
        Assert.Equal(5, fixtures.Length);
        foreach (Fixture fixture in fixtures)
        {
            DateTimeOffset start = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);
            StrategyBar[] bars = fixture.Closes.Select((price, index) => new StrategyBar(
                start.AddMinutes(index), start.AddMinutes(index + 1), price, price, price, price, 0m)).ToArray();
            StrategyPositionContext position = fixture.Holding ? new(1m, fixture.Closes[0], start) : StrategyPositionContext.Flat;
            ScriptProposal first = program.Evaluate(bars, position);
            ScriptProposal repeated = program.Evaluate(bars, position);
            Assert.True(first.State != "SCRIPT ERROR" && first.Action.ToString() == fixture.Expected,
                $"{fixture.Name}: expected {fixture.Expected}, got {first.Action}/{first.State}: {first.Reason}");
            Assert.Equal(first.Action, repeated.Action);
            Assert.Equal(first.State, repeated.State);
            Assert.Equal(first.Reason, repeated.Reason);
        }
    }

    private sealed record Fixture(string Name, string Source, decimal[] Closes, bool Holding, string Expected);
}
