"""Export PhysioNet QTDB segments with cardiologist QT annotations (q1c) for Medical.Net validation."""
import json, os, sys, tempfile
import numpy as np
import wfdb

out = os.path.join(tempfile.gettempdir(), "mednet-qtdb")
os.makedirs(out, exist_ok=True)
records = wfdb.get_record_list("qtdb")
done = 0
for rec in records:
    try:
        ann = wfdb.rdann(rec, "q1c", pn_dir="qtdb")
    except Exception:
        continue
    sym, smp = ann.symbol, ann.sample
    beats = []
    for i, s in enumerate(sym):
        if s != "N":
            continue
        # onset '(' just before the N, T end ')' after the following 't'
        on = smp[i - 1] if i > 0 and sym[i - 1] == "(" else None
        tend = None
        for j in range(i + 1, min(i + 8, len(sym))):
            if sym[j] == "t":
                if j + 1 < len(sym) and sym[j + 1] == ")":
                    tend = smp[j + 1]
                break
            if sym[j] == "N":
                break
        if on is not None and tend is not None:
            beats.append((int(on), int(smp[i]), int(tend)))
    if len(beats) < 8:
        continue
    fs = 250
    # densest window of up to 30 s containing annotated beats
    starts = [b[0] for b in beats]
    best = max(range(len(beats)), key=lambda k: sum(1 for b in beats if starts[k] - fs <= b[0] <= starts[k] + 28 * fs))
    lo, hi = starts[best] - fs, starts[best] + 29 * fs
    sel = [b for b in beats if lo + fs // 2 <= b[0] and b[2] <= hi - fs // 2]
    if len(sel) < 5:
        continue
    sig = wfdb.rdrecord(rec, pn_dir="qtdb", sampfrom=max(0, lo), sampto=hi)
    qts = [(b[2] - b[0]) * 1000 / fs for b in sel]
    json.dump({
        "record": rec, "fs": sig.fs, "leads": sig.sig_name,
        "signals": [np.nan_to_num(sig.p_signal[:, c]).round(4).tolist() for c in range(sig.p_signal.shape[1])],
        "annotated_qt_ms": qts, "annotated_beats": len(sel),
    }, open(os.path.join(out, f"{rec}.json"), "w"))
    done += 1
    print(rec, len(sel), round(float(np.mean(qts))), flush=True)
print("exported", done, "records to", out)
