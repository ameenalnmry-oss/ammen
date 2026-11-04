namespace PharmaLIMS.TrustedSigning;

public sealed record TrustedApplicationRequest(Guid RequestId, string Action, string Username,
    string Password = "", string NewPassword = "", string Ticket = "", string UserJson = "",
    int UserId = 0, byte[]? ExpectedVersion = null, string Reason = "", string Meaning = "");

public interface ITrustedApplicationEndpoint
{
    Task<TrustedSigningReply> HandleAsync(string kernelCallerSid, TrustedApplicationRequest request, CancellationToken cancellationToken);
}

public sealed class TrustedSigningRejectedException(string errorCode) : UnauthorizedAccessException("Trusted service rejected the request (" + errorCode + ").")
{
    public string ErrorCode { get; } = errorCode;
}
