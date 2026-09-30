using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PharmaLIMS.Services;

namespace PharmaLIMS;

public partial class SampleReceiptRegister : Window
{
    private DataTable? loaded;
    private bool busy;
    private string scope = "";
    public SampleReceiptRegister()
    {
        InitializeComponent();
        TypeFilter.ItemsSource=SampleReceiptRegisterQuery.Types;
        TypeFilter.SelectedIndex=0; DateBasis.SelectedIndex=0;
        DateFrom.SelectedDate=DateTime.Today.AddMonths(-1); DateTo.SelectedDate=DateTime.Today;
    }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        loaded=null;
        if (RegisterGrid!=null) RegisterGrid.ItemsSource=null;
        if (ExportButton!=null) ExportButton.IsEnabled=false;
        if (RegisterStatus!=null) RegisterStatus.Text="Filters changed. Load Register to refresh the records.";
    }
    private async void Load_Click(object sender,RoutedEventArgs e)
    {
        if (busy) return;
        busy=true; FilterChanged(sender,e); SetBusy(true);
        try
        {
            if (!DateFrom.SelectedDate.HasValue || !DateTo.SelectedDate.HasValue) throw new InvalidOperationException("Both dates are required.");
            string type=Convert.ToString(TypeFilter.SelectedItem,CultureInfo.InvariantCulture) ?? "All";
            DateTime from=DateFrom.SelectedDate.Value, to=DateTo.SelectedDate.Value;
            bool receipt=DateBasis.SelectedIndex==1;
            var parameters=SampleReceiptRegisterQuery.Parameters(type,from,to,receipt);
            DataTable table=await Task.Run(()=>DatabaseHelper.ExecuteQuery(SampleReceiptRegisterQuery.Query,parameters,commandTimeoutSeconds:60));
            loaded=table;
            scope=$"{type} | {from:yyyy-MM-dd} to {to:yyyy-MM-dd} inclusive | Date basis: {(receipt ? "Recorded laboratory receipt" : "Registration")}";
            RegisterGrid.ItemsSource=table.DefaultView;
            int missing=table.Rows.Cast<DataRow>().Count(r=>r.IsNull("ReceivedDateTime"));
            RegisterStatus.Text=$"{table.Rows.Count} source record(s). {missing} without a recorded lab receipt date. {scope}";
            ExportButton.IsEnabled=table.Rows.Count>0;
        }
        catch (Exception ex)
        {
            loaded=null; RegisterGrid.ItemsSource=null; ExportButton.IsEnabled=false;
            RegisterStatus.Text="Load failed; no records are available for export.";
            MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),"Sample Register",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally {busy=false; SetBusy(false);}
    }
    private void Receipt_Click(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        try
        {
            if(!Login.CanRegisterSamples) throw new InvalidOperationException("Sample registration permission is required.");
            if(RegisterGrid.SelectedItem is not DataRowView row) throw new InvalidOperationException("Select one PRM sample or EM event from the loaded register.");
            if(!row.Row.IsNull("ReceivedDateTime")) throw new InvalidOperationException("This record already has laboratory receipt evidence.");
            string kind=Convert.ToString(row["RecordKind"],CultureInfo.InvariantCulture) ?? "";
            if(kind is not ("PRM" or "EM")) throw new InvalidOperationException("Water receipt is captured during water registration.");
            var dialog=new LaboratoryReceiptDialog(kind,Convert.ToInt32(row["SampleID"],CultureInfo.InvariantCulture),
                Convert.ToString(row["SampleNumber"],CultureInfo.InvariantCulture) ?? ""){Owner=this};
            if(dialog.ShowDialog()==true) Load_Click(sender,e);
        }
        catch(Exception ex) {MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),"Laboratory Receipt",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void SetBusy(bool value)
    {
        ReceiptButton.IsEnabled=!value && Login.CanRegisterSamples; LoadButton.IsEnabled=!value; TypeFilter.IsEnabled=!value; DateBasis.IsEnabled=!value;
        DateFrom.IsEnabled=!value; DateTo.IsEnabled=!value;
    }
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        if (busy || loaded==null || loaded.Rows.Count==0) return;
        try
        {
            string user=Login.CurrentUser ?? "";
            if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("A signed-in user is required to export the register.");
            var dialog=new SaveFileDialog {Filter="PDF report (*.pdf)|*.pdf",FileName="MEDICA_Sample_Register_"+DateTime.Now.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+".pdf"};
            if (dialog.ShowDialog(this)!=true) return;
            DateTime generated=DateTime.Now;
            var tables=SampleReceiptRegisterData.Tables(loaded);
            List<string> summary=new() {"Total source records: "+loaded.Rows.Count,"Dates legend: S = Sampled; R = Laboratory received; G = Registered. All timestamps include date and time.","One water / PRM sample or EM event per register row. EM methods and plates appear in the Tests column.",
                "Date basis is stated in the scope. Lab receipt date is shown only when explicitly documented. Raw-material GRN receipt is not treated as laboratory receipt.",
                "Dates are shown as stored in the source system; no timezone conversion or inferred timestamps. Cancelled / rejected records retain their status."};
            foreach (var table in tables) summary.Add(table.Title);
            TrendPdfReportWriter.Write(dialog.FileName,"LABORATORY SAMPLE REGISTER","REGISTER-"+generated.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture),scope,user,generated,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"medica-logo.png"),summary,Array.Empty<TrendReportChart>(),tables,sampleRegister:true);
            DatabaseHelper.AddAuditTrailAdvanced("SampleRegister",0,"Sample Register Export","",dialog.FileName,"Scope: "+scope+"; Records: "+loaded.Rows.Count,user,"Export",null,null,"Laboratory");
            RegisterStatus.Text="Register PDF saved: "+dialog.FileName+". Open it to preview and print.";
            Process.Start(new ProcessStartInfo(dialog.FileName) {UseShellExecute=true});
        }
        catch (Exception ex) { MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),"Sample Register",MessageBoxButton.OK,MessageBoxImage.Warning); }
    }
}
