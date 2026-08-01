using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;

namespace OmniRef.Infrastructure.Windows.Persistence;

public sealed class SqliteWorkspaceStore : IWorkspaceStore, IDisposable
{
    public const long MaximumEmbeddedAssetBytes = 512L * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private bool _disposed;

    public IWorkspaceFileLease AcquireFileLease(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new WorkspaceFileLease(path);
    }

    public async Task<WorkspaceOpenResult> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Workspace file was not found.", fullPath);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OpenCore(fullPath), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string path,
        WorkspaceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        var fullPath = Path.GetFullPath(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => SaveCore(fullPath, document, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsAsync(
        string sourcePath,
        string destinationPath,
        WorkspaceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(document);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);

        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync(destinationFullPath, document, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                    () => SaveAsCore(sourceFullPath, destinationFullPath, document, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
        string workspacePath,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullSourcePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Asset source was not found.", fullSourcePath);
        }

        return ImportFileCoreAsync(workspacePath, file, cancellationToken);
    }

    public async Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
        string workspacePath,
        Stream source,
        string fileName,
        string? mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Stream workingStream = source;
        string? temporaryPath = null;
        try
        {
            if (!source.CanSeek)
            {
                temporaryPath = Path.Combine(Path.GetTempPath(), $"omniref-{Guid.NewGuid():N}.asset");
                await using (var temporary = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.ReadWrite,
                                 FileShare.None,
                                 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(temporary, cancellationToken).ConfigureAwait(false);
                }

                workingStream = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.SequentialScan);
            }

            if (workingStream.Length - workingStream.Position > MaximumEmbeddedAssetBytes)
            {
                throw new InvalidOperationException("Embedded assets cannot exceed 512 MB.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                        () => ImportStreamCore(
                            Path.GetFullPath(workspacePath),
                            workingStream,
                            fileName,
                            mediaType,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (!ReferenceEquals(workingStream, source))
            {
                await workingStream.DisposeAsync().ConfigureAwait(false);
            }

            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public async Task ExportEmbeddedAssetAsync(
        string workspacePath,
        Guid assetId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var workspaceFullPath = Path.GetFullPath(workspacePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                    () => ExportCore(workspaceFullPath, assetId, destinationFullPath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadEmbeddedAssetAsync(
        string workspacePath,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => ReadAssetCore(Path.GetFullPath(workspacePath), assetId, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompactAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                    () => CompactCore(Path.GetFullPath(workspacePath), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private WorkspaceOpenResult OpenCore(string path)
    {
        using var inspection = OpenConnection(path, readOnly: true);
        if (!HasWorkspaceSchema(inspection))
        {
            throw new InvalidDataException("The selected file is not an OmniRef workspace.");
        }

        var schemaVersion = ReadIntMeta(inspection, "schema_version");
        if (schemaVersion > WorkspaceDocument.CurrentSchemaVersion)
        {
            return new(
                LoadDocument(inspection, schemaVersion),
                WorkspaceOpenMode.ReadOnly,
                "This workspace was created by a newer OmniRef version and is read-only.");
        }

        if (schemaVersion < WorkspaceDocument.CurrentSchemaVersion)
        {
            inspection.Close();
            BackupBeforeMigration(path, schemaVersion);
            using var migrationConnection = OpenConnection(path, readOnly: false);
            Migrate(migrationConnection, schemaVersion);
            schemaVersion = WorkspaceDocument.CurrentSchemaVersion;
            return new(
                LoadDocument(migrationConnection, schemaVersion),
                WorkspaceOpenMode.ReadWrite,
                null);
        }

        var mode = CanWriteFile(path)
            ? WorkspaceOpenMode.ReadWrite
            : WorkspaceOpenMode.ReadOnly;
        var warning = mode == WorkspaceOpenMode.ReadOnly
            ? "The workspace file is not writable and was opened read-only."
            : null;
        return new(LoadDocument(inspection, schemaVersion), mode, warning);
    }

    private WorkspaceDocument LoadDocument(SqliteConnection connection, int schemaVersion)
    {
        var document = new WorkspaceDocument
        {
            SchemaVersion = schemaVersion,
            Id = Guid.Parse(ReadMeta(connection, "workspace_id")),
            Title = ReadMeta(connection, "title"),
            CreatedUtc = ParseDate(ReadMeta(connection, "created_utc")),
            ModifiedUtc = ParseDate(ReadMeta(connection, "modified_utc")),
            ViewportOrigin = new(
                ReadDoubleMeta(connection, "viewport_x"),
                ReadDoubleMeta(connection, "viewport_y")),
            Zoom = ReadDoubleMeta(connection, "zoom")
        };

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, kind, title, x, y, width, height, z_index, parent_frame_id,
                   style_json, content_json, created_utc, modified_utc
            FROM items
            ORDER BY z_index, created_utc;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new BoardItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                Kind = (ItemKind)reader.GetInt32(1),
                Title = reader.GetString(2),
                Bounds = new(
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6)),
                ZIndex = reader.GetInt32(7),
                ParentFrameId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
                Style = JsonSerializer.Deserialize<ItemStyle>(reader.GetString(9), _jsonOptions)
                        ?? new ItemStyle(),
                Content = JsonSerializer.Deserialize<ItemContent>(reader.GetString(10), _jsonOptions)
                          ?? throw new InvalidDataException("An item has invalid content."),
                CreatedUtc = ParseDate(reader.GetString(11)),
                ModifiedUtc = ParseDate(reader.GetString(12))
            };
            document.Items.Add(item);
        }

        LoadTags(connection, document.Items);
        return document;
    }

    private void SaveCore(string path, WorkspaceDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var connection = OpenConnection(path, readOnly: false);
        if (HasWorkspaceSchema(connection))
        {
            var diskVersion = ReadIntMeta(connection, "schema_version");
            if (diskVersion > WorkspaceDocument.CurrentSchemaVersion)
            {
                throw new InvalidOperationException("A newer workspace cannot be overwritten.");
            }
            if (diskVersion < WorkspaceDocument.CurrentSchemaVersion)
            {
                BackupBeforeMigration(path, diskVersion);
                Migrate(connection, diskVersion);
            }
        }
        else
        {
            EnsureSchema(connection);
        }

        document.SchemaVersion = WorkspaceDocument.CurrentSchemaVersion;
        document.ModifiedUtc = DateTimeOffset.UtcNow;

        using var transaction = connection.BeginTransaction();
        WriteMeta(connection, transaction, document);
        Execute(connection, transaction, "DELETE FROM item_tags; DELETE FROM tags; DELETE FROM items;");

        using var insertItem = connection.CreateCommand();
        insertItem.Transaction = transaction;
        insertItem.CommandText =
            """
            INSERT INTO items(
                id, kind, title, x, y, width, height, z_index, parent_frame_id,
                style_json, content_json, created_utc, modified_utc)
            VALUES(
                $id, $kind, $title, $x, $y, $width, $height, $z, $parent,
                $style, $content, $created, $modified);
            """;
        var parameters = new[]
        {
            insertItem.Parameters.Add("$id", SqliteType.Text),
            insertItem.Parameters.Add("$kind", SqliteType.Integer),
            insertItem.Parameters.Add("$title", SqliteType.Text),
            insertItem.Parameters.Add("$x", SqliteType.Real),
            insertItem.Parameters.Add("$y", SqliteType.Real),
            insertItem.Parameters.Add("$width", SqliteType.Real),
            insertItem.Parameters.Add("$height", SqliteType.Real),
            insertItem.Parameters.Add("$z", SqliteType.Integer),
            insertItem.Parameters.Add("$parent", SqliteType.Text),
            insertItem.Parameters.Add("$style", SqliteType.Text),
            insertItem.Parameters.Add("$content", SqliteType.Text),
            insertItem.Parameters.Add("$created", SqliteType.Text),
            insertItem.Parameters.Add("$modified", SqliteType.Text)
        };

        foreach (var item in document.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parameters[0].Value = item.Id.ToString("D");
            parameters[1].Value = (int)item.Kind;
            parameters[2].Value = item.Title;
            parameters[3].Value = item.Bounds.X;
            parameters[4].Value = item.Bounds.Y;
            parameters[5].Value = item.Bounds.Width;
            parameters[6].Value = item.Bounds.Height;
            parameters[7].Value = item.ZIndex;
            parameters[8].Value = item.ParentFrameId?.ToString("D") ?? (object)DBNull.Value;
            parameters[9].Value = JsonSerializer.Serialize(item.Style, _jsonOptions);
            parameters[10].Value = JsonSerializer.Serialize<ItemContent>(item.Content, _jsonOptions);
            parameters[11].Value = FormatDate(item.CreatedUtc);
            parameters[12].Value = FormatDate(item.ModifiedUtc);
            insertItem.ExecuteNonQuery();
        }

        SaveTags(connection, transaction, document.Items);
        transaction.Commit();
    }

    private void SaveAsCore(
        string sourcePath,
        string destinationPath,
        WorkspaceDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? ".",
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            if (File.Exists(sourcePath))
            {
                using var source = OpenConnection(sourcePath, readOnly: true);
                using var destination = OpenConnection(temporaryPath, readOnly: false);
                source.BackupDatabase(destination);
            }

            SaveCore(temporaryPath, document, cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<EmbeddedAssetInfo> ImportFileCoreAsync(
        string workspacePath,
        FileInfo file,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ImportEmbeddedAssetAsync(
                workspacePath,
                source,
                file.Name,
                MediaTypeForExtension(file.Extension),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private EmbeddedAssetInfo ImportStreamCore(
        string workspacePath,
        Stream source,
        string fileName,
        string? mediaType,
        CancellationToken cancellationToken)
    {
        var length = source.Length - source.Position;
        if (length > MaximumEmbeddedAssetBytes)
        {
            throw new InvalidOperationException("Embedded assets cannot exceed 512 MB.");
        }

        using var connection = OpenConnection(workspacePath, readOnly: false);
        EnsureSchema(connection);
        using var transaction = connection.BeginTransaction();
        var assetId = Guid.NewGuid();
        long rowId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO embedded_assets(id, file_name, media_type, length, sha256, data)
                VALUES($id, $name, $media, $length, '', zeroblob($length));
                SELECT rowid FROM embedded_assets WHERE id = $id;
                """;
            insert.Parameters.AddWithValue("$id", assetId.ToString("D"));
            insert.Parameters.AddWithValue("$name", fileName);
            insert.Parameters.AddWithValue("$media", mediaType ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$length", length);
            rowId = (long)(insert.ExecuteScalar()
                           ?? throw new InvalidOperationException("Could not allocate embedded asset."));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var destination = new SqliteBlob(connection, "embedded_assets", "data", rowId, readOnly: false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE embedded_assets SET sha256 = $hash WHERE id = $id;";
            update.Parameters.AddWithValue("$hash", sha256);
            update.Parameters.AddWithValue("$id", assetId.ToString("D"));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return new(assetId, fileName, mediaType, length, sha256);
    }

    private static void ExportCore(
        string workspacePath,
        Guid assetId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(workspacePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rowid, data FROM embedded_assets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException($"Embedded asset {assetId} was not found.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = reader.GetStream(1))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                CopyStream(source, destination, cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static byte[] ReadAssetCore(
        string workspacePath,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(workspacePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, data FROM embedded_assets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException($"Embedded asset {assetId} was not found.");
        }

        using var source = reader.GetStream(1);
        using var destination = new MemoryStream();
        CopyStream(source, destination, cancellationToken);
        return destination.ToArray();
    }

    private void CompactCore(string workspacePath, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(workspacePath, readOnly: false);
        var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT content_json FROM items;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = JsonSerializer.Deserialize<ItemContent>(reader.GetString(0), _jsonOptions);
                var assetId = content switch
                {
                    ImageContent image => image.Source.EmbeddedAssetId,
                    FileContent file => file.Source.EmbeddedAssetId,
                    _ => null
                };
                if (assetId.HasValue)
                {
                    referencedAssets.Add(assetId.Value.ToString("D"));
                }
            }
        }

        using (var transaction = connection.BeginTransaction())
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM embedded_assets;";
            var allIds = new List<string>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    allIds.Add(reader.GetString(0));
                }
            }

            foreach (var id in allIds.Where(id => !referencedAssets.Contains(id)))
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM embedded_assets WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Execute(connection, null, "VACUUM;");
    }

    private static SqliteConnection OpenConnection(string path, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = readOnly
            ? "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;"
            : """
              PRAGMA foreign_keys = ON;
              PRAGMA journal_mode = DELETE;
              PRAGMA synchronous = FULL;
              PRAGMA busy_timeout = 3000;
              """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool CanWriteFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.CanWrite;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasWorkspaceSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'workspace_meta';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        Execute(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspace_meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS items(
                id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                title TEXT NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL,
                width REAL NOT NULL,
                height REAL NOT NULL,
                z_index INTEGER NOT NULL,
                parent_frame_id TEXT NULL,
                style_json TEXT NOT NULL,
                content_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                modified_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_items_z_index ON items(z_index);
            CREATE INDEX IF NOT EXISTS ix_items_parent_frame_id ON items(parent_frame_id);
            CREATE TABLE IF NOT EXISTS tags(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            CREATE TABLE IF NOT EXISTS item_tags(
                item_id TEXT NOT NULL,
                tag_id INTEGER NOT NULL,
                PRIMARY KEY(item_id, tag_id),
                FOREIGN KEY(item_id) REFERENCES items(id) ON DELETE CASCADE,
                FOREIGN KEY(tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS embedded_assets(
                id TEXT PRIMARY KEY,
                file_name TEXT NOT NULL,
                media_type TEXT NULL,
                length INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                data BLOB NOT NULL
            );
            """);

        using var transaction = connection.BeginTransaction();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText =
                "INSERT OR IGNORE INTO workspace_meta(key, value) VALUES('schema_version', $version);";
            schema.Parameters.AddWithValue(
                "$version",
                WorkspaceDocument.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            schema.ExecuteNonQuery();
        }
        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText =
            "INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES($version, $date);";
        migration.Parameters.AddWithValue("$version", WorkspaceDocument.CurrentSchemaVersion);
        migration.Parameters.AddWithValue("$date", FormatDate(DateTimeOffset.UtcNow));
        migration.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void Migrate(SqliteConnection connection, int currentVersion)
    {
        if (currentVersion == WorkspaceDocument.CurrentSchemaVersion)
        {
            return;
        }

        throw new InvalidDataException($"Workspace schema {currentVersion} cannot be migrated.");
    }

    private static void BackupBeforeMigration(string path, int version)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backupPath = $"{path}.v{version}.{timestamp}.bak";
        File.Copy(path, backupPath, overwrite: false);
    }

    private static void WriteMeta(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceDocument document)
    {
        WriteMetaValue(connection, transaction, "schema_version", document.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        WriteMetaValue(connection, transaction, "workspace_id", document.Id.ToString("D"));
        WriteMetaValue(connection, transaction, "title", document.Title);
        WriteMetaValue(connection, transaction, "created_utc", FormatDate(document.CreatedUtc));
        WriteMetaValue(connection, transaction, "modified_utc", FormatDate(document.ModifiedUtc));
        WriteMetaValue(connection, transaction, "viewport_x", document.ViewportOrigin.X.ToString("R", CultureInfo.InvariantCulture));
        WriteMetaValue(connection, transaction, "viewport_y", document.ViewportOrigin.Y.ToString("R", CultureInfo.InvariantCulture));
        WriteMetaValue(connection, transaction, "zoom", document.Zoom.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteMetaValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO workspace_meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM workspace_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
               ?? throw new InvalidDataException($"Workspace metadata '{key}' is missing.");
    }

    private static int ReadIntMeta(SqliteConnection connection, string key) =>
        int.Parse(ReadMeta(connection, key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDoubleMeta(SqliteConnection connection, string key) =>
        double.Parse(ReadMeta(connection, key), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void SaveTags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<BoardItem> items)
    {
        var tagIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var rawTag in item.Tags)
            {
                var tag = rawTag.Trim();
                if (tag.Length == 0)
                {
                    continue;
                }

                if (!tagIds.TryGetValue(tag, out var tagId))
                {
                    using var insertTag = connection.CreateCommand();
                    insertTag.Transaction = transaction;
                    insertTag.CommandText =
                        """
                        INSERT INTO tags(name) VALUES($name)
                        ON CONFLICT(name) DO UPDATE SET name = excluded.name;
                        SELECT id FROM tags WHERE name = $name;
                        """;
                    insertTag.Parameters.AddWithValue("$name", tag);
                    tagId = (long)(insertTag.ExecuteScalar()
                                   ?? throw new InvalidOperationException("Could not save tag."));
                    tagIds[tag] = tagId;
                }

                using var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText =
                    "INSERT OR IGNORE INTO item_tags(item_id, tag_id) VALUES($item, $tag);";
                link.Parameters.AddWithValue("$item", item.Id.ToString("D"));
                link.Parameters.AddWithValue("$tag", tagId);
                link.ExecuteNonQuery();
            }
        }
    }

    private static void LoadTags(SqliteConnection connection, IReadOnlyCollection<BoardItem> items)
    {
        var byId = items.ToDictionary(item => item.Id);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT item_tags.item_id, tags.name
            FROM item_tags
            JOIN tags ON tags.id = item_tags.tag_id
            ORDER BY tags.name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var item))
            {
                item.Tags.Add(reader.GetString(1));
            }
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CopyStream(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? MediaTypeForExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
