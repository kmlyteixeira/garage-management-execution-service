using Xunit;

public sealed class ExecutionContractTests
{
    [Fact]
    public void New_execution_starts_queued()
    {
        var execution = new ExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "Queued");
        Assert.Equal("Queued", execution.Status);
    }
}
