# Local market-data library

Open **Tools > Retain Hi-Res Data** in the app header. The clock lights green only after automatic downloads are enabled and saved. The tool window is modeless, so the main trading controls remain accessible.

## Choose equities

In **Equity lists**, create a named list and paste symbols separated by spaces, commas, or newlines. Connected lookups show company or fund names and reject unsupported symbols. Offline lists can be saved; identities are checked when downloading. Check individual equities and **Save list**. Unchecking a member or removing a list does not delete downloaded history.

**Load Robinhood lists** retrieves accessible personal lists. Choose one and **Preview import**, then select individual equities and save a local copy. The app validates the returned item count and rejects incomplete or paginated responses it cannot finish. It resolves equity identities separately and never edits the Robinhood list. **Refresh source** previews additions and removals, preserving local exclusions; save to apply the preview. Repeating an import lets you keep multiple local lists.

**Export lists** writes portable JSON containing names, symbols and inclusion choices. Private Robinhood list and instrument IDs stay in the local configuration. **Import list file** adds copies with new local IDs.

## Schedule and download

In **Schedule & downloads**, choose the daily time in `HH:mm`, its time zone, and the library folder. Collection always includes all available regular, premarket, after-hours and overnight trading; there is no market-session setting. Press **Save schedule & folder**. The default is 13:15 in the computer's local zone. The saved zone follows daylight saving time; later computer-zone changes do not move it. A skipped clock time runs at the first valid minute; a repeated clock time runs once at the earlier occurrence.

Automatic collection requires the app to remain open and connected. It uses one deduplicated union of saved, enabled lists and included equities. At the chosen time it performs the same saved-history check as **Download now**, including today's completed candles and earlier missing history. It requests all available trading hours, including overnight. Expected coverage follows the actual trading windows for the date and excludes weekend, holiday and early-close closures. The calendar includes recurring US exchange holidays and early closes; unscheduled closures are not predicted.

If authorization expires, use **Reconnect Robinhood** while trading and downloads are idle. This is an explicit login action; scheduled collection never opens a browser for authorization. Reconnecting is refused while a provider request is active.

Each scheduled run inspects the actual saved history separately for every included equity. It finds missing trading sessions and partial days, including older holes when newer data is already present. Imported files and existing regular or extended files participate in the same checks; compatible saved candles are reused when expanding coverage to all available hours. Re-enabling downloads resumes from saved history, independently of when the schedule was enabled. Collection starts with the recent seven-calendar-day window, then discovers earlier available history.

The most recent seven calendar days are automatically retried at each scheduled run. This is a retry budget, not a promise that Robinhood retains seven days of 15-second data. Availability varies; there is no assumed fixed four-day cutoff. Both scheduled collection and **Download now** also search earlier sessions automatically. Older unresolved gaps remain listed by equity and date range under **Schedule & downloads**. A long outage can leave irrecoverable gaps. The app cannot recreate unavailable prices or guarantee a continuous candle for intervals with no reported trades.

Pending work, progress and older gap ranges survive restarts. Disconnected jobs wait for a connection; background requests cannot open a login prompt. Turn off and save automatic downloads to pause automatic queued work. This version does not download while the app is closed.

**Download now** needs no dates. It uses the saved, included equities and library folder, checks their local files, and queues missing 15-second history across all available trading hours. Complete saved coverage is reused. Nearby gaps separated by at most five minutes of saved candles are grouped into requests of at most six hours, reducing broker calls and library scans. Requests stay within actual trading windows and the captured cutoff. It includes today's completed candles through a captured 15-second boundary. A complete download of that current-day snapshot is labeled **Saved so far**, with its cutoff time; a later download can extend it. Forming candles and closed-market periods are excluded.

For each equity, collection checks the recent retry window, then searches backward beyond it. Three consecutive empty broker checks on earlier trading dates end that equity's older-history search. This is a discovery stopping rule, not proof that every earlier session is unavailable: an isolated empty day does not stop the search, and errors never count as empty responses. Complete local files skip broker requests and do not establish current broker availability or reset the empty-response count. Expected empty discovery rows labeled **No data** are hidden from the downloads table but still count toward progress; partial coverage and errors remain visible. The queue can grow while older history is discovered, so its progress describes the work found so far.

