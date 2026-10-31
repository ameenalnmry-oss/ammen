namespace PharmaLIMS.TrustedSigning;

public sealed class TrustedCallerMap
{
    private readonly IReadOnlyDictionary<string, string> _accounts;
    public TrustedCallerMap(IReadOnlyDictionary<string, string> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Count == 0 || accounts.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"^S-1-(?:[0-9]+-)*[0-9]+$") || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 100))
            throw new ArgumentException("Explicit Windows SID-to-account bindings are required.");
        _accounts = new Dictionary<string, string>(accounts, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AllowedWindowsSids => _accounts.Keys.ToArray();

    // Admission is independent of the PharmaLIMS username held in a login ticket.
    public bool IsAllowedCaller(string? callerSid, string serviceSid) =>
        callerSid is not null &&
        !string.Equals(callerSid, serviceSid, StringComparison.OrdinalIgnoreCase) &&
        _accounts.ContainsKey(callerSid);

    public string Resolve(string callerSid, string serviceSid, string expectedUsername)
    {
        if (string.Equals(callerSid, serviceSid, StringComparison.OrdinalIgnoreCase) ||
            !_accounts.TryGetValue(callerSid, out string? username) ||
            !string.Equals(username, expectedUsername, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Windows identity is not bound to the active PharmaLIMS account.");
        return username;
    }
}
