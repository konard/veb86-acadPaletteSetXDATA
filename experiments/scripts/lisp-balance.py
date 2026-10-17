"""Checks that the parentheses of an AutoLISP file are balanced outside strings and comments."""
import sys

for path in sys.argv[1:]:
    depth, line, in_string, escaped = 0, 1, False, False
    text = open(path, encoding="utf-8").read()
    i = 0
    while i < len(text):
        ch = text[i]
        if ch == "\n":
            line += 1
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
        elif ch == ";":
            while i < len(text) and text[i] != "\n":
                i += 1
            continue
        elif ch == '"':
            in_string = True
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth < 0:
                sys.exit(f"{path}:{line}: лишняя закрывающая скобка")
        i += 1
    if depth != 0 or in_string:
        sys.exit(f"{path}: не закрыто скобок {depth}, строка не закрыта: {in_string}")
    print(f"{path}: скобки сбалансированы")
