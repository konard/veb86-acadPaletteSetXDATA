# Generates examples/tzxdata-stage10-load-schema.json: 50 template parameters for the load test (TZXDATA 10.6).
import json, sys
groups = ["Общие", "Электрика", "Кабель", "Монтаж", "Учёт"]
types = ["String", "Double", "Integer", "Boolean", "Enum"]
params = []
ids = []
for i in range(1, 49):
    t = types[(i - 1) % 5]
    pid = "L%02d" % i
    p = {"id": pid, "type": t, "caption": "Параметр %d" % i, "group": groups[(i - 1) // 10]}
    if t == "Double": p["editor"] = {"unit": "м", "precision": 2, "min": 0}
    if t == "Integer": p["editor"] = {"min": 0}
    if t == "Enum": p["enum"] = "LoadPhase"; p["default"] = "A"
    if t == "Boolean": p["default"] = False
    params.append(p); ids.append(pid)
params.append({"id": "L49", "type": "CalculatedString", "caption": "Обозначение", "group": "Учёт", "format": "@@[L01].@@[L03]"})
ids.append("L49")
params.append({"id": "LMat_Mark", "type": "String", "caption": "Марка", "group": "Материалы", "editor": {"vstColumn": 1}})
params.append({"id": "LMat_Count", "type": "Double", "caption": "Кол.", "group": "Материалы", "default": 1, "editor": {"min": 0, "vstColumn": 2}})
params.append({"id": "L50", "type": "Table", "caption": "Материалы", "group": "Материалы", "columns": ["LMat_Mark", "LMat_Count"], "key": "LMat_Mark"})
ids.append("L50")
schema = {
    "format": "BobrovXDATA.schema", "version": 1, "parameters": params,
    "enums": [{"id": "LoadPhase", "caption": "Фаза", "items": [{"key": k, "caption": c} for k, c in [("A", "A (L1)"), ("B", "B (L2)"), ("C", "C (L3)")]]}],
    "templates": [{"id": "LOAD", "caption": "Нагрузочный объект", "entityTypes": ["Line"], "parameters": ids}],
}
lines = ["{", '  "format": "BobrovXDATA.schema",', '  "version": 1,', '  "parameters": [']
lines += [",\n".join("    " + json.dumps(p, ensure_ascii=False) for p in params)]
lines += ["  ],", '  "enums": [', "    " + json.dumps(schema["enums"][0], ensure_ascii=False), "  ],", '  "templates": [',
          "    " + json.dumps(schema["templates"][0], ensure_ascii=False), "  ]", "}"]
open(sys.argv[1], "w", encoding="utf-8").write("\n".join(lines) + "\n")
