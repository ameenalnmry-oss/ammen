using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
internal static partial class CultureUiBindingProbe
{
    private sealed class Frozen : ViewBase
    {
private void DgPreparations_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            var row = RowFromGrid(DgPreparations);
            if (row == null) return;

            string releaseStatus = NormalizeStatus(RowString(row, "ReleaseStatus"));
            _selectedPreparationPrintId = RowInt(row, "MediaPreparationID");

            TxtPreparationNo.Text = RowString(row, "MediaPreparationNo");

            if (CmbPreparationMediaLot != null)
                CmbPreparationMediaLot.SelectedValue = RowInt(row, "MediaLotID");

            DpPreparationDate.SelectedDate = RowDate(row, "PreparationDate");
            DpPreparationExpiry.SelectedDate = RowDate(row, "ExpiryDate");
            TxtQuantityPrepared.Text = RowString(row, "QuantityPrepared");
            TxtQuantityWeighedG.Text = RowString(row, "PowderQuantityG");
            TxtPreparedBy.Text = RowString(row, "PreparedBy");
            TxtBatchSize.Text = RowString(row, "BatchSize");
            TxtFinalPH.Text = RowString(row, "FinalPH");
            TxtAppearance.Text = RowString(row, "Appearance");
            TxtAutoclaveCycleNo.Text = RowString(row, "AutoclaveCycleNo");
            TxtAutoclaveTemperature.Text = RowString(row, "AutoclaveTemperature");
            TxtAutoclaveHoldingTime.Text = RowString(row, "AutoclaveHoldingTime");
            TxtPreparationRemarks.Text = RowString(row, "Remarks");
            if (TxtPreparationReleaseStatus != null)
                TxtPreparationReleaseStatus.Text = releaseStatus;
            LoadPreparationSopFields(RowInt(row, "MediaPreparationID"));

            if (releaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase))
            {
                _selectedPreparationId = RowInt(row, "MediaPreparationID");
                _loadedPreparationVersion = row["WorkflowRowVersion"] is byte[] version ? (byte[])version.Clone() : null;
                UpdatePreparationNextAction();
                SetStatus("Preparation selected for editing / release");
                return;
            }

            _selectedPreparationId = 0;
            _loadedPreparationVersion = null;
            UpdatePreparationNextAction();
            SetStatus("Released or rejected preparations are locked. Use New Preparation to create another batch.");
        }
private void LoadPreparationSopFields(int preparationId)
        {
            if (preparationId <= 0)
                return;

            TxtPreparationLoadNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "LoadNo");
            TxtPreparationMpmNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MpmNo");
            TxtPreparationPurifiedWaterMl.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PurifiedWaterMl");
            TxtPreparationQuantityDispensed.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "QuantityDispensed");
            TxtPreparationBalanceNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "BalanceNo");
            TxtPreparationEquipmentCode.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "EquipmentCode");
            TxtPreparationPhMeterNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PhMeterNo");
            TxtPreparationSterilizationMethod.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MethodOfSterilization");
            TxtPreparationAdditives.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "Additives");
            TxtPreparationVisualEquipmentCode.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "EquipmentCode");
            TxtPreparationVisualObservations.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "VisualObservations");
            SetComboText(CmbPreparationVisualConclusion, LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "Conclusion"));
            TxtPreparationVisualCheckedBy.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "CheckedBy");
        }
