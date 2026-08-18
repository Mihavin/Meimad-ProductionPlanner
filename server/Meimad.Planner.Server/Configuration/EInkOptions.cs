using System.Globalization;

namespace Meimad.Planner.Server.Configuration;

public sealed class EInkOptions
{
    public const string SectionName = "EInk";

    public string PackageRoot { get; init; } = "eink-packages";

    public string TimeZoneId { get; init; } = "Asia/Jerusalem";

    public string[] Workdays { get; init; } =
        ["sunday", "monday", "tuesday", "wednesday", "thursday"];

    public string ShiftStartsAtLocal { get; init; } = "06:00";

    public string ShiftEndsAtLocal { get; init; } = "18:00";

    public int PollIntervalSeconds { get; init; } = 300;

    public int MaximumRetryAttempts { get; init; } = 3;

    public int InitialBackoffSeconds { get; init; } = 15;

    public long MaximumPackageFileBytes { get; init; } = 25 * 1024 * 1024;

    public long MaximumPackageBytes { get; init; } = 100 * 1024 * 1024;

    public int MaximumPackageAssets { get; init; } = 64;

    public int MaximumGeneratedTextCharacters { get; init; } = 100_000;

    internal string ResolvedPackageRoot { get; private init; } = string.Empty;

    public static EInkOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var configured = configuration.GetSection(SectionName).Get<EInkOptions>()
            ?? new EInkOptions();
        var root = ServerStoragePathResolver.Resolve(configured.PackageRoot, contentRootPath);
        if (IsNetworkPath(root))
        {
            throw new InvalidOperationException("EInk:PackageRoot must be server-local, not a UNC path.");
        }

        if (string.IsNullOrWhiteSpace(configured.TimeZoneId))
        {
            throw new InvalidOperationException("EInk:TimeZoneId is required.");
        }

        var validDays = new HashSet<string>(
            ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
            StringComparer.Ordinal);
        if (configured.Workdays.Length == 0
            || configured.Workdays.Any(day => !validDays.Contains(day)))
        {
            throw new InvalidOperationException(
                "EInk:Workdays must contain lowercase weekday names.");
        }

        if (!TimeOnly.TryParseExact(
                configured.ShiftStartsAtLocal,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startsAt)
            || !TimeOnly.TryParseExact(
                configured.ShiftEndsAtLocal,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endsAt)
            || endsAt <= startsAt)
        {
            throw new InvalidOperationException(
                "EInk shift times must be HH:mm and the end must be after the start.");
        }

        if (configured.PollIntervalSeconds is < 30 or > 86400
            || configured.MaximumRetryAttempts is < 1 or > 10
            || configured.InitialBackoffSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException("EInk polling/retry configuration is outside supported bounds.");
        }

        if (configured.MaximumPackageFileBytes is < 1 or > 1_073_741_824
            || configured.MaximumPackageBytes < configured.MaximumPackageFileBytes
            || configured.MaximumPackageBytes > 4_294_967_296
            || configured.MaximumPackageAssets is < 1 or > 500
            || configured.MaximumGeneratedTextCharacters is < 1 or > 5_000_000)
        {
            throw new InvalidOperationException("EInk package-generation limits are outside supported bounds.");
        }

        return configured.WithResolvedRoot(root);
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private EInkOptions WithResolvedRoot(string root) => new()
    {
        PackageRoot = PackageRoot,
        TimeZoneId = TimeZoneId,
        Workdays = Workdays.Distinct(StringComparer.Ordinal).ToArray(),
        ShiftStartsAtLocal = ShiftStartsAtLocal,
        ShiftEndsAtLocal = ShiftEndsAtLocal,
        PollIntervalSeconds = PollIntervalSeconds,
        MaximumRetryAttempts = MaximumRetryAttempts,
        InitialBackoffSeconds = InitialBackoffSeconds,
        MaximumPackageFileBytes = MaximumPackageFileBytes,
        MaximumPackageBytes = MaximumPackageBytes,
        MaximumPackageAssets = MaximumPackageAssets,
        MaximumGeneratedTextCharacters = MaximumGeneratedTextCharacters,
        ResolvedPackageRoot = root
    };
}