Both scheduled collection and **Download now** also retry known incomplete or failed requests for the currently included equities in the saved folder. It checks older partial files copied into the library even when no download record exists. These known gaps are checked even if backward discovery stops before their dates. Empty boundary probes alone do not create an endless backlog of retries. Requests remain strictly 15-second, serial and rate limited; transient retries are bounded, and failures are isolated by ticker. Discovery progress and queued work survive restarts; pausing preserves them. There is no separate Retry missing button.

The download activity panel stays visible across all three tabs. It names the current stock and date while checking saved files, waiting for the broker, or saving candles. Ready batches continue immediately, with serial requests spaced at least 250 milliseconds apart; there is no fixed pause between batches. Retry deadlines and connection waits have their own explanations. The progress bar counts completed checks across the retained queue, including unavailable or partial results. Separate counts distinguish complete files from items needing attention. The current request range is shown in Eastern time alongside the percentage of that stock/date's requested trading time checked. This measures checking progress, not saved-data coverage or completion of an in-flight broker response. Current-step elapsed time and Last progress update as each section finishes, even before the entire date finishes.

Closing the retention window leaves downloads running in the background while PriceSentinel stays open. A compact progress bar inside **Retain Hi-Res Data** shows the current queue progress; its tooltip shows the latest status. Click it during collection to open **Schedule & downloads** with the current job states. Pausing retains the queue; closing the whole app stops collection and preserves pending work for the next launch.

**Pause downloads** cancels the current request and keeps queued work stopped until **Resume downloads** or an app restart. Saved files and queued jobs are preserved. **Cancel connection** interrupts an explicit login request. Closing this tools window leaves background collection running; closing PriceSentinel stops it. You can change tabs, scroll, inspect rows, and edit drafts while downloading. Save/import actions that conflict with active collection are disabled until idle. Updates change existing job rows in place, preserving selection and scrolling. The saved schedule is labeled separately, and **UNSAVED CHANGES** appears when edited schedule/folder settings have not yet been applied.

Retention downloads request **only genuine, completed 15-second candles** across all available trading hours. A complete saved session is reused without downloading again. Compatible candles from older regular or extended files are reused when filling the surrounding trading hours. Each request covers only a missing section, with a maximum of six hours (1,440 candles). Native responses are saved as immutable daily files, and their agreeing candles are combined with existing files when read. Long stretches of complete coverage are skipped. Grouped requests can include saved candles between nearby gaps; every returned overlap must agree with saved prices, volume and timing before a new file is written. Responses containing only already-saved candles advance the check without creating redundant files. Different provenance or changed overlapping candles are rejected rather than silently blended. Empty responses preserve any saved data and leave the missing range unresolved. There is no 30-second or 60-second fallback in this downloader. Replay retains its separate, explicitly labeled coarse-data fallback.

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

Copy a day file, ticker folder, month or year into another library, then **Rescan library**. No original database, credentials or sidecars are needed. Scans validate content and hashes instead of trusting filenames. Malformed, tampered and unfinished temporary files are diagnosed. Identical data fetched again is deduplicated; corrected data produces a `.rev-<hash>.json` file while preserving the old revision. The app does not automatically delete history or impose a lifetime archive size/file-count cutoff. Individual files remain size-limited and validated. Scanning time grows with the archive; disk space still limits how much history can be kept.

Robinhood supplies split-adjusted data without an explicit adjustment revision. Files disclose this as `robinhood-split-unversioned`; hashes pin the exact prices fetched. Different provider/instrument/adjustment identities cannot be silently combined. Conflicting same-day revisions require pins or the explicit **Use latest fetched revision** choice; equal fetch times use a stable hash tie-break.

The **Local library** table shows **Day coverage** for each saved file: the percentage of that file's trading-session coverage for the date filled by real candles. All-hours files include regular, premarket, after-hours and overnight trading; older regular or extended files retain their original scope. Expected candle counts exclude closed-market periods and account for early closes and gaps. Hover over the percentage for saved and expected candle counts. Today's partial snapshot remains below 100% until the full session is covered, even if every candle requested so far was returned. This measures price-candle coverage, not volume completeness. Revisions are measured separately. The revision hash is hidden from the table; selecting a file and **Pin selected for Replay** still identifies its exact revision.

## Replay and analysis

