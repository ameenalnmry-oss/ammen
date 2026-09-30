"""Exercise the actual EM SQL predicates in a portable in-memory fixture.

SQL Server execution and WPF acceptance remain separate release gates.
"""
from pathlib import Path
import re
import sqlite3
import unittest

ROOT = Path(__file__).resolve().parents[1]
TODAY = "2026-09-30"


def source(name):
    return (ROOT / name).read_text(encoding="utf-8-sig")


def literal_after(code, marker):
    part = code.split(marker, 1)[1]
    return re.search(r'@"((?:""|[^"])*)"', part).group(1).replace('""', '"')


def portable(sql):
    sql = re.sub(r"SELECT TOP\s*\(?1\)?", "SELECT", sql, flags=re.I)
    sql = sql.replace("dbo.", "")
    sql = re.sub(r" WITH\s*\(UPDLOCK,\s*HOLDLOCK\)", "", sql, flags=re.I)
    sql = re.sub(r"CAST\((GETDATE|SYSDATETIME)\(\) AS DATE\)", "'" + TODAY + "'", sql, flags=re.I)
    return sql.replace("N'", "'").replace("ISNULL(", "IFNULL(")


class PreparedMediaEligibilityTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.addCleanup(self.db.close)
        self.db.executescript("""
CREATE TABLE MediaPreparations(MediaPreparationID INTEGER, MediaPreparationNo TEXT,
    MediaID INTEGER, MediaLotID INTEGER, ReleaseStatus TEXT, SterilityReview TEXT, ExpiryDate TEXT);
CREATE TABLE CultureMedia(MediaID INTEGER, MediaCode TEXT, MediaName TEXT);
CREATE TABLE CultureMediaLots(MediaLotID INTEGER);
CREATE TABLE EM_PlanSamples(PlanSampleID INTEGER, PlanID INTEGER, SampleCode TEXT, MediaPreparationID INTEGER);
CREATE TABLE EM_Schedules(MediaPreparationID INTEGER);
INSERT INTO CultureMedia VALUES(1,'TSA','Tryptic Soy Agar');
INSERT INTO CultureMediaLots VALUES(1);
INSERT INTO MediaPreparations VALUES(1,'MP-1',1,1,'Released',NULL,'2026-10-01');
INSERT INTO EM_PlanSamples VALUES(1,1,'EM-1',1);
INSERT INTO EM_Schedules VALUES(1);
""")
        planning = source("EMPlanning.xaml.cs")
        self.plan_sql = portable(literal_after(planning, "private static int EnsureReleasedMediaPreparation("))
        self.collection_sql = portable(literal_after(planning, "using SqlCommand mediaGate ="))
        scheduler = source("EMPlanning.xaml.Part2.cs")
        exists = next(line.strip()[4:] for line in scheduler.splitlines() if line.strip().startswith("AND EXISTS(SELECT 1 FROM dbo.MediaPreparations"))
        self.schedule_sql = portable("SELECT 1 FROM dbo.EM_Schedules WHERE " + exists + ";")

    def assert_eligibility(self, eligible):
        plan = self.db.execute(self.plan_sql, {"Reference": "MP-1", "Media": "TSA"}).fetchall()
        scheduled = self.db.execute(self.schedule_sql).fetchall()
        blocked = self.db.execute(self.collection_sql, {"PlanID": 1}).fetchall()
        self.assertEqual(bool(plan), eligible, "Ad-hoc plan eligibility")
        self.assertEqual(bool(scheduled), eligible, "Scheduled plan eligibility")
        self.assertEqual(bool(blocked), not eligible, "Collection revalidation")

    def test_released_media_does_not_depend_on_legacy_sterility_field(self):
        for legacy in (None, "", "Pending", "Passed", "GPT PASSED", "Rejected"):
            with self.subTest(legacy=legacy):
                self.db.execute("UPDATE MediaPreparations SET SterilityReview=?", (legacy,))
                self.assert_eligibility(True)

    def test_unreleased_media_remains_blocked(self):
        for status in (None, "", "Under Release", "Rejected", "Quarantine"):
            with self.subTest(status=status):
                self.db.execute("UPDATE MediaPreparations SET ReleaseStatus=?,SterilityReview='Passed'", (status,))
                self.assert_eligibility(False)

    def test_expired_and_missing_expiry_remain_blocked(self):
        for expiry in (None, "2026-09-29"):
            with self.subTest(expiry=expiry):
                self.db.execute("UPDATE MediaPreparations SET ExpiryDate=?", (expiry,))
                self.assert_eligibility(False)

    def test_expiry_day_is_inclusive_and_status_is_normalized(self):
        self.db.execute("UPDATE MediaPreparations SET ExpiryDate=?,ReleaseStatus=' released '", (TODAY,))
        self.assert_eligibility(True)

    def test_missing_preparation_remains_blocked(self):
        self.db.execute("DELETE FROM MediaPreparations")
        self.assert_eligibility(False)

    def test_changed_release_is_rechecked_at_collection(self):
        self.assert_eligibility(True)
        self.db.execute("UPDATE MediaPreparations SET ReleaseStatus='Rejected'")
        self.assert_eligibility(False)

    def test_direct_registration_keeps_release_and_database_date_gates(self):
        code = source("NewSampleDialog.xaml.cs").split("private bool IsReleasedPreparedMedia(", 1)[1].split("private async Task SaveEnvironmentalEvent", 1)[0]
        self.assertIn('!releaseStatus.Equals("Released", StringComparison.OrdinalIgnoreCase)', code)
        self.assertIn("!expiryDate.HasValue || expiryDate.Value < databaseToday", code)
        self.assertIn("CAST(SYSDATETIME() AS date) AS DatabaseDate", code)
        self.assertNotIn("DateTime.Today", code)
        production = source("NewSampleDialog.xaml.cs").split("private async Task SaveEnvironmentalEvent", 1)[1]
        self.assertIn("if (AppConfig.IsProduction)", production)
        self.assertIn("Direct EM event registration is disabled in Production.", production)

    def test_no_active_legacy_sterility_dependency_in_em_paths(self):
        for name in ("EMPlanning.xaml.cs", "EMPlanning.xaml.Part2.cs", "NewSampleDialog.xaml.cs"):
            with self.subTest(file=name):
                self.assertNotIn("SterilityReview", source(name))
                self.assertNotIn("sterility-approved", source(name))


if __name__ == "__main__":
    unittest.main()
