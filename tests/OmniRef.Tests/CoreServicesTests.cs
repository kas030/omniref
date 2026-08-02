using System.Diagnostics;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;

namespace OmniRef.Tests;

public sealed class CoreServicesTests
{
    [Fact]
    public void ZoomAt_PreservesWorldPointUnderCursor()
    {
        var origin = new WorldPoint(-100, 75);
        var cursor = new WorldPoint(430, 280);
        var before = ViewportMath.ScreenToWorld(cursor, origin, 1.25);

        var result = ViewportMath.ZoomAt(cursor, origin, 1.25, 2.5);
        var after = ViewportMath.ScreenToWorld(cursor, result.Origin, result.Zoom);

        Assert.Equal(before.X, after.X, 8);
        Assert.Equal(before.Y, after.Y, 8);
    }

    [Theory]
    [InlineData(0.1, 256)]
    [InlineData(0.25, 128)]
    [InlineData(0.5, 64)]
    [InlineData(1, 32)]
    [InlineData(2, 16)]
    [InlineData(4, 8)]
    [InlineData(8, 4)]
    public void GridVisualStep_KeepsStableScreenDensity(double zoom, double expectedStep)
    {
        var step = GridMath.GetVisualStep(zoom);

        Assert.Equal(expectedStep, step);
        Assert.InRange(step * zoom, 22, 46);
    }

    [Theory]
    [InlineData(11, 8)]
    [InlineData(12, 16)]
    [InlineData(-11, -8)]
    [InlineData(-12, -16)]
    public void GridSnap_IsSymmetricAcrossOrigin(double value, double expected)
    {
        Assert.Equal(expected, GridMath.Snap(value));
    }

    [Fact]
    public void GridSnapTranslation_UsesGroupAnchor()
    {
        var bounds = new WorldRect(13, -19, 200, 100);

        var delta = GridMath.SnapTranslation(bounds, 10, -10);

        Assert.Equal(11, delta.X);
        Assert.Equal(-13, delta.Y);
    }

    [Fact]
    public void AspectRatioResize_ChangesContinuouslyAcrossDiagonalDrag()
    {
        var initial = new WorldSize(300, 220);
        var minimum = new WorldSize(80, 60);

        var beforeBoundary = ResizeMath.ConstrainToAspectRatio(
            initial,
            new WorldSize(400, 319.9),
            minimum);
        var afterBoundary = ResizeMath.ConstrainToAspectRatio(
            initial,
            new WorldSize(400, 320.1),
            minimum);

        Assert.InRange(Math.Abs(afterBoundary.Width - beforeBoundary.Width), 0, 0.1);
        Assert.InRange(Math.Abs(afterBoundary.Height - beforeBoundary.Height), 0, 0.1);
        Assert.Equal(initial.Width / initial.Height, beforeBoundary.Width / beforeBoundary.Height, 10);
        Assert.Equal(initial.Width / initial.Height, afterBoundary.Width / afterBoundary.Height, 10);
    }

    [Fact]
    public void AspectRatioResize_UsesSingleScaleForBothMinimumDimensions()
    {
        var size = ResizeMath.ConstrainToAspectRatio(
            new WorldSize(400, 100),
            new WorldSize(10, 10),
            new WorldSize(80, 60));

        Assert.Equal(new WorldSize(240, 60), size);
    }

    [Fact]
    public void SpatialIndex_HandlesNegativeCellsAndUpdates()
    {
        var index = new SpatialHashIndex<Guid>(128);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        index.AddOrUpdate(first, new WorldRect(-300, -200, 60, 60));
        index.AddOrUpdate(second, new WorldRect(10, 10, 40, 40));

        Assert.Contains(first, index.Query(new WorldRect(-320, -220, 100, 100)));
        Assert.DoesNotContain(second, index.Query(new WorldRect(-320, -220, 100, 100)));

        index.AddOrUpdate(first, new WorldRect(15, 15, 10, 10));
        var result = index.Query(new WorldRect(0, 0, 100, 100));
        Assert.Contains(first, result);
        Assert.Contains(second, result);
    }

    [Fact]
    public void WorldRectUnion_ContainsEveryInputRectangle()
    {
        var union = WorldRect.Union(
        [
            new WorldRect(-20, 10, 25, 20),
            new WorldRect(40, -15, 10, 80),
            new WorldRect(0, 0, 5, 5)
        ]);

        Assert.Equal(new WorldRect(-20, -15, 70, 80), union);
    }

