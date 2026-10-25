using System.Text;

namespace VeskaFlow.Web.Services;

/// <summary>An iCalendar (.ics) file for one booking, so the customer can add it to the calendar on their phone.</summary>
public static class CalendarFile
{
    public static string Ics(Guid bookingId, string shop, string service, string staff, DateTime startsAtUtc, DateTime endsAtUtc, string manageUrl, DateTime nowUtc)
    {
        static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//VeskaFlow//Booking//EN\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n");
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append($"UID:{bookingId}@veskaflow\r\n");
        sb.Append($"DTSTAMP:{Stamp(nowUtc)}\r\n");
        sb.Append($"DTSTART:{Stamp(startsAtUtc)}\r\n");
        sb.Append($"DTEND:{Stamp(endsAtUtc)}\r\n");
        sb.Append($"SUMMARY:{Escape($"{service} at {shop}")}\r\n");
        sb.Append($"DESCRIPTION:{Escape($"With {staff}. Manage or cancel your booking: {manageUrl}")}\r\n");
        sb.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }
}
