using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using OmniRef.App.Controls;

namespace OmniRef.Tests;

public sealed class OverflowFadingTextBlockTests
{
    [Fact]
    public void FadeMask_TracksOverflowAcrossWidthChanges()
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    var textBlock = new OverflowFadingTextBlock
                    {
                        Text = "A workspace title that needs room"
                    };

                    LayoutAtWidth(textBlock, 400);
                    Assert.Null(textBlock.OpacityMask);

                    foreach (var width in new[] { 120d, 80d, 40d })
                    {
                        LayoutAtWidth(textBlock, width);
                        Assert.NotNull(textBlock.OpacityMask);
                    }

                    LayoutAtWidth(textBlock, 400);
                    Assert.Null(textBlock.OpacityMask);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void LayoutAtWidth(OverflowFadingTextBlock textBlock, double width)
    {
        textBlock.Measure(new Size(width, 40));
        textBlock.Arrange(new Rect(0, 0, width, 40));
    }
}
