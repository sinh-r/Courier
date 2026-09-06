namespace Courier.Scanner;

/// <summary>
/// Maps the <c>StatusCodes.Status200OK</c> constants to their numbers.
/// </summary>
/// <remarks>
/// The syntax tier cannot resolve the constant to its value, and this is one of the few places
/// where hard-coding the mapping is right: the names are fixed by the framework, and the
/// alternative is losing every ProducesResponseType that uses the idiomatic form.
/// </remarks>
internal static class StatusCodeNames
{
    public static int? Parse(string name)
    {
        // Status404NotFound, Status200OK, and so on: the digits follow the "Status" prefix.
        const string Prefix = "Status";

        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length < Prefix.Length + 3)
        {
            return null;
        }

        return int.TryParse(name.AsSpan(Prefix.Length, 3), out var code) ? code : null;
    }
}
