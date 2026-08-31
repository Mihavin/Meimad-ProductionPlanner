using System.Globalization;

namespace Meimad.Planner.Server.Application.Cnc;

/// <summary>
/// Server implementation of the physically vector-matched controller-friendly response algorithm.
/// Production CNC success/failure enforcement remains separately commissioned.
/// </summary>
internal static class CncVerificationResponseAlgorithm
{
    internal const int Version = 1;
    internal const int MinimumSixDigitValue = 100000;
    internal const int MaximumSixDigitValue = 999999;
    private const int InitialState = 7919;
    private const int ReductionBase = 90909;
    private const int Multiplier = 11;
    private static readonly int[] FinalizationDigits = [3, 1, 4, 1, 5, 9];

    internal static string Calculate(
        int nonce,
        int offsetLoaderReleaseToken,
        int ncIdentityToken,
        int responseDigits)
    {
        SixDigits(nonce, nameof(nonce));
        SixDigits(offsetLoaderReleaseToken, nameof(offsetLoaderReleaseToken));
        SixDigits(ncIdentityToken, nameof(ncIdentityToken));
        if (responseDigits is < 4 or > 6)
            throw new ArgumentOutOfRangeException(nameof(responseDigits),
                "Response digits must be between 4 and 6.");

        var state = Fold(InitialState, Version);
        state = FoldSixDigits(state, nonce);
        state = FoldSixDigits(state, offsetLoaderReleaseToken);
        state = FoldSixDigits(state, ncIdentityToken);
        foreach (var digit in FinalizationDigits) state = Fold(state, digit);

        var modulus = responseDigits switch { 4 => 10000, 5 => 100000, _ => 1000000 };
        var response = state % modulus;
        return response.ToString($"D{responseDigits}", CultureInfo.InvariantCulture);
    }

    private static int FoldSixDigits(int state, int value)
    {
        for (var divisor = 100000; divisor > 0; divisor /= 10)
            state = Fold(state, value / divisor % 10);
        return state;
    }

    private static int Fold(int state, int symbol) =>
        state % ReductionBase * Multiplier + symbol;

    private static void SixDigits(int value, string parameter)
    {
        if (value is < MinimumSixDigitValue or > MaximumSixDigitValue)
            throw new ArgumentOutOfRangeException(parameter,
                "Verification inputs must be six-digit integers from 100000 through 999999.");
    }
}
