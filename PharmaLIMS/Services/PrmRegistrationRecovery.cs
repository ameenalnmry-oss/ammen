using PharmaLIMS.TrustedSigning;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PharmaLIMS.Services;

public sealed record PendingPrmRegistration(Guid RequestId,string Actor,string WindowsSid,
    string Server,string Database,PrmRegistrationRequest Payload,string Reason,string Meaning);

/// <summary>One unresolved request per Windows profile. Never replace an uncertain request with a new ID.</summary>
public sealed class PrmRegistrationRecovery
{
    private static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("Windows identity unavailable.");
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PharmaLIMS","PrmRegistration");
    private static string FilePath => Path.Combine(DirectoryPath,"pending-registration.dat");
    private static byte[] Entropy => Encoding.UTF8.GetBytes("PharmaLIMS.PrmRegistration.v1|"+Sid+"|"+PrmRegistrationPolicy.Database);

    public PendingPrmRegistration? Load(string actor)
    {
        if(!File.Exists(FilePath)) return null;
        RequirePrivateDirectory();
        byte[] encrypted=File.ReadAllBytes(FilePath);
        if(encrypted.Length is < 1 or > 131072) throw new InvalidOperationException("Invalid pending Prm registration; preserve it for recovery.");
        byte[] bytes=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.CurrentUser);
        try
        {
            var p=JsonSerializer.Deserialize<PendingPrmRegistration>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow });
            if(p is null || p.RequestId==Guid.Empty || p.Actor!=actor || p.WindowsSid!=Sid ||
                p.Server!=PrmRegistrationPolicy.Server || p.Database!=PrmRegistrationPolicy.Database)
                throw new UnauthorizedAccessException("An unresolved Prm registration belongs to another account or target. Sign in with its original account to recover it.");
            PrmRegistrationPolicy.Validate(p.Payload);
            ValidateEvidence(p.Reason,p.Meaning);
            return p;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public PendingPrmRegistration SaveNew(string actor,PrmRegistrationRequest payload,string reason,string meaning)
    {
        PrmRegistrationPolicy.RequireTarget(AppConfig.ConnectionString,requireIntegratedSecurity:false);
        _ = PrmRegistrationPolicy.CanonicalPayload(payload);
        ValidateEvidence(reason,meaning);
        if(File.Exists(FilePath)) throw new InvalidOperationException("Recover the existing Prm registration before creating another request.");
        if(!Directory.Exists(DirectoryPath))
        {
            Directory.CreateDirectory(DirectoryPath);
            var acl=new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(Sid)); acl.SetAccessRuleProtection(true,false);
            foreach(string sid in new[]{Sid,"S-1-5-18","S-1-5-32-544"})
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
        }
        RequirePrivateDirectory();
        var pending=new PendingPrmRegistration(Guid.NewGuid(),actor,Sid,PrmRegistrationPolicy.Server,PrmRegistrationPolicy.Database,payload,reason,meaning);
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(pending);
        byte[] encrypted;
        try { encrypted=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temp,FilePath,false);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
        return pending;
    }

    public void Complete(string actor,Guid id)
    {
        var pending=Load(actor);
        if(pending?.RequestId!=id) throw new InvalidOperationException("Pending Prm request changed; preserve it for recovery.");
        File.Delete(FilePath);
    }

    private static void ValidateEvidence(string reason,string meaning)
    {
        if(string.IsNullOrWhiteSpace(reason) || reason.Length>500 || string.IsNullOrWhiteSpace(meaning) || meaning.Length>200)
            throw new ArgumentException("The original PRM registration reason and meaning are required.");
    }
    private static void RequirePrivateDirectory()
    {
        var allowed=new[]{Sid,"S-1-5-18","S-1-5-32-544"};
        var acl=new DirectoryInfo(DirectoryPath).GetAccessControl();
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || Array.IndexOf(allowed,owner.Value)<0)
            throw new UnauthorizedAccessException("Prm recovery directory ownership is not private.");
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow && Array.IndexOf(allowed,rule.IdentityReference.Value)<0)
                throw new UnauthorizedAccessException("Prm recovery directory access is not private.");
    }
}
