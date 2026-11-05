using System;

namespace Tryscape.Core.Geometry
{
    /// <summary>
    /// Exact rational in lowest terms with a positive denominator (contracts/v1 common.rational). Used for route entry
    /// fractions and contact centres so platform float rounding can never choose which obstacle was hit first (S08.2).
    /// All arithmetic is checked; R0 magnitudes (doubled millimetres up to 16000, denominators up to 32000) keep
    /// cross-products far inside 64 bits.
    /// </summary>
    public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
    {
        public static readonly Rational Zero = new Rational(0, 1);
        public static readonly Rational One = new Rational(1, 1);

        public Rational(long numerator, long denominator)
        {
            if (denominator == 0)
                throw new DivideByZeroException("Rational denominator is zero");
            if (numerator == long.MinValue || denominator == long.MinValue)
                throw new OverflowException("Rational component out of range");
            if (denominator < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }
            var g = Gcd(Math.Abs(numerator), denominator);
            Numerator = numerator / g;
            _den = denominator / g;
        }

        public long Numerator { get; }

        /// <summary>Always positive. A default-constructed value is treated as zero (denominator normalised to 1).</summary>
        public long Denominator => _den == 0 ? 1 : _den;

        // default(Rational) has _den == 0 and behaves as 0/1.
        readonly long _den;

        public static Rational FromInteger(long value) => new Rational(value, 1);

        public int CompareTo(Rational other)
        {
            checked
            {
                var left = Numerator * other.Denominator;
                var right = other.Numerator * Denominator;
                return left.CompareTo(right);
            }
        }

        public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;
        public override bool Equals(object obj) => obj is Rational other && Equals(other);
        public override int GetHashCode() => Numerator.GetHashCode() * 397 ^ Denominator.GetHashCode();

        public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
        public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
        public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
        public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;
        public static bool operator ==(Rational a, Rational b) => a.Equals(b);
        public static bool operator !=(Rational a, Rational b) => !a.Equals(b);

        /// <summary>Presentation only; never used to decide results.</summary>
        public double ToDouble() => (double)Numerator / Denominator;

        public override string ToString() => Denominator == 1 ? Numerator.ToString() : $"{Numerator}/{Denominator}";

        static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                var t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }
    }
}
