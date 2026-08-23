namespace Cntryl.Pants;

public sealed class PantsWriteConflictException : PantsException
{
    public PantsWriteConflictException(string message, Exception? innerException = null)
        : this(message, isRangeConflict: false, innerException)
    {
    }

    internal PantsWriteConflictException(
        string message,
        bool isRangeConflict,
        Exception? innerException = null)
        : base(PantsErrorCode.WriteConflict, message, innerException)
    {
        IsRangeConflict = isRangeConflict;
    }

    internal bool IsRangeConflict { get; }
}
