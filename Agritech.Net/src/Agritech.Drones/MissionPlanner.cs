using System.Text.Json.Nodes;
using Agritech.Core.Geo;
using Agritech.GIS;

namespace Agritech.Drones;

/// <summary>Camera intrinsics used for photogrammetric planning.</summary>
public sealed record CameraSpec(string Name, double SensorWidthMm, double SensorHeightMm, double FocalLengthMm, int ImageWidthPx, int ImageHeightPx)
{
    public static readonly CameraSpec DjiMavic3M = new("DJI Mavic 3M (RGB)", 17.3, 13.0, 12.29, 5280, 3956);
    public static readonly CameraSpec DjiPhantom4Rtk = new("DJI Phantom 4 RTK", 13.2, 8.8, 8.8, 5472, 3648);
    public static readonly CameraSpec MicaSenseRedEdgeP = new("MicaSense RedEdge-P", 7.12, 5.33, 5.5, 1456, 1088);

    /// <summary>Ground sampling distance (cm/pixel) at altitude (m AGL).</summary>
    public double Gsd(double altitudeM) => SensorWidthMm * altitudeM * 100 / (FocalLengthMm * ImageWidthPx);

    /// <summary>Altitude needed for a target GSD (cm/px).</summary>
    public double AltitudeForGsd(double gsdCm) => gsdCm * FocalLengthMm * ImageWidthPx / (SensorWidthMm * 100);

    public (double WidthM, double HeightM) Footprint(double altitudeM) =>
        (SensorWidthMm * altitudeM / FocalLengthMm, SensorHeightMm * altitudeM / FocalLengthMm);
}

/// <summary>Platform performance used for time/battery estimation.</summary>
public sealed record DroneSpec(string Name, double CruiseSpeedMs = 10, double EnduranceMin = 30, double BatteryReservePct = 25, double TurnTimeS = 4)
{
    public static readonly DroneSpec Multirotor = new("Generic multirotor", 10, 30);
    public static readonly DroneSpec SprayerAgras = new("Spray drone (40 L class)", 7, 12, 30, 5);
}

public enum WaypointAction { None, StartCapture, StopCapture, Spray, Hover }

public sealed record Waypoint(int Index, GeoCoordinate Position, double AltitudeM, WaypointAction Action = WaypointAction.None);

