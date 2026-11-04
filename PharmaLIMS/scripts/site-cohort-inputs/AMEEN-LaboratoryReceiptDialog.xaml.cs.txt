using System.Data;
using System.Globalization;
using System.Windows;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;

namespace PharmaLIMS;
public partial class LaboratoryReceiptDialog : Window
{
    private readonly string kind,number;
    private readonly int id;
    public LaboratoryReceiptDialog(string kind,int id,string number)
    {
        InitializeComponent();
        this.kind=kind;this.id=id;this.number=number;
        Identity.Text=kind+" | "+number;
        Receiver.Text="Received by: "+Login.CurrentUser;
        // Display the server-side chronology before requesting the receipt signature.
        // This is only an explanatory preflight; LaboratoryReceiptSql.Insert remains
        // the authoritative locked validation inside the signed transaction.
        var range = LoadReceiptRange();
        SamplingChronology.Text = $"Sampling: {range.Sampled:yyyy-MM-dd HH:mm}  |  SQL server at opening: {range.ServerNow:yyyy-MM-dd HH:mm}";
        // No prefilled timestamp: the recipient must enter the actual receipt time.
    }
    private (DateTime Sampled, DateTime ServerNow) LoadReceiptRange()
    {
        string sql = kind == "PRM"
            ? @"SELECT SampleDateTime AS SampledAt, SYSDATETIME() AS ServerNow
                FROM dbo.PRM_Samples WHERE SampleID=@SourceID AND SampleNumber=@SourceNumber;"
            : kind == "EM"
                ? @"SELECT EventDate AS SampledAt, SYSDATETIME() AS ServerNow
                    FROM dbo.EM_Events WHERE Id=@SourceID AND EventNo=@SourceNumber;"
                : throw new InvalidOperationException("Unsupported laboratory receipt source.");
        DataTable snapshot = DatabaseHelper.ExecuteQuery(sql, new[]
        {
            new SqlParameter("@SourceID",SqlDbType.Int) { Value=id },
            new SqlParameter("@SourceNumber",SqlDbType.NVarChar,100) { Value=number }
        });
        if (snapshot.Rows.Count != 1 || snapshot.Rows[0].IsNull("SampledAt"))
            throw new InvalidOperationException("Source sampling time is unavailable. Verify the sample registration before receipt.");
        return (Convert.ToDateTime(snapshot.Rows[0]["SampledAt"],CultureInfo.InvariantCulture),
            Convert.ToDateTime(snapshot.Rows[0]["ServerNow"],CultureInfo.InvariantCulture));
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        SaveButton.IsEnabled=false;
        try
        {
            if (!Login.CanRegisterSamples || string.IsNullOrWhiteSpace(Login.CurrentUser))
                throw new InvalidOperationException("Sample registration permission is required.");
            if (Confirmed.IsChecked!=true || !ReceiptDate.SelectedDate.HasValue ||
                !DateTime.TryParseExact(ReceiptTime.Text.Trim(),new[] { "H:mm", "HH:mm" },CultureInfo.InvariantCulture,DateTimeStyles.None,out DateTime time))
                throw new InvalidOperationException("Confirm the receipt checks and enter the actual date and time in HH:mm format.");
            // Normalize display only; retain the exact entered hour and minute.
            ReceiptTime.Text = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            DateTime received=ReceiptDate.SelectedDate.Value.Date.Add(time.TimeOfDay);
            var chronology = LoadReceiptRange();
            SamplingChronology.Text = $"Sampling: {chronology.Sampled:yyyy-MM-dd HH:mm}  |  SQL server now: {chronology.ServerNow:yyyy-MM-dd HH:mm}";
            if (received < chronology.Sampled || received > chronology.ServerNow)
            {
                MessageBox.Show(
                    $"Receipt must be between sampling ({chronology.Sampled:yyyy-MM-dd HH:mm}) " +
                    $"and SQL server time ({chronology.ServerNow:yyyy-MM-dd HH:mm}).\n" +
                    $"Entered receipt: {received:yyyy-MM-dd HH:mm}.\n\n" +
                    "Enter the actual receipt time only. If the recorded sampling time is incorrect, " +
                    "request controlled correction of the sample record; do not invent another receipt time.",
                    "Receipt Chronology", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var signature=new ElectronicSignature(number,Login.CurrentUser,"Accepted Laboratory Receipt",true){Owner=this};
            if(signature.ShowDialog()!=true || !signature.IsConfirmed) return;
            if(!string.Equals(signature.SignedBy,Login.CurrentUser,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The receiver must be the authenticated signer.");
            DatabaseHelper.ExecuteInTransaction((connection,transaction)=>
            {
                string role=DatabaseHelper.EnsureUserPermissionInTransaction(connection,transaction,signature.SignedBy,
                    "CanRegisterSamples","record laboratory receipt");
                using var command=new SqlCommand(LaboratoryReceiptSql.Insert,connection,transaction);
                command.Parameters.AddRange(LaboratoryReceiptSql.Parameters(kind,id,number,received,signature.SignedBy,
                    signature.Reason,signature.Meaning,role,Environment.MachineName));
                int receiptId=Convert.ToInt32(command.ExecuteScalar(),CultureInfo.InvariantCulture);
                int auditId=DatabaseHelper.AddAuditTrailAdvanced(connection,transaction,"LaboratoryReceipts",receiptId,
                    "Accepted Laboratory Receipt","",$"Source={kind}:{id}; Received={received:yyyy-MM-dd HH:mm}; Decision=Accepted",
                    signature.Reason,signature.SignedBy,"Receipt",null,number,"Laboratory");
                if(auditId<=0) throw new InvalidOperationException("Required receipt audit evidence was not saved.");
            });
            DialogResult=true;
        }
        catch(Exception ex) {MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),"Laboratory Receipt",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally {SaveButton.IsEnabled=true;}
    }
}
