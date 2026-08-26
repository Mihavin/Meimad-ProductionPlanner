using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

    internal static int DeriveMachineKey(string machineId, string verificationSecret)
    {
        if (string.IsNullOrWhiteSpace(machineId))
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        if (string.IsNullOrWhiteSpace(verificationSecret))
            throw new ArgumentException("Verification secret is required.", nameof(verificationSecret));

        var secretBytes = Encoding.UTF8.GetBytes(verificationSecret);
        var contextBytes = Encoding.UTF8.GetBytes(
            $"MEIMAD-CNC-VERIFY-V1\0{machineId.Trim()}");
        byte[]? digest = null;
        try
        {
            digest = HMACSHA256.HashData(secretBytes, contextBytes);
            var value = BinaryPrimitives.ReadUInt32BigEndian(digest);
            return MinimumSixDigitValue + (int)(value % 900000u);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (digest is not null) CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static string Calculate(
        int nonce,
        int offsetLoaderReleaseToken,
        int ncIdentityToken,
        int machineKey,
        int responseDigits)
    {
        SixDigits(nonce, nameof(nonce));
        SixDigits(offsetLoaderReleaseToken, nameof(offsetLoaderReleaseToken));
        SixDigits(ncIdentityToken, nameof(ncIdentityToken));
        SixDigits(machineKey, nameof(machineKey));
        if (responseDigits is < 4 or > 6)
            throw new ArgumentOutOfRangeException(nameof(responseDigits),
                "Response digits must be between 4 and 6.");

        var state = Fold(InitialState, Version);
        state = FoldSixDigits(state, nonce);
        state = FoldSixDigits(state, offsetLoaderReleaseToken);
        state = FoldSixDigits(state, ncIdentityToken);
        state = FoldSixDigits(state, machineKey);
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
