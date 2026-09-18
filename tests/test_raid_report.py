import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("raid_report", Path(__file__).parents[1] / "tools" / "raid_report.py")
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class ReportTests(unittest.TestCase):
    def test_counts_keep_stages_and_windows_separate(self):
        data = {"summary.json": {}, "frames.jsonl": [
            {"windowId": 1, "stage": "after_visual"},
            {"windowId": 1, "stage": "after_lock"},
            {"windowId": 2, "stage": "after_lock"}], "events.jsonl": [{"kind": "hit"}]}
        result = report.analyze(data)
        self.assertEqual(result["stages"], {"after_visual": 1, "after_lock": 2})
        self.assertEqual(result["windowRows"], {"1": 2, "2": 1})
        self.assertEqual(result["eventRows"], 1)

    def test_reader_does_not_extract_foreign_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "test.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("summary.json", json.dumps({"version": "test"}))
                archive.writestr("../foreign.txt", "untrusted")
                archive.writestr("frames.jsonl", '{"windowId":1,"time":1}\n')
            data = report.read_report(path)
            self.assertEqual(set(data), {"summary.json", "frames.jsonl"})
            self.assertFalse((Path(temp).parent / "foreign.txt").exists())

    def test_missing_summary_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "test.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("frames.jsonl", "")
            with self.assertRaisesRegex(ValueError, "Missing summary"):
                report.read_report(path)

    def test_partial_checkpoint_reconstructs_window_membership(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "report.partial.jsonl"
            rows = [{"kind": "checkpoint", "metadata": {"version": "test"}},
                    {"kind": "frame", "frameId": 5, "botId": 2, "time": 1, "stage": "after_lock"},
                    {"kind": "window", "windowId": 3, "botId": 2, "frameIds": [5]},
                    {"kind": "checkpointSummary", "bytes": 100}]
            path.write_text("\n".join(json.dumps(r) for r in rows), encoding="utf-8")
            data = report.read_report(path)
            self.assertTrue(data["summary.json"]["recoveredCheckpoint"])
            self.assertEqual(report.analyze(data)["windowRows"], {"3": 1})


if __name__ == "__main__":
    unittest.main()
