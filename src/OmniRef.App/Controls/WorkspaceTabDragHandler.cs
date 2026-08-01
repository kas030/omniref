using System;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;

namespace OmniRef.App.Controls;

internal sealed class WorkspaceTabDragHandler : IDragSource
{
    private readonly Action _dragStarted;
    private readonly Action _dragFinished;

    public WorkspaceTabDragHandler(Action dragStarted, Action dragFinished)
    {
        _dragStarted = dragStarted;
        _dragFinished = dragFinished;
    }

    public bool CanStartDrag(IDragInfo dragInfo) =>
        GongDragDrop.DefaultDragHandler.CanStartDrag(dragInfo);

    public void StartDrag(IDragInfo dragInfo)
    {
        _dragStarted();
        GongDragDrop.DefaultDragHandler.StartDrag(dragInfo);
    }

    public void Dropped(IDropInfo dropInfo) =>
        GongDragDrop.DefaultDragHandler.Dropped(dropInfo);

    public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
    {
        try
        {
            GongDragDrop.DefaultDragHandler.DragDropOperationFinished(operationResult, dragInfo);
        }
        finally
        {
            _dragFinished();
        }
    }

    public void DragCancelled()
    {
        try
        {
            GongDragDrop.DefaultDragHandler.DragCancelled();
        }
        finally
        {
            _dragFinished();
        }
    }

    public bool TryCatchOccurredException(Exception exception) =>
        GongDragDrop.DefaultDragHandler.TryCatchOccurredException(exception);
}
