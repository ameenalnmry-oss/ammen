"""Source provenance gates, separate from actual Windows/SQL/WPF acceptance."""
import base64
import hashlib
import json
import os
from pathlib import Path
import unittest

ROOT = Path(os.environ.get('EQ001_COHORT_REVIEW_DATA', Path(__file__).resolve().parents[1]))
PLAN = ROOT/'scripts/site-cohort-inputs/AMEEN-cohort.json'
FROZEN = ROOT/'tests/fixtures/AMEEN-current-source-preservation-20261104.json'

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def normalized(raw):
    return (raw.decode('utf-8-sig').replace('\r\n','\n').rstrip('\r\n')+'\n').encode()

def artifact(entry):
    raw=(ROOT/entry['Artifact']).read_bytes()
    return base64.b64decode(raw.strip(),validate=True) if entry.get('Encoding')=='base64' else raw

class CurrentSitePreservation(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan=json.loads(PLAN.read_text(encoding='utf-8'))
        cls.frozen=json.loads(FROZEN.read_text(encoding='utf-8'))
        cls.inputs={x['Path']:x for x in cls.plan['Inputs']}
        cls.sidecars={x['Path']:x for x in cls.plan['AdditionalReviewInputs']}

    def test_repair_scope_retains_exact_current_raw_witnesses(self):
        changes=self.plan['SourceUpdateScope']
        self.assertEqual(set(self.frozen['RepairPaths']),{x['Path'] for x in changes})
        self.assertEqual((16,3),(sum(x['Operation']=='update' for x in changes),sum(x['Operation']=='add' for x in changes)))
        for entry in changes:
            with self.subTest(path=entry['Path']):
                captured=self.frozen['CapturedCurrentFiles'].get(entry['Path'])
                original=self.inputs[entry['Path']].get('OriginalRawSha256')
                expected=captured['RawSha256'] if captured else original
                self.assertEqual(expected,entry['ExpectedCurrentRawSha256'])
                self.assertEqual(entry['CandidateRawSha256'],sha(artifact(entry)))

    def test_current_source_and_database_additions_are_retained(self):
        for name,captured in self.frozen['CapturedCurrentFiles'].items():
            if name=='SystemPreflight.xaml.cs':
                continue
            with self.subTest(path=name):
                entry=(self.sidecars if name.startswith('scripts/') else self.inputs)[name]
                raw=artifact(entry)
                if name.startswith('Database/') or name.startswith('scripts/'):
                    self.assertEqual(captured['RawSha256'],sha(raw))
                else:
                    self.assertEqual(captured['NormalizedSha256'],sha(normalized(raw)))
                self.assertEqual(captured['RawSha256'],entry.get('CurrentSiteRawSha256',entry.get('ExactSha256')))

    def test_preflight_merge_changes_only_two_timestamp_expressions(self):
        expected=self.frozen['CurrentPreflight']
        for name in ['_lastReport.ExecutedAt','report.ExecutedAt']:
            before='{'+name+':yyyy-MM-dd HH:mm:ss}'
            after='{'+name+'.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'
            self.assertEqual(1,expected.count(before))
            expected=expected.replace(before,after)
        self.assertEqual(expected,normalized(artifact(self.inputs['SystemPreflight.xaml.cs'])).decode())

    def test_prior_migration_records_and_exact_sql_bytes_are_preserved(self):
        original=self.frozen['OriginalMigrationManifest']
        current=json.loads(artifact(self.inputs['Database/MigrationManifest.json']).decode('utf-8-sig'))
        self.assertEqual(original['freshInstallBaseline'],current['freshInstallBaseline'])
        self.assertEqual(original['migrations'],current['migrations'][:-1])
        self.assertEqual('20261029_001',current['migrations'][-1]['versionKey'])
        self.assertEqual(103,1+len(current['migrations']))
        for entry in [current['freshInstallBaseline']]+current['migrations']:
            with self.subTest(version=entry['versionKey']):
                item=self.inputs['Database/'+entry['file']]
                self.assertEqual(entry['sha256'].lower(),item['ExactSha256'])

    def test_review_sql_is_data_only_and_current_build_inputs_are_explicit(self):
        self.assertEqual((370,243,231),(self.plan['InputCount'],self.plan['CurrentSiteBuildInputCount'],self.plan['OriginalBuildInputCount']))
        self.assertEqual({'PharmaLIMS.csproj':206,'tools/PharmaLIMS.TrustedSigning/PharmaLIMS.TrustedSigning.csproj':25,
                          'tools/PharmaLIMS.TrustedSigning.Host/PharmaLIMS.TrustedSigning.Host.csproj':10},
                         {k:len(v) for k,v in self.plan['BuildGraphs'].items()})
        self.assertEqual(10,len(self.sidecars))
        for entry in self.sidecars.values():
            self.assertIs(False,entry['Execute'])
            self.assertEqual(entry['ExactSha256'],sha(artifact(entry)))
            self.assertNotIn(entry['Path'],self.inputs)
        self.assertIs(False,self.plan['DeploymentAccepted'])

if __name__=='__main__':
    unittest.main()
