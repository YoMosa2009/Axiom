using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

public sealed class BrowserActionRiskTests
{
    [Theory]
    [InlineData("Delete my account permanently")]
    [InlineData("Close account")]
    [InlineData("Pay now")]
    [InlineData("Buy now")]
    [InlineData("Place your order")]
    [InlineData("Confirm and pay")]
    [InlineData("Proceed to checkout")]
    [InlineData("Send money")]
    public void MoneyAndAccountClosingControlsAreNeverPressed(string name)
    {
        Assert.Equal(BrowserActionRisk.Level.Blocked, BrowserActionRisk.Classify(name));
    }

    [Theory]
    [InlineData("Delete draft")]
    [InlineData("Send")]
    [InlineData("Submit form")]
    [InlineData("Unsubscribe")]
    [InlineData("Remove item")]
    public void FinalLookingControlsCarryAWarning(string name)
    {
        Assert.Equal(BrowserActionRisk.Level.Irreversible, BrowserActionRisk.Classify(name));
    }

    [Theory]
    [InlineData("More information")]
    [InlineData("Show result")]
    [InlineData("Your name")]
    [InlineData("Account settings")]
    [InlineData("Payment methods help")]
    [InlineData("")]
    public void OrdinaryControlsAreNormal(string name)
    {
        Assert.Equal(BrowserActionRisk.Level.Normal, BrowserActionRisk.Classify(name));
    }
}
