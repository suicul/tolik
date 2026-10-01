using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class IntentRouterTests
{
    [Theory]
    [InlineData("привет")]
    [InlineData("Привет!")]
    [InlineData("спасибо")]
    [InlineData("пока")]
    [InlineData("да")]
    [InlineData("hello")]
    [InlineData("Толик, привет")]
    public void Smalltalk_stays_conversation(string message)
    {
        Assert.Equal(ChatIntentKind.Conversation, IntentRouter.Classify(message).Kind);
    }

    [Theory]
    [InlineData("открой калькулятор", "калькулятор")]
    [InlineData("Толик, открой калькулятор", "калькулятор")]
    [InlineData("калькулятор", "калькулятор")]
    public void App_requests_route_to_run_process(string message, string expectedApp)
    {
        var intent = IntentRouter.Classify(message);
        Assert.Equal(ChatIntentKind.OpenApp, intent.Kind);
        Assert.Equal(expectedApp, intent.Argument);
    }

    [Fact]
    public void Empty_message_is_conversation()
    {
        Assert.Equal(ChatIntentKind.Conversation, IntentRouter.Classify("  ").Kind);
    }
}
