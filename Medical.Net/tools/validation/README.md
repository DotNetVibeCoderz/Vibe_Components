# Validation scripts

Independent checks used for Medical.Net 1.2. Outputs from the test suite land in `%TEMP%/mednet-v12` (see `V12ImagingTests` / `V12SignalTests`).

```bash
python -m venv .venv && .venv/Scripts/pip install pydicom highdicom wfdb numpy pandas
dotnet test tests/Medical.Tests --filter "FullyQualifiedName~ImagingAiOutputTests|FullyQualifiedName~EcgFileFormatTests"
python tools/validation/verify_seg_sr.py          # highdicom parses the DICOM SEG and the TID 1500 SR
python tools/validation/verify_ecg.py <reader-dir> # pydicom waveform + an independent SCP-ECG reader (*)
python tools/validation/qtdb_export.py            # PhysioNet QT Database (ODC-By) → %TEMP%/mednet-qtdb
dotnet test tests/Medical.Tests --filter QtDatabaseAgreement --logger "console;verbosity=detailed"
MEDNET_QTDB_SPLIT=b dotnet test ...               # held-out half only (a = the half used to choose the window)
```

(*) `<reader-dir>` holds `scp_ecg_file.py` from https://github.com/Ivorforce/SCP-ECG-Python-Interactive-Reader (not redistributed here).
