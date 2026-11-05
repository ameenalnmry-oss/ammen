import hashlib
import json
import unittest
from pathlib import Path

MIGRATION = Path(__file__).resolve().parents[1] / 'Database' / 'Migrations' / '20261010_002_Micro_Equipment_Activity_Ledger.sql'

class MicroEquipmentActivityContractTests(unittest.TestCase):
    def test_issued_v304_ledger_migration_identity_is_preserved(self):
        # Exact SQL bytes supplied in both reviewed project archives. Operational
        # UAT alignment requires this recorded hash before any source/database write.
        issued_hash = '3115438b20551dbb63562711ffe097ebadd4fa64798832df870019d3d33f9e91'
        self.assertEqual(issued_hash, hashlib.sha256(MIGRATION.read_bytes()).hexdigest())
        manifest = json.loads((MIGRATION.parents[1] / 'MigrationManifest.json').read_text(encoding='utf-8'))
        entry = next(item for item in manifest['migrations'] if item['versionKey'] == '20261010_002')
        self.assertEqual(issued_hash, entry['sha256'])

    def test_schema_and_protective_constraints(self):
        sql = MIGRATION.read_text(encoding='utf-8')
        for name in ('MicroEquipmentActivities', 'MicroEquipmentActivityLinks', 'MicroEquipmentActivityAudit', 'MicroEquipmentActivitySignatures'):
            self.assertIn('dbo.' + name, sql)
        self.assertIn('ROWVERSION NOT NULL', sql)
        self.assertIn('FK_MicroEquipmentActivities_Equipment', sql)
        self.assertIn('FK_MicroEquipmentActivityLinks_Activity', sql)
        self.assertIn('TRG_MicroEquipmentActivityAudit_AppendOnly', sql)
        self.assertIn('TRG_MicroEquipmentActivitySignatures_AppendOnly', sql)

    def test_preserves_old_contract(self):
        sql = MIGRATION.read_text(encoding='utf-8').upper()
        for forbidden in ('DROP TABLE DBO.LABEQUIPMENT', 'TRUNCATE TABLE', 'DELETE FROM DBO.LABEQUIPMENTUSAGE'):
            self.assertNotIn(forbidden, sql)
        self.assertIn('REFERENCES DBO.LABEQUIPMENT(EQUIPMENTID)', sql)

    def test_scope_and_multiple_activity_links(self):
        sql = MIGRATION.read_text(encoding='utf-8').upper()
        self.assertIn('MIC-EQ-[0-9][0-9][0-9]', sql)
        self.assertIn('MICROBIOLOGY', sql)
        self.assertIn('MICROEQUIPMENTACTIVITYLINKS', sql)
        self.assertNotIn('UNIQUE(MODULE,RESULTRECORDID)', sql.replace(' ', ''))

if __name__ == '__main__':
    unittest.main()
