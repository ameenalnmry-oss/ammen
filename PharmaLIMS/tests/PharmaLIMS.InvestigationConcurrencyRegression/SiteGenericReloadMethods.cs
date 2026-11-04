#nullable disable
using System.Data;
internal static partial class SiteGenericReloadProbe
{private sealed class Actual : Adapter
{private void ReloadInvestigationTablesForValidation()
        {
            string expected = pendingSavedInvestigationVersion;
            pendingSavedInvestigationVersion = null;
            loadedInvestigationVersion = null;
            if (string.IsNullOrEmpty(expected))
                throw new DBConcurrencyException("Reload the complete investigation before validation.");
            string before = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, false);
            if (!string.Equals(expected, before, StringComparison.Ordinal))
                throw new DBConcurrencyException("The investigation changed after saving. Reload before validation.");
            DataTable affected = DatabaseHelper.GetQualityEventAffectedResults(qualityEventId)
                ?? throw new InvalidOperationException("Affected results were not loaded.");
            DataTable actions = DatabaseHelper.GetQualityEventActions(qualityEventId)
                ?? throw new InvalidOperationException("Actions were not loaded.");
            checklistTable = GetFilteredChecklist() ?? throw new InvalidOperationException("Checklist was not loaded.");
            rootCauseWhysTable = LoadRootCauseWhysTable() ?? throw new InvalidOperationException("Root cause was not loaded.");
            impactAssessmentTable = LoadImpactAssessmentTable() ?? throw new InvalidOperationException("Impact assessment was not loaded.");
            capaItemsTable = LoadCAPAItemsTable() ?? throw new InvalidOperationException("CAPA was not loaded.");
            retestingTable = LoadRetestingTable() ?? throw new InvalidOperationException("Retesting was not loaded.");
            distributionTable = LoadDistributionTable() ?? throw new InvalidOperationException("Distribution was not loaded.");
            string after = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, false);
            if (!string.Equals(expected, after, StringComparison.Ordinal))
                throw new DBConcurrencyException("The investigation changed while validating. Reload before continuing.");
            EnsureEnvironmentalMonitoringStructuredDefaults();
            dgAffectedResults.ItemsSource = affected.DefaultView;
            dgActions.ItemsSource = actions.DefaultView;
            dgChecklist.ItemsSource = checklistTable.DefaultView;
            dgRootCauseWhys.ItemsSource = rootCauseWhysTable.DefaultView;
            dgImpactAssessment.ItemsSource = impactAssessmentTable.DefaultView;
            dgCAPAItems.ItemsSource = capaItemsTable.DefaultView;
            dgRetesting.ItemsSource = retestingTable.DefaultView;
            dgDistribution.ItemsSource = distributionTable.DefaultView;
            loadedInvestigationVersion = expected;
        }
internal override void Reload()=>ReloadInvestigationTablesForValidation();
}
}
