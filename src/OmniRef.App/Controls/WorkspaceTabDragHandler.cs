using System;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;

namespace OmniRef.App.Controls;

internal sealed class WorkspaceTabDragHandler : IDragSource
{
    private readonly Func<bool> _canStartDrag;
    private readonly Action _dragStarted;
    private readonly Action _dragFinished;

    public WorkspaceTabDragHandler(
        Func<bool> canStartDrag,
        Action dragStarted,
        Action dragFinished)
    {
        _canStartDrag = canStartDrag;
        _dragStarted = dragStarted;
        _dragFinished = dragFinished;
    }

    public bool CanStartDrag(IDragInfo dragInfo) =>
        _canStartDrag() && GongDragDrop.DefaultDragHandler.CanStartDrag(dragInfo);

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
