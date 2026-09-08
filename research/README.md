# Research workspace

| Folder | Purpose |
| --- | --- |
| [strategies](strategies/README.md) | Script sources, grouped trial scripts, and source-compatibility fixtures. |
| [tools](tools/README.md) | Reusable replay exporters, auditors, validation programs, and visualization tools. |

Completed experiments, dated reports, protocols, manifests, and recorded results
are indexed in the shared [research archive](../archive/research/README.md).
Current application guides belong in [docs](../docs/README.md); past app audits
and validation records are in the [app archive](../archive/app/README.md).
New exports, generated reports, and build output go under the ignored `artifacts`
directory. Archive a completed study deliberately; keep this directory's root
limited to `strategies/`, `tools/`, and this index.

Stock-specific filenames follow
`Company or Fund (TICKER) Strategy - experimental.thinkscript`. Only
`strategies/OriginalConfirmation.thinkscript` is bundled with the app. Installed
scripts in `%LOCALAPPDATA%\PriceSentinel3000\Strategies` may include newer local
versions than the repository or archived experiments.
