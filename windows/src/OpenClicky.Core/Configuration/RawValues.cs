// Swift `enum X: String` raw values for C# enums: what shell.json and the preferences store,
// so both platforms write the same strings. A member's raw value is its [RawValue] when it has
// one, else its name with the first letter lowered (Swift's case names: openAI, claudeCodePlan).

using System.Collections.Concurrent;
using System.Reflection;

namespace OpenClicky.Core.Configuration;

[AttributeUsage(AttributeTargets.Field)]
public sealed class RawValueAttribute(string value) : Attribute
{
    public string Value { get; } = value;
}

public static class RawValues
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<object, string> ToRaw, Dictionary<string, object> FromRaw)> Tables = new();

    private static (Dictionary<object, string> ToRaw, Dictionary<string, object> FromRaw) Table(Type enumType) =>
        Tables.GetOrAdd(enumType, type =>
        {
            var toRaw = new Dictionary<object, string>();
            var fromRaw = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = field.GetValue(null)!;
                var raw = field.GetCustomAttribute<RawValueAttribute>()?.Value
                    ?? char.ToLowerInvariant(field.Name[0]) + field.Name[1..];
                toRaw[value] = raw;
                fromRaw.TryAdd(raw, value);
            }
            return (toRaw, fromRaw);
        });

    /// <summary>The raw string for an enum value ("openAI" for AnswerSource.OpenAI).</summary>
    public static string RawValue<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        Table(typeof(TEnum)).ToRaw.TryGetValue(value, out var raw) ? raw : value.ToString();

    /// <summary>Swift's `init(rawValue:)`: an exact match, else null.</summary>
    public static TEnum? FromRawValue<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (raw is null) return null;
        return Table(typeof(TEnum)).FromRaw.TryGetValue(raw, out var value) ? (TEnum)value : null;
    }

    public static IReadOnlyList<TEnum> AllCases<TEnum>() where TEnum : struct, Enum => Enum.GetValues<TEnum>();
}
