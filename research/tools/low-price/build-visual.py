"""Build the inline comparison from audited, selected-stock observations only."""

import argparse
import datetime as dt
import json
import math
from pathlib import Path


def finite(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float, str)):
        raise ValueError(f"{name} must be a finite number")
    try:
        number = float(value)
    except ValueError as error:
        raise ValueError(f"{name} must be a finite number") from error
    if not math.isfinite(number):
        raise ValueError(f"{name} must be a finite number")
    return number


def retain_extrema(points, maximum=1000):
    """Keep actual observations, including endpoints and each bin's extrema."""
    if len(points) <= maximum:
        return points
    bins = (maximum - 2) // 2
    indices = {0, len(points) - 1}
    for bucket in range(bins):
        start = 1 + (len(points) - 2) * bucket // bins
        end = 1 + (len(points) - 2) * (bucket + 1) // bins
        if end > start:
            candidates = range(start, end)
            indices.add(min(candidates, key=lambda index: points[index][1]))
            indices.add(max(candidates, key=lambda index: points[index][1]))
    return [points[index] for index in sorted(indices)]


def prepare(source):
    if source.get("schemaVersion") != 1:
        raise ValueError("Expected schemaVersion 1")
    if source.get("selectionFrozen") is not True or source.get("auditPassed") is not True:
        raise ValueError("The selection must be frozen and the independent audit passed")
    if source.get("costIncludesClosingAllowance") is not True:
        raise ValueError("5-bps endpoints must include the ending closing-cost allowance")
    initial = finite(source["initialEquity"], "initialEquity")
    size = finite(source["positionSize"], "positionSize")
    cost = finite(source["costBpsPerSide"], "costBpsPerSide")
    if (initial, size, cost) != (1400, 500, 5):
        raise ValueError("Expected the frozen $1,400 account, $500 position, and 5-bps sensitivity")
    stocks = source["stocks"]
    if len(stocks) != 3 or len({stock["ticker"] for stock in stocks}) != 3:
        raise ValueError("Exactly three distinct selected stocks are required")
    stocks = [{key: str(stock[key]) for key in ("ticker", "company", "profileId")} for stock in stocks]
    tickers = {stock["ticker"] for stock in stocks}
    expected = {"2026-09-02": "development", "2026-09-03": "development", "2026-09-04": "heldout"}
    if len(source["days"]) != 3 or {day["date"] for day in source["days"]} != set(expected):
        raise ValueError("Expected separate Wednesday, Thursday, and Friday observations")
    days = []
    sampled = False
    for day in sorted(source["days"], key=lambda item: item["date"]):
        if day["phase"] != expected[day["date"]]:
            raise ValueError("Friday must be held out; Wednesday and Thursday are development dates")
        if len(day["series"]) != 3 or {series["ticker"] for series in day["series"]} != tickers:
            raise ValueError("Each date needs observations for all three selected stocks")
        series_out = []
        for stock in stocks:
            series = next(series for series in day["series"] if series["ticker"] == stock["ticker"])
            if series.get("status") not in ("complete", "partial"):
                raise ValueError("Missing or unaudited sessions cannot be presented as observed paths")
            gross = finite(series["grossPnl"], "grossPnl")
            adjusted = finite(series["costAdjustedPnl"], "costAdjustedPnl")
            if adjusted > gross + 0.000001:
                raise ValueError("Cost-adjusted P&L exceeds gross P&L")
            coverage = {name: series[name] for name in ("entries", "sourceCount", "expectedSourceCount")}
            if any(isinstance(value, bool) or not isinstance(value, int) or value < 0 for value in coverage.values()):
                raise ValueError("Entry and source counts must be nonnegative integers")
            if not 0 < coverage["sourceCount"] <= coverage["expectedSourceCount"]:
                raise ValueError("Source coverage must be between zero and the expected count")
            first_ready = series["firstReady"]
            if first_ready is not None and not isinstance(first_ready, str):
                raise ValueError("firstReady must be an actual timestamp or null")
            exposure = finite(series["endingExposure"], "endingExposure")
            if exposure < 0:
                raise ValueError("Ending long exposure cannot be negative")
            points = []
            for point in series["points"]:
                timestamp = dt.datetime.fromisoformat(point["t"].replace("Z", "+00:00"))
                if timestamp.utcoffset() is None:
                    raise ValueError("Observation timestamps require an explicit UTC offset")
                equity = finite(point["equity"], "equity")
                points.append([timestamp.timestamp() * 1000, equity - initial])
            if len(points) < 2 or any(b[0] <= a[0] for a, b in zip(points, points[1:])):
                raise ValueError("Each path requires at least two strictly ordered actual observations")
            if abs(points[-1][1] - gross) > 0.000001:
                raise ValueError("Path endpoint does not reconcile with audited gross P&L")
            retained = retain_extrema(points)
            sampled |= len(retained) != len(points)
            series_out.append({"ticker": stock["ticker"], "status": series["status"], "points": retained, "grossPnl": gross,
                               "costAdjustedPnl": adjusted, "observationCount": len(points),
                               **coverage, "firstReady": first_ready, "endingExposure": exposure})
        days.append({"date": day["date"], "phase": day["phase"], "series": series_out})
    if source["timeZone"] != "America/Los_Angeles":
        raise ValueError("Expected Pacific session labels")
    if set(source["missingDates"]) != {"2026-08-31", "2026-09-01"}:
        raise ValueError("The frozen missing-history dates must be recorded explicitly")
    return {"initialEquity": initial, "positionSize": size, "costBpsPerSide": cost,
            "timeZone": source["timeZone"], "missingDates": source["missingDates"],
            "stocks": stocks, "days": days, "sampled": sampled}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Audited visualization-data.json")
    parser.add_argument("output", type=Path, help="Absolute path to the thread-owned inline fragment")
    args = parser.parse_args()
    if not args.output.is_absolute():
        parser.error("output must be an absolute path")
    data = prepare(json.loads(args.input.read_text(encoding="utf-8-sig")))
    payload = json.dumps(data, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    payload = payload.replace("<", "\\u003c").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    template = Path(__file__).with_name("comparison-template.html").read_text(encoding="utf-8")
    if template.count("__AUDITED_COMPARISON_DATA__") != 1:
        raise ValueError("The literal comparison template must have exactly one data placeholder")
    fragment = template.replace("__AUDITED_COMPARISON_DATA__", payload)
    if len(fragment.encode("utf-8")) >= 1_000_000:
        raise ValueError("Inline comparison exceeds 1 MB")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(fragment, encoding="utf-8")
    reread = args.output.read_text(encoding="utf-8")
    if reread != fragment or '\\"' in template or "\\n" in template:
        raise ValueError("Fragment did not retain literal markup")
    print(json.dumps({"output": str(args.output), "bytes": len(fragment.encode("utf-8")),
                      "stocks": [stock["ticker"] for stock in data["stocks"]],
                      "days": len(data["days"]), "sampled": data["sampled"]}))


if __name__ == "__main__":
    main()
