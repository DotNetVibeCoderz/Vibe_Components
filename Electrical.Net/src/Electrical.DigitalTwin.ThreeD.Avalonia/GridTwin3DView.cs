using System.Numerics;
using Electrical.DigitalTwin.ThreeD;
using ThreeNet;
using ThreeNet.Avalonia;

namespace Electrical.DigitalTwin.ThreeD.Avalonia;

/// <summary>
/// Avalonia control showing a <see cref="GridScene3D"/> through ThreeNet: orbit camera (drag to rotate, right-drag to pan, wheel to
/// zoom), materials synchronized with the model every frame, alarm flashing and projected HUD labels. Update the model with
/// <see cref="GridScene3D.ApplyPowerFlow"/> / <see cref="GridScene3D.ApplyTwinState"/> from any thread-safe point on the UI thread;
/// the next frame shows it.
/// </summary>
/// <example>
/// <code>
/// var model = GridSceneBuilder.Build(TestCases.Case14());
/// var view = new GridTwin3DView { Model = model };
/// model.ApplyPowerFlow(PowerFlowSolver.Solve(net));
/// </code>
/// </example>
public class GridTwin3DView : ThreeNetView
{
    private ThreeNetGridScene? _grid;
    private OrbitController? _orbit;
    private GridScene3D? _model;

    public GridTwin3DView()
    {
        Frame += OnFrame;
    }

    /// <summary>The 3D model; setting it rebuilds the ThreeNet scene and frames the camera.</summary>
    public GridScene3D? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(value, _model)) return;
            _orbit?.Dispose(); _orbit = null;
            _grid?.Dispose(); _grid = null;
            _model = value;
            if (value is null) { Scene = null; Camera = null; return; }
            _grid = ThreeNetGridScene.Create(value);
            Scene = _grid.Scene;
            Camera = _grid.Camera;
            var (lo, hi) = value.Bounds();
            _orbit = new OrbitController(this, _grid.Camera) { MaxDistance = 20000, Pitch = 0.6f };
            _orbit.FrameBounds(new BoundingBox(lo, hi), 1.1f);
            _orbit.Apply();
        }
    }

    /// <summary>The ThreeNet realization (null until <see cref="Model"/> is set).</summary>
    public ThreeNetGridScene? Grid => _grid;

    /// <summary>Show HUD labels (default true).</summary>
    public bool ShowLabels { get; set; } = true;

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        if (_grid is null || _model is null) return;
        _model.UpdateAlarmFlash(e.TotalSeconds);
        _grid.Sync();
        if (ShowLabels) _grid.UpdateLabels(e.Renderer.Width, e.Renderer.Height);
    }
}
