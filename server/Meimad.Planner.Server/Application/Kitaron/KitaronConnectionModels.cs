namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed record KitaronConnectionSettings(
    string ServerHost,
    int ServerPort,
    string DatabaseName,
    string ViewSchema,
    string ViewName,
    string Username,
    bool PasswordConfigured,
    bool Enabled,
    int RefreshIntervalSeconds,
    string LastTestStatus,
    DateTimeOffset? LastTestAt,
    string? LastTestMessage,
    int? LastTestColumnCount,
    int Version,
    DateTimeOffset UpdatedAt);

internal sealed record KitaronConnectionUpdate(
    string? ServerHost,
    int ServerPort,
    string? DatabaseName,
    string? ViewSchema,
    string? ViewName,
    string? Username,
    string? Password,
    bool ClearPassword,
    bool Enabled,
    int RefreshIntervalSeconds,
    int ExpectedVersion);

internal sealed record KitaronConnectionTestResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<KitaronSourceColumn> Columns,
    KitaronConnectionSettings Settings);

internal sealed record KitaronSourceColumn(string Name, string DataType);

internal sealed record StoredKitaronConnectionSettings(
    string ServerHost,
    int ServerPort,
    string DatabaseName,
    string ViewSchema,
    string ViewName,
    string Username,
    string? ProtectedPassword,
    bool Enabled,
    int RefreshIntervalSeconds,
    string LastTestStatus,
    DateTimeOffset? LastTestAt,
    string? LastTestMessage,
    int? LastTestColumnCount,
    int Version,
    DateTimeOffset UpdatedAt);

internal interface IKitaronConnectionRepository
{
    Task<StoredKitaronConnectionSettings> GetAsync(CancellationToken cancellationToken);

    Task<StoredKitaronConnectionSettings> UpdateAsync(
        StoredKitaronConnectionSettings settings,
        int expectedVersion,
        CancellationToken cancellationToken);

    Task<StoredKitaronConnectionSettings> RecordTestAsync(
        bool succeeded,
        DateTimeOffset testedAt,
        string message,
        int? columnCount,
        CancellationToken cancellationToken);
}

internal interface IKitaronConnectionTester
{
    Task<IReadOnlyList<KitaronSourceColumn>> TestAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        CancellationToken cancellationToken);
}

internal sealed class KitaronConnectionConcurrencyException : Exception
{
    internal KitaronConnectionConcurrencyException()
        : base("The Kitaron connection settings changed. Refresh and try again.")
    {
    }
}

