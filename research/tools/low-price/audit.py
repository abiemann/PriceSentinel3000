#!/usr/bin/env python3
"""Read-only, exact-decimal audit of the frozen low-price Replay experiment.

Usage: python audit.py RAW_DIRECTORY [--output DIRECTORY] [--include-friday]
Raw files are never modified. Decimal outputs are JSON strings. No app access.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from decimal import Decimal, ROUND_FLOOR, getcontext
import hashlib
import json
from pathlib import Path
import re
import sys

getcontext().prec = 60
D = Decimal
ZERO = D(0)
STARTING = D(1400)
UNIVERSE = ("SOFI", "F", "RIVN", "SNAP", "NU", "CCL", "AAL", "PINS")
ORDER = ("C1", "C2", "C3", "B1", "R1")
DEVELOPMENT = ("2026-08-31", "2026-09-01", "2026-09-02", "2026-09-03")
FRIDAY = "2026-09-04"
EXPECTED = 1560
MINIMUM = 1529  # ceil(1560 * 0.98)
BPS = (D(0), D(1), D("2.5"), D(5), D(10), D(25))
PER_SHARE = (D("0.005"), D("0.01"))
FIXED = dict(startingBalance=1400, tradesSettleImmediately=True,
             positionSizeBasis="FixedAmount", positionSizeValue=500,
             quantityLimitMode="AsManyAsPossible", maximumQuantity=100,
             unlimitedEntries=True, maximumEntriesPerDay=1,
             maximumDailyLossBasis="FixedAmount", maximumDailyLossValue=50,
             stopLossBasis="PurchasePriceDeclinePercentage", stopLossValue=1,
             bufferMinutes=15, quotePollingSeconds=5, scriptBarIntervalSeconds=60,
             chartCandleIntervalSeconds=15, reconciliationSeconds=45,
             reconciliationLookbackSeconds=900, reconciliationCompletionDelaySeconds=30,
             replayTime="06:30", replayEndTime="13:00", replaySpeed=100)
ENUMS = dict(PositionSizeBasis=0, QuantityLimitMode=0, MaximumDailyLossBasis=0,
             StopLossBasis=1)
REPO = Path(__file__).resolve().parents[3]
ROOT = REPO / "archive/research/low-price-2026-09-06"


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def read_json(path):
    with Path(path).open(encoding="utf-8-sig") as handle:
        return json.load(handle, parse_float=D, object_pairs_hook=unique_object,
                         parse_constant=lambda value: (_ for _ in ()).throw(
                             ValueError(f"Non-finite JSON number: {value}")))


def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, D)):
        raise ValueError(f"Expected JSON number, got {type(value).__name__}")
    value = D(value)
    if not value.is_finite():
        raise ValueError("Non-finite decimal")
    return value


def decimal_text(value):
    return format(value, "f")


def json_ready(value):
    if isinstance(value, D):
        return decimal_text(value)
    if isinstance(value, dict):
        return {key: json_ready(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_ready(item) for item in value]
    return value


def canonical(value):
    # Numeric trailing zeros must not change an otherwise identical data signature.
    if isinstance(value, (D, int)) and not isinstance(value, bool):
        return format(D(value).normalize(), "f")
    if isinstance(value, dict):
        return {key: canonical(item) for key, item in sorted(value.items())}
    if isinstance(value, list):
        return [canonical(item) for item in value]
    return value


def digest(value):
    return hashlib.sha256(json.dumps(canonical(value), sort_keys=True,
                                    separators=(",", ":")).encode()).hexdigest()


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def utc(text):
    fraction = re.search(r"\.(\d+)", text)
    if fraction and len(fraction[1]) > 6 and any(digit != "0" for digit in fraction[1][6:]):
        raise ValueError(f"Timestamp exceeds exact microsecond precision: {text}")
    value = datetime.fromisoformat(text.replace("Z", "+00:00"))
    if value.tzinfo is None:
        raise ValueError(f"Timestamp without timezone: {text}")
    return value.astimezone(timezone.utc)


def stamp(value):
    return value.isoformat()


def load_manifest(path=ROOT / "candidates" / "manifest.json"):
    manifest = read_json(path)
    if manifest["CandidateOrder"] != list(ORDER):
        raise ValueError("Candidate manifest order differs from protocol")
    candidates = {item["Id"]: item for item in manifest["Candidates"]}
    if len(candidates) != 5 or set(candidates) != set(ORDER):
        raise ValueError("Candidate manifest is incomplete")
    for profile, candidate in candidates.items():
        required = 51 if profile == "R1" else 84 if profile == "B1" else 85
        if candidate["RequiredWarmupBars"] != required:
            raise ValueError(f"Unexpected warmup for {profile}")
        source = (Path(path).parent / candidate["File"]).read_bytes().decode("utf-8-sig")
        if sha(source) != candidate["SourceSha256"]:
            raise ValueError(f"Candidate source hash mismatch: {profile}")
    return candidates


class Checks:
    def __init__(self):
        self.failures = []

    def check(self, condition, message):
        if not condition:
            self.failures.append(message)

    def amount(self, expected, actual, label):
        actual = number(actual)
        self.check(expected == actual,
                   f"{label}: expected {expected}, recorded {actual}, delta {actual - expected}")


def check_settings(run, job, checks, completed):
    expected = dict(FIXED, symbol=job["symbol"], replayDate=job["date"],
                    strategyId=job["strategyId"])
    for container in ("requestSettings",):
        if container in run:
            for key, value in expected.items():
                checks.check(run[container][key] == value, f"{container}.{key} mismatch")
    for key, value in expected.items():
        checks.check(run["status"]["settings"][key] == value, f"status.settings.{key} mismatch")
        if completed:
            pinned_key = key[0].upper() + key[1:]
            checks.check(run["results"]["settings"][pinned_key] == ENUMS.get(pinned_key, value),
                         f"results.settings.{pinned_key} mismatch")


def check_stream(stream, kind, session, through, checks):
    records, pages = stream["records"], stream["pages"]
    checks.check(isinstance(records, list) and isinstance(pages, list) and bool(pages),
                 f"{kind}: malformed or missing stream/pages")
    prior = 0
    for index, page in enumerate(pages):
        prefix = f"{kind}.pages[{index}]"
        checks.check(page["sessionId"] == session and page["mode"] == "Replay", prefix + " identity")
        checks.check(page["truncated"] is False and page["firstAvailableSequence"] == 1,
                     prefix + " truncated or unavailable records")
        next_sequence = page["nextSequence"]
        checks.check(prior <= next_sequence <= len(records), prefix + " invalid pagination cursor")
        checks.check(next_sequence > prior or (not records and len(pages) == 1), prefix + " stalled cursor")
        checks.check(page["hasMore"] == (index < len(pages) - 1), prefix + " incomplete pagination")
        checks.check(utc(page["observedThroughUtc"]) == through, prefix + " observed-through mismatch")
        prior = next_sequence
    checks.check(prior == len(records), kind + " final page count mismatch")
    for index, record in enumerate(records, 1):
        checks.check(record["sequence"] == index and not record.get("omitted", False),
                     f"{kind}: sequence/omission at {index}")
    return records


def sensitivities(gross, turnover, traded_shares, ending_value, ending_quantity):
    notional = turnover + ending_value
    shares = traded_shares + ending_quantity
    return ({decimal_text(bps): gross - notional * bps / D(10000) for bps in BPS},
            {decimal_text(cost): gross - shares * cost for cost in PER_SHARE})


def audit_run(run, candidate, path="<memory>"):
    checks = Checks()
    job = run["job"]
    row = dict(file=str(path), symbol=job["symbol"], date=job["date"], profile=job["profile"],
               unavailable=False, failures=checks.failures)
    try:
        status = run["status"]
        completed = status["operationState"] == "completed"
        check_settings(run, job, checks, completed)
        checks.check(status["effectiveMode"] == "Replay" and status["selectedMode"] == "Replay", "Not Replay")
        checks.check(isinstance(status["processId"], int) and status["processId"] > 0 and bool(status["operationId"]),
                     "Missing process/operation identity")
        row.update(processId=status["processId"], operationId=status["operationId"])
        checks.check(job["sourceSha256"] == candidate["SourceSha256"], "Job source differs from frozen candidate")
        if not completed:
            row["unavailable"] = True
            checks.check(status["operationState"] == "failed" and status["sessionId"] is None
                         and status["totalObservations"] == 0 and status["processedObservations"] == 0,
                         "Unavailable attempt is not a failed empty session")
            error = status["operationError"]
            checks.check(isinstance(error, str) and error.startswith(f"Robinhood returned no {job['symbol']} trades"),
                         "Failure is not explicit provider no-history")
            row["error"] = error
            return row
        results, indicators = run["results"], run["indicators"]
        session = status["sessionId"]
        row["sessionId"] = session
        checks.check(bool(session) and results["sessionId"] == session and indicators["sessionId"] == session,
                     "Session identity mismatch")
        checks.check(results["outcome"] == "COMPLETED" and results["mode"] == indicators["mode"] == "Replay",
                     "Uncompleted or wrong-mode results")
        checks.check(status["operationError"] is None and not status["running"] and not status["starting"]
                     and not status["paused"] and status["fast"], "Completed status has error or active operation")
        pinned = results["settings"]["Strategy"]
        source_hash = sha(pinned["Source"])
        row["sourceSha256"] = source_hash
        for value in (pinned["SourceSha256"], status["strategy"]["sourceSha256"],
                      indicators["strategy"]["SourceSha256"], candidate["SourceSha256"]):
            checks.check(source_hash == value, "Pinned script hash mismatch")
        for value in (pinned["Id"], status["strategy"]["id"], indicators["strategy"]["Id"]):
            checks.check(value == job["strategyId"], "Pinned strategy ID mismatch")
        checks.check(pinned["Inputs"] == candidate["DefaultInputs"] == indicators["strategy"]["Inputs"],
                     "Pinned inputs differ from manifest")
        for spec in (pinned, indicators["strategy"]):
            checks.check(spec["RuntimeVersion"] == "thinkscript-subset-v1" and spec["DataModel"] == "completed-price-bars-v1"
                         and spec["CandleIntervalSeconds"] == 60, "Runtime/data-model mismatch")
        checks.check(pinned["HostVersion"] == indicators["strategy"]["HostVersion"]
                     and pinned["HostVersion"].split("+")[0] == status["appVersion"], "Host version identity mismatch")
        checks.check(status["strategy"]["isBuiltIn"] is False, "Expected external strategy")
        src = run["source"]["records"]
        if not src:
            raise ValueError("Completed Replay has no source records")
        through = utc(src[-1]["endsAtUtc"])
        src = check_stream(run["source"], "source", session, through, checks)
        bars = check_stream(run["strategy"], "strategy", session, through, checks)
        events = check_stream(run["events"], "events", session, through, checks)
        count = len(src)
        checks.check(count == len(events) == status["totalObservations"] == status["processedObservations"]
                     == results["summary"]["quoteCount"] == indicators["observationSequence"], "Source/event/count mismatch")
        checks.check(len(bars) == status["completedStrategyBars"], "Strategy count mismatch")
        checks.check(utc(indicators["observedThroughUtc"]) == through, "Indicator observed-through mismatch")
        start = utc(job["date"] + "T06:30:00-07:00")
        end = utc(job["date"] + "T13:00:00-07:00")
        gaps, source_map, by_end = [], {}, {}
        prior_end = start
        signature = []
        for obs in src:
            at, stop = utc(obs["startsAtUtc"]), utc(obs["endsAtUtc"])
            label = f"source[{obs['sequence']}]"
            checks.check(start <= at < stop <= end and stop - at == timedelta(seconds=15)
                         and at >= prior_end and at.second % 15 == 0 and at.microsecond == 0, label + " timing/order")
            checks.check(at == utc(obs["sourceTimestampUtc"]) and stop == utc(obs["availableAtUtc"])
                         == utc(obs["evaluationTimestampUtc"]), label + " availability/lookahead")
            checks.check(obs["intervalSeconds"] == 15 and obs["finalized"] is True and obs["fresh"] is True
                         and obs["kind"] == "historical_candle", label + " source model")
            prices = {key: number(obs[key]) for key in ("open", "high", "low", "close", "last", "bid", "ask", "volume")}
            checks.check(prices["low"] > 0 and prices["high"] >= max(prices["open"], prices["close"])
                         and prices["low"] <= min(prices["open"], prices["close"]) and prices["volume"] >= 0,
                         label + " OHLCV validity")
            checks.check(prices["last"] == prices["close"] and prices["bid"] == prices["ask"] == 0, label + " quote model")
            if at > prior_end:
                gaps.append(dict(fromUtc=stamp(prior_end), throughUtc=stamp(at),
                                 missingBars=int((at-prior_end).total_seconds()) // 15))
            prior_end = stop
            source_map[at] = obs
            signature.append(dict(startsAtUtc=stamp(at), endsAtUtc=stamp(stop), **prices))
        if prior_end < end:
            gaps.append(dict(fromUtc=stamp(prior_end), throughUtc=stamp(end),
                             missingBars=int((end-prior_end).total_seconds()) // 15))
        # Independently derive every complete minute. Missing source pieces never get synthesized.
        expected_starts = [start + timedelta(minutes=i) for i in range(390)
                           if all(start + timedelta(minutes=i, seconds=j*15) in source_map for j in range(4))]
        checks.check([utc(bar["startsAtUtc"]) for bar in bars] == expected_starts, "Strategy aggregation coverage mismatch")
        for bar in bars:
            at, stop = utc(bar["startsAtUtc"]), utc(bar["endsAtUtc"])
            label = f"strategy[{bar['sequence']}]"
            pieces = [source_map[at + timedelta(seconds=15*j)] for j in range(4)]
            expected_bar = dict(open=pieces[0]["open"], close=pieces[-1]["close"],
                                high=max(p["high"] for p in pieces), low=min(p["low"] for p in pieces),
                                volume=sum((number(p["volume"]) for p in pieces), ZERO))
            for key, value in expected_bar.items():
                checks.amount(number(value), bar[key], label + "." + key)
            checks.check(stop-at == timedelta(minutes=1) and stop == utc(bar["availableAtUtc"])
                         and bar["finalized"] is True and bar["observationSequence"] == pieces[-1]["sequence"], label + " timing/identity")
            by_end[stop] = bar
        required = candidate["RequiredWarmupBars"]
        capacity = min(4096, max(256, required * 2))
        cash, quantity, average, realized = STARTING, ZERO, ZERO, ZERO
        peak, drawdown, turnover, traded_shares = STARTING, ZERO, ZERO, ZERO
        entries = fills = completed_bars = version = evaluation_count = 0
        previous_end, first_ready, last_evaluation, retained = start, None, None, []
        evaluated_version = -1
        equity_path, fill_records, risk_overrides = [], [], Counter()
        exposed_observations = 0
        for index, event in enumerate(events):
            obs = src[index]
            at, stop = utc(obs["startsAtUtc"]), utc(obs["endsAtUtc"])
            label = f"event[{event['sequence']}]"
            checks.check(event["sequence"] == event["observationSequence"] == obs["sequence"]
                         and utc(event["evaluatedAtUtc"]) == stop, label + " source/time identity")
            if at != previous_end and index > 0:
                retained.clear()
                version += 1
            previous_end = stop
            if stop in by_end:
                retained.append(by_end[stop])
                retained = retained[-capacity:]
                completed_bars += 1
                version += 1
                checks.check(by_end[stop]["barVersion"] == version, label + " completed bar version")
            evaluation = event["scriptEvaluation"]
            checks.check(event["scriptEvaluated"] == (evaluation is not None), label + " evaluation presence")
            checks.check(event["scriptEvaluated"] == (event["strategyEvaluated"] and version != evaluated_version),
                         label + " missing or repeated version evaluation")
            if evaluation is not None:
                evaluated_version = version
                evaluation_count += 1
                checks.check(evaluation["requiredWarmupBars"] == required and evaluation["retainedBars"] == len(retained)
                             and evaluation["completedBarCount"] == completed_bars and evaluation["barVersion"] == version
                             and evaluation["isWarmingUp"] == (len(retained) < required), label + " warmup/version")
                checks.check(utc(evaluation["evaluatedAtUtc"]) == stop, label + " script evaluation timestamp")
                proposal = evaluation["proposal"]
                checks.check(proposal["action"] in ("Buy", "Sell", "Hold") and evaluation["state"] != "SCRIPT ERROR",
                             label + " unexpected script fault/action")
                checks.check(proposal["indicatorsTruncated"] is False and proposal["indicatorCount"] == len(proposal["indicators"])
                             and len(proposal["indicators"]) <= proposal["indicatorLimit"] <= 64, label + " indicator truncation/count")
                if len(retained) < required:
                    checks.check(proposal["action"] == "Hold", label + " action before warmup")
                elif first_ready is None:
                    first_ready = event["evaluatedAtUtc"]
                latest = evaluation["latestBar"]
                checks.check((latest is None) == (not retained), label + " latest-bar presence")
                if retained and latest:
                    for key in ("startsAtUtc", "endsAtUtc", "open", "high", "low", "close", "volume"):
                        checks.check(latest[key] == retained[-1][key], label + " latest-bar " + key)
                last_evaluation = evaluation
            checks.check(event["strategyEvaluated"] == (event["strategyProposal"] is not None), label + " strategy proposal presence")
            checks.check(event["evaluationSequence"] == (evaluation_count if event["strategyEvaluated"] else None),
                         label + " evaluation sequence")
            if event["riskOverride"]:
                override = event["riskOverride"]
                if not isinstance(override, (str, dict)):
                    raise ValueError("Unexpected risk-override schema")
                description = override if isinstance(override, str) else override.get("state", override.get("reason", "unspecified"))
                risk_overrides[str(description)] += 1
            fill, order = event["fill"], event["order"]
            checks.check((fill is None) == (order is None), label + " immediate fill/order presence")
            if fill:
                fills += 1
                price, amount = number(fill["price"]), number(fill["quantity"])
                checks.check(price == obs["close"] and amount > 0 and fill["orderId"] == order["id"]
                             and stop == utc(fill["filledAtUtc"]) == utc(order["submittedAtUtc"]), label + " fill identity/model")
                signal = event["decision"]["signal"]
                expected_side = "Sell" if signal in ("Sell", "StopLoss", "DailyLoss") else signal
                checks.check(fill["side"] == order["side"] == expected_side
                             and amount == order["quantity"] and price == order["expectedPrice"], label + " order/decision agreement")
                if signal == "StopLoss":
                    checks.check(quantity > 0 and price <= average * D("0.99") and event["riskOverride"] == "STOP LOSS",
                                 label + " purchase-price stop threshold")
                turnover += price * amount
                traded_shares += amount
                if fill["side"] == "Buy":
                    expected_quantity = (min(D(500), cash) / price * D(1000000)).to_integral_value(rounding=ROUND_FLOOR) / D(1000000)
                    checks.check(quantity == 0 and amount == expected_quantity, label + " buy sizing")
                    checks.amount(ZERO, fill["realizedProfitLoss"], label + " buy realized")
                    cash -= amount * price
                    quantity, average = amount, price
                    entries += 1
                else:
                    checks.check(fill["side"] == "Sell" and quantity > 0 and amount == quantity, label + " sell quantity")
                    profit = (price - average) * amount
                    checks.amount(profit, fill["realizedProfitLoss"], label + " sell realized")
                    realized += profit
                    cash += amount * price
                    quantity = average = ZERO
                fill_records.append(fill)
            mark = number(obs["last"])
            market_value = quantity * mark
            equity = cash + market_value
            unrealized = quantity * (mark - average)
            account = dict(cash=cash, buyingPower=cash, equity=equity, positionQuantity=quantity,
                           averagePrice=average, marketValue=market_value, realizedProfitLoss=realized,
                           unrealizedProfitLoss=unrealized)
            for key, value in account.items():
                checks.amount(value, event["account"][key], label + ".account." + key)
            checks.check(event["account"]["entriesToday"] == entries, label + " entries")
            peak = max(peak, equity)
            drawdown = max(drawdown, peak - equity)
            exposed_observations += int(quantity > 0)
            equity_path.append(dict(sequence=event["sequence"], atUtc=event["evaluatedAtUtc"], equity=equity,
                                    grossPnl=equity-STARTING, quantity=quantity, marketValue=market_value,
                                    tradedNotional=turnover, tradedShares=traded_shares))
        if not events:
            raise ValueError("No events in completed run")
        for key, value in account.items():
            checks.amount(value, results["account"][key], "final.account." + key)
        checks.check(equity - STARTING == realized + unrealized, "Final realized/unrealized reconciliation")
        checks.check(results["account"]["entriesToday"] == entries, "Final entries")
        summary = results["summary"]
        checks.check(summary["fillCount"] == fills and summary["orderCount"] == fills and summary["decisionCount"] == count,
                     "Final fill/order/decision counts")
        limit = results["recordLimit"]
        checks.check(len(results["fills"]) == min(fills, limit), "Final fills retained count")
        checks.check(results["fills"] == fill_records[-limit:], "Final fills differ from event fills")
        checks.check(indicators["latestEvaluation"] == last_evaluation and indicators["evaluationSequence"] == evaluation_count,
                     "Final indicator evaluation mismatch")
        warmup = indicators["currentWarmup"]
        checks.check(warmup["requiredBars"] == required and warmup["retainedBars"] == len(retained)
                     and warmup["remainingBars"] == max(0, required-len(retained)) and warmup["ready"] == (len(retained) >= required)
                     and warmup["completedBarCount"] == completed_bars and warmup["barVersion"] == version,
                     "Final warmup mismatch")
        checks.check(warmup["historyStartsAtUtc"] == (retained[0]["startsAtUtc"] if retained else None), "Final retained history start")
        bps_costs, share_costs = sensitivities(equity-STARTING, turnover, traded_shares, market_value, quantity)
        endpoints = utc(src[0]["startsAtUtc"]) == start and through == end
        row.update(dataSha256=digest(signature), sourceCount=count, strategyCount=len(bars),
                   firstSource=src[0]["startsAtUtc"], lastClose=src[-1]["endsAtUtc"], exactEndpoints=endpoints,
                   coverage=D(count)/D(EXPECTED), dataEligible=count >= MINIMUM and endpoints,
                   firstReady=first_ready, requiredWarmupBars=required, retainedCapacity=capacity,
                   endingRetainedBars=len(retained), endingReady=len(retained) >= required,
                   gapCount=len(gaps), gaps=gaps, grossPnl=equity-STARTING, realized=realized,
                   unrealized=unrealized, endingQuantity=quantity, endingExposure=market_value,
                   endingCash=cash, endingEquity=equity, entries=entries, fills=fills,
                   maximumDrawdown=drawdown, tradedNotional=turnover, tradedShares=traded_shares,
                   closingAllowanceNotional=market_value, closingAllowanceShares=quantity,
                   exposedObservations=exposed_observations, exposureFraction=D(exposed_observations)/D(count),
                   riskOverrides=dict(risk_overrides), netByBps=bps_costs, netByPerShare=share_costs,
                   primaryScore=bps_costs["5"], equityPath=equity_path)
    except (KeyError, TypeError, ValueError, IndexError, ArithmeticError) as error:
        checks.failures.append(f"Schema/audit failure: {type(error).__name__}: {error}")
    return row


def rank_runs(rows, universe=UNIVERSE):
    """The protocol alone determines eligibility, candidate winners and stock ranks."""
    stock_reviews, winners = [], []
    for symbol in universe:
        own = [row for row in rows if row.get("symbol") == symbol and row.get("date") in DEVELOPMENT]
        problems, available, unavailable, eligible = [], [], [], []
        for day in DEVELOPMENT:
            day_rows = [row for row in own if row["date"] == day]
            present = [row for row in day_rows if not row["unavailable"]]
            missing = [row for row in day_rows if row["unavailable"]]
            if not day_rows:
                problems.append(f"{day}: history not attempted")
                continue
            if any(row["failures"] for row in day_rows):
                problems.append(f"{day}: audit failure")
            if present and missing:
                problems.append(f"{day}: inconsistent available/no-history evidence")
            if not present:
                unavailable.append(day)
                continue
            available.append(day)
            if Counter(row["profile"] for row in present) != Counter(ORDER):
                problems.append(f"{day}: candidates missing, duplicated, or unknown")
                continue
            if len({row.get("dataSha256") for row in present}) != 1:
                problems.append(f"{day}: historical source differs across candidates")
            if all(row.get("dataEligible", False) for row in present):
                eligible.append(day)
        if len(eligible) < 2:
            problems.append("Fewer than two eligible development days")
        rankings = []
        for profile in ORDER:
            matching = [row for row in own if row["profile"] == profile and row["date"] in eligible
                        and not row["unavailable"] and not row["failures"]]
            if len(matching) != len(eligible) or not matching:
                continue
            scores = [row["primaryScore"] for row in matching]
            rankings.append(dict(profile=profile, days=len(matching), totalPrimaryScore=sum(scores, ZERO),
                                 meanDailyPrimaryScore=sum(scores, ZERO)/D(len(scores)), worstDayPrimaryScore=min(scores),
                                 maximumDailyDrawdown=max(row["maximumDrawdown"] for row in matching),
                                 entries=sum(row["entries"] for row in matching), grossPnl=sum((row["grossPnl"] for row in matching), ZERO),
                                 positiveEveryDay=all(score > 0 for score in scores),
                                 daily=[dict(date=row["date"], primaryScore=row["primaryScore"], dataSha256=row["dataSha256"],
                                             sourceSha256=row["sourceSha256"], file=row["file"]) for row in matching]))
        rankings.sort(key=lambda row: (-row["totalPrimaryScore"], -row["worstDayPrimaryScore"],
                                       row["maximumDailyDrawdown"], row["entries"], ORDER.index(row["profile"])))
        active = [row for row in rankings if row["entries"] >= 1]
        if not active:
            problems.append("No candidate has an entry on eligible development dates")
        winner = dict(active[0], symbol=symbol) if active and not problems else None
        review = dict(symbol=symbol, ready=winner is not None, problems=problems,
                      availableDevelopmentDates=available, unavailableDevelopmentDates=unavailable,
                      eligibleDevelopmentDates=eligible, ranking=rankings, winner=winner)
        stock_reviews.append(review)
        if winner:
            winners.append(winner)
    winners.sort(key=lambda row: (not row["positiveEveryDay"], -row["meanDailyPrimaryScore"],
                                 -row["worstDayPrimaryScore"], row["maximumDailyDrawdown"], row["symbol"]))
    for index, row in enumerate(winners, 1):
        row.update(rank=index, tier=1 if row["positiveEveryDay"] else 2)
    return stock_reviews, winners


def markdown(report):
    lines = ["# Low-price Replay decimal audit", "", f"Generated {report['generatedAtUtc']}.", "",
             f"Audited {report['auditedRuns']} completed runs and {report['unavailableAttempts']} no-history attempts; "
             f"{report['invalidRuns']} invalid exports. Friday never enters selection.", "",
             "Primary score deducts 5 bps on traded notional plus a closing-cost allowance on ending exposure. "
             "This is a static-ledger sensitivity, not an execution simulation.", "",
             "| Rank | Symbol | Candidate | Tier | Days | Gross total | 5 bps total | Worst day | Maximum daily drawdown |",
             "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    money = lambda amount: f"{amount:.4f}"
    for row in report["stockRanking"]:
        lines.append(f"| {row['rank']} | {row['symbol']} | {row['profile']} | {row['tier']} | {row['days']} | "
                     f"{money(row['grossPnl'])} | {money(row['totalPrimaryScore'])} | {money(row['worstDayPrimaryScore'])} | "
                     f"{money(row['maximumDailyDrawdown'])} |")
    lines.extend(["", "Selected: " + (", ".join(row["symbol"] + "/" + row["profile"] for row in report["selections"]) or "none"), ""])
    if report["selectionShortfall"]:
        lines.append(f"Selection shortfall: {report['selectionShortfall']} fewer than the requested three eligible stocks.")
    if any(row["tier"] == 2 for row in report["selections"]):
        lines.append("Tier 2 selections did not have a positive primary score on every eligible development day.")
    for stock in report["stocks"]:
        if stock["problems"]:
            lines.append(f"- {stock['symbol']}: " + "; ".join(stock["problems"]))
    lines.extend(["", "## Daily evidence", "", "| Symbol | Date | Candidate | Coverage | Gross | 5 bps | Drawdown | Entries | Ending exposure | Valid |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"])
    for row in report["runs"]:
        if "grossPnl" in row:
            lines.append(f"| {row['symbol']} | {row['date']} | {row['profile']} | {row['sourceCount']}/1560 | "
                         f"{money(row['grossPnl'])} | {money(row['primaryScore'])} | {money(row['maximumDrawdown'])} | "
                         f"{row['entries']} | {money(row['endingExposure'])} | {'no' if row['failures'] else 'yes'} |")
    failures = [row for row in report["runs"] + report["unavailable"] if row["failures"]]
    if failures:
        lines.extend(["", "## Audit failures", ""])
        for row in failures:
            lines.append(f"- {row['file']}: " + "; ".join(row["failures"][:8]))
    return "\n".join(lines) + "\n"


def visualization_data(report, equity, frozen):
    companies = {"SOFI": "SoFi Technologies", "F": "Ford Motor", "RIVN": "Rivian Automotive",
                 "SNAP": "Snap", "NU": "Nu Holdings", "CCL": "Carnival Corporation",
                 "AAL": "American Airlines Group", "PINS": "Pinterest"}
    metrics = {(row["symbol"], row["profile"], row["date"]): row for row in report["runs"]}
    days = []
    for date in DEVELOPMENT + (FRIDAY,):
        series = []
        for path in equity:
            if path["date"] != date:
                continue
            row = metrics[path["symbol"], path["profile"], date]
            series.append(dict(ticker=path["symbol"], status="complete" if not row["failures"] and row["dataEligible"] else "partial",
                               points=[dict(t=point["atUtc"], equity=point["equity"]) for point in path["observations"]],
                               grossPnl=row["grossPnl"], costAdjustedPnl=row["primaryScore"],
                               entries=row["entries"], sourceCount=row["sourceCount"], expectedSourceCount=EXPECTED,
                               firstReady=row["firstReady"], endingExposure=row["endingExposure"]))
        if series:
            days.append(dict(date=date, phase="heldout" if date == FRIDAY else "development", series=series))
    selected_symbols = {row["symbol"] for row in report["selections"]}
    missing = [day for day in DEVELOPMENT if selected_symbols and all(
        any(row["symbol"] == symbol and row["date"] == day and not row["failures"] for row in report["unavailable"])
        for symbol in selected_symbols)]
    return dict(schemaVersion=1, initialEquity=STARTING, positionSize=D(500), costBpsPerSide=D(5),
                timeZone="America/Los_Angeles", selectionFrozen=frozen,
                auditPassed=report["invalidRuns"] == 0 and not report["selectionPending"],
                costIncludesClosingAllowance=True, missingDates=missing,
                stocks=[dict(ticker=row["symbol"], company=companies[row["symbol"]], profileId=row["profile"])
                        for row in report["selections"]], days=days)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, default=REPO / "artifacts/low-price-research/audit-current")
    parser.add_argument("--include-friday", action="store_true", help="Audit Friday files but never rank on Friday")
    parser.add_argument("--frozen-selection", type=Path, help="Verify ordered selections against a previously frozen JSON file")
    args = parser.parse_args()
    candidates = load_manifest()
    rows = []
    pattern = re.compile(r"^(?:unavailable-)?([A-Z]+)-(2026-\d{2}-\d{2})-(C1|C2|C3|B1|R1)\.json$")
    for path in sorted(args.directory.glob("*.json")):
        if path.name.startswith("failed-"):
            failure = read_json(path)
            job = failure["job"]
            if job["symbol"] in UNIVERSE and job["date"] in DEVELOPMENT + ((FRIDAY,) if args.include_friday else ()):
                rows.append(dict(file=str(path), symbol=job["symbol"], date=job["date"], profile=job["profile"],
                                 unavailable=False, failures=["Unexplained runner failure export: " + str(failure.get("error"))]))
            continue
        match = pattern.match(path.name)
        if not match or match[1] not in UNIVERSE or match[2] not in DEVELOPMENT + ((FRIDAY,) if args.include_friday else ()):
            continue
        try:
            run = read_json(path)
            row = audit_run(run, candidates[match[3]], path)
            if (row["symbol"], row["date"], row["profile"]) != match.groups():
                row["failures"].append("Filename/job identity mismatch")
            row["fileSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        except (ValueError, TypeError, KeyError) as error:
            row = dict(file=str(path), symbol=match[1], date=match[2], profile=match[3], unavailable=path.name.startswith("unavailable-"),
                       failures=[f"Raw JSON/schema failure: {error}"])
        rows.append(row)
    # Session/operation reuse must be visible even if a copied file's ledger matches.
    for field in ("sessionId", "operationId"):
        identities = defaultdict(list)
        for row in rows:
            if row.get(field):
                identities[row[field]].append(row)
        for grouped in identities.values():
            if len(grouped) > 1:
                for row in grouped:
                    row["failures"].append(f"Reused {field} across exports")
    sources = defaultdict(list)
    for row in rows:
        if not row["unavailable"] and row.get("dataSha256"):
            sources[row["symbol"], row["date"]].append(row)
    for grouped in sources.values():
        if len({row["dataSha256"] for row in grouped}) > 1:
            for row in grouped:
                row["failures"].append("Historical source differs across candidates")
    stocks, ranking = rank_runs(rows)
    incomplete = any(problem not in ("Fewer than two eligible development days",
                                     "No candidate has an entry on eligible development dates")
                     for stock in stocks for problem in stock["problems"])
    selected = ranking[:3] if not incomplete else []
    frozen = False
    if args.frozen_selection:
        frozen_rows = read_json(args.frozen_selection)["selections"]
        frozen = [(row["symbol"], row["profile"]) for row in frozen_rows] == [(row["symbol"], row["profile"]) for row in selected]
        if not frozen or len(selected) != 3:
            raise ValueError("Ordered current selections differ from frozen selection; no outputs written")
    selected_keys = {(row["symbol"], row["profile"]) for row in selected}
    equity = []
    for row in rows:
        path = row.pop("equityPath", None)
        if path is not None and (row["symbol"], row["profile"]) in selected_keys:
            equity.append(dict(symbol=row["symbol"], profile=row["profile"], date=row["date"],
                               dataSha256=row["dataSha256"], sourceSha256=row["sourceSha256"], observations=path))
    report = dict(generatedAtUtc=stamp(datetime.now(timezone.utc)), protocolSha256=hashlib.sha256((ROOT/"protocol.md").read_bytes()).hexdigest(),
                  candidateManifestSha256=hashlib.sha256((ROOT/"candidates"/"manifest.json").read_bytes()).hexdigest(),
                  auditorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), selectionUsesFriday=False,
                  decimalArithmetic="60 significant digits; exact ledger equality; decimal outputs are strings",
                  sourceSignature="SHA256 of canonical UTC source start/end and exact OHLC,last,bid,ask,volume; excludes observedAtUtc",
                  auditedRuns=sum(not row["unavailable"] for row in rows), unavailableAttempts=sum(row["unavailable"] for row in rows),
                  invalidRuns=sum(bool(row["failures"]) for row in rows),
                  runs=[row for row in rows if not row["unavailable"]], unavailable=[row for row in rows if row["unavailable"]],
                  stocks=stocks, stockRanking=ranking, selections=selected, selectionShortfall=max(0, 3-len(selected)))
    report["selectionPending"] = incomplete
    args.output.mkdir(parents=True, exist_ok=True)
    for name, data in (("audit.json", report), ("selected-equity.json", dict(selectionUsesFriday=False, runs=equity)),
                       ("visualization-data.json", visualization_data(report, equity, frozen))):
        (args.output/name).write_text(json.dumps(json_ready(data), indent=2) + "\n", encoding="utf-8")
    (args.output/"audit.md").write_text(markdown(report), encoding="utf-8")
    print(f"{report['auditedRuns']} runs, {report['unavailableAttempts']} unavailable attempts, "
          f"{report['invalidRuns']} invalid exports; {len(ranking)} eligible stocks; output {args.output}")
    return 1 if report["invalidRuns"] else 0


if __name__ == "__main__":
    sys.exit(main())
