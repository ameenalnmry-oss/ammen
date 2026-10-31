using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;

internal static class CultureVisibleEvidenceProbe
{
    internal static readonly (string Name,SqlDbType Type,object Value)[] Fields={
        ("MediaPreparationNo",SqlDbType.NVarChar,"MP-1"),("MediaID",SqlDbType.Int,1),("MediaLotID",SqlDbType.Int,10),
        ("PreparationDate",SqlDbType.Date,new DateTime(2026,10,1)),("ExpiryDate",SqlDbType.Date,new DateTime(2026,11,1)),
        ("QuantityPrepared",SqlDbType.NVarChar,"1000 mL"),("PowderQuantityG",SqlDbType.Decimal,10m),("PreparedBy",SqlDbType.NVarChar,"preparer"),
        ("BatchSize",SqlDbType.NVarChar,"1000 mL"),("FinalPH",SqlDbType.Decimal,7.1m),("Appearance",SqlDbType.NVarChar,"clear"),
        ("AutoclaveCycleNo",SqlDbType.NVarChar,"C1"),("AutoclaveTemperature",SqlDbType.NVarChar,"121"),("AutoclaveHoldingTime",SqlDbType.NVarChar,"15"),
        ("Remarks",SqlDbType.NVarChar,"controlled preparation"),("MediaPreparationID",SqlDbType.Int,1)};
    internal static readonly string[] A6={"LoadNo","MpmNo","PurifiedWaterMl","QuantityDispensed","BalanceNo","EquipmentCode","PhMeterNo","MethodOfSterilization","Additives"};
    internal static readonly string[] A7={"EquipmentCode","VisualObservations","Conclusion","CheckedBy"};

    internal static int Run()
    {
        var loaded=Snapshot(Data());var valid=Parameters();var sop=Sop(true);int rejected=0,expected=0;
        Call(loaded,valid,sop,true);
        foreach(var field in Fields)
        {
            var input=Parameters();var p=input.Single(x=>x.ParameterName=="@"+field.Name);
            p.Value=field.Value is DateTime d?d.AddDays(1):field.Value is int n?n+1:field.Value is decimal dec?dec+1:(object)(field.Value+" changed");
            expected++;if(Reject(()=>Call(loaded,input,sop,true)))rejected++;
        }
        foreach(var key in sop.Keys)
        {
            var changed=Sop(true);changed[key]+=" changed";expected++;if(Reject(()=>Call(loaded,valid,changed,true)))rejected++;
        }
        expected++;if(Reject(()=>Call(null,valid,sop,true)))rejected++;
        expected++;if(Reject(()=>Call(loaded,valid[..^1],sop,true)))rejected++;
        var missing=Sop(true);missing.Remove(("1035-L-0005/A6","LoadNo"));expected++;if(Reject(()=>Call(loaded,valid,missing,true)))rejected++;
        Call(loaded,valid,Sop(false),false);
        Console.WriteLine($"CULTURE VISIBLE: {rejected}/{expected} changed/missing scientific-input cases rejected; unchanged preparation and pre-visual A6 controls accepted. Pure data/helper execution, not WPF.");
        if(rejected!=expected)throw new Exception("RED: a persisted snapshot alone accepted unsaved visible scientific evidence.");
        return 0;
    }

    internal static bool Reject(Action action)
    {
        try{action();return false;}
        catch(DBConcurrencyException){return true;}
        catch(InvalidOperationException){return true;}
        catch(ArgumentException){return true;}
    }

    private static void Call(MediaPreparationAmendmentSnapshot? loaded,SqlParameter[] inputs,IReadOnlyDictionary<(string,string),string> sop,bool visual)
    {
        var method=typeof(MediaPreparationAmendmentSnapshot).GetMethod("EnsureVisibleSaved",BindingFlags.Static|BindingFlags.Public);
        if(method==null)
        {
            // The existing complete persisted comparison has no visible-input
            // argument. Execute it with unchanged persisted evidence as witness.
            if(loaded!=null)MediaPreparationAmendmentSnapshot.EnsureUnchanged(loaded,loaded);
            return;
        }
        try{method.Invoke(null,new object?[]{loaded,inputs,sop,visual});}
        catch(TargetInvocationException ex){throw ex.InnerException!;}
    }

    internal static SqlParameter[] Parameters()=>Fields.Select(f=>new SqlParameter("@"+f.Name,f.Type){Value=f.Value}).ToArray();
    internal static Dictionary<(string,string),string> Sop(bool visual)
    {
        var result=A6.ToDictionary(f=>("1035-L-0005/A6",f),f=>"value "+f);
        if(visual)foreach(string f in A7)result[("1035-L-0005/A7",f)]=f=="Conclusion"?"Satisfactory":f=="CheckedBy"?"reviewer":"value "+f;
        return result;
    }
    internal static MediaPreparationAmendmentSnapshot Snapshot(DataSet ds)=>(MediaPreparationAmendmentSnapshot)typeof(MediaPreparationAmendmentSnapshot).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(DataSet)},null)!.Invoke(new object[]{ds});
    internal static DataSet Data()
    {
        var ds=new DataSet();var p=new DataTable();
        foreach(var f in Fields)p.Columns.Add(f.Name,f.Value.GetType());
        foreach(var f in new[]{("ReleaseStatus",typeof(string)),("VisualCheckedBy",typeof(string)),("VisualCheckedAt",typeof(DateTime)),("SterilityReviewedBy",typeof(string)),("SterilityReviewedAt",typeof(DateTime)),("ReleasedBy",typeof(string)),("ReleasedAt",typeof(DateTime)),("WorkflowRowVersion",typeof(byte[]))})p.Columns.Add(f.Item1,f.Item2);
        var row=p.NewRow();foreach(var f in Fields)row[f.Name]=f.Value;row["ReleaseStatus"]="Under Release";row["WorkflowRowVersion"]=new byte[]{0,0,0,0,0,0,0,1};p.Rows.Add(row);ds.Tables.Add(p);
        var s=new DataTable();foreach(var c in new[]{("ID",typeof(int)),("EntityType",typeof(string)),("EntityID",typeof(int)),("AnnexureCode",typeof(string)),("FieldName",typeof(string)),("FieldValue",typeof(string))})s.Columns.Add(c.Item1,c.Item2);
        int id=0;foreach(var field in Sop(true))s.Rows.Add(++id,"MediaPreparation",1,field.Key.Item1,field.Key.Item2,field.Value);ds.Tables.Add(s);
        var sig=new DataTable();foreach(var c in new[]{("ID",typeof(int)),("EntityType",typeof(string)),("EntityID",typeof(int)),("ActionType",typeof(string)),("SignedBy",typeof(string))})sig.Columns.Add(c.Item1,c.Item2);
        sig.Rows.Add(1,"MediaPreparation",1,"Prepare Culture Media","preparer");ds.Tables.Add(sig);return ds;
    }
}
