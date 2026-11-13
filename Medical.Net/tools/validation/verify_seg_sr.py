"""Independent validation of Medical.Net DICOM SEG + SR with pydicom / highdicom."""
import glob, os, sys, tempfile
import numpy as np
import pydicom
import highdicom as hd

root = os.path.join(tempfile.gettempdir(), "mednet-v12")

def check(name, series_dir, expect_type):
    seg_path = os.path.join(root, f"{name}-seg.dcm")
    sr_path = os.path.join(root, f"{name}-sr.dcm")
    ct = [pydicom.dcmread(p) for p in sorted(glob.glob(os.path.join(root, series_dir, "*.dcm")))]
    seg = hd.seg.segread(seg_path)
    print(f"== {name}: SEG {seg.SegmentationType}, {seg.number_of_segments} segment(s), {seg.NumberOfFrames} frame(s)")
    for n in seg.segment_numbers:
        d = seg.get_segment_description(n)
        print(f"   segment {n}: {d.segment_label} | category={d.segmented_property_category.meaning} | type={d.segmented_property_type.meaning} | algo={d.algorithm_type.value}")
        assert d.segmented_property_type.value == expect_type, d.segmented_property_type
    # Reconstruct by source instance; compare to frames referencing the CT.
    src_uids = [c.SOPInstanceUID for c in ct]
    referenced = seg.get_source_image_uids()
    ref_sops = {r[2] for r in referenced}
    assert ref_sops <= set(src_uids), "SEG references unknown instances"
    vol = seg.get_pixels_by_source_instance(source_sop_instance_uids=src_uids, ignore_spatial_locations=True)
    arr = np.asarray(vol)
    print(f"   pixels by source instance: shape={arr.shape}, voxels={int(arr.sum())}")
    assert arr.shape[0] == len(ct) and arr.sum() > 0
    # geometry check: highdicom volume API agrees with the CT frame of reference
    assert seg.FrameOfReferenceUID == ct[0].FrameOfReferenceUID

    sr = hd.sr.srread(sr_path)
    print(f"   SR {type(sr).__name__}: template={sr.ContentTemplateSequence[0].TemplateIdentifier}")
    groups = sr.content.get_volumetric_roi_measurement_groups()
    assert len(groups) == seg.number_of_segments, (len(groups), seg.number_of_segments)
    for g in groups:
        segref = g.referenced_segment
        meas = [(m.name.meaning, m.value, m.unit.value) for m in g.get_measurements()]
        print(f"   group {g.tracking_identifier!r}: finding={g.finding_type.meaning}, sites={[s.value.meaning for s in g.finding_sites]}, segment_ref={type(segref).__name__}, measurements={meas}")
        assert g.finding_type.value == expect_type

check("hemorrhage", "head", "50960005")
check("nodules", "chest", "27925004")
print("OK: highdicom parsed SEG and TID 1500 SR")
