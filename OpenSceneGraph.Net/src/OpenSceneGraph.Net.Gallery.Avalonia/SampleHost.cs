using OpenSceneGraph.Net.Avalonia;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>Builds a sample into a fresh viewer and attaches it to an <see cref="OsgView"/>.</summary>
public static class SampleHost
{
    /// <summary>
    /// Creates the scene with <see cref="GallerySample.Build"/>, assigns viewer and scene to the view and returns the context.
    /// </summary>
    /// <remarks>
    /// Setting <see cref="Viewer.SceneData"/> homes the camera manipulator on the next frame, which would overwrite the
    /// camera pose a sample configured (e.g. <c>TrackballManipulator { Distance = 360, Pitch = 32 }</c>). When the sample
    /// installed its own manipulator, the pending home is consumed by one manipulator-less frame so the authored view is
    /// kept; Space / Home still re-frames the scene on demand. Samples that keep the default manipulator are auto-homed.
    /// </remarks>
    public static SampleContext Attach(OsgView view, GallerySample sample, Viewer viewer, out Exception? buildError)
    {
        var ctx = new SampleContext(viewer);
        var defaultManipulator = viewer.CameraManipulator;
        Node root;
        buildError = null;
        try { root = sample.Build(ctx); }
        catch (Exception ex) { root = new Group(); buildError = ex; }

        view.Viewer = viewer;
        view.Scene = root;
        viewer.SceneData = root;
        if (!ReferenceEquals(viewer.CameraManipulator, defaultManipulator) && viewer.CameraManipulator is { } authored)
        {
            viewer.CameraManipulator = null;
            viewer.Frame(); // consumes the pending home (and runs a zero-length first update)
            viewer.CameraManipulator = authored;
        }
        return ctx;
    }
}
