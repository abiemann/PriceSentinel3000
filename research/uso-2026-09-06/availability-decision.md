# USO data shortfall and Friday diagnostic

Recorded after the ten frozen development candidate runs, before any Friday USO
Replay. Protocol commit: `de74456`.

Monday and Tuesday returned explicit no-history errors. All five candidates
received identical counts on Wednesday (1,369 source observations) and Thursday
(1,247). Both are below the frozen minimum of 1,529. Every development account
remained at $1,400 with zero orders or fills. No profile qualifies for selection.
The full independent audits will establish the precise gap/warmup diagnostics.

The original selection rule remains unchanged: **there is no eligible winner**.
To finish inspecting the requested week's availability, run Friday with the
unchanged C1 baseline only. C1 was the original availability reference and the
first profile in the frozen order; it is not chosen by profit or Friday results.
Friday is an additional data/warmup diagnostic, not validation of a selected
winning strategy. Do not tune inputs or shorten warmup to force activity.

Provide `United States Oil Fund (USO) Confirmation - experimental.thinkscript`
as a byte-identical C1 research starter. Label it explicitly unvalidated for USO
in the report and manifest. Verify its development behavior matches C1 before
running Friday. The filename identifies the intended fund; the app symbol must
still be matched manually. This provides a reproducible starting point for better
history or future real-time Paper observations, without claiming an optimized
USO script. No successful performance conclusion follows from a flat account
that never completed warmup.
