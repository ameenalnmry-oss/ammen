using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using PharmaLIMS.TrustedSigning;

namespace PharmaLIMS.Services;

/// <summary>Pure validation. The host supplies the complete owner-locked snapshot; no SQL or caller scientific metadata is accepted.</summary>
public static partial class WaterWorkflowValidator
{
    public static WaterWorkflowMutationPlan Build(WaterWorkflowSnapshot state, WaterWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(state); WaterWorkflowPolicy.Validate(request);
        var header=state.Header.Rows[0];
        if(Int(header,"SampleID")!=request.SampleId || Text(header,"SampleNumber")!=request.SampleNumber ||
           Text(header,"Status")!=request.ExpectedStatus || !string.Equals(state.Hash,request.ExpectedSnapshot,StringComparison.OrdinalIgnoreCase))
            throw new DBConcurrencyException("The Water sample or its clinical evidence changed. Reload before signing.");
        string type=Text(header,"SampleType").Trim().ToLowerInvariant();
        if(!(type.Contains("water",StringComparison.Ordinal) || type.Contains("pws",StringComparison.Ordinal) ||
             type.Contains("ptws",StringComparison.Ordinal) || type is "purified" or "potable"))
            throw new InvalidOperationException("Only Water samples may use this boundary.");
        var scientific=Scientific(state);
        string status=request.ExpectedStatus;
        if(request.Operation=="StartAnalysis")
        {
            Require(status is "Registered" or "Incubation","Only Registered or Incubation samples may start analysis.");
            DateTime start=request.AnalysisStartedAt!.Value;
            Require(Date(header,"SamplingDateTime") is DateTime sampled && Date(header,"ReceivedDateTime") is DateTime received &&
                    start>=sampled && start>=received && start<=state.DatabaseNow.AddMinutes(1),"Analysis start must follow sampling and receipt and cannot be in the future.");
            return Plan(request,"In Analysis",Date(header,"AnalysisStartedDateTime")??start,null,false,Array.Empty<WaterWorkflowRowPlan>());
        }
        if(request.Operation=="CorrectTimes") return CorrectTimes(state,request);
        var source=state.Results.Rows.Cast<DataRow>().ToArray();
        Require(source.All(r=>Int(r,"SampleID")==request.SampleId),"Snapshot contains a result for another sample.");
        var visible=source.Where(r=>!scientific.IsRemovedTest(Text(r,"TestName"))).ToDictionary(r=>Int(r,"SampleTestID"));
        Require(visible.Count==request.Results.Length && request.Results.All(r=>visible.ContainsKey(r.SampleTestId)),"Save exactly the complete visible Water test set; duplicate, omitted or foreign result keys are forbidden.");
        var originals=DisplayRows(state).ToDictionary(r=>r.SampleTestId);
        bool pendingOnly=status is "Under Investigation" or "Released After Investigation";
        bool save=request.Operation=="SaveResults";
        if(save)
        {
            Require(status is "In Analysis" or "Partially Entered" or "Results Entered" or "Alert" or "Under Investigation" or "Released After Investigation", "This sample status cannot accept results.");
            Require(Date(header,"SamplingDateTime") is not null && Date(header,"ReceivedDateTime") is not null && Date(header,"AnalysisStartedDateTime") is not null,"Sampling, receipt and analysis start must be recorded before entering results.");
        }
        else
        {
            Require(request.Operation switch { "SubmitReview"=>status is "Results Entered" or "Alert" or "Released After Investigation", "Review"=>status=="Under Review", "Approve"=>status=="Reviewed", _=>false },"Invalid controlled workflow transition.");
            Require(request.Results.All(r=>r==originals[r.SampleTestId]),"Save and reload all visible results, remarks and resource/LES evidence before signing this operation.");
        }
        var rows=new List<WaterWorkflowRowPlan>();
        int pending=source.Count(r=>Null(r,"ResultValue")); int alerts=0; int oos=0;
        bool anyResultChanged=false; bool newOos=false;
        foreach(var input in request.Results.OrderBy(r=>r.SampleTestId))
        {
            var old=visible[input.SampleTestId]; var item=Item(state,old); var original=originals[input.SampleTestId];
            bool frozen=pendingOnly && !Null(old,"ResultValue");
            if(frozen) Require(input==original,"Only pending Water tests may change during or after investigation; completed result, remarks and all resource/LES evidence are frozen.");
            string oldRaw=Text(old,"ResultValue"); string stored=oldRaw;
            if(save && input.ResultValue!=original.ResultValue)
            {
                Require(!string.IsNullOrWhiteSpace(input.ResultValue) || Null(old,"ResultValue"),"An existing result cannot be silently cleared.");
                if(!string.IsNullOrWhiteSpace(input.ResultValue))
                {
                    item.ResultValue=input.ResultValue;
                    Require(scientific.TryNormalizeResultForSave(item,out decimal value),"Invalid controlled result for "+item.TestName+".");
                    Require(WaterResultValueContract.IsExactlyRepresentable(value),"The changed result cannot be stored exactly as decimal(18,4); rounding is forbidden.");
                    stored=WaterResultValueContract.FormatNumeric(value);
                }
            }
            bool valueChanged=!NumericEquivalent(stored,oldRaw);
            item.ResultValue=stored;
            item.Remarks=save && !frozen ? input.Remarks.Trim() : input.Remarks;
            string evaluated=scientific.CalculatePassFail(item);
            Require(evaluated is "PASS" or "ALERT" or "OOS" or "Pending","The frozen result or acceptance limits cannot be assessed for "+item.TestName+".");
            if(evaluated is "ALERT" or "OOS") Require(item.Remarks.Length>0,"Remarks are required for "+evaluated+" result "+item.TestName+".");
            bool complete=!string.IsNullOrWhiteSpace(stored);
            if(Null(old,"ResultValue") && complete) pending--;
            if(evaluated=="ALERT") alerts++;
            if(evaluated=="OOS") oos++;
            anyResultChanged|=valueChanged;
            newOos|=valueChanged && Null(old,"ResultValue") && evaluated=="OOS";
            bool writeEvidence=save && complete && !frozen;
            int[] supporting=Array.Empty<int>(); WaterWorkflowExecutionPlan? execution=null;
            if(writeEvidence)
            {
                if(Text(old,"Category").Contains("MICRO",StringComparison.OrdinalIgnoreCase))
                    Require(Date(header,"IncubationEndDate") is DateTime end && end<=state.DatabaseNow,"Microbiological Water results require completed recorded incubation.");
                (supporting,execution)=ValidateEvidence(state,old,input,stored);
            }
            rows.Add(new WaterWorkflowRowPlan(input.SampleTestId,save && !frozen && valueChanged,
                complete?stored:null,string.IsNullOrEmpty(item.Remarks)?null:item.Remarks,
                valueChanged?evaluated:Text(old,"ResultStatus"),valueChanged?scientific.BuildDeviationType(item,evaluated):NullableText(old,"DeviationType"),
                writeEvidence,input.EquipmentId,supporting,EmptyNull(input.TestKitCode),EmptyNull(input.TestKitLot),input.TestKitExpiry,
                stored,execution,Version(old)));
        }
        if(!save)
        {
            Require(pending==0,"All Water tests, including hidden historical rows, must be complete before review or approval.");
            EnsureQualityGate(state);
            // SQL independently enforces current role, permission and signer separation from locked signatures.
            return Plan(request,request.Operation switch {"SubmitReview"=>"Under Review","Review"=>"Reviewed",_=>"Approved"},null,null,false,Array.Empty<WaterWorkflowRowPlan>());
        }
        string next=oos>0?"Under Investigation":pending>0?"Partially Entered":alerts>0?"Alert":"Results Entered";
        if(status=="Released After Investigation" && !newOos) next="Released After Investigation";
        return Plan(request,next,null,null,pending==0 && anyResultChanged,rows.ToArray());
    }

