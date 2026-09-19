using Microsoft.Data.SqlClient;
using System;
using System.Data;

namespace PharmaLIMS.Infrastructure
{
    /// <summary>
    /// Adds SQL parameters with an explicit SqlDbType contract instead of relying on
    /// AddWithValue inference. Call sites remain responsible for selecting the type,
    /// length, precision, and scale that match the controlled SQL contract.
    /// </summary>
    internal static class SqlParameterCollectionExtensions
    {
        internal static SqlParameter AddExplicit(
            this SqlParameterCollection parameters,
            string parameterName,
            SqlDbType sqlDbType,
            object? value,
            int size = 0,
            byte precision = 0,
            byte scale = 0)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            if (string.IsNullOrWhiteSpace(parameterName))
                throw new ArgumentException("SQL parameter name is required.", nameof(parameterName));

            SqlParameter parameter = size == 0
                ? parameters.Add(parameterName, sqlDbType)
                : parameters.Add(parameterName, sqlDbType, size);

            if (precision > 0)
                parameter.Precision = precision;
            if (scale > 0)
                parameter.Scale = scale;

            parameter.Value = value ?? DBNull.Value;
            return parameter;
        }
    }
}
