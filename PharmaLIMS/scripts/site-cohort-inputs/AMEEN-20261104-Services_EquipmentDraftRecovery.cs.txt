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

public sealed record PendingEquipmentDraft(Guid RequestId, string Actor, string WindowsSid,
    EquipmentDraftRequest Payload, string CanonicalPayload, string OriginalReason, string OriginalMeaning);

/// <summary>Encrypted current-user pending equipment draft; preserves RequestId across uncertain responses.</summary>
public sealed class EquipmentDraftRecovery
{
    private static string Sid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("Windows identity is unavailable.");
    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PharmaLIMS", "EquipmentDraft");
    private static byte[] Entropy => Encoding.UTF8.GetBytes("PharmaLIMS.EquipmentDraft.v1|" + Sid);

    private static string PendingPath(string actor, int equipmentId)
    {
        if (string.IsNullOrWhiteSpace(actor) || equipmentId <= 0)
            throw new ArgumentException("Equipment draft recovery requires actor and equipment.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.ToUpperInvariant())));
        return Path.Combine(DirectoryPath, key + "-" + equipmentId + ".dat");
    }

    public PendingEquipmentDraft? Load(string actor, int equipmentId)
    {
        string path = PendingPath(actor, equipmentId);
        if (!File.Exists(path)) return null;
        RequirePrivateDirectory();
        byte[] encrypted = File.ReadAllBytes(path);
        if (encrypted.Length is < 1 or > 32768)
            throw new InvalidOperationException("Invalid equipment pending record; preserve it for recovery.");
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var pending = JsonSerializer.Deserialize<PendingEquipmentDraft>(plain,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (pending is null || pending.RequestId == Guid.Empty || pending.WindowsSid != Sid ||
                !string.Equals(pending.Actor, actor, StringComparison.OrdinalIgnoreCase) ||
                pending.Payload.EquipmentId != equipmentId ||
                string.IsNullOrWhiteSpace(pending.OriginalReason) ||
                string.IsNullOrWhiteSpace(pending.OriginalMeaning))
                throw new UnauthorizedAccessException("Pending equipment draft belongs to another identity or is invalid.");
            EquipmentDraftContract.ValidateTarget(AppConfig.ConnectionString);
            if (!string.Equals(pending.CanonicalPayload,
                EquipmentDraftContract.CanonicalPayload(pending.Payload), StringComparison.Ordinal))
                throw new InvalidOperationException("Pending equipment payload changed. Preserve for investigation.");
            return pending;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public PendingEquipmentDraft SaveNew(string actor, EquipmentDraftRequest payload, string reason, string meaning)
    {
        EquipmentDraftContract.ValidateTarget(AppConfig.ConnectionString);
        string canonical = EquipmentDraftContract.CanonicalPayload(payload);
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(meaning))
            throw new ArgumentException("Original signature reason and meaning are required.");
        string path = PendingPath(actor, payload.EquipmentId);
        if (File.Exists(path))
            throw new InvalidOperationException("Recover the previous equipment draft request before creating another.");
        EnsurePrivateDirectory();
        var pending = new PendingEquipmentDraft(Guid.NewGuid(), actor, Sid, payload, canonical, reason, meaning);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(pending);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return pending;
    }

    public void Complete(string actor, int equipmentId, Guid requestId)
    {
        PendingEquipmentDraft? pending = Load(actor, equipmentId);
        if (pending?.RequestId != requestId)
            throw new InvalidOperationException("Pending equipment request changed; preserve it for investigation.");
        File.Delete(PendingPath(actor, equipmentId));
    }

    private static void EnsurePrivateDirectory()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            Directory.CreateDirectory(DirectoryPath);
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(Sid));
            acl.SetAccessRuleProtection(true, false);
            foreach (string accountSid in new[] { Sid, "S-1-5-18", "S-1-5-32-544" })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(accountSid),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
        }
        RequirePrivateDirectory();
    }

    private static void RequirePrivateDirectory()
    {
        var allowed = new[] { Sid, "S-1-5-18", "S-1-5-32-544" };
        var acl = new DirectoryInfo(DirectoryPath).GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            Array.IndexOf(allowed, owner.Value) < 0)
            throw new UnauthorizedAccessException("Equipment recovery directory owner is not private.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                Array.IndexOf(allowed, rule.IdentityReference.Value) < 0)
                throw new UnauthorizedAccessException("Equipment recovery directory allows another Windows identity.");
    }
}
