namespace Memorizer.IntegrationTests;

/// <summary>
/// Ensures the shared Postgres database is reset exactly once per test class.
/// xUnit runs test classes within a collection sequentially, so this guard is
/// safe as long as every DB-touching class in <see cref="IntegrationTestCollection"/>
/// calls <see cref="ForNewClassAsync"/>. The guard is keyed by fixture instance to
/// avoid cross-collection interference (each collection has its own container).
/// </summary>
internal static class TestDatabaseReset
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntegrationTestFixture, Type> LastResetByFixture = new();

    /// <summary>
    /// Resets the database when <paramref name="testClass"/> runs for the first time.
    /// Subsequent tests in the same class are no-ops.
    /// </summary>
    public static ValueTask ForNewClassAsync(IntegrationTestFixture fixture, Type testClass)
    {
        lock (Gate)
        {
            if (LastResetByFixture.TryGetValue(fixture, out var last) && last == testClass)
                return ValueTask.CompletedTask;
        }

        return ResetAsync(fixture, testClass);
    }

    private static async ValueTask ResetAsync(IntegrationTestFixture fixture, Type testClass)
    {
        await fixture.ResetDatabaseAsync(TestContext.Current.CancellationToken);
        lock (Gate)
        {
            LastResetByFixture[fixture] = testClass;
        }
    }
}
