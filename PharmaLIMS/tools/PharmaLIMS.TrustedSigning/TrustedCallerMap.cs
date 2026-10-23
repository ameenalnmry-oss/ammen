namespace PharmaLIMS.TrustedSigning;

public sealed class TrustedCallerMap
{
    private readonly IReadOnlyDictionary<string, string> _accounts;
    public TrustedCallerMap(IReadOnlyDictionary<string, string> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Count == 0 || accounts.Any(p => !p.Key.StartsWith("S-1-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 100))
            throw new ArgumentException("Explicit Windows SID-to-account bindings are required.");
        _accounts = new Dictionary<string, string>(accounts, StringComparer.OrdinalIgnoreCase);
    }

    public string Resolve(string callerSid, string serviceSid, string expectedUsername)
    {
        if (string.Equals(callerSid, serviceSid, StringComparison.OrdinalIgnoreCase) ||
            !_accounts.TryGetValue(callerSid, out string? username) ||
            !string.Equals(username, expectedUsername, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Windows identity is not bound to the active PharmaLIMS account.");
        return username;
    }
}
