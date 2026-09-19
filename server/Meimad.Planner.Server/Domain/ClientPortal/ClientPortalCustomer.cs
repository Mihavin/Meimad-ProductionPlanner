namespace Meimad.Planner.Server.Domain.ClientPortal;

/// <summary>
/// One Customer that is pushed to the cloud customer portal, and the portal customer id it is
/// pushed under. This used to be an <c>appsettings.json</c> array (<c>ClientPortal:Customers</c>);
/// it now lives in the <c>client_portal_customers</c> table so Setup can change it without a
/// Server restart. Only the mapping moved: <c>ClientPortal:Enabled</c>, the ingest URL, the shared
/// secret and the poll interval remain deployment configuration.
/// </summary>
internal sealed record ClientPortalCustomer(
    string CustomerId,
    string Customer,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The portal customer id doubles as a Firestore document id and a Firebase Auth claim, so it
    /// is restricted to 1-64 lowercase letters, digits, '-' or '_'. This is the single definition;
    /// the service, the repository and the API all call it rather than repeating a regex.
    /// </summary>
    internal static bool IsValidCustomerId(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');
}
