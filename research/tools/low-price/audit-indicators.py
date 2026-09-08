"""Independently recompute the five frozen candidates from past retained bars.

Only the Python standard library is used. No application or market-data calls.
Prices are parsed as Decimal and converted to binary64 for indicator arithmetic,
matching the documented interpreter numeric model; accounting is audited elsewhere.
"""

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path


TOLERANCE = Decimal("1e-10")


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), parse_float=Decimal)


def canonical(source):
    return re.sub(r"\s+", "", re.sub(r"#.*$", "", source, flags=re.MULTILINE))


def at(value):
    return datetime.fromisoformat(value)


def ema(values, period):
    """Seed with the first retained price; recompute over this retained window."""
    alpha = 2.0 / (period + 1)
    result = [values[0]]
    for value in values[1:]:
        result.append(alpha * value + (1.0 - alpha) * result[-1])
    return result


def rsi(values, period):
    """Wilder smoothing seeded by the arithmetic mean of the first p deltas."""
    gains, losses = [], []
    for previous, current in zip(values, values[1:]):
        change = current - previous
        gains.append(max(change, 0.0))
        losses.append(max(-change, 0.0))
    result = [None] * period
    gain, loss = sum(gains[:period]) / period, sum(losses[:period]) / period
    alpha = 1.0 / period
    for index in range(period - 1, len(gains)):
        if index >= period:
            gain = alpha * gains[index] + (1.0 - alpha) * gain
            loss = alpha * losses[index] + (1.0 - alpha) * loss
        result.append(50.0 if gain == 0 and loss == 0 else 100.0 if loss == 0 else 100.0 - 100.0 / (1.0 + gain / loss))
    return result


def formulas(profile, inputs, bars):
    close = [float(bar["close"]) for bar in bars]
    high = [float(bar["high"]) for bar in bars]
    low = [float(bar["low"]) for bar in bars]
    if profile.startswith("C"):
        fast = ema(close, int(inputs["fastLength"]))
        slow = ema(close, int(inputs["slowLength"]))
        strength = rsi(close, int(inputs["momentumLength"]))
        trend_up = fast[-1] > slow[-1] and slow[-1] >= slow[-2]
        recovered = close[-1] > fast[-1] and close[-2] <= fast[-2]
        improving = strength[-1] >= float(inputs["entryStrength"]) and strength[-1] > strength[-2]
        enter = trend_up and recovered and improving
        leave = close[-1] < slow[-1] or fast[-1] < slow[-1] or strength[-1] < float(inputs["exitStrength"])
        return dict(fastTrend=fast[-1], slowTrend=slow[-1], strength=strength[-1],
                    trendUp=trend_up, recoveredFast=recovered, strengthImproving=improving,
                    enterLong=enter, exitLong=leave)
    if profile == "B1":
        entry_length, exit_length = int(inputs["entryLength"]), int(inputs["exitLength"])
        prior_high = max(high[-entry_length - 1:-1])
        previous_prior_high = max(high[-entry_length - 2:-2])
        prior_low = min(low[-exit_length - 1:-1])
        trend = ema(close, int(inputs["trendLength"]))[-1]
        crossed = close[-1] > prior_high and close[-2] <= previous_prior_high
        return dict(priorHigh=prior_high, priorLow=prior_low, trend=trend,
                    enterLong=crossed and close[-1] > trend,
                    exitLong=close[-1] < prior_low or close[-1] < trend)
    if profile == "R1":
        mean_length = int(inputs["meanLength"])
        mean = sum(close[-mean_length:]) / mean_length
        strength = rsi(close, int(inputs["momentumLength"]))
        crossed = strength[-1] > float(inputs["recoveryStrength"]) and strength[-2] <= float(inputs["recoveryStrength"])
        return dict(meanPrice=mean, strength=strength[-1], enterLong=crossed and close[-1] < mean,
                    exitLong=close[-1] >= mean or strength[-1] < float(inputs["failureStrength"]))
    raise ValueError(f"Unsupported frozen profile {profile}")


