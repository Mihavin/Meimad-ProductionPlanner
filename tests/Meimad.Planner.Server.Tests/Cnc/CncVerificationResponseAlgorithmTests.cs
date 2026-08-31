using Meimad.Planner.Server.Application.Cnc;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncVerificationResponseAlgorithmTests
{
    [Theory]
    [InlineData(731841, 483920, 654321, 6, "736536")]
    [InlineData(731842, 483920, 654321, 6, "841432")]
    [InlineData(100000, 100000, 100000, 4, "1795")]
    [InlineData(999999, 999999, 999999, 5, "74795")]
    public void Published_secretless_vectors_are_stable(
        int nonce, int offsetToken, int ncIdentity, int digits, string expected) =>
        Assert.Equal(expected, CncVerificationResponseAlgorithm.Calculate(
            nonce, offsetToken, ncIdentity, digits));

    [Fact]
    public void Every_exact_binding_input_changes_the_response()
    {
        var baseline = CncVerificationResponseAlgorithm.Calculate(731841, 483920, 654321, 6);

        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(731842, 483920, 654321, 6));
        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(731841, 483921, 654321, 6));
        Assert.NotEqual(baseline, CncVerificationResponseAlgorithm.Calculate(731841, 483920, 654322, 6));
    }

    [Theory]
    [InlineData(99999, 483920, 654321, 6)]
    [InlineData(731841, 1000000, 654321, 6)]
    [InlineData(731841, 483920, 0, 6)]
    [InlineData(731841, 483920, 654321, 3)]
    [InlineData(731841, 483920, 654321, 7)]
    public void Invalid_ranges_fail_closed(
        int nonce, int offsetToken, int ncIdentity, int digits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CncVerificationResponseAlgorithm.Calculate(nonce, offsetToken, ncIdentity, digits));
}
