using System.Data;
using System.Reflection;
using PharmaLIMS.Services;

internal static class CultureChildEvidenceProbe
{
    internal static int Run(bool requireLegacyRejection)
    {
        int unsafeAccepted=0,fullRejected=0;
        foreach(var change in new Action<DataSet>[] {
            ds=>ds.Tables[1].Rows[0]["FieldValue"]="LOAD-NEW",
            ds=>ds.Tables[1].Rows.Add(2,"MediaPreparation",1,"1035-L-0005/A6","Additives","new"),
            ds=>ds.Tables[1].Rows.Clear(),
            ds=>ds.Tables[2].Rows[0]["SignedBy"]="other preparer",
            ds=>ds.Tables[2].Rows.Add(2,"MediaPreparation",1,"Visual Check Prepared Media","reviewer"),
            ds=>ds.Tables[2].Rows.Clear() })
        {
            DataSet before=Data();DataSet after=before.Copy();change(after);
            bool blocked=false;
            try{CultureMediaPreparationGuard.EnsureAmendable((byte[])before.Tables[0].Rows[0]["WorkflowRowVersion"],(byte[])after.Tables[0].Rows[0]["WorkflowRowVersion"],false);}
            catch(DBConcurrencyException){blocked=true;}
            if(!blocked)unsafeAccepted++;
            var loaded=Snapshot(before);var current=Snapshot(after);
            try{MediaPreparationAmendmentSnapshot.EnsureCanAmend(loaded,current);}
            catch(DBConcurrencyException){fullRejected++;}
            foreach(int stage in new[]{1,2})
            {
                bool rejected=false;
                try{MediaPreparationAmendmentSnapshot.EnsureUnchanged(loaded,current);}catch(DBConcurrencyException){rejected=true;}
                if(!rejected)throw new Exception("Complete evidence comparison missed child-only review/release change.");
            }
        }
        Console.WriteLine($"CULTURE PORTABLE: original byte guard accepted {unsafeAccepted}/6 child-only changes; complete reference snapshot rejected {fullRejected}/6; review/release reference comparisons rejected 12/12. Hand-built data, not SQL/WPF execution.");
        if(unsafeAccepted!=6 || fullRejected!=6)throw new Exception("Incomplete Culture child-evidence reproduction.");
        if(requireLegacyRejection)throw new Exception("RED: actual local byte guard accepted changed SOP/signature evidence while parent version stayed the same.");
        return 0;
    }

    private static MediaPreparationAmendmentSnapshot Snapshot(DataSet ds)=>
        (MediaPreparationAmendmentSnapshot)typeof(MediaPreparationAmendmentSnapshot)
        .GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(DataSet)},null)!.Invoke(new object[]{ds});

    private static DataSet Data()
    {
        var ds=new DataSet();var parent=new DataTable("Preparation");
        foreach(var c in new[]{("MediaPreparationID",typeof(int)),("MediaLotID",typeof(int)),("ReleaseStatus",typeof(string)),("WorkflowRowVersion",typeof(byte[])),
            ("VisualCheckedBy",typeof(string)),("VisualCheckedAt",typeof(DateTime)),("SterilityReviewedBy",typeof(string)),("SterilityReviewedAt",typeof(DateTime)),("ReleasedBy",typeof(string)),("ReleasedAt",typeof(DateTime))})parent.Columns.Add(c.Item1,c.Item2);
        parent.Rows.Add(1,10,"Under Release",new byte[]{0,0,0,0,0,0,0,1},DBNull.Value,DBNull.Value,DBNull.Value,DBNull.Value,DBNull.Value,DBNull.Value);ds.Tables.Add(parent);
        var fields=new DataTable("SopFields");foreach(var c in new[]{("ID",typeof(int)),("EntityType",typeof(string)),("EntityID",typeof(int)),("AnnexureCode",typeof(string)),("FieldName",typeof(string)),("FieldValue",typeof(string))})fields.Columns.Add(c.Item1,c.Item2);
        fields.Rows.Add(1,"MediaPreparation",1,"1035-L-0005/A6","LoadNo","LOAD-OLD");ds.Tables.Add(fields);
        var signatures=new DataTable("Signatures");foreach(var c in new[]{("ID",typeof(int)),("EntityType",typeof(string)),("EntityID",typeof(int)),("ActionType",typeof(string)),("SignedBy",typeof(string))})signatures.Columns.Add(c.Item1,c.Item2);
        signatures.Rows.Add(1,"MediaPreparation",1,"Prepare Culture Media","preparer");ds.Tables.Add(signatures);
        return ds;
    }
}
