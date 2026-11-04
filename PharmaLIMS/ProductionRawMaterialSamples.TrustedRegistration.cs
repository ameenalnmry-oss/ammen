using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
namespace PharmaLIMS;
public partial class ProductionRawMaterialSamples
{
    private bool _prmRegistrationBusy;
    private async Task SaveTrustedPrmSampleAsync()
    {
        if(!TrustedApplicationSession.IsPrmRegistrationConfigured)
            throw new InvalidOperationException("Trusted PRM registration is disabled. An administrator must configure the independent EQ001 UAT registration boundary.");
        PrmRegistrationPolicy.RequireTarget(AppConfig.ConnectionString,requireIntegratedSecurity:false);
        string actor=Login.CurrentUser ?? throw new UnauthorizedAccessException("Sign in before PRM registration.");
        var recovery=new PrmRegistrationRecovery();
        var pending=recovery.Load(actor);
        string action="PrmRegister";
        if(pending is not null)
        {
            var choice=MessageBox.Show(this,"An unresolved PRM registration exists for this account.\n\n"+
                "Yes: recover/retry the original saved registration.\nNo: cancel the original request safely. If already committed, its saved sample is returned.\n"+
                "Cancel: preserve the request for later recovery.\n\nRequest: "+pending.RequestId,
                "Recover PRM Registration",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
            if(choice==MessageBoxResult.Cancel) return;
            if(choice==MessageBoxResult.No) action="PrmCancel";
        }
        var payload=pending?.Payload ?? CaptureTrustedPrmRegistration();
        var authentication=new ElectronicSignature(pending?.RequestId.ToString() ?? "Number assigned on commit",actor,
            action=="PrmCancel"?"Cancel Pending PRM Registration":"PRM Sample Registration") { Owner=this };
        if(authentication.ShowDialog()!=true || !authentication.IsConfirmed) return;
        if(!string.Equals(authentication.SignedBy,actor,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("PRM registration authentication must match the signed-in account.");
        // Persist and flush the original typed request before the first dispatch.
        if(!string.Equals(Login.CurrentUser,actor,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The active account changed during PRM registration authentication.");
        TrustedApplicationSession.RequirePrmRegistrationFrame(payload,actor,pending?.Reason ?? authentication.Reason,pending?.Meaning ?? authentication.Meaning);
        pending ??=recovery.SaveNew(actor,payload,authentication.Reason,authentication.Meaning);
        PrmRegistrationResult result;
        try
        {
            result=await TrustedApplicationSession.PrmRegistrationAsync(action,pending.RequestId,pending.Payload,
                authentication.SignedBy,pending.Reason,pending.Meaning);
        }
        catch(Exception ex)
        {
            ApplicationLogger.Error("PRM registration outcome unconfirmed; preserve request "+pending.RequestId.ToString("N"),ex);
            MessageBox.Show(this,"The registration outcome was not confirmed. Your original request is saved for recovery.\n"+
                "Use Save to recover or cancel this same request before preparing another registration.\n\nRequest: "+pending.RequestId+
                "\n\n"+UserFacingError.SafeMessage(ex),"PRM Registration Needs Recovery",MessageBoxButton.OK,MessageBoxImage.Warning);
            return;
        }
        if(result.State=="Pending") throw new InvalidOperationException("The original request remains pending. Recover that same request.");
        if(result.State=="Committed")
        {
            // Recovery may refer to another category and unrelated form values. Never bind that form to the committed ID.
            ClearForm();
            TxtStatus.Text="Sample registered successfully: "+result.SampleNumber+". Assigned tests: "+result.TestCount+". Select the saved sample in the list to edit it.";
            MessageBox.Show(this,"Sample registered successfully: "+result.SampleNumber+".\nAssigned tests: "+result.TestCount+
                "\nSelect the saved sample in the list to view or edit it.","PRM Registration Saved",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        try { recovery.Complete(actor,pending.RequestId); }
        catch(Exception ex)
        {
            ApplicationLogger.Error("PRM outcome confirmed; local receipt cleanup failed for "+pending.RequestId.ToString("N"),ex);
            MessageBox.Show(this,"The service confirmed "+result.State+". Local recovery cleanup failed; the next recovery returns this same outcome.",
                "PRM Registration Saved",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        if(result.State=="Cancelled")
            MessageBox.Show(this,"The original request is cancelled and cannot register a sample later. You may prepare a new registration.",
                "PRM Registration Cancelled",MessageBoxButton.OK,MessageBoxImage.Information);
    }
    private PrmRegistrationRequest CaptureTrustedPrmRegistration()
    {
        // Called only for a new request: recovery must not validate or mutate an unrelated current form.
        ValidateForm();
        // Existing form mapping is retained. Generated and actor-owned values never enter the typed payload.
        var fields=BuildParameters("",GetRequiredSampleDateTime(),0)
            .Where(p=>p.ParameterName is not ("@SampleNumber" or "@SpecificationVersionNo" or "@CreatedBy"))
            .ToDictionary(p=>p.ParameterName.TrimStart('@'),p=>p.Value is DBNull ? null : p.Value);
        foreach(string key in fields.Keys.ToArray())
            if(fields[key] is null && key is not ("ReceivedDate" or "ManufacturerExpiryDate" or "RetestDate" or "PreviousRetestDate" or "ProposedRetestDate" or "ManufacturingDate" or "PackagingDate" or "ExpiryDate" or "SampleQuantity")) fields[key]="";
        var request=PrmRegistrationFormMapping.Capture(fields);
        _ = PrmRegistrationPolicy.CanonicalPayload(request);
        return request;
    }
}
