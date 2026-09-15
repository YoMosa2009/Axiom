using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseSafetyTests
    {
        [Fact]
        public void Evaluate_BlocksDestructiveTyping()
        {
            var action = new ComputerUseAction
            {
                Type = ComputerUseActionType.Type,
                Text = "rm -rf /"
            };
            var model = new ComputerUseSafetyVerdict
            {
                Ok = true,
                Dangerous = false,
                Risk = "low",
                Causes = "types a command",
                Effects = "shell deletes files"
            };

            ComputerUseSafetyVerdict verdict = ComputerUseSafety.Evaluate(action, model);
            Assert.True(verdict.Dangerous);
            Assert.False(verdict.Ok);
        }

        [Fact]
        public void Evaluate_RequiresAskWhenModelOmitsPass()
        {
            var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 10, Y = 10 };
            ComputerUseSafetyVerdict verdict = ComputerUseSafety.Evaluate(action, new ComputerUseSafetyVerdict());
            Assert.True(verdict.RequiresAsk);
        }

        [Fact]
        public void Evaluate_AllowsOrdinaryClickWithCompletePass()
        {
            var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 40, Y = 90 };
            var model = new ComputerUseSafetyVerdict
            {
                Ok = true,
                Dangerous = false,
                Risk = "low",
                Causes = "Activates the Start button",
                Effects = "Start menu opens"
            };

            ComputerUseSafetyVerdict verdict = ComputerUseSafety.Evaluate(action, model);
            Assert.False(verdict.Dangerous);
            Assert.True(verdict.Ok);
        }
    }
}
