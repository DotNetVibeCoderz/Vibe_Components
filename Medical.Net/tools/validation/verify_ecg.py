"""Independent decoding of Medical.Net ECG files: pydicom (DICOM waveform) and an independent SCP-ECG reader."""
import io, os, sys, tempfile
import numpy as np
import pydicom

root = os.path.join(tempfile.gettempdir(), "mednet-v12")
ds = pydicom.dcmread(os.path.join(root, "ecg-12lead.dcm"))
print("DICOM:", ds.SOPClassUID.name, "| modality", ds.Modality)
wf = ds.waveform_array(0)                      # pydicom applies sensitivity / baseline -> physical units (uV)
grp = ds.WaveformSequence[0]
names = [c.ChannelSourceSequence[0].CodeMeaning for c in grp.ChannelDefinitionSequence]
print("  channels:", names)
print("  samples:", wf.shape, "fs", grp.SamplingFrequency, "unit", grp.ChannelDefinitionSequence[0].ChannelSensitivityUnitsSequence[0].CodeValue)
I, II, III = wf[:, 0], wf[:, 1], wf[:, 2]
print("  Einthoven max |III - (II - I)| =", float(np.max(np.abs(III - (II - I)))), "uV")
anns = [(a.ConceptNameCodeSequence[0].CodeMeaning, a.NumericValue) for a in ds.WaveformAnnotationSequence if "NumericValue" in a]
print("  annotations:", anns)

sys.path.insert(0, sys.argv[1])
from scp_ecg_file import SCPFile
dicom_mv = wf / 1000.0
for name in ["ecg-raw.scp", "ecg-huffman.scp"]:
    with open(os.path.join(root, name), "rb") as f:
        scp = SCPFile.read(f)
        tags = scp.section1().read_tags_and_interpret()
        data = scp.section6_dataframe()
    cols = list(data.columns)
    lead_i = np.asarray(data[cols[0]], dtype=float)
    err = float(np.max(np.abs(lead_i[: len(dicom_mv)] - dicom_mv[: len(lead_i), 0])))
    print(f"SCP {name}: leads={cols} samples={len(lead_i)} | patient={getattr(tags, 'patient_id', None)} | max |lead I - DICOM lead I| = {err:.4f} mV")
    assert err < 0.004, err
print("OK: pydicom and the independent SCP-ECG reader decode Medical.Net files")
