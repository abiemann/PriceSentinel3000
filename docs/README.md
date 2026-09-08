# Application documentation

This folder contains current application and developer guides. Past audits and
verification records are indexed in the [documentation archive](../archive/app/README.md).
Stock selection, strategy experiments, and performance comparisons belong in
[research](../research/README.md).
Remaining product work and release checks are tracked in [TODO](../TODO.md);
[DESIGN](../DESIGN.md) distinguishes implemented behavior from proposed features.

| Document | Purpose |
| --- | --- |
| [Architecture](architecture.md) | Components, dependencies, storage, and trading workflows. |
| [Strategy scripting](strategy-scripting.md) | Installing scripts, supported syntax, candle timing, and host risk controls. |
| [MCP automation](automation.md) | Controlling Replay/Paper sessions and reading app telemetry. |
| [Market-data library](market-data-library.md) | Download lists, schedules, local history, and Replay availability. |
| [Documentation archive](../archive/app/README.md) | Dated audit findings and software validation records for debugging. |

Put new dated audit and validation reports in the repository's `archive/app/` and add them to its
index. Keep current behavior and instructions in the guides above. Documentation
images belong in `images/`.
