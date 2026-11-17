"""Client for the axsim file protocol.

  python simcli.py next  <protoDir> <role>
  python simcli.py reply <protoDir> <role> <replyFile>

'next' prints the oldest unanswered request for the role (waits up to 100 s).
Computer Use planner requests arrive on the 'actor' role; answer them as 'actor' too.
Roles: cu -> actor, observer; agent -> turn1, turn2, ... (agent --tier=micro|compact uses the text protocol).
'reply' answers it with the contents of replyFile, then prints the next request.
"""
import os, sys, time, glob, shutil

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def pending(d):
    for req in sorted(glob.glob(os.path.join(d, "req-*.md"))):
        rep = req.replace("req-", "rep-")[:-3] + ".txt"
        if not os.path.exists(rep):
            return req, rep
    return None, None


def show_next(d, wait=100):
    end = time.time() + wait
    while time.time() < end:
        req, _ = pending(d)
        if req:
            time.sleep(0.3)
            print(open(req, encoding="utf-8").read())
            return
        done = os.path.join(d, "done.md")
        if os.path.exists(done):
            print(open(done, encoding="utf-8").read())
            return
        time.sleep(0.5)
    print("NO REQUEST YET. Run the same 'next' command again.")


def main():
    cmd, proto, role = sys.argv[1], sys.argv[2], sys.argv[3]
    d = os.path.join(proto, role)
    os.makedirs(d, exist_ok=True)
    if cmd == "reply":
        req, rep = pending(d)
        if not req:
            print("There is no open request to answer.")
        else:
            tmp = rep + ".tmp"
            shutil.copyfile(sys.argv[4], tmp)
            os.replace(tmp, rep)
    show_next(d)


main()
