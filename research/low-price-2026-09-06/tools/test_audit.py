"""Focused corruption/reconciliation/ranking checks; no app calls or market tuning."""
import copy
from decimal import Decimal as D
from pathlib import Path
import unittest

import audit

REPO = Path(__file__).resolve().parents[3]


class AuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = audit.load_manifest()
        cls.fixture = audit.read_json(REPO / "artifacts/low-price-research/development/SOFI-2026-09-02-C1.json")

    def checked(self, mutate=None):
        raw = copy.deepcopy(self.fixture)
        if mutate:
            mutate(raw)
        return audit.audit_run(raw, self.manifest["C1"])

    def test_real_export_reconciles_exactly_and_preserves_gap(self):
        row = self.checked()
        self.assertEqual(row["failures"], [])
        self.assertEqual(row["sourceCount"], 1559)
        self.assertEqual(row["gapCount"], 1)
        self.assertTrue(row["dataEligible"])
        self.assertEqual(row["grossPnl"], row["realized"] + row["unrealized"])
        self.assertEqual(row["grossPnl"], row["equityPath"][-1]["equity"] - D(1400))
        self.assertFalse(row["endingReady"])

    def test_one_tenth_nanodollar_corruption_is_rejected(self):
        row = self.checked(lambda raw: raw["events"]["records"][0]["account"].update(cash=D("1400.0000000001")))
        self.assertTrue(any("account.cash" in failure for failure in row["failures"]))

    def test_truncated_page_is_rejected(self):
        row = self.checked(lambda raw: raw["source"]["pages"][-1].update(truncated=True))
        self.assertTrue(any("truncated" in failure for failure in row["failures"]))

    def test_aggregate_corruption_is_rejected(self):
        row = self.checked(lambda raw: raw["strategy"]["records"][0].update(volume=0))
        self.assertTrue(any("strategy[1].volume" in failure for failure in row["failures"]))

    def test_warmup_corruption_is_rejected(self):
        row = self.checked(lambda raw: raw["events"]["records"][0]["scriptEvaluation"].update(requiredWarmupBars=84))
        self.assertTrue(any("warmup" in failure for failure in row["failures"]))

    def test_nonfinite_and_duplicate_numbers_rejected(self):
        self.assertRaises(ValueError, audit.number, True)
        self.assertRaises(ValueError, audit.unique_object, [("x", 1), ("x", 2)])
        self.assertEqual(audit.json_ready({"money": D("1.234567890123456789")}), {"money": "1.234567890123456789"})

    def test_cost_sensitivity_has_one_closing_allowance(self):
        bps, shares = audit.sensitivities(D(1), D(1000), D(10), D(100), D(2))
        self.assertEqual(bps["5"], D("0.45"))
        self.assertEqual(shares["0.005"], D("0.94"))

    def test_adapted_mag7_schema_fixture(self):
        # Same C1 formula; change only metadata/comments to adapt the historical schema.
        # This fixture never enters the low-price ranking or audit output.
        raw = audit.read_json(REPO / "artifacts/mag7-research/AAPL-2026-09-02-baseline.json")
        raw["job"] = copy.deepcopy(self.fixture["job"])
        raw["status"]["settings"].update(symbol="SOFI", strategyId=raw["job"]["strategyId"])
        raw["results"]["settings"].update(Symbol="SOFI", StrategyId=raw["job"]["strategyId"])
        for spec in (raw["results"]["settings"]["Strategy"], raw["indicators"]["strategy"]):
            spec.update(Id=raw["job"]["strategyId"], SourceSha256=self.manifest["C1"]["SourceSha256"])
        raw["results"]["settings"]["Strategy"]["Source"] = self.fixture["results"]["settings"]["Strategy"]["Source"]
        raw["status"]["strategy"].update(id=raw["job"]["strategyId"], sourceSha256=self.manifest["C1"]["SourceSha256"])
        row = audit.audit_run(raw, self.manifest["C1"])
        # Older exports passed through floating-point JSON serialization. Any strict
        # differences must be ledger precision differences, not schema incompatibility.
        self.assertFalse(any("Schema/audit failure" in failure for failure in row["failures"]))
        self.assertTrue(all("expected " in failure and "recorded " in failure for failure in row["failures"]))


def ranking_fixture(symbol="SOFI"):
    rows = [dict(symbol=symbol, date=day, profile="C1", unavailable=True, failures=[])
            for day in audit.DEVELOPMENT[:2]]
    for day in audit.DEVELOPMENT[2:]:
        for profile in audit.ORDER:
            rows.append(dict(symbol=symbol, date=day, profile=profile, unavailable=False, failures=[],
                             dataEligible=True, dataSha256=day, sourceSha256=profile, file="synthetic",
                             primaryScore=D(1), grossPnl=D(2), maximumDrawdown=D(1), entries=1))
    return rows


class RankingTests(unittest.TestCase):
    def test_tie_falls_back_to_frozen_candidate_order(self):
        _, winners = audit.rank_runs(ranking_fixture(), ("SOFI",))
        self.assertEqual(winners[0]["profile"], "C1")

    def test_one_day_is_ineligible(self):
        rows = ranking_fixture()
        for row in rows:
            if row["date"] == audit.DEVELOPMENT[-1]:
                row["dataEligible"] = False
        _, winners = audit.rank_runs(rows, ("SOFI",))
        self.assertEqual(winners, [])

    def test_missing_comparison_or_source_change_blocks_winner(self):
        for rows in (ranking_fixture()[:-1], ranking_fixture()):
            if len(rows) == 12:
                rows[-1]["dataSha256"] = "different"
            _, winners = audit.rank_runs(rows, ("SOFI",))
            self.assertEqual(winners, [])

    def test_friday_has_no_ranking_effect(self):
        rows = ranking_fixture()
        before = audit.rank_runs(rows, ("SOFI",))
        friday = dict(rows[-1], date=audit.FRIDAY, primaryScore=D("99999999"))
        self.assertEqual(before, audit.rank_runs(rows + [friday], ("SOFI",)))

    def test_positive_every_day_tier_precedes_higher_average(self):
        rows = ranking_fixture("SOFI") + ranking_fixture("NU")
        for row in rows:
            if row["symbol"] == "NU" and not row["unavailable"]:
                row["primaryScore"] = D(-1) if row["date"] == audit.DEVELOPMENT[-1] else D(100)
        _, winners = audit.rank_runs(rows, ("SOFI", "NU"))
        self.assertEqual([row["symbol"] for row in winners], ["SOFI", "NU"])
        self.assertEqual([row["tier"] for row in winners], [1, 2])

    def test_inactive_candidate_cannot_win(self):
        rows = ranking_fixture()
        for row in rows:
            if not row["unavailable"] and row["profile"] == "C1":
                row.update(entries=0, primaryScore=D(10))
        _, winners = audit.rank_runs(rows, ("SOFI",))
        self.assertEqual(winners[0]["profile"], "C2")


if __name__ == "__main__":
    unittest.main()
