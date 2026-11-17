import sys, os

ROOT = r"C:\Users\user\source\repos\Axiom"


def edit(rel, pairs):
    p = os.path.join(ROOT, rel)
    s = open(p, encoding="utf-8", newline="").read()
    bom = s.startswith("\ufeff")
    crlf = "\r\n" in s
    for old, new in pairs:
        o = old.replace("\n", "\r\n") if crlf else old
        n = new.replace("\n", "\r\n") if crlf else new
        c = s.count(o)
        if c != 1:
            # mixed endings: try LF form
            if s.count(old) == 1:
                o, n = old, new
            else:
                print("FAIL", rel, repr(old[:90]), c)
                sys.exit(1)
        s = s.replace(o, n)
    open(p, "w", encoding="utf-8", newline="").write(s)
    print("ok", rel, len(pairs))
