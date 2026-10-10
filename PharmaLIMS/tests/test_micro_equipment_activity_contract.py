import unittest
from pathlib import Path

MIGRATION = Path(__file__).resolve().parents[1] / 'Database' / 'Migrations' / '20261010_002_Micro_Equipment_Activity_Ledger.sql'

class MicroEquipmentActivityContractTests(unittest.TestCase):
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
        self.assertIn('MIC-EQ-%', sql)
        self.assertIn('MICROEQUIPMENTACTIVITYLINKS', sql)
        self.assertNotIn('UNIQUE(MODULE,RESULTRECORDID)', sql.replace(' ', ''))

if __name__ == '__main__':
    unittest.main()
