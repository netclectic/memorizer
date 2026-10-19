using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Tests that verify provider settings synchronization between database and services.
/// These tests ensure that:
/// 1. Database provider settings are loaded into IConfiguration at startup
/// 2. Services using IOptionsSnapshot receive the database-configured values
/// 3. Changes to provider settings via UI are reflected in subsequent requests
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProviderSettingsSyncTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private IHost? _host;

    public ProviderSettingsSyncTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        // Reset the shared DB for this class, then clean up provider_settings table before each test
        await TestDatabaseReset.ForNewClassAsync(_fixture, GetType());

        // Clean up provider_settings before each test (in case a prior run leaked rows).
        await CleanProviderSettingsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
            _host.Dispose();
        }

        // Symmetric cleanup — this class shares the Postgres container with the rest of
        // the collection and writes ACTIVE provider_settings rows to prove DB settings
        // override config. Without removing them on teardown, the last row (e.g. the
        // nonexistent 'custom-embedding-model') survives into other tests: their full-app
        // startup (InitializationService) applies it, so their embedding calls target a
        // model Ollama has not pulled and fail. Clean on the way out, not just in.
        await CleanProviderSettingsAsync();
    }

    private async Task CleanProviderSettingsAsync()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using NpgsqlCommand cmd = new NpgsqlCommand("DELETE FROM provider_settings", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private IHost CreateHost(Dictionary<string, string?> configOverrides)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                    ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                    ["Embeddings:Model"] = "all-minilm",
                    ["Embeddings:Timeout"] = "00:00:30",
                    ["LLM:ApiUrl"] = _fixture.OllamaApiUrl,
                    ["LLM:Model"] = "qwen2:0.5b",
                    ["LLM:Timeout"] = "00:02:00",
                });

                // Add test-specific overrides
                config.AddInMemoryCollection(configOverrides);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddMemorizer(initialize: true);
            });

        return builder.Build();
    }

    [Fact]
    public async Task EmbeddingService_Should_UseValuesFromDatabase_WhenProviderSettingsExist()
    {
        // Arrange - Create a provider setting in the database before starting the host
        string customApiUrl = "http://custom-embedding-server:11434";
        string customModel = "custom-embedding-model";

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);

        string insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using NpgsqlCommand cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.Embedding);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Test Embedding Provider");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = customApiUrl,
            model = customModel
        }));
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act - Start the host (InitializationService will load database settings)
        _host = CreateHost(new Dictionary<string, string?>());
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Give InitializationService time to complete
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - Check that configuration was updated from database
        IConfiguration config = _host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(customApiUrl, config["Embeddings:ApiUrl"]);
        Assert.Equal(customModel, config["Embeddings:Model"]);

        // Assert - Check that IOptionsSnapshot picks up the database values
        using IServiceScope scope = _host.Services.CreateScope();
        IOptionsSnapshot<EmbeddingSettings> embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        Assert.Equal(customApiUrl, embeddingSettings.Value.ApiUrl.ToString().TrimEnd('/'));
        Assert.Equal(customModel, embeddingSettings.Value.Model);
    }

    [Fact]
    public async Task LlmSettings_Should_UseValuesFromDatabase_WhenProviderSettingsExist()
    {
        // Arrange - Create a provider setting in the database before starting the host
        string customApiUrl = "http://custom-llm-server:11434";
        string customModel = "custom-llm-model";

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);

        string insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using NpgsqlCommand cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.MemorizerAgent);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Test LLM Provider");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = customApiUrl,
            model = customModel,
            timeout = "00:05:00"
        }));
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act - Start the host (InitializationService will load database settings)
        _host = CreateHost(new Dictionary<string, string?>());
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Give InitializationService time to complete
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - Check that configuration was updated from database
        IConfiguration config = _host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(customApiUrl, config["LLM:ApiUrl"]);
        Assert.Equal(customModel, config["LLM:Model"]);

        // Assert - Check that IOptionsSnapshot picks up the database values
        using IServiceScope scope = _host.Services.CreateScope();
        IOptionsSnapshot<LlmSettings> llmSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<LlmSettings>>();
        Assert.Equal(customApiUrl, llmSettings.Value.ApiUrl.ToString().TrimEnd('/'));
        Assert.Equal(customModel, llmSettings.Value.Model);
    }

    [Fact]
    public async Task InitializationService_Should_SeedFromEnvironment_WhenNoDatabaseSettingsExist()
    {
        // Arrange - Ensure no provider settings exist (already cleaned in InitializeAsync)
        string envApiUrl = _fixture.OllamaApiUrl;
        string envModel = "all-minilm";

        // Act - Start the host with environment configuration
        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = envApiUrl,
            ["Embeddings:Model"] = envModel,
        });
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Give InitializationService time to complete
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - Check that provider settings were seeded to database
        IStorage storage = _host.Services.GetRequiredService<IStorage>();
        ProviderSettings? embeddingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, TestContext.Current.CancellationToken);

        Assert.NotNull(embeddingProvider);
        JsonElement config = embeddingProvider!.Config.RootElement;
        Assert.Equal(envApiUrl, config.GetProperty("apiUrl").GetString());
        Assert.Equal(envModel, config.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProviderSettings_Should_UpdateConfiguration_WhenChangedViaStorage()
    {
        // Arrange - Start with initial settings
        string initialApiUrl = _fixture.OllamaApiUrl;
        string initialModel = "all-minilm";

        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = initialApiUrl,
            ["Embeddings:Model"] = initialModel,
        });
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Verify initial settings were loaded
        IConfiguration config = _host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(initialModel, config["Embeddings:Model"]);

        // Act - Update provider settings in database (simulating UI change)
        IStorage storage = _host.Services.GetRequiredService<IStorage>();
        ProviderSettings? existingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, TestContext.Current.CancellationToken);
        Assert.NotNull(existingProvider);

        string updatedModel = "nomic-embed-text";
        ProviderSettings updatedProvider = new ProviderSettings
        {
            Id = existingProvider!.Id,
            ProviderType = ProviderTypes.Embedding,
            ProviderName = ProviderNames.Ollama,
            DisplayName = "Updated Embedding Provider",
            Config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                apiUrl = initialApiUrl,
                model = updatedModel
            })),
            IsActive = true,
            CreatedAt = existingProvider.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        await storage.SaveProviderSettingsAsync(updatedProvider, TestContext.Current.CancellationToken);

        // Update configuration manually (in real app, this would be done via an event or controller)
        config["Embeddings:Model"] = updatedModel;

        // Assert - New scope should get updated settings via IOptionsSnapshot
        using IServiceScope scope = _host.Services.CreateScope();
        IOptionsSnapshot<EmbeddingSettings> embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        Assert.Equal(updatedModel, embeddingSettings.Value.Model);
    }

    [Fact]
    public async Task Beta1Upgrade_Should_OverrideLocalhostDefaults_WithEnvironmentConfig()
    {
        // Arrange - Simulate beta1 state: database has localhost defaults
        string localhostApiUrl = "http://localhost:11434";
        string defaultModel = "all-minilm";

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_fixture.PostgresConnectionString);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);

        string insertSql = @"
            INSERT INTO provider_settings (id, provider_type, provider_name, display_name, config, is_active, created_at, updated_at)
            VALUES (@id, @providerType, @providerName, @displayName, @config::jsonb, true, NOW(), NOW())";

        await using NpgsqlCommand cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("providerType", ProviderTypes.Embedding);
        cmd.Parameters.AddWithValue("providerName", ProviderNames.Ollama);
        cmd.Parameters.AddWithValue("displayName", "Ollama Embeddings");
        cmd.Parameters.AddWithValue("config", JsonSerializer.Serialize(new
        {
            apiUrl = localhostApiUrl,
            model = defaultModel
        }));
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Environment has non-localhost config (real production server)
        string productionApiUrl = _fixture.OllamaApiUrl; // This is not localhost
        string productionModel = "all-minilm";

        // Act - Start the host with production environment configuration
        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = productionApiUrl,
            ["Embeddings:Model"] = productionModel,
        });
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - Database should be updated with environment values (beta1 fix)
        IStorage storage = _host.Services.GetRequiredService<IStorage>();
        ProviderSettings? embeddingProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, TestContext.Current.CancellationToken);

        Assert.NotNull(embeddingProvider);
        JsonElement config = embeddingProvider!.Config.RootElement;

        // The localhost default should have been overridden with environment config
        Assert.Equal(productionApiUrl, config.GetProperty("apiUrl").GetString());

        // IOptionsSnapshot should also reflect the updated values
        using IServiceScope scope = _host.Services.CreateScope();
        IOptionsSnapshot<EmbeddingSettings> embeddingSettings = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>();
        Assert.Equal(productionApiUrl.TrimEnd('/'), embeddingSettings.Value.ApiUrl.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task EmbeddingModelChange_Should_BeDetected_WhenSavingProviderSettings()
    {
        // Arrange - Start with initial embedding model
        string apiUrl = _fixture.OllamaApiUrl;
        string initialModel = "all-minilm";

        _host = CreateHost(new Dictionary<string, string?>
        {
            ["Embeddings:ApiUrl"] = apiUrl,
            ["Embeddings:Model"] = initialModel,
        });
        await _host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        IStorage storage = _host.Services.GetRequiredService<IStorage>();

        // Verify initial model was set
        ProviderSettings? initialProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, TestContext.Current.CancellationToken);
        Assert.NotNull(initialProvider);
        JsonElement initialConfig = initialProvider!.Config.RootElement;
        Assert.Equal(initialModel, initialConfig.GetProperty("model").GetString());

        // Act - Change to a different model
        string newModel = "nomic-embed-text";
        ProviderSettings updatedProvider = new ProviderSettings
        {
            Id = initialProvider.Id,
            ProviderType = ProviderTypes.Embedding,
            ProviderName = ProviderNames.Ollama,
            DisplayName = "Updated Embedding Provider",
            Config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                apiUrl,
                model = newModel
            })),
            IsActive = true,
            CreatedAt = initialProvider.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        await storage.SaveProviderSettingsAsync(updatedProvider, TestContext.Current.CancellationToken);

        // Assert - The model should be detected as changed in the database
        ProviderSettings? finalProvider = await storage.GetActiveProviderAsync(ProviderTypes.Embedding, TestContext.Current.CancellationToken);
        Assert.NotNull(finalProvider);
        JsonElement finalConfig = finalProvider!.Config.RootElement;
        Assert.Equal(newModel, finalConfig.GetProperty("model").GetString());

        // The previous model was different
        Assert.NotEqual(newModel, initialModel);
    }
}

