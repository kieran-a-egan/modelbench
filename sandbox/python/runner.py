import ast, base64, io, json, os, sys, time

CAP = 1 << 20
MAX_ERR = 500


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


def run_cases(req, code_obj):
    n = len(req["inputs"])
    fn = None
    try:
        ns = {"__name__": "candidate"}
        exec(code_obj, ns)
        fn = ns.get(req["entryPoint"])
        if not callable(fn):
            raise NameError("entry point '%s' not found" % req["entryPoint"])
    except Exception as e:
        return [{"index": i, "output": None, "error": err(e), "elapsedMs": 0.0} for i in range(n)]
    cases = []
    for i, args in enumerate(req["inputs"]):
        best, out, error = None, None, None
        try:
            for _ in range(max(1, req["repetitions"])):
                t = time.perf_counter()
                out = fn(*args)
                dt = (time.perf_counter() - t) * 1000
                best = dt if best is None else min(best, dt)
            out = norm(out)
        except Unserialisable as e:
            out, error = None, "unserialisable output: %s" % e
        except Exception as e:
            out, error = None, err(e)
        cases.append({"index": i, "output": out, "error": error, "elapsedMs": best or 0.0})
    return cases


def main():
    req = json.loads(sys.stdin.read())
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
                result["cases"] = run_cases(req, code_obj)
    except SyntaxError as e:
        result["compiled"], result["compileErrors"] = False, syntax_error(e)
    except (ValueError, RecursionError, MemoryError) as e:
        result["compiled"], result["compileErrors"] = False, err(e)
    del req
    real.write("MBRESULT:%s:%s\n" % (nonce, base64.b64encode(json.dumps(result, separators=(",", ":")).encode()).decode()))
    real.flush()


main()
