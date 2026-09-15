namespace GeometryEngine.Core.Common;

/// <summary>
/// An optional value. Absence here is legal and expected; it is not a failure.
/// Failure belongs to <see cref="Result{T}"/>.
/// </summary>
public readonly struct Maybe<T> : IEquatable<Maybe<T>>
{
    private readonly T? _value;

    public bool HasValue { get; }
    public bool HasNoValue => !HasValue;

    private Maybe(T value)
    {
        _value = value;
        HasValue = true;
    }

    public static Maybe<T> Some(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "Use Maybe<T>.None() rather than wrapping null.");
        }

        return new Maybe<T>(value);
    }

    public static Maybe<T> None() => default;

    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("Maybe<T> is empty. Check HasValue first.");

    public T GetValueOrDefault(T fallback) => HasValue ? _value! : fallback;

    public Maybe<TOut> Map<TOut>(Func<T, TOut> map) =>
        HasValue ? Maybe<TOut>.Some(map(_value!)) : Maybe<TOut>.None();

    public Maybe<TOut> Bind<TOut>(Func<T, Maybe<TOut>> bind) =>
        HasValue ? bind(_value!) : Maybe<TOut>.None();

    public Result<T> ToResult(Error error) =>
        HasValue ? Result<T>.Success(_value!) : Result<T>.Failure(error);

    public bool Equals(Maybe<T> other) =>
        HasValue == other.HasValue && (!HasValue || EqualityComparer<T>.Default.Equals(_value!, other._value!));

    public override bool Equals(object? obj) => obj is Maybe<T> other && Equals(other);
    public override int GetHashCode() => HasValue ? EqualityComparer<T>.Default.GetHashCode(_value!) : 0;
    public static bool operator ==(Maybe<T> left, Maybe<T> right) => left.Equals(right);
    public static bool operator !=(Maybe<T> left, Maybe<T> right) => !left.Equals(right);
    public override string ToString() => HasValue ? $"Some({_value})" : "None";
}