private void BtnNewPreparation_Click(object sender, RoutedEventArgs e)
        {
            _selectedPreparationId = 0;
            _loadedPreparationVersion = null;
            TxtPreparationNo.Text = "Generated on Save";
            DpPreparationDate.SelectedDate = DateTime.Today;
            DpPreparationExpiry.SelectedDate = DateTime.Today.AddDays(14);
            TxtQuantityPrepared.Clear();
            TxtQuantityWeighedG.Clear();
            TxtPreparedBy.Text = string.Empty;
            TxtBatchSize.Clear();
            TxtFinalPH.Clear();
            TxtAppearance.Clear();
            TxtAutoclaveCycleNo.Clear();
            TxtAutoclaveTemperature.Clear();
            TxtAutoclaveHoldingTime.Clear();
            TxtPreparationLoadNo.Clear();
            TxtPreparationMpmNo.Clear();
            TxtPreparationPurifiedWaterMl.Clear();
            TxtPreparationQuantityDispensed.Clear();
            TxtPreparationBalanceNo.Clear();
            TxtPreparationEquipmentCode.Clear();
            TxtPreparationPhMeterNo.Clear();
            TxtPreparationSterilizationMethod.Clear();
            TxtPreparationAdditives.Clear();
            TxtPreparationVisualEquipmentCode.Clear();
            TxtPreparationVisualObservations.Clear();
            SetComboText(CmbPreparationVisualConclusion, "Pending");
            TxtPreparationVisualCheckedBy.Text = string.Empty;
            TxtPreparationRemarks.Clear();
            if (TxtPreparationReleaseStatus != null) TxtPreparationReleaseStatus.Text = "Under Release";
            _selectedPreparationPrintId = 0;
            DgPreparations.SelectedItem = null;

            if (CmbPreparationMediaLot != null && _selectedLotId > 0)
                CmbPreparationMediaLot.SelectedValue = _selectedLotId;

            ShowToast("New preparation ready", "📋");
            SetStatus("New preparation ready");
        }
        internal override void Load()=>DgPreparations_SelectionChanged(this,new SelectionChangedEventArgs());
        internal override void New()=>BtnNewPreparation_Click(this,new RoutedEventArgs());
    }
    private sealed class Corrected : ViewBase
    {
private void DgPreparations_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            var row = RowFromGrid(DgPreparations);
            if (row == null)
            {
                _loadedPreparationSnapshot = null;
                _loadedPreparationVersion = null;
                _selectedPreparationId = 0;
                _selectedPreparationPrintId = 0;
                return;
            }
            InvalidatePreparationEvidence();
            try
            {
                MediaPreparationAmendmentSnapshot? loaded = null;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                    loaded = MediaPreparationAmendmentSnapshot.Read(conn, tx, RowInt(row, "MediaPreparationID"), AppConfig.CommandTimeoutSeconds));
                if (loaded == null) throw new DBConcurrencyException("Preparation evidence was not loaded.");
                row = loaded.Preparation;
                string releaseStatus = NormalizeStatus(RowString(row, "ReleaseStatus"));
                _selectedPreparationPrintId = RowInt(row, "MediaPreparationID");

                TxtPreparationNo.Text = RowString(row, "MediaPreparationNo");

                if (CmbPreparationMediaLot != null)
                    CmbPreparationMediaLot.SelectedValue = RowInt(row, "MediaLotID");

                DpPreparationDate.SelectedDate = RowDate(row, "PreparationDate");
                DpPreparationExpiry.SelectedDate = RowDate(row, "ExpiryDate");
                TxtQuantityPrepared.Text = RowString(row, "QuantityPrepared");
                TxtQuantityWeighedG.Text = RowString(row, "PowderQuantityG");
                TxtPreparedBy.Text = RowString(row, "PreparedBy");
                TxtBatchSize.Text = RowString(row, "BatchSize");
                TxtFinalPH.Text = RowString(row, "FinalPH");
                TxtAppearance.Text = RowString(row, "Appearance");
                TxtAutoclaveCycleNo.Text = RowString(row, "AutoclaveCycleNo");
                TxtAutoclaveTemperature.Text = RowString(row, "AutoclaveTemperature");
                TxtAutoclaveHoldingTime.Text = RowString(row, "AutoclaveHoldingTime");
                TxtPreparationRemarks.Text = RowString(row, "Remarks");
                if (TxtPreparationReleaseStatus != null)
                    TxtPreparationReleaseStatus.Text = releaseStatus;
                LoadPreparationSopFields(RowInt(row, "MediaPreparationID"), loaded);

                if (releaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedPreparationId = RowInt(row, "MediaPreparationID");
                    _loadedPreparationVersion = row["WorkflowRowVersion"] is byte[] version ? (byte[])version.Clone() : null;
                    _loadedPreparationSnapshot = loaded;
                    _preparationLoadFailed = false;
                    UpdatePreparationNextAction();
                    SetStatus("Preparation selected for editing / release");
                    return;
                }

                _selectedPreparationId = 0;
                _loadedPreparationVersion = null;
                _loadedPreparationSnapshot = loaded;
                _preparationLoadFailed = false;
                UpdatePreparationNextAction();
                SetStatus("Released or rejected preparations are locked. Use New Preparation to create another batch.");
            }
            catch (Exception ex)
            {
                InvalidatePreparationEvidence();
                ShowError("Error loading complete preparation evidence. Reload or select New Preparation before continuing", ex);
            }
        }
