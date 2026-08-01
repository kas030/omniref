using System;
using System.Collections.Generic;
using GongSolutions.Wpf.DragDrop;

namespace OmniRef.App.Controls;

internal sealed class WorkspaceTabDropHandler : IDropTarget
{
    private readonly Func<IReadOnlyList<object>> _captureOrder;
    private readonly Action<double> _updateAutoScroll;
    private readonly Action _stopAutoScroll;
    private readonly Action _orderChanged;

    public WorkspaceTabDropHandler(
        Func<IReadOnlyList<object>> captureOrder,
        Action<double> updateAutoScroll,
        Action stopAutoScroll,
        Action orderChanged)
    {
        _captureOrder = captureOrder;
        _updateAutoScroll = updateAutoScroll;
        _stopAutoScroll = stopAutoScroll;
        _orderChanged = orderChanged;
    }

    public void DropHint(IDropHintInfo dropHintInfo) =>
        DragDrop.DefaultDropHandler.DropHint(dropHintInfo);

    public void DragEnter(IDropInfo dropInfo) =>
        DragDrop.DefaultDropHandler.DragEnter(dropInfo);

    public void DragOver(IDropInfo dropInfo)
    {
        DragDrop.DefaultDropHandler.DragOver(dropInfo);
        if (dropInfo.Effects == System.Windows.DragDropEffects.None)
        {
            _stopAutoScroll();
            return;
        }

        _updateAutoScroll(dropInfo.DropPosition.X);
    }

    public void DragLeave(IDropInfo dropInfo)
    {
        _stopAutoScroll();
        DragDrop.DefaultDropHandler.DragLeave(dropInfo);
    }

    public void Drop(IDropInfo dropInfo)
    {
        _stopAutoScroll();
        var previousOrder = _captureOrder();
        DragDrop.DefaultDropHandler.Drop(dropInfo);
        var currentOrder = _captureOrder();
        if (!OrdersMatch(previousOrder, currentOrder))
        {
            _orderChanged();
        }
    }

    private static bool OrdersMatch(IReadOnlyList<object> first, IReadOnlyList<object> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (!ReferenceEquals(first[index], second[index]))
            {
                return false;
            }
        }

        return true;
    }
}
