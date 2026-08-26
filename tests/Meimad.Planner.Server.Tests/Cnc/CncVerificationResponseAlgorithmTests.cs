using Meimad.Planner.Server.Application.Cnc;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncVerificationResponseAlgorithmTests
{
    [Theory]
    [InlineData(731841, 483920, 654321, 271828, 6, "438513")]
    [InlineData(731842, 483920, 654321, 271828, 6, "286999")]
    [InlineData(731841, 483921, 654321, 271828, 6, "543409")]
    [InlineData(731841, 483920, 654322, 271828, 6, "953665")]
    [InlineData(731841, 483920, 654321, 271829, 6, "210076")]
    [InlineData(100000, 100000, 100000, 100000, 4, "0282")]
    [InlineData(999999, 999999, 999999, 999999, 5, "69667")]
    public void Published_bench_vectors_are_stable(
        int nonce, int offsetToken, int ncIdentity, int machineKey,
        int digits, string expected) =>
        Assert.Equal(expected, CncVerificationResponseAlgorithm.Calculate(
            nonce, offsetToken, ncIdentity, machineKey, digits));

    [Fact]
    public void Every_required_input_changes_the_reference_response()
    {
        var baseline = CncVerificationResponseAlgorithm.Calculate(
            731841, 483920, 654321, 271828, 6);

        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(
            731842, 483920, 654321, 271828, 6));
        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(
            731841, 483921, 654321, 271828, 6));
        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(
            731841, 483920, 654322, 271828, 6));
        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(
            731841, 483920, 654321, 271829, 6));
    }

    [Fact]
    public void Machine_key_derivation_is_stable_machine_specific_and_six_digits()
    {
        var first = CncVerificationResponseAlgorithm.DeriveMachineKey(
            "machine-1", "correct horse battery staple");
        var repeated = CncVerificationResponseAlgorithm.DeriveMachineKey(
            "machine-1", "correct horse battery staple");
        var otherMachine = CncVerificationResponseAlgorithm.DeriveMachineKey(
            "machine-2", "correct horse battery staple");

        Assert.Equal(first, repeated);
        Assert.Equal(425445, first);
        Assert.InRange(first, 100000, 999999);
        Assert.NotEqual(first, otherMachine);
    }

    [Theory]
    [InlineData(99999, 483920, 654321, 271828, 6)]
    [InlineData(731841, 1000000, 654321, 271828, 6)]
    [InlineData(731841, 483920, 0, 271828, 6)]
    [InlineData(731841, 483920, 654321, 99999, 6)]
    [InlineData(731841, 483920, 654321, 271828, 3)]
    [InlineData(731841, 483920, 654321, 271828, 7)]
    public void Invalid_ranges_fail_closed(
        int nonce, int offsetToken, int ncIdentity, int machineKey, int digits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CncVerificationResponseAlgorithm.Calculate(
                nonce, offsetToken, ncIdentity, machineKey, digits));
}
