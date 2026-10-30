using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;

internal static partial class CultureUiBindingProbe
{
    private abstract partial class ViewBase
    {
        protected bool _isLoading;
        protected int _selectedPreparationId=1,_selectedPreparationPrintId=1,_selectedLotId=10;
        protected byte[]? _loadedPreparationVersion=new byte[]{0,0,0,0,0,0,0,1};
        protected MediaPreparationAmendmentSnapshot? _loadedPreparationSnapshot;
        protected bool _preparationLoadFailed,_newPreparationRequested=true;
        protected readonly Grid DgPreparations=new();
        internal DataRow? GridRow;
        internal string? Error;
        internal int FailAt=-1,Bindings;
        internal int Selected=>_selectedPreparationId;
        internal bool HasEvidence=>_loadedPreparationSnapshot!=null;
        internal bool Failed=>_preparationLoadFailed;
        internal bool NewRequested=>_newPreparationRequested;
        internal abstract void Load();internal abstract void New();
        internal void CheckCanSave()=>MediaPreparationAmendmentSnapshot.EnsureSaveSelection(_preparationLoadFailed,_newPreparationRequested,_selectedPreparationId);
        protected sealed class SelectionChangedEventArgs{}
        protected sealed class RoutedEventArgs{}
        protected sealed class Grid{public object? SelectedItem{get;set;}}
        protected sealed class Control
        {
            private readonly ViewBase owner;private string text="";private DateTime? date;private object? selected;
            internal Control(ViewBase owner){this.owner=owner;}
            private void Bind(){if(++owner.Bindings==owner.FailAt)throw new InvalidOperationException("Injected control binding failure.");}
            public string Text{get=>text;set{Bind();text=value;}}
            public DateTime? SelectedDate{get=>date;set{Bind();date=value;}}
            public object? SelectedValue{get=>selected;set{Bind();selected=value;}}
            public void Clear()=>Text="";
        }
        protected static class AppConfig{internal static int CommandTimeoutSeconds=>15;}
        protected static class DatabaseHelper{internal static Action<Action<SqlConnection,SqlTransaction>> Run=null!;internal static void ExecuteInTransaction(Action<SqlConnection,SqlTransaction> action)=>Run(action);}
        internal static void SetTransaction(SqlConnection c,SqlTransaction tx)
        {DatabaseHelper.Run=action=>action(c,tx);Connection=c;Transaction=tx;}
        private static SqlConnection Connection=null!;private static SqlTransaction Transaction=null!;
        protected DataRow? RowFromGrid(Grid grid)=>GridRow;
        protected static int RowInt(DataRow row,string col)=>Convert.ToInt32(row[col],CultureInfo.InvariantCulture);
        protected static string RowString(DataRow row,string col)=>Convert.ToString(row[col],CultureInfo.InvariantCulture) ?? "";
        protected static DateTime? RowDate(DataRow row,string col)=>row.IsNull(col)?null:Convert.ToDateTime(row[col],CultureInfo.InvariantCulture);
        protected static string NormalizeStatus(string status)=>status.Trim();
        protected void UpdatePreparationNextAction(){}
        protected void SetStatus(string text){}
        protected void ShowToast(string text,string icon){}
        protected void ShowError(string text,Exception ex)=>Error=text+": "+ex.Message;
        protected static void SetComboText(Control control,string text)=>control.Text=text;
        protected static string LoadSopField(string entity,int id,string annexure,string field)
        {
            using var cmd=new SqlCommand("SELECT TOP(1) FieldValue FROM dbo.CultureMediaSopFields WHERE EntityType=@Entity AND EntityID=@ID AND AnnexureCode=@Annexure AND FieldName=@Field",Connection,Transaction);
            cmd.Parameters.AddWithValue("@Entity",entity);cmd.Parameters.AddWithValue("@ID",id);cmd.Parameters.AddWithValue("@Annexure",annexure);cmd.Parameters.AddWithValue("@Field",field);
            return Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture) ?? "";
        }
    }

    internal static void Run(SqlConnection c)
    {
        using(var tx=c.BeginTransaction())
        {
            ViewBase.SetTransaction(c,tx);
            var initial=MediaPreparationAmendmentSnapshot.Read(c,tx,1,15).Preparation.Table.Copy();
            using(var change=new SqlCommand("UPDATE dbo.MediaPreparations SET FinalPH=N'7.2' WHERE MediaPreparationID=1; UPDATE dbo.CultureMediaSopFields SET FieldValue=N'LOAD-NEW' WHERE FieldName=N'LoadNo'",c,tx))change.ExecuteNonQuery();
            var old=new Frozen{GridRow=initial.Rows[0]};old.Load();
            var current=new Corrected{GridRow=initial.Rows[0]};current.Load();
            if(old.PH!="7.1"||old.LoadNumber!="LOAD-NEW"||current.PH!="7.2"||current.LoadNumber!="LOAD-NEW"||current.Failed||!current.HasEvidence||current.Selected!=1)
                throw new Exception("Atomic preparation display reproduction/fix failed.");
            current.CheckScope();
            var other=initial.Copy();other.Rows[0]["MediaPreparationID"]=2;current.GridRow=other.Rows[0];
            if(!CultureVisibleEvidenceProbe.Reject(current.CheckScope))throw new Exception("Grid/form scope mismatch was accepted.");
            current.GridRow=initial.Rows[0];
            var legacyFailure=new Frozen{GridRow=initial.Rows[0],FailAt=2};bool threw=false;
            try{legacyFailure.Load();}catch(InvalidOperationException){threw=true;}
            var failure=new Corrected{GridRow=initial.Rows[0],FailAt=2};failure.Load();
            if(!threw||legacyFailure.Selected!=1||!failure.Failed||failure.HasEvidence||failure.Selected!=0||failure.Error==null)
                throw new Exception("Partial binding did not invalidate all signing evidence.");
            if(!CultureVisibleEvidenceProbe.Reject(failure.CheckCanSave))throw new Exception("Failed load became a new preparation.");
            failure.FailAt=-1;failure.New();failure.GridRow=null;failure.Load();failure.CheckCanSave();
            if(!failure.NewRequested||failure.Failed||failure.Selected!=0)throw new Exception("Explicit New Preparation did not recover from failed loading.");
            using(var change=new SqlCommand("UPDATE dbo.MediaPreparations SET ReleaseStatus=N'Released' WHERE MediaPreparationID=1",c,tx))change.ExecuteNonQuery();
            var released=MediaPreparationAmendmentSnapshot.Read(c,tx,1,15).Preparation.Table.Copy();
            var locked=new Corrected{GridRow=released.Rows[0]};locked.Load();
            if(locked.Failed||locked.Selected!=0||locked.NewRequested||!CultureVisibleEvidenceProbe.Reject(locked.CheckCanSave))
                throw new Exception("Released preparation implicitly became a new batch.");
            tx.Rollback();
        }
        Console.WriteLine("PASS SITE CULTURE UI: actual selection/SOP/new handlers show a coherent SQL snapshot, reject partial binding, recover via explicit New, and prevent implicit copying of released records. Control adapters, not WPF instantiation.");
    }
}
