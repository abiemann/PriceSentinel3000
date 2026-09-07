"""Offline fault injection into copies of one saved Replay export; no app calls."""
import copy
import importlib.util
import json
import sys
from decimal import Decimal
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
ROOT = HERE.parents[3]
spec = importlib.util.spec_from_file_location("indicator_audit", HERE.parent / "audit-indicators.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
manifest_path = HERE.parent.parent / "candidates" / "manifest.json"
candidates = module.read(manifest_path)["Candidates"]
for candidate in candidates:
    candidate["logic"] = module.canonical((manifest_path.parent / candidate["File"]).read_text(encoding="utf-8-sig"))
sample = ROOT / "artifacts" / "low-price-research" / "development" / "AAL-2026-09-02-C1.json"
original = module.read(sample)
results = []
for case in ("unchanged", "indicator value + 0.01", "wrong proposed action"):
    changed = copy.deepcopy(original)
    evaluation = next(event["scriptEvaluation"] for event in changed["events"]["records"]
                      if event["scriptEvaluation"] and not event["scriptEvaluation"]["isWarmingUp"])
    if case == "indicator value + 0.01":
        evaluation["proposal"]["indicators"][0]["value"] += Decimal("0.01")
    elif case == "wrong proposed action":
        evaluation["proposal"]["action"] = "Buy" if evaluation["proposal"]["action"] != "Buy" else "Sell"
    module.read = lambda _path: changed
    result = module.audit(sample, candidates)
    expected_valid = case == "unchanged"
    assert (result["failureCount"] == 0) == expected_valid, (case, result["failures"])
    results.append(dict(case=case, passed=True, detectedFailures=result["failureCount"], failures=result["failures"]))
(HERE / "indicator-negative-results.json").write_text(json.dumps(dict(appCalls=0, cases=results), indent=2) + "\n", encoding="utf-8")
print("PASS: unchanged fixture accepted; corrupt indicator and proposed action rejected.")
