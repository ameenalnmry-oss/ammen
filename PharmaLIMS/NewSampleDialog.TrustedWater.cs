using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace PharmaLIMS;

public partial class NewSampleDialog
{
    private async System.Threading.Tasks.Task SaveTrustedWaterSampleAsync()
    {
        WaterRegistrationPolicy.RequireTarget(AppConfig.ConnectionString);
        string actor=Login.CurrentUser ?? throw new UnauthorizedAccessException("Sign in before registration.");
        var recovery=new WaterRegistrationRecovery();
        PendingWaterRegistration? pending=recovery.Load(actor);
        string action="WaterRegister";
        if(pending is not null)
        {
            var choice=MessageBox.Show(this,
                "An unresolved Water registration was saved for this account.\n\n"+
                "Yes: recover/retry the original saved registration.\n"+
                "No: cancel the original request safely on the service. If it already committed, its saved result will be returned.\n"+
                "Cancel: leave it unresolved. Do not create another registration.\n\nRequest: "+pending.RequestId,
                "Recover Water Registration",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
            if(choice==MessageBoxResult.Cancel) return;
            if(choice==MessageBoxResult.No) action="WaterCancel";
        }
        WaterRegistrationRequest payload=pending?.Payload ?? CaptureTrustedWaterRequest();
        var signature=ConfirmRegistrationSignature(pending?.RequestId.ToString() ?? "Number assigned on commit",
            action=="WaterCancel"?"Cancel Pending Water Registration":"Water Sample Registration");
        if(signature is null) return;
        if(!string.Equals(signature.SignedBy,actor,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Registration signature must match the active account.");
        // Durable recovery must succeed before the first network dispatch.
        pending ??=recovery.SaveNew(actor,payload);
        _waterOperationInProgress=true;
        WaterRegistrationResult result;
        try
        {
            try
            {
                result=await TrustedApplicationSession.WaterAsync(action,pending.RequestId,pending.Payload,
                    signature.SignedBy,signature.Reason,signature.Meaning);
            }
            catch(Exception ex)
            {
                ApplicationLogger.Error("Trusted Water registration outcome unconfirmed; request "+pending.RequestId.ToString("N")+". Recovery retained.",ex);
                MessageBox.Show(this,
                    "The registration outcome was not confirmed. Do not create another request.\n"+
                    "Your original registration is saved for recovery. Use Save Registration to recover or cancel that same request.\n\n"+
                    "Request: "+pending.RequestId+"\n\n"+UserFacingError.SafeMessage(ex),
                    "Water Registration Needs Recovery",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            if(result.State=="Pending")
                throw new InvalidOperationException("The original registration remains pending. Recover the same request.");
            if(result.State=="Cancelled")
            {
                recovery.Complete(actor,pending.RequestId);
                MessageBox.Show(this,"The original request was cancelled on the service. It cannot create a sample later.\nYou may now prepare a new registration.",
                    "Water Registration Cancelled",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            // From here registration is committed, regardless of cleanup, label preview or printer errors.
            RegisteredSampleId=result.SampleId;
            RegisteredSampleNumber=result.SampleNumber;
            RegisteredSampleStatus=result.Status;
            _isDirty=false;
            if(txtSampleNo is not null) txtSampleNo.Text=result.SampleNumber;
            try { recovery.Complete(actor,pending.RequestId); }
            catch(Exception ex)
            {
                ApplicationLogger.Error("Water registration committed but local recovery cleanup failed; request "+pending.RequestId.ToString("N")+".",ex);
                MessageBox.Show(this,"Registration succeeded. The local recovery record could not be cleared; the next recovery will return this same saved sample.",
                    "Registration Saved",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            MessageBox.Show(this,"Water sample registered successfully!\n\nSample No: "+result.SampleNumber+
                "\nStatus: "+result.Status+"\nTests Inserted: "+result.TestCount.ToString(CultureInfo.InvariantCulture),
                "Saved",MessageBoxButton.OK,MessageBoxImage.Information);
            if(!result.LabelPrinted && MessageBox.Show(this,"Do you want to preview and print the water sample label now?",
                "Water Sample Label",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            {
                try
                {
                    DataTable rows=DatabaseHelper.ExecuteQuery(
                        "SELECT PointCodeSnapshot,PointNameSnapshot FROM dbo.Samples WHERE SampleID=@id AND SampleNumber=@number;",
                        new[]{new SqlParameter("@id",result.SampleId),new SqlParameter("@number",result.SampleNumber)});
                    if(rows.Rows.Count!=1) throw new InvalidOperationException("Saved label snapshot is unavailable.");
                    string point=Convert.ToString(rows.Rows[0]["PointCodeSnapshot"],CultureInfo.InvariantCulture)+" - "+
                        Convert.ToString(rows.Rows[0]["PointNameSnapshot"],CultureInfo.InvariantCulture);
                    bool printed=LabelPrintingService.PrintWaterSampleLabel(this,result.SampleNumber,payload.WaterType,point,
                        payload.SamplingAt.ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture),payload.SampledBy,result.Status);
                    if(printed)
                    {
                        var printSignature=ConfirmRegistrationSignature(result.SampleNumber,"Water Sample Label Printed");
                        if(printSignature is null)
                            MessageBox.Show(this,"Registration succeeded. Label printing acknowledgement was cancelled; printing was not recorded.","Registration Saved",MessageBoxButton.OK,MessageBoxImage.Warning);
                        else
                            await TrustedApplicationSession.WaterAsync("WaterLabel",pending.RequestId,payload,
                                printSignature.SignedBy,printSignature.Reason,printSignature.Meaning);
                    }
                }
                catch(Exception ex)
                {
                    ApplicationLogger.Error("Water registration committed; label printing or acknowledgement failed for "+result.SampleNumber+".",ex);
                    MessageBox.Show(this,"Registration succeeded: "+result.SampleNumber+".\nLabel printing or its acknowledgement was not confirmed. Do not register the sample again.",
                        "Registration Saved — Label Needs Attention",MessageBoxButton.OK,MessageBoxImage.Warning);
                }
            }
        }
        finally { _waterOperationInProgress=false; }
        if(_isPlanRegistration) DialogResult=true;
        else Close();
    }

    private WaterRegistrationRequest CaptureTrustedWaterRequest()
    {
        if(cboPointCode.SelectedItem is not DataRowView point)
            throw new InvalidOperationException("Please select an active Water sampling point.");
        if(_isPlanRegistration && (!_waterPlanSampleId.HasValue || _plannedWaterTestIds.Count==0 || !_selectedTestIds.SetEquals(_plannedWaterTestIds)))
            throw new InvalidOperationException("The selected tests must match the distributed Water Plan.");
        if(!int.TryParse(txtContainerCount?.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out int containers))
            throw new InvalidOperationException("Container Count must be a whole number greater than zero.");
        string decision=GetComboSelectionText(cboReceiptDecision);
        bool incubate=decision=="Accepted" && HasSelectedMicrobiologicalTest();
        var request=new WaterRegistrationRequest(Convert.ToInt32(point["Id"],CultureInfo.InvariantCulture),
            IsPurifiedWaterMode()?"Purified Water":"Potable Water",_selectedTestIds.Order().ToArray(),
            DateTime.SpecifyKind(GetSelectedSamplingDateTime(),DateTimeKind.Unspecified),
            DateTime.SpecifyKind(GetReceivedInLabDateTime(),DateTimeKind.Unspecified),
            incubate?DateTime.SpecifyKind(GetIncubationStartedDateTime(),DateTimeKind.Unspecified):null,
            txtSampledBy.Text.Trim(),txtReceivedBy.Text.Trim(),containers,txtSampleVolume.Text.Trim(),
            GetComboSelectionText(cboContainerCondition),txtReceiptTemperature.Text.Trim(),decision,txtReceiptDeviationReason.Text.Trim(),
            incubate?GetComboSelectionText(cboWaterIncubationProgram):"",incubate?txtWaterIncubatorId.Text.Trim():"",
            _isPlanRegistration?_waterPlanSampleId:null);
        WaterRegistrationPolicy.Validate(request);
        return request;
    }
}
