import os
import runpy
import unittest
from pathlib import Path
from unittest.mock import patch


class FixtureTests(unittest.TestCase):
    def load(self, scenario):
        with patch.dict(os.environ, {"HN_SMOKE_SCENARIO": scenario}):
            return runpy.run_path(str(Path(__file__).with_name("fixture.py")))["ITEMS"]

    def test_smoke_is_unchanged(self):
        items = self.load("Smoke")
        self.assertEqual(list(items), [101, 103, 102, 104])
        self.assertEqual([items[key]["score"] for key in items], [5, 150, 150, 900])
        self.assertEqual(items[104]["title"], "Highest score, last upstream")

    def test_browser_order_and_ties(self):
        items = self.load("Browser")
        self.assertEqual(list(items), list(range(225, 200, -1)))
        ordered = sorted(items.values(), key=lambda item: (-item["score"], item["id"]))
        self.assertEqual([item["id"] for item in ordered], list(range(201, 226)))
        for index, item in enumerate(ordered):
            self.assertEqual(item["score"], 1000 - index // 2 * 10)
            self.assertEqual(item["url"], f'https://fixture.invalid/{item["id"]}')
            self.assertEqual(item["descendants"], index + 1)
        self.assertEqual(self.load("Smoke")[104]["score"], 900)

    def test_invalid_scenario_fails(self):
        with self.assertRaises(ValueError):
            self.load("Typo")


if __name__ == "__main__":
    unittest.main()