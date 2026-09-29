using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Core.Models;

namespace MetroHub.Presentation.Controllers;

/// <summary>
/// Encapsulates canvas marquee rubber-band selection: mouse capture, visual box layout,
/// and tile bounding-box intersection calculations.
/// </summary>
public sealed class RubberBandSelectionController
{
    private readonly FrameworkElement? _captureElement;
    private readonly System.Windows.Shapes.Rectangle? _rubberBandBox;
    private readonly Func<IEnumerable<TileModel>> _tilesProvider;
    private readonly Action _clearSelectionAction;

    private bool _isActive;
    private Point _startPoint;
    private bool _hasMoved;
    private HashSet<TileModel> _preSelected = new();

    public bool IsActive => _isActive;

    public RubberBandSelectionController(
        FrameworkElement? captureElement,
        System.Windows.Shapes.Rectangle? rubberBandBox,
        Func<IEnumerable<TileModel>> tilesProvider,
        Action clearSelectionAction)
    {
        _captureElement = captureElement;
        _rubberBandBox = rubberBandBox;
        _tilesProvider = tilesProvider;
        _clearSelectionAction = clearSelectionAction;
    }

    public void Start(Point canvasMouse, bool isCtrlDown)
    {
        _isActive = true;
        _hasMoved = false;
        _startPoint = canvasMouse;
        _preSelected = new HashSet<TileModel>(_tilesProvider().Where(t => t.IsSelected));

        if (!isCtrlDown)
        {
            _clearSelectionAction();
            _preSelected.Clear();
        }

        if (_rubberBandBox != null)
        {
            Canvas.SetLeft(_rubberBandBox, canvasMouse.X);
            Canvas.SetTop(_rubberBandBox, canvasMouse.Y);
            _rubberBandBox.Width = 0;
            _rubberBandBox.Height = 0;
            _rubberBandBox.Visibility = Visibility.Collapsed;
        }

        _captureElement?.CaptureMouse();
    }

    public void Update(Point canvasMouse, bool isCtrlDown)
    {
        if (!_isActive) return;

        Vector diff = canvasMouse - _startPoint;
        if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
        {
            _hasMoved = true;
        }

        if (_hasMoved && _rubberBandBox != null)
        {
            double left = Math.Min(_startPoint.X, canvasMouse.X);
            double top = Math.Min(_startPoint.Y, canvasMouse.Y);
            double width = Math.Abs(canvasMouse.X - _startPoint.X);
            double height = Math.Abs(canvasMouse.Y - _startPoint.Y);

            Canvas.SetLeft(_rubberBandBox, left);
            Canvas.SetTop(_rubberBandBox, top);
            _rubberBandBox.Width = width;
            _rubberBandBox.Height = height;
            _rubberBandBox.Visibility = Visibility.Visible;

            var marqueeRect = new Rect(left, top, width, height);

            foreach (var t in _tilesProvider())
            {
                var tileRect = new Rect(t.X, t.Y, t.WidthPixels, t.HeightPixels);
                bool intersects = marqueeRect.IntersectsWith(tileRect);

                if (isCtrlDown)
                {
                    t.IsSelected = intersects
                        ? !_preSelected.Contains(t)
                        : _preSelected.Contains(t);
                }
                else
                {
                    t.IsSelected = intersects;
                }
            }
        }
    }

    public void End(bool isCtrlDown)
    {
        if (!_isActive) return;

        _isActive = false;
        _captureElement?.ReleaseMouseCapture();

        if (_rubberBandBox != null)
        {
            _rubberBandBox.Visibility = Visibility.Collapsed;
        }

        if (!_hasMoved && !isCtrlDown)
        {
            _clearSelectionAction();
        }
    }

    public void Cancel()
    {
        _isActive = false;
        if (_rubberBandBox != null)
        {
            _rubberBandBox.Visibility = Visibility.Collapsed;
        }
    }
}
