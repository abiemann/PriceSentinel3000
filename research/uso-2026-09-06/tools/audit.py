"""USO-only adapter for the established exact-decimal Replay ledger audit.

Reads exports without app access. Friday is audited only when explicitly requested
and is never ranked. Output decimals are JSON strings; unavailable is not zero.
"""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
BASE_PATH = ROOT.parent / "low-price-2026-09-06/tools/audit.py"
SPEC = importlib.util.spec_from_file_location("low_price_decimal_audit", BASE_PATH)
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
PATTERN = re.compile(r"^(?:unavailable-)?(USO)-(2026-\d{2}-\d{2})-(C1|C2|C3|B1|R1)\.json$")


def warmup_diagnostics(run):
    ends = {BASE.utc(bar["endsAtUtc"]) for bar in run["strategy"]["records"]}
    previous_end, consecutive, maximum = None, 0, 0
    for source in run["source"]["records"]:
        at, stop = BASE.utc(source["startsAtUtc"]), BASE.utc(source["endsAtUtc"])
        if previous_end is not None and at != previous_end:
            consecutive = 0
        if stop in ends:
            consecutive += 1
            maximum = max(maximum, consecutive)
        previous_end = stop
    evaluations = [event["scriptEvaluation"] for event in run["events"]["records"] if event["scriptEvaluation"] is not None]
    return dict(maximumContiguousStrategyBars=maximum,
                readyEvaluations=sum(not item["isWarmingUp"] for item in evaluations),
                warmupEvaluations=sum(item["isWarmingUp"] for item in evaluations))


