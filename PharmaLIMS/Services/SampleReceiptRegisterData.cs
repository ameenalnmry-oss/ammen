using System.Data;
using System.Globalization;
namespace PharmaLIMS.Services;
internal static class SampleReceiptRegisterData
{
    private static string NormalizeType(string value) => SampleReceiptRegisterQuery.NormalizeType(value);
    internal static IReadOnlyList<TrendReportTable> Tables(DataTable source)
    {
        List<TrendReportTable> tables=new();
        foreach (var group in source.Rows.Cast<DataRow>().GroupBy(r=>NormalizeType(Text(r,"SampleType"))).OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            DataTable table=new();
            foreach (string name in new[] {"No","SampleNumber","Description","BatchOrLot","Quantity","Dates","SampledBy","ReceivedBy","RegisteredBy","ReceiptDecision","Tests","Status"}) table.Columns.Add(name);
            int number=0;
            foreach (DataRow row in group.OrderBy(r=>Date(r,"RegisteredDateTime")).ThenBy(r=>Text(r,"SampleNumber"),StringComparer.Ordinal).ThenBy(r=>Text(r,"RecordKind")).ThenBy(r=>Text(r,"SampleID"),StringComparer.Ordinal))
                table.Rows.Add((++number).ToString(CultureInfo.InvariantCulture),Text(row,"SampleNumber"),Text(row,"Description"),Text(row,"BatchOrLot"),Text(row,"Quantity"),
                    "S: "+DateText(row,"SamplingDateTime")+"\nR: "+DateText(row,"ReceivedDateTime")+"\nG: "+DateText(row,"RegisteredDateTime"),
                    Text(row,"SampledBy"),Text(row,"ReceivedBy"),Text(row,"RegisteredBy"),Text(row,"ReceiptDecision"),Text(row,"Tests"),Text(row,"Status"));
            tables.Add(new TrendReportTable(group.Key+" | "+table.Rows.Count+" record(s)",table,table.Columns.Cast<DataColumn>().Select(c=>c.ColumnName).ToArray()));
        }
        return tables;
    }
    internal static string Text(DataRow row,string name) => !row.Table.Columns.Contains(name)||row.IsNull(name)||string.IsNullOrWhiteSpace(Convert.ToString(row[name],CultureInfo.InvariantCulture)) ? "Not recorded" : Convert.ToString(row[name],CultureInfo.InvariantCulture)!.Trim();
    private static DateTime Date(DataRow row,string name) => row.Table.Columns.Contains(name)&&!row.IsNull(name) ? Convert.ToDateTime(row[name],CultureInfo.InvariantCulture) : DateTime.MinValue;
    private static string DateText(DataRow row,string name) => Date(row,name)==DateTime.MinValue ? "Not recorded" : Date(row,name).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
}
