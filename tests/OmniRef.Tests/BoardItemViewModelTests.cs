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
    public void Url_ExposesUrlContentForPropertiesPanel()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.Url,
            Content = new UrlContent("https://example.com/reference")
        });

        Assert.True(viewModel.IsUrl);
        Assert.Equal("https://example.com/reference", viewModel.Url);
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

    [Fact]
    public void EmbeddedFile_ExposesStorageMetadataAndApplicableActions()
    {
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.File,
            Content = new FileContent(new SourceDescriptor(
                @"C:\assets\reference.pdf",
                "assets/reference.pdf",
                AssetMode.EmbeddedCopy,
                Guid.NewGuid(),
                "reference.pdf",
                1_572_864,
                null), ".pdf")
        });

        Assert.True(viewModel.HasSource);
        Assert.True(viewModel.IsEmbedded);
        Assert.False(viewModel.IsExternalReference);
        Assert.False(viewModel.CanEmbed);
        Assert.False(viewModel.CanReveal);
        Assert.Equal("reference.pdf", viewModel.SourceFileName);
        Assert.Equal("1.5 MB", viewModel.SourceSizeText);
    }

    [Fact]
    public void ReplacingExternalSourceWithEmbeddedSource_NotifiesStorageProperties()
    {
        var external = new SourceDescriptor(
            @"C:\assets\reference.pdf",
            null,
            AssetMode.ExternalReference,
            null,
            "reference.pdf",
            1024,
            null);
        var viewModel = new BoardItemViewModel(new BoardItem
        {
            Kind = ItemKind.File,
            Content = new FileContent(external, ".pdf")
        });
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.ReplaceContent(new FileContent(external with
        {
            Mode = AssetMode.EmbeddedCopy,
            EmbeddedAssetId = Guid.NewGuid()
        }, ".pdf"));

        Assert.Contains(nameof(BoardItemViewModel.IsEmbedded), changedProperties);
        Assert.Contains(nameof(BoardItemViewModel.IsExternalReference), changedProperties);
        Assert.Contains(nameof(BoardItemViewModel.CanEmbed), changedProperties);
        Assert.Contains(nameof(BoardItemViewModel.CanReveal), changedProperties);
    }
}
