# Local market-data library

Open **Tools > Retain Hi-Res Data** in the app header. The clock lights green only after automatic downloads are enabled and saved. The tool window is modeless, so the main trading controls remain accessible.

## Choose equities

In **Equity lists**, create a named list and paste symbols separated by spaces, commas, or newlines. Connected lookups show company or fund names and reject unsupported symbols. Offline lists can be saved; identities are checked when downloading. Check individual equities and **Save list**. Unchecking a member or removing a list does not delete downloaded history.

**Load Robinhood lists** retrieves accessible personal lists. Choose one and **Preview import**, then select individual equities and save a local copy. The app validates the returned item count and rejects incomplete or paginated responses it cannot finish. It resolves equity identities separately and never edits the Robinhood list. **Refresh source** previews additions and removals, preserving local exclusions; save to apply the preview. Repeating an import lets you keep multiple local lists.

**Export lists** writes portable JSON containing names, symbols and inclusion choices. Private Robinhood list and instrument IDs stay in the local configuration. **Import list file** adds copies with new local IDs.

## Schedule and download

In **Schedule & downloads**, choose the daily time in `HH:mm`, its time zone, regular or extended coverage, and the library folder. Press **Save schedule & folder**. The default is 13:15 in the computer's local zone. The saved zone follows daylight saving time; later computer-zone changes do not move it. A skipped clock time runs at the first valid minute; a repeated clock time runs once at the earlier occurrence.

Automatic collection requires the app to remain open and connected. It uses one deduplicated union of saved, enabled lists and included equities. At the chosen time it targets the latest session whose close plus 15 minutes has passed. A time before today's finalization collects the prior eligible session. Regular sessions are 09:30–16:00 Eastern, or 13:00 on early-close days. Extended coverage is 04:00–20:00 Eastern, or 17:00 on early-close days. The calendar includes recurring US exchange holidays and early closes; unscheduled closures are not predicted.

If authorization expires, use **Reconnect Robinhood** while trading and downloads are idle. This is an explicit login action; scheduled collection never opens a browser for authorization. Reconnecting is refused while a provider request is active.

Missed runs catch up within seven calendar days, bounded by when automatic collection was enabled. Pending work and progress survive restarts. Disconnected jobs wait for a connection; background requests cannot open a login prompt. Turn off and save automatic downloads to pause automatic queued work. This version does not download while the app is closed.

**Download now** queues the saved equities for an explicit date range, then connects if needed. Ending weekends/holidays are skipped; unfinished trading sessions are rejected. **Retry missing** requeues partial, unavailable and failed jobs. **Cancel request** cancels the active request, including login; pending work is retained and can retry on the next polling cycle. Requests are serial and rate limited, transient retries are bounded, and failures are isolated by ticker.

The job table shows the actual source interval and status. A complete saved 15-second session is reused without downloading again. If 15-second data is unavailable, the provider is tried at 30 seconds and then 60 seconds. A coarse result is marked partial relative to the requested high-resolution archive, even when its own candle coverage is complete. Existing sparse fine candles are preserved; no missing prices are reconstructed.

## Portable history

The default root is `%LOCALAPPDATA%\PriceSentinel3000\MarketData`. The folder can be changed when a download is not active. For example:

```text
MarketData/
  README.md
  2026/
    08 - August/
      NFLX/
        2026-08-24.15s.json
    09 - September/
      SOXL/
        2026-09-04.15s.json
```

Only needed folders are created. Month names are fixed English with numeric prefixes. Daily grouping uses `America/New_York`; each candle retains exact UTC start, end and availability times. Schema 1 accepts finalized candles available at source close. Fetch time is separate.

Each self-contained UTF-8 JSON document includes schema version, public instrument identity, provider, actual interval, adjustment policy/basis, requested coverage, gaps and an immutable SHA-256 dataset hash. Prices and volumes are invariant decimal strings. Unknown volume is `null`; complete price coverage does not imply complete OHLCV. The library's generated README describes these rules and canonical hashing.

Copy a day file, ticker folder, month or year into another library, then **Rescan library**. No original database, credentials or sidecars are needed. Scans validate content and hashes instead of trusting filenames. Malformed, tampered and unfinished temporary files are diagnosed. Identical data fetched again is deduplicated; corrected data produces a `.rev-<hash>.json` file while preserving the old revision. The app does not automatically delete history.

Robinhood supplies split-adjusted data without an explicit adjustment revision. Files disclose this as `robinhood-split-unversioned`; hashes pin the exact prices fetched. Different provider/instrument/adjustment identities cannot be silently combined. Conflicting same-day revisions require pins or the explicit **Use latest fetched revision** choice; equal fetch times use a stable hash tie-break.

## Replay and analysis

Replay checks local 15-second files first. With network enabled and no usable local fine history, it tries provider 15-second history, then local/provider 30-second, then local/provider 60-second history. Actual imported 120-second history is the last local fallback. The Robinhood adapter has no 120-second request. Downloaded Replay data is validated and saved before use.

Usable sparse local history remains sparse, with a visible gap warning; Replay does not silently substitute coarse candles or refetch revised prices. Use the collection workflow to request missing history explicitly. One source interval is used per run. The existing completed-bar aggregation and strategy-interval checks still apply; coarser data cannot be turned into finer candles.

Choose **Replay from local files only** to prevent provider requests. **Use offline** on the welcome screen opens the app without login. In the library tab, select a dataset and **Pin selected for Replay**, or enter multiple hashes separated by commas. Pins restrict the next Replay to those exact files; missing, invalid or incompatible pins fail instead of substituting data. These selection controls apply to the next Replay in this app instance. Session journal/MCP results retain the exact chosen hashes, source, coverage and provenance.

The companion MCP server adds two read-only tools, usable without an active session or authentication:

- `library_datasets(offset, limit, catalogHash)`: validated dataset metadata and bounded diagnostics. Save the returned catalog hash and pass it on subsequent pages; a changed catalog requires restarting pagination.
- `library_candles(datasetHash, offset, limit)`: exact immutable candles, coverage and provenance, up to 100 rows per page. Prices are decimal strings and unavailable volume stays null.

These tools read only the configured library and accept no arbitrary filesystem path. Both are also safe to inspect while LIVE is selected. Collection never submits orders or changes the app's selected trading strategy.

## Verification

Automated tests cover portable files, exact decimals and unavailable volume, tamper detection, corrected revisions, concurrent/atomic writes, DST and calendars, durable queues and retries, editable/imported lists, offline/pinned Replay, and MCP pagination. An isolated WPF probe checks all three tabs at normal and minimum window sizes, saved clock state, modeless behavior and binding errors. Authenticated read-only provider checks verify real personal lists and 15-second candles. Real-time Paper trading during an open market remains a separate release check.
