namespace Cntryl.Pants.Runtime;

public sealed class PantsTimeAndExceptionContractTests
{
    [Fact]
    public void ShouldSaturateUnixTimestampBoundaryConversions()
    {
        Assert.Equal(ulong.MaxValue, UnixTimestamp.FromDateTimeOffset(DateTimeOffset.MaxValue));
        Assert.Equal(0UL, UnixTimestamp.FromDateTimeOffset(DateTimeOffset.MinValue));
        Assert.Equal(0UL, UnixTimestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch));
        Assert.Equal(
            DateTimeOffset.MaxValue,
            UnixTimestamp.ToDateTimeOffsetSaturating(ulong.MaxValue));
        Assert.Equal(
            ulong.MaxValue,
            UnixTimestamp.FromDateTimeOffset(
                UnixTimestamp.ToDateTimeOffsetSaturating(ulong.MaxValue)));
    }

    [Fact]
    public void ShouldSaturateTtlOverflowAndExpireAtInclusiveBoundary()
    {
        var nearMaximum = DateTimeOffset.MaxValue.AddTicks(-1);

        var expiration = UnixTimestamp.ExpirationFromTimeToLive(
            nearMaximum,
            TimeSpan.MaxValue);

        Assert.Equal(ulong.MaxValue, expiration);
        Assert.True(UnixTimestamp.IsExpired(
            1_000,
            DateTimeOffset.UnixEpoch.AddMilliseconds(1_000)));
        Assert.False(UnixTimestamp.IsExpired(
            1_000,
            DateTimeOffset.UnixEpoch.AddMilliseconds(999)));
    }

    [Fact]
    public void ShouldMapEveryErrorCodeToItsExceptionSubtypeAndPreserveInnerException()
    {
        var expectedTypes = new Dictionary<PantsErrorCode, Type>
        {
            [PantsErrorCode.Io] = typeof(PantsIOException),
            [PantsErrorCode.NotFound] = typeof(PantsNotFoundException),
            [PantsErrorCode.InvalidArgument] = typeof(PantsInvalidArgumentException),
            [PantsErrorCode.Corruption] = typeof(PantsCorruptionException),
            [PantsErrorCode.NotSupported] = typeof(PantsNotSupportedException),
            [PantsErrorCode.Internal] = typeof(PantsInternalException),
            [PantsErrorCode.InvalidPath] = typeof(PantsInvalidPathException),
            [PantsErrorCode.NoSpace] = typeof(PantsNoSpaceException),
            [PantsErrorCode.RecoveryFailed] = typeof(PantsRecoveryFailedException),
            [PantsErrorCode.CompatibilityError] = typeof(PantsCompatibilityException),
            [PantsErrorCode.WriteStall] = typeof(PantsWriteStallException),
            [PantsErrorCode.MemoryModeViolation] = typeof(PantsMemoryModeViolationException),
            [PantsErrorCode.Fenced] = typeof(PantsFencedException),
            [PantsErrorCode.LeaseHeld] = typeof(PantsLeaseHeldException),
            [PantsErrorCode.LeaseUnavailable] = typeof(PantsLeaseUnavailableException),
            [PantsErrorCode.LeaseIndeterminate] = typeof(PantsLeaseIndeterminateException),
            [PantsErrorCode.LeaseEpochExhausted] = typeof(PantsLeaseEpochExhaustedException),
            [PantsErrorCode.WriteConflict] = typeof(PantsWriteConflictException),
            [PantsErrorCode.Aborted] = typeof(PantsAbortedException),
            [PantsErrorCode.Busy] = typeof(PantsBusyException),
            [PantsErrorCode.Timeout] = typeof(PantsTimeoutException),
            [PantsErrorCode.ResourceLimit] = typeof(PantsResourceLimitException)
        };
        var codes = Enum.GetValues<PantsErrorCode>();
        Assert.Equal(codes.Order(), expectedTypes.Keys.Order());

        foreach (var code in codes)
        {
            var inner = new InvalidOperationException(code.ToString());

            var exception = PantsException.Create(code, "mapped", inner);

            Assert.Equal(expectedTypes[code], exception.GetType());
            Assert.Equal(code, exception.Code);
            Assert.Same(inner, exception.InnerException);
        }
    }

    [Fact]
    public void ShouldMapEveryRuntimeExceptionBranchAndPreserveItsCause()
    {
        var pants = new PantsBusyException("busy");
        Assert.Same(pants, RuntimeExceptionMapper.ToPublicException(pants));

        var rollback = new WalCommitRollbackException(
            new IOException("commit"),
            new IOException("rollback"));
        AssertMapped<PantsAbortedException>(rollback, PantsErrorCode.Aborted);

        var noSpace = new TestIOException("native failure", 28);
        AssertMapped<PantsNoSpaceException>(noSpace, PantsErrorCode.NoSpace);

        var messageNoSpace = new IOException("Disk full while writing.");
        AssertMapped<PantsNoSpaceException>(messageNoSpace, PantsErrorCode.NoSpace);

        var io = new IOException("unrelated I/O");
        AssertMapped<PantsIOException>(io, PantsErrorCode.Io);

        var denied = new UnauthorizedAccessException("denied");
        AssertMapped<PantsIOException>(denied, PantsErrorCode.Io);

        var unexpected = new InvalidOperationException("unexpected");
        AssertMapped<PantsInternalException>(unexpected, PantsErrorCode.Internal);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callerCancellation = new OperationCanceledException(cancellation.Token);
        Assert.Same(
            callerCancellation,
            RuntimeExceptionMapper.ToPublicException(
                callerCancellation,
                cancellation.Token));

        var engineCancellation = new OperationCanceledException(CancellationToken.None);
        AssertMapped<PantsAbortedException>(engineCancellation, PantsErrorCode.Aborted);
    }

    static void AssertMapped<TException>(Exception source, PantsErrorCode code)
        where TException : PantsException
    {
        var mapped = Assert.IsType<TException>(RuntimeExceptionMapper.ToPublicException(source));
        Assert.Equal(code, mapped.Code);
        Assert.Same(source, mapped.InnerException);
    }

    sealed class TestIOException : IOException
    {
        public TestIOException(string message, int nativeCode)
            : base(message)
        {
            HResult = nativeCode;
        }
    }
}
