using System.Net;

namespace VeskaFlow.Web.Tests;

/// <summary>Things found on a real phone: the reminder notice links only its action, and on a narrow screen the date gets its own row above the day arrows.</summary>
[Collection("http")]
public class PhoneLayoutTests
{
    [Fact]
    public async Task On_a_phone_the_date_has_its_own_row_and_the_arrows_sit_under_it()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var css = await client.GetStringAsync("/css/site.css");
        Assert.Contains("grid-template-areas: \"date date\" \"prev next\"", css);
        Assert.Contains(".daynav strong { grid-area: date;", css);
        Assert.Contains(".daynav a:first-child { grid-area: prev;", css);
        Assert.Contains(".daynav a:last-child { grid-area: next;", css);
    }

    [Fact]
    public void The_reminder_notice_is_the_message_followed_by_the_action_and_the_old_full_text_still_reads_the_same()
    {
        Assert.Equal("1 customer is due a reminder for an upcoming appointment.", Text.ReminderAlertMessage(1));
        Assert.Equal("3 customers are due a reminder for an upcoming appointment.", Text.ReminderAlertMessage(3));
        Assert.Equal("Send them a message.", Text.ReminderAlertAction);
        Assert.Equal(Text.ReminderAlertMessage(3) + " " + Text.ReminderAlertAction, Text.ReminderAlertText(3));      // the bell still shows the whole sentence
    }
}
