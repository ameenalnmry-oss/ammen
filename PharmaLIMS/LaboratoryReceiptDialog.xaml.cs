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
        // No prefilled timestamp: the recipient must enter the actual receipt time.
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        SaveButton.IsEnabled=false;
        try
        {
            if (!Login.CanRegisterSamples || string.IsNullOrWhiteSpace(Login.CurrentUser))
                throw new InvalidOperationException("Sample registration permission is required.");
            if (Confirmed.IsChecked!=true || !ReceiptDate.SelectedDate.HasValue ||
                !DateTime.TryParseExact(ReceiptTime.Text.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out DateTime time))
                throw new InvalidOperationException("Confirm the receipt checks and enter the actual date and time in HH:mm format.");
            DateTime received=ReceiptDate.SelectedDate.Value.Date.Add(time.TimeOfDay);
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
