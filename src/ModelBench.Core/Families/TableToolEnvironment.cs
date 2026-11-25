using System.Globalization;
using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Families;

public sealed record ToolCustomer(string Id, string Name, string City, string Currency);
public sealed record ToolOrder(string Id, string CustomerId, string Status, decimal Amount, string Currency);
public sealed record ToolRate(string From, string To, decimal Value);
public sealed record ToolStock(string Sku, int Qty);

/// <summary>Seeded tables behind the tool-calling family (TaskHidden.Environment).</summary>
public sealed record ToolData(IReadOnlyList<ToolCustomer> Customers, IReadOnlyList<ToolOrder> Orders, IReadOnlyList<ToolRate> Rates, IReadOnlyList<ToolStock> Stock);

/// <summary>
/// Read-only simulated tools over <see cref="ToolData"/>. Bad ids or arguments return {"error": "..."} instead of throwing.
/// State is a log of the calls made.
/// </summary>
public sealed class TableToolEnvironment(ToolData data) : ISimulatedEnvironment
{
    private readonly List<(string Tool, bool Ok)> _log = [];

    public JsonElement State => JsonUtil.Element(new { calls = _log.Count, log = _log.Select(l => new { tool = l.Tool, ok = l.Ok }) });

    public string Execute(ToolCall call)
    {
        object result;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            result = Run(call.Name, doc.RootElement);
        }
        catch (JsonException) { result = Error("arguments are not valid JSON"); }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or OverflowException or ArgumentException)
        {
            result = Error(ex.Message);
        }
        _log.Add((call.Name, result is not ErrorResult));
        return JsonUtil.Serialize(result);
    }

    private sealed record ErrorResult(string Error);
    private static ErrorResult Error(string message) => new(message);

    private object Run(string tool, JsonElement args)
    {
        switch (tool)
        {
            case "lookup_customer":
                return Arg(args, "customer_id", out var id, out var e) ? data.Customers.FirstOrDefault(c => c.Id == id) is { } c ? c : Error($"unknown customer '{id}'") : e;
            case "list_orders":
                return Arg(args, "customer_id", out id, out e)
                    ? data.Customers.Any(c => c.Id == id) ? new { orders = data.Orders.Where(o => o.CustomerId == id).Select(o => new { o.Id, o.Status, o.Amount, o.Currency }) } : Error($"unknown customer '{id}'")
                    : e;
            case "get_exchange_rate":
                if (!Arg(args, "from", out var from, out e) || !Arg(args, "to", out var to, out e)) return e;
                if (from == to) return new { from, to, rate = 1m };
                return data.Rates.FirstOrDefault(r => r.From == from && r.To == to) is { } rate ? new { from, to, rate = rate.Value } : Error($"no rate from '{from}' to '{to}'");
            case "get_stock":
                return Arg(args, "sku", out id, out e) ? data.Stock.FirstOrDefault(s => s.Sku == id) is { } s ? new { sku = s.Sku, quantity = s.Qty } : Error($"unknown sku '{id}'") : e;
            case "calculate":
                if (!Arg(args, "expression", out var expr, out e)) return e;
                return new CalcResult(Arithmetic.Evaluate(expr));
            default:
                return Error($"unknown tool '{tool}'");
        }
    }

    private sealed record CalcResult(decimal Result);

    private static bool Arg(JsonElement args, string name, out string value, out ErrorResult error)
    {
        value = "";
        error = Error($"missing or non-string argument '{name}'");
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString()!;
        return true;
    }

    /// <summary>Decimal arithmetic: + - * /, unary minus and parentheses. No eval, no functions, no variables.</summary>
    internal sealed class Arithmetic
    {
        private const int MaxLength = 1000, MaxDepth = 50;
        private readonly string _s;
        private int _pos, _depth;

        private Arithmetic(string s) => _s = s;

        public static decimal Evaluate(string expression)
        {
            if (expression.Length > MaxLength) throw new ArgumentException($"expression longer than {MaxLength} characters");
            var p = new Arithmetic(expression);
            var v = p.Sum();
            p.Skip();
            return p._pos == expression.Length ? v : throw new FormatException($"unexpected '{expression[p._pos]}' at position {p._pos}");
        }

        private void Skip() { while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++; }

        private bool Take(char c)
        {
            Skip();
            if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; }
            return false;
        }

        private decimal Sum()
        {
            var v = Product();
            while (true)
            {
                if (Take('+')) v += Product();
                else if (Take('-')) v -= Product();
                else return v;
            }
        }

        private decimal Product()
        {
            var v = Unary();
            while (true)
            {
                if (Take('*')) v *= Unary();
                else if (Take('/')) v /= Unary();
                else return v;
            }
        }

        private decimal Unary()
        {
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            if (Take('('))
            {
                if (++_depth > MaxDepth) throw new ArgumentException("parentheses nested too deeply");
                var v = Sum();
                if (!Take(')')) throw new FormatException("missing ')'");
                _depth--;
                return v;
            }
            Skip();
            var start = _pos;
            while (_pos < _s.Length && (char.IsAsciiDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
            return decimal.TryParse(_s.AsSpan(start, _pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
                ? n : throw new FormatException(_pos < _s.Length ? $"unexpected '{_s[_pos]}' at position {_pos}" : "unexpected end of expression");
        }
    }
}
