using System.Numerics;
using System.Text.RegularExpressions;

namespace ModelBench.Core.Families;

/// <summary>Exact rational, always in lowest terms with a positive denominator. Create with <see cref="Of"/>.</summary>
public readonly partial record struct Rational
{
    public BigInteger Num { get; }
    public BigInteger Den { get; }

    private Rational(BigInteger num, BigInteger den) { Num = num; Den = den; }

    public static Rational Of(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException();
        if (den.Sign < 0) { num = -num; den = -den; }
        var g = BigInteger.GreatestCommonDivisor(num, den);
        return new Rational(num / g, den / g);
    }

    public static Rational Of(BigInteger n) => new(n, BigInteger.One);

    public static Rational operator +(Rational a, Rational b) => Of(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static Rational operator -(Rational a, Rational b) => Of(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static Rational operator *(Rational a, Rational b) => Of(a.Num * b.Num, a.Den * b.Den);
    public static Rational operator /(Rational a, Rational b) => Of(a.Num * b.Den, a.Den * b.Num);

    public bool IsZero => Num.IsZero;
    public Rational Abs() => new(BigInteger.Abs(Num), Den);
    public int CompareTo(Rational o) => (Num * o.Den).CompareTo(o.Num * Den);

    /// <summary>Canonical text: "p/q" in lowest terms, or just "p" when the denominator is 1.</summary>
    public override string ToString() => Den.IsOne ? Num.ToString() : $"{Num}/{Den}";

    [GeneratedRegex(@"^([+-]?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$")]
    private static partial Regex DecimalPattern();

    /// <summary>Accepts "p/q", integers and decimals ("-12.5", "1e-5"). Anything else fails.</summary>
    public static bool TryParse(string? text, out Rational value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            if (!TryParseInt(text[..slash], out var p) || !TryParseInt(text[(slash + 1)..], out var q) || q.IsZero) return false;
            value = Of(p, q);
            return true;
        }
        var m = DecimalPattern().Match(text);
        if (!m.Success) return false;
        var frac = m.Groups[3].Value;
        var num = BigInteger.Parse(m.Groups[2].Value + frac);
        if (m.Groups[1].Value == "-") num = -num;
        var scale = -frac.Length + (m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
        if (Math.Abs(scale) > 1000) return false;
        value = scale >= 0 ? Of(num * BigInteger.Pow(10, scale)) : Of(num, BigInteger.Pow(10, -scale));
        return true;
    }

    private static bool TryParseInt(string s, out BigInteger v)
    {
        v = default;
        s = s.Trim();
        return s.Length > 0 && s.All(c => char.IsAsciiDigit(c) || c is '+' or '-') && BigInteger.TryParse(s, out v);
    }
}
