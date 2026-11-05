using PharmaLIMS.TrustedSigning;

internal static class ActivitySigningChecks
{
    internal static async Task RunAsync()
    {
        var account = new VerifiedActivitySigner("AMEEN\\LimsReview", true, false, false, true, false, new byte[8]);
        var authentication = new ActivityAuthentication(account);
        var store = new ActivityStore();
        var coordinator = new TrustedActivitySigningCoordinator(authentication, store);
        var request = new ActivitySigningRequest(Guid.NewGuid(), 42, "Reviewed", "Verified execution evidence", DateTimeOffset.UtcNow,
            new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, "EQ001_UAT_Test");

        var snapshotRequest = new ActivitySnapshotRequest(Guid.NewGuid(), 42, 35, "Reviewed");
        foreach (var invalidSnapshot in new[] {
            snapshotRequest with { RequestId = Guid.Empty },
            snapshotRequest with { ActivityId = 0 },
            snapshotRequest with { EquipmentId = 0 },
            snapshotRequest with { Stage = "SignedByOther" }
        })
        {
            await Denied(() => coordinator.ReadSnapshotAsync(invalidSnapshot));
            Check(store.SnapshotReads == 0, "Invalid snapshot must fail before persistence is read.");
        }

        await coordinator.CommitAsync(request, account.Username, "fresh-secret");
        Check(store.Signer == account.Username && store.ActivityId == 42, "Verified caller must reach the activity store.");
        store.Signer = null;

        foreach (var invalid in new[] {
            request with { RequestId = Guid.Empty },
            request with { ActivityId = 0 },
            request with { Stage = "SignedByOther" },
            request with { ExpectedVersion = new byte[8] },
            request with { Reason = "line\nbreak" },
            request with { IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) },
            request with { DatabaseName = "Production" }
        })
        {
            authentication.Called = false;
            await Denied(() => coordinator.CommitAsync(invalid, account.Username, "fresh-secret"));
            Check(!authentication.Called && store.Signer is null, "Invalid activity must fail before credentials or persistence.");
        }

        await Denied(() => coordinator.CommitAsync(request, account.Username, ""));
        authentication.Result = account with { Username = "forged-user" };
        await Denied(() => coordinator.CommitAsync(request, account.Username, "fresh-secret"));
        authentication.Result = account with { IsLocked = true };
        await Denied(() => coordinator.CommitAsync(request, account.Username, "fresh-secret"));
        authentication.Result = account with { CanReviewResults = false };
        await Denied(() => coordinator.CommitAsync(request, account.Username, "fresh-secret"));
        authentication.Result = account;
        await Denied(() => coordinator.CommitAsync(request with { Stage = "Approved" }, account.Username, "fresh-secret"));
        Check(store.Signer is null, "Rejected activity signer must never reach persistence.");

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { await coordinator.CommitAsync(request, account.Username, "fresh-secret", stop.Token); throw new Exception("Cancelled signature accepted."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Activity boundary checks passed; no Windows or SQL activity signature was issued.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Denied(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException) { return; }
        throw new Exception("Invalid activity signing request was accepted.");
    }

    private sealed class ActivityAuthentication(VerifiedActivitySigner result) : ITrustedActivityAuthenticator
    {
        public bool Called { get; set; }
        public VerifiedActivitySigner Result { get; set; } = result;
        public Task<VerifiedActivitySigner?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        { Called = true; return Task.FromResult<VerifiedActivitySigner?>(Result); }
    }
    private sealed class ActivityStore : ITrustedActivitySignatureStore
    {
        public string? Signer { get; set; }
        public long ActivityId { get; private set; }
        public int SnapshotReads { get; private set; }
        public Task<ActivitySnapshot> ReadSnapshotAsync(ActivitySnapshotRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotReads++;
            return Task.FromResult(new ActivitySnapshot(request.ActivityId, request.EquipmentId,
                "Submitted", new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, "EQ001_UAT_Test"));
        }
        public Task CommitOnceAsync(ActivitySigningRequest request, VerifiedActivitySigner signer, CancellationToken cancellationToken)
        { Signer = signer.Username; ActivityId = request.ActivityId; return Task.CompletedTask; }
    }
}
