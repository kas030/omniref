using OmniRef.Core.Models;
using OmniRef.Core.Services;

namespace OmniRef.Tests;

public sealed class BoardItemFactoryTests
{
    [Fact]
    public void FromPath_FileTitleIncludesExtension()
    {
        var item = BoardItemFactory.FromPath(
            @"C:\workspaces\board.omniref",
            @"C:\assets\reference.pdf",
            new WorldPoint(0, 0),
            1);

        Assert.Equal(ItemKind.File, item.Kind);
        Assert.Equal("reference.pdf", item.Title);
    }

    [Fact]
    public void FromPath_ImageTitleStillOmitsExtension()
    {
        var item = BoardItemFactory.FromPath(
            @"C:\workspaces\board.omniref",
            @"C:\assets\reference.png",
            new WorldPoint(0, 0),
            1);

        Assert.Equal(ItemKind.Image, item.Kind);
        Assert.Equal("reference", item.Title);
    }
}
