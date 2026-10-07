using JobFinder.Integrations.HeadHunter;

namespace JobFinder.Tests.Integrations;

public sealed class HhChatConfirmationTests
{
    [Fact]
    public void Old_identical_message_and_shared_first_forty_characters_do_not_confirm_a_new_send()
    {
        var text = new string('a', 50) + " ending";
        var old = new HhChatMessage("1", null, text, null, true);
        Assert.False(HhChat.IsConfirmed([old], [old], text, false));
        Assert.False(HhChat.IsConfirmed([], [old with { Text = new string('a', 50) + " other" }], text, false));
        Assert.True(HhChat.IsConfirmed([old], [old, old with { Id = "2" }], text, false));
    }

    [Fact]
    public void Cover_letter_requires_new_full_text_even_when_the_response_bubble_is_updated_in_place()
    {
        var before = new HhChatMessage("1", null, "Резюме доставлено", null, true);
        var after = before with { Text = "Сопроводительное письмо\nПолный текст письма." };
        Assert.True(HhChat.IsConfirmed([before], [after], "Полный  текст\nписьма.", true));
        Assert.False(HhChat.IsConfirmed([after], [after], "Полный текст письма.", true));
        Assert.False(HhChat.IsConfirmed([before], [after with { Mine = false }], "Полный текст письма.", true));
        Assert.False(HhChat.IsConfirmed([], [after], "", true));
    }
}