    [Fact]
    public void SpatialIndex_QueriesThreeThousandItemsQuickly()
    {
        var index = new SpatialHashIndex<int>();
        for (var item = 0; item < 3000; item++)
        {
            index.AddOrUpdate(item, new WorldRect((item % 100) * 60, (item / 100) * 60, 50, 50));
        }

        var watch = Stopwatch.StartNew();
        for (var query = 0; query < 500; query++)
        {
            _ = index.Query(new WorldRect(query % 100 * 20, query % 30 * 20, 1920, 1080));
        }
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Spatial queries took {watch.Elapsed}.");
    }

    [Fact]
    public void UndoHistory_EnforcesCapacityAndSupportsRedo()
    {
        var value = 0;
        var history = new UndoHistory(2);
        for (var index = 1; index <= 3; index++)
        {
            var next = index;
            var previous = value;
            history.Execute(new DelegateUndoableCommand(
                $"Set {next}",
                () => value = next,
                () => value = previous));
        }

        history.Undo();
        Assert.Equal(2, value);
        history.Undo();
        Assert.Equal(1, value);
        history.Undo();
        Assert.Equal(1, value);
        history.Redo();
        Assert.Equal(2, value);
    }

    [Fact]
    public void Search_FindsChineseTextPathsAndTags()
    {
        var items = new[]
        {
            new BoardItem
            {
                Title = "设计资料",
                Kind = ItemKind.Text,
                Content = new TextContent("常用颜色"),
                Tags = ["视觉", "项目甲"]
            },
            new BoardItem
            {
                Title = "Notes",
                Kind = ItemKind.Text,
                Content = new TextContent("meeting")
            }
        };

        Assert.Single(WorkspaceSearch.Search(items, "项目甲"));
        Assert.Single(WorkspaceSearch.Search(items, "颜色"));
        Assert.Single(WorkspaceSearch.Search(items, "MEETING"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyQueryReturnsAllItems(string query)
    {
        var older = new BoardItem
        {
            Title = "Older",
            ModifiedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var newer = new BoardItem
        {
            Title = "Newer",
            ModifiedUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var results = WorkspaceSearch.Search([older, newer], query);

        Assert.Equal([newer, older], results);
    }

    [Fact]
    public void Search_ThreeThousandItemsCompletesWithinInteractiveTarget()
    {
        var items = Enumerable.Range(0, 3000)
            .Select(index => new BoardItem
            {
                Title = $"Reference {index}",
                Kind = ItemKind.Text,
                Content = new TextContent(index == 2874 ? "needle 项目目标" : $"notes {index}"),
                Tags = [$"tag-{index % 20}"]
            })
            .ToList();

        var watch = Stopwatch.StartNew();
        var results = WorkspaceSearch.Search(items, "项目目标");
        watch.Stop();

        Assert.Single(results);
        Assert.True(
            watch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Search took {watch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Theory]
    [InlineData("https://example.com", ClipboardImportKind.Url)]
    [InlineData("http://example.com/a", ClipboardImportKind.Url)]
    [InlineData("remember this", ClipboardImportKind.Text)]
    [InlineData("", ClipboardImportKind.None)]
    public void ClipboardClassifier_UsesStablePrecedence(string text, ClipboardImportKind expected)
    {
        var importer = new DefaultClipboardImporter();
        var result = importer.Classify(new ClipboardSnapshot([], null, text));
        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public void ClipboardClassifier_PrefersFilesThenBitmapOverText()
    {
        var importer = new DefaultClipboardImporter();

        Assert.Equal(
            ClipboardImportKind.Files,
            importer.Classify(new ClipboardSnapshot([@"C:\one.txt"], [1, 2], "text")).Kind);
        Assert.Equal(
            ClipboardImportKind.Image,
            importer.Classify(new ClipboardSnapshot([], [1, 2], "text")).Kind);
    }

    [Fact]
    public void PathResolver_PrefersExistingRelativePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniRef.PathTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workspace = Path.Combine(directory, "board.omniref");
            var movedAsset = Path.Combine(directory, "assets", "reference.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(movedAsset)!);
            File.WriteAllText(movedAsset, "reference");
            var source = new SourceDescriptor(
                @"C:\old-location\reference.txt",
                Path.Combine("assets", "reference.txt"),
                AssetMode.ExternalReference,
                null,
                "reference.txt",
                null,
                null);

            Assert.Equal(movedAsset, PathResolver.Resolve(workspace, source));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
