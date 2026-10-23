using CesiumNet.Interop;

namespace CesiumNet;

/// <summary>Camera control.</summary>
public sealed class CameraApi
{
    private readonly CesiumController _c;
    internal CameraApi(CesiumController controller) => _c = controller;

    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Smoothly flies to a destination. Completes when the flight ends (or is cancelled).</summary>
    public ValueTask FlyToAsync(CameraView view, CancellationToken cancellationToken = default)
        => _c.CallVoidAsync("camera.flyTo", JsArgs.Rent().Obj(view, J.CameraView).ToJson(), cancellationToken);

    /// <summary>Flies to a longitude/latitude/height (degrees, meters).</summary>
    public ValueTask FlyToAsync(double longitude, double latitude, double height, double heading = 0, double pitch = -90, double? duration = null, CancellationToken cancellationToken = default)
        => FlyToAsync(new CameraView { Destination = new(longitude, latitude, height), Heading = heading, Pitch = pitch, Duration = duration }, cancellationToken);

    /// <summary>Instantly sets the camera view.</summary>
    public ValueTask SetViewAsync(CameraView view)
        => _c.CallVoidAsync("camera.setView", JsArgs.Rent().Obj(view, J.CameraView).ToJson());

    /// <summary>Flies so that a rectangle fills the view.</summary>
    public ValueTask FlyToRectangleAsync(GeoRectangle rectangle, double? duration = null)
        => _c.CallVoidAsync("camera.flyToRectangle", JsArgs.Rent().Obj(rectangle, J.GeoRectangle).Num(duration).ToJson());

    /// <summary>Flies to the default home view.</summary>
    public ValueTask FlyHomeAsync(double? duration = null)
        => _c.CallVoidAsync("camera.flyHome", JsArgs.Rent().Num(duration).ToJson());

    /// <summary>Flies to an entity, data source, tileset or point collection by id.</summary>
    public ValueTask FlyToObjectAsync(string id, HeadingPitchRange? offset = null, double? duration = null)
        => _c.CallVoidAsync("camera.flyToObject", JsArgs.Rent().Str(id).Obj(offset, J.NullableHeadingPitchRange).Num(duration).ToJson());

    /// <summary>Locks the camera to orbit a target point (until <see cref="ReleaseLookAtAsync"/>).</summary>
    public ValueTask LookAtAsync(GeoPoint target, HeadingPitchRange offset)
        => _c.CallVoidAsync("camera.lookAt", JsArgs.Rent().Obj(target, J.GeoPoint).Obj(offset, J.HeadingPitchRange).ToJson());

    /// <summary>Releases a <see cref="LookAtAsync"/> lock.</summary>
    public ValueTask ReleaseLookAtAsync() => _c.CallVoidAsync("camera.releaseLookAt", JsArgs.Empty);

    /// <summary>Follows an entity with the camera (null stops tracking).</summary>
    public ValueTask TrackEntityAsync(string? entityId)
        => _c.CallVoidAsync("camera.track", JsArgs.Rent().Str(entityId).ToJson());

    /// <summary>Continuously orbits around a target at <paramref name="degreesPerSecond"/>.</summary>
    public ValueTask StartOrbitAsync(GeoPoint target, double range, double pitch = -30, double degreesPerSecond = 6)
        => _c.CallVoidAsync("camera.startOrbit", JsArgs.Rent().Obj(target, J.GeoPoint).Num(range).Num(pitch).Num(degreesPerSecond).ToJson());

    /// <summary>Slowly spins the globe (presentation mode).</summary>
    public ValueTask StartSpinAsync(double degreesPerSecond = 2)
        => _c.CallVoidAsync("camera.startSpin", JsArgs.Rent().Num(degreesPerSecond).ToJson());

    /// <summary>Stops orbit/spin animations.</summary>
    public ValueTask StopAnimationAsync() => _c.CallVoidAsync("camera.stopAnimation", JsArgs.Empty);

    /// <summary>Zooms in (positive) or out (negative) by meters (default: 20% of height).</summary>
    public ValueTask ZoomAsync(double? meters = null, bool zoomIn = true)
        => _c.CallVoidAsync("camera.zoom", JsArgs.Rent().Num(meters).Bool(zoomIn).ToJson());

    /// <summary>Translates (pans) the camera in a direction by meters (default: 10% of height).</summary>
    public ValueTask MoveAsync(CameraDirection direction, double? meters = null)
        => _c.CallVoidAsync("camera.move", JsArgs.Rent().Str(direction.ToString()).Num(meters).ToJson());

    /// <summary>Rotates the camera around the globe center (orbit) by degrees.</summary>
    public ValueTask RotateAsync(CameraDirection direction, double degrees = 5)
        => _c.CallVoidAsync("camera.rotate", JsArgs.Rent().Str(direction.ToString()).Num(degrees).ToJson());

    /// <summary>Turns the camera in place (look/tilt) by degrees: Left/Right = yaw, Up/Down = tilt.</summary>
    public ValueTask LookAsync(CameraDirection direction, double degrees = 5)
        => _c.CallVoidAsync("camera.look", JsArgs.Rent().Str(direction.ToString()).Num(degrees).ToJson());

    /// <summary>Rolls the camera by degrees.</summary>
    public ValueTask TwistAsync(double degrees)
        => _c.CallVoidAsync("camera.twist", JsArgs.Rent().Num(degrees).ToJson());

    /// <summary>Gets the current camera state.</summary>
    public async ValueTask<CameraState> GetStateAsync(CancellationToken cancellationToken = default)
        => await _c.CallAsync("camera.getState", JsArgs.Empty, J.CameraState, cancellationToken).ConfigureAwait(false)
           ?? throw new CesiumInteropException("camera.getState", "No state returned.");

    /// <summary>Configures mouse/touch camera inputs.</summary>
    public ValueTask ConfigureControllerAsync(CameraControllerOptions options)
        => _c.CallVoidAsync("camera.configureController", JsArgs.Rent().Obj(options, J.CameraControllerOptions).ToJson());

    /// <summary>
    /// Enables first-person / free-flight mode: <b>W/A/S/D</b> move, <b>Q/E</b> down/up, mouse drag to look,
    /// <b>Shift</b> to speed up. Set <see cref="FlightModeOptions.WalkHeight"/> to walk on the ground.
    /// </summary>
    public ValueTask EnableFlightModeAsync(FlightModeOptions? options = null)
        => _c.CallVoidAsync("camera.flightMode", JsArgs.Rent().Bool(true).Obj(options ?? new FlightModeOptions(), J.FlightModeOptions).ToJson());

    /// <summary>Disables first-person / free-flight mode.</summary>
    public ValueTask DisableFlightModeAsync()
        => _c.CallVoidAsync("camera.flightMode", JsArgs.Rent().Bool(false).Str(null).ToJson());
}
