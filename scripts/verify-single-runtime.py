"""Exercise the Windows connector through a running OS and MCP front.

Use an isolated deployment and an empty dedicated folder. Run `exercise`, restart
the AppHost with the same stores/keys, then run `recover` using the same proof file.
Only the dedicated folder is authorized; exercise writes proof.txt there.
"""
import argparse
import json
from pathlib import Path
import urllib.request


def request(url, data=None):
    body = None if data is None else json.dumps(data).encode()
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    with urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers), timeout=60) as response:
        text = response.read().decode()
    if not text:
        return None
    return json.loads(next((line[6:] for line in text.splitlines() if line.startswith("data: ")), text))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["exercise", "recover"])
    parser.add_argument("--os", required=True, help="OS front URL")
    parser.add_argument("--mcp", required=True, help="MCP front URL")
    parser.add_argument("--folder", required=True, type=Path)
    parser.add_argument("--proof", required=True, type=Path)
    args = parser.parse_args()
    base = args.os.rstrip("/") + "/brains/owner"
    endpoint = args.mcp.rstrip("/") + "/brains/owner/mcp"
    folder = args.folder.resolve()
    device = request(base + "/windows/device")["machineId"]
    key = f"owner/connection/windows/{device}"
    roots = base + f"/windows/{device}/roots"

    def call(contract, target, method, arguments, refused=False):
        result = request(endpoint, {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
            "name": "neurons_call", "arguments": {"contract": contract if contract.startswith("IntoChat.OS.") else "IntoChat.OS.Modules.Microsoft.Windows." + contract,
            "key": target, "method": method, "arguments": json.dumps(arguments)}}})["result"]
        assert bool(result.get("isError")) == refused, result
        if refused:
            return
        content = result.get("content", [])
        return json.loads(content[0]["text"]) if content else None

    if args.mode == "exercise":
        folder.mkdir(parents=True, exist_ok=True)
        assert not (folder / "proof.txt").exists(), "Use a fresh dedicated folder"
        request(base + f"/connections/{device}/connect", {"connectorId": "windows", "authMethodId": "device", "value": device})
        request(roots + "/allow", {"path": str(folder)})
        call("IFileSystem", key + "/files", "Write", [str(folder / "proof.txt"), "single runtime MCP proof"])
        assert call("IFileSystem", key + "/files", "Read", [str(folder / "proof.txt")]) == "single runtime MCP proof"
        assert str(folder / "proof.txt") in call("IFileSystem", key + "/files", "List", [str(folder)])
        command = call("IConsole", key + "/console", "Run", ["cd", str(folder), 10])
        assert command["exitCode"] == 0 and command["standardOutput"].strip().lower() == str(folder).lower(), command
        request(roots + "/revoke", {"path": str(folder)})
        call("IFileSystem", key + "/files", "Read", [str(folder / "proof.txt")], refused=True)
        call("IFileSystem", "owner/connection/windows/00000000000000000000000000000001/files", "List", ["."], refused=True)
        request(roots + "/allow", {"path": str(folder)})
        cursor, events = 0, []
        while True:
            page = request(base + f"/signals?since={cursor}&limit=1000")
            events += [event for event in page["signals"] if event["type"] == "FileWritten" and event["payload"]["Path"] == str(folder / "proof.txt")]
            if page["next"] == cursor:
                break
            cursor = page["next"]
        assert events, "FileWritten was not durably recorded"
        app = "owner/apps/" + folder.name
        app_spec = "Persist this app across a runtime restart."
        created = call("IntoChat.OS.Modules.Apps.IApp", app, "Create", [
            {"title": "Single runtime proof", "description": "Durable app state test", "operations": [], "settings": [], "slots": []}, app_spec])
        assert created["spec"] == app_spec
        args.proof.write_text(json.dumps({"device": device, "folder": str(folder), "event": events[-1], "app": app, "spec": app_spec}), encoding="utf-8")
    else:
        proof = json.loads(args.proof.read_text(encoding="utf-8"))
        assert device == proof["device"] and str(folder) == proof["folder"]
        assert str(folder) in request(roots), "Allowed folder state did not survive restart"
        assert call("IFileSystem", key + "/files", "Read", [str(folder / "proof.txt")]) == "single runtime MCP proof"
        if "app" in proof:
            assert call("IntoChat.OS.Modules.Apps.IApp", proof["app"], "Read", [])["spec"] == proof["spec"]
        event = proof["event"]
        page = request(base + f'/signals?since={event["sequence"] - 1}&limit=1')
        assert page["signals"] == [event], "Signal or durable cursor changed across restart"
    print(args.mode + ": Windows MCP checks passed")


if __name__ == "__main__":
    main()
