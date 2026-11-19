import json
import subprocess
import sys

def main():
    try:
        raw_in = sys.stdin.read() if not sys.stdin.isatty() else ""
        payload = json.loads(raw_in) if raw_in.strip() else {}
    except Exception:
        payload = {}

    event = payload.get("hookEventName", "Stop")

    try:
        proc = subprocess.run(
            ["slopwatch", "analyze", "-d", ".", "--hook"],
            capture_output=True,
            text=True
        )
        has_error = proc.returncode != 0
        error_msg = proc.stderr.strip() or proc.stdout.strip()
    except Exception as ex:
        has_error = False
        error_msg = str(ex)

    if event == "Stop" or not event:
        if has_error and error_msg:
            resp = {
                "decision": "continue",
                "reason": error_msg
            }
        else:
            resp = {"decision": ""}
    elif event == "PostInvocation":
        if has_error and error_msg:
            resp = {
                "injectSteps": [
                    {
                        "systemMessage": {
                            "systemMessage": error_msg
                        }
                    }
                ],
                "terminationBehavior": "force_continue"
            }
        else:
            resp = {"injectSteps": []}
    else:
        resp = {}

    print(json.dumps(resp))
    sys.exit(0)

if __name__ == "__main__":
    main()
