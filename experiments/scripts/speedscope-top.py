# Self and inclusive time per frame of the main thread of a speedscope file of dotnet-trace: speedscope-top.py file [N] [filter]
import json, sys, collections
data = json.load(open(sys.argv[1]))
frames = [f["name"] for f in data["shared"]["frames"]]
top = int(sys.argv[2]) if len(sys.argv) > 2 else 40
needle = sys.argv[3] if len(sys.argv) > 3 else ""
prof = max(data["profiles"], key=lambda p: len(p.get("events", p.get("samples", []))))
self_t = collections.Counter(); incl = collections.Counter()
stack = []; last = None
for e in prof["events"]:
    t = e["at"]
    if last is not None and stack:
        d = t - last
        leaf = len(stack) - 1
        while leaf > 0 and frames[stack[leaf]] in ("CPU_TIME", "UNMANAGED_CODE_TIME"): leaf -= 1
        self_t[stack[leaf]] += d
        for f in set(stack): incl[f] += d
    last = t
    if e["type"] == "O": stack.append(e["frame"])
    else: stack.pop()
total = prof["endValue"] - prof["startValue"]
print("total", round(total), prof.get("unit"))
print("--- self")
for f, v in self_t.most_common(top):
    if needle in frames[f]: print(round(v), frames[f][:160])
print("--- inclusive")
for f, v in incl.most_common(top * 3):
    if needle in frames[f]: print(round(v), frames[f][:160])
