using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using PharmaLIMS.TrustedSigning;

namespace PharmaLIMS.Services;
public static partial class WaterWorkflowValidator
{
    private static (int[],WaterWorkflowExecutionPlan?) ValidateEvidence(WaterWorkflowSnapshot state,DataRow row,WaterWorkflowRow input,string stored)
    {
        string name=Text(row,"TestName"); int test=Int(row,"TestID");
        var equipment=state.Equipment.Rows.Cast<DataRow>().Select(e=>new LabEquipmentChoice
        {
            EquipmentID=Int(e,"EquipmentID"),EquipmentCode=Text(e,"EquipmentCode"),EquipmentName=Text(e,"EquipmentName"),EquipmentType=Text(e,"EquipmentType"),
            // Re-derive readiness from the locked catalog rather than trusting a cached or caller choice.
            IsAvailable=Bool(e,"IsActive") && Text(e,"EquipmentStatus")=="Active" &&
                Text(e,"QualificationStatus") is "Qualified" or "Due Soon" or "Not Required" &&
                (Date(e,"NextQualificationDate") is not DateTime q || q.Date>=state.DatabaseNow.Date) &&
                (!Bool(e,"CalibrationRequired") || Text(e,"CalibrationStatus") is "Calibrated" or "Due Soon" or "Not Required" &&
                (Date(e,"NextCalibrationDate") is not DateTime c || c.Date>=state.DatabaseNow.Date))
        }).ToArray();
        WaterResourceEvidenceService.Validate(name,stored,input.EquipmentId,input.SupportingEquipmentCodes,input.TestKitCode,input.TestKitLot,input.TestKitExpiry,equipment,state.DatabaseNow.Date);
        var supporting=WaterResourceEvidenceService.ParseCodes(input.SupportingEquipmentCodes).Select(code=>equipment.Single(e=>e.EquipmentCode.Equals(code,StringComparison.OrdinalIgnoreCase)).EquipmentID).ToArray();
        var rules=state.Compatibility.Rows.Cast<DataRow>().Where(r=>Text(r,"Module")=="WATER" && Text(r,"MatchKey")=="TESTID:"+test.ToString(CultureInfo.InvariantCulture) && Bool(r,"IsActive")).ToArray();
        // The installed assignment boundary applies the controlled compatibility rule to the primary instrument.
        if(input.EquipmentId is int primary)
        {
            var selected=equipment.Single(e=>e.EquipmentID==primary);
            Require(rules.Length==0 || rules.Any(r=>string.Equals(Text(r,"EquipmentType"),selected.EquipmentType,StringComparison.OrdinalIgnoreCase)),"The primary instrument type is not approved by the locked Water compatibility policy.");
        }
        WaterLesExecutionService.ValidateForSave(test,name,stored,input.LesSampleTemperatureC,input.LesVerificationReference,input.LesVerificationConfirmed);
        WaterWorkflowExecutionPlan? execution=null;
        if(WaterLesExecutionService.IsStructuredLesTest(test))
        {
            Require(input.EquipmentId.HasValue,"Structured Water LES requires a controlled primary instrument.");
            execution=new(WaterLesExecutionService.GetProcedureReference(test),WaterLesExecutionService.GetGuidance(test),
                test==3?WaterResultValueContract.FormatNumeric(WaterLesTemperatureContract.Parse(input.LesSampleTemperatureC)):null,
                input.LesVerificationReference.Trim(),input.LesVerificationConfirmed,input.LesExecutionRemarks.Trim());
        }
        else Require(string.IsNullOrWhiteSpace(input.LesSampleTemperatureC) && string.IsNullOrWhiteSpace(input.LesVerificationReference) && !input.LesVerificationConfirmed && string.IsNullOrWhiteSpace(input.LesExecutionRemarks),"LES fields apply only to structured Water tests.");
        return(supporting,execution);
    }
    private static void EnsureQualityGate(WaterWorkflowSnapshot state)
    {
        Require(state.QualityEvents.Columns.Contains("QualityEventID") && state.QualityEvents.Columns.Contains("CurrentStatus") && state.AffectedResults.Columns.Contains("QualityEventID") && state.AffectedResults.Columns.Contains("SampleTestID"),"Required quality-event evidence is unavailable.");
        var events=state.QualityEvents.Rows.Cast<DataRow>().ToArray();
        Require(events.All(e=>Text(e,"CurrentStatus") is "Closed" or "QA Closed" or "Cancelled" or "Rejected Closed"),"Open Water quality events prevent review or approval.");
        foreach(var result in state.Results.Rows.Cast<DataRow>().Where(r=>Text(r,"ResultStatus").ToUpperInvariant() is "OOS" or "ACTION" or "FAIL"))
        {
            int id=Int(result,"SampleTestID");
            bool disposition=state.AffectedResults.Rows.Cast<DataRow>().Where(a=>Int(a,"SampleTestID")==id).Any(a=>events.Any(e=>Int(e,"QualityEventID")==Int(a,"QualityEventID") && Text(e,"CurrentStatus")=="Closed" &&
                Text(e,"FinalDisposition") is "Accept with Justification" or "Release After Investigation" or "Released After Investigation" or "Retest Accepted" or "Resample Accepted"));
            Require(disposition,"A nonconforming Water result requires a linked closed quality event with an approved final disposition.");
        }
    }
    private static WaterWorkflowMutationPlan CorrectTimes(WaterWorkflowSnapshot state,WaterWorkflowRequest request)
    {
        Require(request.ExpectedStatus is not ("Under Review" or "Reviewed" or "Approved" or "COA Issued" or "Completed"),"Locked Water records cannot have timestamps corrected.");
        Require(state.Certificates.Rows.Cast<DataRow>().All(c=>Bool(c,"IsCancelled")),"A Water sample with an active certificate cannot have timestamps corrected.");
        var h=state.Header.Rows[0]; var t=request.Times!;
        DateTime? oldStart=Date(h,"IncubationStartedDateTime"),oldEnd=Date(h,"IncubationEndDate");
        Require(oldStart.HasValue==oldEnd.HasValue,"The locked incubation start/end pair is incomplete and needs approved remediation.");
        Require(t.IncubationStartedDateTime.HasValue==oldStart.HasValue,"Timestamp correction cannot add or remove the approved incubation program.");
        DateTime? derived=oldEnd;
        if(oldStart.HasValue)
        {
            TimeSpan duration=oldEnd!.Value-oldStart.Value;
            Require(duration>TimeSpan.Zero,"The approved incubation duration is invalid.");
            derived=t.IncubationStartedDateTime!.Value.Add(duration);
        }
        Require(t.IncubationEndDate==derived,"Incubation end must preserve the duration derived from the locked approved incubation program.");
        Require(t.SamplingDateTime is DateTime sampling && t.ReceivedDateTime is DateTime received && received>=sampling,"Sampling and receipt are required in chronological order.");
        Require(!t.IncubationStartedDateTime.HasValue || t.IncubationStartedDateTime>=t.ReceivedDateTime,"Incubation cannot precede receipt.");
        Require(!t.AnalysisStartedDateTime.HasValue || t.AnalysisStartedDateTime>=t.ReceivedDateTime && t.AnalysisStartedDateTime>=t.SamplingDateTime,"Analysis cannot precede sampling or receipt.");
        DateTime? completed=Date(h,"AnalysisCompletedDateTime");
        Require(!completed.HasValue || t.AnalysisStartedDateTime.HasValue && t.AnalysisStartedDateTime<=completed,"Analysis start must precede the recorded readonly completion time.");
        Require(new[]{t.SamplingDateTime,t.ReceivedDateTime,t.IncubationStartedDateTime,t.AnalysisStartedDateTime}.All(x=>!x.HasValue || x<=state.DatabaseNow.AddMinutes(1)),"Corrected manual timestamps cannot be in the future.");
        return Plan(request,request.ExpectedStatus,null,t with {IncubationEndDate=derived},false,Array.Empty<WaterWorkflowRowPlan>());
    }
}
