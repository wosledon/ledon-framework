using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ledon.Ids;

/// <summary>
/// ULID（Universally Unique Lexicographically Sortable Identifier）：
/// 48 位毫秒时间戳 + 80 位随机数，26 位 Crockford Base32 字符串。
/// 同一毫秒内按生成顺序单调递增。
/// </summary>
public readonly struct Ulid : IEquatable<Ulid>, IComparable<Ulid>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly Lock Gate = new();
    private static long _lastTimestamp;
    private static UInt128 _lastRandom;

    private readonly UInt128 _value;

    private Ulid(UInt128 value) => _value = value;

    /// <summary>生成新的 ULID（线程安全，同毫秒单调）。</summary>
    public static Ulid New()
    {
        lock (Gate)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            UInt128 random;

            if (timestamp <= _lastTimestamp)
            {
                timestamp = _lastTimestamp;
                random = _lastRandom + 1;

                // 80 位随机数溢出：借用下一毫秒，保证严格单调
                if (random >> 80 != 0)
                {
                    timestamp++;
                    random = Random80();
                }
            }
            else
            {
                random = Random80();
            }

            _lastTimestamp = timestamp;
            _lastRandom = random;

            return new Ulid(((UInt128)(ulong)timestamp << 80) | random);
        }
    }

    /// <summary>时间部分（UTC）。</summary>
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds((long)(_value >> 80));

    /// <summary>26 位 Crockford Base32 字符串。</summary>
    public override string ToString()
    {
        Span<char> chars = stackalloc char[26];
        var value = _value;

        for (var index = 25; index >= 0; index--)
        {
            chars[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    /// <summary>解析 ULID 字符串（大小写不敏感，支持 I/L/O 别名）。</summary>
    public static Ulid Parse(string value) =>
        TryParse(value, out var ulid)
            ? ulid
            : throw new FormatException($"不是合法的 ULID：{value}");

    /// <summary>尝试解析 ULID 字符串。</summary>
    public static bool TryParse(string? value, out Ulid ulid)
    {
        ulid = default;

        if (value is null || value.Length != 26)
            return false;

        // 128 位最多编码成 26 个 5 位字符（首位只能是 0-7，避免溢出）
        var first = Decode(value[0]);

        if (first is < 0 or > 7)
            return false;

        UInt128 parsed = (uint)first;

        for (var index = 1; index < value.Length; index++)
        {
            var digit = Decode(value[index]);

            if (digit < 0)
                return false;

            parsed = (parsed << 5) | (uint)digit;
        }

        ulid = new Ulid(parsed);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(Ulid other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Ulid other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(Ulid other) => _value.CompareTo(other._value);

    /// <summary>相等比较。</summary>
    public static bool operator ==(Ulid left, Ulid right) => left.Equals(right);

    /// <summary>不等比较。</summary>
    public static bool operator !=(Ulid left, Ulid right) => !left.Equals(right);

    /// <summary>排序比较。</summary>
    public static bool operator <(Ulid left, Ulid right) => left.CompareTo(right) < 0;

    /// <summary>排序比较。</summary>
    public static bool operator >(Ulid left, Ulid right) => left.CompareTo(right) > 0;

    /// <summary>16 字节大端表示。</summary>
    public byte[] ToByteArray()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), (ulong)(_value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), (ulong)_value);

        return bytes;
    }

    /// <summary>从 16 字节大端表示还原。</summary>
    public static Ulid FromByteArray(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("ULID 必须是 16 字节", nameof(bytes));

        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);

        return new Ulid(((UInt128)high << 64) | low);
    }

    private static UInt128 Random80()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);

        UInt128 value = 0;

        foreach (var b in bytes)
            value = (value << 8) | b;

        return value;
    }

    private static int Decode(char c)
    {
        c = char.ToUpperInvariant(c);

        return c switch
        {
            'I' or 'L' => 1,
            'O' => 0,
            >= '0' and <= '9' => c - '0',
            _ => Alphabet.IndexOf(c),
        };
    }
}
