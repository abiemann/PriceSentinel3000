# Strategy source files

All repository `.thinkscript` files live here. Application documentation is in
[docs](../../docs/README.md), completed experiment reports and results are in
[archive](../../archive/research/README.md), and analysis tools are in [tools](../tools/README.md).

| Location | Contents |
| --- | --- |
| This directory | Fourteen named stock/fund profiles and the original example. |
| [candidates](candidates/) | Fifteen trial scripts, grouped by the study that evaluated them. |
| [validation](validation/) | Source provenance, synthetic decision fixtures, recorded compatibility results, and the validation program. |

`OriginalConfirmation.thinkscript` is the one bundled educational example.
The application build copies it to `Strategies/OriginalConfirmation.thinkscript`;
moving the repository source does not change installed strategy IDs or contents.
The other profiles are research versions and may differ from newer local scripts.

The stock/fund profiles and original example declare
`# PriceSentinel: tested-candle-seconds=60`, their reference tested strategy
interval. This is separate from the source data resolution. See the
[interval metadata guide](../../docs/strategy-scripting.md#tested-candle-interval)
for selection and override behavior. The frozen trial scripts in `candidates`
retain their original bytes; their test configuration stays in the study records.

To use a stock profile, copy its `.thinkscript` file directly into
`%LOCALAPPDATA%\PriceSentinel3000\Strategies`, then refresh and select it in the app.
The app does not scan nested folders. Use the stock/fund ticker in the filename
when choosing the session symbol; the script itself does not enforce the symbol.

Run the original example's synthetic compatibility checks from the repository root:

```powershell
dotnet run --project research/strategies/validation/StrategyExamples.csproj
```

Names, source hashes, session IDs, and observed results in historical evidence
describe the original runs. Relocation preserved source bytes; subsequent
interval annotations change active files' hashes without changing their rules.
Use the sources pinned in the recorded sessions or experiment commits when
reproducing historical hashes. Manifest file paths point to the new locations;
older recorded manifest hashes refer to the versions captured at those
experiments' commits.
