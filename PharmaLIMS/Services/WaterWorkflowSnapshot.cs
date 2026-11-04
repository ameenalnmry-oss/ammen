using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;

namespace PharmaLIMS.Services;

/// <summary>The same ordered owner-locked or SELECT-only clinical snapshot.</summary>
public sealed class WaterWorkflowSnapshot
{
    private static readonly string[] Names = { "Header", "Results", "Assignments", "Resources", "Executions", "Equipment", "Compatibility", "QualityEvents", "AffectedResults", "Signatures", "Certificates", "PlanSamples", "Plans", "Clock" };
    private readonly DataTable[] tables;
    public DataTable Header => tables[0];
    public DataTable Results => tables[1];
    public DataTable Assignments => tables[2];
    public DataTable Resources => tables[3];
    public DataTable Executions => tables[4];
    public DataTable Equipment => tables[5];
    public DataTable Compatibility => tables[6];
    public DataTable QualityEvents => tables[7];
    public DataTable AffectedResults => tables[8];
    public DataTable Signatures => tables[9];
    public DataTable Certificates => tables[10];
    public DataTable PlanSamples => tables[11];
    public DataTable Plans => tables[12];
    public DataTable Clock => tables[13];
    public DateTime DatabaseNow { get; }
    public string Hash => Fingerprint();

    public WaterWorkflowSnapshot(IEnumerable<DataTable> source)
    {
        tables = source.Select(t=>t.Copy()).ToArray();
        if(tables.Length!=Names.Length) throw new InvalidOperationException("Water snapshot requires exactly fourteen ordered result sets.");
        for(int i=0;i<Names.Length;i++) tables[i].TableName=Names[i];
        if(Header.Rows.Count!=1 || !Header.Columns.Contains("SampleID") || Clock.Rows.Count!=1 || !Clock.Columns.Contains("DatabaseNow"))
            throw new InvalidOperationException("Water sample or database clock is missing or ambiguous.");
        DatabaseNow = Convert.ToDateTime(Clock.Rows[0]["DatabaseNow"],CultureInfo.InvariantCulture);
    }
    public static WaterWorkflowSnapshot FromReader(SqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result=new List<DataTable>();
        do
        {
            if(result.Count==Names.Length) throw new InvalidOperationException("Unexpected Water snapshot result set.");
            var table=new DataTable(Names[result.Count]) { Locale=CultureInfo.InvariantCulture };
            for(int c=0;c<reader.FieldCount;c++) table.Columns.Add(reader.GetName(c),reader.GetFieldType(c));
            while(reader.Read()) { var values=new object[reader.FieldCount]; reader.GetValues(values); table.Rows.Add(values); }
            result.Add(table);
        } while(reader.NextResult());
        return new WaterWorkflowSnapshot(result);
    }
    public static WaterWorkflowSnapshot Load(SqlConnection connection, SqlTransaction transaction, int sampleId)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(transaction);
        if(sampleId<=0 || transaction.Connection!=connection) throw new ArgumentException("A sample and its existing transaction are required.");
        using var command=new SqlCommand(WaterWorkflowSqlContract.SnapshotQuery.Replace("{LOCK}","WITH (HOLDLOCK)",StringComparison.Ordinal),connection,transaction);
        command.Parameters.Add("@sampleId",SqlDbType.Int).Value=sampleId;
        using var reader=command.ExecuteReader();
        return FromReader(reader);
    }
    private string Fingerprint()
    {
        using var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
        {
            writer.Write("WaterWorkflowSnapshot/1");
            int sample=Convert.ToInt32(Header.Rows[0]["SampleID"],CultureInfo.InvariantCulture);
            for(int i=0;i<13;i++)
            {
                // These catalogs are locked for validation; unrelated catalog changes must not invalidate the loaded clinical grid.
                if(i is 5 or 6) continue;
                var table=tables[i];
                writer.Write(Names[i]);
                var columns=table.Columns.Cast<DataColumn>().ToArray();
                if(i==12) columns=columns.Where(c=>c.ColumnName is "WaterPlanID" or "Status").ToArray();
                writer.Write(columns.Length);
                foreach(var column in columns) { writer.Write(column.ColumnName); writer.Write(column.DataType.FullName??""); }
                var rows=table.Rows.Cast<DataRow>();
                if(i==11) rows=rows.Where(r=>r.Table.Columns.Contains("SampleID") && r["SampleID"]!=DBNull.Value && Convert.ToInt32(r["SampleID"],CultureInfo.InvariantCulture)==sample);
                // Binary row encoding and ordering also makes fixture/provider row ordering immaterial.
                var encoded=rows.Select(row=>EncodeRow(row,columns)).OrderBy(x=>Convert.ToHexString(x),StringComparer.Ordinal).ToArray();
                writer.Write(encoded.Length);
                foreach(var row in encoded) { writer.Write(row.Length); writer.Write(row); }
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static byte[] EncodeRow(DataRow row,DataColumn[] columns)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        foreach(var c in columns)
        {
            object value=row[c]; writer.Write(value!=DBNull.Value);
            if(value==DBNull.Value) continue;
            if(value is byte[] bytes) {writer.Write(bytes.Length);writer.Write(bytes);}
            else if(value is DateTime time) writer.Write(time.Ticks);
            else if(value is decimal number) foreach(int part in decimal.GetBits(number)) writer.Write(part);
            else if(value is double d) writer.Write(d);
            else if(value is float f) writer.Write(f);
            else if(value is bool b) writer.Write(b);
            else writer.Write(Convert.ToString(value,CultureInfo.InvariantCulture)??"");
        }
        writer.Flush(); return stream.ToArray();
    }
}
