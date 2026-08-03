using OmniRef.App.ViewModels;
using OmniRef.Core.Models;

namespace OmniRef.Tests;

public sealed class BoardItemViewModelTests
{
    [Fact]
    public void SecondaryPreviewText_ReplacesLineBreaksWithSpaces()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.Text,
            Content = new TextContent("First\r\nSecond\nThird\rFourth")
        });

        Assert.Equal("First Second Third Fourth", viewModel.SecondaryPreviewText);
    }

    [Fact]
    public void ReplaceContent_NotifiesSecondaryPreviewTextChange()
    {
        var viewModel = new BoardItemViewModel(new BoardItem());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.ReplaceContent(new TextContent("Updated\ntext"));

        Assert.Contains(nameof(BoardItemViewModel.SecondaryPreviewText), changedProperties);
        Assert.Equal("Updated text", viewModel.SecondaryPreviewText);
    }

    [Fact]
    public void SourcePath_UsesAbsolutePathWhenAvailable()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.File,
            Content = new FileContent(new SourceDescriptor(
                @"C:\assets\reference.pdf",
                "assets/reference.pdf",
                AssetMode.ExternalReference,
                null,
                "reference.pdf",
                null,
                null))
        });

        Assert.True(viewModel.HasSourcePath);
        Assert.Equal(@"C:\assets\reference.pdf", viewModel.SourcePath);
    }

    [Fact]
    public void SourcePath_FallsBackToRelativePath()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.Folder,
            Content = new FolderContent(new SourceDescriptor(
                null,
                "assets/reference",
                AssetMode.ExternalReference,
                null,
                "reference",
                null,
                null))
        });

        Assert.Equal("assets/reference", viewModel.SourcePath);
    }

    [Fact]
    public void SourcePath_IsEmptyForNonFileContent()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.Text,
            Content = new TextContent("Text")
        });

        Assert.False(viewModel.HasSourcePath);
        Assert.Equal(string.Empty, viewModel.SourcePath);
    }

    [Fact]
    public void FileType_UsesUppercaseExtensionWithoutLeadingDot()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.File,
            Content = new FileContent(new SourceDescriptor(
                @"C:\assets\reference.pdf",
                null,
                AssetMode.ExternalReference,
                null,
                "reference.pdf",
                null,
                null), ".pdf")
        });

        Assert.True(viewModel.IsFile);
        Assert.Equal("PDF", viewModel.FileType);
    }
}