    /// <summary>The installed deterministic display formatter and complete stored visible evidence, also usable by the desktop.</summary>
    public static WaterWorkflowRow[] DisplayRows(WaterWorkflowSnapshot state)
    {
        var scientific=Scientific(state);
        return state.Results.Rows.Cast<DataRow>().Where(r=>!scientific.IsRemovedTest(Text(r,"TestName"))).Select(row=>
        {
            int id=Int(row,"SampleTestID"); var resource=state.Resources.Rows.Cast<DataRow>().Where(r=>Int(r,"SampleTestID")==id).ToArray();
            var les=state.Executions.Rows.Cast<DataRow>().SingleOrDefault(r=>Int(r,"SampleTestID")==id);
            int? primary=NullableInt(row,"EquipmentID");
            // Same first-instrument/kit interpretation and insertion order as installed LoadLatest.
            int? resourcePrimary=null; string kit="",lot=""; DateTime? expiry=null; var extras=new List<string>();
            foreach(var r in resource)
            {
                if(Text(r,"ResourceKind")=="TEST_KIT") {kit=Text(r,"KitCode");lot=Text(r,"KitLot");expiry=Date(r,"KitExpiry");}
                else if(Text(r,"ResourceKind")=="INSTRUMENT" && NullableInt(r,"EquipmentID") is int instrument)
                {
                    if(resourcePrimary is null && string.IsNullOrWhiteSpace(kit)) resourcePrimary=instrument;
                    else if(Text(r,"EquipmentCodeSnapshot").Length>0) extras.Add(Text(r,"EquipmentCodeSnapshot"));
                }
            }
            return new WaterWorkflowRow(id,scientific.FormatResultForDisplay(Item(state,row)),Text(row,"Remarks"),primary??resourcePrimary,
                string.Join(", ",extras),kit,lot,expiry,les is null || Null(les,"SampleTemperatureC")?"":Convert.ToDecimal(les["SampleTemperatureC"],CultureInfo.InvariantCulture).ToString("0.##",CultureInfo.InvariantCulture),
                les is null?"":Text(les,"VerificationReference"),les is not null && Bool(les,"VerificationConfirmed"),les is null?"":Text(les,"ExecutionRemarks"));
        }).ToArray();
    }
    private static WaterWorkflowScientific Scientific(WaterWorkflowSnapshot state)=>new(Text(state.Header.Rows[0],"SampleType"),Text(state.Header.Rows[0],"EffectivePointCode"));
    private static WaterWorkflowScientificItem Item(WaterWorkflowSnapshot state,DataRow row)
    {
        var item=new WaterWorkflowScientificItem {SampleTestID=Int(row,"SampleTestID"),TestID=Int(row,"TestID"),TestName=Text(row,"TestName"),Unit=Text(row,"Unit"),
            AlertLimit=Number(row,"AlertLimit"),ActionLimit=Number(row,"ActionLimit"),SpecificationText=Text(row,"LimitDescription"),ResultValue=Text(row,"ResultValue"),Remarks=Text(row,"Remarks")};
        if(Int(row,"HasSpecSnapshot")==0) Scientific(state).ApplyEffectiveSpecification(item);
        return item;
    }
    private static WaterWorkflowMutationPlan Plan(WaterWorkflowRequest r,string status,DateTime? start,WaterWorkflowTimes? times,bool completed,WaterWorkflowRowPlan[] rows)=>
        new(1,r.Operation,r.SampleId,r.ExpectedStatus,r.ExpectedSnapshot.ToUpperInvariant(),status,start,times,completed,rows);
    private static bool NumericEquivalent(string left,string right)=>left==right || ControlledNumericValue.TryParse(left,out decimal a) && ControlledNumericValue.TryParse(right,out decimal b) && a==b;
    private static string Version(DataRow row) => row.Table.Columns.Contains("ResultRowVersion") && row["ResultRowVersion"] is byte[] bytes && bytes.Length==8 ? Convert.ToBase64String(bytes) : throw new InvalidOperationException("The authoritative result rowversion is missing.");
    private static bool Null(DataRow row,string column)=>!row.Table.Columns.Contains(column) || row[column]==DBNull.Value;
    private static string Text(DataRow row,string column)=>Null(row,column)?"":Convert.ToString(row[column],CultureInfo.InvariantCulture)??"";
    private static string? NullableText(DataRow row,string column)=>Null(row,column)?null:Text(row,column);
    private static int Int(DataRow row,string column)=>Convert.ToInt32(row[column],CultureInfo.InvariantCulture);
    private static int? NullableInt(DataRow row,string column)=>Null(row,column)?null:Int(row,column);
    private static DateTime? Date(DataRow row,string column)=>Null(row,column)?null:Convert.ToDateTime(row[column],CultureInfo.InvariantCulture);
    private static decimal? Number(DataRow row,string column)=>Null(row,column)?null:Convert.ToDecimal(row[column],CultureInfo.InvariantCulture);
    private static bool Bool(DataRow row,string column)=>!Null(row,column) && Convert.ToBoolean(row[column],CultureInfo.InvariantCulture);
    private static string? EmptyNull(string value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static void Require(bool condition,string message) {if(!condition) throw new InvalidOperationException(message);}
}
