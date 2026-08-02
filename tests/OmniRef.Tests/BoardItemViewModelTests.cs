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
}
