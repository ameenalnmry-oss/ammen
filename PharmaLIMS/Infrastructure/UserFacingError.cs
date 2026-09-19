using Microsoft.Data.SqlClient;
using System.Security;
using System.Linq;
using System.IO;
using System.Reflection;

namespace PharmaLIMS.Infrastructure
{
    public static class UserFacingError
    {
        public static string SafeMessage(Exception exception, string operation = "The requested operation")
        {
            ArgumentNullException.ThrowIfNull(exception);

            string reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            ApplicationLogger.Error($"User-facing failure {reference}. Operation: {operation}.", exception);

            Exception effectiveException = Unwrap(exception);

            if (effectiveException is UnauthorizedAccessException or SecurityException)
                return "Your account is not authorized to complete this action. Reference: " + reference + ".";

            if (effectiveException is DatabaseMigrationException migrationException)
                return migrationException.OperatorMessage.TrimEnd('.') + ". Reference: " + reference + ".";

            if (effectiveException is TimeoutException ||
                (effectiveException is SqlException sqlException && sqlException.Number == -2))
            {
                return "The operation timed out before it could be completed. No retry should be assumed successful. Reference: " + reference + ".";
            }

            if (effectiveException is SqlException controlledSqlException &&
                controlledSqlException.Number >= 50000 &&
                IsControlledBusinessMessage(controlledSqlException.Message))
            {
                return controlledSqlException.Message.Trim() + " Reference: " + reference + ".";
            }

            if (effectiveException is IOException)
                return "The file operation could not be completed. Verify the file is accessible and try again. Reference: " + reference + ".";

            if (effectiveException is ArgumentException)
                return "The supplied data is not valid for this operation. Reference: " + reference + ".";

            if (effectiveException is InvalidOperationException && IsControlledBusinessMessage(effectiveException.Message))
                return effectiveException.Message.Trim() + " Reference: " + reference + ".";

            if (effectiveException is SqlException)
                return "The database operation could not be completed. No partial success should be assumed. Reference: " + reference + ".";

            return operation.TrimEnd('.') + " could not be completed. Review the PharmaLIMS log using reference " + reference + ".";
        }

        private static Exception Unwrap(Exception exception)
        {
            Exception current = exception;

            while (true)
            {
                if (current is AggregateException aggregateException &&
                    aggregateException.InnerExceptions.Count == 1 &&
                    aggregateException.InnerException != null)
                {
                    current = aggregateException.InnerException;
                    continue;
                }

                if (current is TargetInvocationException targetInvocationException &&
                    targetInvocationException.InnerException != null)
                {
                    current = targetInvocationException.InnerException;
                    continue;
                }

                if (current is TypeInitializationException typeInitializationException &&
                    typeInitializationException.InnerException != null)
                {
                    current = typeInitializationException.InnerException;
                    continue;
                }

                return current;
            }
        }

        private static bool IsControlledBusinessMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string value = message.Trim();
            string lower = value.ToLowerInvariant();

            string[] sensitiveMarkers =
            {
                "dbo.", "select ", "insert ", "update ", "delete ", "foreign key",
                "constraint", "sql", "server=", "data source=", "invalid column",
                "invalid object", "stack trace", " at system.", " at microsoft."
            };

            return value.Length <= 420 && !sensitiveMarkers.Any(lower.Contains);
        }
    }
}