def read_rows(directory, candidates, include_friday=False):
    rows = []
    dates = BASE.DEVELOPMENT + ((BASE.FRIDAY,) if include_friday else ())
    for path in sorted(directory.glob("*.json")):
        match = PATTERN.fullmatch(path.name)
        if path.name.startswith("failed-USO-"):
            # The runner appends a unique batch suffix to failed exports.
            failure = BASE.read_json(path)
            job = failure["job"]
            if job["symbol"] == "USO" and job["date"] in dates:
                rows.append(dict(file=str(path), symbol="USO", date=job["date"], profile=job["profile"],
                                 unavailable=False, failures=["Runner failure: " + str(failure.get("error"))]))
            continue
        if not match or match[2] not in dates:
            continue
        try:
            run = BASE.read_json(path)
            row = BASE.audit_run(run, candidates[match[3]], path)
            if (row["symbol"], row["date"], row["profile"]) != match.groups():
                row["failures"].append("Filename/job identity mismatch")
            if not row["failures"] and not row["unavailable"]:
                row.update(warmup_diagnostics(run))
            row["fileSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        except (ValueError, TypeError, KeyError) as error:
            row = dict(file=str(path), symbol=match[1], date=match[2], profile=match[3],
                       unavailable=path.name.startswith("unavailable-"), failures=[f"Raw JSON/schema failure: {error}"])
        rows.append(row)
    for field in ("sessionId", "operationId"):
        grouped = defaultdict(list)
        for row in rows:
            if row.get(field):
                grouped[row[field]].append(row)
        for copies in grouped.values():
            if len(copies) > 1:
                for row in copies:
                    row["failures"].append(f"Reused {field} across exports")
    sources = defaultdict(list)
    for row in rows:
        if not row["unavailable"] and row.get("dataSha256"):
            sources[row["symbol"], row["date"]].append(row)
    for same_day in sources.values():
        if len({row["dataSha256"] for row in same_day}) > 1:
            for row in same_day:
                row["failures"].append("Historical source differs across candidates")
    return rows


def select(rows):
    stocks, ranking = BASE.rank_runs(rows, ("USO",))
    pending = any(problem not in ("Fewer than two eligible development days",
                                 "No candidate has an entry on eligible development dates")
                  for stock in stocks for problem in stock["problems"])
    return stocks, ranking, ranking[:1] if not pending else [], pending


def verify_frozen(frozen, selected):
    # File paths may be relocated. All ranking values, dates and source hashes must agree.
    def without_paths(value):
        if isinstance(value, dict):
            return {key: without_paths(item) for key, item in value.items() if key != "file"}
        if isinstance(value, list):
            return [without_paths(item) for item in value]
        return value
    if frozen.get("selectionUsesFriday") is not False or without_paths(frozen["selections"]) != without_paths(BASE.json_ready(selected)):
        raise ValueError("Current development selection, scores or source hashes differ from the frozen selection")


def markdown(report):
    lines = ["# USO Replay decimal audit", "", f"Generated {report['generatedAtUtc']}.", "",
             f"{report['auditedRuns']} completed runs; {report['unavailableAttempts']} no-history attempts; "
             f"{report['invalidRuns']} invalid exports. Friday never enters selection.", "",
             "Primary score subtracts 5 bps per traded notional plus a closing-cost allowance on ending exposure. "
             "This is a static-ledger sensitivity; open positions are marked at close, not liquidated.", "",
             "A ledger-audit pass verifies exported data and account arithmetic. It does not establish "
             "adequate historical coverage, completed indicator warmup, or strategy performance validation.", "",
             "Selected: " + (report["selections"][0]["profile"] if report["selections"] else "none") + ".", ""]
    if report["selectionShortfall"]:
        lines.append("No validated candidate: selection shortfall is one.")
    for stock in report["stocks"]:
        lines.extend("- " + problem for problem in stock["problems"])
    lines.extend(["", "## Development ranking", "", "| Candidate | Days | Gross | 5 bps | Worst day | Maximum daily drawdown | Entries |",
                  "| --- | --- | --- | --- | --- | --- | --- |"])
    for row in report["stocks"][0]["ranking"]:
        lines.append(f"| {row['profile']} | {row['days']} | {row['grossPnl']:.6f} | {row['totalPrimaryScore']:.6f} | "
                     f"{row['worstDayPrimaryScore']:.6f} | {row['maximumDailyDrawdown']:.6f} | {row['entries']} |")
    lines.extend(["", "## Daily evidence", "", "| Date | Candidate | Coverage | Gross | 5 bps | Drawdown | Entries | Ending exposure | Ledger audit |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"])
    for row in report["runs"]:
        if "grossPnl" in row:
            lines.append(f"| {row['date']} | {row['profile']} | {row['sourceCount']}/1560 | {row['grossPnl']:.6f} | "
                         f"{row['primaryScore']:.6f} | {row['maximumDrawdown']:.6f} | {row['entries']} | "
                         f"{row['endingExposure']:.6f} | {'fail' if row['failures'] else 'pass'} |")
    lines.extend(["", "## Unavailable history", ""])
    lines.extend(f"- {row['date']} / {row['profile']}: {row.get('error', 'see audit failures')}" for row in report["unavailable"])
    failures = [row for row in report["runs"] + report["unavailable"] if row["failures"]]
    if failures:
        lines.extend(["", "## Audit failures", ""])
        lines.extend(f"- {row['file']}: " + "; ".join(row["failures"][:8]) for row in failures)
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-friday", action="store_true")
    parser.add_argument("--frozen-selection", type=Path)
    args = parser.parse_args()
    candidates = BASE.load_manifest(ROOT / "candidates/manifest.json")
    rows = read_rows(args.directory, candidates, args.include_friday)
    stocks, ranking, selected, pending = select(rows)
    if args.frozen_selection:
        verify_frozen(BASE.read_json(args.frozen_selection), selected)
    keys = {(row["symbol"], row["profile"]) for row in selected}
    equity = []
    for row in rows:
        path = row.pop("equityPath", None)
        if path is not None and (row["symbol"], row["profile"]) in keys:
            equity.append(dict(symbol=row["symbol"], profile=row["profile"], date=row["date"],
                               dataSha256=row["dataSha256"], sourceSha256=row["sourceSha256"], observations=path))
    report = dict(generatedAtUtc=BASE.stamp(datetime.now(timezone.utc)),
                  protocolSha256=hashlib.sha256((ROOT / "protocol.md").read_bytes()).hexdigest(),
                  candidateManifestSha256=hashlib.sha256((ROOT / "candidates/manifest.json").read_bytes()).hexdigest(),
                  auditorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  baseAuditorSha256=hashlib.sha256(BASE_PATH.read_bytes()).hexdigest(),
                  selectionUsesFriday=False, selectionFrozen=bool(args.frozen_selection),
                  decimalArithmetic="60 significant digits; exact ledger equality; decimal outputs are strings",
                  sourceSignature="SHA256 of canonical UTC source start/end and exact OHLC,last,bid,ask,volume; excludes observedAtUtc",
                  auditedRuns=sum(not row["unavailable"] for row in rows), unavailableAttempts=sum(row["unavailable"] for row in rows),
                  invalidRuns=sum(bool(row["failures"]) for row in rows),
                  runs=[row for row in rows if not row["unavailable"]], unavailable=[row for row in rows if row["unavailable"]],
                  stocks=stocks, stockRanking=ranking, selections=selected, selectionShortfall=1-len(selected), selectionPending=pending)
    args.output.mkdir(parents=True, exist_ok=True)
    for name, value in (("audit.json", report), ("selected-equity.json", dict(selectionUsesFriday=False, runs=equity))):
        (args.output / name).write_text(json.dumps(BASE.json_ready(value), indent=2) + "\n", encoding="utf-8", newline="\n")
    (args.output / "audit.md").write_text(markdown(report), encoding="utf-8", newline="\n")
    print(f"{report['auditedRuns']} completed, {report['unavailableAttempts']} unavailable, {report['invalidRuns']} invalid; "
          f"selected {selected[0]['profile'] if selected else 'none'}; output {args.output}")
    return 1 if report["invalidRuns"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