def audit(path, candidates):
    run = read(path)
    failures, failure_count = [], 0

    def check(condition, message):
        nonlocal failure_count
        if not condition:
            failure_count += 1
            if len(failures) < 100:
                failures.append(message)

    pinned = run["results"]["settings"]["Strategy"]
    source = pinned["Source"]
    matches = [candidate for candidate in candidates if canonical(source) == candidate["logic"]]
    if len(matches) != 1:
        raise ValueError("Pinned source does not match exactly one frozen candidate after comment/whitespace removal")
    candidate = matches[0]
    profile = candidate["Id"]
    declared_profile = run["job"]["profile"]
    check(declared_profile == profile or declared_profile not in {c["Id"] for c in candidates}, "Job profile contradicts pinned formula")
    inputs, required = candidate["DefaultInputs"], candidate["RequiredWarmupBars"]
    check(pinned["Inputs"] == inputs, "Pinned input defaults differ from the frozen manifest")
    source_hash = hashlib.sha256(source.encode("utf-8")).hexdigest()
    check(source_hash == pinned["SourceSha256"] == run["status"]["strategy"]["sourceSha256"], "Pinned source/hash mismatch")
    check(pinned["CandleIntervalSeconds"] == 60, "Unexpected strategy interval")
    check(pinned["RuntimeVersion"] == "thinkscript-subset-v1", "Unexpected strategy runtime")
    sources, bars, events = run["source"]["records"], run["strategy"]["records"], run["events"]["records"]
    check(len(sources) == len(events), "Source/event counts differ")
    check(run["status"]["sessionId"] == run["results"]["sessionId"] == run["indicators"]["sessionId"], "Session identity mismatch")
    capacity = min(4096, max(256, required * 2))
    retained, previous_end, position = [], None, Decimal(0)
    completed, version, cursor, last_evaluated_version, evaluation_sequence = 0, 0, 0, -1, 0
    ready_count, warmup_count, indicator_count, reset_count, preemptions, conflicts = 0, 0, 0, 0, 0, 0
    max_error = Decimal(0)
    actions = {"Buy": 0, "Sell": 0, "Hold": 0}
    first_ready, last_ready = None, None
    for index, event in enumerate(events):
        if index >= len(sources):
            break
        observation = sources[index]
        sequence = index + 1
        label = f"event {sequence}"
        check(event["sequence"] == event["observationSequence"] == observation["sequence"] == sequence, f"{label}: observation link")
        start, end = at(observation["startsAtUtc"]), at(observation["endsAtUtc"])
        check(at(event["evaluatedAtUtc"]) == end, f"{label}: execution availability")
        if previous_end is not None and start != previous_end:
            retained.clear()
            version += 1
            reset_count += 1
        previous_end = end
        while cursor < len(bars) and bars[cursor]["observationSequence"] <= sequence:
            bar = bars[cursor]
            check(bar["observationSequence"] == sequence, f"{label}: late/misordered strategy bar")
            check(at(bar["endsAtUtc"]) <= end and at(bar["availableAtUtc"]) <= end, f"{label}: future strategy bar")
            if retained:
                check(at(retained[-1]["endsAtUtc"]) == at(bar["startsAtUtc"]), f"{label}: retained history crosses a gap")
            retained.append(bar)
            retained = retained[-capacity:]
            cursor += 1
            completed += 1
            version += 1
            check(bar["sequence"] == completed and bar["barVersion"] == version, f"{label}: completed bar identity/version")
        evaluated = event["scriptEvaluation"]
        check(bool(evaluated) == event["scriptEvaluated"], f"{label}: evaluation telemetry flag")
        expected_evaluation = event["strategyEvaluated"] and version != last_evaluated_version
        check(event["scriptEvaluated"] == expected_evaluation, f"{label}: new evaluation/version consistency")
        if not event["strategyEvaluated"]:
            preemptions += 1
            check(event["strategyProposal"] is None and event["evaluationSequence"] is None and evaluated is None,
                  f"{label}: preempted strategy exposed a proposal/evaluation")
        if evaluated:
            evaluation_sequence += 1
            last_evaluated_version = version
            check(event["evaluationSequence"] == evaluation_sequence, f"{label}: evaluation sequence")
            check(evaluated["barVersion"] == version and evaluated["completedBarCount"] == completed,
                  f"{label}: evaluation bar identity")
            check(evaluated["retainedBars"] == len(retained) and evaluated["requiredWarmupBars"] == required,
                  f"{label}: retained/warmup count")
            check(at(evaluated["evaluatedAtUtc"]) == end, f"{label}: snapshot time")
            warming = len(retained) < required
            check(evaluated["isWarmingUp"] == warming, f"{label}: warmup state")
            latest = evaluated["latestBar"]
            if retained:
                check(latest is not None, f"{label}: latest bar absent")
                if latest:
                    check(all(latest[key] == retained[-1][key] for key in ("startsAtUtc", "endsAtUtc", "open", "high", "low", "close", "volume")),
                          f"{label}: latest bar differs from available retained history")
            else:
                check(latest is None, f"{label}: empty retained history exposed a bar")
            proposal = evaluated["proposal"]
            exported = proposal["indicators"]
            check(not proposal["indicatorsTruncated"] and proposal["indicatorCount"] == len(exported), f"{label}: indicator truncation/count")
            if warming:
                warmup_count += 1
                check(proposal["action"] == "Hold" and evaluated["state"] == "WARMING UP", f"{label}: action before warmup")
                check(all(item["value"] is None and item["state"] == "warming_up" for item in exported), f"{label}: warmup fabricated values")
            else:
                ready_count += 1
                first_ready = first_ready or event["evaluatedAtUtc"]
                last_ready = event["evaluatedAtUtc"]
                values = formulas(profile, inputs, retained)
                check(len(exported) == len(values) and {item["name"] for item in exported} == set(values), f"{label}: declaration set")
                for item in exported:
                    name = item["name"]
                    if name not in values:
                        continue
                    expected = values[name]
                    expected_decimal = Decimal(int(expected)) if isinstance(expected, bool) else Decimal(str(expected))
                    check(item["state"] == "available" and item["value"] is not None, f"{label}/{name}: indicator availability")
                    if item["value"] is not None:
                        difference = abs(Decimal(item["value"]) - expected_decimal)
                        max_error = max(max_error, difference)
                        check(difference <= TOLERANCE, f"{label}/{name}: expected {expected_decimal}, observed {item['value']}, difference {difference}")
                        indicator_count += 1
                enter, leave = values["enterLong"], values["exitLong"]
                if enter and leave:
                    conflicts += 1
                    action, state = "Hold", "CONFLICTING SIGNALS"
                else:
                    action = "Buy" if enter and position == 0 else "Sell" if leave and position > 0 else "Hold"
                    state = {"Buy": "BUY SIGNAL", "Sell": "SELL SIGNAL", "Hold": "HOLD"}[action]
                check(proposal["action"] == action and proposal["state"] == state and evaluated["state"] == state,
                      f"{label}: position-aware proposal expected {action}/{state}")
                check(event["strategyProposal"] is not None and event["strategyProposal"]["signal"] == action,
                      f"{label}: adapter proposal differs from formula action")
                actions[action] += 1
        elif event["strategyEvaluated"]:
            check(event["evaluationSequence"] == evaluation_sequence and event["strategyProposal"]["state"] == "WAITING FOR CANDLE",
                  f"{label}: cached evaluation reference/waiting state")
        position = Decimal(event["account"]["positionQuantity"])
    check(cursor == len(bars), "Unprocessed/future strategy bars remain at export end")
    current = run["indicators"]["currentWarmup"]
    check(current["retainedBars"] == len(retained) and current["barVersion"] == version and current["completedBarCount"] == completed,
          "Final retained history/version/count mismatch")
    check(current["requiredBars"] == required and current["ready"] == (len(retained) >= required), "Final readiness mismatch")
    check(current["historyStartsAtUtc"] == (retained[0]["startsAtUtc"] if retained else None), "Final retained history start mismatch")
    return dict(file=str(path.resolve()), symbol=run["job"]["symbol"], date=run["job"]["date"], profile=profile,
                sessionId=run["status"]["sessionId"], sourceSha256=source_hash, eventCount=len(events),
                readyEvaluations=ready_count, warmupEvaluations=warmup_count, indicatorComparisons=indicator_count,
                maximumIndicatorAbsoluteError=str(max_error), requiredWarmupBars=required, retainedCapacity=capacity,
                gapResets=reset_count, hostPreemptions=preemptions, conflictingSignals=conflicts, proposedActions=actions,
                firstReady=first_ready, lastReady=last_ready, failureCount=failure_count, failures=failures)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True, help="Directory containing completed exporter JSON files")
    parser.add_argument("--manifest", type=Path, default=Path(__file__).resolve().parents[3] / "archive/research/low-price-2026-09-06/candidates/manifest.json")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = read(args.manifest)
    candidates = manifest["Candidates"]
    for candidate in candidates:
        text = (args.manifest.parent / candidate["File"]).read_text(encoding="utf-8-sig")
        # Read bytes for source identity, as universal-newline conversion may change CRLF.
        raw = (args.manifest.parent / candidate["File"]).read_bytes()
        if hashlib.sha256(raw).hexdigest() != candidate["SourceSha256"]:
            raise ValueError(f"Frozen candidate file hash mismatch: {candidate['File']}")
        candidate["logic"] = canonical(text)
    rows = []
    for path in sorted(args.directory.glob("*.json")):
        if not re.match(r"^[A-Z][A-Z0-9.]*-\d{4}-\d{2}-\d{2}-[^/]+\.json$", path.name):
            continue
        try:
            rows.append(audit(path, candidates))
        except Exception as exception:
            rows.append(dict(file=str(path.resolve()), failureCount=1, failures=[f"Audit could not complete: {exception}"]))
    invalid = sum(row["failureCount"] > 0 for row in rows)
    result = dict(generatedAtUtc=datetime.now(timezone.utc).isoformat(), tolerance=str(TOLERANCE),
                  method="Independent Python binary64 formulas over only retained, completed bars available at each event; Decimal input parsing and absolute-error comparisons",
                  auditScope="Indicator formulas, availability, history resets, evaluation identities, and position-aware strategy proposals. Account arithmetic and host risk rules are audited separately.",
                  auditedRuns=len(rows), invalidRuns=invalid, readyEvaluations=sum(row.get("readyEvaluations", 0) for row in rows),
                  indicatorComparisons=sum(row.get("indicatorComparisons", 0) for row in rows), runs=rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Audited {len(rows)} runs, {result['readyEvaluations']} ready evaluations, {result['indicatorComparisons']} indicator values; invalid runs: {invalid}")
    return 1 if invalid or not rows else 0


if __name__ == "__main__":
    raise SystemExit(main())
