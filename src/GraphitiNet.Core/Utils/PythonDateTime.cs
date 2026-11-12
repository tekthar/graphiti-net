// Copyright 2001-2024, Python Software Foundation
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Translated from datetime_fromisoformat and its helpers in Modules/_datetimemodule.c (CPython 3.13, PSF License
// Version 2; see THIRD-PARTY-NOTICES.md) and changed for .NET.

using System.Text;

namespace GraphitiNet.Utils;

/// <summary>
/// Python's <c>datetime.fromisoformat</c> (CPython 3.13), which upstream uses to read the timestamps an LLM returns.
/// </summary>
/// <remarks>
/// <para>
/// Python accepts far more than strict ISO 8601, and rejects some things .NET's parsers accept, so the port follows
/// CPython's C parser step by step: dates <c>YYYY-MM-DD</c>, <c>YYYYMMDD</c>, <c>YYYY-Www</c>, <c>YYYYWww</c>,
/// <c>YYYY-Www-D</c> and <c>YYYYWwwD</c>; any single character (not only <c>T</c>) between date and time; times
/// <c>HH</c>, <c>HH:MM</c>, <c>HHMM</c>, <c>HH:MM:SS</c>, <c>HHMMSS</c>, with a fraction after <c>.</c> or <c>,</c>
/// (digits after the sixth are dropped, not rounded); offsets <c>Z</c>, <c>±HH</c>, <c>±HH:MM</c>, <c>±HHMM</c> and
/// longer forms with seconds and fractions, of any size below a day.
/// </para>
/// <para>CPython parses the UTF-8 bytes of the text as a NUL-terminated C string; the port does the same, so an
/// embedded NUL ends the text where Python's parser stops.</para>
/// </remarks>
internal static class PythonDateTime
{
    /// <summary>A parsed datetime: the wall-clock value (microsecond resolution) and its UTC offset, or
    /// <see langword="null"/> for a naive datetime.</summary>
    internal readonly record struct Parsed(DateTime Local, TimeSpan? Offset)
    {
        /// <summary>Upstream's <c>ensure_utc</c>: a naive value is taken as UTC, an aware one converted.</summary>
        /// <returns><see langword="false"/> where Python raises <c>OverflowError</c>: the conversion leaves the range
        /// of datetimes (years 1 to 9999).</returns>
        public bool TryToUtc(out DateTimeOffset utc)
        {
            utc = default;
            var ticks = Local.Ticks - (Offset ?? TimeSpan.Zero).Ticks;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            utc = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
    }

    /// <summary>Parses text as <c>datetime.fromisoformat</c> does.</summary>
    /// <returns><see langword="false"/> where Python raises <c>ValueError</c>.</returns>
    public static bool TryFromIsoFormat(string text, out Parsed result)
    {
        ArgumentNullException.ThrowIfNull(text);
        result = default;

        // _sanitize_isoformat_str: at least 7 code points; a lone surrogate is allowed only as the separator.
        var codePoints = CodePoints(text);
        if (codePoints.Count < 7)
        {
            return false;
        }

        foreach (var position in new[] { 7, 8, 10 })
        {
            if (position > codePoints.Count)
            {
                break;
            }

            if (position < codePoints.Count && codePoints[position] is >= 0xD800 and <= 0xDFFF)
            {
                codePoints[position] = 'T';
                break;
            }
        }

        if (codePoints.Any(cp => cp is >= 0xD800 and <= 0xDFFF))
        {
            return false;  // UnicodeEncodeError: not encodable as UTF-8
        }

        var bytes = Utf8(codePoints);
        var s = new CString(bytes);
        var len = bytes.Length;
        var separator = FindSeparator(s, len);

        var rv = ParseDate(s, 0, separator, out var year, out var month, out var day);
        int hour = 0, minute = 0, second = 0, microsecond = 0, tzOffset = 0, tzMicrosecond = 0;
        if (rv == 0 && len > separator)
        {
            var p = separator;
            var lead = s[p];
            p += (lead & 0x80) == 0 ? 1 : (lead & 0xf0) switch { 0xe0 => 3, 0xf0 => 4, _ => 2 };
            rv = ParseTime(s, p, len - p, out hour, out minute, out second, out microsecond, out tzOffset,
                out tzMicrosecond);
        }

        if (rv < 0)
        {
            return false;
        }

        TimeSpan? offset = null;
        if (rv == 1)
        {
            // new_delta(0, tzoffset, tz_useconds, normalize=1), then new_timezone: strictly within a day.
            var ticks = (tzOffset * TimeSpan.TicksPerSecond) + (tzMicrosecond * 10L);
            if (ticks <= -TimeSpan.TicksPerDay || ticks >= TimeSpan.TicksPerDay)
            {
                return false;
            }

            offset = TimeSpan.FromTicks(ticks);
        }

        // check_date_args and check_time_args.
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59 ||
            microsecond is < 0 or > 999_999)
        {
            return false;
        }

        result = new Parsed(
            new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(microsecond * 10L),
            offset);
        return true;
    }

    // _find_isoformat_datetime_separator, on the UTF-8 bytes.
    private static int FindSeparator(CString s, int len)
    {
        if (len == 7)
        {
            return 7;
        }

        if (s[4] == '-')
        {
            if (s[5] == 'W')
            {
                if (len < 8)
                {
                    return -1;
                }

                if (len > 8 && s[8] == '-')
                {
                    if (len == 9)
                    {
                        return -1;
                    }

                    return len > 10 && IsDigit(s[10]) ? 8 : 10;
                }

                return 8;
            }

            return 10;
        }

        if (s[4] == 'W')
        {
            var idx = 7;
            for (; idx < len; idx++)
            {
                if (!IsDigit(s[idx]))
                {
                    break;
                }
            }

            if (idx < 9)
            {
                return idx;
            }

            return idx % 2 == 0 ? 7 : 8;
        }

        return 8;
    }

    // parse_isoformat_date.
    private static int ParseDate(CString s, int p, int len, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (len < 0)
        {
            // A separator position of -1 makes CPython read the date from a pointer before the text; the date
            // digits never parse there, so it fails.
            return -1;
        }

        if (!ParseDigits(s, ref p, ref year, 4))
        {
            return -1;
        }

        var usesSeparator = s[p] == '-';
        if (usesSeparator)
        {
            p++;
        }

        if (s[p] == 'W')
        {
            p++;
            int isoWeek = 0, isoDay = 0;
            if (!ParseDigits(s, ref p, ref isoWeek, 2))
            {
                return -3;
            }

            if (p < len)
            {
                if (usesSeparator && s[p++] != '-')
                {
                    return -2;
                }

                if (!ParseDigits(s, ref p, ref isoDay, 1))
                {
                    return -4;
                }
            }
            else
            {
                isoDay = 1;
            }

            return IsoToYmd(year, isoWeek, isoDay, out year, out month, out day) ? 0 : -5;
        }

        if (!ParseDigits(s, ref p, ref month, 2))
        {
            return -1;
        }

        if (usesSeparator && s[p++] != '-')
        {
            return -2;
        }

        return ParseDigits(s, ref p, ref day, 2) ? 0 : -1;
    }

    // iso_to_ymd. Fails where CPython returns an error, or where the result leaves the datetime range (which
    // CPython's datetime constructor then rejects).
    private static bool IsoToYmd(int isoYear, int isoWeek, int isoDay, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (isoYear is < 1 or > 9999)
        {
            return false;
        }

        var firstDay = new DateOnly(isoYear, 1, 1);
        if (isoWeek is <= 0 or >= 53)
        {
            var firstWeekday = ((int)firstDay.DayOfWeek + 6) % 7;
            if (!(isoWeek == 53 && (firstWeekday == 3 || (firstWeekday == 2 && DateTime.IsLeapYear(isoYear)))))
            {
                return false;
            }
        }

        if (isoDay is <= 0 or >= 8)
        {
            return false;
        }

        // iso_week1_monday, in DateOnly day numbers (CPython's ordinal minus one).
        var firstDayNumber = firstDay.DayNumber;
        var firstWeekdayOfYear = ((int)firstDay.DayOfWeek + 6) % 7;
        var week1Monday = firstDayNumber - firstWeekdayOfYear + (firstWeekdayOfYear > 3 ? 7 : 0);
        var target = week1Monday + ((isoWeek - 1) * 7) + isoDay - 1;
        if (target < DateOnly.MinValue.DayNumber || target > DateOnly.MaxValue.DayNumber)
        {
            return false;
        }

        var date = DateOnly.FromDayNumber(target);
        (year, month, day) = (date.Year, date.Month, date.Day);
        return true;
    }

    // parse_hh_mm_ss_ff. Returns a negative code on failure, 1 when text is left before end, 0 at the end.
    private static int ParseHhMmSsFf(CString s, int p, int pEnd, out int hour, out int minute, out int second,
        out int microsecond)
    {
        var values = new int[3];
        microsecond = 0;
        var hasSeparator = true;
        for (var i = 0; i < 3; i++)
        {
            if (!ParseDigits(s, ref p, ref values[i], 2))
            {
                (hour, minute, second) = (values[0], values[1], values[2]);
                return -3;
            }

            var c = s[p++];
            if (i == 0)
            {
                hasSeparator = c == ':';
            }

            if (c is (byte)'.' or (byte)',')
            {
                if (p >= pEnd)
                {
                    (hour, minute, second) = (values[0], values[1], values[2]);
                    return -3;
                }

                break;
            }

            if (p >= pEnd)
            {
                (hour, minute, second) = (values[0], values[1], values[2]);
                return c != 0 ? 1 : 0;
            }

            if (hasSeparator && c == ':')
            {
                continue;
            }

            if (!hasSeparator)
            {
                p--;
            }
            else
            {
                (hour, minute, second) = (values[0], values[1], values[2]);
                return -4;
            }
        }

        (hour, minute, second) = (values[0], values[1], values[2]);
        var remaining = pEnd - p;
        var toParse = Math.Min(remaining, 6);
        if (!ParseDigits(s, ref p, ref microsecond, toParse))
        {
            return -3;
        }

        if (toParse < 6)
        {
            // CPython indexes its correction table with to_parse - 1; to_parse is never 0 here because a decimal
            // mark must be followed by something, and the loop above only falls through after one.
            microsecond *= toParse switch { 1 => 100000, 2 => 10000, 3 => 1000, 4 => 100, _ => 10 };
        }

        while (IsDigit(s[p]))
        {
            p++;
        }

        return s[p] != 0 ? 1 : 0;
    }

    // parse_isoformat_time. 0: no offset; 1: with offset; negative: invalid.
    private static int ParseTime(CString s, int p, int len, out int hour, out int minute, out int second,
        out int microsecond, out int tzOffset, out int tzMicrosecond)
    {
        tzOffset = tzMicrosecond = 0;
        var pEnd = p + len;
        var tzPos = p;
        do
        {
            if (s[tzPos] is (byte)'Z' or (byte)'+' or (byte)'-')
            {
                break;
            }
        }
        while (++tzPos < pEnd);

        var rv = ParseHhMmSsFf(s, p, tzPos, out hour, out minute, out second, out microsecond);
        if (rv < 0)
        {
            return rv;
        }

        if (tzPos == pEnd)
        {
            return rv == 1 ? -5 : 0;
        }

        if (s[tzPos] == 'Z')
        {
            return s[tzPos + 1] != 0 ? -5 : 1;
        }

        var sign = s[tzPos] == '-' ? -1 : 1;
        tzPos++;
        rv = ParseHhMmSsFf(s, tzPos, pEnd, out var tzHour, out var tzMinute, out var tzSecond, out tzMicrosecond);
        tzOffset = sign * ((tzHour * 3600) + (tzMinute * 60) + tzSecond);
        tzMicrosecond *= sign;
        return rv != 0 ? -5 : 1;
    }

    private static bool ParseDigits(CString s, ref int p, ref int value, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var digit = s[p++] - '0';
            if (digit is < 0 or > 9)
            {
                return false;
            }

            value = (value * 10) + digit;
        }

        return true;
    }

    private static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';

    private static List<int> CodePoints(string text)
    {
        var result = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
            {
                result.Add(text[i]);
            }
        }

        return result;
    }

    private static byte[] Utf8(List<int> codePoints)
    {
        var sb = new StringBuilder();
        foreach (var cp in codePoints)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // The UTF-8 bytes as CPython sees them: NUL-terminated, so reads at or past the end give 0. (CPython's parser
    // never reads past the terminator: a NUL fails every digit and separator check.)
    private readonly struct CString(byte[] bytes)
    {
        public byte this[int index] => index >= 0 && index < bytes.Length ? bytes[index] : (byte)0;
    }
}
