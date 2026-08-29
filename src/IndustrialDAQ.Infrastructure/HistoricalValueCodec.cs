using System.Globalization;

namespace IndustrialDAQ.Infrastructure;

/// <summary>历史值与不变区域字符串之间的可逆转换。</summary>
public static class HistoricalValueCodec
{
    public static (string? Value, string ValueType) Serialize(object? value, Type? declaredType = null)
    {
        var type = value?.GetType() ?? declaredType ?? typeof(string);
        if (value is null) return (null, type.FullName ?? type.Name);
        return (value is string text ? text : Convert.ToString(value, CultureInfo.InvariantCulture), type.FullName ?? type.Name);
    }

    public static object? Deserialize(string? value, string? valueType)
    {
        if (value is null) return null;
        return valueType switch
        {
            "System.Boolean" => bool.Parse(value), "System.Int16" => short.Parse(value, CultureInfo.InvariantCulture),
            "System.Int32" => int.Parse(value, CultureInfo.InvariantCulture), "System.Int64" => long.Parse(value, CultureInfo.InvariantCulture),
            "System.UInt16" => ushort.Parse(value, CultureInfo.InvariantCulture), "System.UInt32" => uint.Parse(value, CultureInfo.InvariantCulture),
            "System.Single" => float.Parse(value, CultureInfo.InvariantCulture), "System.Double" => double.Parse(value, CultureInfo.InvariantCulture),
            "System.Decimal" => decimal.Parse(value, CultureInfo.InvariantCulture), _ => value
        };
    }

    public static bool TryGetDouble(string? value, string? valueType, out double result)
    {
        try
        {
            var typed = Deserialize(value, valueType);
            if (typed is null or bool or string) { result = 0; return false; }
            result = Convert.ToDouble(typed, CultureInfo.InvariantCulture);
            return true;
        }
        catch { result = 0; return false; }
    }
}
