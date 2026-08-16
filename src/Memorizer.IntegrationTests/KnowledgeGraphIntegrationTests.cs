using Memorizer.Extensions;
using Memorizer.Models;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for the knowledge graph service (IGraphService).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class KnowledgeGraphIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public void Dispose()
    {
        (_services as IDisposable)?.Dispose();
    }

    public KnowledgeGraphIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _services = CreateServices();
    }

    private IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storage"] = _fixture.PostgresConnectionString,
                ["Embeddings:ApiUrl"] = _fixture.OllamaApiUrl,
                ["Embeddings:Model"] = "all-minilm",
                ["Embeddings:Timeout"] = TimeSpan.FromMinutes(1).ToString()
            })
            .Build());

        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
        {
            client.BaseAddress = new Uri(_fixture.OllamaApiUrl);
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        services.AddSingleton(new EmbeddingSettings
        {
            ApiUrl = new Uri(_fixture.OllamaApiUrl),
            Model = "all-minilm",
            Timeout = TimeSpan.FromMinutes(1)
        });

        services.AddMemorizer();
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetKnowledgeGraph_ReturnsAllNodesAndEdges_WithNoScope()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();
        var graph = _services.GetRequiredService<IGraphService>();

        var workspace = await storage.CreateWorkspaceAsync("Graph Root", "groot", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(workspace.Id, "Graph Project", "gp", cancellationToken: TestContext.Current.CancellationToken);

        var created = new List<MemoryId>();
        try
        {
            var m1 = await storage.StoreMemory(
                "reference", "graph node one", "test", new[] { "g1" },
                new Confidence(1.0), "Graph One", owner: MemoryOwner.ForWorkspace(workspace.Id), cancellationToken: TestContext.Current.CancellationToken);
            var m2 = await storage.StoreMemory(
                "reference", "graph node two", "test", new[] { "g2" },
                new Confidence(1.0), "Graph Two", owner: MemoryOwner.ForProject(project.Id), cancellationToken: TestContext.Current.CancellationToken);
            var m3 = await storage.StoreMemory(
                "reference", "graph node three", "test", new[] { "g3" },
                new Confidence(1.0), "Graph Three", cancellationToken: TestContext.Current.CancellationToken); // unfiled
            created.AddRange(new[] { m1.Id, m2.Id, m3.Id });

            await storage.CreateRelationship(m1.Id, m2.Id, "related-to", cancellationToken: TestContext.Current.CancellationToken);
            await storage.CreateRelationship(m2.Id, m3.Id, "explains", cancellationToken: TestContext.Current.CancellationToken);

            // Act
            var global = await graph.GetKnowledgeGraphAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Contains(global.Nodes, n => n.Id == m1.Id.Value);
            Assert.Contains(global.Nodes, n => n.Id == m2.Id.Value);
            Assert.Contains(global.Nodes, n => n.Id == m3.Id.Value);
            Assert.Contains(global.Edges, e => e.From == m1.Id.Value && e.To == m2.Id.Value && e.Type == "related-to");
            Assert.Contains(global.Edges, e => e.From == m2.Id.Value && e.To == m3.Id.Value && e.Type == "explains");
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id, TestContext.Current.CancellationToken);
            await storage.DeleteProjectAsync(project.Id, TestContext.Current.CancellationToken);
            await storage.DeleteWorkspaceAsync(workspace.Id, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetKnowledgeGraph_WorkspaceScope_ExcludesOutOfScopeNodesAndEdges()
    {
        // Arrange
        var storage = _services.GetRequiredService<IStorage>();
        var graph = _services.GetRequiredService<IGraphService>();

        var workspace = await storage.CreateWorkspaceAsync("Graph Root", "groot", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(workspace.Id, "Graph Project", "gp", cancellationToken: TestContext.Current.CancellationToken);

        var created = new List<MemoryId>();
        try
        {
            var m1 = await storage.StoreMemory(
                "reference", "graph node one", "test", new[] { "g1" },
                new Confidence(1.0), "Graph One", owner: MemoryOwner.ForWorkspace(workspace.Id), cancellationToken: TestContext.Current.CancellationToken);
            var m2 = await storage.StoreMemory(
                "reference", "graph node two", "test", new[] { "g2" },
                new Confidence(1.0), "Graph Two", owner: MemoryOwner.ForProject(project.Id), cancellationToken: TestContext.Current.CancellationToken);
            var m3 = await storage.StoreMemory(
                "reference", "graph node three", "test", new[] { "g3" },
                new Confidence(1.0), "Graph Three", cancellationToken: TestContext.Current.CancellationToken); // unfiled, outside scope
            created.AddRange(new[] { m1.Id, m2.Id, m3.Id });

            await storage.CreateRelationship(m1.Id, m2.Id, "related-to", cancellationToken: TestContext.Current.CancellationToken);
            await storage.CreateRelationship(m2.Id, m3.Id, "explains", cancellationToken: TestContext.Current.CancellationToken);

            // Act - workspace scope covers the workspace + its projects, not unfiled
            var scoped = await graph.GetKnowledgeGraphAsync(workspaceId: workspace.Id, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Contains(scoped.Nodes, n => n.Id == m1.Id.Value);
            Assert.Contains(scoped.Nodes, n => n.Id == m2.Id.Value);
            Assert.DoesNotContain(scoped.Nodes, n => n.Id == m3.Id.Value);
            Assert.Contains(scoped.Edges, e => e.From == m1.Id.Value && e.To == m2.Id.Value);
            Assert.DoesNotContain(scoped.Edges, e => e.To == m3.Id.Value);
        }
        finally
        {
            foreach (var id in created)
                await storage.Delete(id, TestContext.Current.CancellationToken);
            await storage.DeleteProjectAsync(project.Id, TestContext.Current.CancellationToken);
            await storage.DeleteWorkspaceAsync(workspace.Id, TestContext.Current.CancellationToken);
        }
    }
}
