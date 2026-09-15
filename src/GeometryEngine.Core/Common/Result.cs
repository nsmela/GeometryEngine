namespace GeometryEngine.Core.Common;

/// <summary>
/// The outcome of an operation that has no return value.
/// Failure is modelled in the type system rather than thrown.
/// </summary>
public class Result
{
    private readonly Error _error;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public Error Error => IsSuccess
        ? throw new InvalidOperationException("Cannot read Error from a success result. Check IsFailure first.")
        : _error;

    protected Result(bool isSuccess, Error error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    public static Result Success() => new(true, Error.None);
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error == Error.None)
        {
            throw new ArgumentException("A failure needs a meaningful error, not Error.None.", nameof(error));
        }

        return new Result(false, error);
    }

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// The outcome of an operation that either yields a value or a domain error.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read Value from a failed result ({Error}). Check IsSuccess first.");

    private Result(T value) : base(true, Error.None) => _value = value;
    private Result(Error error) : base(false, error) => _value = default;

    public static Result<T> Success(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "A success needs a value. Use Maybe<T> when absence is legal.");
        }

        return new Result<T>(value);
    }

    public new static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error == Error.None)
        {
            throw new ArgumentException("A failure needs a meaningful error, not Error.None.", nameof(error));
        }

        return new Result<T>(error);
    }

    /// <summary>Projects the value of a success; propagates the error of a failure.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut>.Success(map(Value)) : Result<TOut>.Failure(Error);

    /// <summary>Chains an operation that may itself fail.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind) =>
        IsSuccess ? bind(Value) : Result<TOut>.Failure(Error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
