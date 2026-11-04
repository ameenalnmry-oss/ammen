using System.IO.Pipes;
using System.Security.Authentication;

namespace PharmaLIMS.TrustedSigning;

public static class TrustedPipeConnectionPool
{
    public const int MaximumConnections = 4;
    public static async Task RunAsync(NamedPipeServerStream[] pipes,
        Func<NamedPipeServerStream, CancellationToken, Task> serve,
        CancellationToken cancellationToken, TaskCompletionSource? readiness = null)
    {
        if (pipes.Length != MaximumConnections)
            throw new ArgumentException("A fixed listener pool is required.", nameof(pipes));
        var workers = pipes.Select(ListenAsync).ToArray();
        readiness?.TrySetResult();
        await Task.WhenAll(workers).ConfigureAwait(false);

        async Task ListenAsync(NamedPipeServerStream pipe)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                // Disconnect also releases a stalled OS stream if a transport operation
                // does not promptly honor cancellation. Keep the server handle alive.
                using (deadline.Token.Register(() => {
                    try { if (pipe.IsConnected) pipe.Disconnect(); }
                    catch (IOException) { }
                }))
                {
                    try
                    {
                        await serve(pipe, deadline.Token).ConfigureAwait(false);
                        // DisconnectNamedPipe discards unread outbound bytes on Windows.
                        // Our clients close the pipe after consuming the framed reply.
                        // Await that close before reusing this handle; FlushAsync only
                        // flushes to the OS and cannot prove the peer read the reply.
                        // Keep the original deadline active, including for a client
                        // that reads its reply but never closes (or sends extra bytes).
                        var remainder = new byte[256];
                        while (await pipe.ReadAsync(remainder, deadline.Token).ConfigureAwait(false) != 0) { }
                    }
                    catch (Exception ex) when (ex is IOException or AuthenticationException or
                        OperationCanceledException or ArgumentException or UnauthorizedAccessException or
                        System.Text.Json.JsonException)
                    { /* A malformed or abandoned client must not terminate the host. */ }
                }
                if (pipe.IsConnected) pipe.Disconnect();
            }
        }
    }
}
