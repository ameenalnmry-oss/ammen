using System;
using System.Threading;

namespace PharmaLIMS.Infrastructure;

// Host-only scope entered after independent credential/ticket verification.
// Never populate it from desktop claims or mutable Login.CurrentUser state.
internal static class TrustedAuditActorContext
{
    private static readonly AsyncLocal<string?> Actor = new();

    internal static IDisposable Enter(string verifiedActor)
    {
        if (string.IsNullOrWhiteSpace(verifiedActor) || verifiedActor.Length > 100)
            throw new UnauthorizedAccessException("A verified audit actor is required.");
        string? previous = Actor.Value;
        Actor.Value = verifiedActor.Trim();
        return new Scope(previous);
    }

    internal static string? Resolve(string requestedActor)
    {
        string? verifiedActor = Actor.Value;
        if (verifiedActor is null) return null;
        if (!string.Equals(verifiedActor, requestedActor?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Audit actor must match the independently verified account.");
        return verifiedActor;
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            Actor.Value = previous;
            _disposed = true;
        }
    }
}
