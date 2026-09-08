"""Synthetic per-fund selection/freeze checks without semiconductor market data."""
import copy
from decimal import Decimal as D
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import audit


def fixture(symbol):
    rows = [dict(symbol=symbol, date=date, profile="C1", unavailable=True, failures=[],
                 fileSha256=symbol+date, sourceSha256="C1") for date in audit.BASE.DEVELOPMENT[:2]]
    for date in audit.BASE.DEVELOPMENT[2:]:
        for profile in audit.BASE.ORDER:
            rows.append(dict(symbol=symbol, date=date, profile=profile, unavailable=False, failures=[],
                             fileSha256=symbol+date+profile, dataEligible=True, dataSha256=symbol+date,
                             sourceSha256=profile, file="synthetic", primaryScore=D(1), grossPnl=D(2),
                             maximumDrawdown=D(1), entries=1))
    return rows


class SemiconductorTests(unittest.TestCase):
    def test_both_funds_select_independently(self):
        _, selected = audit.select(fixture("SOXL") + fixture("SOXX"))
        self.assertEqual([(row["symbol"], row["profile"]) for row in selected], [("SOXL", "C1"), ("SOXX", "C1")])

    def test_one_funds_coverage_or_corruption_does_not_block_other(self):
        for kind in ("coverage", "missing", "corruption", "source"):
            rows = fixture("SOXL")
            if kind == "coverage":
                for row in rows:
                    if row["date"] == audit.BASE.DEVELOPMENT[-1]:
                        row["dataEligible"] = False
            elif kind == "missing":
                rows.pop()
            elif kind == "corruption":
                rows[-1]["failures"].append("bad ledger")
            else:
                rows[-1]["dataSha256"] = "different"
            self.assertEqual([row["symbol"] for row in audit.select(rows + fixture("SOXX"))[1]], ["SOXX"])

    def test_shared_session_corruption_blocks_both_affected_funds(self):
        rows = fixture("SOXL") + fixture("SOXX")
        for row in rows:
            row["sessionId"] = "shared"
        audit.check_cross_run_identity(rows)
        self.assertEqual(audit.select(rows)[1], [])

    def test_inactive_candidate_cannot_win(self):
        rows = fixture("SOXL")
        for row in rows:
            if not row["unavailable"] and row["profile"] == "C1":
                row.update(entries=0, primaryScore=D(100))
        self.assertEqual(audit.select(rows)[1][0]["profile"], "C2")

    def test_friday_changes_neither_selection_nor_development_evidence(self):
        rows = fixture("SOXL") + fixture("SOXX")
        friday = dict(rows[-1], date=audit.BASE.FRIDAY, primaryScore=D("99999999"), fileSha256="friday")
        self.assertEqual(audit.select(rows), audit.select(rows + [friday]))
        self.assertEqual(audit.development_evidence(rows), audit.development_evidence(rows + [friday]))

    def test_frozen_empty_selection_pins_completed_unavailable_and_scores(self):
        rows = fixture("SOXL") + fixture("SOXX")
        report = dict(selections=[], developmentEvidence=audit.development_evidence(rows),
                      protocolSha256="protocol", candidateManifestSha256="manifest")
        frozen = audit.BASE.json_ready(dict(report, selectionUsesFriday=False,
                                           developmentAuditSha256=hashlib.sha256(b"report").hexdigest()))
        with tempfile.TemporaryDirectory() as directory, patch.object(audit, "ROOT", Path(directory)):
            (Path(directory)/"development-audit.json").write_bytes(b"report")
            audit.verify_frozen(frozen, report)
            mutations = [(0, "fileSha256", "altered unavailable"), (2, "dataSha256", "altered source data"),
                         (2, "sourceSha256", "altered candidate"), (2, "primaryScore", "100")]
            for index, key, value in mutations:
                changed = copy.deepcopy(frozen)
                changed["developmentEvidence"][index][key] = value
                self.assertRaises(ValueError, audit.verify_frozen, changed, report)
            (Path(directory)/"development-audit.json").write_bytes(b"changed")
            self.assertRaises(ValueError, audit.verify_frozen, frozen, report)

    def test_frozen_winner_scores_are_verified(self):
        rows = fixture("SOXL") + fixture("SOXX")
        report = dict(selections=audit.select(rows)[1], developmentEvidence=audit.development_evidence(rows),
                      protocolSha256="protocol", candidateManifestSha256="manifest")
        frozen = audit.BASE.json_ready(dict(report, selectionUsesFriday=False, developmentAuditSha256="unused"))
        frozen["selections"][0]["totalPrimaryScore"] = "999"
        self.assertRaises(ValueError, audit.verify_frozen, frozen, report)

    def test_frozen_manifest_allows_only_verified_location_changes(self):
        manifest = dict(ValidationProject="../../old/Validation.csproj", CandidateOrder=["C1"],
                        Candidates=[dict(Id="C1", File="Script.thinkscript", SourceSha256="source",
                                         DefaultInputs=dict(length=8))])
        relocated = copy.deepcopy(manifest)
        relocated["ValidationProject"] = "../../../tools/Validation.csproj"
        relocated["Candidates"][0]["File"] = "../../../strategies/Script.thinkscript"
        with tempfile.TemporaryDirectory() as directory, patch.object(audit, "ROOT", Path(directory)):
            root = Path(directory)
            (root / "candidates").mkdir()
            original = root / "candidates/manifest.frozen.json"
            current = root / "candidates/manifest.json"
            original.write_text(json.dumps(manifest))
            current.write_text(json.dumps(relocated))
            development = root / "development-audit.json"
            development.write_bytes(b"unchanged development evidence")
            report = dict(selections=[], developmentEvidence=[], protocolSha256="protocol",
                          candidateManifestSha256=hashlib.sha256(current.read_bytes()).hexdigest())
            frozen = dict(report, selectionUsesFriday=False,
                          candidateManifestSha256=hashlib.sha256(original.read_bytes()).hexdigest(),
                          developmentAuditSha256=hashlib.sha256(development.read_bytes()).hexdigest())
            audit.verify_frozen(frozen, report)
            for key, value in (("SourceSha256", "changed"), ("DefaultInputs", dict(length=9)),
                               ("File", "Different.thinkscript")):
                changed = copy.deepcopy(relocated)
                changed["Candidates"][0][key] = value
                current.write_text(json.dumps(changed))
                report["candidateManifestSha256"] = hashlib.sha256(current.read_bytes()).hexdigest()
                self.assertRaises(ValueError, audit.verify_frozen, frozen, report)
            current.write_text(json.dumps(relocated))
            report["candidateManifestSha256"] = "changed report hash"
            self.assertRaises(ValueError, audit.verify_frozen, frozen, report)
            report["candidateManifestSha256"] = hashlib.sha256(current.read_bytes()).hexdigest()
            original.write_bytes(b"changed snapshot")
            self.assertRaises(ValueError, audit.verify_frozen, frozen, report)

    def test_malformed_development_is_invalid_and_friday_unread_by_default(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path/"SOXL-2026-09-02-C1.json").write_text("{}")
            (path/"SOXX-2026-09-04-C1.json").write_text("not json")
            rows = audit.read_rows(path, {"C1": {}})
            self.assertEqual(len(rows), 1)
            self.assertTrue(rows[0]["failures"])
            self.assertTrue(rows[0]["fileSha256"])
            self.assertEqual(len(audit.read_rows(path, {"C1": {}}, True)), 2)


if __name__ == "__main__":
    unittest.main()
