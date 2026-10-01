# Scores artifacts/voice-quality/probe.json: per-phrase word match + total recall.
# Usage: python scripts/score_probe.py [probe.json path]
import difflib
import json
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
path = sys.argv[1] if len(sys.argv) > 1 else "artifacts/voice-quality/probe.json"
rows = json.load(open(path, encoding="utf-8"))
tot_e = tot_m = 0
for r in rows:
    e = r["expected"].lower().split()
    h = r["heard"].lower().split()
    matched = sum(b.size for b in difflib.SequenceMatcher(None, e, h).get_matching_blocks())
    tot_e += len(e)
    tot_m += matched
    print(f"[{r['id']}] match={matched}/{len(e)} heard={h and r['heard']}")
print(f"TOTAL word recall: {tot_m}/{tot_e} = {tot_m / tot_e:.1%}")
