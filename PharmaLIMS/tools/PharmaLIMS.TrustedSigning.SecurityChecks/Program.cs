using System.Security.Cryptography;
using PharmaLIMS.TrustedSigning;

string salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
string hash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
    "CorrectPassword#2026", Convert.FromBase64String(salt),
    600000, HashAlgorithmName.SHA256, 32));

Assert(TrustedCredentialVerifier.VerifyModernCredential("CorrectPassword#2026", salt, hash),
    "Modern PBKDF2 credential must verify.");
Assert(!TrustedCredentialVerifier.VerifyModernCredential("WrongPassword#2026", salt, hash),
    "Incorrect password must fail.");
Assert(!TrustedCredentialVerifier.VerifyModernCredential("CorrectPassword#2026", "broken", hash),
    "Malformed salt must fail.");
Assert(!TrustedCredentialVerifier.VerifyModernCredential("CorrectPassword#2026", salt, "broken"),
    "Malformed stored hash must fail.");

var account = new TrustedSignerAccount(
    "reviewer", hash, salt, true, false, false, true,
    1, new byte[8], "[MIGRATED]");
var users = new FakeAccounts(account);
var caller = new FakeCaller("reviewer");
var authenticator = new TrustedHostAuthenticator(caller, users);
var store = new FakeSignatureStore();
var coordinator = new TrustedSigningCoordinator(authenticator, store);
var request = new EquipmentSigningRequest(
    Guid.NewGuid(), SigningRequestPolicy.EquipmentUpdate,
    1, "EQ-001", "Controlled equipment update",
    DateTimeOffset.UtcNow, new byte[8], "{\"EquipmentCode\":\"EQ-001\"}");

await coordinator.CommitEquipmentSignatureAsync(request, "CorrectPassword#2026");
Assert(store.SavedCount == 1, "Authorized current signer must reach trusted store.");

int previousLookups = users.LoadCount;
await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { RequestId = Guid.Empty }, "CorrectPassword#2026"));
Assert(users.LoadCount == previousLookups,
    "Malformed request must be rejected before reauthentication.");

await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) }, "CorrectPassword#2026"));
Assert(users.LoadCount == previousLookups, "Expired request must not consume an authentication attempt.");

await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { EquipmentId = null }, "CorrectPassword#2026"));
Assert(users.LoadCount == previousLookups, "Update without equipment ID must fail before authentication.");

await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { RequestId = Guid.NewGuid() }, "WrongPassword#2026"));
Assert(users.FailedCount == 1 && store.SavedCount == 1,
    "Incorrect credential must record a failure and never write a signature.");

users.AllowCommit = false;
await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { RequestId = Guid.NewGuid() }, "CorrectPassword#2026"));
Assert(store.SavedCount == 1, "Changed account state must block signing at commit.");

users.AllowCommit = true;
caller.Username = "other";
await ExpectDenied(() => coordinator.CommitEquipmentSignatureAsync(
    request with { RequestId = Guid.NewGuid() }, "CorrectPassword#2026"));
Assert(store.SavedCount == 1, "Unmapped host principal must not impersonate another user.");

var map = new TrustedCallerMap(new Dictionary<string,string> { ["S-1-5-21-1-2-3-1001"] = "reviewer" });
Assert(map.Resolve("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", "reviewer") == "reviewer", "Only the mapped Windows SID determines the signer.");
await ExpectDenied(() => Task.Run(() => map.Resolve("S-1-5-21-1-2-3-9999", "S-1-5-21-1-2-3-1002", "reviewer")));
await ExpectDenied(() => Task.Run(() => map.Resolve("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", "administrator")));
await ExpectDenied(() => Task.Run(() => map.Resolve("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001", "reviewer")));
await ExpectDenied(() => Task.Run(() => new TrustedPipeClientOptions("../bad-pipe", new string('A',64)).Validate()));
await ExpectDenied(() => Task.Run(() => new TrustedPipeClientOptions("eq001", "invalid-pin").Validate()));
using (var frame = new MemoryStream()) {
    await TrustedPipeProtocol.WriteAsync(frame, new TrustedSigningEnvelope(request, "reviewer", "secret"), default);
    frame.Position = 0;
    var roundtrip = await TrustedPipeProtocol.ReadAsync<TrustedSigningEnvelope>(frame, default);
    Assert(roundtrip.ExpectedUsername == "reviewer" && roundtrip.Request.RequestId == request.RequestId, "Bounded IPC frame roundtrips a request.");
}
using (var frame = new MemoryStream(new byte[] {0, 2, 0, 0})) {
    await ExpectDenied(() => TrustedPipeProtocol.ReadAsync<TrustedSigningEnvelope>(frame, default));
}

Console.WriteLine("PASS: EQ-001 independent credential validation, host-bound identity, and fail-closed coordination.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectDenied(Func<Task> action)
{
    try { await action(); }
    catch (UnauthorizedAccessException) { return; }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("Expected EQ-001 rejection did not occur.");
}

sealed class FakeCaller(string username) : ITrustedCallerIdentity
{
    public string Username { get; set; } = username;
    public string GetAuthenticatedUsername() => Username;
}

sealed class FakeAccounts(TrustedSignerAccount account) : ITrustedSignerAccountRepository
{
    public int LoadCount { get; private set; }
    public int FailedCount { get; private set; }
    public bool AllowCommit { get; set; } = true;
    public Task<TrustedSignerAccount?> LoadByUsernameAsync(string username, CancellationToken ct)
    {
        LoadCount++;
        return Task.FromResult<TrustedSignerAccount?>(
            string.Equals(username, account.Username, StringComparison.OrdinalIgnoreCase)
                ? account : null);
    }
    public Task RecordFailedAttemptAsync(TrustedSignerAccount verified, CancellationToken ct)
    {
        FailedCount++;
        return Task.CompletedTask;
    }
    public Task<bool> ConfirmSuccessIfStillAuthorizedAsync(TrustedSignerAccount verified, CancellationToken ct)
        => Task.FromResult(AllowCommit);
}

sealed class FakeSignatureStore : ITrustedEquipmentSignatureStore
{
    public int SavedCount { get; private set; }
    public Task CommitOnceAsync(EquipmentSigningRequest request, VerifiedSigner signer, CancellationToken ct)
    {
        if (signer.Username != "reviewer") throw new UnauthorizedAccessException();
        SavedCount++;
        return Task.CompletedTask;
    }
}
