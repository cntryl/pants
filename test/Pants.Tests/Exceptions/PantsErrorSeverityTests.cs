namespace Cntryl.Pants.Exceptions;

public sealed class PantsErrorSeverityTests
{
    public static TheoryData<PantsErrorCode> AllCodes() => new(Enum.GetValues<PantsErrorCode>());

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void ShouldClassifyEveryErrorCodeAsMidgeDoes(PantsErrorCode code)
    {
        var expected = code switch
        {
            PantsErrorCode.NotFound or PantsErrorCode.InvalidArgument or PantsErrorCode.NotSupported or
                PantsErrorCode.InvalidPath or PantsErrorCode.MemoryModeViolation or
                PantsErrorCode.WriteConflict => PantsErrorSeverity.Caller,
            PantsErrorCode.Io or PantsErrorCode.LeaseHeld or PantsErrorCode.LeaseUnavailable or
                PantsErrorCode.Aborted => PantsErrorSeverity.Transient,
            PantsErrorCode.NoSpace or PantsErrorCode.WriteStall or PantsErrorCode.Busy or
                PantsErrorCode.Timeout => PantsErrorSeverity.Backpressure,
            PantsErrorCode.Fenced or PantsErrorCode.LeaseEpochExhausted or
                PantsErrorCode.LeaseIndeterminate => PantsErrorSeverity.Fenced,
            PantsErrorCode.Internal or PantsErrorCode.ResourceLimit => PantsErrorSeverity.Defect,
            PantsErrorCode.Corruption or PantsErrorCode.RecoveryFailed or
                PantsErrorCode.CompatibilityError => PantsErrorSeverity.Fatal,
            _ => throw new InvalidOperationException($"Unclassified error code {code}.")
        };

        Assert.Equal(expected, code.GetSeverity());
        Assert.Equal(expected, PantsException.Create(code, "x").Severity);
    }

    [Theory]
    [InlineData(PantsErrorCode.NoSpace)]
    [InlineData(PantsErrorCode.WriteStall)]
    [InlineData(PantsErrorCode.Busy)]
    [InlineData(PantsErrorCode.Timeout)]
    public void ShouldClassifyAsBackpressureWhenErrorIsTransientAdmissionPressure(PantsErrorCode code) =>
        Assert.Equal(PantsErrorSeverity.Backpressure, code.GetSeverity());

    [Theory]
    [InlineData(PantsErrorCode.Corruption)]
    [InlineData(PantsErrorCode.RecoveryFailed)]
    [InlineData(PantsErrorCode.CompatibilityError)]
    public void ShouldClassifyAsFatalWhenErrorIndicatesUnrecoverableDataLoss(PantsErrorCode code) =>
        Assert.Equal(PantsErrorSeverity.Fatal, code.GetSeverity());

    [Theory]
    [InlineData(PantsErrorCode.Fenced)]
    [InlineData(PantsErrorCode.LeaseEpochExhausted)]
    [InlineData(PantsErrorCode.LeaseIndeterminate)]
    public void ShouldClassifyAsFencedWhenWriterLostAuthority(PantsErrorCode code) =>
        Assert.Equal(PantsErrorSeverity.Fenced, code.GetSeverity());

    [Fact]
    public void ShouldClassifyResourceLimitAsNonRetryableWhenLimitMayBePermanent()
    {
        var severity = PantsErrorCode.ResourceLimit.GetSeverity();

        Assert.Equal(PantsErrorSeverity.Defect, severity);
        Assert.NotEqual(PantsErrorSeverity.Transient, severity);
        Assert.NotEqual(PantsErrorSeverity.Backpressure, severity);
    }
}