private void LoadPreparationSopFields(int preparationId, MediaPreparationAmendmentSnapshot snapshot)
        {
            if (preparationId <= 0)
                return;

            TxtPreparationLoadNo.Text = snapshot.SopField("1035-L-0005/A6", "LoadNo");
            TxtPreparationMpmNo.Text = snapshot.SopField("1035-L-0005/A6", "MpmNo");
            TxtPreparationPurifiedWaterMl.Text = snapshot.SopField("1035-L-0005/A6", "PurifiedWaterMl");
            TxtPreparationQuantityDispensed.Text = snapshot.SopField("1035-L-0005/A6", "QuantityDispensed");
            TxtPreparationBalanceNo.Text = snapshot.SopField("1035-L-0005/A6", "BalanceNo");
            TxtPreparationEquipmentCode.Text = snapshot.SopField("1035-L-0005/A6", "EquipmentCode");
            TxtPreparationPhMeterNo.Text = snapshot.SopField("1035-L-0005/A6", "PhMeterNo");
            TxtPreparationSterilizationMethod.Text = snapshot.SopField("1035-L-0005/A6", "MethodOfSterilization");
            TxtPreparationAdditives.Text = snapshot.SopField("1035-L-0005/A6", "Additives");
            TxtPreparationVisualEquipmentCode.Text = snapshot.SopField("1035-L-0005/A7", "EquipmentCode");
            TxtPreparationVisualObservations.Text = snapshot.SopField("1035-L-0005/A7", "VisualObservations");
            SetComboText(CmbPreparationVisualConclusion, snapshot.SopField("1035-L-0005/A7", "Conclusion"));
            TxtPreparationVisualCheckedBy.Text = snapshot.SopField("1035-L-0005/A7", "CheckedBy");
        }
private void BtnNewPreparation_Click(object sender, RoutedEventArgs e)
        {
            _newPreparationRequested = true;
            _preparationLoadFailed = false;
            _loadedPreparationSnapshot = null;
            _selectedPreparationId = 0;
            _loadedPreparationVersion = null;
            TxtPreparationNo.Text = "Generated on Save";
            DpPreparationDate.SelectedDate = DateTime.Today;
            DpPreparationExpiry.SelectedDate = DateTime.Today.AddDays(14);
            TxtQuantityPrepared.Clear();
            TxtQuantityWeighedG.Clear();
            TxtPreparedBy.Text = string.Empty;
            TxtBatchSize.Clear();
            TxtFinalPH.Clear();
            TxtAppearance.Clear();
            TxtAutoclaveCycleNo.Clear();
            TxtAutoclaveTemperature.Clear();
            TxtAutoclaveHoldingTime.Clear();
            TxtPreparationLoadNo.Clear();
            TxtPreparationMpmNo.Clear();
            TxtPreparationPurifiedWaterMl.Clear();
            TxtPreparationQuantityDispensed.Clear();
            TxtPreparationBalanceNo.Clear();
            TxtPreparationEquipmentCode.Clear();
            TxtPreparationPhMeterNo.Clear();
            TxtPreparationSterilizationMethod.Clear();
            TxtPreparationAdditives.Clear();
            TxtPreparationVisualEquipmentCode.Clear();
            TxtPreparationVisualObservations.Clear();
            SetComboText(CmbPreparationVisualConclusion, "Pending");
            TxtPreparationVisualCheckedBy.Text = string.Empty;
            TxtPreparationRemarks.Clear();
            if (TxtPreparationReleaseStatus != null) TxtPreparationReleaseStatus.Text = "Under Release";
            _selectedPreparationPrintId = 0;
            DgPreparations.SelectedItem = null;

            if (CmbPreparationMediaLot != null && _selectedLotId > 0)
                CmbPreparationMediaLot.SelectedValue = _selectedLotId;

            ShowToast("New preparation ready", "📋");
            SetStatus("New preparation ready");
        }
private void InvalidatePreparationEvidence()
        {
            _loadedPreparationSnapshot=null;
            _loadedPreparationVersion=null;
            _selectedPreparationId=0;
            _selectedPreparationPrintId=0;
            _preparationLoadFailed=true;
            _newPreparationRequested=false;
        }
