namespace Cntryl.Pants.Storage.Internal.Lease;

/// <summary>
///     Parses an RFC 3339 <c>date-time</c> exactly: culture-invariant, ASCII digits only, and an
///     explicit <c>Z</c> or <c>±hh:mm</c> offset is required, so a timestamp is never read as
///     host-local time.
/// </summary>
/// <remarks>
///     Any number of fractional-second digits is accepted (Midge writes nanoseconds). Digits
///     beyond tick precision round up, so a parsed instant is never earlier than the written one
///     and a record never looks older than it is. A leap second (<c>:60</c>) is read as the
///     last tick of the preceding second for the same reason.
/// </remarks>
static class Rfc3339Timestamp
{
    const int FractionDigitsPerTick = 7;

    public static bool TryParse(string text, out DateTimeOffset value)
    {
        value = default;
        var s = text.AsSpan();
        if (s.Length < 20 ||
            !TryDigits(s, 0, 4, out var year) || s[4] != '-' ||
            !TryDigits(s, 5, 2, out var month) || s[7] != '-' ||
            !TryDigits(s, 8, 2, out var day) || s[10] is not ('T' or 't') ||
            !TryDigits(s, 11, 2, out var hour) || s[13] != ':' ||
            !TryDigits(s, 14, 2, out var minute) || s[16] != ':' ||
            !TryDigits(s, 17, 2, out var second))
        {
            return false;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        var position = 19;
        long fractionTicks = 0;
        if (s[position] == '.')
        {
            position++;
            var start = position;
            var roundUp = false;
            while (position < s.Length && IsDigit(s[position]))
            {
                var digit = s[position] - '0';
                var index = position - start;
                if (index < FractionDigitsPerTick)
                {
                    fractionTicks = (fractionTicks * 10) + digit;
                }
                else if (digit != 0)
                {
                    roundUp = true;
                }

                position++;
            }

            var count = position - start;
            if (count == 0)
            {
                return false;
            }

            for (var index = count; index < FractionDigitsPerTick; index++)
            {
                fractionTicks *= 10;
            }

            if (roundUp)
            {
                fractionTicks++;
            }
        }

        if (!TryParseOffset(s[position..], out var offset))
        {
            return false;
        }

        if (second == 60)
        {
            second = 59;
            fractionTicks = TimeSpan.TicksPerSecond - 1;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified)
                .AddTicks(fractionTicks);
            var utcTicks = local.Ticks - offset.Ticks;
            if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            value = new DateTimeOffset(utcTicks, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    static bool TryParseOffset(ReadOnlySpan<char> s, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (s is ['Z' or 'z'])
        {
            return true;
        }

        if (s.Length != 6 || s[0] is not ('+' or '-') || s[3] != ':' ||
            !TryDigits(s, 1, 2, out var hours) || !TryDigits(s, 4, 2, out var minutes) ||
            hours > 23 || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (s[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    static bool TryDigits(ReadOnlySpan<char> s, int start, int count, out int value)
    {
        value = 0;
        for (var index = start; index < start + count; index++)
        {
            if (!IsDigit(s[index]))
            {
                return false;
            }

            value = (value * 10) + (s[index] - '0');
        }

        return true;
    }

    static bool IsDigit(char c) => c is >= '0' and <= '9';
}
