using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ADBControl.Desktop.Controls;

public sealed record InteractiveSurfacePalette(
    Brush Background,
    Brush HoverBackground,
    Brush PressedBackground,
    Brush SelectedBackground,
    Brush Border,
    Brush SelectedBorder,
    Brush Foreground,
    Brush SelectedForeground);

// Draws every visual state itself while Border provides only layout and hit testing.
// This keeps the interaction surface free from WinUI Button/ListView selection templates.
public sealed class InteractiveSurface : UserControl
{
    private readonly InteractiveSurfacePalette _palette;
    private readonly Border _frame;
    private readonly bool _preserveContentForeground;
    private bool _isPressed;
    private bool _isPointerOver;
    private bool _isSelected;
    private bool _isEnabled = true;
    private bool _invokedFromPointerRelease;

    public InteractiveSurface(
        UIElement content,
        InteractiveSurfacePalette palette,
        CornerRadius cornerRadius,
        Thickness padding,
        bool preserveContentForeground = false)
    {
        _palette = palette;
        _preserveContentForeground = preserveContentForeground;
        _frame = new Border
        {
            Child = content,
            CornerRadius = cornerRadius,
            Padding = padding,
            BorderThickness = new Thickness(1),
            Background = palette.Background,
            BorderBrush = palette.Border,
        };
        Content = _frame;
        UseLayoutRounding = true;
        PointerEntered += (_, _) =>
        {
            _isPointerOver = true;
            ApplyState();
        };
        PointerExited += (_, _) =>
        {
            _isPointerOver = false;
            _isPressed = false;
            ApplyState();
        };
        PointerPressed += (_, e) =>
        {
            if (!IsInteractive)
                return;
            _isPressed = true;
            CapturePointer(e.Pointer);
            ApplyState();
        };
        PointerReleased += (_, e) =>
        {
            var invoke = _isPressed && IsInteractive;
            _isPressed = false;
            ReleasePointerCapture(e.Pointer);
            ApplyState();
            if (invoke)
            {
                // Some input sources surface only Tapped; keep one invocation per gesture.
                _invokedFromPointerRelease = true;
                Invoked?.Invoke(this, EventArgs.Empty);
            }
        };
        Tapped += (_, _) =>
        {
            if (_invokedFromPointerRelease)
            {
                _invokedFromPointerRelease = false;
                return;
            }

            if (IsInteractive)
                Invoked?.Invoke(this, EventArgs.Empty);
        };
        PointerCaptureLost += (_, _) =>
        {
            _isPressed = false;
            ApplyState();
        };
    }

    public event EventHandler? Invoked;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            ApplyState();
        }
    }

    public bool IsInteractive
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            IsHitTestVisible = value;
            Opacity = value ? 1 : 0.56;
            ApplyState();
        }
    }

    public void SetAutomationName(string name)
    {
        AutomationProperties.SetName(this, name);
    }

    private void ApplyState()
    {
        if (_isSelected)
        {
            _frame.Background = _palette.SelectedBackground;
            _frame.BorderBrush = _palette.SelectedBorder;
            ApplyPaletteForeground(_palette.SelectedForeground);
            return;
        }

        _frame.Background = _isPressed
            ? _palette.PressedBackground
            : _isPointerOver && _isEnabled
                ? _palette.HoverBackground
                : _palette.Background;
        _frame.BorderBrush = _palette.Border;
        ApplyPaletteForeground(_palette.Foreground);
    }

    private void ApplyPaletteForeground(Brush foreground)
    {
        if (!_preserveContentForeground)
            ApplyForeground(_frame.Child, foreground);
    }
    private static void ApplyForeground(UIElement? element, Brush foreground)
    {
        switch (element)
        {
            case TextBlock text:
                text.Foreground = foreground;
                break;
            case FontIcon icon:
                icon.Foreground = foreground;
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<UIElement>())
                    ApplyForeground(child, foreground);
                break;
            case Border border:
                ApplyForeground(border.Child, foreground);
                break;
            case ContentControl control:
                ApplyForeground(control.Content as UIElement, foreground);
                break;
        }
    }
}