private void EnsurePreparationSigningScope(int targetId,string displayedNo,MediaPreparationAmendmentSnapshot? original)
        {
            DataRow? selected = RowFromGrid(DgPreparations);
            if(targetId>0 && (selected==null || RowInt(selected,"MediaPreparationID")!=targetId))
                throw new DBConcurrencyException("The selected grid record differs from the loaded preparation. Reload and review the intended record before signing.");
            MediaPreparationAmendmentSnapshot.EnsureSigningScope(targetId,displayedNo,original,
                _selectedPreparationId,TxtPreparationNo.Text,_loadedPreparationSnapshot,
                _preparationLoadFailed,_newPreparationRequested);
        }
        internal override void Load()=>DgPreparations_SelectionChanged(this,new SelectionChangedEventArgs());
        internal override void New()=>BtnNewPreparation_Click(this,new RoutedEventArgs());
        internal void CheckScope()=>EnsurePreparationSigningScope(_selectedPreparationId,TxtPreparationNo.Text,_loadedPreparationSnapshot);
    }
    private abstract partial class ViewBase
    {
        protected readonly Control CmbPreparationMediaLot;
        protected readonly Control CmbPreparationVisualConclusion;
        protected readonly Control DpPreparationDate;
        protected readonly Control DpPreparationExpiry;
        protected readonly Control TxtAppearance;
        protected readonly Control TxtAutoclaveCycleNo;
        protected readonly Control TxtAutoclaveHoldingTime;
        protected readonly Control TxtAutoclaveTemperature;
        protected readonly Control TxtBatchSize;
        protected readonly Control TxtFinalPH;
        protected readonly Control TxtPreparationAdditives;
        protected readonly Control TxtPreparationBalanceNo;
        protected readonly Control TxtPreparationEquipmentCode;
        protected readonly Control TxtPreparationLoadNo;
        protected readonly Control TxtPreparationMpmNo;
        protected readonly Control TxtPreparationNo;
        protected readonly Control TxtPreparationPhMeterNo;
        protected readonly Control TxtPreparationPurifiedWaterMl;
        protected readonly Control TxtPreparationQuantityDispensed;
        protected readonly Control TxtPreparationReleaseStatus;
        protected readonly Control TxtPreparationRemarks;
        protected readonly Control TxtPreparationSterilizationMethod;
        protected readonly Control TxtPreparationVisualCheckedBy;
        protected readonly Control TxtPreparationVisualEquipmentCode;
        protected readonly Control TxtPreparationVisualObservations;
        protected readonly Control TxtPreparedBy;
        protected readonly Control TxtQuantityPrepared;
        protected readonly Control TxtQuantityWeighedG;
        protected ViewBase()
        {
            CmbPreparationMediaLot=new Control(this);
            CmbPreparationVisualConclusion=new Control(this);
            DpPreparationDate=new Control(this);
            DpPreparationExpiry=new Control(this);
            TxtAppearance=new Control(this);
            TxtAutoclaveCycleNo=new Control(this);
            TxtAutoclaveHoldingTime=new Control(this);
            TxtAutoclaveTemperature=new Control(this);
            TxtBatchSize=new Control(this);
            TxtFinalPH=new Control(this);
            TxtPreparationAdditives=new Control(this);
            TxtPreparationBalanceNo=new Control(this);
            TxtPreparationEquipmentCode=new Control(this);
            TxtPreparationLoadNo=new Control(this);
            TxtPreparationMpmNo=new Control(this);
            TxtPreparationNo=new Control(this);
            TxtPreparationPhMeterNo=new Control(this);
            TxtPreparationPurifiedWaterMl=new Control(this);
            TxtPreparationQuantityDispensed=new Control(this);
            TxtPreparationReleaseStatus=new Control(this);
            TxtPreparationRemarks=new Control(this);
            TxtPreparationSterilizationMethod=new Control(this);
            TxtPreparationVisualCheckedBy=new Control(this);
            TxtPreparationVisualEquipmentCode=new Control(this);
            TxtPreparationVisualObservations=new Control(this);
            TxtPreparedBy=new Control(this);
            TxtQuantityPrepared=new Control(this);
            TxtQuantityWeighedG=new Control(this);
        }
        internal string PH=>TxtFinalPH.Text;
        internal string LoadNumber=>TxtPreparationLoadNo.Text;
    }
}
