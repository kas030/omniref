using OmniRef.Core.Models;
using OmniRef.Infrastructure.Windows.Persistence;
using Microsoft.Data.Sqlite;

namespace OmniRef.Tests;

public sealed class SqliteWorkspaceStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OmniRef.Tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAndOpen_RoundTripsEveryItemKindAndTags()
    {
        var path = Path.Combine(_directory, "roundtrip.omniref");
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();

        await store.SaveAsync(path, document);
        var opened = await store.OpenAsync(path);

        Assert.Equal(WorkspaceOpenMode.ReadWrite, opened.Mode);
        Assert.Equal(document.Id, opened.Document.Id);
        Assert.Equal(document.Title, opened.Document.Title);
        Assert.Equal(document.Items.Count, opened.Document.Items.Count);
        Assert.Equal(
            Enum.GetValues<ItemKind>().Order(),
            opened.Document.Items.Select(item => item.Kind).Order());
        var text = Assert.IsType<TextContent>(
            opened.Document.Items.Single(item => item.Kind == ItemKind.Text).Content);
        Assert.Equal("hello 世界", text.Text);
        Assert.Contains("重要", opened.Document.Items.Single(item => item.Kind == ItemKind.Text).Tags);
    }

    [Fact]
    public async Task EmbeddedAsset_StreamsPersistsExportsAndSurvivesSaveAs()
    {
        var sourceWorkspace = Path.Combine(_directory, "source.omniref");
        var copiedWorkspace = Path.Combine(_directory, "copied.omniref");
        var exported = Path.Combine(_directory, "exported.bin");
        var bytes = Enumerable.Range(0, 65536).Select(value => (byte)(value % 251)).ToArray();
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(sourceWorkspace, document);

        await using var input = new MemoryStream(bytes);
        var asset = await store.ImportEmbeddedAssetAsync(
            sourceWorkspace,
            input,
            "payload.bin",
            "application/octet-stream");
        var fileItem = document.Items.Single(item => item.Kind == ItemKind.File);
        var fileContent = Assert.IsType<FileContent>(fileItem.Content);
        fileItem.Content = fileContent with
        {
            Source = fileContent.Source with
            {
                Mode = AssetMode.EmbeddedCopy,
                EmbeddedAssetId = asset.Id,
                AbsolutePath = null,
                RelativePath = null
            }
        };
        await store.SaveAsync(sourceWorkspace, document);
        await store.SaveAsAsync(sourceWorkspace, copiedWorkspace, document);
        await store.ExportEmbeddedAssetAsync(copiedWorkspace, asset.Id, exported);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(exported));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), asset.Sha256);
    }

    [Fact]
    public async Task SaveAs_DoesNotModifySourceWorkspace()
    {
        var source = Path.Combine(_directory, "a.omniref");
        var destination = Path.Combine(_directory, "b.omniref");
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(source, document);
        var originalTitle = document.Title;

        document.Title = "Copied workspace";
        await store.SaveAsAsync(source, destination, document);

        Assert.Equal(originalTitle, (await store.OpenAsync(source)).Document.Title);
        Assert.Equal("Copied workspace", (await store.OpenAsync(destination)).Document.Title);
    }

    [Fact]
    public async Task FutureSchema_OpensReadOnlyAndCannotBeOverwritten()
    {
        var path = Path.Combine(_directory, "future.omniref");
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());
        SetSchemaVersion(path, WorkspaceDocument.CurrentSchemaVersion + 1);

        var opened = await store.OpenAsync(path);

        Assert.Equal(WorkspaceOpenMode.ReadOnly, opened.Mode);
        Assert.NotNull(opened.Warning);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(path, opened.Document));
        Assert.Equal(
            WorkspaceDocument.CurrentSchemaVersion + 1,
            ReadSchemaVersion(path));
    }

    [Fact]
    public async Task OlderSchema_CreatesBackupBeforeMigrationAttempt()
    {
        var path = Path.Combine(_directory, "legacy.omniref");
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());
        SetSchemaVersion(path, 0);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(path));

        Assert.Single(Directory.GetFiles(_directory, "legacy.omniref.v0.*.bak"));
        Assert.Equal(0, ReadSchemaVersion(path));
    }

    private static WorkspaceDocument CreateDocument()
    {
        var source = new SourceDescriptor(
            @"C:\missing\sample.bin",
            null,
            AssetMode.ExternalReference,
            null,
            "sample.bin",
            42,
            DateTimeOffset.UtcNow);
        return new WorkspaceDocument
        {
            Title = "Test workspace",
            ViewportOrigin = new WorldPoint(-25, 70),
            Zoom = 1.5,
            Items =
            [
                new BoardItem { Kind = ItemKind.Image, Title = "Image", Content = new ImageContent(source) },
                new BoardItem { Kind = ItemKind.File, Title = "File", Content = new FileContent(source, ".bin") },
                new BoardItem { Kind = ItemKind.Folder, Title = "Folder", Content = new FolderContent(source) },
                new BoardItem
                {
                    Kind = ItemKind.Text,
                    Title = "Text",
                    Content = new TextContent("hello 世界"),
                    Tags = ["重要", "测试"]
                },
                new BoardItem
                {
                    Kind = ItemKind.Url,
                    Title = "URL",
                    Content = new UrlContent("https://example.com", "example.com")
                },
                new BoardItem { Kind = ItemKind.Frame, Title = "Frame", Content = new FrameContent() }
            ]
        };
    }

    private static void SetSchemaVersion(string path, int version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workspace_meta SET value = $version WHERE key = 'schema_version';";
        command.Parameters.AddWithValue("$version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM workspace_meta WHERE key = 'schema_version';";
        return int.Parse(
            (string)command.ExecuteScalar()!,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