In the dashboard's **Data timing** panel, enter a Replay date and press **Enter**, press **CHECK**, or choose a day with the calendar button. The check uses the current symbol, local start/end times, saved session bounds, library folder, offline choice and revision pins. It does not start Replay or write candles to disk. Changing these inputs invalidates the previous check.

The date field and calendar use these colors, with text labels and tooltips:

| Color | Availability for the entire selected time range |
| --- | --- |
| Dark green / 15D | Complete genuine 15-second data on disk |
| Light green / 15B | Complete genuine 15-second data verified with Robinhood |
| Orange / 30–60s | Coarser 30- or 60-second data; the status gives the exact interval |
| Red / 2m | Two-minute data on disk; this adapter cannot request two-minute candles from Robinhood |
| Gray / GAP, ?, or — | Partial coverage, not checked, or no returned history; see the explanation |

Opening or paging the calendar scans local files without querying the broker for every day. Selecting a date checks the broker as needed, using an existing connection; it never opens a login prompt. Broker day colors are cached for up to five minutes. Coarse local calendar colors identify what is saved, and their tooltips prompt a selected-date check for finer broker data. There is no assumed age cutoff: successful responses containing synthesized/interpolated placeholders do not count as genuine source coverage.

Broker verification reads the selected range into memory because the API has no separate availability endpoint. **START** reuses the checked candles, saves the selected broker result, or rereads the exact checked disk hashes, then constructs the Replay stream. It does not request the same checked broker data twice. Missing or modified pinned files fail rather than silently selecting another revision. A changed dashboard selection requires new data selection. An explicit check prefers complete coverage, trying the supported resolutions in order; partial history remains clearly labeled if no complete source is found. Script interval compatibility is still validated at START.

START and the availability check use the same disk-first selection. Replay reads saved 15-, 30-, 60- and 120-second data before making broker requests. It combines compatible saved pieces whose overlapping candles agree. If coverage remains incomplete, it requests the span containing the gaps at genuine 15-second, then 30-second, then 1-minute resolution. Saved candles have priority when the same native interval is returned again. The Robinhood adapter has no native 2-minute request; genuine imported 2-minute files can be used, but unavailable prices are never invented.

When filling a gap requires a coarser bar, Replay uses that whole bar and aggregates complete finer spans upward to a uniform Replay interval. It never splits a coarse bar or counts overlapping bars twice. Remaining gaps stay visible. The source-data label and session/MCP provenance identify combined history, its native resolutions and exact source hashes. Files retain their native candles; only the in-memory Replay stream is aggregated. A strategy must support the resulting Replay interval, and its tested interval is never silently changed. Prices, signals, risk checks and simulated fills become available only at the completed Replay candle's close.

Before START, choose **Replay from local files only** in the dashboard's Replay settings to prevent broker requests. It is off by default and locked during a session. Changing it invalidates the previous availability check. **Use offline** on the welcome screen opens the app without login. In the library tab, select a dataset and **Pin selected for Replay**, or enter multiple hashes separated by commas. Pins restrict the next Replay to those exact files; missing, invalid or incompatible pins fail instead of substituting data. Ordinary saved coverage is combined automatically when overlaps agree; different prices or volume still require explicit revision selection. These choices apply to the next Replay in this app instance.

The companion MCP server adds two read-only tools, usable without an active session or authentication:

- `library_datasets(offset, limit, catalogHash)`: validated dataset metadata and bounded diagnostics. Save the returned catalog hash and pass it on subsequent pages; a changed catalog requires restarting pagination.
- `library_candles(datasetHash, offset, limit)`: exact immutable candles, coverage and provenance, up to 100 rows per page. Prices are decimal strings and unavailable volume stays null.

These tools read only the configured library and accept no arbitrary filesystem path. Both are also safe to inspect while LIVE is selected. Collection never submits orders or changes the app's selected trading strategy.

## Verification

Automated tests cover portable files, exact decimals and unavailable volume, tamper detection, corrected revisions, concurrent/atomic writes, DST and calendars, durable queues and retries, editable/imported lists, offline/pinned Replay, and MCP pagination. An isolated WPF probe checks all three tabs at normal and minimum window sizes, saved clock state, modeless behavior and binding errors. Authenticated read-only provider checks verify real personal lists and 15-second candles. Real-time Paper trading during an open market remains a separate release check.
