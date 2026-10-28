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

public sealed record PendingWaterWorkflow(Guid RequestId, string Actor, string WindowsSid,
    WaterWorkflowRequest Payload, string CanonicalPayload, string OriginalReason, string OriginalMeaning);

/// <summary>Durable, current-user recovery. An uncertain actor/sample request can never be replaced.</summary>
public sealed class WaterWorkflowRecovery
{
    private static string Sid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("Windows identity is unavailable for Water recovery.");
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PharmaLIMS", "WaterWorkflow");
    private static byte[] Entropy => Encoding.UTF8.GetBytes("PharmaLIMS.WaterWorkflow.v1|" + Sid);
    private static string PendingPath(string actor, int sampleId)
    {
        if (string.IsNullOrWhiteSpace(actor) || sampleId <= 0) throw new ArgumentException("Water recovery requires an actor and sample.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.ToUpperInvariant())));
        return Path.Combine(DirectoryPath, key + "-" + sampleId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".dat");
    }

    public PendingWaterWorkflow? Load(string actor, int sampleId)
    {
        string path = PendingPath(actor, sampleId);
        if (!File.Exists(path)) return null;
        RequirePrivateDirectory();
        byte[] encrypted = File.ReadAllBytes(path);
        if (encrypted.Length is < 1 or > 262144)
            throw new InvalidOperationException("Invalid pending Water workflow. Preserve it for recovery.");
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var pending = JsonSerializer.Deserialize<PendingWaterWorkflow>(plain,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (pending is null || pending.RequestId == Guid.Empty || pending.WindowsSid != Sid ||
                !string.Equals(pending.Actor, actor, StringComparison.OrdinalIgnoreCase) || pending.Payload.SampleId != sampleId)
                throw new UnauthorizedAccessException("The unresolved Water workflow belongs to a different actor, Windows identity or sample.");
            WaterWorkflowPolicy.RequireTarget(AppConfig.ConnectionString, pending.Payload, requireIntegratedSecurity: false);
            if (!string.Equals(pending.CanonicalPayload, WaterWorkflowPolicy.CanonicalPayload(pending.Payload), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(pending.OriginalReason) || string.IsNullOrWhiteSpace(pending.OriginalMeaning))
                throw new InvalidOperationException("The unresolved Water workflow identity is invalid. Preserve it for recovery.");
            return pending;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public PendingWaterWorkflow SaveNew(string actor, WaterWorkflowRequest payload, string reason, string meaning)
    {
        WaterWorkflowPolicy.RequireTarget(AppConfig.ConnectionString, payload, requireIntegratedSecurity: false);
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(meaning))
            throw new ArgumentException("The original signature reason and meaning are required.");
        string canonical = WaterWorkflowPolicy.CanonicalPayload(payload);
        string path = PendingPath(actor, payload.SampleId);
        if (File.Exists(path)) throw new InvalidOperationException("Recover or cancel the existing Water workflow before creating another request.");
        EnsurePrivateDirectory();
        var pending = new PendingWaterWorkflow(Guid.NewGuid(), actor, Sid, payload, canonical, reason, meaning);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(pending);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return pending;
    }

    public void Complete(string actor, int sampleId, Guid requestId, string state)
    {
        if (state is not ("Committed" or "Cancelled"))
            throw new InvalidOperationException("An uncertain Water outcome must remain available for recovery.");
        PendingWaterWorkflow? pending = Load(actor, sampleId);
        if (pending?.RequestId != requestId)
            throw new InvalidOperationException("Pending Water workflow changed. Preserve it for recovery.");
        File.Delete(PendingPath(actor, sampleId));
    }

    private static void EnsurePrivateDirectory()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            Directory.CreateDirectory(DirectoryPath);
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(Sid));
            acl.SetAccessRuleProtection(true, false);
            foreach (string sid in new[] { Sid, "S-1-5-18", "S-1-5-32-544" })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
        }
        RequirePrivateDirectory();
    }
    private static void RequirePrivateDirectory()
    {
        var allowed = new[] { Sid, "S-1-5-18", "S-1-5-32-544" };
        var acl = new DirectoryInfo(DirectoryPath).GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || Array.IndexOf(allowed, owner.Value) < 0)
            throw new UnauthorizedAccessException("Water workflow recovery directory ownership is not private.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && Array.IndexOf(allowed, rule.IdentityReference.Value) < 0)
                throw new UnauthorizedAccessException("Water workflow recovery directory access is not private.");
    }
}
