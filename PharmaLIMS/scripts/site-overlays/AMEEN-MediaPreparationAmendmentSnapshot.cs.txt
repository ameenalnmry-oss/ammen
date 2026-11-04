using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PharmaLIMS.Services
{
    /// <summary>Persisted preparation evidence, captured and compared while the parent and child ranges are locked.</summary>
    internal sealed class MediaPreparationAmendmentSnapshot
    {
        private readonly DataSet _evidence;
        public DataRow Preparation => _evidence.Tables[0].Rows[0];
        public string AuditValue { get; }

        private MediaPreparationAmendmentSnapshot(DataSet evidence)
        {
            if (evidence.Tables.Count != 3 || evidence.Tables[0].Rows.Count != 1)
                throw new DBConcurrencyException("The preparation no longer exists. Reload before continuing.");
            _evidence = evidence;
            AuditValue = JsonSerializer.Serialize(new
            {
                Preparation = AuditRows(evidence.Tables[0]),
                SopFields = AuditRows(evidence.Tables[1]),
                Signatures = AuditRows(evidence.Tables[2])
            });
        }

        private static object[] AuditRows(DataTable table) => table.Rows.Cast<DataRow>()
            .Select(row => table.Columns.Cast<DataColumn>().OrderBy(column => column.ColumnName, StringComparer.Ordinal)
                .ToDictionary(column => column.ColumnName, column => row.IsNull(column) ? null : row[column]))
            .OrderBy(row => JsonSerializer.Serialize(row), StringComparer.Ordinal).Cast<object>().ToArray();

        public static MediaPreparationAmendmentSnapshot Read(SqlConnection connection, SqlTransaction transaction, int preparationId, int commandTimeout)
        {
            using var command = new SqlCommand(@"
SELECT * FROM dbo.MediaPreparations WITH (UPDLOCK,HOLDLOCK)
WHERE MediaPreparationID=@MediaPreparationID;
SELECT * FROM dbo.CultureMediaSopFields WITH (UPDLOCK,HOLDLOCK)
WHERE EntityType=N'MediaPreparation' AND EntityID=@MediaPreparationID;
SELECT * FROM dbo.CultureMediaSignatures WITH (UPDLOCK,HOLDLOCK)
WHERE EntityType=N'MediaPreparation' AND EntityID=@MediaPreparationID;", connection, transaction)
            { CommandTimeout = commandTimeout };
            command.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = preparationId;
            using var adapter = new SqlDataAdapter(command);
            var evidence = new DataSet();
            adapter.Fill(evidence);
            return new MediaPreparationAmendmentSnapshot(evidence);
        }

        public string SopField(string annexureCode, string fieldName)
        {
            DataRow? field = _evidence.Tables[1].Rows.Cast<DataRow>().SingleOrDefault(row =>
                Convert.ToString(row["AnnexureCode"], CultureInfo.InvariantCulture) == annexureCode &&
                Convert.ToString(row["FieldName"], CultureInfo.InvariantCulture) == fieldName);
            return field == null || field.IsNull("FieldValue") ? string.Empty : Convert.ToString(field["FieldValue"], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public static void EnsureUnchanged(MediaPreparationAmendmentSnapshot? loaded, MediaPreparationAmendmentSnapshot current)
        {
            if (loaded == null || !string.Equals(loaded.AuditValue, current.AuditValue, StringComparison.Ordinal))
                throw new DBConcurrencyException("The preparation, its scientific fields or signatures changed after loading. Reload and review the current record before signing again.");
        }

        public static void EnsureCanAmend(MediaPreparationAmendmentSnapshot? loaded, MediaPreparationAmendmentSnapshot current)
        {
            DataRow row = current.Preparation;
            if (!string.Equals(Convert.ToString(row["ReleaseStatus"], CultureInfo.InvariantCulture), "Under Release", StringComparison.OrdinalIgnoreCase) ||
                new[] { "VisualCheckedBy", "VisualCheckedAt", "SterilityReviewedBy", "SterilityReviewedAt", "ReleasedBy", "ReleasedAt" }.Any(column => !row.IsNull(column)) ||
                current._evidence.Tables[2].Rows.Cast<DataRow>().Any(signature =>
                {
                    string action = Convert.ToString(signature["ActionType"], CultureInfo.InvariantCulture) ?? string.Empty;
                    return action.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
                           action.Contains("Visual Check", StringComparison.OrdinalIgnoreCase) ||
                           action.Contains("Release Prepared", StringComparison.OrdinalIgnoreCase);
                }))
                throw new DBConcurrencyException("The preparation is signed or no longer amendable. Reload before continuing.");
            EnsureUnchanged(loaded, current);
        }
        private static readonly (string Name, SqlDbType Type)[] VisibleParentContract =
        {
            ("MediaPreparationNo",SqlDbType.NVarChar),("MediaID",SqlDbType.Int),("MediaLotID",SqlDbType.Int),
            ("PreparationDate",SqlDbType.Date),("ExpiryDate",SqlDbType.Date),("QuantityPrepared",SqlDbType.NVarChar),
            ("PowderQuantityG",SqlDbType.Decimal),("PreparedBy",SqlDbType.NVarChar),("BatchSize",SqlDbType.NVarChar),
            ("FinalPH",SqlDbType.Decimal),("Appearance",SqlDbType.NVarChar),("AutoclaveCycleNo",SqlDbType.NVarChar),
            ("AutoclaveTemperature",SqlDbType.NVarChar),("AutoclaveHoldingTime",SqlDbType.NVarChar),
            ("Remarks",SqlDbType.NVarChar),("MediaPreparationID",SqlDbType.Int)
        };
        private static readonly string[] PreparationFields =
            { "LoadNo","MpmNo","PurifiedWaterMl","QuantityDispensed","BalanceNo","EquipmentCode","PhMeterNo","MethodOfSterilization","Additives" };
        private static readonly string[] VisualFields = { "EquipmentCode","VisualObservations","Conclusion","CheckedBy" };

        public static void EnsureVisibleSaved(MediaPreparationAmendmentSnapshot? loaded,
            IEnumerable<SqlParameter> visibleParent,
            IReadOnlyDictionary<(string Annexure,string Field),string> visibleSop,bool includeVisual)
        {
            if(loaded==null)throw new DBConcurrencyException("Reload the preparation before signing its scientific evidence.");
            var parameters=ReadVisibleParent(visibleParent);
            foreach(var field in VisibleParentContract)
                if(!loaded.Preparation.Table.Columns.Contains(field.Name) ||
                    !SameVisibleValue(parameters[field.Name].Value,loaded.Preparation[field.Name],field.Type))
                    throw new DBConcurrencyException("Save and reload changed preparation fields before independent review or release.");
            var required=PreparationFields.Select(f=>(Annexure:"1035-L-0005/A6",Field:f))
                .Concat(includeVisual?VisualFields.Select(f=>(Annexure:"1035-L-0005/A7",Field:f)):Array.Empty<(string Annexure,string Field)>()).ToArray();
            if(visibleSop==null || visibleSop.Count!=required.Length || required.Any(key=>!visibleSop.ContainsKey(key)))
                throw new DBConcurrencyException("A complete visible SOP evidence set is required before signing.");
            foreach(var key in required)
                if(!string.Equals((visibleSop[key] ?? string.Empty).Trim(),loaded.SopField(key.Annexure,key.Field).Trim(),StringComparison.Ordinal))
                    throw new DBConcurrencyException("Save and reload changed SOP fields before independent review or release.");
        }

        public static void EnsureInputsUnchanged(IEnumerable<SqlParameter> before,IEnumerable<SqlParameter> after,
            IReadOnlyDictionary<(string Annexure,string Field),string> beforeSop,
            IReadOnlyDictionary<(string Annexure,string Field),string> afterSop)
        {
            var original=ReadVisibleParent(before);var current=ReadVisibleParent(after);
            foreach(var field in VisibleParentContract)
                if(!SameVisibleValue(original[field.Name].Value,current[field.Name].Value,field.Type))
                    throw new DBConcurrencyException("Preparation input changed during electronic signing. Review it and sign again.");
            var required=PreparationFields.Select(f=>("1035-L-0005/A6",f)).Concat(VisualFields.Select(f=>("1035-L-0005/A7",f))).ToArray();
            if(beforeSop==null || afterSop==null || beforeSop.Count!=required.Length || afterSop.Count!=required.Length ||
                required.Any(key=>!beforeSop.ContainsKey(key) || !afterSop.ContainsKey(key)) ||
                beforeSop.Any(field=>!afterSop.TryGetValue(field.Key,out string? value) ||
                    !string.Equals((field.Value ?? string.Empty).Trim(),(value ?? string.Empty).Trim(),StringComparison.Ordinal)))
                throw new DBConcurrencyException("SOP input changed during electronic signing. Review it and sign again.");
        }

        public static void EnsureSaveSelection(bool loadFailed,bool newRequested,int selectedId)
        {
            if(loadFailed || selectedId<0 || (selectedId==0 && !newRequested))
                throw new DBConcurrencyException("Reload the saved preparation, or click New Preparation to start a new batch. Partially loaded, released or rejected records cannot become a new preparation implicitly.");
        }

        public static void EnsureSigningScope(int targetId,string displayedNo,MediaPreparationAmendmentSnapshot? original,
            int currentId,string currentDisplayedNo,MediaPreparationAmendmentSnapshot? current,bool loadFailed,bool newRequested)
        {
            if(loadFailed || targetId<0 || targetId!=currentId ||
                !string.Equals(displayedNo.Trim(),currentDisplayedNo.Trim(),StringComparison.Ordinal) ||
                (targetId==0 && (!newRequested || original!=null || current!=null)) ||
                (targetId>0 && (newRequested || original==null || current==null ||
                    Convert.ToInt32(original.Preparation["MediaPreparationID"],CultureInfo.InvariantCulture)!=targetId ||
                    Convert.ToInt32(current.Preparation["MediaPreparationID"],CultureInfo.InvariantCulture)!=targetId ||
                    !string.Equals(original.AuditValue,current.AuditValue,StringComparison.Ordinal))))
                throw new DBConcurrencyException("The selected preparation or its loaded evidence changed during signing. Reload, review and sign the intended record again.");
        }

        private static Dictionary<string,SqlParameter> ReadVisibleParent(IEnumerable<SqlParameter> source)
        {
            if(source==null)throw new DBConcurrencyException("Visible preparation fields were not captured.");
            var result=source.ToDictionary(p=>p.ParameterName.TrimStart('@'),StringComparer.OrdinalIgnoreCase);
            if(result.Count!=VisibleParentContract.Length || VisibleParentContract.Any(f=>!result.TryGetValue(f.Name,out SqlParameter? p) || p.SqlDbType!=f.Type))
                throw new DBConcurrencyException("The visible preparation field contract is incomplete or changed.");
            return result;
        }

        private static bool SameVisibleValue(object? left,object? right,SqlDbType type)
        {
            bool leftNull=left==null || left==DBNull.Value,rightNull=right==null || right==DBNull.Value;
            if(type==SqlDbType.NVarChar)
                return string.Equals(leftNull?string.Empty:Convert.ToString(left,CultureInfo.InvariantCulture)?.Trim(),
                    rightNull?string.Empty:Convert.ToString(right,CultureInfo.InvariantCulture)?.Trim(),StringComparison.Ordinal);
            if(leftNull || rightNull)return leftNull==rightNull;
            try
            {
                if(type==SqlDbType.Date)return Convert.ToDateTime(left,CultureInfo.InvariantCulture).Date==Convert.ToDateTime(right,CultureInfo.InvariantCulture).Date;
                return Convert.ToDecimal(left,CultureInfo.InvariantCulture)==Convert.ToDecimal(right,CultureInfo.InvariantCulture);
            }
            catch(Exception ex) when(ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {throw new DBConcurrencyException("Stored or visible scientific values are not comparable. Controlled correction is required.",ex);}
        }

        public static void EnsureCanVisualCheck(MediaPreparationAmendmentSnapshot? loaded,MediaPreparationAmendmentSnapshot current)
        {
            EnsureUnchanged(loaded,current);DataRow row=current.Preparation;
            if(!string.Equals(Convert.ToString(row["ReleaseStatus"],CultureInfo.InvariantCulture),"Under Release",StringComparison.OrdinalIgnoreCase) ||
                !row.IsNull("VisualCheckedBy") || !row.IsNull("VisualCheckedAt") || !row.IsNull("ReleasedBy") || !row.IsNull("ReleasedAt") ||
                current.HasSignatureAction("Visual Check") || current.HasSignatureAction("Release Prepared"))
                throw new DBConcurrencyException("The preparation already has visual/release evidence or is no longer under release. Controlled QA correction is required.");
        }

        public static void EnsureCanRelease(MediaPreparationAmendmentSnapshot? loaded,MediaPreparationAmendmentSnapshot current)
        {
            EnsureUnchanged(loaded,current);DataRow row=current.Preparation;
            if(!string.Equals(Convert.ToString(row["ReleaseStatus"],CultureInfo.InvariantCulture),"Under Release",StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(Convert.ToString(row["VisualCheckedBy"],CultureInfo.InvariantCulture)) || row.IsNull("VisualCheckedAt") ||
                !row.IsNull("ReleasedBy") || !row.IsNull("ReleasedAt") || current.HasSignatureAction("Release Prepared") ||
                !current.SopField("1035-L-0005/A7","Conclusion").Equals("Satisfactory",StringComparison.OrdinalIgnoreCase))
                throw new DBConcurrencyException("The preparation does not have unchanged signed visual evidence available for final release.");
        }

        private bool HasSignatureAction(string text)=>_evidence.Tables[2].Rows.Cast<DataRow>().Any(row=>
            (Convert.ToString(row["ActionType"],CultureInfo.InvariantCulture) ?? string.Empty).Contains(text,StringComparison.OrdinalIgnoreCase));

    }
}
