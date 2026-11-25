using System.Globalization;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// Questions answered by chaining calls to read-only record tools (customers, orders, exchange rates, stock, calculate).
/// Complexity: 1 one lookup; 2 rate + calculate; 3 orders + rate + calculate; 4 customer + orders + rate + calculate;
/// 5 two customers' orders converted into the first customer's reporting currency (6 calls).
/// The tables live only in TaskHidden.Environment; the model sees them solely through tool results.
/// </summary>
public sealed class ToolCallingFamily : IBenchmarkFamily
{
    public string Id => "tool-calling";
    public int Version => 2;
    public string Category => "tool-calling";
    public string Description => "Answer questions by chaining 1-6 calls to record, exchange-rate, stock and calculator tools.";
    public ExecutionMode Mode => ExecutionMode.ToolAssisted;

    private static readonly string[] Currencies = ["USD", "EUR", "GBP", "CHF", "CAD"];
    private static readonly string[] Statuses = ["pending", "shipped", "cancelled"];
    private static readonly string[] First = ["Maren", "Joel", "Elke", "Kasim", "Rina", "Bodil", "Lev", "Dara", "Nico", "Suri", "Teo", "Vera"];
    private static readonly string[] Last = ["Okafor", "Tolvane", "Harley", "Wendahl", "Bransky", "Mortimer", "Perrault", "Salford", "Garnier", "Fenwick"];
    private static readonly string[] Cities = ["Lisbon", "Oslo", "Gdansk", "Porto", "Bergen", "Turin", "Ghent", "Split", "Tartu", "Graz"];

    private const string SystemPrompt = "You answer questions about a company's records using the provided tools. Look facts up with the tools instead of guessing, "
        + "and do every calculation with the calculate tool. When you are done, reply with the final answer inside <answer></answer> tags: "
        + "a plain number (rounded to 2 decimals for money, no currency symbol) or the requested text.";

