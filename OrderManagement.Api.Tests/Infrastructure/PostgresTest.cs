namespace OrderManagement.Api.Tests.Infrastructure;

public class PostgresTest : IAsyncLifetime
{
    private readonly PostgresTestFixture _fixture = new();

    public Task InitializeAsync()
    {
        return _fixture.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Database_ShouldBeReachable()
    {
        await using var db = _fixture.CreateDbContext();

        var canConnect = await db.Database.CanConnectAsync();

        Assert.True(canConnect);
    }
}
