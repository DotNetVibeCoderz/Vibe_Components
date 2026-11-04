namespace OpenSceneGraph.Net.Manipulators;

public enum InputEventType : byte { Push, Release, Drag, Move, Scroll, KeyDown, KeyUp, Resize, Frame, DoubleClick }

[Flags]
public enum MouseButtons : byte { None = 0, Left = 1, Middle = 2, Right = 4 }

[Flags]
public enum KeyModifiers : byte { None = 0, Shift = 1, Control = 2, Alt = 4 }

/// <summary>
/// Platform-neutral input event (<c>osgGA::GUIEventAdapter</c>). Coordinates are pixels with the origin at the
/// top-left of the view; hosts (Avalonia, Blazor, native windows) translate their events into this type.
/// </summary>
public readonly record struct InputEvent(
    InputEventType Type,
    float X = 0,
    float Y = 0,
    MouseButtons Buttons = MouseButtons.None,
    float ScrollDelta = 0,
    string? Key = null,
    KeyModifiers Modifiers = KeyModifiers.None,
    int Width = 0,
    int Height = 0)
{
    /// <summary>X normalised to -1..1 across the view.</summary>
    public float NormalizedX => Width > 0 ? X / Width * 2 - 1 : 0;

    /// <summary>Y normalised to -1..1 (up is positive).</summary>
    public float NormalizedY => Height > 0 ? 1 - Y / Height * 2 : 0;

    public bool IsKey(string key) => string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Handles input events (<c>osgGA::GUIEventHandler</c>). Return true to consume the event.</summary>
public interface IEventHandler
{
    bool Handle(in InputEvent e, Viewer viewer);
}

/// <summary>Thread-safe queue that hosts push events into; the viewer drains it at the start of each frame.</summary>
public sealed class EventQueue
{
    private readonly Lock _lock = new();
    private List<InputEvent> _pending = new(32);
    private List<InputEvent> _swap = new(32);

    public void Push(in InputEvent e)
    {
        lock (_lock)
        {
            // coalesce consecutive drags/moves to keep the queue short at high pointer rates
            if (_pending.Count > 0 && e.Type is InputEventType.Drag or InputEventType.Move && _pending[^1].Type == e.Type)
                _pending[^1] = e;
            else _pending.Add(e);
        }
    }

    internal List<InputEvent> Drain()
    {
        lock (_lock)
        {
            (_pending, _swap) = (_swap, _pending);
            _pending.Clear();
            return _swap;
        }
    }
}
