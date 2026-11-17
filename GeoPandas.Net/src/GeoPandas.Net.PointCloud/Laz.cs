using System.Text;
using laszip.net;

namespace GeoPandas.Net.PointCloud;

/// <summary>LAZ (LASzip-compressed LAS) through laszip.net — a managed port of LASzip 2.x (LGPL-2.1), supporting point formats 0–5.</summary>
internal static class Laz
{
    public static PointCloud Read(string path, LasInfo info, LasReadOptions opt)
    {
        if (info.PointFormat > 5)
            throw new NotSupportedException($"LAZ point format {info.PointFormat} (LAS 1.4 layered compression) is not supported by laszip.net; decompress with `pdal translate in.laz out.las` or LAStools `laszip`, then read the .las.");
        var lz = laszip_dll.laszip_create();
        bool compressed = true;
        Check(lz, lz.laszip_open_reader(path, ref compressed));
        try
        {
            var h = lz.header;
            long n = h.number_of_point_records > 0 ? h.number_of_point_records : (long)h.extended_number_of_point_records;
            var classes = opt.Classes is null ? null : new bool[256];
            if (opt.Classes is not null) foreach (var c in opt.Classes) classes![c] = true;
            var b = new Las.Builder(info.PointFormat, (int)Math.Min(n, opt.MaxPoints ?? n));
            var coords = new double[3];
            for (long i = 0; i < n; i++)
            {
                Check(lz, lz.laszip_read_point());
                if (opt.Step > 1 && i % opt.Step != 0) continue;
                var p = lz.point;
                lz.laszip_get_coordinates(coords);
                if (opt.Bbox is { } box && !box.Contains(coords[0], coords[1])) continue;
                byte cls = (byte)(p.classification & 0x1F);
                if (classes is not null && !classes[cls]) continue;
                ushort r = 0, g = 0, bl = 0;
                if (p.rgb is { Length: >= 3 } rgb) { r = rgb[0]; g = rgb[1]; bl = rgb[2]; }
                b.Add(coords[0], coords[1], coords[2], p.intensity, p.return_number, p.number_of_returns_of_given_pulse, cls,
                    p.scan_angle_rank, p.user_data, p.point_source_ID, p.gps_time, r, g, bl, 0);
                if (opt.MaxPoints is { } mx && b.Count >= mx) break;
            }
            return b.Build(info);
        }
        finally { lz.laszip_close_reader(); }
    }

    public static void Write(PointCloud pc, string path, int fmt, double scale)
    {
        var lz = laszip_dll.laszip_create();
        var h = lz.header;
        var b = pc.Bounds;
        h.version_major = 1; h.version_minor = 2;
        h.point_data_format = (byte)fmt;
        h.point_data_record_length = (ushort)(fmt switch { 0 => 20, 1 => 28, 2 => 26, _ => 34 });
        h.x_scale_factor = h.y_scale_factor = h.z_scale_factor = scale;
        h.x_offset = pc.Count == 0 ? 0 : Math.Floor(b.MinX); h.y_offset = pc.Count == 0 ? 0 : Math.Floor(b.MinY); h.z_offset = pc.Count == 0 ? 0 : Math.Floor(b.MinZ);
        h.min_x = b.MinX; h.max_x = b.MaxX; h.min_y = b.MinY; h.max_y = b.MaxY; h.min_z = b.MinZ; h.max_z = b.MaxZ;
        h.number_of_point_records = (uint)pc.Count;
        var sys = Encoding.ASCII.GetBytes("GeoPandas.Net");
        Array.Copy(sys, h.system_identifier, Math.Min(sys.Length, h.system_identifier.Length));
        Array.Copy(sys, h.generating_software, Math.Min(sys.Length, h.generating_software.Length));
        for (int i = 0; i < pc.Count; i++) { int r = pc.ReturnNumber[i]; if (r is >= 1 and <= 5) h.number_of_points_by_return[r - 1]++; }
        Check(lz, lz.laszip_set_header(h));
        if (pc.Crs?.Epsg is { } code)
        {
            var keys = new[]
            {
                new laszip_geokey { key_id = 1024, tiff_tag_location = 0, count = 1, value_offset = (ushort)(pc.Crs.IsGeographic ? 2 : 1) },
                new laszip_geokey { key_id = (ushort)(pc.Crs.IsGeographic ? 2048 : 3072), tiff_tag_location = 0, count = 1, value_offset = (ushort)code }
            };
            Check(lz, lz.laszip_set_geokeys((ushort)keys.Length, keys));
        }
        Check(lz, lz.laszip_open_writer(path, true));
        try
        {
            var p = lz.point;
            var coords = new double[3];
            for (int i = 0; i < pc.Count; i++)
            {
                coords[0] = pc.X[i]; coords[1] = pc.Y[i]; coords[2] = pc.Z[i];
                lz.laszip_set_coordinates(coords);
                p.intensity = pc.Intensity[i];
                p.return_number = pc.ReturnNumber[i];
                p.number_of_returns_of_given_pulse = pc.NumberOfReturns[i];
                p.classification = (byte)(pc.Classification[i] & 0x1F);
                p.scan_angle_rank = (sbyte)Math.Clamp(Math.Round(pc.ScanAngle[i]), -90, 90);
                p.user_data = pc.UserData[i];
                p.point_source_ID = pc.PointSourceId[i];
                if (pc.GpsTime is not null) p.gps_time = pc.GpsTime[i];
                if (pc.HasColor) { p.rgb[0] = pc.Red![i]; p.rgb[1] = pc.Green![i]; p.rgb[2] = pc.Blue![i]; }
                Check(lz, lz.laszip_write_point());
            }
        }
        finally { lz.laszip_close_writer(); }
    }

    private static void Check(laszip_dll lz, int rc)
    {
        if (rc != 0) throw new InvalidDataException("LASzip: " + (lz.laszip_get_error() ?? $"error {rc}"));
    }
}
