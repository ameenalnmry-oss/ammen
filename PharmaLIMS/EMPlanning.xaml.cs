using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Printing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PharmaLIMS
{
    public partial class EMPlanning : Window
    {
        private readonly ObservableCollection<AreaChoice> _areas = new();
        private readonly ObservableCollection<PlanRow> _plans = new();
        private readonly ObservableCollection<PlanSampleRow> _samples = new();
        private readonly ObservableCollection<ScheduleRow> _schedules = new();
        private readonly ObservableCollection<WaterPointChoice> _waterPoints = new();
        private readonly ObservableCollection<WaterPlanRow> _waterPlans = new();
        private readonly ObservableCollection<WaterPlanSampleRow> _waterSamples = new();
        private bool _initialLoadInProgress;

        public EMPlanning()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _initialLoadInProgress = true;
            Mouse.OverrideCursor = Cursors.Wait;
            IsEnabled = false;
            try
            {
                lblUser.Text = $"{CurrentUser()}  |  {CurrentRole()}";
                cboPlanType.SelectedIndex = 0;
                cboSource.SelectedIndex = 0;
                cboFrequency.SelectedIndex = 0;
                dpLogin.SelectedDate = DateTime.Today;
                dpDue.SelectedDate = DateTime.Today;
                dpRequired.SelectedDate = DateTime.Today.AddDays(1);
                dpScheduleNext.SelectedDate = DateTime.Today;
                chkNegativeControl.IsChecked = true;
                chkNegativeControl.IsEnabled = false;
                cboWaterType.SelectedIndex = 0;
                cboWaterSource.SelectedIndex = 0;
                cboWaterFrequency.SelectedIndex = 1;
                dpWaterDue.SelectedDate = DateTime.Today;
                dpWaterRequired.SelectedDate = DateTime.Today.AddDays(1);

                string waterType = SelectedText(cboWaterType);
                InitialPlanningData data = await Task.Run(() => QueryInitialPlanningData(waterType));
                ApplyMethods(data.Methods);
                ApplyAreas(data.Areas);
                ApplyPlans(data.Plans);
                ApplySchedules(data.Schedules);
                ApplyWaterPoints(data.WaterPoints);
                ApplyWaterPlans(data.WaterPlans);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("EM Planning startup failed.", ex);
                MessageBox.Show(
                    "The planning workspace could not finish loading. Check the database connection and application log, then retry.",
                    "EM Planning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _initialLoadInProgress = false;
                IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private static InitialPlanningData QueryInitialPlanningData(string waterType)
        {
            return new InitialPlanningData
            {
                Methods = QueryMethods(),
                Areas = QueryAreas(),
                Plans = QueryPlans(),
                Schedules = QuerySchedules(),
                WaterPoints = QueryWaterPoints(waterType),
                WaterPlans = QueryWaterPlans()
            };
        }

        private void WaterType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _initialLoadInProgress) return;

            try
            {
                ApplyControlledWaterFrequency();
                LoadWaterPoints();
            }
            catch (Exception ex)
            {
                ShowWaterError(ex, "Load water sampling points");
            }
        }

        private void ApplyControlledWaterFrequency()
        {
            if (cboWaterType == null || cboWaterFrequency == null)
                return;

            string waterType = SelectedText(cboWaterType);
            cboWaterFrequency.SelectedIndex = waterType.Equals("Purified", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        private void LoadWaterPoints()
        {
            if (cboWaterType == null) return;
            ApplyWaterPoints(QueryWaterPoints(SelectedText(cboWaterType)));
        }

        private static DataTable QueryWaterPoints(string type)
        {
            return DatabaseHelper.ExecuteQuery(@"SELECT Id,PointCode,PointName,ISNULL(Location,N'') Location
FROM dbo.WaterSamplingPoints WHERE ISNULL(Status,N'')=N'Active' AND WaterType=@Type ORDER BY PointCode;",
                new[] { new SqlParameter("@Type", type) }, commandTimeoutSeconds: 10);
        }

        private void ApplyWaterPoints(DataTable table)
        {
            _waterPoints.Clear();
            foreach (DataRow row in table.Rows) _waterPoints.Add(new WaterPointChoice(row));
            gridWaterPoints.ItemsSource = _waterPoints;
        }

        private void SelectAllWaterPoints_Click(object sender, RoutedEventArgs e)
        {
            foreach (WaterPointChoice point in _waterPoints)
                point.IsSelected = true;
            gridWaterPoints.Items.Refresh();
        }

        private void ClearWaterPoints_Click(object sender, RoutedEventArgs e)
        {
            foreach (WaterPointChoice point in _waterPoints)
                point.IsSelected = false;
            gridWaterPoints.Items.Refresh();
        }

        private void CreateWaterPlan_Click(object sender, RoutedEventArgs e) => ExecuteWaterUi(CreateWaterPlan, "Create water plan");

        private void CreateWaterPlan()
        {
            EnsureWaterPlanningSchemaReady();
            ApplyControlledWaterFrequency();

            gridWaterPoints.CommitEdit(DataGridEditingUnit.Cell, true);
            gridWaterPoints.CommitEdit(DataGridEditingUnit.Row, true);

            List<WaterPointChoice> points = _waterPoints.Where(p => p.IsSelected).ToList();
            if (points.Count == 0)
                throw new InvalidOperationException("Select at least one water sampling point.");

            if (!dpWaterDue.SelectedDate.HasValue || !dpWaterRequired.SelectedDate.HasValue)
                throw new InvalidOperationException("Collection Due Date and Analysis Required Date are mandatory.");

            DateTime dueDate = dpWaterDue.SelectedDate.Value.Date;
            DateTime requiredDate = dpWaterRequired.SelectedDate.Value.Date;
            if (requiredDate < dueDate)
                throw new InvalidOperationException("Analysis Required Date must be on or after Collection Due Date.");

            string waterType = SelectedText(cboWaterType);
            string sourceType = SelectedText(cboWaterSource);
            string frequency = SelectedText(cboWaterFrequency);
            string requiredFrequency = waterType.Equals("Purified", StringComparison.OrdinalIgnoreCase)
                ? "Fortnightly"
                : "Monthly";

            if (!frequency.Equals(requiredFrequency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{waterType} water must use the controlled {requiredFrequency} frequency.");

            if (string.IsNullOrWhiteSpace(sourceType))
                throw new InvalidOperationException("Select the water plan source before creating the plan.");
            if (!sourceType.Equals("Routine", StringComparison.OrdinalIgnoreCase) &&
                !sourceType.Equals("Ad-hoc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Water Plan source must be Routine or Ad-hoc. 'Scheduled' is reserved for a future approved schedule-master workflow and cannot be assigned manually.");

            ElectronicSignature signature = ConfirmSignature("NEW WATER PLAN", "Create Water Collection Plan")
                ?? throw new OperationCanceledException();

            int planId = 0;
            string planNo = string.Empty;
            string stage = "plan numbering";

            try
            {
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    stage = "plan numbering";
                    planNo = GetNextWaterPlanNo(connection, transaction);

                    stage = "plan header insert";
                    using (SqlCommand plan = new SqlCommand(@"
INSERT dbo.Water_Plans
    (PlanNo,WaterType,SourceType,Frequency,DueDate,RequiredDate,AnalysisProfile,Status,Notes,CreatedBy)
OUTPUT INSERTED.WaterPlanID
VALUES
    (@No,@Type,@Source,@Frequency,@Due,@Required,@Profile,N'Planned',@Notes,@User);", connection, transaction))
                    {
                        plan.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        plan.Parameters.Add("@No", SqlDbType.NVarChar, 50).Value = planNo;
                        plan.Parameters.Add("@Type", SqlDbType.NVarChar, 30).Value = waterType;
                        plan.Parameters.Add("@Source", SqlDbType.NVarChar, 20).Value = sourceType;
                        plan.Parameters.Add("@Frequency", SqlDbType.NVarChar, 30).Value = frequency;
                        plan.Parameters.Add("@Due", SqlDbType.Date).Value = dueDate;
                        plan.Parameters.Add("@Required", SqlDbType.Date).Value = requiredDate;
                        plan.Parameters.Add("@Profile", SqlDbType.NVarChar, 40).Value = waterType.Equals("Purified", StringComparison.OrdinalIgnoreCase) ? "PW" : "PTW";
                        plan.Parameters.Add("@Notes", SqlDbType.NVarChar, -1).Value = Db(txtWaterNotes.Text);
                        plan.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;

                        object? insertedId = plan.ExecuteScalar();
                        if (insertedId == null || insertedId == DBNull.Value)
                            throw new InvalidOperationException("Water plan creation did not return a valid plan identifier. No success should be assumed.");

                        planId = Convert.ToInt32(insertedId, CultureInfo.InvariantCulture);
                        if (planId <= 0)
                            throw new InvalidOperationException("Water plan creation returned an invalid plan identifier. No success should be assumed.");
                    }

                    stage = "sampling point insert";
                    foreach (WaterPointChoice point in points)
                    {
                        using SqlCommand sample = new SqlCommand(@"
INSERT dbo.Water_PlanSamples
    (WaterPlanID,PointID,PointCode,PointName,Location,AnalysisProfile,Status)
VALUES
    (@Plan,@Point,@Code,@Name,@Location,@Profile,N'Planned');", connection, transaction)
                        {
                            CommandTimeout = AppConfig.CommandTimeoutSeconds
                        };
                        sample.Parameters.Add("@Plan", SqlDbType.Int).Value = planId;
                        sample.Parameters.Add("@Point", SqlDbType.Int).Value = point.Id;
                        sample.Parameters.Add("@Code", SqlDbType.NVarChar, 50).Value = point.PointCode;
                        sample.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = Db(point.PointName);
                        sample.Parameters.Add("@Location", SqlDbType.NVarChar, 300).Value = Db(point.Location);
                        sample.Parameters.Add("@Profile", SqlDbType.NVarChar, 40).Value = waterType.Equals("Purified", StringComparison.OrdinalIgnoreCase) ? "PW" : "PTW";
                        sample.ExecuteNonQuery();
                    }

                    stage = "electronic signature insert";
                    InsertWaterSignature(connection, transaction, planId, "Plan Created", signature);

                    stage = "audit trail insert";
                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction,
                        "Water_Plans", planId, "Water Plan Created", "", "Planned",
                        signature.Reason, signature.SignedBy,
                        "Status", null, planNo, "Water");
                });
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Water plan creation failed during {stage}.", ex);
                throw;
            }

            foreach (WaterPointChoice point in points)
                point.IsSelected = false;
            gridWaterPoints.Items.Refresh();

            MessageBox.Show(
                $"Water plan created successfully.\n\n{planNo}\nPoints: {points.Count}",
                "Water Planning",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            try
            {
                LoadWaterPlans();
            }
            catch (Exception refreshException)
            {
                ApplicationLogger.Warning(
                    $"Water plan {planNo} was created, but the Water Queue could not be refreshed immediately.",
                    refreshException);
                MessageBox.Show(
                    "The water plan was created, but the Water Queue could not refresh. Use Refresh before creating another plan.",
                    "Water Planning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static void EnsureWaterPlanningSchemaReady()
        {
            DataTable missing = DatabaseHelper.ExecuteQuery(@"
DECLARE @Missing TABLE(Item nvarchar(200) NOT NULL);

IF OBJECT_ID(N'dbo.Water_Plans',N'U') IS NULL
    INSERT @Missing VALUES(N'Water plan header table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Water_Plans',N'WaterPlanID') IS NULL INSERT @Missing VALUES(N'Water plan identifier');
    IF COL_LENGTH(N'dbo.Water_Plans',N'PlanNo') IS NULL INSERT @Missing VALUES(N'Water plan number');
    IF COL_LENGTH(N'dbo.Water_Plans',N'WaterType') IS NULL INSERT @Missing VALUES(N'Water type');
    IF COL_LENGTH(N'dbo.Water_Plans',N'SourceType') IS NULL INSERT @Missing VALUES(N'Water source');
    IF COL_LENGTH(N'dbo.Water_Plans',N'Frequency') IS NULL INSERT @Missing VALUES(N'Water frequency');
    IF COL_LENGTH(N'dbo.Water_Plans',N'DueDate') IS NULL INSERT @Missing VALUES(N'Water due date');
    IF COL_LENGTH(N'dbo.Water_Plans',N'RequiredDate') IS NULL INSERT @Missing VALUES(N'Water required date');
    IF COL_LENGTH(N'dbo.Water_Plans',N'AnalysisProfile') IS NULL INSERT @Missing VALUES(N'Water analysis profile');
    IF COL_LENGTH(N'dbo.Water_Plans',N'Status') IS NULL INSERT @Missing VALUES(N'Water plan status');
    IF COL_LENGTH(N'dbo.Water_Plans',N'Notes') IS NULL INSERT @Missing VALUES(N'Water plan notes');
    IF COL_LENGTH(N'dbo.Water_Plans',N'CreatedBy') IS NULL INSERT @Missing VALUES(N'Water plan creator');
END;

IF OBJECT_ID(N'dbo.Water_PlanSamples',N'U') IS NULL
    INSERT @Missing VALUES(N'Water plan sample table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'WaterPlanSampleID') IS NULL INSERT @Missing VALUES(N'Water plan sample identifier');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'WaterPlanID') IS NULL INSERT @Missing VALUES(N'Water plan sample link');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'PointID') IS NULL INSERT @Missing VALUES(N'Water sampling point link');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'PointCode') IS NULL INSERT @Missing VALUES(N'Water sampling point code');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'PointName') IS NULL INSERT @Missing VALUES(N'Water sampling point name');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'Location') IS NULL INSERT @Missing VALUES(N'Water sampling point location');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'AnalysisProfile') IS NULL INSERT @Missing VALUES(N'Water sample analysis profile');
    IF COL_LENGTH(N'dbo.Water_PlanSamples',N'Status') IS NULL INSERT @Missing VALUES(N'Water plan sample status');
END;

IF OBJECT_ID(N'dbo.Water_PlanSampleTests',N'U') IS NULL
    INSERT @Missing VALUES(N'Water plan sample tests table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Water_PlanSampleTests',N'WaterPlanSampleID') IS NULL INSERT @Missing VALUES(N'Water plan sample test link');
    IF COL_LENGTH(N'dbo.Water_PlanSampleTests',N'TestID') IS NULL INSERT @Missing VALUES(N'Water plan controlled test identifier');
END;

IF OBJECT_ID(N'dbo.Water_PlanSampleAttempts',N'U') IS NULL
    INSERT @Missing VALUES(N'Water plan sample attempts table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Water_PlanSampleAttempts',N'WaterPlanSampleID') IS NULL INSERT @Missing VALUES(N'Water attempt plan sample link');
    IF COL_LENGTH(N'dbo.Water_PlanSampleAttempts',N'SampleID') IS NULL INSERT @Missing VALUES(N'Water attempt sample link');
    IF COL_LENGTH(N'dbo.Water_PlanSampleAttempts',N'SampleNumber') IS NULL INSERT @Missing VALUES(N'Water attempt sample number');
    IF COL_LENGTH(N'dbo.Water_PlanSampleAttempts',N'Outcome') IS NULL INSERT @Missing VALUES(N'Water attempt outcome');
END;

IF OBJECT_ID(N'dbo.Water_PlanSignatures',N'U') IS NULL
    INSERT @Missing VALUES(N'Water plan signature table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'WaterPlanID') IS NULL INSERT @Missing VALUES(N'Water signature plan link');
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'ActionType') IS NULL INSERT @Missing VALUES(N'Water signature action');
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'ActionReason') IS NULL INSERT @Missing VALUES(N'Water signature reason');
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'SignedBy') IS NULL INSERT @Missing VALUES(N'Water signer');
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'UserRole') IS NULL INSERT @Missing VALUES(N'Water signer role');
    IF COL_LENGTH(N'dbo.Water_PlanSignatures',N'MeaningOfSignature') IS NULL INSERT @Missing VALUES(N'Water signature meaning');
END;

IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
    INSERT @Missing VALUES(N'User table');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.Users',N'Username') IS NULL INSERT @Missing VALUES(N'Username');
    IF COL_LENGTH(N'dbo.Users',N'IsActive') IS NULL INSERT @Missing VALUES(N'User active status');
    IF COL_LENGTH(N'dbo.Users',N'Role') IS NULL INSERT @Missing VALUES(N'User role');
END;

SELECT Item FROM @Missing ORDER BY Item;", commandTimeoutSeconds: 10);

            if (missing.Rows.Count == 0)
                return;

            string details = string.Join(", ", missing.Rows.Cast<DataRow>()
                .Select(row => Convert.ToString(row["Item"], CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            ApplicationLogger.Warning("Water planning schema readiness failed. Missing or incomplete items: " + details);
            throw new InvalidOperationException(
                "Water planning database prerequisites are incomplete. Run System Preflight and Database Maintenance before creating or changing a water plan.");
        }

        private static string GetNextWaterPlanNo(SqlConnection connection, SqlTransaction transaction)
        {
            using SqlCommand dateCommand = new SqlCommand("SELECT CONVERT(char(8), SYSDATETIME(), 112);", connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            string dateKey = Convert.ToString(dateCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            if (dateKey.Length != 8 || !dateKey.All(char.IsDigit))
                throw new InvalidOperationException("SQL Server did not return a valid Water Plan date key.");

            string prefix = "WPL-" + dateKey + "-";
            using SqlCommand lockCommand = new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'PharmaLIMS-WaterPlanNo',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=10000; IF @r<0 THROW 51031,'Unable to acquire water plan numbering lock.',1;", connection, transaction)
            {
                CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 15)
            };
            lockCommand.ExecuteNonQuery();

            using SqlCommand command = new SqlCommand("SELECT ISNULL(MAX(TRY_CONVERT(int,RIGHT(PlanNo,4))),0)+1 FROM dbo.Water_Plans WITH(UPDLOCK,HOLDLOCK) WHERE PlanNo LIKE @Prefix+'%';", connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 32).Value = prefix;
            int nextNumber = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (nextNumber < 1 || nextNumber > 9999)
                throw new InvalidOperationException("The Water Plan daily number sequence is outside the supported range.");

            return prefix + nextNumber.ToString("0000", CultureInfo.InvariantCulture);
        }

        private void LoadWaterPlans()
        {
            ApplyWaterPlans(QueryWaterPlans());
        }

        private static DataTable QueryWaterPlans()
        {
            return DatabaseHelper.ExecuteQuery(@"SELECT P.WaterPlanID,P.PlanNo,P.WaterType,P.SourceType,ISNULL(P.Frequency,N'') Frequency,P.DueDate,P.RequiredDate,P.Status,ISNULL(P.Notes,N'') Notes,
COUNT(S.WaterPlanSampleID) PointCount,SUM(CASE WHEN S.SampleID IS NOT NULL THEN 1 ELSE 0 END) RegisteredCount
FROM dbo.Water_Plans P LEFT JOIN dbo.Water_PlanSamples S ON S.WaterPlanID=P.WaterPlanID
GROUP BY P.WaterPlanID,P.PlanNo,P.WaterType,P.SourceType,P.Frequency,P.DueDate,P.RequiredDate,P.Status,P.Notes
ORDER BY CASE WHEN P.Status IN(N'Cancelled',N'Completed') THEN 1 ELSE 0 END,P.DueDate DESC,P.WaterPlanID DESC;", commandTimeoutSeconds: 10);
        }

        private void ApplyWaterPlans(DataTable table)
        {
            _waterPlans.Clear();
            foreach (DataRow row in table.Rows) _waterPlans.Add(new WaterPlanRow(row));
            gridWaterPlans.ItemsSource = _waterPlans;
        }

        private void WaterPlans_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridWaterPlans.SelectedItem is WaterPlanRow plan) LoadWaterSamples(plan.WaterPlanID);
        }

        private void LoadWaterSamples(int planId)
        {
            _waterSamples.Clear();
            DataTable table = DatabaseHelper.ExecuteQuery(@"SELECT ps.WaterPlanSampleID,ps.WaterPlanID,ps.PointID,ps.PointCode,ISNULL(ps.PointName,N'') PointName,ISNULL(ps.Location,N'') Location,ISNULL(ps.AnalysisProfile,N'Full') AnalysisProfile,ISNULL(x.TestNames,N'') TestNames,ps.Status,ps.SampleID,ISNULL(ps.SampleNumber,N'') SampleNumber
FROM dbo.Water_PlanSamples ps OUTER APPLY(SELECT STRING_AGG(t.TestName,N', ') WITHIN GROUP(ORDER BY t.SortOrder,t.TestName) TestNames FROM dbo.Water_PlanSampleTests pt INNER JOIN dbo.Tests t ON t.TestID=pt.TestID WHERE pt.WaterPlanSampleID=ps.WaterPlanSampleID) x
WHERE ps.WaterPlanID=@Plan ORDER BY ps.PointCode;", new[] { new SqlParameter("@Plan", planId) });
            foreach (DataRow row in table.Rows) _waterSamples.Add(new WaterPlanSampleRow(row));
            gridWaterSamples.ItemsSource = _waterSamples;
        }

        private void RefreshWater_Click(object sender, RoutedEventArgs e) { LoadWaterPlans(); _waterSamples.Clear(); }

        private void DistributeWater_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            WaterPlanRow plan = RequireWaterPlan("Planned");
            LoadWaterSamples(plan.WaterPlanID);
            if (_waterSamples.Any(s => string.IsNullOrWhiteSpace(s.TestNames)))
                throw new InvalidOperationException("Assign approved tests to every planned water sample before distribution. Use Select all, then Choose / Apply Tests.");
            ElectronicSignature signature = ConfirmSignature(plan.PlanNo, "Distribute Water Collection Plan") ?? throw new OperationCanceledException();
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureActiveUserInTransaction(connection, transaction, signature.SignedBy, "distribute a water collection plan");

                int plannedSampleCount;
                using (SqlCommand gate = new SqlCommand(@"
SELECT COUNT_BIG(S.WaterPlanSampleID) AS SampleCount,
       SUM(CASE WHEN S.WaterPlanSampleID IS NULL OR ISNULL(S.Status,N'Planned')=N'Planned' THEN 0 ELSE 1 END) AS NonPlannedCount
FROM dbo.Water_Plans P WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.Water_PlanSamples S WITH(UPDLOCK,HOLDLOCK) ON S.WaterPlanID=P.WaterPlanID
WHERE P.WaterPlanID=@Plan AND P.Status=N'Planned'
GROUP BY P.WaterPlanID;", connection, transaction))
                {
                    gate.Parameters.Add("@Plan", SqlDbType.Int).Value = plan.WaterPlanID;
                    using SqlDataReader reader = gate.ExecuteReader();
                    if (!reader.Read())
                        throw new DBConcurrencyException("The water plan changed after it was loaded. Refresh and retry.");
                    plannedSampleCount = Convert.ToInt32(reader["SampleCount"], CultureInfo.InvariantCulture);
                    int nonPlannedCount = reader["NonPlannedCount"] == DBNull.Value ? 0 : Convert.ToInt32(reader["NonPlannedCount"], CultureInfo.InvariantCulture);
                    if (plannedSampleCount <= 0)
                        throw new InvalidOperationException("A Water Plan cannot be distributed without planned sampling points.");
                    if (nonPlannedCount != 0)
                        throw new DBConcurrencyException("One or more Water Plan points changed before distribution. Refresh and retry.");
                }

                using (SqlCommand testGate = new SqlCommand(@"
SELECT COUNT_BIG(*)
FROM dbo.Water_PlanSamples S WITH(HOLDLOCK)
WHERE S.WaterPlanID=@Plan
  AND NOT EXISTS(SELECT 1 FROM dbo.Water_PlanSampleTests T WITH(HOLDLOCK) WHERE T.WaterPlanSampleID=S.WaterPlanSampleID);", connection, transaction))
                {
                    testGate.Parameters.Add("@Plan", SqlDbType.Int).Value = plan.WaterPlanID;
                    if (Convert.ToInt64(testGate.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new InvalidOperationException("Every planned Water point must have frozen approved tests before distribution.");
                }

                using (SqlCommand parent = new SqlCommand(@"
UPDATE dbo.Water_Plans
SET Status=N'Distributed',DistributedBy=@User,DistributedAt=SYSDATETIME()
WHERE WaterPlanID=@Plan AND Status=N'Planned';", connection, transaction))
                {
                    parent.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                    parent.Parameters.Add("@Plan", SqlDbType.Int).Value = plan.WaterPlanID;
                    if (parent.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("The water plan changed during distribution. No partial distribution was committed.");
                }

                using (SqlCommand children = new SqlCommand(@"
UPDATE dbo.Water_PlanSamples
SET Status=N'Distributed'
WHERE WaterPlanID=@Plan AND ISNULL(Status,N'Planned')=N'Planned';", connection, transaction))
                {
                    children.Parameters.Add("@Plan", SqlDbType.Int).Value = plan.WaterPlanID;
                    if (children.ExecuteNonQuery() != plannedSampleCount)
                        throw new DBConcurrencyException("The Water Plan point set changed during distribution. No partial distribution was committed.");
                }

                InsertWaterSignature(connection, transaction, plan.WaterPlanID, "Distributed", signature);
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction,
                    "Water_Plans", plan.WaterPlanID, "Water Plan Distributed", "Planned", "Distributed",
                    signature.Reason, signature.SignedBy,
                    "Status", null, plan.PlanNo, "Water");
            });
            LoadWaterPlans();
        });

        private void RegisterWaterSample_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            if (!plan.Status.Equals("Distributed", StringComparison.OrdinalIgnoreCase) &&
                !plan.Status.Equals("In Collection", StringComparison.OrdinalIgnoreCase) &&
                !plan.Status.Equals("Recollection Required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Distribute the plan before registering samples.");
            if (gridWaterSamples.SelectedItem is not WaterPlanSampleRow item) throw new InvalidOperationException("Select one sampling point.");
            if (item.SampleID.HasValue && !item.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This point is already linked to sample " + item.SampleNumber + ".");
            NewSampleDialog dialog = App.ServiceProvider!.GetRequiredService<NewSampleDialog>();
            dialog.Owner = this; dialog.PrepareWaterPlanRegistration(plan.WaterType, item.PointID, plan.DueDate, item.AnalysisProfile, item.WaterPlanSampleID);
            if (dialog.ShowDialog() != true || dialog.RegisteredSampleId <= 0) return;
            // NewSampleDialog commits the Samples row, SampleTests, Water_PlanSampleAttempts,
            // Water_PlanSamples linkage and Water_Plans status in one SQL transaction.
            LoadWaterPlans(); LoadWaterSamples(plan.WaterPlanID);
        });

        private void OpenWaterResults_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            ResultsEntry results = App.ServiceProvider!.GetRequiredService<ResultsEntry>(); results.Owner = this; results.Show();
            if (gridWaterSamples.SelectedItem is WaterPlanSampleRow item && item.SampleID.HasValue) results.LoadSample(item.SampleID.Value);
        });

        private void SelectAllWaterSamples_Changed(object sender, RoutedEventArgs e)
        {
            bool selected = chkSelectAllWaterSamples.IsChecked == true;
            foreach (WaterPlanSampleRow sample in _waterSamples) sample.IsSelected = selected;
            gridWaterSamples.Items.Refresh();
        }

        private void ApplyWaterTests_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            if (!plan.Status.Equals("Planned", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Water Plan tests are frozen after Distribution. Create a new controlled plan/revision instead of changing distributed instructions.");
            gridWaterSamples.CommitEdit(DataGridEditingUnit.Cell, true); gridWaterSamples.CommitEdit(DataGridEditingUnit.Row, true);
            List<WaterPlanSampleRow> selected = _waterSamples.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Check at least one planned sample, or use Select all.");
            if (selected.Any(s => s.SampleID.HasValue))
                throw new InvalidOperationException("Tests cannot be changed after sample registration. Uncheck registered/rejected samples and apply tests only to unregistered plan rows.");
            string profile = SelectedText(cboBulkWaterTests);
            if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("Select the tests profile.");
            DataTable allowed = DatabaseHelper.GetActiveWaterTestIdsForProfile(plan.WaterType == "Purified" ? "PW" : "PTW");
            List<int> allowedIds = allowed.AsEnumerable().Select(r => Convert.ToInt32(r["TestID"], CultureInfo.InvariantCulture)).Distinct().ToList();
            var testIds = profile == "Full" ? allowedIds.ToList() : new List<int>();
            if (profile != "Full" && allowedIds.Count > 0)
            {
                DataTable categories = DatabaseHelper.ExecuteQuery("SELECT TestID,ISNULL(TestCategory,N'') TestCategory FROM dbo.Tests WHERE TestID IN (" + string.Join(",", allowedIds) + ");");
                foreach (DataRow row in categories.Rows)
                {
                    string category = row["TestCategory"]?.ToString() ?? "";
                    if ((profile == "Microbiology" && category.Contains("micro", StringComparison.OrdinalIgnoreCase)) || (profile == "Physical / Chemical" && !category.Contains("micro", StringComparison.OrdinalIgnoreCase)))
                        testIds.Add(Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture));
                }
            }
            DataTable choicesTable = allowedIds.Count == 0 ? new DataTable() : DatabaseHelper.ExecuteQuery(
                "SELECT TestID,TestName,ISNULL(TestCategory,N'') TestCategory FROM dbo.Tests WHERE TestID IN (" + string.Join(",", allowedIds) + ") ORDER BY TestCategory,SortOrder,TestName;");
            var choices = choicesTable.AsEnumerable().Select(row => new WaterTestChoice
            {
                TestID = Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture),
                TestName = row["TestName"]?.ToString() ?? "",
                Category = row["TestCategory"]?.ToString() ?? "",
                IsSelected = testIds.Contains(Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture))
            }).ToList();
            var testDialog = new WaterTestSelectionDialog(choices) { Owner = this };
            if (testDialog.ShowDialog() != true) return;
            testIds = testDialog.SelectedTestIds.ToList();
            if (testIds.Count == 0) throw new InvalidOperationException("No approved tests were found for the selected profile.");
            string actor = CurrentUser();
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureActiveUserInTransaction(connection, transaction, actor, "assign tests to a water collection plan");
                using (SqlCommand statusLock = new SqlCommand("SELECT Status FROM dbo.Water_Plans WITH(UPDLOCK,HOLDLOCK) WHERE WaterPlanID=@Plan;", connection, transaction))
                {
                    statusLock.Parameters.Add("@Plan", SqlDbType.Int).Value = plan.WaterPlanID;
                    string lockedStatus = Convert.ToString(statusLock.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!lockedStatus.Equals("Planned", StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The Water Plan was distributed or changed while test assignment was open. No tests were changed.");
                }
                foreach (WaterPlanSampleRow sample in selected)
                {
                    using SqlCommand clear = new SqlCommand("DELETE dbo.Water_PlanSampleTests WHERE WaterPlanSampleID=@ID; UPDATE dbo.Water_PlanSamples SET AnalysisProfile=@Profile WHERE WaterPlanSampleID=@ID;", connection, transaction);
                    clear.Parameters.AddExplicit("@ID", SqlDbType.Int, sample.WaterPlanSampleID); clear.Parameters.AddExplicit("@Profile", SqlDbType.NVarChar, profile, size: 20); clear.ExecuteNonQuery();
                    foreach (int testId in testIds)
                    {
                        using SqlCommand add = new SqlCommand("INSERT dbo.Water_PlanSampleTests(WaterPlanSampleID,TestID) VALUES(@ID,@Test);", connection, transaction);
                        add.Parameters.AddExplicit("@ID", SqlDbType.Int, sample.WaterPlanSampleID); add.Parameters.AddExplicit("@Test", SqlDbType.Int, testId); add.ExecuteNonQuery();
                    }
                }
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction,
                    "Water_Plans", plan.WaterPlanID, "Bulk Water Tests Assigned", "", profile,
                    $"Applied {testIds.Count} approved tests to {selected.Count} planned samples", actor,
                    "AnalysisProfile", null, plan.PlanNo, "Water");
            });
            LoadWaterSamples(plan.WaterPlanID);
            MessageBox.Show($"{profile} tests applied to {selected.Count} samples.\nApproved tests per sample: {testIds.Count}", "Water Planning", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        private List<WaterPlanSampleRow> SelectedWaterSamples(bool requireSelection)
        {
            gridWaterSamples.CommitEdit(DataGridEditingUnit.Cell, true); gridWaterSamples.CommitEdit(DataGridEditingUnit.Row, true);
            List<WaterPlanSampleRow> selected = _waterSamples.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0 && !requireSelection) selected = _waterSamples.ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Select at least one planned sample.");
            return selected;
        }

        private void PrintWaterList_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            List<WaterPlanSampleRow> rows = SelectedWaterSamples(false);
            var document = new FlowDocument { PageWidth=793,PageHeight=1122,PagePadding=new Thickness(35),ColumnWidth=double.PositiveInfinity,FontFamily=new FontFamily("Segoe UI"),FontSize=9 };
            document.Blocks.Add(new Paragraph(new Run("WATER COLLECTION AND ANALYSIS LIST")) { FontSize=18,FontWeight=FontWeights.Bold,TextAlignment=TextAlignment.Center });
            document.Blocks.Add(new Paragraph(new Run($"Plan: {plan.PlanNo}   Water: {plan.WaterType}   Due: {plan.DueDate:yyyy-MM-dd}   Required: {plan.RequiredDate:yyyy-MM-dd}")));
            Table table = new Table { CellSpacing=0 }; foreach (double width in new[]{35d,90d,170d,180d,120d,110d}) table.Columns.Add(new TableColumn{Width=new GridLength(width)});
            var group = new TableRowGroup(); table.RowGroups.Add(group); AddPrintRow(group,true,"#","Point","Name","Location","Tests","Sample No.");
            int index=0; foreach (WaterPlanSampleRow row in rows) AddPrintRow(group,false,(++index).ToString(),row.PointCode,row.PointName,row.Location,string.IsNullOrWhiteSpace(row.TestNames)?row.AnalysisProfile:row.TestNames,row.SampleNumber);
            document.Blocks.Add(table); document.Blocks.Add(new Paragraph(new Run("Collected By: __________________   Reviewed By: __________________   Date/Time: __________________")){Margin=new Thickness(0,15,0,0)});
            PrintDialog dialog=new PrintDialog(); if(dialog.ShowDialog()==true) { dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,"Water Collection List "+plan.PlanNo); DatabaseHelper.AddAuditTrailAdvanced("Water_Plans",plan.WaterPlanID,"Water Collection List Printed","","Printed",$"Printed {rows.Count} planned samples",CurrentUser()); }
        });

        private void PrintWaterLabels_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            List<WaterPlanSampleRow> rows = SelectedWaterSamples(true);
            var document = new FlowDocument { PageWidth=793,PageHeight=1122,PagePadding=new Thickness(28),ColumnGap=12,ColumnWidth=350 };
            foreach (WaterPlanSampleRow row in rows)
            {
                string number=string.IsNullOrWhiteSpace(row.SampleNumber)?"PLANNED - NOT A RESULT | "+plan.PlanNo:row.SampleNumber;
                var block=new Section{BorderBrush=Brushes.Black,BorderThickness=new Thickness(1),Padding=new Thickness(10),Margin=new Thickness(0,0,0,10)};
                block.Blocks.Add(new Paragraph(new Run("PharmaLIMS | WATER SAMPLE")){FontWeight=FontWeights.Bold,FontSize=12,Margin=new Thickness(0)});
                block.Blocks.Add(new Paragraph(new Run(number)){FontFamily=new FontFamily("Consolas"),FontWeight=FontWeights.Bold,FontSize=16,Margin=new Thickness(0,5,0,3)});
                block.Blocks.Add(new Paragraph(new Run($"Point: {row.PointCode} - {row.PointName}\nLocation: {row.Location}\nWater: {plan.WaterType} | Tests: {row.AnalysisProfile}\nDue: {plan.DueDate:yyyy-MM-dd}")){FontSize=9,Margin=new Thickness(0)});
                document.Blocks.Add(block);
            }
            PrintDialog dialog=new PrintDialog(); if(dialog.ShowDialog()==true) { dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,"Water Labels "+plan.PlanNo); DatabaseHelper.AddAuditTrailAdvanced("Water_Plans",plan.WaterPlanID,"Water Labels Printed","","Printed",$"Printed {rows.Count} labels",CurrentUser()); }
        });

        private void CancelWaterPlan_Click(object sender, RoutedEventArgs e) => ExecuteUi(() =>
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            if (plan.Status is "Cancelled" or "Completed") throw new InvalidOperationException("This plan cannot be cancelled.");
            if (!DatabaseHelper.CanApproveResults(CurrentUser()))
                throw new InvalidOperationException("Approval permission is required to cancel a water plan.");
            if (_waterSamples.Any(s => s.SampleID.HasValue) && MessageBox.Show(
                "This plan already contains registered water samples. Cancelling the plan will not cancel those controlled sample records. Continue?",
                "Cancel Water Plan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ElectronicSignature signature = ConfirmSignature(plan.PlanNo, "Cancel Water Plan") ?? throw new OperationCanceledException();
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(connection, transaction, signature.SignedBy, "CanApproveResults", "cancel a water plan");
                using SqlCommand command = new SqlCommand(@"UPDATE dbo.Water_Plans SET Status=N'Cancelled',CancelledBy=@User,CancelledAt=SYSDATETIME(),CancellationReason=@Reason WHERE WaterPlanID=@Plan AND Status NOT IN (N'Cancelled',N'Completed');
UPDATE dbo.Water_PlanSamples SET Status=N'Cancelled' WHERE WaterPlanID=@Plan AND SampleID IS NULL;", connection, transaction);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100); command.Parameters.AddExplicit("@Reason", SqlDbType.NVarChar, signature.Reason, size: -1); command.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.WaterPlanID);
                if (command.ExecuteNonQuery() < 1)
                    throw new DBConcurrencyException("The water plan changed after it was loaded. Refresh and retry.");
                InsertWaterSignature(connection, transaction, plan.WaterPlanID, "Cancelled", signature);
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction,
                    "Water_Plans", plan.WaterPlanID, "Water Plan Cancelled", plan.Status, "Cancelled",
                    signature.Reason, signature.SignedBy,
                    "Status", null, plan.PlanNo, "Water");
            });
            LoadWaterPlans(); _waterSamples.Clear();
        });

        private WaterPlanRow RequireWaterPlan(string status)
        {
            if (gridWaterPlans.SelectedItem is not WaterPlanRow plan) throw new InvalidOperationException("Select a water plan first.");
            if (!plan.Status.Equals(status, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"This action requires status '{status}'. Current status: {plan.Status}.");
            return plan;
        }

        private static void InsertWaterSignature(SqlConnection connection, SqlTransaction transaction, int planId, string action, ElectronicSignature signature)
        {
            if (planId <= 0)
                throw new InvalidOperationException("A valid Water Plan identifier is required before storing the electronic signature.");
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(signature.SignedBy) ||
                string.IsNullOrWhiteSpace(signature.Reason) || string.IsNullOrWhiteSpace(signature.Meaning))
                throw new InvalidOperationException("Complete electronic-signature metadata is required for the Water Plan action.");

            string currentRole = DatabaseHelper.EnsureActiveUserInTransaction(
                connection, transaction, signature.SignedBy, "sign a water-plan action");

            using SqlCommand command = new SqlCommand(@"
INSERT dbo.Water_PlanSignatures
    (WaterPlanID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature)
VALUES
    (@Plan,@Action,@Reason,@User,@Role,@Meaning);", connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            command.Parameters.Add("@Plan", SqlDbType.Int).Value = planId;
            command.Parameters.Add("@Action", SqlDbType.NVarChar, 100).Value = action.Trim();
            command.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = signature.Reason.Trim();
            command.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy.Trim();
            command.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(currentRole) ? (object)DBNull.Value : currentRole.Trim();
            command.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning.Trim();
            command.ExecuteNonQuery();
        }

        private static string CurrentUser() => string.IsNullOrWhiteSpace(Login.CurrentUser) ? "Unknown" : Login.CurrentUser.Trim();
        private static string CurrentRole() => string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? "Unknown" : Login.CurrentUserRole.Trim();
        private static string SelectedText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? combo.Text.Trim();

        private void LoadMethods()
        {
            ApplyMethods(QueryMethods());
        }

        private static DataTable QueryMethods()
        {
            return DatabaseHelper.ExecuteQuery("SELECT MethodName FROM dbo.EM_MonitoringMethods WHERE IsActive=1 ORDER BY Id;", commandTimeoutSeconds: 10);
        }

        private void ApplyMethods(DataTable table)
        {
            cboMethod.Items.Clear();
            cboMethod.Items.Add("Both Methods");
            foreach (DataRow row in table.Rows)
                cboMethod.Items.Add(row["MethodName"].ToString());
            if (cboMethod.Items.Count > 0) cboMethod.SelectedIndex = 0;
        }

        private void LoadAreas()
        {
            ApplyAreas(QueryAreas());
        }

        private static DataTable QueryAreas()
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT Id, AreaCode, AreaName, ISNULL(AreaGroup,N'') AreaGroup, ISNULL(Grade,N'Unclassified') Grade
FROM dbo.EM_Areas WHERE ISNULL(IsActive,1)=1 ORDER BY AreaGroup, AreaCode;", commandTimeoutSeconds: 10);
        }

        private void ApplyAreas(DataTable table)
        {
            _areas.Clear();
            foreach (DataRow row in table.Rows)
            {
                _areas.Add(new AreaChoice
                {
                    Id = Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture),
                    AreaCode = row["AreaCode"].ToString() ?? "",
                    AreaName = row["AreaName"].ToString() ?? "",
                    AreaGroup = row["AreaGroup"].ToString() ?? "",
                    Grade = row["Grade"].ToString() ?? ""
                });
            }
            gridAreas.ItemsSource = _areas;
        }

        private void SelectAllAreas_Click(object sender, RoutedEventArgs e)
        {
            foreach (AreaChoice area in _areas)
                area.IsSelected = true;
            gridAreas.Items.Refresh();
        }

        private void ClearAreas_Click(object sender, RoutedEventArgs e)
        {
            foreach (AreaChoice area in _areas)
                area.IsSelected = false;
            gridAreas.Items.Refresh();
        }

        private void LoadPlans()
        {
            ApplyPlans(QueryPlans());
        }

        private static DataTable QueryPlans()
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT P.PlanID,P.PlanNo,P.PlanType,P.SourceType,P.LoginDate,P.SampleDueDate,P.RequiredDate,P.Status,P.CreatedBy,P.PlanNotes,
       P.ScheduleID,ISNULL(SCH.ScheduleName,N'') ScheduleName,ISNULL(SCH.VersionNo,0) ScheduleVersion,
       COUNT(S.PlanSampleID) SampleCount
FROM dbo.EM_Plans P
LEFT JOIN dbo.EM_PlanSamples S ON S.PlanID=P.PlanID
LEFT JOIN dbo.EM_Schedules SCH ON SCH.ScheduleID=P.ScheduleID
GROUP BY P.PlanID,P.PlanNo,P.PlanType,P.SourceType,P.LoginDate,P.SampleDueDate,P.RequiredDate,P.Status,P.CreatedBy,P.PlanNotes,
         P.ScheduleID,SCH.ScheduleName,SCH.VersionNo
ORDER BY CASE WHEN P.Status IN (N'Cancelled',N'Completed') THEN 1 ELSE 0 END,P.SampleDueDate DESC,P.PlanID DESC;", commandTimeoutSeconds: 10);
        }

        private void ApplyPlans(DataTable table)
        {
            _plans.Clear();
            foreach (DataRow row in table.Rows) _plans.Add(new PlanRow(row));
            gridPlans.ItemsSource = _plans;
        }

        private void LoadSchedules()
        {
            ApplySchedules(QuerySchedules());
        }

        private static DataTable QuerySchedules()
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT ScheduleID,ScheduleName,PlanType,Frequency,NextDueDate,ISNULL(Method,N'') Method,IsActive,
       ISNULL(ApprovalStatus,N'Draft') ApprovalStatus,ISNULL(VersionNo,1) VersionNo,
       ISNULL(CreatedBy,N'') CreatedBy,ISNULL(ReviewedBy,N'') ReviewedBy,ReviewedAt,ISNULL(ApprovedBy,N'') ApprovedBy,ApprovedAt,EffectiveFrom,
       ISNULL(SamplingLocation,N'') SamplingLocation,ISNULL(ApprovedPointCount,0) ApprovedPointCount
FROM dbo.EM_Schedules ORDER BY IsActive DESC,NextDueDate,ScheduleName;", commandTimeoutSeconds: 10);
        }

        private void ApplySchedules(DataTable table)
        {
            _schedules.Clear();
            foreach (DataRow row in table.Rows) _schedules.Add(new ScheduleRow(row));
            gridSchedules.ItemsSource = _schedules;
        }

        private void cboMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string method = cboMethod.SelectedItem?.ToString() ?? "";
            bool personnel = method.Equals("Personnel Monitoring", StringComparison.OrdinalIgnoreCase);
            txtEmployeeId.IsEnabled = personnel;
            txtEmployeeName.IsEnabled = personnel;
        }

        private void CreatePlan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                gridAreas.CommitEdit(DataGridEditingUnit.Cell, true);
                gridAreas.CommitEdit(DataGridEditingUnit.Row, true);
                List<AreaChoice> selectedAreas = _areas.Where(a => a.IsSelected).ToList();
                string planType = SelectedText(cboPlanType);
                const string source = "Ad-hoc";
                string method = cboMethod.SelectedItem?.ToString()?.Trim() ?? "";
                if (selectedAreas.Count == 0) throw new InvalidOperationException("Select at least one EM area.");
                if (string.IsNullOrWhiteSpace(method)) throw new InvalidOperationException("Select a monitoring method.");
                if (!dpLogin.SelectedDate.HasValue || !dpDue.SelectedDate.HasValue || !dpRequired.SelectedDate.HasValue)
                    throw new InvalidOperationException("Login, sampling due and required dates are mandatory.");
                if (dpDue.SelectedDate.Value.Date < dpLogin.SelectedDate.Value.Date || dpRequired.SelectedDate.Value.Date < dpDue.SelectedDate.Value.Date)
                    throw new InvalidOperationException("Required Date must be on/after Sampling Due Date, which must be on/after Login Date.");
                if (string.IsNullOrWhiteSpace(txtMedia.Text) || string.IsNullOrWhiteSpace(txtMediaLot.Text))
                    throw new InvalidOperationException("Media and released media preparation number are mandatory.");
                int mediaPreparationId = EnsureReleasedMediaPreparation(txtMedia.Text.Trim(), txtMediaLot.Text.Trim());
                if (method == "Personnel Monitoring" && (string.IsNullOrWhiteSpace(txtEmployeeId.Text) || string.IsNullOrWhiteSpace(txtEmployeeName.Text)))
                    throw new InvalidOperationException("Employee ID and name are mandatory for personnel monitoring.");

                var signature = ConfirmSignature("NEW EM PLAN", "Create EM Plan");
                if (signature == null) return;

                string planNo = "";
                int planId = 0;
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    planNo = GetNextPlanNo(connection, transaction);
                    using SqlCommand planCommand = new SqlCommand(@"
INSERT dbo.EM_Plans(PlanNo,PlanType,SourceType,LoginDate,SampleDueDate,RequiredDate,Status,SetNo,PlanNotes,CreatedBy)
OUTPUT INSERTED.PlanID
VALUES(@No,@Type,@Source,@Login,@Due,@Required,N'Planned',@Set,@Notes,@User);", connection, transaction);
                    planCommand.Parameters.AddExplicit("@No", SqlDbType.NVarChar, planNo, size: 50);
                    planCommand.Parameters.AddExplicit("@Type", SqlDbType.NVarChar, planType, size: 50);
                    planCommand.Parameters.AddExplicit("@Source", SqlDbType.NVarChar, source, size: 50);
                    planCommand.Parameters.AddExplicit("@Login", SqlDbType.Date, dpLogin.SelectedDate.Value.Date);
                    planCommand.Parameters.AddExplicit("@Due", SqlDbType.Date, dpDue.SelectedDate.Value.Date);
                    planCommand.Parameters.AddExplicit("@Required", SqlDbType.Date, dpRequired.SelectedDate.Value.Date);
                    planCommand.Parameters.AddExplicit("@Set", SqlDbType.NVarChar, Db(txtSetNo.Text), size: 100);
                    planCommand.Parameters.AddExplicit("@Notes", SqlDbType.NVarChar, Db(txtNotes.Text), size: -1);
                    planCommand.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                    planId = Convert.ToInt32(planCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

                    int sequence = 0;
                    foreach (AreaChoice area in selectedAreas)
                    {
                        foreach (SampleSeed seed in BuildSeeds(connection, transaction, area, method, txtLocation.Text.Trim()))
                        {
                            sequence++;
                            InsertPlanSample(connection, transaction, planId, area.Id, seed.Method, seed.Location,
                                MakeCode(planNo, area.AreaCode, seed.Method, sequence), false, mediaPreparationId);
                        }
                    }
                    InsertPlanSample(connection, transaction, planId, null, "Negative Control", "Transport / Media Negative Control", "NC-" + planNo, true, mediaPreparationId);
                    InsertSignature(connection, transaction, planId, "Plan Created", signature);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction,
                        "EM_Plans", planId, "EM Plan Created", "", "Planned",
                        signature.Reason, signature.SignedBy,
                        "Status", null, planNo, "Environmental Monitoring");
                });
                MessageBox.Show($"Plan created successfully.\n\n{planNo}", "EM Planning", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadPlans();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private IEnumerable<SampleSeed> BuildSeeds(SqlConnection connection, SqlTransaction transaction, AreaChoice area, string method, string locationContext)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT Id,SequenceNo,Method,PlateCode FROM dbo.EM_AreaTemplates
WHERE AreaId=@Area AND ISNULL(IsActive,1)=1
  AND (@Method=N'Both Methods' AND Method IN (N'Settle Plate',N'Active Air Sampling')
       OR UPPER(LTRIM(RTRIM(Method)))=UPPER(@Method))
ORDER BY SequenceNo,Id;", connection, transaction);
            command.Parameters.AddExplicit("@Area", SqlDbType.Int, area.Id);
            command.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, method, size: 100);
            using SqlDataReader reader = command.ExecuteReader();
            var result = new List<SampleSeed>();
            while (reader.Read())
                result.Add(new SampleSeed(
                    Convert.ToInt32(reader["Id"], CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader["SequenceNo"], CultureInfo.InvariantCulture),
                    reader["Method"].ToString() ?? method,
                    ComposeSamplingLocation(locationContext, reader["PlateCode"].ToString(), area.AreaName)));
            if (result.Count == 0)
                throw new InvalidOperationException($"No approved active {method} sampling templates exist for {area.AreaCode} - {area.AreaName}. Configure the approved EM program before creating the plan.");
            return result;
        }

        private static int EnsureReleasedMediaPreparation(string mediaText, string preparationOrLot)
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 p.MediaPreparationID
FROM dbo.MediaPreparations p
INNER JOIN dbo.CultureMedia m ON m.MediaID=p.MediaID
LEFT JOIN dbo.CultureMediaLots l ON l.MediaLotID=p.MediaLotID
WHERE UPPER(LTRIM(RTRIM(p.MediaPreparationNo)))=UPPER(LTRIM(RTRIM(@Reference)))
  AND (UPPER(LTRIM(RTRIM(m.MediaCode)))=UPPER(LTRIM(RTRIM(@Media)))
       OR UPPER(LTRIM(RTRIM(m.MediaName)))=UPPER(LTRIM(RTRIM(@Media)))
       OR (UPPER(LTRIM(RTRIM(@Media))) IN (N'TSA',N'TRYPTIC SOY AGAR') AND (UPPER(LTRIM(RTRIM(m.MediaCode))) IN (N'TSA',N'TRYPTIC SOY AGAR') OR UPPER(LTRIM(RTRIM(m.MediaName))) IN (N'TSA',N'TRYPTIC SOY AGAR'))))
  AND UPPER(LTRIM(RTRIM(ISNULL(p.ReleaseStatus,N''))))=N'RELEASED'
  AND UPPER(LTRIM(RTRIM(ISNULL(p.SterilityReview,N'')))) IN (N'PASSED',N'RELEASED',N'GPT PASSED')
  AND p.ExpiryDate IS NOT NULL AND p.ExpiryDate>=CAST(GETDATE() AS DATE)
ORDER BY p.MediaPreparationID DESC;",
                new[]
                {
                    new SqlParameter("@Reference", preparationOrLot),
                    new SqlParameter("@Media", mediaText)
                });
            if (table.Rows.Count == 0)
                throw new InvalidOperationException("The selected media reference is not a released, sterility-approved, unexpired prepared-media batch. Select a valid released Media Preparation No.");
            return Convert.ToInt32(table.Rows[0]["MediaPreparationID"], CultureInfo.InvariantCulture);
        }

        private void InsertPlanSample(SqlConnection connection, SqlTransaction transaction, int planId, int? areaId,
            string method, string location, string code, bool negative, int? mediaPreparationId)
        {
            using SqlCommand command = new SqlCommand(@"
INSERT dbo.EM_PlanSamples(PlanID,AreaID,Method,SamplingLocation,SampleCode,IsNegativeControl,EmployeeID,EmployeeName,MediaUsed,MediaLotNo,MediaPreparationID,Status)
VALUES(@Plan,@Area,@Method,@Location,@Code,@Negative,@EmployeeID,@EmployeeName,@Media,@Lot,@MediaPreparationID,N'Planned');", connection, transaction);
            command.Parameters.AddExplicit("@Plan", SqlDbType.Int, planId);
            command.Parameters.AddExplicit("@Area", SqlDbType.Int, areaId.HasValue ? areaId.Value : DBNull.Value);
            command.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, method, size: 100);
            command.Parameters.AddExplicit("@Location", SqlDbType.NVarChar, Db(location), size: 200);
            command.Parameters.AddExplicit("@Code", SqlDbType.NVarChar, code, size: 100);
            command.Parameters.AddExplicit("@Negative", SqlDbType.Bit, negative);
            command.Parameters.AddExplicit("@EmployeeID", SqlDbType.NVarChar, Db(txtEmployeeId.Text), size: 100);
            command.Parameters.AddExplicit("@EmployeeName", SqlDbType.NVarChar, Db(txtEmployeeName.Text), size: 200);
            command.Parameters.AddExplicit("@Media", SqlDbType.NVarChar, Db(txtMedia.Text), size: 200);
            command.Parameters.AddExplicit("@Lot", SqlDbType.NVarChar, Db(txtMediaLot.Text), size: 100);
            command.Parameters.AddExplicit("@MediaPreparationID", SqlDbType.Int, mediaPreparationId.HasValue ? mediaPreparationId.Value : DBNull.Value);
            command.ExecuteNonQuery();
        }

        private static string ComposeSamplingLocation(string? context, string? templateLocation, string fallback)
        {
            string site = string.IsNullOrWhiteSpace(templateLocation) ? fallback.Trim() : templateLocation.Trim();
            string prefix = context?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prefix)) return site;
            if (string.Equals(prefix, site, StringComparison.OrdinalIgnoreCase)) return site;
            return prefix + " / " + site;
        }

        private static string MakeCode(string planNo, string areaCode, string method, int sequence)
        {
            string token = method switch { "Settle Plate" => "SP", "Active Air Sampling" => "AA", "Contact Plate" => "CP", "Surface Swab" => "SW", "Personnel Monitoring" => "PM", _ => "EM" };
            return $"{areaCode}-{token}-{planNo[^4..]}-{sequence:00}";
        }

        private static string GetNextPlanNo(SqlConnection connection, SqlTransaction transaction)
        {
            using SqlCommand command = new SqlCommand(@"
DECLARE @Next INT;
SELECT @Next=ISNULL(MAX(TRY_CONVERT(INT,RIGHT(PlanNo,4))),0)+1 FROM dbo.EM_Plans WITH(UPDLOCK,HOLDLOCK);
SELECT N'EMP-'+CONVERT(NVARCHAR(8),GETDATE(),112)+N'-'+RIGHT(N'0000'+CONVERT(NVARCHAR(10),@Next),4);", connection, transaction);
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("Unable to create plan number.");
        }

        private void gridPlans_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridPlans.SelectedItem is not PlanRow plan) { _samples.Clear(); return; }
            LoadSamples(plan.PlanID);

            DateTime? plannedEnd = _samples
                .Where(sample => sample.PlannedIncubationEnd.HasValue)
                .Select(sample => sample.PlannedIncubationEnd)
                .FirstOrDefault();
            txtIncubationEnd.Text = plannedEnd?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private void LoadSamples(int planId)
        {
            _samples.Clear();
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT S.*,
       ISNULL(A.AreaCode,N'CONTROL') AreaCode,
       ISNULL(A.AreaName,N'Control') AreaName,
       ISNULL(A.Grade,N'N/A') Grade
FROM dbo.EM_PlanSamples S
LEFT JOIN dbo.EM_Areas A ON A.Id=S.AreaID
WHERE S.PlanID=@Plan
ORDER BY S.PlanSampleID;",
                new[] { new SqlParameter("@Plan", planId) });
            foreach (DataRow row in table.Rows) _samples.Add(new PlanSampleRow(row));
            gridSamples.ItemsSource = _samples;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadPlans();

        private void Distribute_Click(object sender, RoutedEventArgs e) => ExecuteUi(() => Transition("Planned", "Distributed", "Distribute EM Samples", "Samples distributed for controlled collection"));

        private void Collect_Click(object sender, RoutedEventArgs e)
            => ExecuteUi(CollectPlan);

        private void CollectPlan()
        {
            if (gridPlans.SelectedItem is not PlanRow plan)
                throw new InvalidOperationException("Select an EM plan first.");
            bool finalControlReading = plan.Status.Equals("Incubation Complete", StringComparison.OrdinalIgnoreCase);
            if (finalControlReading && _samples.Any(sample => !sample.IncubationPhase2End.HasValue))
                throw new InvalidOperationException("Negative control may only be read after documented completion of incubation phase 2.");
            if (!plan.Status.Equals("Distributed", StringComparison.OrdinalIgnoreCase) && !finalControlReading)
                throw new InvalidOperationException("Collection checks are available only for Distributed plans. Final control reading is available only after incubation completion.");

            var collectionDialog = new EMCollectionDialog(plan.PlanNo, _samples, finalControlReading) { Owner = this };
            if (collectionDialog.ShowDialog() != true) return;

            List<EMCollectionDialog.CollectionItem> results = collectionDialog.Results.ToList();
            bool controlGrowthDetected = finalControlReading && results.Any(sample =>
                sample.IsNegativeControl && sample.NegativeControlResult.Equals("Growth Detected", StringComparison.OrdinalIgnoreCase));
            List<EMCollectionDialog.CollectionItem> collectionExcursions = finalControlReading
                ? new List<EMCollectionDialog.CollectionItem>()
                : results.Where(sample => !sample.IsNegativeControl &&
                    (!sample.PlateCondition.Equals("Acceptable", StringComparison.OrdinalIgnoreCase) ||
                     !sample.KitCondition.Equals("Released / Within Expiry", StringComparison.OrdinalIgnoreCase))).ToList();

            string signatureAction = controlGrowthDetected
                ? "Record EM Negative Control Failure"
                : finalControlReading ? "Read EM Negative Control" : "Collect EM Samples";
            ElectronicSignature signature = ConfirmSignature(plan.PlanNo, signatureAction) ?? throw new OperationCanceledException();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                foreach (EMCollectionDialog.CollectionItem sample in results)
                {
                    if (finalControlReading && !sample.IsNegativeControl) continue;
                    bool collectionExcursion = !finalControlReading && !sample.IsNegativeControl &&
                        (!sample.PlateCondition.Equals("Acceptable", StringComparison.OrdinalIgnoreCase) ||
                         !sample.KitCondition.Equals("Released / Within Expiry", StringComparison.OrdinalIgnoreCase));
                    bool controlFailure = finalControlReading && sample.IsNegativeControl &&
                        sample.NegativeControlResult.Equals("Growth Detected", StringComparison.OrdinalIgnoreCase);
                    string sampleStatus = finalControlReading
                        ? (controlFailure ? "Control Failed" : "Incubation Complete")
                        : (collectionExcursion ? "Recollection Required" : "Collected");

                    using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_PlanSamples SET PlateCondition=@PlateCondition,KitCondition=@KitCondition,SamplingStart=@Start,SamplingEnd=@End,
TransportMinC=@Min,TransportMaxC=@Max,NegativeControlResult=@Control,
CollectionExcursion=CASE WHEN @FinalControl=0 THEN @CollectionExcursion ELSE CollectionExcursion END,
ControlReadBy=CASE WHEN @FinalControl=1 AND IsNegativeControl=1 THEN @ControlReadBy ELSE ControlReadBy END,
ControlReadAt=CASE WHEN @FinalControl=1 AND IsNegativeControl=1 THEN SYSDATETIME() ELSE ControlReadAt END,
Status=@SampleStatus WHERE PlanSampleID=@ID;", connection, transaction);
                    command.Parameters.AddExplicit("@PlateCondition", SqlDbType.NVarChar, sample.PlateCondition.Trim(), size: 100);
                    command.Parameters.AddExplicit("@KitCondition", SqlDbType.NVarChar, sample.KitCondition.Trim(), size: 100);
                    command.Parameters.AddExplicit("@Start", SqlDbType.DateTime2, Db(ParseDate(sample.SamplingStartText)));
                    command.Parameters.AddExplicit("@End", SqlDbType.DateTime2, Db(ParseDate(sample.SamplingEndText)));
                    command.Parameters.AddExplicit("@Min", SqlDbType.Decimal, ParseDecimal(sample.TransportMinText)!.Value, precision: 18, scale: 6);
                    command.Parameters.AddExplicit("@Max", SqlDbType.Decimal, ParseDecimal(sample.TransportMaxText)!.Value, precision: 18, scale: 6);
                    command.Parameters.AddExplicit("@Control", SqlDbType.NVarChar, Db(sample.NegativeControlResult), size: 100);
                    command.Parameters.AddExplicit("@CollectionExcursion", SqlDbType.Bit, collectionExcursion);
                    command.Parameters.AddExplicit("@SampleStatus", SqlDbType.NVarChar, sampleStatus, size: 50);
                    command.Parameters.AddExplicit("@FinalControl", SqlDbType.Bit, finalControlReading);
                    command.Parameters.AddExplicit("@ControlReadBy", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                    command.Parameters.AddExplicit("@ID", SqlDbType.Int, sample.PlanSampleID);
                    command.ExecuteNonQuery();
                }

                string newPlanStatus = plan.Status;
                string signatureRecordAction;
                if (controlGrowthDetected)
                {
                    newPlanStatus = "Recollection Required";
                    UpdatePlan(connection, transaction, plan.PlanID, newPlanStatus, signature.SignedBy, "Incubation Complete");
                    string description = "EM negative control showed Growth Detected after incubation. The affected plan is invalid for result release and requires investigation/recollection.";
                    DatabaseHelper.EnsureOpenSourceQualityEvent(connection, transaction, "EM Planning", plan.PlanID, plan.PlanNo,
                        "Negative Control Failure", "Major", signature.SignedBy, description,
                        "Plan placed in Recollection Required status. Do not release results from this plan until QA investigation/disposition is completed.");
                    signatureRecordAction = "Negative Control Failed";
                }
                else if (collectionExcursions.Count > 0)
                {
                    newPlanStatus = "Recollection Required";
                    UpdatePlan(connection, transaction, plan.PlanID, newPlanStatus, signature.SignedBy, "Distributed");
                    string affected = string.Join(", ", collectionExcursions.Select(item => item.SampleCode + " [" + item.PlateCondition + "; " + item.KitCondition + "]"));
                    string description = "EM collection condition excursion recorded for: " + affected + ".";
                    DatabaseHelper.EnsureOpenSourceQualityEvent(connection, transaction, "EM Planning", plan.PlanID, plan.PlanNo,
                        "Collection Excursion", "Major", signature.SignedBy, description,
                        "Actual condition was retained. Plan placed in Recollection Required status pending QA disposition.");
                    signatureRecordAction = "Collection Excursion Recorded";
                }
                else
                {
                    if (!finalControlReading)
                    {
                        newPlanStatus = "Collected";
                        UpdatePlan(connection, transaction, plan.PlanID, newPlanStatus, signature.SignedBy, "Distributed");
                    }
                    signatureRecordAction = finalControlReading ? "Negative Control Read" : "Collected";
                }

                InsertSignature(connection, transaction, plan.PlanID, signatureRecordAction, signature);
                Audit(connection, transaction, plan, plan.Status,
                    finalControlReading && !controlGrowthDetected ? "Incubation Complete - Control Read" : newPlanStatus, signature);
            });

            LoadPlans();
            if (controlGrowthDetected)
                MessageBox.Show("Negative control growth was recorded exactly as observed. A Quality Event was opened and the plan is now Recollection Required.",
                    "EM Control Failure", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (collectionExcursions.Count > 0)
                MessageBox.Show("The collection excursion was recorded exactly as observed. A Quality Event was opened and the plan is now Recollection Required.",
                    "EM Collection Excursion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Incubate_Click(object sender, RoutedEventArgs e)
            => ExecuteUi(IncubatePlan);

        private void IncubatePlan()
        {
            PlanRow plan = RequirePlan("Collected");
            if (string.IsNullOrWhiteSpace(txtBacteriaIncubator.Text) || string.IsNullOrWhiteSpace(txtFungiIncubator.Text))
                throw new InvalidOperationException("Enter both bacteria and fungi incubator IDs before starting incubation.");

            DateTime phase1Start = DatabaseHelper.GetAuthoritativeDatabaseTime();
            DateTime phase1TargetEnd = phase1Start.AddDays(3);
            DateTime plannedEnd = phase1Start.AddDays(5);
            txtIncubationEnd.Text = plannedEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            ElectronicSignature? signature = ConfirmSignature(plan.PlanNo, "Start EM Incubation Phase 1");
            if (signature == null) return;

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand samples = new SqlCommand(@"
UPDATE dbo.EM_PlanSamples
SET IncubationStart=@Phase1Start,
    IncubationEnd=NULL,
    PlannedIncubationEnd=@PlannedEnd,
    IncubationPhase1Start=@Phase1Start,
    IncubationPhase1TargetEnd=@Phase1TargetEnd,
    IncubationPhase1End=NULL,
    IncubationPhase1Temp=N'20-25 C',
    IncubationPhase1ActualTempC=NULL,
    IncubationPhase1CompletedBy=NULL,
    IncubationPhase1CompletedAt=NULL,
    IncubationPhase2Start=NULL,
    IncubationPhase2TargetEnd=NULL,
    IncubationPhase2End=NULL,
    IncubationPhase2Temp=N'30-35 C',
    IncubationPhase2ActualTempC=NULL,
    IncubationPhase2CompletedBy=NULL,
    IncubationPhase2CompletedAt=NULL,
    BacteriaIncubatorID=@Bacteria,
    FungiIncubatorID=@Fungi,
    Status=N'Incubating Phase 1'
WHERE PlanID=@Plan AND Status=N'Collected';", connection, transaction);
                samples.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                samples.Parameters.AddExplicit("@Phase1Start", SqlDbType.DateTime2, phase1Start);
                samples.Parameters.AddExplicit("@Phase1TargetEnd", SqlDbType.DateTime2, phase1TargetEnd);
                samples.Parameters.AddExplicit("@PlannedEnd", SqlDbType.DateTime2, plannedEnd);
                samples.Parameters.AddExplicit("@Bacteria", SqlDbType.NVarChar, txtBacteriaIncubator.Text.Trim(), size: 100);
                samples.Parameters.AddExplicit("@Fungi", SqlDbType.NVarChar, txtFungiIncubator.Text.Trim(), size: 100);
                if (samples.ExecuteNonQuery() != _samples.Count)
                    throw new DBConcurrencyException("One or more EM samples changed before incubation was started. Refresh and retry.");
                UpdatePlan(connection, transaction, plan.PlanID, "Incubating Phase 1", signature.SignedBy, "Collected");
                InsertSignature(connection, transaction, plan.PlanID, "Incubation Phase 1 Started", signature);
                DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                    "Start Incubation Phase 1", "Collected", "Incubating Phase 1", signature.Reason,
                    signature.SignedBy, "Status", null, plan.PlanNo, "Environmental Monitoring");
            });
            LoadPlans();
        }

        private void CompletePhase1_Click(object sender, RoutedEventArgs e) => ExecuteUi(CompletePhase1);

        private void CompletePhase1()
        {
            PlanRow plan = RequirePlan("Incubating Phase 1", "Incubating");
            decimal actualTemperature = RequireTemperature(txtPhase1ActualTemp.Text, 20m, 25m, "phase 1");
            bool temperatureExcursion = actualTemperature < 20m || actualTemperature > 25m;
            DateTime phase1Start = GetMinimumPlanDate(plan.PlanID, "IncubationPhase1Start")
                ?? throw new InvalidOperationException("Phase 1 start time is missing.");
            DateTime phase1RequiredEnd = phase1Start.AddHours(72);
            DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
            bool developmentTimingOverrideUsed = databaseNow < phase1RequiredEnd;
            if (developmentTimingOverrideUsed && !AppConfig.AllowEarlyMicrobiologyResults)
                throw new InvalidOperationException("Phase 1 cannot be completed before 72 hours have elapsed.");
            if (developmentTimingOverrideUsed)
            {
                ApplicationLogger.Warning(
                    "Development-only EM phase 1 timing override was used for plan " + plan.PlanNo +
                    ". Recorded target end: " + phase1RequiredEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
            }

            ElectronicSignature? signature = ConfirmSignature(plan.PlanNo, "Complete Phase 1 and Start Phase 2");
            if (signature == null) return;
            DateTime transitionAt = databaseNow;
            DateTime phase2TargetEnd = transitionAt.AddHours(48);

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_PlanSamples
SET IncubationPhase1End=@TransitionAt,
    IncubationPhase1ActualTempC=@ActualTemp,
    IncubationPhase1Excursion=@TemperatureExcursion,
    IncubationPhase1CompletedBy=@User,
    IncubationPhase1CompletedAt=@TransitionAt,
    IncubationPhase2Start=@TransitionAt,
    IncubationPhase2TargetEnd=@Phase2TargetEnd,
    Status=N'Incubating Phase 2'
WHERE PlanID=@Plan
  AND Status IN (N'Incubating Phase 1',N'Incubating')
  AND IncubationPhase1End IS NULL;", connection, transaction);
                command.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                command.Parameters.AddExplicit("@TransitionAt", SqlDbType.DateTime2, transitionAt);
                command.Parameters.AddExplicit("@ActualTemp", SqlDbType.Decimal, actualTemperature, precision: 18, scale: 6);
                command.Parameters.AddExplicit("@TemperatureExcursion", SqlDbType.Bit, temperatureExcursion);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                command.Parameters.AddExplicit("@Phase2TargetEnd", SqlDbType.DateTime2, phase2TargetEnd);
                if (command.ExecuteNonQuery() != _samples.Count)
                    throw new DBConcurrencyException("One or more EM samples changed before the phase transition. Refresh and retry.");
                UpdatePlanFromStatuses(connection, transaction, plan.PlanID, "Incubating Phase 2", signature.SignedBy,
                    "Incubating Phase 1", "Incubating");
                InsertSignature(connection, transaction, plan.PlanID, "Incubation Phase 1 Completed / Phase 2 Started", signature);
                DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                    "Incubation Phase Transition", plan.Status, "Incubating Phase 2", signature.Reason,
                    signature.SignedBy, "Status", null, plan.PlanNo, "Environmental Monitoring");
                if (temperatureExcursion)
                {
                    string description = $"EM incubation phase 1 actual temperature {actualTemperature:0.0} °C was outside the controlled 20-25 °C range.";
                    DatabaseHelper.EnsureOpenSourceQualityEvent(connection, transaction, "EM Planning", plan.PlanID, plan.PlanNo,
                        "Incubation Excursion", "Major", signature.SignedBy, description,
                        "Continue documented incubation; release to EM Results is blocked until QA disposition of the Quality Event.");
                    DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                        "Incubation Temperature Excursion Recorded", "20-25 °C", actualTemperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C",
                        signature.Reason, signature.SignedBy, "IncubationPhase1ActualTempC", null, plan.PlanNo, "Environmental Monitoring");
                }
                if (developmentTimingOverrideUsed)
                {
                    DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                        "Development Incubation Timing Override", "Phase 1 requires 72 elapsed hours",
                        "Early phase 1 completion permitted in Development only", signature.Reason,
                        signature.SignedBy, "IncubationPhase1TargetEnd", null, plan.PlanNo, "Environmental Monitoring");
                }
            });
            LoadPlans();
        }

        private void CompletePhase2_Click(object sender, RoutedEventArgs e) => ExecuteUi(CompletePhase2);

        private void CompletePhase2()
        {
            PlanRow plan = RequirePlan("Incubating Phase 2");
            decimal actualTemperature = RequireTemperature(txtPhase2ActualTemp.Text, 30m, 35m, "phase 2");
            bool temperatureExcursion = actualTemperature < 30m || actualTemperature > 35m;
            DateTime phase2Start = GetMinimumPlanDate(plan.PlanID, "IncubationPhase2Start")
                ?? throw new InvalidOperationException("Phase 2 start time is missing.");
            DateTime phase2RequiredEnd = phase2Start.AddHours(48);
            DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
            bool developmentTimingOverrideUsed = databaseNow < phase2RequiredEnd;
            if (developmentTimingOverrideUsed && !AppConfig.AllowEarlyMicrobiologyResults)
                throw new InvalidOperationException("Phase 2 cannot be completed before 48 hours have elapsed.");
            if (developmentTimingOverrideUsed)
            {
                ApplicationLogger.Warning(
                    "Development-only EM phase 2 timing override was used for plan " + plan.PlanNo +
                    ". Recorded target end: " + phase2RequiredEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
            }

            ElectronicSignature? signature = ConfirmSignature(plan.PlanNo, "Complete EM Incubation Phase 2");
            if (signature == null) return;
            DateTime completedAt = databaseNow;

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_PlanSamples
SET IncubationPhase2End=@CompletedAt,
    IncubationPhase2ActualTempC=@ActualTemp,
    IncubationPhase2Excursion=@TemperatureExcursion,
    IncubationPhase2CompletedBy=@User,
    IncubationPhase2CompletedAt=@CompletedAt,
    IncubationEnd=@CompletedAt,
    Status=N'Incubation Complete'
WHERE PlanID=@Plan
  AND Status=N'Incubating Phase 2'
  AND IncubationPhase2End IS NULL;", connection, transaction);
                command.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                command.Parameters.AddExplicit("@CompletedAt", SqlDbType.DateTime2, completedAt);
                command.Parameters.AddExplicit("@ActualTemp", SqlDbType.Decimal, actualTemperature, precision: 18, scale: 6);
                command.Parameters.AddExplicit("@TemperatureExcursion", SqlDbType.Bit, temperatureExcursion);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                if (command.ExecuteNonQuery() != _samples.Count)
                    throw new DBConcurrencyException("One or more EM samples changed before phase 2 completion. Refresh and retry.");
                UpdatePlan(connection, transaction, plan.PlanID, "Incubation Complete", signature.SignedBy, "Incubating Phase 2");
                InsertSignature(connection, transaction, plan.PlanID, "Incubation Phase 2 Completed", signature);
                DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                    "Complete Incubation Phase 2", "Incubating Phase 2", "Incubation Complete", signature.Reason,
                    signature.SignedBy, "Status", null, plan.PlanNo, "Environmental Monitoring");
                if (temperatureExcursion)
                {
                    string description = $"EM incubation phase 2 actual temperature {actualTemperature:0.0} °C was outside the controlled 30-35 °C range.";
                    DatabaseHelper.EnsureOpenSourceQualityEvent(connection, transaction, "EM Planning", plan.PlanID, plan.PlanNo,
                        "Incubation Excursion", "Major", signature.SignedBy, description,
                        "Incubation was documented as observed; release to EM Results is blocked until QA disposition of the Quality Event.");
                    DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                        "Incubation Temperature Excursion Recorded", "30-35 °C", actualTemperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C",
                        signature.Reason, signature.SignedBy, "IncubationPhase2ActualTempC", null, plan.PlanNo, "Environmental Monitoring");
                }
                if (developmentTimingOverrideUsed)
                {
                    DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                        "Development Incubation Timing Override", "Phase 2 requires 48 elapsed hours",
                        "Early phase 2 completion permitted in Development only", signature.Reason,
                        signature.SignedBy, "IncubationPhase2TargetEnd", null, plan.PlanNo, "Environmental Monitoring");
                }
            });
            LoadPlans();
        }

        private void ReadNegativeControl_Click(object sender, RoutedEventArgs e) => ExecuteUi(CollectPlan);

        private static decimal RequireTemperature(string text, decimal minimum, decimal maximum, string phase)
        {
            if (!decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
                throw new InvalidOperationException($"Enter the actual {phase} temperature as a number.");
            // Record the observation exactly as measured. Out-of-range values are controlled
            // as excursions and Quality Events; they must never be hidden by validation.
            if (value < -20m || value > 80m)
                throw new InvalidOperationException($"The actual {phase} temperature is outside the supported recording range (-20 to 80 °C). Verify the entry.");
            return value;
        }

        private static DateTime? GetMinimumPlanDate(int planId, string columnName)
        {
            string safeColumn = columnName switch
            {
                "IncubationPhase1Start" => "IncubationPhase1Start",
                "IncubationPhase2Start" => "IncubationPhase2Start",
                _ => throw new ArgumentOutOfRangeException(nameof(columnName))
            };
            object? value = DatabaseHelper.ExecuteScalar(
                $"SELECT MIN({safeColumn}) FROM dbo.EM_PlanSamples WHERE PlanID=@Plan;",
                new[] { new SqlParameter("@Plan", planId) });
            return value == null || value == DBNull.Value ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private void SendResults_Click(object sender, RoutedEventArgs e)
            => ExecuteUi(SendToResults);

        private void SendToResults()
        {
            PlanRow plan = RequirePlan("Incubation Complete");
            if (DatabaseHelper.HasOpenSourceQualityEvent("EM Planning", plan.PlanID))
                throw new InvalidOperationException("This EM plan has an open collection/incubation Quality Event. QA disposition is required before release to EM Results.");
            if (_samples.Count == 0)
                throw new InvalidOperationException("The selected plan has no samples to release.");
            if (_samples.Any(sample => !string.Equals(sample.Status, "Incubation Complete", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("All plan samples must have completed both incubation phases before release to results.");
            if (_samples.Any(s => !s.IncubationStart.HasValue || !s.IncubationEnd.HasValue))
                throw new InvalidOperationException("Incubation start and end date/time must be recorded for every sample.");
            DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
            bool developmentTimingOverrideUsed = _samples.Any(s =>
                (s.PlannedIncubationEnd.HasValue && s.PlannedIncubationEnd.Value > databaseNow) ||
                s.IncubationEnd!.Value > databaseNow);
            if (developmentTimingOverrideUsed && !AppConfig.AllowEarlyMicrobiologyResults)
                throw new InvalidOperationException("Incubation is not complete. Samples cannot be released before the recorded end time.");
            if (developmentTimingOverrideUsed)
            {
                ApplicationLogger.Warning(
                    "Development-only early EM release was used for plan " + plan.PlanNo + ".");
            }
            if (_samples.Any(s => s.IncubationEnd!.Value <= s.IncubationStart!.Value))
                throw new InvalidOperationException("One or more samples have an invalid incubation period.");
            if (_samples.Any(s => s.EventID.HasValue))
                throw new InvalidOperationException("One or more samples were already released to EM Results Entry. Duplicate release is blocked.");
            if (_samples.Any(s => s.IsNegativeControl && !string.Equals(s.NegativeControlResult, "No Growth", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Negative control must be recorded as 'No Growth' before release to results.");

            List<IGrouping<int, PlanSampleRow>> resultGroups = _samples
                .Where(s => !s.IsNegativeControl && s.AreaID.HasValue)
                .GroupBy(s => s.AreaID!.Value)
                .ToList();
            if (resultGroups.Count == 0)
                throw new InvalidOperationException("No valid area samples are available for EM Results Entry.");

            const string releaseAction = "Release EM Samples to Results Entry";
            var signature = ConfirmSignature(plan.PlanNo, releaseAction);
            if (signature == null) return;
            List<string> events = new();
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                foreach (IGrouping<int, PlanSampleRow> group in resultGroups)
                {
                    string eventNo = DatabaseHelper.GetNextEMEventNumber(connection, transaction);
                    int eventId;
                    using (SqlCommand ev = new SqlCommand(@"
INSERT dbo.EM_Events(EventNo,AreaId,EventDate,AreaCodeSnapshot,AreaNameSnapshot,GradeSnapshot,AreaSnapshotSource,MediaUsed,MediaLotNo,MediaPreparationID,SamplingTimeFrom,SamplingTimeTo,
IncubationStart,IncubationEnd,IncubationTemperature,IncubatorNo1,IncubatorNo2,NegativeControlResult,
FinalResult,Remarks,MonitoringCategory,WorkflowStatus,PlanID,CreatedAt)
OUTPUT INSERTED.Id VALUES(@No,@Area,@Date,@AreaCode,@AreaName,@Grade,N'Native controlled plan release',@Media,@Lot,@MediaPreparationID,@From,@To,@Inc,@IncEnd,@IncTemp,@Inc1,@Inc2,@Control,
N'Pending',@Remarks,N'Plan Based',N'Pending',@Plan,SYSDATETIME());", connection, transaction))
                    {
                        PlanSampleRow first = group.First();
                        ev.Parameters.AddExplicit("@No", SqlDbType.NVarChar, eventNo, size: 50); ev.Parameters.AddExplicit("@Area", SqlDbType.Int, group.Key);
                        ev.Parameters.AddExplicit("@Date", SqlDbType.DateTime2, first.SamplingStart ?? databaseNow); ev.Parameters.AddExplicit("@AreaCode", SqlDbType.NVarChar, Db(first.AreaCode), size: 100); ev.Parameters.AddExplicit("@AreaName", SqlDbType.NVarChar, Db(first.AreaName), size: 200); ev.Parameters.AddExplicit("@Grade", SqlDbType.NVarChar, Db(first.Grade), size: 100);
                        ev.Parameters.AddExplicit("@Media", SqlDbType.NVarChar, Db(first.MediaUsed), size: 200); ev.Parameters.AddExplicit("@Lot", SqlDbType.NVarChar, Db(first.MediaLotNo), size: 100);
                        ev.Parameters.AddExplicit("@MediaPreparationID", SqlDbType.Int, first.MediaPreparationID.HasValue ? first.MediaPreparationID.Value : DBNull.Value);
                        ev.Parameters.AddExplicit("@From", SqlDbType.NVarChar, Db(first.SamplingStartText), size: 50); ev.Parameters.AddExplicit("@To", SqlDbType.NVarChar, Db(first.SamplingEndText), size: 50);
                        ev.Parameters.AddExplicit("@Inc", SqlDbType.NVarChar, first.IncubationStart!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), size: 50);
                        ev.Parameters.AddExplicit("@IncEnd", SqlDbType.NVarChar, Db(first.IncubationEnd?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)), size: 50);
                        ev.Parameters.AddExplicit("@IncTemp", SqlDbType.NVarChar, Db(first.BuildIncubationTemperatureSummary()), size: 200);
                        ev.Parameters.AddExplicit("@Inc1", SqlDbType.NVarChar, Db(first.BacteriaIncubatorID), size: 100);
                        ev.Parameters.AddExplicit("@Inc2", SqlDbType.NVarChar, Db(first.FungiIncubatorID), size: 100);
                        string negativeControl = _samples.FirstOrDefault(s => s.IsNegativeControl)?.NegativeControlResult ?? "";
                        ev.Parameters.AddExplicit("@Control", SqlDbType.NVarChar, Db(negativeControl), size: 100);
                        string releaseRemark = "Created from controlled plan " + plan.PlanNo;
                        ev.Parameters.AddExplicit("@Remarks", SqlDbType.NVarChar, Db(releaseRemark), size: -1); ev.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                        eventId = Convert.ToInt32(ev.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
                    int seq = 0;
                    foreach (PlanSampleRow sample in group)
                    {
                        seq++;
                        using SqlCommand plate = new SqlCommand(@"
INSERT dbo.EM_EventPlates(EventId,Method,PlateCode,SequenceNo,Status,SampleSite,PlanSampleID,CreatedAt)
VALUES(@Event,@Method,@Code,@Seq,N'Pending',@Site,@Sample,SYSDATETIME());
UPDATE dbo.EM_PlanSamples SET EventID=@Event,Status=N'Ready for Results' WHERE PlanSampleID=@Sample;", connection, transaction);
                        plate.Parameters.AddExplicit("@Event", SqlDbType.Int, eventId); plate.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, sample.Method, size: 100);
                        plate.Parameters.AddExplicit("@Code", SqlDbType.NVarChar, sample.SampleCode, size: 100); plate.Parameters.AddExplicit("@Seq", SqlDbType.Int, seq);
                        plate.Parameters.AddExplicit("@Site", SqlDbType.NVarChar, Db(sample.SamplingLocation), size: 200); plate.Parameters.AddExplicit("@Sample", SqlDbType.Int, sample.PlanSampleID);
                        plate.ExecuteNonQuery();
                    }
                    events.Add(eventNo);
                }
                using (SqlCommand controls = new SqlCommand(@"
UPDATE dbo.EM_PlanSamples
SET Status=N'Control Passed'
WHERE PlanID=@Plan AND IsNegativeControl=1 AND NegativeControlResult=N'No Growth';", connection, transaction))
                {
                    controls.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                    controls.ExecuteNonQuery();
                }
                UpdatePlan(connection, transaction, plan.PlanID, "Ready for Results", signature.SignedBy, "Incubation Complete");
                InsertSignature(connection, transaction, plan.PlanID, "Released to Results", signature);
                Audit(connection, transaction, plan, "Incubation Complete", "Ready for Results", signature);
                if (developmentTimingOverrideUsed)
                {
                    DatabaseHelper.AddAuditTrailAdvanced(connection, transaction, "EM_Plans", plan.PlanID,
                        "Development Incubation Timing Override", "Planned incubation completion required",
                        "Early EM release permitted in Development only", signature.Reason,
                        signature.SignedBy, "PlannedIncubationEnd", null, plan.PlanNo, "Environmental Monitoring");
                }
            });
            LoadPlans();
            string resultMessage = developmentTimingOverrideUsed
                ? "Samples were released using the audited Development-only incubation timing override."
                : "Incubation completed and samples were released successfully.";
            MessageBox.Show(resultMessage + "\n\nEM Events ready in EM Results Entry:\n\n" + string.Join("\n", events), "Results Handoff", MessageBoxButton.OK, MessageBoxImage.Information);
            if (events.Count > 0 && App.ServiceProvider != null)
            {
                EMResultsEntry resultsWindow = App.ServiceProvider.GetRequiredService<EMResultsEntry>();
                resultsWindow.Owner = this;
                resultsWindow.Show();
                resultsWindow.OpenEvent(events[0]);
            }
        }

        private void CancelPlan_Click(object sender, RoutedEventArgs e)
            => ExecuteUi(CancelPlan);

        private void CancelPlan()
        {
            if (gridPlans.SelectedItem is not PlanRow plan) throw new InvalidOperationException("Select a plan first.");
            if (plan.Status is "Completed" or "Cancelled") throw new InvalidOperationException("This plan cannot be cancelled.");
            if (!DatabaseHelper.CanApproveResults(CurrentUser()))
                throw new InvalidOperationException("Approval permission is required to cancel an EM plan.");
            var signature = ConfirmSignature(plan.PlanNo, "Cancel EM Plan");
            if (signature == null) return;
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, signature.SignedBy, "CanApproveResults", "cancel an EM plan");
                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_Plans SET Status=N'Cancelled',CancelledBy=@User,CancelledAt=SYSDATETIME(),CancellationReason=@Reason
WHERE PlanID=@Plan AND Status NOT IN (N'Completed',N'Cancelled');
UPDATE dbo.EM_PlanSamples SET Status=N'Cancelled' WHERE PlanID=@Plan AND Status<>N'Completed';", connection, transaction);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100); command.Parameters.AddExplicit("@Reason", SqlDbType.NVarChar, signature.Reason, size: -1);
                command.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID);
                if (command.ExecuteNonQuery() < 1)
                    throw new DBConcurrencyException("The EM plan changed after it was loaded. Refresh and retry.");
                InsertSignature(connection, transaction, plan.PlanID, "Cancelled", signature);
                Audit(connection, transaction, plan, plan.Status, "Cancelled", signature);
            });
            LoadPlans();
        }

        private void Transition(string required, string next, string action, string meaning)
        {
            PlanRow plan = RequirePlan(required);
            var signature = ConfirmSignature(plan.PlanNo, action); if (signature == null) return;
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand samples = new SqlCommand("UPDATE dbo.EM_PlanSamples SET Status=@Status WHERE PlanID=@Plan;", connection, transaction);
                samples.Parameters.AddExplicit("@Status", SqlDbType.NVarChar, next, size: 50); samples.Parameters.AddExplicit("@Plan", SqlDbType.Int, plan.PlanID); samples.ExecuteNonQuery();
                UpdatePlan(connection, transaction, plan.PlanID, next, signature.SignedBy, required);
                InsertSignature(connection, transaction, plan.PlanID, meaning, signature);
                Audit(connection, transaction, plan, required, next, signature);
            });
            LoadPlans();
        }

        private PlanRow RequirePlan(params string[] statuses)
        {
            if (gridPlans.SelectedItem is not PlanRow plan)
                throw new InvalidOperationException("Select a plan first.");
            if (statuses == null || statuses.Length == 0 || !statuses.Any(status => plan.Status.Equals(status, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"This action requires status '{string.Join("' or '", statuses ?? Array.Empty<string>())}'. Current status: {plan.Status}.");
            return plan;
        }

        private static void UpdatePlan(SqlConnection connection, SqlTransaction transaction, int planId, string status, string user, string? expectedStatus = null)
        {
            string extra = status switch
            {
                "Distributed" => ",DistributedBy=@User,DistributedAt=SYSDATETIME()",
                "Collected" => ",CollectedBy=@User,CollectedAt=SYSDATETIME()",
                "Incubating Phase 1" => ",IncubatedBy=@User,IncubatedAt=SYSDATETIME()",
                _ => ""
            };
            using SqlCommand command = new SqlCommand($"UPDATE dbo.EM_Plans SET Status=@Status{extra} WHERE PlanID=@Plan AND (@Expected IS NULL OR Status=@Expected);", connection, transaction);
            command.Parameters.AddExplicit("@Status", SqlDbType.NVarChar, status, size: 50);
            command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, user, size: 100);
            command.Parameters.AddExplicit("@Plan", SqlDbType.Int, planId);
            command.Parameters.AddExplicit("@Expected", SqlDbType.NVarChar, string.IsNullOrWhiteSpace(expectedStatus) ? DBNull.Value : expectedStatus, size: 50);
            if (command.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("The EM plan was changed by another user. Reload and retry.");
        }

        private static void UpdatePlanFromStatuses(SqlConnection connection, SqlTransaction transaction, int planId,
            string status, string user, params string[] expectedStatuses)
        {
            if (expectedStatuses == null || expectedStatuses.Length == 0)
                throw new ArgumentException("At least one expected status is required.", nameof(expectedStatuses));
            var names = new List<string>();
            using SqlCommand command = new SqlCommand { Connection = connection, Transaction = transaction };
            for (int index = 0; index < expectedStatuses.Length; index++)
            {
                string name = "@Expected" + index.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddExplicit(name, SqlDbType.NVarChar, expectedStatuses[index], size: 50);
            }
            command.CommandText = $"UPDATE dbo.EM_Plans SET Status=@Status WHERE PlanID=@Plan AND Status IN ({string.Join(",", names)});";
            command.Parameters.AddExplicit("@Status", SqlDbType.NVarChar, status, size: 50);
            command.Parameters.AddExplicit("@Plan", SqlDbType.Int, planId);
            if (command.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("The EM plan was changed by another user. Reload and retry.");
        }

        private static void InsertSignature(SqlConnection connection, SqlTransaction transaction, int planId, string action, ElectronicSignature signature)
        {
            string currentRole = DatabaseHelper.EnsureActiveUserInTransaction(
                connection, transaction, signature.SignedBy, "sign an EM-plan action");
            using SqlCommand command = new SqlCommand(@"
INSERT dbo.EM_PlanSignatures(PlanID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature)
VALUES(@Plan,@Action,@Reason,@User,@Role,@Meaning);", connection, transaction);
            command.Parameters.AddExplicit("@Plan", SqlDbType.Int, planId); command.Parameters.AddExplicit("@Action", SqlDbType.NVarChar, action, size: 100);
            command.Parameters.AddExplicit("@Reason", SqlDbType.NVarChar, signature.Reason, size: -1); command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
            command.Parameters.AddExplicit("@Role", SqlDbType.NVarChar, currentRole, size: 100); command.Parameters.AddExplicit("@Meaning", SqlDbType.NVarChar, signature.Meaning, size: 500); command.ExecuteNonQuery();
        }

        private static void Audit(
            SqlConnection connection,
            SqlTransaction transaction,
            PlanRow plan,
            string oldValue,
            string newValue,
            ElectronicSignature signature) =>
            DatabaseHelper.AddAuditTrailAdvanced(
                connection, transaction,
                "EM_Plans", plan.PlanID, "EM Plan Status Changed", oldValue, newValue,
                signature.Reason, signature.SignedBy,
                "Status", null, plan.PlanNo, "Environmental Monitoring");

        private static bool IsDevelopmentAdminOverride()
        {
            string role = CurrentRole();
            return AppConfig.DevelopmentAdminFullPermissions &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        private static ElectronicSignature? ConfirmSignature(string record, string action)
        {
            var signature = new ElectronicSignature(record, CurrentUser(), action, true) { Owner = Application.Current.MainWindow };
            return signature.ShowDialog() == true && signature.IsConfirmed ? signature : null;
        }

        private void SaveSchedule_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                gridAreas.CommitEdit(DataGridEditingUnit.Cell, true);
                gridAreas.CommitEdit(DataGridEditingUnit.Row, true);
                List<AreaChoice> selectedAreas = _areas.Where(a => a.IsSelected).ToList();
                if (string.IsNullOrWhiteSpace(txtScheduleName.Text) || !dpScheduleNext.SelectedDate.HasValue)
                    throw new InvalidOperationException("Schedule name and next due date are required.");
                if (selectedAreas.Count == 0) throw new InvalidOperationException("Select at least one area for the schedule.");
                string method = cboMethod.SelectedItem?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(method)) throw new InvalidOperationException("Select the schedule method.");
                if (string.IsNullOrWhiteSpace(txtMedia.Text) || string.IsNullOrWhiteSpace(txtMediaLot.Text))
                    throw new InvalidOperationException("Media and released media preparation number are required for a schedule.");
                int mediaPreparationId = EnsureReleasedMediaPreparation(txtMedia.Text.Trim(), txtMediaLot.Text.Trim());
                if (method == "Personnel Monitoring" && (string.IsNullOrWhiteSpace(txtEmployeeId.Text) || string.IsNullOrWhiteSpace(txtEmployeeName.Text)))
                    throw new InvalidOperationException("Employee ID and name are required for a personnel schedule.");
                var signature = ConfirmSignature(txtScheduleName.Text.Trim(), "Create EM Schedule Revision"); if (signature == null) return;
                int scheduleId = 0;
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    int versionNo;
                    using (SqlCommand versionCommand = new SqlCommand(@"
SELECT ISNULL(MAX(VersionNo),0)+1
FROM dbo.EM_Schedules WITH(UPDLOCK,HOLDLOCK)
WHERE UPPER(LTRIM(RTRIM(ScheduleName)))=UPPER(LTRIM(RTRIM(@Name)));", connection, transaction))
                    {
                        versionCommand.Parameters.AddExplicit("@Name", SqlDbType.NVarChar, txtScheduleName.Text.Trim(), size: 200);
                        versionNo = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    using SqlCommand command = new SqlCommand(@"
INSERT dbo.EM_Schedules
(ScheduleName,PlanType,Frequency,NextDueDate,Method,SamplingLocation,MediaUsed,MediaLotNo,MediaPreparationID,
 IncludeNegativeControl,EmployeeID,EmployeeName,CreatedBy,ApprovalStatus,VersionNo,EffectiveFrom,ApprovedBy,ApprovedAt)
OUTPUT INSERTED.ScheduleID
VALUES(@Name,@Type,@Frequency,@Date,@Method,@Location,@Media,@Lot,@MediaPreparationID,1,@EmployeeID,@EmployeeName,@User,@ApprovalStatus,@VersionNo,@EffectiveFrom,@ApprovedBy,@ApprovedAt);", connection, transaction);
                    command.Parameters.AddExplicit("@Name", SqlDbType.NVarChar, txtScheduleName.Text.Trim(), size: 200);
                    command.Parameters.AddExplicit("@Type", SqlDbType.NVarChar, SelectedText(cboPlanType), size: 50);
                    command.Parameters.AddExplicit("@Frequency", SqlDbType.NVarChar, SelectedText(cboFrequency), size: 50);
                    command.Parameters.AddExplicit("@Date", SqlDbType.DateTime2, dpScheduleNext.SelectedDate.Value.Date);
                    command.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, method, size: 100);
                    command.Parameters.AddExplicit("@Location", SqlDbType.NVarChar, Db(txtLocation.Text), size: 200);
                    command.Parameters.AddExplicit("@Media", SqlDbType.NVarChar, txtMedia.Text.Trim(), size: 200);
                    command.Parameters.AddExplicit("@Lot", SqlDbType.NVarChar, txtMediaLot.Text.Trim(), size: 100);
                    command.Parameters.AddExplicit("@MediaPreparationID", SqlDbType.Int, mediaPreparationId);
                    command.Parameters.AddExplicit("@ApprovalStatus", SqlDbType.NVarChar, "Draft", size: 50);
                    command.Parameters.AddExplicit("@VersionNo", SqlDbType.Int, versionNo);
                    command.Parameters.AddExplicit("@EffectiveFrom", SqlDbType.Date, DBNull.Value);
                    command.Parameters.AddExplicit("@ApprovedBy", SqlDbType.NVarChar, DBNull.Value, size: 100);
                    command.Parameters.AddExplicit("@ApprovedAt", SqlDbType.DateTime2, DBNull.Value);
                    command.Parameters.AddExplicit("@EmployeeID", SqlDbType.NVarChar, Db(txtEmployeeId.Text), size: 100);
                    command.Parameters.AddExplicit("@EmployeeName", SqlDbType.NVarChar, Db(txtEmployeeName.Text), size: 200);
                    command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                    scheduleId = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    foreach (AreaChoice area in selectedAreas)
                    {
                        using SqlCommand areaCommand = new SqlCommand(
                            "INSERT dbo.EM_ScheduleAreas(ScheduleID,AreaID) VALUES(@Schedule,@Area);", connection, transaction);
                        areaCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, scheduleId);
                        areaCommand.Parameters.AddExplicit("@Area", SqlDbType.Int, area.Id);
                        areaCommand.ExecuteNonQuery();
                    }

                    InsertScheduleSignature(connection, transaction, scheduleId, "Schedule Revision Created", signature);
                    InsertAudit(connection, transaction, "EM_Schedules", scheduleId, "EM Schedule Revision Created",
                        string.Empty, "Draft", signature.Reason, signature.SignedBy, "ApprovalStatus",
                        txtScheduleName.Text.Trim() + " v" + versionNo.ToString(CultureInfo.InvariantCulture));
                });
                MessageBox.Show("Schedule saved.", "EM Schedule", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadSchedules();
            }
            catch (Exception ex) { ShowError(ex); }
        }


        private void ReviewSchedule_Click(object sender, RoutedEventArgs e) => ExecuteUi(ReviewSchedule);

        private void ReviewSchedule()
        {
            if (gridSchedules.SelectedItem is not ScheduleRow schedule)
                throw new InvalidOperationException("Select a schedule first.");
            if (!schedule.IsActive)
                throw new InvalidOperationException("Inactive schedules cannot be reviewed.");
            if (!schedule.ApprovalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only Draft schedules can be reviewed.");
            if (!DatabaseHelper.CanReviewResults(CurrentUser()))
                throw new UnauthorizedAccessException("Review permission is required to review an EM schedule.");
            if (!IsDevelopmentAdminOverride() &&
                schedule.CreatedBy.Equals(CurrentUser(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The schedule creator and reviewer must be different users.");

            ElectronicSignature signature = ConfirmSignature(schedule.ScheduleName, "Review EM Schedule")
                ?? throw new OperationCanceledException();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string reviewerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, signature.SignedBy, "CanReviewResults", "review an EM schedule");
                string createdBy;
                using (SqlCommand ownerCommand = new SqlCommand(
                    "SELECT ISNULL(CreatedBy,N'') FROM dbo.EM_Schedules WITH(UPDLOCK,HOLDLOCK) WHERE ScheduleID=@Schedule;",
                    connection, transaction))
                {
                    ownerCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                    createdBy = Convert.ToString(ownerCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
                }
                bool developmentAdministrator =
                    AppConfig.IsDevelopment &&
                    AppConfig.DevelopmentAdminFullPermissions &&
                    (reviewerRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                     reviewerRole.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
                if (!developmentAdministrator && createdBy.Equals(signature.SignedBy, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The schedule creator and reviewer must be different users.");

                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_Schedules
SET ApprovalStatus=N'Reviewed',ReviewedBy=@User,ReviewedAt=SYSDATETIME()
WHERE ScheduleID=@Schedule AND IsActive=1 AND ISNULL(ApprovalStatus,N'Draft')=N'Draft';", connection, transaction);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                command.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                if (command.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("The schedule changed after it was loaded. Refresh and try again.");
                InsertScheduleSignature(connection, transaction, schedule.ScheduleID, "Schedule Reviewed", signature);
                InsertAudit(connection, transaction, "EM_Schedules", schedule.ScheduleID, "Review EM Schedule", "Draft", "Reviewed", signature.Reason, signature.SignedBy, "ApprovalStatus", schedule.ScheduleName);
            });
            LoadSchedules();
        }

        private void ApproveSchedule_Click(object sender, RoutedEventArgs e) => ExecuteUi(ApproveSchedule);

        private void ApproveSchedule()
        {
            if (gridSchedules.SelectedItem is not ScheduleRow schedule)
                throw new InvalidOperationException("Select a schedule first.");
            if (!schedule.IsActive)
                throw new InvalidOperationException("Inactive schedules cannot be approved.");
            if (!schedule.ApprovalStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The schedule must be reviewed before approval.");
            if (!DatabaseHelper.CanApproveResults(CurrentUser()))
                throw new UnauthorizedAccessException("Approval permission is required to approve an EM schedule.");
            if (!IsDevelopmentAdminOverride() &&
                schedule.ReviewedBy.Equals(CurrentUser(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The schedule reviewer and approver must be different users.");

            ElectronicSignature signature = ConfirmSignature(schedule.ScheduleName, "Approve EM Schedule")
                ?? throw new OperationCanceledException();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand databaseDateCommand = new SqlCommand("SELECT CAST(SYSDATETIME() AS date);", connection, transaction);
                DateTime databaseToday = Convert.ToDateTime(databaseDateCommand.ExecuteScalar(), CultureInfo.InvariantCulture).Date;
                DateTime effectiveFrom = schedule.NextDueDate.Date < databaseToday ? databaseToday : schedule.NextDueDate.Date;
                string approverRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, signature.SignedBy, "CanApproveResults", "approve an EM schedule");
                string reviewedBy;
                using (SqlCommand reviewerCommand = new SqlCommand(
                    "SELECT ISNULL(ReviewedBy,N'') FROM dbo.EM_Schedules WITH(UPDLOCK,HOLDLOCK) WHERE ScheduleID=@Schedule;",
                    connection, transaction))
                {
                    reviewerCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                    reviewedBy = Convert.ToString(reviewerCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
                }
                bool developmentAdministrator =
                    AppConfig.IsDevelopment &&
                    AppConfig.DevelopmentAdminFullPermissions &&
                    (approverRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                     approverRole.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
                if (!developmentAdministrator && reviewedBy.Equals(signature.SignedBy, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The schedule reviewer and approver must be different users.");

                List<AreaChoice> approvedAreas = LoadScheduleAreas(connection, transaction, schedule.ScheduleID);
                if (approvedAreas.Count == 0)
                    throw new InvalidOperationException("The schedule has no active areas.");
                var approvedSeeds = approvedAreas
                    .SelectMany(area => BuildScheduleSeeds(connection, transaction, area, schedule.Method, schedule.SamplingLocation)
                        .Select(seed => (Area: area, Seed: seed)))
                    .ToList();
                int approvedPointCount = approvedSeeds.Count;
                if (approvedPointCount <= 0)
                    throw new InvalidOperationException("The approved schedule must contain at least one active sampling point.");

                using (SqlCommand existingSnapshot = new SqlCommand("SELECT COUNT(1) FROM dbo.EM_SchedulePointSnapshots WITH(UPDLOCK,HOLDLOCK) WHERE ScheduleID=@Schedule;", connection, transaction))
                {
                    existingSnapshot.Parameters.Add("@Schedule", SqlDbType.Int).Value = schedule.ScheduleID;
                    if (Convert.ToInt32(existingSnapshot.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new InvalidOperationException("This schedule revision already has an approved point snapshot. Create a new revision instead of overwriting approved evidence.");
                }

                int snapshotSequence = 0;
                foreach (var item in approvedSeeds)
                {
                    snapshotSequence++;
                    using SqlCommand snapshot = new SqlCommand(@"
INSERT dbo.EM_SchedulePointSnapshots
(ScheduleID,SnapshotSequence,AreaID,AreaCodeSnapshot,AreaNameSnapshot,GradeSnapshot,TemplateID,TemplateSequenceNo,MethodSnapshot,LocationSnapshot,CapturedBy)
VALUES(@Schedule,@Sequence,@Area,@AreaCode,@AreaName,@Grade,@TemplateID,@TemplateSequence,@Method,@Location,@User);", connection, transaction);
                    snapshot.Parameters.Add("@Schedule", SqlDbType.Int).Value = schedule.ScheduleID;
                    snapshot.Parameters.Add("@Sequence", SqlDbType.Int).Value = snapshotSequence;
                    snapshot.Parameters.Add("@Area", SqlDbType.Int).Value = item.Area.Id;
                    snapshot.Parameters.Add("@AreaCode", SqlDbType.NVarChar, 100).Value = item.Area.AreaCode;
                    snapshot.Parameters.Add("@AreaName", SqlDbType.NVarChar, 200).Value = item.Area.AreaName;
                    snapshot.Parameters.Add("@Grade", SqlDbType.NVarChar, 100).Value = Db(item.Area.Grade);
                    snapshot.Parameters.Add("@TemplateID", SqlDbType.Int).Value = item.Seed.TemplateID;
                    snapshot.Parameters.Add("@TemplateSequence", SqlDbType.Int).Value = item.Seed.TemplateSequenceNo;
                    snapshot.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = item.Seed.Method;
                    snapshot.Parameters.Add("@Location", SqlDbType.NVarChar, 200).Value = item.Seed.Location;
                    snapshot.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                    snapshot.ExecuteNonQuery();
                }

                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_Schedules
SET ApprovalStatus=N'Approved',ApprovedBy=@User,ApprovedAt=SYSDATETIME(),EffectiveFrom=@EffectiveFrom,
    ApprovedPointCount=@ApprovedPointCount
WHERE ScheduleID=@Schedule AND IsActive=1 AND ISNULL(ApprovalStatus,N'Draft')=N'Reviewed';", connection, transaction);
                command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                command.Parameters.AddExplicit("@EffectiveFrom", SqlDbType.Date, effectiveFrom);
                command.Parameters.AddExplicit("@ApprovedPointCount", SqlDbType.Int, approvedPointCount);
                command.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                if (command.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("The schedule changed after it was loaded. Refresh and try again.");

                using (SqlCommand supersedeCommand = new SqlCommand(@"
UPDATE dbo.EM_Schedules
SET IsActive=0,ApprovalStatus=N'Superseded'
WHERE ScheduleID<>@Schedule
  AND IsActive=1
  AND ISNULL(ApprovalStatus,N'Draft')=N'Approved'
  AND UPPER(LTRIM(RTRIM(ScheduleName)))=
      (SELECT UPPER(LTRIM(RTRIM(ScheduleName))) FROM dbo.EM_Schedules WHERE ScheduleID=@Schedule);", connection, transaction))
                {
                    supersedeCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                    supersedeCommand.ExecuteNonQuery();
                }

                InsertScheduleSignature(connection, transaction, schedule.ScheduleID, "Schedule Approved", signature);
                InsertAudit(connection, transaction, "EM_Schedules", schedule.ScheduleID, "Approve EM Schedule", "Reviewed", "Approved", signature.Reason, signature.SignedBy, "ApprovalStatus", schedule.ScheduleName);
            });
            LoadSchedules();
        }

        private void DeactivateSchedule_Click(object sender, RoutedEventArgs e) => ExecuteUi(DeactivateSchedule);

        private void DeactivateSchedule()
        {
            if (gridSchedules.SelectedItem is not ScheduleRow schedule)
                throw new InvalidOperationException("Select a schedule first.");
            if (!schedule.IsActive)
                throw new InvalidOperationException("The selected schedule is already inactive.");
            if (!DatabaseHelper.CanApproveResults(CurrentUser()))
                throw new UnauthorizedAccessException("QA approval permission is required to deactivate an EM schedule.");

            ElectronicSignature signature = ConfirmSignature(schedule.ScheduleName, "Deactivate EM Schedule")
                ?? throw new OperationCanceledException();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, signature.SignedBy, "CanApproveResults", "deactivate an EM schedule");
                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.EM_Schedules
SET IsActive=0,ApprovalStatus=N'Superseded'
WHERE ScheduleID=@Schedule AND IsActive=1;", connection, transaction);
                command.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                if (command.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("The schedule changed after it was loaded. Refresh and try again.");
                InsertScheduleSignature(connection, transaction, schedule.ScheduleID, "Schedule Deactivated", signature);
                InsertAudit(connection, transaction, "EM_Schedules", schedule.ScheduleID, "Deactivate EM Schedule", schedule.ApprovalStatus, "Superseded", signature.Reason, signature.SignedBy, "ApprovalStatus", schedule.ScheduleName);
            });
            LoadSchedules();
        }

        private static void InsertScheduleSignature(SqlConnection connection, SqlTransaction transaction, int scheduleId, string action, ElectronicSignature signature)
        {
            string currentRole = DatabaseHelper.EnsureActiveUserInTransaction(
                connection, transaction, signature.SignedBy, "sign an EM-schedule action");
            using SqlCommand command = new SqlCommand(@"
INSERT dbo.EM_ScheduleSignatures(ScheduleID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature)
VALUES(@Schedule,@Action,@Reason,@User,@Role,@Meaning);", connection, transaction);
            command.Parameters.AddExplicit("@Schedule", SqlDbType.Int, scheduleId);
            command.Parameters.AddExplicit("@Action", SqlDbType.NVarChar, action, size: 100);
            command.Parameters.AddExplicit("@Reason", SqlDbType.NVarChar, signature.Reason, size: -1);
            command.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
            command.Parameters.AddExplicit("@Role", SqlDbType.NVarChar, currentRole, size: 100);
            command.Parameters.AddExplicit("@Meaning", SqlDbType.NVarChar, signature.Meaning, size: 500);
            command.ExecuteNonQuery();
        }

        private static void InsertAudit(SqlConnection connection, SqlTransaction transaction, string tableName, int recordId,
            string action, string oldValue, string newValue, string reason, string user, string fieldName, string reference)
        {
            DatabaseHelper.AddAuditTrailAdvanced(
                connection,
                transaction,
                tableName,
                recordId,
                action,
                oldValue,
                newValue,
                reason,
                user,
                fieldName,
                null,
                reference,
                "Environmental Monitoring");
        }

        private void GenerateDuePlans_Click(object sender, RoutedEventArgs e) => ExecuteUi(GenerateDuePlans);
    }
}
