"""Compare named script verification exports with their original candidate runs."""
import argparse
import copy
from datetime import datetime, timezone
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import re


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"), parse_float=Decimal)


def strip_order_ids(record):
    record = copy.deepcopy(record)
    if record.get("order"):
        record["order"].pop("id")
    if record.get("fill"):
        record["fill"].pop("orderId")
    return record


def pinned_settings(value):
    value = copy.deepcopy(value)
    value.pop("StrategyId")
    for key in ("Id", "Name", "FileName"):
        value["Strategy"].pop(key)
    return value


def indicators(value):
    value = copy.deepcopy(value)
    value.pop("sessionId")
    for key in ("Id", "Name", "FileName"):
        value["strategy"].pop(key)
    return value


def compare(left, right):
    failures = []

    def check(condition, label):
        if not condition:
            failures.append(label)

    for side, run in (("original", left), ("verification", right)):
        source = run["results"]["settings"]["Strategy"]["Source"]
        check(hashlib.sha256(source.encode()).hexdigest() == run["results"]["settings"]["Strategy"]["SourceSha256"], f"{side}: source identity")
        check(run["status"]["sessionId"] == run["results"]["sessionId"] == run["indicators"]["sessionId"], f"{side}: session identity")
        check(run["results"]["outcome"] == "COMPLETED" and run["status"]["operationState"] == "completed", f"{side}: completed Replay")
        for event in run["events"]["records"]:
            if event["fill"]:
                check(event["order"]["id"] == event["fill"]["orderId"], f"{side}: order/fill identity at {event['sequence']}")
    check(left["results"]["settings"]["Strategy"]["Source"] == right["results"]["settings"]["Strategy"]["Source"], "Pinned source bytes differ")
    check(left["status"]["sessionId"] != right["status"]["sessionId"], "Verification reused the original session ID")
    check(left["status"]["operationId"] != right["status"]["operationId"], "Verification reused the original operation ID")
    check(left["job"]["strategyId"] != right["job"]["strategyId"], "Verification did not use a distinct named strategy ID")
    check(pinned_settings(left["results"]["settings"]) == pinned_settings(right["results"]["settings"]), "Pinned settings/runtime/source differ after named-identity removal")
    check(indicators(left["indicators"]) == indicators(right["indicators"]), "Indicator snapshots differ")
    for kind in ("source", "strategy", "events"):
        a, b = left[kind]["records"], right[kind]["records"]
        check(len(a) == len(b), f"{kind}: record count")
        for index, (old, new) in enumerate(zip(a, b), 1):
            if kind == "source":
                old = {key: value for key, value in old.items() if key != "observedAtUtc"}
                new = {key: value for key, value in new.items() if key != "observedAtUtc"}
            elif kind == "events":
                old, new = strip_order_ids(old), strip_order_ids(new)
            check(old == new, f"{kind}: semantic difference at sequence {index}")
        pages_a = [{key: value for key, value in page.items() if key != "sessionId"} for page in left[kind]["pages"]]
        pages_b = [{key: value for key, value in page.items() if key != "sessionId"} for page in right[kind]["pages"]]
        check(pages_a == pages_b, f"{kind}: page metadata differs")
    for key in ("account", "summary", "decisions", "recordLimit"):
        check(left["results"][key] == right["results"][key], f"Results {key} differs")
    a = [{key: value for key, value in fill.items() if key != "orderId"} for fill in left["results"]["fills"]]
    b = [{key: value for key, value in fill.items() if key != "orderId"} for fill in right["results"]["fills"]]
    check(a == b, "Results fills differ")
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--verification", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows = []
    for path in sorted(args.verification.glob("*.json")):
        if not re.match(r"^[A-Z]+-\d{4}-\d{2}-\d{2}-(C[123]|B1|R1)\.json$", path.name):
            continue
        original = args.reference / path.name
        row = dict(file=str(path.resolve()), reference=str(original.resolve()))
        try:
            left, right = read(original), read(path)
            row.update(symbol=right["job"]["symbol"], date=right["job"]["date"], profile=right["job"]["profile"],
                       originalSessionId=left["status"]["sessionId"], verificationSessionId=right["status"]["sessionId"],
                       sourceSha256=right["results"]["settings"]["Strategy"]["SourceSha256"],
                       sourceRecords=len(right["source"]["records"]), strategyRecords=len(right["strategy"]["records"]), eventRecords=len(right["events"]["records"]))
            failures = compare(left, right)
        except Exception as exception:
            failures = [f"Comparison could not complete: {exception}"]
        row.update(matched=not failures, failureCount=len(failures), failures=failures)
        rows.append(row)
    invalid = sum(not row["matched"] for row in rows)
    report = dict(generatedAtUtc=datetime.now(timezone.utc).isoformat(), comparedRuns=len(rows), mismatchedRuns=invalid,
                  comparison="Exact Decimal semantic equality of all source/strategy/events, cached indicators, settings/runtime/source, results decisions/fills/account. Only source observedAtUtc, generated order IDs (internal correlation checked), session IDs, and renamed strategy identity fields are ignored.",
                  runs=rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Compared {len(rows)} named verification runs; mismatches: {invalid}")
    return 1 if invalid or not rows else 0


if __name__ == "__main__":
    raise SystemExit(main())
