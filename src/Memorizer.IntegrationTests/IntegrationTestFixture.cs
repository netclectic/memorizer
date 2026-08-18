using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Memorizer.Services;
using Npgsql;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;

namespace Memorizer.IntegrationTests;

[CollectionDefinition(nameof(IntegrationTestCollection))]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture> { }

public class IntegrationTestFixture : IAsyncLifetime
{
    public readonly PostgreSqlContainer PostgresContainer;
    public readonly IContainer OllamaContainer;

    // Limit pool size to prevent connection exhaustion.
    // Each test class creates its own IServiceProvider with its own NpgsqlDataSource/pool.
    // Test classes must implement IDisposable to properly release connections.
    public string PostgresConnectionString =>
        PostgresContainer.GetConnectionString() + ";Maximum Pool Size=10;Minimum Pool Size=0;";
    public string OllamaApiUrl => $"http://{OllamaContainer.Hostname}:{OllamaContainer.GetMappedPublicPort(11434)}";

    public IntegrationTestFixture()
    {
        PostgresContainer = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithCleanUp(true)
            .WithPortBinding(5432, true)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithDatabase("postgmem")
            .Build();

        OllamaContainer = new ContainerBuilder("ollama/ollama:latest")
            .WithPortBinding(11434, true)
            .WithCleanUp(true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(11434)))
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        // Start containers
        await Task.WhenAll(
            PostgresContainer.StartAsync(),
            OllamaContainer.StartAsync()
        );

        // Run migrations
        await SchemaMigrator.MigrateAsync(PostgresConnectionString);

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(OllamaApiUrl);
        httpClient.Timeout = TimeSpan.FromMinutes(5); // Model pull can take a while

        // Pull the embedding model
        var embeddingModelPullRequest = new
        {
            name = "all-minilm"
        };

        var embeddingResponse = await httpClient.PostAsJsonAsync("/api/pull", embeddingModelPullRequest);
        embeddingResponse.EnsureSuccessStatusCode();

        // Wait for the embedding model to be ready
        var embeddingRequest = new
        {
            model = "all-minilm",
            prompt = "test"
        };

        var ready = false;
        var attempts = 0;
        const int maxAttempts = 10;

        while (!ready && attempts < maxAttempts)
        {
            try
            {
                var embeddingTestResponse = await httpClient.PostAsJsonAsync("/api/embeddings", embeddingRequest);
                if (embeddingTestResponse.IsSuccessStatusCode)
                {
                    ready = true;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    attempts++;
                }
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                attempts++;
            }
        }

        if (!ready)
        {
            throw new Exception("Failed to initialize Ollama embedding model after multiple attempts");
        }
    }

    /// <summary>
    /// Resets all user data so each test class starts from a clean database.
    /// Removes memories (and their relationships/events/versions), projects, and
    /// non-system workspaces. Preserves the schema, migration history, system
    /// workspaces (Unfiled, System Memories), embedding config, and provider settings.
    /// </summary>
    public async Task ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(PostgresConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Delete in FK-dependency order. memories are deleted before projects so the
        // project-delete trigger that moves memories to Unfiled has no work to do.
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM memory_relationships;
            DELETE FROM memory_events;
            DELETE FROM memory_versions;
            DELETE FROM memories;
            DELETE FROM projects;
            DELETE FROM workspaces WHERE is_system = false;
            """, conn, tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            PostgresContainer.DisposeAsync().AsTask(),
            OllamaContainer.DisposeAsync().AsTask()
        );
    }
}