    private static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        Tool("lookup_customer", "Look up a customer by id. Returns id, name, city and reporting currency.", """{"customer_id":{"type":"string","description":"Customer id, e.g. C-1234"}}""", "customer_id"),
        Tool("list_orders", "List all orders of a customer (id, status, amount, currency).", """{"customer_id":{"type":"string","description":"Customer id"}}""", "customer_id"),
        Tool("get_exchange_rate", "Get the exchange rate: 1 unit of 'from' currency equals 'rate' units of 'to' currency.", """{"from":{"type":"string","description":"ISO currency code"},"to":{"type":"string","description":"ISO currency code"}}""", "from", "to"),
        Tool("get_stock", "Get the units in stock for a SKU.", """{"sku":{"type":"string","description":"SKU, e.g. SKU-1234"}}""", "sku"),
        Tool("calculate", "Evaluate an arithmetic expression with + - * / and parentheses on decimal numbers.", """{"expression":{"type":"string","description":"e.g. (12.50 + 3) * 1.0825"}}""", "expression"),
    ];

    private static ToolDefinition Tool(string name, string description, string properties, params string[] required)
        => new(name, description, JsonDocument.Parse($$"""{"type":"object","properties":{{properties}},"required":[{{string.Join(",", required.Select(r => $"\"{r}\""))}}]}""").RootElement.Clone());

    /// <summary>What the question is about; kept in TaskHidden.Reference for validation. To is the target currency for pending-total.</summary>
    private sealed record Ref(string Kind, string[] Customers, string? Sku, string? From, string? To, decimal Amount);

    private sealed record Step(string Name, object Args);

    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
    private static string RateText(decimal d) => d.ToString("0.0000", CultureInfo.InvariantCulture);

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var ids = new List<string>();
        var names = new List<string>();
        string Unique(List<string> seen, Func<string> make)
        {
            string v;
            do v = make(); while (seen.Contains(v));
            seen.Add(v);
            return v;
        }

        // Order currency per customer; targets (index 0, and 1 at complexity 5) are fixed up so every conversion is really needed.
        var customers = new List<ToolCustomer>();
        var orderCur = new List<string>();
        for (var i = 0; i < 4 + c; i++)
        {
            var oc = rng.Pick(Currencies);
            if (i == 1 && c == 5)
                while (oc == orderCur[0] || oc == customers[0].Currency) oc = rng.Pick(Currencies);
            string rep;
            do rep = rng.Pick(Currencies); while (rep == oc);
            customers.Add(new ToolCustomer(Unique(ids, () => $"C-{rng.Range(1000, 9999)}"),
                Unique(names, () => $"{rng.Pick(First)} {rng.Pick(Last)}"), rng.Pick(Cities), rep));
            orderCur.Add(oc);
        }

        var orders = new List<ToolOrder>();
        for (var i = 0; i < customers.Count; i++)
            for (var k = 0; k < rng.Range(3, 6); k++)
                orders.Add(new ToolOrder($"O-{orders.Count + 1:0000}", customers[i].Id, i < 2 && k < 2 ? "pending" : rng.Pick(Statuses), rng.Range(1000, 90000) / 100m, orderCur[i]));

        var rates = new List<ToolRate>();
        foreach (var a in Currencies)
            foreach (var b in Currencies.Where(x => x != a))
                rates.Add(new ToolRate(a, b, rng.Range(5000, 20000) / 10000m));

        var skus = new List<string>();
        var stock = Enumerable.Range(0, 8).Select(_ => new ToolStock(Unique(skus, () => $"SKU-{rng.Range(1000, 9999)}"), rng.Range(0, 400))).ToList();
        var data = new ToolData(customers, orders, rates, stock);

        decimal Rate(string from, string to) => rates.Single(r => r.From == from && r.To == to).Value;
        string Sum(string customerId) => string.Join(" + ", orders.Where(o => o.CustomerId == customerId && o.Status == "pending").Select(o => Money(o.Amount)));
        var a0 = customers[0];
        var steps = new List<Step>();
        Ref reference;
        string question;
        switch (c)
        {
            case 1 when rng.Chance(0.5):
                var sku = rng.Pick(stock).Sku;
                question = $"How many units of {sku} are currently in stock?";
                steps.Add(new Step("get_stock", new { sku }));
                reference = new Ref("stock", [], sku, null, null, 0);
                break;
            case 1:
                question = $"In which city is customer {a0.Id} based?";
                steps.Add(new Step("lookup_customer", new { customer_id = a0.Id }));
                reference = new Ref("city", [a0.Id], null, null, null, 0);
                break;
            case 2:
                var amount = rng.Range(10000, 500000) / 100m;
                var from = rng.Pick(Currencies);
                var to = rng.Pick(Currencies.Where(x => x != from).ToList());
                question = $"What is {Money(amount)} {from} worth in {to}? Round to 2 decimals.";
                steps.Add(new Step("get_exchange_rate", new { from, to }));
                steps.Add(new Step("calculate", new { expression = $"{Money(amount)} * {RateText(Rate(from, to))}" }));
                reference = new Ref("convert", [], null, from, to, amount);
                break;
            case 3:
                var target = rng.Pick(Currencies.Where(x => x != orderCur[0]).ToList());
                question = $"What is the total value of all pending orders of customer {a0.Id}, converted to {target}? Round to 2 decimals.";
                steps.Add(new Step("list_orders", new { customer_id = a0.Id }));
                steps.Add(new Step("get_exchange_rate", new { from = orderCur[0], to = target }));
                steps.Add(new Step("calculate", new { expression = $"({Sum(a0.Id)}) * {RateText(Rate(orderCur[0], target))}" }));
                reference = new Ref("pending-total", [a0.Id], null, null, target, 0);
                break;
            case 4:
                question = $"What is the total value of all pending orders of customer {a0.Id}, converted to that customer's reporting currency? Round to 2 decimals.";
                steps.Add(new Step("lookup_customer", new { customer_id = a0.Id }));
                steps.Add(new Step("list_orders", new { customer_id = a0.Id }));
                steps.Add(new Step("get_exchange_rate", new { from = orderCur[0], to = a0.Currency }));
                steps.Add(new Step("calculate", new { expression = $"({Sum(a0.Id)}) * {RateText(Rate(orderCur[0], a0.Currency))}" }));
                reference = new Ref("pending-total", [a0.Id], null, null, a0.Currency, 0);
                break;
            default:
                var b0 = customers[1];
                question = $"What is the combined value of all pending orders of customers {a0.Id} and {b0.Id}, expressed in the reporting currency of {a0.Id}? Round to 2 decimals.";
                steps.Add(new Step("lookup_customer", new { customer_id = a0.Id }));
                steps.Add(new Step("list_orders", new { customer_id = a0.Id }));
                steps.Add(new Step("list_orders", new { customer_id = b0.Id }));
                steps.Add(new Step("get_exchange_rate", new { from = orderCur[0], to = a0.Currency }));
                steps.Add(new Step("get_exchange_rate", new { from = orderCur[1], to = a0.Currency }));
                steps.Add(new Step("calculate", new { expression = $"({Sum(a0.Id)}) * {RateText(Rate(orderCur[0], a0.Currency))} + ({Sum(b0.Id)}) * {RateText(Rate(orderCur[1], a0.Currency))}" }));
                reference = new Ref("pending-total", [a0.Id, b0.Id], null, null, a0.Currency, 0);
                break;
        }

        // The expected answer comes from running the reference calls through the real tools; Validate recomputes it with LINQ.
        var env = new TableToolEnvironment(data);
        string last = "";
        var calls = steps.Select((s, i) => new ToolCall($"call_{i + 1}", s.Name, JsonUtil.Serialize(s.Args))).ToList();
        foreach (var call in calls) last = env.Execute(call);
        using var lastDoc = JsonDocument.Parse(last);
        var root = lastDoc.RootElement;
        var isNumber = reference.Kind != "city";
        string expected = reference.Kind switch
        {
            "stock" => root.GetProperty("quantity").GetInt32().ToString(CultureInfo.InvariantCulture),
            "city" => root.GetProperty("city").GetString()!,
            _ => Money(Math.Round(root.GetProperty("result").GetDecimal(), 2, MidpointRounding.AwayFromZero)),
        };

        // calculate is never required: arithmetic without the tool is legitimate and the answer grader checks the result.
        var required = steps.Where(s => s.Name != "calculate").Select(s => new { name = s.Name, args = s.Args }).ToList();
        GraderSpec answer = isNumber
            ? new("numeric", "answer", JsonUtil.Element(new { expected, absTol = "0.01" }))
            : new("exact", "answer", JsonUtil.Element(new { expected, normalize = "trim" }));
        var hidden = new TaskHidden(
            [new GraderSpec("tool-calls", "tool-calls", JsonUtil.Element(new { required, maxCalls = 2 * steps.Count + 2 })), answer],
            JsonUtil.Element(data), JsonUtil.Element(reference));
        var prompt = new TaskPrompt(SystemPrompt, question + "\n\nGive the final answer as <answer>…</answer>.", AnswerFormat.Tagged, Tools);

        List<ChatMessage> reference_turns = [.. calls.Select(call => new ChatMessage(Role.Assistant, null, [call])), new ChatMessage(Role.Assistant, $"<answer>{expected}</answer>")];
        var wrongAnswer = isNumber ? Money(decimal.Parse(expected, CultureInfo.InvariantCulture) * 1.1m + 1) : "Atlantis";
        var wrongFirst = new ToolCall("call_1", calls[0].Name, calls[0].ArgumentsJson.Replace("\":\"", "\":\"x"));
        return new GeneratedTask(prompt, hidden, reference_turns,
        [
            [new ChatMessage(Role.Assistant, $"<answer>{wrongAnswer}</answer>")],
            [new ChatMessage(Role.Assistant, null, [wrongFirst]), new ChatMessage(Role.Assistant, $"<answer>{expected}</answer>")],
        ]);
    }

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        var h = task.Hidden;
        if (h.Environment is not { } envJson || h.Reference is not { } refJson) return ValidationResult.Fail("missing environment or reference");
        var data = JsonUtil.Deserialize<ToolData>(envJson.GetRawText());
        var r = JsonUtil.Deserialize<Ref>(refJson.GetRawText());

        // Independent recomputation straight from the tables.
        decimal RateOf(string from, string to) => from == to ? 1m : data.Rates.Single(x => x.From == from && x.To == to).Value;
        string expected;
        switch (r.Kind)
        {
            case "stock": expected = data.Stock.Single(s => s.Sku == r.Sku).Qty.ToString(CultureInfo.InvariantCulture); break;
            case "city": expected = data.Customers.Single(x => x.Id == r.Customers[0]).City; break;
            case "convert": expected = Money(Math.Round(r.Amount * RateOf(r.From!, r.To!), 2, MidpointRounding.AwayFromZero)); break;
            case "pending-total":
                var total = data.Orders.Where(o => r.Customers.Contains(o.CustomerId) && o.Status == "pending").Sum(o => o.Amount * RateOf(o.Currency, r.To!));
                expected = Money(Math.Round(total, 2, MidpointRounding.AwayFromZero));
                break;
            default: return ValidationResult.Fail($"unknown reference kind '{r.Kind}'");
        }
        var answer = h.Graders.FirstOrDefault(g => g.Name == "answer");
        var graded = answer?.Config.GetProperty("expected").GetString();
        if (graded != expected) problems.Add($"expected answer '{graded}' differs from recomputed '{expected}'");
        if (r.Kind == "pending-total" && r.Customers.Length > 0 && r.Customers.Length != r.Customers.Distinct().Count()) problems.Add("duplicate customers");
        if (r.Kind == "pending-total" && !data.Orders.Any(o => r.Customers.Contains(o.CustomerId) && o.Status == "pending")) problems.Add("no pending orders to total");
        if (task.Prompt.User.Contains(expected, StringComparison.Ordinal) && r.Kind == "city") problems.Add("the answer appears in the prompt");
        if (task.Prompt.Tools.Count != Tools.Count) problems.Add("unexpected tool set");

        // Required calls must resolve to real records.
        var spec = h.Graders.FirstOrDefault(g => g.Type == "tool-calls");
        if (spec is null) return ValidationResult.Fail([.. problems, "no tool-calls grader"]);
        var required = spec.Config.GetProperty("required").EnumerateArray().ToList();
        foreach (var req in required)
        {
            var name = req.GetProperty("name").GetString();
            var args = req.TryGetProperty("args", out var a) ? a : default;
            string? Get(string p) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(p, out var v) ? v.GetString() : null;
            var ok = name switch
            {
                "lookup_customer" or "list_orders" => data.Customers.Any(x => x.Id == Get("customer_id")),
                "get_stock" => data.Stock.Any(x => x.Sku == Get("sku")),
                "get_exchange_rate" => data.Rates.Any(x => x.From == Get("from") && x.To == Get("to")),
                "calculate" => true,
                _ => false,
            };
            if (!ok) problems.Add($"required call {req.GetRawText()} does not resolve to existing records");
        }
                // Reference turns must be exactly the required calls, then the answer.
        var refCalls = task.ReferenceTurns.SelectMany(t => t.ToolCalls ?? []).ToList();
        if (!refCalls.Where(x => x.Name != "calculate").Select(x => x.Name).SequenceEqual(required.Select(x => x.GetProperty("name").GetString()!))) problems.Add("reference calls differ from the required calls");
        if (spec.Config.GetProperty("maxCalls").GetInt32() != 2 * refCalls.Count + 2) problems.Add("maxCalls is not 2*min+2");
        if (task.ReferenceTurns[^1].Content != $"<answer>{expected}</answer>") problems.Add("reference final answer is not the recomputed answer");

        // Replaying the reference calls through the tools must not produce an error.
        var env = new TableToolEnvironment(data);
        foreach (var call in refCalls)
            if (env.Execute(call).StartsWith("{\"error\"", StringComparison.Ordinal)) problems.Add($"reference call {call.Name} returned an error");
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    public ISimulatedEnvironment? CreateEnvironment(TaskHidden hidden)
        => new TableToolEnvironment(JsonUtil.Deserialize<ToolData>(hidden.Environment!.Value.GetRawText()));
}