public sealed record MissionPlan(
    IReadOnlyList<Waypoint> Waypoints, double LineSpacingM, double PhotoIntervalM, double GsdCm, double TotalDistanceM,
    double FlightTimeMin, int EstimatedPhotos, int BatteriesRequired, double CoveredAreaHa, double HeadingDeg)
{
    /// <summary>Exports as a QGroundControl <c>.plan</c> (MAVLink mission) JSON document.</summary>
    public string ToQGroundControlPlan(GeoCoordinate home)
    {
        var items = new JsonArray();
        int seq = 1;
        items.Add((JsonNode)Item(seq++, 22, 0, 0, 0, Waypoints[0].Position, Waypoints[0].AltitudeM)); // MAV_CMD_NAV_TAKEOFF
        foreach (var w in Waypoints)
        {
            items.Add((JsonNode)Item(seq++, 16, 0, 0, 0, w.Position, w.AltitudeM)); // MAV_CMD_NAV_WAYPOINT
            if (w.Action == WaypointAction.StartCapture) items.Add((JsonNode)Cmd(seq++, 206, PhotoIntervalM)); // DO_SET_CAM_TRIGG_DIST
            if (w.Action == WaypointAction.StopCapture) items.Add((JsonNode)Cmd(seq++, 206, 0));
        }
        items.Add((JsonNode)Cmd(seq, 20, 0)); // RETURN_TO_LAUNCH
        var plan = new JsonObject
        {
            ["fileType"] = "Plan",
            ["groundStation"] = "Agritech.Net",
            ["version"] = 1,
            ["mission"] = new JsonObject
            {
                ["version"] = 2, ["firmwareType"] = 12, ["vehicleType"] = 2, ["cruiseSpeed"] = 10, ["hoverSpeed"] = 5,
                ["plannedHomePosition"] = new JsonArray(home.Latitude, home.Longitude, home.Altitude),
                ["items"] = items,
            },
            ["geoFence"] = new JsonObject { ["circles"] = new JsonArray(), ["polygons"] = new JsonArray(), ["version"] = 2 },
            ["rallyPoints"] = new JsonObject { ["points"] = new JsonArray(), ["version"] = 2 },
        };
        return plan.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Item(int seq, int cmd, double p1, double p2, double p3, GeoCoordinate c, double alt) => new()
    {
        ["type"] = "SimpleItem", ["autoContinue"] = true, ["command"] = cmd, ["doJumpId"] = seq, ["frame"] = 3,
        ["params"] = new JsonArray(p1, p2, p3, null, c.Latitude, c.Longitude, alt),
    };

    private static JsonObject Cmd(int seq, int cmd, double p1) => new()
    {
        ["type"] = "SimpleItem", ["autoContinue"] = true, ["command"] = cmd, ["doJumpId"] = seq, ["frame"] = 2,
        ["params"] = new JsonArray(p1, 0, 0, 0, 0, 0, 0),
    };
}

/// <summary>Coverage-path (boustrophedon / lawnmower) mission planner for mapping and spraying.</summary>
public static class MissionPlanner
{
    /// <summary>Photogrammetry mapping mission.</summary>
    /// <param name="frontOverlap">Forward overlap (0–0.95), typically 0.75–0.8 for crops.</param>
    /// <param name="sideOverlap">Side overlap, typically 0.65–0.75.</param>
    /// <param name="headingDeg">Track direction; null = aligned with the field's longest edge.</param>
    public static MissionPlan PlanSurvey(GeoPolygon field, CameraSpec camera, double altitudeM, double frontOverlap = 0.75, double sideOverlap = 0.7,
        DroneSpec? drone = null, double? headingDeg = null)
    {
        var (fw, fh) = camera.Footprint(altitudeM);
        double spacing = fw * (1 - sideOverlap);
        double photoInterval = fh * (1 - frontOverlap);
        var plan = Coverage(field, spacing, altitudeM, drone ?? DroneSpec.Multirotor, headingDeg, photoInterval, true);
        return plan with { GsdCm = Math.Round(camera.Gsd(altitudeM), 2) };
    }

    /// <summary>Spraying mission with swath width (m).</summary>
    public static MissionPlan PlanSpray(GeoPolygon field, double swathWidthM, double altitudeM = 3, DroneSpec? drone = null, double? headingDeg = null) =>
        Coverage(field, swathWidthM, altitudeM, drone ?? DroneSpec.SprayerAgras, headingDeg, 0, false);

    private static MissionPlan Coverage(GeoPolygon field, double spacing, double alt, DroneSpec drone, double? headingDeg, double photoInterval, bool capture)
    {
        if (spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        var local = field.ToLocal(out var origin);
        double angle = headingDeg is { } h ? (90 - h) * Math.PI / 180 : Geometry.LongestEdgeAngle(local);
        // Rotate so tracks are horizontal.
        var rot = local.Select(p => Geometry.Rotate(p, -angle)).ToArray();
        double minY = rot.Min(p => p.Y), maxY = rot.Max(p => p.Y);
        var wps = new List<Waypoint>();
        bool flip = false;
        int idx = 0;
        for (double y = minY + spacing / 2; y < maxY; y += spacing)
        {
            var intervals = Geometry.ScanlineIntervals(rot, y);
            if (intervals.Count == 0) continue;
            // For concave fields, each interval becomes its own pass.
            var ordered = flip ? intervals.AsEnumerable().Reverse() : intervals;
            foreach (var (x0, x1) in ordered)
            {
                var a = new Point2(flip ? x1 : x0, y);
                var b = new Point2(flip ? x0 : x1, y);
                wps.Add(new Waypoint(idx++, GeoMath.FromLocal(origin, Geometry.Rotate(a, angle)), alt, capture ? WaypointAction.StartCapture : WaypointAction.Spray));
                wps.Add(new Waypoint(idx++, GeoMath.FromLocal(origin, Geometry.Rotate(b, angle)), alt, capture ? WaypointAction.StopCapture : WaypointAction.None));
            }
            flip = !flip;
        }
        double dist = 0, captureDist = 0;
        for (int i = 1; i < wps.Count; i++)
        {
            double d = GeoMath.Distance(wps[i - 1].Position, wps[i].Position);
            dist += d;
            if (wps[i - 1].Action is WaypointAction.StartCapture or WaypointAction.Spray) captureDist += d;
        }
        int lines = wps.Count / 2;
        double timeMin = (dist / drone.CruiseSpeedMs + lines * drone.TurnTimeS) / 60;
        double usable = drone.EnduranceMin * (1 - drone.BatteryReservePct / 100);
        int photos = capture && photoInterval > 0 ? (int)Math.Ceiling(captureDist / photoInterval) + lines : 0;
        double heading = (90 - angle * 180 / Math.PI + 360) % 360;
        return new MissionPlan(wps, Math.Round(spacing, 2), Math.Round(photoInterval, 2), 0, Math.Round(dist, 1), Math.Round(timeMin, 1), photos,
            Math.Max(1, (int)Math.Ceiling(timeMin / usable)), Math.Round(field.AreaHectares, 3), Math.Round(heading, 1));
    }
}
