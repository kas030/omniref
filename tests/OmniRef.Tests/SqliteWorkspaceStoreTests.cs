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
        Assert.Equal(document.Zoom, opened.Document.Zoom);
        Assert.Equal(document.Items.Count, opened.Document.Items.Count);
        Assert.All(opened.Document.Items, item => Assert.NotEqual(default, item.LastAccessedUtc));
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
    public async Task AnalyzeAndCompact_ReportAndRemoveUnreferencedEmbeddedAsset()
    {
        var path = Path.Combine(_directory, "compact.omniref");
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());
        await using (var input = new MemoryStream(bytes))
        {
            await store.ImportEmbeddedAssetAsync(
                path,
                input,
                "unused.bin",
                "application/octet-stream");
        }

        var analysis = await store.AnalyzeCompactionAsync(path);
        var result = await store.CompactAsync(path);
        var after = await store.AnalyzeCompactionAsync(path);

        Assert.Equal(1, analysis.UnreferencedAssetCount);
        Assert.True(analysis.EstimatedReclaimableBytes >= bytes.Length);
        Assert.Equal(analysis.FileSize, result.SizeBefore);
        Assert.Equal(1, result.RemovedAssetCount);
        Assert.True(result.ReclaimedBytes > 0);
        Assert.Equal(result.SizeAfter, after.FileSize);
        Assert.Equal(0, after.UnreferencedAssetCount);
    }

    [Fact]
    public async Task ReferencedEmbeddedAsset_DoesNotLeaveBlobSizedFreePages()
    {
        var path = Path.Combine(_directory, "embedded-space.omniref");
        var bytes = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, document);
        EmbeddedAssetInfo asset;
        await using (var input = new MemoryStream(bytes))
        {
            asset = await store.ImportEmbeddedAssetAsync(
                path,
                input,
                "large.bin",
                "application/octet-stream");
        }

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
        await store.SaveAsync(path, document);

        var analysis = await store.AnalyzeCompactionAsync(path);

        Assert.Equal(0, analysis.UnreferencedAssetCount);
        Assert.True(
            analysis.EstimatedReclaimableBytes < bytes.Length / 20,
            $"Expected little reclaimable space, but found {analysis.EstimatedReclaimableBytes} bytes.");
        Assert.True(
            analysis.FileSize < bytes.Length + (512 * 1024),
            $"Expected one BLOB allocation, but workspace size was {analysis.FileSize} bytes.");
    }

    [Fact]
    public async Task SaveAs_DoesNotModifySourceWorkspace()
    {
        var source = Path.Combine(_directory, "a.omniref");
        var destination = Path.Combine(_directory, "b.omniref");
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(source, document);
        var originalZoom = document.Zoom;

        document.Zoom = 2.25;
        await store.SaveAsAsync(source, destination, document);

        Assert.Equal(originalZoom, (await store.OpenAsync(source)).Document.Zoom);
        Assert.Equal(2.25, (await store.OpenAsync(destination)).Document.Zoom);
    }

    [Fact]
    public async Task SaveViewport_UpdatesOnlyWorkspaceMetadata()
    {
        var path = Path.Combine(_directory, "viewport.omniref");
        var document = CreateDocument();
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, document);
        var itemIds = document.Items.Select(item => item.Id).ToArray();
        var lastAccessedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        await store.SaveViewportAsync(path, new WorldPoint(300, -175), 2.25, lastAccessedUtc);
        Assert.Equal(
            lastAccessedUtc,
            DateTimeOffset.Parse(
                ReadMetaValue(path, "last_accessed_utc"),
                System.Globalization.CultureInfo.InvariantCulture));
        var opened = await store.OpenAsync(path);

        Assert.Equal(new WorldPoint(300, -175), opened.Document.ViewportOrigin);
        Assert.Equal(2.25, opened.Document.Zoom);
        Assert.True(opened.Document.LastAccessedUtc > lastAccessedUtc);
        Assert.Equal(itemIds, opened.Document.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task NewWorkspace_UsesDevelopmentSchemaV1AndLastAccessedFields()
    {
        var path = Path.Combine(_directory, "schema-v1.omniref");
        using var store = new SqliteWorkspaceStore();

        await store.SaveAsync(path, CreateDocument());

        Assert.Equal(1, ReadSchemaVersion(path));
        Assert.Contains("last_accessed_utc", ReadItemColumns(path));
        Assert.DoesNotContain("modified_utc", ReadItemColumns(path));
        Assert.Contains("last_accessed_utc", ReadMetaKeys(path));
        Assert.DoesNotContain("modified_utc", ReadMetaKeys(path));
    }

    [Fact]
    public async Task OpenWritableWorkspace_UpdatesWorkspaceLastAccessedTime()
    {
        var path = Path.Combine(_directory, "last-accessed.omniref");
        var originalAccess = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());
        SetMetaValue(path, "last_accessed_utc", originalAccess.ToString("O"));

        var opened = await store.OpenAsync(path);

        Assert.True(opened.Document.LastAccessedUtc > originalAccess);
        Assert.Equal(
            opened.Document.LastAccessedUtc,
            DateTimeOffset.Parse(
                ReadMetaValue(path, "last_accessed_utc"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task FileLease_PreventsWorkspaceDeletionUntilReleased()
    {
        var path = Path.Combine(_directory, "leased.omniref");
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());

        using (var lease = store.AcquireFileLease(path))
        {
            Assert.True(lease.IsCurrent);
            var updated = CreateDocument();
            updated.Zoom = 2.5;
            await store.SaveAsync(path, updated);
            await store.CompactAsync(path);
            Assert.Equal(2.5, (await store.OpenAsync(path)).Document.Zoom);
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.True(File.Exists(path));
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FileLease_ReportsFalseWhenUnderlyingHandleWasForciblyClosed()
    {
        var path = Path.Combine(_directory, "invalid-handle.omniref");
        using var store = new SqliteWorkspaceStore();
        await store.SaveAsync(path, CreateDocument());
        using var lease = store.AcquireFileLease(path);
        var streamField = lease.GetType().GetField(
            "_stream",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var stream = Assert.IsType<FileStream>(streamField?.GetValue(lease));

        stream.Dispose();

        Assert.False(lease.IsCurrent);
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

    private static string ReadMetaValue(string path, string key)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM workspace_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string)command.ExecuteScalar()!;
    }

    private static void SetMetaValue(string path, string key, string value)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workspace_meta SET value = $value WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadItemColumns(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(items);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static HashSet<string> ReadMetaKeys(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM workspace_meta;";
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

}
