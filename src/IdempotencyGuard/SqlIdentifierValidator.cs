using System.Text.RegularExpressions;

namespace IdempotencyGuard;

/// <summary>
/// Validates SQL identifiers (table names, schema names) to prevent
/// injection and ensure compatibility across database providers.
/// </summary>
public static partial class SqlIdentifierValidator
{
    // Allow letters, digits, underscores. Must start with a letter or underscore.
    private static readonly Regex SafeIdentifierPattern = new(
        @"^[a-zA-Z_][a-zA-Z0-9_]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates that the given identifier is safe for use in SQL statements.
    /// Throws <see cref="ArgumentException"/> if the identifier contains
    /// characters that could cause SQL injection or compatibility issues.
    /// </summary>
    public static void ThrowIfUnsafe(string identifier, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("SQL identifier cannot be null or empty.", parameterName);
        }

        if (!SafeIdentifierPattern.IsMatch(identifier))
        {
            throw new ArgumentException(
                $"SQL identifier '{identifier}' contains unsafe characters. " +
                "Only letters, digits, and underscores are allowed, and the identifier must start with a letter or underscore.",
                parameterName);
        }
    }
}
