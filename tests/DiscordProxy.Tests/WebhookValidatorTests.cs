using System.Text.Json;
using DiscordProxy.Validation;

namespace DiscordProxy.Tests;

public sealed class WebhookValidatorTests
{
    [Theory]
    [InlineData("1553762945471742045")] // 19 digits
    [InlineData("12345678901234567")]  // 17 digits, lower bound
    [InlineData("12345678901234567890")] // 20 digits, upper bound
    public void ValidIdsPass(string id) => Assert.True(WebhookValidator.IsValidId(id));

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456")]   // 16 digits
    [InlineData("123456789012345678901")] // 21 digits
    [InlineData("155376294547174204a")] // non-digit
    [InlineData(" 1553762945471742045")]
    public void InvalidIdsFail(string id) => Assert.False(WebhookValidator.IsValidId(id));

    [Theory]
    [InlineData("NHIEjMFXM6uiWVISvC19up4iY9CI2Eoag4qCa8az5U9bsBu3o7n4l4It307iH52SAHt-")]
    [InlineData("abcdefghij1234567890")]
    [InlineData("abc.def_ghi-jklmnopqrst")]
    public void ValidTokensPass(string token) => Assert.True(WebhookValidator.IsValidToken(token));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space inside token ok")]
    [InlineData("has/slash")]
    public void InvalidTokensFail(string token) => Assert.False(WebhookValidator.IsValidToken(token));

    [Fact]
    public void TokenBoundaryLengths()
    {
        Assert.False(WebhookValidator.IsValidToken(new string('a', 19)));
        Assert.True(WebhookValidator.IsValidToken(new string('a', 20)));
        Assert.True(WebhookValidator.IsValidToken(new string('a', 200)));
        Assert.False(WebhookValidator.IsValidToken(new string('a', 201)));
    }

    [Theory]
    [InlineData(@"{""content"":""hi""}", true)]
    [InlineData(@"{""content"":""""}", false)]
    [InlineData(@"{""content"":""   ""}", true)] // whitespace passes here; Discord decides
    [InlineData(@"{""content"":123}", false)] // wrong type, even with valid embeds nearby
    [InlineData(@"{""content"":123,""embeds"":[{""description"":""x""}]}", false)]
    [InlineData(@"{""embeds"":[{""description"":""x""}]}", true)]
    [InlineData(@"{""embeds"":[]}", false)]
    [InlineData(@"{""embeds"":""nope""}", false)]
    [InlineData(@"{""embeds"":""0"",""content"":""hi""}", false)] // the "0" plugin case
    [InlineData(@"{""embeds"":[{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""},{""description"":""x""}]}", false)] // 11 embeds
    [InlineData(@"{""components"":[{""type"":1}]}", true)]
    [InlineData(@"{""components"":""nope"",""content"":""hi""}", false)]
    [InlineData(@"{""poll"":{}}", true)]
    [InlineData(@"{""poll"":123}", false)]
    [InlineData(@"{""poll"":[],""content"":""hi""}", false)]
    [InlineData(@"{}", false)]
    public void PayloadNeedsAtLeastOneMessageField(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(valid, WebhookValidator.ValidatePayload(document.RootElement) is null);
    }

    [Fact]
    public void NonObjectPayloadFails()
    {
        using var document = JsonDocument.Parse(@"[1,2]");
        Assert.NotNull(WebhookValidator.ValidatePayload(document.RootElement));
    }
}
