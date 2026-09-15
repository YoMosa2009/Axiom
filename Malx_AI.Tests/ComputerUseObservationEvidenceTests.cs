using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseObservationEvidenceTests
    {
        [Fact]
        public void VisualChange_RequiresDifferentCapturedImageBytes()
        {
            Assert.False(ComputerUseObservationEvidence.HasVisualChange([1, 2, 3], [1, 2, 3]));
            Assert.True(ComputerUseObservationEvidence.HasVisualChange([1, 2, 3], [1, 2, 4]));
        }
    }
}
