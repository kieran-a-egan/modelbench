import ast, base64, ctypes, io, json, os, select, shutil, signal, sys, threading, time

CAP = 1 << 20
MAX_ERR = 500
# Total JSON bytes of all cases in one frame. The host keeps the first 1 MiB (DockerSandbox.OutputCap) of
# "MBRESULT:<nonce>:<base64>\n"; base64 of 1 MiB - ~100 is ~786 KB of JSON, so this leaves ~46 KB for the envelope and the
# "output budget exceeded" errors. Keep in step with DockerSandbox.OutputCap and the C# runner.
OUTPUT_BUDGET = 740_000
# The runner hard-exits this long after the last case could legitimately finish, so a dead host cannot leave the container running.
SELF_DEADLINE_SLACK_MS = 4_000


class Capped(io.StringIO):
    """Keeps at most CAP characters; the rest is discarded."""
    def write(self, s):
        room = CAP - self.tell()
        if room > 0:
            super().write(s[:room])
        return len(s)


class Unserialisable(Exception):
    pass


def err(e):
    return (type(e).__name__ + ": " + str(e))[:MAX_ERR]


def norm(v):
    if v is None or isinstance(v, (bool, int, str)):
        return v
    if isinstance(v, float):
        if v != v or v in (float("inf"), float("-inf")):
            raise Unserialisable("float")
        return v
    if isinstance(v, (list, tuple)):
        return [norm(x) for x in v]
    if isinstance(v, (set, frozenset)):
        return sorted(norm(x) for x in v)
    if isinstance(v, dict):
        if not all(isinstance(k, str) for k in v):
            raise Unserialisable("dict")
        return {k: norm(x) for k, x in v.items()}
    raise Unserialisable(type(v).__name__)


def syntax_error(e):
    return "line %s: %s" % (e.lineno, e.msg)


def inspect_code(code):
    tree = ast.parse(code)
    imports, funcs, classes, calls = set(), [], [], set()
    loop, recursive = False, []
    for n in ast.walk(tree):
        if isinstance(n, ast.Import):
            imports.update(a.name.split(".")[0] for a in n.names)
        elif isinstance(n, ast.ImportFrom) and n.module:
            imports.add(n.module.split(".")[0])
        elif isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)):
            funcs.append(n.name)
            if any(isinstance(c, ast.Call) and isinstance(c.func, ast.Name) and c.func.id == n.name
                   for b in n.body for c in ast.walk(b)):
                recursive.append(n.name)
        elif isinstance(n, ast.ClassDef):
            classes.append(n.name)
        elif isinstance(n, ast.Call):
            if isinstance(n.func, ast.Name):
                calls.add(n.func.id)
            elif isinstance(n.func, ast.Attribute):
                calls.add(n.func.attr)
        if isinstance(n, (ast.For, ast.AsyncFor, ast.While, ast.ListComp, ast.SetComp, ast.DictComp, ast.GeneratorExp)):
            loop = True
    return {"imports": sorted(imports), "functions": funcs, "classes": classes,
            "calledNames": sorted(calls), "hasLoop": loop, "recursiveFunctions": recursive}


def run_case(code_obj, entry, args, reps):
    """Runs in the case's child process: fresh module state, no path to the runner's stdout."""
    try:
        ns = {"__name__": "candidate"}
        exec(code_obj, ns)
        fn = ns.get(entry)
        if not callable(fn):
            raise NameError("entry point '%s' not found" % entry)
        best = None
        for _ in range(max(1, reps)):
            t = time.perf_counter()
            out = fn(*args)
            dt = (time.perf_counter() - t) * 1000
            best = dt if best is None else min(best, dt)
        return {"output": norm(out), "error": None, "elapsedMs": best or 0.0}
    except Unserialisable as e:
        return {"output": None, "error": "unserialisable output: %s" % e, "elapsedMs": 0.0}
    except Exception as e:
        return {"output": None, "error": err(e), "elapsedMs": 0.0}


def reap():
    """The runner is PID 1, so it inherits a case's leftover processes: stop and reap them, and clear every channel a case could leave state in (/tmp, /dev/shm, POSIX queues, SysV IPC), so nothing outlives its case."""
    if os.getpid() != 1:
        return  # only as the container's init; never signal every process of a host user
    try:
        os.kill(-1, signal.SIGKILL)  # every process but init
    except ProcessLookupError:
        pass
    while True:
        try:
            os.waitpid(-1, 0)
        except ChildProcessError:
            break
    try:
        for d in ("/tmp", "/dev/shm", "/dev/mqueue"):  # the writable mounts are the other state a case could leave for the next
            for e in os.scandir(d):
                rm_tree(e.path)
        libc = ctypes.CDLL(None)  # SysV objects outlive their creator; IPC_RMID (0) the ones the case (same uid) made
        for name, rm in (("shm", lambda i: libc.shmctl(i, 0, None)), ("msg", lambda i: libc.msgctl(i, 0, None)), ("sem", lambda i: libc.semctl(i, 0, 0))):
            for line in open("/proc/sysvipc/" + name).read().splitlines()[1:]:
                rm(int(line.split()[1]))
        # fail closed: anything left over would reach the next case
        if any(os.listdir(d) for d in ("/tmp", "/dev/shm", "/dev/mqueue")) or any(len(open("/proc/sysvipc/" + n).read().splitlines()) > 1 for n in ("shm", "msg", "sem")):
            raise OSError("state remains")
    except Exception:  # a candidate must not be able to turn its failures into infra errors
        global stuck
        stuck = True


