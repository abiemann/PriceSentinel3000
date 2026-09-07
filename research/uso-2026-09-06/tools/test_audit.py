"""Synthetic USO selection checks; no USO market data or P&L is inspected."""
import copy
from decimal import Decimal as D
import json
from pathlib import Path
import tempfile
import unittest

import audit


def fixture():
    rows = [dict(symbol="USO", date=date, profile="C1", unavailable=True, failures=[])
            for date in audit.BASE.DEVELOPMENT[:2]]
    for date in audit.BASE.DEVELOPMENT[2:]:
        for profile in audit.BASE.ORDER:
            rows.append(dict(symbol="USO", date=date, profile=profile, unavailable=False, failures=[],
                             dataEligible=True, dataSha256=date, sourceSha256=profile, file="synthetic",
                             primaryScore=D(1), grossPnl=D(2), maximumDrawdown=D(1), entries=1))
    return rows


class UsoAuditTests(unittest.TestCase):
    def test_frozen_order_breaks_complete_tie(self):
        _, _, selected, pending = audit.select(fixture())
        self.assertFalse(pending)
        self.assertEqual([(row["symbol"], row["profile"]) for row in selected], [("USO", "C1")])

    def test_friday_does_not_change_winner(self):
        rows = fixture()
        before = audit.select(rows)
        friday = dict(rows[-1], date=audit.BASE.FRIDAY, primaryScore=D("999999999"))
        self.assertEqual(before, audit.select(rows + [friday]))

    def test_one_eligible_day_has_no_winner(self):
        rows = fixture()
        for row in rows:
            if row["date"] == audit.BASE.DEVELOPMENT[-1]:
                row["dataEligible"] = False
        stocks, _, selected, _ = audit.select(rows)
        self.assertEqual(selected, [])
        self.assertIn("Fewer than two eligible development days", stocks[0]["problems"])

    def test_missing_comparison_and_different_sources_block_selection(self):
        variants = [fixture()[:-1], fixture()]
        variants[1][-1]["dataSha256"] = "different"
        for rows in variants:
            self.assertEqual(audit.select(rows)[2], [])
            self.assertTrue(audit.select(rows)[3])

    def test_inactive_high_score_cannot_win(self):
        rows = fixture()
        for row in rows:
            if not row["unavailable"] and row["profile"] == "C1":
                row.update(entries=0, primaryScore=D(100))
        self.assertEqual(audit.select(rows)[2][0]["profile"], "C2")

    def test_frozen_scores_and_hashes_are_verified(self):
        selected = audit.select(fixture())[2]
        frozen = dict(selectionUsesFriday=False, selections=audit.BASE.json_ready(selected))
        audit.verify_frozen(frozen, selected)
        for mutate in (lambda row: row.update(totalPrimaryScore="100"),
                       lambda row: row["daily"][0].update(dataSha256="corrupted"),
                       lambda row: row["daily"][0].update(sourceSha256="corrupted")):
            damaged = copy.deepcopy(frozen)
            mutate(damaged["selections"][0])
            self.assertRaises(ValueError, audit.verify_frozen, damaged, selected)

    def test_empty_frozen_selection_and_relocated_paths(self):
        audit.verify_frozen(dict(selectionUsesFriday=False, selections=[]), [])
        selected = audit.select(fixture())[2]
        frozen = dict(selectionUsesFriday=False, selections=audit.BASE.json_ready(selected))
        frozen["selections"][0]["daily"][0]["file"] = "relocated"
        audit.verify_frozen(frozen, selected)
        frozen["selectionUsesFriday"] = True
        self.assertRaises(ValueError, audit.verify_frozen, frozen, selected)

    def test_malformed_development_export_is_invalid_and_friday_is_unread(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)
            (path / "USO-2026-09-02-C1.json").write_text("{}")
            (path / "USO-2026-09-04-C1.json").write_text("invalid json")
            rows = audit.read_rows(path, {"C1": {}})
            self.assertEqual(len(rows), 1)
            self.assertTrue(rows[0]["failures"])
            self.assertEqual(len(audit.read_rows(path, {"C1": {}}, True)), 2)

    def test_runner_failure_blocks_even_with_other_valid_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)
            (path / "failed-USO-2026-09-02-C1-batch.json").write_text(json.dumps(
                dict(job=dict(symbol="USO", date="2026-09-02", profile="C1"), error="timeout")))
            failures = audit.read_rows(path, {})
            self.assertEqual(audit.select(fixture() + failures)[2], [])

    def test_source_gap_restarts_contiguous_warmup_count(self):
        def stamp(minute):
            return f"2026-09-02T13:{minute:02d}:00+00:00"
        run = dict(strategy=dict(records=[dict(endsAtUtc=stamp(minute)) for minute in (31, 32, 34)]),
                   source=dict(records=[dict(startsAtUtc=stamp(minute), endsAtUtc=stamp(minute + 1)) for minute in (30, 31, 33)]),
                   events=dict(records=[dict(scriptEvaluation=None), dict(scriptEvaluation=dict(isWarmingUp=True)),
                                        dict(scriptEvaluation=dict(isWarmingUp=False))]))
        self.assertEqual(audit.warmup_diagnostics(run), dict(maximumContiguousStrategyBars=2, readyEvaluations=1, warmupEvaluations=1))


if __name__ == "__main__":
    unittest.main()
