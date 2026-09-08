# Archived research

Completed studies are retained here so their results, assumptions, source hashes,
and selection decisions remain available. These are historical observations,
not current stock recommendations or proof of future profitability.
The [project history index](../README.md) also links past app audits and validation.

| Study or report | Contents |
| --- | --- |
| [Magnificent Seven](mag7-2026-09-06/README.md) | Seven-stock experiment and frozen profile selection. |
| [Low-price stocks](low-price-2026-09-06/README.md) | Screening and AAL, NU, and SOFI experiments. |
| [USO](uso-2026-09-06/README.md) | Oil-fund strategy experiment. |
| [SOXL and SOXX](semiconductors-2026-09-06/README.md) | Semiconductor-fund experiments. |
| [NFLX initial replay](nflx-replay-2026-09-06.md) | Netflix script development and validation. |
| [Strategy sources and compatibility](strategy-sources-and-compatibility.md) | Source selection, provenance, and interpreter compatibility investigation. |

Script sources are in [strategies](../../research/strategies/README.md); runnable analysis
programs are in [tools](../../research/tools/README.md). Raw replay exports and new generated
output remain under `artifacts` at the repository root.

Organization changes update file-location links and manifest paths only.
Recorded results, source hashes, session IDs, and original experiment dates are
preserved. Historical manifest hashes refer to the versions captured at the
original experiment commits, before file-location updates.

The semiconductor study retains `candidates/manifest.frozen.json` as the exact
manifest named by its selection freeze. Its auditor verifies that copy's hash
and permits only equivalent file-location changes in the current manifest;
script hashes, parameters, candidate order, and selection results remain checked.