def rm_tree(path):
    """Same uid as the case, so a mode-000 directory is chmodded back open before it is emptied."""
    if os.path.isdir(path) and not os.path.islink(path):
        os.chmod(path, 0o700)
        for e in os.scandir(path):
            rm_tree(e.path)
        os.rmdir(path)
    else:
        os.unlink(path)


stuck = False  # set when reap() could not clear the case's state


def fork_case(code_obj, entry, args, reps, timeout_ms):
    """MB-004: candidate code runs only in a child. The runner checks its exit status and is the only writer of frames."""
    r, w = os.pipe()
    pid = os.fork()
    if pid == 0:
        try:
            null = os.open(os.devnull, os.O_RDWR)
            for fd in (0, 1, 2):
                os.dup2(null, fd)
            os.dup2(w, 3)
            os.closerange(4, 1 << 16)  # drops the runner's stdout dup and the read end
            sys.stdout, sys.stderr = Capped(), Capped()
            data = json.dumps(run_case(code_obj, entry, args, reps), separators=(",", ":")).encode()
            while data:
                data = data[os.write(3, data):]
            os._exit(0)
        finally:
            os._exit(1)
    os.close(w)
    chunks, size, status = [], 0, None
    limit_ms = timeout_ms * max(1, reps)
    deadline, timed_out = time.monotonic() + limit_ms / 1000, False
    while True:
        if select.select([r], [], [], 0.05)[0]:
            b = os.read(r, 65536)
            if not b:
                break
            if size < CAP:
                chunks.append(b)
                size += len(b)
        elif status is None:
            done, st = os.waitpid(pid, os.WNOHANG)
            if done:
                status = st
                reap()  # the case's own children may still hold the pipe open; stopping them ends it
        if status is None and not timed_out and time.monotonic() > deadline:
            timed_out = True  # per-case deadline: only this case fails
            os.kill(pid, signal.SIGKILL)
            status = os.waitpid(pid, 0)[1]  # before reap(), which would reap the child too
            reap()
    os.close(r)
    if status is None:
        status = os.waitpid(pid, 0)[1]
    reap()
    if timed_out:
        return {"output": None, "error": "case timed out after %d ms" % limit_ms, "elapsedMs": 0.0}
    if os.WIFSIGNALED(status):
        return {"output": None, "error": "case process killed by signal %d" % os.WTERMSIG(status), "elapsedMs": 0.0}
    if os.WEXITSTATUS(status) != 0:
        return {"output": None, "error": "case process exited with status %d" % os.WEXITSTATUS(status), "elapsedMs": 0.0}
    try:
        c = json.loads(b"".join(chunks)[:CAP])
        output, error, elapsed = c["output"], c["error"], c["elapsedMs"]
        if not (error is None or isinstance(error, str)) or isinstance(elapsed, bool) or not isinstance(elapsed, (int, float)):
            raise ValueError
        return {"output": output, "error": error if error is None else error[:MAX_ERR], "elapsedMs": float(elapsed)}
    except Exception:
        return {"output": None, "error": "case process produced no result", "elapsedMs": 0.0}


def main():
    # Not dumpable: case processes run as the same user but cannot open /proc/1/fd/1 or /proc/1/mem to reach the frame stream.
    if ctypes.CDLL(None).prctl(4, 0, 0, 0, 0) != 0:  # PR_SET_DUMPABLE
        raise OSError("prctl(PR_SET_DUMPABLE) failed")
    for sig in (signal.SIGINT, signal.SIGTERM):  # as PID 1 a case must not be able to stop the runner
        signal.signal(sig, signal.SIG_IGN)
    req = json.loads(sys.stdin.read())
    calls = max(1, req["repetitions"]) * max(1, len(req["inputs"])) if req["action"] == "run" else 1
    watchdog = threading.Timer((req["timeoutMs"] * calls + SELF_DEADLINE_SLACK_MS) / 1000, os._exit, [1])
    watchdog.daemon = True
    watchdog.start()
    real = os.fdopen(os.dup(1), "w")
    nonce = req["nonce"]
    result = {"compiled": True, "compileErrors": None, "cases": [], "astFacts": None}
    sys.stdout, sys.stderr = Capped(), Capped()
    action = req["action"]
    try:
        if action == "inspect":
            result["astFacts"] = inspect_code(req["code"])
        else:
            code_obj = compile(req["code"], "<candidate>", "exec")
            if action == "run":
                used = 0
                for i, args in enumerate(req["inputs"]):
                    if stuck:
                        c = {"output": None, "error": "sandbox state could not be cleared after case %d" % (i - 1), "elapsedMs": 0.0}
                    else:
                        c = fork_case(code_obj, req["entryPoint"], args, req["repetitions"], req["timeoutMs"])
                        size = len(json.dumps(c, separators=(",", ":")))
                        if used + size > OUTPUT_BUDGET:
                            c = {"output": None, "error": "output budget exceeded", "elapsedMs": 0.0}
                        else:
                            used += size
                    result["cases"].append(dict(index=i, **c))
    except SyntaxError as e:
        result["compiled"], result["compileErrors"] = False, syntax_error(e)
    except (ValueError, RecursionError, MemoryError) as e:
        result["compiled"], result["compileErrors"] = False, err(e)
    del req
    real.write("MBRESULT:%s:%s\n" % (nonce, base64.b64encode(json.dumps(result, separators=(",", ":")).encode()).decode()))
    real.flush()


main()
