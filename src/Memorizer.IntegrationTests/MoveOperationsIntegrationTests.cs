using Memorizer.Extensions;
using Memorizer.IntegrationTests.Logging;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for move operations:
/// - Moving a project (and descendants) to a different workspace
/// - Reparenting a workspace under a new parent or promoting to top-level
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MoveOperationsIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _services;

    public ValueTask InitializeAsync() => TestDatabaseReset.ForNewClassAsync(_fixture, GetType());

    public ValueTask DisposeAsync()
    {
        (_services as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public MoveOperationsIntegrationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
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
        services.AddLogging(builder => builder.AddXUnit(_output));

        return services.BuildServiceProvider();
    }

    // ===== Project Move Tests =====

    [Fact]
    public async Task MoveProjectToWorkspace_HappyPath_MovesProjectAndDescendants()
    {
        var storage = _services.GetRequiredService<IStorage>();

        // Arrange
        var srcWorkspace = await storage.CreateWorkspaceAsync("Source WS", cancellationToken: TestContext.Current.CancellationToken);
        var dstWorkspace = await storage.CreateWorkspaceAsync("Destination WS", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(srcWorkspace.Id, "Root Project", cancellationToken: TestContext.Current.CancellationToken);
        var child = await storage.CreateProjectAsync(srcWorkspace.Id, "Child Project", parentId: project.Id, cancellationToken: TestContext.Current.CancellationToken);
        var grandchild = await storage.CreateProjectAsync(srcWorkspace.Id, "Grandchild Project", parentId: child.Id, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var moved = await storage.MoveProjectToWorkspaceAsync(project.Id, dstWorkspace.Id, cancellationToken: TestContext.Current.CancellationToken);

        // Assert root was moved
        Assert.Equal(dstWorkspace.Id, moved.WorkspaceId);
        Assert.Null(moved.ParentId);

        // Assert descendants were moved
        var movedChild = await storage.GetProjectAsync(child.Id, TestContext.Current.CancellationToken);
        var movedGrandchild = await storage.GetProjectAsync(grandchild.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(movedChild);
        Assert.NotNull(movedGrandchild);
        Assert.Equal(dstWorkspace.Id, movedChild!.WorkspaceId);
        Assert.Equal(dstWorkspace.Id, movedGrandchild!.WorkspaceId);

        _output.WriteLine($"Moved {project.Id} with {child.Id} and {grandchild.Id} to {dstWorkspace.Id}");

        // Cleanup
        await storage.DeleteProjectAsync(grandchild.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteProjectAsync(child.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteProjectAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(srcWorkspace.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(dstWorkspace.Id, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveProjectToWorkspace_WithNewParent_SetsParentInTargetWorkspace()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var srcWs = await storage.CreateWorkspaceAsync("Src WS Move Parent", cancellationToken: TestContext.Current.CancellationToken);
        var dstWs = await storage.CreateWorkspaceAsync("Dst WS Move Parent", cancellationToken: TestContext.Current.CancellationToken);
        var projectToMove = await storage.CreateProjectAsync(srcWs.Id, "Move Me", cancellationToken: TestContext.Current.CancellationToken);
        var parentInDst = await storage.CreateProjectAsync(dstWs.Id, "Parent In Dst", cancellationToken: TestContext.Current.CancellationToken);

        var moved = await storage.MoveProjectToWorkspaceAsync(projectToMove.Id, dstWs.Id, parentInDst.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(dstWs.Id, moved.WorkspaceId);
        Assert.Equal(parentInDst.Id, moved.ParentId);

        // Cleanup
        await storage.DeleteProjectAsync(moved.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteProjectAsync(parentInDst.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(srcWs.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(dstWs.Id, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveProjectToWorkspace_SameWorkspace_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var ws = await storage.CreateWorkspaceAsync("Same WS Move", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(ws.Id, "Project Same WS", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.MoveProjectToWorkspaceAsync(project.Id, ws.Id, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteProjectAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(ws.Id, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveProjectToWorkspace_InvalidTargetWorkspace_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var ws = await storage.CreateWorkspaceAsync("Valid WS Move Invalid Target", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(ws.Id, "Project Invalid Target WS", cancellationToken: TestContext.Current.CancellationToken);
        var nonExistentWorkspaceId = new WorkspaceId(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.MoveProjectToWorkspaceAsync(project.Id, nonExistentWorkspaceId, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteProjectAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(ws.Id, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveProjectToWorkspace_NewParentInWrongWorkspace_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var srcWs = await storage.CreateWorkspaceAsync("Src WS Wrong Parent", cancellationToken: TestContext.Current.CancellationToken);
        var dstWs = await storage.CreateWorkspaceAsync("Dst WS Wrong Parent", cancellationToken: TestContext.Current.CancellationToken);
        var otherWs = await storage.CreateWorkspaceAsync("Other WS Wrong Parent", cancellationToken: TestContext.Current.CancellationToken);

        var project = await storage.CreateProjectAsync(srcWs.Id, "Project Wrong Parent", cancellationToken: TestContext.Current.CancellationToken);
        var parentInOtherWs = await storage.CreateProjectAsync(otherWs.Id, "Parent In Other WS", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.MoveProjectToWorkspaceAsync(project.Id, dstWs.Id, parentInOtherWs.Id, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteProjectAsync(project.Id, TestContext.Current.CancellationToken);
        await storage.DeleteProjectAsync(parentInOtherWs.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(srcWs.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(dstWs.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(otherWs.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveProjectToWorkspace_MemoriesRemainAssigned()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var srcWs = await storage.CreateWorkspaceAsync("Src WS Memories", cancellationToken: TestContext.Current.CancellationToken);
        var dstWs = await storage.CreateWorkspaceAsync("Dst WS Memories", cancellationToken: TestContext.Current.CancellationToken);
        var project = await storage.CreateProjectAsync(srcWs.Id, "Project With Memories", cancellationToken: TestContext.Current.CancellationToken);

        // Add a memory to the project
        var memory = await storage.StoreMemory(
            "test", "test content", "test", null,
            new Models.ValueTypes.Confidence(1.0), "Test Memory",
            owner: MemoryOwner.ForProject(project.Id),
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Move
        var moved = await storage.MoveProjectToWorkspaceAsync(project.Id, dstWs.Id, cancellationToken: TestContext.Current.CancellationToken);

        // Memory should still be assigned to the project
        var memoryCount = await storage.GetMemoryCountByOwnerAsync(MemoryOwner.ForProject(moved.Id), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, memoryCount);

        // Cleanup
        await storage.Delete(memory.Id, TestContext.Current.CancellationToken);
        await storage.DeleteProjectAsync(project.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(srcWs.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(dstWs.Id, TestContext.Current.CancellationToken);
    }

    // ===== Workspace Reparent Tests =====

    [Fact]
    public async Task ReparentWorkspace_HappyPath_MovesUnderNewParent()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var parent = await storage.CreateWorkspaceAsync("Parent WS Reparent", cancellationToken: TestContext.Current.CancellationToken);
        var child = await storage.CreateWorkspaceAsync("Child WS To Move", cancellationToken: TestContext.Current.CancellationToken);

        var updated = await storage.UpdateWorkspaceAsync(child.Id, newParentId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(parent.Id, updated.ParentId);

        // Verify it appears under the parent
        var children = await storage.GetWorkspacesAsync(parentId: parent.Id, includeSystem: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(children, w => w.Id == child.Id);

        // Cleanup
        await storage.DeleteWorkspaceAsync(child.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(parent.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReparentWorkspace_MakeTopLevel_RemovesParent()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var parent = await storage.CreateWorkspaceAsync("Parent WS TopLevel Test", cancellationToken: TestContext.Current.CancellationToken);
        var child = await storage.CreateWorkspaceAsync("Child WS Make TopLevel", parentId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(parent.Id, child.ParentId);

        var updated = await storage.UpdateWorkspaceAsync(child.Id, makeTopLevel: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(updated.ParentId);

        // Cleanup
        await storage.DeleteWorkspaceAsync(child.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(parent.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReparentWorkspace_SelfReference_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var ws = await storage.CreateWorkspaceAsync("Self Ref WS", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UpdateWorkspaceAsync(ws.Id, newParentId: ws.Id, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteWorkspaceAsync(ws.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReparentWorkspace_CircularReference_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var grandparent = await storage.CreateWorkspaceAsync("GP WS Circular", cancellationToken: TestContext.Current.CancellationToken);
        var parent = await storage.CreateWorkspaceAsync("Parent WS Circular", parentId: grandparent.Id, cancellationToken: TestContext.Current.CancellationToken);
        var child = await storage.CreateWorkspaceAsync("Child WS Circular", parentId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        // Trying to move grandparent under child (circular)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UpdateWorkspaceAsync(grandparent.Id, newParentId: child.Id, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteWorkspaceAsync(child.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(parent.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(grandparent.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReparentWorkspace_BothFlagsSet_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var parent = await storage.CreateWorkspaceAsync("Parent WS Both Flags", cancellationToken: TestContext.Current.CancellationToken);
        var child = await storage.CreateWorkspaceAsync("Child WS Both Flags", parentId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UpdateWorkspaceAsync(child.Id, newParentId: parent.Id, makeTopLevel: true, cancellationToken: TestContext.Current.CancellationToken));

        // Cleanup
        await storage.DeleteWorkspaceAsync(child.Id, TestContext.Current.CancellationToken);
        await storage.DeleteWorkspaceAsync(parent.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReparentWorkspace_TargetIsSystemWorkspace_ThrowsInvalidOperation()
    {
        var storage = _services.GetRequiredService<IStorage>();

        var ws = await storage.CreateWorkspaceAsync("WS Move To System", cancellationToken: TestContext.Current.CancellationToken);

        // Get system workspaces
        var allWs = await storage.GetWorkspacesAsync(parentId: null, includeSystem: true, cancellationToken: TestContext.Current.CancellationToken);
        var systemWs = allWs.FirstOrDefault(w => w.IsSystem);

        if (systemWs != null)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.UpdateWorkspaceAsync(ws.Id, newParentId: systemWs.Id, cancellationToken: TestContext.Current.CancellationToken));
        }

        // Cleanup
        await storage.DeleteWorkspaceAsync(ws.Id, TestContext.Current.CancellationToken);
    }
}
