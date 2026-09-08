"""Offline SOXL/SOXX adapter. Exact ledger checks are shared; each fund qualifies independently."""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import re

TOOLS = Path(__file__).resolve().parents[1]
ROOT = TOOLS.parents[1] / "archive/research/semiconductors-2026-09-06"
USO_PATH = TOOLS / "uso/audit.py"
SPEC = importlib.util.spec_from_file_location("uso_audit_helpers", USO_PATH)
USO = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(USO)
BASE = USO.BASE
FUNDS = ("SOXL", "SOXX")
PATTERN = re.compile(r"^(unavailable-|failed-)?(SOXL|SOXX)-(2026-\d{2}-\d{2})-(C1|C2|C3|B1|R1)(?:-[^.]+)?\.json$")


def read_rows(directory, candidates, include_friday=False):
    rows = []
    dates = BASE.DEVELOPMENT + ((BASE.FRIDAY,) if include_friday else ())
    for path in sorted(directory.glob("*.json")):
        match = PATTERN.fullmatch(path.name)
        if not match or match[3] not in dates:
            continue
        prefix, symbol, date, profile = match.groups()
        row = dict(file=str(path), symbol=symbol, date=date, profile=profile,
                   unavailable=prefix == "unavailable-", failures=[])
        try:
            run = BASE.read_json(path)
            if prefix == "failed-":
                row["failures"].append("Runner failure: " + str(run.get("error")))
            else:
                row = BASE.audit_run(run, candidates[profile], path)
                if (row["symbol"], row["date"], row["profile"]) != (symbol, date, profile):
                    row["failures"].append("Filename/job identity mismatch")
                    row.update(symbol=symbol, date=date, profile=profile)
                if row["unavailable"] != (prefix == "unavailable-"):
                    row["failures"].append("Filename/availability mismatch")
                if not row["failures"] and not row["unavailable"]:
                    row.update(USO.warmup_diagnostics(run))
            row["sourceSha256"] = run["job"]["sourceSha256"]
        except (ValueError, TypeError, KeyError) as error:
            row["failures"].append(f"Raw JSON/schema failure: {error}")
        row["fileSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        rows.append(row)
    check_cross_run_identity(rows)
    return rows


def check_cross_run_identity(rows):
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


def select(rows):
    reviews, _ = BASE.rank_runs(rows, FUNDS)
    # A failure or shortfall for one fund must not suppress the other fund's winner.
    return reviews, [review["winner"] for review in reviews if review["winner"] is not None]


def development_evidence(rows):
    keys = ("symbol", "date", "profile", "fileSha256", "sourceSha256", "dataSha256", "unavailable", "error",
            "primaryScore", "grossPnl", "entries", "maximumDrawdown", "dataEligible", "failures")
    return [{key: row.get(key) for key in keys} for row in sorted(rows, key=lambda row: (row["symbol"], row["date"], row["profile"], row["fileSha256"]))
            if row["date"] in BASE.DEVELOPMENT]


def verify_frozen(frozen, report):
    USO.verify_frozen(frozen, report["selections"])
    for key in ("developmentEvidence", "protocolSha256"):
        if frozen[key] != BASE.json_ready(report[key]):
            raise ValueError(f"Frozen {key} differs; development files, scores or configuration changed")
    if frozen["candidateManifestSha256"] != report["candidateManifestSha256"]:
        # Retain the original hash while allowing only verified path relocation.
        original = ROOT / "candidates/manifest.frozen.json"
        current = ROOT / "candidates/manifest.json"
        if (hashlib.sha256(original.read_bytes()).hexdigest() != frozen["candidateManifestSha256"]
                or hashlib.sha256(current.read_bytes()).hexdigest() != report["candidateManifestSha256"]):
            raise ValueError("Frozen or current candidate manifest hash differs")
        def without_locations(path):
            manifest = BASE.read_json(path)
            manifest["ValidationProject"] = Path(manifest["ValidationProject"]).name
            for candidate in manifest["Candidates"]:
                candidate["File"] = Path(candidate["File"]).name
            return manifest
        if without_locations(original) != without_locations(current):
            raise ValueError("Frozen candidate manifest differs beyond file locations")
    development = ROOT / "development-audit.json"
    if hashlib.sha256(development.read_bytes()).hexdigest() != frozen["developmentAuditSha256"]:
        raise ValueError("Frozen development audit file changed")


def markdown(report):
    lines = ["# SOXL/SOXX Replay decimal audit", "", f"Generated {report['generatedAtUtc']}.", "",
             f"{report['auditedRuns']} completed runs; {report['unavailableAttempts']} unavailable attempts; {report['invalidRuns']} invalid exports.", "",
             "Each fund qualifies independently. Friday is never ranked. A ledger-audit pass checks export integrity and account arithmetic; "
             "it does not establish adequate source coverage, completed warmup or strategy performance validation.", "",
             "Primary score deducts 5 bps per traded notional plus a closing-cost allowance on ending exposure. "
             "This is static-ledger sensitivity; open positions are marked at close, not liquidated.", ""]
    for stock in report["stocks"]:
        winner = stock["winner"]
        lines.extend([f"## {stock['symbol']}", "", "Selected: " + (winner["profile"] if winner else "none") + ".", ""])
        lines.extend("- " + problem for problem in stock["problems"])
        lines.extend(["", "| Candidate | Eligible days | Gross | 5 bps | Worst day | Maximum daily drawdown | Entries |",
                      "| --- | --- | --- | --- | --- | --- | --- |"])
        for row in stock["ranking"]:
            lines.append(f"| {row['profile']} | {row['days']} | {row['grossPnl']:.6f} | {row['totalPrimaryScore']:.6f} | "
                         f"{row['worstDayPrimaryScore']:.6f} | {row['maximumDailyDrawdown']:.6f} | {row['entries']} |")
    lines.extend(["", "## Daily evidence", "", "| Fund | Date | Candidate | Coverage | Gross | 5 bps | Entries | Ending exposure | Ledger audit |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"])
    for row in report["runs"]:
        if "grossPnl" in row:
            lines.append(f"| {row['symbol']} | {row['date']} | {row['profile']} | {row['sourceCount']}/1560 | {row['grossPnl']:.6f} | "
                         f"{row['primaryScore']:.6f} | {row['entries']} | {row['endingExposure']:.6f} | {'fail' if row['failures'] else 'pass'} |")
    lines.extend(["", "## Unavailable history", ""])
    lines.extend(f"- {row['symbol']} / {row['date']} / {row['profile']}: {row.get('error', 'see audit failures')}" for row in report["unavailable"])
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
    rows = read_rows(args.directory, BASE.load_manifest(ROOT / "candidates/manifest.json"), args.include_friday)
    stocks, selected = select(rows)
    evidence = development_evidence(rows)
    selected_keys = {(row["symbol"], row["profile"]) for row in selected}
    equity = []
    for row in rows:
        path = row.pop("equityPath", None)
        if path is not None and (row["symbol"], row["profile"]) in selected_keys:
            equity.append(dict(symbol=row["symbol"], date=row["date"], profile=row["profile"], observations=path,
                               sourceSha256=row["sourceSha256"], dataSha256=row["dataSha256"]))
    report = dict(generatedAtUtc=BASE.stamp(datetime.now(timezone.utc)),
                  protocolSha256=hashlib.sha256((ROOT / "protocol.md").read_bytes()).hexdigest(),
                  candidateManifestSha256=hashlib.sha256((ROOT / "candidates/manifest.json").read_bytes()).hexdigest(),
                  auditorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  baseAuditorSha256=hashlib.sha256(USO.BASE_PATH.read_bytes()).hexdigest(),
                  warmupHelperSha256=hashlib.sha256(USO_PATH.read_bytes()).hexdigest(),
                  selectionUsesFriday=False, selectionFrozen=bool(args.frozen_selection),
                  decimalArithmetic="60 significant digits; exact ledger equality; decimal outputs are strings",
                  sourceSignature="SHA256 of canonical UTC source start/end and exact OHLC,last,bid,ask,volume; excludes observedAtUtc",
                  auditedRuns=sum(not row["unavailable"] for row in rows), unavailableAttempts=sum(row["unavailable"] for row in rows),
                  invalidRuns=sum(bool(row["failures"]) for row in rows), developmentEvidence=evidence,
                  runs=[row for row in rows if not row["unavailable"]], unavailable=[row for row in rows if row["unavailable"]],
                  stocks=stocks, selections=selected, selectionShortfall=len(FUNDS)-len(selected))
    if args.frozen_selection:
        verify_frozen(BASE.read_json(args.frozen_selection), report)
    args.output.mkdir(parents=True, exist_ok=True)
    for name, value in (("audit.json", report), ("selected-equity.json", dict(selectionUsesFriday=False, runs=equity))):
        (args.output / name).write_text(json.dumps(BASE.json_ready(value), indent=2) + "\n", encoding="utf-8", newline="\n")
    (args.output / "audit.md").write_text(markdown(report), encoding="utf-8", newline="\n")
    print(f"{report['auditedRuns']} completed, {report['unavailableAttempts']} unavailable, {report['invalidRuns']} invalid; "
          f"selected {[(row['symbol'],row['profile']) for row in selected]}; output {args.output}")
    return 1 if report["invalidRuns"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
