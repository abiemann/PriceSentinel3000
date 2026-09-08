# Research tools

| Folder | Tools |
| --- | --- |
| [low-price](low-price/README.md) | Shared MCP replay exporter, exact-decimal ledger and indicator auditors, replay comparison, visual reports, and candidate validation. |
| [mag7](mag7/) | PowerShell development and frozen-selection audits for the seven-stock study. |
| [uso](uso/README.md) | Oil-fund audit adapter and source-gap diagnostics. |
| [semiconductors](semiconductors/README.md) | SOXL/SOXX audit adapter with independent eligibility checks per fund. |

USO reuses the low-price audit implementation; the semiconductor adapter also
reuses USO's diagnostics. Keep these tool groups together. Their default study
inputs come from [archive](../../archive/research/README.md), and source candidates come
from [strategies](../strategies/README.md).

Write new generated output to the ignored repository `artifacts` directory.
The archived JSON reports are evidence from completed runs; creating another
report should not overwrite them. Group-specific READMEs include run commands.

The replay exporter controls the visible app through MCP. The Python and
PowerShell audit tools inspect existing exports without broker or app access.
