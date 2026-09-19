using Microsoft.Data.SqlClient;
using System.Data;
using System.Threading;

namespace PharmaLIMS.Infrastructure
{
    public class DatabaseConnection
    {
        private readonly string _connectionString;

        public DatabaseConnection()
        {
            _connectionString = AppConfig.ConnectionString;
        }

        public SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        /// <summary>
        /// Creates a physical SQL connection that never enters the ADO.NET pool.
        /// Session-owned application locks used by Database Maintenance must not be able
        /// to survive by being returned to a pooled SQL session. Full System Preflight
        /// uses a transaction-owned Shared lease and does not require an unpooled session.
        /// </summary>
        public SqlConnection CreateUnpooledConnection()
        {
            var builder = new SqlConnectionStringBuilder(_connectionString)
            {
                Pooling = false
            };
            return new SqlConnection(builder.ConnectionString);
        }

        private static bool IsPoolUnsafeSqlException(SqlException ex)
        {
            string message = ex.Message ?? string.Empty;
            return ex.Class >= 20 ||
                   message.Contains("A severe error occurred on the current command", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("Operation cancelled by user", StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearPoolAfterCancelledOrSevereCommand(SqlConnection connection)
        {
            try
            {
                SqlConnection.ClearPool(connection);
            }
            catch (Exception cleanupException)
            {
                ApplicationLogger.Warning(
                    "SQL connection pool cleanup could not be completed after a cancelled or severe command.",
                    cleanupException);
            }
        }

        public DataTable ExecuteQuery(string query, SqlParameter[]? parameters = null, int? commandTimeoutSeconds = null)
        {
            var table = new DataTable();

            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(table);
            return table;
        }

        public int ExecuteNonQuery(string query, SqlParameter[]? parameters = null, int? commandTimeoutSeconds = null)
        {
            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            connection.Open();
            return command.ExecuteNonQuery();
        }

        public object? ExecuteScalar(string query, SqlParameter[]? parameters = null, int? commandTimeoutSeconds = null)
        {
            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            connection.Open();
            return command.ExecuteScalar();
        }

        public void ExecuteInTransaction(Action<SqlConnection, SqlTransaction> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            using var connection = CreateConnection();
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                action(connection, transaction);
                transaction.Commit();
            }
            catch (Exception)
            {
                try
                {
                    if (transaction.Connection != null)
                        transaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    ApplicationLogger.Warning(
                        "Database transaction rollback failed after an earlier operation failure. The original failure is preserved.",
                        rollbackException);
                }

                throw;
            }
        }

        public async Task<DataTable> ExecuteQueryAsync(
            string query,
            SqlParameter[]? parameters = null,
            int? commandTimeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var table = new DataTable();

            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                table.Load(reader);

                return table;
            }
            catch (OperationCanceledException)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
            catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw new OperationCanceledException(
                    "The database operation exceeded its caller deadline and was cancelled.",
                    ex,
                    cancellationToken);
            }
            catch (SqlException ex) when (IsPoolUnsafeSqlException(ex))
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
        }

        public async Task<int> ExecuteNonQueryAsync(
            string query,
            SqlParameter[]? parameters = null,
            int? commandTimeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
            catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw new OperationCanceledException(
                    "The database operation exceeded its caller deadline and was cancelled.",
                    ex,
                    cancellationToken);
            }
            catch (SqlException ex) when (IsPoolUnsafeSqlException(ex))
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
        }

        public async Task<object?> ExecuteScalarAsync(
            string query,
            SqlParameter[]? parameters = null,
            int? commandTimeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = commandTimeoutSeconds.HasValue
                    ? Math.Max(1, commandTimeoutSeconds.Value)
                    : AppConfig.CommandTimeoutSeconds
            };

            if (parameters != null)
                command.Parameters.AddRange(parameters);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
            catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw new OperationCanceledException(
                    "The database operation exceeded its caller deadline and was cancelled.",
                    ex,
                    cancellationToken);
            }
            catch (SqlException ex) when (IsPoolUnsafeSqlException(ex))
            {
                ClearPoolAfterCancelledOrSevereCommand(connection);
                throw;
            }
        }

        public async Task<T> ExecuteScalarAsync<T>(
            string query,
            SqlParameter[]? parameters = null,
            int? commandTimeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteScalarAsync(query, parameters, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            return result == DBNull.Value || result == null ? default! : (T)Convert.ChangeType(result, typeof(T));
        }

        public async Task ExecuteInTransactionAsync(Func<SqlConnection, SqlTransaction, Task> action)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var transaction = connection.BeginTransaction();

            try
            {
                await action(connection, transaction).ConfigureAwait(false);
                transaction.Commit();
            }
            catch (Exception)
            {
                try
                {
                    if (transaction.Connection != null)
                        transaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    ApplicationLogger.Warning(
                        "Database transaction rollback failed after an earlier asynchronous operation failure. The original failure is preserved.",
                        rollbackException);
                }

                throw;
            }
        }
    }
}
